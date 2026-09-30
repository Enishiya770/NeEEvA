using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

public partial class SenseVoiceSpeechToText
{
    private readonly Dictionary<int, ConversationTrace> m_CaptureTraces = new Dictionary<int, ConversationTrace>();
    public void RegisterCaptureTrace(int serial, ConversationTrace trace)
    {
        if (trace == null) return;
        m_CaptureTraces[serial] = trace;
        while (m_CaptureTraces.Count > 32) m_CaptureTraces.Remove(m_CaptureTraces.Keys.Min());
    }
    private ConversationTrace CaptureTrace(int serial) => m_CaptureTraces.TryGetValue(serial, out var trace) ? trace : null;
    private void TraceAnalysisResponse(ConversationTrace trace, Response r, int capture, int request)
    {
        if (trace == null || r == null) return;
        try
        {
            var segments = new JArray();
            foreach (var s in (r.turn_segments ?? new TurnTranscriptSegment[0]).Take(64))
                segments.Add(new JObject { ["id"] = s.id, ["startSeconds"] = s.start_seconds, ["endSeconds"] = s.end_seconds,
                    ["region"] = s.region, ["type"] = s.type, ["typeSource"] = s.type_source, ["status"] = s.status,
                    ["characters"] = (s.text ?? "").Length, ["reviewRequired"] = s.review_required });
            trace.Mark("audio.analysis_response", new JObject { ["captureSerial"] = capture, ["requestSerial"] = request,
                ["analysisAvailable"] = r.singing_analysis_available, ["rawVadObserved"] = r.raw_vad_observed,
                ["rawVadHasSpeech"] = r.raw_vad_observed ? (bool?)r.raw_vad_has_speech : null,
                ["rawVadSpeechMs"] = r.raw_vad_observed ? (int?)r.raw_vad_speech_ms : null,
                ["inputStatus"] = r.input_status, ["speakerStatus"] = r.speaker_status, ["isSinging"] = r.is_singing,
                ["singingProbability"] = r.singing_probability, ["voicedRatio"] = r.voiced_ratio,
                ["pitchBackend"] = r.pitch_backend, ["pitchStability"] = r.pitch_stability,
                ["contentOriginSeconds"] = r.audio_content_start_seconds,
                ["singingStartContentSeconds"] = r.singing_start_seconds, ["singingEndContentSeconds"] = r.singing_end_seconds,
                ["recoveryStartContentSeconds"] = r.singing_recovery_start_seconds, ["recoveryEndContentSeconds"] = r.singing_recovery_end_seconds,
                ["scoreWindowOffsetSeconds"] = r.singing_score_window_offset_seconds,
                ["analysisWindowOffsetSeconds"] = r.singing_analysis_window_offset_seconds,
                ["turnSegmentsSchema"] = r.turn_segments_schema, ["turnSegmentsComplete"] = r.turn_segments_complete,
                ["turnSegmentsTotal"] = r.turn_segments?.Length ?? 0, ["turnSegments"] = segments,
                ["f0Available"] = r.singing_score?.f0_hz?.Length > 0,
                ["wordTimestampsAvailable"] = false,
                ["scope"] = "server evidence before semantic acceptance; island bounds content-relative; segment/score origins retained as reported, not word alignment" });
            float[] f0 = r.singing_score?.f0_hz;
            if (f0 != null)
            {
                int keep = Math.Min(f0.Length, 6000);
                for (int i = 0; i < keep; i += 200)
                    trace.Sample("audio.f0_frames", new JObject { ["startFrame"] = i, ["totalFrames"] = f0.Length,
                        ["retainedFrames"] = keep, ["truncated"] = keep < f0.Length, ["frameSeconds"] = r.singing_score.frame_seconds,
                        ["contentOriginSeconds"] = r.audio_content_start_seconds, ["scoreWindowOffsetSeconds"] = r.singing_score_window_offset_seconds,
                        ["hz"] = new JArray(f0.Skip(i).Take(Math.Min(200, keep - i)).Select(x => float.IsNaN(x) || float.IsInfinity(x) ? (float?)null : x)) });
            }
        }
        catch { }
    }
    private void TraceSingingEvidence(SingingEvidenceSnapshot evidence)
    {
        var trace = CaptureTrace(evidence.CaptureSessionSerial)?.Child("audio-evidence");
        if (trace == null) return;
        try
        {
            trace.Mark("audio.evidence", new JObject {
                ["captureSerial"] = evidence.CaptureSessionSerial, ["recordingId"] = evidence.RecordingId,
                ["rawSeconds"] = evidence.RawSeconds, ["cleanStartSeconds"] = evidence.CleanStartSeconds,
                ["cleanEndSeconds"] = evidence.CleanEndSeconds, ["expandedStartSeconds"] = evidence.RecoveryStartSeconds,
                ["expandedEndSeconds"] = evidence.RecoveryEndSeconds, ["timelineOriginSeconds"] = evidence.TimelineOriginSeconds,
                ["mode"] = evidence.AcousticMode, ["singingProbability"] = evidence.SingingProbability,
                ["pitchStability"] = evidence.PitchStability, ["melodicIslandSeconds"] = evidence.MelodicIslandSeconds,
                ["contentSeconds"] = evidence.ContentSeconds, ["rawSha256"] = WaveformTrace.Describe(evidence.RawWavBytes)["sha256"],
                ["headBoundary"] = BoundaryTrace(evidence.HeadExtraSegments), ["tailBoundary"] = BoundaryTrace(evidence.TailExtraSegments),
                ["scope"] = "measured-acoustic-evidence-not-semantic-phrase-acceptance; MIDI timeline, not raw F0" });
            float[] pitch = evidence.PitchTimelineMidi ?? new float[0];
            // Bounded payload chunks preserve frame indices; long recordings explicitly report truncation.
            int keep = Math.Min(pitch.Length, 6000);
            for (int i = 0; i < keep; i += 200)
                trace.Sample("audio.pitch_frames", new JObject { ["startFrame"] = i, ["totalFrames"] = pitch.Length,
                    ["retainedFrames"] = keep, ["truncated"] = keep < pitch.Length, ["frameSeconds"] = evidence.FrameSeconds,
                    ["timelineOriginSeconds"] = evidence.TimelineOriginSeconds,
                    ["midi"] = new JArray(pitch.Skip(i).Take(Math.Min(200, keep - i)).Select(x => float.IsNaN(x) || float.IsInfinity(x) ? (float?)null : x)) });
        }
        catch { }
    }
    private static JArray BoundaryTrace(SingingBoundarySubsegment[] values)
    {
        var result = new JArray();
        foreach (var s in (values ?? new SingingBoundarySubsegment[0]).Take(64))
            result.Add(new JObject { ["startSeconds"] = s.start_seconds, ["endSeconds"] = s.end_seconds,
                ["expandedStartSeconds"] = s.expanded_start_seconds, ["expandedEndSeconds"] = s.expanded_end_seconds,
                ["type"] = s.type, ["voicedRatio"] = s.voiced_ratio, ["pitchSmoothRatio"] = s.pitch_smooth_ratio });
        return result;
    }
    private void TracePracticeSource(PracticePhrase phrase, byte[] wav, bool expanded)
    {
        // Preserve actual tracked trims. Do not infer raw range from a requested boundary that may not have been applied.
        if (CaptureTrace(phrase.CaptureSessionSerial) == null) return;
        WaveformTrace.Annotate(wav, phrase.ClipRef, phrase.Revision);
    }
    public JObject TraceSelectedMaterials(string references, string order, string range)
    {
        var entries = new JArray();
        // This list is the exact resolved stable order; never substitutes current list position.
        foreach (string token in (order ?? "").Split(','))
        {
            if (!token.StartsWith("stable:", StringComparison.Ordinal) || !int.TryParse(token.Substring(7), out int stable)) continue;
            var p = m_PracticePhrases.Find(x => x.StableId == stable);
            if (p == null) continue;
            var e = p.RecordingEvidence;
            entries.Add(new JObject { ["clipRef"] = p.ClipRef, ["stableId"] = p.StableId, ["revision"] = p.Revision,
                ["captureSerial"] = p.CaptureSessionSerial, ["capture"] = p.ActiveCapture,
                ["declaredStartSeconds"] = e == null ? (float?)null : (p.ActiveCapture == "expanded" ? e.RecoveryStartSeconds : e.CleanStartSeconds) + p.ActiveTrimHeadSeconds,
                ["declaredEndSeconds"] = e == null ? (float?)null : (p.ActiveCapture == "expanded" ? e.RecoveryEndSeconds : e.CleanEndSeconds) - p.ActiveTrimTailSeconds,
                ["audio"] = WaveformTrace.Describe(p.WavBytes) });
        }
        return new JObject { ["requestedRefs"] = references, ["range"] = range, ["resolved"] = entries,
            ["scope"] = "prepared-not-played; declared boundaries and actual PCM lineage are separate" };
    }
}
