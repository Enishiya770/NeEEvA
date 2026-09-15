using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Explicitly enabled object suite in the existing isolated real-room fixture. Intent inputs are controlled,
/// while RGB, selected local entity, device state and Chat execution are production. No model HTTP or hand-press claim.</summary>
public static class SceneObjectIntegrationRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static string directory;
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Object integration requires the existing isolated real-room Play fixture.");
        checks = 0; directory = Path.Combine(output, "scene-objects"); Directory.CreateDirectory(directory);
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(),
            ["scope"] = "Controlled explicit object intents through actual Chat scene-action dispatch using the real imported room, calibrated VRM camera, authored lamp and fresh GPU images. No Qwen semantic/autonomy, physical switch press, hand animation or native locomotion claim. The original strict-pixel baseline runs separately with awareness disabled.",
            ["modelHttpRequests"] = 0, ["nativeRequests"] = 0 };
        bool originalAwareness = (bool)Get(chat, "m_EnableSceneObjectAwareness");
        SceneObjectLightSwitch lamp = null;
        var stack = new Stack<IEnumerator>();
        try
        {
            Set(chat, "m_EnableSceneObjectAwareness", true);
            Check(bridge != null && bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing, "Existing room bridge is connected and idle.");
            Check(SceneObjectLampCatalog.EnsureRegistered(bridge.environmentRoot, out lamp, out string reason),
                "Only the actual authored lamp may provide device capability: " + reason);
            Check(lamp.Root.gameObject.scene == bridge.avatar.gameObject.scene && lamp.DeviceLight != null && lamp.TryValidate(out _),
                "The registered device belongs to this loaded real room and has unchanged local components.");
            LastReport["originalLampState"] = JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));
            LastReport["lampEntityId"] = lamp.EntityId;
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
            Call(chat, "CancelSceneAction", "object-integration-finished");
            Call(chat, "ClearScheduledAgentWake");
            if (lamp != null)
            {
                bool restored = lamp.RestoreOriginal(out string restoreReason);
                LastReport["restoredOwnedLampState"] = restored; LastReport["restoreReason"] = restoreReason;
                if (!restored) LastReport["status"] = "failed";
            }
            Set(chat, "m_EnableSceneObjectAwareness", originalAwareness);
            if ((string)LastReport["status"] == "running")
            {
                LastReport["status"] = "failed";
                LastReport["error"] = "The fixture did not finish; see the enclosing Unity exception.";
            }
            LastReport["checks"] = checks;
            File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString() + "\n");
        }
        if ((string)LastReport["status"] != "passed")
            throw new InvalidOperationException("Object integration or owned lamp restoration did not complete.");
    }

    private static IEnumerator Exercise(ChatSample chat, ArdyRoomDialogueBridge bridge, SceneObjectLightSwitch lamp)
    {
        int originalOperation = bridge.Controller.OperationRevision;
        Vector3 originalRoot = bridge.avatar.transform.position;
        yield return Prepare(chat);
        SceneVisionObservation source = null;
        yield return Capture(chat, "object-inspection-source", x => source = x);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out var selected, out string why) && selected.VisibleConfirmed,
            "The actual camera sees an opaque part of this exact registered lamp: " + why);
        Check(selected.Root == lamp.Root && selected.EntityId == lamp.EntityId && selected.TryValidate(out _),
            "The selected fresh pixel resolves to the authored lamp's local identity.");
        bool wasOn = lamp.ReadState().on;
        yield return Dispatch(chat, bridge, "inspect-object", source, selected.ImagePoint, null, "inspect-lamp");
        var inspect = Fact(chat);
        Check((bool?)inspect["objectConfirmed"] == true && (string)inspect["entityId"] == lamp.EntityId && (bool?)inspect["stateChanged"] != true,
            "Read-only inspection confirms this local entity without granting a mutation result.");
        Check(lamp.ReadState().on == wasOn && (bool?)inspect["reached"] != true, "Inspection neither changes light power nor claims arrival.");
        var objectFacts = JObject.Parse(chat.SceneActionContextJson)["objectContext"];
        Check((string)objectFacts?["selected"]?["entityId"] == lamp.EntityId &&
            (bool?)objectFacts["selected"]["visibleInCapturedFrame"] == true,
            "The role receives the selected entity's sourced local dossier, not a scene inventory.");
        LastReport["inspectionFact"] = inspect;

        foreach (bool wanted in new[] { !wasOn, wasOn })
        {
            yield return Prepare(chat);
            yield return Capture(chat, "object-power-source", x => source = x);
            Check(chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out selected, out why) && selected.VisibleConfirmed,
                "Each opposite power change starts from the currently visible same lamp, never an old target pixel: " + why);
            string name = wanted ? "light-on" : "light-off";
            yield return Dispatch(chat, bridge, "set-object-state", source, selected.ImagePoint, wanted ? "on" : "off", name);
            var fact = Fact(chat); var after = chat.SceneVisionCapture.LatestObservation; var state = lamp.ReadState();
            Check((string)fact["phase"] == "performed" && (bool?)fact["stateChanged"] == true && (string)fact["entityId"] == lamp.EntityId,
                "A state-change result requires a real change of the same identified capability.");
            Check(state.on == wanted && state.lightEnabled == wanted && state.emissionEnabled == wanted,
                "Both the bound runtime Light and owned visible emission actually match the requested power.");
            Check(after.HasImage && after.CaptureId != source.CaptureId && after.CaptureId == (string)fact["resultCaptureId"],
                "Performed is followed by a new actual image, not the source image or a predicted outcome.");
            Check((bool?)fact["reached"] != true, "Runtime power control never claims walking to the device or physically pressing it.");
            var stateResult = JObject.Parse(chat.SceneActionContextJson)["objectContext"]?["stateResult"];
            Check((bool?)stateResult?["freshVisualObservation"] == true && (bool?)stateResult?["physicalSwitchPress"] != true &&
                (bool?)stateResult?["changed"] == true, "Device receipt separates actual state change/new pixels from physical hand interaction.");
            SaveImage(name + "-result", after); LastReport[name + "State"] = JObject.Parse(JsonUtility.ToJson(state));
        }

        yield return Prepare(chat);
        yield return Capture(chat, "object-already-satisfied-source", x => source = x);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out selected, out why),
            "A repeated same-state request uses a fresh pixel of the exact lamp.");
        bool alreadyOn = lamp.ReadState().on;
        yield return Dispatch(chat, bridge, "set-object-state", source, selected.ImagePoint, alreadyOn ? "on" : "off", "already-satisfied");
        var unchanged = Fact(chat); var unchangedImage = chat.SceneVisionCapture.LatestObservation;
        var unchangedResult = JObject.Parse(chat.SceneActionContextJson)["objectContext"]?["stateResult"];
        Check((string)unchanged["phase"] == "already-satisfied" && (bool?)unchanged["stateChanged"] == false &&
            (bool?)unchangedResult?["changed"] == false && lamp.ReadState().on == alreadyOn,
            "A device already in the requested state is explicitly unchanged, never a newly performed switch.");
        Check((bool?)unchangedResult?["freshVisualObservation"] == true && unchangedImage.HasImage &&
            unchangedImage.CaptureId != source.CaptureId && unchangedImage.CaptureId == (string)unchanged["resultCaptureId"],
            "Already-satisfied is read back with its own new image instead of reusing a prior successful result.");

        // An old capture identity cannot authorize a current-image state mutation.
        yield return Prepare(chat);
        yield return Capture(chat, "object-stale-id-source", x => source = x);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out selected, out why), "Current same lamp remains visible for stale-ID refusal.");
        bool beforeRefusal = lamp.ReadState().on;
        yield return Dispatch(chat, bridge, "set-object-state", source, selected.ImagePoint, beforeRefusal ? "off" : "on", "stale-capture-id", "obsolete-capture-id");
        Check((string)Fact(chat)["phase"] != "performed" && lamp.ReadState().on == beforeRefusal,
            "An unmatched capture identity is rejected without changing actual lamp power.");
        Check(((string)Fact(chat)["reason"] ?? "").Contains("visual-reference-not-current"), "The stale-ID case reaches the intended capture-identity fence.");

        yield return Prepare(chat);
        yield return Capture(chat, "object-no-capability-source", x => source = x);
        SceneObservedObject ordinary = FindOrdinaryObject(chat.SceneVisionCapture, source, lamp.EntityId);
        Check(ordinary != null && ordinary.Root.GetComponent<SceneObjectLightSwitch>() == null,
            "A different real visible room surface has no registered lamp capability.");
        beforeRefusal = lamp.ReadState().on;
        yield return Dispatch(chat, bridge, "set-object-state", source, ordinary.ImagePoint, beforeRefusal ? "off" : "on", "no-capability");
        Check((string)Fact(chat)["phase"] != "performed" && lamp.ReadState().on == beforeRefusal,
            "Selecting an ordinary surface cannot operate a neighboring light.");
        Check(((string)Fact(chat)["reason"] ?? "").Contains("no-registered-power-capability"), "The ordinary-surface case reaches the intended capability fence.");

        // The same measured distance must fail when outside the declared local interaction range.
        // This policy-boundary fixture does not teleport the avatar, lamp or camera to fabricate distance.
        yield return Prepare(chat);
        yield return Capture(chat, "object-distance-source", x => source = x);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out selected, out why), "A fresh exact lamp target exists for the distance boundary.");
        FieldInfo rangeField = typeof(ChatSample).GetField("m_SceneDeviceDistance", Flags);
        Check(rangeField != null, "Production exposes its serialized object interaction distance.");
        float originalRange = (float)rangeField.GetValue(chat);
        float actualDistance = Vector3.Distance(bridge.avatar.transform.position, lamp.Root.position);
        beforeRefusal = lamp.ReadState().on;
        try
        {
            rangeField.SetValue(chat, .5f);
            Check(actualDistance > .5f, "The real lamp lies outside the explicitly tightened test interaction range.");
            yield return Dispatch(chat, bridge, "set-object-state", source, selected.ImagePoint, beforeRefusal ? "off" : "on", "outside-interaction-range");
            Check((string)Fact(chat)["phase"] != "performed" && lamp.ReadState().on == beforeRefusal,
                "An out-of-range current lamp is not operated.");
            Check(((string)Fact(chat)["reason"] ?? "").Contains("outside-nearby-operation-distance"), "The distance case reaches the actual local-device range fence.");
            LastReport["distanceBoundaryFixture"] = new JObject { ["configuredTestRangeMetres"] = .5,
                ["measuredRootToDeviceDistanceMetres"] = actualDistance, ["originalRangeMetres"] = originalRange,
                ["scope"] = "Only the policy range was temporarily tightened; no root or scene geometry was moved." };
        }
        finally { rangeField.SetValue(chat, originalRange); }

        yield return Prepare(chat);
        yield return Capture(chat, "image-region-source", x => source = x);
        Check(FindImageOnlyPixel(chat.SceneVisionCapture, source, out Vector2 region),
            "The current real room image contains a visible direction without confirmed entity or strict 3D support.");
        beforeRefusal = lamp.ReadState().on;
        yield return Dispatch(chat, bridge, "look-at", source, region, null, "image-region-only");
        var regionFact = Fact(chat); var resultImage = chat.SceneVisionCapture.LatestObservation;
        Check((string)regionFact["focusKind"] == "image-region-bearing" && (bool?)regionFact["objectConfirmed"] != true &&
            (bool?)regionFact["stateChanged"] != true && (bool?)regionFact["reached"] != true,
            "Direction-only attention does not invent an identified object, a state change or an arrival.");
        Check((string)regionFact["phase"] == "observed" && resultImage.HasImage && resultImage.CaptureId != source.CaptureId &&
            resultImage.CaptureId == (string)regionFact["resultCaptureId"], "Direction-only attention gets a new actual picture.");
        Check(Quaternion.Angle(source.CameraRotation, resultImage.CameraRotation) > 2,
            "Image-region attention changes the calibrated actual camera direction.");
        Check(lamp.ReadState().on == beforeRefusal && bridge.Controller.OperationRevision == originalOperation &&
            Vector3.Distance(originalRoot, bridge.avatar.transform.position) < .0001f,
            "Object/region tests never operate an unselected device, acquire native locomotion or translate the actor.");
        SaveImage("image-region-result", resultImage);

        yield return Prepare(chat);
        yield return Capture(chat, "object-cancel-after-state-source", x => source = x);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out selected, out why),
            "The cancellation fixture starts with the actual lamp visible in a fresh image.");
        bool beforeCancel = lamp.ReadState().on;
        yield return Dispatch(chat, bridge, "set-object-state", source, selected.ImagePoint,
            beforeCancel ? "off" : "on", "cancel-after-state-applied", cancelAfterApplied: true);
        var cancelled = Fact(chat);
        var cancelledResult = JObject.Parse(chat.SceneActionContextJson)["objectContext"]?["stateResult"];
        Check((string)cancelled["phase"] == "cancelled" && (bool?)cancelled["stateChanged"] == true &&
            (string)cancelledResult?["actionId"] == (string)cancelled["actionId"] && (bool?)cancelledResult?["changed"] == true,
            "Cancellation preserves the exact action's already-applied state change in both action and device facts.");
        Check(lamp.ReadState().on == !beforeCancel &&
            (string)cancelledResult?["visualConfirmation"] == "interrupted-after-state-applied" &&
            (bool?)cancelledResult?["freshVisualObservation"] != true && string.IsNullOrEmpty((string)cancelled["resultCaptureId"]),
            "An interrupted verification keeps the actual light change without rollback or a fabricated visually confirmed result.");
    }

    private static IEnumerator Prepare(ChatSample chat)
    {
        Call(chat, "CancelSceneAction", "object-fixture-next");
        yield return Wait(() => IdleHead(chat), 5, "Previous actual head ownership returns");
        typeof(SceneActionIntegrationRegression).GetMethod("ResetRound", Flags).Invoke(null, new object[] { chat });
        Set(chat, "m_EnableSceneObjectAwareness", true);
    }

    private static IEnumerator Capture(ChatSample chat, string purpose, Action<SceneVisionObservation> complete)
    {
        SceneVisionObservation value = null;
        Call(chat, "RequestSceneObservation", purpose, new Action<SceneVisionObservation>(x => value = x));
        yield return Wait(() => value != null, 5, "Actual GPU source capture");
        Check(value.HasImage, "This case has real current character-view pixels.");
        SaveImage(purpose, value); complete(value);
    }

    private static IEnumerator Dispatch(ChatSample chat, ArdyRoomDialogueBridge bridge, string intent,
        SceneVisionObservation source, Vector2 pixel, string state, string caseName, string captureOverride = null,
        bool cancelAfterApplied = false)
    {
        string command = state == null ? "请观察选中的地方。" : state == "on" ? "请打开选中的灯。" : "请关闭选中的灯。";
        Call(chat, "RecordAcceptedMotionUserTurn", command);
        var wire = new JObject { ["intent"] = intent, ["origin"] = "user", ["evidence"] = command,
            ["visualTarget"] = new JObject { ["captureId"] = captureOverride ?? source.CaptureId,
                ["x"] = pixel.x, ["y"] = pixel.y, ["label"] = "本次图像中选择的地方" } };
        if (state != null) wire["objectState"] = state;
        Check(RoomTaskProtocol.TryDecision(wire.ToString(), command, false, out var decision), "Controlled object decision passes the actual parser.");
        Set(chat, "m_FormalResponseInFlight", true); Set(chat, "m_AgentRoundInFlight", true);
        int generation = (int)Get(chat, "m_FormalResponseGeneration"); bool completed = false, cancelledAfterApplied = false;
        Exception cancellationError = null;
        // Observe a real synchronous production event after TrySetPower committed, but before the
        // asynchronous result image returns. This invokes the existing pause cancellation boundary;
        // it neither invents a result nor adds a test-only path to production device code.
        Application.LogCallback onLog = (message, trace, type) => {
            if (!cancelAfterApplied || cancelledAfterApplied || !message.StartsWith("[Vision/Action] ", StringComparison.Ordinal)) return;
            try
            {
                var logged = JObject.Parse(message.Substring("[Vision/Action] ".Length));
                if ((string)logged["phase"] != "state-applied" || (string)logged["intent"] != intent ||
                    (string)logged["captureId"] != source.CaptureId) return;
                cancelledAfterApplied = true;
                LastReport["cancellationObservedStateApplied"] = logged;
                Call(chat, "PauseSceneAction");
            }
            catch (Exception error) { cancellationError = error; }
        };
        if (cancelAfterApplied) Application.logMessageReceived += onLog;
        try
        {
            Call(chat, "BeginSceneAction", decision, source, generation, new Action(() => completed = true));
            if (!cancelAfterApplied) yield return Wait(() => completed, 22, "Production " + caseName + " completion");
            else
            {
                yield return Wait(() => cancelledAfterApplied || completed || cancellationError != null, 22, "Actual state commit before cancellation");
                if (cancellationError != null) throw new InvalidOperationException("Exact state-applied cancellation callback failed.", cancellationError);
                Check(cancelledAfterApplied && !completed, "The production pause boundary cancels after a real state commit and before completion.");
                yield return Wait(() => IdleHead(chat) && !chat.SceneVisionCapture.IsCapturePending, 6, "Cancelled attention and outstanding real GPU callbacks drain");
                for (int i = 0; i < 4; i++) yield return null;
                Check(!completed && !(bool)Get(chat, "m_SceneActionActive") &&
                    (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1 &&
                    (int)Get(chat, "m_FormalResponseGeneration") > generation &&
                    !(bool)Get(chat, "m_FormalResponseInFlight") && !(bool)Get(chat, "m_AgentRoundInFlight"),
                    "Late actual capture callbacks cannot revive the cancelled action, formal owner or preparation completion.");
                LastReport["cancellationTriggerScope"] = "Existing PauseSceneAction invoked synchronously on the actual matching state-applied log event, after real device mutation and before result-image verification. No synthetic state/result or delayed transport substitute.";
            }
        }
        finally { if (cancelAfterApplied) Application.logMessageReceived -= onLog; }
        Call(chat, "ClearScheduledAgentWake"); Set(chat, "m_FormalResponseInFlight", false); Set(chat, "m_AgentRoundInFlight", false);
        var fact = Fact(chat);
        ((JArray)LastReport["cases"]).Add(new JObject { ["name"] = caseName, ["sourceCaptureId"] = source.CaptureId,
            ["controlledDecision"] = wire, ["fact"] = fact, ["objectContext"] = JObject.Parse(chat.SceneActionContextJson)["objectContext"]?.DeepClone() });
        File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString() + "\n");
    }

    private static SceneObservedObject FindOrdinaryObject(NevaSceneVisionCapture capture, SceneVisionObservation source, string lampId)
    {
        for (int y = 25; y <= 70; y += 5) for (int x = 20; x <= 80; x += 5)
            if (capture.TryResolveObservedObject(source, new Vector2(x / 100f, y / 100f), out var value, out _) &&
                value.VisibleConfirmed && value.EntityId != lampId && value.Root != null && value.Root.GetComponent<SceneObjectLightSwitch>() == null) return value;
        return null;
    }

    private static bool FindImageOnlyPixel(NevaSceneVisionCapture capture, SceneVisionObservation source, out Vector2 result)
    {
        for (int y = 25; y <= 70; y += 3) for (int x = 20; x <= 80; x += 3)
        {
            var pixel = new Vector2(x / 100f, y / 100f);
            if (Vector2.Distance(pixel, new Vector2(.5f, .5f)) < .12f || capture.TryResolve(source, pixel, out _, out _)) continue;
            if (capture.TryResolveObservedObject(source, pixel, out var value, out _) &&
                (value.VisibleConfirmed || capture.TryFindObservedObject(source, value.EntityId, out _, out _))) continue;
            result = pixel; return true;
        }
        result = default; return false;
    }

    private static void SaveImage(string name, SceneVisionObservation image)
    {
        File.WriteAllBytes(Path.Combine(directory, name + ".jpg"), Convert.FromBase64String(image.ImageDataUrl.Substring(image.ImageDataUrl.IndexOf(',') + 1)));
        File.WriteAllText(Path.Combine(directory, name + ".metadata.json"), image.MetadataJson);
    }
    private static IEnumerator Wait(Func<bool> ready, float seconds, string label)
    { float deadline = Time.realtimeSinceStartup + seconds; while (!ready()) { if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(label); yield return null; } }
    private static bool IdleHead(ChatSample chat) { var value = (NevaSceneAttention)Get(chat, "m_SceneAttention"); return value == null || !value.IsBusy; }
    private static JObject Fact(ChatSample chat) => JObject.Parse(JsonUtility.ToJson(Get(chat, "m_SceneActionFact")));
    private static void Check(bool valid, string reason) { checks++; if (!valid) throw new InvalidOperationException(reason); }
    private static object Get(object value, string name) => value.GetType().GetField(name, Flags).GetValue(value);
    private static void Set(object value, string name, object item) => value.GetType().GetField(name, Flags).SetValue(value, item);
    private static object Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, Flags).Invoke(value, args);
}
