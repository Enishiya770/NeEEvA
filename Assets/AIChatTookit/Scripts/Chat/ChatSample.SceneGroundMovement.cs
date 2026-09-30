using System;
using System.Collections;
using NeEEvA.Perception;
using UnityEngine;

public partial class ChatSample
{
    private static string GroundDestinationKey(SceneGroundedTarget target)
        => target.SurfaceId + ":" + Mathf.RoundToInt(target.WorldPoint.x / .4f) + ":" +
            Mathf.RoundToInt(target.WorldPoint.y / .4f) + ":" + Mathf.RoundToInt(target.WorldPoint.z / .4f);

    private void BeginSceneGroundMovement(RoomTaskProtocol.Decision decision, SceneVisionObservation observation,
        int generation, Action completedPreparation)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        string failure = null;
        SceneGroundedTarget target = null;
        if (!m_EnableSceneActions || !RoomMotionOutputEnabled || !SceneEyesOpen) failure = "scene-action-disabled";
        else if (m_SceneActionActive || bridge == null || !bridge.IsConnected || bridge.ReservesBody || bridge.IsFollowing)
            failure = "existing-body-task-has-priority";
        else if (observation == null || decision.visualTarget == null ||
            observation.CaptureId != decision.visualTarget.captureId || !IsFreshSceneObservation(observation))
            failure = "visual-reference-not-current";
        else if (!m_SceneVisionCapture.TryResolve(observation,
            new Vector2(decision.visualTarget.x, decision.visualTarget.y), out target, out failure)) { }
        else if (decision.IsSelfInitiated && m_SceneTargetRevisitAt.TryGetValue(
            "move-to-ground:" + GroundDestinationKey(target), out float next) && Time.realtimeSinceStartup < next)
            failure = "same-visible-floor-destination-autonomous-revisit-suppressed";
        if (!string.IsNullOrEmpty(failure))
        {
            m_RoomTaskDispatch = "not-submitted: " + failure;
            if (!m_SceneActionActive) RecordSceneAction(new SceneActionFact {
                intent = decision.intent, origin = decision.origin, captureId = observation?.CaptureId,
                visualLabel = decision.visualTarget?.label }, "rejected", failure);
            completedPreparation();
            return;
        }
        var motion = new DialogueMotionIntent(decision.intent, "", generation, ++m_MotionSequence);
        RegisterMotionAction(ref motion);
        m_RoomTaskActionId = motion.ActionId;
        var fact = new SceneActionFact {
            actionId = motion.ActionId, intent = decision.intent, origin = decision.origin,
            captureId = observation.CaptureId, visualLabel = decision.visualTarget.label,
            surfaceId = target.SurfaceId, requestedImagePoint = target.ImagePoint, measuredSurface = target.WorldPoint,
            scope = "One current image point on visible floor. Measured surface is not automatically walkable: actual support, same-level complete navigation and body clearance must pass. groundArrived/reached report actual floor arrival only; resultCaptureId is a fresh resulting view, not object reidentification. No teleport, random destination or furniture-to-floor projection." };
        m_SceneActionActive = true;
        int epoch = ++m_SceneActionEpoch;
        m_PendingSceneFormalCaptureGeneration = generation;
        RecordSceneAction(fact, "grounded", "current visible surface measured; support and route not yet accepted");
        StartCoroutine(RunScenePreparationSafely(
            PrepareSceneGroundMovement(target, fact, bridge, generation, epoch, completedPreparation), fact, generation, epoch));
    }

    private IEnumerator PrepareSceneGroundMovement(SceneGroundedTarget target, SceneActionFact fact,
        ArdyRoomDialogueBridge bridge, int generation, int epoch, Action completed)
    {
        bool Current() => epoch == m_SceneActionEpoch && generation == m_FormalResponseGeneration &&
            isActiveAndEnabled && SceneEyesOpen && !m_UserSpeechActiveForAutonomy && ReferenceEquals(m_SceneActionFact, fact);
        void FinishPreparation(string dispatch)
        {
            if (!Current()) return;
            m_PendingSceneFormalCaptureGeneration = -1; m_RoomTaskDispatch = dispatch; completed();
        }
        void Reject(string reason)
        {
            if (!Current()) return;
            m_SceneActionActive = false;
            RecordSceneAction(fact, "rejected", reason);
            FinishPreparation("not-submitted: " + reason);
        }
        try
        {
            if (!Current()) yield break;
            if (!target.TryValidate(out string failure)) { Reject(failure); yield break; }
            // The current image already measured this floor. Looking down at a nearby footfall
            // is unnecessary and may exceed head limits; release the preceding view for walking.
            m_SceneAttention?.Cancel("visible-floor-ready-for-locomotion");
            float deadline = Time.realtimeSinceStartup + 2.5f;
            while (Current() && m_SceneAttention != null && m_SceneAttention.IsBusy && Time.realtimeSinceStartup < deadline) yield return null;
            if (!Current()) yield break;
            if (m_SceneAttention != null && m_SceneAttention.IsBusy) { Reject("head-release-timeout"); yield break; }
            string Validate() => !SceneEyesOpen || !isActiveAndEnabled || epoch != m_SceneActionEpoch
                ? "visible-floor-action-owner-invalidated" : target.TryValidateGeometry(out string reason) ? null : reason;
            RememberSceneTarget("move-to-ground", GroundDestinationKey(target));
            BeginSceneResultWatch(bridge, fact.actionId);
            if (!bridge.MoveToVisibleGround(fact.actionId, target.CaptureId, target.SurfaceId, target.WorldPoint, Validate, out failure))
            { EndSceneResultWatch(fact.actionId); Reject(failure); yield break; }
            RecordSceneAction(fact, "submitted", "measured floor, support, complete route and body clearance accepted; native ARDY arrival pending");
            int operation = bridge.Controller.OperationRevision;
            StartCoroutine(RunSceneGroundMovementResultSafely(
                ObserveSceneGroundMovementResult(fact, bridge, epoch, operation), fact, bridge, epoch, operation));
            FinishPreparation("ground-attempt-submitted");
        }
        finally
        {
            if (epoch == m_SceneActionEpoch && m_SceneActionActive && fact.phase == "grounded")
            {
                m_SceneActionActive = false; m_PendingSceneFormalCaptureGeneration = -1;
                RecordSceneAction(fact, "cancelled", "ground-preparation-owner-invalidated");
            }
        }
    }

    private void FailSceneGroundMovement(SceneActionFact fact, ArdyRoomDialogueBridge bridge,
        int epoch, int operation, string reason)
    {
        if (epoch != m_SceneActionEpoch || !m_SceneActionActive || !ReferenceEquals(m_SceneActionFact, fact)) return;
        // An execution failure is not a user cancellation. Emit one failed fact so a
        // continuing activity consumes its retry budget without a preceding cancelled fact.
        ++m_SceneActionEpoch;
        EndSceneResultWatch(fact.actionId);
        m_SceneActionActive = false;
        if (bridge != null && bridge.Controller != null && bridge.ActionId == fact.actionId &&
            bridge.Controller.OperationRevision == operation) bridge.StopMovement("failed", reason);
        fact.postArrivalObservationStatus = "execution-failed";
        RecordSceneAction(fact, "failed", reason);
    }

    private IEnumerator RunSceneGroundMovementResultSafely(IEnumerator routine, SceneActionFact fact,
        ArdyRoomDialogueBridge bridge, int epoch, int operation)
    {
        Exception failure = null;
        try
        {
            while (true)
            {
                bool next = false;
                try { next = routine.MoveNext(); }
                catch (Exception error) { failure = error; }
                if (failure != null || !next) break;
                yield return routine.Current;
            }
        }
        finally { (routine as IDisposable)?.Dispose(); }
        if (failure != null) FailSceneGroundMovement(fact, bridge, epoch, operation,
            "visible-floor-result-exception: " + failure.Message);
    }

    private IEnumerator ObserveSceneGroundMovementResult(SceneActionFact fact, ArdyRoomDialogueBridge bridge,
        int epoch, int operation)
    {
        bool Current() => epoch == m_SceneActionEpoch && isActiveAndEnabled && m_SceneActionActive &&
            ReferenceEquals(m_SceneActionFact, fact);
        float deadline = Time.realtimeSinceStartup + 120f;
        ArdyRoomDialogueBridge.SceneTargetResult result = null;
        while (Current() && bridge != null && Time.realtimeSinceStartup < deadline)
        {
            result = m_SceneResultActionId == fact.actionId ? m_SceneResult : null;
            if (result != null) break;
            yield return null;
        }
        if (!Current()) yield break;
        if (result == null) { FailSceneGroundMovement(fact, bridge, epoch, operation, "visible-floor-movement-result-timeout"); yield break; }
        if (!result.terminal || result.actionId != fact.actionId || result.targetKind != "visible-ground" ||
            result.motionKind != "move-to-ground" || result.captureId != fact.captureId ||
            result.targetId != fact.surfaceId || result.controllerOperationRevision != operation)
        { FailSceneGroundMovement(fact, bridge, epoch, operation, "visible-floor-result-reference-mismatch"); yield break; }
        EndSceneResultWatch(fact.actionId);
        fact.groundArrived = fact.reached = result.reached && result.status == "arrived";
        fact.postArrivalObservationStatus = fact.groundArrived ? "pending" : "not-arrived";
        RecordSceneAction(fact, fact.groundArrived ? "arrival-observing" : result.status, result.reason);
        bool SameBody() => bridge != null && bridge.Controller != null && bridge.ActionId == fact.actionId &&
            bridge.Controller.OperationRevision == operation;
        // Failure also gets one fresh view so the next decision can choose a genuinely new goal.
        if (SameBody() && SceneEyesOpen)
        {
            deadline = Time.realtimeSinceStartup + 4f;
            while (Current() && SameBody() && bridge.ReservesBody && Time.realtimeSinceStartup < deadline) yield return null;
            if (!Current()) yield break;
            if (SameBody() && !bridge.ReservesBody)
            {
                bool received = false;
                SceneVisionObservation observation = null;
                float requestedAt = Time.realtimeSinceStartup;
                RequestFreshSceneObservation("scene-ground-movement-result", value => { observation = value; received = true; });
                deadline = Time.realtimeSinceStartup + 4.5f;
                while (Current() && SameBody() && !received && Time.realtimeSinceStartup < deadline) yield return null;
                if (!Current()) yield break;
                if (SameBody() && received && observation?.HasImage == true && observation.CaptureId != fact.captureId &&
                    observation.CapturedAtRealtime >= requestedAt && IsFreshSceneObservation(observation))
                {
                    fact.resultCaptureId = observation.CaptureId;
                    fact.postArrivalObservationStatus = "new-current-view";
                }
                else fact.postArrivalObservationStatus = "new-view-unavailable-or-owner-changed";
            }
        }
        if (!Current()) yield break;
        if (fact.postArrivalObservationStatus == "pending") fact.postArrivalObservationStatus = "new-view-unavailable-or-owner-changed";
        m_SceneActionActive = false;
        RecordSceneAction(fact, result.status, result.reason);
        if (m_AgentRunning) ScheduleNextTick(Mathf.Max(2f, m_MinTickSec), "scene-ground-movement-result");
    }
}
