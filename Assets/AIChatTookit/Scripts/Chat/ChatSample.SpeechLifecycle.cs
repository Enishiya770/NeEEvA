using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private AudioClip m_SettledOpeningClip;
    private float m_SettledOpeningStartedAt = -1f;
    private float m_OrphanSpeechSince = -1f;

    // A producer may still be planning after an opening ends. Only presentation
    // and microphone protection end here; no task/result is declared complete.
    private void ReleaseSpeechPresentation(int owner, string reason)
    {
        if (owner != m_FormalResponseGeneration || IsVoiceOutputPlaying || IsSingingRuntimeActivityActive()) return;
        bool notify = IsAISpeaking;
        IsAISpeaking = false;
        m_CurrentlyPlayingText = "";
        StopDirectStreamingSubtitle();
        m_SubtitleOverlay?.CancelUtterance(owner, true);
        if (m_TextBack != null) m_TextBack.text = "";
        SetAnimator("state", 0);
        // Preserve the measured end time, rather than adding another acoustic
        // tail each time a cancelled owner releases an already-ended opening.
        if (m_WasVoiceOutputPlaying) m_LastVoiceOutputEndedRealtime = Time.realtimeSinceStartup;
        m_OrphanSpeechSince = -1f;
        Debug.Log("[Speech/Lifecycle] " + new JObject { ["generation"] = owner, ["reason"] = reason,
            ["logicalSpeaking"] = IsAISpeaking, ["actualPlaying"] = IsVoiceOutputPlaying,
            ["formalPending"] = m_FormalResponseInFlight, ["listenerNotified"] = notify,
            ["taskCompleted"] = false }.ToString(Formatting.None), this);
        if (notify) OnAISpeakDone?.Invoke();
    }

    private void TickSpeechLifecycle()
    {
        ObservePreparedPlaybackEvidence();
        if (m_SceneTimePaused || Time.timeScale <= 0) return;
        ObserveLatencyFillerProgress();
        if (m_LatencyFillerPlayed && !m_RealFirstAudioStarted && m_LatencyFillerClip != null &&
            (m_SettledOpeningClip != m_LatencyFillerClip || m_SettledOpeningStartedAt != m_LatencyFillerStartedAt) &&
            !IsVoiceOutputPlaying && !IsSingingRuntimeActivityActive())
        {
            m_SettledOpeningClip = m_LatencyFillerClip;
            m_SettledOpeningStartedAt = m_LatencyFillerStartedAt;
            ReleaseSpeechPresentation(m_OpeningOwner,
                m_LatencyProgressComplete ? "opening-audio-completed" : "opening-ended-without-completion-proof");
            // Keep the receipt and prefix until the formal handoff consumes it.
            // The delayed Played flag is NOT a speaking/microphone lock.
        }
        bool orphan = IsAISpeaking && !IsVoiceOutputPlaying && m_StreamComplete && m_TTSSenderDone &&
            m_PendingChunks.Count == 0 && m_PendingClips.Count == 0 && m_SentenceBuffer.Length == 0 &&
            !m_FormalResponseInFlight && !IsSingingRuntimeActivityActive();
        if (!orphan) { m_OrphanSpeechSince = -1f; return; }
        if (m_OrphanSpeechSince < 0f) m_OrphanSpeechSince = Time.realtimeSinceStartup;
        if (Time.realtimeSinceStartup - m_OrphanSpeechSince >= 2f)
            ReleaseSpeechPresentation(m_FormalResponseGeneration, "orphan-speaking-state-recovered");
    }

    // Called before revoking the generation. Old callbacks cannot invoke this
    // after a replacement has acquired the source. Actual progress is retained.
    private void CancelOwnedSpeechOutput(int owner, string reason)
    {
        if (owner != m_FormalResponseGeneration) return;
        SettleEndedPreparedPlayback();
        ObserveLatencyFillerProgress();
        if (m_LatencyFillerPlayed) {
            if(IsOwnedLatencyFillerPlaying())FreezeOpeningPlayback(reason);
            CommitPlayedLatencyFiller();
        }
        else if (IsAISpeaking && !string.IsNullOrEmpty(m_CurrentlyPlayingText))
            RecordActivityInterruption(m_CurrentlyPlayingText, reason);
        FinishRoomSpeechPlaybackHistory(m_AssistantHeardText.ToString());
        m_SentenceBuffer.Length = 0; ClearPendingSpeechChunks("queue-cleared"); ClearPendingSpeechClips("queue-cleared");
        m_StreamComplete = true; m_TTSSenderDone = true;
        // Singing has an independent executor/owner. Cancelling room speech
        // does not revoke it or seize its AudioSource.
        if (!IsSingingRuntimeActivityActive()) {
            m_ChatSettings?.m_TextToSpeech?.CancelStreaming();
            m_ChatSettings?.m_TextToSpeech?.CancelPreparedSpeech();
            if (m_AudioSource != null && m_AudioSource.isPlaying) {
                StopSpeechAudio(); m_LastVoiceOutputEndedRealtime = Time.realtimeSinceStartup;
            }
            ReleaseSpeechPresentation(owner, reason);
        }
    }
}
