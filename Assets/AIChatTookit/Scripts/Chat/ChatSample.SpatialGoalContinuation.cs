using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private sealed class VerifiedSpatialDeviceReferent
    {
        public string goalId, entityId;
        public Transform environment, root;
        public UniVRM10.Vrm10Instance avatar;
        public NeEEvA.Perception.NevaSceneVisionCapture source;
        public JObject receipt;
    }

    private readonly List<VerifiedSpatialDeviceReferent> m_VerifiedSpatialDeviceHistory = new List<VerifiedSpatialDeviceReferent>();
    private readonly List<VerifiedSpatialDeviceReferent> m_VerifiedObservedDeviceHistory = new List<VerifiedSpatialDeviceReferent>();
    private JObject m_SpatialGoalLatestReview;
    private RoomTaskProtocol.Decision m_AnswerThenResumeObservation;
    private string m_AnswerThenResumeGoalId;
    private int m_AnswerThenResumeUserTurn = -1;
    private sealed class ReviewedGoalAnswerObligation
    {
        public SpatialDeviceGoal goal;
        public int userTurn, workEpoch, visionEpoch, generation, recoveryInputRevision=-1, recoveryVisionEpoch=-1;
        public LLM model;
        public string mode, expectedText, playedText;
        public bool delivered;
    }
    private ReviewedGoalAnswerObligation m_ReviewedGoalAnswerObligation;

    private bool ReviewedGoalAnswerOwnerCurrent(SpatialDeviceGoal goal)
    {
        var answer=m_ReviewedGoalAnswerObligation;
        return answer != null && ReferenceEquals(answer.goal,goal) && ReferenceEquals(goal,m_SpatialDeviceGoal) &&
            SpatialGoalOwnerCurrent(goal) && answer.userTurn == m_ActionConstraints.CurrentUserTurn &&
            answer.workEpoch == m_WorkEpoch && answer.visionEpoch == m_SceneVisionEpoch &&
            answer.model != null && answer.model == m_ChatSettings?.m_ChatModel;
    }

    private void RequireReviewedGoalAnswer(string mode)
    {
        m_ReviewedGoalAnswerObligation=new ReviewedGoalAnswerObligation {
            goal=m_SpatialDeviceGoal,userTurn=m_ActionConstraints.CurrentUserTurn,workEpoch=m_WorkEpoch,
            visionEpoch=m_SceneVisionEpoch,generation=m_FormalResponseGeneration,model=m_ChatSettings?.m_ChatModel,mode=mode };
    }

    private bool ReviewedGoalAnswerAllowsResume(SpatialDeviceGoal goal)
    {
        var answer=m_ReviewedGoalAnswerObligation;
        if (answer == null || !ReferenceEquals(answer.goal,goal) || answer.userTurn != m_ActionConstraints.CurrentUserTurn) return true;
        return ReviewedGoalAnswerOwnerCurrent(goal) && answer.mode == "answer-then-resume" && answer.delivered;
    }

    private void PreserveReviewedGoalAnswerForInputRecovery()
    {
        if (ReviewedGoalAnswerOwnerCurrent(m_SpatialDeviceGoal) && !m_ReviewedGoalAnswerObligation.delivered &&
            m_InterruptedUserQuestion == m_ActionConstraints.CurrentUserText && m_InterruptedUserTurn == m_ActionConstraints.CurrentUserTurn)
            m_ReviewedGoalAnswerObligation.recoveryInputRevision=m_InputAudioRevision+1;
    }

    private void SealReviewedGoalAnswerInputRecovery()
    {
        var answer=m_ReviewedGoalAnswerObligation;
        if (answer != null && answer.recoveryInputRevision == m_InputAudioRevision &&
            ReferenceEquals(answer.goal,m_SpatialDeviceGoal) && SpatialGoalOwnerCurrent(answer.goal) &&
            answer.userTurn == m_ActionConstraints.CurrentUserTurn && answer.workEpoch == m_WorkEpoch &&
            answer.model == m_ChatSettings?.m_ChatModel)
            answer.recoveryVisionEpoch=m_SceneVisionEpoch;
    }

    private bool CurrentGenerationHasRejectedDeviceRequest(int generation)
        => generation == m_FormalResponseGeneration &&
            (int?)m_SpatialGoalLatestReview?["generation"] == generation &&
            (int?)m_SpatialGoalLatestReview?["userTurn"] == m_ActionConstraints.CurrentUserTurn &&
            (int?)m_SpatialGoalLatestReview?["workEpoch"] == m_WorkEpoch &&
            (bool?)m_SpatialGoalLatestReview?["registered"] == false &&
            (string)m_SpatialGoalLatestReview?["requestAssessment"] == "requested" &&
            (string)m_SpatialGoalLatestReview?["proposal"]?["operation"] == "set" &&
            (string)m_SpatialGoalLatestReview?["originalRequest"] == m_ActionConstraints.CurrentUserText;

    private bool CurrentGenerationRequiresReviewedGoalAnswer(int generation)
        => generation == m_FormalResponseGeneration && !CurrentGenerationHasRejectedDeviceRequest(generation) &&
            (ReviewedGoalAnswerOwnerCurrent(m_SpatialDeviceGoal) && !m_ReviewedGoalAnswerObligation.delivered &&
                m_ReviewedGoalAnswerObligation.generation == generation ||
             (int?)m_SpatialGoalLatestReview?["generation"] == generation && (bool?)m_SpatialGoalLatestReview?["answerRequired"] == true);

    private bool RestoreUndeliveredReviewedGoalAnswer(int generation)
    {
        var answer=m_ReviewedGoalAnswerObligation;
        if (generation != m_FormalResponseGeneration || answer == null || answer.delivered) return false;
        bool exactInputRecovery=ReferenceEquals(answer.goal,m_SpatialDeviceGoal) && SpatialGoalOwnerCurrent(answer.goal) &&
            answer.userTurn == m_ActionConstraints.CurrentUserTurn && answer.workEpoch == m_WorkEpoch &&
            answer.model == m_ChatSettings?.m_ChatModel && answer.recoveryInputRevision == m_InputAudioRevision &&
            answer.recoveryVisionEpoch == m_SceneVisionEpoch;
        if (!ReviewedGoalAnswerOwnerCurrent(m_SpatialDeviceGoal) && !exactInputRecovery) return false;
        answer.generation=generation; answer.expectedText=null; answer.playedText=null; answer.visionEpoch=m_SceneVisionEpoch;
        answer.recoveryInputRevision=-1; answer.recoveryVisionEpoch=-1;
        m_SpatialDeviceGoal.visionEpoch=m_SceneVisionEpoch;
        if (m_SpatialGoalLatestReview != null)
        {
            m_SpatialGoalLatestReview=(JObject)m_SpatialGoalLatestReview.DeepClone();
            m_SpatialGoalLatestReview["generation"]=generation;
            m_SpatialGoalLatestReview["answerRequired"]=true;
            m_SpatialGoalLatestReview["responseMode"]=answer.mode;
        }
        m_SpatialDeviceGoal.executionSuspended=true;
        m_SpatialDeviceGoal.resumeAfterDialogue=answer.mode == "answer-then-resume";
        return true;
    }

    private static string ReviewedGoalAnswerFallback(string language)
        => language == "ja" ? "その質問への答えはまだ確認できていないわ。方角や理由、進み具合を推測で答えることはできないの。" :
           language == "en" ? "I haven't confirmed the answer to that question yet. I can't give an unverified direction, cause, or progress as fact." :
           "我还没有确认这次问题的答案，不能把未核实的方位、原因或进展当成事实。";

    private static string NormalizeReviewedAnswerText(string text)
        => new string((text ?? "").Where(value=>!char.IsWhiteSpace(value)).ToArray());

    private void BindReviewedGoalAnswerSpeech(int generation,string text)
    {
        if (generation == m_FormalResponseGeneration && ReviewedGoalAnswerOwnerCurrent(m_SpatialDeviceGoal) &&
            !m_ReviewedGoalAnswerObligation.delivered && m_ReviewedGoalAnswerObligation.generation == generation)
        {
            m_ReviewedGoalAnswerObligation.expectedText=NormalizeReviewedAnswerText(text);
            m_ReviewedGoalAnswerObligation.playedText=null;
        }
    }

    private void RecordReviewedGoalAnswerPlayedChunk(int generation,string text)
    {
        var answer=m_ReviewedGoalAnswerObligation;
        if (generation != m_FormalResponseGeneration || !ReviewedGoalAnswerOwnerCurrent(m_SpatialDeviceGoal) ||
            answer.generation != generation || answer.delivered || string.IsNullOrEmpty(answer.expectedText)) return;
        // Only completed formal-answer chunks count; latency filler and interrupted partial audio do not.
        answer.playedText+=NormalizeReviewedAnswerText(text);
    }

    private void CompleteReviewedGoalAnswerPlayback(int generation)
    {
        var answer=m_ReviewedGoalAnswerObligation;
        if (generation != m_FormalResponseGeneration || !ReviewedGoalAnswerOwnerCurrent(m_SpatialDeviceGoal) ||
            answer.generation != generation || string.IsNullOrEmpty(answer.expectedText) ||
            answer.expectedText != answer.playedText) return;
        answer.delivered=true;
        Debug.Log($"[Vision/GoalAnswer] goal={answer.goal.id} userTurn={answer.userTurn} generation={generation} delivered=true mode={answer.mode}",this);
    }

    // A restriction survives recovery of this same accepted user turn, even
    // after its pending draft was cancelled. It grants no action authority.
    private sealed class IndependentSpatialRequestScope
    {
        public int userTurn;
        public Transform environment;
        public UniVRM10.Vrm10Instance avatar;
        public NeEEvA.Perception.NevaSceneVisionCapture source;
        public LLM model;
        public JObject appliedReview;
    }
    private IndependentSpatialRequestScope m_IndependentSpatialRequestScope;

    // Inspection establishes a historical referent before the first operation.
    // A new independent authorization may select this source as a same-entity
    // constraint. It never supplies device-write authority or current visibility.
    private void RememberVerifiedObservedDeviceReferent(SceneActionFact fact)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (fact == null || fact.phase != "observed" || !fact.semanticTargetConfirmed ||
            !fact.objectConfirmed || fact.targetPurpose != "direct-target" ||
            string.IsNullOrEmpty(fact.entityId) || string.IsNullOrEmpty(fact.matchCaptureId) ||
            string.IsNullOrEmpty(fact.confirmedCaptureId) || fact.resultCaptureId != fact.confirmedCaptureId ||
            !KnownSceneObjectBinding(fact.entityId, out var known) || bridge == null ||
            (string)m_SelectedSceneObject?["entityId"] != fact.entityId ||
            (string)m_SelectedSceneObject?["captureId"] != fact.resultCaptureId) return;
        var item = new VerifiedSpatialDeviceReferent {
            entityId = fact.entityId, root = known.root, environment = bridge.environmentRoot,
            avatar = bridge.avatar, source = m_SceneVisionCapture,
            receipt = new JObject {
                ["entityId"] = fact.entityId, ["actionId"] = fact.actionId,
                ["originalRequest"] = SpatialGoalBoundText(m_ActionConstraints.CurrentUserText, 600),
                ["targetDescription"] = fact.targetDescription, ["semanticReason"] = fact.targetMatchReason,
                ["matchCaptureId"] = fact.matchCaptureId, ["confirmedCaptureId"] = fact.confirmedCaptureId,
                ["observedUtc"] = fact.observedUtc, ["historical"] = true,
                ["semanticTargetConfirmedAtObservation"] = true,
                ["observation"] = PickRoomFields(m_SelectedSceneObject, "entityId", "sceneLabel", "captureId", "observedUtc", "capabilities"),
                ["scope"] = "Actual direct-target semantic match followed by same-entity opaque confirmation in a new image and observed registered capability. Previously seen referent only: no executed operation, goal ID, current visibility, current power state or action authority. Assistant speech alone cannot create this record. An independent authorization may select this engine-listed source to constrain the same entity; every action still needs its own fresh semantic match."
            }
        };
        if (!VerifiedSpatialReferentCurrent(item)) return;
        m_VerifiedObservedDeviceHistory.RemoveAll(old => old.entityId == item.entityId);
        m_VerifiedObservedDeviceHistory.Add(item);
        while (m_VerifiedObservedDeviceHistory.Count > 6) m_VerifiedObservedDeviceHistory.RemoveAt(0);
    }

    private JArray BuildVerifiedObservedDeviceReferentHistory() => new JArray(m_VerifiedObservedDeviceHistory
        .Where(VerifiedSpatialReferentCurrent).Select(item => item.receipt.DeepClone()));

    private bool VerifiedSpatialReferentCurrent(VerifiedSpatialDeviceReferent item)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var device = item?.root != null ? item.root.GetComponent<NeEEvA.Perception.SceneObjectLightSwitch>() : null;
        return item != null && bridge != null && item.environment != null && item.environment == bridge.environmentRoot &&
            item.environment.gameObject.scene == gameObject.scene && item.avatar != null && item.avatar == bridge.avatar &&
            item.source != null && item.source == m_SceneVisionCapture && item.source.Target == item.avatar &&
            device != null && device.IsBound && device.EntityId == item.entityId && device.TryValidate(out _);
    }

    private void RememberVerifiedSpatialDeviceReferent(SpatialDeviceGoal goal, SceneActionFact fact, JObject evidence)
    {
        if (!SpatialGoalExecutionCurrent(goal) || goal.status != "satisfied" || evidence == null ||
            (string)evidence["entityId"] != goal.entityId || !fact.semanticTargetConfirmed) return;
        if (fact.phase != "performed" && m_VerifiedSpatialDeviceHistory.Any(item => item.goalId == goal.id &&
            (bool?)item.receipt["actualOperationPerformed"] == true)) return;
        m_VerifiedSpatialDeviceHistory.RemoveAll(item => item.goalId == goal.id);
        m_VerifiedSpatialDeviceHistory.Add(new VerifiedSpatialDeviceReferent {
            goalId=goal.id, entityId=goal.entityId, root=goal.entityRoot, environment=goal.environment,
            avatar=goal.avatar, source=goal.source,
            receipt=new JObject {
                ["goalId"]=goal.id, ["entityId"]=goal.entityId, ["originalRequest"]=goal.originalRequest,
                ["targetDescription"]=goal.targetDescription, ["desiredState"]=goal.desiredState,
                ["actionPhase"]=fact.phase, ["actualOperationPerformed"]=fact.phase == "performed",
                ["completionEvidence"]=evidence.DeepClone(), ["historical"]=true,
                ["identitySource"]="same-target-semantic-match-and-fresh-engine-completion-receipt",
                ["scope"]="Verified historical referent and state outcome only. This supplies identity context, not current visibility, position or device-write authority."
            }
        });
        while (m_VerifiedSpatialDeviceHistory.Count > 6) m_VerifiedSpatialDeviceHistory.RemoveAt(0);
    }

    private JArray BuildVerifiedDeviceReferentHistory() => new JArray(m_VerifiedSpatialDeviceHistory
        .Where(VerifiedSpatialReferentCurrent).Select(item=>item.receipt.DeepClone()));

    // Source IDs name authoritative receipts, never a model-supplied entity pair.
    // Keep both kinds even for one entity: seeing it and operating it establish
    // different facts. The semantic reviewer must select the appropriate source.
    private JArray BuildSpatialGoalReferentSources()
    {
        var result = new JArray();
        foreach (var item in m_VerifiedObservedDeviceHistory.Where(VerifiedSpatialReferentCurrent))
        {
            string actionId = (string)item.receipt?["actionId"];
            if (string.IsNullOrEmpty(actionId)) continue;
            result.Add(new JObject {
                ["sourceId"] = "observed-device:" + actionId, ["kind"] = "observed-device",
                ["entityId"] = item.entityId,
                ["originalRequest"] = item.receipt["originalRequest"]?.DeepClone(),
                ["targetDescription"] = item.receipt["targetDescription"]?.DeepClone(),
                ["observedUtc"] = item.receipt["observedUtc"]?.DeepClone()
            });
        }
        foreach (var item in m_VerifiedSpatialDeviceHistory.Where(VerifiedSpatialReferentCurrent))
        {
            if (string.IsNullOrEmpty(item.goalId)) continue;
            result.Add(new JObject {
                ["sourceId"] = "operated-goal:" + item.goalId, ["kind"] = "operated-goal",
                ["entityId"] = item.entityId,
                ["originalRequest"] = item.receipt["originalRequest"]?.DeepClone(),
                ["targetDescription"] = item.receipt["targetDescription"]?.DeepClone(),
                ["desiredState"] = item.receipt["desiredState"]?.DeepClone(),
                ["actionPhase"] = item.receipt["actionPhase"]?.DeepClone()
            });
        }
        return result;
    }

    private bool TryResolveReviewedSpatialReferent(RoomTaskProtocol.DeviceGoalReview review,
        out string entityId, out Transform root, out string reason)
    {
        entityId=null; root=null; reason=null;
        if (review?.HasTypedReferent == true)
        {
            if (!RoomTaskProtocol.TryValidateListedDeviceReferent(review, BuildSpatialGoalReferentSources(), out reason)) return false;
            if (review.referentKind == "unbound") return true;
            var source = review.referentKind == "observed-device" ?
                m_VerifiedObservedDeviceHistory.LastOrDefault(item =>
                    "observed-device:" + (string)item.receipt?["actionId"] == review.referentSourceId && VerifiedSpatialReferentCurrent(item)) :
                m_VerifiedSpatialDeviceHistory.LastOrDefault(item =>
                    "operated-goal:" + item.goalId == review.referentSourceId && VerifiedSpatialReferentCurrent(item));
            if (source == null) { reason="device-goal-reviewed-source-no-longer-current"; return false; }
            entityId=source.entityId; root=source.root; return true;
        }
        string reviewedEntity=review?.referentEntityId, reviewedGoal=review?.referentGoalId;
        if (string.IsNullOrEmpty(reviewedEntity)) return true;
        var standing=m_SpatialDeviceGoal;
        if (!string.IsNullOrEmpty(reviewedGoal) && SpatialGoalOwnerCurrent(standing) && standing.id == reviewedGoal &&
            standing.entityId == reviewedEntity && standing.entityRoot != null)
        { entityId=standing.entityId; root=standing.entityRoot; return true; }
        var history=m_VerifiedSpatialDeviceHistory.LastOrDefault(item => item.goalId == reviewedGoal &&
            item.entityId == reviewedEntity && VerifiedSpatialReferentCurrent(item));
        if (history == null) { reason="device-goal-reviewed-referent-not-in-verified-history"; return false; }
        entityId=history.entityId; root=history.root; return true;
    }

    private static bool SpatialGoalUnfinished(SpatialDeviceGoal goal) => goal != null && goal.status != "satisfied" && goal.status != "cancelled";

    private void PauseSpatialGoal(string reason)
    {
        var goal=m_SpatialDeviceGoal;
        if (!SpatialGoalOwnerCurrent(goal) || !SpatialGoalUnfinished(goal)) return;
        bool canResume=goal.status != "waiting_clarification" && goal.status != "blocked";
        goal.executionSuspended=true; ++goal.executionRevision;
        if (goal.status != "waiting_clarification" && goal.status != "blocked") goal.status="suspended";
        goal.reason=SpatialGoalBoundText(reason, 240);
        goal.resumeAfterDialogue=canResume && goal.lastReviewedUserTurn == m_ActionConstraints.CurrentUserTurn &&
            (!ReviewedGoalAnswerOwnerCurrent(goal) || m_ReviewedGoalAnswerObligation.mode == "answer-then-resume");
        Debug.Log($"[Vision/GoalLifecycle] goal={goal.id} state={goal.status} execution=paused revision={goal.executionRevision} reason={goal.reason}", this);
    }

    private void SetSpatialGoalAwaitingClarification(string reason)
    {
        var goal=m_SpatialDeviceGoal;
        if (!SpatialGoalOwnerCurrent(goal) || !SpatialGoalUnfinished(goal)) return;
        goal.status="waiting_clarification"; goal.reason=SpatialGoalBoundText(reason, 240);
        goal.executionSuspended=true; goal.resumeAfterDialogue=false; ++goal.executionRevision;
        m_WorkStatus="waiting_user"; m_WorkReviewOpen=false;
    }

    private void BindSpatialGoalExecution(SpatialDeviceGoal goal)
    {
        if (!SpatialGoalOwnerCurrent(goal) || !SpatialGoalUnfinished(goal)) return;
        ++goal.executionRevision; goal.executionSuspended=false;
        goal.currentRequestOnlyUserTurn=-1;
        goal.workEpoch=m_WorkEpoch; goal.userTurn=m_ActionConstraints.CurrentUserTurn;
        goal.formalGeneration=m_FormalResponseGeneration; goal.visionEpoch=m_SceneVisionEpoch;
        goal.latestUserText=m_ActionConstraints.CurrentUserText;
        goal.status="searching"; goal.resumeAfterDialogue=false;
    }

    private bool NeedsSpatialGoalUserUpdateReview(bool autonomous) => !autonomous && SpatialGoalOwnerCurrent(m_SpatialDeviceGoal) &&
        SpatialGoalUnfinished(m_SpatialDeviceGoal) && m_SpatialDeviceGoal.lastReviewedUserTurn != m_ActionConstraints.CurrentUserTurn;

    private bool IndependentSpatialRequestScopeCurrent()
    {
        var scope = m_IndependentSpatialRequestScope;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (scope != null && scope.userTurn == m_ActionConstraints.CurrentUserTurn &&
            scope.environment != null && scope.environment == bridge?.environmentRoot &&
            scope.environment.gameObject.scene == gameObject.scene && scope.avatar != null &&
            scope.avatar == bridge.avatar && scope.source != null && scope.source == m_SceneVisionCapture &&
            scope.source.Target == scope.avatar && scope.model != null && scope.model == m_ChatSettings?.m_ChatModel)
            return true;
        return false;
    }

    private bool CurrentUserTurnRequiresIndependentSpatialRequest() => IndependentSpatialRequestScopeCurrent() ||
        (SpatialGoalOwnerCurrent(m_SpatialDeviceGoal) && SpatialGoalUnfinished(m_SpatialDeviceGoal) &&
            m_SpatialDeviceGoal.currentRequestOnlyUserTurn == m_ActionConstraints.CurrentUserTurn);

    private void RestrictSpatialPlanningToCurrentRequest()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        m_IndependentSpatialRequestScope = new IndependentSpatialRequestScope {
            userTurn = m_ActionConstraints.CurrentUserTurn, environment = bridge?.environmentRoot,
            avatar = bridge?.avatar, source = m_SceneVisionCapture, model = m_ChatSettings?.m_ChatModel,
            appliedReview = m_SpatialGoalLatestReview == null ? null : (JObject)m_SpatialGoalLatestReview.DeepClone()
        };
    }

    private void RestoreIndependentSpatialReview(int generation)
    {
        var receipt = m_IndependentSpatialRequestScope?.appliedReview;
        if (receipt == null || !IndependentSpatialRequestScopeCurrent() ||
            (int?)m_SpatialGoalLatestReview?["generation"] == generation) return;
        // Empty-ASR recovery is still this accepted human turn. Rebind only the
        // same owner-scoped applied result, never a model proposal from history.
        m_SpatialGoalLatestReview = (JObject)receipt.DeepClone();
        m_SpatialGoalLatestReview["recoveredFromGeneration"] = receipt["generation"]?.DeepClone();
        m_SpatialGoalLatestReview["generation"] = generation;
    }

    private void RestrictUnapprovedSpatialGoalUpdateToCurrentRequest()
    {
        RestrictSpatialPlanningToCurrentRequest();
        var goal=m_SpatialDeviceGoal;
        if (!SpatialGoalOwnerCurrent(goal) || !SpatialGoalUnfinished(goal)) return;
        goal.lastReviewedUserTurn=m_ActionConstraints.CurrentUserTurn;
        goal.currentRequestOnlyUserTurn=m_ActionConstraints.CurrentUserTurn;
    }

    private static bool TryValidateCurrentRequestOnlyDecision(RoomTaskProtocol.Decision decision, out string reason)
    {
        reason=null;
        if (decision == null || decision.deviceGoal != null || decision.IsObjectState)
        { reason="invalid-current-request-only-device-authority: plan only the latest independent request; no device proposal, cancellation or state write is authorized in this planning pass"; return false; }
        return true;
    }

    private string BuildCurrentRequestOnlyDecisionInput(string input)
    {
        var value=JObject.Parse(input);
        bool autonomous=(bool?)value["autonomous"]==true;
        value["standingGoal"]=value["spatialGoal"]?.DeepClone();
        value["spatialGoal"]=null;
        value["pendingDeviceRequest"]=BuildPendingDeviceClarificationContext();
        value["latestGoalUpdate"]=!autonomous && (int?)m_SpatialGoalLatestReview?["generation"] == m_FormalResponseGeneration ?
            m_SpatialGoalLatestReview.DeepClone() : null;
        if(autonomous) { value["currentUserText"]=""; value["latestUserText"]=""; }
        value["currentRequestOnly"]=true;
        value["quietSpatialGoalStep"]=false;
        // A parentless observation made for this exact new request remains
        // usable; the paused device goal's observation children never do.
        var observed=(value["objectTargetReview"] as JObject)?["observationGoal"] as JObject;
        bool currentObservation=observed != null && string.IsNullOrEmpty((string)observed["parentSpatialGoalId"]) &&
            (int?)observed["userTurnAtCreation"] == m_ActionConstraints.CurrentUserTurn &&
            ((string)observed["status"] == "pending" || (string)observed["status"] == "historical");
        value["pendingObservationGoal"]=currentObservation ? observed.DeepClone() : null;
        var contract=(JObject)value["decisionContractFacts"];
        contract["deviceGoalAlreadyReviewed"]=false;
        contract["allowedObservationGoalIds"]=currentObservation ? new JArray((string)observed["goalId"]) : new JArray();
        contract["currentRequestOnlyRule"]=autonomous ?
            "This tick has no new human request. The old device-goal restriction still forbids deviceGoal and set-object-state; it does not replay the old independent search or turn. Plan only the current autonomousIntention and quote that intention as evidence. Merely explaining a result or asking the user a question requires no spatial action. Choose none with origin=none and empty evidence when no motion is intended. Repair cannot promote a no-action choice into motion." :
            "Plan only currentUserText, the complete actual latest user request. standingGoal and pendingDeviceRequest are background context, not action authority. Their quoted target description may explain what the user now wants to look for; an earlier review rejection is not a user cancellation. Do not resume the old device operation or output deviceGoal (including cancel) or set-object-state. A currently requested independent search, turn, look, inspection, observation approach, user movement or stop remains available under normal evidence checks. If latestGoalUpdate.cancellationApplied=true, cancellation is already applied: handle any remaining action in the SAME user sentence without asking to cancel again. Choose none when no independent action is requested. These limits also apply to decision repair.";
        return value.ToString(Newtonsoft.Json.Formatting.None);
    }

    private bool TryApplySpatialGoalUserUpdate(RoomTaskProtocol.DeviceGoalReview review, string userText,
        out bool continueGoal, out string reason)
    {
        continueGoal=false; reason=null;
        var goal=m_SpatialDeviceGoal;
        if (!SpatialGoalOwnerCurrent(goal) || !SpatialGoalUnfinished(goal) || review == null ||
            !review.HasUserUpdateFields || review.responseMode == null || !review.IsApproved || !string.Equals(userText, m_ActionConstraints.CurrentUserText, StringComparison.Ordinal))
        { reason="spatial-goal-user-update-not-independently-approved"; return false; }
        if (review.relation != "new" && review.relation != "unrelated" && review.referentGoalId != goal.id)
        { reason="spatial-goal-user-update-goal-id-mismatch"; return false; }
        if (!TryResolveReviewedSpatialReferent(review, out string entity, out Transform root, out reason)) return false;
        review.responseMode = RoomTaskProtocol.ResolveGoalUpdateResponseMode(review,
            ReadCompactCurrentRequestSemantics(m_FormalResponseGeneration));
        m_AnswerThenResumeObservation=null; m_AnswerThenResumeGoalId=null; m_AnswerThenResumeUserTurn=-1;
        m_ReviewedGoalAnswerObligation=null;
        goal.latestUserText=userText; goal.lastReviewedUserTurn=m_ActionConstraints.CurrentUserTurn;
        goal.userUpdates.Add(new JObject { ["userTurn"]=goal.lastReviewedUserTurn, ["text"]=SpatialGoalBoundText(userText, 600),
            ["relation"]=review.relation, ["reason"]=review.reason, ["responseMode"]=review.responseMode });
        while (goal.userUpdates.Count > 4) goal.userUpdates.RemoveAt(0);
        switch (review.relation)
        {
            case "continue": case "clarify":
                if (!string.IsNullOrEmpty(entity))
                {
                    if (!string.IsNullOrEmpty(goal.entityId) && goal.entityId != entity)
                    { reason="spatial-goal-continuation-cannot-switch-bound-entity"; return false; }
                    goal.entityId=entity; goal.entityRoot=root;
                }
                BindSpatialGoalExecution(goal);
                if (review.responseMode == "act") continueGoal=true;
                else
                {
                    PauseSpatialGoal("answer-reviewed-user-question-before-any-device-resume");
                    goal.resumeAfterDialogue=review.responseMode == "answer-then-resume";
                }
                break;
            case "cancel": CancelSpatialGoal("independently-reviewed-user-cancellation"); break;
            case "new": CancelSpatialGoal("independently-reviewed-user-replacement"); break;
            case "unrelated":
                PauseSpatialGoal("unrelated-user-dialogue-preserves-authorized-goal");
                // Only this independently approved relation may carry the old
                // objective across a new user/work/vision owner. A later epoch
                // change without another review must not silently rebind it.
                goal.workEpoch=m_WorkEpoch; goal.userTurn=m_ActionConstraints.CurrentUserTurn;
                goal.visionEpoch=m_SceneVisionEpoch;
                // Preserving the old objective is not permission to resume it
                // after an independent request, even without first-reply routing.
                goal.resumeAfterDialogue=false;
                break;
            default: reason="invalid-spatial-goal-user-update-relation"; return false;
        }
        if (review.responseMode != "act") RequireReviewedGoalAnswer(review.responseMode);
        goal.currentRequestOnlyUserTurn=-1;
        Debug.Log($"[Vision/GoalUserUpdate] goal={goal.id} userTurn={goal.lastReviewedUserTurn} relation={review.relation} state={goal.status} continuing={continueGoal}", this);
        return true;
    }

    // A relative head/body request has no stored pixel target. Hold it until the
    // answer finishes, then bind it to the new current capture under the same
    // reviewed goal/user owner; a later user turn cannot replay this preface.
    private bool DeferReviewedObservationUntilAfterAnswer(RoomTaskProtocol.Decision decision)
    {
        var goal=m_SpatialDeviceGoal;
        if (decision?.IsViewChange != true || decision.viewChange == null || decision.origin != "user" ||
            !SpatialGoalOwnerCurrent(goal) || !SpatialGoalUnfinished(goal) || !goal.resumeAfterDialogue ||
            !goal.executionSuspended || goal.lastReviewedUserTurn != m_ActionConstraints.CurrentUserTurn ||
            goal.latestUserText != m_ActionConstraints.CurrentUserText || string.IsNullOrWhiteSpace(decision.evidence) ||
            decision.evidence.Length > 160 || goal.latestUserText.IndexOf(decision.evidence, StringComparison.Ordinal) < 0) return false;
        m_AnswerThenResumeObservation=decision;
        m_AnswerThenResumeGoalId=goal.id; m_AnswerThenResumeUserTurn=m_ActionConstraints.CurrentUserTurn;
        return true;
    }

    private bool TryResumeSpatialGoalAfterDialogue()
    {
        var goal=m_SpatialDeviceGoal;
        if (!HasRoomTaskPlanner || !SpatialGoalOwnerCurrent(goal) || !SpatialGoalUnfinished(goal) || !goal.resumeAfterDialogue || !ReviewedGoalAnswerAllowsResume(goal) ||
            goal.status == "blocked" || goal.status == "waiting_clarification" || CurrentUserTurnRequiresIndependentSpatialRequest() ||
            goal.lastReviewedUserTurn != m_ActionConstraints.CurrentUserTurn || goal.userTurn != m_ActionConstraints.CurrentUserTurn ||
            goal.workEpoch != m_WorkEpoch || goal.visionEpoch != m_SceneVisionEpoch ||
            !isActiveAndEnabled || !SceneEyesOpen || !RoomMotionOutputEnabled || Time.timeScale <= 0 ||
            m_UserSpeechActiveForAutonomy || m_SceneTimePaused ||
            IsAISpeaking || IsVoiceOutputPlaying || m_FormalResponseInFlight || m_AgentRoundInFlight) return false;
        int generation=BeginFormalResponseGeneration();
        BindSpatialGoalExecution(goal);
        int executionRevision=goal.executionRevision;
        m_AgentCurrentRoundIsTick=true; m_AgentRoundInFlight=true;
        m_FormalResponseInFlight=true; m_RoomTaskGeneration=generation;
        m_RoomSpeechGuardGeneration=generation; m_RoomSpeechReviewModel=m_ChatSettings.m_ChatModel;
        m_RoomTaskIntent="device-task"; m_RoomTaskDispatch="resuming-reviewed-goal";
        RequestSceneObservation("spatial-goal-dialogue-resume", observation => {
            if (generation != m_FormalResponseGeneration || !ReferenceEquals(m_SpatialDeviceGoal, goal) ||
                goal.executionRevision != executionRevision || !SpatialGoalExecutionCurrent(goal) ||
                !isActiveAndEnabled || !SceneEyesOpen || !RoomMotionOutputEnabled || Time.timeScale <= 0 ||
                m_UserSpeechActiveForAutonomy || m_SceneTimePaused || CurrentUserTurnRequiresIndependentSpatialRequest()) return;
            if (!IsFreshSceneObservation(observation))
            {
                SetSpatialGoalAwaitingClarification("standing-goal-resume-observation-unavailable");
                DispatchSpatialGoalTerminalReply(generation);
                return;
            }
            BindFormalSceneObservation(observation, generation);
            // This is the same reviewed user objective, not a new autonomous
            // device proposal. The task owns search, approach, operation and its
            // bounded recovery; dialogue cannot restart a blocked task budget.
            RoomTaskProtocol.Decision preface=null;
            if (m_AnswerThenResumeGoalId == goal.id && m_AnswerThenResumeUserTurn == m_ActionConstraints.CurrentUserTurn)
            {
                preface=m_AnswerThenResumeObservation;
                if (preface?.viewChange != null) preface.viewChange.captureId=observation.CaptureId;
            }
            m_AnswerThenResumeObservation=null; m_AnswerThenResumeGoalId=null; m_AnswerThenResumeUserTurn=-1;
            if (!TryBeginSpatialGoalRunnerWithObservation(goal.id, generation, preface))
            {
                SetSpatialGoalAwaitingClarification("standing-goal-runner-unavailable");
                DispatchSpatialGoalTerminalReply(generation);
            }
        });
        return true;
    }
}
