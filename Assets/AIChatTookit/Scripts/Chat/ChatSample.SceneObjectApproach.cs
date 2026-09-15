using System;
using System.Collections;
using System.Collections.Generic;
using NeEEvA.Motion;
using NeEEvA.Perception;
using UniVRM10;
using UnityEngine;

public partial class ChatSample
{
    [SerializeField, Range(.4f, 1f)] private float m_SceneObjectApproachObservationInterval = .7f;
    private sealed class SceneObjectApproachFocus
    {
        public bool pending, acquired, initialAcquireDone, mayAcquire = true;
        public float nextCapture, lastSeen = -1;
        public Vector3 point;
    }

    /// <summary>A committed reference to a previously seen rigid part, not continuous sight.
    /// The short picture lifetime authorizes submission; subsequent checks validate this same
    /// target geometry and scene binding independently of field of view and picture age.</summary>
    private sealed class SceneObjectApproachAnchor
    {
        public const float MaximumPositionDrift = .03f, MaximumRootRotationDrift = 3f;
        public readonly SceneObservedObject observed;
        public readonly NevaSceneVisionCapture source;
        public readonly ArdyRoomDialogueBridge bridge;
        public readonly Vrm10Instance avatar;
        public readonly Transform environment, root, parent;
        public readonly Vector3 rootPosition, rootScale, pointAtCapture, pointInRenderer;
        public readonly Quaternion rootRotation;
        public Vector3 focusRegion;
        private readonly NeEEvA.Motion.ArdyRoomLocomotionController controller;
        private readonly NeEEvA.Motion.ArdyRoomNavigation navigation;

        public SceneObjectApproachAnchor(SceneObservedObject value, ArdyRoomDialogueBridge owner)
        {
            if (value == null || !value.VisibleConfirmed || owner == null || value.Root == null || value.entry?.renderer == null)
                throw new ArgumentException("Object approach requires an actually observed opaque entity and a room owner.");
            observed = value; source = value.source; bridge = owner; avatar = owner.avatar;
            environment = owner.environmentRoot; controller = owner.Controller; navigation = owner.Navigation;
            root = value.Root; parent = root.parent; rootPosition = root.position;
            rootRotation = root.rotation; rootScale = root.lossyScale;
            pointAtCapture = value.WorldPoint;
            focusRegion = pointAtCapture;
            // Use the matrix captured with the ID image. A late animated transform cannot rewrite
            // which local point was actually seen or supply an unseen destination.
            pointInRenderer = value.entry.transformMatrix.inverse.MultiplyPoint3x4(pointAtCapture);
        }

        public string Validate()
        {
            if (source == null || !source.IsObservationBindingCurrent(observed.observation) || source.Target != avatar ||
                bridge == null || !bridge.IsConnected || bridge.avatar != avatar || bridge.environmentRoot != environment ||
                bridge.Controller != controller || bridge.Navigation != navigation || controller == null || controller.Avatar != avatar ||
                navigation == null || navigation.EnvironmentRoot != environment || avatar == null || !avatar.gameObject.activeInHierarchy)
                return "object-approach-source-or-room-binding-changed";
            if (root == null || environment == null || root.parent != parent || !root.gameObject.activeInHierarchy ||
                root.gameObject.scene != avatar.gameObject.scene || !root.gameObject.scene.isLoaded ||
                (root != environment && !root.IsChildOf(environment)))
                return "object-approach-entity-binding-changed";
            if (!observed.entry.observedRigid) return "object-approach-geometry-invalid: selected part is not a rigid observed mesh";
            if (!observed.entry.TryValidateObserved(false, out string reason))
                return "object-approach-geometry-invalid: " + reason;
            if (!Finite(root.position) || !Finite(root.lossyScale) ||
                Vector3.Distance(root.position, rootPosition) > MaximumPositionDrift ||
                Quaternion.Angle(root.rotation, rootRotation) > MaximumRootRotationDrift ||
                Vector3.Distance(root.lossyScale, rootScale) > .002f * Mathf.Max(1f, rootScale.magnitude))
                return "object-approach-target-moved: entity frame changed; reobserve before a new attempt";
            Vector3 currentPoint = observed.entry.renderer.transform.TransformPoint(pointInRenderer);
            if (!Finite(currentPoint) || Vector3.Distance(currentPoint, pointAtCapture) > MaximumPositionDrift)
                return "object-approach-target-moved: visible part left the committed 3 cm reference envelope";
            return null;
        }
        private static bool Finite(Vector3 point) => !float.IsNaN(point.x) && !float.IsInfinity(point.x) &&
            !float.IsNaN(point.y) && !float.IsInfinity(point.y) && !float.IsNaN(point.z) && !float.IsInfinity(point.z);
    }

