using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Explicit single live model reference check in the isolated real-room fixture. Never auto-runs.</summary>
public static class SceneObjectReferenceQwenRegression
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string Endpoint = "http://127.0.0.1:8080/v1/chat/completions";
    private const string Command = "再把刚才那盏小灯打开";
    private static string directory;
    private static int checks, httpCalls;
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("The explicit reference probe requires the existing isolated real-room Play fixture.");
        directory = Path.Combine(output, "scene-object-reference-qwen"); Directory.CreateDirectory(directory);
        checks = 0; httpCalls = 0;
        LastReport = new JObject {
            ["status"]="running", ["endpoint"]=Endpoint, ["controlledSetup"]=new JArray(),
            ["scope"]="Controlled real-view inspection, runtime lamp-off and deliberate ordinary-surface selection establish object history through production execution. Then one actual shared-Qwen production room request must independently resolve the user's reference to the previously identified lamp. Only the exact accepted returned lamp-on entity decision is dispatched; unexpected intent/target fails without replacement. This is one reference episode, not a free-autonomy or general semantic reliability result.",
            ["limits"]="One unchanged production wire POST, original 240 output tokens, at most 20 seconds HTTP, no model retry or target correction, no desktop feed, ASR/TTS or formal-speech HTTP; no native locomotion.",
            ["httpRequests"]=new JArray(), ["modelCallbackInvoked"]=false
        };
        bool originalAwareness = (bool)Get(chat, "m_EnableSceneObjectAwareness");
        SceneObjectLightSwitch lamp = null;
        var stack = new Stack<IEnumerator>();
        try
        {
            Set(chat, "m_EnableSceneObjectAwareness", true);
            Check(chat != null && model != null && bridge != null && bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing,
                "The actual room fixture must be connected and idle.");
            Check(SceneObjectLampCatalog.EnsureRegistered(bridge.environmentRoot, out lamp, out string why),
                "Only the actual registered room fixture may provide lamp capability: " + why);
            Check(lamp.TryValidate(out _) && lamp.Root.gameObject.scene == bridge.avatar.gameObject.scene,
                "The audited lamp binding must still belong to this real actor scene.");
            LastReport["lampEntityId"] = lamp.EntityId;
            LastReport["originalLampState"] = JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));
            stack.Push(Exercise(chat, bridge, model, lamp));
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
                catch (Exception error) { LastReport["status"]="failed"; LastReport["error"]=error.ToString(); throw; }
                if (stack.Count > 0) yield return yielded;
            }
            LastReport["status"]="passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            Call(chat, "CancelSceneAction", "reference-live-fixture-finished");
            Call(chat, "InvalidateFormalResponse", "reference-live-fixture-finished");
            chat.StopAllCoroutines(); Call(chat, "ClearScheduledAgentWake");
            if (lamp != null)
            {
                bool restored = lamp.RestoreOriginal(out string why);
                LastReport["restoredOwnedLampState"] = restored; LastReport["restoreReason"] = why;
                if (!restored) LastReport["status"]="failed";
            }
            Set(chat, "m_EnableSceneObjectAwareness", originalAwareness);
            LastReport["checks"]=checks; LastReport["httpRequestCount"]=httpCalls;
            if ((string)LastReport["status"] == "running") LastReport["status"]="failed";
            Save();
        }
        if ((string)LastReport["status"] != "passed")
            throw new InvalidOperationException("The real reference episode or owned lamp restoration did not complete.");
    }

    private static IEnumerator Exercise(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, SceneObjectLightSwitch lamp)
    {
        int originalOperation = bridge.Controller.OperationRevision;
        Vector3 originalRoot = bridge.avatar.transform.position;
        Quaternion originalRotation = bridge.avatar.transform.rotation;
        yield return Prepare(chat);
        SceneVisionObservation source = null;
        yield return Capture(chat, "setup-inspect-source", x => source = x);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out var selected, out string why) && selected.VisibleConfirmed,
            "Actual current-view pixels must confirm this exact authored lamp: " + why);
        yield return DispatchSetup(chat, "inspect-object", source, selected.ImagePoint, null, "inspect-lamp");
        Check((bool?)Fact(chat)["objectConfirmed"] == true && (string)Fact(chat)["entityId"] == lamp.EntityId,
            "Production inspection must first establish real lamp identity.");

        yield return Prepare(chat);
        yield return Capture(chat, "setup-off-source", x => source = x);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out selected, out why) && selected.VisibleConfirmed,
            "Controlled off must select the same lamp in another real current image: " + why);
        yield return DispatchSetup(chat, "set-object-state", source, selected.ImagePoint, "off", "lamp-off");
        Check(!lamp.ReadState().on && !lamp.ReadState().lightEnabled && !lamp.ReadState().emissionEnabled &&
            ((string)Fact(chat)["phase"] == "performed" || (string)Fact(chat)["phase"] == "already-satisfied"),
            "Controlled setup must leave the real registered emitter and runtime Light off.");
        SaveImage("setup-lamp-off", chat.SceneVisionCapture.LatestObservation);

        yield return Prepare(chat);
        yield return Capture(chat, "setup-wrong-surface-source", x => source = x);
        SceneObservedObject ordinary = FindOrdinaryObject(chat.SceneVisionCapture, source, lamp.EntityId);
        Check(ordinary != null, "The real current view must provide a separate ordinary surface for the deliberate selection error.");
        yield return DispatchSetup(chat, "set-object-state", source, ordinary.ImagePoint, "on", "wrong-ordinary-surface");
        JObject afterWrong = JObject.Parse(chat.SceneActionContextJson)["objectContext"] as JObject;
        Check((string)Fact(chat)["phase"] == "rejected" && ((string)Fact(chat)["reason"] ?? "").Contains("no-registered-power-capability") &&
            !lamp.ReadState().on && (string)afterWrong?["selected"]?["entityId"] == ordinary.EntityId,
            "Production must select a different ordinary entity, reject its capability and keep the original lamp off.");
        Check(afterWrong?["knownObjectHistory"] is JArray prior && prior.Any(x => (string)x["entityId"] == lamp.EntityId),
            "The earlier lamp remains known after a different surface becomes selected.");
        LastReport["ordinaryEntityId"] = ordinary.EntityId;

        // This is the sole model-dependent step. The real planner builds both the wire and callback.
        yield return Prepare(chat);
        Call(chat, "RecordAcceptedMotionUserTurn", Command);
        yield return Capture(chat, "live-reference-source", x => source = x);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out selected, out why) && selected.VisibleConfirmed,
            "The previously known lamp must still be genuinely visible at the new reference request: " + why);
        int decisionsBefore = model.Decisions.Count, speechBefore = model.Speech.Count;
        int generation = (int)Get(chat, "m_FormalResponseGeneration");
        Set(chat, "m_AgentRoundInFlight", true);
        Call(chat, "BindFormalSceneObservation", source, generation);
        Check((bool)Call(chat, "TryPlanRoomTask", Command, source.ImageDataUrl, generation, false, null),
            "The production room planner must own the reference request.");
        yield return Wait(() => model.Decisions.Count > decisionsBefore, 5, "Production room reference request capture");
        Check(model.Decisions.Count == decisionsBefore + 1, "Only one production decision may be pending before the live request.");
        var pending = model.Decisions[decisionsBefore];
        var input = JObject.Parse(pending.Input); var payload = JObject.Parse(pending.Wire);
        var objects = input["sceneActionFacts"]?["objectContext"];
        Check(pending.Kind == "room" && pending.Image == source.ImageDataUrl && (string)input["currentUserText"] == Command &&
            (string)input["visualObservation"]?["captureId"] == source.CaptureId,
            "The exact user reference and this real image must enter the production room request.");
        Check(input["visibleKnownObjects"] is JArray visible && visible.Any(x =>
            (string)x["entityId"] == lamp.EntityId && (string)x["captureId"] == source.CaptureId) &&
            objects?["knownObjectHistory"] is JArray history && history.Any(x => (string)x["entityId"] == lamp.EntityId) &&
            (string)objects["selected"]?["entityId"] == ordinary.EntityId,
            "Production input must expose historical/current lamp identity while selected is the wrong ordinary entity.");
        Check((int?)payload["max_tokens"] == 640 &&
            payload.SelectTokens("$..image_url.url").Select(x => (string)x).SequenceEqual(new[] { source.ImageDataUrl }) &&
            (bool)Call(chat, "IsFreshSceneObservation", source),
            "Unmodified production parameters retain 240 tokens and only the current real-view image.");
        LastReport["sourceCaptureId"] = source.CaptureId; LastReport["userText"] = Command;
        File.WriteAllText(Path.Combine(directory, "room-input.json"), pending.Input + "\n");
        string completion = null;
        yield return Post(pending.Wire, source, value => completion = value);
        LastReport["modelDecisionText"] = completion; Save();
        Check(RoomTaskProtocol.TryDecision(completion, Command, false, out var decision), "Real Qwen output must pass the unchanged production decision parser.");
        LastReport["modelDecision"] = JObject.FromObject(decision);
        Check(decision.intent == "set-object-state" && decision.objectState == "on" && decision.IsEntityReference &&
            decision.entityTarget.entityId == lamp.EntityId && decision.entityTarget.captureId == source.CaptureId,
            "Actual Qwen must independently choose lamp-on with the original known entity/current capture; other choices fail without substitution.");
        LastReport["modelCallbackInvoked"] = true;
        pending.Complete(true, completion, "actual-shared-qwen");
        yield return Wait(() => !(bool)Get(chat, "m_SceneActionActive") && model.Speech.Count > speechBefore,
            22, "Exact model callback's production object execution and captured-only formal handoff");
        var fact = Fact(chat); var image = chat.SceneVisionCapture.LatestObservation; var state = lamp.ReadState();
        LastReport["actualSceneAction"] = fact;
        LastReport["actualLampState"] = JObject.Parse(JsonUtility.ToJson(state));
        LastReport["actualObjectContext"] = JObject.Parse(chat.SceneActionContextJson)["objectContext"]?.DeepClone();
        LastReport["formalRequestsCapturedOnly"] = model.Speech.Count - speechBefore;
        Check(model.Decisions.Count == decisionsBefore + 1 && httpCalls == 1,
            "No model retry, target correction or additional real request may repair the reference episode.");
        Check((string)fact["phase"] == "performed" && (string)fact["entityId"] == lamp.EntityId &&
            (bool?)fact["stateChanged"] == true && state.on && state.lightEnabled && state.emissionEnabled,
            "The original model result must actually turn the exact same real emitter and runtime Light on.");
        Check(image != null && image.HasImage && image.CaptureId != source.CaptureId &&
            image.CaptureId == (string)fact["resultCaptureId"], "Execution must produce its own actual new result image.");
        Check((bool?)fact["reached"] != true && bridge.Controller.OperationRevision == originalOperation &&
            Vector3.Distance(originalRoot, bridge.avatar.transform.position) < .0001f &&
            Quaternion.Angle(originalRotation, bridge.avatar.transform.rotation) < .001f,
            "Reference resolution must not invent physical pressing, native locomotion or root movement.");
        SaveImage("live-lamp-on-result", image);
        LastReport["outcome"] = "same-known-entity-reopened-and-reobserved";
    }

    private static IEnumerator Post(string exactWire, SceneVisionObservation source, Action<string> complete)
    {
        Check(++httpCalls == 1, "Only one live model POST is permitted.");
        File.WriteAllText(Path.Combine(directory, "room-request.json"), exactWire + "\n");
        float submitted = Time.realtimeSinceStartup;
        string hash; using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(exactWire))).Replace("-", "").ToLowerInvariant();
        var record = new JObject { ["submittedUtc"]=DateTime.UtcNow.ToString("o"), ["sourceCaptureId"]=source.CaptureId,
            ["sourceAgeSeconds"]=submitted-source.CapturedAtRealtime, ["maxTokens"]=240,
            ["productionWireUnmodified"]=true, ["requestSha256"]=hash };
        ((JArray)LastReport["httpRequests"]).Add(record); Save();
        using (var request = new UnityWebRequest(Endpoint, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(exactWire));
            request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 19; request.redirectLimit = 0;
            request.SetRequestHeader("Content-Type", "application/json");
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                if (Time.realtimeSinceStartup-submitted > 20) { request.Abort(); throw new TimeoutException("Single reference POST exceeded 20 seconds; no retry."); }
                yield return null;
            }
            string envelope = request.downloadHandler.text;
            record["httpStatus"]=request.responseCode; record["elapsedSeconds"]=Time.realtimeSinceStartup-submitted;
            Check(envelope != null && envelope.Length <= 200000, "Model response must fit the bounded envelope.");
            File.WriteAllText(Path.Combine(directory, "room-response.json"), envelope + "\n");
            Check(request.result == UnityWebRequest.Result.Success && request.responseCode == 200, "The single real Qwen POST must succeed.");
            object[] args = { envelope, null, null };
            bool valid = (bool)typeof(ChatQW).GetMethod("TryReadRoomTaskCompletion", Flags).Invoke(null, args);
            record["completionValid"]=valid; record["completionStatus"]=(string)args[2];
            var body = JObject.Parse(envelope); record["usage"]=body["usage"]?.DeepClone(); record["timings"]=body["timings"]?.DeepClone(); Save();
            Check(valid, "The actual Qwen envelope must be complete and valid: " + (string)args[2]);
            complete((string)args[1]);
        }
    }

    private static IEnumerator DispatchSetup(ChatSample chat, string intent, SceneVisionObservation source, Vector2 pixel, string state, string name)
    {
        string command = state == null ? "请观察选中的地方。" : state == "on" ? "请打开选中的灯。" : "请关闭选中的灯。";
        Call(chat, "RecordAcceptedMotionUserTurn", command);
        var wire = new JObject { ["intent"]=intent, ["origin"]="user", ["evidence"]=command,
            ["visualTarget"]=new JObject { ["captureId"]=source.CaptureId, ["x"]=pixel.x, ["y"]=pixel.y, ["label"]="本次图像中选择的地方" } };
        if (state != null) wire["objectState"]=state;
        Check(RoomTaskProtocol.TryDecision(wire.ToString(), command, false, out var decision), "Controlled setup uses the actual closed parser.");
        Set(chat, "m_FormalResponseInFlight", true); Set(chat, "m_AgentRoundInFlight", true);
        bool completed = false;
        Call(chat, "BeginSceneAction", decision, source, (int)Get(chat, "m_FormalResponseGeneration"), new Action(() => completed = true));
        yield return Wait(() => completed, 22, "Controlled production " + name);
        Call(chat, "ClearScheduledAgentWake"); Set(chat, "m_FormalResponseInFlight", false); Set(chat, "m_AgentRoundInFlight", false);
        ((JArray)LastReport["controlledSetup"]).Add(new JObject { ["name"]=name, ["decision"]=wire,
            ["fact"]=Fact(chat), ["objectContext"]=JObject.Parse(chat.SceneActionContextJson)["objectContext"]?.DeepClone() }); Save();
    }
    private static IEnumerator Prepare(ChatSample chat)
    {
        Call(chat, "CancelSceneAction", "reference-fixture-next");
        yield return Wait(() => IdleHead(chat), 5, "Previous attention ownership return");
        typeof(SceneActionIntegrationRegression).GetMethod("ResetRound", Flags).Invoke(null, new object[] { chat });
        Set(chat, "m_EnableSceneObjectAwareness", true);
    }
    private static IEnumerator Capture(ChatSample chat, string purpose, Action<SceneVisionObservation> complete)
    {
        SceneVisionObservation source = null;
        Call(chat, "RequestSceneObservation", purpose, new Action<SceneVisionObservation>(x => source = x));
        yield return Wait(() => source != null, 5, "Actual isolated character-view GPU capture");
        Check(source.HasImage, "This step requires real current character-view pixels."); SaveImage(purpose, source); complete(source);
    }
    private static SceneObservedObject FindOrdinaryObject(NevaSceneVisionCapture capture, SceneVisionObservation source, string lampId)
    {
        for (int y=25; y<=70; y+=5) for (int x=20; x<=80; x+=5)
            if (capture.TryResolveObservedObject(source, new Vector2(x/100f,y/100f), out var value, out _) &&
                value.VisibleConfirmed && value.EntityId != lampId && value.Root != null && value.Root.GetComponent<SceneObjectLightSwitch>() == null) return value;
        return null;
    }
    private static void SaveImage(string name, SceneVisionObservation image)
    {
        File.WriteAllBytes(Path.Combine(directory, name+".jpg"), Convert.FromBase64String(image.ImageDataUrl.Substring(image.ImageDataUrl.IndexOf(',')+1)));
        File.WriteAllText(Path.Combine(directory, name+".metadata.json"), image.MetadataJson);
    }
    private static IEnumerator Wait(Func<bool> ready, float seconds, string label)
    { float deadline=Time.realtimeSinceStartup+seconds; while (!ready()) { if (Time.realtimeSinceStartup>deadline) throw new TimeoutException(label); yield return null; } }
    private static bool IdleHead(ChatSample chat) { var value=(NevaSceneAttention)Get(chat,"m_SceneAttention"); return value==null || !value.IsBusy; }
    private static JObject Fact(ChatSample chat) => JObject.Parse(JsonUtility.ToJson(Get(chat,"m_SceneActionFact")));
    private static void Check(bool valid, string reason) { checks++; if (!valid) throw new InvalidOperationException(reason); }
    private static void Save() => File.WriteAllText(Path.Combine(directory,"report.json"), LastReport.ToString()+"\n");
    private static object Get(object value, string name) => value.GetType().GetField(name,Flags).GetValue(value);
    private static void Set(object value, string name, object item) => value.GetType().GetField(name,Flags).SetValue(value,item);
    private static object Call(object value, string name, params object[] args) => value.GetType().GetMethod(name,Flags).Invoke(value,args);
}
