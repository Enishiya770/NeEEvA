using System;
using System.Linq;
using Newtonsoft.Json.Linq;
#if UNITY_EDITOR
using System.Collections;
using System.Reflection;
using NeEEvA.Perception;
using NeEEvA.Motion;
using UnityEngine;
using UnityEngine.SceneManagement;
#endif

// Schema/parser regression plus an EditMode controlled current-frame geometry
// fixture. No HTTP, GPU image recognition, navigation or power writes are tested.
public static class RoomBoundEntityPrerequisiteRegression
{
    private static int checks;
    private const string Lamp = "scene-object-93d535ced013470e8d1ff5ad79a924fc";
    private const string Corrupt = "scene-object-93d535ced0134708ab073bab901d30be4";
    private static void Check(bool ok, string message)
    { ++checks; if (!ok) throw new InvalidOperationException(message); }

    private static bool Fits(JToken schema, JToken value)
    {
        string type = (string)schema["type"];
        bool Number(JToken t) => t.Type == JTokenType.Integer || t.Type == JTokenType.Float;
        if (type == "object" && value.Type != JTokenType.Object || type == "string" && value.Type != JTokenType.String ||
            type == "number" && !Number(value) || type == "integer" && value.Type != JTokenType.Integer) return false;
        if (schema["enum"] is JArray choices && !choices.Any(c => JToken.DeepEquals(c, value))) return false;
        if (value is JObject obj)
        {
            var props = schema["properties"] as JObject;
            if (schema["required"] is JArray required && required.Any(k => obj.Property((string)k) == null)) return false;
            foreach (var p in obj.Properties())
                if (props?[p.Name] == null ? (bool?)schema["additionalProperties"] == false : !Fits(props[p.Name], p.Value)) return false;
        }
        if (schema["oneOf"] is JArray one && one.Count(s => Fits(s, value)) != 1) return false;
        if (schema["anyOf"] is JArray any && !any.Any(s => Fits(s, value))) return false;
        return true;
    }

