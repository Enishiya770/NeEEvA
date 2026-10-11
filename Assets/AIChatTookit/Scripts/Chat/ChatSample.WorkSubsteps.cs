using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private bool m_WorkSubstepReviewPending;
    private JObject m_WorkRequestSemantics;
    private LLM m_WorkRequestSemanticsModel;
    private PrivateDecisionResult m_PrivateDecisionFailure;
    private int m_PrivateDecisionRetries;
    private JObject m_PreviousUnfinishedWork;
    private bool m_ReadOnlyWorkRecheckScheduled;
    private JObject m_WorkDeviceResult;
    private SpatialDeviceGoal m_WorkDeviceResultGoal;
    private int m_WorkDeviceResultEpoch = -1, m_WorkDeviceResultTurn = -1;

    private void RememberCompletedDeviceStep()
    {
        // A completed event survives a later speech generation. It is never an
        // execution lease or a claim that an unseen device is still unchanged.
        m_WorkDeviceResult = SpatialGoalResultContextPolicy.ProjectResultFacts(BuildSpatialGoalExecutionFacts());
        m_WorkDeviceResultGoal = m_SpatialDeviceGoal;
        m_WorkDeviceResultEpoch = m_WorkEpoch;
        m_WorkDeviceResultTurn = m_ActionConstraints.CurrentUserTurn;
        if (m_WorkDeviceResult == null) return;
        m_WorkDeviceResult["historicalResult"] = true;
        m_WorkDeviceResult["recordedGeneration"] = m_FormalResponseGeneration;
        m_WorkDeviceResult["recordedAtUtc"] = System.DateTime.UtcNow.ToString("o");
        m_WorkDeviceResult["scope"] = "Immutable result of this user's earlier step. operationSummary and lastAction describe what actually happened then, not current visibility/state or permission to repeat it. Null is unknown, never false. Current movement receipts are separate.";
        Debug.Log("[Work/CompletedDeviceStep] " + m_WorkDeviceResult.ToString(Newtonsoft.Json.Formatting.None), this);
    }

    private void RememberUnfinishedWorkBeforeReset(bool opening)
    {
        if (!opening) { m_PreviousUnfinishedWork = null; return; }
        if (!m_WorkReviewOpen) { m_PreviousUnfinishedWork = null; return; }
        // Retain one complete request and its outcome, never an action permission.
        // A new read-only follow-up must not overwrite it with the assistant's promise.
        if (m_WorkRequestSemantics == null ||
            (bool?)m_WorkRequestSemantics["requiresActionOrControl"] != true) return;
        var room = ReadCompactRoomTaskFacts(GetComponent<ArdyRoomDialogueBridge>());
        m_PreviousUnfinishedWork = new JObject {
            ["request"] = m_WorkRequestSemantics["currentUserText"]?.DeepClone(),
            ["userTurn"] = m_WorkRequestSemantics["userTurn"]?.DeepClone(),
            ["workEpoch"] = m_WorkEpoch, ["statusAtInterruption"] = m_WorkStatus,
            ["bodyAtInterruption"] = PickRoomFields(room["currentFacts"] as JObject,
                "connected", "currentlyNearAndFacingUser", "actualUserDistance", "activeActionId", "executionTargetKind"),
            ["executionAtInterruption"] = room["execution"]?.DeepClone(),
            ["resultAtInterruption"] = room["lastActionResults"]?.DeepClone(),
            ["privateFailure"] = m_PrivateDecisionFailure?.Status,
            ["scope"] = "Historical unfinished whole request, not a new instruction or permission. A satisfied device subgoal or arrival at an object does not establish arrival at the user. Re-read the complete current utterance: explanation-only, cancellation and negation override resumption."
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
        // Never send the same oversized packet via the larger formal planner.
        // Keep the unmet request and diagnosis; a new user turn resets the hold.
        m_WorkSubstepReviewPending = false;
        ClearScheduledAgentWake();
        if (m_WorkReviewOpen) m_WorkStatus = "review-blocked:" + result.Status;
        HandleSystemNotice(new SystemNotice("private_review_failed", SystemNoticeSeverity.Error,
            "任务进度复核未完成，自动续接已暂停。", result.Status, "Agent", false));
    }
}
