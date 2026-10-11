using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private JObject m_RoomTaskSequence;
    private string m_RoomTaskSequenceSelection;
    private LLM m_RoomTaskSequenceModel;
    private ArdyRoomDialogueBridge m_RoomTaskSequenceBridge;
    private UnityEngine.Object m_RoomTaskSequenceAvatar, m_RoomTaskSequenceEnvironment;

    private bool RoomTaskSequenceCurrent()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        return m_RoomTaskSequence != null && (int?)m_RoomTaskSequence["workEpoch"] == m_WorkEpoch &&
            (int?)m_RoomTaskSequence["userTurn"] == m_ActionConstraints.CurrentUserTurn &&
            (string)m_RoomTaskSequence["request"] == m_ActionConstraints.CurrentUserText &&
            m_RoomTaskSequenceModel == m_ChatSettings?.m_ChatModel && bridge != null && bridge == m_RoomTaskSequenceBridge &&
            bridge.avatar == m_RoomTaskSequenceAvatar && bridge.environmentRoot == m_RoomTaskSequenceEnvironment;
    }

    private JObject ReadRoomTaskSequence()
    {
        if (!RoomTaskSequenceCurrent()) return null;
        var room = ReadCompactRoomTaskFacts(m_RoomTaskSequenceBridge);
        var execution = room["execution"] as JObject;
        var scene = BuildSceneActionFacts()["lastAction"] as JObject;
        var search = BuildSceneSearchContext();
        foreach (var step in ((JArray)m_RoomTaskSequence["steps"]).OfType<JObject>())
        {
            if ((string)step["status"] != "running") continue;
            string action = (string)step["actionId"], goal = (string)step["goalId"];
            JObject receipt = null; bool complete = false;
            if (!string.IsNullOrEmpty(goal) && m_WorkDeviceResult != null && m_WorkDeviceResultEpoch == m_WorkEpoch &&
                m_WorkDeviceResultTurn == m_ActionConstraints.CurrentUserTurn && (string)m_WorkDeviceResult.SelectToken("goal.goalId") == goal)
            {
                receipt = m_WorkDeviceResult;
                complete = (string)receipt.SelectToken("goal.status") == "satisfied" &&
                    (string)receipt.SelectToken("goal.desiredState") == (string)step["desiredState"];
            }
            else if (!string.IsNullOrEmpty(action))
            {
                if ((string)execution?["actionId"] == action && (string)execution?["phase"] == "arrived" &&
                    (string)step["kind"] == "approach" && (string)execution?["targetKind"] == "user") { receipt = execution; complete = true; }
                else if ((string)scene?["actionId"] == action && (string)step["kind"] == "observation" &&
                    new[] { "performed", "observed", "arrived" }.Contains((string)scene?["phase"])) { receipt = scene; complete = true; }
                else if ((string)search?["taskId"] == action && (string)step["intent"] == "search-scene" &&
                    (int?)search?["workEpoch"] == m_WorkEpoch && (int?)search?["userTurn"] == m_ActionConstraints.CurrentUserTurn &&
                    new[] { "found", "completed" }.Contains((string)search?["status"])) { receipt = search; complete = true; }
                else if ((string)search?["taskId"] == action && (int?)search?["workEpoch"] == m_WorkEpoch &&
                    new[] { "incomplete", "cancelled" }.Contains((string)search?["status"])) receipt = search;
                else if ((string)execution?["actionId"] == action && (string)step["kind"] == "stop-moving" &&
                    !m_RoomTaskSequenceBridge.IsFollowing && !m_RoomTaskSequenceBridge.ReservesBody &&
                    new[] { "stopped", "cancelled", "idle" }.Contains((string)execution?["phase"])) { receipt = execution; complete = true; }
                else if ((string)execution?["actionId"] == action && new[] { "failed", "blocked", "cancelled" }.Contains((string)execution?["phase"])) receipt = execution;
                else if ((string)scene?["actionId"] == action && new[] { "failed", "rejected", "cancelled", "arrived-object-unconfirmed" }.Contains((string)scene?["phase"])) receipt = scene;
            }
            if (receipt == null) continue;
            step["status"] = complete ? "completed" : "failed";
            step["receipt"] = !string.IsNullOrEmpty(goal) ? receipt.DeepClone() : PickRoomFields(receipt,
                "taskId", "actionId", "intent", "targetKind", "sceneTargetId", "entityId", "phase", "status", "reason", "stage",
                "generation", "workEpoch", "userTurn", "observedUtc", "operationRevision", "stateChanged", "objectReobserved", "reached");
        }
        var result = (JObject)m_RoomTaskSequence.DeepClone();
        result["readyStepIds"] = RoomTaskSequenceProtocol.Ready((JArray)result["steps"]);
        result["scope"] = "Whole current request with engine-recorded progress. Pending predecessors forbid their successors. Receipts are historical events, not fresh state or permission; speech alone never completes a step. Each next action still needs current target, semantic and execution checks.";
        return result;
    }

    private bool ValidateRoomTaskSequence(string json, RoomTaskProtocol.Decision decision, out string reason)
    {
        var state = ReadRoomTaskSequence();
        if (state == null)
        {
            if (!RoomTaskSequenceProtocol.TryPlan(json, m_ActionConstraints.CurrentUserText, out var plan, out reason)) return false;
            if (plan.Count == 0)
                return RoomTaskSequenceProtocol.TrySelect(json, decision, plan, out m_RoomTaskSequenceSelection, out reason);
            foreach (var step in plan.OfType<JObject>()) step["status"] = "pending";
            m_RoomTaskSequence = new JObject { ["workEpoch"] = m_WorkEpoch, ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
                ["request"] = m_ActionConstraints.CurrentUserText, ["steps"] = plan };
            m_RoomTaskSequenceModel = m_ChatSettings.m_ChatModel;
            m_RoomTaskSequenceBridge = GetComponent<ArdyRoomDialogueBridge>();
            m_RoomTaskSequenceAvatar = m_RoomTaskSequenceBridge.avatar; m_RoomTaskSequenceEnvironment = m_RoomTaskSequenceBridge.environmentRoot;
            Debug.Log("[Work/TaskSequence] " + m_RoomTaskSequence.ToString(Formatting.None), this);
            state = ReadRoomTaskSequence();
        }
        return RoomTaskSequenceProtocol.TrySelect(json, decision, (JArray)state["steps"], out m_RoomTaskSequenceSelection, out reason);
    }

    private void BindRoomTaskSequenceStep(string goalId = null)
    {
        if (!RoomTaskSequenceCurrent() || string.IsNullOrEmpty(m_RoomTaskSequenceSelection)) return;
        var step = ((JArray)m_RoomTaskSequence["steps"]).OfType<JObject>().FirstOrDefault(s => (string)s["id"] == m_RoomTaskSequenceSelection);
        if (step == null || (string)step["status"] != "pending") return;
        if (m_RoomTaskIntent == "inspect" && m_RoomTaskDispatch == "read-only-checked" && (string)step["kind"] == "observation")
        {
            step["status"] = "completed";
            step["receipt"] = new JObject { ["intent"] = "inspect", ["dispatch"] = "read-only-checked", ["generation"] = m_FormalResponseGeneration,
                ["scope"] = "Actual current target evidence was read; no movement, recognition or device operation was performed." };
            return;
        }
        if (string.IsNullOrEmpty(goalId) && string.IsNullOrEmpty(m_RoomTaskActionId)) return;
        step["status"] = "running"; step["goalId"] = goalId; step["actionId"] = m_RoomTaskActionId;
        step["intent"] = m_RoomTaskIntent;
        step["generation"] = m_FormalResponseGeneration;
    }
}
