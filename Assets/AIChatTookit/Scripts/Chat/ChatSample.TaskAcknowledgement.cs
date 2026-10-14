using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private int m_AcceptedTaskAcknowledgementGeneration = -1;

    // Called only after an executor/search lease accepts current user work.
    // It has no target/state/completion claim and never delays the action.
    private void AcknowledgeAcceptedTask(int generation, string owner)
    {
        if (!m_EnableLatencyFiller || generation != m_FormalResponseGeneration || string.IsNullOrEmpty(owner) ||
            m_AgentCurrentRoundIsTick || m_RealFirstAudioStarted || m_LatencyFillerPlayed ||
            m_AcceptedTaskAcknowledgementGeneration == generation || m_AudioSource == null ||
            m_AudioSource.isPlaying || m_ChatSettings?.m_TextToSpeech == null) return;
        string language = SpatialGoalReportLanguage(null);
        if (!m_ChatSettings.m_TextToSpeech.TryPlayLatencyFiller(language, "accepted-task", m_AudioSource,
                out string text, out float duration)) return;
        m_AcceptedTaskAcknowledgementGeneration = generation;
        m_LatencyFillerGeneration = generation;
        m_LatencyFillerPlayed = true;
        m_LatencyFillerFromEou = true; // preserve this owned clip through formal writer startup
        m_LatencyFillerClip = m_AudioSource.clip;
        m_LatencyFillerText = text;
        m_LatencyFillerStartedAt = Time.realtimeSinceStartup;
        m_LatencyFillerDuration = duration;
        m_CurrentlyPlayingText = text;
        if (m_TextBack != null) m_TextBack.text = text;
        SetAnimator("state", 2); IsAISpeaking = true;
        Debug.Log("[Room/AcceptedTaskAcknowledgement] " + new JObject {
            ["generation"]=generation, ["owner"]=owner, ["language"]=language, ["text"]=text,
            ["scope"]="Current task accepted; no completion asserted. Playback progress is measured separately."
        }.ToString(Newtonsoft.Json.Formatting.None), this);
    }
}
