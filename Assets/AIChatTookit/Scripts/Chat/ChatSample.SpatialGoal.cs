using System;
using System.Collections.Generic;
using System.Linq;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // A reviewed user objective survives its visual/navigation prerequisites. It is
    // session memory, never an inventory search or a device-operation authority.
    private sealed class SpatialDeviceGoal
    {
        public string id, originalRequest, requestQuote, targetDescription, proposedTargetDescription, desiredState;
        public JObject confirmedTarget;
        public JObject referenceContext;
        public string status = "active", reason = "reviewed-user-device-request", entityId;
        public Transform environment, entityRoot;
        public UniVRM10.Vrm10Instance avatar;
        public NevaSceneVisionCapture source;
        public LLM model;
        public int workEpoch, userTurn, formalGeneration, visionEpoch;
        public int executionRevision, lastReviewedUserTurn;
        // Failed update review fences every planning generation for this exact
        // user turn, including empty-ASR recovery of the accepted request.
        public int currentRequestOnlyUserTurn = -1;
        public bool executionSuspended, resumeAfterDialogue;
        public string latestUserText;
        public string referentKind, referentSourceId;
        public readonly List<JObject> userUpdates = new List<JObject>();
        public float createdAt;
        public string createdUtc;
        public JObject completionEvidence;
        public readonly List<JObject> steps = new List<JObject>();
        public readonly List<JObject> failures = new List<JObject>();
        public readonly HashSet<string> terminalSteps = new HashSet<string>(StringComparer.Ordinal);
        public readonly Queue<string> terminalOrder = new Queue<string>();
        public readonly List<JObject> views = new List<JObject>();
        public int completedStepCount;
    }

    private sealed class SpatialGoalActionOwner
    {
        public SpatialDeviceGoal goal;
        public SceneActionFact fact;
        public int executionRevision;
    }

    private SpatialDeviceGoal m_SpatialDeviceGoal;
    private readonly Dictionary<string, SpatialGoalActionOwner> m_SpatialGoalActions = new Dictionary<string, SpatialGoalActionOwner>(StringComparer.Ordinal);
    private readonly Queue<string> m_SpatialGoalActionOrder = new Queue<string>();

    private bool SpatialGoalOwnerCurrent(SpatialDeviceGoal goal)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        bool current = goal != null && bridge != null && goal.environment != null && bridge.environmentRoot == goal.environment &&
            goal.environment.gameObject.scene == gameObject.scene && goal.avatar != null && bridge.avatar == goal.avatar &&
            goal.source != null && goal.source == m_SceneVisionCapture && goal.source.Target == goal.avatar &&
            goal.model != null && goal.model == m_ChatSettings?.m_ChatModel;
        if (goal != null && !current && goal.status != "cancelled")
        { goal.status="cancelled"; goal.reason="room-avatar-model-or-source-owner-changed"; goal.executionSuspended=true; ++goal.executionRevision; }
        return current;
    }

    // Logical authorization outlives a speech turn. Each execution lease still
    // belongs to one exact work/user/formal/vision owner, so old callbacks cannot
    // act after interruption or acquire the resumed task's authority.
    private bool SpatialGoalExecutionCurrent(SpatialDeviceGoal goal) => SpatialGoalOwnerCurrent(goal) &&
        !goal.executionSuspended && goal.workEpoch == m_WorkEpoch && goal.userTurn == m_ActionConstraints.CurrentUserTurn &&
        goal.formalGeneration == m_FormalResponseGeneration && goal.visionEpoch == m_SceneVisionEpoch;

    private bool IsCurrentSpatialGoal(string goalId) => !string.IsNullOrEmpty(goalId) &&
        m_SpatialDeviceGoal?.id == goalId && SpatialGoalOwnerCurrent(m_SpatialDeviceGoal) && m_SpatialDeviceGoal.status != "cancelled";

    private string GetActiveSpatialGoalId()
    {
        RefreshSpatialGoalCompletionValidity();
        return SpatialGoalExecutionCurrent(m_SpatialDeviceGoal) &&
            (m_SpatialDeviceGoal.status == "active" || m_SpatialDeviceGoal.status == "searching") ? m_SpatialDeviceGoal.id : null;
    }

    private bool SpatialGoalPreventsWorkClosure() => GetActiveSpatialGoalId() != null;

    private bool SpatialGoalProposalAlreadyRegistered(RoomTaskProtocol.Decision decision, string originalRequest)
    {
        var proposal = decision?.deviceGoal;
        var goal = m_SpatialDeviceGoal;
        return proposal != null && proposal.operation == "set" && SpatialGoalExecutionCurrent(goal) && goal.status != "cancelled" &&
            string.Equals(originalRequest, goal.originalRequest, StringComparison.Ordinal) &&
            string.Equals(proposal.requestQuote, goal.requestQuote, StringComparison.Ordinal) &&
            string.Equals(proposal.targetDescription, goal.proposedTargetDescription, StringComparison.Ordinal) && proposal.desiredState == goal.desiredState;
    }

    private bool TryRegisterReviewedSpatialGoal(RoomTaskProtocol.Decision decision, string originalRequest,
        RoomTaskProtocol.DeviceGoalReview review, out string reason)
    {
        reason = null;
        var proposal = decision?.deviceGoal;
        if (proposal == null || review == null || !review.IsApproved)
        { reason = "device-goal-not-independently-approved"; return false; }
        if (decision.origin == "autonomous" || string.IsNullOrWhiteSpace(originalRequest) ||
            !string.Equals(originalRequest, m_ActionConstraints.CurrentUserText, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(proposal.requestQuote) || proposal.requestQuote.Length > 160 ||
            originalRequest.IndexOf(proposal.requestQuote, StringComparison.Ordinal) < 0)
        { reason = "device-goal-user-evidence-or-owner-not-current"; return false; }
        if (proposal.operation == "cancel")
        {
            if (!string.IsNullOrEmpty(proposal.targetDescription) || !string.IsNullOrEmpty(proposal.desiredState))
            { reason = "invalid-device-goal-cancellation"; return false; }
            CancelSpatialGoal("independently-reviewed-user-cancellation");
            ClearPendingDeviceClarification("independently-reviewed-user-cancellation"); return true;
        }
        if (proposal.operation != "set" || string.IsNullOrWhiteSpace(proposal.targetDescription) || proposal.targetDescription.Length > 240 ||
            (proposal.desiredState != "on" && proposal.desiredState != "off") ||
            (decision.IsObjectState && decision.objectState != proposal.desiredState))
        { reason = "invalid-reviewed-device-goal"; return false; }
        if (SpatialGoalProposalAlreadyRegistered(decision, originalRequest)) return true;
        if (!TryCreateReviewedSpatialGoal(proposal, originalRequest, originalRequest, review, out reason)) return false;
        ClearPendingDeviceClarification("new-independently-reviewed-user-goal-registered");
        return true;
    }

    // Only the normal exact-current-text front door or the one-use pending
    // approval lease may call this shared creation step. It does not itself turn
    // quoted history or a rejected draft into an approved objective.
    private bool TryCreateReviewedSpatialGoal(RoomTaskProtocol.DeviceGoalProposal proposal, string originalRequest,
        string latestUserText, RoomTaskProtocol.DeviceGoalReview review, out string reason)
    {
        reason = null;
        if (proposal == null || review?.IsApproved != true || proposal.operation != "set" ||
            string.IsNullOrWhiteSpace(originalRequest) || string.IsNullOrWhiteSpace(proposal.requestQuote) ||
            proposal.requestQuote.Length > 160 || originalRequest.IndexOf(proposal.requestQuote, StringComparison.Ordinal) < 0 ||
            string.IsNullOrWhiteSpace(proposal.targetDescription) || proposal.targetDescription.Length > 240 ||
            (proposal.desiredState != "on" && proposal.desiredState != "off"))
        { reason = "invalid-reviewed-device-goal"; return false; }
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (!m_EnableSceneActions || !m_EnableSceneObjectAwareness || !SceneEyesOpen || bridge == null || bridge.environmentRoot == null ||
            bridge.avatar == null || m_SceneVisionCapture == null || m_SceneVisionCapture.Target != bridge.avatar || m_ChatSettings?.m_ChatModel == null)
        { reason = "device-goal-room-avatar-or-vision-unavailable"; return false; }
        if (!TryResolveReviewedSpatialReferent(review, out string inheritedEntity, out Transform inheritedRoot, out reason)) return false;
        CancelSpatialGoal("replaced-by-independently-reviewed-user-goal");
        m_SpatialDeviceGoal = new SpatialDeviceGoal {
            id="device-goal-" + Guid.NewGuid().ToString("N"), originalRequest=originalRequest,
            // Approval establishes the user's operation, not the proposal's visual
            // guess. Keep the exact human reference until independent matching.
            requestQuote=proposal.requestQuote, targetDescription=originalRequest,
            proposedTargetDescription=proposal.targetDescription, desiredState=proposal.desiredState,
            referenceContext=review.referenceContext?.DeepClone() as JObject ?? RoomTaskProtocol.BuildDeviceReferenceContext(JArray.Parse(RecentRoomDialogue())),
            environment=bridge.environmentRoot, avatar=bridge.avatar, source=m_SceneVisionCapture,
            model=m_ChatSettings.m_ChatModel, formalGeneration=m_FormalResponseGeneration, visionEpoch=m_SceneVisionEpoch,
            workEpoch=m_WorkEpoch, userTurn=m_ActionConstraints.CurrentUserTurn,
            lastReviewedUserTurn=m_ActionConstraints.CurrentUserTurn, latestUserText=latestUserText,
            entityId=inheritedEntity, entityRoot=inheritedRoot,
            referentKind=review.referentKind, referentSourceId=review.referentSourceId,
            createdAt=Time.realtimeSinceStartup, createdUtc=DateTime.UtcNow.ToString("o")
        };
        Debug.Log("[Vision/Goal] registered " + BuildSpatialGoalContext().ToString(Newtonsoft.Json.Formatting.None), this);
        return true;
    }

    private void CancelSpatialGoal(string reason)
    {
        if (m_SpatialDeviceGoal == null || m_SpatialDeviceGoal.status == "cancelled") return;
        m_SpatialDeviceGoal.status = "cancelled"; m_SpatialDeviceGoal.reason = SpatialGoalBoundText(reason, 240);
        m_SpatialDeviceGoal.executionSuspended=true; m_SpatialDeviceGoal.resumeAfterDialogue=false; ++m_SpatialDeviceGoal.executionRevision;
        // Keep bounded action-owner tombstones: an old callback cannot acquire the
        // next goal's authority just because the current goal changed.
    }

    private void BlockSpatialGoal(string reason)
    {
        if (GetActiveSpatialGoalId() == null) return;
        m_SpatialDeviceGoal.status = "blocked"; m_SpatialDeviceGoal.reason = SpatialGoalBoundText(reason, 240);
    }

    private static string SpatialGoalBoundText(string text, int limit) => string.IsNullOrEmpty(text) ? "" : text.Substring(0, Math.Min(limit, text.Length));

    private JObject BuildSpatialGoalContext()
    {
        RefreshSpatialGoalCompletionValidity();
        var goal = m_SpatialDeviceGoal;
        if (goal == null) return new JObject { ["status"]="none" };
        bool owner = SpatialGoalOwnerCurrent(goal);
        return new JObject {
            ["goalId"]=goal.id, ["status"]=owner ? goal.status : "cancelled", ["ownerCurrent"]=owner,
            ["reason"]=owner ? goal.reason : "room-avatar-model-or-source-owner-changed",
            ["executionCurrent"]=SpatialGoalExecutionCurrent(goal), ["executionSuspended"]=goal.executionSuspended,
            ["executionRevision"]=goal.executionRevision, ["latestUserText"]=goal.latestUserText,
            ["userUpdates"]=new JArray(goal.userUpdates.Select(update=>update.DeepClone())),
            ["workEpoch"]=goal.workEpoch, ["userTurn"]=goal.userTurn, ["formalGeneration"]=goal.formalGeneration, ["visionEpoch"]=goal.visionEpoch,
            ["originalRequest"]=goal.originalRequest, ["requestQuote"]=goal.requestQuote,
            ["targetDescription"]=goal.targetDescription, ["desiredState"]=goal.desiredState, ["boundEntityId"]=goal.entityId,
            ["targetDescriptionSource"]="original-user-request; proposal visual guesses are not established identity",
            ["referenceContext"]=owner ? goal.referenceContext?.DeepClone() : null,
            ["confirmedTarget"]=owner ? goal.confirmedTarget?.DeepClone() : null,
            ["referentKind"]=goal.referentKind, ["referentSourceId"]=goal.referentSourceId,
            ["createdUtc"]=goal.createdUtc,
            ["completionPredicate"]=new JObject {
                ["kind"]="same-semantically-matched-entity-current-registered-device-readback",
                ["entityId"]=goal.entityId, ["desiredState"]=goal.desiredState,
                ["requiresCurrentOpaqueReobservation"]=true, ["requiresFreshReadback"]=true,
                ["substepCompletionDoesNotSatisfyGoal"]=true
            },
            ["completionEvidence"]=goal.completionEvidence?.DeepClone(),
            ["completionEvidenceValid"]=owner && goal.status == "satisfied",
            ["boundEntityMemory"]=owner && !string.IsNullOrEmpty(goal.entityId) && KnownSceneObjectBinding(goal.entityId, out var known) ? KnownSceneSpatialFacts(known) : null,
            ["searchProgress"]=new JObject { ["completedSteps"]=new JArray(goal.steps.Select(step=>step.DeepClone())), ["stepCount"]=goal.completedStepCount,
                ["observedViews"]=new JArray(goal.views.Select(view=>view.DeepClone())) },
            ["failedCandidates"]=new JArray(goal.failures.Select(failure=>failure.DeepClone())),
            ["scope"]="Independently reviewed current-user objective, bounded real action progress and per-capture failed candidates. Steps, arrival and historical sightings do not complete device state. Failures are not global exclusions or proof of absence. A reviewed engine-listed historical source may constrain the same entity; execution still needs direct-target semantic match and fresh opaque confirmation. Stored identity/position is never device action authority."
        };
    }

    private void RefreshSpatialGoalCompletionValidity()
    {
        var goal = m_SpatialDeviceGoal;
        if (goal == null || goal.status != "satisfied" || !SpatialGoalOwnerCurrent(goal)) return;
        var device = goal.entityRoot != null ? goal.entityRoot.GetComponent<SceneObjectLightSwitch>() : null;
        if (device != null && device.EntityId == goal.entityId && device.TryValidate(out _) &&
            JToken.DeepEquals(goal.completionEvidence?["actual"], JObject.Parse(JsonUtility.ToJson(device.ReadState())))) return;
        goal.status="searching"; goal.reason="completed-device-readback-invalidated; fresh-target-reobservation-required";
        if (goal.completionEvidence != null) goal.completionEvidence["historicalOnly"]=true;
    }

    // This hook records actual sampled directions, not an imagined room search.
    // A missing bound target is negative evidence for this frame alone.
    private void ObserveSpatialGoalView(SceneVisionObservation observation, string purpose)
    {
        if (GetActiveSpatialGoalId() == null || observation == null || !SceneSpatialObservationUsable(observation)) return;
        var goal=m_SpatialDeviceGoal;
        if (observation.CapturedAtRealtime < goal.createdAt || goal.views.Exists(v => (string)v["captureId"] == observation.CaptureId)) return;
        var confirmed=new JArray(); var notVisible=new JArray();
        if (!string.IsNullOrEmpty(goal.entityId) && KnownSceneObjectBinding(goal.entityId, out var known))
        {
            if (SceneVisionGrounding.TryFindObject(goal.source, observation, goal.entityId, out var seen, out _) && seen.VisibleConfirmed && seen.Root == known.root && seen.TryValidate(out _)) confirmed.Add(goal.entityId);
            else notVisible.Add(goal.entityId);
        }
        goal.views.Add(new JObject { ["captureId"]=observation.CaptureId, ["capturedAtUtc"]=observation.CapturedAtUtc,
            ["capturedAtRealtime"]=observation.CapturedAtRealtime, ["cameraPosition"]=SceneSpatialVector(observation.CameraPosition),
            ["viewDirectionWorld"]=SceneSpatialVector(observation.CameraRotation * Vector3.forward), ["purpose"]=purpose ?? "",
            ["confirmedBoundEntityIds"]=confirmed, ["notVisibleBoundEntityIds"]=notVisible, ["historical"]=true,
            ["scope"]="This real RGB/opaque-grounding view only. Unconfirmed is not absent from the room; historical capture cannot authorize a current action." });
        while (goal.views.Count > 6) goal.views.RemoveAt(0);
    }

    private bool TryValidateSpatialGoalStep(RoomTaskProtocol.Decision decision, out string reason)
    {
        reason = null;
        if (GetActiveSpatialGoalId() == null) return true;
        if (decision == null) { reason="missing-spatial-goal-step"; return false; }
        var goal = m_SpatialDeviceGoal;
        if (decision.deviceGoal != null && !SpatialGoalProposalAlreadyRegistered(decision, goal.originalRequest))
        { reason="active-device-goal-cannot-be-changed-by-substep"; return false; }
        if (decision.intent == "approach" || decision.intent == "follow" || decision.intent == "stop-moving" || decision.origin == "autonomous")
        { reason="device-goal-step-cannot-switch-to-user-movement-or-autonomous-owner"; return false; }
        if (decision.IsObjectState && decision.objectState != goal.desiredState)
        { reason="device-goal-step-state-conflicts-with-goal"; return false; }
        if (!string.IsNullOrEmpty(goal.entityId) && decision.entityTarget != null && decision.entityTarget.entityId != goal.entityId && !decision.IsObservationApproach)
        { reason="device-goal-step-entity-conflicts-with-bound-target"; return false; }
        return true;
    }

    private bool TryValidateSpatialGoalConfirmedTarget(SceneActionFact fact, SceneObservedObject selected, out string reason)
    {
        reason = null;
        if (fact != null && !string.IsNullOrEmpty(fact.actionId) && m_SpatialGoalActions.TryGetValue(fact.actionId, out var oldOwner) &&
            (oldOwner.goal != m_SpatialDeviceGoal || oldOwner.fact != fact || oldOwner.executionRevision != oldOwner.goal.executionRevision ||
             !SpatialGoalExecutionCurrent(oldOwner.goal) || oldOwner.goal.status == "cancelled" || oldOwner.goal.status == "blocked"))
        { reason="spatial-goal-action-owner-invalidated"; return false; }
        if (GetActiveSpatialGoalId() == null) return true;
        var goal = m_SpatialDeviceGoal;
        if (fact == null || selected == null || !selected.VisibleConfirmed || selected.source != goal.source ||
            selected.observation.CapturedAtRealtime < goal.createdAt || !IsFreshSceneObservation(selected.observation) || !selected.TryValidate(out reason))
        { reason="spatial-goal-target-not-currently-confirmed"; return false; }
        if (fact.targetPurpose == "observe-closer")
        {
            if (fact.intent == "set-object-state") { reason="observation-reference-cannot-satisfy-device-goal"; return false; }
            return true;
        }
        if (!fact.semanticTargetConfirmed || fact.targetMatchVerdict != "matched" || fact.targetPurpose != "direct-target" ||
            !string.Equals(fact.targetDescription, BoundObjectGoal(goal.originalRequest), StringComparison.Ordinal))
        { reason="spatial-goal-main-target-semantic-match-missing"; return false; }
        if (!string.IsNullOrEmpty(goal.entityId) && (selected.EntityId != goal.entityId || selected.Root != goal.entityRoot))
        { reason="spatial-goal-confirmed-entity-conflicts-with-bound-target"; return false; }
        if (fact.intent == "set-object-state" && fact.requestedState != goal.desiredState)
        { reason="spatial-goal-confirmed-step-state-conflicts-with-goal"; return false; }
        var device = selected.Root != null ? selected.Root.GetComponent<SceneObjectLightSwitch>() : null;
        if (device != null && device.IsBound && device.EntityId == selected.EntityId && device.TryValidate(out _))
        {
            goal.entityId=selected.EntityId; goal.entityRoot=selected.Root;
            goal.confirmedTarget=new JObject { ["entityId"]=selected.EntityId, ["sceneLabel"]=selected.SceneLabel,
                ["captureId"]=selected.CaptureId, ["observedUtc"]=selected.observation.CapturedAtUtc,
                ["scope"]="Historical opaque entity independently matched to the original user request. The scene label describes the registered entity; it does not replace the user's constraints or establish current visibility." };
        }
        // A semantically matched non-device may be inspected, but cannot bind or
        // satisfy the main device target; the existing capability gate rejects it.
        return true;
    }

    // Inspection can legitimately identify a non-device. Completing the device
    // search is stricter: it must bind this already-authorized goal to a usable
    // registered controller, without substituting an existing bound identity.
    private bool TryBindSpatialGoalSearchTarget(SceneActionFact fact, SceneObservedObject selected, out string reason)
    {
        var goal = m_SpatialDeviceGoal;
        if (!SpatialGoalExecutionCurrent(goal) || GetActiveSpatialGoalId() != goal?.id)
        { reason = "search-device-goal-owner-not-current"; return false; }
        if (!TryValidateSpatialGoalConfirmedTarget(fact, selected, out reason)) return false;
        if (m_SpatialDeviceGoal != goal || !SpatialGoalExecutionCurrent(goal))
        { reason = "search-device-goal-owner-not-current"; return false; }
        var device = selected?.Root != null ? selected.Root.GetComponent<SceneObjectLightSwitch>() : null;
        if (device == null || !device.IsBound || device.EntityId != selected.EntityId || !device.TryValidate(out _))
        { reason = "search-selected-object-has-no-registered-control"; return false; }
        if (goal.entityId != selected.EntityId || goal.entityRoot != selected.Root)
        { reason = "search-confirmed-object-not-bound-to-device-goal"; return false; }
        reason = null;
        return true;
    }

    private static bool SpatialGoalTerminalPhase(string phase) => phase == "observed" || phase == "confirmed" || phase == "performed" ||
        phase == "already-satisfied" || phase == "arrived" || phase == "arrived-object-unconfirmed" || phase == "repositioned" ||
        phase == "rejected" || phase == "failed" || phase == "cancelled" || phase == "state-applied-unconfirmed";

    private void ObserveSpatialGoalAction(SceneActionFact fact)
    {
        if (fact == null || fact.origin != "user" || GetActiveSpatialGoalId() == null || string.IsNullOrEmpty(fact.actionId)) return;
        var goal = m_SpatialDeviceGoal;
        if (!m_SpatialGoalActions.TryGetValue(fact.actionId, out var owner))
        {
            // Terminal-only input is never allowed to adopt a new goal. Starts
            // establish ownership before model target matching or async movement.
            if (SpatialGoalTerminalPhase(fact.phase)) return;
            owner = new SpatialGoalActionOwner { goal=goal, fact=fact, executionRevision=goal.executionRevision };
            m_SpatialGoalActions[fact.actionId]=owner; m_SpatialGoalActionOrder.Enqueue(fact.actionId);
            while (m_SpatialGoalActionOrder.Count > 64) m_SpatialGoalActions.Remove(m_SpatialGoalActionOrder.Dequeue());
        }
        if (owner.goal != goal || owner.fact != fact || owner.executionRevision != goal.executionRevision || !SpatialGoalExecutionCurrent(owner.goal)) return;
        string terminalKey=fact.actionId + ":" + fact.phase;
        if (!SpatialGoalTerminalPhase(fact.phase) || !goal.terminalSteps.Add(terminalKey)) return;
        goal.terminalOrder.Enqueue(terminalKey); goal.completedStepCount++;
        while (goal.terminalOrder.Count > 32) goal.terminalSteps.Remove(goal.terminalOrder.Dequeue());
        goal.status="searching";
        goal.steps.Add(new JObject {
            ["actionId"]=fact.actionId, ["intent"]=fact.intent, ["phase"]=fact.phase, ["reason"]=SpatialGoalBoundText(fact.reason, 240),
            ["captureId"]=fact.resultCaptureId ?? fact.confirmedCaptureId ?? fact.captureId,
            ["entityId"]=fact.entityId, ["targetPurpose"]=fact.targetPurpose, ["observedUtc"]=fact.observedUtc,
            ["actualRoot"]=SceneSpatialVector(fact.actualRoot), ["actualViewChangeDegrees"]=fact.actualViewChangeDegrees,
            ["mainGoalSatisfied"]=false
        });
        while (goal.steps.Count > 16) goal.steps.RemoveAt(0);
        if (fact.phase == "rejected" || fact.phase == "failed" || fact.phase == "arrived-object-unconfirmed" || fact.phase == "state-applied-unconfirmed")
        {
            goal.failures.Add(new JObject { ["actionId"]=fact.actionId, ["entityId"]=fact.entityId ?? fact.requestedEntityId,
                ["captureId"]=fact.matchCaptureId ?? fact.captureId, ["verdict"]=fact.targetMatchVerdict,
                ["reason"]=SpatialGoalBoundText(fact.reason, 240), ["observedUtc"]=fact.observedUtc,
                ["scope"]="Failure of this candidate/step in this frame; not a global exclusion or missing/broken device proof." });
            while (goal.failures.Count > 8) goal.failures.RemoveAt(0);
        }
        if (TrySpatialGoalCompletionEvidence(goal, fact, out var evidence))
        {
            goal.status="satisfied"; goal.reason="same-target-fresh-device-readback-matches-request"; goal.completionEvidence=evidence;
            goal.steps[goal.steps.Count-1]["mainGoalSatisfied"]=true;
            RememberVerifiedSpatialDeviceReferent(goal, fact, evidence);
            Debug.Log("[Vision/Goal] satisfied " + BuildSpatialGoalContext().ToString(Newtonsoft.Json.Formatting.None), this);
        }
    }

    private bool TrySpatialGoalCompletionEvidence(SpatialDeviceGoal goal, SceneActionFact fact, out JObject evidence)
    {
        evidence=null;
        if (!SpatialGoalExecutionCurrent(goal) || string.IsNullOrEmpty(goal.entityId) || goal.entityRoot == null ||
            fact.entityId != goal.entityId || !fact.objectConfirmed || !fact.semanticTargetConfirmed || fact.targetMatchVerdict != "matched" ||
            fact.targetPurpose != "direct-target" || !string.Equals(fact.targetDescription, BoundObjectGoal(goal.originalRequest), StringComparison.Ordinal) ||
            (fact.phase != "performed" && fact.phase != "already-satisfied" && fact.phase != "observed") ||
            (fact.intent != "set-object-state" && fact.intent != "inspect-object")) return false;
        string capture = fact.resultCaptureId;
        if (string.IsNullOrEmpty(capture) || !KnownSceneObjectBinding(goal.entityId, out var known) || known.root != goal.entityRoot ||
            known.observed == null || known.observed.CaptureId != capture || known.observed.observation.CapturedAtRealtime < goal.createdAt ||
            !SceneSpatialObservationUsable(known.observed.observation) || !IsFreshSceneObservation(known.observed.observation) ||
            !known.observed.TryValidate(out _) || (string)m_SelectedSceneObject?["entityId"] != goal.entityId ||
            (string)m_SelectedSceneObject?["captureId"] != capture || m_SelectedSceneDevice == null || m_SelectedSceneDevice.Root != goal.entityRoot ||
            m_SelectedSceneDevice.EntityId != goal.entityId || !m_SelectedSceneDevice.TryValidate(out _)) return false;
        var readback = m_SelectedSceneObject["runtimeDeviceStateReadAfterObservation"] as JObject;
        if (readback == null || (string)readback["entityId"] != goal.entityId || (bool?)readback["registered"] != true ||
            (bool?)readback["on"] != (goal.desiredState == "on") ||
            !JToken.DeepEquals(readback, JObject.Parse(JsonUtility.ToJson(m_SelectedSceneDevice.ReadState())))) return false;
        if (fact.intent == "set-object-state" && (fact.requestedState != goal.desiredState || !SceneResultBindingValid() ||
            m_SceneResultDevice != m_SelectedSceneDevice || (string)m_SceneObjectStateResult?["actionId"] != fact.actionId ||
            (string)m_SceneObjectStateResult?["entityId"] != goal.entityId || (bool?)m_SceneObjectStateResult?["completed"] != true ||
            (bool?)m_SceneObjectStateResult?["freshVisualObservation"] != true ||
            (string)m_SceneObjectStateResult?["resultCaptureId"] != capture || !JToken.DeepEquals(m_SceneObjectStateResult?["actual"], readback))) return false;
        evidence = new JObject { ["actionId"]=fact.actionId, ["entityId"]=goal.entityId, ["resultCaptureId"]=capture,
            ["observedUtc"]=known.observed.CapturedAtUtc, ["readAtUtc"]=m_SelectedSceneObject["stateReadAtUtc"]?.DeepClone(),
            ["actual"]=readback.DeepClone(), ["desiredState"]=goal.desiredState,
            ["scope"]="Same direct-target semantically matched entity, fresh opaque frame and registered runtime readback. Live read here only invalidates a changed saved readback; it cannot create new perception. This proves the requested device state, not room-wide darkness, visual brightness or physical touching." };
        return true;
    }
}
