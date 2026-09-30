using System;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Detached source identity. Current uses the exact prepared PCM/pitch; named
// coarse ranges use retained raw evidence without preparing/admitting a clip.
public sealed class RecordingBoundarySource
{
    public string Clip, Revision, RecordingId, RawHash, CurrentHash, PitchHash, LineageHash, Transcript, SingingText;
    public float Start, End, FrameSeconds, TrimHead, TrimTail;
    public string ActiveCapture;
    public string SelectionRange = "current", EvidenceStamp, EvidencePitchHash, SourcePlaybackStatus;
    public int StorageRevision;
    public int SampleRate;
    public long SampleCount;
    public string EvidenceRevision;
    public float BaselineStart, BaselineEnd, PitchOrigin;
    public bool UsesRetainedEvidence;
    public JArray TranscriptSegments = new JArray();
    [JsonIgnore] public byte[] RawWav, CurrentWav;
    [JsonIgnore] public float[] Pitch;
    public JObject Facts(bool current) => new JObject {
        ["key"] = Clip, ["revision"] = Revision, ["recordingId"] = RecordingId,
        ["storageRevision"] = StorageRevision, ["evidenceStamp"] = EvidenceStamp,
        ["sample_rate"] = SampleRate, ["sample_count"] = SampleCount, ["evidence_revision"] = EvidenceRevision,
        ["audio_sha256"] = RawHash, ["selected_audio_sha256"] = CurrentHash,
        ["pitch_sha256"] = PitchHash, ["pcm_lineage_hash"] = LineageHash,
        ["currentStartSeconds"] = Start, ["currentEndSeconds"] = End,
        ["selectionRange"] = SelectionRange, ["sourcePlaybackStatus"] = SourcePlaybackStatus,
        ["selectedBaseline"] = new JObject { ["start_seconds"] = BaselineStart, ["end_seconds"] = BaselineEnd },
        ["analysisEnvelope"] = new JObject { ["start_seconds"] = Start, ["end_seconds"] = End },
        ["usesRetainedEvidence"] = UsesRetainedEvidence,
        ["transcript"] = Transcript ?? "", ["singingText"] = SingingText ?? "",
        ["transcriptSegments"] = TranscriptSegments?.DeepClone(),
        ["currentVersionMatches"] = current,
        ["scope"] = UsesRetainedEvidence
            ? "Frozen raw evidence for read-only boundary analysis; coarse selected baseline is not a verified boundary or playback. Only the edited edge may change inside the analysis envelope; opposite edge fixed. No source confirmation or execution permission."
            : "Fixed prepared source/version; completed playback only when enclosed by last completed playback binding. Content satisfaction unverified; not new execution permission." };
    public static string Hash(byte[] bytes)
    { if (bytes == null) return ""; using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    public static string HashPitch(float[] pitch)
    { if (pitch == null) return ""; var bytes = new byte[pitch.Length * sizeof(float)]; Buffer.BlockCopy(pitch, 0, bytes, 0, bytes.Length); return Hash(bytes); }

    public bool MatchesProposal(RecordingSingingMaterial material, JObject edit) => MatchesProposal(material, edit, out _);
    public bool MatchesProposal(RecordingSingingMaterial material, JObject edit, out string reason)
    {
        reason = "source_version_changed";
        if (material == null || material.ClipRef != Clip || material.Revision != Revision ||
            material.PlaybackStatus != SourcePlaybackStatus) return false;
        JObject facts;
        try { facts = JObject.Parse(material.Description ?? "{}"); } catch (JsonException) { return false; }
        if ((string)edit?["kind"] == "segments")
        {
            reason = "source_evidence_changed";
            return RecordingSegmentIdentity.Matches(facts["boundaryEvidence"] as JObject, RawHash, SampleRate, SampleCount, TranscriptSegments) &&
                RecordingSegmentIdentity.TrySelect(edit, facts["boundaryEvidence"] as JObject, out _, out reason);
        }
        string exclude = (string)edit?["exclude_text"], keep = (string)edit?["keep_text"];
        if (string.IsNullOrWhiteSpace(exclude) || string.IsNullOrWhiteSpace(keep) ||
            !ContainsAnchor(exclude, false) || !ContainsAnchor(keep, true)) return false;
        if (!UsesRetainedEvidence) { reason = null; return true; }
        reason = "source_evidence_changed";
        if (TranscriptSegments != null && TranscriptSegments.Count > 0 &&
            !RecordingSegmentIdentity.Matches(facts["boundaryEvidence"] as JObject, RawHash, SampleRate, SampleCount, TranscriptSegments)) return false;
        // A late final may update evidence without changing the playable revision.
        // It must not silently replace the evidence the planner actually selected.
        string prefix = SelectionRange == "expanded" ? "Expanded" : "Clean";
        bool Number(JToken t, float value) => t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) &&
            Math.Abs((double)t - value) <= .0001;
        bool matches = (string)facts["RecordingTranscript"] == (Transcript ?? "") &&
            ((string)facts["SingingSegmentText"] ?? "") == (SingingText ?? "") &&
            Number(facts[prefix + "StartSeconds"], BaselineStart) && Number(facts[prefix + "EndSeconds"], BaselineEnd);
        if (matches) reason = null;
        return matches;
    }
    public bool ContainsAnchor(string text, bool singing) => !string.IsNullOrEmpty(text) &&
        ((Transcript ?? "").Contains(text) || singing && (SingingText ?? "").Contains(text) ||
        TranscriptSegments != null && System.Linq.Enumerable.Any(TranscriptSegments,
            s => (string)s["status"] == "ok" && ((string)s["text"] ?? "").Contains(text)));
}
