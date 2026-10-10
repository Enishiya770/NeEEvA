using System;
using System.Collections.Generic;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private const float SceneSweepAngleStep = 40f;
    private const float SceneSweepRequestSpacing = .5f;
    private const float SceneSweepDeliverySeconds = 30f;
    private const int SceneSweepFrameLimit = 4;
    private SceneSweepLease m_SceneSweep;

    private sealed class SceneSweepFrame
    {
        public SceneVisionObservation observation;
        public float bodyYawAtRequest, cameraYaw;
        public JArray knownObjects;
    }

    private sealed class SceneSweepLease
    {
        public int sceneEpoch, generation, workEpoch, userTurn, visionEpoch, operation;
        public string actionId;
        public ArdyRoomDialogueBridge bridge;
        public ArdyRoomLocomotionController controller;
        public NevaSceneVisionCapture source;
        public Transform avatar, environment, user;
        public SceneVisionObservation start, terminal;
        public Vector3 previousForward;
        public float bodyYaw, lastRequestYaw, lastRequestedAt = -1f, lastCapturedAt = -1f, motionEndedAt = -1f;
        public int requests;
        public bool pending, completed;
        public readonly List<SceneSweepFrame> frames = new List<SceneSweepFrame>();
    }

    private SceneSweepLease BeginSceneSweep(SceneActionFact fact, SceneVisionObservation source,
        ArdyRoomDialogueBridge bridge, int generation, int sceneEpoch)
    {
        DiscardSceneSweep("new-turn");
        if (bridge?.avatar == null || bridge.Controller == null || m_SceneVisionCapture == null) return null;
        var lease = new SceneSweepLease {
            sceneEpoch = sceneEpoch, generation = generation, workEpoch = m_WorkEpoch,
            userTurn = m_ActionConstraints.CurrentUserTurn, visionEpoch = m_SceneVisionEpoch,
            actionId = fact.actionId, bridge = bridge, controller = bridge.Controller,
            operation = bridge.Controller.OperationRevision, source = m_SceneVisionCapture,
            avatar = bridge.avatar.transform, environment = bridge.environmentRoot, user = bridge.userHead,
            start = source, previousForward = Vector3.ProjectOnPlane(bridge.avatar.transform.forward, Vector3.up).normalized
        };
        m_SceneSweep = lease;
        return lease;
    }

    private bool SceneSweepBindingCurrent(SceneSweepLease lease) => lease != null &&
        ReferenceEquals(m_SceneSweep, lease) && isActiveAndEnabled && SceneEyesOpen &&
        !m_SceneTimePaused && Time.timeScale > 0 && !m_UserSpeechActiveForAutonomy &&
        lease.sceneEpoch == m_SceneActionEpoch && lease.workEpoch == m_WorkEpoch &&
        lease.userTurn == m_ActionConstraints.CurrentUserTurn && lease.visionEpoch == m_SceneVisionEpoch &&
        lease.bridge != null && lease.bridge == GetComponent<ArdyRoomDialogueBridge>() && lease.bridge.IsConnected &&
        lease.bridge.avatar != null && lease.bridge.avatar.transform == lease.avatar &&
        lease.bridge.environmentRoot == lease.environment && lease.bridge.userHead == lease.user &&
        lease.bridge.Controller == lease.controller && lease.controller != null &&
        lease.controller.OperationRevision == lease.operation && lease.bridge.ActionId == lease.actionId &&
        lease.source != null && lease.source == m_SceneVisionCapture && lease.source.isActiveAndEnabled &&
        lease.source.Target != null && lease.source.Target.transform == lease.avatar &&
        lease.source.IsObservationBindingCurrent(lease.start);

    private bool SceneSweepCaptureCurrent(SceneSweepLease lease) => SceneSweepBindingCurrent(lease) &&
        !lease.completed && lease.generation == m_FormalResponseGeneration && m_SceneActionActive &&
        m_SceneActionFact?.actionId == lease.actionId && m_SceneActionFact.intent == "turn-in-place" &&
        (lease.motionEndedAt >= 0 || lease.bridge.IsOrientingScene);

    private void TickSceneSweep(SceneSweepLease lease)
    {
        if (!SceneSweepCaptureCurrent(lease) || lease.motionEndedAt >= 0) return;
        Vector3 forward = Vector3.ProjectOnPlane(lease.avatar.forward, Vector3.up).normalized;
        lease.bodyYaw += Vector3.SignedAngle(lease.previousForward, forward, Vector3.up);
        lease.previousForward = forward;
        float now = Time.realtimeSinceStartup;
        if (lease.pending || lease.requests >= SceneSweepFrameLimit || lease.source.IsCapturePending ||
            Mathf.Abs(lease.bodyYaw - lease.lastRequestYaw) < SceneSweepAngleStep ||
            (lease.lastRequestedAt >= 0 && now - lease.lastRequestedAt < SceneSweepRequestSpacing) ||
            (lease.lastCapturedAt >= 0 && now - lease.lastCapturedAt < SceneSweepRequestSpacing)) return;
        lease.pending = true; lease.requests++;
        lease.lastRequestedAt = now; lease.lastRequestYaw = lease.bodyYaw;
        float bodyYaw = lease.bodyYaw, requestedAt = now;
        RequestSceneObservation("scene-turn-sweep", observation =>
            AcceptSceneSweepFrame(lease, observation, requestedAt, bodyYaw));
    }

    private void AcceptSceneSweepFrame(SceneSweepLease lease, SceneVisionObservation observation,
        float requestedAt, float bodyYaw)
    {
        lease.pending = false;
        if (!SceneSweepCaptureCurrent(lease)) return;
        if (observation?.HasImage != true || !lease.source.IsObservationBindingCurrent(observation) ||
            observation.CapturedAtRealtime < requestedAt || observation.Frame <= lease.start.Frame ||
            (lease.motionEndedAt >= 0 && observation.CapturedAtRealtime > lease.motionEndedAt) ||
            lease.frames.Count >= SceneSweepFrameLimit) return;
        foreach (var frame in lease.frames)
            if (frame.observation.CaptureId == observation.CaptureId) return;
        // A historical frame is checked against its capture binding, not today's view angle.
        // The exact RGB and ID buffer authorize only previously known entities in that frame.
        var known = ObserveKnownSceneObjects(observation, "scene-turn-sweep") ?? new JArray();
        ObserveSpatialGoalView(observation, "scene-turn-sweep");
        float cameraYaw = Vector3.SignedAngle(Vector3.ProjectOnPlane(lease.start.CameraRotation * Vector3.forward, Vector3.up),
            Vector3.ProjectOnPlane(observation.CameraRotation * Vector3.forward, Vector3.up), Vector3.up);
        lease.lastCapturedAt = observation.CapturedAtRealtime;
        lease.frames.Add(new SceneSweepFrame { observation = observation,
            bodyYawAtRequest = bodyYaw, cameraYaw = cameraYaw, knownObjects = (JArray)known.DeepClone() });
        Debug.Log($"[Vision/Sweep] action={lease.actionId} generation={lease.generation} frame={observation.Frame} " +
            $"capture={observation.CaptureId} bodyYawAtRequest={bodyYaw:F1} cameraYawAtCapture={cameraYaw:F1} " +
            $"retained={lease.frames.Count} known={known.Count}; historical-image-only", this);
    }

    private void EndSceneSweepMotion(SceneSweepLease lease)
    {
        // The native completion callback has already released IsOrientingScene.
        if (SceneSweepBindingCurrent(lease) && lease.generation == m_FormalResponseGeneration &&
            !lease.completed && m_SceneActionActive && m_SceneActionFact?.actionId == lease.actionId)
            lease.motionEndedAt = Time.realtimeSinceStartup;
    }

    private void CompleteSceneSweep(SceneSweepLease lease, SceneVisionObservation terminal)
    {
        if (!SceneSweepCaptureCurrent(lease) || terminal?.HasImage != true ||
            !lease.source.IsObservationBindingCurrent(terminal) || lease.motionEndedAt < 0 ||
            terminal.CapturedAtRealtime < lease.motionEndedAt) return;
        lease.terminal = terminal; lease.completed = true;
    }

    private void DiscardSceneSweep(string reason)
    {
        var lease = m_SceneSweep; m_SceneSweep = null;
        if (lease == null) return;
        lease.frames.Clear(); lease.pending = false;
        Debug.Log($"[Vision/Sweep] action={lease.actionId} discarded={reason}; retained-history-released; prior-consumer-delivery-not-revoked", this);
    }

    private SceneSweepLease DeliverableSceneSweep(SceneVisionObservation current)
    {
        var lease = m_SceneSweep;
        // Formal generation may advance for the next planner. The completed body operation,
        // scene/work/user/binding leases and a new current image still have to match.
        if (!SceneSweepBindingCurrent(lease) || !lease.completed || lease.terminal == null ||
            m_SceneActionActive || current?.HasImage != true ||
            !lease.source.IsObservationBindingCurrent(current) || current.Frame < lease.terminal.Frame ||
            current.CapturedAtRealtime < lease.terminal.CapturedAtRealtime ||
            Time.realtimeSinceStartup - lease.terminal.CapturedAtRealtime > SceneSweepDeliverySeconds)
        {
            if (lease != null && (!SceneSweepBindingCurrent(lease) || lease.completed &&
                Time.realtimeSinceStartup - lease.terminal.CapturedAtRealtime > SceneSweepDeliverySeconds))
                DiscardSceneSweep("delivery-owner-or-age-invalid");
            return null;
        }
        return lease;
    }

    private JObject BuildSceneSweepContext(SceneVisionObservation current)
    {
        var lease = DeliverableSceneSweep(current); var frames = new JArray();
        var delivered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in GetScenePlanningHistoryImages(current)) delivered.Add(image.CaptureId);
        if (lease != null)
            foreach (var frame in lease.frames)
                frames.Add(new JObject {
                    ["imageRole"] = "historical-turn-sweep", ["visualObservation"] = JObject.Parse(frame.observation.MetadataJson),
                    ["imageAttachedToThisPlanner"] = delivered.Contains(frame.observation.CaptureId),
                    ["ageSeconds"] = Mathf.Max(0f, Time.realtimeSinceStartup - frame.observation.CapturedAtRealtime),
                    ["bodyYawAtRequestDegrees"] = frame.bodyYawAtRequest,
                    ["cameraYawRelativeToStartDegrees"] = frame.cameraYaw,
                    ["returnDirection"] = SceneHistoricalViewBearing(frame.observation, current),
                    ["knownEntitiesObservedInThisFrame"] = frame.knownObjects.DeepClone()
                });
        return new JObject {
            ["available"] = lease != null && frames.Count > 0,
            ["actionId"] = lease?.actionId ?? "", ["sourceCaptureId"] = lease?.start.CaptureId ?? "",
            ["terminalCaptureId"] = lease?.terminal.CaptureId ?? "", ["frames"] = frames,
            ["currentCaptureId"] = current?.CaptureId ?? "",
            ["scope"] = "Historical RGB snapshots from the most recent completed actual turn, in capture order. " +
                "imageAttachedToThisPlanner indicates which recorded frames actually have pixel attachments in this request; metadata alone is not a delivered image. " +
                "Only currentCaptureId is the current view. These images are not continuous sight, current visibility, " +
                "device state or action permission. Known entity observations are same-frame evidence of previously seen identities only. " +
                "Use historical direction as a search hypothesis; acquire a fresh current image before selecting or operating a target. " +
                "The user's requested final angle was retained; no automatic target choice or early stop occurred."
        };
    }

    private IReadOnlyList<SceneVisionObservation> GetSceneSweepImages(SceneVisionObservation current)
    {
        var lease = DeliverableSceneSweep(current);
        if (lease == null) return Array.Empty<SceneVisionObservation>();
        var images = new SceneVisionObservation[lease.frames.Count];
        for (int i = 0; i < images.Length; i++) images[i] = lease.frames[i].observation;
        return images;
    }
}
