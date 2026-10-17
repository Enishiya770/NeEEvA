using Newtonsoft.Json.Linq;
using System.Linq;
using UnityEngine;

public partial class ChatSample
{
    private bool m_WorkSubstepReviewPending;
    private JObject m_WorkRequestSemantics;
    private LLM m_WorkRequestSemanticsModel;
    private PrivateDecisionResult m_PrivateDecisionFailure;
    private int m_PrivateDecisionFailureEpoch = -1, m_PrivateDecisionFailureTurn = -1;
    private bool PrivateDecisionBlocksCurrentWork() => m_PrivateDecisionFailure != null && m_WorkReviewOpen &&
        m_PrivateDecisionFailureEpoch == m_WorkEpoch && m_PrivateDecisionFailureTurn == m_ActionConstraints.CurrentUserTurn;
    private int m_PrivateDecisionRetries;
    private JObject m_PreviousUnfinishedWork;
    private void RecordHistoricalWorkTermination(string previousActionId, JObject receipt, string reason)
    {
        if (string.IsNullOrEmpty(previousActionId) || receipt==null || m_PreviousUnfinishedWork==null ||
            !new[]{"stopped","cancelled","disconnected"}.Contains((string)receipt["phase"])) return;
        var steps=m_PreviousUnfinishedWork.SelectToken("taskSequence.steps") as JArray;
        bool owned=(string)m_PreviousUnfinishedWork.SelectToken("executionAtInterruption.actionId")==previousActionId ||
            steps!=null && steps.OfType<JObject>().Any(s=>(string)s["actionId"]==previousActionId);
        if(!owned)return;
        // Keep the immutable earlier steps and their completion states. This is
        // the executor's later termination, never completion or replay authority.
        m_PreviousUnfinishedWork["termination"]=new JObject{
            ["committed"]=true,["previousActionId"]=previousActionId,["reason"]=reason,
            ["receipt"]=TaskContinuationContextPolicy.Outcome(receipt),
            ["replacementUserTurn"]=m_ActionConstraints.CurrentUserTurn,
            ["scope"]="Actual termination after this snapshot. Earlier successful effects remain; unfinished steps did not become successful and are not automatically owed again."};
        Debug.Log("[Work/HistoricalTermination] "+m_PreviousUnfinishedWork["termination"].ToString(Newtonsoft.Json.Formatting.None),this);
    }
    private bool m_ReadOnlyWorkRecheckScheduled;
    private JObject m_WorkDeviceResult;
    private SpatialDeviceGoal m_WorkDeviceResultGoal;
    private int m_WorkDeviceResultEpoch = -1, m_WorkDeviceResultTurn = -1;

    private void RememberCompletedDeviceStep()
    {
        // A completed event survives a later speech generation. It is never an
        // execution lease or a claim that an unseen device is still unchanged.
        var facts = BuildSpatialGoalExecutionFacts();
        try { m_WorkDeviceResult = SpatialGoalResultContextPolicy.ProjectResultFacts(facts) ?? facts; }
        catch (System.Exception error)
        {
            // A reporting optimisation must never discard an engine receipt or
            // prevent the executor from settling. Full facts still face the
            // final request budget before they can be sent to a model.
            m_WorkDeviceResult = facts;
            Debug.LogWarning("[Work/ResultProjectionFailure] " + error, this);
        }
        m_WorkDeviceResultGoal = m_SpatialDeviceGoal;
        m_WorkDeviceResultEpoch = m_WorkEpoch;
        m_WorkDeviceResultTurn = m_ActionConstraints.CurrentUserTurn;
        if (m_WorkDeviceResult == null) return;
        m_WorkDeviceResult["historicalResult"] = true;
        m_WorkDeviceResult["recordedGeneration"] = m_FormalResponseGeneration;
        m_WorkDeviceResult["recordedAtUtc"] = System.DateTime.UtcNow.ToString("o");
        m_WorkDeviceResult["scope"] = "Immutable result of this user's earlier step. operationSummary and lastAction describe what actually happened then, not current visibility/state or permission to repeat it. Null is unknown, never false. Current movement receipts are separate.";
        ArchiveDeviceEvent(m_WorkDeviceResult);
        Debug.Log("[Work/CompletedDeviceStep] " + m_WorkDeviceResult.ToString(Newtonsoft.Json.Formatting.None), this);
    }

