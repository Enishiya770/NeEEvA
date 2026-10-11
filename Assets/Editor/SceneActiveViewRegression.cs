using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Controlled directional intents through production scene dispatch, actual rig, GPU images and native ARDY.
/// Runs only in the existing isolated real-room Play fixture. No new test scene, model HTTP or speech.</summary>
public static class SceneActiveViewRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public static JObject LastReport { get; private set; }
    private static int checks;
    private static void Check(bool valid, string reason)
    { checks++; if (!valid) throw new InvalidOperationException(reason); }
    public static int RunProtocolChecks()
    {
        int start = checks;
        foreach (string intent in new[] { "orient-view", "turn-in-place" })
        {
            JObject value = Decision(intent, "fixture-capture", 25, 0);
            Check(RoomTaskProtocol.TryDecision(value.ToString(), "", true, out var parsed) && parsed.IsVisual && parsed.IsViewChange && parsed.visualTarget == null,
                "Valid directional decision is a visual action without a fabricated visible target.");
            Check(!RoomTaskProtocol.TryDecision(value.ToString(), "", false, out _), "Autonomy source cannot become a user command.");
            foreach (JToken bad in new JToken[] { -181, 181, 0, "25", JValue.CreateNull(), new JValue(double.NaN), new JValue(double.PositiveInfinity) })
            { var copy = (JObject)value.DeepClone(); copy["viewChange"]["yaw"] = bad; Check(!RoomTaskProtocol.TryDecision(copy.ToString(), "", true, out _), "Yaw must be finite, nonzero and bounded."); }
            foreach (string key in new[] { "captureId", "yaw", "pitch" })
            { var copy = (JObject)value.DeepClone(); ((JObject)copy["viewChange"]).Remove(key); Check(!RoomTaskProtocol.TryDecision(copy.ToString(), "", true, out _), "All directional fields are required."); }
            foreach (JToken bad in new JToken[] { "", new string('a',129), JValue.CreateNull() })
            { var copy = (JObject)value.DeepClone(); copy["viewChange"]["captureId"] = bad; Check(!RoomTaskProtocol.TryDecision(copy.ToString(), "", true, out _), "Capture identity must be explicit and bounded."); }
            var extra = (JObject)value.DeepClone(); extra["visualTarget"] = new JObject { ["x"] = .5, ["y"] = .5, ["captureId"] = "fixture", ["label"] = "invented" };
            Check(!RoomTaskProtocol.TryDecision(extra.ToString(), "", true, out _), "Image-point and unknown-direction actions cannot be mixed.");
            extra = (JObject)value.DeepClone(); extra["viewChange"]["worldPosition"] = new JArray(1,2,3);
            Check(!RoomTaskProtocol.TryDecision(extra.ToString(), "", true, out _), "No extra coordinates allowed.");
            extra = (JObject)value.DeepClone(); extra["intent"] = "none";
            Check(!RoomTaskProtocol.TryDecision(extra.ToString(), "", true, out _), "No-action cannot carry executable direction.");
        }
        Check(!RoomTaskProtocol.TryDecision(Decision("orient-view", "capture", 51, 0).ToString(), "", true, out _), "Head yaw stays inside 50 degrees.");
        Check(!RoomTaskProtocol.TryDecision(Decision("turn-in-place", "capture", 60, 10).ToString(), "", true, out _), "Body turn never pretends to pitch its whole body.");
        Check(RoomTaskProtocol.TryDecision(Decision("orient-view", "capture", 0, -20).ToString(), "", true, out _), "Vertical-only exploration is supported.");
        foreach (float yaw in new[] { -180f, 180f })
            Check(RoomTaskProtocol.TryDecision(Decision("turn-in-place", "capture", yaw, 0).ToString(), "", true, out _), "Half turn is a bounded body request.");
        var branches = (JArray)JObject.Parse(RoomTaskProtocol.DecisionSchema)["oneOf"];
        JObject ViewBranch(string intent) => branches.OfType<JObject>().Single(branch =>
            branch["properties"]["deviceGoal"] == null && branch["properties"]["intent"]["enum"].Values<string>().Contains(intent));
        var head = ViewBranch("orient-view")["properties"]["viewChange"]["properties"];
        var body = ViewBranch("turn-in-place")["properties"]["viewChange"]["properties"];
        Check((int)head["yaw"]["minimum"] == -50 && (int)head["yaw"]["maximum"] == 50 &&
            (int)head["pitch"]["minimum"] == -25 && (int)head["pitch"]["maximum"] == 25 &&
            (int)body["yaw"]["minimum"] == -180 && (int)body["yaw"]["maximum"] == 180 &&
            (int)body["pitch"]["minimum"] == 0 && (int)body["pitch"]["maximum"] == 0,
            "Wire schema separates neck and body limits.");
        return checks - start;
    }
    private static JObject Decision(string intent, string id, float yaw, float pitch) => new JObject {
        ["intent"] = intent, ["origin"] = "autonomous", ["evidence"] = "希望查看当前视野之外的方向",
        ["viewChange"] = new JObject { ["captureId"] = id, ["yaw"] = yaw, ["pitch"] = pitch } };

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Active view regression requires the existing isolated real-room Play fixture.");
        checks = 0;
        string directory = Path.Combine(output, "scene-active-view"); Directory.CreateDirectory(directory);
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(), ["scope"] =
            "Controlled unknown-direction requests through actual BeginSceneAction; calibrated VRM head/eye pose, fresh GPU image, native ARDY in-place body turn and explicit cancellation. Controlled choices are not evidence of Qwen autonomy or semantic understanding. No direct actor/user transform writes, no TTS/model HTTP." };
        var stack = new Stack<IEnumerator>(); stack.Push(Exercise(chat, bridge, directory));
        try
        {
            LastReport["protocolChecks"] = RunProtocolChecks();
            while (stack.Count > 0)
            {
                object current = null;
                try
                {
                    while (stack.Count > 0)
                    {
                        var routine = stack.Peek();
                        if (!routine.MoveNext()) { (routine as IDisposable)?.Dispose(); stack.Pop(); continue; }
                        if (routine.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        current = routine.Current; break;
                    }
                }
                catch (Exception error) { LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw; }
                if (stack.Count > 0) yield return current;
            }
            LastReport["status"] = "passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            Call(chat, "CancelSceneAction", "active-view-regression-complete");
            LastReport["checks"] = checks;
            File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString());
        }
    }
    private static IEnumerator Exercise(ChatSample chat, ArdyRoomDialogueBridge bridge, string directory)
    {
        Check(chat != null && bridge != null && bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing,
            "Actual native room fixture is connected and idle.");
        foreach (string mode in new[] { "head", "body", "cancel-body" })
        {
            typeof(SceneActionIntegrationRegression).GetMethod("ResetRound", Flags).Invoke(null, new object[] { chat });
            yield return Wait(() => !bridge.ReservesBody && IdleHead(chat), 5, "prior exact owner released");
            Set(chat, "m_FormalResponseInFlight", true); Set(chat, "m_AgentRoundInFlight", true);
            SceneVisionObservation source = null;
            Call(chat, "RequestSceneObservation", "active-view-fixture", new Action<SceneVisionObservation>(value => source = value));
            yield return Wait(() => source != null, 5, "source GPU observation");
            Check(source.HasImage, "Each direction starts with actual fresh source pixels.");
            string intent = mode == "head" ? "orient-view" : "turn-in-place";
            float yaw = mode == "head" ? -22 : mode == "body" ? -55 : 75;
            Check(RoomTaskProtocol.TryDecision(Decision(intent, source.CaptureId, yaw, mode == "head" ? 10 : 0).ToString(), "", true, out var decision), "Controlled protocol validates.");
            Vector3 beforeRoot = bridge.avatar.transform.position; Quaternion beforeBody = bridge.avatar.transform.rotation;
            int operation = bridge.Controller.OperationRevision, generation = (int)Get(chat, "m_FormalResponseGeneration");
            bool completed = false;
            Call(chat, "BeginSceneAction", decision, source, generation, new Action(() => completed = true));
            string actionId = (string)Fact(chat)["actionId"];
            Check(!string.IsNullOrEmpty(actionId) && (bool)Get(chat, "m_SceneActionActive"), "Actual scene action has registered identity and ongoing state.");
            var record = new JObject { ["mode"] = mode, ["actionId"] = actionId, ["sourceCaptureId"] = source.CaptureId };
            ((JArray)LastReport["cases"]).Add(record);
            if (mode == "cancel-body")
            {
                yield return Wait(() => bridge.IsOrientingScene && (bridge.Controller.OperationRevision > operation) &&
                    Quaternion.Angle(beforeBody, bridge.avatar.transform.rotation) > 3, 40, "native turn has actually started");
                Call(chat, "CancelSceneAction", "explicit-stop-active-view");
                Vector3 stoppedAt = bridge.avatar.transform.position;
                yield return Wait(() => !bridge.ReservesBody, 5, "smooth native cancel ownership release");
                Check(!completed && (string)Fact(chat)["phase"] == "cancelled" && !(bool)Get(chat, "m_SceneActionActive"),
                    "Explicit stop revokes pending image/preparation, never revives its completion.");
                Check(bridge.LastSceneOrientResult?.actionId == actionId && bridge.LastSceneOrientResult.oriented == false,
                    "Stopped body turn yields a matching non-success terminal.");
                Check(Vector3.Distance(stoppedAt, bridge.avatar.transform.position) < .03f && bridge.avatar.GetComponent<Animator>().enabled,
                    "Stopped in-place turn releases actual Animator and preserves root position.");
                record["terminal"] = JObject.Parse(JsonUtility.ToJson(bridge.LastSceneOrientResult));
            }
            else
            {
                yield return Wait(() => completed, 45, "production direction completes with fresh actual image");
                var fact = Fact(chat); var image = chat.SceneVisionCapture.LatestObservation;
                Check((string)fact["phase"] == "observed" && image.HasImage && image.CaptureId != source.CaptureId &&
                    image.CaptureId == (string)fact["resultCaptureId"] && (bool?)fact["reached"] != true,
                    "Observed is backed by the new actual capture, never an invented arrival.");
                Check((float)fact["actualViewChangeDegrees"] > 10, "The new picture comes from a materially changed actual view.");
                SaveImage(directory, mode + "-source", source); SaveImage(directory, mode + "-result", image);
                if (mode == "head")
                {
                    var attention = (NevaSceneAttention)Get(chat, "m_SceneAttention");
                    Check(attention.IsSettled && attention.OwnerId == actionId && bridge.Controller.OperationRevision == operation,
                        "Head exploration retains its actual view without acquiring native body execution.");
                    Check(Vector3.Distance(beforeRoot, bridge.avatar.transform.position) < .0001f && Quaternion.Angle(beforeBody, bridge.avatar.transform.rotation) < .01f,
                        "Head exploration does not move/rotate root.");
                    float deadline = Time.realtimeSinceStartup + .5f; while (Time.realtimeSinceStartup < deadline) yield return null;
                    Check(attention.IsSettled && attention.OwnerId == actionId, "New view remains available to the following decision.");
                    Call(chat, "OnApplicationPause", true);
                    yield return Wait(() => !attention.IsBusy, 3f, "paused retained head returns smoothly");
                    Check(bridge.Controller.OperationRevision == operation && !bridge.IsFollowing && !bridge.ReservesBody,
                        "Application pause releases only scene attention without starting or acquiring a follow/body task.");
                    Call(chat, "OnApplicationPause", false);
                }
                else
                {
                    var terminal = bridge.LastSceneOrientResult;
                    Check(terminal != null && terminal.actionId == actionId && terminal.oriented && terminal.status == "turned" &&
                        Mathf.Abs(terminal.actualYaw - yaw) < 15 && terminal.actualRootDisplacement < .16f,
                        "Native body turn physically reaches requested yaw on its original legal floor.");
                    Check(bridge.Controller.OperationRevision > operation && !bridge.ReservesBody && bridge.avatar.GetComponent<Animator>().enabled,
                        "Native turn returns body ownership to Animator.");
                    record["terminal"] = JObject.Parse(JsonUtility.ToJson(terminal));
                }
                record["fact"] = fact;
            }
        }
    }
    private static void SaveImage(string output, string name, SceneVisionObservation observation)
    {
        File.WriteAllBytes(Path.Combine(output, name + ".jpg"), Convert.FromBase64String(observation.ImageDataUrl.Substring(observation.ImageDataUrl.IndexOf(',') + 1)));
        File.WriteAllText(Path.Combine(output, name + ".metadata.json"), observation.MetadataJson);
    }
    private static IEnumerator Wait(Func<bool> condition, float seconds, string label)
    {
        float deadline = Time.realtimeSinceStartup + seconds;
        while (!condition()) { if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(label); yield return null; }
    }
    private static bool IdleHead(ChatSample chat) { var attention = (NevaSceneAttention)Get(chat,"m_SceneAttention"); return attention == null || !attention.IsBusy; }
    private static JObject Fact(ChatSample chat) => JObject.Parse(JsonUtility.ToJson(Get(chat,"m_SceneActionFact")));
    private static object Get(object value,string name) => value.GetType().GetField(name,Flags).GetValue(value);
    private static void Set(object value,string name,object data) => value.GetType().GetField(name,Flags).SetValue(value,data);
    private static object Call(object value,string name,params object[] args) => value.GetType().GetMethod(name,Flags).Invoke(value,args);
}