    // Planning uses the imported neutral pose, never the already turned observation head.
    // A region can remain observable off the camera centre while the native head stays bounded.
    private static Vector3 SceneObjectNeutralEyeOffset(ArdyRoomDialogueBridge bridge, NevaEyeTracking eyes)
    {
        var head = bridge.importedReference.Get(HumanBodyBones.Head);
        Vector3 rootScale = bridge.avatar.transform.lossyScale, boneScale = eyes.Head.lossyScale;
        Vector3 relativeScale = new Vector3(boneScale.x / rootScale.x, boneScale.y / rootScale.y, boneScale.z / rootScale.z);
        Vector3 localEye = head.positionInRoot + head.rotationInRoot *
            Vector3.Scale(eyes.Head.InverseTransformPoint(eyes.EyeOrigin), relativeScale);
        return Vector3.Scale(localEye - Vector3.up * bridge.importedReference.groundY, rootScale);
    }

    private static float SceneObjectViewSlack(float verticalFov)
        => Mathf.Clamp(verticalFov * .5f - 8f, 0f, 8f);

    private static float SceneObjectObservationDistance(Vector3 point, float groundY, Vector3 neutralEyeOffset,
        float up, float down, float viewSlack, float arrivalTolerance)
    {
        float delta = point.y - (groundY + neutralEyeOffset.y);
        // Keep 3 degrees of neck reserve. Off-centre visibility uses at most 8 degrees of the
        // actual camera's inner view; it does not increase a bone/eye rotation limit.
        float angle = Mathf.Max(1f, (delta >= 0 ? up : down) - 3f) + viewSlack;
        float axial = Mathf.Abs(delta) / Mathf.Tan(angle * Mathf.Deg2Rad);
        float along = Mathf.Sqrt(Mathf.Max(0f, axial * axial - neutralEyeOffset.x * neutralEyeOffset.x));
        return Mathf.Max(.8f, along + neutralEyeOffset.z + arrivalTolerance + .05f);
    }

    private static float SceneObjectStationPitch(Vector3 point, Vector3 ground, Vector3 neutralEyeOffset,
        float arrivalTolerance)
    {
        Vector3 forward = point - ground; forward.y = 0;
        if (forward.sqrMagnitude < .000001f) return 90f;
        Quaternion facing = Quaternion.LookRotation(forward.normalized, Vector3.up);
        Vector3 eye = ground + facing * neutralEyeOffset;
        // Arrival tolerance is a possible forward stop error, not an unknown eye offset radius.
        eye += forward.normalized * arrivalTolerance;
        Vector3 delta = point - eye;
        return Mathf.Atan2(delta.y, new Vector2(delta.x, delta.z).magnitude) * Mathf.Rad2Deg;
    }