    public static int RunPureChecks()
    {
        checks = 0;
        foreach (string origin in new[] { "user", "actor_draft" })
        {
            var source = new RoomTaskProtocol.ActorProposalSource { Id = "current-source", Revision = 1,
                Origin = origin, Purpose = "plan", Text = "把刚才的灯打开，再观察旁边的柜子。", Coverage = "compound",
                PlanId = "current-plan", PlanRevision = 2, BoundEntityId = Lamp };
            var schema = JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null, source));
            var compatible = RoomTaskProtocol.StructuredResponseFormat(schema.ToString())["json_schema"]["schema"];
            JObject Wire(string intent) => new JObject { ["intent"] = intent, ["origin"] = origin, ["evidence"] = "",
                ["source_ref"] = RoomTaskProtocol.PlanSourceReference(source), ["planId"] = source.PlanId,
                ["planRevision"] = source.PlanRevision, ["planRemaining"] = source.Text,
                ["entityTarget"] = new JObject { ["captureId"] = "current-image", ["entityId"] = Lamp } };
            foreach (string intent in new[] { "approach-object", "inspect-object", "reobserve-object", "set-object-state", "reposition-view" })
            {
                var wire = Wire(intent);
                if (origin == "actor_draft") { wire["actorDraftId"] = source.Id; wire["actorDraftRevision"] = source.Revision; }
                if (intent == "set-object-state") wire["objectState"] = "on";
                if (intent == "reposition-view") wire["viewPosition"] = new JObject { ["direction"] = "closer", ["distance"] = .3, ["purpose"] = "read-detail" };
                Check(Fits(schema, wire) && Fits(compatible, wire) && RoomTaskProtocol.TryDecision(wire.ToString(), source.Text, false, source, out _, out _),
                    origin + " retains the same bound entity in " + intent + " on the compatible wire and parser.");
                wire["entityTarget"]["entityId"] = Corrupt;
                Check(!Fits(schema, wire) && !Fits(compatible, wire) && !RoomTaskProtocol.TryDecision(wire.ToString(), source.Text, false, source, out _, out string failure) && failure == "plan-target-identity-changed",
                    "The actual c33 mixed lamp/plan UUID cannot be generated or accepted; it is not silently repaired.");
                wire["entityTarget"]["entityId"] = Lamp; wire["targetSelection"] = "reselect";
                Check(!Fits(schema, wire) && !Fits(compatible, wire), "Bound plan never advertises target reselection.");
                wire.Remove("targetSelection"); wire.Remove("entityTarget");
                wire["visualTarget"] = new JObject { ["captureId"] = "current-image", ["x"] = .5, ["y"] = .5, ["label"] = "lamp" };
                Check(!Fits(schema, wire) && !Fits(compatible, wire), "Bound object action cannot fall back to arbitrary pixels.");
            }
            source.Coverage = "atomic"; source.PlanResultCount = 1; source.PlanOnlyObservedBoundEntity = true;
            schema = JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null, source));
            Check((string)schema["properties"]["planCompletion"]["properties"]["entityId"]["enum"][0] == Lamp,
                "Observation-first completion declaration uses the same bound entity enum.");
            source.BoundEntityId = null;
            schema = JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null, source));
            Check(schema["properties"]["entityTarget"]["properties"]["entityId"]["enum"] == null &&
                ((JArray)schema["oneOf"]).Any(b => b["properties"]?["visualTarget"] != null &&
                    ((JArray)b["properties"]["intent"]["enum"]).Values<string>().Contains("set-object-state")),
                "An unbound adopted plan still selects a currently observed target normally.");
        }
        var plain = JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null));
        Check(plain["properties"]["entityTarget"]["properties"]["entityId"]["enum"] == null,
            "Ordinary non-plan requests do not inherit another plan's identity.");
        return checks;
    }

    public static int RunChecks()
    {
        RunPureChecks();
#if UNITY_EDITOR
        foreach (string origin in new[] { "user", "actor_draft" })
        using (var f = new Fixture(origin))
        {
            var step = f.Step(); string before = f.Plan.Facts().ToString();
            Check(f.Prerequisite(step, out var fact) && (string)fact["entityId"] == f.Identity.EntityId &&
                (string)fact["requiredNextStep"] == "approach-object" && (string)fact["captureId"] == f.Observation.CaptureId,
                origin + " pixel-bound known device receives the actual outside-distance prerequisite.");
            Check((float)fact["distanceMeters"] == 5 && (float)fact["maximumDistanceMeters"] == 2.5f &&
                (bool)fact["withinDistance"] == false && (string)fact["targetResolutionSource"] == "current-image-pixel-to-known-entity",
                "Distance comes from the current same-root capability and actual executor limit.");
            Check(f.Plan.Facts().ToString() == before && step.entityTarget == null && step.visualTarget != null &&
                !(bool)Get(f.Chat, "m_SceneActionActive") && f.Host.transform.position == Vector3.zero && f.Device.ReadState().on,
                "Prerequisite lookup does not bind/rewrite the plan, navigate, submit an action or switch power.");
            step.entityTarget = new RoomTaskProtocol.EntityTarget { captureId = f.Observation.CaptureId, entityId = Corrupt };
            Check(!f.Prerequisite(step, out _), "An arbitrary wrong entity ID is not repaired from accompanying pixels.");
            step.entityTarget = null; step.visualTarget.captureId = "old-image";
            Check(!f.Prerequisite(step, out _), "Pixel coordinates cannot cross capture identities.");
            step.visualTarget.captureId = f.Observation.CaptureId; step.planRevision++;
            Check(!f.Prerequisite(step, out _), "A stale plan revision cannot gain a prerequisite.");
            step.planRevision--; step.planId = "other-plan";
            Check(!f.Prerequisite(step, out _), "A foreign plan cannot gain a prerequisite.");
        }
        foreach (string mutation in new[] { "near", "unknown", "disabled", "cancelled", "new-generation", "user-speaking", "stale", "moved-geometry", "opaque-missing", "capability-invalid" })
        using (var f = new Fixture("user", mutation == "near" ? 2f : 5f))
        {
            if (mutation == "unknown") ((IDictionary)Get(f.Chat, "m_KnownSceneObjects")).Clear();
            if (mutation == "disabled") Set(f.Chat, "m_EnableSceneObjectAwareness", false);
            if (mutation == "cancelled") f.Plan.Cancel("cancel-test");
            if (mutation == "new-generation") Set(f.Chat, "m_FormalResponseGeneration", 6);
            if (mutation == "user-speaking") Set(f.Chat, "m_UserSpeechActiveForAutonomy", true);
            if (mutation == "stale") Set(f.Observation, "<CapturedAtRealtime>k__BackingField", Time.realtimeSinceStartup - 60f);
            if (mutation == "moved-geometry") f.Identity.transform.position += Vector3.right;
            if (mutation == "opaque-missing") for (int i = 1; i < f.Depth.Length; i += 2) f.Depth[i] = -4097;
            if (mutation == "capability-invalid") f.Device.DeviceLight.intensity += 1;
            Check(!f.Prerequisite(f.Step(), out _), "Current pixel prerequisite preserves gate: " + mutation);
        }
#endif
        return checks;
    }

