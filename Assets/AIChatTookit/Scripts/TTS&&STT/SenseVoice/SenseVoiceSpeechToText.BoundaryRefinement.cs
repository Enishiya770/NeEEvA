using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class SenseVoiceSpeechToText
{
    private sealed class BoundaryLease
    {
        public RecordingBoundarySource Source;
        public float Start, End;
        public Func<bool> IsCurrent;
        public bool Consumed;
    }
    private readonly Dictionary<string, BoundaryLease> m_VerifiedBoundaryLeases = new Dictionary<string, BoundaryLease>(StringComparer.Ordinal);

    public RecordingBoundarySource SnapshotBoundarySource(string clip) =>
        SnapshotBoundarySource(clip, "current", "lead", out _);

    // The named range is a coarse proposal, not a prepared/current playback.
    // Retain the raw spoken edge for analysis while fixing the opposite edge.
    public RecordingBoundarySource SnapshotBoundarySource(string clip, string range, string edge, out string reason)
    {
        reason = "boundary_source_audio_unavailable";
        var p = FindSingingClip(clip);
        var candidate = p == null ? null : m_QuarantinedSingingCandidates.Find(c => c.Phrase == p);
        string playback = candidate?.PlaybackStatus ?? "ready";
        bool retained = range == "clean" || range == "expanded";
        if (p == null || !retained && (range != "current" || playback != "ready") ||
            edge != "lead" && edge != "tail") return null;
        var e = retained ? p.LatestRecordingEvidence ?? p.RecordingEvidence : p.RecordingEvidence;
        if (e == null || !SameRecording(p, e) || !HasUsableWavPayload(e.RawWavBytes)) return null;
        byte[] raw = e.RawWavBytes, current = p.WavBytes;
        var pitch = retained ? e.PitchTimelineMidi : p.MidiTimeline;
        float frame = retained ? e.FrameSeconds : p.FrameSeconds;
        if (pitch == null || !HasPlayablePitchTimeline(pitch) || !BoundaryFinite(frame) || frame <= 0f)
        { reason = "boundary_source_pitch_unavailable"; return null; }
        float duration = GetWavDurationSeconds(raw);
        float baselineStart = retained ? range == "expanded" ? e.RecoveryStartSeconds : e.CleanStartSeconds
            : (p.ActiveCapture == "expanded" ? e.RecoveryStartSeconds : e.CleanStartSeconds) + p.ActiveTrimHeadSeconds;
        float baselineEnd = retained ? range == "expanded" ? e.RecoveryEndSeconds : e.CleanEndSeconds
            : (p.ActiveCapture == "expanded" ? e.RecoveryEndSeconds : e.CleanEndSeconds) - p.ActiveTrimTailSeconds;
        if (!BoundaryFinite(baselineStart) || !BoundaryFinite(baselineEnd) || baselineStart < 0f ||
            baselineEnd > duration + .001f || baselineEnd - baselineStart < 1f ||
            !retained && (!HasUsableWavPayload(current) || Math.Abs(GetWavDurationSeconds(current) - (baselineEnd - baselineStart)) > .025f))
        { reason = "boundary-source-range-unavailable"; return null; }
        var lineage = WaveformTrace.Describe(raw);
        if (!RecordingSegmentIdentity.TryWaveFormat(raw, out int sampleRate, out long sampleCount)) return null;
        reason = null;
        return new RecordingBoundarySource {
            Clip = p.ClipRef, Revision = candidate != null ? "pending" : p.Revision.ToString(CultureInfo.InvariantCulture),
            StorageRevision = p.Revision, RecordingId = e.RecordingId,
            SampleRate = sampleRate, SampleCount = sampleCount,
            EvidenceRevision = (string)e.BoundaryTranscriptEvidence?["evidence_revision"],
            RawWav = (byte[])raw.Clone(), CurrentWav = retained ? null : (byte[])current.Clone(), Pitch = (float[])pitch.Clone(),
            RawHash = RecordingBoundarySource.Hash(raw), CurrentHash = RecordingBoundarySource.Hash(current),
            PitchHash = RecordingBoundarySource.HashPitch(p.MidiTimeline),
            EvidencePitchHash = RecordingBoundarySource.HashPitch(pitch), EvidenceStamp = BoundaryEvidenceStamp(e),
            LineageHash = RecordingBoundarySource.Hash(Encoding.UTF8.GetBytes(lineage.ToString(Formatting.None))),
            Transcript = e.Text, SingingText = e.SingingText, UsesRetainedEvidence = retained,
            TranscriptSegments = BoundaryTranscriptSegments(e),
            SelectionRange = range, SourcePlaybackStatus = playback,
            BaselineStart = baselineStart, BaselineEnd = baselineEnd,
            Start = retained && edge == "lead" ? 0f : baselineStart,
            End = retained && edge == "tail" ? duration : baselineEnd,
            PitchOrigin = retained ? e.TimelineOriginSeconds : baselineStart,
            FrameSeconds = frame, TrimHead = p.ActiveTrimHeadSeconds, TrimTail = p.ActiveTrimTailSeconds, ActiveCapture = p.ActiveCapture
        };
    }

    private static bool BoundaryFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static string BoundaryEvidenceStamp(SingingEvidenceSnapshot e) => e == null ? "" :
        RecordingBoundarySource.Hash(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new {
            e.RecordingId, e.CaptureSessionSerial, e.Text, e.SingingText, e.TimelineOriginSeconds, e.FrameSeconds,
            e.RawSeconds, e.CleanStartSeconds, e.CleanEndSeconds, e.RecoveryStartSeconds, e.RecoveryEndSeconds,
            e.HeadExtraText, e.TailExtraText, e.HeadExtraType, e.TailExtraType,
            e.HeadExtraReviewRequired, e.TailExtraReviewRequired,
            e.BoundaryTranscriptEvidence,
            pitch = RecordingBoundarySource.HashPitch(e.PitchTimelineMidi)
        })));

    public bool BoundarySourceStillCurrent(RecordingBoundarySource source) => BoundarySourceChangeReason(source) == null;
    public string BoundarySourceChangeReason(RecordingBoundarySource source)
    {
        var p = source == null ? null : FindSingingClip(source.Clip);
        if (p == null || p.Revision != source.StorageRevision) return "source_version_changed";
        var candidate = m_QuarantinedSingingCandidates.Find(c => c.Phrase == p);
        if ((candidate != null ? "pending" : p.Revision.ToString(CultureInfo.InvariantCulture)) != source.Revision) return "source_version_changed";
        if (!RecordingSegmentIdentity.TryWaveFormat(source.RawWav, out int sourceRate, out long sourceCount) ||
            source.SampleRate != sourceRate || source.SampleCount != sourceCount) return "source_version_changed";
        var e = source.UsesRetainedEvidence ? p.LatestRecordingEvidence ?? p.RecordingEvidence : p.RecordingEvidence;
        bool pcmMatches = e != null && (candidate?.PlaybackStatus ?? "ready") == source.SourcePlaybackStatus && e.RecordingId == source.RecordingId &&
            RecordingBoundarySource.Hash(e.RawWavBytes) == source.RawHash &&
            RecordingBoundarySource.Hash(source.RawWav) == source.RawHash &&
            RecordingBoundarySource.HashPitch(source.Pitch) == source.EvidencePitchHash &&
            (source.UsesRetainedEvidence || RecordingBoundarySource.Hash(source.CurrentWav) == source.CurrentHash) &&
            RecordingBoundarySource.Hash(p.WavBytes) == source.CurrentHash && RecordingBoundarySource.HashPitch(p.MidiTimeline) == source.PitchHash;
        if (!pcmMatches) return "source_version_changed";
        return BoundaryEvidenceStamp(e) == source.EvidenceStamp ? null : "source_evidence_changed";
    }

    private static JObject BoundaryManifest(RecordingBoundarySource source, JObject edit, string id, string playedRevision) => new JObject {
        ["schema"] = 1, ["request_id"] = id, ["source_id"] = source.Clip,
        ["source_revision"] = source.Revision, ["audio_sha256"] = source.RawHash,
        ["source_offset_seconds"] = 0, ["current_start_seconds"] = source.Start, ["current_end_seconds"] = source.End,
        ["edge"] = edit["edge"], ["exclude_text"] = edit["exclude_text"], ["keep_text"] = edit["keep_text"],
        ["language"] = "auto", ["played_revision"] = playedRevision ?? "", ["pcm_lineage_hash"] = source.LineageHash,
        ["selection_range"] = source.SelectionRange, ["selected_baseline_start_seconds"] = source.BaselineStart,
        ["selected_baseline_end_seconds"] = source.BaselineEnd,
        ["analysis_scope"] = source.UsesRetainedEvidence ? "retained_raw_edge_with_opposite_edge_fixed" : "prepared_current_strict_shrink"
    };

    public IEnumerator RefineRecordingBoundary(RecordingBoundarySource source, JObject edit, string playedRevision,
        Func<bool> isCurrent, Func<bool> executionCurrent, Action<JObject> complete)
    {
        bool Current() => (isCurrent == null || isCurrent()) && BoundarySourceStillCurrent(source);
        JObject Fail(string reason) => new JObject { ["status"] = "boundary_unresolved", ["reason"] = reason, ["range"] = null };
        if (!Current()) { complete(Fail(BoundarySourceChangeReason(source) ?? "source_binding_unavailable")); yield break; }
        string id = "boundary-" + Guid.NewGuid().ToString("N");
        var manifest = BoundaryManifest(source, edit, id, playedRevision);
        var form = new WWWForm(); form.AddBinaryData("audio_file", source.RawWav, "source.wav", "audio/wav");
        form.AddField("manifest", manifest.ToString(Formatting.None));
        using (var request = UnityWebRequest.Post(m_ServerSetting.TrimEnd('/') + "/singing/refine-boundary", form))
        {
            request.timeout = 40;
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                if (!Current())
                {
                    request.Abort(); StartCoroutine(ControlAnalysis("cancel", id));
                    // A replaced request has no right to deliver a reply. A still
                    // current request whose source changed needs the true cause.
                    if (isCurrent == null || isCurrent()) complete(Fail(BoundarySourceChangeReason(source) ?? "source_binding_unavailable"));
                    yield break;
                }
                yield return null;
            }
            if (!Current())
            {
                if (isCurrent == null || isCurrent()) complete(Fail(BoundarySourceChangeReason(source) ?? "source_binding_unavailable"));
                yield break;
            }
            JObject result = null;
            if (request.result == UnityWebRequest.Result.Success)
                try { result = JObject.Parse(request.downloadHandler.text); } catch (JsonException) { }
            if (result == null) { complete(Fail("boundary_service_unavailable")); yield break; }
            if ((string)result["status"] != "resolved") { complete(result); yield break; }
            if (!TryBindRecordingBoundaryResult(source, edit, id, playedRevision, result, executionCurrent, out string reason))
            { complete(Fail(reason)); yield break; }
            complete(result);
        }
    }

    // Kept separate so the exact HTTP binding and deferred preparation can be
    // replayed without inference, playback, source mutation or a fake server.
    public bool TryBindRecordingSegmentBoundary(RecordingBoundarySource source, JObject edit, JObject request,
        JObject result, Func<bool> executionCurrent, out string reason)
    {
        reason = "source_version_changed";
        if (!BoundarySourceStillCurrent(source) || executionCurrent != null && !executionCurrent()) return false;
        if (!RecordingSegmentBoundary.TryValidate(source, request, result, out long start, out long end, out reason)) return false;
        // This mutation is confined to the accepted provider result, after all
        // integer source/selection bindings. It does not prepare or play audio.
        result["range"]["start_seconds"] = start / (double)source.SampleRate;
        result["range"]["end_seconds"] = end / (double)source.SampleRate;
        result["edit"] = edit.DeepClone(); result["selection_range"] = source.SelectionRange;
        result["source_snapshot"] = source.Facts(true); result["mode_verified"] = true;
        result["current_playback"] = source.UsesRetainedEvidence ? null : source.Facts(true);
        m_VerifiedBoundaryLeases[source.Clip] = new BoundaryLease {
            Source = source, Start = (float)(start / (double)source.SampleRate), End = (float)(end / (double)source.SampleRate), IsCurrent = executionCurrent };
        return true;
    }

    private bool TryBindRecordingBoundaryResult(RecordingBoundarySource source, JObject edit, string id,
        string playedRevision, JObject result, Func<bool> executionCurrent, out string reason)
    {
        reason = "boundary_result_binding_mismatch";
        double Number(JToken token) => token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            ? (double)token : double.NaN;
        bool SameNumber(JToken token, double expected) => Math.Abs(Number(token) - expected) <= .0001;
        var range = result?["range"] as JObject;
        double start = Number(range?["start_seconds"]), end = Number(range?["end_seconds"]);
        bool lead = (string)edit?["edge"] == "lead";
        if (!BoundarySourceStillCurrent(source) || (string)result?["status"] != "resolved" ||
            (string)result["request_id"] != id || (string)result["source_id"] != source.Clip ||
            (string)result["source_revision"] != source.Revision || (string)result["audio_sha256"] != source.RawHash ||
            (string)result["pcm_lineage_hash"] != source.LineageHash || (string)result["played_revision"] != (playedRevision ?? "") ||
            !SameNumber(result["source_offset_seconds"], 0) || !SameNumber(result["current_start_seconds"], source.Start) ||
            !SameNumber(result["current_end_seconds"], source.End) || (string)result["edge"] != (string)edit["edge"] ||
            double.IsNaN(start) || double.IsInfinity(start) || double.IsNaN(end) || double.IsInfinity(end) ||
            start < source.Start - .0001 || end > source.End + .0001 || end - start < 1 ||
            (lead ? Math.Abs(end - source.End) > .0001 || start <= source.Start : Math.Abs(start - source.Start) > .0001 || end >= source.End)) return false;
        result["edit"] = edit.DeepClone(); result["selection_range"] = source.SelectionRange;
        result["source_snapshot"] = source.Facts(true);
        result["current_playback"] = source.UsesRetainedEvidence ? null : source.Facts(true);
        m_VerifiedBoundaryLeases[source.Clip] = new BoundaryLease {
            Source = source, Start = (float)start, End = (float)end, IsCurrent = executionCurrent };
        reason = null; return true;
    }

    private bool TryApplyVerifiedBoundary(string clip, float start, float end, bool confirmUser, bool excludeSpeech,
        out bool handled, out int stableId, out string failure)
    {
        handled = false; stableId = 0; failure = "";
        if (!m_VerifiedBoundaryLeases.TryGetValue(clip, out var lease)) return false;
        handled = true;
        if (lease.Consumed) { failure = "boundary_unresolved: verified boundary already consumed; no legacy fallback"; return false; }
        if (Math.Abs(start - lease.Start) > .001f || Math.Abs(end - lease.End) > .001f)
        { failure = "boundary_unresolved: requested range differs from verified boundary; no legacy fallback"; return false; }
        lease.Consumed = true;
        var source = lease.Source;
        if (lease.IsCurrent != null && !lease.IsCurrent() || !BoundarySourceStillCurrent(source))
        { failure = "boundary_unresolved: source version or input changed; no audio modified"; return false; }
        var p = FindSingingClip(clip);
        byte[] buffer = source.UsesRetainedEvidence ? source.RawWav : source.CurrentWav;
        float bufferOrigin = source.UsesRetainedEvidence ? 0f : source.Start;
        if (!TryDecodePcmWav(buffer, out float[] samples, out int sampleRate))
        { failure = "boundary_unresolved: source PCM unavailable"; return false; }
        int first = Mathf.RoundToInt((start - bufferOrigin) * sampleRate), last = Mathf.RoundToInt((end - bufferOrigin) * sampleRate);
        if (first < 0 || last > samples.Length + 1 || last - first < sampleRate)
        { failure = "boundary_unresolved: PCM coordinate mismatch"; return false; }
        last = Math.Min(last, samples.Length);
        var pcm = new float[last - first]; Array.Copy(samples, first, pcm, 0, pcm.Length);
        float frame = source.FrameSeconds;
        float pitchStart = bufferOrigin + first / (float)sampleRate - source.PitchOrigin;
        if (!BoundaryFinite(frame) || frame <= 0 || pitchStart < -.0001f ||
            end > source.PitchOrigin + source.Pitch.Length * frame + .025f)
        { failure = "boundary_unresolved: retained range lacks a matching pitch timeline"; return false; }
        var pitch = new float[Mathf.CeilToInt((pcm.Length / (float)sampleRate) / frame)];
        for (int i = 0; i < pitch.Length; i++)
        {
            int sourceFrame = Mathf.FloorToInt((Mathf.Max(0f, pitchStart) + i * frame) / frame);
            pitch[i] = sourceFrame >= 0 && sourceFrame < source.Pitch.Length ? source.Pitch[sourceFrame] : 0;
        }
        if (!HasPlayablePitchTimeline(pitch)) { failure = "boundary_unresolved: no retained pitch in range"; return false; }
        byte[] wav = EncodeMonoPcm16Wav(pcm, sampleRate);
        if (wav == null || wav.Length <= 44) { failure = "boundary_unresolved: encoding failed"; return false; }
        PracticePhrase prepared;
        if (source.UsesRetainedEvidence)
        {
            var evidence = p.LatestRecordingEvidence ?? p.RecordingEvidence;
            if (start < evidence.RecoveryStartSeconds || end > evidence.RecoveryEndSeconds ||
                !TryPrepareEvidenceRange(p, evidence, "expanded", start - evidence.RecoveryStartSeconds,
                    evidence.RecoveryEndSeconds - end, out prepared, out failure))
            { if (string.IsNullOrEmpty(failure)) failure = "boundary_unresolved: refined range outside retained preparation coverage"; return false; }
        }
        else
        {
            prepared = p.CopyForRevision();
            prepared.ActiveTrimHeadSeconds = source.TrimHead + first / (float)sampleRate;
            prepared.ActiveTrimTailSeconds = source.TrimTail + (samples.Length - last) / (float)sampleRate;
        }
        WaveformTrace.Transform(buffer, wav, first / (double)sampleRate, last / (double)sampleRate);
        prepared.WavBytes = wav; prepared.MidiTimeline = pitch; prepared.FrameSeconds = frame;
        prepared.Seconds = pcm.Length / (float)sampleRate; prepared.Revision = p.Revision + 1;
        var candidate = m_QuarantinedSingingCandidates.Find(c => c.Phrase == p);
        int index = candidate != null ? m_PracticePhrases.Count : m_PracticePhrases.IndexOf(p);
        int priorStableId = prepared.StableId;
        if (candidate != null) prepared.StableId = m_NextPracticePhraseStableId + 1;
        if (!ValidatePreparedEvidenceRange(index, prepared, excludeSpeech, out failure)) return false;
        prepared.StableId = priorStableId;
        if (candidate != null)
        {
            if (!AdmitSelectedSingingCandidate(candidate.CandidateId, confirmUser, out int admittedIndex, out string status, prepared) || status != "ready")
            { failure = "boundary_unresolved: prepared source admission failed; " + status; return false; }
            stableId = m_PracticePhrases[admittedIndex - 1].StableId;
        }
        else
        {
            m_PracticePhrases[index] = prepared; stableId = prepared.StableId;
            if (confirmUser) ConfirmSingingClipSource(clip, out _);
        }
        Debug.Log("[Sing/BoundaryRefinement] applied " + JsonConvert.SerializeObject(new { clip, priorRevision = source.Revision,
            prepared.Revision, rawStart = start, rawEnd = end, selectionRange = source.SelectionRange,
            pcmSha256 = RecordingBoundarySource.Hash(wav), pitchSha256 = RecordingBoundarySource.HashPitch(pitch), originalRetained = true }));
        return true;
    }
}
