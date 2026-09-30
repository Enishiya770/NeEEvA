using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public partial class SenseVoiceSpeechToText
{
    private readonly RecordingContinuationIsolation m_ContinuationIsolation = new RecordingContinuationIsolation();
    private readonly Dictionary<int, float> m_IsolatedCaptureEvidence = new Dictionary<int, float>();

    public bool IsCaptureRejectedSelfSpeech(int capture) => m_ContinuationIsolation.IsRejected(capture);

    private void ObserveCaptureIsolation(int capture, bool completed, string reason, float seconds)
    {
        if (!m_ContinuationIsolation.Reject(capture, completed, reason, seconds)) return;
        InvalidateRejectedCaptureEvidence(capture, seconds);
        var descendants = m_PracticePhrases.Select(p => p.CaptureSessionSerial)
            .Concat(m_QuarantinedSingingCandidates.Select(c => c.Phrase.CaptureSessionSerial))
            .Concat(new[] { m_LiveRecordingCandidateSessionSerial, m_LastSingingCacheCaptureSessionSerial,
                m_RollbackSingingCacheCaptureSessionSerial }).Distinct().ToArray();
        foreach (int descendant in descendants)
            InvalidateRejectedCaptureEvidence(descendant, m_ContinuationIsolation.InheritedRejectedSeconds(descendant));
        Debug.Log($"[ASR/InputIsolation] capture={capture} rejectedPrefix={seconds:F3}s reason=self-speech; " +
            "sealed PCM cannot regain user identity through continuation");
    }

    private bool IsolateContinuationAudio(int capture, ref byte[] audio, ref float excludedSeconds)
    {
        float required = m_ContinuationIsolation.InheritedRejectedSeconds(capture);
        float additional = required - excludedSeconds;
        if (additional <= 0.0001f) return true;
        InvalidateRejectedCaptureEvidence(capture, required);
        if (!RecordingContinuationIsolation.TryRemovePrefix(audio, additional, out byte[] suffix, out float removed))
            return false;
        WaveformTrace.Transform(audio, suffix, removed, WaveformTrace.Duration(audio));
        audio = suffix;
        excludedSeconds += removed;
        // The old inventory prefix coordinates describe the untrimmed upload.
        // Retain the private isolation lineage, but do not publish that stale mapping.
        m_CaptureOverlaps.Remove(capture);
        RegisterRecordingPcmLineage(audio, capture);
        Debug.Log($"[ASR/InputIsolation] capture={capture} removedPrefix={excludedSeconds:F3}s " +
            $"remaining={GetWavDurationSeconds(audio):F3}s; analyze only the new-user suffix");
        return HasUsableWavPayload(audio);
    }

    private void InvalidateRejectedCaptureEvidence(int capture, float rejectedSeconds)
    {
        if (capture <= 0 || rejectedSeconds <= 0f ||
            m_IsolatedCaptureEvidence.TryGetValue(capture, out float prior) && rejectedSeconds <= prior) return;
        m_IsolatedCaptureEvidence[capture] = rejectedSeconds;
        m_RecordingPcmLineage.Retire(capture);
        // A preview may have retained the untrimmed waveform already. Its longer
        // evidence must not beat the freshly analysed, shorter user suffix. Drop
        // only this capture's old versions. The capture/event sequence stays, but
        // the rejected source's clip identity is retired: even an old pending
        // goal must not promote onto different PCM after this exceptional crop.
        m_ClipIdentities.Remove(capture);
        int removed = m_PracticePhrases.RemoveAll(p => p.CaptureSessionSerial == capture) +
            m_QuarantinedSingingCandidates.RemoveAll(c => c.Phrase.CaptureSessionSerial == capture);
        if (m_RollbackSingingCacheCaptureSessionSerial == capture)
        {
            m_RollbackSingingAudioBytes = m_RollbackSingingRecoveryAudioBytes = null;
            m_RollbackSingingPerformanceMidi = m_RollbackSingingRecoveryPerformanceMidi = new float[0];
            m_RollbackSingingLyrics = m_RollbackSingingPerformanceLanguage = "";
            m_RollbackSingingAudioTime = m_RollbackSingingPerformanceTime = -999f;
            m_RollbackSingingPerformanceScore = null;
            m_RollbackSingingCacheEvidence = null;
            m_RollbackSingingCacheCaptureSessionSerial = 0;
        }
        if (m_LastSingingCacheCaptureSessionSerial == capture)
            RevokeSingingPlaybackAlias("terminal self-speech invalidated retained capture PCM", capture);
        if (m_LastSingingEvidence?.CaptureSessionSerial == capture) m_LastSingingEvidence = null;
        if (m_LastPlayableCandidateEvidence?.CaptureSessionSerial == capture)
        {
            m_LastPlayableCandidateEvidence = null;
            m_LastPlayableCandidateAudioBytes = null;
            m_LastPlayableCandidatePitchTimelineMidi = new float[0];
            m_LastPlayableCandidateTime = -999f;
        }
        if (capture == m_LiveRecordingCandidateSessionSerial) m_CurrentRecordingEvidencePublished = false;
        Debug.Log($"[ASR/InputIsolation] capture={capture} revokedRetainedVersions={removed}; " +
            "fresh suffix requires new evidence/source confirmation; unrelated recordings retained");
    }

}
