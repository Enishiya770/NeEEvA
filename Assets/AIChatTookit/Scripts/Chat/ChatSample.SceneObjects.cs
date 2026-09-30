using System;
using System.Collections;
using System.Collections.Generic;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    [SerializeField, Tooltip("将关注像素关联到当前可见物件；仅显式登记的设备提供开关能力。")]
    private bool m_EnableSceneObjectAwareness = true;
    [SerializeField, Range(.5f, 3f)] private float m_SceneDeviceDistance = 2.5f;
    private Transform m_SceneObjectEnvironment;
    private bool m_SceneObjectCatalogAttempted;
    private JObject m_SelectedSceneObject, m_SceneObjectStateResult;
    private SceneObjectLightSwitch m_SelectedSceneDevice, m_SceneResultDevice;
    private Transform m_SceneResultEnvironment;
    private UniVRM10.Vrm10Instance m_SceneResultAvatar;
    private readonly Queue<JObject> m_RecentSceneObjects = new Queue<JObject>();
    private readonly Dictionary<string, float> m_SceneDeviceChangedAt = new Dictionary<string, float>();
    private sealed class KnownSceneObject
    {
        public Transform root;
        public UniVRM10.Vrm10Instance avatar;
        public JObject facts;
        public float lastSeen;
    }
    private readonly Dictionary<string, KnownSceneObject> m_KnownSceneObjects = new Dictionary<string, KnownSceneObject>();

    private void EnsureSceneObjectCatalog()
    {
        if (!m_EnableSceneObjectAwareness || !m_EnableSceneActions || !Application.isPlaying) return;
        var environment = GetComponent<ArdyRoomDialogueBridge>()?.environmentRoot;
        if (environment == null || environment.gameObject.scene != gameObject.scene) return;
        if (m_SceneObjectEnvironment != environment)
        {
            m_SceneObjectEnvironment = environment; m_SceneObjectCatalogAttempted = false;
            m_SelectedSceneObject = null; m_SelectedSceneDevice = null; m_SceneResultDevice = null; m_SceneObjectStateResult = null;
            m_RecentSceneObjects.Clear(); m_SceneDeviceChangedAt.Clear(); m_KnownSceneObjects.Clear();
        }
        if (m_SceneObjectCatalogAttempted) return;
        // The first continuous-vision setup can run from OnEnable while the
        // scene is still loading. Do not consume the one profile scan then.
        // A later formal observation calls this again before capturing pixels;
        // only ready environments reach the potentially expensive structure scan.
        if (!SceneObjectLampCatalog.IsEnvironmentReady(environment, out _)) return;
        m_SceneObjectCatalogAttempted = true;
        if (SceneObjectLampCatalog.EnsureRegistered(environment, out _, out string failure))
            Debug.Log("[Vision/Object] registered audited runtime lamp profile; local facts exposed only after visible selection", this);
        else Debug.Log("[Vision/Object] no lamp capability installed: " + failure, this);
    }

    private JObject BuildSceneObjectFacts()
    {
        var environment = GetComponent<ArdyRoomDialogueBridge>()?.environmentRoot;
        bool same = environment != null && environment == m_SceneObjectEnvironment;
        bool deviceEvidenceValid = same && SceneResultBindingValid() &&
            JToken.DeepEquals(m_SceneObjectStateResult?["actual"],
                JObject.Parse(JsonUtility.ToJson(m_SceneResultDevice.ReadState())));
        return new JObject {
            ["enabled"] = m_EnableSceneObjectAwareness && m_EnableSceneActions && SceneEyesOpen,
            ["selected"] = same ? m_SelectedSceneObject?.DeepClone() : null,
            ["stateResult"] = same ? m_SceneObjectStateResult?.DeepClone() : null,
            ["recentObservations"] = same ? new JArray(m_RecentSceneObjects) : new JArray(),
            ["knownObjectHistory"] = same ? KnownSceneObjectHistory() : new JArray(),
            ["targetReview"] = SceneObjectTargetContext(),
            ["lastDeviceStateEvidenceValid"] = deviceEvidenceValid,
            ["deviceEvidenceScope"] = "Engine result-validity check only; false invalidates prior device state claims. No new device setting or visual observation is supplied here; inspect again to establish current facts.",
            ["rejectionCauseScope"] = "Pixel/identity resolution failure does not establish which intended object was selected. Missing registered power capability concerns only the resolved entity and this software interface. Neither finding diagnoses electricity, cables, outlets, broken hardware or whether the intended lamp is a nonfunctional ornament. A different selected entity does not erase the prior device's successful operation.",
            ["scope"] = "Only previously selected visible entities, observed timestamps and explicit device results. These are snapshots, not continuous perception or a room inventory. Scene-authored labels are annotations, not visual recognition. Hierarchy does not prove support/contact. No arbitrary Inspector edits or physical hand interaction. On/off controls the registered emitter and Light flag; baked lighting may remain.",
            ["deviceDistanceLimitMeters"] = Mathf.Clamp(m_SceneDeviceDistance, .5f, 3f)
        };
    }

    private JObject BuildSpatialObjectMemory()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        bool same = m_EnableSceneObjectAwareness && bridge != null && bridge.environmentRoot != null &&
            bridge.environmentRoot == m_SceneObjectEnvironment;
        // KnownSceneObjectHistory validates scene/avatar/identity ownership, but does not read
        // current transforms or claim that a historical entity is visible in the current frame.
        return SpatialObjectMemory.Project(same ? KnownSceneObjectHistory() : new JArray(),
            same && SceneResultBindingValid() ? m_SceneObjectStateResult : null, DateTime.UtcNow);
    }

    private bool KnownSceneObjectBinding(string entityId, out KnownSceneObject value)
    {
        value = null;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (string.IsNullOrEmpty(entityId) || bridge == null || bridge.environmentRoot == null ||
            bridge.environmentRoot != m_SceneObjectEnvironment || !m_KnownSceneObjects.TryGetValue(entityId, out value) ||
            value.root == null || value.avatar == null || value.avatar != bridge.avatar ||
            value.root.gameObject.scene != gameObject.scene || !value.root.gameObject.activeInHierarchy ||
            (value.root != m_SceneObjectEnvironment && !value.root.IsChildOf(m_SceneObjectEnvironment))) return false;
        if ((string)value.facts["identitySource"] == "scene-authored")
        {
            var identity = value.root.GetComponent<SceneObjectIdentity>();
            if (identity == null || !identity.enabled || identity.EntityId != entityId) return false;
        }
        return true;
    }

    private JArray KnownSceneObjectHistory()
    {
        var result = new JArray();
        foreach (var item in m_KnownSceneObjects)
            if (KnownSceneObjectBinding(item.Key, out var known)) result.Add(known.facts.DeepClone());
        return result;
    }

    // Re-find only objects this character previously identified. Never enumerate unobserved scene objects.
    private JArray BuildVisibleKnownObjects(SceneVisionObservation observation)
    {
        var result = new JArray();
        if (!m_EnableSceneObjectAwareness || !SceneEyesOpen || observation == null || !IsFreshSceneObservation(observation)) return result;
        foreach (var item in m_KnownSceneObjects)
        {
            if (!KnownSceneObjectBinding(item.Key, out var known)) continue;
            if (!m_SceneVisionCapture.TryFindObservedObject(observation, item.Key, out var current, out _) ||
                !current.VisibleConfirmed || current.Root != known.root || !current.TryValidate(out _)) continue;
            result.Add(new JObject {
                ["captureId"]=observation.CaptureId, ["entityId"]=item.Key,
                ["sceneLabel"]=current.SceneLabel, ["identitySource"]=current.IdentitySource,
                ["imagePoint"]=new JObject { ["x"]=current.ImagePoint.x, ["y"]=current.ImagePoint.y },
                ["deviceOperationPrerequisite"]=BuildSceneDeviceOperationPrerequisite(current),
                ["scope"]="Same previously identified entity confirmed visible in this captured frame. Copy entityTarget for reference; execution still requires a new physical-view confirmation. No new device-state read or capability grant."
            });
        }
        return result;
    }

    private void RememberSceneObject(SceneObservedObject value, SceneObjectLightSwitch device)
    {
        if (value == null || !value.VisibleConfirmed) return;
        var capabilities = new JArray("inspect-object", "look-at");
        if (device != null && device.IsBound && device.EntityId == value.EntityId && device.TryValidate(out _)) capabilities.Add("set-object-state:on|off");
        m_SelectedSceneObject = new JObject {
            ["entityId"] = value.EntityId, ["sceneLabel"] = value.SceneLabel,
            ["identitySource"] = value.IdentitySource, ["captureId"] = value.CaptureId,
            ["observedUtc"] = value.CapturedAtUtc, ["visibleInCapturedFrame"] = true,
            ["pointAtCapture"] = JObject.FromObject(new { x=value.WorldPoint.x, y=value.WorldPoint.y, z=value.WorldPoint.z }),
            ["imagePointAtCapture"] = new JObject { ["x"] = value.ImagePoint.x, ["y"] = value.ImagePoint.y },
            ["observerPoseAtCapture"] = new JObject {
                ["position"] = JObject.FromObject(new { x=value.observation.CameraPosition.x, y=value.observation.CameraPosition.y, z=value.observation.CameraPosition.z }),
                ["rotation"] = JObject.FromObject(new { x=value.observation.CameraRotation.x, y=value.observation.CameraRotation.y, z=value.observation.CameraRotation.z, w=value.observation.CameraRotation.w }) },
            ["scope"] = value.MeasurementScope, ["capabilities"] = capabilities,
            ["runtimeDeviceStateReadAfterObservation"] = device == null ? null : JObject.Parse(JsonUtility.ToJson(device.ReadState())),
            ["stateReadAtUtc"] = device == null ? null : DateTime.UtcNow.ToString("o"),
            ["localStructure"] = device == null ? null : new JObject {
                ["source"]="engine-registered-local-structure; not an inference from image or proof that every part is visible",
                ["relation"]="parts-of-selected-entity; does not mean resting-on furniture",
                ["emissivePartName"]=device.EmissionRenderer != null ? device.EmissionRenderer.name : "",
                ["lightComponentNodeName"]=device.DeviceLight != null ? device.DeviceLight.name : ""
            }
        };
        m_SelectedSceneDevice = device;
        if (!m_KnownSceneObjects.ContainsKey(value.EntityId) && m_KnownSceneObjects.Count >= 16)
        {
            string oldest = null; float time = float.MaxValue;
            foreach (var entry in m_KnownSceneObjects) if (entry.Value.lastSeen < time) { oldest=entry.Key; time=entry.Value.lastSeen; }
            if (oldest != null) m_KnownSceneObjects.Remove(oldest);
        }
        m_KnownSceneObjects[value.EntityId] = new KnownSceneObject {
            root=value.Root, avatar=GetComponent<ArdyRoomDialogueBridge>()?.avatar, lastSeen=Time.realtimeSinceStartup,
            facts=PickRoomFields(m_SelectedSceneObject, "entityId", "sceneLabel", "identitySource", "captureId", "observedUtc", "capabilities",
                "pointAtCapture", "imagePointAtCapture", "observerPoseAtCapture", "runtimeDeviceStateReadAfterObservation", "stateReadAtUtc")
        };
        m_RecentSceneObjects.Enqueue(PickRoomFields(m_SelectedSceneObject,
            "entityId", "sceneLabel", "identitySource", "captureId", "observedUtc"));
        while (m_RecentSceneObjects.Count > 8) m_RecentSceneObjects.Dequeue();
        Debug.Log("[Vision/Object] " + m_SelectedSceneObject.ToString(Newtonsoft.Json.Formatting.None), this);
    }

    private bool SceneResultBindingValid()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        return bridge != null && m_SceneResultDevice != null && m_SceneResultDevice.Root != null &&
            m_SceneResultEnvironment != null && bridge.environmentRoot == m_SceneResultEnvironment &&
            m_SceneResultAvatar != null && bridge.avatar == m_SceneResultAvatar &&
            m_SceneResultDevice.Root.gameObject.scene == gameObject.scene &&
            (m_SceneResultDevice.Root == m_SceneResultEnvironment || m_SceneResultDevice.Root.IsChildOf(m_SceneResultEnvironment));
    }

    // This stamp is internal to the public-speech freshness check, never a source of hidden visual facts.
    private JToken SceneObjectSpeechStamp() => new JObject {
        ["result"] = m_SceneObjectStateResult?.DeepClone(),
        ["binding"] = SceneResultBindingValid() ? m_SceneResultDevice.EntityId : null,
        ["currentDeviceState"] = SceneResultBindingValid() ? JObject.Parse(JsonUtility.ToJson(m_SceneResultDevice.ReadState())) : null
    };

    private void BeginSceneObjectAction(RoomTaskProtocol.Decision decision, SceneVisionObservation observation,
        int generation, Action completed)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        string reject = null;
        SceneObservedObject selected = null;
        bool canReviewSelection = false;
        string requestedCapture = decision.entityTarget?.captureId ?? decision.visualTarget?.captureId;
        string requestedEntity = decision.entityTarget?.entityId;
        if (!m_EnableSceneObjectAwareness || !m_EnableSceneActions || !RoomMotionOutputEnabled || !SceneEyesOpen)
            reject = "scene-object-actions-disabled";
        else if (m_SceneActionActive || bridge == null || !bridge.IsConnected || bridge.ReservesBody || bridge.IsFollowing)
            reject = "existing-body-task-has-priority";
        else if (observation == null || string.IsNullOrEmpty(requestedCapture) ||
            observation.CaptureId != requestedCapture || !IsFreshSceneObservation(observation))
            reject = "visual-reference-not-current";
        else if (decision.entityTarget != null)
        {
            canReviewSelection = true;
            if (decision.visualTarget != null || !KnownSceneObjectBinding(requestedEntity, out var known))
                reject = "entity-reference-not-known-in-current-room";
            else if (!m_SceneVisionCapture.TryFindObservedObject(observation, requestedEntity, out selected, out reject) ||
                !selected.VisibleConfirmed || selected.Root != known.root || !selected.TryValidate(out reject))
                reject = "referenced-entity-not-visible-in-current-capture: " + (reject ?? "no-confirmed-opaque-part");
            else reject = null;
        }
        else
        {
            canReviewSelection = true;
            var pixel = new Vector2(decision.visualTarget.x, decision.visualTarget.y);
            m_SceneVisionCapture.TryResolveObservedObject(observation, pixel, out selected, out reject);
            if (selected != null && !selected.VisibleConfirmed &&
                m_SceneVisionCapture.TryFindObservedObject(observation, selected.EntityId, out var opaque, out _)) selected = opaque;
            if (selected?.VisibleConfirmed == true && selected.TryValidate(out reject)) reject = null;
            else if (decision.intent == "look-at") { selected = null; reject = null; }
            else reject = "visible-object-not-confirmed: " + (reject ?? "only-transparent-or-unknown-candidate");
        }
        // A wrong/unknown referent can be repaired against candidates in THIS current picture.
        // Source freshness, body ownership and output gates above are never bypassed.
        if (canReviewSelection && m_EnableSceneObjectTargetMatching) reject = null;
        // An already bound entity cannot acquire power control from another matcher
        // response. Explicit reselection may propose a different visible referent;
        // it still passes the same capability check after matching, before looking.
        if (reject == null && decision.IsObjectState && decision.entityTarget != null &&
            !decision.RequestsTargetReselection && selected?.VisibleConfirmed == true &&
            !TryGetSceneObjectPowerCapability(selected, bridge?.environmentRoot, out _, out string capabilityFailure))
            reject = capabilityFailure;
        if (reject != null)
        {
            m_RoomTaskDispatch = "not-submitted: " + reject;
            if (!m_SceneActionActive) RecordSceneAction(new SceneActionFact { intent=decision.intent, origin=decision.origin,
                captureId=observation?.CaptureId, visualLabel=decision.visualTarget?.label,
                requestedEntityId=requestedEntity, requestedState=decision.objectState,
                requestedImagePoint=decision.visualTarget == null ? Vector2.zero : new Vector2(decision.visualTarget.x, decision.visualTarget.y),
                targetResolutionSource=decision.entityTarget == null ? "image-pixel" : "remembered-entity" }, "rejected", reject);
            completed(); return;
        }
        var motion = new DialogueMotionIntent(decision.intent, "", generation, ++m_MotionSequence);
        RegisterMotionAction(ref motion); m_RoomTaskActionId = motion.ActionId;
        var fact = new SceneActionFact { actionId=motion.ActionId, intent=decision.intent, origin=decision.origin,
            gazeHold=decision.gazeHold,
            captureId=observation.CaptureId, visualLabel=decision.visualTarget?.label ?? selected?.SceneLabel,
            viewDirection=decision.viewPosition?.direction, viewPurpose=decision.viewPosition?.purpose,
            requestedViewDistance=decision.viewPosition?.distance ?? 0,
            viewPurposeStatus=decision.IsViewPosition ? "not-yet-moved" : null,
            requestedEntityId=requestedEntity, requestedState=decision.objectState,
            requestedImagePoint=decision.visualTarget == null ? selected?.ImagePoint ?? new Vector2(.5f,.5f) : new Vector2(decision.visualTarget.x, decision.visualTarget.y),
            targetResolutionSource=decision.entityTarget == null ? "image-pixel" : "remembered-entity",
            entityId=selected?.EntityId, objectConfirmed=selected?.VisibleConfirmed == true,
            focusKind=selected == null ? "image-region-bearing" : "visible-object",
            scope="Image-region focus proves a view change only. Object identity is separately confirmed from visible opaque geometry. Scene labels are authored. Device operation is bounded runtime control, never physical touching or navigation." };
        if (!TryHandOffSceneViewToObjectPreparation(fact, generation))
            CancelPersistentSceneAttention("new-scene-target");
        m_SceneActionActive = true; int epoch = ++m_SceneActionEpoch;
        m_PendingSceneFormalCaptureGeneration = generation;
        RecordSceneAction(fact, "preparing", "preparing selected-object observation or image-region focus");
        StartCoroutine(RunScenePreparationSafely(PrepareSceneObject(decision, observation, selected, fact, bridge, generation, epoch, completed), fact, generation, epoch));
    }

    private IEnumerator PrepareSceneObject(RoomTaskProtocol.Decision decision, SceneVisionObservation initial,
        SceneObservedObject selected, SceneActionFact fact, ArdyRoomDialogueBridge bridge, int generation, int epoch, Action completed)
    {
        var source = m_SceneVisionCapture;
        var avatar = bridge.avatar;
        var environment = bridge.environmentRoot;
        var controller = bridge.Controller;
        int workEpoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn;
        var planCurrent = CaptureSceneActorPlanLease(decision, generation);
        bool handedToNavigation = false;
        bool BindingCurrent() => source != null && source == m_SceneVisionCapture && source.Target == avatar &&
            avatar != null && environment != null && bridge.avatar == avatar && bridge.environmentRoot == environment &&
            bridge.Controller == controller && controller != null && controller.Avatar == avatar &&
            bridge.Navigation != null && bridge.Navigation.EnvironmentRoot == environment &&
            environment.gameObject.scene == gameObject.scene;
        bool Current() => epoch == m_SceneActionEpoch && generation == m_FormalResponseGeneration && isActiveAndEnabled &&
            SceneEyesOpen && !m_UserSpeechActiveForAutonomy && bridge != null && bridge.IsConnected &&
            workEpoch == m_WorkEpoch && userTurn == m_ActionConstraints.CurrentUserTurn && planCurrent();
        void Finish(string phase, string reason)
        {
            if (!Current()) return;
            m_SceneActionActive = false; m_PendingSceneFormalCaptureGeneration = -1;
            RecordSceneAction(fact, phase, reason); m_RoomTaskDispatch = phase + ": " + reason; completed();
        }
        void Reject(string reason) { m_SceneAttention?.Release(fact.actionId, reason); Finish("rejected", reason); }
        try
        {
            if (!Current()) yield break;
            if (!BindingCurrent()) { Reject("object-source-binding-changed"); yield break; }
            if (bridge.ReservesBody || bridge.IsFollowing) { Reject("existing-body-task-has-priority"); yield break; }
            if (m_EnableSceneObjectTargetMatching)
            {
                string matchFailure = null; SceneObservedObject matched = null;
                yield return MatchSceneObjectTarget(decision, initial, selected, fact,
                    () => Current() && BindingCurrent() && !bridge.ReservesBody && !bridge.IsFollowing,
                    (value, reason) => { matched = value; matchFailure = reason; });
                if (!Current()) yield break;
                if (!BindingCurrent()) { Reject("object-source-binding-changed"); yield break; }
                if (matched == null && decision.intent == "look-at" && decision.visualTarget != null &&
                    IsFreshSceneObservation(initial) && (fact.targetMatchVerdict == "uncertain" ||
                    (matchFailure ?? "").StartsWith("object-candidate-view-unavailable", StringComparison.Ordinal)))
                {
                    // Looking toward an unresolved image region is an observation, not a semantic match.
                    fact.entityId = null; fact.objectConfirmed = fact.semanticTargetConfirmed = false;
                    fact.focusKind = "image-region-bearing"; fact.needsCloserObservation = true;
                }
                else if (matched == null) { Reject(matchFailure ?? "object-target-match-interrupted"); yield break; }
                selected = matched;
            }
            if (decision.IsObjectState &&
                !TryGetSceneObjectPowerCapability(selected, environment, out _, out string capabilityFailure))
            { Reject(capabilityFailure); yield break; }
            var eyes = m_SceneVisionCapture.Target.GetComponent<NevaEyeTracking>();
            if (eyes == null || !eyes.IsInitialized || !eyes.isActiveAndEnabled) { Reject("calibrated-attention-rig-unavailable"); yield break; }
            float deadline = Time.realtimeSinceStartup + 2f;
            while (Current() && m_SceneAttention != null && m_SceneAttention.IsReturning && Time.realtimeSinceStartup < deadline) yield return null;
            if (!Current()) yield break;
            if (m_SceneAttention != null && m_SceneAttention.IsReturning) { Reject("prior-attention-still-returning"); yield break; }
            if (m_SceneAttention == null) m_SceneAttention = GetComponent<NevaSceneAttention>() ?? gameObject.AddComponent<NevaSceneAttention>();
            if (m_SceneAttention.Target != eyes.Target) m_SceneAttention.Configure(eyes);
            float tangent = Mathf.Tan(initial.VerticalFov * Mathf.Deg2Rad * .5f);
            Vector2 focusPixel = selected != null ? selected.ImagePoint : new Vector2(decision.visualTarget.x, decision.visualTarget.y);
            Vector3 ray = new Vector3((focusPixel.x * 2f - 1f) * tangent * initial.Width / initial.Height,
                (1f - focusPixel.y * 2f) * tangent, 1f).normalized;
            Vector3 focus = selected == null ? initial.CameraPosition + initial.CameraRotation * ray * 3f : selected.WorldPoint;
            if (!m_SceneAttention.TryLookAt(fact.actionId, focus, out string failure, 12f)) { Reject(failure); yield break; }
            deadline = Time.realtimeSinceStartup + 3f;
            while (Current() && !m_SceneAttention.IsSettled && Time.realtimeSinceStartup < deadline) yield return null;
            if (!Current()) yield break;
            if (!m_SceneAttention.IsSettled) { Reject("attention-did-not-settle"); yield break; }
            SceneVisionObservation refreshed = null; bool captured = false;
            RequestFreshSceneObservation("scene-object-confirm", value => { refreshed=value; captured=true; });
            deadline = Time.realtimeSinceStartup + 4.5f;
            while (Current() && !captured && Time.realtimeSinceStartup < deadline) yield return null;
            if (!Current()) yield break;
            if (!BindingCurrent()) { Reject("object-source-binding-changed"); yield break; }
            if (!captured || refreshed == null || refreshed.CaptureId == initial.CaptureId || !IsFreshSceneObservation(refreshed))
            { Reject("focused-object-observation-unavailable"); yield break; }
            BindFormalSceneObservation(refreshed, generation); fact.confirmedCaptureId = refreshed.CaptureId;
            if (bridge.ReservesBody || bridge.IsFollowing) { Reject("body-task-started-during-object-confirmation"); yield break; }
            if (selected == null)
            {
                fact.resultCaptureId = refreshed.CaptureId;
                if (!RetainSceneAttention(fact)) { Reject("attention-retention-lost"); yield break; }
                Finish("observed", "actual head focused toward the image region and captured a new view; object identity and depth remain unconfirmed"); yield break;
            }
            if (!m_SceneVisionCapture.TryConfirmObject(refreshed, selected, out var confirmed, out failure) || !confirmed.VisibleConfirmed)
            { Reject(failure ?? "object-no-longer-visible"); yield break; }
            selected = confirmed; fact.entityId=selected.EntityId; fact.objectConfirmed=true;
            var device = selected.Root == null ? null : selected.Root.GetComponent<SceneObjectLightSwitch>();
            if (device != null && device.EntityId != selected.EntityId) device = null;
            RememberSceneObject(selected, device);
            CompleteSceneObservationGoal(decision, selected, fact);
            if (decision.intent == "approach-object" || decision.IsViewPosition)
            {
                yield return SubmitConfirmedSceneObjectApproach(decision, selected, refreshed, fact, bridge, generation, epoch, completed);
                handedToNavigation = m_SceneActionActive && (fact.phase == "submitted" || fact.phase == "arrival-observing");
                yield break;
            }
            if (!decision.IsObjectState)
            {
                fact.resultCaptureId=refreshed.CaptureId;
                if (!RetainSceneAttention(fact)) { Reject("attention-retention-lost"); yield break; }
                Finish("observed", "selected visible entity confirmed in a new view; local authored facts and registered capabilities recorded"); yield break;
            }
            if (!TryGetSceneObjectPowerCapability(selected, environment, out device, out failure))
            { Reject(failure); yield break; }
            if (device.Root != m_SceneObjectEnvironment && !device.Root.IsChildOf(m_SceneObjectEnvironment))
            { Reject("device-outside-bound-environment"); yield break; }
            if (Vector3.Distance(bridge.avatar.transform.position, device.Root.position) > Mathf.Clamp(m_SceneDeviceDistance, .5f, 3f))
            { Reject("device-outside-nearby-operation-distance"); yield break; }
            bool on = decision.objectState == "on";
            if (decision.objectState != "on" && decision.objectState != "off") { Reject("unsupported-object-state"); yield break; }
            if (decision.IsSelfInitiated && device.ReadState().on != on &&
                m_SceneDeviceChangedAt.TryGetValue(selected.EntityId, out float changedAt) && Time.realtimeSinceStartup - changedAt < 30f)
            { Reject("autonomous-device-change-cooldown"); yield break; }
            if (!Current()) yield break;
            if (!BindingCurrent()) { Reject("object-source-binding-changed"); yield break; }
            if (bridge.ReservesBody || bridge.IsFollowing) { Reject("body-task-started-before-device-operation"); yield break; }
            if (!m_EnableSceneActions || !m_EnableSceneObjectAwareness || !RoomMotionOutputEnabled)
            { Reject("scene-object-actions-disabled-before-operation"); yield break; }
            if (!selected.TryValidate(out failure)) { Reject(failure); yield break; }
            if (!device.TrySetPower(fact.actionId, on, out var result, out failure))
            {
                m_SceneResultDevice=device; m_SceneResultEnvironment=bridge.environmentRoot; m_SceneResultAvatar=bridge.avatar;
                m_SceneObjectStateResult=JObject.Parse(JsonUtility.ToJson(result));
                m_SceneObjectStateResult["appliedAtUtc"]=DateTime.UtcNow.ToString("o");
                m_SceneObjectStateResult["visualConfirmation"]="operation-failed-actual-state-retained";
                fact.stateChanged=result.changed;
                if (result.partial || result.changed) Finish("state-applied-unconfirmed", "device operation failed after a partial change; actual state retained: " + failure);
                else Reject(failure);
                yield break;
            }
            // Commit the real result before any asynchronous capture/cancellation. Never roll back a newer user's change.
            fact.stateChanged=result.changed;
            m_SceneResultDevice=device; m_SceneResultEnvironment=bridge.environmentRoot; m_SceneResultAvatar=bridge.avatar;
            if (result.changed) m_SceneDeviceChangedAt[selected.EntityId]=Time.realtimeSinceStartup;
            m_SceneObjectStateResult=JObject.Parse(JsonUtility.ToJson(result));
            m_SceneObjectStateResult["appliedAtUtc"]=DateTime.UtcNow.ToString("o");
            m_SceneObjectStateResult["visualConfirmation"]="pending";
            float appliedRealtime = Time.realtimeSinceStartup;
            RecordSceneAction(fact, "state-applied", "registered device state applied; awaiting a fresh image, not a physical switch press");
            refreshed=null; captured=false;
            RequestFreshSceneObservation("scene-object-state-result", value => { refreshed=value; captured=true; });
            deadline=Time.realtimeSinceStartup+4.5f;
            while (Current() && !captured && Time.realtimeSinceStartup < deadline) yield return null;
            if (!Current()) yield break;
            if (!BindingCurrent()) { Finish("state-applied-unconfirmed", "object binding changed after state was applied; historical result retained"); yield break; }
            bool newResultImage = captured && refreshed != null && refreshed.CaptureId != fact.confirmedCaptureId &&
                refreshed.CapturedAtRealtime >= appliedRealtime && IsFreshSceneObservation(refreshed);
            bool visible = newResultImage &&
                m_SceneVisionCapture.TryConfirmObject(refreshed, selected, out confirmed, out _);
            // Emission changes intentionally invalidate the old material snapshot; find the same registered entity in the new render.
            if (!visible && newResultImage)
                visible=m_SceneVisionCapture.TryFindObservedObject(refreshed, selected.EntityId, out confirmed, out _);
            if (visible)
            {
                BindFormalSceneObservation(refreshed, generation); fact.resultCaptureId=refreshed.CaptureId;
                m_SceneObjectStateResult["freshVisualObservation"]=true;
                m_SceneObjectStateResult["visualConfirmation"]="same-entity-in-new-image";
                m_SceneObjectStateResult["resultCaptureId"]=refreshed.CaptureId;
                RememberSceneObject(confirmed, device);
            }
            else m_SceneObjectStateResult["visualConfirmation"]="unavailable-state-result-retained";
            if (!device.TryValidate(out failure) || device.ReadState().on != on)
            { Finish("state-applied-unconfirmed", "device changed externally after operation; historical result retained, current state not confirmed"); yield break; }
            m_SceneAttention.RetainView(fact.actionId, 20f);
            Finish(visible ? (result.changed ? "performed" : "already-satisfied") : "state-applied-unconfirmed", visible ? (result.changed ?
                "runtime lamp emission and Light component state read back; same entity captured in a new image, without a visual brightness judgment; baked illumination may remain; no physical hand press" :
                "registered device was already in the requested state; read back and captured again, no new state change or physical hand press") :
                "runtime device state was processed, but new visual confirmation is unavailable; inspect changed to distinguish a real change from an already-satisfied request");
        }
        finally
        {
            if ((string)m_SceneObjectStateResult?["actionId"] == fact.actionId && (string)m_SceneObjectStateResult["visualConfirmation"] == "pending")
                m_SceneObjectStateResult["visualConfirmation"]="interrupted-after-state-applied";
            if (epoch == m_SceneActionEpoch && m_SceneActionActive && !handedToNavigation)
            { m_SceneAttention?.Release(fact.actionId, "object-preparation-ended"); m_SceneActionActive=false; m_PendingSceneFormalCaptureGeneration=-1; }
        }
    }

    private static bool TryGetSceneObjectPowerCapability(SceneObservedObject selected, Transform environment,
        out SceneObjectLightSwitch device, out string reason)
    {
        device = null; reason = "selected-object-has-no-registered-power-capability";
        if (selected?.VisibleConfirmed != true || selected.Root == null || !selected.TryValidate(out _)) return false;
        var candidate = selected.Root.GetComponent<SceneObjectLightSwitch>();
        if (candidate == null || candidate.Root != selected.Root || candidate.EntityId != selected.EntityId || !candidate.IsBound)
            return false;
        if (environment == null || (candidate.Root != environment && !candidate.Root.IsChildOf(environment)))
        { reason = "device-outside-bound-environment"; return false; }
        if (!candidate.TryValidate(out var invalid))
        { reason = "registered-power-capability-invalid: " + invalid; return false; }
        device = candidate; reason = null; return true;
    }
}
