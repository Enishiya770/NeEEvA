using System;
using System.Collections.Generic;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private const float SceneHistoricalMemorySeconds = SceneVisionObservation.MaximumHistoryAgeSeconds;
    private sealed class SceneSpatialViewSample
    {
        public NevaSceneVisionCapture source;
        public SceneVisionObservation observation;
        public Transform environment;
        public UniVRM10.Vrm10Instance avatar;
        public LLM model;
        public JObject facts;
        public JArray confirmations;
    }
    private readonly List<SceneSpatialViewSample> m_SceneSpatialViewSamples = new List<SceneSpatialViewSample>();
    private sealed class SceneDialogueView
    {
        public SceneVisionObservation observation;
        public NevaSceneVisionCapture source;
        public Transform environment;
        public UniVRM10.Vrm10Instance avatar;
        public LLM model;
        public int visionEpoch;
    }
    private readonly List<SceneDialogueView> m_SceneDialogueViews = new List<SceneDialogueView>();

    // Called only when a real image is supplied to a formal visual consumer.
    // Store pixels/pose, never extract an entity or authority from assistant prose.
    private void RememberSceneDialogueView(SceneVisionObservation observation)
    {
        if (GetActiveSpatialGoalId() != null || m_SpatialGoalRunner != null ||
            !SceneSpatialObservationUsable(observation) || !IsFreshSceneObservation(observation)) return;
        m_SceneDialogueViews.RemoveAll(sample => !SceneDialogueViewCurrent(sample));
        foreach (var sample in m_SceneDialogueViews) if (sample.observation.CaptureId == observation.CaptureId) return;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        // Refresh an equivalent viewpoint in place instead of allowing repeated
        // conversation captures to evict the one different earlier direction.
        for (int i = m_SceneDialogueViews.Count - 1; i >= 0; i--)
            if (SceneDialogueSameView(m_SceneDialogueViews[i].observation, observation)) m_SceneDialogueViews.RemoveAt(i);
        m_SceneDialogueViews.Add(new SceneDialogueView { observation = observation, source = m_SceneVisionCapture,
            environment = bridge.environmentRoot, avatar = bridge.avatar, model = m_ChatSettings?.m_ChatModel, visionEpoch = m_SceneVisionEpoch });
        while (m_SceneDialogueViews.Count > 2) m_SceneDialogueViews.RemoveAt(0);
    }

    private bool SceneDialogueViewCurrent(SceneDialogueView sample)
    {
        return sample != null && SceneHistoricalObservationReadable(sample.source, sample.observation,
            sample.environment, sample.avatar, sample.model);
    }

    // Historical ownership deliberately excludes turn/vision epoch and eye state.
    // Consumers must separately require a fresh current frame before locating or acting.
    private bool SceneHistoricalObservationReadable(NevaSceneVisionCapture source, SceneVisionObservation observation,
        Transform environment, UniVRM10.Vrm10Instance avatar, LLM model)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        return source != null && source == m_SceneVisionCapture && source.isActiveAndEnabled &&
            bridge != null && environment != null && environment == bridge.environmentRoot &&
            avatar != null && avatar == bridge.avatar && source.Target == avatar &&
            model != null && model == m_ChatSettings?.m_ChatModel &&
            source.IsObservationHistoryBindingCurrent(observation) &&
            SceneDialogueViewAgeValid(observation.CapturedAtRealtime, Time.realtimeSinceStartup);
    }

    private static bool SceneDialogueViewAgeValid(float capturedAt, float now) => now >= capturedAt && now - capturedAt <= SceneHistoricalMemorySeconds;

    private static bool SceneDialogueSameView(SceneVisionObservation first, SceneVisionObservation second) =>
        Vector3.Distance(first.CameraPosition, second.CameraPosition) <= .35f && Quaternion.Angle(first.CameraRotation, second.CameraRotation) <= 25f;

    private JObject SceneHistoricalViewBearing(SceneVisionObservation historical, SceneVisionObservation current)
    {
        Vector3 oldDirection = Vector3.ProjectOnPlane(historical.CameraRotation * Vector3.forward, Vector3.up);
        return new JObject {
            ["relativeToCurrentCaptureId"] = current.CaptureId,
            ["yawRelativeToCurrentCameraDegrees"] = Vector3.SignedAngle(Vector3.ProjectOnPlane(current.CameraRotation * Vector3.forward, Vector3.up), oldDirection, Vector3.up),
            ["yawRelativeToCurrentBodyDegrees"] = current.HasBodyPose ? (JToken)Vector3.SignedAngle(Vector3.ProjectOnPlane(current.BodyRotation * Vector3.forward, Vector3.up), oldDirection, Vector3.up) : null,
            ["cameraTranslationSinceCaptureMeters"] = Vector3.Distance(historical.CameraPosition, current.CameraPosition),
            ["selfPose"] = BuildSceneSelfPoseContext(current, historical),
            ["scope"] = "Measured bearings to this historical CAMERA DIRECTION, not to an identified target or destination. Positive yaw is right. Camera/head and body bearings differ. Translation changes what would be seen; a fresh observation is required after any chosen return." };
    }

    private JObject BuildSceneSelfPoseContext(SceneVisionObservation current, SceneVisionObservation historical = null)
    {
        if (!SceneSpatialObservationUsable(current) || !IsFreshSceneObservation(current)) return null;
        Vector3 cameraForward = current.CameraRotation * Vector3.forward;
        Vector3 bodyForward = current.BodyRotation * Vector3.forward;
        Vector3 bodyHorizontal = Vector3.ProjectOnPlane(bodyForward, Vector3.up).normalized;
        Vector3 cameraInBody = Quaternion.Inverse(current.BodyRotation) * cameraForward;
        bool hasPrior = historical != null && m_SceneVisionCapture.IsObservationHistoryBindingCurrent(historical);
        return new JObject {
            ["captureId"] = current.CaptureId, ["capturedAtUtc"] = current.CapturedAtUtc,
            ["cameraPosition"] = SceneSpatialVector(current.CameraPosition), ["cameraRotation"] = SceneSpatialRotation(current.CameraRotation),
            ["cameraForward"] = SceneSpatialVector(cameraForward), ["hasBodyPose"] = current.HasBodyPose,
            ["bodyPosition"] = current.HasBodyPose ? SceneSpatialVector(current.BodyPosition) : null,
            ["bodyRotation"] = current.HasBodyPose ? SceneSpatialRotation(current.BodyRotation) : null,
            ["bodyForward"] = current.HasBodyPose ? SceneSpatialVector(bodyForward) : null,
            ["cameraYawRelativeToBodyDegrees"] = current.HasBodyPose ? (JToken)Vector3.SignedAngle(bodyHorizontal, Vector3.ProjectOnPlane(cameraForward, Vector3.up), Vector3.up) : null,
            ["cameraPitchRelativeToBodyDegreesUp"] = current.HasBodyPose ? (JToken)(Mathf.Atan2(cameraInBody.y, new Vector2(cameraInBody.x, cameraInBody.z).magnitude) * Mathf.Rad2Deg) : null,
            ["sinceHistoricalCapture"] = !hasPrior ? null : new JObject {
                ["captureId"] = historical.CaptureId,
                ["cameraDisplacementWorld"] = SceneSpatialVector(current.CameraPosition - historical.CameraPosition),
                ["cameraTranslationMeters"] = Vector3.Distance(historical.CameraPosition, current.CameraPosition),
                ["cameraRotationDegrees"] = Quaternion.Angle(historical.CameraRotation, current.CameraRotation),
                ["bodyDisplacementWorld"] = current.HasBodyPose && historical.HasBodyPose ? SceneSpatialVector(current.BodyPosition - historical.BodyPosition) : null,
                ["bodyTranslationMeters"] = current.HasBodyPose && historical.HasBodyPose ? (JToken)Vector3.Distance(historical.BodyPosition, current.BodyPosition) : null,
                ["bodyYawChangeDegreesRight"] = current.HasBodyPose && historical.HasBodyPose ? (JToken)Vector3.SignedAngle(Vector3.ProjectOnPlane(historical.BodyRotation * Vector3.forward, Vector3.up), bodyHorizontal, Vector3.up) : null
            },
            ["scope"] = "Measured body and head-camera poses from the same render as this current image. Signed yaw is positive right, elevation positive up. Relative movement compares only these measured poses, never an inferred action, route or unseen object movement."
        };
    }

    private JObject BuildSceneRememberedPointBearing(Vector3 frozenWorldPoint, SceneVisionObservation current,
        string sourceCaptureId, SceneVisionObservation historical = null)
    {
        var selfPose = BuildSceneSelfPoseContext(current, historical);
        if (selfPose == null) return null;
        Vector3 offset = frozenWorldPoint - current.CameraPosition;
        Vector3 local = Quaternion.Inverse(current.CameraRotation) * offset;
        Vector3 bodyForward = Vector3.ProjectOnPlane(current.BodyRotation * Vector3.forward, Vector3.up).normalized;
        Vector3 bodyOffset = frozenWorldPoint - current.BodyPosition;
        return new JObject {
            ["sourceCaptureId"] = sourceCaptureId, ["historical"] = true, ["actionAuthorization"] = false,
            ["relativeToCurrentView"] = new JObject {
                ["viewCaptureId"] = current.CaptureId,
                ["yawDegreesRight"] = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg,
                ["elevationDegreesUp"] = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg,
                ["distanceMeters"] = offset.magnitude,
                ["scope"] = "Camera-relative bearing to frozen historical pixels, not current visibility, current object position, reachability or permission."
            },
            ["relativeToCurrentBody"] = !current.HasBodyPose ? null : new JObject {
                ["viewCaptureId"] = current.CaptureId,
                ["yawDegrees"] = Mathf.Atan2(Vector3.Dot(bodyOffset, Vector3.Cross(Vector3.up, bodyForward)), Vector3.Dot(bodyOffset, bodyForward)) * Mathf.Rad2Deg,
                ["elevationDegreesUp"] = Mathf.Atan2(bodyOffset.y, Vector3.ProjectOnPlane(bodyOffset, Vector3.up).magnitude) * Mathf.Rad2Deg,
                ["distanceMeters"] = bodyOffset.magnitude, ["horizontalDistanceMeters"] = Vector3.ProjectOnPlane(bodyOffset, Vector3.up).magnitude,
                ["bodyPosition"] = SceneSpatialVector(current.BodyPosition), ["bodyForward"] = SceneSpatialVector(bodyForward),
                ["scope"] = "Body-relative bearing to the same frozen historical point, measured at current capture time. Distinct from head/camera bearing; never a route or action authorization."
            },
            ["selfPose"] = selfPose
        };
    }

    private JArray BuildSceneDialogueViewContext(SceneVisionObservation current)
    {
        var result = new JArray();
        if (!SceneSpatialObservationUsable(current) || !IsFreshSceneObservation(current)) return result;
        var delivered = GetScenePlanningHistoryImages(current);
        foreach (var sample in m_SceneDialogueViews)
        {
            if (!SceneDialogueViewCurrent(sample) || sample.observation.CaptureId == current.CaptureId) continue;
            bool attached = false;
            foreach (var image in delivered) if (image.CaptureId == sample.observation.CaptureId) attached = true;
            result.Add(new JObject { ["captureId"] = sample.observation.CaptureId, ["imageRole"] = "historical-dialogue-view", ["visualObservation"] = JObject.Parse(sample.observation.MetadataJson),
                ["imageAttachedToThisPlanner"] = attached, ["ageSeconds"] = Mathf.Max(0, Time.realtimeSinceStartup - sample.observation.CapturedAtRealtime),
                ["returnDirection"] = SceneHistoricalViewBearing(sample.observation, current), ["verifiedEntity"] = false, ["actionAuthorization"] = false,
                ["scope"] = "Actual earlier RGB supplied to a formal visual consumer in this room/avatar/model binding. No assistant description was accepted as an identified entity. Use attached pixels only as an unverified search hypothesis; reobserve and independently match the user's target in a fresh current image before acting." });
        }
        return result;
    }

    private IReadOnlyList<SceneVisionObservation> GetScenePlanningHistoryImages(SceneVisionObservation current)
    {
        var result = new List<SceneVisionObservation>();
        if (m_ChatSettings?.m_ChatModel?.SupportsRoomObservationHistory != true ||
            !SceneSpatialObservationUsable(current) || !IsFreshSceneObservation(current)) return result;
        var sweep = GetSceneSweepImages(current);
        SceneVisionObservation dialogue = null;
        for (int i = m_SceneDialogueViews.Count - 1; i >= 0; i--)
        {
            var sample = m_SceneDialogueViews[i];
            if (!SceneDialogueViewCurrent(sample) || sample.observation.CaptureId == current.CaptureId) continue;
            bool duplicate = false;
            foreach (var frame in sweep) if (frame.CaptureId == sample.observation.CaptureId) duplicate = true;
            if (duplicate) continue;
            if (dialogue == null) dialogue = sample.observation;
            if (!SceneDialogueSameView(sample.observation, current)) { dialogue = sample.observation; break; }
        }
        // Preserve up to three evenly distributed sweep samples when one earlier
        // dialogue frame is available; never increase the four-image wire budget.
        return MergeScenePlanningHistoryImages(sweep, dialogue);
    }

    private static IReadOnlyList<SceneVisionObservation> MergeScenePlanningHistoryImages(IReadOnlyList<SceneVisionObservation> sweep, SceneVisionObservation dialogue)
    {
        var result = new List<SceneVisionObservation>();
        int count = Mathf.Min(sweep.Count, dialogue == null ? 4 : 3);
        for (int i = 0; i < count; i++) result.Add(sweep[count <= 1 ? 0 : Mathf.RoundToInt(i * (sweep.Count - 1f) / (count - 1))]);
        if (dialogue != null) result.Add(dialogue);
        return result;
    }

    // These samples come from rendered opaque pixels. Live transforms are used only
    // to revoke a stale sample, never to update an unseen object's remembered position.
    private bool SceneSpatialObservationUsable(SceneVisionObservation observation)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        return m_EnableSceneObjectAwareness && SceneEyesOpen &&
            bridge != null && bridge.environmentRoot != null && bridge.environmentRoot == m_SceneObjectEnvironment &&
            bridge.avatar != null && m_SceneVisionCapture != null && m_SceneVisionCapture.Target == bridge.avatar &&
            m_SceneVisionCapture.IsObservationBindingCurrent(observation) &&
            observation.HasSurfaceGrounding && SceneVisionGrounding.Fresh(observation);
    }

    private void RememberSceneSpatialSample(SceneObservedObject value, JArray capabilities, string purpose)
    {
        if (value == null || !value.VisibleConfirmed || value.source != m_SceneVisionCapture ||
            !SceneSpatialObservationUsable(value.observation) || !value.TryValidate(out _)) return;
        if (m_KnownSceneObjects.TryGetValue(value.EntityId, out var previous) &&
            previous.observed != null && previous.observed.observation.CapturedAtRealtime > value.observation.CapturedAtRealtime)
            return; // GPU callbacks may arrive after a newer real sample.

        var root = value.Root;
        bool snapshotMatchesGeometry = value.entry.TryValidateObserved(true, out _);
        var facts = new JObject {
            ["entityId"] = value.EntityId, ["sceneLabel"] = value.SceneLabel,
            ["identitySource"] = value.IdentitySource, ["captureId"] = value.CaptureId,
            ["observedUtc"] = value.CapturedAtUtc, ["capturedAtRealtime"] = value.observation.CapturedAtRealtime,
            ["pointAtCapture"] = SceneSpatialVector(value.WorldPoint),
            ["cameraPositionAtCapture"] = SceneSpatialVector(value.observation.CameraPosition),
            ["cameraRotationAtCapture"] = SceneSpatialRotation(value.observation.CameraRotation),
            ["bodyPositionAtCapture"] = value.observation.HasBodyPose ? SceneSpatialVector(value.observation.BodyPosition) : null,
            ["bodyRotationAtCapture"] = value.observation.HasBodyPose ? SceneSpatialRotation(value.observation.BodyRotation) : null,
            ["viewDirectionWorldAtCapture"] = SceneSpatialVector(value.observation.CameraRotation * Vector3.forward),
            ["capabilities"] = capabilities?.DeepClone() ?? new JArray("inspect-object", "look-at"),
            ["samplePurpose"] = purpose ?? "",
            ["rootPositionAtCapture"] = snapshotMatchesGeometry ? SceneSpatialVector(root.position) : null,
            ["rootRotationAtCapture"] = snapshotMatchesGeometry ? SceneSpatialRotation(root.rotation) : null,
            ["rootScaleAtCapture"] = snapshotMatchesGeometry ? SceneSpatialVector(root.lossyScale) : null,
            ["scope"] = "Previously confirmed opaque pixels and their exact capture-time geometry/view. Root snapshot is recorded only while captured renderer geometry still matches. Historical location is a search hint, never current visibility, a navigation destination, device state or action permission."
        };
        m_KnownSceneObjects[value.EntityId] = new KnownSceneObject {
            root = root, avatar = GetComponent<ArdyRoomDialogueBridge>()?.avatar,
            source = value.source, observed = value, lastSeen = value.observation.CapturedAtRealtime,
            facts = facts, hasRootSnapshot = snapshotMatchesGeometry,
            rootPosition = snapshotMatchesGeometry ? root.position : default,
            rootRotation = snapshotMatchesGeometry ? root.rotation : Quaternion.identity,
            rootScale = snapshotMatchesGeometry ? root.lossyScale : default,
            positionInvalidated = !snapshotMatchesGeometry
        };
    }

    private bool KnownSceneSpatialPositionValid(KnownSceneObject known, out string reason)
    {
        reason = null;
        if (known == null || !known.hasRootSnapshot || known.positionInvalidated || known.source == null ||
            !known.source.IsObservationHistoryBindingCurrent(known.observed?.observation))
        { reason = "captured-position-invalidated; new opaque-pixel observation required"; return false; }
        if (known.root == null || Vector3.Distance(known.root.position, known.rootPosition) > .002f ||
            Quaternion.Angle(known.root.rotation, known.rootRotation) > .1f ||
            Vector3.Distance(known.root.lossyScale, known.rootScale) > .002f ||
            known.observed == null || !known.observed.entry.TryValidateObserved(true, out _))
        {
            known.positionInvalidated = true;
            reason = "bound object or captured geometry changed; old point retained as history only";
            return false;
        }
        return true;
    }

    private JObject KnownSceneSpatialFacts(KnownSceneObject known)
    {
        var facts = (JObject)known.facts.DeepClone();
        bool valid = KnownSceneSpatialPositionValid(known, out string reason);
        facts["historical"] = true;
        facts["positionMemoryValid"] = valid;
        facts["positionInvalidReason"] = reason;
        facts["requiresReobservation"] = true;
        facts["actionAuthorization"] = false;
        facts["currentVisibility"] = "not-established";
        return facts;
    }

    private JObject BuildSceneSpatialMemory(SceneVisionObservation current)
    {
        bool viewValid = SceneSpatialObservationUsable(current) && IsFreshSceneObservation(current);
        var objects = new JArray();
        if (viewValid)
        {
            foreach (var item in m_KnownSceneObjects)
            {
                if (!KnownSceneObjectBinding(item.Key, out var known)) continue;
                var facts = KnownSceneSpatialFacts(known);
                facts["sampleAgeSeconds"] = Mathf.Max(0, Time.realtimeSinceStartup - known.lastSeen);
                facts["relativeToCurrentView"] = null;
                facts["relativeToCurrentBody"] = null;
                if ((bool)facts["positionMemoryValid"])
                {
                    var bearing = BuildSceneRememberedPointBearing(known.observed.WorldPoint, current,
                        known.observed.CaptureId, known.observed.observation);
                    facts["relativeToCurrentView"] = bearing?["relativeToCurrentView"]?.DeepClone();
                    facts["relativeToCurrentBody"] = bearing?["relativeToCurrentBody"]?.DeepClone();
                    facts["selfPose"] = bearing?["selfPose"]?.DeepClone();
                }
                objects.Add(facts);
            }
        }
        return new JObject {
            ["source"] = "Unity-known-object-capture-memory",
            ["viewCaptureId"] = viewValid ? current.CaptureId : null,
            ["objects"] = objects,
            ["selfPose"] = viewValid ? BuildSceneSelfPoseContext(current) : null,
            ["viewSamples"] = viewValid ? SceneSpatialViewHistory() : new JArray(),
            ["dialogueViews"] = viewValid ? BuildSceneDialogueViewContext(current) : new JArray(),
            ["requiresReobservation"] = true, ["actionAuthorization"] = false,
            ["scope"] = "Objects are previously opaque-confirmed entities; dialogueViews are separate unverified earlier RGB views, with no inferred entity or permission from assistant prose. Both retain their actual capture source and pose in this room/avatar/visual binding. Hidden object transforms can invalidate old object samples but never supply a new position. Use historical bearings/images to choose a fresh view, then independently match its actual pixels before approaching or operating. Empty/invalid memory does not establish absence."
        };
    }

    // Accept actual frames obtained during a turn even when the head has already
    // moved farther by callback time. Binding, age and that frame's opaque ID pixels
    // remain mandatory; no current-orientation compatibility check belongs here.
    private JArray ObserveKnownSceneObjects(SceneVisionObservation observation, string purpose)
    {
        var result = new JArray();
        if (!SceneSpatialObservationUsable(observation)) return result;
        ObserveSceneVisibleCatalog(observation, purpose);
        foreach (var sample in m_SceneSpatialViewSamples)
            if (sample.source == m_SceneVisionCapture && sample.observation.CaptureId == observation.CaptureId)
                return (JArray)sample.confirmations.DeepClone();
        var ids = new List<string>(m_KnownSceneObjects.Keys);
        var confirmedIds = new JArray();
        var notVisibleIds = new JArray();
        foreach (string id in ids)
        {
            if (!KnownSceneObjectBinding(id, out var known)) continue;
            if (!SceneVisionGrounding.TryFindObject(m_SceneVisionCapture, observation, id, out var observed, out _) ||
                !observed.VisibleConfirmed || observed.Root != known.root || !observed.TryValidate(out _))
            { notVisibleIds.Add(id); continue; }
            RememberSceneSpatialSample(observed, known.facts["capabilities"] as JArray, purpose);
            confirmedIds.Add(id);
            result.Add(new JObject {
                ["entityId"] = id, ["captureId"] = observation.CaptureId,
                ["observedUtc"] = observation.CapturedAtUtc, ["pointAtCapture"] = SceneSpatialVector(observed.WorldPoint),
                ["imagePoint"] = new JObject { ["x"] = observed.ImagePoint.x, ["y"] = observed.ImagePoint.y },
                ["scope"] = "Same known entity's opaque pixels confirmed in this sampled frame only. No selected-object replacement, device read, state change, action or claim of visibility at callback time."
            });
        }
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        m_SceneSpatialViewSamples.Add(new SceneSpatialViewSample {
            source = m_SceneVisionCapture, observation = observation, environment = bridge.environmentRoot, avatar = bridge.avatar, model = m_ChatSettings?.m_ChatModel,
            confirmations = (JArray)result.DeepClone(),
            facts = new JObject {
                ["captureId"] = observation.CaptureId, ["capturedAtUtc"] = observation.CapturedAtUtc,
                ["capturedAtRealtime"] = observation.CapturedAtRealtime, ["purpose"] = purpose ?? "",
                ["cameraPosition"] = SceneSpatialVector(observation.CameraPosition),
                ["cameraRotation"] = SceneSpatialRotation(observation.CameraRotation),
                ["bodyPosition"] = observation.HasBodyPose ? SceneSpatialVector(observation.BodyPosition) : null,
                ["bodyRotation"] = observation.HasBodyPose ? SceneSpatialRotation(observation.BodyRotation) : null,
                ["viewDirectionWorld"] = SceneSpatialVector(observation.CameraRotation * Vector3.forward),
                ["confirmedKnownEntityIds"] = confirmedIds, ["notVisibleKnownEntityIds"] = notVisibleIds,
                ["historical"] = true,
                ["scope"] = "Actual RGB and opaque ID sampling at this pose/time. notVisibleKnownEntityIds means no valid opaque confirmation in this frame only; it does not prove absence, a searched room region, or visibility in another/current frame. Unknown objects are not enumerated."
            }
        });
        m_SceneSpatialViewSamples.Sort((a, b) => a.observation.CapturedAtRealtime.CompareTo(b.observation.CapturedAtRealtime));
        while (m_SceneSpatialViewSamples.Count > 6) m_SceneSpatialViewSamples.RemoveAt(0);
        return result;
    }

    private JArray SceneSpatialViewHistory()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var result = new JArray();
        foreach (var sample in m_SceneSpatialViewSamples)
            if (SceneHistoricalObservationReadable(sample.source, sample.observation, sample.environment, sample.avatar, sample.model))
                result.Add(sample.facts.DeepClone());
        return result;
    }

    private static JObject SceneSpatialVector(Vector3 value) => new JObject { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z };
    private static JObject SceneSpatialRotation(Quaternion value) => new JObject { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z, ["w"] = value.w };
}
