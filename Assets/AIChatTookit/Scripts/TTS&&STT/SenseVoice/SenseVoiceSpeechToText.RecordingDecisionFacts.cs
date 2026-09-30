using System.Linq;
using UnityEngine;

public partial class SenseVoiceSpeechToText
{
    private int m_LastTurnSegmentsCaptureSessionSerial;
    // A detached value snapshot for the current accepted final recording only.
    // The consumer separately binds its input transaction/generation. No acoustic
    // conclusion, source confirmation, range choice or recording mutation occurs.
    public object DescribeTimeOrderedTranscriptForFinalInput(string finalInput)
    {
        if (finalInput != LastText || !HasTimeOrderedTranscript ||
            m_LastTurnSegmentsCaptureSessionSerial != InputCaptureSessionSerial ||
            DescribeRetainedRecordingForFinalInput(finalInput) == null) return null;
        return new {
            captureSessionSerial = InputCaptureSessionSerial,
            segments = m_LastTurnSegments.Where(s => s != null).Select(s => new {
                s.id, s.start_seconds, s.end_seconds, s.region, s.type, s.type_source,
                s.text, s.language, s.status, s.confidence_available, s.review_required,
                alternatives = s.alternatives?.Where(a => a != null).Select(a => new {
                    a.source, a.text, a.language, a.start_seconds, a.end_seconds }).ToArray()
            }).ToArray()
        };
    }

    // A route may inspect availability, but may not infer it from a cache alias
    // or from the number of unrelated recordings. Bind this view to BOTH the
    // live capture identity and the exact final text of the current request.
    public object DescribeRetainedRecordingForFinalInput(string finalInput)
    {
        var current = m_LastSingingEvidence;
        if (current == null || current.CaptureSessionSerial <= 0 ||
            current.CaptureSessionSerial != m_LiveRecordingCandidateSessionSerial ||
            string.IsNullOrEmpty(finalInput) || current.Text != finalInput) return null;
        var phrase = m_PracticePhrases.FirstOrDefault(p => SameRecording(p, current)) ??
            m_QuarantinedSingingCandidates.FirstOrDefault(c => SameRecording(c.Phrase, current))?.Phrase;
        var retained = phrase?.LatestRecordingEvidence ?? phrase?.RecordingEvidence;
        if (phrase == null || retained == null || retained.Text != finalInput ||
            !SameRecording(phrase, retained) || !HasUsableWavPayload(retained.RawWavBytes)) return null;
        var candidate = m_QuarantinedSingingCandidates.FirstOrDefault(c => c.Phrase == phrase);
        bool Range(float start, float end) => !float.IsNaN(start) && !float.IsNaN(end) &&
            !float.IsInfinity(start) && !float.IsInfinity(end) && start >= 0f &&
            end - start >= 1f && end <= retained.RawSeconds + .02f;
        return new {
            retained = true, matchesCurrentFinalInput = true,
            captureSessionSerial = retained.CaptureSessionSerial, clip = phrase.ClipRef,
            recordingTranscript = retained.Text, singingSegmentText = retained.SingingText ?? "",
            rawSeconds = retained.RawSeconds, pitchTimelineAvailable = HasPlayablePitchTimeline(retained.PitchTimelineMidi),
            recoverableRanges = new {
                clean = Range(retained.CleanStartSeconds, retained.CleanEndSeconds)
                    ? new[] { retained.CleanStartSeconds, retained.CleanEndSeconds } : null,
                expanded = Range(retained.RecoveryStartSeconds, retained.RecoveryEndSeconds)
                    ? new[] { retained.RecoveryStartSeconds, retained.RecoveryEndSeconds } : null },
            sourceStatus = candidate == null ? (phrase.PendingConfirmation ? "pending" : "confirmed_user") :
                (candidate.SourceConfirmed ? "confirmed_user" : "pending"),
            playbackStatus = candidate?.PlaybackStatus ?? "ready", contentVerdict = "undecided",
            scope = "这份原始录音已实际留存且与本轮最终输入同源；不是仅有歌词文字。只证明素材存在，不证明用户要求回唱、歌声/来源成立或允许播放；仍由角色理解请求并经独立审核。"
        };
    }

