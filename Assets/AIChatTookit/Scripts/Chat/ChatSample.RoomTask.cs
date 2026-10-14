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
    private bool m_RoomTaskSpatialAnswer;
    private bool m_RoomTaskFormatError;
    private int m_RoomTaskTargetRevision;
    private int m_RoomSpeechReviewOwnerRevision;
    private LLM m_RoomSpeechReviewModel;
    private JObject m_RoomTaskDecisionFailure;

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
        CancelSceneSearchTask("room-transaction-cancelled: " + reason);
        PauseSpatialGoal("room-transaction-cancelled: " + reason);
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
            "poseReturning", "movingTargetRevision", "committedTargetRevision", "movingTargetNeedsRestart", "movingTargetStatus",
            "navigationRecoveryActive", "navigationRecoveryAttempted", "navigationRecoveryReason", "activeBlockingCategory");
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
            "actionId", "intent", "targetKind", "captureId", "sceneTargetId", "phase", "stage", "reason", "executionReason", "operationRevision", "observedUtc",
            "approachRetargetStatus", "approachRetargetCount", "approachRetargetTravel");
        execution["matchesRequestedAction"] = !string.IsNullOrEmpty(m_RoomTaskActionId) &&
            (string)execution["actionId"] == m_RoomTaskActionId;
        var recovery = full.SelectToken("latestExecutionObservation.streamTelemetry") as JObject;
        if ((bool?)recovery?["failureAppliesToCurrentOperation"] == true &&
            (int?)recovery["failureOperationRevision"] == (int?)execution["operationRevision"] &&
            !string.IsNullOrEmpty((string)recovery["lastFailureCategory"]))
            execution["boundedRecovery"] = PickRoomFields(recovery, "eventName", "lastFailureCategory", "lastFailureReason",
                "recoveryCount", "qualityRecoveryCount", "qualityRecoveryLimit", "controlRecoveryCount", "controlRecoveryLimit", "recovering",
                "failureOperationRevision", "failureRequestId", "failureSessionId", "failureObservedUtc");
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
            bool textTruncated = text.Length > 1600;
            if (text.Length > 1600) text = text.Substring(text.Length - 1600);
            recent.Add(new { role = message.role, quotedText = text, textTruncated });
        }
        return JsonConvert.SerializeObject(recent);
    }

    private string BuildRoomDecisionInput(string userText, bool autonomous, string prompt, ArdyRoomDialogueBridge bridge)
        => BuildRoomDecisionInputWithObservation(userText, autonomous, prompt, bridge, null);

    private JObject ReadRoomSceneActionFacts()
    {
        var facts = BuildSceneActionFacts();
        if (facts["objectContext"] is JObject objects && objects["knownObjectHistory"] is JArray history)
        {
            // Full frozen geometry and bearings appear once in sceneSpatialMemory.
            // Keep these existing referent records small in planner/reply context.
            objects["knownObjectHistory"] = new JArray(history.OfType<JObject>().Select(item =>
                PickRoomFields(item, "entityId", "sceneLabel", "identitySource", "captureId", "observedUtc", "capabilities",
                    "historical", "positionMemoryValid", "requiresReobservation", "actionAuthorization", "currentVisibility")));
        }
        return facts;
    }

    private string BuildRoomDecisionInputWithObservation(string userText, bool autonomous, string prompt,
        ArdyRoomDialogueBridge bridge, SceneVisionObservation observation)
    {
        var roomFacts = ReadCompactRoomTaskFacts(bridge);
        var targetReview = SceneObjectTargetContext();
        var recordedGoal = targetReview?["observationGoal"] as JObject;
        var standingGoal = BuildSpatialGoalContext();
        string standingGoalId = (bool?)standingGoal["ownerCurrent"] == true &&
            (string)standingGoal["status"] != "cancelled" && (string)standingGoal["status"] != "satisfied" ? (string)standingGoal["goalId"] : null;
        var continuableGoal = recordedGoal != null && (string)recordedGoal["status"] != "resolved" &&
            (string)recordedGoal["status"] != "superseded" &&
            (string)recordedGoal["status"] != "suspended" &&
            (standingGoalId == null || (string)recordedGoal["parentSpatialGoalId"] == standingGoalId ||
                IsCurrentIndependentSceneObservationGoal(recordedGoal, standingGoal)) &&
            (!autonomous || (string)recordedGoal["status"] == "pending") ? recordedGoal : null;
        return JsonConvert.SerializeObject(new {
            planningContextVersion = "working-memory-v1",
            currentUserText = userText, autonomous,
            taskSequenceRequired = !autonomous && m_ChatSettings.m_ChatModel.SupportsRoomTaskSequences &&
                !(SpatialGoalExecutionCurrent(m_SpatialDeviceGoal) && SpatialGoalUnfinished(m_SpatialDeviceGoal)),
            taskSequence = autonomous ? null : ReadRoomTaskSequence(),
            bodyTaskHandoff = new { following = bridge.IsFollowing, reservesBody = bridge.ReservesBody,
                scope = "See execution for the current body task. This is not permission to cancel it. Interpret the complete request including stopping/replacing/retaining it. Finish an explicit stop prerequisite before a new body task; device cancellation cannot erase a newly requested operation." },
            currentRequestSemantics = autonomous ? null : ReadCompactCurrentRequestSemantics(m_FormalResponseGeneration),
            autonomousAttention = autonomous ? ReadAutonomyAttention() : null,
            attentionState = ReadAttentionState("planner"),
            latestUserText = autonomous ? "" : m_ActionConstraints.CurrentUserText,
            recentDialogue = JArray.Parse(RecentRoomDialogue()), currentFacts = roomFacts["currentFacts"],
            lastActionResults = roomFacts["lastActionResults"], execution = roomFacts["execution"],
            lastSceneTargetResult = roomFacts["lastSceneTargetResult"],
            currentSupport = roomFacts["currentSupport"], scope = roomFacts["scope"],
            visualObservation = observation != null ? JObject.Parse(observation.MetadataJson) : null,
            visualEvidence = BuildSceneObservationContext(observation),
            sceneActionFacts = ReadRoomSceneActionFacts(),
            visibleKnownObjects = BuildVisibleKnownObjects(observation),
            sceneActionPrerequisites = BuildSceneActionPrerequisites(observation),
            sceneSpatialMemory = BuildSceneSpatialMemory(observation),
            visibleObjectCatalog = BuildSceneVisibleCatalogContext(observation),
            sceneSearch = BuildSceneSearchContext(),
            turnSweep = BuildSceneSweepContext(observation),
            spatialGoal = BuildSpatialGoalContext(),
            deviceGoalCancellationAvailable = SpatialGoalOwnerCurrent(m_SpatialDeviceGoal) && SpatialGoalUnfinished(m_SpatialDeviceGoal),
            verifiedDeviceHistory = BuildVerifiedDeviceReferentHistory(),
            verifiedObservedDeviceHistory = BuildVerifiedObservedDeviceReferentHistory(),
            referentSources = BuildSpatialGoalReferentSources(),
            objectTargetReview = targetReview,
            pendingObservationGoal = continuableGoal,
            decisionContractFacts = new {
                currentUserTurn = m_ActionConstraints.CurrentUserTurn,
                deviceGoalAlreadyReviewed = GetActiveSpatialGoalId() != null,
                evidenceSource = "For a reviewed standing-goal step currentUserText is its original approved request; latestUserText is separately reviewed dialogue/update. For a new goal only actual currentUserText supplies new authorization. Historical receipts resolve identity, never authorize a device write.",
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
        => TryPlanRoomTaskCore(prompt, imageUrl, generation, recordHistory, context);

    private bool TryPlanRoomTaskCore(string prompt, string imageUrl, int generation, bool recordHistory, string context,
        WorkRoomStepRequest workStep = null, bool recoveredExistingUserTurn = false)
    {
        if (!HasRoomTaskPlanner) return false;
        bool currentRequestOnly = CurrentUserTurnRequiresIndependentSpatialRequest();
        if (currentRequestOnly && !m_AgentCurrentRoundIsTick) RestoreIndependentSpatialReview(generation);
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var model = m_ChatSettings.m_ChatModel;
        var evidence = bridge.ReadCurrentTargetEvidence(true);
        var requestAvatar = bridge.avatar;
        var requestEnvironment = bridge.environmentRoot;
        var requestController = bridge.Controller;
        var requestUser = bridge.userHead;
        int workEpoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn;
        m_RoomPlanningBodyLease = new JObject { ["generation"] = generation, ["userTurn"] = userTurn,
            ["actionId"] = bridge.ActiveTaskId, ["following"] = bridge.IsFollowing };
        bool standingGoalStep = !currentRequestOnly && !RoomTaskSequenceUnfinished() &&
            SpatialGoalExecutionCurrent(m_SpatialDeviceGoal) && SpatialGoalUnfinished(m_SpatialDeviceGoal);
        bool autonomous = !standingGoalStep && m_AgentCurrentRoundIsTick && workStep == null;
        string autonomousIntention = autonomous ? m_SceneAutonomyIntention : "";
        bool preserveAutonomousNoAction = false;
        string userText = standingGoalStep ? m_SpatialDeviceGoal.originalRequest : autonomous ? "" : m_ActionConstraints.CurrentUserText;
        m_FormalResponseInFlight = true;
        // Own the complete room transaction, including the planner and deferred captures.
        // Pause/reset must release it even if the provider never calls back.
        m_RoomSpeechGuardGeneration = generation;
        m_RoomSpeechReviewModel = model;
        m_RoomTaskGeneration = generation;
        m_RoomTaskIntent = "none"; m_RoomTaskDispatch = "not-submitted"; m_RoomTaskActionId = "";
        m_RoomTaskDecisionFailure = null;
        m_RoomTaskSpatialAnswer = false;
        m_RoomTaskSequenceSelection = null;
        m_RoomTaskTargetRevision = evidence.currentTargetRevision;
        bool consumed = false;
        int visualRefreshes = 0, targetCorrections = 0, decisionRevision = 0, decisionRepairs = 0;
        int searchesStarted = 0, searchStartRefreshes = 0, proposalReinterpretations = 0;
        bool deviceGoalReviewComplete = false, deviceGoalReviewRejected = false;
        PendingDeviceClarification reviewedPendingClarification = null;
        string deviceGoalReviewFailure = null;
        Action<bool, string, string> pendingDeviceGoalReview = null;
        Action<bool, string, string> pendingControlReview = null;
        SceneVisionStopPreparation preparedBodyReview = null;
        float controlReviewDeadline = -1f;
        string equivalenceCandidate = null;
        bool equivalenceApproved = false, bodyHandoffAttempted = false, preparedStopContinuationRequested = false;
        string committedPreparationId = null, committedPreparationDispatch = null;
        float deviceGoalReviewDeadline = -1f;
        JObject visualCorrection = null, decisionRepair = null, proposalInterpretationCorrection = null;
        SceneVisionObservation observation = GetFormalSceneObservation(generation);
        bool planningReplyPending = true;
        bool planLatestGoalUpdate = false, answerRequired = CurrentGenerationRequiresReviewedGoalAnswer(generation),
            answerThenResume = ReviewedGoalAnswerOwnerCurrent(m_SpatialDeviceGoal) &&
                m_ReviewedGoalAnswerObligation.mode == "answer-then-resume" && !m_ReviewedGoalAnswerObligation.delivered;
        if (answerRequired) { currentRequestOnly=true; standingGoalStep=false; autonomous=false; userText=m_ActionConstraints.CurrentUserText; }
        bool CurrentRequest()
        {
            bool current = generation == m_FormalResponseGeneration && workEpoch == m_WorkEpoch &&
                userTurn == m_ActionConstraints.CurrentUserTurn && !m_UserSpeechActiveForAutonomy &&
                !m_SceneTimePaused && isActiveAndEnabled && model == m_ChatSettings?.m_ChatModel &&
                bridge != null && bridge == GetComponent<ArdyRoomDialogueBridge>() && bridge.IsConnected &&
                bridge.avatar == requestAvatar && bridge.environmentRoot == requestEnvironment && bridge.Controller == requestController && bridge.userHead == requestUser &&
                (!autonomous || autonomousIntention == m_SceneAutonomyIntention) &&
                (workStep == null || IsCurrentWorkRoomStep(workStep));
            if (!current && generation == m_FormalResponseGeneration && m_RoomSpeechGuardGeneration == generation)
                CancelRoomSpeechReview("room-planner-owner-invalidated");
            return current;
        }
        bool CurrentReviewedClarification() => reviewedPendingClarification != null &&
            ReferenceEquals(reviewedPendingClarification, m_PendingDeviceClarification) &&
            PendingDeviceClarificationOwnerCurrent(reviewedPendingClarification) &&
            reviewedPendingClarification.createdGeneration == generation &&
            reviewedPendingClarification.createdUserTurn == userTurn && CurrentRequest();
        System.Collections.IEnumerator WatchPlannerOwner()
        {
            while (planningReplyPending && generation == m_FormalResponseGeneration)
            {
                if (!CurrentRequest()) yield break;
                if (pendingControlReview != null && Time.realtimeSinceStartup >= controlReviewDeadline)
                {
                    var expiredControl = pendingControlReview;
                    model.CancelRoomPreparationMessage();
                    expiredControl(false, "", "control-review-deadline");
                }
                if (pendingDeviceGoalReview != null && Time.realtimeSinceStartup >= deviceGoalReviewDeadline)
                {
                    var expiredReview = pendingDeviceGoalReview;
                    pendingDeviceGoalReview = null;
                    model.CancelRoomDeviceGoalReviewMessage();
                    expiredReview(false, "", "device-goal-review-deadline");
                }
                yield return null;
            }
        }
        string input = AddWorkRoomStepDecisionContext(
            BuildRoomDecisionInputWithObservation(userText, autonomous, prompt, bridge, observation), workStep);
        void ReviewControl(string controlInput, Action<bool, string, string> callback)
        {
            if (TryReusePreparedBodyReview(preparedBodyReview, controlInput, out string cachedReview))
            {
                preparedBodyReview = null; // This transaction can consume the judgment once.
                Debug.Log($"[Room/ControlReviewReuse] generation={generation} sameInput=true permissionRevalidated=true noHttp=true", this);
                callback(true, cachedReview, "same-turn-prepared-body-review");
                return;
            }
            consumed = true;
            int revision = ++decisionRevision;
            bool settled = false;
            pendingControlReview = (ok, result, status) => {
                if (settled || revision != decisionRevision || !CurrentRequest()) return;
                settled = true; pendingControlReview = null; consumed = false;
                callback(ok, result, status);
            };
            controlReviewDeadline = Time.realtimeSinceStartup + 3f;
            try { model.PostRoomPreparationMessage(controlInput, pendingControlReview); }
            catch (Exception) { pendingControlReview?.Invoke(false, "", "control-review-request-failed"); }
        }
        bool HandoffDeviceGoal(RoomTaskProtocol.Decision latestDecision = null)
        {
            if (currentRequestOnly || !CurrentRequest() || GetActiveSpatialGoalId() == null) return false;
            // A standing device objective cannot intercept the latest stop or a
            // different ready obligation. Latest whole-turn order owns the handoff.
            if (latestDecision?.intent == "stop-moving" ||
                RoomTaskSequenceUnfinished() && !SelectedRoomTaskSequenceIsDevice()) return false;
            string goalId = GetActiveSpatialGoalId();
            if (!bodyHandoffAttempted && (bridge.IsFollowing || bridge.ReservesBody))
            {
                bodyHandoffAttempted = true;
                string oldAction = bridge.ActiveTaskId;
                int oldOperation = bridge.Controller.OperationRevision;
                bool oldFollowing = bridge.IsFollowing;
                ReviewControl(RoomControlReviewProtocol.BodyInput(m_ActionConstraints.CurrentUserText,
                    oldAction, oldOperation, bridge.Phase, bridge.IsFollowing).ToString(Formatting.None), (ok, result, status) => {
                    if (GetActiveSpatialGoalId() != goalId) return;
                    string failure = null;
                    bool approved = ok && RoomControlReviewProtocol.TryBodyDecision(result, m_ActionConstraints.CurrentUserText, true, out var control, out _) &&
                        control.intent == "stop-moving";
                    string stopId = m_MotionIdentityPrefix + ":handoff:" + generation + ":" + (++m_MotionSequence);
                    bool stopped = approved && bridge.StopForTaskIfCurrent(stopId, oldAction, oldOperation, out failure, oldFollowing);
                    Debug.Log("[Room/BodyHandoff] " + new JObject {
                        ["generation"] = generation, ["userTurn"] = userTurn, ["goalId"] = goalId,
                        ["previousActionId"] = oldAction, ["previousOperation"] = oldOperation,
                        ["reviewApproved"] = approved, ["stopCommitted"] = stopped,
                        ["failure"] = failure, ["wireStatus"] = status,
                        ["execution"] = ReadCompactRoomTaskFacts(bridge)["execution"]?.DeepClone()
                    }.ToString(Formatting.None), this);
                    // The retry is bounded and still checks current body ownership.
                    // A denial/timeout never grants a stop or a device write.
                    HandoffDeviceGoal(latestDecision);
                });
                return true;
            }
            // The approved objective, not the draft's pixel/angle/first action,
            // owns the rest of this transaction. Never re-enter RoomTask per step.
            consumed = true; planningReplyPending = false; ++decisionRevision;
            if (workStep != null) workStep.replyCommitted = true;
            RecordWorkRoomTaskContext();
            BindRoomTaskSequenceStep(GetActiveSpatialGoalId());
            bool newlyReviewedProposal = deviceGoalReviewComplete && !deviceGoalReviewRejected &&
                latestDecision?.deviceGoal?.operation == "set" &&
                SpatialGoalProposalAlreadyRegistered(latestDecision, m_ActionConstraints.CurrentUserText);
            var requestedObservation = (planLatestGoalUpdate || newlyReviewedProposal) &&
                IsReviewedLatestUserObservation(latestDecision, m_ActionConstraints.CurrentUserText) ? latestDecision : null;
            if (!TryBeginSpatialGoalRunnerWithObservation(GetActiveSpatialGoalId(), generation, requestedObservation))
            {
                BlockSpatialGoal(SpatialGoalStartUnavailableReason(bridge, observation));
                DispatchSpatialGoalTerminalReply(generation);
            }
            return true;
        }
        void OnDecision(bool ok, string json, string status, string sourceText, RoomSpeechPreparation prepared)
        {
            if (consumed || !CurrentRequest()) return;
            if (autonomous && status == AutonomyPremiseProtocol.Rejected)
            {
                // A speculative private premise must not become a public failure
                // explanation or be rewritten into an action by format recovery.
                consumed = true; planningReplyPending = false;
                m_AutonomousReplyOwner = null; m_RoomTaskDispatch = "autonomy-premise-rejected";
                m_RoomSpeechGuardGeneration = -1; m_RoomSpeechReviewModel = null;
                m_FormalResponseInFlight = false; ClearStagedFormalObservation();
                OnStreamDelta("<silent/>"); OnStreamComplete("<silent/>");
                return;
            }
            RoomTaskProtocol.Decision decision = null;
            string validationFailure = ok ? null : "transport:" + (status ?? "unknown");
            bool reviewedGoalStep = (standingGoalStep) && SpatialGoalExecutionCurrent(m_SpatialDeviceGoal) &&
                sourceText == m_SpatialDeviceGoal.originalRequest &&
                m_SpatialDeviceGoal.lastReviewedUserTurn == m_ActionConstraints.CurrentUserTurn &&
                m_SpatialDeviceGoal.latestUserText == m_ActionConstraints.CurrentUserText;
            bool sequenceRequired = !autonomous && !standingGoalStep && model.SupportsRoomTaskSequences;
            string executionJson = sequenceRequired ? RoomTaskSequenceProtocol.DecisionOnly(json) : json;
            bool valid = ok && (reviewedGoalStep
                ? RoomTaskProtocol.TryReviewedGoalStepDecision(executionJson, sourceText, m_ActionConstraints.CurrentUserText, out decision, out validationFailure)
                : autonomous ? RoomTaskProtocol.TryAutonomousDecision(executionJson, autonomousIntention, preserveAutonomousNoAction, out decision, out validationFailure)
                : RoomTaskProtocol.TryDecision(executionJson, sourceText, false, out decision, out validationFailure));
            if (TryGetSceneVisionPreparedStop(generation, out string preparedStopId, out string preparedStopDispatch))
            { committedPreparationId = preparedStopId; committedPreparationDispatch = preparedStopDispatch; }
            string sceneStopId = committedPreparationId, sceneStopDispatch = committedPreparationDispatch;
            bool sceneStopCommitted = !string.IsNullOrEmpty(sceneStopId) && bridge.ActionId == sceneStopId;
            bool stopFallbackOnly = false;
            if (m_SceneVisionStopPreparation?.completed == true && !string.IsNullOrEmpty(m_SceneVisionStopPreparation.reviewJson))
                preparedBodyReview = m_SceneVisionStopPreparation;
            CancelSceneVisionStopPreparation();
            if (sceneStopCommitted)
            {
                // The quick stop is a separate successful operation; it cannot turn
                // a failed complete-request planner into a successful remainder plan.
                if (!valid || sequenceRequired && ReadRoomTaskSequence() == null &&
                    !RoomTaskSequenceProtocol.TryPlan(json, m_ActionConstraints.CurrentUserText, out _, out _))
                {
                    RecordRoomTaskDecisionFailure(generation, userTurn, ok, status, validationFailure ?? "invalid-task-sequence-after-stop", sceneStopId);
                    valid = true; stopFallbackOnly = true;
                    decision = new RoomTaskProtocol.Decision { intent = "stop-moving" };
                }
            }
            if (valid && currentRequestOnly && !TryValidateCurrentRequestOnlyDecision(decision, out string currentRequestFailure))
            { valid = false; validationFailure = currentRequestFailure; }
            if (valid && sequenceRequired && ReadRoomTaskSequence() == null &&
                RoomTaskSequenceProtocol.TryPlan(json, m_ActionConstraints.CurrentUserText, out _, out _) &&
                RoomTaskSequenceProtocol.RequiresEquivalenceReview(json))
            {
                if (equivalenceCandidate != json)
                {
                    ReviewControl(RoomControlReviewProtocol.CoverageInput(m_ActionConstraints.CurrentUserText, json).ToString(Formatting.None),
                        (reviewOk, result, reviewStatus) => {
                            equivalenceCandidate = json;
                            equivalenceApproved = reviewOk && RoomControlReviewProtocol.AcceptsCoverage(result);
                            Debug.Log("[Room/SequenceEquivalence] " + new JObject {
                                ["generation"] = generation, ["userTurn"] = userTurn, ["approved"] = equivalenceApproved,
                                ["wireStatus"] = reviewStatus, ["review"] = result
                            }.ToString(Formatting.None), this);
                            OnDecision(ok, json, status, sourceText, prepared);
                        });
                    return;
                }
                if (!equivalenceApproved) {
                    valid = sceneStopCommitted; validationFailure = "invalid-task-sequence-equivalence";
                    if (sceneStopCommitted) {
                        RecordRoomTaskDecisionFailure(generation, userTurn, ok, status, validationFailure, sceneStopId);
                        stopFallbackOnly = true;
                        decision = new RoomTaskProtocol.Decision { intent = "stop-moving" };
                    }
                }
            }
            if (valid && sceneStopCommitted && !stopFallbackOnly && sequenceRequired &&
                (!RoomTaskSequenceProtocol.RequiresEquivalenceReview(json) || equivalenceCandidate == json && equivalenceApproved))
            {
                AdoptPreparedStopSequence(json, sceneStopId, sceneStopDispatch);
                if (decision.intent == "stop-moving" && WorkNextIntentProtocol.HasReadyStep(ReadRoomTaskSequence()) &&
                    !preparedStopContinuationRequested)
                {
                    // Settle only the actual stop, then plan the remaining ready
                    // step in this transaction. No extra writer/work-review round.
                    preparedStopContinuationRequested = true;
                    m_RoomTaskSequenceSelection = null;
                    Debug.Log("[Room/PreparedStopHandoff] stopped prerequisite settled; planning the remaining current request", this);
                    RequestFresh(); return;
                }
            }
            if (valid && !stopFallbackOnly && !(sceneStopCommitted && decision.intent == "stop-moving") && sequenceRequired &&
                decision.deviceGoal?.operation != "cancel" && !ValidateRoomTaskSequence(json, decision, out string sequenceFailure))
            { valid = false; validationFailure = sequenceFailure; }
            if (valid && decision.deviceGoal != null && !SpatialGoalProposalAlreadyRegistered(decision, sourceText))
            {
                if (standingGoalStep)
                { valid = false; validationFailure = "invalid-device-goal-change-in-continuation"; }
                else if (!deviceGoalReviewComplete)
                {
                    consumed = true;
                    int reviewRevision = ++decisionRevision;
                    var proposalReferentSources = BuildSpatialGoalReferentSources();
                    var proposalDialogue = JArray.Parse(RecentRoomDialogue());
                    string reviewInput = new JObject {
                        ["currentUserText"] = sourceText,
                        ["mode"] = "new-proposal",
                        ["proposal"] = JObject.FromObject(decision.deviceGoal),
                        ["taskSequence"] = ReadRoomTaskSequence(), ["taskStep"] = m_RoomTaskSequenceSelection,
                        ["existingGoal"] = BuildSpatialGoalContext(),
                        ["recentDialogue"] = proposalDialogue.DeepClone(),
                        ["verifiedDeviceHistory"] = BuildVerifiedDeviceReferentHistory(),
                        ["verifiedObservedDeviceHistory"] = BuildVerifiedObservedDeviceReferentHistory(),
                        ["referentSources"] = proposalReferentSources.DeepClone(),
                        ["scope"] = "Independently compare this complete actual user request with the proposed device task. No scene action or device write has been authorized by this proposal."
                    }.ToString(Formatting.None);
                    Action<bool, string, string> OnDeviceGoalReview = (reviewOk, reviewJson, reviewStatus) => {
                        if (deviceGoalReviewComplete || reviewRevision != decisionRevision || !CurrentRequest()) return;
                        pendingDeviceGoalReview = null;
                        deviceGoalReviewComplete = true;
                        RoomTaskProtocol.DeviceGoalReview review = null;
                        bool reviewed = reviewOk && RoomTaskProtocol.TryDeviceGoalProposalReview(reviewJson, out review) &&
                            RoomTaskProtocol.TryValidateListedDeviceReferent(review, proposalReferentSources, out deviceGoalReviewFailure);
                        if (reviewed) review.referenceContext = RoomTaskProtocol.BuildDeviceReferenceContext(proposalDialogue);
                        bool registered = reviewed && TryRegisterReviewedSpatialGoal(decision, sourceText, review, out deviceGoalReviewFailure);
                        deviceGoalReviewRejected = !registered;
                        if (!registered && string.IsNullOrEmpty(deviceGoalReviewFailure))
                            deviceGoalReviewFailure = reviewOk ? "device-goal-review-rejected" : "device-goal-review-transport-failed";
                        Debug.Log("[Room/DeviceGoalReview] " + new JObject {
                            ["generation"] = generation, ["workEpoch"] = workEpoch, ["userTurn"] = userTurn,
                            ["verdict"] = review?.verdict, ["reason"] = deviceGoalReviewFailure ?? review?.reason,
                            ["modelReason"] = review?.reason, ["validationFailure"] = deviceGoalReviewFailure,
                            ["referentKind"] = review?.referentKind, ["referentSourceId"] = review?.referentSourceId,
                            ["legacyReferentGoalId"] = review?.referentGoalId, ["legacyReferentEntityId"] = review?.referentEntityId,
                            ["registered"] = registered, ["requestAssessment"] = review?.requestAssessment, ["wireStatus"] = reviewStatus
                        }.ToString(Formatting.None), this);
                        m_SpatialGoalLatestReview=new JObject { ["generation"]=generation, ["userTurn"]=userTurn, ["workEpoch"]=workEpoch,
                            ["verdict"]=review?.verdict,
                            ["reason"]=deviceGoalReviewFailure ?? review?.reason, ["modelReason"]=review?.reason,
                            ["validationFailure"]=deviceGoalReviewFailure, ["requestAssessment"]=review?.requestAssessment,
                            ["referentKind"]=review?.referentKind, ["referentSourceId"]=review?.referentSourceId,
                            ["registered"]=registered, ["proposal"]=JObject.FromObject(decision.deviceGoal) };
                        if (!registered)
                        {
                            // Parsed but unlisted/invalid references are not independent
                            // semantic evidence that the human request needs clarification.
                            if (reviewed) RememberPendingDeviceClarification(decision, sourceText, review, generation);
                            var pendingContext=BuildPendingDeviceClarificationContext();
                            m_SpatialGoalLatestReview["pendingId"]=pendingContext?["pendingId"]?.DeepClone();
                            m_SpatialGoalLatestReview["originalRequest"]=sourceText;
                        }
                        bool pendingRetained=!registered && PendingDeviceClarificationOwnerCurrent(m_PendingDeviceClarification) &&
                            m_PendingDeviceClarification.createdGeneration == generation;
                        reviewedPendingClarification = reviewed && pendingRetained &&
                            review.verdict == "uncertain" && review.requestAssessment == "target-unclear" &&
                            m_PendingDeviceClarification.createdUserTurn == userTurn ? m_PendingDeviceClarification : null;
                        m_SpatialGoalLatestReview["pendingRetained"]=pendingRetained;
                        m_SpatialGoalLatestReview["proposalDiscarded"]=!registered && !pendingRetained;
                        if (!registered && !pendingRetained)
                        {
                            // The failed interpretation supplies no device authority.
                            // Preserve any other already-approved goal, and give the
                            // actual current utterance one ordinary planning pass.
                            deviceGoalReviewRejected=false;
                            if (review?.requestAssessment == "requested" && review.verdict == "rejected" && proposalReinterpretations == 0)
                            {
                                ++proposalReinterpretations;
                                proposalInterpretationCorrection=new JObject {
                                    ["rejectedProposal"]=JObject.FromObject(decision.deviceGoal),["reviewReason"]=review.reason,
                                    ["attempt"]=proposalReinterpretations,["maximumAttempts"]=1,
                                    ["scope"]="The actual user requested a device operation, but this interpretation was rejected. Reinterpret the complete current utterance once; preserve all target/state/negative constraints. A corrected proposal must receive its own independent authorization review. No old proposal or goal supplies new authority. If unresolved, return none and explain the uncertainty." };
                                deviceGoalReviewComplete=false; currentRequestOnly=false; standingGoalStep=false; planLatestGoalUpdate=false;
                                autonomous=false; userText=m_ActionConstraints.CurrentUserText;
                                consumed=false; RequestFresh(); return;
                            }
                            currentRequestOnly=true; standingGoalStep=false; planLatestGoalUpdate=false;
                            autonomous=false; userText=m_ActionConstraints.CurrentUserText;
                            m_SpatialGoalLatestReview["currentRequestOnly"]=true;
                            // A rejected proposal owes an action-result explanation,
                            // not an invented spatial question. Preserve only a real
                            // independently reviewed answer obligation from this turn.
                            m_SpatialGoalLatestReview["answerRequired"]=answerRequired;
                            m_SpatialGoalLatestReview["proposalResultExplanationRequired"]=true;
                            RestrictSpatialPlanningToCurrentRequest();
                            consumed=false; RequestFresh(); return;
                        }
                        consumed = false;
                        OnDecision(ok, json, status, sourceText, prepared);
                    };
                    pendingDeviceGoalReview = OnDeviceGoalReview;
                    deviceGoalReviewDeadline = Time.realtimeSinceStartup + 30f;
                    try { model.PostRoomDeviceGoalReviewMessage(reviewInput, OnDeviceGoalReview); }
                    catch (Exception error)
                    {
                        Debug.LogWarning("[Room/DeviceGoalReview] request failed: " + error.GetType().Name, this);
                        OnDeviceGoalReview(false, "", "device-goal-review-request-failed");
                    }
                    return;
                }
            }
            if (valid && deviceGoalReviewRejected)
            { valid = false; validationFailure = deviceGoalReviewFailure; }
            if (valid && decision.origin == "user" && decision.IsObjectState &&
                GetActiveSpatialGoalId() == null && decision.deviceGoal == null)
            {
                // A device write cannot omit the independently reviewed main goal.
                // Let the existing bounded repair produce a new full proposal;
                // never invent one from keywords or infer authorization in code.
                valid = false; validationFailure = "invalid-missing-device-goal-proposal";
            }
            if (valid && answerRequired && deviceGoalReviewComplete && !deviceGoalReviewRejected &&
                decision.deviceGoal?.operation == "set" && SpatialGoalProposalAlreadyRegistered(decision, sourceText))
            {
                // A compound replacement/question has two obligations. Its new
                // proposal was independently approved, but answering still comes
                // first; only this fresh approved goal may resume afterwards.
                PauseSpatialGoal("answer-before-new-reviewed-device-task");
                m_SpatialDeviceGoal.resumeAfterDialogue=true;
                RequireReviewedGoalAnswer("answer-then-resume");
                answerThenResume=true; currentRequestOnly=true; standingGoalStep=false; planLatestGoalUpdate=false;
                m_SpatialGoalLatestReview["answerRequired"]=true;
                m_SpatialGoalLatestReview["responseMode"]="answer-then-resume";
                if (decision.IsViewChange && IsFreshSceneObservation(observation) && decision.viewChange?.captureId == observation.CaptureId)
                    DeferReviewedObservationUntilAfterAnswer(decision);
                decision=new RoomTaskProtocol.Decision {intent="none",origin="none",evidence=""};
            }
            if (valid && answerThenResume && decision.IsViewChange && IsFreshSceneObservation(observation) &&
                decision.viewChange?.captureId == observation.CaptureId && DeferReviewedObservationUntilAfterAnswer(decision))
                decision = new RoomTaskProtocol.Decision { intent="none", origin="none", evidence="" };
            if (valid && !sceneStopCommitted && HandoffDeviceGoal(decision)) return;
            if (valid && !sequenceRequired && !TryValidateSpatialGoalStep(decision, out string goalStepFailure))
            { valid = false; validationFailure = goalStepFailure; }
            if (valid && !TryValidateSceneDevicePlanningConditions(decision, observation, out string preconditionFailure))
            { valid = false; validationFailure = preconditionFailure; }
            if (valid && decision.IsSceneSearch && (searchesStarted > 0 || SpatialGoalRunnerOwnsGeneration(generation)))
            { valid = false; validationFailure = "invalid-scene-search-restart: this transaction already searched or is executing a device runner; use its current result, a legal single observation step or none for clarification"; }
            if (valid && decision.IsSceneSearch && !IsFreshSceneObservation(observation))
            {
                if (searchStartRefreshes++ == 0)
                {
                    consumed = true; ++decisionRevision;
                    m_PendingSceneFormalCaptureGeneration = generation;
                    RequestSceneObservation("scene-search-start-refresh", refreshed => {
                        if (!CurrentRequest()) return;
                        m_PendingSceneFormalCaptureGeneration = -1;
                        observation = IsFreshSceneObservation(refreshed) ? refreshed : null;
                        imageUrl = observation?.ImageDataUrl;
                        BindFormalSceneObservation(observation, generation);
                        // This accepted decision contains only the original task mode,
                        // not a pixel/entity/angle. The new search planner sees new RGB.
                        consumed = false;
                        OnDecision(ok, json, status, sourceText, prepared);
                    });
                    return;
                }
                valid = false; validationFailure = "search-start-current-image-unavailable";
            }
            // A continuation ID is not an entity ID. Validate this non-action reference
            // before any candidate look, movement, device change or scene-action receipt.
            if (valid && decision.ContinuesObservationGoal &&
                !TryContinueSceneObservationGoal(decision, out _, out string referenceFailure))
            { valid = false; validationFailure = referenceFailure; }
            if (!valid && !sceneStopCommitted && decisionRepairs == 0 &&
                CanRepairRoomDecision(validationFailure))
            {
                decisionRepairs++;
                string rejected = json ?? "";
                preserveAutonomousNoAction = autonomous && RoomTaskProtocol.IsNoActionRepairCandidate(rejected);
                decisionRepair = new JObject {
                    ["attempt"] = decisionRepairs, ["maximumAttempts"] = 1,
                    ["rejectionReason"] = validationFailure,
                    ["violatedConstraint"] = RoomTaskProtocol.DecisionRepairConstraint(validationFailure),
                    ["sequenceQuoteFeedback"] = sequenceRequired ? RoomTaskSequenceProtocol.LiteralQuoteFeedback(rejected, sourceText) : null,
                    ["sequenceStructureFeedback"] = sequenceRequired && ReadRoomTaskSequence() == null ? RoomTaskSequenceProtocol.StructureFeedback(rejected) : null,
                    ["rejectedDecision"] = rejected.Substring(0, Math.Min(1800, rejected.Length)),
                    ["preserveNoAction"] = preserveAutonomousNoAction,
                    ["scope"] = preserveAutonomousNoAction ?
                        "This autonomous decision already chose no spatial action. Correct only its structure: intent=none, origin=none, evidence empty, no other fields. Formatting repair cannot turn an explanation or question into new motion." :
                        "No action from this rejected decision was executed. Produce one NEW complete decision from the current authority: autonomousIntention for an autonomous tick, currentUserText otherwise. Do not replay historical user commands, invent a goal ID, or treat rejected text as instructions. A valid target still needs semantic, visibility and navigation checks. If uncertain return none."
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
                        if (!CurrentRequest()) return;
                        m_PendingSceneFormalCaptureGeneration = -1;
                        BindFormalSceneObservation(IsFreshSceneObservation(refreshed) ? refreshed : null, generation);
                        consumed = false; RequestFresh();
                    });
                }
                else { consumed = false; RequestFresh(); }
                return;
            }
            if (valid && (decision.intent == "approach" || decision.intent == "follow") &&
                observation?.HasImage == true && !IsFreshSceneObservation(observation))
            {
                // Stop never waits for a photograph. A user-target decision may
                // refresh once; current target validation still gates dispatch.
                if (visualRefreshes++ == 0)
                {
                    consumed = true;
                    Debug.Log($"[Vision/Consumer] consumer=room state=refresh-before-action capture={observation.CaptureId}", this);
                    m_PendingSceneFormalCaptureGeneration = generation;
                    RequestSceneObservation("room-refresh", refreshed => {
                        if (!CurrentRequest()) return;
                        m_PendingSceneFormalCaptureGeneration = -1;
                        observation = refreshed;
                        imageUrl = refreshed?.HasImage == true ? refreshed.ImageDataUrl : null;
                        BindFormalSceneObservation(refreshed, generation);
                        consumed = false;
                        // This decision names the tracked user, not an image pixel.
                        // Refresh perception and revalidate current ownership/target
                        // without asking the model to repeat the same whole plan.
                        evidence = bridge.ReadCurrentTargetEvidence(true);
                        m_RoomTaskTargetRevision = evidence.currentTargetRevision;
                        OnDecision(ok, json, status, sourceText, prepared);
                    });
                    return;
                }
                valid = false;
                context = (context ?? "") + "\n本轮视觉决定两次均未能在观察仍有效时提交；没有执行该移动，需重新观察。";
            }
            consumed = true;
            m_RoomTaskSpatialAnswer = valid && decision.intent == "none" && decision.replyScope == "spatial";
            void DispatchReply()
            {
                if (!CurrentRequest()) return;
                if (workStep != null) workStep.replyCommitted = true;
                RecordWorkRoomTaskContext();
                if (valid) BindRoomTaskSequenceStep();
                BindAutonomousReplyOwner(generation, autonomous, valid, bridge);
                if (sceneStopCommitted && (stopFallbackOnly || decision?.intent == "stop-moving") &&
                    !m_WorkSubstepReviewPending) QueueWorkSubstepReview(generation, "stopped");
                Debug.Log($"[Room/Task] generation={generation} userTurn={userTurn} targetRevision={evidence.currentTargetRevision} intent={m_RoomTaskIntent} dispatch={m_RoomTaskDispatch} action={m_RoomTaskActionId}", this);
                if (valid && TryHandleSpatialGoalPlanSettled(generation))
                { planningReplyPending = false; return; }
                if (valid && !autonomous && currentRequestOnly && !answerRequired && decision.intent == "none" &&
                    (int?)m_SpatialGoalLatestReview?["generation"] == generation &&
                    (bool?)m_SpatialGoalLatestReview?["cancellationApplied"] == true)
                {
                    // A pure cancellation is reported only after the planner
                    // checked the whole utterance for any remaining action.
                    planningReplyPending = false;
                    DispatchSpatialGoalTerminalReply(generation);
                    return;
                }
                if (!valid && ReadRoomTaskDecisionFailure() == null && (deviceGoalReviewRejected || standingGoalStep))
                {
                    // Rejected proposals and unresolved standing goals also use
                    // the small, grounded result route; do not re-enter the broad
                    // prose/tool context or reopen them from a private tick.
                    if (SpatialGoalUnfinished(m_SpatialDeviceGoal)) SetSpatialGoalAwaitingClarification(validationFailure);
                    m_WorkStatus="waiting_user"; m_WorkReviewOpen=false;
                    planningReplyPending=false;
                    DispatchSpatialGoalTerminalReply(generation);
                    return;
                }
                DispatchRoomMotionReplyWhenReady(bridge, generation, workEpoch, userTurn, () => {
                    if (!CurrentRequest()) return;
                    planningReplyPending = false;
                    if ((workStep != null || recoveredExistingUserTurn) && UseMovementReplyPacket(generation))
                    {
                        DispatchPlannedMovementRoomReply(ResolveFormalSceneImage(generation), generation);
                        return;
                    }
                    string replyContext = (context ?? "") + "\n" + BuildRoomTaskReplyFacts() + RoomMotionReadinessReplyContext();
                    if (workStep != null || recoveredExistingUserTurn)
                        DispatchPlannedWorkRoomReply(replyContext, ResolveFormalSceneImage(generation), generation);
                    else
                        DispatchFormalStream(prompt, ResolveFormalSceneImage(generation), generation,
                            recordHistory, replyContext, true);
                });
            }
            if (valid && decision.IsSceneSearch)
            {
                searchesStarted++;
                m_RoomTaskIntent = decision.intent;
                BeginSceneSearchTask(decision, observation, generation, () => {
                    if (!CurrentRequest()) return;
                    m_RoomTaskIntent = "search-scene";
                    var result = BuildSceneSearchContext();
                    m_RoomTaskDispatch = "search-" + ((string)result["status"] ?? "incomplete");
                    DispatchReply();
                });
                return;
            }
            if (valid && decision.IsVisual)
            {
                m_RoomTaskIntent = decision.intent;
                BeginSceneAction(decision, observation, generation, () => {
                    var fact = m_SceneActionFact;
                    if (!SpatialGoalRunnerOwnsGeneration(generation) &&
                        decision.visualTarget != null && targetCorrections == 0 && fact?.canReobserveTarget == true &&
                        fact.captureId == observation?.CaptureId && !m_SceneActionActive &&
                        !bridge.ReservesBody && !bridge.IsFollowing && SceneEyesOpen &&
                        CurrentRequest())
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
                            if (!CurrentRequest()) return;
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
                bool awaitingClarification = deviceGoalReviewRejected && CurrentReviewedClarification();
                m_RoomTaskIntent = "unresolved";
                m_RoomTaskDispatch = awaitingClarification ? "goal-awaiting-clarification" : "decision-unavailable";
                if (!awaitingClarification)
                    RecordRoomTaskDecisionFailure(generation, userTurn, ok, status, validationFailure);
                if (standingGoalStep && SpatialGoalUnfinished(m_SpatialDeviceGoal))
                {
                    BlockSpatialGoal("current-decision-unavailable");
                    m_WorkStatus = "blocked"; m_WorkReviewOpen = false;
                }
                // A successful HTTP envelope is not a valid executable decision. Record the
                // exact rejecting gate and a bounded, escaped payload, without any image,
                // role prompt, auth header or full request context.
                string candidate = json ?? "";
                Debug.LogWarning("[Room/DecisionValidation] " + new JObject {
                    ["generation"] = generation, ["userTurn"] = userTurn,
                    ["wireAccepted"] = ok, ["wireStatus"] = status,
                    ["reason"] = validationFailure ?? "observation-expired-before-dispatch",
                    ["disposition"] = awaitingClarification ? "goal-awaiting-clarification" : "decision-unavailable",
                    ["candidate"] = candidate.Substring(0, Math.Min(1800, candidate.Length)),
                    ["candidateTruncated"] = candidate.Length > 1800,
                    ["scope"] = awaitingClarification ?
                        "Independent current review retained this exact unapproved request because its target is unclear. No action or planning-service failure is asserted; the same pending owner supplies the clarification reply." :
                        "Rejected output only; nothing was executed from this decision. HTTP receipt and action validation are separate."
                }.ToString(Formatting.None), this);
            }
            else
            {
                m_RoomTaskIntent = decision.intent;
                if (decision.intent == "approach" || decision.intent == "follow")
                {
                    var current = bridge.ReadCurrentTargetEvidence();
                    // A scheduled tick cannot repeat the same completed/failed attempt.
                    if (decision.intent == "follow" && bridge.IsFollowing)
                    { m_RoomTaskDispatch = "follow-already-active"; m_RoomTaskActionId = bridge.ActiveTaskId; }
                    else if (autonomous && current.lastAttemptAppliesToCurrentTarget)
                        m_RoomTaskDispatch = "same-target-autonomous-retry-suppressed";
                    else
                    {
                        var intent = new DialogueMotionIntent(decision.intent, "", generation, ++m_MotionSequence);
                        RegisterMotionAction(ref intent);
                        m_RoomTaskActionId = intent.ActionId;
                        string failure;
                        bool submitted = decision.intent == "follow"
                            ? (!autonomous ? bridge.FollowForCurrentUser(intent.ActionId, evidence.currentTargetRevision, requestUser, out failure)
                                : bridge.FollowForTarget(intent.ActionId, evidence.currentTargetRevision, out failure))
                            : bridge.ApproachForTarget(intent.ActionId, evidence.currentTargetRevision, out failure);
                        m_RoomTaskDispatch = submitted ? (decision.intent == "follow" ? "follow-submitted" : "attempt-submitted")
                            : "not-submitted: " + failure;
                        if (submitted && !autonomous && (bridge.ReservesBody || bridge.IsFollowing)) AcknowledgeAcceptedTask(generation, intent.ActionId);
                    }
                }
                else if (decision.intent == "stop-moving")
                {
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
                    if (sequenceRequired && !stopFallbackOnly && !preparedStopContinuationRequested)
                    {
                        AdoptPreparedStopSequence(json, m_RoomTaskActionId, m_RoomTaskDispatch);
                        if (WorkNextIntentProtocol.HasReadyStep(ReadRoomTaskSequence()))
                        {
                            preparedStopContinuationRequested = true;
                            committedPreparationId = m_RoomTaskActionId;
                            committedPreparationDispatch = m_RoomTaskDispatch;
                            consumed = false;
                            Debug.Log("[Room/PreparedStopHandoff] planner stop settled; planning the remaining current request", this);
                            RequestFresh(); return;
                        }
                    }
                }
                else if (decision.intent == "inspect")
                { bridge.ReadCurrentTargetEvidence(true); m_RoomTaskDispatch = "read-only-checked"; }
            }
            DispatchReply();
        }
        void RequestFresh()
        {
            if (!CurrentRequest()) return;
            if (standingGoalStep && !planLatestGoalUpdate && HandoffDeviceGoal()) return;
            evidence = bridge.ReadCurrentTargetEvidence(true);
            imageUrl = ResolveFormalSceneImage(generation);
            observation = GetFormalSceneObservation(generation);
            m_RoomTaskTargetRevision = evidence.currentTargetRevision;
            input = AddWorkRoomStepDecisionContext(
                BuildRoomDecisionInputWithObservation(userText, autonomous, prompt, bridge, observation), workStep);
            if (!standingGoalStep && !autonomous && model.SupportsRoomTaskSequences)
            {
                var wholeTurn = JObject.Parse(input);
                wholeTurn["taskSequenceRequired"] = true;
                input = wholeTurn.ToString(Formatting.None);
            }
            if (currentRequestOnly) input = BuildCurrentRequestOnlyDecisionInput(input);
            if (answerRequired)
            {
                var answerInput=JObject.Parse(input);
                answerInput["answerRequired"]=true;
                answerInput["answerBeforeDeviceResume"]=answerThenResume;
                input=answerInput.ToString(Formatting.None);
            }
            if (planLatestGoalUpdate)
            {
                var latestInput = JObject.Parse(input);
                latestInput["quietSpatialGoalStep"] = true;
                latestInput["userObservationBeforeDeviceTask"] = true;
                input = latestInput.ToString(Formatting.None);
            }
            if (proposalInterpretationCorrection != null)
            {
                var correctedProposalInput=JObject.Parse(input);
                correctedProposalInput["proposalInterpretationCorrection"]=proposalInterpretationCorrection.DeepClone();
                input=correctedProposalInput.ToString(Formatting.None);
            }
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
            var sweepImages = new List<LLM.RoomObservationImage>();
            if (model.SupportsRoomObservationHistory && SceneEyesOpen && observation?.HasImage == true)
                foreach (var frame in GetScenePlanningHistoryImages(observation))
                    sweepImages.Add(new LLM.RoomObservationImage(frame.CaptureId, frame.MetadataJson, frame.ImageDataUrl));
            Debug.Log($"[Vision/Consumer] consumer=room generation={generation} capture={observation?.CaptureId ?? "none"} image={!string.IsNullOrEmpty(imageUrl)} historicalViewImages={sweepImages.Count}", this);
            int requestRevision = ++decisionRevision;
            model.PostRoomTaskMessageWithObservationHistory(input, imageUrl, sweepImages, (ok, json, status) => {
                if (requestRevision == decisionRevision) OnDecision(ok, json, status, userText, null);
            });
        }
        if (Application.isPlaying) StartCoroutine(WatchPlannerOwner());
        if (NeedsPendingDeviceClarificationReview(autonomous))
        {
            consumed=true;
            string latestUserText=m_ActionConstraints.CurrentUserText;
            var pendingContext=BuildPendingDeviceClarificationContext();
            int updateRevision=++decisionRevision;
            var updateInput=(JObject)pendingContext.DeepClone();
            updateInput["mode"]="pending-proposal-update";
            updateInput["latestUserText"]=latestUserText;
            updateInput["currentUserText"]=latestUserText;
            updateInput["verifiedDeviceHistory"]=BuildVerifiedDeviceReferentHistory();
            updateInput["verifiedObservedDeviceHistory"]=BuildVerifiedObservedDeviceReferentHistory();
            var pendingReferentSources=BuildSpatialGoalReferentSources();
            updateInput["referentSources"]=pendingReferentSources.DeepClone();
            updateInput["recentDialogue"]=JArray.Parse(RecentRoomDialogue());
            // Preserve the pending context's actual intervening user words and
            // replacement/cancellation scope; a shorter override would discard
            // the instruction needed to interpret that history without authority.
            bool pendingUpdateSettled=false;
            Action<bool,string,string> OnPendingUpdate=(reviewOk,reviewJson,reviewStatus)=>{
                if (pendingUpdateSettled || updateRevision != decisionRevision || !CurrentRequest()) return;
                pendingUpdateSettled=true;
                pendingDeviceGoalReview=null;
                RoomTaskProtocol.DeviceGoalReview update=null;
                string updateFailure=null;
                bool continued=false;
                bool updated=reviewOk && RoomTaskProtocol.TryPendingDeviceClarificationReview(reviewJson,out update) &&
                    RoomTaskProtocol.TryValidateListedDeviceReferent(update,pendingReferentSources,out updateFailure) &&
                    TryApplyPendingDeviceClarificationUpdate(update,latestUserText,out continued,out updateFailure);
                if (!updated && string.IsNullOrEmpty(updateFailure))
                    updateFailure=reviewOk ? "invalid-pending-device-update-review" : "pending-device-update-review-transport-failed";
                if (!updated)
                    SettleFailedPendingDeviceReview(updateFailure ?? reviewStatus ?? "pending-review-unavailable");
                bool cancellationApplied=updated && update.relation == "cancel";
                bool planCurrentRequestOnly=!updated || cancellationApplied;
                m_SpatialGoalLatestReview=new JObject { ["generation"]=generation,["verdict"]=update?.verdict,
                    ["reason"]=updateFailure ?? update?.reason ?? reviewStatus,["modelReason"]=update?.reason,
                    ["validationFailure"]=updateFailure,["relation"]=update?.relation,
                    ["referentKind"]=update?.referentKind,["referentSourceId"]=update?.referentSourceId,
                    ["reviewApplied"]=updated,["cancellationApplied"]=cancellationApplied,
                    ["currentRequestOnly"]=planCurrentRequestOnly,
                    ["pendingRetained"]=PendingDeviceClarificationOwnerCurrent(m_PendingDeviceClarification),
                    ["registered"]=continued,["pendingId"]=pendingContext["pendingId"]?.DeepClone(),
                    ["originalRequest"]=pendingContext["originalUserRequest"]?.DeepClone(),["proposal"]=pendingContext["proposal"]?.DeepClone() };
                Debug.Log("[Room/PendingGoalClarificationReview] "+new JObject {
                    ["generation"]=generation,["userTurn"]=userTurn,["accepted"]=updated,["registered"]=continued,
                    ["cancellationApplied"]=cancellationApplied,["currentRequestOnly"]=planCurrentRequestOnly,
                    ["pendingId"]=pendingContext["pendingId"]?.DeepClone(),["relation"]=update?.relation,
                    ["verdict"]=update?.verdict, ["modelReason"]=update?.reason,
                    ["referentKind"]=update?.referentKind,["referentSourceId"]=update?.referentSourceId,
                    ["validationFailure"]=updateFailure,
                    ["reason"]=updateFailure ?? update?.reason ?? reviewStatus
                }.ToString(Formatting.None),this);
                if (planCurrentRequestOnly)
                {
                    // The device review cannot veto the latest independent
                    // search/movement request. Applied cancellation likewise
                    // must not consume a compound "cancel that, then turn".
                    RestrictSpatialPlanningToCurrentRequest();
                    currentRequestOnly=true;
                }
                standingGoalStep=continued;
                planLatestGoalUpdate=continued;
                autonomous=false;
                userText=continued ? m_SpatialDeviceGoal.originalRequest : latestUserText;
                consumed=false;
                if (continued)
                {
                    // This is a newly approved goal, so acquire its first action
                    // image after approval instead of planning on the draft's view.
                    m_PendingSceneFormalCaptureGeneration=generation;
                    RequestSceneObservation("pending-clarification-goal-start",refreshed=>{
                        if (!CurrentRequest() || !SpatialGoalExecutionCurrent(m_SpatialDeviceGoal)) return;
                        m_PendingSceneFormalCaptureGeneration=-1;
                        if (!IsFreshSceneObservation(refreshed))
                        {
                            SetSpatialGoalAwaitingClarification("pending-clarification-current-image-unavailable");
                            planningReplyPending=false;
                            DispatchSpatialGoalTerminalReply(generation);
                            return;
                        }
                        BindFormalSceneObservation(refreshed,generation);
                        RequestFresh();
                    });
                    return;
                }
                RequestFresh();
            };
            if (!BeginPendingDeviceClarificationReview(generation,latestUserText))
                OnPendingUpdate(false,"","pending-review-owner-invalidated");
            else
            {
                pendingDeviceGoalReview=OnPendingUpdate;
                deviceGoalReviewDeadline=Time.realtimeSinceStartup+13f;
                try { model.PostRoomDeviceGoalReviewMessage(updateInput.ToString(Formatting.None),OnPendingUpdate); }
                catch (Exception) { OnPendingUpdate(false,"","pending-proposal-review-request-failed"); }
            }
        }
        else if (NeedsSpatialGoalUserUpdateReview(autonomous))
        {
            consumed=true;
            string latestUserText=m_ActionConstraints.CurrentUserText;
            int updateRevision=++decisionRevision;
            string updateInput=new JObject {
                ["mode"]="goal-user-update", ["currentUserText"]=latestUserText, ["latestUserText"]=latestUserText,
                ["currentRequestSemantics"]=ReadCompactCurrentRequestSemantics(generation),
                ["existingGoal"]=BuildSpatialGoalContext(), ["verifiedDeviceHistory"]=BuildVerifiedDeviceReferentHistory(),
                ["verifiedObservedDeviceHistory"]=BuildVerifiedObservedDeviceReferentHistory(),
                ["referentSources"]=BuildSpatialGoalReferentSources(),
                ["recentDialogue"]=JArray.Parse(RecentRoomDialogue()),
                ["scope"]="Classify the actual latest utterance against the standing independently approved unfinished goal. Preserve original authorization for continuation/clarification and ordinary dialogue. Cancellation/replacement needs actual user semantics."
            }.ToString(Formatting.None);
            bool userUpdateSettled=false;
            Action<bool,string,string> OnGoalUserUpdate=(reviewOk,reviewJson,reviewStatus)=>{
                if (userUpdateSettled || updateRevision != decisionRevision || !CurrentRequest()) return;
                userUpdateSettled=true;
                pendingDeviceGoalReview=null;
                RoomTaskProtocol.DeviceGoalReview update=null;
                string updateFailure=null;
                bool parsed=reviewOk && RoomTaskProtocol.TryDeviceGoalUserUpdateReview(reviewJson,out update);
                string reviewedResponseMode=update?.responseMode;
                bool continueGoal=false;
                bool updated=parsed && TryApplySpatialGoalUserUpdate(update,latestUserText,out continueGoal,out updateFailure);
                bool independentRequest=!updated || update?.relation == "cancel" ||
                    update?.relation == "unrelated" && update.responseMode == "act";
                if (!updated && string.IsNullOrEmpty(updateFailure))
                    updateFailure=reviewOk ? "invalid-goal-user-update-review" : "goal-user-update-review-transport-failed";
                Debug.Log("[Room/GoalUserUpdateReview] "+new JObject {
                    ["generation"]=generation,["userTurn"]=userTurn,["accepted"]=updated,["relation"]=update?.relation,
                    ["verdict"]=update?.verdict,["responseMode"]=update?.responseMode,["validationFailure"]=updateFailure,["wireStatus"]=reviewStatus,
                    ["reviewedResponseMode"]=reviewedResponseMode,["currentRequestSemantics"]=ReadCompactCurrentRequestSemantics(generation),
                    ["reason"]=update?.reason ?? updateFailure ?? reviewStatus,
                    ["currentRequestOnly"]=independentRequest
                }.ToString(Formatting.None),this);
                m_SpatialGoalLatestReview=new JObject { ["generation"]=generation, ["verdict"]=update?.verdict,
                    ["reason"]=update?.reason ?? updateFailure ?? reviewStatus, ["relation"]=update?.relation,
                    ["responseMode"]=update?.responseMode, ["answerRequired"]=updated && update.responseMode != "act",
                    ["reviewApplied"]=updated,["cancellationApplied"]=updated && update.relation == "cancel",
                    ["currentRequestOnly"]=independentRequest,
                    ["validationFailure"]=updateFailure, ["wireStatus"]=reviewStatus };
                if (!updated)
                {
                    RestrictUnapprovedSpatialGoalUpdateToCurrentRequest();
                    SetSpatialGoalAwaitingClarification(updateFailure ?? "goal-user-update-review-uncertain");
                    // Failure to update the old device goal is not a rejection of
                    // a new observation/movement request. Plan only the actual
                    // latest utterance, with no authority to write, replace or
                    // cancel a device goal. The same fence covers repair passes.
                    currentRequestOnly=true;
                    standingGoalStep=false; autonomous=false; userText=latestUserText;
                    consumed=false;
                    RequestFresh();
                    return;
                }
                if (update.relation == "cancel" || update.relation == "unrelated" && update.responseMode == "act")
                {
                    RestrictSpatialPlanningToCurrentRequest();
                    currentRequestOnly=true;
                }
                answerRequired=update.responseMode != "act";
                answerThenResume=answerRequired && update.responseMode == "answer-then-resume";
                // This local fence only protects the answer pass. The reviewed
                // resume lease survives it; persistent failed-review fences do not.
                if (answerRequired && update.relation != "new") currentRequestOnly=true;
                // Continuing an objective is not permission to discard newly
                // added stop/order constraints in this accepted user turn.
                standingGoalStep=continueGoal && !model.SupportsRoomTaskSequences;
                planLatestGoalUpdate=standingGoalStep;
                autonomous=false;
                userText=standingGoalStep ? m_SpatialDeviceGoal.originalRequest : latestUserText;
                consumed=false;
                RequestFresh();
            };
            pendingDeviceGoalReview=OnGoalUserUpdate;
            deviceGoalReviewDeadline=Time.realtimeSinceStartup+13f;
            try { model.PostRoomDeviceGoalReviewMessage(updateInput,OnGoalUserUpdate); }
            catch (Exception) { OnGoalUserUpdate(false,"","goal-user-update-review-request-failed"); }
        }
        else if (standingGoalStep || autonomous || workStep != null || !TryConsumeRoomPreparation(generation, bridge, OnDecision, RequestFresh)) RequestFresh();
        return true;
    }

    private static bool CanRepairRoomDecision(string reason)
        => !string.IsNullOrEmpty(reason) && (reason.StartsWith("invalid-", StringComparison.Ordinal) ||
            reason.StartsWith("unexpected-", StringComparison.Ordinal) || reason.StartsWith("conflicting-", StringComparison.Ordinal) ||
            reason == "evidence-not-in-current-user-text" || reason == "user-origin-in-autonomous-round" ||
            reason == "observation-goal-reference-not-pending" || reason == "newer-user-turn-superseded-observation-goal" ||
            reason == "transport:room-task-incomplete-output");

    private void RecordRoomTaskDecisionFailure(int generation, int userTurn, bool wireAccepted, string status, string reason, string completedStopActionId = null)
    {
        // Keep the failed planner's receipt separate from prior navigation/scene
        // results. It proves only that this decision submitted no new action.
        bool classifiedTransportFailure = ChatQW.TryReadRoomTaskFailureStatus(status, out JObject providerFailure);
        bool preflightBudgetFailure = !wireAccepted && classifiedTransportFailure &&
            (string)providerFailure["category"] == "context-budget" && (bool?)providerFailure["httpRequestSent"] == false;
        bool transportFailure = !wireAccepted && classifiedTransportFailure && !preflightBudgetFailure;
        string wireStatus = status ?? "unknown";
        string validationReason = reason ?? "decision-unavailable";
        m_RoomTaskDecisionFailure = new JObject {
            ["generation"] = generation, ["userTurn"] = userTurn,
            ["stage"] = preflightBudgetFailure ? "decision-preflight" : "action-planning",
            ["kind"] = preflightBudgetFailure ? "input-budget-exceeded" : transportFailure ? "service-request-failed" : "decision-not-executable",
            ["wireAccepted"] = wireAccepted,
            ["wireStatus"] = wireStatus.Substring(0, Math.Min(256, wireStatus.Length)),
            ["validationReason"] = validationReason.Substring(0, Math.Min(256, validationReason.Length)),
            ["newActionSubmittedByThisDecision"] = false,
            ["scope"] = "This current planning decision failed before a new action was dispatched. It is not evidence that the user was unclear, that navigation failed, or that prior scene actions never occurred. Do not infer the underlying cause, a retry, or eventual success without an explicit receipt."
        };
        if (!wireAccepted && classifiedTransportFailure)
            m_RoomTaskDecisionFailure["providerDiagnostic"] = providerFailure;
        if (preflightBudgetFailure)
            m_RoomTaskDecisionFailure["scope"] = "This decision was stopped locally because the essential input still exceeded its budget after projection. No HTTP request was sent, no provider rejected it, and no new action was dispatched. Do not blame user clarity, claim a network outage, or promise an unsubmitted retry. Previous executed actions remain separate facts.";
        if (!string.IsNullOrEmpty(completedStopActionId)) {
            m_RoomTaskDecisionFailure["remainingAfterCommittedStop"] = true;
            m_RoomTaskDecisionFailure["completedStopActionId"] = completedStopActionId;
        }
        RememberRoomPlanningFailure(m_RoomTaskDecisionFailure);
    }

    private JObject ReadRoomTaskDecisionFailure()
    {
        if (!(m_RoomTaskIntent == "unresolved" && m_RoomTaskDispatch == "decision-unavailable") &&
            !((bool?)m_RoomTaskDecisionFailure?["remainingAfterCommittedStop"] == true && m_RoomTaskIntent == "stop-moving" &&
                m_RoomTaskDispatch == "stop-submitted" && (string)m_RoomTaskDecisionFailure["completedStopActionId"] == m_RoomTaskActionId) ||
            m_RoomTaskGeneration != m_FormalResponseGeneration ||
            (int?)m_RoomTaskDecisionFailure?["generation"] != m_FormalResponseGeneration ||
            (int?)m_RoomTaskDecisionFailure?["userTurn"] != m_ActionConstraints.CurrentUserTurn) return null;
        return (JObject)m_RoomTaskDecisionFailure.DeepClone();
    }

    private string BuildRoomTaskReplyFacts()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        return "[本轮空间任务；由同一模型的结构化意图及Unity执行事实共同确定]\n" +
            JsonConvert.SerializeObject(new {
                intent = m_RoomTaskIntent, dispatch = m_RoomTaskDispatch, actionId = m_RoomTaskActionId,
                targetRevision = m_RoomTaskTargetRevision,
                taskSequence = ReadRoomTaskSequence(), recentPublicReplies = ReadRecentRoomReplies(),
                priorTaskResults = BuildMovementPriorResults(),
                historicalDeviceEvents = ReadHistoricalDeviceEvents(),
                decisionFailure = ReadRoomTaskDecisionFailure(),
                requestedDevicePlanRejected = CurrentGenerationHasRejectedDeviceRequest(m_FormalResponseGeneration),
                deviceGoalUpdate = (int?)m_SpatialGoalLatestReview?["generation"] == m_FormalResponseGeneration ?
                    PickRoomFields(m_SpatialGoalLatestReview, "relation", "reviewApplied", "cancellationApplied", "currentRequestOnly", "registered", "requestAssessment", "pendingRetained", "proposalDiscarded", "answerRequired", "responseMode") : null,
                roomFacts = ReadCompactRoomTaskFacts(bridge), sceneActionFacts = ReadRoomSceneActionFacts(),
                movementReportRequirement = RoomMovementResultPolicy.Requirement(ReadCompactRoomTaskFacts(bridge), m_RoomTaskIntent, m_RoomTaskActionId),
                spatialReplyFacts = BuildSpatialAnswerFacts(GetFormalSceneObservation(m_RoomTaskGeneration)),
                sceneSearchReportRequirement = BuildRoomSearchReportRequirement(),
                turnSweep = ReadRoomSweepReplyFacts(GetFormalSceneObservation(m_RoomTaskGeneration))
            }) +
            "\n本轮的房间动作已由上述步骤决定，普通台词不再提交任何approach/follow/stop-moving。" +
            "intent=none表示本轮没有提交新空间动作；requestedDevicePlanRejected=false且replyScope=spatial时先具体回答本轮关于已见物件、方位、记忆或动作原因的问题。依据spatialReplyFacts区分历史与当前、身体与视线、已知与未知，不以找找看/转回来等新动作承诺代替答案。普通非空间话题继续真实话题；inspect只查询用户接近路线/空间执行状态，不代表已看过另一个方向。" +
            "其他空间intent本轮只生成简短自然台词，不输出任何工具标签；不得以挥手/摇头替换走近。" +
            "search-scene的总结果由sceneActionFacts.sceneSearch决定，lastAction只代表最近子步。found才说明当前目标匹配并重新看见；completed只说明环顾任务完成；incomplete/blocked不证明目标不存在。物件model-described描述仍需目标核验，历史位置不等于当前可见，找到设备不能说已开关。" +
            "look-at/approach-object/reposition-view/orient-view/turn-in-place/inspect-object/set-object-state可在自主轮保持silent而不发声；以sceneActionFacts解释实际视觉动作，不能说成已走到用户身边。转向取得新图才是获得了新视野，不证明未知方向的猜测。objectContext是被关注物件的带时间快照，场景标注不是视觉辨认，未观察对象不能编造。设备开关只改变登记发光体及Light，不是伸手按开关，烘焙照明可能保留。state-applied-unconfirmed只证明状态已执行但新视觉尚未确认。" +
            "reposition-view是保持面向已确认目标的小范围观察站位调整。viewDirection和actualViewOffset说明请求方向与实际位移；arrived只证明移动到位并重认目标，不证明看清文字或整个物件完整入镜。结合动作后的当前图片与viewBefore/viewAfter可见像素证据评价看全/读细节/避遮挡目的；图像不足时如实保留不确定，不把后退改说成靠近。" +
            "attempt-submitted只是已尝试；先读当前阶段，拒绝不能说已完成。not-submitted是尚未尝试，不引用旧失败冒充本次结果。" +
            "decisionFailure只证明这次规划未能提交新动作，不证明用户要求了动作或普通问答不能回答。用户确实请求动作时如实说明失败和未开始；若本轮只是看图、记忆、方位或一般聊天，直接回答有证据的内容，不用规划失败代替答案。不把故障说成用户没说清，不推断路线不可达、不编造原因、不承诺马上过去或已经重试。先前动作仍只按各自事实说明。" +
            "requestedDevicePlanRejected=true表示用户确实提出设备操作，但本轮没有通过审核的可执行解释；这仍是动作请求，不是方位问题。若没有派发新动作，就简短说明这次未得到可执行计划、尚未开始；不说用户没说清或没授权，不声称已经搜索、已完成或承诺重试。有实际派发动作则仍按其独立receipt说明。" +
            "requestedDevicePlanRejected=false且deviceGoalUpdate.answerRequired=true时先具体回答本轮的状态、方向、记忆或原因问题，使用已知goal/sceneActionFacts/当前视觉，不能以再次索要身份或动作承诺代替答案；没有证据的部分明确不确定。responseMode=answer-then-resume只表示回答结束后程序可能按仍有效所有权续行，不能预报已完成。proposalDiscarded=true时错误提案不属于用户要求，回到本轮真实话题；requestAssessment=uncertain只表示审核不确定，不能说用户没有授权，也不要把提案对象当事实。pendingRetained=true才有真实待澄清草稿。" +
            "优先回应本轮完整用户要求及当前动作事实，不让旧设备草稿盖过本轮寻找或转向。deviceGoalUpdate.cancellationApplied=true才证明取消已生效；relation只是模型分类。未开始搜索不能说找过没找到，审核未通过不能说成用户没有授权。" +
            "approach=你靠近用户，用户自己往后走不等于你后退。台词方向、是否尝试和结果必须一致。" +
            "follow是持续跟随：follow-waiting是在用户身边等待其移动，仍未停止跟随；follow-paused是路径受阻暂时等待新位置，不是已完成。" +
            "controlEntryPoints说明当前已接入的口头与场景动作能力。超时或本次站位失败不能推断为只能由按钮移动；preparing还不是正在走，失败原因只描述实际记录的阶段。" +
            "不要把候选空间检查推测成用户坐姿或主观心理；发声格式故障不能解释为走神。";
    }

    private JObject ReadRoomSweepReplyFacts(SceneVisionObservation current)
    {
        var sweep = BuildSceneSweepContext(current);
        var samples = new JArray();
        if (sweep["frames"] is JArray frames)
            foreach (var frame in frames.OfType<JObject>())
                samples.Add(new JObject {
                    ["captureId"] = frame["visualObservation"]?["captureId"]?.DeepClone(),
                    ["capturedAtUtc"] = frame["visualObservation"]?["capturedAtUtc"]?.DeepClone(),
                    ["knownEntitiesObservedInThisFrame"] = frame["knownEntitiesObservedInThisFrame"] is JArray known ?
                        new JArray(known.OfType<JObject>().Select(item => PickRoomFields(item, "entityId", "captureId", "observedUtc"))) : new JArray()
                });
        return new JObject {
            ["available"] = sweep["available"]?.DeepClone(), ["actionId"] = sweep["actionId"]?.DeepClone(),
            ["currentCaptureId"] = current?.CaptureId, ["historicalSamples"] = samples,
            ["scope"] = "Bounded actual turn-sweep evidence for the spoken result only. An entity seen in a historical sample may be outside the current picture. No current visibility, device state, task success or further action is inferred."
        };
    }

    private bool ShouldGuardRoomSpeech(int generation) => m_RoomTaskGeneration == generation &&
        (m_RoomTaskIntent != "none" || m_RoomTaskSpatialAnswer || UseFocusedReplyPacket(generation) || CurrentUserTurnRequiresIndependentSpatialRequest() ||
         CurrentGenerationRequiresReviewedGoalAnswer(generation));

    private JObject BuildRoomSearchReportRequirement() => m_RoomTaskIntent == "search-scene"
        ? RoomTaskProtocol.SceneSearchReportRequirement(BuildSceneSearchContext(), m_FormalResponseGeneration)
        : new JObject { ["required"] = false };

    private JObject BuildSpatialAnswerFacts(SceneVisionObservation observation) => new JObject {
        ["replyScope"] = m_RoomTaskSpatialAnswer ? "spatial" : "action-result",
        ["intent"] = m_RoomTaskIntent, ["dispatch"] = m_RoomTaskDispatch, ["actionId"] = m_RoomTaskActionId,
        ["decisionFailure"] = ReadRoomTaskDecisionFailure(),
        ["newSpatialActionSubmitted"] = !string.IsNullOrEmpty(m_RoomTaskActionId) &&
            !m_RoomTaskDispatch.StartsWith("not-submitted", StringComparison.Ordinal),
        ["searchAttempted"] = (bool?)BuildSceneSearchContext()["searchAttempted"] == true,
        ["sceneSpatialMemory"] = BuildSceneSpatialMemory(observation),
        ["spatialEvidenceIncludedInVisualContext"] = true,
        ["scope"] = "Respond to the actual user request from source-linked facts; an action request is not automatically a direction question. Current body and camera bearings are different frames; old directions must be recomputed from frozen world points. Authored names and earlier descriptions are retrieval hints, not current identity proof. No submitted action means no new turn, approach or search may be announced. Unknown direction stays unknown; do not infer furniture locations from room conventions." };

    private JObject BuildIndependentSpatialReplyFacts()
    {
        if (!CurrentUserTurnRequiresIndependentSpatialRequest() && !((int?)m_SpatialGoalLatestReview?["generation"] == m_FormalResponseGeneration &&
            (bool?)m_SpatialGoalLatestReview?["answerRequired"] == true)) return null;
        return new JObject {
            ["currentRequestOnly"] = true,
            ["requestedDevicePlanRejected"] = CurrentGenerationHasRejectedDeviceRequest(m_FormalResponseGeneration),
            ["latestGoalReview"] = (int?)m_SpatialGoalLatestReview?["generation"] == m_FormalResponseGeneration ?
                PickRoomFields(m_SpatialGoalLatestReview, "reviewApplied", "cancellationApplied", "relation", "registered", "requestAssessment", "pendingRetained", "proposalDiscarded", "answerRequired", "responseMode") : null,
            ["pendingDeviceRequest"] = PickRoomFields(BuildPendingDeviceClarificationContext(), "pendingId", "status", "registered"),
            ["currentRequestSearchAttempted"] = m_RoomTaskIntent == "none" ? new JValue(false) : JValue.CreateNull(),
            ["scope"] = "Facts for the current independent request. requestedDevicePlanRejected=true means the user did request a device operation but no proposed interpretation passed review; it does not turn their command into a question. Explain that no executable plan was obtained, without claiming the user was unclear, forgot to authorize, or that a search/action was completed. Only cancellationApplied=true proves cancellation; a raw relation is not applied state. intent=none means no search or movement was submitted this turn, not that a search failed. For a submitted action use this turn's separate sceneActionFacts/actionId; these pending-device facts must not replace its actual result. A reviewer rejection does not mean the human withheld authorization."
        };
    }

    private string BuildFormalResponseRequestContext(string observationContext, int generation, bool roomTaskPrepared)
    {
        if (!roomTaskPrepared || !ShouldGuardRoomSpeech(generation))
            return BuildMotionRequestContext(observationContext);
        return BuildCommittedSpatialReplyContext(observationContext, generation);
    }

    private JObject BuildCommittedReplyHumanInput(int generation)
    {
        bool bound = generation == m_FormalResponseGeneration && !m_AgentCurrentRoundIsTick &&
            string.Equals(m_LastUserMsg, m_ActionConstraints.CurrentUserText, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(m_LastUserMsg);
        return new JObject {
            ["generation"] = generation, ["workEpoch"] = m_WorkEpoch, ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
            ["currentHumanTurnPresent"] = bound,
            ["primaryUtterance"] = bound ? m_LastUserMsg : null,
            ["fullInputEvidence"] = bound ? m_LastUserEvidence : null,
            ["scope"] = "Typed accepted user-turn data, serialized for this reply owner. primaryUtterance is the accepted transcript; fullInputEvidence retains all acoustic measurements, time-ordered ASR and alternatives. Measurements do not turn spoken words into a different request. Neither field is a new utterance, an action command from this writer, or permission to retry. Absent current input on an autonomous/result turn must not promote historical commands."
        };
    }

    private string BuildCommittedSpatialReplyContext(string observationContext, int generation)
    {
        // Evidence-only questions share the closed writer with selected spatial results.
        // Neither kind of public reply can create an action; the route-specific facts retain
        // whether any planning or execution actually happened.
        var evidenceReply = ReadRoomEvidenceReplyFacts(generation);
        bool evidenceOnly = evidenceReply != null;
        if (UseWorkResultReplyPacket(generation)) return BuildWorkResultReplyContext(generation);
        if (UseFocusedReplyPacket(generation)) return BuildFocusedReplyContext(generation);
        if (IsPlanningFailureOnlyReply(generation))
            return CommittedSpatialReplyProtocol.AppendReplyFacts("", new JObject {
                ["planningFailureOnly"] = true,
                ["humanInput"] = BuildCommittedReplyHumanInput(generation),
                ["currentRequestSemantics"] = ReadCompactCurrentRequestSemantics(generation),
                ["decisionFailure"] = ReadRoomTaskDecisionFailure(),
                ["concurrentBodyFailure"] = ReadConcurrentBodyFailure(generation),
                ["scope"] = "Report only this decision's recorded failure and that it submitted no new action. No scene, navigation or device failure is implied; previous actions retain their own outcomes. Do not promise a retry."
            });
        string result = (observationContext ?? "") +
            (evidenceOnly
                ? "\n[本轮只读问题的正式回答]\n根据完整本轮原话和此前实际交付的对话回答当前问题；本路线没有新增动作规划或执行。询问此前说法、状态或故障时，使用对应历史回执；询问所见时，使用当前图像。声学标签、重复ASR备选、附图和旧任务不替代本轮问题。"
                : "\n[本轮任务结果的正式回应]\n依据本轮完整用户要求、角色提示词及带动作标识的实际结果自然回应。动作是否提交、执行或失败只以本轮对应事实为准；有图片不表示用户改为询问画面。") +
            "这里不再输出motion或其他工具标签，也不另选上身动作替代它。" +
            "保留是否已提交、正在准备/移动、实际到达、拒绝、已重认和仍未知的区别。可沉默观察；不得从请求或旧结果推断已完成。";
        var failure = ReadRoomTaskDecisionFailure();
        if (failure != null)
            result += "\n[本次动作规划失败事实]\n" + failure.ToString(Formatting.None) +
                "\n这不是对用户意图的分类。若本轮只是询问已有视觉、记忆、方位或一般聊天，直接回答有证据的内容，不用动作失败代替答案。若用户确实要求执行动作，说明这次规划失败及未启动新动作；input-budget-exceeded表示输入在本地超出处理预算，未发送请求，不能说成服务拒绝或网络故障。不要把理解意图说成动作已开始，不把请求失败归咎于用户表达，不编造等待原因。";
        result = CommittedSpatialReplyProtocol.AppendReplyFacts(result, new JObject {
            ["humanInput"] = BuildCommittedReplyHumanInput(generation),
            ["currentRequestSemantics"] = ReadCompactCurrentRequestSemantics(generation),
            ["readOnlyEvidenceReply"] = evidenceReply?.DeepClone(),
            ["attentionState"] = ReadAttentionState("writer"),
            ["decisionFailure"] = failure?.DeepClone(),
            ["currentActionReceipt"] = BuildCurrentReplyActionReceipt(),
            ["previousReplyFailureReceipt"] = ReadPreviousReplyFailureReceipt(),
            ["scope"] = "Current generation's answer route and exact diagnostic receipts. Historical failures remain historical, not a failure of this answer or new action authority. Choose only the facts relevant to the complete current question; empty diagnostics do not establish success."
        });
        Debug.Log($"[Room/ReplyContext] generation={generation} intent={m_RoomTaskIntent} mode=committed-spatial-description characters={result.Length} independentMotionMenu=false", this);
        return result;
    }

    private JObject BuildCurrentReplyActionReceipt()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var room = ReadCompactRoomTaskFacts(bridge);
        var requirement = RoomMovementResultPolicy.Requirement(room, m_RoomTaskIntent, m_RoomTaskActionId);
        string statement = RoomMovementResultPolicy.Fallback(room, m_RoomTaskIntent, m_RoomTaskActionId);
        if (m_RoomTaskIntent == "stop-moving" && m_RoomTaskDispatch == "stop-submitted" &&
            bridge != null && bridge.ActionId == m_RoomTaskActionId && !bridge.ReservesBody && !bridge.IsFollowing)
            statement = "我已经停止移动了。";
        if ((string)requirement["expectedReport"] == "arrived")
            statement = "我已经走到你身边了。";
        // This freshly built reporting view already includes execution below.
        // Do not repeat its full receipt or an inapplicable movement contract.
        requirement.Remove("execution");
        if ((bool?)requirement["required"] == false)
            requirement = new JObject { ["required"] = false, ["scope"] = "No movement report is owed by this reply; historical execution below remains valid." };
        return new JObject {
            ["intent"] = m_RoomTaskIntent, ["dispatch"] = m_RoomTaskDispatch, ["requestedActionId"] = m_RoomTaskActionId,
            ["execution"] = room["execution"]?.DeepClone(), ["observedStateStatement"] = statement,
            ["movementReportRequirement"] = requirement,
            ["scope"] = "Latest engine receipt for THIS reply. The execution action ID and matchesRequestedAction distinguish older work. Preserve observedStateStatement when describing this movement, translating naturally into the reply language: preparing is not moving/waiting, recovery is not normal following, waiting nearby is not starting a new walk. Neither prior dialogue nor images override this phase. previousReplyReceipt is historical; a reply failure is not a planning failure and does not establish failure of this action. No new or repeated action is authorized." };
    }

    private bool IsPlanningFailureOnlyReply(int generation)
    {
        // Mixed requests still need their question answered from relevant evidence.
        string kind = (string)ReadCompactCurrentRequestSemantics(generation)?["requestKind"];
        return generation == m_FormalResponseGeneration && (kind == "action" || kind == "control") &&
            ReadRoomEvidenceReplyFacts(generation) == null && (string.IsNullOrEmpty(m_RoomTaskActionId) ||
                (bool?)ReadRoomTaskDecisionFailure()?["remainingAfterCommittedStop"] == true) &&
            (bool?)ReadRoomTaskDecisionFailure()?["newActionSubmittedByThisDecision"] == false;
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
        string candidate = buffer.ToString();
        bool consumed = false;
        int reviewRevision = 0;
        int repairAttempts = 0;
        string repairLanguage = null;
        bool repairPending = false;
        string reviewedFactScope = "legacy";
        string reviewedRequestDisposition = "uncertain";
        string reviewedPlanningFailureReport = "uncertain";
        string reviewedIndependentSpeech = null;
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
            // A failure is authoritative about the attempted plan, not about
            // whether the user asked for an action. Preserve independently
            // reviewed answer-only claims; an action/mixed/unknown request still
            // receives the exact failure receipt rather than an invented cause.
            bool reviewedAnswerOnly = !CurrentRequestRequiresActionOrControl(generation) &&
                coherent && reviewedRequestDisposition == "answer-only" &&
                (reviewedPlanningFailureReport == "not-mentioned" ||
                 reviewedPlanningFailureReport == (string)ReadRoomTaskDecisionFailure()?["kind"]);
            bool diagnosedPlanningFailure = !reviewedAnswerOnly &&
                (string.IsNullOrEmpty(m_RoomTaskActionId) || (bool?)ReadRoomTaskDecisionFailure()?["remainingAfterCommittedStop"] == true) &&
                (bool?)ReadRoomTaskDecisionFailure()?["newActionSubmittedByThisDecision"] == false;
            if (diagnosedPlanningFailure && (!coherent || ReadConcurrentBodyFailure(generation) != null ||
                reviewedPlanningFailureReport != (string)ReadRoomTaskDecisionFailure()?["kind"]))
            {
                coherent = false;
                reviewStatus = "diagnosed-planning-failure-exact-report:" + reviewStatus;
            }
            if (!diagnosedPlanningFailure && generation == m_SpatialGoalTerminalReplyGeneration && !ValidateSpatialGoalTerminalProof(generation))
            {
                // Preserve the proof used to generate this candidate across both
                // generation and review latency. A freshly captured review alone
                // must not revive a candidate based on an invalidated completion.
                coherent = false; reviewStatus = "device-goal-proof-changed-before-release";
            }
            consumed = true;
            ReleaseOwner();
            bool terminalResult = !diagnosedPlanningFailure && generation == m_SpatialGoalTerminalReplyGeneration;
            string language = repairLanguage ?? (terminalResult ? SpatialGoalReportLanguage(buffer.LanguageCode) :
                coherent ? buffer.LanguageCode : SpatialGoalReportLanguage(buffer.LanguageCode));
            bool quietFallback = !diagnosedPlanningFailure && !terminalResult && !coherent && !UseMovementReplyPacket(generation) && m_AgentCurrentRoundIsTick && !IsWorkSceneFeedbackGuard(generation) &&
                (bool?)BuildRoomSearchReportRequirement()["required"] != true;
            string fallback = RoomTaskFallback(bridge == GetComponent<ArdyRoomDialogueBridge>() ? bridge : null);
            if (UseWorkResultReplyPacket(generation))
            {
                var outcome = BuildWorkRoomCompletionObservation();
                fallback = RoomMovementResultPolicy.Fallback(outcome?["currentRoomFacts"] as JObject,
                    (string)outcome?["intent"], (string)outcome?["actionId"]) ?? fallback;
            }
            if (terminalResult)
                fallback = BuildSpatialGoalReviewFallback(language);
            else if (!diagnosedPlanningFailure && (bool?)BuildRoomSearchReportRequirement()["required"] == true)
                fallback = BuildRoomSearchFallback();
            if (!diagnosedPlanningFailure && !terminalResult && m_RoomTaskIntent == "inspect" && (reviewedFactScope == "visual-observation" || reviewedFactScope == "none"))
                fallback = "我还没有确认当前画面中的情况。";
            string text = quietFallback ? "<silent/>" : coherent ? reviewedIndependentSpeech ?? candidate : terminalResult ? fallback :
                !diagnosedPlanningFailure && CurrentGenerationRequiresReviewedGoalAnswer(generation) ? ReviewedGoalAnswerFallback(language) : LocalizeRoomFallback(fallback, language);
            if (!coherent && !diagnosedPlanningFailure && !terminalResult && UseMovementReplyPacket(generation) &&
                bridge.ActionId == m_RoomTaskActionId && bridge.Phase == "arrived")
                text = MovementProgressText(ReadCompactRoomTaskFacts(bridge), m_RoomTaskIntent, m_RoomTaskActionId, language, false) ?? text;
            if (diagnosedPlanningFailure && !coherent)
                text += ConcurrentBodyFailureNotice(ReadConcurrentBodyFailure(generation), language);
            if (!coherent && !quietFallback && CurrentFormalGenerationFailed(generation))
                text += (language == "en" ? " " : "") + FormalGenerationFailureNotice(language);
            if (!quietFallback) text = AppendRoomRequestProgress(text, language, generation);
            RecordFinalRoomReply(generation, text, reviewStatus, bridge);
            // Only the audited public reply enters history and the action parser.
            // Raw candidates remain diagnostics; no discarded promises become memory.
            var historyReceipt = recordHistory ? new LLM.SendData("assistant", text) : null;
            if (historyReceipt != null) model.m_DataList.Add(historyReceipt);
            BindRoomSpeechPlayback(generation, language, bridge, model, historyReceipt);
            BindReviewedGoalAnswerSpeech(generation, text);
            if (quietFallback) OnStreamDelta("<silent/>");
            else if (coherent && text == candidate)
                foreach (var part in buffer.Snapshot()) OnSpeechStreamDelta(part);
            else OnSpeechStreamDelta(new SpeechText(text, language, "room-task-fallback", SpeechActionDependency.Independent));
            // A diagnostic fallback may explain a failed body step without
            // delivering other pending tool answers. Keep those results unread.
            if (coherent || !UseReceiptOnlySpeechReview(generation)) AcknowledgeObservations(observations, generation, text);
            m_FormalResponseInFlight = false;
            Debug.Log($"[Room/SpeechReview] generation={generation} action={m_RoomTaskActionId} releasedCandidate={coherent} repairAttempts={repairAttempts} status={reviewStatus}", this);
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
                {
                    // The owner is still Fresh here; never cancel a replacement
                    // turn. Stop an abandoned repair's HTTP stream as well as
                    // rejecting its late callbacks so it cannot occupy a slot.
                    if (repairPending) { repairPending = false; ++reviewRevision; model.CancelActiveResponse(); }
                    model.CancelRoomTaskPlanningMessage(); // Also stop a pending words-only or factual review HTTP request.
                    Finish(false, "review-deadline"); yield break;
                }
                yield return null;
            }
        }
        if (IsPlanningFailureOnlyReply(generation))
        { Finish(false, "local-planning-failure-receipt"); return; }
        if (generation != m_SpatialGoalTerminalReplyGeneration && !UseWorkResultReplyPacket(generation) && m_AgentCurrentRoundIsTick && IsSceneVisualIntent(m_RoomTaskIntent) &&
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
        bool TryRepairEvidenceReply(JObject verdict, string guardRejectedReason)
        {
            // A validated semantic rejection can repair an answer, never a plan.
            // Consume the single allowance before capture/provider dispatch, so
            // duplicate reviewer callbacks cannot start another continuation.
            bool languageRepair = guardRejectedReason != null && guardRejectedReason.StartsWith("speech-language-", StringComparison.Ordinal);
            bool evidenceRepair = ReadRoomEvidenceReplyFacts(generation) != null && m_RoomTaskDispatch == "read-only-evidence" &&
                string.IsNullOrEmpty(m_RoomTaskActionId) && generation != m_SpatialGoalTerminalReplyGeneration;
            bool movementRepair = (m_RoomTaskIntent == "approach" || m_RoomTaskIntent == "follow" || UseWorkResultReplyPacket(generation)) &&
                (bool?)CurrentSpeechMovementRequirement(generation)?["required"] == true;
            bool terminalRepairAllowed = generation == m_SpatialGoalTerminalReplyGeneration && ValidateSpatialGoalTerminalProof(generation);
            bool autonomousRepair = ReadAutonomousReplyPurpose(generation) != null;
            if (repairAttempts != 0 || ((string)verdict["verdict"] != "inconsistent" && string.IsNullOrEmpty(guardRejectedReason)) ||
                (!languageRepair && !evidenceRepair && !movementRepair && !terminalRepairAllowed && !autonomousRepair) || CurrentFormalGenerationFailed(generation))
                return false;
            // A state correction is not a reason to switch conversation language.
            repairLanguage = SpatialGoalReportLanguage(buffer.LanguageCode);
            ++repairAttempts;
            int repairRevision = ++reviewRevision;
            string rejectedCandidate = candidate;
            string rejectedLanguage = buffer.LanguageCode;
            bool dispatched = false, completed = false;
            Debug.Log($"[Room/SpeechRepair] generation={generation} attempt={repairAttempts} state=preparing reason={guardRejectedReason ?? "semantic-review-inconsistent"}", this);
            void SendRepair(SceneVisionObservation observation)
            {
                if (dispatched || consumed || repairRevision != reviewRevision || !Fresh()) return;
                dispatched = true;
                if (!SceneEyesOpen || !IsFreshSceneObservation(observation)) observation = null;
                BindFormalSceneObservation(observation, generation);
                // Rebuild the same read-only evidence context with the newly
                // bound view. RequestContext was consumed by the initial writer;
                // copying it would also risk presenting its old image as current.
                string context;
                bool terminalRepair = generation == m_SpatialGoalTerminalReplyGeneration;
                if (terminalRepair)
                {
                    if (!ValidateSpatialGoalTerminalProof(generation)) { Finish(false, "device-goal-proof-changed-before-repair"); return; }
                    var facts = BuildSpatialGoalExecutionFacts();
                    facts["taskSequence"] = ReadRoomTaskSequence();
                    context = SpatialGoalResultContextPolicy.BuildContext((string)facts.SelectToken("goal.originalRequest") ??
                        m_ActionConstraints.CurrentUserText, facts.ToString(Formatting.None));
                }
                else if (UseReceiptOnlySpeechReview(generation)) context = BuildCommittedSpatialReplyContext("", generation);
                else context = BuildCommittedSpatialReplyContext(BuildFormalObservationContext(evidenceRepair
                    ? BuildReadOnlyReplyEvidenceContext(observation, generation) : BuildRoomTaskReplyFacts()), generation);
                // A movement sentence cannot replace an independent answer or a
                // compound question. Only an outcome-only producer supplies it.
                string factualBaseline = terminalRepair || UseReceiptOnlySpeechReview(generation)
                    ? CurrentReplyRepairBaseline(context, repairLanguage) : null;
                string feedback = terminalRepair
                    ? SpatialGoalResultContextPolicy.BuildRepairFeedback(rejectedCandidate, verdict, guardRejectedReason, repairLanguage, factualBaseline)
                    : CommittedSpatialReplyProtocol.BuildReviewRepairFeedback(rejectedCandidate, verdict, guardRejectedReason,
                        repairLanguage, BuildCommittedReplyHumanInput(generation), context, UseReceiptOnlySpeechReview(generation), factualBaseline);
                var repaired = new SpeechTextBuffer();
                Debug.Log($"[Room/SpeechRepair] generation={generation} attempt={repairAttempts} state=dispatched capture={observation?.CaptureId ?? "none"} historyRecorded=false", this);
                try
                {
                    repairPending = true;
                    model.PostSpeechFeedbackStreamForStage(context, feedback, part => {
                        if (!completed && !consumed && repairRevision == reviewRevision && Fresh()) repaired.Append(part);
                    }, full => {
                        if (completed || consumed || repairRevision != reviewRevision || !Fresh()) return;
                        completed = true;
                        repairPending = false;
                        if (!RoomTaskProtocol.IsPlainSpeech(repaired.ToString()) || m_RoomTaskFormatError ||
                            (repairLanguage != null && repaired.LanguageCode != repairLanguage) ||
                            repaired.Snapshot().Any(part => part.ActionDependency == SpeechActionDependency.AfterAction))
                        { Finish(false, "evidence-repair-invalid-or-failed"); return; }
                        if (repaired.LanguageCode == rejectedLanguage && string.Equals(repaired.ToString().Trim(), rejectedCandidate.Trim(), StringComparison.Ordinal))
                        {
                            Debug.LogWarning($"[Room/SpeechRepair] generation={generation} attempt={repairAttempts} state=no-progress reason=identical-rejected-speech", this);
                            Finish(false, "speech-repair-no-progress");
                            return;
                        }
                        buffer = repaired; candidate = repaired.ToString(); candidateExecutable = full;
                        reviewedFactScope = "legacy"; reviewedRequestDisposition = "uncertain";
                        reviewedPlanningFailureReport = "uncertain";
                        Debug.Log($"[Room/SpeechRepair] generation={generation} attempt={repairAttempts} state=awaiting-review", this);
                        PrepareReview(0);
                    }, observation?.HasImage == true ? observation.ImageDataUrl : null, false,
                        terminalRepair ? FormalResponseStage.SpatialGoalResult : FormalResponseStage.CommittedSpatialReply);
                }
                catch (Exception error)
                {
                    repairPending = false;
                    Debug.LogWarning("[Room/SpeechRepair] request failed: " + error.Message, this);
                    Finish(false, "evidence-repair-request-failed");
                }
            }
            if (UseReceiptOnlySpeechReview(generation) || !SceneEyesOpen) SendRepair(null);
            else if (TryGetPreparedRoomSpeechObservation(generation, out var ready, out _)) SendRepair(ready);
            else RequestSceneObservation("evidence-speech-repair", SendRepair);
            return true;
        }
        void SubmitReview(int attempt, SceneVisionObservation observation, string observationSource)
        {
            if (consumed || !Fresh()) return;
            // Preparation and submission are synchronous. A callback must never
            // get one unchecked chance to send a known expired image.
            if (!UseReceiptOnlySpeechReview(generation) && SceneEyesOpen && !IsFreshSceneObservation(observation))
            {
                if (ReadRoomEvidenceReplyFacts(generation) == null)
                { Finish(false, "visual-review-preparation-unavailable"); return; }
                observation = null; // A historical/status answer may not need pixels; never pass an expired frame as current.
            }
            var snapshot = CaptureRoomSpeechSnapshot(bridge, observation);
            bool movementPacket = UseFocusedReplyPacket(generation);
            bool workResultPacket = UseWorkResultReplyPacket(generation);
            bool terminalPacket = UseTerminalResultReviewPacket(generation);
            var progressOwner = BuildRoomSpeechProgressOwner(generation);
            reviewedIndependentSpeech = null;
            Debug.Log($"[Room/SpeechReviewView] generation={generation} attempt={attempt} source={observationSource} capture={observation?.CaptureId ?? "none"} age={(observation == null ? -1f : Time.realtimeSinceStartup - observation.CapturedAtRealtime):F3}s", this);
            string input = JsonConvert.SerializeObject(new {
                intendedAction = m_RoomTaskIntent, dispatch = m_RoomTaskDispatch, actionId = m_RoomTaskActionId,
                decisionFailure = ReadRoomTaskDecisionFailure(),
                humanInput = BuildCommittedReplyHumanInput(generation),
                currentRequestSemantics = workResultPacket || movementPacket ? ReadMovementReplySemantics(generation) : ReadCompactCurrentRequestSemantics(generation),
                replyPurpose = movementPacket ? ReadAutonomousReplyPurpose(generation) : null,
                attentionState = ReadAttentionState("reviewer"),
                interactionTime = movementPacket ? BuildAttentionInteractionTime() : null,
                pendingResults = movementPacket ? CreateObservationReceipt()?.Facts : null,
                configuredDialogueLanguage = model.DialogueLanguage,
                readOnlyEvidenceReply = ReadRoomEvidenceReplyFacts(generation),
                previousReplyFailureReceipt = ReadPreviousReplyFailureReceipt(),
                publicSpeech = candidate, publicSpeechLanguage = buffer.LanguageCode,
                speechClaimOwner = new { instance = GetInstanceID(), generation, epoch, ownerRevision },
                requestedUserText = m_ActionConstraints.CurrentUserText,
                requestedUserTextIsHistorical = movementPacket && ReadAutonomousReplyPurpose(generation) != null,
                recentDialogue = ReadAutonomousReplyPurpose(generation) != null ? JArray.Parse(RecentRoomDialogue()) : null,
                recentPublicReplies = terminalPacket ? null : ReadRecentRoomReplies(),
                taskSequence = ReadAutonomousReplyPurpose(generation) == null ? ReadRoomTaskSequence() : null,
                speechProgressOwner = progressOwner,
                declaredMotion = ExtractRoomReviewMotion(candidateExecutable),
                historicalDeviceEvents = ReadHistoricalDeviceEvents(),
                eventContext = terminalPacket ? null : BuildReplyEventContext(generation),
                currentRoomFacts = generation == m_SpatialGoalTerminalReplyGeneration ?
                    PickRoomFields(ReadCompactRoomTaskFacts(bridge)["currentFacts"] as JObject,
                        "connected", "bodyReserved", "activeActionId", "observedAtRealtime", "actualRoot") : ReadCompactRoomTaskFacts(bridge),
                movementReportRequirement = CurrentSpeechMovementRequirement(generation),
                workResultFacts = workResultPacket ? BuildWorkResultReplyFacts(generation) : null,
                sceneActionFacts = movementPacket || workResultPacket ? BuildMovementPriorResults() : generation == m_SpatialGoalTerminalReplyGeneration ?
                    (terminalPacket ? SpatialGoalResultContextPolicy.ProjectResultFacts(BuildSpatialGoalExecutionFacts()) : BuildSpatialGoalExecutionFacts()) : BuildSceneActionFacts(),
                spatialReplyFacts = movementPacket || terminalPacket || workResultPacket ? null : BuildSpatialAnswerFacts(observation),
                sceneSearchReportRequirement = movementPacket || terminalPacket || workResultPacket ? null : BuildRoomSearchReportRequirement(),
                deviceGoalFacts = movementPacket || terminalPacket || workResultPacket ? null : BuildIndependentSpatialReplyFacts(),
                turnSweep = movementPacket || terminalPacket || workResultPacket ? null : ReadRoomSweepReplyFacts(observation),
                visualObservation = observation != null ? JObject.Parse(observation.MetadataJson) : null,
                visualEvidence = workResultPacket || movementPacket ? null : terminalPacket ? BuildWorkReviewObservationContext(observation) : BuildSceneObservationContext(observation),
                reviewAttempt = attempt, repairAttempts,
                reviewAttention = movementPacket ? "owned-body-receipt-and-reply-purpose" : terminalPacket ? "owned-device-result-and-current-request" : "full-required-evidence",
                scope = "Classify dependencies of the actual public claim. A user route assessment is not a verdict on image contents or another scene target." +
                    (terminalPacket ? " publicSpeech is the device-result segment. The runtime appends other requested steps' current progress from owned execution receipts at release/playback. This segment's device success never establishes completion of the whole task. Independent questions still require their answers." : "")
            });
            int revision = ++reviewRevision;
            Action<bool, string, string> OnReview = (ok, output, status) => {
                if (consumed || revision != reviewRevision || !Fresh()) return;
                if (!ok) { Finish(false, status); return; }
                if (!RoomTaskProtocol.TryReviewScope(output, out string factScope))
                {
                    string rejected = output ?? "";
                    Debug.LogWarning("[Room/SpeechReviewValidation] " + new JObject {
                        ["generation"] = generation, ["actionId"] = m_RoomTaskActionId,
                        ["reason"] = "invalid-review-json", ["wireStatus"] = status,
                        ["candidate"] = rejected.Substring(0, Math.Min(600, rejected.Length)),
                        ["candidateTruncated"] = rejected.Length > 600
                    }.ToString(Formatting.None), this);
                    Finish(false, "invalid-review-json"); return;
                }
                reviewedFactScope = factScope;
                var dependencies = ResolveRoomSpeechDependencies(m_RoomTaskIntent, factScope);
                var changed = ChangedRoomSpeechFacts(snapshot, dependencies);
                var verdict = JObject.Parse(output);
                if (progressOwner != null && RoomTaskProtocol.ReviewAllows(output) && !RoomSpeechPartsProtocol.TryRead(verdict, candidate,
                    out reviewedIndependentSpeech, out string partsFailure))
                { Finish(false, partsFailure); return; }
                reviewedRequestDisposition = (string)verdict["requestDisposition"] ?? "uncertain";
                if (CurrentRequestRequiresActionOrControl(generation)) reviewedRequestDisposition = "action-request";
                reviewedPlanningFailureReport = (string)verdict["planningFailureReport"] ?? "uncertain";
                string guardRejectedReason = status == "speech-claim-language-mismatch" ? "speech-language-mismatch" :
                    RoomTaskProtocol.ReviewPlanningFailureRejection(output, ReadRoomTaskDecisionFailure(), ReadRoomEvidenceReplyFacts(generation));
                guardRejectedReason = guardRejectedReason ?? RoomTaskProtocol.ReviewLanguageRejection(verdict, buffer.LanguageCode);
                if (guardRejectedReason == null && !RoomMovementResultPolicy.ReviewReports(output,
                    CurrentSpeechMovementRequirement(generation)))
                    guardRejectedReason = "speech-movement-state-mismatch";
                Debug.Log("[Room/SpeechReviewVerdict] " + new JObject {
                    ["generation"] = generation, ["workEpoch"] = epoch, ["userTurn"] = userTurn,
                    ["actionId"] = m_RoomTaskActionId, ["intent"] = m_RoomTaskIntent,
                    ["attempt"] = attempt, ["verdict"] = verdict["verdict"],
                    ["reason"] = verdict["reason"], ["factScope"] = factScope,
                    ["conflict"] = verdict["conflict"], ["conflictQuote"] = verdict["conflictQuote"],
                    ["targetConsistency"] = verdict["targetConsistency"],
                    ["targetComparison"] = verdict["targetComparison"],
                    ["otherClaimsSupport"] = verdict["otherClaimsSupport"],
                    ["taskRelation"] = verdict["taskRelation"],
                    ["speechParts"] = verdict["speechParts"],
                    ["taskRelationComparison"] = verdict["taskRelationComparison"],
                    ["otherClaimsComparison"] = verdict["otherClaimsComparison"],
                    ["requestDisposition"] = reviewedRequestDisposition,
                    ["planningFailureReport"] = reviewedPlanningFailureReport,
                    ["declaredLanguage"] = buffer.LanguageCode, ["actualLanguage"] = verdict["actualLanguage"],
                    ["guardRejectedReason"] = guardRejectedReason,
                    ["resultReport"] = verdict["resultReport"],
                    ["terminalExpectedReport"] = generation == m_SpatialGoalTerminalReplyGeneration ?
                        SpatialGoalResultContextPolicy.RequiredReport(BuildSpatialGoalExecutionFacts()) : null,
                    ["captureId"] = observation?.CaptureId,
                    ["factsChanged"] = new JArray(changed), ["wireStatus"] = status,
                    ["scope"] = "Validated reviewer judgment only; current execution facts retain action authority."
                }.ToString(Formatting.None), this);
                // A words-only rejection is independent of view age or movement.
                // Repair it before spending another capture/factual-review cycle.
                if (guardRejectedReason != null && guardRejectedReason.StartsWith("speech-language-", StringComparison.Ordinal))
                {
                    if (TryRepairEvidenceReply(verdict, guardRejectedReason)) return;
                    Finish(false, guardRejectedReason); return;
                }
                if (changed.Count == 0)
                {
                    if (guardRejectedReason == "speech-movement-state-mismatch" && UseMovementReplyPacket(generation))
                    { Finish(false, "movement-state-refreshed-from-current-receipt"); return; }
                    if (TryRepairEvidenceReply(verdict, guardRejectedReason)) return;
                    bool currentFailedAttempt = string.IsNullOrEmpty(m_RoomTaskActionId) &&
                        (bool?)ReadRoomTaskDecisionFailure()?["newActionSubmittedByThisDecision"] == false;
                    Finish(string.IsNullOrEmpty(guardRejectedReason) && RoomTaskProtocol.ReviewAllows(output) &&
                        RoomMovementResultPolicy.ReviewReports(output,
                            CurrentSpeechMovementRequirement(generation)) &&
                        (currentFailedAttempt || RoomTaskProtocol.ReviewReportsSceneSearch(output, BuildRoomSearchReportRequirement())) &&
                        (currentFailedAttempt || generation != m_SpatialGoalTerminalReplyGeneration || ValidateSpatialGoalTerminalReview(output)),
                        guardRejectedReason ?? status);
                    return;
                }
                Debug.Log($"[Room/SpeechReviewFacts] generation={generation} action={m_RoomTaskActionId} intent={m_RoomTaskIntent} scope={factScope} attempt={attempt} changed={string.Join(",", changed)} userRevision={snapshot.targetRevision}->{bridge?.ReadCurrentTargetEvidence().currentTargetRevision}", this);
                if (changed.Contains("spatialGoalCompletion"))
                { Finish(false, "device-goal-proof-changed-during-review"); return; }
                if (changed.Contains("userTaskPhase") || changed.Contains("userTaskOperation"))
                {
                    if (UseMovementReplyPacket(generation)) { Finish(false, "movement-state-refreshed-from-current-receipt"); return; }
                    if (TryRepairEvidenceReply(verdict, "speech-movement-state-changed")) return;
                }
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
            if (UseWorkResultReplyPacket(generation)) { SubmitReview(attempt, null, "owned-work-result"); return; }
            if (UseFocusedReplyPacket(generation)) { SubmitReview(attempt, null, "owned-body-receipt"); return; }
            if (!SceneEyesOpen) { SubmitReview(attempt, null, "eyes-closed"); return; }
            if (TryGetPreparedRoomSpeechReviewObservation(generation, out var ready, out string source))
            {
                BindFormalSceneObservation(ready, generation);
                SubmitReview(attempt, ready, source);
                return;
            }
            int captureRevision = ++reviewRevision;
            m_PendingSceneFormalCaptureGeneration = generation;
            Debug.Log($"[Room/SpeechReviewView] generation={generation} attempt={attempt} source=capture-required", this);
            RequestSceneObservation(attempt == 0 ? "speech-review-prepare" : "speech-review-refresh", refreshed => {
                if (consumed || captureRevision != reviewRevision || !Fresh()) return;
                if (!IsFreshSceneObservation(refreshed))
                {
                    if (ReadRoomEvidenceReplyFacts(generation) != null)
                    { BindFormalSceneObservation(null, generation); SubmitReview(attempt, null, "current-image-unavailable"); }
                    else Finish(false, "visual-review-preparation-unavailable");
                    return;
                }
                BindFormalSceneObservation(refreshed, generation);
                SubmitReview(attempt, refreshed, "captured-for-review");
            });
        }
        if (Application.isPlaying) StartCoroutine(WatchReviewOwner());
        if (m_MovementReplyStateGeneration == generation && m_MovementReplyState != null &&
            !JToken.DeepEquals(m_MovementReplyState, MovementPlaybackStamp(ReadCompactRoomTaskFacts(bridge), m_RoomTaskIntent, m_RoomTaskActionId)))
        {
            if (UseMovementReplyPacket(generation)) { Finish(false, "movement-state-refreshed-before-review"); return; }
            if (TryRepairEvidenceReply(new JObject { ["verdict"] = "inconsistent", ["reason"] = "Movement state changed after drafting." },
                "speech-movement-state-changed-before-review")) return;
        }
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
        // A current failed attempt takes precedence over an older goal's answer
        // obligation. ReadRoomTaskDecisionFailure already fences generation/turn.
        var planningFailure = ReadRoomTaskDecisionFailure();
        if ((bool?)planningFailure?["remainingAfterCommittedStop"] == true)
            return (string)planningFailure["kind"] == "input-budget-exceeded"
                ? "我已经停止移动了，但后续动作的信息超出规划预算，还没有开始执行。"
                : (string)planningFailure["kind"] == "service-request-failed"
                ? "我已经停止移动了，但后续动作的规划请求失败，还没有开始执行。"
                : "我已经停止移动了，但后续没有得到可执行的动作计划，还没有开始执行。";
        if (string.IsNullOrEmpty(m_RoomTaskActionId) &&
            (bool?)planningFailure?["newActionSubmittedByThisDecision"] == false)
        {
            string kind = (string)planningFailure["kind"];
            if (kind == "input-budget-exceeded") return "这次规划要处理的信息太多，没能生成动作计划，还没有开始新的动作。";
            return kind == "service-request-failed"
                ? "这次生成动作计划的服务请求失败了，没有启动新的动作。"
                : "这次没有得到可执行的动作计划，没有启动新的动作。";
        }
        if (CurrentGenerationHasRejectedDeviceRequest(m_FormalResponseGeneration) &&
            (m_RoomTaskIntent == "none" || m_RoomTaskIntent == "unresolved") && string.IsNullOrEmpty(m_RoomTaskActionId))
            return "这次没有得到可执行的动作计划，没有启动新的动作。";
        if (CurrentGenerationRequiresReviewedGoalAnswer(m_FormalResponseGeneration)) return ReviewedGoalAnswerFallback("zh");
        if (ReadRoomEvidenceReplyFacts(m_FormalResponseGeneration) != null)
            return "这次我没能准确回答你的问题。";
        if (bridge == null || !bridge.IsConnected) return "房间移动目前没有连接，我还不能确认这次动作的结果。";
        if (m_RoomTaskIntent == "search-scene") return BuildRoomSearchFallback();
        if (m_RoomTaskIntent == "none" && m_RoomTaskSpatialAnswer)
            return "我还不能根据已有观察确认你问的空间情况，这次没有执行新的移动或搜索。";
        if (m_RoomTaskIntent == "none" && CurrentUserTurnRequiresIndependentSpatialRequest())
            return "这次还没有执行新的动作。";
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
        if (m_RoomTaskIntent == "unresolved")
        {
            string failureKind = (string)ReadRoomTaskDecisionFailure()?["kind"];
            if (failureKind == "input-budget-exceeded") return "这次规划要处理的信息太多，没能生成动作计划，还没有开始新的动作。";
            return failureKind == "service-request-failed"
                ? "这次生成动作计划的服务请求失败了，没有启动新的动作。"
                : "这次没有得到可执行的动作计划，没有启动新的动作。";
        }
        if (m_RoomTaskIntent == "inspect")
            return current.assessment.canPlanRoute ? "我重新检查了现在的位置，附近有可用路线；这次还没有开始移动。"
                : "我重新检查了现在的位置，暂时还没有确认可以走近的路线。";
        string movementState = RoomMovementResultPolicy.Fallback(ReadCompactRoomTaskFacts(bridge), m_RoomTaskIntent, m_RoomTaskActionId);
        if (movementState != null) return movementState;
        if (m_RoomTaskIntent == "follow")
        {
            if (!bridge.IsFollowing || bridge.ActiveTaskId != m_RoomTaskActionId)
                return "这次跟随尚未继续执行，需要确认当前任务状态。";
            return "跟随任务仍然开启，但当前行走状态尚未确认。";
        }
        if (bridge.ActionId == m_RoomTaskActionId && bridge.Phase == "arrived" && current.currentlyNearAndFacingUser)
            return "我已经走到你身边了。";
        if (current.currentTargetRevision != m_RoomTaskTargetRevision)
            return "位置发生了变化，我需要按现在的位置重新确认，不能沿用刚才的结果。";
        if (bridge.ReservesBody) return bridge.ActionId == m_RoomTaskActionId ? "当前移动请求已提交，但还不能确认执行进度。"
            : "当前还有房间动作在执行，这次没有另外启动新的走近。";
        if (m_RoomTaskDispatch != "attempt-submitted") return "这次还没有开始新的走近，我需要以现在的位置重新确认。";
        if (bridge.Controller != null && bridge.Controller.Stage == "target-budget") return "这段路线超过了我目前单次行走的时长限制，还没能走到你身边。";
        if (bridge.Phase == "unreachable") return "这次还没能走到你身边，当前位置的路线或空间检查没有通过。";
        return "这次走近还没有完成，我不能把它当作已经成功。";
    }

    private string BuildRoomSearchFallback()
    {
        var requirement = BuildRoomSearchReportRequirement();
        switch ((string)requirement["expectedReport"])
        {
            case "found": return "这次已经确认并重新看见了你要找的目标；这不代表执行了设备操作。";
            case "survey-completed": return "这次环顾已完成程序记录的观察范围；遮挡或未能辨认的内容仍不能确认。";
            case "cancelled": return "这次搜索已经中止，还不能确认整个寻找任务完成。";
            default: return (bool?)requirement["searchAttempted"] == true
                ? "这次搜索还没有确认整个目标或观察任务完成，不能仅凭看过的范围说目标不存在。"
                : "这次还没有完成有效搜索，目标和观察范围仍未确认。";
        }
    }
}
