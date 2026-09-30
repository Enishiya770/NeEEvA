using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using NeEEvA.Perception;

public partial class ChatSample
{
    // Spatial intent is committed once before its spoken description. The role's
    // ordinary prose/tool stream never obtains a second room-action authority.
    private int m_RoomTaskGeneration = -1, m_RoomSpeechGuardGeneration = -1;
    private string m_RoomTaskIntent = "none", m_RoomTaskDispatch = "not-submitted", m_RoomTaskActionId = "";
    private bool m_RoomTaskFormatError;
    private int m_RoomTaskTargetRevision;
    private int m_RoomSpeechReviewOwnerRevision;
    private LLM m_RoomSpeechReviewModel;

    private void CancelRoomSpeechReview(string reason)
    {
        ++m_RoomSpeechReviewOwnerRevision;
        int owner = m_RoomSpeechGuardGeneration;
        var ownerModel = m_RoomSpeechReviewModel;
        m_RoomSpeechReviewModel = null;
        m_RoomSpeechGuardGeneration = -1;
        if (m_PendingSceneFormalCaptureGeneration == owner) m_PendingSceneFormalCaptureGeneration = -1;
        // A reset may already have advanced the formal generation. Clear the old
        // guard, but never cancel a replacement model request or formal owner.
        if (owner < 0 || owner != m_FormalResponseGeneration) return;
        ++m_FormalResponseGeneration;
        m_FormalResponseInFlight = false; m_StreamComplete = true; m_TTSSenderDone = true;
        m_AgentRoundInFlight = false;
        ClearStagedFormalObservation();
        if (ownerModel != null) ownerModel.CancelActiveResponse();
        Debug.Log($"[Room/SpeechReview] generation={owner} state=cancelled reason={reason}", this);
    }

    private bool HasRoomTaskPlanner => m_UseStreaming && m_IsVoiceMode && m_ChatSettings?.m_TextToSpeech != null &&
        RoomMotionOutputEnabled && m_ChatSettings?.m_ChatModel != null &&
        m_ChatSettings.m_ChatModel.SupportsRoomTaskMessages && GetComponent<ArdyRoomDialogueBridge>()?.IsConnected == true;

    private static JObject PickRoomFields(JObject source, params string[] fields)
    {
        var result = new JObject();
        foreach (string field in fields)
            if (source?[field] != null) result[field] = source[field].DeepClone();
        return result;
    }

    private JObject ReadCompactRoomTaskFacts(ArdyRoomDialogueBridge bridge)
    {
        if (bridge == null) return new JObject { ["connected"] = false };
        // Keep Unity's field serialization for vectors. Do not walk Vector3.normalized
        // with Newtonsoft, or repeat the legacy diagnostic/assessment tree in prompts.
        var full = JObject.Parse(bridge.DescribeRoomContext());
        var current = full["currentFacts"] as JObject;
        var facts = PickRoomFields(current, "currentTargetRevision", "lastAttemptTargetRevision",
            "lastAttemptAppliesToCurrentTarget", "attemptStatus", "connected", "userAnchorValid", "userTrackingValid", "userSupportVerified", "executionTargetKind", "scope",
            "bodyReserved", "activeActionId", "observedAtRealtime", "currentlyNearAndFacingUser",
            "actualUserDistance", "actualFacingErrorDegrees", "desiredUserDistance", "actualRoot", "userHeadWorld",
            "taskMode", "followActive", "activeTaskId", "controllerOperationRevision", "followLegCount",
            "followTargetUpdatedAtRealtime", "followNextPlanAtRealtime", "followResumeDistance",
            "segmentIndex", "segmentCount", "preparedSegmentCount", "plannedDurationSeconds", "playedDurationSeconds", "waitingForSegment",
            "poseReturning", "movingTargetRevision", "committedTargetRevision", "movingTargetNeedsRestart", "movingTargetStatus");
        var assessment = current?["assessment"] as JObject;
        var compactAssessment = PickRoomFields(assessment, "state", "canPlanRoute", "budgetChecked",
            "executionGuaranteed", "routeLength", "reason", "observedAtRealtime", "expiresAtRealtime", "scope");
        compactAssessment["targetKind"] = "user";
        var observation = assessment?["observation"] as JObject;
        compactAssessment["observation"] = PickRoomFields(observation, "stage", "code", "summary",
            "candidateCount", "acceptedCount", "startRejected", "targetRejected", "pathRejected",
            "userClearanceRejected", "otherRejected");
        facts["assessment"] = compactAssessment;
        var execution = PickRoomFields(full["latestExecutionObservation"] as JObject,
            "actionId", "targetKind", "captureId", "sceneTargetId", "phase", "stage", "reason", "executionReason", "operationRevision", "observedUtc");
        execution["matchesRequestedAction"] = !string.IsNullOrEmpty(m_RoomTaskActionId) &&
            (string)execution["actionId"] == m_RoomTaskActionId;
        return new JObject {
            ["controlEntryPoints"] = new JObject {
                ["spokenTaskPlannerAvailable"] = HasRoomTaskPlanner,
                ["sceneActionsAvailable"] = m_EnableSceneActions && m_EnableSceneObjectAwareness,
                ["sameRoomControllerAsEditorButtons"] = bridge.IsConnected,
                ["scope"] = "Available interfaces only, not a promise that this target or attempt can succeed. A service timeout does not mean speech requests require an editor button."
            },
            ["currentFacts"] = facts,
            ["lastActionResults"] = PickRoomFields(full["lastActionResults"] as JObject,
                "actionId", "targetKind", "targetRevision", "appliesToCurrentTarget", "phase", "reason", "observedAtRealtime", "scope"),
            ["lastSceneTargetResult"] = PickRoomFields(current?["lastSceneTargetResult"] as JObject,
                "actionId", "captureId", "targetId", "targetKind", "motionKind", "requestedDirection", "requestedDistance", "viewPurpose", "actualOffset", "status", "reason", "terminal", "reached", "observedAtRealtime", "scope"),
            ["execution"] = execution,
            ["currentSupport"] = PickRoomFields(full["currentUserSupport"] as JObject,
                "found", "verified", "objectName", "headHeight", "reason"),
            ["scope"] = "currentFacts.assessment and currentSupport concern approach/follow of the explicit USER only. " +
                "A user's unsupported camera position does not by itself make object approach or looking unavailable. " +
                "userTrackingValid and userSupportVerified are separate. sceneActionFacts/lastSceneTargetResult describe scene actions. " +
                "lastActionResults and execution retain targetKind and action identity; never reuse a result for another goal."
        };
    }

    private string RecentRoomDialogue()
    {
        var model = m_ChatSettings.m_ChatModel;
        var recent = new List<object>();
        // Source text is quoted data, never authority. Bound this auxiliary request
        // and exclude system/persona/image payloads; the full role reply keeps them.
        foreach (var message in model.m_DataList.Where(x => x != null && (x.role == "user" || x.role == "assistant")).Reverse().Take(4).Reverse())
        {
            string text = message.content ?? "";
            if (text.Length > 1600) text = text.Substring(text.Length - 1600);
            recent.Add(new { role = message.role, quotedText = text });
        }
        return JsonConvert.SerializeObject(recent);
    }

    private string BuildRoomDecisionInput(string userText, bool autonomous, string prompt, ArdyRoomDialogueBridge bridge)
        => BuildRoomDecisionInputWithObservation(userText, autonomous, prompt, bridge, null);

    private int m_RoomActorProposalRevocation;