    private void RememberUnfinishedWorkBeforeReset(bool opening)
    {
        if (!opening) { m_PreviousUnfinishedWork = null; return; }
        // A closed correction/answer is not proof that its parent request was
        // completed. Keep bounded historical outcomes, never an execution lease.
        // Retain one complete request and its outcome, never an action permission.
        // A new read-only follow-up must not overwrite it with the assistant's promise.
        // The first-reply classifier can fail while the full planner succeeds.
        // Prefer the owned canonical task, never an earlier classifier's route.
        var sequence = ReadRoomTaskSequence();
        bool planned = sequence?["steps"] is JArray steps && steps.Count > 0;
        if (!planned && (m_WorkRequestSemantics == null ||
            (bool?)m_WorkRequestSemantics["requiresActionOrControl"] != true)) return;
        var room = ReadCompactRoomTaskFacts(GetComponent<ArdyRoomDialogueBridge>());
        var earlier = new JArray();
        if (m_PreviousUnfinishedWork != null) {
            foreach (var row in (m_PreviousUnfinishedWork["earlierRequests"] as JArray ?? new JArray())) earlier.Add(row.DeepClone());
            var previous = (JObject)m_PreviousUnfinishedWork.DeepClone(); previous.Remove("earlierRequests");
            earlier.Add(previous);
            earlier = new JArray(earlier.Reverse().Take(3).Reverse());
        }
        m_PreviousUnfinishedWork = new JObject {
            ["request"] = planned ? sequence["request"]?.DeepClone() : m_WorkRequestSemantics["currentUserText"]?.DeepClone(),
            ["userTurn"] = planned ? sequence["userTurn"]?.DeepClone() : m_WorkRequestSemantics["userTurn"]?.DeepClone(),
            ["workEpoch"] = m_WorkEpoch, ["statusAtInterruption"] = m_WorkStatus,
            ["bodyAtInterruption"] = PickRoomFields(room["currentFacts"] as JObject,
                "connected", "currentlyNearAndFacingUser", "actualUserDistance", "activeActionId", "executionTargetKind"),
            ["executionAtInterruption"] = room["execution"]?.DeepClone(),
            ["resultAtInterruption"] = room["lastActionResults"]?.DeepClone(),
            ["privateFailure"] = m_PrivateDecisionFailure?.Status,
            ["taskSequence"] = sequence,
            ["executionProgress"] = WorkNextIntentProtocol.Progress(sequence),
            ["taskReceiptSource"] = planned ? "owned-task-sequence" : "reviewed-request",
            ["currentStepNotStarted"] = planned && ((JArray)sequence["steps"]).OfType<JObject>().Any(s =>
                (string)s["status"] == "pending" && string.IsNullOrEmpty((string)s["actionId"])),
            ["planningFailure"] = ReadRoomTaskDecisionFailure(),
            ["earlierRequests"] = earlier,
            ["scope"] = "Historical requests and actual outcomes, never permission. Scheduler closed does not prove the parent task completed. A correction of a prerequisite may concern an earlier request; resolve this from ALL later human turns, preserving cancellations/replacements. Completed events remain completed. Explanation-only, cancellation and negation override resumption; no historical request is automatically executed."
        };
    }

    private void ScheduleReadOnlyWorkRecheck(int generation)
    {
        if (generation != m_FormalResponseGeneration || m_ReadOnlyWorkRecheckScheduled ||
            m_PreviousUnfinishedWork == null || !m_WorkReviewOpen || m_WorkNoProgress ||
            m_WorkContinuations != 0 || m_UserSpeechActiveForAutonomy || m_SceneTimePaused) return;
        m_ReadOnlyWorkRecheckScheduled = true;
        ScheduleNextTick(2f, "read-only-work-recheck");
        Debug.Log("[Work/ReadOnlyRecheck] " + new JObject {
            ["generation"] = generation, ["workEpoch"] = m_WorkEpoch,
            ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
            ["maximumAttempts"] = 1, ["actionAuthorized"] = false,
            ["scope"] = "Review after the current reply settles; the current full request may require no action."
        }.ToString(Newtonsoft.Json.Formatting.None), this);
    }

    private void QueueWorkSubstepReview(int generation, string outcome)
    {
        if (generation != m_FormalResponseGeneration || !m_WorkReviewOpen || m_WorkNoProgress ||
            m_UserSpeechActiveForAutonomy || m_SceneTimePaused) return;
        // The independent work review decides whether ALL clauses are satisfied.
        // A failed prerequisite also needs review; it does not authorize its sequel.
        m_WorkStatus = "substep-result-pending";
        m_WorkSubstepReviewPending = true;
        MarkSettledRoomStepsReviewQueued();
        RecordWorkRoomTaskContext();
        m_SceneActionReviewUserTurn = m_ActionConstraints.CurrentUserTurn;
        ScheduleNextTick(2f, "scene-action-substep-result");
        Debug.Log("[Work/Substep] " + new JObject {
            ["generation"] = generation, ["workEpoch"] = m_WorkEpoch,
            ["userTurn"] = m_ActionConstraints.CurrentUserTurn, ["actionId"] = m_RoomTaskActionId,
            ["outcome"] = outcome, ["workClosed"] = false,
            ["scope"] = "Substep receipt only. Review the complete current request before closing or proposing the next step; no new action is authorized."
        }.ToString(Newtonsoft.Json.Formatting.None), this);
    }