    // A read-only semantic view: full transcript appears once, and current
    // playback coordinates stay separate from the latest retained raw evidence.
    // No range selection, revision change, source verdict or playback occurs.
    public object DescribeRecordingSingingDecisionFacts(string reference)
    {
        var phrase = FindSingingClip(reference);
        if (phrase == null) return null;
        var e = phrase.LatestRecordingEvidence ?? phrase.RecordingEvidence;
        var current = phrase.RecordingEvidence;
        var candidate = m_QuarantinedSingingCandidates.Find(c => c.Phrase == phrase);
        bool ready = candidate == null || candidate.PlaybackStatus == "ready";
        bool rawAudioRetained = e != null && SameRecording(phrase, e) && e.RawWavBytesStoredLength > 44;
        bool originalAudioAvailable = rawAudioRetained || phrase.SourceCleanWavBytesStoredLength > 44 ||
            phrase.SourceExpandedWavBytesStoredLength > 44;
        bool preparedAudioAvailable = ready && phrase.WavBytesStoredLength > 44;
        string capture = ready ? phrase.ActiveCapture ?? "clean" : "unselected";
        float currentStart = capture == "expanded" ? current?.RecoveryStartSeconds ?? 0f : current?.CleanStartSeconds ?? 0f;
        float currentEnd = capture == "expanded" ? current?.RecoveryEndSeconds ?? 0f : current?.CleanEndSeconds ?? 0f;
        object overlapFacts = null;
        if (m_CaptureOverlaps.TryGetValue(phrase.CaptureSessionSerial, out var overlap))
        {
            var earlier = m_PracticePhrases.Find(p => p.CaptureSessionSerial == overlap.EarlierSession) ??
                m_QuarantinedSingingCandidates.Find(c => c.Phrase.CaptureSessionSerial == overlap.EarlierSession)?.Phrase;
            overlapFacts = new { shared_prefix_raw = new[] { 0f, overlap.PrefixSeconds },
                earlier_ref = earlier?.ClipRef ?? "not_retained", overlap_not_repeat = true, speaker_continuity = "unknown" };
        }
        object[] Segments(SingingBoundarySubsegment[] values) => values == null ? new object[0] :
            values.Where(s => s != null).Select(s => (object)new {
                rawStartSeconds = (e?.RecoveryStartSeconds ?? 0f) + s.expanded_start_seconds,
                rawEndSeconds = (e?.RecoveryStartSeconds ?? 0f) + s.expanded_end_seconds, s.type }).ToArray();
        return new {
            phrase.RecordingSequence, phrase.CaptureSessionSerial,
            RecordingTranscript = e?.Text ?? "", SingingSegmentText = e?.SingingText ?? "",
            // Actual buffer facts, distinct from source confirmation, preparation
            // and a semantic singing verdict. No permission is granted here.
            RawAudioRetained = rawAudioRetained, RawAudioByteCount = rawAudioRetained ? e.RawWavBytesStoredLength : 0,
            HasAvailableOriginalAudio = originalAudioAvailable, PreparedAudioAvailable = preparedAudioAvailable,
            phrase.Lyrics, phrase.PrecedingSpeech,
            AgoSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - phrase.AtRealtime),
            SourceStatus = candidate == null ? (phrase.PendingConfirmation ? "pending" : "confirmed_user") :
                (candidate.SourceConfirmed ? "confirmed_user" : "pending"),
            AcousticMode = e?.AcousticMode ?? "unavailable", AudioEvent = e?.AudioEvent ?? "",
            SingingProbability = e?.SingingProbability ?? 0f, ContentVerdict = "undecided",
            PitchTimelineAvailable = e != null && HasPlayablePitchTimeline(e.PitchTimelineMidi),
            RawSeconds = e?.RawSeconds ?? 0f,
            CleanStartSeconds = e?.CleanStartSeconds ?? 0f, CleanEndSeconds = e?.CleanEndSeconds ?? 0f,
            ExpandedStartSeconds = e?.RecoveryStartSeconds ?? 0f, ExpandedEndSeconds = e?.RecoveryEndSeconds ?? 0f,
            currentPlayback = new { selected = ready, capture, phrase.Revision, phrase.Seconds,
                CurrentStartSeconds = ready ? currentStart + phrase.ActiveTrimHeadSeconds : 0f,
                CurrentEndSeconds = ready ? currentEnd - phrase.ActiveTrimTailSeconds : 0f },
            HeadExtraReviewRequired = e?.HeadExtraReviewRequired ?? false,
            TailExtraReviewRequired = e?.TailExtraReviewRequired ?? false,
            HeadExtraText = e?.HeadExtraText ?? "", TailExtraText = e?.TailExtraText ?? "",
            HeadExtraType = e?.HeadExtraType ?? "none", TailExtraType = e?.TailExtraType ?? "none",
            HeadSegments = Segments(e?.HeadExtraSegments), TailSegments = Segments(e?.TailExtraSegments),
            CleanLeadInUnverifiedSeconds = e?.CleanLeadInUnverifiedSeconds ?? 0f,
            CleanLeadInEvidenceText = e?.CleanLeadInEvidenceText ?? "", overlap = overlapFacts,
            pcmLineage = DescribeRecordingPcmLineage(phrase.CaptureSessionSerial, e?.RawSeconds ?? 0f),
            scope = "录音证据不是唱歌或来源结论；currentPlayback属于现有播放版本，显式clean/expanded使用上列最新原始录音坐标，执行前仍须审核与验证。"
        };
    }
}
