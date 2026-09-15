using System;
using System.Collections;
using UnityEngine;

public partial class ChatSample
{
    [Tooltip("实验性：直接复用倾听草稿判断来收口。真实模型验证已出现未完成纠错被误判闭合，默认仅记录 shadow-hit，仍走成熟静音后的独立复核。")]
    [SerializeField] private bool m_EnablePreparedBoundaryReuse = false;
    private PreparedTurnBoundaryEvidence m_PreparedBoundaryEvidence;
    private PreparedTurnBoundary m_PreparedBoundary;
    private readonly PreparedListeningPolicy m_PreparedListeningPolicy = new PreparedListeningPolicy();
    private PreparedListeningTrace m_LastPreparedListeningTrace;

    private sealed class PreparedListeningTrace
    {
        public string Id;
        public int RequestVersion, InputRevision, TranscriptVersion;
        public PreparedTurnBoundaryEvidence Source;
        public float SentAt;
        public bool InFlight;
    }

    private static bool PreparedListeningEligible(string transcript) =>
        !string.IsNullOrWhiteSpace(transcript) && transcript.Trim().Length >= PreparedListeningPolicy.MinimumTranscriptChars;

    private bool NeedsLatestPreparedListening() =>
        m_PreparedListeningPolicy.NeedsLatest(m_PreparedBoundaryEvidence, m_InputAudioRevision);

    // The existing ordinary listening coroutine delegates here. This remains a single
    // low-priority request: no second model, extra concurrent request, or EOU wait.
    private IEnumerator RequestPreparedListeningAfterDelay(int transcriptVersion, string transcript)
    {
        float delay; string scheduleReason;
        if (!m_PreparedListeningPolicy.TryDelay(m_PreparedBoundaryEvidence, m_InputAudioRevision,
                Time.realtimeSinceStartup, m_LastSpeculativeRequestTime, m_SpeculativeMinRequestInterval,
                out delay, out scheduleReason))
        {
            m_SpeculativeDraftCoroutine = null;
            LogPreparedListeningLifecycle("skipped", scheduleReason, null);
            yield break;
        }
        LogPreparedListeningLifecycle("scheduled", scheduleReason + ":delay=" + delay.ToString("F2"), null);
        if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
        m_SpeculativeDraftCoroutine = null;
        if (transcriptVersion != m_StreamingTranscriptVersion || transcript != m_StreamingTranscript ||
            m_StreamingTurnIsSinging || m_SpeculativeRequestInFlight || m_TurnBoundaryRequestInFlight)
            yield break;

        PreparedTurnBoundaryEvidence source = m_PreparedBoundaryEvidence;
        float requestedAt = Time.realtimeSinceStartup;
        if (!string.Equals(transcript, (source.Transcript ?? "").Trim(), StringComparison.Ordinal) ||
            requestedAt < source.SampledAt || requestedAt - source.SampledAt > PreparedTurnBoundary.MaximumSnapshotAge)
        {
            LogPreparedListeningLifecycle("skipped", "source-evidence-not-current", null);
            yield break;
        }
        if (!m_PreparedListeningPolicy.MarkSent(source, m_InputAudioRevision, requestedAt)) yield break;

        m_LastDraftTranscript = transcript;
        m_LastSpeculativeRequestTime = requestedAt;
        int requestVersion = ++m_SpeculativeRequestVersion;
        m_SpeculativeRequestInFlight = true;
        int inputRevision = m_InputAudioRevision, audioMs = m_StreamingLatestAudioMs;
        var trace = new PreparedListeningTrace
        {
            Id = "listening-" + source.CaptureSerial + "-" + inputRevision + "-" + requestVersion,
            Source = source, InputRevision = inputRevision, RequestVersion = requestVersion,
            TranscriptVersion = transcriptVersion, SentAt = requestedAt, InFlight = true,
        };
        m_LastPreparedListeningTrace = trace;
        LogPreparedListeningLifecycle("sent", scheduleReason, trace);
        m_ChatSettings.m_ChatModel.ListeningPreparationRequestId = trace.Id;
        m_ChatSettings.m_ChatModel.PostListeningPreparationMsg(PreparedListeningProtocol.BuildPrompt(transcript), response =>
        {
            trace.InFlight = false;
            bool currentRequest = requestVersion == m_SpeculativeRequestVersion;
            LogPreparedListeningLifecycle(currentRequest ? "returned" : "stale-return", "responseChars=" + (response ?? "").Length, trace);
            if (!currentRequest) return;
            m_SpeculativeRequestInFlight = false;
            StagePreparedTurnBoundary(response, transcript, source, inputRevision, requestedAt);
            if (TranscriptContinuity(transcript, m_StreamingTranscript) < 0.55f)
            { ScheduleSpeculativeRefreshIfNeeded(); return; }
            SpeculativeDraft parsed = ParseSpeculativeDraft(response);
            if (parsed != null)
            {
                parsed.sourceTranscript = transcript;
                parsed.sourceInputRevision = inputRevision; parsed.sourceAudioMs = audioMs;
                StoreStreamingObservedMode(parsed, transcript);
                if (IsStrongSpeculativeSpeechVeto(parsed) && OnStableUserSpeechObserved != null)
                    OnStableUserSpeechObserved(m_LastObservedModeConfidence, m_StreamingLatestAudioMs);
                if (!string.IsNullOrWhiteSpace(parsed.draft))
                {
                    parsed.draft = StripAgentTagsForTTS(parsed.draft).Trim();
                    m_SpeculativeDraft = parsed;
                    if (m_LogSpeculativeListening)
                        Debug.Log($"[流式倾听] 临时草稿就绪 confidence={parsed.confidence:F2} mode={parsed.observed_mode}/{parsed.mode_confidence:F2}: \"{parsed.draft}\"");
                    if (!m_StreamingTurnIsSinging) PrepareSingingBridge(parsed);
                }
            }
            ScheduleSpeculativeRefreshIfNeeded();
        });
    }

