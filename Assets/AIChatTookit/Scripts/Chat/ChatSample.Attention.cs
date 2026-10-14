using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private SceneAttentionState m_Attention = new SceneAttentionState();
    private double m_AttentionNextUpdate;
    private int m_AttentionLoggedRevision = -1;

    private void TickSceneAttention(bool force = false)
    {
        double now = Time.realtimeSinceStartup;
        if (!force && now < m_AttentionNextUpdate) return;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (bridge == null || m_ChatSettings?.m_ChatModel == null) return;
        string binding = bridge.GetInstanceID() + ":" + (bridge.avatar == null ? 0 : bridge.avatar.GetInstanceID()) + ":" +
            (bridge.environmentRoot == null ? 0 : bridge.environmentRoot.GetInstanceID()) + ":" +
            m_ChatSettings.m_ChatModel.GetInstanceID() + ":" + m_AgentSessionStartTime;
        var semantics = ReadCompactCurrentRequestSemantics(m_FormalResponseGeneration);
        bool pendingSteps = RoomTaskSequenceCurrent() && m_RoomTaskSequence["steps"] is JArray steps &&
            steps.OfType<JObject>().Any(s => new[] { "pending", "running", "preparing" }.Contains((string)s["status"]));
        bool active = pendingSteps || HasWorkToolInFlight() || SceneActionIsBusy || SpatialGoalUnfinished(m_SpatialDeviceGoal) || bridge.ReservesBody;
        bool uncertain = ReadRoomTaskDecisionFailure() != null || RoomMovementResultPolicy.IsTerminalFailure(bridge.Phase) && active;
        bool autonomous = m_AgentCurrentRoundIsTick && !m_WorkReviewOpen;
        string topic = autonomous ? m_SceneAutonomyIntention : m_ActionConstraints.CurrentUserText;
        string purpose = autonomous ? "autonomous:" + topic : "user:" + m_ActionConstraints.CurrentUserTurn + ":" + m_WorkEpoch;
        // Only observation-backed records, never a scene-wide Transform inventory.
        var observed = m_SceneVisibleMemory.Where(x => SceneVisibleCatalogHistoryBinding(x.view)).Select(x => new JObject {
            ["entityId"] = x.entry.entityId, ["captureId"] = x.view.observation.CaptureId,
            ["observedAt"] = x.view.observation.CapturedAtRealtime,
            ["description"] = x.description ?? x.matchedOriginalTarget ?? "",
            ["descriptionCaptureId"] = x.description != null ? x.descriptionCaptureId : x.matchCaptureId
        }).ToArray();
        string entity = active ? (string)BuildSpatialGoalContext()?["boundEntityId"] : null;
        bool talking = m_UserSpeechActiveForAutonomy || m_FormalResponseInFlight || IsVoiceOutputPlaying ||
            autonomous && m_AgentRoundInFlight || semantics != null && m_WorkReviewOpen;
        m_Attention.Update(binding, purpose, topic, active, uncertain, talking, observed,
            entity == null ? Array.Empty<string>() : new[] { entity }, now);
        m_AttentionNextUpdate = now + m_Attention.UpdateInterval;
        var state = m_Attention.Snapshot("runtime", now);
        int revision = (int)state["revision"];
        if (revision != m_AttentionLoggedRevision)
        {
            m_AttentionLoggedRevision = revision;
            Debug.Log("[Attention/State] " + state.ToString(Formatting.None), this);
        }
    }

    private JObject ReadAttentionState(string consumer)
    {
        TickSceneAttention(true);
        var state = m_Attention.Snapshot(consumer, Time.realtimeSinceStartup);
        state["workEpoch"] = m_WorkEpoch; state["userTurn"] = m_ActionConstraints.CurrentUserTurn;
        return state;
    }

    private JObject BuildAttentionInteractionTime() => new JObject {
        ["observedUtc"] = DateTime.UtcNow.ToString("o"),
        ["voiceOutputPlaying"] = IsVoiceOutputPlaying,
        ["latestAcceptedReply"] = m_LastFormalReplyReceipt == null ? null : PickRoomFields(m_LastFormalReplyReceipt,
            "generation", "recordedAtUtc", "publicText", "completedPlaybackText"),
        ["scope"] = "Accepted text and completedPlaybackText are distinct. Use these timestamps and recent dialogue for temporal claims; do not invent a long silence from the lack of a new user command."
    };
}
