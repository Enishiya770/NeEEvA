using System;
using System.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private ActorPlanRuntime m_ActorPlan;
    private FastLoopTurnContext m_ActorPlanTurn;
    private int m_ActorPlanRevocation;
    private int m_ActorPlanWatchedRevision = -1;
    private long m_ActorPlanInputSequence;
    private bool HasActiveActorPlan => m_ActorPlan?.Active == true;

    private void BeginUserGoalPlan(FastLoopTurnContext turn)
    {
        string userText = FastLoopCurrentUserText(turn.Prompt);
        if (!FastLoopTurnCurrent(turn) || string.IsNullOrWhiteSpace(userText)) return;
        CancelActorPlan("replaced-by-current-user-goal");
        m_ActorPlan = new ActorPlanRuntime(userText, "user");
        if (m_RecoveredInputGeneration == turn.Generation) m_ActorPlan.RestoreCompletedCheckpoint(m_RecoveredPlanCheckpoint);
        m_ActorPlanWatchedRevision = -1;
        m_ActorPlanTurn = turn;
        m_ActorPlanInputSequence = m_ActorInputSequence;
        m_ActorPlanRevocation = m_RoomActorProposalRevocation;
        turn.ActorProposal = new JObject {
            ["source"] = "current-user-goal", ["goalId"] = m_ActorPlan.Id,
            ["goalRevision"] = 1, ["text"] = userText, ["purpose"] = "plan",
            ["scope"] = "Original current user request, not a role draft or completed action. The room decision may adopt a step or choose none. Preserve the whole unmet request across steps; do not convert refusal or dialogue into movement."
        };
        UpdateActorPlanProposal();
        m_ResponseTrace?.Mark("actor_plan.registered", new JObject {
            ["id"] = m_ActorPlan.Id, ["generation"] = turn.Generation,
            ["source"] = "current-user-goal", ["goal"] = userText, ["fullDraftRetained"] = false });
    }

    private void CancelActorPlan(string reason)
    {
        var plan = m_ActorPlan;
        RevokeActorBodyHandoff(plan, reason);
        if (plan == null || !plan.Active && plan.State != "ongoing") return;
        plan.Cancel(reason);
        RevokeActorPlanAcceptance(plan, reason);
        m_ResponseTrace?.Mark("actor_plan.cancelled", new JObject {
            ["id"] = plan.Id, ["revision"] = plan.Revision, ["reason"] = reason });
    }

    private bool ActorPlanOwnerCurrent(ActorPlanRuntime plan, FastLoopTurnContext turn) =>
        ReferenceEquals(plan, m_ActorPlan) && ReferenceEquals(turn, m_ActorPlanTurn) &&
        plan != null && FastLoopTurnCurrent(turn) &&
        m_ActorPlanRevocation == m_RoomActorProposalRevocation;

    private bool ActorPlanCurrent(ActorPlanRuntime plan, FastLoopTurnContext turn) =>
        ActorPlanOwnerCurrent(plan, turn) && plan.Active;

    private void BeginActorPlan(FastLoopTurnContext turn, ActorDraft draft, ActorIntentDecision decision)
    {
        if (draft == null || decision?.RequiresFullReview == true || (decision?.Kind != "replan" && decision?.Kind != "act") ||
            (string)turn.ActorProposal?["purpose"] != "plan") return;
        CancelActorPlan("replaced-by-new-role-goal");
        // The unabridged role draft remains the authority. The model's concise
        // goal is a useful explanation, not permission to drop a later clause.
        m_ActorPlan = new ActorPlanRuntime(draft.Text, "actor_draft", decision.Coverage, decision.Goal);
        if (decision.Domain == "other_action" && decision.Coverage == "atomic" &&
            decision.MotionScope == "body_turn" && decision.Degrees.HasValue)
            m_ActorPlan.AdoptBodyTurnCompletion(decision.Degrees.Value);
        if (decision.Domain == "other_action" && decision.Coverage == "atomic" && decision.Task == "other" &&
            decision.DeviceTargetIsProgramBound)
            m_ActorPlan.AdoptDeviceCompletion(1, decision.TargetEntityId, decision.DeviceState, draft.Text, "actor_draft");
        if (m_RecoveredInputGeneration == turn.Generation) m_ActorPlan.RestoreCompletedCheckpoint(m_RecoveredPlanCheckpoint);
        m_ActorPlanWatchedRevision = -1;
        m_ActorPlanTurn = turn;
        m_ActorPlanInputSequence = m_ActorInputSequence;
        m_ActorPlanRevocation = m_RoomActorProposalRevocation;
        UpdateActorPlanProposal();
        m_ResponseTrace?.Mark("actor_plan.registered", new JObject {
            ["id"] = m_ActorPlan.Id, ["draftId"] = draft.Id,
            ["generation"] = turn.Generation, ["coverage"] = decision.Coverage,
            ["goal"] = decision.Goal, ["fullDraftRetained"] = true });
    }

    private void UpdateActorPlanProposal()
    {
        if (m_ActorPlanTurn?.ActorProposal != null && m_ActorPlan != null)
            m_ActorPlanTurn.ActorProposal["actorPlan"] = m_ActorPlan.Facts();
    }

    // Read-only cross-turn reference. Cancellation retains history, never a
    // reusable execution lease; a new role choice still needs its own owner.
    private JObject PreviousActorPlanFacts()
    {
        if (m_ActorPlan == null) return null;
        return new JObject {
            ["id"] = m_ActorPlan.Id, ["revision"] = m_ActorPlan.Revision,
            ["sourceOrigin"] = m_ActorPlan.SourceOrigin, ["state"] = m_ActorPlan.State,
            ["originalGoal"] = m_ActorPlan.OriginalGoal, ["compiledGoal"] = m_ActorPlan.CompiledGoal,
            ["goalCoverage"] = m_ActorPlan.GoalCoverage, ["remainingGoal"] = m_ActorPlan.RemainingGoal,
            ["targetEntityId"] = m_ActorPlan.EntityId, ["completionCondition"] = m_ActorPlan.DeviceCompletionFacts(),
            ["results"] = m_ActorPlan.Results.DeepClone(), ["reason"] = m_ActorPlan.Reason,
            ["sourceTurn"] = new JObject { ["generation"] = m_ActorPlanTurn?.Generation,
                ["epoch"] = m_ActorPlanTurn?.Epoch, ["inputSequence"] = m_ActorPlanInputSequence },
            ["scope"] = "Historical plan reference only, including cancellations and verified past results; not current intent, current visibility, execution authority or permission to resume. compiledGoal preserves the earlier classifier's full interpretation, not an additional role promise. A current role adoption must bind its own full goal and owner."
        };
    }

    private bool AcceptActorPlanStep(RoomTaskProtocol.Decision decision, int generation, out string reason)
    {
        reason = null;
        if (decision?.planId == null) return true;
        var plan = m_ActorPlan; var turn = m_ActorPlanTurn;
        if (!ActorPlanCurrent(plan, turn) || turn.Generation != generation || decision.planId != plan.Id)
        { reason = "plan-owner-no-longer-current"; return false; }
        if (decision.planRevision != plan.Revision)
        { reason = "plan-revision-no-longer-current"; return false; }
        if (decision.intent == "none") return true;
        if (!plan.PreservesPrerequisiteGoal(decision.intent, decision.entityTarget?.entityId, decision.planRemaining))
        { reason = "invalid-prerequisite-goal-remainder"; return false; }
        if (decision.intent == "turn-in-place" &&
            (decision.viewChange == null || !plan.MatchesBodyTurnRequest(decision.viewChange.yaw)))
        { reason = "adopted-body-turn-amplitude-changed"; return false; }
        if (decision.planCompletion != null &&
            (!KnownSceneObjectBinding(decision.planCompletion.entityId, out _) ||
             !plan.AdoptDeviceCompletion(decision.planRevision, decision.planCompletion.entityId,
                decision.planCompletion.state, decision.planCompletion.goalText, decision.origin)))
        { reason = "device-completion-not-initial-adopted-goal"; return false; }
        var completion = plan.DeviceCompletionFacts();
        if (completion != null && decision.intent == "set-object-state" &&
            (decision.objectState != (string)completion["state"] || decision.entityTarget?.entityId != (string)completion["entityId"]))
        { reason = "adopted-device-goal-changed"; return false; }
        string fingerprint = decision.intent + ":" + (decision.entityTarget?.entityId ?? "") + ":" +
            (decision.objectState ?? "") + ":" + (decision.viewChange == null ? "" :
                decision.viewChange.yaw.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + decision.viewChange.pitch);
        // Unresolved pixels have no stable identity yet; after success the
        // selected entity is bound and subsequent object steps must use it.
        return plan.Propose(decision.planRevision, decision.intent, decision.planRemaining, fingerprint, out reason);
    }

    private bool ConsumeActorPlanStep(RoomTaskProtocol.Decision decision, bool valid, int generation)
    {
        var plan = m_ActorPlan; var turn = m_ActorPlanTurn;
        if (!ActorPlanCurrent(plan, turn) || generation != turn.Generation) return false;
        if (!valid)
        {
            CancelActorPlan("invalid-room-plan-step");
            PublishActorPlanClosure(plan, turn, false, "invalid-room-plan-step");
            return true;
        }
        if (decision.intent == "none")
        {
            if (plan.CloseWithoutNextStep(decision.planRevision, decision.planRemaining))
            {
                UpdateActorPlanProposal();
                PublishActorPlanClosure(plan, turn, true, "no-further-adopted-step");
                return true;
            }
            // Spatial planning has no executable next step. Preserve the full
            // adopted goal for the existing source/range/skill decision stage.
            CancelActorPlan("remaining-goal-needs-nonspatial-planner");
            UpdateActorPlanProposal();
            return false;
        }
        if (m_ActorPlanWatchedRevision == decision.planRevision) return true;
        if (plan.State != "executing" || !plan.BindAction(decision.planRevision, m_RoomTaskActionId))
        {
            string rejection = string.IsNullOrWhiteSpace(m_RoomTaskDispatch) ? "plan-step-not-submitted" : m_RoomTaskDispatch;
            if (plan.RejectUnsubmitted(decision.planRevision, decision.intent, rejection))
            {
                RememberPlanActionOutcome(plan, turn, null, decision.intent, "not-submitted", rejection, false);
                UpdateActorPlanProposal();
                m_ResponseTrace?.Mark("actor_plan.step_not_submitted", new JObject { ["id"] = plan.Id,
                    ["step"] = decision.planRevision, ["intent"] = decision.intent, ["reason"] = rejection,
                    ["actionId"] = null, ["attemptId"] = plan.Results.Last?["attemptId"], ["submitted"] = false });
            }
            CancelActorPlan("plan-step-not-submitted");
            PublishActorPlanClosure(plan, turn, false, "plan-step-not-submitted");
            return true;
        }
        var retainedBridge = GetComponent<ArdyRoomDialogueBridge>();
        if (decision.intent == "follow" && decision.planContinuation != "parallel" && m_RoomTaskDispatch == "follow-already-active" &&
            retainedBridge?.IsConnected == true && retainedBridge.IsFollowing &&
            retainedBridge.ActiveTaskId == m_RoomTaskActionId)
        {
            plan.MarkOngoing("same-follow-owner-retained; no new motion or finite completion claimed");
            UpdateActorPlanProposal();
            string language = ActorTaskLanguageFor(turn.Generation);
            string text = CurrentActorTaskReceipt(language);
            if (decision.planContinuation == "after_completion") text += ActorFollowDependencyReceipt(language);
            PublishActorTaskReceipt(turn, text, language, "persistent-follow-retained", true);
            return true;
        }
        m_ActorPlanWatchedRevision = decision.planRevision;
        m_FormalResponseInFlight = true;
        TryPublishActorPlanAcceptance(plan, turn, decision, m_RoomTaskActionId);
        if (!ActorPlanCurrent(plan, turn)) return true;
        StartCoroutine(AwaitActorPlanStep(plan, turn, decision, m_RoomTaskActionId));
        return true;
    }

    private IEnumerator AwaitActorPlanStep(ActorPlanRuntime plan, FastLoopTurnContext turn,
        RoomTaskProtocol.Decision decision, string actionId)
    {
        int revision = decision.planRevision;
        float deadline = Time.realtimeSinceStartup + 75f;
        string phase = null, reason = null, entity = null;
        bool success = false, bindEntity = false;
        float followStableSince = -1f;
        var followOwner = decision.intent == "follow" ? CaptureRoomFollowIdentity(GetComponent<ArdyRoomDialogueBridge>()) : null;
        while (ActorPlanCurrent(plan, turn) && Time.realtimeSinceStartup < deadline)
        {
            TryPublishActorPlanAcceptance(plan, turn, decision, actionId);
            if (!ActorPlanCurrent(plan, turn)) yield break;
            if (decision.intent == "inspect" || decision.intent == "stop-looking")
            {
                if (m_RoomTaskActionId != actionId) { reason = "plan-read-or-release-replaced"; break; }
                if (decision.intent == "inspect")
                { success = m_RoomTaskDispatch == "read-only-checked"; phase = success ? "observed" : "failed"; reason = m_RoomTaskDispatch; break; }
                if (m_PersistentSceneAttention?.active != true && m_SceneAttention?.IsReturning != true)
                { success = m_RoomTaskDispatch == "gaze-stop-submitted"; phase = success ? "stopped" : "failed"; reason = m_RoomTaskDispatch; break; }
            }
            else if (decision.intent == "explore")
            {
                if (m_SceneExploration.active && m_SceneExploration.goalId == actionId && m_RoomTaskDispatch == "exploration-active")
                {
                    // Registration is real, but an open-ended exploration has
                    // no terminal result that authorizes its dependent steps.
                    plan.MarkOngoing("exploration-registered; remaining dependent goal awaits a finite completion condition");
                    UpdateActorPlanProposal();
                    string language = ActorTaskLanguageFor(turn.Generation);
                    string text = language == "zh" ? "我会在这里走走看看，后面的步骤还没有执行。" :
                        language == "en" ? "I'll explore here; the later steps have not run yet." : "ここを歩いて見てみるね。続きの手順はまだ実行していないわ。";
                    PublishActorTaskReceipt(turn, text, language, "persistent-exploration-accepted", false);
                    yield break;
                }
                reason = "plan-exploration-not-active"; break;
            }
            else if (IsSceneVisualIntent(decision.intent))
            {
                var fact = m_SceneActionFact;
                if (fact?.actionId != actionId) { reason = "plan-action-replaced"; break; }
                if (!m_SceneActionActive)
                {
                    phase = fact.phase; reason = fact.reason; entity = fact.entityId;
                    bindEntity = !decision.IsObservationApproach && fact.targetPurpose != "observe-closer" && !fact.needsCloserObservation;
                    success = phase == "arrived" || phase == "observed" || phase == "performed" ||
                        phase == "already-satisfied" || phase == "turned";
                    break;
                }
            }
            else
            {
                var bridge = GetComponent<ArdyRoomDialogueBridge>();
                if (bridge == null || bridge.ActionId != actionId) { reason = "plan-controller-replaced"; break; }
                phase = bridge.Phase;
                if (decision.intent == "follow")
                {
                    if (!RoomFollowIdentityCurrent(followOwner)) { reason = "plan-follow-binding-changed"; break; }
                    if (bridge.IsFollowing && bridge.ActiveTaskId == actionId &&
                        (phase == "moving" || phase == "follow-waiting"))
                    {
                        if (followStableSince < 0) followStableSince = Time.realtimeSinceStartup;
                        if (m_RoomTaskDispatch == "follow-already-active" || Time.realtimeSinceStartup - followStableSince >= 1f)
                        { success = true; reason = "follow-running-confirmed; continuous task is not completed"; break; }
                    }
                    else followStableSince = -1f;
                }
                else if (phase == "arrived" || decision.intent == "stop-moving" && phase == "stopped")
                { success = true; reason = "actual-controller-result"; break; }
                if (HasDefinitiveRoomActionFailure())
                { reason = string.IsNullOrEmpty(bridge.Status) ? m_RoomTaskDispatch : bridge.Status; break; }
            }
            yield return null;
        }
        if (!ActorPlanCurrent(plan, turn)) { plan.Cancel("plan-owner-invalidated"); yield break; }
        if (reason == null) reason = "plan-step-result-timeout";
        string verifiedDeviceState = VerifiedActorPlanDeviceState(decision, actionId, entity, phase);
        double? verifiedBodyYaw = VerifiedActorPlanBodyYaw(decision, actionId, phase);
        if (!plan.Resolve(revision, actionId, success, phase ?? "failed", reason, entity,
            bindEntity && !string.IsNullOrEmpty(entity), verifiedDeviceState, verifiedBodyYaw,
            verifiedBodyYaw.HasValue ? (double?)decision.viewChange.yaw : null)) yield break;
        success = (bool?)plan.Results.Last?["succeeded"] == true;
        RememberPlanActionOutcome(plan, turn, actionId, decision.intent, phase, reason, success);
        if (success && decision.intent == "follow" && decision.planContinuation != "parallel")
            plan.MarkOngoing("continuous-follow-retained; no finite task completion claimed");
        if (success && plan.Results.Last is JObject completedResult)
        {
            completedResult["requestedState"] = decision.objectState;
            completedResult["outcome"] = decision.intent == "follow" ? "ongoing" : "completed";
            if (decision.intent == "follow") completedResult["continuation"] = decision.planContinuation ?? "unspecified";
        }
        UpdateActorPlanProposal();
        m_ResponseTrace?.Mark("actor_plan.step_result", new JObject {
            ["id"] = plan.Id, ["step"] = revision, ["intent"] = decision.intent,
            ["actionId"] = actionId, ["phase"] = phase, ["success"] = success,
            ["remainingGoal"] = plan.RemainingGoal, ["boundEntityId"] = plan.EntityId,
            ["completionCondition"] = plan.DeviceCompletionFacts(), ["verifiedDeviceState"] = verifiedDeviceState,
            ["bodyTurnCompletion"] = plan.BodyTurnCompletionFacts(), ["verifiedBodyYaw"] = verifiedBodyYaw,
            ["closureReason"] = plan.Reason });
        if (!ActorPlanOwnerCurrent(plan, turn)) yield break;
        if (!success || plan.State == "completed" || plan.State == "ongoing")
        {
            RevokeActorBodyHandoff(plan, plan.State == "completed" ? "finite-plan-completed" : "plan-not-completed");
            if (plan.State != "ongoing" && !ClaimActorPlanClosure(plan)) yield break;
            string language = ActorTaskLanguageFor(turn.Generation);
            string receipt = decision.intent == "follow" || decision.intent == "approach"
                ? CurrentActorTaskReceipt(language) : CurrentRoomActionReceipt(language);
            if (!success) receipt = ActorPlanClosureText(plan, language, false);
            else if (string.IsNullOrEmpty(receipt)) receipt = ActorPlanClosureText(plan, language, true);
            if (success && decision.intent == "follow" && decision.planContinuation == "after_completion")
                receipt += ActorFollowDependencyReceipt(language);
            PublishActorTaskReceipt(turn, receipt, language, "persistent-plan-result", false);
            yield break;
        }
        bool received = false;
        if (SceneEyesOpen)
        {
            RequestFreshSceneObservation("actor-plan-next-step", observation => {
                if (!ActorPlanCurrent(plan, turn)) return;
                BindFormalSceneObservation(IsFreshSceneObservation(observation) ? observation : null, turn.Generation);
                received = true;
            });
            deadline = Time.realtimeSinceStartup + 5f;
            while (ActorPlanCurrent(plan, turn) && !received && Time.realtimeSinceStartup < deadline) yield return null;
        }
        if (!ActorPlanCurrent(plan, turn)) { plan.Cancel("plan-owner-invalidated"); yield break; }
        if (SceneEyesOpen && !received)
        {
            CancelActorPlan("plan-next-observation-timeout");
            PublishActorPlanClosure(plan, turn, false, "plan-next-observation-timeout");
            yield break;
        }
        // Same turn, same provider lease, a new step revision. No new user
        // history item, no general dispatch, and no replay of the prior step.
        if (!TryPlanRoomTask(turn.Prompt, ResolveFormalSceneImage(turn.Generation), turn.Generation,
            turn.RecordHistory, (turn.TransientContext ?? "") + "\n" + plan.Facts().ToString(Formatting.None)))
        {
            CancelActorPlan("plan-next-planner-unavailable");
            PublishActorPlanClosure(plan, turn, false, "plan-next-planner-unavailable");
        }
    }

    private string VerifiedActorPlanDeviceState(RoomTaskProtocol.Decision decision, string actionId, string entityId, string phase)
    {
        if (decision.intent != "set-object-state" || (phase != "performed" && phase != "already-satisfied") ||
            string.IsNullOrEmpty(entityId) || !SceneResultBindingValid() || m_SceneResultDevice == null) return null;
        var result = m_SceneObjectStateResult;
        if ((string)result?["actionId"] != actionId || (string)result?["entityId"] != entityId ||
            (bool?)result?["completed"] != true || (bool?)result?["freshVisualObservation"] != true ||
            (string)result?["actual"]?["entityId"] != entityId || result?["actual"]?["on"]?.Type != JTokenType.Boolean ||
            m_SceneResultDevice.EntityId != entityId) return null;
        bool actual = (bool)result["actual"]["on"];
        if (!JToken.DeepEquals(result["actual"], JObject.Parse(JsonUtility.ToJson(m_SceneResultDevice.ReadState()))) ||
            actual != (decision.objectState == "on")) return null;
        return actual ? "on" : "off";
    }
}