    private void CancelPreparedListeningRequest(string reason)
    {
        PreparedListeningTrace trace = m_LastPreparedListeningTrace;
        if (trace == null || !trace.InFlight) return;
        trace.InFlight = false;
        // Cancellation still spends the capture budget. Rapid corrections cannot keep
        // resetting it and flooding the same model that must answer the final turn.
        LogPreparedListeningLifecycle("cancelled", reason, trace);
    }

    private void LogPreparedListeningLifecycle(string phase, string reason, PreparedListeningTrace trace)
    {
        var current = m_PreparedBoundaryEvidence;
        var source = trace == null ? current : trace.Source;
        Debug.Log($"[Semantic EOU/Listening] phase={phase} reason={reason} " +
            $"requestId={(trace == null ? "none" : trace.Id)} capture={source.CaptureSerial} " +
            $"inputRevision={(trace == null ? m_InputAudioRevision : trace.InputRevision)} " +
            $"requestVersion={(trace == null ? -1 : trace.RequestVersion)} " +
            $"transcriptVersion={(trace == null ? m_StreamingTranscriptVersion : trace.TranscriptVersion)} " +
            $"sourceTextRevision={source.TextRevision} currentTextRevision={current.TextRevision} " +
            $"sourceActivityRevision={source.ActivityRevision} currentActivityRevision={current.ActivityRevision} " +
            $"sourceAudioMs={source.AudioMs} currentAudioMs={current.AudioMs} " +
            $"sourceSampledAt={source.SampledAt:F3}s sentAt={(trace == null ? -1f : trace.SentAt):F3}s " +
            $"now={Time.realtimeSinceStartup:F3}s elapsed={(trace == null ? -1f : Time.realtimeSinceStartup - trace.SentAt):F3}s " +
            $"captureRequests={m_PreparedListeningPolicy.Count}/{PreparedListeningPolicy.MaximumRequestsPerCapture}");
    }

    // RTSpeechHandler publishes real recording facts. No microphone state is inferred
    // from draft text or model confidence, and this method never closes a recording.
    public void ObservePreparedTurnBoundaryEvidence(PreparedTurnBoundaryEvidence evidence)
    {
        PreparedTurnBoundaryEvidence previous = m_PreparedBoundaryEvidence;
        m_PreparedBoundaryEvidence = evidence;
        // ASR can still repeat the same words when the user resumes after a pause.
        // Only a real recorder activity revision schedules this refresh; coverage-only
        // updates do not. Normal update handling owns changed transcript scheduling.
        if (isActiveAndEnabled && m_EnableSpeculativeListening && previous.Recording && evidence.Recording &&
            previous.CaptureSerial == evidence.CaptureSerial &&
            previous.ActivityRevision != evidence.ActivityRevision &&
            string.Equals(evidence.Transcript, previous.Transcript, StringComparison.Ordinal) &&
            string.Equals((evidence.Transcript ?? "").Trim(), m_StreamingTranscript, StringComparison.Ordinal) &&
            m_ChatSettings != null && m_ChatSettings.m_ChatModel != null)
            ScheduleSpeculativeRefreshIfNeeded();
    }