    private string BuildRoomDecisionInputWithObservation(string userText, bool autonomous, string prompt,
        ArdyRoomDialogueBridge bridge, SceneVisionObservation observation)
    {
        var roomFacts = ReadCompactRoomTaskFacts(bridge);
        var targetReview = SceneObjectTargetContext();
        var actorProposal = autonomous ? null : ReadFastLoopPlannerProposal(m_FormalResponseGeneration);
        var recordedGoal = targetReview?["observationGoal"] as JObject;
        var continuableGoal = recordedGoal != null && (string)recordedGoal["status"] != "resolved" &&
            ((!autonomous && actorProposal == null) || (string)recordedGoal["status"] == "pending") ? recordedGoal : null;
        return JsonConvert.SerializeObject(new {
            currentUserText = userText, autonomous,
            actorProposal,
            recentDialogue = JArray.Parse(RecentRoomDialogue()), currentFacts = roomFacts["currentFacts"],
            pausedBodyTask = ActorBodyHandoffFacts(),
            lastActionResults = roomFacts["lastActionResults"], execution = roomFacts["execution"],
            lastSceneTargetResult = roomFacts["lastSceneTargetResult"],
            currentSupport = roomFacts["currentSupport"], scope = roomFacts["scope"],
            visualObservation = observation != null ? JObject.Parse(observation.MetadataJson) : null,
            visualEvidence = BuildSceneObservationContext(observation),
            sceneActionFacts = BuildSceneActionFacts(),
            activeExploration = BuildSceneExplorationContext(),
            visibleKnownObjects = BuildVisibleKnownObjects(observation),
            objectPrerequisites = BuildRoomObjectPrerequisites(observation),
            objectTargetReview = targetReview,
            pendingObservationGoal = continuableGoal,
            decisionContractFacts = new {
                autonomousExplorationStep = m_ExplorationStepGeneration == m_FormalResponseGeneration &&
                    m_ExplorationStepRevision == m_SceneExploration.revision,
                autonomousTranslationSuppressed = m_ExplorationAutonomousSuppressed,
                autonomousTranslationRule = "When suppressed, no self-initiated (autonomous or actor_draft) explore, move-to-ground, approach-object, reposition-view, approach or follow. Looking/inspection and a new explicit user movement request remain available; explicit user explore can begin a new continuing activity.",
                currentUserTurn = m_ActionConstraints.CurrentUserTurn,
                source_ref = RoomTaskProtocol.PlanSourceReference(RoomTaskProtocol.ReadActorProposal(actorProposal)),
                evidenceSource = "actorProposal.source=current-user-goal retains the current full USER request and permits user/none only, with currentUserText evidence and no actorDraftId. source=unpublished-current-role-draft permits actor_draft/none only, with exact text and draftId/draftRevision; only purpose=plan permits planning. verify/missing purpose cannot execute. Both sources retain planId/revision and every remaining step. recentDialogue never supplies new authority. Current user restrictions and physical validation still apply.",
                allowedObservationGoalIds = continuableGoal == null ? new string[0] : new[] { (string)continuableGoal["goalId"] },
                goalReferenceRule = "No listed goal means omit observationGoalId. An already identified object can be approached by entityTarget without an observation goal. Entity IDs, action IDs and capture IDs never substitute for goal IDs.",
                headYawLimit = 50, headPitchLimit = 25, bodyYawLimit = 180,
                turnRule = "Turn around uses turn-in-place, never a 180 degree neck turn. Return a new complete decision; the runtime does not clamp or rewrite an invalid action."
            },
            sceneTime = JObject.Parse(SceneTimeContextJson),
            autonomousIntention = autonomous ? m_SceneAutonomyIntention : "",
            autonomousContext = autonomous ? (prompt ?? "").Substring(0, Math.Min(4000, (prompt ?? "").Length)) : ""
        });
    }

