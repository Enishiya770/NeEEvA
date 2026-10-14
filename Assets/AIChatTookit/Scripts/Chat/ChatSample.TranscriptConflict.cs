using System.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private string m_TranscriptClarificationInput;
    private string m_TranscriptClarificationOutput;

    private bool TryResolveTranscriptConflict(string original, bool streamingExit)
    {
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        var packet = sense?.ConsumeTranscriptDisagreement();
        if (packet == null) return false;
        Debug.Log("[ASR/Disagreement] " + packet.ToString(Formatting.None), this);
        CancelPendingEouLatencyFiller("transcript-disagreement", true);
        RejectFastHumBackAfterFinal("transcript-disagreement");
        ResetSpeculativeTurn(); ResetStreamingSemanticSingingLatch();
        CancelClosedRoomPreparation("transcript-disagreement"); CancelSceneVisionStopPreparation();
        StartCoroutine(ResolveTranscriptConflict(packet, sense, original, streamingExit));
        return true;
    }

    private IEnumerator ResolveTranscriptConflict(JObject packet, SenseVoiceSpeechToText sense, string original, bool streamingExit)
    {
        int revision = m_InputAudioRevision, capture = sense.InputCaptureSessionSerial;
        int epoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn;
        var provider = m_ChatSettings?.m_ChatModel;
        bool Current() => isActiveAndEnabled && revision == m_InputAudioRevision &&
            epoch == m_WorkEpoch && userTurn == m_ActionConstraints.CurrentUserTurn &&
            m_ChatSettings?.m_ChatModel == provider && m_ChatSettings?.m_SpeechToText == sense &&
            capture == sense.InputCaptureSessionSerial && !m_SceneTimePaused;
        bool equivalent = false;
        ++m_FinalAsrRequestsInFlight;
        try {
            if (m_ChatSettings?.m_ChatModel is ChatQW model)
                yield return model.ReviewTranscriptComparison(packet, Current, value => equivalent = value);
        } finally { m_FinalAsrRequestsInFlight = Mathf.Max(0, m_FinalAsrRequestsInFlight - 1); }
        if (!Current()) yield break;
        if (equivalent) { DealingTextCallback(original, streamingExit); yield break; }
        string evidence = "[Unresolved audio input; neither ASR hypothesis is accepted user wording]\n" + packet.ToString(Formatting.None);
        if (m_RecordTips != null) m_RecordTips.text = "这句话的识别结果不一致，需要确认。";
        if (!m_AutoSend) { if (m_InputWord != null) m_InputWord.text = ""; yield break; }
        m_TranscriptClarificationInput = evidence;
        SendDataInternal(evidence, null, "[本轮语音未能可靠识别，等待澄清]");
    }

    private bool TryStartTranscriptClarification(string input)
    {
        if (m_TranscriptClarificationInput == null) return false;
        if (m_TranscriptClarificationInput != input) { m_TranscriptClarificationInput = null; return false; }
        m_TranscriptClarificationOutput = "<lang code=\"" + SpatialGoalReportLanguage(null) +
            "\"/><speech mode=\"independent\"/><say>" + TranscriptDisagreementPolicy.Clarification(SpatialGoalReportLanguage(null)) + "</say>";
        // Complete the normal user-turn lifecycle, but never assemble a scene
        // packet, route actions, admit prepared promises or select a past task.
        m_UserSpeechActiveForAutonomy = false; m_AgentRoundInFlight = m_AgentRunning;
        m_LastUserTurnTime = Time.realtimeSinceStartup;
        m_SingingRequestSubmittedSinceUserTurn = false; m_NoSingingWorkContinuations = 0;
        m_SceneAutonomyIntention = ""; m_QuietAutonomyRound = false;
        m_PostHumBackFeedbackActive = false; m_PostHumBackFeedbackSummary = "";
        m_AgentCurrentRoundIsTick = false; ClearRoundParsed(); CancelAutonomyIntentProbe();
        if (m_UseStreaming && m_IsVoiceMode && m_ChatSettings.m_TextToSpeech != null)
            StartStreaming(input, false);
        else {
            string output = m_TranscriptClarificationOutput;
            CommitTranscriptClarificationHistory(input, output);
            m_TranscriptClarificationInput = m_TranscriptClarificationOutput = null;
            CallBack(output);
        }
        return true;
    }

    private LLM.SendData CommitTranscriptClarificationHistory(string input, string output)
    {
        var model = m_ChatSettings?.m_ChatModel;
        if (model == null) return null;
        model.m_DataList.Add(new LLM.SendData("user", input));
        var history = new LLM.SendData("assistant", output);
        model.m_DataList.Add(history);
        return history;
    }

    private bool TryPublishTranscriptClarification(string input, int generation)
    {
        if (m_TranscriptClarificationInput != input || m_TranscriptClarificationOutput == null) return false;
        string output = m_TranscriptClarificationOutput;
        m_TranscriptClarificationInput = m_TranscriptClarificationOutput = null;
        ClearStagedFormalObservation();
        var history = CommitTranscriptClarificationHistory(input, output);
        BindRoomSpeechPlayback(generation, SpatialGoalReportLanguage(null), null, m_ChatSettings?.m_ChatModel, history);
        Debug.Log($"[ASR/Clarification] generation={generation} sceneFactsIncluded=false actionSubmitted=false attempts=1", this);
        m_FormalResponseInFlight = false;
        OnStreamDelta(output); OnStreamComplete(output);
        return true;
    }
}
