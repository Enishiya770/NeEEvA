using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>One explicit real-model search/match episode in the existing rendered room.</summary>
public static class SceneSearchLiveRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string UserText = "找一下柜子上的发光小灯，先只看。";
    private const string Endpoint = "http://127.0.0.1:8080/v1/chat/completions";
    private static int checks;
    private static string directory;
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Array.IndexOf(Environment.GetCommandLineArgs(), "-nevaSceneSearchLive") < 0 ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Live search requires its explicit isolated Play flag.");
        checks = 0; directory = Path.Combine(output, "scene-search-live"); Directory.CreateDirectory(directory);
        LastReport = new JObject { ["status"] = "running", ["userText"] = UserText,
            ["scope"] = "One real ChatQW search and independent matching episode over actual room RGB, with real head actions if selected. Search entry is explicitly supplied as find; this does not measure room-intent classification, ASR, speech or native body navigation. Test-only lamp identity is an outcome oracle, never injected as the selected candidate or target point.",
            ["limits"] = "180 seconds for the search episode; production 12-step/36-decision/3-no-progress limits and production HTTP deadlines. No inference retry, answer replacement, device writes, native service, slot initialization or actor teleport.",
            ["setupNetwork"] = new JArray(), ["modelTraffic"] = new JArray() };
        string[] fields = { "m_EnableSceneActions", "m_EnableSceneObjectAwareness", "m_EnableSceneObjectTargetMatching",
            "m_EnableScreenVision", "m_AgentEyesOpen", "m_AgentRunning", "m_FormalResponseInFlight", "m_AgentRoundInFlight",
            "m_EnableFirstReplyRouting", "m_SpatialDeviceGoal", "m_PendingDeviceClarification", "m_IndependentSpatialRequestScope" };
        object[] saved = fields.Select(name => Get(chat, name)).ToArray();
        var stack = new Stack<IEnumerator>(); stack.Push(Exercise(chat, bridge, model));
        try
        {
            while (stack.Count > 0)
            {
                object next = null;
                try
                {
                    while (stack.Count > 0)
                    {
                        var routine = stack.Peek();
                        if (!routine.MoveNext()) { (routine as IDisposable)?.Dispose(); stack.Pop(); continue; }
                        if (routine.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        next = routine.Current; break;
                    }
                }
                catch (Exception error) { LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw; }
                if (stack.Count > 0) yield return next;
            }
            LastReport["status"] = "passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            LastReport["terminalFacts"] = ((JObject)Call(chat, "BuildSceneSearchContext")).DeepClone();
            Call(chat, "CancelSceneSearchTask", "live-search-fixture-finished");
            model.DisableLiveSceneSearch();
            LastReport["modelTraffic"] = model.LiveSceneSearchTraffic.DeepClone();
            Call(chat, "CancelSceneAction", "live-search-fixture-finished");
            Call(chat, "ClearScheduledAgentWake");
            for (int i = 0; i < fields.Length; i++) Set(chat, fields[i], saved[i]);
            LastReport["checks"] = checks;
            File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString());
        }
    }

    private static IEnumerator Exercise(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        Check(bridge != null && bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing &&
            bridge.serviceUrl.TrimEnd('/') == "http://127.0.0.1:1", "Live search has no native GPU service or prior body owner.");
        string modelId = null;
        using (var request = UnityWebRequest.Get("http://127.0.0.1:8080/v1/models"))
        {
            request.timeout = 6; float started = Time.realtimeSinceStartup;
            var setupRequest = request.SendWebRequest();
            // The Editor fixture flattens iterators itself; wait explicitly for
            // async operations instead of treating one Update as completion.
            while (!setupRequest.isDone) yield return null;
            string body = request.downloadHandler?.text ?? "";
            File.WriteAllText(Path.Combine(directory, "setup-models.response.json"), body);
            ((JArray)LastReport["setupNetwork"]).Add(new JObject { ["method"] = "GET", ["path"] = "/v1/models",
                ["statusCode"] = request.responseCode, ["elapsedSeconds"] = Time.realtimeSinceStartup - started,
                ["scope"] = "Read-only model discovery; no inference or slot initialization." });
            Check(request.result == UnityWebRequest.Result.Success && request.responseCode == 200, "Actual local model discovery succeeds.");
            var ids = ((JArray)JObject.Parse(body)["data"]).OfType<JObject>().Select(item => (string)item["id"])
                .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray();
            Check(ids.Length == 1, "The local server exposes exactly one unambiguous model ID for this evidence run."); modelId = ids[0];
        }
        LastReport["modelId"] = modelId; LastReport["endpoint"] = Endpoint;
        Call(typeof(SceneActionIntegrationRegression), "ResetRound", chat);
        foreach (string field in new[] { "m_EnableSceneActions", "m_EnableSceneObjectAwareness", "m_EnableSceneObjectTargetMatching",
            "m_EnableScreenVision", "m_AgentEyesOpen", "m_AgentRunning", "m_FormalResponseInFlight", "m_AgentRoundInFlight" }) Set(chat, field, true);
        Set(chat, "m_EnableFirstReplyRouting", false); Set(chat, "m_SpatialDeviceGoal", null);
        Set(chat, "m_PendingDeviceClarification", null); Set(chat, "m_IndependentSpatialRequestScope", null);
        Call(chat, "EnsureSceneObjectCatalog");
        Check(SceneObjectLampCatalog.EnsureRegistered(bridge.environmentRoot, out var lamp, out string failure) && lamp.TryValidate(out _),
            "A real room lamp supplies only the test outcome oracle: " + failure);
        JObject before = JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));
        int writesBefore = ((IDictionary)Get(lamp, "appliedActions")).Count;
        LastReport["oracle"] = new JObject { ["entityId"] = lamp.EntityId, ["initialState"] = before.DeepClone(),
            ["appliedActionCount"] = writesBefore,
            ["setup"] = "SceneObjectLampCatalog.EnsureRegistered prepares runtime registration before the initial state/action snapshots. No TrySetPower setup call is made; an already-lit, pixel-visible lamp is a checked precondition.",
            ["scope"] = "Evaluation only; this oracle is not appended to search or match input. The ordinary visible catalog may naturally include its engine identity among all candidates." };
        Check((bool?)before["on"] == true, "The real source fixture already has its lamp on; this test does not turn it on as setup.");
        Call(chat, "RecordAcceptedMotionUserTurn", UserText);
        SceneVisionObservation source = null;
        Call(chat, "RequestSceneObservation", "live-search-initial", new Action<SceneVisionObservation>(value => source = value));
        yield return Wait(() => source != null, 8, "actual initial room capture");
        Check(source.HasImage && source.HasSurfaceGrounding &&
            chat.SceneVisionCapture.TryFindObservedObject(source, lamp.EntityId, out var oracleVisible, out failure) && oracleVisible.VisibleConfirmed,
            "The initial actual pixels contain the real lamp, so this bounded case requires no native relocation: " + failure);
        SaveImage("initial", source);
        model.EnableLiveSceneSearch(Endpoint, modelId);
        var entry = new JObject { ["intent"] = "search-scene", ["origin"] = "user", ["evidence"] = UserText,
            ["searchTask"] = new JObject { ["mode"] = "find" } };
        Check(RoomTaskProtocol.TryDecision(entry.ToString(), UserText, false, out var decision), "Explicit find entry preserves the full request without adding target coordinates.");
        int reviews = model.DeviceGoalReviews.Count, operation = bridge.Controller.OperationRevision;
        int searches = model.SceneSearchRequests.Count, matches = model.Matches.Count, completed = 0;
        float began = Time.realtimeSinceStartup;
        Call(chat, "BeginSceneSearchTask", decision, source, (int)Get(chat, "m_FormalResponseGeneration"), new Action(() => completed++));
        while (completed == 0 && Time.realtimeSinceStartup - began < 180f)
        {
            if (((IDictionary)Get(lamp, "appliedActions")).Count != writesBefore || model.DeviceGoalReviews.Count != reviews ||
                Get(chat, "m_SpatialDeviceGoal") != null)
                Check(false, "Live observation applied a device operation or acquired device-goal authority.");
            yield return null;
        }
        LastReport["episodeSeconds"] = Time.realtimeSinceStartup - began;
        var result = (JObject)Call(chat, "BuildSceneSearchContext"); LastReport["terminalFacts"] = result.DeepClone();
        LastReport["searchRequests"] = model.SceneSearchRequests.Count - searches;
        LastReport["matchRequests"] = model.Matches.Count - matches;
        LastReport["finalState"] = JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));
        LastReport["appliedActionDelta"] = ((IDictionary)Get(lamp, "appliedActions")).Count - writesBefore;
        Check(completed == 1 && (string)result["status"] == "found" && (string)result["foundEntityId"] == lamp.EntityId,
            "The real model loop finds the real requested lamp within 180 seconds; incomplete/timeout/wrong-object results remain failures.");
        var proof = ((JArray)result["receipts"]).OfType<JObject>().Last(item => (bool?)item["confirmed"] == true);
        string selection = (string)proof["selectionCaptureId"], matchCapture = (string)proof["matchCaptureId"], found = (string)result["foundCaptureId"];
        Check(!string.IsNullOrEmpty(selection) && !string.IsNullOrEmpty(matchCapture) && !string.IsNullOrEmpty(found) &&
            selection != matchCapture && matchCapture != found && selection != found,
            "Actual model selection, independent semantic matching and final same-entity proof have three distinct capture identities.");
        var final = chat.SceneVisionCapture.LatestObservation;
        Check(final.CaptureId == found && (bool)Call(chat, "IsFreshSceneObservation", final) &&
            chat.SceneVisionCapture.TryFindObservedObject(final, lamp.EntityId, out var observed, out failure) && observed.VisibleConfirmed,
            "Found is backed by fresh actual pixels of the same real lamp, not by a model claim or historical frame.");
        SaveImage("found", final);
        Check((bool?)result["deviceWriteAuthorized"] == false && model.DeviceGoalReviews.Count == reviews &&
            ((IDictionary)Get(lamp, "appliedActions")).Count == writesBefore && JToken.DeepEquals(before, LastReport["finalState"]),
            "Zero registered device operations occurred, including an already-on write or a change followed by restoration.");
        Check(bridge.Controller.OperationRevision == operation && !bridge.ReservesBody,
            "The live acceptance episode uses no native body operation; any head observations are independently rendered.");
        Check(model.SceneSearchRequests.Count > searches && model.Matches.Count > matches &&
            model.LiveSceneSearchTraffic.OfType<JObject>().All(item => item["accepted"] != null),
            "Both real provider stages completed and every dispatched inference retains a result record.");
    }

    private static void SaveImage(string name, SceneVisionObservation view)
    {
        File.WriteAllBytes(Path.Combine(directory, name + ".jpg"), Convert.FromBase64String(view.ImageDataUrl.Substring(view.ImageDataUrl.IndexOf(',') + 1)));
        File.WriteAllText(Path.Combine(directory, name + ".metadata.json"), view.MetadataJson);
    }
    private static IEnumerator Wait(Func<bool> ready, float seconds, string label)
    { float until = Time.realtimeSinceStartup + seconds; while (!ready()) { if (Time.realtimeSinceStartup > until) throw new TimeoutException(label); yield return null; } }
    private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
    private static object Get(object target, string field) => target.GetType().GetField(field, Flags).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Flags).SetValue(target, value);
    private static object Call(object target, string name, params object[] args)
    {
        Type type = target as Type ?? target.GetType();
        try { return type.GetMethods(Flags).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target is Type ? null : target, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
}