    private bool TryPlanRoomTask(string prompt, string imageUrl, int generation, bool recordHistory, string context)
    {
        if (!HasRoomTaskPlanner) return false;
        var planningTrace = m_ResponseTrace?.Child("room-planning");
        planningTrace?.Mark("room.plan_begin", new JObject { ["generation"] = generation });
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var model = m_ChatSettings.m_ChatModel;
        var evidence = bridge.ReadCurrentTargetEvidence(true);
        var retainedFollow = CaptureRoomFollowIdentity(bridge);
        int workEpoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn;
        bool autonomous = m_AgentCurrentRoundIsTick;
        var actorSource = autonomous ? null : RoomTaskProtocol.ReadActorProposal(ReadFastLoopPlannerProposal(generation));
        var actorTurn = m_FastLoopTurn;
        int actorSpeechRound = m_CurrentSpeechRoundId;
        int actorRevocation = m_RoomActorProposalRevocation;
        bool ActorProposalCurrent() => actorSource == null || (actorRevocation == m_RoomActorProposalRevocation &&
            RoomTaskProtocol.SameActorProposal(actorSource, RoomTaskProtocol.ReadActorProposal(ReadFastLoopPlannerProposal(generation))));
        string userText = autonomous ? "" : m_ActionConstraints.CurrentUserText;
        m_FormalResponseInFlight = true;
        m_RoomTaskGeneration = generation;
        m_RoomTaskIntent = "none"; m_RoomTaskDispatch = "not-submitted"; m_RoomTaskActionId = "";
        m_RoomTaskTargetRevision = evidence.currentTargetRevision;
        bool consumed = false;
        bool bodyHandoffPrepared = false;
        int visualRefreshes = 0, targetCorrections = 0, decisionRevision = 0, decisionRepairs = 0;
        JObject visualCorrection = null, decisionRepair = null;
        SceneVisionObservation observation = GetFormalSceneObservation(generation);
        string input = BuildRoomDecisionInputWithObservation(userText, autonomous, prompt, bridge, observation);
        void EndRevokedActorProposal()
        {
            // Scene output may be disabled without advancing the formal turn.
            // Drop this pending planner as well as its action authority, but never
            // let an old callback end a replacement model/turn/room request.
            if (actorSource == null || generation != m_FormalResponseGeneration || workEpoch != m_WorkEpoch ||
                userTurn != m_ActionConstraints.CurrentUserTurn || actorSpeechRound != m_CurrentSpeechRoundId ||
                !ReferenceEquals(actorTurn, m_FastLoopTurn) || !ReferenceEquals(model, m_ChatSettings?.m_ChatModel) ||
                m_RoomTaskGeneration != generation || !m_FormalResponseInFlight) return;
            consumed = true; ++decisionRevision;
            m_RoomTaskIntent = "unresolved"; m_RoomTaskDispatch = "not-submitted: actor-proposal-revoked";
            m_RoomTaskGeneration = -1;
            ++m_FormalResponseGeneration;
            m_FormalTransportDispatchGeneration = -1;
            m_FirstReplyFailureDrainGeneration = -1;
            m_FormalResponseInFlight = false; m_StreamComplete = true; m_TTSSenderDone = true;
            m_AgentRoundInFlight = false;
            if (m_PendingSceneFormalCaptureGeneration == generation) m_PendingSceneFormalCaptureGeneration = -1;
            ClearStagedFormalObservation();
            planningTrace?.Mark("room.plan_cancelled", new JObject { ["generation"] = generation,
                ["reason"] = "actor-proposal-revoked", ["actionSubmitted"] = false });
            // Revoke all callback fences before entering provider code. Keep the
            // accepted user history and any independent held task unchanged.
            model.CancelActiveResponse();
        }
        void OnDecision(bool ok, string json, string status, string sourceText, RoomSpeechPreparation prepared)
        {
            if (consumed || generation != m_FormalResponseGeneration || workEpoch != m_WorkEpoch ||
                userTurn != m_ActionConstraints.CurrentUserTurn || m_UserSpeechActiveForAutonomy || !isActiveAndEnabled ||
                (m_ExplorationStepGeneration == generation && SceneExplorationInputPending)) return;
            if (!ActorProposalCurrent()) { EndRevokedActorProposal(); return; }
            // A closed-recording prefetch may have completed before the accepted
            // user turn advanced the controller's target revision. Re-read the
            // current room evidence before any non-visual approach/follow submit;
            // the prefetch decision is reusable, but its old revision is not an
            // execution permit.
            if (prepared != null)
            {
                evidence = bridge.ReadCurrentTargetEvidence(true);
                m_RoomTaskTargetRevision = evidence.currentTargetRevision;
            }
            RoomTaskProtocol.Decision decision = null;
            string validationFailure = ok ? null : "transport:" + (status ?? "unknown");
            bool valid = ok && RoomTaskProtocol.TryDecision(json, sourceText, autonomous, actorSource, out decision, out validationFailure,
                m_ExplorationAutonomousSuppressed, retainedFollow?.TaskId);
            if (valid && m_ExplorationStepGeneration == generation &&
                !RoomTaskProtocol.IsExplorationStepIntent(decision.intent))
            { valid = false; validationFailure = "invalid-exploration-step"; }
            if (valid && decision.IsSelfInitiated && m_ExplorationAutonomousSuppressed &&
                (decision.intent == "explore" || decision.intent == "move-to-ground" || decision.intent == "approach-object" ||
                 decision.intent == "reposition-view" || decision.intent == "approach" || decision.intent == "follow"))
            { valid = false; validationFailure = "autonomous-movement-stopped"; }
            bool sceneStopCommitted = TryGetSceneVisionPreparedStop(generation, out string sceneStopId, out string sceneStopDispatch);
            CancelSceneVisionStopPreparation();
            if (sceneStopCommitted)
            {
                valid = true;
                decision = new RoomTaskProtocol.Decision { intent = "stop-moving" };
            }
            // A continuation ID is not an entity ID. Validate this non-action reference
            // before any candidate look, movement, device change or scene-action receipt.
            if (valid && decision.ContinuesObservationGoal &&
                !TryContinueSceneObservationGoal(decision, out _, out string referenceFailure))
            { valid = false; validationFailure = referenceFailure; }
            // A known ID remains a reference while the request is in flight,
            // not permission to act on an expired picture. Refresh its current
            // facts before deciding whether observation or approach is needed.
            if (valid && decision.entityTarget != null && decision.planId != null &&
                KnownSceneObjectBinding(decision.entityTarget.entityId, out _) &&
                decision.entityTarget.captureId == observation?.CaptureId && !IsFreshSceneObservation(observation))
            {
                if (visualRefreshes++ == 0 && SceneEyesOpen)
                {
                    consumed = true; int refreshRevision = ++decisionRevision;
                    m_PendingSceneFormalCaptureGeneration = generation;
                    RequestFreshSceneObservation("room-prerequisite-refresh", refreshed => {
                        if (refreshRevision != decisionRevision || generation != m_FormalResponseGeneration ||
                            workEpoch != m_WorkEpoch || userTurn != m_ActionConstraints.CurrentUserTurn ||
                            !ActorProposalCurrent() || m_UserSpeechActiveForAutonomy || !isActiveAndEnabled) return;
                        m_PendingSceneFormalCaptureGeneration = -1;
                        observation = refreshed; BindFormalSceneObservation(IsFreshSceneObservation(refreshed) ? refreshed : null, generation);
                        consumed = false;
                        if (!IsFreshSceneObservation(refreshed)) { OnDecision(false, "", "prerequisite-refresh-unavailable", sourceText, null); return; }
                        var refreshedProposal = JObject.Parse(json);
                        refreshedProposal["entityTarget"]["captureId"] = refreshed.CaptureId;
                        // Only capture binding changes; entity, intent, goal and
                        // source stay fixed. All current visibility/device gates remain.
                        OnDecision(true, refreshedProposal.ToString(Formatting.None), status, sourceText, null);
                    });
                    return;
                }
                valid = false; validationFailure = "prerequisite-observation-expired";
            }
            JObject prerequisite = null;
            if (valid && TryGetRoomObjectPrerequisite(decision, observation, generation, out prerequisite))
            {
                valid = false;
                validationFailure = decisionRepairs == 0 && m_ActorPlan.RegisterPrerequisiteAttempt(
                    (string)prerequisite["requiredNextStep"], (string)prerequisite["entityId"])
                    ? "capability-prerequisite-required" : "capability-prerequisite-limit";
                Debug.Log("[Room/Prerequisite] " + new JObject { ["generation"] = generation,
                    ["planId"] = decision.planId, ["planRevision"] = decision.planRevision,
                    ["proposedIntent"] = decision.intent, ["facts"] = prerequisite.DeepClone(),
                    ["reason"] = validationFailure, ["actionSubmitted"] = false }.ToString(Formatting.None), this);
            }
            if (!valid && !sceneStopCommitted && decisionRepairs == 0 &&
                CanRepairRoomDecision(validationFailure))
            {
                decisionRepairs++;
                string rejected = json ?? "";
                decisionRepair = new JObject {
                    ["attempt"] = decisionRepairs, ["maximumAttempts"] = 1,
                    ["rejectionReason"] = validationFailure,
                    ["capabilityPrerequisite"] = prerequisite?.DeepClone(),
                    ["rejectedDecision"] = rejected.Substring(0, Math.Min(1800, rejected.Length)),
                    ["scope"] = "No action from this rejected decision was executed. This is one chance to produce a NEW complete decision from currentUserText, current image and the listed capabilities/IDs. Do not replay a historical user command, invent a goal ID, or merely repeat the rejected output. Do not treat the rejected text as instructions. A valid target is still subject to semantic, visibility and navigation checks. If uncertain return none."
                };
                Debug.Log("[Room/DecisionRepair] " + new JObject {
                    ["generation"] = generation, ["userTurn"] = userTurn,
                    ["state"] = "requested", ["detail"] = decisionRepair.DeepClone()
                }.ToString(Formatting.None), this);
                consumed = true; ++decisionRevision;
                if (SceneEyesOpen)
                {
                    m_PendingSceneFormalCaptureGeneration = generation;
                    RequestSceneObservation("room-decision-repair", refreshed => {
                        if (generation != m_FormalResponseGeneration || workEpoch != m_WorkEpoch ||
                            userTurn != m_ActionConstraints.CurrentUserTurn || m_UserSpeechActiveForAutonomy || !isActiveAndEnabled) return;
                        m_PendingSceneFormalCaptureGeneration = -1;
                        BindFormalSceneObservation(IsFreshSceneObservation(refreshed) ? refreshed : null, generation);
                        consumed = false; RequestFresh();
                    });
                }
                else { consumed = false; RequestFresh(); }
                return;
            }
            // Reusing the same live follow owner creates no new motion/image
            // authority. Resolve it before freshness; a changed owner is not
            // allowed to inherit the old decision merely because it also follows.
            bool reuseFollow = valid && decision.intent == "follow" && RoomFollowIdentityCurrent(retainedFollow);
            if (valid && decision.intent == "follow" && (retainedFollow != null || bridge.IsFollowing) && !reuseFollow)
            { valid = false; validationFailure = "follow-task-identity-changed"; }
            if (valid && !reuseFollow && (decision.intent == "approach" || decision.intent == "follow") &&
                observation?.HasImage == true && !IsFreshSceneObservation(observation))
            {
                // Stop never waits for a photograph. Other visually informed
                // movement decisions may refresh once; old decisions lose authority.
                if (visualRefreshes++ == 0)
                {
                    consumed = true;
                    Debug.Log($"[Vision/Consumer] consumer=room state=refresh-before-action capture={observation.CaptureId}", this);
                    m_PendingSceneFormalCaptureGeneration = generation;
                    RequestSceneObservation("room-refresh", refreshed => {
                        if (generation != m_FormalResponseGeneration || workEpoch != m_WorkEpoch ||
                            userTurn != m_ActionConstraints.CurrentUserTurn || !isActiveAndEnabled) return;
                        m_PendingSceneFormalCaptureGeneration = -1;
                        observation = refreshed;
                        imageUrl = refreshed?.HasImage == true ? refreshed.ImageDataUrl : null;
                        BindFormalSceneObservation(refreshed, generation);
                        consumed = false;
                        RequestFresh();
                    });
                    return;
                }
                valid = false;
                context = (context ?? "") + "\n本轮视觉决定两次均未能在观察仍有效时提交；没有执行该移动，需重新观察。";
            }
            if (valid && decision.bodyHandoff != null && !bodyHandoffPrepared)
            {
                consumed = true;
                StartCoroutine(PrepareActorBodyHandoff(decision, retainedFollow, (ready, failure) => {
                    if (generation != m_FormalResponseGeneration || workEpoch != m_WorkEpoch ||
                        userTurn != m_ActionConstraints.CurrentUserTurn || !ActorProposalCurrent() ||
                        m_UserSpeechActiveForAutonomy || !isActiveAndEnabled) return;
                    consumed = false; bodyHandoffPrepared = ready;
                    if (!ready)
                    {
                        // Retain a rejected attempt without pretending a body action was accepted.
                        if (AcceptActorPlanStep(decision, generation, out _))
                        {
                            m_RoomTaskIntent = decision.intent;
                            m_RoomTaskDispatch = "not-submitted: " + failure;
                            ConsumeActorPlanStep(decision, true, generation);
                        }
                        return;
                    }
                    OnDecision(true, json, status, sourceText, null);
                }));
                return;
            }
            if (valid && decision.bodyHandoff != null && bodyHandoffPrepared &&
                (!ActorBodyHandoffCurrent(m_ActorBodyHandoff) || m_ActorBodyHandoff.Mode != decision.bodyHandoff.mode))
            { valid = false; validationFailure = "body-handoff-owner-changed"; }
            if (valid && !AcceptActorPlanStep(decision, generation, out string planFailure))
            { valid = false; validationFailure = planFailure; }
            consumed = true;
            void DispatchReply()
            {
                if (generation != m_FormalResponseGeneration || workEpoch != m_WorkEpoch ||
                    userTurn != m_ActionConstraints.CurrentUserTurn || !ActorProposalCurrent() || !isActiveAndEnabled) return;
                planningTrace?.Mark("room.decision_resolved", new JObject { ["generation"] = generation,
                    ["intent"] = m_RoomTaskIntent, ["dispatch"] = m_RoomTaskDispatch, ["hasActionId"] = !string.IsNullOrEmpty(m_RoomTaskActionId) });
                if (generation != m_FormalResponseGeneration || workEpoch != m_WorkEpoch || !ActorProposalCurrent()) return;
                RecordWorkRoomTaskContext();
                Debug.Log($"[Room/Task] generation={generation} userTurn={userTurn} targetRevision={evidence.currentTargetRevision} intent={m_RoomTaskIntent} dispatch={m_RoomTaskDispatch} action={m_RoomTaskActionId}", this);
                if (ConsumeActorPlanStep(decision, valid, generation)) return;
                if (CompleteSceneExplorationStep(generation)) return;
                if (valid && TryDeliverActorPlannedTaskReceipt(decision, generation)) return;
                DispatchRoomMotionReplyWhenReady(bridge, generation, workEpoch, userTurn, () => {
                    if (valid && TryDeliverRoomActionReceipt(decision, generation, recordHistory)) return;
                    DispatchFormalStream(prompt, ResolveFormalSceneImage(generation), generation, recordHistory,
                        (context ?? "") + "\n" + BuildRoomTaskReplyFacts() + RoomMotionReadinessReplyContext() +
                        (ReferenceEquals(m_ActorPlanTurn, m_FastLoopTurn) && m_ActorPlan != null
                            ? "\n[保留的完整角色目标与实际步骤事实；未完成部分仍未执行]\n" + m_ActorPlan.Facts().ToString(Formatting.None) : ""), true);
                });
            }
            if (valid && decision.IsVisual)
            {
                if (decision.origin == "user" && m_SceneExploration.active)
                    StopSceneExploration("new-user-spatial-task", true);
                m_RoomTaskIntent = decision.intent;
                BeginSceneAction(decision, observation, generation, () => {
                    var fact = m_SceneActionFact;
                    if (decision.visualTarget != null && targetCorrections == 0 && fact?.canReobserveTarget == true &&
                        fact.captureId == observation?.CaptureId && !m_SceneActionActive &&
                        !bridge.ReservesBody && !bridge.IsFollowing && SceneEyesOpen &&
                        generation == m_FormalResponseGeneration && workEpoch == m_WorkEpoch &&
                        userTurn == m_ActionConstraints.CurrentUserTurn && !m_UserSpeechActiveForAutonomy)
                    {
                        targetCorrections++;
                        ++decisionRevision; // The old model callback stays revoked while acquiring the new image.
                        visualCorrection = new JObject {
                            ["attempt"] = 1, ["maximumAttempts"] = 1,
                            ["previousDecision"] = JObject.FromObject(new { decision.intent, decision.origin, decision.evidence, decision.visualTarget }),
                            ["measuredFailure"] = fact.reason,
                            ["scope"] = "No head or root action was started. Re-observe and choose a clearly visible solid point consistent with the same purpose, or decline. This is not evidence that another point is valid. No automatic neighbor substitution."
                        };
                        m_PendingSceneFormalCaptureGeneration = generation;
                        Debug.Log($"[Vision/Consumer] consumer=room state=reobserve-rejected-pixel capture={observation?.CaptureId}", this);
                        RequestSceneObservation("scene-target-reselect", refreshed => {
                            if (generation != m_FormalResponseGeneration || workEpoch != m_WorkEpoch ||
                                userTurn != m_ActionConstraints.CurrentUserTurn || m_UserSpeechActiveForAutonomy || !isActiveAndEnabled) return;
                            m_PendingSceneFormalCaptureGeneration = -1;
                            if (refreshed?.HasImage != true || !IsFreshSceneObservation(refreshed) || !SceneEyesOpen ||
                                bridge.ReservesBody || bridge.IsFollowing)
                            { m_RoomTaskDispatch = "not-submitted: target-reobservation-unavailable"; DispatchReply(); return; }
                            observation = refreshed;
                            BindFormalSceneObservation(refreshed, generation);
                            consumed = false;
                            RequestFresh();
                        });
                        return;
                    }
                    DispatchReply();
                });
                return;
            }
            if (!valid)
            {
                m_RoomTaskIntent = "unresolved";
                m_RoomTaskDispatch = "decision-unavailable";
                // A successful HTTP envelope is not a valid executable decision. Record the
                // exact rejecting gate and a bounded, escaped payload, without any image,
                // role prompt, auth header or full request context.
                string candidate = json ?? "";
                Debug.LogWarning("[Room/DecisionValidation] " + new JObject {
                    ["generation"] = generation, ["userTurn"] = userTurn,
                    ["wireAccepted"] = ok, ["wireStatus"] = status,
                    ["reason"] = validationFailure ?? "observation-expired-before-dispatch",
                    ["candidate"] = candidate.Substring(0, Math.Min(1800, candidate.Length)),
                    ["candidateTruncated"] = candidate.Length > 1800,
                    ["scope"] = "Rejected output only; nothing was executed from this decision. HTTP receipt and action validation are separate."
                }.ToString(Formatting.None), this);
            }
            else
            {
                m_RoomTaskIntent = decision.intent;
                if (decision.intent == "explore")
                {
                    CancelPersistentSceneAttention("new-body-task");
                    m_RoomTaskDispatch = StartSceneExploration(decision, generation)
                        ? "exploration-active" : "not-submitted: exploration-unavailable-or-stopped";
                    m_RoomTaskActionId = m_SceneExploration.active ? m_SceneExploration.goalId : "";
                }
                else if (decision.intent == "approach" || decision.intent == "follow")
                {
                    if (reuseFollow && RoomFollowIdentityCurrent(retainedFollow))
                    {
                        m_RoomTaskDispatch = "follow-already-active";
                        m_RoomTaskActionId = retainedFollow.TaskId;
                        planningTrace?.Mark("room.follow_reused", new JObject {
                            ["generation"] = generation, ["actionId"] = retainedFollow.TaskId,
                            ["phase"] = bridge.Phase, ["newMotionSubmitted"] = false,
                            ["imageRefreshRequired"] = false });
                    }
                    else SubmitRoomUserTask(decision, bridge, generation, evidence.currentTargetRevision,
                        () => generation == m_FormalResponseGeneration && workEpoch == m_WorkEpoch &&
                            userTurn == m_ActionConstraints.CurrentUserTurn && ActorProposalCurrent() &&
                            !m_UserSpeechActiveForAutonomy && isActiveAndEnabled);
                }
                else if (decision.intent == "stop-looking")
                {
                    CancelPersistentSceneAttention("explicit-stop-looking");
                    m_RoomTaskDispatch = "gaze-stop-submitted";
                    m_RoomTaskActionId = "gaze-release:" + Guid.NewGuid().ToString("N");
                }
                else if (decision.intent == "stop-moving")
                {
                    StopSceneExploration("explicit-stop", decision.origin == "user" || sceneStopCommitted);
                    CancelSceneAction("explicit-stop");
                    if (sceneStopCommitted)
                    { m_RoomTaskActionId = sceneStopId; m_RoomTaskDispatch = sceneStopDispatch; }
                    else if (prepared != null && prepared.stopActionId != null)
                    { m_RoomTaskActionId = prepared.stopActionId; m_RoomTaskDispatch = prepared.stopDispatch; }
                    else
                    {
                        var intent = new DialogueMotionIntent("stop-moving", "", generation, ++m_MotionSequence);
                        RegisterMotionAction(ref intent);
                        m_RoomTaskActionId = intent.ActionId;
                        bridge.StopForTask(intent.ActionId);
                        m_RoomTaskDispatch = "stop-submitted";
                    }
                }
                else if (decision.intent == "inspect")
                {
                    bridge.ReadCurrentTargetEvidence(true); m_RoomTaskDispatch = "read-only-checked";
                    m_RoomTaskActionId = "room-read:" + Guid.NewGuid().ToString("N");
                }
            }
            DispatchReply();
        }
        void RequestFresh()
        {
            if (generation != m_FormalResponseGeneration || workEpoch != m_WorkEpoch ||
                userTurn != m_ActionConstraints.CurrentUserTurn || m_UserSpeechActiveForAutonomy ||
                (m_ExplorationStepGeneration == generation && SceneExplorationInputPending)) return;
            if (!ActorProposalCurrent()) { EndRevokedActorProposal(); return; }
            if (HasActiveActorPlan) m_ActorPlan.RetryUnsubmitted(m_ActorPlan.Revision);
            evidence = bridge.ReadCurrentTargetEvidence(true);
            imageUrl = ResolveFormalSceneImage(generation);
            observation = GetFormalSceneObservation(generation);
            m_RoomTaskTargetRevision = evidence.currentTargetRevision;
            input = BuildRoomDecisionInputWithObservation(userText, autonomous, prompt, bridge, observation);
            if (decisionRepair != null)
            {
                var repairInput = JObject.Parse(input);
                repairInput["decisionRepair"] = decisionRepair.DeepClone();
                input = repairInput.ToString(Formatting.None);
            }
            if (visualCorrection != null)
            {
                var correctedInput = JObject.Parse(input);
                correctedInput["visualCorrection"] = visualCorrection.DeepClone();
                input = correctedInput.ToString(Formatting.None);
            }
            Debug.Log($"[Vision/Consumer] consumer=room generation={generation} capture={observation?.CaptureId ?? "none"} image={!string.IsNullOrEmpty(imageUrl)}", this);
            int requestRevision = ++decisionRevision;
            model.PostRoomTaskMessage(input, imageUrl, false, (ok, json, status) => {
                if (requestRevision == decisionRevision) OnDecision(ok, json, status, userText, null);
            });
        }
        void PrepareInitialDecision()
        {
            // FirstReply can consume several seconds of the original frame's
            // twelve-second lifetime. Do not spend that same budget again on room
            // planning and candidate matching. A just-captured frame is reusable.
            if (RoomFollowIdentityCurrent(retainedFollow) || !SceneEyesOpen || observation?.HasImage != true ||
                Time.realtimeSinceStartup - observation.CapturedAtRealtime < 1f ||
                TryGetSceneVisionPreparedStop(generation, out _, out _))
            { RequestFresh(); return; }
            var avatar = bridge.avatar; var environment = bridge.environmentRoot;
            int visionEpoch = m_SceneVisionEpoch;
            int captureRevision = ++decisionRevision;
            m_PendingSceneFormalCaptureGeneration = generation;
            RequestFreshSceneObservation("room-decision-start", refreshed => {
                if (generation != m_FormalResponseGeneration || captureRevision != decisionRevision) return;
                if (workEpoch != m_WorkEpoch || userTurn != m_ActionConstraints.CurrentUserTurn ||
                    m_UserSpeechActiveForAutonomy || !isActiveAndEnabled || model != m_ChatSettings?.m_ChatModel ||
                    bridge == null || bridge != GetComponent<ArdyRoomDialogueBridge>() || !bridge.IsConnected ||
                    avatar != bridge.avatar || environment != bridge.environmentRoot ||
                    visionEpoch != m_SceneVisionEpoch)
                { InvalidateFormalResponse("room-initial-view-owner-changed"); return; }
                m_PendingSceneFormalCaptureGeneration = -1;
                BindFormalSceneObservation(IsFreshSceneObservation(refreshed) ? refreshed : null, generation);
                planningTrace?.Mark("room.initial_view_refreshed", new JObject {
                    ["fresh"] = IsFreshSceneObservation(refreshed), ["captureId"] = refreshed?.CaptureId });
                RequestFresh();
            });
        }
        // User-only prefetch predates the adopted role proposal and cannot decide
        // this handoff. Rebuild the planner input with the same turn's proposal.
        if (autonomous || ReadFastLoopPlannerProposal(generation) != null ||
            !TryConsumeRoomPreparation(generation, bridge, OnDecision, PrepareInitialDecision)) PrepareInitialDecision();
        return true;
    }

