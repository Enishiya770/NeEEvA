using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public partial class SenseVoiceSpeechToText
{
    private readonly Dictionary<int, int> m_RecordingSequences = new Dictionary<int, int>();
    private int m_NextRecordingSequence;

    private int ReserveRecordingSequence(int session)
    {
        if (session <= 0) return ++m_NextRecordingSequence;
        if (!m_RecordingSequences.TryGetValue(session, out int sequence))
            m_RecordingSequences[session] = sequence = ++m_NextRecordingSequence;
        return sequence;
    }

    private int EnsureRecordingSequence(PracticePhrase phrase)
    {
        if (phrase.RecordingSequence <= 0)
            phrase.RecordingSequence = ReserveRecordingSequence(phrase.CaptureSessionSerial);
        return phrase.RecordingSequence;
    }

    private static bool SameRecording(PracticePhrase phrase, SingingEvidenceSnapshot evidence)
    {
        return phrase != null && evidence != null &&
            (evidence.CaptureSessionSerial > 0
                ? phrase.CaptureSessionSerial == evidence.CaptureSessionSerial
                : phrase.CaptureSessionSerial <= 0 && Mathf.Abs(phrase.AtRealtime - evidence.AtRealtime) < .001f);
    }

    private static void RetainRecordingEvidence(PracticePhrase phrase, SingingEvidenceSnapshot evidence)
    {
        if (!SameRecording(phrase, evidence) || !HasUsableWavPayload(evidence.RawWavBytes)) return;
        var retained = phrase.LatestRecordingEvidence ?? phrase.RecordingEvidence;
        // A late preview or restored playable cache must not erase a longer final recording.
        if (retained != null && evidence.RawSeconds < retained.RawSeconds - .01f) return;
        if (EquivalentRecordingEvidence(retained, evidence))
        {
            phrase.LatestRecordingEvidence = retained;
            return;
        }
        phrase.LatestRecordingEvidence = CloneSingingEvidence(evidence);
    }

    private static bool EquivalentRecordingEvidence(SingingEvidenceSnapshot a, SingingEvidenceSnapshot b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a == null || b == null) return false;
        return a.TimelineOriginSeconds == b.TimelineOriginSeconds &&
            a.CaptureSessionSerial == b.CaptureSessionSerial &&
            a.FrameSeconds == b.FrameSeconds &&
            a.Text == b.Text &&
            a.SingingText == b.SingingText &&
            a.Language == b.Language &&
            a.RawSeconds == b.RawSeconds &&
            a.CleanStartSeconds == b.CleanStartSeconds &&
            a.CleanEndSeconds == b.CleanEndSeconds &&
            a.RecoveryStartSeconds == b.RecoveryStartSeconds &&
            a.RecoveryEndSeconds == b.RecoveryEndSeconds &&
            a.SingingProbability == b.SingingProbability &&
            a.PitchStability == b.PitchStability &&
            a.MelodicIslandSeconds == b.MelodicIslandSeconds &&
            a.ContentSeconds == b.ContentSeconds &&
            a.HeadExtraSeconds == b.HeadExtraSeconds &&
            a.TailExtraSeconds == b.TailExtraSeconds &&
            a.HeadExtraText == b.HeadExtraText &&
            a.TailExtraText == b.TailExtraText &&
            a.HeadExtraType == b.HeadExtraType &&
            a.TailExtraType == b.TailExtraType &&
            a.HeadExtraProbability == b.HeadExtraProbability &&
            a.TailExtraProbability == b.TailExtraProbability &&
            a.HeadExtraReviewRequired == b.HeadExtraReviewRequired &&
            a.TailExtraReviewRequired == b.TailExtraReviewRequired &&
            a.HeadExtraMelodicSeconds == b.HeadExtraMelodicSeconds &&
            a.TailExtraMelodicSeconds == b.TailExtraMelodicSeconds &&
            a.HeadExtraMelodicRatio == b.HeadExtraMelodicRatio &&
            a.TailExtraMelodicRatio == b.TailExtraMelodicRatio &&
            a.HeadExtraLongestMelodicRunSeconds == b.HeadExtraLongestMelodicRunSeconds &&
            a.TailExtraLongestMelodicRunSeconds == b.TailExtraLongestMelodicRunSeconds &&
            a.CleanLeadInUnverifiedSeconds == b.CleanLeadInUnverifiedSeconds &&
            a.CleanLeadInEvidenceText == b.CleanLeadInEvidenceText &&
            a.CleanLeadInEvidenceType == b.CleanLeadInEvidenceType &&
            a.CleanLeadInEvidenceProbability == b.CleanLeadInEvidenceProbability &&
            (a.RawWavBytes ?? new byte[0]).SequenceEqual(b.RawWavBytes ?? new byte[0]) &&
            (a.PitchTimelineMidi ?? new float[0]).SequenceEqual(b.PitchTimelineMidi ?? new float[0]) &&
            (a.HeadExtraSegments ?? new SingingBoundarySubsegment[0]).Select(s => JsonUtility.ToJson(s))
                .SequenceEqual((b.HeadExtraSegments ?? new SingingBoundarySubsegment[0]).Select(s => JsonUtility.ToJson(s))) &&
            (a.TailExtraSegments ?? new SingingBoundarySubsegment[0]).Select(s => JsonUtility.ToJson(s))
                .SequenceEqual((b.TailExtraSegments ?? new SingingBoundarySubsegment[0]).Select(s => JsonUtility.ToJson(s)));
    }


    private void UpdateRetainedRecordingEvidence(SingingEvidenceSnapshot evidence)
    {
        if (evidence == null) return;
        foreach (var phrase in m_PracticePhrases) RetainRecordingEvidence(phrase, evidence);
        foreach (var candidate in m_QuarantinedSingingCandidates)
            RetainRecordingEvidence(candidate.Phrase, evidence);
    }

    // Returned separately from the CURRENT audio facts. Explicit range selection
    // uses this evidence; merely receiving a final ASR result never revises playback.
    public string DescribeLatestSingingRecordingEvidence(string clipRef)
    {
        var phrase = m_PracticePhrases.Find(p => p.ClipRef == clipRef) ??
            m_QuarantinedSingingCandidates.Find(c => c.Phrase.ClipRef == clipRef)?.Phrase;
        var e = phrase?.LatestRecordingEvidence;
        if (e == null || ReferenceEquals(e, phrase.RecordingEvidence)) return "";
        string Text(string value) => (value ?? "").Replace("\r", " ").Replace("\n", " ");
        string Segments(SingingBoundarySubsegment[] segments) => segments == null ? "[]" :
            "[" + string.Join(";", segments.Where(s => s != null).Select(s =>
                $"raw_window=[{e.RecoveryStartSeconds + s.expanded_start_seconds:F2}," +
                $"{e.RecoveryStartSeconds + s.expanded_end_seconds:F2}] type={s.type}")) + "]";
        return
            $"[Sing/LatestRecording] ref={clipRef} current_audio={phrase.Seconds:F2}s raw={e.RawSeconds:F2}s " +
            $"clean=[{e.CleanStartSeconds:F2},{e.CleanEndSeconds:F2}] expanded=[{e.RecoveryStartSeconds:F2},{e.RecoveryEndSeconds:F2}] " +
            $"pitch_timeline={(HasPlayablePitchTimeline(e.PitchTimelineMidi) ? "available" : "unavailable")} acoustic_p={e.SingingProbability:F3} " +
            $"transcript=\"{Text(e.Text)}\" singing_transcript=\"{Text(e.SingingText)}\" " +
            $"head_extra={e.HeadExtraSeconds:F2}s/{e.HeadExtraType}/review={e.HeadExtraReviewRequired} text=\"{Text(e.HeadExtraText)}\" segments={Segments(e.HeadExtraSegments)} " +
            $"tail_extra={e.TailExtraSeconds:F2}s/{e.TailExtraType}/review={e.TailExtraReviewRequired} text=\"{Text(e.TailExtraText)}\" segments={Segments(e.TailExtraSegments)} " +
            $"clean_lead_unverified={e.CleanLeadInUnverifiedSeconds:F2}s text=\"{Text(e.CleanLeadInEvidenceText)}\"；" +
            $"这是完整录音证据，不是当前播放范围或来源结论。显式 range/起止时间基于此证据；current 保持已有版本。";
    }

    private static void CopyEvidenceBoundaries(PracticePhrase phrase, SingingEvidenceSnapshot evidence)
    {
        phrase.HeadExtraSeconds = evidence.HeadExtraSeconds;
        phrase.TailExtraSeconds = evidence.TailExtraSeconds;
        phrase.HeadExtraText = evidence.HeadExtraText;
        phrase.TailExtraText = evidence.TailExtraText;
        phrase.HeadExtraType = evidence.HeadExtraType;
        phrase.TailExtraType = evidence.TailExtraType;
        phrase.HeadExtraProbability = evidence.HeadExtraProbability;
        phrase.TailExtraProbability = evidence.TailExtraProbability;
        phrase.HeadExtraReviewRequired = evidence.HeadExtraReviewRequired;
        phrase.TailExtraReviewRequired = evidence.TailExtraReviewRequired;
        phrase.HeadExtraMelodicSeconds = evidence.HeadExtraMelodicSeconds;
        phrase.TailExtraMelodicSeconds = evidence.TailExtraMelodicSeconds;
        phrase.HeadExtraMelodicRatio = evidence.HeadExtraMelodicRatio;
        phrase.TailExtraMelodicRatio = evidence.TailExtraMelodicRatio;
        phrase.HeadExtraLongestMelodicRunSeconds = evidence.HeadExtraLongestMelodicRunSeconds;
        phrase.TailExtraLongestMelodicRunSeconds = evidence.TailExtraLongestMelodicRunSeconds;
        phrase.HeadExtraSegments = evidence.HeadExtraSegments;
        phrase.TailExtraSegments = evidence.TailExtraSegments;
        phrase.CleanLeadInUnverifiedSeconds = evidence.CleanLeadInUnverifiedSeconds;
        phrase.CleanLeadInEvidenceText = evidence.CleanLeadInEvidenceText;
        phrase.CleanLeadInEvidenceType = evidence.CleanLeadInEvidenceType;
        phrase.CleanLeadInEvidenceProbability = evidence.CleanLeadInEvidenceProbability;
    }

    private bool ValidatePreparedEvidenceRange(int index, PracticePhrase prepared,
        bool excludeSpeech, out string failure)
    {
        // The existing validator is synchronous and side-effect-free. Scope its
        // view to the staged version, restoring the real list even on failure.
        var original = m_PracticePhrases[index];
        try
        {
            m_PracticePhrases[index] = prepared;
            return TryValidatePracticeBoundarySelection("stable:" + prepared.StableId,
                false, 0f, 0f, excludeSpeech, out failure);
        }
        finally { m_PracticePhrases[index] = original; }
    }

    private static bool TryPrepareEvidenceRange(PracticePhrase original, SingingEvidenceSnapshot evidence,
        string capture, float trimHead, float trimTail, out PracticePhrase prepared, out string error)
    {
        prepared = null;
        error = "";
        if (evidence == null || !HasUsableWavPayload(evidence.RawWavBytes))
        { error = "该候选没有完整原始边界证据，无法恢复；可以询问用户。"; return false; }
        capture = (capture ?? "").Trim().ToLowerInvariant();
        if ((capture != "clean" && capture != "expanded") || trimHead < 0f || trimTail < 0f ||
            float.IsNaN(trimHead) || float.IsNaN(trimTail) || float.IsInfinity(trimHead) || float.IsInfinity(trimTail))
        { error = "capture 只能为 clean/expanded，裁剪必须为非负有限秒数。"; return false; }
        float sourceStart = capture == "expanded" ? evidence.RecoveryStartSeconds : evidence.CleanStartSeconds;
        float sourceEnd = capture == "expanded" ? evidence.RecoveryEndSeconds : evidence.CleanEndSeconds;
        float start = sourceStart + trimHead, end = sourceEnd - trimTail;
        // Three seconds is the automatic evidence threshold, not an execution
        // veto on an explicitly selected, voiced short phrase.
        if (end - start < 1f || start < evidence.TimelineOriginSeconds - .05f || end > evidence.RawSeconds + .05f)
        { error = "所选范围不足 1 秒、超出录音或缺少对应音高时间线；未改动候选。"; return false; }
        float frame = Mathf.Clamp(evidence.FrameSeconds, .02f, .25f);
        float[] timeline = SliceFloatArray(evidence.PitchTimelineMidi,
            Mathf.FloorToInt(Mathf.Max(0f, start - evidence.TimelineOriginSeconds) / frame),
            Mathf.CeilToInt(Mathf.Max(0f, end - evidence.TimelineOriginSeconds) / frame));
        if (!HasPlayablePitchTimeline(timeline))
        { error = "所选范围缺少可用旋律时间线；没有替换为旧旋律。"; return false; }
        byte[] audio = TrimWavWindow(evidence.RawWavBytes, start, end, out _, out _);
        if (!HasUsableWavPayload(audio)) { error = "所选范围音频解码失败。"; return false; }
        var phrase = original.CopyForRevision();
        phrase.RecordingEvidence = evidence;
        phrase.Language = evidence.Language ?? phrase.Language;
        CopyEvidenceBoundaries(phrase, evidence);
        phrase.WavBytes = audio;
        phrase.MidiTimeline = timeline;
        phrase.FrameSeconds = frame;
        phrase.Seconds = GetWavDurationSeconds(audio);
        phrase.SourceCleanWavBytes = TrimWavWindow(evidence.RawWavBytes,
            evidence.CleanStartSeconds, evidence.CleanEndSeconds, out _, out _);
        phrase.SourceExpandedWavBytes = TrimWavWindow(evidence.RawWavBytes,
            evidence.RecoveryStartSeconds, evidence.RecoveryEndSeconds, out _, out _);
        phrase.SourceCleanMidiTimeline = SliceFloatArray(evidence.PitchTimelineMidi,
            Mathf.FloorToInt(Mathf.Max(0f, evidence.CleanStartSeconds - evidence.TimelineOriginSeconds) / frame),
            Mathf.CeilToInt(Mathf.Max(0f, evidence.CleanEndSeconds - evidence.TimelineOriginSeconds) / frame));
        phrase.SourceExpandedMidiTimeline = SliceFloatArray(evidence.PitchTimelineMidi,
            Mathf.FloorToInt(Mathf.Max(0f, evidence.RecoveryStartSeconds - evidence.TimelineOriginSeconds) / frame),
            Mathf.CeilToInt(Mathf.Max(0f, evidence.RecoveryEndSeconds - evidence.TimelineOriginSeconds) / frame));
        phrase.RecoveryWavBytes = phrase.SourceExpandedWavBytes;
        phrase.RecoveryMidiTimeline = phrase.SourceExpandedMidiTimeline;
        phrase.OriginalCleanSeconds = evidence.CleanEndSeconds - evidence.CleanStartSeconds;
        phrase.OriginalExpandedSeconds = evidence.RecoveryEndSeconds - evidence.RecoveryStartSeconds;
        phrase.RecoverySeconds = phrase.OriginalExpandedSeconds;
        phrase.ActiveCapture = capture;
        phrase.ActiveTrimHeadSeconds = trimHead;
        phrase.ActiveTrimTailSeconds = trimTail;
        // Keep the complete original recording in Evidence for further recovery.
        // Expanded text isn't automatically equated with the clean lyric transcript.
        phrase.Lyrics = capture == "clean" && trimHead == 0f && trimTail == 0f
            ? evidence.SingingText ?? "" : "";

        bool sameAudio = original.WavBytes != null && original.WavBytes.SequenceEqual(audio) &&
            original.MidiTimeline != null && original.MidiTimeline.SequenceEqual(timeline);
        if (!sameAudio && original.WavBytes != null && original.MidiTimeline != null &&
            original.MidiTimeline.SequenceEqual(timeline) &&
            TryDecodePcmWav(original.WavBytes, out var currentSamples, out int currentRate) &&
            TryDecodePcmWav(evidence.RawWavBytes, out var rawSamples, out int rawRate) && currentRate == rawRate)
            sameAudio = currentSamples.SequenceEqual(SliceFloatArray(rawSamples,
                Mathf.RoundToInt(start * rawRate), Mathf.RoundToInt(end * rawRate)));
        if (sameAudio)
        {
            phrase.WavBytes = original.WavBytes;
            phrase.MidiTimeline = original.MidiTimeline;
        }
        if (!sameAudio) phrase.Revision++;
        prepared = phrase;
        return true;
    }
}
