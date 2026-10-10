using System;
using System.Collections;
using System.Collections.Generic;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    [SerializeField, Tooltip("允许依据当前角色视野关注目标，并在房间导航审核后走近。")]
    private bool m_EnableSceneActions = true;
    private NevaSceneAttention m_SceneAttention;
    private int m_SceneActionEpoch;
    private bool m_SceneActionActive;
    private string m_SceneAutonomyIntention = "";
    private SceneActionFact m_SceneActionFact;
    private ArdyRoomDialogueBridge m_SceneResultBridge;
    private string m_SceneResultActionId;
    private ArdyRoomDialogueBridge.SceneTargetResult m_SceneResult;
    private readonly Queue<float> m_SceneViewStarts = new Queue<float>();
    private ArdyRoomDialogueBridge m_SceneOrientBridge;
    private string m_SceneOrientWatchId;
    private ArdyRoomDialogueBridge.SceneOrientResult m_SceneOrientResult;
    private readonly Dictionary<string, float> m_SceneTargetRevisitAt = new Dictionary<string, float>();

    [Serializable] private sealed class SceneActionFact
    {
        public string source = "Unity-visible-target-execution";
        public string actionId, intent, origin, captureId, confirmedCaptureId, resultCaptureId;
        public string surfaceId, visualLabel, phase, reason, observedUtc;
        public string entityId, focusKind;
        public string requestedEntityId, requestedState, targetResolutionSource;
        public Vector2 requestedImagePoint;
        public bool objectConfirmed, stateChanged;
        public bool groundArrived, objectReobserved;
        public string postArrivalObservationStatus;
        public string targetPurpose, targetDescription, targetMatchVerdict, targetMatchReason, matchCaptureId;
        public bool semanticTargetConfirmed, needsCloserObservation;
        public Vector2 navigationFocusImagePoint;
        public float observationStandOff;
        public float predictedObservationPitch;
        public string observationGoalId;
        public string viewDirection, viewPurpose, viewPurposeStatus;
        public float requestedViewDistance;
        public Vector3 actualViewOffset;
        public SceneObjectViewSample viewBefore, viewAfter;
        public Vector3 intentFocusWorldPoint;
        public bool hasIntentFocus, navigationFocusAdjusted;
        public string trackingVisibility, trackingCaptureId, trackingFocusStatus;
        public Vector3 trackingFocusWorldPoint;
        public int trackingConfirmedImages, trackingMovingConfirmedImages;
        public float trackingLastSeenRealtime = -1;
        public int trackingFocusLastAppliedFrame = -1;
        public float trackingFocusAppliedDegrees;
        public Vector3 measuredSurface, actualRoot;
        public float requestedYaw, requestedPitch, actualViewChangeDegrees;
        public bool active, reached, canReobserveTarget;
        public string scope = "visualLabel is the model's image interpretation, not verified object identity/function. " +
            "Surface and root are engine measurements. Turning/confirmation/submission are not arrival. " +
            "Movement is to legal nearby same-level ground, not onto the surface.";
    }

    private static bool IsSceneVisualIntent(string intent) => intent == "search-scene" || intent == "look-at" || intent == "approach-object" || intent == "reposition-view" || intent == "orient-view" || intent == "turn-in-place" || intent == "inspect-object" || intent == "set-object-state";

    public string SceneActionContextJson => BuildSceneActionFacts().ToString(Newtonsoft.Json.Formatting.None);

    private JObject BuildSceneActionFacts()
    {
        return new JObject {
            ["enabled"] = m_EnableSceneActions && RoomMotionOutputEnabled && SceneEyesOpen,
            ["active"] = m_SceneActionActive,
            ["lastAction"] = m_SceneActionFact == null ? null : JObject.Parse(JsonUtility.ToJson(m_SceneActionFact)),
            ["objectContext"] = BuildSceneObjectFacts(),
            ["sceneSearch"] = BuildSceneSearchContext(),
            ["capability"] = "look-at or approach-object using captureId plus normalized visible image point; " +
                "reposition-view uses a freshly confirmed entity to adjust the same-floor observation station farther/closer/left/right by 0.2 to 1.2 metres while facing that target. see-whole/read-detail/avoid-occlusion are observation purposes, not promised outcomes; assess the new image after movement. " +
                "orient-view changes real head direction without needing an already visible target; turn-in-place turns the actual body on its current legal floor. Bounded historical snapshots may be acquired during an actual body turn; a new current view is still captured after completion. No stairs or touching. " +
                "When objectContext.enabled, inspect-object resolves only the selected visible entity; set-object-state on/off requires its explicit registered nearby device capability and fresh confirmation. look-at can focus an unresolved image region without claiming a measured object. Autonomous view changes bounded to three per 30 seconds."
        };
    }

    private void RecordSceneAction(SceneActionFact fact, string phase, string reason)
    {
        fact.phase = phase; fact.reason = reason ?? ""; fact.active = m_SceneActionActive;
        fact.observedUtc = DateTime.UtcNow.ToString("o");
        var avatar = GetComponent<ArdyRoomDialogueBridge>()?.avatar;
        if (avatar != null) fact.actualRoot = avatar.transform.position;
        m_SceneActionFact = fact;
        RecordSceneActivity(fact.actionId, fact.intent, fact.visualLabel, phase, fact.reason);
        Debug.Log("[Vision/Action] " + JsonUtility.ToJson(fact), this);
        ObserveSpatialGoalRunnerAction(fact);
    }

    private void BeginSceneAction(RoomTaskProtocol.Decision decision, SceneVisionObservation observation,
        int generation, Action completedPreparation)
    {
        if (decision.IsViewChange) { BeginSceneViewChange(decision, observation, generation, completedPreparation); return; }
        if (decision.intent == "inspect-object" || decision.IsObjectState || decision.IsViewPosition ||
            (m_EnableSceneObjectAwareness && (decision.intent == "approach-object" || decision.intent == "look-at")))
        { BeginSceneObjectAction(decision, observation, generation, completedPreparation); return; }
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        string reject = null;
        bool unresolvedPixel = false;
        SceneGroundedTarget target = null;
        if (!m_EnableSceneActions || !RoomMotionOutputEnabled || !SceneEyesOpen) reject = "scene-action-disabled";
        else if (m_SceneActionActive || bridge == null || !bridge.IsConnected || bridge.ReservesBody || bridge.IsFollowing)
            reject = "existing-body-task-has-priority";
        else if (observation == null || decision.visualTarget == null ||
            observation.CaptureId != decision.visualTarget.captureId || !IsFreshSceneObservation(observation))
            reject = "visual-reference-not-current";
        else if (!m_SceneVisionCapture.TryResolve(observation,
            new Vector2(decision.visualTarget.x, decision.visualTarget.y), out target, out reject))
        {
            if (m_EnableSceneObjectAwareness && decision.intent == "look-at")
            { BeginSceneObjectAction(decision, observation, generation, completedPreparation); return; }
            unresolvedPixel = true;
        }
        else if (decision.origin == "autonomous" && m_SceneTargetRevisitAt.TryGetValue(decision.intent + ":" + target.SurfaceId, out float next) &&
            Time.realtimeSinceStartup < next) reject = "same-visible-surface-autonomous-revisit-suppressed";

        if (!string.IsNullOrEmpty(reject))
        {
            m_RoomTaskDispatch = "not-submitted: " + reject;
            // A competing new request must not overwrite the facts of the ongoing action.
            if (!m_SceneActionActive)
                RecordSceneAction(new SceneActionFact { intent = decision.intent, origin = decision.origin,
                    captureId = observation?.CaptureId, visualLabel = decision.visualTarget?.label,
                    canReobserveTarget = unresolvedPixel }, "rejected", reject);
            completedPreparation();
            return;
        }
        var motion = new DialogueMotionIntent(decision.intent, "", generation, ++m_MotionSequence);
        RegisterMotionAction(ref motion);
        m_RoomTaskActionId = motion.ActionId;
        var fact = new SceneActionFact { actionId = motion.ActionId, intent = decision.intent, origin = decision.origin,
            captureId = observation.CaptureId, visualLabel = decision.visualTarget.label,
            surfaceId = target.SurfaceId, measuredSurface = target.WorldPoint };
        m_SceneActionActive = true;
        int epoch = ++m_SceneActionEpoch;
        // This pending owner includes the bounded physical look/capture phase, not only GPU readback.
        m_PendingSceneFormalCaptureGeneration = generation;
        RecordSceneAction(fact, "grounded", "visible surface measured; no route or arrival promised");
        StartCoroutine(RunScenePreparationSafely(PrepareSceneAction(decision, target, fact, bridge, generation, epoch, completedPreparation), fact, generation, epoch));
    }

    private IEnumerator RunScenePreparationSafely(IEnumerator routine, SceneActionFact fact, int generation, int epoch)
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
        if (failure == null || epoch != m_SceneActionEpoch || generation != m_FormalResponseGeneration) yield break;
        bool goalRunnerOwned = SpatialGoalRunnerOwnsGeneration(generation);
        CancelSceneAction("scene-preparation-exception");
        RecordSceneAction(fact, "failed", failure.Message);
        if (goalRunnerOwned && SpatialGoalRunnerOwnsGeneration(generation))
        { TryHandleSpatialGoalPlanSettled(generation); yield break; }
        // A partial dispatch cannot retain a busy speech round or later revive it.
        ++m_FormalResponseGeneration;
        m_ChatSettings?.m_ChatModel?.CancelActiveResponse();
        m_PendingSceneFormalCaptureGeneration = -1;
        m_FormalResponseInFlight = false; m_AgentRoundInFlight = false;
        m_StreamComplete = true; m_TTSSenderDone = true;
        ClearStagedFormalObservation();
        Debug.LogWarning("[Vision/Action] preparation failed; exact response owner released: " + failure.Message, this);
        if (m_AgentRunning) ScheduleNextTick(Mathf.Max(2f, m_MinTickSec), "scene-action-preparation-failed");
    }

    private IEnumerator PrepareSceneAction(RoomTaskProtocol.Decision decision, SceneGroundedTarget target,
        SceneActionFact fact, ArdyRoomDialogueBridge bridge, int generation, int epoch, Action completed)
    {
        bool Current() => epoch == m_SceneActionEpoch && generation == m_FormalResponseGeneration &&
            isActiveAndEnabled && SceneEyesOpen && !m_UserSpeechActiveForAutonomy;
        void FinishPreparation(string dispatch)
        {
            if (!Current()) return;
            m_PendingSceneFormalCaptureGeneration = -1;
            m_RoomTaskDispatch = dispatch;
            completed();
        }
        void Reject(string reason)
        {
            m_SceneAttention?.Release(fact.actionId, reason);
            m_SceneActionActive = false;
            RecordSceneAction(fact, "rejected", reason);
            RememberSceneTarget(decision.intent, target.SurfaceId);
            FinishPreparation("not-submitted: " + reason);
        }
        try
        {
        if (!Current()) yield break;
        var eyes = m_SceneVisionCapture.Target.GetComponent<NevaEyeTracking>();
        if (eyes == null || !eyes.IsInitialized || !eyes.isActiveAndEnabled)
        { Reject("calibrated-attention-rig-unavailable"); yield break; }
        float returnDeadline = Time.realtimeSinceStartup + 2f;
        while (Current() && m_SceneAttention != null && m_SceneAttention.IsReturning && Time.realtimeSinceStartup < returnDeadline) yield return null;
        if (!Current()) yield break;
        if (m_SceneAttention != null && m_SceneAttention.IsReturning) { Reject("prior-attention-still-returning"); yield break; }
        if (m_SceneAttention == null)
            m_SceneAttention = GetComponent<NevaSceneAttention>() ?? gameObject.AddComponent<NevaSceneAttention>();
        if (m_SceneAttention.Target != eyes.Target) m_SceneAttention.Configure(eyes);
        if (!m_SceneAttention.TryLookAt(fact.actionId, target.WorldPoint, out string failure, 6f))
        { Reject(failure); yield break; }
        RecordSceneAction(fact, "looking", "turning calibrated head and eyes toward measured visible point");
        float deadline = Time.realtimeSinceStartup + 3f;
        while (Current() && !m_SceneAttention.IsSettled && Time.realtimeSinceStartup < deadline) yield return null;
        if (!Current()) yield break;
        if (!m_SceneAttention.IsSettled || !target.TryValidate(out failure))
        { Reject(failure ?? "attention-did-not-settle"); yield break; }

        SceneVisionObservation refreshed = null;
        bool captured = false;
        RequestSceneObservation("scene-target-confirm", value => { refreshed = value; captured = true; });
        deadline = Time.realtimeSinceStartup + 4.5f;
        while (Current() && !captured && Time.realtimeSinceStartup < deadline) yield return null;
        if (!Current()) yield break;
        if (!captured || refreshed == null || refreshed.CaptureId == fact.captureId || !IsFreshSceneObservation(refreshed))
        {
            string detail = !captured ? "readback-timeout" : refreshed == null ? "no-observation" :
                refreshed.CaptureId == fact.captureId ? "same-capture" : !refreshed.HasImage ? refreshed.State : "view-no-longer-compatible";
            Reject("focused-observation-unavailable: " + detail); yield break;
        }
        if (!m_SceneVisionCapture.TryConfirmVisible(refreshed, target, out var confirmed, out failure))
        { Reject(failure); yield break; }
        target = confirmed;
        fact.confirmedCaptureId = refreshed.CaptureId;
        BindFormalSceneObservation(refreshed, generation);
        RecordSceneAction(fact, "confirmed", "same visible surface observed again after the actual head turn");
        // Keep the new view long enough to inspect; returning head control never writes root motion.
        float dwell = Time.realtimeSinceStartup + (decision.intent == "look-at" ? 1.1f : .15f);
        while (Current() && Time.realtimeSinceStartup < dwell) yield return null;
        if (!Current()) yield break;
        if (decision.intent == "look-at")
        {
            // Keep the actual head view through the next decision rather than immediately
            // undoing the newly acquired image before the model can use it.
            if (!m_SceneAttention.RetainView(fact.actionId, 20f)) { Reject("attention-retention-lost"); yield break; }
            RememberSceneTarget(decision.intent, target.SurfaceId);
            m_SceneActionActive = false;
            RecordSceneAction(fact, "observed", "actual head view retained for the next fresh-image decision; no locomotion requested");
            FinishPreparation("look-completed"); yield break;
        }
        m_SceneAttention.Release(fact.actionId, "target-observed");
        deadline = Time.realtimeSinceStartup + 2f;
        while (Current() && m_SceneAttention.IsReturning && Time.realtimeSinceStartup < deadline) yield return null;
        if (!Current()) yield break;
        if (m_SceneAttention.IsReturning) { Reject("head-release-timeout"); yield break; }
        RememberSceneTarget(decision.intent, target.SurfaceId);
        string ValidateTarget() => target.TryValidateGeometry(out string error) ? null : error;
        BeginSceneResultWatch(bridge, fact.actionId);
        if (!bridge.ApproachSceneTarget(fact.actionId, fact.confirmedCaptureId, target.SurfaceId,
            target.WorldPoint, ValidateTarget, out failure))
        { EndSceneResultWatch(fact.actionId); Reject(failure); yield break; }
        RecordSceneAction(fact, "submitted", "nearby ground and complete route checked; waiting for actual movement result");
        // A committed move survives an ordinary autonomous reply. Its own epoch,
        // scene target validator and bridge action identity own further execution.
        StartCoroutine(ObserveSceneActionResult(fact, bridge, epoch));
        FinishPreparation("object-attempt-submitted");
        }
        finally
        {
            // The speech generation may change without a user-motion cancellation.
            // Only release this preparation, never a newer task or its pending capture.
            if (epoch == m_SceneActionEpoch && m_SceneActionActive && fact.phase != "submitted")
            {
                m_SceneAttention?.Release(fact.actionId, "preparation-owner-invalidated");
                m_SceneActionActive = false;
                RecordSceneAction(fact, "cancelled", "preparation-owner-invalidated");
                if (m_PendingSceneFormalCaptureGeneration == generation) m_PendingSceneFormalCaptureGeneration = -1;
            }
        }
    }

    private void BeginSceneViewChange(RoomTaskProtocol.Decision decision, SceneVisionObservation observation,
        int generation, Action completed)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        string failure = null;
        if (!m_EnableSceneActions || !RoomMotionOutputEnabled || !SceneEyesOpen) failure = "scene-action-disabled";
        else if (m_SceneActionActive || bridge == null || !bridge.IsConnected || bridge.ReservesBody || bridge.IsFollowing)
            failure = "existing-body-task-has-priority";
        else if (decision.viewChange == null || observation == null || observation.CaptureId != decision.viewChange.captureId ||
            !IsFreshSceneObservation(observation)) failure = "view-change-reference-not-current";
        float now = Time.realtimeSinceStartup;
        while (m_SceneViewStarts.Count > 0 && now - m_SceneViewStarts.Peek() > 30f) m_SceneViewStarts.Dequeue();
        if (failure == null && decision.origin == "autonomous" && m_SceneViewStarts.Count >= 3)
            failure = "view-change-short-window-budget: consider the acquired observations before another turn";
        if (failure != null)
        {
            m_RoomTaskDispatch = "not-submitted: " + failure;
            if (!m_SceneActionActive) RecordSceneAction(new SceneActionFact { intent = decision.intent, origin = decision.origin,
                captureId = observation?.CaptureId, visualLabel = decision.evidence }, "rejected", failure);
            completed(); return;
        }
        var motion = new DialogueMotionIntent(decision.intent, "", generation, ++m_MotionSequence);
        RegisterMotionAction(ref motion); m_RoomTaskActionId = motion.ActionId;
        var fact = new SceneActionFact { actionId = motion.ActionId, intent = decision.intent, origin = decision.origin,
            captureId = observation.CaptureId, visualLabel = decision.evidence,
            requestedYaw = decision.viewChange.yaw, requestedPitch = decision.viewChange.pitch,
            scope = "Purpose is the model's intention, not a claim about unseen space. Angles are relative requests; only actual head/body motion and a new image establish a changed view. No root teleport, object identity or arrival is implied." };
        m_SceneActionActive = true; int epoch = ++m_SceneActionEpoch;
        m_SceneViewStarts.Enqueue(now); m_PendingSceneFormalCaptureGeneration = generation;
        RecordSceneAction(fact, "preparing", "current observation accepted; preparing a bounded change of actual view");
        StartCoroutine(RunScenePreparationSafely(PrepareSceneViewChange(decision, observation, fact, bridge,
            generation, epoch, completed), fact, generation, epoch));
    }

    private IEnumerator PrepareSceneViewChange(RoomTaskProtocol.Decision decision, SceneVisionObservation source,
        SceneActionFact fact, ArdyRoomDialogueBridge bridge, int generation, int epoch, Action completed)
    {
        SceneSweepLease sweep = null;
        int workEpoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn, visionEpoch = m_SceneVisionEpoch;
        var capture = m_SceneVisionCapture; var boundAvatar = bridge?.avatar;
        var environment = bridge?.environmentRoot; var user = bridge?.userHead; var controller = bridge?.Controller;
        bool Current() => epoch == m_SceneActionEpoch && generation == m_FormalResponseGeneration &&
            workEpoch == m_WorkEpoch && userTurn == m_ActionConstraints.CurrentUserTurn && visionEpoch == m_SceneVisionEpoch &&
            isActiveAndEnabled && SceneEyesOpen && !m_UserSpeechActiveForAutonomy && !m_SceneTimePaused && Time.timeScale > 0 &&
            bridge != null && bridge.IsConnected && bridge.avatar == boundAvatar && bridge.environmentRoot == environment &&
            bridge.userHead == user && bridge.Controller == controller && capture != null && capture == m_SceneVisionCapture &&
            capture.Target == boundAvatar && capture.isActiveAndEnabled && capture.IsObservationBindingCurrent(source) &&
            (sweep == null || SceneSweepBindingCurrent(sweep));
        void Finish(string phase, string reason, string dispatch)
        {
            if (!Current()) return;
            m_SceneActionActive = false; m_PendingSceneFormalCaptureGeneration = -1;
            RecordSceneAction(fact, phase, reason); m_RoomTaskDispatch = dispatch; completed();
        }
        void Fail(string reason)
        {
            if (sweep != null) DiscardSceneSweep(reason);
            m_SceneAttention?.Release(fact.actionId, reason);
            if (bridge != null && bridge.ActionId == fact.actionId) bridge.StopMovement("cancelled", reason);
            Finish("rejected", reason, "not-completed: " + reason);
        }
        try
        {
            if (!Current()) yield break;
            var avatar = m_SceneVisionCapture.Target;
            var eyes = avatar != null ? avatar.GetComponent<NevaEyeTracking>() : null;
            if (eyes == null || !eyes.IsInitialized || !eyes.isActiveAndEnabled)
            { Fail("calibrated-attention-rig-unavailable"); yield break; }
            if (m_SceneAttention == null) m_SceneAttention = GetComponent<NevaSceneAttention>() ?? gameObject.AddComponent<NevaSceneAttention>();
            if (m_SceneAttention.Target != eyes.Target) m_SceneAttention.Configure(eyes);
            if (decision.intent == "orient-view")
            {
                if (!m_SceneAttention.TryOrientView(fact.actionId, fact.requestedYaw, fact.requestedPitch, out string error))
                { Fail(error); yield break; }
                RecordSceneAction(fact, "looking", "head and eyes are moving toward the requested unknown direction");
                float deadline = Time.realtimeSinceStartup + 4.5f;
                while (Current() && !m_SceneAttention.IsSettled && Time.realtimeSinceStartup < deadline) yield return null;
                if (!Current()) yield break;
                if (!m_SceneAttention.IsSettled) { Fail("orientation-did-not-settle"); yield break; }
                if (!m_SceneAttention.RetainView(fact.actionId, 20f)) { Fail("orientation-owner-lost"); yield break; }
            }
            else
            {
                m_SceneAttention.Cancel("yield-to-body-orientation");
                float deadline = Time.realtimeSinceStartup + 2.5f;
                while (Current() && m_SceneAttention.IsReturning && Time.realtimeSinceStartup < deadline) yield return null;
                if (!Current()) yield break;
                if (m_SceneAttention.IsReturning) { Fail("head-release-timeout"); yield break; }
                string Validate() => epoch != m_SceneActionEpoch || !SceneEyesOpen || !isActiveAndEnabled ||
                    m_SceneVisionCapture == null || !m_SceneVisionCapture.isActiveAndEnabled || m_SceneVisionCapture.Target != avatar ||
                    avatar == null || !avatar.gameObject.activeInHierarchy || avatar.gameObject.scene.handle != source.SceneHandle ?
                    "scene-orientation-owner-or-binding-invalid" : null;
                BeginSceneOrientWatch(bridge, fact.actionId);
                if (!bridge.TryOrientScene(fact.actionId, fact.captureId, fact.requestedYaw, Validate, out string error))
                { EndSceneOrientWatch(); Fail(error); yield break; }
                sweep = BeginSceneSweep(fact, source, bridge, generation, epoch);
                RecordSceneAction(fact, "turning-body", "native ARDY is turning on the current validated floor; bounded historical images may be sampled during motion, without continuous sight or a claim about unseen contents");
                deadline = Time.realtimeSinceStartup + 100f;
                while (Current() && m_SceneOrientResult == null && Time.realtimeSinceStartup < deadline)
                { TickSceneSweep(sweep); yield return null; }
                if (!Current()) yield break;
                var result = m_SceneOrientResult; EndSceneOrientWatch();
                if (result == null || result.actionId != fact.actionId || !result.oriented)
                { Fail(result?.reason ?? "native-orientation-result-timeout"); yield break; }
                EndSceneSweepMotion(sweep);
                RecordSceneAction(fact, "turned", "native body turn completed; acquiring the actual resulting view");
            }
            SceneVisionObservation next = null; bool received = false;
            RequestSceneObservation("scene-view-change-result", value => { next = value; received = true; });
            float captureDeadline = Time.realtimeSinceStartup + 4.5f;
            while (Current() && !received && Time.realtimeSinceStartup < captureDeadline) yield return null;
            if (!Current()) yield break;
            if (!received || next == null || next.CaptureId == source.CaptureId || !IsFreshSceneObservation(next))
            { Fail("changed-view-image-unavailable"); yield break; }
            fact.actualViewChangeDegrees = Quaternion.Angle(source.CameraRotation, next.CameraRotation);
            if (fact.actualViewChangeDegrees < 1f) { Fail("actual-view-did-not-change"); yield break; }
            fact.confirmedCaptureId = fact.resultCaptureId = next.CaptureId;
            ObserveKnownSceneObjects(next, "scene-view-change-result");
            ObserveSpatialGoalView(next, "scene-view-change-result");
            CompleteSceneSweep(sweep, next);
            BindFormalSceneObservation(next, generation);
            Finish("observed", "actual view changed and a new image was acquired; decide from these pixels, not assumed unseen contents", "view-change-completed");
        }
        finally
        {
            if (sweep != null && !sweep.completed && ReferenceEquals(m_SceneSweep, sweep))
                DiscardSceneSweep("view-preparation-owner-invalidated");
            EndSceneOrientWatch(fact.actionId);
            if (epoch == m_SceneActionEpoch && m_SceneActionActive)
            {
                m_SceneAttention?.Release(fact.actionId, "view-preparation-owner-invalidated");
                if (bridge != null && bridge.ActionId == fact.actionId) bridge.StopMovement("cancelled", "view-preparation-owner-invalidated");
                m_SceneActionActive = false;
                RecordSceneAction(fact, "cancelled", "view-preparation-owner-invalidated");
                if (m_PendingSceneFormalCaptureGeneration == generation) m_PendingSceneFormalCaptureGeneration = -1;
            }
        }
    }

    private void BeginSceneOrientWatch(ArdyRoomDialogueBridge bridge, string id)
    {
        EndSceneOrientWatch(); m_SceneOrientBridge = bridge; m_SceneOrientWatchId = id; m_SceneOrientResult = null;
        bridge.SceneOrientCompleted += OnSceneOrientResult;
    }
    private void OnSceneOrientResult(ArdyRoomDialogueBridge.SceneOrientResult result)
    { if (result != null && result.actionId == m_SceneOrientWatchId && m_SceneOrientResult == null) m_SceneOrientResult = result; }
    private void EndSceneOrientWatch(string id = null)
    {
        if (id != null && id != m_SceneOrientWatchId) return;
        if (m_SceneOrientBridge != null) m_SceneOrientBridge.SceneOrientCompleted -= OnSceneOrientResult;
        m_SceneOrientBridge = null; m_SceneOrientWatchId = null; m_SceneOrientResult = null;
    }

    private void RememberSceneTarget(string intent, string id)
    {
        if (m_SceneTargetRevisitAt.Count > 32) m_SceneTargetRevisitAt.Clear();
        m_SceneTargetRevisitAt[intent + ":" + id] = Time.realtimeSinceStartup + 90f;
    }

    private IEnumerator ObserveSceneActionResult(SceneActionFact fact, ArdyRoomDialogueBridge bridge, int epoch)
    {
        float deadline = Time.realtimeSinceStartup + 120f;
        while (epoch == m_SceneActionEpoch && isActiveAndEnabled && bridge != null && Time.realtimeSinceStartup < deadline)
        {
            var result = m_SceneResultActionId == fact.actionId ? m_SceneResult : null;
            if (result != null && result.actionId == fact.actionId)
            {
                EndSceneResultWatch(fact.actionId);
                m_SceneActionActive = false;
                fact.reached = result.reached;
                RecordSceneAction(fact, result.status, result.reason);
                bool received = false;
                RequestSceneObservation("scene-action-result", observation => {
                    received = true;
                    if (epoch != m_SceneActionEpoch) return;
                    fact.resultCaptureId = observation?.HasImage == true ? observation.CaptureId : null;
                    RecordSceneAction(fact, result.status, result.reason);
                });
                float captureDeadline = Time.realtimeSinceStartup + 4.5f;
                while (epoch == m_SceneActionEpoch && !received && Time.realtimeSinceStartup < captureDeadline) yield return null;
                if (epoch == m_SceneActionEpoch && m_AgentRunning && !SpatialGoalRunnerOwnsAction(fact))
                    ScheduleNextTick(Mathf.Max(2f, m_MinTickSec), "scene-action-result");
                yield break;
            }
            yield return null;
        }
        if (epoch == m_SceneActionEpoch) CancelSceneAction("scene-action-result-timeout");
    }

    private void BeginSceneResultWatch(ArdyRoomDialogueBridge bridge, string actionId)
    {
        EndSceneResultWatch(m_SceneResultActionId);
        m_SceneResultBridge = bridge; m_SceneResultActionId = actionId; m_SceneResult = null;
        bridge.SceneTargetCompleted += OnSceneTargetResult;
    }

    private void OnSceneTargetResult(ArdyRoomDialogueBridge.SceneTargetResult result)
    {
        if (result != null && result.actionId == m_SceneResultActionId && m_SceneResult == null) m_SceneResult = result;
    }

    private void EndSceneResultWatch(string actionId)
    {
        if (actionId != m_SceneResultActionId) return;
        if (m_SceneResultBridge != null) m_SceneResultBridge.SceneTargetCompleted -= OnSceneTargetResult;
        m_SceneResultBridge = null; m_SceneResultActionId = null; m_SceneResult = null;
    }

    private void PauseSceneAction()
    {
        // Application pause cannot leave a private preparation latched. Do not invalidate
        // unrelated speech, gestures or the independently owned user-follow task.
        bool preparing = m_SceneActionActive && m_PendingSceneFormalCaptureGeneration == m_FormalResponseGeneration;
        int owner = m_FormalResponseGeneration;
        if (!m_SceneActionActive && (m_SceneAttention == null || !m_SceneAttention.IsBusy)) return;
        CancelSceneAction("application-paused");
        if (!preparing || owner != m_FormalResponseGeneration) return;
        ++m_FormalResponseGeneration; m_PendingSceneFormalCaptureGeneration = -1;
        m_FormalResponseInFlight = false; m_AgentRoundInFlight = false;
        m_StreamComplete = true; m_TTSSenderDone = true;
        ClearStagedFormalObservation(); m_ChatSettings?.m_ChatModel?.CancelActiveResponse();
    }

    private void CancelSceneAction(string reason)
    {
        HandleSpatialGoalActionCancellation(reason);
        DiscardSceneSweep(reason);
        CancelSceneObjectTargetMatch();
        ++m_SceneActionEpoch;
        EndSceneResultWatch(m_SceneResultActionId);
        EndSceneOrientWatch();
        m_SceneAttention?.Cancel(reason);
        if (!m_SceneActionActive) return;
        m_SceneActionActive = false;
        var fact = m_SceneActionFact;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (fact != null && bridge != null && bridge.ActionId == fact.actionId)
            bridge.StopMovement("cancelled", reason);
        if (fact != null) RecordSceneAction(fact, "cancelled", reason);
    }
}