    private static bool CanRepairRoomDecision(string reason)
        => !string.IsNullOrEmpty(reason) && (reason.StartsWith("invalid-", StringComparison.Ordinal) ||
            reason.StartsWith("unexpected-", StringComparison.Ordinal) || reason.StartsWith("conflicting-", StringComparison.Ordinal) ||
            reason == "evidence-not-in-current-user-text" || reason == "user-origin-in-autonomous-round" ||
            reason == "capability-prerequisite-required" || reason == "adopted-body-turn-amplitude-changed" ||
            reason == "observation-goal-reference-not-pending" || reason == "newer-user-turn-superseded-observation-goal" ||
            reason == "transport:room-task-incomplete-output");

    private string BuildRoomTaskReplyFacts()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        return "[本轮空间任务；由同一模型的结构化意图及Unity执行事实共同确定]\n" +
            JsonConvert.SerializeObject(new {
                intent = m_RoomTaskIntent, dispatch = m_RoomTaskDispatch, actionId = m_RoomTaskActionId,
                targetRevision = m_RoomTaskTargetRevision,
                roomFacts = ReadCompactRoomTaskFacts(bridge), sceneActionFacts = BuildSceneActionFacts(),
                activeExploration = BuildSceneExplorationContext()
            }) +
            "\n本轮的房间动作已由上述步骤决定，普通台词不再提交任何approach/follow/stop-moving。" +
            "intent=none表示本轮没有空间任务，继续真实话题；inspect只查询用户接近路线/空间执行状态，不代表已看过另一个方向；纯图像评论不必查询用户路线。" +
            "explore建立持续探索任务，由后续新图决定落点、移动、观察或休息，不是到达或已开始迈步。exploration-active只说明任务已受理，可简短回应；不要声称已经逛过。stop-moving同时结束探索，之后不会因为旧任务自动再次出发。move-to-ground是走到本次图像可见的合法地面，结果以实际执行为准。" +
            "其他空间intent本轮只生成简短自然台词，不输出任何工具标签；不得以挥手/摇头替换走近。" +
            "look-at/approach-object/reposition-view/orient-view/turn-in-place/inspect-object/set-object-state可在自主轮保持silent而不发声；以sceneActionFacts解释实际视觉动作，不能说成已走到用户身边。转向取得新图才是获得了新视野，不证明未知方向的猜测。objectContext是被关注物件的带时间快照，场景标注不是视觉辨认，未观察对象不能编造。设备开关只改变登记发光体及Light，不是伸手按开关，烘焙照明可能保留。state-applied-unconfirmed只证明状态已执行但新视觉尚未确认。" +
            "reposition-view是保持面向已确认目标的小范围观察站位调整。viewDirection和actualViewOffset说明请求方向与实际位移；arrived只证明移动到位并重认目标，不证明看清文字或整个物件完整入镜。结合动作后的当前图片与viewBefore/viewAfter可见像素证据评价看全/读细节/避遮挡目的；图像不足时如实保留不确定，不把后退改说成靠近。" +
            "attempt-submitted只是已尝试；先读当前阶段，拒绝不能说已完成。not-submitted是尚未尝试，不引用旧失败冒充本次结果。" +
            "approach=你靠近用户，用户自己往后走不等于你后退。台词方向、是否尝试和结果必须一致。" +
            "follow是持续跟随：follow-waiting是在用户身边等待其移动，仍未停止跟随；follow-paused是路径受阻暂时等待新位置，不是已完成。" +
            "接近/跟随请求刚受理且仍活动时，优先用符合角色口吻的一句回应表达愿意尝试，不必追加瞬时步态、距离、速度匹配或完成宣告；任务暂停/失败时改述实际阻碍。用户正在问执行结果时仍如实回答结果，不用受理回应回避问题。" +
            "controlEntryPoints说明当前已接入的口头与场景动作能力。超时或本次站位失败不能推断为只能由按钮移动；preparing还不是正在走，失败原因只描述实际记录的阶段。" +
            "不要把候选空间检查推测成用户坐姿或主观心理；发声格式故障不能解释为走神。";
    }

    private bool ShouldGuardRoomSpeech(int generation) => m_RoomTaskGeneration == generation && m_RoomTaskIntent != "none";

    private string BuildFormalResponseRequestContext(string observationContext, int generation, bool roomTaskPrepared)
    {
        if (!roomTaskPrepared || !ShouldGuardRoomSpeech(generation))
            return BuildMotionRequestContext(observationContext);
        // This transaction already selected its one spatial intent, and its transient context
        // includes the compact current execution facts. A public description cannot issue a
        // second motion. Do not append the unrelated free-generation grammar or the legacy
        // full navigation diagnostic tree again. Ordinary intent=none retains that capability.
        string result = (observationContext ?? "") +
            "\n[本轮空间执行后的正式回应]\n依据本轮完整用户要求、角色提示词、当前图片及带动作标识的程序结果自然回应。" +
            "本轮空间动作已经单独决定；这里不再输出motion或其他工具标签，也不另选上身动作替代它。" +
            "保留是否已提交、正在准备/移动、实际到达、拒绝、已重认和仍未知的区别。可沉默观察；不得从请求或旧结果推断已完成。";
        Debug.Log($"[Room/ReplyContext] generation={generation} intent={m_RoomTaskIntent} mode=committed-spatial-description characters={result.Length} independentMotionMenu=false", this);
        return result;
    }

    private void CompleteRoomSpeech(SpeechTextBuffer buffer, string candidateExecutable, int generation,
        bool recordHistory, ObservationReceipt observations)
    {
        if (generation != m_FormalResponseGeneration || !isActiveAndEnabled || m_UserSpeechActiveForAutonomy || m_SceneTimePaused) return;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var model = m_ChatSettings.m_ChatModel;
        var avatar = bridge != null ? bridge.avatar : null;
        var environment = bridge != null ? bridge.environmentRoot : null;
        int visionEpoch = m_SceneVisionEpoch;
        int epoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn;
        var reviewTrace = m_ResponseTrace?.Child("room-speech-review");
        reviewTrace?.Mark("speech.review_begin", new JObject { ["generation"] = generation,
            ["intent"] = m_RoomTaskIntent, ["dispatch"] = m_RoomTaskDispatch });
        string candidate = buffer.ToString();
        bool consumed = false;
        int reviewRevision = 0;
        string reviewedFactScope = "legacy";
        int ownerRevision = ++m_RoomSpeechReviewOwnerRevision;
        // This formal round has produced no public audio yet. Register the entire
        // review/refresh continuation so reset/disable can release its owner even
        // when the cancelled provider/capture intentionally never calls back.
        m_RoomSpeechGuardGeneration = generation;
        m_RoomSpeechReviewModel = model;
        m_PendingSceneFormalCaptureGeneration = generation;
        bool Fresh() => generation == m_FormalResponseGeneration && epoch == m_WorkEpoch &&
            ownerRevision == m_RoomSpeechReviewOwnerRevision &&
            userTurn == m_ActionConstraints.CurrentUserTurn && !m_UserSpeechActiveForAutonomy &&
            !m_SceneTimePaused && isActiveAndEnabled && model != null && model == m_ChatSettings?.m_ChatModel &&
            bridge != null && bridge == GetComponent<ArdyRoomDialogueBridge>() && bridge.IsConnected &&
            bridge.avatar == avatar && avatar != null && bridge.environmentRoot == environment && environment != null &&
            m_SceneVisionEpoch == visionEpoch;
        void ReleaseOwner()
        {
            if (ownerRevision != m_RoomSpeechReviewOwnerRevision) return;
            m_RoomSpeechGuardGeneration = -1;
            m_RoomSpeechReviewModel = null;
            if (m_PendingSceneFormalCaptureGeneration == generation) m_PendingSceneFormalCaptureGeneration = -1;
        }
        // Binding can disappear after formal generation but before its review
        // starts. The early silent/invalid-speech branches below precede the
        // watchdog, so revoke this freshly registered owner synchronously.
        if (!Fresh()) { CancelRoomSpeechReview("review-binding-unavailable"); return; }
        void Finish(bool coherent, string reviewStatus)
        {
            if (consumed || !Fresh()) return;
            consumed = true;
            ReleaseOwner();
            reviewTrace?.Mark("speech.review_complete", new JObject { ["coherent"] = coherent,
                ["status"] = reviewStatus, ["factScope"] = reviewedFactScope });
            string language = coherent ? buffer.LanguageCode : RoomFallbackLanguage(buffer.LanguageCode);
            bool quietFallback = !coherent && m_AgentCurrentRoundIsTick && !IsWorkSceneFeedbackGuard(generation);
            string fallback = bridge == GetComponent<ArdyRoomDialogueBridge>() && bridge != null && bridge.IsConnected
                ? RoomTaskFallback(bridge) : "房间移动目前没有连接，我还不能确认这次动作的结果。";
            if (m_RoomTaskIntent == "inspect" && (reviewedFactScope == "visual-observation" || reviewedFactScope == "none"))
                fallback = "我还没有确认当前画面中的情况。";
            string text = quietFallback ? "<silent/>" : coherent ? candidate :
                HasDefinitiveRoomActionFailure() ? CurrentRoomActionReceipt(language) : LocalizeRoomFallback(fallback, language);
            // Only the audited public reply enters history and the action parser.
            // Raw candidates remain diagnostics; no discarded promises become memory.
            if (recordHistory) model.m_DataList.Add(new LLM.SendData("assistant", text));
            reviewTrace?.Mark(quietFallback ? "speech.silent_fallback" : "speech.released", new JObject {
                ["candidateReleased"] = coherent, ["characters"] = quietFallback ? 0 : text.Length });
            if (quietFallback) OnStreamDelta("<silent/>");
            else if (coherent)
                foreach (var part in buffer.Snapshot()) OnSpeechStreamDelta(part);
            else OnSpeechStreamDelta(new SpeechText(text, language, "room-task-fallback", SpeechActionDependency.Independent));
            AcknowledgeObservations(observations, generation, text);
            m_FormalResponseInFlight = false;
            Debug.Log($"[Room/SpeechReview] generation={generation} action={m_RoomTaskActionId} releasedCandidate={coherent} status={reviewStatus}", this);
            OnStreamComplete(text);
        }
        System.Collections.IEnumerator WatchReviewOwner()
        {
            float deadline = Time.realtimeSinceStartup + 40f;
            while (!consumed && ownerRevision == m_RoomSpeechReviewOwnerRevision)
            {
                if (!Fresh())
                {
                    // An epoch-only cancellation must also release this generation;
                    // never cancel a replacement formal response.
                    if (generation == m_FormalResponseGeneration) CancelRoomSpeechReview("review-owner-invalidated");
                    yield break;
                }
                if (Time.realtimeSinceStartup >= deadline)
                { Finish(false, "review-deadline"); yield break; }
                yield return null;
            }
        }
        if (m_AgentCurrentRoundIsTick && IsSceneVisualIntent(m_RoomTaskIntent) &&
            string.IsNullOrWhiteSpace(candidate) && (candidateExecutable ?? "").Trim() == "<silent/>" && !m_RoomTaskFormatError)
        {
            if (!Fresh()) return;
            consumed = true; ReleaseOwner(); m_FormalResponseInFlight = false;
            if (recordHistory) model.m_DataList.Add(new LLM.SendData("assistant", "<silent/>"));
            AcknowledgeObservations(observations, generation, "<silent/>");
            OnStreamDelta("<silent/>"); OnStreamComplete("<silent/>");
            return;
        }
        if (!RoomTaskProtocol.IsPlainSpeech(candidate) || m_RoomTaskFormatError ||
            buffer.Snapshot().Any(part => part.ActionDependency == SpeechActionDependency.AfterAction))
        { Finish(false, "missing-or-invalid-public-speech"); return; }
        // A stable rejected result is not evidence that an arbitrary success claim
        // is safe. Use the existing factual fallback instead of asking a reviewer
        // to potentially relabel this failed action as factScope=none.
        if (HasDefinitiveRoomActionFailure())
        { Finish(false, "current-action-rejected-use-factual-receipt"); return; }
        void SubmitReview(int attempt, SceneVisionObservation observation, string observationSource)
        {
            if (consumed || !Fresh()) return;
            // Preparation and submission are synchronous. A callback must never
            // get one unchecked chance to send a known expired image.
            if (SceneEyesOpen && !IsFreshSceneObservation(observation))
            { Finish(false, "visual-review-preparation-unavailable"); return; }
            reviewTrace?.Child("review-view").Mark("speech.review_view_ready", new JObject {
                ["attempt"] = attempt, ["source"] = observationSource });
            var snapshot = CaptureRoomSpeechSnapshot(bridge, observation);
            Debug.Log($"[Room/SpeechReviewView] generation={generation} attempt={attempt} source={observationSource} capture={observation?.CaptureId ?? "none"} age={(observation == null ? -1f : Time.realtimeSinceStartup - observation.CapturedAtRealtime):F3}s", this);
            string input = JsonConvert.SerializeObject(new {
                intendedAction = m_RoomTaskIntent, dispatch = m_RoomTaskDispatch, actionId = m_RoomTaskActionId,
                publicSpeech = candidate, requestedUserText = m_ActionConstraints.CurrentUserText,
                declaredMotion = ExtractRoomReviewMotion(candidateExecutable),
                currentRoomFacts = ReadCompactRoomTaskFacts(bridge), sceneActionFacts = BuildSceneActionFacts(),
                activeExploration = BuildSceneExplorationContext(),
                acceptedRequest = new { available = HasCurrentRoomRequestReceipt(snapshot),
                    actionId = snapshot.activeTaskId,
                    scope = "Same accepted approach/follow request only; no evidence of present movement, arrival, route, distance, speed or visual contents." },
                visualObservation = observation != null ? JObject.Parse(observation.MetadataJson) : null,
                visualEvidence = BuildSceneObservationContext(observation),
                reviewAttempt = attempt, scope = "Classify dependencies of the actual public claim. A user route assessment is not a verdict on image contents or another scene target."
            });
            int revision = ++reviewRevision;
            Action<bool, string, string> OnReview = (ok, output, status) => {
                if (consumed || revision != reviewRevision || !Fresh()) return;
                if (!ok || !RoomTaskProtocol.TryReviewScope(output, out string factScope)) { Finish(false, status); return; }
                reviewedFactScope = factScope;
                if (HasDefinitiveRoomActionFailure())
                { Finish(false, "action-failed-during-review"); return; }
                // An explicit semantic rejection cannot be rescued by re-auditing
                // the unchanged old sentence against a newer frame. Finish with
                // current factual fallback now; do not spend another model trip.
                if (!RoomTaskProtocol.ReviewAllows(output))
                {
                    reviewTrace?.Mark("speech.review_retry_avoided", new JObject {
                        ["reason"] = "candidate-not-approved", ["attempt"] = attempt });
                    Finish(false, status); return;
                }
                if (RoomTaskProtocol.ReviewUsesAcceptedRequest(output))
                {
                    if (!HasCurrentRoomRequestReceipt(snapshot))
                    { Finish(false, "accepted-request-no-longer-current"); return; }
                    reviewTrace?.Mark("speech.review_receipt_validated", new JObject {
                        ["actionId"] = snapshot.activeTaskId, ["attempt"] = attempt,
                        ["scope"] = "same ongoing accepted request; not current-motion or completion evidence" });
                    Finish(true, status); return;
                }
                var dependencies = ResolveRoomSpeechDependencies(m_RoomTaskIntent, factScope);
                var changed = ChangedRoomSpeechFacts(snapshot, dependencies);
                if (changed.Count == 0) { Finish(RoomTaskProtocol.ReviewAllows(output), status); return; }
                Debug.Log($"[Room/SpeechReviewFacts] generation={generation} action={m_RoomTaskActionId} intent={m_RoomTaskIntent} scope={factScope} attempt={attempt} changed={string.Join(",", changed)} userRevision={snapshot.targetRevision}->{bridge?.ReadCurrentTargetEvidence().currentTargetRevision}", this);
                if (attempt == 0 && bridge != null && bridge.IsConnected)
                {
                    ++reviewRevision; // Old successful callbacks lose authority during refresh.
                    PrepareReview(1);
                    return;
                }
                Finish(false, "related-facts-changed-during-review");
            };
            try
            {
                model.PostRoomTaskMessage(input, observation?.HasImage == true && SceneEyesOpen ? observation.ImageDataUrl : null,
                    true, OnReview);
            }
            catch (Exception error)
            {
                Debug.LogWarning("[Room/SpeechReview] request failed: " + error.Message, this);
                Finish(false, "review-request-failed");
            }
        }
        void PrepareReview(int attempt)
        {
            if (consumed || !Fresh()) return;
            if (!SceneEyesOpen) { SubmitReview(attempt, null, "eyes-closed"); return; }
            if (TryGetPreparedRoomSpeechObservation(generation, out var ready, out string source))
            {
                BindFormalSceneObservation(ready, generation);
                SubmitReview(attempt, ready, source);
                return;
            }
            var captureTrace = reviewTrace?.Child("review-capture");
            captureTrace?.Mark("vision.capture_requested", new JObject { ["attempt"] = attempt });
            int captureRevision = ++reviewRevision;
            m_PendingSceneFormalCaptureGeneration = generation;
            Debug.Log($"[Room/SpeechReviewView] generation={generation} attempt={attempt} source=capture-required", this);
            RequestSceneObservation(attempt == 0 ? "speech-review-prepare" : "speech-review-refresh", refreshed => {
                if (consumed || captureRevision != reviewRevision || !Fresh()) return;
                captureTrace?.Mark("vision.capture_returned", new JObject { ["fresh"] = IsFreshSceneObservation(refreshed) });
                if (!IsFreshSceneObservation(refreshed))
                { Finish(false, "visual-review-preparation-unavailable"); return; }
                BindFormalSceneObservation(refreshed, generation);
                SubmitReview(attempt, refreshed, "captured-for-review");
            });
        }
        if (Application.isPlaying) StartCoroutine(WatchReviewOwner());
        PrepareReview(0);
    }

    private static string ExtractRoomReviewMotion(string executable)
    {
        string copy = executable ?? "";
        return DialogueMotionProtocol.TryExtract(ref copy, 0, 0, out var intent, out var error, true)
            ? intent.Name : string.IsNullOrEmpty(error) ? "none" : "invalid-motion";
    }

    private string RoomTaskFallback(ArdyRoomDialogueBridge bridge)
    {
        if (bridge == null || !bridge.IsConnected) return "房间移动目前没有连接，我还不能确认这次动作的结果。";
        var current = bridge.ReadCurrentTargetEvidence();
        if (IsSceneVisualIntent(m_RoomTaskIntent))
        {
            if (m_RoomTaskIntent == "set-object-state" && m_SceneActionFact?.intent == m_RoomTaskIntent && m_SceneActionFact.phase == "rejected" &&
                (m_SceneActionFact.actionId ?? "") == (m_RoomTaskActionId ?? ""))
            {
                string cause = m_SceneActionFact.reason ?? "";
                if (cause.StartsWith("visible-object-not-confirmed", StringComparison.Ordinal) ||
                    cause.StartsWith("referenced-entity-not-visible-in-current-capture", StringComparison.Ordinal))
                    return "这次还没在画面里确认到你指的物件，因此没有执行开关。";
                if (cause == "selected-object-has-no-registered-power-capability")
                    return "这次选中的物件没有可用开关接口，还不能确认选中了你说的那个物件。";
                if (cause == "entity-reference-not-known-in-current-room")
                    return "我还没在当前房间确认这个物件的身份，需要重新看清。";
                if (cause == "device-outside-nearby-operation-distance")
                    return "我离这个物件太远，这次没有执行开关。";
                if (cause == "visual-reference-not-current")
                    return "刚才的画面已不适合确认目标，我需要重新看一下。";
            }
            if (m_SceneActionFact?.actionId != m_RoomTaskActionId || string.IsNullOrEmpty(m_RoomTaskActionId))
                return "这次还没有执行新的视觉目标动作。";
            if (m_SceneActionFact.reached) return m_RoomTaskIntent == "reposition-view"
                ? "我已经调整了观察位置，还需要确认这个角度能否看清目标。"
                : "我已经走到那个目标附近的可站位置了。";
            if (m_SceneActionFact.phase == "already-satisfied") return "灯具本来就处于你要求的状态，这次没有再次切换。";
            if (m_SceneActionFact.phase == "performed") return "灯具的运行状态已更新，我也重新看了一眼；房间原有的烘焙照明可能仍然保留。";
            if (m_SceneActionFact.phase == "state-applied-unconfirmed") return "灯具状态已执行更新，但我还没有完成这次的确认。";
            if (m_SceneActionFact.phase == "observed") return "我已经看向那里确认了一下。";
            if (m_SceneActionActive) return m_RoomTaskIntent == "approach-object"
                ? "我正在尝试靠近刚才看到的目标。" : m_RoomTaskIntent == "reposition-view"
                ? "我正在尝试调整观察位置，之后再确认能否看清。" : "我正在调整视向，等看清后再确认。";
            return "这次还没完成，视觉目标或移动检查没有通过。";
        }
        if (m_RoomTaskIntent == "stop-moving" && m_RoomTaskDispatch == "stop-submitted")
            return bridge.ActionId == m_RoomTaskActionId && !bridge.ReservesBody && !bridge.IsFollowing ? "我已经停止移动了。"
                : "刚才的停止请求已经处理，当前动作状态又发生了变化。";
        if (m_RoomTaskIntent == "unresolved") return "我还没确认这次的移动意图，暂时没有提交新的动作。";
        if (m_RoomTaskIntent == "inspect")
            return current.assessment.canPlanRoute ? "我重新检查了现在的位置，附近有可用路线；这次还没有开始移动。"
                : "我重新检查了现在的位置，暂时还没有确认可以走近的路线。";
        if (m_RoomTaskIntent == "follow")
        {
            if (bridge.ActionId == m_RoomTaskActionId && bridge.Phase == "preparing")
                return "我已经接到跟随要求，正在准备行走动作，还没有开始走。";
            if (bridge.ActionId == m_RoomTaskActionId && bridge.Phase == "failed" &&
                (bridge.Status ?? "").IndexOf("504", StringComparison.Ordinal) >= 0)
                return "这次跟随请求已提交，但准备行走动作时服务等待超时，所以没有走起来。";
            if (!bridge.IsFollowing || bridge.ActiveTaskId != m_RoomTaskActionId)
                return "这次跟随尚未继续执行，需要确认当前任务状态。";
            if (bridge.Phase == "follow-paused") return "现在的路线检查没有通过，我先停在这里，等位置变化后再确认。";
            if (bridge.Phase == "follow-waiting") return "我在你附近，会继续跟着你。";
            return "我正在跟着你移动。";
        }
        if (bridge.ActionId == m_RoomTaskActionId && bridge.Phase == "arrived" && current.currentlyNearAndFacingUser)
            return "我已经走到你身边了。";
        if (current.currentTargetRevision != m_RoomTaskTargetRevision)
            return "位置发生了变化，我需要按现在的位置重新确认，不能沿用刚才的结果。";
        if (bridge.ReservesBody) return bridge.ActionId == m_RoomTaskActionId ? "我正在尝试走近你。"
            : "当前还有房间动作在执行，这次没有另外启动新的走近。";
        if (m_RoomTaskDispatch != "attempt-submitted") return "这次还没有开始新的走近，我需要以现在的位置重新确认。";
        if (bridge.Controller != null && bridge.Controller.Stage == "target-budget") return "这段路线超过了我目前单次行走的时长限制，还没能走到你身边。";
        if (bridge.Phase == "unreachable") return "这次还没能走到你身边，当前位置的路线或空间检查没有通过。";
        return "这次走近还没有完成，我不能把它当作已经成功。";
    }
}