#if UNITY_EDITOR
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed class Fixture : IDisposable
    {
        public readonly GameObject Host;
        public readonly ChatSample Chat;
        public readonly ActorPlanRuntime Plan;
        public readonly SceneObjectIdentity Identity;
        public readonly SceneObjectLightSwitch Device;
        public readonly SceneVisionObservation Observation;
        public readonly float[] Depth = new float[50];
        private readonly Material material;
        public Fixture(string origin, float distance = 5)
        {
            // Use the isolated runner's loaded scene. HideAndDontSave detaches
            // objects from normal scene membership; do not save or replace it.
            var fixtureScene = SceneManager.GetActiveScene();
            if (!fixtureScene.IsValid() || !fixtureScene.isLoaded)
                throw new InvalidOperationException("The isolated fixture requires an already loaded scene.");
            Host = new GameObject("controlled pixel prerequisite fixture");
            SceneManager.MoveGameObjectToScene(Host, fixtureScene);
            Chat = Host.AddComponent<ChatSample>(); Set(Chat, "m_ChatSettings", new ChatSetting());
            Set(Chat, "m_EnableScreenVision", true); Set(Chat, "m_AgentEyesOpen", true); Set(Chat, "m_SceneTimePaused", true);
            Set(Chat, "m_SceneVisionApplicationPaused", false); Set(Chat, "m_FormalResponseGeneration", 5);
            var runtime = new FastLoopRuntime(); Set(Chat, "m_FastLoop", runtime);
            var turnType = typeof(ChatSample).GetNestedType("FastLoopTurnContext", BindingFlags.NonPublic);
            var turn = Activator.CreateInstance(turnType, F, null, new object[] { null, "把灯打开。", null, null, 5,
                (int)Get(Chat, "m_WorkEpoch"), (int)Get(Chat, "m_CurrentSpeechRoundId"), runtime.OutputRevision, true }, null);
            Set(Chat, "m_FastLoopTurn", turn); Set(Chat, "m_ActorPlanTurn", turn);
            Plan = new ActorPlanRuntime("把灯打开。", origin); Set(Chat, "m_ActorPlan", Plan);
            Set(Chat, "m_ActorPlanRevocation", Get(Chat, "m_RoomActorProposalRevocation"));
            var bridge = Host.AddComponent<ArdyRoomDialogueBridge>(); bridge.connectOnStart = false; bridge.chat = Chat;
            bridge.avatar = Host.AddComponent<UniVRM10.Vrm10Instance>(); bridge.avatar.enabled = false;
            var eyes = Host.AddComponent<NevaEyeTracking>();
            Set(eyes, "avatar", bridge.avatar); Set(eyes, "calibrated", true); Set(eyes, "headFrameLocalRotation", Quaternion.identity);
            foreach (string bone in new[] { "head", "leftEye", "rightEye" }) Set(eyes, bone, Host.transform);
            var capture = Host.AddComponent<NevaSceneVisionCapture>();
            Set(capture, "avatar", bridge.avatar); Set(capture, "eyeTracking", eyes); Set(capture, "<IsObserving>k__BackingField", true);
            Set(Chat, "m_SceneVisionCapture", capture);
            var environment = new GameObject("controlled environment"); environment.transform.SetParent(Host.transform, false);
            bridge.environmentRoot = environment.transform; Set(Chat, "m_SceneObjectEnvironment", environment.transform);
            var lamp = new GameObject("controlled registered lamp"); lamp.transform.SetParent(environment.transform, false);
            lamp.transform.position = new Vector3(0, 0, distance);
            Identity = lamp.AddComponent<SceneObjectIdentity>(); Identity.Configure(SceneObjectLampCatalog.Label, SceneObjectLampCatalog.AuthoredKey);
            var surface = GameObject.CreatePrimitive(PrimitiveType.Cube); surface.transform.SetParent(lamp.transform, false);
            var renderer = surface.GetComponent<Renderer>(); var mesh = surface.GetComponent<MeshFilter>().sharedMesh;
            material = new Material(Shader.Find("Standard")); material.SetColor("_EmissionColor", Color.white); material.EnableKeyword("_EMISSION"); renderer.sharedMaterial = material;
            var light = new GameObject("controlled light").AddComponent<Light>(); light.transform.SetParent(lamp.transform, false);
            // Seed controlled registered capability state, then exercise the real
            // validation/read gate. This does not call the Play-only registration.
            Device = lamp.AddComponent<SceneObjectLightSwitch>();
            Set(Device, "emissionRenderer", renderer); Set(Device, "deviceLight", light); Set(Device, "emissionMesh", mesh);
            Set(Device, "ownedMaterial", material); Set(Device, "expectedEmissionColor", Color.white); Set(Device, "expectedEmissionKeyword", true);
            Set(Device, "expectedLightEnabled", light.enabled); Set(Device, "sceneHandle", Host.scene.handle);
            Set(Device, "boundEntityId", Identity.EntityId); Set(Device, "bound", true);
            Set(Device, "expectedMaterialState", Call(typeof(SceneObjectLightSwitch), "MaterialState", material));
            Set(Device, "expectedLightConfiguration", Call(typeof(SceneObjectLightSwitch), "LightConfiguration", light));
            Check(Device.TryValidate(out var error), "Controlled capability must satisfy real ownership validation: " + error +
                " loaded=" + Device.gameObject.scene.isLoaded + " scene=" + Device.gameObject.scene.handle + "/" + Host.scene.handle +
                " active=" + Device.isActiveAndEnabled + " identity=" + Identity.isActiveAndEnabled);
            var entryType = typeof(SceneVisionGrounding).GetNestedType("Entry", BindingFlags.NonPublic);
            var entry = Activator.CreateInstance(entryType, true);
            void E(string field, object value) => Set(entry, field, value);
            E("renderer", renderer); E("instanceId", renderer.GetInstanceID()); E("layer", renderer.gameObject.layer);
            E("matrix", renderer.localToWorldMatrix); E("transformMatrix", renderer.transform.localToWorldMatrix); E("bounds", renderer.bounds);
            E("mesh", mesh); E("vertices", mesh.vertexCount); E("submeshes", mesh.subMeshCount); E("meshBounds", mesh.bounds);
            E("materials", renderer.sharedMaterials); E("observedRigid", true); E("stationary", true); E("identity", Identity);
            E("identityRoot", Identity.Root); E("entityId", Identity.EntityId); E("sceneLabel", Identity.SceneLabel);
            E("identitySource", Identity.IdentitySource); E("authoredKey", Identity.AuthoredKey);
            E("observedSignatures", new[] { (string)Call(typeof(SceneVisionGrounding), "ObservedSignature", material) });
            var entries = Array.CreateInstance(entryType, 1); entries.SetValue(entry, 0);
            var frame = Activator.CreateInstance(typeof(SceneVisionGrounding).GetNestedType("Frame", BindingFlags.NonPublic), true);
            Set(frame, "entries", entries); Set(frame, "width", 5); Set(frame, "height", 5); Set(frame, "rowsStartAtTop", true);
            for (int i = 0; i < Depth.Length; i += 2) { Depth[i] = distance; Depth[i + 1] = 1; } Set(frame, "depthIds", Depth);
            Observation = (SceneVisionObservation)typeof(SceneVisionObservation).GetConstructors(F).Single().Invoke(new object[] {
                (string)Get(capture, "binding") + "-controlled", "ready", "", DateTime.UtcNow.ToString("o"), Time.realtimeSinceStartup, 1,
                new Pose(Vector3.zero, Quaternion.identity), 60f, 5, 5, "data:image/png;base64,CONTROLLED_NOT_RENDERED",
                Host.scene.handle, Host.scene.name, Host.scene.path, Call(typeof(NevaSceneVisionCapture), "CurrentLoadedSceneFingerprint"), frame });
            Check(capture.TryResolveObservedObject(Observation, new Vector2(.5f, .5f), out var seen, out error) && seen.VisibleConfirmed,
                "Production same-frame pixel resolver must confirm the controlled opaque geometry: " + error);
            Call(Chat, "RememberSceneObject", seen, Device);
        }
        public RoomTaskProtocol.Decision Step() => new RoomTaskProtocol.Decision { intent = "set-object-state", origin = Plan.SourceOrigin,
            objectState = "on", planId = Plan.Id, planRevision = Plan.Revision, planRemaining = "",
            visualTarget = new RoomTaskProtocol.VisualTarget { captureId = Observation.CaptureId, x = .5f, y = .5f, label = "same lamp" } };
        public bool Prerequisite(RoomTaskProtocol.Decision step, out JObject fact)
        {
            object[] args = { step, Observation, 5, null }; bool result = (bool)Call(Chat, "TryGetRoomObjectPrerequisite", args);
            fact = args[3] as JObject; return result;
        }
        public void Dispose()
        {
            Set(Device, "bound", false); UnityEngine.Object.DestroyImmediate(Host);
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
        }
    }
    private static object Get(object obj, string field) => obj.GetType().GetField(field, F).GetValue(obj);
    private static void Set(object obj, string field, object value) => obj.GetType().GetField(field, F).SetValue(obj, value);
    private static object Call(object obj, string method, params object[] args)
    {
        Type type = obj as Type ?? obj.GetType();
        try { return type.GetMethods(F).Single(m => m.Name == method && m.GetParameters().Length == args.Length).Invoke(obj is Type ? null : obj, args); }
        catch (TargetInvocationException e) { throw e.InnerException ?? e; }
    }
#endif
}
