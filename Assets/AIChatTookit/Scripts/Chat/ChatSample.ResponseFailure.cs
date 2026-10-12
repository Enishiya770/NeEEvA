using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private int m_ResponseFailureGeneration = -1, m_ResponseFailureUserTurn = -1;
    private string m_ResponseFailureReason, m_ResponseFailureKind;
    private JObject m_LastFormalReplyReceipt;
    private JObject m_RoomPlanningBodyLease;

    private JObject ReadConcurrentBodyFailure(int generation)
    {
        if ((int?)m_RoomPlanningBodyLease?["generation"] != generation || generation != m_FormalResponseGeneration ||
            (int?)m_RoomPlanningBodyLease?["userTurn"] != m_ActionConstraints.CurrentUserTurn) return null;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var execution = ReadCompactRoomTaskFacts(bridge)["execution"] as JObject;
        if (bridge == null || bridge.IsFollowing || bridge.ReservesBody ||
            string.IsNullOrEmpty((string)m_RoomPlanningBodyLease["actionId"]) ||
            bridge.ActionId != (string)m_RoomPlanningBodyLease["actionId"] ||
            (string)execution?["actionId"] != (string)m_RoomPlanningBodyLease["actionId"] ||
            (string)execution?["phase"] != "failed") return null;
        return new JObject { ["execution"] = execution.DeepClone(),
            ["wasFollowing"] = m_RoomPlanningBodyLease["following"]?.DeepClone(),
            ["scope"] = "The identified previous body task failed while this turn was being planned. This is not a successful stop command or a failure of a newly dispatched action." };
    }

    private static string ConcurrentBodyFailureNotice(JObject failure, string language)
    {
        if (failure == null) return "";
        return language == "ja" ? "それとは別に、それまでの移動は実行エラーで終わったわ。今回の停止指示が成功したという意味ではないの。" :
            language == "en" ? " Separately, the previous movement ended with an execution failure; that does not mean this stop command succeeded." :
            "另外，之前的移动已因执行失败结束，并非这次停止指令执行成功。";
    }
    private ArdyRoomDialogueBridge m_LastFormalReplyBridge;
    private Object m_LastFormalReplyAvatar, m_LastFormalReplyEnvironment;
    private float m_LastFormalReplySession = -1;
    private readonly System.Collections.Generic.Queue<JObject> m_RecentRoomReplies = new System.Collections.Generic.Queue<JObject>();

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
        ["httpRequestSent"] = m_ResponseFailureKind != "reply-budget-exceeded" &&
            m_ResponseFailureReason != "invalid-result-context" && m_ResponseFailureReason != "invalid-result-facts",
        ["scope"] = "Formal reply generation only; this does not change planning or execution outcomes." };

    private void RecordFinalRoomReply(int generation, string text, string reviewStatus, ArdyRoomDialogueBridge bridge)
    {
        if (ReadPreviousFormalReplyReceipt() == null) m_RecentRoomReplies.Clear();
        var room = ReadCompactRoomTaskFacts(bridge);
        m_LastFormalReplyReceipt = new JObject {
            ["generation"] = generation, ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
            ["recordedAtUtc"] = System.DateTime.UtcNow.ToString("o"), ["replyFailure"] = CurrentReplyFailure(generation),
            ["planningFailure"] = ReadRoomTaskDecisionFailure(), ["reviewStatus"] = reviewStatus,
            ["concurrentBodyFailure"] = ReadConcurrentBodyFailure(generation),
            ["intent"] = m_RoomTaskIntent, ["dispatch"] = m_RoomTaskDispatch, ["actionId"] = m_RoomTaskActionId,
            ["execution"] = room["execution"]?.DeepClone(),
            ["sceneAction"] = PickRoomFields(BuildSceneActionFacts()["lastAction"] as JObject,
                "actionId", "intent", "phase", "reason", "observedUtc", "reached", "stateChanged"),
            ["publicText"] = text,
            ["scope"] = "Accepted final public reply and contemporaneous independent execution receipts. Historical snapshot, not current state or retry permission; accepted text does not prove audio playback." };
        m_LastFormalReplyBridge = bridge; m_LastFormalReplyAvatar = bridge?.avatar;
        m_LastFormalReplyEnvironment = bridge?.environmentRoot; m_LastFormalReplySession = m_AgentSessionStartTime;
        m_RecentRoomReplies.Enqueue(new JObject { ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
            ["actionId"] = m_RoomTaskActionId, ["publicText"] = text });
        while (m_RecentRoomReplies.Count > 4) m_RecentRoomReplies.Dequeue();
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

    private JArray ReadRecentRoomReplies() => ReadPreviousFormalReplyReceipt() == null ? new JArray() :
        new JArray(System.Linq.Enumerable.Select(m_RecentRoomReplies, r => r.DeepClone()));
}
