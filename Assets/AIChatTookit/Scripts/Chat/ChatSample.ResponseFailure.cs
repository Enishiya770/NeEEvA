using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private int m_ResponseFailureGeneration = -1, m_ResponseFailureUserTurn = -1;
    private string m_ResponseFailureReason, m_ResponseFailureKind;
    private JObject m_LastFormalReplyReceipt;
    private ArdyRoomDialogueBridge m_LastFormalReplyBridge;
    private Object m_LastFormalReplyAvatar, m_LastFormalReplyEnvironment;
    private float m_LastFormalReplySession = -1;

    private void HandleResponseGenerationFailure(string reason)
    {
        if (!isActiveAndEnabled || !m_FormalResponseInFlight || m_UserSpeechActiveForAutonomy || m_SceneTimePaused) return;
        // Provider only emits for its still-current request, before completion.
        // Do not reinterpret a response timeout as a failed room/device operation.
        m_ResponseFailureGeneration = m_FormalResponseGeneration;
        m_ResponseFailureUserTurn = m_ActionConstraints.CurrentUserTurn;
        m_ResponseFailureReason = reason;
        m_ResponseFailureKind = reason == "context-budget" ? "reply-budget-exceeded" :
            reason == "no-public-output-deadline" || reason == "total-deadline" || reason == "delta-stalled" ? "reply-timeout" :
            reason == "http-failure" ? "reply-service-failed" : "reply-incomplete-output";
        if (m_RoomSpeechGuardGeneration == m_FormalResponseGeneration) m_RoomTaskFormatError = true;
    }

    private bool CurrentFormalGenerationFailed(int generation) =>
        m_ResponseFailureGeneration == generation && m_ResponseFailureUserTurn == m_ActionConstraints.CurrentUserTurn;

    private string FormalGenerationFailureNotice(string language) => m_ResponseFailureKind == "reply-budget-exceeded"
        ? language == "ja" ? "情報量が返事の処理枠を超えたわ。" : language == "en"
            ? "My reply exceeded its context budget. " : "回复所需信息超出处理预算了。"
        : m_ResponseFailureKind == "reply-timeout"
        ? language == "ja" ? "返事の生成が時間切れになってしまったわ。" : language == "en"
            ? "Generating my reply timed out. " : "刚才的回复生成超时了。"
        : m_ResponseFailureKind == "reply-service-failed"
        ? language == "ja" ? "返事を生成するサービスへのリクエストが失敗したわ。" : language == "en"
            ? "The reply generation service request failed. " : "这次回复生成的服务请求失败了。"
        : language == "ja" ? "返事を最後まで生成できなかったわ。" : language == "en"
            ? "My reply could not be completed. " : "刚才没能生成完整的回复。";

    private JObject CurrentReplyFailure(int generation) => !CurrentFormalGenerationFailed(generation) ? null : new JObject {
        ["kind"] = m_ResponseFailureKind, ["reason"] = m_ResponseFailureReason,
        ["generation"] = generation, ["userTurn"] = m_ResponseFailureUserTurn,
        ["httpRequestSent"] = m_ResponseFailureKind != "reply-budget-exceeded",
        ["scope"] = "Formal reply generation only; this does not change planning or execution outcomes." };

    private void RecordFinalRoomReply(int generation, string text, string reviewStatus, ArdyRoomDialogueBridge bridge)
    {
        var room = ReadCompactRoomTaskFacts(bridge);
        m_LastFormalReplyReceipt = new JObject {
            ["generation"] = generation, ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
            ["recordedAtUtc"] = System.DateTime.UtcNow.ToString("o"), ["replyFailure"] = CurrentReplyFailure(generation),
            ["planningFailure"] = ReadRoomTaskDecisionFailure(), ["reviewStatus"] = reviewStatus,
            ["intent"] = m_RoomTaskIntent, ["dispatch"] = m_RoomTaskDispatch, ["actionId"] = m_RoomTaskActionId,
            ["execution"] = room["execution"]?.DeepClone(),
            ["sceneAction"] = PickRoomFields(BuildSceneActionFacts()["lastAction"] as JObject,
                "actionId", "intent", "phase", "reason", "observedUtc", "reached", "stateChanged"),
            ["publicText"] = text,
            ["scope"] = "Accepted final public reply and contemporaneous independent execution receipts. Historical snapshot, not current state or retry permission; accepted text does not prove audio playback." };
        m_LastFormalReplyBridge = bridge; m_LastFormalReplyAvatar = bridge?.avatar;
        m_LastFormalReplyEnvironment = bridge?.environmentRoot; m_LastFormalReplySession = m_AgentSessionStartTime;
        Debug.Log("[Room/FinalReplyReceipt] " + m_LastFormalReplyReceipt.ToString(Formatting.None), this);
    }

    private JObject ReadPreviousFormalReplyReceipt()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (m_LastFormalReplyReceipt == null || bridge == null || bridge != m_LastFormalReplyBridge ||
            bridge.avatar != m_LastFormalReplyAvatar || bridge.environmentRoot != m_LastFormalReplyEnvironment ||
            m_AgentSessionStartTime != m_LastFormalReplySession) return null;
        return (JObject)m_LastFormalReplyReceipt.DeepClone();
    }

    private JObject ReadPreviousReplyFailureReceipt()
    {
        var receipt = ReadPreviousFormalReplyReceipt();
        if (receipt == null || receipt["replyFailure"]?.Type == JTokenType.Null && receipt["planningFailure"]?.Type == JTokenType.Null) return null;
        var result = PickRoomFields(receipt, "generation", "userTurn", "recordedAtUtc", "replyFailure", "planningFailure");
        result["historical"] = true;
        result["scope"] = "Previous reply's failure diagnostics only, not failures of this turn. They supply no current movement phase, result or retry authority.";
        return result;
    }
}
