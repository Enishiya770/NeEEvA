using System;
using System.Collections;
using System.Collections.Generic;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // One owner orders an approved device task. Only search interprets where the
    // target is; another room-planning loop cannot compete with this controller.
    private const int SpatialGoalRunnerStepLimit = 8;
    private const int SpatialGoalRunnerSearchLimit = 2;
    private const float SpatialGoalRunnerDuration = 180f;
    private const float SpatialGoalRunnerActionDeadline = 125f;
    private SpatialGoalRunnerLease m_SpatialGoalRunner;
    private JObject m_SpatialGoalRunnerLastReport;

    private sealed class SpatialGoalRunnerLease
    {
        public string goalId, originalRequest, latestUserText, phase = "capturing";
        public string decisionIntent, decisionCapture, terminalReason, latestFailureReason, forcedFailure;
        public int generation, workEpoch, userTurn, visionEpoch, sceneEpoch, goalExecutionRevision;
        public int steps, searchAttempts, freshnessRetries, controllerOperation;
        public float startedAt;
        public bool ending, terminalReceived, requireSearch, nativeOperation;
        public ArdyRoomDialogueBridge bridge;
        public NeEEvA.Motion.ArdyRoomLocomotionController controller;
        public UniVRM10.Vrm10Instance avatar;
        public Transform environment, user;
        public NevaSceneVisionCapture source;
        public SceneVisionObservation bindingObservation, observation;
        public LLM model;
        public SceneActionFact fact;
        public RoomTaskProtocol.Decision userObservation;
        public readonly HashSet<string> completedActions = new HashSet<string>(StringComparer.Ordinal);
        public readonly JArray receipts = new JArray();
    }

    private bool SpatialGoalRunnerBindingCurrent(SpatialGoalRunnerLease lease) => lease != null &&
        ReferenceEquals(m_SpatialGoalRunner, lease) && !lease.ending && isActiveAndEnabled &&
        m_EnableSceneActions && RoomMotionOutputEnabled && SceneEyesOpen && !m_UserSpeechActiveForAutonomy &&
        !m_SceneTimePaused && Time.timeScale > 0 && lease.generation == m_FormalResponseGeneration &&
        lease.workEpoch == m_WorkEpoch && lease.userTurn == m_ActionConstraints.CurrentUserTurn &&
        lease.latestUserText == m_ActionConstraints.CurrentUserText && lease.visionEpoch == m_SceneVisionEpoch &&
        lease.model != null && lease.model == m_ChatSettings?.m_ChatModel &&
        lease.bridge != null && lease.bridge == GetComponent<ArdyRoomDialogueBridge>() && lease.bridge.IsConnected &&
        lease.avatar != null && lease.avatar.gameObject.activeInHierarchy && lease.bridge.avatar == lease.avatar &&
        lease.environment != null && lease.bridge.environmentRoot == lease.environment && lease.bridge.userHead == lease.user &&
        lease.controller != null && lease.bridge.Controller == lease.controller &&
        lease.source != null && lease.source == m_SceneVisionCapture && lease.source.isActiveAndEnabled &&
        lease.source.Target == lease.avatar && lease.source.IsObservationBindingCurrent(lease.bindingObservation) &&
        m_SpatialDeviceGoal?.id == lease.goalId && m_SpatialDeviceGoal.originalRequest == lease.originalRequest &&
        lease.goalExecutionRevision == m_SpatialDeviceGoal.executionRevision && SpatialGoalExecutionCurrent(m_SpatialDeviceGoal);

    private bool SpatialGoalRunnerOwnsGeneration(int generation) =>
        m_SpatialGoalRunner?.generation == generation && SpatialGoalRunnerBindingCurrent(m_SpatialGoalRunner);

    private bool SpatialGoalRunnerDelegatesSearch(int generation) => SpatialGoalRunnerOwnsGeneration(generation) &&
        m_SpatialGoalRunner.phase == "searching" && GetActiveSpatialGoalId() == m_SpatialGoalRunner.goalId;

    // Evidence is a bounded verbatim quotation, not necessarily the entire turn.
    // Only this independently reviewed latest user update can supply a preface;
    // a valid citation of the standing goal's older request is insufficient.
    private bool IsReviewedLatestUserObservation(RoomTaskProtocol.Decision decision, string latestUserText) =>
        decision?.IsViewChange == true && decision.origin == "user" &&
        !string.IsNullOrWhiteSpace(decision.evidence) && decision.evidence.Length <= 160 &&
        !string.IsNullOrEmpty(latestUserText) &&
        latestUserText.IndexOf(decision.evidence, StringComparison.Ordinal) >= 0 &&
        latestUserText == m_ActionConstraints.CurrentUserText && SpatialGoalExecutionCurrent(m_SpatialDeviceGoal) &&
        m_SpatialDeviceGoal.lastReviewedUserTurn == m_ActionConstraints.CurrentUserTurn &&
        m_SpatialDeviceGoal.latestUserText == latestUserText;

    private bool TryBeginSpatialGoalRunner(string goalId, int generation)
        => TryBeginSpatialGoalRunnerWithObservation(goalId, generation, null);

    private bool TryBeginSpatialGoalRunnerWithObservation(string goalId, int generation, RoomTaskProtocol.Decision userObservation)
    {
        if (SpatialGoalRunnerOwnsGeneration(generation)) return m_SpatialGoalRunner.goalId == goalId;
        if (m_SpatialGoalRunner != null) CancelSpatialGoalRunner("runner-replaced");
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var observation = GetFormalSceneObservation(generation) ?? m_SceneVisionCapture?.LatestObservation;
        if (string.IsNullOrEmpty(goalId) || GetActiveSpatialGoalId() != goalId || generation != m_FormalResponseGeneration ||
            observation?.HasImage != true || bridge == null || !bridge.IsConnected || bridge.avatar == null ||
            bridge.environmentRoot == null || bridge.Controller == null || m_SceneVisionCapture == null ||
            !m_SceneVisionCapture.IsObservationBindingCurrent(observation) || !RoomMotionOutputEnabled ||
            !m_EnableSceneActions || !SceneEyesOpen || m_UserSpeechActiveForAutonomy || m_SceneTimePaused ||
            m_SceneSearch != null || m_SceneActionActive || bridge.ReservesBody || bridge.IsFollowing ||
            bridge.Controller.IsBusy || bridge.Controller.IsMoving) return false;
        var lease = new SpatialGoalRunnerLease {
            goalId = goalId, generation = generation, workEpoch = m_WorkEpoch,
            userTurn = m_ActionConstraints.CurrentUserTurn, originalRequest = m_SpatialDeviceGoal.originalRequest,
            latestUserText = m_ActionConstraints.CurrentUserText, visionEpoch = m_SceneVisionEpoch,
            sceneEpoch = m_SceneActionEpoch, goalExecutionRevision = m_SpatialDeviceGoal.executionRevision,
            bridge = bridge, controller = bridge.Controller, avatar = bridge.avatar,
            environment = bridge.environmentRoot, user = bridge.userHead, source = m_SceneVisionCapture,
            bindingObservation = observation, model = m_ChatSettings?.m_ChatModel, startedAt = Time.realtimeSinceStartup,
            userObservation = userObservation
        };
        m_SpatialGoalRunner = lease; m_SpatialGoalRunnerLastReport = null;
        // Dialogue resumes enter here without a RoomTask planner transaction.
        // Give cancellation and the final speech audit the same exact owner.
        m_RoomTaskGeneration = generation; m_RoomSpeechGuardGeneration = generation;
        m_RoomSpeechReviewModel = lease.model; m_FormalResponseInFlight = true;
        m_RoomTaskIntent = "device-task"; m_RoomTaskDispatch = "unified-device-task-started";
        Debug.Log($"[SpatialGoal/Runner] goal={goalId} generation={generation} controller=unified-device-task started", this);
        StartCoroutine(WatchSpatialGoalRunner(lease));
        StartCoroutine(RunSpatialGoalRunnerSafely(lease));
        return true;
    }

    private IEnumerator RunSpatialGoalRunnerSafely(SpatialGoalRunnerLease lease)
    {
        var stack = new Stack<IEnumerator>(); stack.Push(RunSpatialGoalRunner(lease));
        try
        {
            while (stack.Count > 0 && SpatialGoalRunnerBindingCurrent(lease))
            {
                var current = stack.Peek(); bool moved = false; Exception failure = null;
                try { moved = current.MoveNext(); } catch (Exception error) { failure = error; }
                if (failure != null) { FinishSpatialGoalRunner(lease, "device-task-exception:" + failure.GetType().Name, false); yield break; }
                if (!moved) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                if (current.Current is IEnumerator nested) stack.Push(nested);
                else yield return current.Current;
            }
        }
        finally { while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose(); }
    }

    private IEnumerator WatchSpatialGoalRunner(SpatialGoalRunnerLease lease)
    {
        while (ReferenceEquals(m_SpatialGoalRunner, lease) && !lease.ending)
        {
            if (!SpatialGoalRunnerBindingCurrent(lease)) { CancelSpatialGoalRunner("runner-owner-invalidated"); yield break; }
            if (!string.IsNullOrEmpty(lease.forcedFailure))
            { FinishSpatialGoalRunner(lease, lease.forcedFailure, false); yield break; }
            if ((lease.phase == "approaching" || lease.phase == "operating" || lease.phase == "observing-user-request") && lease.fact != null &&
                (lease.sceneEpoch != m_SceneActionEpoch || !ReferenceEquals(lease.fact, m_SceneActionFact) ||
                 lease.nativeOperation && (lease.controller.OperationRevision != lease.controllerOperation ||
                                           lease.bridge.ActionId != lease.fact.actionId)))
            { CancelSpatialGoalRunner("body-or-action-replaced"); yield break; }
            if (Time.realtimeSinceStartup - lease.startedAt >= SpatialGoalRunnerDuration)
            { FinishSpatialGoalRunner(lease, "bounded-task-duration-exhausted", false); yield break; }
            yield return null;
        }
    }

    private IEnumerator CaptureSpatialGoalRunnerObservation(SpatialGoalRunnerLease lease)
    {
        lease.phase = "capturing";
        float until = Time.realtimeSinceStartup + 5f;
        bool Busy() => SceneActionIsBusy || lease.bridge.ReservesBody || lease.bridge.IsFollowing || lease.controller.IsBusy || lease.controller.IsMoving;
        while (SpatialGoalRunnerBindingCurrent(lease) && Busy() && Time.realtimeSinceStartup < until) yield return null;
        if (!SpatialGoalRunnerBindingCurrent(lease)) yield break;
        if (Busy()) { FinishSpatialGoalRunner(lease, "terminal-body-owner-did-not-release", false); yield break; }
        bool received = false; SceneVisionObservation image = null; float requestedAt = Time.realtimeSinceStartup;
        RequestSceneObservation("device-task-current-step", value => {
            if (!SpatialGoalRunnerBindingCurrent(lease) || lease.phase != "capturing") return;
            image = value; received = true;
        });
        until = Time.realtimeSinceStartup + 4.5f;
        while (SpatialGoalRunnerBindingCurrent(lease) && !received && Time.realtimeSinceStartup < until) yield return null;
        if (!SpatialGoalRunnerBindingCurrent(lease)) yield break;
        if (!received || image?.HasImage != true || image.CapturedAtRealtime < requestedAt || !IsFreshSceneObservation(image))
        { FinishSpatialGoalRunner(lease, "device-task-current-image-unavailable", false); yield break; }
        lease.observation = image;
        BindFormalSceneObservation(image, lease.generation);
        ObserveKnownSceneObjects(image, "device-task-current-step");
        ObserveSpatialGoalView(image, "device-task-current-step");
    }

    private IEnumerator RunSpatialGoalRunner(SpatialGoalRunnerLease lease)
    {
        yield return null; // Release the originating RoomTask callback before dispatch.
        if (lease.userObservation != null)
        {
            var requested = lease.userObservation;
            if (!IsReviewedLatestUserObservation(requested, lease.latestUserText) ||
                requested.viewChange?.captureId != lease.bindingObservation.CaptureId || !IsFreshSceneObservation(lease.bindingObservation))
            { FinishSpatialGoalRunner(lease, "current-user-observation-not-executable", false); yield break; }
            lease.phase = "observing-user-request"; lease.steps++;
            lease.decisionIntent = requested.intent; lease.decisionCapture = lease.bindingObservation.CaptureId;
            m_RoomTaskIntent = requested.intent;
            BeginSceneAction(requested, lease.bindingObservation, lease.generation, () => { });
            float until = Time.realtimeSinceStartup + SpatialGoalRunnerActionDeadline;
            while (SpatialGoalRunnerBindingCurrent(lease) && !lease.terminalReceived && Time.realtimeSinceStartup < until) yield return null;
            if (!SpatialGoalRunnerBindingCurrent(lease)) yield break;
            if (!lease.terminalReceived || lease.fact?.phase != "observed")
            { FinishSpatialGoalRunner(lease, lease.fact?.reason ?? "current-user-observation-deadline", false); yield break; }
        }
        while (SpatialGoalRunnerBindingCurrent(lease))
        {
            if (lease.steps >= SpatialGoalRunnerStepLimit)
            { FinishSpatialGoalRunner(lease, lease.latestFailureReason ?? "bounded-device-step-budget-exhausted", false); yield break; }
            yield return CaptureSpatialGoalRunnerObservation(lease);
            if (!SpatialGoalRunnerBindingCurrent(lease)) yield break;
            var goal = m_SpatialDeviceGoal;
            if ((string)BuildSpatialGoalContext()["status"] == "satisfied")
            { FinishSpatialGoalRunner(lease, "requested-goal-satisfied-by-runtime-evidence", true); yield break; }
            if (GetActiveSpatialGoalId() != lease.goalId)
            { FinishSpatialGoalRunner(lease, goal.reason ?? "device-goal-not-actionable", false); yield break; }
            bool visible = !string.IsNullOrEmpty(goal.entityId) && goal.entityRoot != null &&
                TryGetCurrentKnownSceneObject(lease.observation, goal.entityId, out _, out var observed) &&
                observed.Root == goal.entityRoot && observed.TryValidate(out _);
            if (lease.requireSearch || !visible)
            {
                if (lease.searchAttempts >= SpatialGoalRunnerSearchLimit)
                { AwaitSpatialGoalRunnerClarification(lease, lease.latestFailureReason ?? "target-not-confirmed-after-bounded-search"); yield break; }
                lease.phase = "searching"; lease.searchAttempts++; lease.requireSearch = false;
                bool completed = false;
                var search = new RoomTaskProtocol.Decision { intent = "search-scene", origin = "user", evidence = lease.originalRequest,
                    searchTask = new RoomTaskProtocol.SceneSearchTask { mode = "find" } };
                BeginSceneSearchTask(search, lease.observation, lease.generation, () => {
                    if (SpatialGoalRunnerBindingCurrent(lease) && lease.phase == "searching") completed = true;
                });
                while (SpatialGoalRunnerBindingCurrent(lease) && !completed)
                {
                    if (m_SceneSearch == null) { FinishSpatialGoalRunner(lease, "device-search-owner-lost", false); yield break; }
                    yield return null;
                }
                if (!SpatialGoalRunnerBindingCurrent(lease)) yield break;
                var result = BuildSceneSearchContext();
                lease.receipts.Add(new JObject { ["stage"] = "searching", ["status"] = result["status"]?.DeepClone(),
                    ["reason"] = result["reason"]?.DeepClone(), ["taskId"] = result["taskId"]?.DeepClone(),
                    ["entityId"] = result["foundEntityId"]?.DeepClone(),
                    ["searchAttempted"] = result["searchAttempted"]?.DeepClone(),
                    ["decisions"] = result["decisions"]?.DeepClone(), ["motionSteps"] = result["steps"]?.DeepClone() });
                if ((string)result["status"] != "found" || (string)result["foundEntityId"] != goal.entityId)
                {
                    string searchFailure = (string)result["reason"];
                    AwaitSpatialGoalRunnerClarification(lease, string.IsNullOrWhiteSpace(searchFailure) ? "search-did-not-confirm-target" : searchFailure);
                    yield break;
                }
                lease.latestFailureReason = null;
                continue;
            }
            float beforeDistance = Vector3.Distance(lease.avatar.transform.position, goal.entityRoot.position);
            bool approach = beforeDistance > Mathf.Clamp(m_SceneDeviceDistance, .5f, 3f);
            var decision = new RoomTaskProtocol.Decision { intent = approach ? "approach-object" : "set-object-state",
                origin = "user", evidence = lease.originalRequest, objectState = approach ? null : goal.desiredState,
                entityTarget = new RoomTaskProtocol.EntityTarget { captureId = lease.observation.CaptureId, entityId = goal.entityId } };
            string invalid;
            if (!TryValidateSpatialGoalStep(decision, out invalid) ||
                !TryValidateSceneDevicePlanningConditions(decision, lease.observation, out invalid))
            { FinishSpatialGoalRunner(lease, invalid, false); yield break; }
            lease.phase = approach ? "approaching" : "operating"; lease.steps++;
            lease.decisionIntent = decision.intent; lease.decisionCapture = lease.observation.CaptureId;
            lease.fact = null; lease.terminalReceived = lease.nativeOperation = false; lease.sceneEpoch = m_SceneActionEpoch;
            m_RoomTaskIntent = decision.intent;
            // Original target matching, path, ownership, distance and device gates remain.
            BeginSceneAction(decision, lease.observation, lease.generation, () => { });
            float until = Time.realtimeSinceStartup + SpatialGoalRunnerActionDeadline;
            while (SpatialGoalRunnerBindingCurrent(lease) && !lease.terminalReceived && Time.realtimeSinceStartup < until) yield return null;
            if (!SpatialGoalRunnerBindingCurrent(lease)) yield break;
            if (!lease.terminalReceived) { FinishSpatialGoalRunner(lease, lease.phase + "-deadline", false); yield break; }
            lease.phase = "verifying";
            if ((string)BuildSpatialGoalContext()["status"] == "satisfied")
            { FinishSpatialGoalRunner(lease, "requested-goal-satisfied-by-runtime-evidence", true); yield break; }
            var fact = lease.fact;
            if (approach && fact?.phase == "arrived")
            {
                float distance = Vector3.Distance(lease.avatar.transform.position, goal.entityRoot.position);
                if (distance > Mathf.Clamp(m_SceneDeviceDistance, .5f, 3f) && beforeDistance - distance < .15f)
                { FinishSpatialGoalRunner(lease, "approach-made-no-distance-progress", false); yield break; }
                lease.latestFailureReason = null; continue;
            }
            string reason = fact?.reason ?? "device-step-produced-no-result";
            lease.latestFailureReason = reason;
            if (reason == "device-outside-nearby-operation-distance") continue;
            if ((reason.Contains("expired") || reason == "visual-reference-not-current" || fact?.phase == "state-applied-unconfirmed") &&
                lease.freshnessRetries++ < 2) continue;
            if (fact?.phase == "arrived-object-unconfirmed" || reason.StartsWith("object-target-", StringComparison.Ordinal) ||
                reason.StartsWith("object-reconfirmation-failed:", StringComparison.Ordinal) ||
                reason.StartsWith("visible-object-not-confirmed", StringComparison.Ordinal) ||
                reason.StartsWith("referenced-entity-not-visible", StringComparison.Ordinal))
            { lease.requireSearch = true; continue; }
            FinishSpatialGoalRunner(lease, reason, false); yield break;
        }
    }

    private void ObserveSpatialGoalRunnerAction(SceneActionFact fact)
    {
        ObserveSpatialGoalAction(fact);
        var lease = m_SpatialGoalRunner;
        if (!SpatialGoalRunnerBindingCurrent(lease) || fact == null ||
            (lease.phase != "approaching" && lease.phase != "operating" && lease.phase != "observing-user-request")) return;
        if (lease.fact == null)
        {
            if (fact.intent != lease.decisionIntent || fact.captureId != lease.decisionCapture) return;
            lease.fact = fact; lease.sceneEpoch = m_SceneActionEpoch;
        }
        if (!ReferenceEquals(lease.fact, fact)) return;
        if ((fact.phase == "submitted" || fact.phase == "turning-body") && lease.bridge.ActionId == fact.actionId)
        { lease.nativeOperation = true; lease.controllerOperation = lease.controller.OperationRevision; }
        if (fact.active || !SpatialGoalTerminalPhase(fact.phase)) return;
        if (!lease.completedActions.Add((fact.actionId ?? "") + ":" + fact.phase)) return;
        lease.terminalReceived = true; lease.nativeOperation = false;
        lease.receipts.Add(new JObject { ["step"] = lease.steps, ["actionId"] = fact.actionId, ["intent"] = fact.intent,
            ["phase"] = fact.phase, ["entityId"] = fact.entityId, ["requestedState"] = fact.requestedState,
            ["resultCaptureId"] = fact.resultCaptureId, ["reason"] = fact.reason });
    }

    private bool SpatialGoalRunnerOwnsAction(SceneActionFact fact) => fact != null &&
        SpatialGoalRunnerBindingCurrent(m_SpatialGoalRunner) &&
        (ReferenceEquals(m_SpatialGoalRunner.fact, fact) || SpatialGoalRunnerDelegatesSearch(m_FormalResponseGeneration));

    private bool TryHandleSpatialGoalPlanSettled(int generation) => SpatialGoalRunnerOwnsGeneration(generation);

    private JObject BuildSpatialGoalRunnerContext(int generation)
    {
        var lease = m_SpatialGoalRunner;
        if (lease != null && lease.generation == generation)
            return new JObject { ["controller"] = "unified-device-task", ["active"] = !lease.ending,
                ["goalId"] = lease.goalId, ["generation"] = generation, ["workEpoch"] = lease.workEpoch,
                ["userTurn"] = lease.userTurn, ["goalExecutionRevision"] = lease.goalExecutionRevision,
                ["phase"] = lease.phase, ["steps"] = lease.steps, ["stepLimit"] = SpatialGoalRunnerStepLimit,
                ["searchAttempts"] = lease.searchAttempts, ["searchLimit"] = SpatialGoalRunnerSearchLimit,
                ["latestFailureReason"] = lease.latestFailureReason, ["terminalReason"] = lease.terminalReason,
                ["receipts"] = lease.receipts.DeepClone(), ["originalUserRequest"] = lease.originalRequest,
                ["latestUserText"] = lease.latestUserText,
                ["scope"] = "One reviewed device goal owns search, approach, operation and verification. Search has no write authority. Completion requires same-entity fresh runtime proof." };
        if ((int?)m_SpatialGoalRunnerLastReport?["generation"] == generation && generation == m_FormalResponseGeneration)
            return (JObject)m_SpatialGoalRunnerLastReport.DeepClone();
        return new JObject { ["active"] = false };
    }

    private void AwaitSpatialGoalRunnerClarification(SpatialGoalRunnerLease lease, string reason)
    {
        if (!SpatialGoalRunnerBindingCurrent(lease)) return;
        // Exhausting an internal search is an execution obstacle even before a
        // target is bound. It does not establish ambiguity in the user's request.
        // Genuine referent clarification remains with the explicit goal review.
        FinishSpatialGoalRunner(lease, reason, false);
    }

    private void FinishSpatialGoalRunner(SpatialGoalRunnerLease lease, string reason, bool satisfied)
    {
        if (!SpatialGoalRunnerBindingCurrent(lease)) return;
        if (!satisfied) BlockSpatialGoal(reason);
        EndSpatialGoalRunner(lease, reason, satisfied ? "satisfied" : "blocked");
    }

    private void EndSpatialGoalRunner(SpatialGoalRunnerLease lease, string reason, string outcome)
    {
        lease.terminalReason = reason; lease.phase = outcome;
        lease.latestFailureReason = outcome == "satisfied" ? null : reason;
        m_SpatialGoalRunnerLastReport = BuildSpatialGoalRunnerContext(lease.generation);
        m_SpatialGoalRunnerLastReport["active"] = false;
        lease.ending = true; m_SpatialGoalRunner = null;
        ReleaseSpatialGoalRunnerWork(lease, reason, true);
        Debug.Log($"[SpatialGoal/Runner] goal={lease.goalId} generation={lease.generation} controller=unified-device-task outcome={outcome} steps={lease.steps} reason={reason}", this);
        DispatchSpatialGoalTerminalReply(lease.generation);
    }

    private void HandleSpatialGoalActionCancellation(string reason)
    {
        var lease = m_SpatialGoalRunner;
        bool executionFailure = (reason ?? "").IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
            (reason ?? "").IndexOf("mismatch", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason == "scene-preparation-exception" || reason == "object-approach-result-exception";
        if (executionFailure && SpatialGoalRunnerBindingCurrent(lease)) { lease.forcedFailure = reason; return; }
        CancelSpatialGoalRunnerCore(reason, false);
    }

    private void ReleaseSpatialGoalRunnerWork(SpatialGoalRunnerLease lease, string reason, bool cancelOwnedAction)
    {
        if (m_SceneSearch?.deviceGoalId == lease.goalId && m_SceneSearch.generation == lease.generation) CancelSceneSearchTask(reason);
        lease.model?.CancelRoomTaskPlanningMessage();
        if (cancelOwnedAction && lease.fact != null && ReferenceEquals(m_SceneActionFact, lease.fact) &&
            m_SceneActionActive && lease.sceneEpoch == m_SceneActionEpoch) CancelSceneAction(reason);
    }

    private void CancelSpatialGoalRunner(string reason) => CancelSpatialGoalRunnerCore(reason, true);

    private void CancelSpatialGoalRunnerCore(string reason, bool cancelOwnedAction)
    {
        var lease = m_SpatialGoalRunner; m_SpatialGoalRunner = null;
        if (lease == null) return;
        lease.ending = true;
        if (m_SpatialDeviceGoal?.id == lease.goalId) PauseSpatialGoal(reason);
        ReleaseSpatialGoalRunnerWork(lease, reason, cancelOwnedAction);
        if (lease.generation == m_FormalResponseGeneration) CancelRoomSpeechReview("spatial-goal-cancelled: " + reason);
        Debug.Log($"[SpatialGoal/Runner] goal={lease.goalId} generation={lease.generation} cancelled={reason}; no stale action or reply", this);
    }
}