    private bool TrySelectSceneObjectApproachReference(SceneObservedObject selected, SceneVisionObservation observation,
        ArdyRoomDialogueBridge bridge, out SceneObservedObject reference, out float standOff, out string failure,
        Vector3? intendedRegion = null)
    {
        var scanTimer = System.Diagnostics.Stopwatch.StartNew();
        reference = null; standOff = 0; failure = null;
        if (selected == null || !selected.VisibleConfirmed || selected.CaptureId != observation?.CaptureId ||
            bridge == null || bridge.avatar == null || bridge.importedReference == null || bridge.Controller == null)
        { failure = "object-observation-station-source-unavailable"; return false; }
        var eyes = bridge.avatar.GetComponent<NevaEyeTracking>();
        if (eyes == null || !eyes.IsInitialized || !eyes.isActiveAndEnabled)
        { failure = "object-observation-station-eye-rig-unavailable"; return false; }
        Vector3 neutralEye = SceneObjectNeutralEyeOffset(bridge, eyes);
        Vector3 currentGround = bridge.avatar.transform.position + Vector3.up *
            bridge.importedReference.groundY * bridge.avatar.transform.lossyScale.y;
        Vector3 region = intendedRegion ?? selected.WorldPoint;
        Vector3 localRegion = Quaternion.Inverse(observation.CameraRotation) * (region - observation.CameraPosition);
        float regionX = localRegion.z > .01f ? .5f + localRegion.x / (2f * localRegion.z *
            Mathf.Tan(observation.VerticalFov * Mathf.Deg2Rad * .5f) * observation.Width / observation.Height) : selected.ImagePoint.x;
        float up = Mathf.Clamp(m_SceneAttention != null ? m_SceneAttention.maximumUpDegrees : 25f, 5, 30);
        float down = Mathf.Clamp(m_SceneAttention != null ? m_SceneAttention.maximumDownDegrees : 30f, 5, 35);
        float viewSlack = SceneObjectViewSlack(observation.VerticalFov);
        float bestScore = float.PositiveInfinity, minimumRequired = float.PositiveInfinity;
        int opaquePixels = 0, regionPixels = 0, angleRejected = 0;
        SceneObservedObject best = null; float bestStandOff = 0;
        void Consider(SceneObservedObject value)
        {
            if (value == null || !value.VisibleConfirmed || value.EntityId != selected.EntityId || value.Root != selected.Root ||
                value.CaptureId != observation.CaptureId || !value.TryValidate(out _)) return;
            float offset = new Vector2(value.WorldPoint.x - region.x, value.WorldPoint.z - region.z).magnitude;
            if (offset > .5f || Mathf.Abs(value.ImagePoint.x - regionX) > .18f) return;
            regionPixels++;
            float required = SceneObjectObservationDistance(value.WorldPoint, currentGround.y, neutralEye,
                up, down, viewSlack, bridge.Controller.ArrivalTolerance);
            minimumRequired = Mathf.Min(minimumRequired, required);
            if (required > 2f || float.IsNaN(required) || float.IsInfinity(required)) { angleRejected++; return; }
            float delta = value.WorldPoint.y - (currentGround.y + neutralEye.y);
            float score = required + Mathf.Abs(delta) * .25f + offset * .15f;
            if (score >= bestScore) return;
            bestScore = score; best = value; bestStandOff = required;
        }
        Consider(selected);
        // Scan this entity's actual ID pixels, including thin tabletops and small parts that a
        // 5%-spaced screen grid skipped. Bounds/names never supply an unseen point. Only call the
        // full resolver for potentially better points; it retains opaque and 3x3 edge validation.
        float pixelScore = bestScore; Vector2 bestPixel = default; bool hasBestPixel = false;
        var frame = observation.Grounding;
        if (frame?.depthIds != null && frame.entries != null)
        {
            var eligible = new bool[frame.entries.Length];
            for (int i = 0; i < eligible.Length; i++)
                eligible[i] = frame.entries[i].entityId == selected.EntityId && frame.entries[i].identityRoot == selected.Root &&
                    frame.entries[i].observedRigid && frame.entries[i].TryValidateObserved(false, out _);
            float tangent = Mathf.Tan(observation.VerticalFov * Mathf.Deg2Rad * .5f);
            for (int y = 1; y < frame.height - 1; y++) for (int x = 1; x < frame.width - 1; x++)
            {
                float px = (x + .5f) / frame.width;
                if (Mathf.Abs(px - regionX) > .18f) continue;
                int sample = (y * frame.width + x) * 2, id = Mathf.RoundToInt(frame.depthIds[sample + 1]);
                if (!SceneVisionGrounding.ObjectEntryId(id, eligible.Length, out int entryIndex, out bool opaque) ||
                    !opaque || !eligible[entryIndex]) continue;
                opaquePixels++;
                float depth = frame.depthIds[sample];
                if (!SceneVisionGrounding.ConsistentObjectPixel(frame, x, y, id, depth)) continue;
                float py = frame.rowsStartAtTop ? (y + .5f) / frame.height : 1f - (y + .5f) / frame.height;
                Vector3 local = new Vector3((px * 2f - 1f) * tangent * frame.width / frame.height * depth,
                    (1f - py * 2f) * tangent * depth, depth);
                Vector3 world = observation.CameraPosition + observation.CameraRotation * local;
                float offset = new Vector2(world.x - region.x, world.z - region.z).magnitude;
                if (offset > .5f) continue;
                float required = SceneObjectObservationDistance(world, currentGround.y, neutralEye, up, down, viewSlack,
                    bridge.Controller.ArrivalTolerance);
                regionPixels++; minimumRequired = Mathf.Min(minimumRequired, required);
                if (required > 2f) { angleRejected++; continue; }
                float score = required + Mathf.Abs(world.y - currentGround.y - neutralEye.y) * .25f + offset * .15f;
                if (score >= pixelScore) continue;
                pixelScore = score; bestPixel = new Vector2(px, py); hasBestPixel = true;
            }
        }
        if (hasBestPixel && m_SceneVisionCapture.TryResolveObservedObject(observation, bestPixel, out var bestValue, out _)) Consider(bestValue);
        scanTimer.Stop();
        if (best == null)
        {
            failure = regionPixels == 0 ? "object-observation-region-not-visible: no stable opaque pixel in the original attention region" :
                "object-observation-station-outside-head-range: bounded head plus inner camera view still requires over 2 m";
            Debug.Log("[Vision/ApproachStation] " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                captureId=observation.CaptureId, entityId=selected.EntityId, opaquePixels, regionPixels, angleRejected,
                minimumRequired = float.IsInfinity(minimumRequired) ? -1 : minimumRequired,
                scanMilliseconds=scanTimer.Elapsed.TotalMilliseconds, neutralEyeHeight=neutralEye.y, neutralEyeForward=neutralEye.z, groundY=currentGround.y,
                up, down, viewSlack, reason=failure }));
            return false;
        }
        reference = best; standOff = bestStandOff;
        Debug.Log("[Vision/ApproachFocusSelection] " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
            captureId=observation.CaptureId, entityId=selected.EntityId, opaquePixels, regionPixels,
            scanMilliseconds=scanTimer.Elapsed.TotalMilliseconds, exactResolverCalls=hasBestPixel ? 1 : 0,
            standOff=bestStandOff, scope="Only this frame's same-entity pixels within the original 0.5 m XZ attention region; no scene search." }));
        return true;
    }

    // Called only after the object pipeline has actually looked, captured a different image,
    // re-identified this entity and completed its semantic target check.
    private IEnumerator SubmitConfirmedSceneObjectApproach(RoomTaskProtocol.Decision decision,
        SceneObservedObject selected, SceneVisionObservation confirmedObservation, SceneActionFact fact,
        ArdyRoomDialogueBridge bridge, int generation, int epoch, Action completed)
    {
        int workEpoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn;
        bool Current() => epoch == m_SceneActionEpoch && generation == m_FormalResponseGeneration &&
            workEpoch == m_WorkEpoch && userTurn == m_ActionConstraints.CurrentUserTurn &&
            isActiveAndEnabled && SceneEyesOpen && !m_UserSpeechActiveForAutonomy && ReferenceEquals(m_SceneActionFact, fact);
        void FinishPreparation(string dispatch)
        {
            if (!Current()) return;
            m_PendingSceneFormalCaptureGeneration = -1; m_RoomTaskDispatch = dispatch; completed();
        }
        void Reject(string reason)
        {
            if (!Current()) return;
            m_SceneAttention?.Release(fact.actionId, reason); m_SceneActionActive = false;
            RecordSceneAction(fact, "rejected", reason); FinishPreparation("not-submitted: " + reason);
        }
        if (!Current()) yield break;
        if (!m_EnableSceneActions || !m_EnableSceneObjectAwareness || !RoomMotionOutputEnabled ||
            (decision.intent != "approach-object" && !decision.IsViewPosition) || selected == null || !selected.VisibleConfirmed || confirmedObservation == null ||
            selected.CaptureId != confirmedObservation.CaptureId || fact.confirmedCaptureId != confirmedObservation.CaptureId ||
            confirmedObservation.CaptureId == fact.captureId || !IsFreshSceneObservation(confirmedObservation) ||
            !selected.TryValidate(out _))
        { Reject("confirmed-object-approach-reference-unavailable"); yield break; }
        if (bridge == null || !bridge.IsConnected || bridge.ReservesBody || bridge.IsFollowing)
        { Reject("existing-body-task-has-priority"); yield break; }
        string revisitKind = decision.IsViewPosition ? "reposition-view:" + decision.viewPosition.direction : "approach-object";
        if (decision.origin == "autonomous" && m_SceneTargetRevisitAt.TryGetValue(revisitKind + ":" + selected.EntityId, out float next) &&
            Time.realtimeSinceStartup < next)
        { Reject("same-visible-entity-autonomous-revisit-suppressed"); yield break; }
        float standOff = 0;
        if (!decision.IsViewPosition)
        {
            if (!TrySelectSceneObjectApproachReference(selected, confirmedObservation, bridge, out var observationReference,
                out standOff, out string selectionFailure, fact.hasIntentFocus ? fact.intentFocusWorldPoint : (Vector3?)null))
            { Reject(selectionFailure); yield break; }
            selected = observationReference;
        }
        var anchor = new SceneObjectApproachAnchor(selected, bridge);
        anchor.focusRegion = fact.hasIntentFocus ? fact.intentFocusWorldPoint : anchor.pointAtCapture;
        string failure = anchor.Validate();
        if (failure != null) { Reject(failure); yield break; }
        fact.entityId = selected.EntityId; fact.objectConfirmed = true; fact.measuredSurface = selected.WorldPoint;
        fact.navigationFocusImagePoint = selected.ImagePoint; fact.observationStandOff = standOff;
        fact.navigationFocusAdjusted = fact.hasIntentFocus && Vector3.Distance(selected.WorldPoint, fact.intentFocusWorldPoint) > .05f;
        fact.groundArrived = fact.objectReobserved = fact.reached = false;
        fact.postArrivalObservationStatus = "not-arrived";
        fact.trackingVisibility = "not-sampled-during-movement";
        fact.trackingCaptureId = selected.CaptureId; fact.trackingFocusWorldPoint = selected.WorldPoint;
        fact.trackingConfirmedImages = fact.trackingMovingConfirmedImages = 0;
        fact.trackingLastSeenRealtime = confirmedObservation.CapturedAtRealtime;
        if (decision.IsViewPosition)
        {
            fact.viewDirection = decision.viewPosition.direction; fact.viewPurpose = decision.viewPosition.purpose;
            fact.requestedViewDistance = decision.viewPosition.distance;
            fact.viewPurposeStatus = "awaiting-movement-and-new-image";
            fact.viewBefore = SceneObjectViewSample.Measure(confirmedObservation, selected);
        }
        fact.scope = "Approach of an exact previously observed entity to a validated nearby same-level floor station. " +
            "groundArrived/reached measure that historical floor arrival only; objectReobserved requires the same entity in a real new image after arrival. " +
            "Navigation focus is an actually visible same-entity point within 0.5 m XZ of the intended region, with bounded vertical adjustment. The head remains within its original limits; up to 8 degrees of the actual inner camera view may retain an off-centre target. Viewability is distinct from exact gaze centering. " +
            "trackingVisibility describes intermittent local images, not uninterrupted sight between samples. " +
            "No hand contact, device operation, target-height foot placement or knowledge through occlusion is implied.";
        if (decision.IsViewPosition) fact.scope = "Same-floor observation repositioning while facing the confirmed target. " +
            "The requested direction and distance are relative to the actor facing that target. Actual arrival and same-entity reobservation are separate facts. " +
            "viewBefore/viewAfter measure only rendered opaque pixels; smaller/larger bounds or no edge clipping do not prove the whole object is visible, text is readable or occlusion is absent. Assess the new RGB against viewPurpose; no touching or stairs.";
        m_SceneAttention?.Release(fact.actionId, "confirmed-object-ready-for-locomotion");
        float deadline = Time.realtimeSinceStartup + 2.5f;
        while (Current() && m_SceneAttention != null && m_SceneAttention.IsBusy && Time.realtimeSinceStartup < deadline) yield return null;
        if (!Current()) yield break;
        if (m_SceneAttention != null && m_SceneAttention.IsBusy) { Reject("head-release-timeout"); yield break; }
        failure = anchor.Validate();
        if (failure != null) { Reject(failure); yield break; }
        Vector3 currentGround = bridge.avatar.transform.position + Vector3.up * bridge.importedReference.groundY * bridge.avatar.transform.lossyScale.y;
        ArdyRoomObjectApproachPlan observationPlan;
        bool planned = decision.IsViewPosition
            ? ArdyRoomViewPositionPlanner.TryPlan(bridge.Navigation, currentGround, anchor.pointAtCapture, bridge.userHead,
                decision.viewPosition.direction, decision.viewPosition.distance, out observationPlan, out failure, out _)
            : ArdyRoomObjectApproachPlanner.TryPlan(bridge.Navigation, currentGround, anchor.pointAtCapture, bridge.userHead,
                standOff, out observationPlan, out failure, out _);
        if (!planned)
        { Reject("object-observation-station-unreachable: " + failure); yield break; }
        var currentEyes = bridge.avatar.GetComponent<NevaEyeTracking>();
        Vector3 eyeOffset = SceneObjectNeutralEyeOffset(bridge, currentEyes);
        float pitch = SceneObjectStationPitch(anchor.pointAtCapture, observationPlan.GroundTarget,
            eyeOffset, bridge.Controller.ArrivalTolerance);
        float upRange = Mathf.Clamp(m_SceneAttention != null ? m_SceneAttention.maximumUpDegrees : 25f, 5, 30);
        float downRange = Mathf.Clamp(m_SceneAttention != null ? m_SceneAttention.maximumDownDegrees : 30f, 5, 35);
        float viewSlack = SceneObjectViewSlack(confirmedObservation.VerticalFov);
        float residual = Mathf.Max(0f, Mathf.Abs(pitch) - Mathf.Max(1f, (pitch >= 0 ? upRange : downRange) - 3f));
        Debug.Log("[Vision/ApproachStation] " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
            actionId=fact.actionId, captureId=confirmedObservation.CaptureId, entityId=selected.EntityId,
            standOff, pitch, residual, viewSlack, canCenter=Mathf.Abs(pitch) <= (pitch >= 0 ? upRange : downRange),
            regionPreserved=true, scope="Predicted neutral eye at the legal floor station; native head limits unchanged. Off-centre region visibility is not exact gaze centering or actual arrival." }));
        if (residual > viewSlack + .01f)
        { Reject("object-observation-station-outside-head-range: planned floor does not preserve the focus inside the bounded actual view"); yield break; }
        fact.predictedObservationPitch = pitch;
        RememberSceneTarget(revisitKind, selected.EntityId);
        BeginSceneResultWatch(bridge, fact.actionId);
        bool submitted = decision.IsViewPosition
            ? bridge.RepositionSceneTarget(fact.actionId, selected.CaptureId, selected.EntityId, anchor.pointAtCapture,
                decision.viewPosition.direction, decision.viewPosition.distance, decision.viewPosition.purpose, anchor.Validate, out failure)
            : bridge.ApproachSceneTarget(fact.actionId, selected.CaptureId, selected.EntityId,
                anchor.pointAtCapture, anchor.Validate, out failure, standOff);
        if (!submitted)
        { EndSceneResultWatch(fact.actionId); Reject(failure); yield break; }
        RecordSceneAction(fact, "submitted", decision.IsViewPosition
            ? "same entity committed; requested relative observation station and route checked; target-facing translation and fresh-image assessment pending"
            : "same entity committed; legal nearby ground and full route checked; actual arrival pending");
        StartCoroutine(RunSceneObjectApproachResultSafely(
            ObserveSceneObjectApproachResult(fact, anchor, epoch, bridge.Controller.OperationRevision), fact, epoch));
        FinishPreparation(decision.IsViewPosition ? "view-position-attempt-submitted" : "object-attempt-submitted");
    }

    private IEnumerator RunSceneObjectApproachResultSafely(IEnumerator routine, SceneActionFact fact, int epoch)
    {
        var stack = new Stack<IEnumerator>(); stack.Push(routine);
        Exception failure = null;
        try
        {
            while (stack.Count > 0)
            {
                object yielded = null;
                try
                {
                    while (stack.Count > 0)
                    {
                        var top = stack.Peek();
                        if (!top.MoveNext()) { (top as IDisposable)?.Dispose(); stack.Pop(); continue; }
                        if (top.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        yielded = top.Current; break;
                    }
                }
                catch (Exception error) { failure = error; break; }
                if (stack.Count > 0) yield return yielded;
            }
        }
        finally { while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose(); }
        if (failure == null || epoch != m_SceneActionEpoch || !ReferenceEquals(m_SceneActionFact, fact)) yield break;
        CancelSceneAction("object-approach-result-exception");
        RecordSceneAction(fact, fact.groundArrived ? "arrived-object-unconfirmed" : "failed", failure.Message);
    }

    private IEnumerator ObserveSceneObjectApproachResult(SceneActionFact fact, SceneObjectApproachAnchor anchor, int epoch, int submittedOperation)
    {
        bool Current() => epoch == m_SceneActionEpoch && isActiveAndEnabled && m_SceneActionActive &&
            ReferenceEquals(m_SceneActionFact, fact);
        void Finish(string phase, string reason)
        {
            if (!Current()) return;
            if (fact.intent == "reposition-view" && !fact.objectReobserved)
                fact.viewPurposeStatus = fact.groundArrived ? "arrived-without-current-target-image" : "movement-not-completed";
            m_SceneAttention?.ReleaseLocomotionFocus(fact.actionId, "scene-object-attempt-ended");
            EndSceneResultWatch(fact.actionId); m_SceneActionActive = false;
            RecordSceneAction(fact, phase, reason);
            if (m_AgentRunning) ScheduleNextTick(Mathf.Max(2f, m_MinTickSec), "scene-object-approach-result");
        }
        void Unconfirmed(string status, string reason)
        {
            fact.objectReobserved = false; fact.postArrivalObservationStatus = status;
            m_SceneAttention?.Release(fact.actionId, reason);
            Finish("arrived-object-unconfirmed", reason);
        }
        float deadline = Time.realtimeSinceStartup + 120f;
        var focus = new SceneObjectApproachFocus { point = anchor.pointAtCapture, lastSeen = anchor.observed.observation.CapturedAtRealtime };
        ArdyRoomDialogueBridge.SceneTargetResult result = null;
        while (Current() && Time.realtimeSinceStartup < deadline)
        {
            result = m_SceneResultActionId == fact.actionId ? m_SceneResult : null;
            if (result != null && result.actionId == fact.actionId) break;
            TickSceneObjectApproachFocus(fact, anchor, submittedOperation, focus,
                () => Current() && anchor.bridge != null && anchor.bridge.Controller != null &&
                    anchor.bridge.Controller.OperationRevision == submittedOperation && anchor.bridge.ActionId == fact.actionId);
            yield return null;
        }
        if (!Current()) yield break;
        m_SceneAttention?.ReleaseLocomotionFocus(fact.actionId, "movement-terminal-reobserve-current-view");
        if (result == null) { CancelSceneAction("scene-object-approach-result-timeout"); yield break; }
        EndSceneResultWatch(fact.actionId);
        if (!result.terminal || result.targetKind != "scene-object" || result.targetId != anchor.observed.EntityId ||
            result.captureId != anchor.observed.CaptureId || result.controllerOperationRevision != submittedOperation ||
            (fact.intent == "reposition-view" && (result.motionKind != fact.intent ||
                result.requestedDirection != fact.viewDirection || result.viewPurpose != fact.viewPurpose ||
                Mathf.Abs(result.requestedDistance - fact.requestedViewDistance) > .0001f)))
        {
            CancelSceneAction("scene-object-result-reference-mismatch");
            yield break;
        }
        fact.groundArrived = fact.reached = result.reached && result.status == "arrived";
        if (fact.intent == "reposition-view") fact.actualViewOffset = result.actualOffset;
        if (!fact.groundArrived)
        {
            fact.postArrivalObservationStatus = "not-arrived";
            Finish(result.status, result.reason); yield break;
        }
        fact.postArrivalObservationStatus = "pending";
        RecordSceneAction(fact, "arrival-observing", "actual floor station and facing reached; same-entity sight has not yet been confirmed");
        bool SameCompletedOperation() => anchor.bridge != null && anchor.bridge.Controller != null &&
            anchor.bridge.Controller.OperationRevision == result.controllerOperationRevision && anchor.bridge.ActionId == fact.actionId;
        if (!SameCompletedOperation())
        { Unconfirmed("body-owner-changed", "floor arrival occurred before another body operation replaced this attempt"); yield break; }
        string failure = anchor.Validate();
        if (failure != null) { Unconfirmed("target-invalidated", failure); yield break; }
        deadline = Time.realtimeSinceStartup + 4f;
        while (Current() && SameCompletedOperation() && anchor.bridge.ReservesBody && Time.realtimeSinceStartup < deadline) yield return null;
        if (!Current()) yield break;
        if (!SameCompletedOperation())
        { Unconfirmed("body-owner-changed", "floor arrival retained; a newer body operation owns subsequent movement"); yield break; }
        if (anchor.bridge.ReservesBody || anchor.bridge.IsFollowing)
        { Unconfirmed("body-owner-unavailable", "floor arrival occurred but body ownership was not available for reobservation"); yield break; }
        if (!SceneEyesOpen || m_SceneVisionCapture != anchor.source)
        { Unconfirmed("source-unavailable", "floor arrival occurred but current scene sight is unavailable"); yield break; }
        failure = anchor.Validate();
        if (failure != null) { Unconfirmed("target-invalidated", failure); yield break; }
        // Inspect the actual arrived view first. A low object may require a bounded downward
        // orientation after the walking overlay returns. A direction is not an identity proof:
        // only the following fresh image can establish that the same entity is seen again.
        SceneVisionObservation observation = null;
        SceneObservedObject seen = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool received = false;
            float requestedAt = Time.realtimeSinceStartup;
            RequestSceneObservation("scene-object-arrival-confirm", value => { observation = value; received = true; });
            deadline = Time.realtimeSinceStartup + 4.5f;
            while (Current() && SameCompletedOperation() && !received && Time.realtimeSinceStartup < deadline) yield return null;
            if (!Current()) yield break;
            if (!SameCompletedOperation())
            { Unconfirmed("body-owner-changed", "a newer body operation superseded this arrival image"); yield break; }
            failure = anchor.Validate();
            if (failure != null) { Unconfirmed("target-invalidated", failure); yield break; }
            if (!received || observation == null || !observation.HasImage || observation.CaptureId == fact.confirmedCaptureId ||
                observation.CapturedAtRealtime < requestedAt || !IsFreshSceneObservation(observation))
            { Unconfirmed("new-image-unavailable", "actual floor arrival retained; a new compatible image after arrival is unavailable"); yield break; }
            fact.resultCaptureId = observation.CaptureId;
            if (anchor.source.TryFindObservedObject(observation, fact.entityId, out seen, out failure) &&
                seen.VisibleConfirmed && seen.Root == anchor.root && seen.TryValidate(out failure)) break;
            seen = null;
            var eyes = anchor.avatar.GetComponent<NevaEyeTracking>();
            if (attempt != 0 || eyes == null || m_SceneAttention == null) break;
            Vector3 local = Quaternion.Inverse(eyes.HeadFrameRotation) * (anchor.pointAtCapture - eyes.EyeOrigin);
            if (local.z <= .025f) break;
            float yaw = Mathf.Clamp(Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg,
                -Mathf.Min(45f, m_SceneAttention.maximumYawDegrees - 3f), Mathf.Min(45f, m_SceneAttention.maximumYawDegrees - 3f));
            float pitch = Mathf.Clamp(Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg,
                -Mathf.Min(25f, m_SceneAttention.maximumDownDegrees - 3f), Mathf.Min(25f, m_SceneAttention.maximumUpDegrees - 3f));
            if (!m_SceneAttention.TryOrientView(fact.actionId, yaw, pitch, out _)) break;
            RecordSceneAction(fact, "arrival-observing", "floor arrival retained; making one bounded view change toward the previously seen region, which is not yet reidentified");
            deadline = Time.realtimeSinceStartup + 4.5f;
            while (Current() && SameCompletedOperation() && m_SceneAttention.IsBusy && !m_SceneAttention.IsSettled &&
                Time.realtimeSinceStartup < deadline) yield return null;
            if (!Current()) yield break;
            if (!SameCompletedOperation())
            { Unconfirmed("body-owner-changed", "newer movement owns the body during arrival view adjustment"); yield break; }
            if (!m_SceneAttention.IsSettled) break;
        }
        if (seen == null)
        { Unconfirmed("same-entity-not-visible", "actual floor arrival retained; same object could not be confirmed after the bounded arrival observation: " + failure); yield break; }
        if (anchor.Validate() != null) { Unconfirmed("target-invalidated", "object reference changed during arrival reobservation"); yield break; }
        fact.objectReobserved = true; fact.postArrivalObservationStatus = "same-entity-in-new-image";
        if (fact.intent == "reposition-view")
        {
            fact.viewAfter = SceneObjectViewSample.Measure(observation, seen);
            fact.viewPurposeStatus = "new-image-ready-for-visual-assessment";
        }
        var device = seen.Root.GetComponent<SceneObjectLightSwitch>();
        if (device != null && device.EntityId != seen.EntityId) device = null;
        RememberSceneObject(seen, device);
        m_SceneAttention?.RetainView(fact.actionId, 20f);
        Finish("arrived", fact.intent == "reposition-view"
            ? "requested relative floor station and target facing reached; the same entity is visible in a new image. Compare the current RGB against viewPurpose; readability, whole-object visibility and occlusion improvement are not established by arrival alone"
            : "actual nearby floor station and facing reached; the same entity is visible in a new arrival image; no touching or device operation");
    }

    private void TickSceneObjectApproachFocus(SceneActionFact fact, SceneObjectApproachAnchor anchor, int operation,
        SceneObjectApproachFocus state, Func<bool> current)
    {
        if (!current()) return;
        float now = Time.realtimeSinceStartup;
        var controller = anchor.bridge.Controller;
        fact.trackingFocusLastAppliedFrame = m_SceneAttention != null ? m_SceneAttention.LocomotionFocusLastAppliedFrame : -1;
        fact.trackingFocusAppliedDegrees = m_SceneAttention != null ? m_SceneAttention.LocomotionFocusAppliedDegrees : 0f;
        bool playing = controller.Player != null && controller.Player.IsPlaying;
        // One initial commitment can acquire gaze as native playback becomes available. After
        // a negative/expired sample, only a NEW visible image can authorize reacquisition.
        if (playing && state.mayAcquire && !state.initialAcquireDone && m_SceneAttention != null)
        {
            state.acquired = m_SceneAttention.TryBeginLocomotionFocus(fact.actionId, state.point, controller, operation, out string reason);
            fact.trackingFocusStatus = state.acquired ? "native-focus-lease-accepted" : reason;
            if (state.acquired) state.initialAcquireDone = true;
        }
        if (!state.pending && now >= state.nextCapture)
        {
            state.pending = true;
            state.nextCapture = now + Mathf.Clamp(m_SceneObjectApproachObservationInterval, .4f, 1f);
            float requestedAt = now;
            RequestSceneObservation("scene-object-moving-focus", observation => {
                state.pending = false;
                if (!current()) return;
                string failure = null; SceneObservedObject seen = null;
                bool fresh = observation != null && observation.HasImage && observation.CapturedAtRealtime >= requestedAt &&
                    IsFreshSceneObservation(observation);
                bool visible = fresh && TryFindSceneObjectApproachFocus(anchor, observation, state.point, out seen, out failure);
                fact.trackingVisibility = visible ? "visible" : fresh ? "not-visible" : "unavailable";
                fact.trackingCaptureId = fresh ? observation.CaptureId : "";
                state.mayAcquire = visible;
                if (visible)
                {
                    state.lastSeen = observation.CapturedAtRealtime; state.point = seen.WorldPoint;
                    fact.trackingLastSeenRealtime = state.lastSeen; fact.trackingFocusWorldPoint = state.point;
                    fact.trackingConfirmedImages++;
                    if (controller.IsMoving && controller.Player != null && controller.Player.IsPlaying) fact.trackingMovingConfirmedImages++;
                    if (m_SceneAttention != null && controller.Player != null && controller.Player.IsPlaying)
                    {
                        bool applied = state.acquired && m_SceneAttention.UpdateLocomotionFocusPoint(
                            fact.actionId, state.point, controller, operation, out failure);
                        if (!applied) applied = m_SceneAttention.TryBeginLocomotionFocus(
                            fact.actionId, state.point, controller, operation, out failure);
                        state.acquired = applied; state.initialAcquireDone = applied;
                        fact.trackingFocusStatus = applied ? "native-focus-lease-accepted" : failure;
                    }
                }
                else
                {
                    m_SceneAttention?.ReleaseLocomotionFocus(fact.actionId, fresh ? "same-entity-not-visible" : "current-image-unavailable");
                    state.acquired = false; state.initialAcquireDone = true;
                    fact.trackingFocusStatus = fresh ? "released-target-not-visible" : "released-observation-unavailable";
                }
                Debug.Log("[Vision/ApproachFocus] " + Newtonsoft.Json.JsonConvert.SerializeObject(new {
                    actionId = fact.actionId, entityId = fact.entityId, operation, captureId = fact.trackingCaptureId,
                    visibility = fact.trackingVisibility, focusStatus = fact.trackingFocusStatus,
                    actualFocusAppliedFrame = m_SceneAttention != null ? m_SceneAttention.LocomotionFocusLastAppliedFrame : -1,
                    actualFocusAppliedDegrees = m_SceneAttention != null ? m_SceneAttention.LocomotionFocusAppliedDegrees : 0f,
                    point = new { x = fact.trackingFocusWorldPoint.x, y = fact.trackingFocusWorldPoint.y, z = fact.trackingFocusWorldPoint.z },
                    confirmedImages = fact.trackingConfirmedImages,
                    sourceTime = fresh ? observation.CapturedAtRealtime : -1, reason = failure
                }), this);
            });
        }
        // Visibility loss withdraws its gaze lease; a still-valid remembered target and audited
        // route can continue around an obstacle. Only target/scene/terrain guards stop movement.
    }

    private bool TryFindSceneObjectApproachFocus(SceneObjectApproachAnchor anchor, SceneVisionObservation observation,
        Vector3 previousFocus, out SceneObservedObject seen, out string failure)
    {
        seen = null; failure = null;
        Vector3 local = Quaternion.Inverse(observation.CameraRotation) * (previousFocus - observation.CameraPosition);
        float tangent = Mathf.Tan(observation.VerticalFov * Mathf.Deg2Rad * .5f);
        if (local.z > .02f)
        {
            var pixel = new Vector2(.5f + local.x / (2f * local.z * tangent * observation.Width / observation.Height),
                .5f - local.y / (2f * local.z * tangent));
            if (pixel.x >= 0 && pixel.x <= 1 && pixel.y >= 0 && pixel.y <= 1 &&
                anchor.source.TryResolveObservedObject(observation, pixel, out var near, out _) &&
                near.VisibleConfirmed && near.EntityId == anchor.observed.EntityId && near.Root == anchor.root && near.TryValidate(out _) &&
                new Vector2(near.WorldPoint.x - anchor.focusRegion.x, near.WorldPoint.z - anchor.focusRegion.z).magnitude <= .5f)
            { seen = near; return true; }
        }
        if (!anchor.source.TryFindObservedObject(observation, anchor.observed.EntityId, out var any, out failure)) return false;
        // Reacquire only this original target region, never a far end of a long cabinet/wall.
        return TrySelectSceneObjectApproachReference(any, observation, anchor.bridge, out seen, out _, out failure, anchor.focusRegion);
    }
}
