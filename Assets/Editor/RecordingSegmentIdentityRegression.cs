using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Pure CLR checks: no Unity, inference, network, playback or production mutation.
public static class RecordingSegmentIdentityRegression
{
    public static int Run(string root)
    {
        int count = 0;
        void Check(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); count++; }
        string sample = Path.Combine(root, "work/log-evaluation-20260928-2336/audio-c15/original.wav");
        if (!File.Exists(sample)) sample = Path.Combine(root, "work/loop-convergence-20260928-2212/live-c13/original.wav");
        var wav = File.ReadAllBytes(sample);
        Check(RecordingSegmentIdentity.TryWaveFormat(wav, out int rate, out long samples), "real stored mono PCM header");
        string hash = RecordingBoundarySource.Hash(wav);
        var legacy = new JArray(new JObject { ["id"] = 1, ["start_seconds"] = 6.35f, ["end_seconds"] = 11.87f,
            ["text"] = "止められない素失の予感。", ["origin"] = "segment", ["region"] = "island", ["status"] = "ok" },
            new JObject { ["id"] = 2, ["start_seconds"] = 11.87f, ["end_seconds"] = 15.68f,
            ["text"] = "好，轮到你车了。", ["origin"] = "segment", ["region"] = "tail", ["status"] = "ok" });
        var evidence = RecordingSegmentIdentity.CreateEvidence(hash, rate, samples, "c15-fixture", 15, legacy);
        Check(evidence != null, "real decimal windows canonicalize");
        Check((long)evidence["segments"][0]["start_sample"] == 101600, "6.35 seconds maps to actual integer sample101600");
        var parsed = JObject.Parse(evidence.ToString(Formatting.None));
        Check(RecordingSegmentIdentity.Matches(parsed, hash, rate, samples, legacy), "legacy Single vs parsed Double have identical source sample identity");
        Check(JToken.DeepEquals(evidence, parsed), "canonical full serialization readback is exact");
        var source = new RecordingBoundarySource { Clip = "clip:c15", Revision = "0", SourcePlaybackStatus = "ready", RawHash = hash,
            SampleRate = rate, SampleCount = samples, EvidenceRevision = (string)evidence["evidence_revision"],
            Transcript = "止められない素失の予感。 好，轮到你车了。", SingingText = "止められない素失の予感。",
            UsesRetainedEvidence = true, SelectionRange = "clean", BaselineStart = 0f, BaselineEnd = 11.87f,
            Start = 0f, End = samples / (float)rate, TranscriptSegments = (JArray)legacy.DeepClone() };
        var facts = new JObject { ["RecordingTranscript"] = source.Transcript, ["SingingSegmentText"] = source.SingingText,
            ["CleanStartSeconds"] = 0f, ["CleanEndSeconds"] = 11.87f, ["boundaryEvidence"] = evidence.DeepClone() };
        var material = new RecordingSingingMaterial { ClipRef = source.Clip, Revision = "0", PlaybackStatus = "ready", Description = facts.ToString(Formatting.None) };
        var trim = new JObject { ["kind"] = "trim", ["exclude_text"] = "好，轮到你车了。", ["keep_text"] = "止められない素失の予感。" };
        Check(source.MatchesProposal(material, trim), "actual production MatchesProposal accepts unchanged fully serialized decimal source");
        material.Revision = "1"; Check(!source.MatchesProposal(material, trim), "stale audio revision denied"); material.Revision = "0";
        material.ClipRef = "clip:other"; Check(!source.MatchesProposal(material, trim), "foreign source denied"); material.ClipRef = source.Clip;
        var altered = (JObject)evidence.DeepClone(); altered["audio_sha256"] = new string('0', 64);
        Check(!RecordingSegmentIdentity.Matches(altered, hash, rate, samples, legacy), "foreign PCM denied");
        altered = (JObject)evidence.DeepClone(); altered["segments"][0]["text"] = "different lyric";
        Check(!RecordingSegmentIdentity.Matches(altered, hash, rate, samples, legacy), "changed text cannot retain old segment ID");
        altered = (JObject)evidence.DeepClone(); altered["segments"][0]["start_sample"] = 101601;
        Check(!RecordingSegmentIdentity.Matches(altered, hash, rate, samples, legacy), "one sample mutation cannot retain old segment ID");
        altered = (JObject)evidence.DeepClone(); altered["segments"][0]["word_aligned"] = true;
        Check(!RecordingSegmentIdentity.Matches(altered, hash, rate, samples, legacy), "ASR candidate cannot claim word alignment");
        altered = (JObject)evidence.DeepClone(); altered["sample_rate"] = 8000;
        Check(!RecordingSegmentIdentity.Matches(altered, hash, rate, samples, legacy), "coordinate rate mismatch denied");
        Check(RecordingSegmentIdentity.ValidLegacyAnchors("很好，接下来我上第二段。", "初めて"), "real c9 three character anchor accepted consistently");
        Check(!RecordingSegmentIdentity.ValidLegacyAnchors("x", "初めて"), "insufficient removed anchor denied");
        Check(!RecordingSegmentIdentity.ValidLegacyAnchors("初めて", "初めて あなた"), "overlapping anchors denied");
        var edit = new JObject { ["kind"] = "segments", ["target"] = "material", ["evidence_revision"] = evidence["evidence_revision"].DeepClone(),
            ["keep_segment_ids"] = new JArray(evidence["segments"][0]["segment_id"].DeepClone()),
            ["exclude_segment_ids"] = new JArray(evidence["segments"][1]["segment_id"].DeepClone()) };
        Check(RecordingSegmentIdentity.TrySelect(edit, parsed, out var selection, out _), "ordinary selection uses source segment IDs without copied anchors");
        Check((string)selection["status"] == "candidate", "semantic windows never authorize cuts");
        Check(source.MatchesProposal(material, edit), "segment selection matches actual immutable source");
        var badEdit = (JObject)edit.DeepClone(); badEdit["keep_segment_ids"][0] = "seg_foreign";
        Check(!RecordingSegmentIdentity.TrySelect(badEdit, parsed, out _, out _), "unknown segment denied");
        badEdit = (JObject)edit.DeepClone(); badEdit["exclude_segment_ids"][0] = badEdit["keep_segment_ids"][0].DeepClone();
        Check(!RecordingSegmentIdentity.TrySelect(badEdit, parsed, out _, out _), "contradictory selected and excluded segment denied");
        badEdit = (JObject)edit.DeepClone(); badEdit["evidence_revision"] = "stale";
        Check(!RecordingSegmentIdentity.TrySelect(badEdit, parsed, out _, out _), "stale metadata denied independently of PCM version");
        var request = RecordingSegmentBoundary.Request(source, selection);
        Check(!RecordingSegmentBoundary.TryValidate(source, request, new JObject { ["status"] = "candidate" }, out _, out _, out _), "offline PANNs candidate cannot create a crop");
        var response = new JObject { ["status"] = "resolved", ["classification_supported"] = true, ["boundary_supported"] = true,
            ["quality"] = "classification-and-refinement-supported", ["provider"] = "controlled-test", ["model_version"] = "fixture", ["refinement_version"] = "fixture",
            ["request_id"] = request["request_id"].DeepClone(), ["source_id"] = source.Clip, ["source_revision"] = source.Revision,
            ["audio_sha256"] = hash, ["sample_rate"] = rate, ["sample_count"] = samples, ["evidence_revision"] = source.EvidenceRevision,
            ["pcm_lineage_hash"] = "",
            ["selection_sha256"] = request["selection_sha256"].DeepClone(), ["range"] = new JObject { ["start_sample"] = 101600, ["end_sample"] = 189920 },
            ["uncertainty"] = new JObject { ["start_min_sample"] = 101440, ["start_max_sample"] = 101760, ["end_min_sample"] = 189760, ["end_max_sample"] = 190080 } };
        Check(RecordingSegmentBoundary.TryValidate(source, request, response, out long lo, out long hi, out _) && lo == 101600 && hi == 189920,
            "explicitly controlled accepted provider result binds integer PCM; not acoustic ground truth");
        var wrongResponse = (JObject)response.DeepClone(); wrongResponse["selection_sha256"] = "wrong";
        Check(!RecordingSegmentBoundary.TryValidate(source, request, wrongResponse, out _, out _, out _), "provider result bound to different selection denied");
        wrongResponse = (JObject)response.DeepClone(); wrongResponse["boundary_supported"] = false;
        Check(!RecordingSegmentBoundary.TryValidate(source, request, wrongResponse, out _, out _, out _), "classification without refinement denied");
        wrongResponse = (JObject)response.DeepClone(); wrongResponse["range"]["start_sample"] = 101600.1;
        Check(!RecordingSegmentBoundary.TryValidate(source, request, wrongResponse, out _, out _, out _), "fractional sample response denied");
        wrongResponse = (JObject)response.DeepClone(); wrongResponse["uncertainty"]["end_max_sample"] = samples + 1;
        Check(!RecordingSegmentBoundary.TryValidate(source, request, wrongResponse, out _, out _, out _), "out-of-source uncertainty denied");
        wrongResponse = (JObject)response.DeepClone(); wrongResponse["classification_supported"] = "invalid-bool";
        Check(!RecordingSegmentBoundary.TryValidate(source, request, wrongResponse, out _, out _, out _), "malformed provider data rejects without throwing");
        altered = (JObject)evidence.DeepClone(); altered["sample_count"] = new JObject();
        Check(!RecordingSegmentIdentity.Matches(altered, hash, rate, samples, legacy), "malformed evidence data rejects without throwing");
        return count;
    }
}
