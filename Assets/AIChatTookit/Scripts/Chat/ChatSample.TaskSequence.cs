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
    private readonly System.Collections.Generic.HashSet<string> m_RoomTaskSequenceReviewedSteps = new System.Collections.Generic.HashSet<string>();
    private float m_RoomTaskSequenceProgressCheckAt;

    private bool AdoptReviewedParentTask(string goalId, string request)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var previous = m_PreviousUnfinishedWork?["taskSequence"] as JObject;
        if (RoomTaskSequenceCurrent() || request != m_ActionConstraints.CurrentUserText || bridge == null ||
            m_RoomTaskSequenceModel != m_ChatSettings?.m_ChatModel || bridge != m_RoomTaskSequenceBridge ||
            bridge.avatar != m_RoomTaskSequenceAvatar || bridge.environmentRoot != m_RoomTaskSequenceEnvironment ||
            !(previous?["steps"] is JArray steps)) return false;
        var step = steps.OfType<JObject>().FirstOrDefault(s => (string)s["goalId"] == goalId && !string.IsNullOrEmpty(goalId));
        if (step == null) return false;
        var resumed = TaskContinuationContextPolicy.Resume(previous, (string)step["id"], request,
            m_WorkEpoch, m_ActionConstraints.CurrentUserTurn);
        if (resumed == null) return false;
        m_RoomTaskSequence = resumed;
        m_RoomTaskSequenceSelection = ((JArray)resumed["readyStepIds"]).Values<string>().Contains((string)step["id"]) ? (string)step["id"] : null;
        m_RoomTaskSequenceReviewedSteps.Clear(); m_RoomTaskSequenceProgressCheckAt = 0;
        m_SequenceBodyAttempts.Clear(); m_SequenceBodySubmission = null;
        Debug.Log("[Work/ParentTaskResumed] " + resumed.ToString(Formatting.None), this);
        return true;
    }

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
                    (bool?)receipt.SelectToken("goal.completionEvidenceValid") == true &&
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
                else if ((string)execution?["actionId"] == action && RoomMovementResultPolicy.IsTerminalFailure((string)execution?["phase"])) receipt = execution;
                else if ((string)scene?["actionId"] == action && new[] { "failed", "rejected", "cancelled", "arrived-object-unconfirmed", "observed-target-unconfirmed" }.Contains((string)scene?["phase"])) receipt = scene;
            }
            if (receipt == null) continue;
            step["status"] = complete ? "completed" : "failed";
            step["receipt"] = !string.IsNullOrEmpty(goal) ? receipt.DeepClone() : PickRoomFields(receipt,
                "taskId", "actionId", "intent", "targetKind", "sceneTargetId", "entityId", "phase", "status", "reason", "stage",
                "generation", "workEpoch", "userTurn", "observedUtc", "operationRevision", "stateChanged", "objectReobserved", "reached",
                "approachRetargetStatus", "approachRetargetCount", "approachRetargetTravel", "historyReferenceId", "resultCaptureId", "viewVerification", "viewFinding");
        }
        TaskAttemptSettlementPolicy.Reconcile(m_RoomTaskSequence);
        var result = (JObject)m_RoomTaskSequence.DeepClone();
        result["readyStepIds"] = RoomTaskSequenceProtocol.Ready((JArray)result["steps"]);
        result["scope"] = "Whole current request with engine-recorded progress. Pending predecessors forbid their successors. Receipts are historical events, not fresh state or permission; speech alone never completes a step. Each next action still needs current target, semantic and execution checks.";
        return RoomTaskContextPolicy.Sequence(result);
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
            // Commit a plan only together with a compatible current selection.
            // A rejected proposal must not freeze the next repair into its plan.
            if (!RoomTaskSequenceProtocol.TrySelect(json, decision, plan, out var selection, out reason)) return false;
            m_RoomTaskSequence = new JObject { ["workEpoch"] = m_WorkEpoch, ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
                ["request"] = m_ActionConstraints.CurrentUserText, ["steps"] = plan,
                ["coverage"] = JObject.Parse(json)["taskCoverage"]?.DeepClone(),
                ["answerObligations"] = ReplyAnswerObligationProtocol.FromPlan(m_ActionConstraints.CurrentUserText,
                    JObject.Parse(json)["taskCoverage"] as JArray, ReadCompactCurrentRequestSemantics(m_FormalResponseGeneration)) };
            if (((JArray)m_RoomTaskSequence["answerObligations"]).Count > 0)
                m_RoomTaskSequence["answerEvidence"] = new JObject {
                    ["observedHistory"] = BuildVerifiedObservedDeviceReferentHistory(),
                    ["eventHistory"] = ReadHistoricalDeviceEvents(),
                    ["requestFailureHistory"] = ReadPreviousRoomPlanningFailure(),
                    ["scope"] = "Frozen before dispatch of this task plan. Records retain their own entity/time. Missing records mean evidence unavailable, not proof an event never happened. This snapshot cannot establish current state." };
            m_RoomTaskSequenceReviewedSteps.Clear(); m_RoomTaskSequenceProgressCheckAt = 0;
            m_SequenceBodyAttempts.Clear(); m_SequenceBodySubmission = null;
            m_RoomTaskSequenceModel = m_ChatSettings.m_ChatModel;
            m_RoomTaskSequenceBridge = GetComponent<ArdyRoomDialogueBridge>();
            m_RoomTaskSequenceAvatar = m_RoomTaskSequenceBridge.avatar; m_RoomTaskSequenceEnvironment = m_RoomTaskSequenceBridge.environmentRoot;
            Debug.Log("[Work/TaskSequence] " + m_RoomTaskSequence.ToString(Formatting.None), this);
            state = ReadRoomTaskSequence();
        }
        if (!RoomTaskSequenceProtocol.TrySelect(json, decision, (JArray)state["steps"], out m_RoomTaskSequenceSelection, out reason)) return false;
        // Do not skip an explicit ready stop while the old body task still owns
        // execution, even if a draft incorrectly omitted its dependency edge.
        if (m_RoomTaskSequenceBridge.IsFollowing || m_RoomTaskSequenceBridge.ReservesBody)
        {
            var steps = (JArray)state["steps"];
            bool stopReady = steps.OfType<JObject>().Any(s => (string)s["kind"] == "stop-moving" &&
                RoomTaskSequenceProtocol.Ready(steps).Values<string>().Contains((string)s["id"]));
            if (stopReady && decision.intent != "stop-moving" &&
                (decision.deviceGoal?.operation == "set" || decision.intent == "approach" || decision.intent == "follow" || decision.IsVisual))
            { reason = "invalid-task-sequence-body-handoff"; return false; }
        }
        return true;
    }

    private bool RoomTaskSequenceUnfinished() => (ReadRoomTaskSequence()?["steps"] as JArray)?.OfType<JObject>()
        .Any(step => (string)step["status"] != "completed") == true;

    private bool SelectedRoomTaskSequenceIsDevice()
    {
        var state = ReadRoomTaskSequence();
        return state?["steps"] is JArray steps && steps.OfType<JObject>().Any(step =>
            (string)step["id"] == m_RoomTaskSequenceSelection && (string)step["kind"] == "device-state" &&
            new[] { "pending", "running" }.Contains((string)step["status"]));
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
            ObserveRoomTaskSequenceProgress(true);
            return;
        }
        if (string.IsNullOrEmpty(goalId) && string.IsNullOrEmpty(m_RoomTaskActionId)) return;
        step["status"] = "running"; step["goalId"] = goalId; step["actionId"] = m_RoomTaskActionId;
        step["intent"] = m_RoomTaskIntent;
        step["generation"] = m_FormalResponseGeneration;
        SnapshotActivityOrigins();
        ObserveRoomTaskSequenceProgress(true);
    }

    private void AdoptPreparedStopSequence(string json, string actionId, string dispatch)
    {
        // The stop already has its own current execution proof. Attach it to a
        // ready matching obligation without executing the planner's later action.
        try
        {
            var state = ReadRoomTaskSequence();
            JArray steps;
            if (state != null) steps = (JArray)state["steps"];
            else
            {
                if (!RoomTaskSequenceProtocol.TryPlan(json, m_ActionConstraints.CurrentUserText, out steps, out _)) return;
                foreach (var step in steps.OfType<JObject>()) step["status"] = "pending";
            }
            var ready = RoomTaskSequenceProtocol.Ready(steps).Values<string>().ToArray();
            var stop = steps.OfType<JObject>().FirstOrDefault(s => (string)s["kind"] == "stop-moving" && ready.Contains((string)s["id"]));
            if (stop == null) return;
            var source = JObject.Parse(json); source["taskStep"] = stop["id"].DeepClone();
            if (!ValidateRoomTaskSequence(source.ToString(Formatting.None), new RoomTaskProtocol.Decision { intent = "stop-moving" }, out _)) return;
            var bridge = GetComponent<ArdyRoomDialogueBridge>();
            if (bridge == null || bridge.ActionId != actionId || bridge.Phase != "stopped" || dispatch != "stop-submitted") return;
            var owned = ((JArray)m_RoomTaskSequence["steps"]).OfType<JObject>().First(s => (string)s["id"] == (string)stop["id"]);
            owned["status"] = "completed"; owned["intent"] = "stop-moving"; owned["actionId"] = actionId;
            owned["generation"] = m_FormalResponseGeneration;
            owned["receipt"] = new JObject { ["actionId"] = actionId, ["phase"] = "stopped", ["dispatch"] = dispatch,
                ["execution"] = ReadCompactRoomTaskFacts(bridge)["execution"]?.DeepClone() };
            m_RoomTaskSequenceReviewedSteps.Add((string)owned["id"]);
            m_RoomTaskSequenceSelection = null;
        }
        catch (JsonException) { } // Full-request review remains owed even if the later plan was malformed.
    }

    private void ObserveRoomTaskSequenceProgress(bool immediate = false)
    {
        if (!m_WorkReviewOpen || m_WorkNoProgress || !RoomTaskSequenceCurrent() ||
            m_UserSpeechActiveForAutonomy || m_SceneTimePaused || !isActiveAndEnabled ||
            (!immediate && Time.realtimeSinceStartup < m_RoomTaskSequenceProgressCheckAt)) return;
        m_RoomTaskSequenceProgressCheckAt = Time.realtimeSinceStartup + .5f;
        var sourceSteps = (JArray)m_RoomTaskSequence["steps"];
        if (!sourceSteps.OfType<JObject>().Any(s => !m_RoomTaskSequenceReviewedSteps.Contains((string)s["id"]) &&
            new[] { "running", "completed", "failed", "blocked" }.Contains((string)s["status"]))) return;
        var state = ReadRoomTaskSequence();
        foreach (var step in ((JArray)state["steps"]).OfType<JObject>())
        {
            if (!new[] { "completed", "failed", "blocked" }.Contains((string)step["status"]) ||
                !m_RoomTaskSequenceReviewedSteps.Add((string)step["id"])) continue;
            // Queue once per real terminal receipt, including synchronous stops.
            // A pending or running step and generated speech never unlock a sequel.
            if (!m_WorkSubstepReviewPending) QueueWorkSubstepReview(m_FormalResponseGeneration, (string)step["status"]);
            Debug.Log("[Work/StepSettled] " + new JObject {
                ["workEpoch"] = m_WorkEpoch, ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
                ["stepId"] = step["id"], ["status"] = step["status"], ["actionId"] = step["actionId"],
                ["readyStepIds"] = state["readyStepIds"], ["newActionSubmitted"] = false
            }.ToString(Formatting.None), this);
        }
    }

    private void MarkSettledRoomStepsReviewQueued()
    {
        var state = ReadRoomTaskSequence();
        if (!(state?["steps"] is JArray steps)) return;
        foreach (var step in steps.OfType<JObject>())
            if (new[] { "completed", "failed", "blocked" }.Contains((string)step["status"]))
                m_RoomTaskSequenceReviewedSteps.Add((string)step["id"]);
    }
}
