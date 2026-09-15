using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Previously seen object references through real Chat execution and fresh room camera images.
/// Controlled decisions isolate referent binding from model pixel accuracy; no actor teleport or model HTTP.</summary>
public static class SceneObjectReferenceRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static string directory;
    private static GameObject substitute;
    private static Material substituteMaterial;
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Object references require the existing isolated real-room Play fixture.");
        checks = 0; directory = Path.Combine(output, "scene-object-references"); Directory.CreateDirectory(directory);
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(), ["modelHttpRequests"] = 0, ["nativeRequests"] = 0,
            ["scope"] = "Real room lamp and calibrated RGB/depth-ID images through production Chat object dispatch. Controlled current-image references exercise off, an intervening ordinary-object selection, and on of the same known entity. Freshness, history, occlusion and same-label identity isolation are enforced. No model semantic reliability, physical hand press or scene-wide darkness claim." };
        bool originalAwareness = (bool)Get(chat, "m_EnableSceneObjectAwareness");
        SceneObjectLightSwitch lamp = null;
        var stack = new Stack<IEnumerator>();
        try
        {
            Set(chat, "m_EnableSceneObjectAwareness", true);
            Check(bridge != null && bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing, "Real room is bound and body is idle.");
            Check(SceneObjectLampCatalog.EnsureRegistered(bridge.environmentRoot, out lamp, out string reason), "Real room lamp registration: " + reason);
            Check(lamp.TryValidate(out reason), "Same actual lamp has valid runtime capability: " + reason);
            LastReport["lampEntityId"] = lamp.EntityId;
            LastReport["originalState"] = JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));
            stack.Push(Exercise(chat, bridge, lamp));
            while (stack.Count > 0)
            {
                object yielded = null;
                try
                {
                    while (stack.Count > 0)
                    {
                        var routine = stack.Peek();
                        if (!routine.MoveNext()) { (routine as IDisposable)?.Dispose(); stack.Pop(); continue; }
                        if (routine.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        yielded = routine.Current; break;
                    }
                }
                catch (Exception error) { LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw; }
                if (stack.Count > 0) yield return yielded;
            }
            LastReport["status"] = "passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            Call(chat, "CancelSceneAction", "object-reference-fixture-finished"); Call(chat, "ClearScheduledAgentWake");
            if (substitute != null) UnityEngine.Object.DestroyImmediate(substitute);
            if (substituteMaterial != null) UnityEngine.Object.DestroyImmediate(substituteMaterial);
            substitute = null; substituteMaterial = null;
            if (lamp != null)
            {
                bool restored = lamp.RestoreOriginal(out string restoreReason);
                LastReport["restoredOwnedLampState"] = restored; LastReport["restoreReason"] = restoreReason;
                if (!restored) LastReport["status"] = "failed";
            }
            Set(chat, "m_EnableSceneObjectAwareness", originalAwareness);
            if ((string)LastReport["status"] == "running") LastReport["status"] = "failed";
            LastReport["checks"] = checks; WriteReport();
        }
        if ((string)LastReport["status"] != "passed") throw new InvalidOperationException("Object reference fixture or lamp restoration failed.");
    }

    private static IEnumerator Exercise(ChatSample chat, ArdyRoomDialogueBridge bridge, SceneObjectLightSwitch lamp)
    {
        Vector3 root = bridge.avatar.transform.position;
        int operation = bridge.Controller.OperationRevision;
        SceneVisionObservation source = null;
        yield return Prepare(chat);
        yield return Capture(chat, "remember-original-lamp", x => source = x);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out var lampPixel, out string reason), "Real lamp is initially visible: " + reason);
        yield return Dispatch(chat, source, PixelWire("inspect-object", source, lampPixel.ImagePoint), "initial-pixel-inspection");
        Check((string)Fact(chat)["phase"] == "observed" && (string)Fact(chat)["entityId"] == lamp.EntityId, "Initial pixel inspection establishes the real lamp referent.");
        Check(HasHistory(chat, lamp.EntityId), "Known history contains the explicitly observed lamp.");

        // Establish an actual off state through the existing pixel route, as in the user's session.
        if (!lamp.ReadState().on)
        {
            yield return Prepare(chat); yield return Capture(chat, "initial-on-source", x => source = x);
            Check(chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out lampPixel, out reason), "Initial on uses real lamp pixels: " + reason);
            yield return Dispatch(chat, source, PixelWire("set-object-state", source, lampPixel.ImagePoint, "on"), "prepare-on");
            Check(lamp.ReadState().on && (string)Fact(chat)["phase"] == "performed", "Lamp is actually on before the off/on reproduction.");
        }
        yield return Prepare(chat); yield return Capture(chat, "turn-off-source", x => source = x);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out lampPixel, out reason), "Off operation starts at current lamp pixels: " + reason);
        yield return Dispatch(chat, source, PixelWire("set-object-state", source, lampPixel.ImagePoint, "off"), "turn-off-by-pixel");
        Check(!lamp.ReadState().on && (string)Fact(chat)["phase"] == "performed" && (bool?)Fact(chat)["stateChanged"] == true,
            "The original lamp genuinely changes to off before re-selection.");
        string offCapture = (string)Fact(chat)["resultCaptureId"];

        yield return Prepare(chat); yield return Capture(chat, "ordinary-object-source", x => source = x);
        var ordinary = FindOrdinary(chat.SceneVisionCapture, source, lamp.EntityId);
        Check(ordinary != null, "Another real opaque room surface can be selected while lamp is off.");
        yield return Dispatch(chat, source, PixelWire("inspect-object", source, ordinary.ImagePoint), "select-other-real-object");
        Check((string)ObjectContext(chat)["selected"]?["entityId"] == ordinary.EntityId && ordinary.EntityId != lamp.EntityId,
            "Latest selection truly changes to an ordinary renderer, reproducing referent pressure.");
        Check(HasHistory(chat, lamp.EntityId) && !lamp.ReadState().on,
            "Selecting another surface preserves the lamp's known identity and leaves it off.");
        Check((bool?)ObjectContext(chat)["lastDeviceStateEvidenceValid"] == true &&
            (string)ObjectContext(chat)["stateResult"]?["entityId"] == lamp.EntityId,
            "An ordinary renderer selection does not invalidate the unchanged original lamp's actual off receipt.");
        CheckResultOwnerBoundary(chat, bridge);

        yield return Prepare(chat); yield return Capture(chat, "known-lamp-reopen-source", x => source = x);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out lampPixel, out reason), "Dark lamp has verified opaque pixels in this new image: " + reason);
        var visible = VisibleKnown(chat, source);
        var anchor = visible.OfType<JObject>().SingleOrDefault(value => (string)value["entityId"] == lamp.EntityId);
        Check(anchor != null && (string)anchor["captureId"] == source.CaptureId && anchor["imagePoint"] is JObject,
            "The current-frame known-object anchor preserves the lamp identity even after another object was selected.");
        Check(chat.SceneVisionCapture.TryResolveObservedObject(source,
            new Vector2((float)anchor["imagePoint"]["x"], (float)anchor["imagePoint"]["y"]), out var anchored, out reason) &&
            anchored.VisibleConfirmed && anchored.EntityId == lamp.EntityId,
            "The offered image anchor is a real opaque pixel of that same lamp: " + reason);
        Check(anchor["runtimeDeviceStateReadAfterObservation"] == null && anchor["actual"] == null && anchor["capabilities"] == null,
            "Reference anchors add neither a new device-state read nor a capability grant.");
        var decisionInput = JObject.Parse((string)Call(chat, "BuildRoomDecisionInputWithObservation", "把刚才那盏灯再打开。", false, "", bridge, source));
        File.WriteAllText(Path.Combine(directory, "reopen-room-input.json"), decisionInput.ToString());
        // The in-memory anchors contain Single values while wire JSON parses as Double. Compare
        // the exact JSON representation at both boundaries, not Single versus parsed-double bits.
        var expectedWireAnchors = JArray.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(visible));
        LastReport["knownAnchorWire"] = new JObject { ["expected"] = expectedWireAnchors,
            ["actual"] = decisionInput["visibleKnownObjects"]?.DeepClone() };
        Check(JToken.DeepEquals(decisionInput["visibleKnownObjects"], expectedWireAnchors),
            "Actual room-decision input contains the same current-frame anchors after JSON numeric serialization.");
        yield return Dispatch(chat, source, ReferenceWire("set-object-state", source.CaptureId, lamp.EntityId, "on"), "reopen-known-lamp-after-other-selection");
        var reopened = Fact(chat);
        Check(lamp.ReadState().on && lamp.ReadState().lightEnabled && lamp.ReadState().emissionEnabled &&
            (string)reopened["phase"] == "performed" && (bool?)reopened["stateChanged"] == true && (string)reopened["entityId"] == lamp.EntityId,
            "Explicit known-entity reference reopens the exact original lamp after background selection.");
        Check((string)reopened["requestedEntityId"] == lamp.EntityId && !string.IsNullOrEmpty((string)reopened["targetResolutionSource"]),
            "Execution records the requested entity and actual target resolution source.");
        Check((string)reopened["resultCaptureId"] != source.CaptureId && (string)reopened["resultCaptureId"] != offCapture &&
            chat.SceneVisionCapture.LatestObservation.CaptureId == (string)reopened["resultCaptureId"],
            "Reopening is confirmed with a real new image of the same entity.");

        // Same request with old/unknown references must not mutate an otherwise operable, visible lamp.
        foreach (var entry in new[] {
            new[] { "old-capture-reference", offCapture, lamp.EntityId },
            new[] { "unknown-zero-identity", "current", "scene-object-00000000000000000000000000000000" },
            new[] { "unknown-fresh-identity", "current", "scene-object-" + Guid.NewGuid().ToString("N") } })
        {
            yield return Prepare(chat); yield return Capture(chat, entry[0] + "-source", x => source = x);
            string captureId = entry[1] == "current" ? source.CaptureId : entry[1];
            yield return Dispatch(chat, source, ReferenceWire("set-object-state", captureId, entry[2], "off"), entry[0]);
            Check((string)Fact(chat)["phase"] == "rejected" && lamp.ReadState().on && (bool?)Fact(chat)["stateChanged"] != true,
                "Old capture or unknown identity cannot change real lamp power: " + entry[0]);
            Check(((string)Fact(chat)["reason"] ?? "").Contains(entry[0] == "old-capture-reference" ?
                "visual-reference-not-current" : "entity-reference-not-known-in-current-room"),
                "Reference refusal reaches the intended identity/freshness gate: " + entry[0]);
        }

        // A different opaque object explicitly authored with the same label/key is still a different entity.
        // Its broad foreground geometry physically occludes the actual lamp; the actor and lamp never move.
        yield return Prepare(chat);
        Check(chat.SceneVisionCapture.TryGetCurrentCameraPose(out Pose pose), "Current actual camera pose supplies the isolated foreground fixture.");
        substitute = GameObject.CreatePrimitive(PrimitiveType.Quad); substitute.name = lamp.Root.name;
        substitute.transform.SetParent(bridge.environmentRoot, false);
        substitute.transform.SetPositionAndRotation(pose.position + pose.rotation * new Vector3(0, 0, .42f), pose.rotation);
        substitute.transform.localScale = new Vector3(1.5f / bridge.environmentRoot.lossyScale.x, 1.5f / bridge.environmentRoot.lossyScale.y,
            1f / bridge.environmentRoot.lossyScale.z);
        UnityEngine.Object.DestroyImmediate(substitute.GetComponent<Collider>());
        substituteMaterial = new Material(Shader.Find("Unlit/Color")) { color = new Color(.4f, .4f, .4f, 1f) };
        substitute.GetComponent<Renderer>().sharedMaterial = substituteMaterial;
        var substituteIdentity = substitute.AddComponent<SceneObjectIdentity>();
        substituteIdentity.Configure(lamp.Identity.SceneLabel, lamp.Identity.AuthoredKey);
        Check(substituteIdentity.EntityId != lamp.EntityId && substituteIdentity.SceneLabel == lamp.Identity.SceneLabel,
            "Same authored name/label/key never duplicate an existing runtime identity.");
        yield return Capture(chat, "same-label-foreground-source", x => source = x);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source, substituteIdentity.EntityId, out var replacement, out reason) && replacement.VisibleConfirmed,
            "The new same-label substitute really is visible in front of the original lamp: " + reason);
        Check(!chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out _, out _) && lamp.TryValidate(out _),
            "The actual lamp is fully occluded although its original binding/capability remains valid.");
        Check(!VisibleKnown(chat, source).OfType<JObject>().Any(value => (string)value["entityId"] == lamp.EntityId ||
            (string)value["entityId"] == substituteIdentity.EntityId),
            "A hidden known lamp and an unobserved visible substitute are both absent from current known-object anchors.");
        yield return Dispatch(chat, source, ReferenceWire("set-object-state", source.CaptureId, substituteIdentity.EntityId, "off"), "unobserved-same-label-new-identity");
        Check((string)Fact(chat)["phase"] == "rejected" && lamp.ReadState().on,
            "A visible same-label new identity without prior observation cannot borrow the old lamp's reference or capability.");
        Check(((string)Fact(chat)["reason"] ?? "").Contains("entity-reference-not-known-in-current-room"),
            "The unobserved substitute fails the known-identity requirement even though its geometry is visible.");

        yield return Prepare(chat); yield return Capture(chat, "hidden-known-lamp-source", x => source = x);
        yield return Dispatch(chat, source, ReferenceWire("set-object-state", source.CaptureId, lamp.EntityId, "off"), "hidden-known-lamp");
        Check((string)Fact(chat)["phase"] == "rejected" && lamp.ReadState().on && (bool?)Fact(chat)["objectConfirmed"] != true,
            "Previously known lamp cannot be operated through a current opaque occluder.");
        Check(((string)Fact(chat)["reason"] ?? "").Contains("referenced-entity-not-visible-in-current-capture"),
            "The hidden original reaches the actual current-image visibility gate.");

        yield return Prepare(chat); yield return Capture(chat, "observe-same-label-substitute-source", x => source = x);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source, substituteIdentity.EntityId, out replacement, out reason), "Same-label substitute remains visible: " + reason);
        yield return Dispatch(chat, source, PixelWire("inspect-object", source, replacement.ImagePoint), "observe-same-label-substitute");
        Check((string)Fact(chat)["phase"] == "observed" && (string)Fact(chat)["entityId"] == substituteIdentity.EntityId,
            "Explicit inspection can establish the new substitute as its own separate identity.");
        yield return Prepare(chat); yield return Capture(chat, "known-substitute-no-capability-source", x => source = x);
        yield return Dispatch(chat, source, ReferenceWire("set-object-state", source.CaptureId, substituteIdentity.EntityId, "off"), "known-same-label-substitute-has-no-capability");
        Check((string)Fact(chat)["phase"] == "rejected" && lamp.ReadState().on &&
            ((string)Fact(chat)["reason"] ?? "").Contains("no-registered-power-capability"),
            "Even an observed same-label substitute cannot inherit another entity's power capability.");
        Check(bridge.Controller.OperationRevision == operation && Vector3.Distance(root, bridge.avatar.transform.position) < .0001f,
            "Reference tests never translate the actor or acquire native locomotion.");
    }

    private static JObject PixelWire(string intent, SceneVisionObservation source, Vector2 pixel, string state = null)
    {
        var value = new JObject { ["intent"] = intent, ["origin"] = "user", ["evidence"] = Command(state),
            ["visualTarget"] = new JObject { ["captureId"] = source.CaptureId, ["x"] = pixel.x, ["y"] = pixel.y, ["label"] = "当前明确选择的物件" } };
        if (state != null) value["objectState"] = state; return value;
    }
    private static JObject ReferenceWire(string intent, string capture, string entity, string state = null)
    {
        var value = new JObject { ["intent"] = intent, ["origin"] = "user", ["evidence"] = Command(state),
            ["entityTarget"] = new JObject { ["captureId"] = capture, ["entityId"] = entity } };
        if (state != null) value["objectState"] = state; return value;
    }
    private static string Command(string state) => state == null ? "请观察这个物件。" : state == "on" ? "把刚才那盏灯再打开。" : "请关闭这个物件的灯。";
    private static IEnumerator Prepare(ChatSample chat)
    {
        Call(chat, "CancelSceneAction", "object-reference-fixture-next");
        yield return Wait(() => IdleHead(chat), 5, "Previous real head ownership returns");
        typeof(SceneActionIntegrationRegression).GetMethod("ResetRound", Flags).Invoke(null, new object[] { chat });
        Set(chat, "m_EnableSceneObjectAwareness", true);
    }
    private static IEnumerator Capture(ChatSample chat, string purpose, Action<SceneVisionObservation> completed)
    {
        SceneVisionObservation value = null;
        Call(chat, "RequestSceneObservation", purpose, new Action<SceneVisionObservation>(x => value = x));
        yield return Wait(() => value != null, 5, "Actual reference-source GPU capture");
        Check(value.HasImage && value.HasSurfaceGrounding, "Reference source has current actual RGB and companion geometry.");
        Call(chat, "BindFormalSceneObservation", value, (int)Get(chat, "m_FormalResponseGeneration"));
        File.WriteAllBytes(Path.Combine(directory, purpose + ".jpg"), Convert.FromBase64String(value.ImageDataUrl.Substring(value.ImageDataUrl.IndexOf(',') + 1)));
        File.WriteAllText(Path.Combine(directory, purpose + ".metadata.json"), value.MetadataJson);
        completed(value);
    }
    private static IEnumerator Dispatch(ChatSample chat, SceneVisionObservation source, JObject wire, string name)
    {
        string command = (string)wire["evidence"]; Call(chat, "RecordAcceptedMotionUserTurn", command);
        Check(RoomTaskProtocol.TryDecision(wire.ToString(), command, false, out var decision), "Reference case passes production parser: " + name);
        Set(chat, "m_FormalResponseInFlight", true); Set(chat, "m_AgentRoundInFlight", true);
        int generation = (int)Get(chat, "m_FormalResponseGeneration"); bool done = false;
        Call(chat, "BeginSceneAction", decision, source, generation, new Action(() => done = true));
        yield return Wait(() => done, 22, "Production reference action: " + name);
        Call(chat, "ClearScheduledAgentWake"); Set(chat, "m_FormalResponseInFlight", false); Set(chat, "m_AgentRoundInFlight", false);
        ((JArray)LastReport["cases"]).Add(new JObject { ["name"] = name, ["decision"] = wire,
            ["sourceCaptureId"] = source.CaptureId, ["fact"] = Fact(chat), ["objectContext"] = ObjectContext(chat) });
        WriteReport();
    }
    private static SceneObservedObject FindOrdinary(NevaSceneVisionCapture capture, SceneVisionObservation observation, string lampId)
    {
        for (int y = 25; y <= 70; y += 5) for (int x = 20; x <= 80; x += 5)
            if (capture.TryResolveObservedObject(observation, new Vector2(x / 100f, y / 100f), out var candidate, out _) &&
                candidate.VisibleConfirmed && candidate.EntityId != lampId && candidate.Root.GetComponent<SceneObjectLightSwitch>() == null) return candidate;
        return null;
    }
    private static bool HasHistory(ChatSample chat, string entityId) =>
        (ObjectContext(chat)["knownObjectHistory"] as JArray)?.OfType<JObject>().Any(value => (string)value["entityId"] == entityId) == true;
    private static JArray VisibleKnown(ChatSample chat, SceneVisionObservation source) => (JArray)Call(chat, "BuildVisibleKnownObjects", source);
    private static void CheckResultOwnerBoundary(ChatSample chat, ArdyRoomDialogueBridge bridge)
    {
        var originalAvatar = bridge.avatar;
        var before = (JObject)Call(chat, "BuildSceneObjectFacts");
        var actualResult = before["stateResult"]?.DeepClone();
        Check(originalAvatar != null && actualResult is JObject && (bool?)before["lastDeviceStateEvidenceValid"] == true,
            "Owner-boundary fixture begins with an actual unchanged result for the bound avatar.");
        JObject mismatched = null;
        try
        {
            // Synchronous binding-only negative: no frame, motion, capture or scene transform is changed.
            bridge.avatar = null;
            mismatched = (JObject)Call(chat, "BuildSceneObjectFacts");
            Check((bool?)mismatched["lastDeviceStateEvidenceValid"] == false,
                "An absent result-owner avatar invalidates current device evidence even when its old device still exists.");
            Check(JToken.DeepEquals(mismatched["stateResult"], actualResult),
                "Owner mismatch preserves the actual historical state receipt without presenting it as current valid evidence.");
        }
        finally { bridge.avatar = originalAvatar; }
        var restored = (JObject)Call(chat, "BuildSceneObjectFacts");
        Check((bool?)restored["lastDeviceStateEvidenceValid"] == true && JToken.DeepEquals(restored["stateResult"], actualResult),
            "Restoring the exact owner revalidates the unchanged actual result without rerunning an operation.");
        LastReport["resultOwnerBoundary"] = new JObject {
            ["scope"] = "Only bridge.avatar is temporarily nulled and restored synchronously; reads use production BuildSceneObjectFacts. No yielded frame, actor/device transform change or new device action.",
            ["mismatched"] = mismatched, ["restored"] = restored
        };
    }
    private static JObject ObjectContext(ChatSample chat) => (JObject)JObject.Parse(chat.SceneActionContextJson)["objectContext"];
    private static JObject Fact(ChatSample chat) => JObject.Parse(JsonUtility.ToJson(Get(chat, "m_SceneActionFact")));
    private static IEnumerator Wait(Func<bool> ready, float seconds, string label)
    { float until = Time.realtimeSinceStartup + seconds; while (!ready()) { if (Time.realtimeSinceStartup > until) throw new TimeoutException(label); yield return null; } }
    private static bool IdleHead(ChatSample chat) { var attention = (NevaSceneAttention)Get(chat, "m_SceneAttention"); return attention == null || !attention.IsBusy; }
    private static void Check(bool valid, string reason) { checks++; if (!valid) throw new InvalidOperationException(reason); }
    private static void WriteReport() => File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString() + "\n");
    private static object Get(object target, string field) => target.GetType().GetField(field, Flags).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Flags).SetValue(target, value);
    private static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Flags).Invoke(target, args);
}
