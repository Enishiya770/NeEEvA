using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private SpeechText? m_PendingInputFailureReceipt;
    private int m_LastReportedInputFailureCapture = -1;
    private SenseVoiceSpeechToText m_LastReportedInputFailureProvider;

    // Provenance only. Recording a weak candidate does not grant interruption
    // or invalidate the pending formal answer.
    public void NotifyUserRecordingCandidate() => m_RealtimeUserTurnPending = true;

    private void HandleFailedFinalSpeech(SenseVoiceSpeechToText sense)
    {
        // A failed new recording is not proof that the user said nothing. Do
        // not answer the old question in place of that missing utterance.
        m_InterruptedUserQuestion = null;
        m_InterruptedHeardText = "";
        m_UserSpeechActiveForAutonomy = false;
        m_UserTurnAwaitingReplySince = -1f;
        CancelAutonomyIntentProbe();
        m_WorkReviewOpen = false;
        m_WorkStatus = "waiting_user";
        m_WorkIntent = "";
        ClearScheduledAgentWake();
        string reason = sense?.LastFinalRejectionReason ?? "input-error";
        int capture = sense?.LastFinalCaptureSerial ?? -1;
        Debug.LogWarning($"[ASR/InputFailure] capture={capture} reason={reason}; " +
            "最终识别失败，保留为服务错误；未把预览当最终原话，也不恢复旧问题的回答。", this);
        if (m_RecordTips != null) m_RecordTips.text = "刚才的语音识别失败，请再说一次。";
        if (capture == m_LastReportedInputFailureCapture && ReferenceEquals(sense, m_LastReportedInputFailureProvider)) return;
        m_LastReportedInputFailureCapture = capture;
        m_LastReportedInputFailureProvider = sense;
        if (!m_AutoSend || !isActiveAndEnabled || m_ChatSettings?.m_ChatModel == null ||
            m_ChatSettings.m_TextToSpeech == null) return;
        m_AgentCurrentRoundIsTick = false;
        m_AgentRoundInFlight = m_AgentRunning;
        ClearRoundParsed();
        m_PendingInputFailureReceipt = new SpeechText("刚才那段话没听完整，可以再说一次吗？", "zh",
            "input-failure", SpeechActionDependency.Independent);
        StartStreaming("", false);
    }

    private bool CompletePendingInputFailureReceipt(SpeechText? receipt, int generation)
    {
        if (receipt == null) return false;
        m_RoundWaitForUser = true;
        m_ResponseTrace?.Mark("input.failure_receipt", new JObject {
            ["captureSerial"] = m_LastReportedInputFailureCapture,
            ["generation"] = generation, ["modelRequested"] = false,
            ["scope"] = "missing final input; clarification only, no old-question recovery or inferred user content"
        });
        CompleteFastLoopReply(m_ChatSettings.m_ChatModel, "", receipt.Value.Text, receipt.Value.LanguageCode, generation, false);
        return true;
    }
}