    private JObject ReadMovementReplySemantics(int generation)
    {
        var current = ReadCompactCurrentRequestSemantics(generation);
        if (current != null) return current;
        var value = m_WorkRequestSemantics;
        // Reuse interpretation for REPORTING only. Planner action permission is
        // still independently reviewed for the current step, target and user.
        if (value == null || generation != m_FormalResponseGeneration || !m_WorkReviewOpen ||
            m_WorkRoomTaskEpoch != m_WorkEpoch || m_WorkRoomTaskUserTurn != m_ActionConstraints.CurrentUserTurn ||
            m_WorkRoomRequestText != m_ActionConstraints.CurrentUserText ||
            m_WorkRoomTaskActionId != m_RoomTaskActionId || string.IsNullOrEmpty(m_RoomTaskActionId) ||
            (int?)value["workEpoch"] != m_WorkEpoch || (int?)value["userTurn"] != m_ActionConstraints.CurrentUserTurn ||
            (string)value["currentUserText"] != m_ActionConstraints.CurrentUserText ||
            m_WorkRequestSemanticsModel != m_ChatSettings?.m_ChatModel ||
            !isActiveAndEnabled || m_UserSpeechActiveForAutonomy || m_SceneTimePaused) return null;
        var result = PickRoomFields(value, "requestKind", "currentTurnEvidence", "workEpoch", "userTurn", "actionAuthorization");
        result["source"] = "same-user-work-report-interpretation";
        result["sourceGeneration"] = value["generation"]?.DeepClone();
        result["generation"] = generation;
        return result;
    }

    private JObject BuildMovementPriorResults()
    {
        // A new accepted autonomous purpose may recall completed events, but
        // cannot inherit the former task's still-mandatory reporting contract.
        // Pending work prevents this purpose from being bound in the first place.
        if (ReadAutonomousReplyPurpose(m_FormalResponseGeneration) != null) return null;
        if (m_SpatialDeviceGoal == null || !SpatialGoalOwnerCurrent(m_SpatialDeviceGoal) ||
            m_SpatialDeviceGoal.latestUserText != m_ActionConstraints.CurrentUserText) return null;
        if (m_WorkDeviceResult != null && ReferenceEquals(m_WorkDeviceResultGoal, m_SpatialDeviceGoal) &&
            m_WorkDeviceResultEpoch == m_WorkEpoch && m_WorkDeviceResultTurn == m_ActionConstraints.CurrentUserTurn)
            return (JObject)m_WorkDeviceResult.DeepClone();
        var facts = BuildSpatialGoalExecutionFacts();
        // Keep failed/unknown outcomes too. These are reports of this user's
        // prior step, not present visibility or permission to operate again.
        return SpatialGoalResultContextPolicy.ProjectResultFacts(facts);
    }

    private void HandlePrivateDecisionFailure(PrivateDecisionResult result)
    {
        Debug.LogWarning("[Agent/PrivateDecisionFailure] " + new JObject {
            ["workEpoch"] = m_WorkEpoch, ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
            ["kind"] = result.Status, ["httpRequestSent"] = result.HttpRequestSent,
            ["workOpen"] = m_WorkReviewOpen, ["newActionSubmitted"] = false,
            ["scope"] = "Private review failed. No model decision, work closure or continuation permission was obtained."
        }.ToString(Newtonsoft.Json.Formatting.None), this);
        if (result.Status != "context-budget" && m_PrivateDecisionRetries++ == 0)
        { ScheduleNextTick(5f, "private-decision-retry"); return; }
        m_PrivateDecisionFailure = result;
        m_PrivateDecisionFailureEpoch = m_WorkEpoch;
        m_PrivateDecisionFailureTurn = m_ActionConstraints.CurrentUserTurn;
        // Never send the same oversized packet via the larger formal planner.
        // Keep the unmet request and diagnosis; a new user turn resets the hold.
        m_WorkSubstepReviewPending = false;
        ClearScheduledAgentWake();
        if (m_WorkReviewOpen) m_WorkStatus = "review-blocked:" + result.Status;
        else ScheduleNextTick(30f, "independent-attention-after-review-failure");
        HandleSystemNotice(new SystemNotice("private_review_failed", SystemNoticeSeverity.Error,
            "任务进度复核未完成，自动续接已暂停。", result.Status, "Agent", false));
    }
}