    private void StagePreparedTurnBoundary(string response, string transcript,
        PreparedTurnBoundaryEvidence source, int inputRevision, float requestedAt)
    {
        m_PreparedBoundary = null; // A later continue/uncertain result supersedes older preparation.
        string reason = "not-evaluated";
        PreparedTurnBoundary candidate;
        float now = Time.realtimeSinceStartup;
        // The ordinary draft can tolerate ASR prefix growth. An endpoint cannot.
        if (!string.Equals(transcript, (source.Transcript ?? "").Trim(), StringComparison.Ordinal))
        {
            LogPreparedTurnBoundary("rejected", "draft-source-mismatch", null);
            return;
        }
        if (!PreparedTurnBoundary.TryCreate(response, source, inputRevision, requestedAt, now,
                out candidate, out reason, m_LastPreparedListeningTrace == null ? "fixture" : m_LastPreparedListeningTrace.Id) ||
            !candidate.MatchesLease(m_PreparedBoundaryEvidence, m_InputAudioRevision, now, out reason))
        {
            LogPreparedTurnBoundary("rejected", reason, candidate);
            return;
        }
        m_PreparedBoundary = candidate;
        LogPreparedTurnBoundary("ready", "conditional-only", candidate);
    }

    private bool TryConsumePreparedTurnBoundary(string transcript, out TurnBoundaryDecision decision)
    {
        decision = null;
        PreparedTurnBoundary candidate = m_PreparedBoundary;
        if (candidate == null)
        {
            LogPreparedTurnBoundary("miss", m_SpeculativeRequestInFlight ?
                "draft-still-in-flight" : "no-ready-candidate", null);
            return false;
        }
        m_PreparedBoundary = null; // A boundary review consumes the lease, including a miss.
        string reason = "not-evaluated";
        if (!string.Equals((transcript ?? "").Trim(),
                (m_PreparedBoundaryEvidence.Transcript ?? "").Trim(), StringComparison.Ordinal))
        {
            LogPreparedTurnBoundary("miss", "review-source-mismatch", candidate);
            return false;
        }
        if (m_StreamingTurnIsSinging ||
            !candidate.CanReuse(m_PreparedBoundaryEvidence, m_InputAudioRevision,
                Time.realtimeSinceStartup, out reason))
        {
            if (m_StreamingTurnIsSinging) reason = "semantic-singing-active";
            LogPreparedTurnBoundary("miss", reason, candidate);
            return false;
        }
        if (!m_EnablePreparedBoundaryReuse)
        {
            // Model validation found a semantically unfinished correction falsely
            // marked closed. Recorder evidence alone cannot prove semantic completeness.
            // Keep preparation observable without allowing it to replace the matured
            // independent boundary review until that failure has been resolved.
            LogPreparedTurnBoundary("shadow-hit", "experimental-reuse-disabled", candidate);
            return false;
        }
        decision = candidate.Decision;
        LogPreparedTurnBoundary("hit", reason, candidate);
        return true;
    }

    private void LogPreparedTurnBoundary(string outcome, string reason, PreparedTurnBoundary candidate)
    {
        float now = Time.realtimeSinceStartup;
        Debug.Log($"[Semantic EOU/Prepared] outcome={outcome} reason={reason} " +
            $"requestId={(candidate == null ? m_LastPreparedListeningTrace?.Id ?? "none" : candidate.RequestId)} " +
            $"capture={m_PreparedBoundaryEvidence.CaptureSerial} " +
            $"textRevision={m_PreparedBoundaryEvidence.TextRevision} " +
            $"activityRevision={m_PreparedBoundaryEvidence.ActivityRevision} " +
            $"quiet={m_PreparedBoundaryEvidence.QuietSeconds:F2}s " +
            $"audioMs={m_PreparedBoundaryEvidence.AudioMs} " +
            $"requestAge={(candidate == null ? m_LastPreparedListeningTrace == null ? -1f : now - m_LastPreparedListeningTrace.SentAt : now - candidate.RequestedAt):F2}s " +
            $"readyAge={(candidate == null ? -1f : now - candidate.PreparedAt):F2}s");
    }
}
