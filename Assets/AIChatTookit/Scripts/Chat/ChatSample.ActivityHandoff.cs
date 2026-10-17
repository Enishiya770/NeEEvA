using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private JObject m_ActivityHandoff;
    private float m_ActivityHandoffLogAt;

    private JObject ActivityTaskContext(JObject action)
    {
        string id = (string)action["id"];
        var fact = m_SceneActionActive && m_SceneActionFact?.actionId == id ? m_SceneActionFact : null;
        var runner = m_SpatialGoalRunner;
        bool prerequisite = fact != null && runner != null && SpatialGoalRunnerBindingCurrent(runner) &&
            (ReferenceEquals(runner.fact, fact) || m_SceneSearch?.deviceGoalId == runner.goalId);
        var goal = prerequisite ? m_SpatialDeviceGoal : null;
        var steps = RoomTaskSequenceCurrent() ? m_RoomTaskSequence["steps"] as JArray : null;
        var step = steps?.OfType<JObject>().FirstOrDefault(s => (string)s["actionId"] == id ||
            goal != null && (string)s["goalId"] == goal.id);
        if (fact == null && step == null) return action["task"] as JObject;
        return new JObject {
            ["relationship"] = prerequisite ? runner.phase == "operating" ? "task-step" : "prerequisite" : step != null ? "task-step" : "independent-activity",
            ["parentGoalId"] = goal?.id, ["requestedOperation"] = goal?.desiredState,
            ["targetEntityId"] = fact?.entityId ?? goal?.entityId,
            ["targetDescription"] = fact?.targetDescription ?? goal?.targetDescription,
            ["targetPurpose"] = fact?.targetPurpose ?? fact?.viewPurpose,
            ["confirmed"] = fact?.semanticTargetConfirmed,
            ["step"] = step == null ? null : PickRoomFields(step, "id", "kind", "status", "dependsOn", "target", "desiredState"),
            ["scope"] = "Executor-owned relationship, not completion. A prerequisite serves this goal; an unfinished goal alone is not a conflict. Independent reasonable activity needs no verbatim user command."
        };
    }

    private void BeginActivityHandoff(JObject frame, JObject action, JObject stoppedState)
    {
        m_ActivityHandoff = new JObject {
            ["ownerBefore"] = frame["owner"]?.DeepClone() ?? new JObject { ["generation"] = m_FormalResponseGeneration,
                ["workEpoch"] = m_WorkEpoch, ["userTurn"] = m_ActionConstraints.CurrentUserTurn }, ["ownerAfterGeneration"] = m_FormalResponseGeneration,
            ["request"] = frame["facts"]?["request"]?.DeepClone(), ["stoppedActivity"] = stoppedState?.DeepClone(),
            ["decision"] = action.DeepClone(), ["state"] = "awaiting-review", ["reviewsDispatched"] = 0,
            ["createdAtRealtime"] = Time.realtimeSinceStartup,
            ["scope"] = "Local monitor intervention, not service failure or completed user work. One fresh review may explain or propose a next step; normal execution checks still apply. No action retry has been submitted."
        };
        m_ActivityHandoffLogAt = 0;
        m_ActivityHandoff["result"] = WorkResultDeliveryProtocol.Result("process-intervention", "paused", new JObject {
            ["actionId"] = action["id"]?.DeepClone(), ["operationVersion"] = action["version"]?.DeepClone(),
            ["actualOutcome"] = "stopped-by-local-monitor", ["reasonCode"] = "process-monitor",
            ["monitorInterpretation"] = action["reason"]?.DeepClone(), ["task"] = stoppedState?["task"]?.DeepClone(),
            ["scope"] = "This action was locally stopped. The monitor reason is an interpretation, not an independently proven fault. No service failure, task success, device change or replacement execution is established. Explain the pause and preserve unfinished work."
        });
        Debug.Log("[Activity/Handoff] " + m_ActivityHandoff.ToString(Formatting.None), this);
        ScheduleNextTick(.2f, "activity-monitor-feedback");
    }

    private JObject ReadActivityInterventionResult() => m_ActivityHandoff != null &&
        (int?)m_ActivityHandoff.SelectToken("ownerBefore.workEpoch") == m_WorkEpoch &&
        (int?)m_ActivityHandoff.SelectToken("ownerBefore.userTurn") == m_ActionConstraints.CurrentUserTurn
        ? m_ActivityHandoff["result"] as JObject : null;

    private void TickActivityHandoff()
    {
        if (m_ActivityHandoff == null) return;
        if ((int?)m_ActivityHandoff.SelectToken("ownerBefore.workEpoch") != m_WorkEpoch ||
            (int?)m_ActivityHandoff.SelectToken("ownerBefore.userTurn") != m_ActionConstraints.CurrentUserTurn) {
            m_ActivityHandoff = null; return;
        }
        if ((string)m_ActivityHandoff["state"] != "awaiting-review" ||
            Time.realtimeSinceStartup < m_ActivityHandoffLogAt) return;
        m_ActivityHandoffLogAt = Time.realtimeSinceStartup + 5f;
        Debug.Log("[Activity/HandoffWait] " + new JObject {
            ["generation"] = m_FormalResponseGeneration, ["waitSeconds"] = Time.realtimeSinceStartup - (float)m_ActivityHandoff["createdAtRealtime"],
            ["logicalSpeaking"] = IsAISpeaking, ["actualPlaying"] = IsVoiceOutputPlaying,
            ["formalPending"] = m_FormalResponseInFlight, ["agentPending"] = m_AgentRoundInFlight,
            ["privatePending"] = m_AutonomyProbeInFlight, ["sceneBusy"] = SceneActionIsBusy,
            ["toolBusy"] = HasWorkToolInFlight(), ["paused"] = m_SceneTimePaused,
            ["userSpeaking"] = m_UserSpeechActiveForAutonomy, ["closing"] = m_AgentGracefulShutdownPending,
            ["scheduledReason"] = m_SceneReviewClock.Reason, ["reviewLimit"] = 1
        }.ToString(Formatting.None), this);
    }

    private void ObserveActivityHandoffDispatch(string reason)
    {
        if (reason != "activity-monitor-feedback" || m_ActivityHandoff == null) return;
        m_ActivityHandoff["state"] = "review-dispatched";
        m_ActivityHandoff["reviewsDispatched"] = 1;
        if(m_LastActivityFeedback!=null)m_LastActivityFeedback["handoff"]=m_ActivityHandoff.DeepClone();
        Debug.Log("[Activity/Handoff] " + m_ActivityHandoff.ToString(Formatting.None), this);
    }
}
