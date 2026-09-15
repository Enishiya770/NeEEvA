using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>One controlled autonomy-to-native-arrival chain in the existing isolated actual-room fixture.</summary>
public static class SceneActionProductionApproachRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public static JObject LastReport { get; private set; }
    private sealed class Context
    {
        public ChatSample chat;
        public ArdyRoomDialogueBridge bridge;
        public NevaSceneVisionIntegrationModel model;
        public NevaEyeTracking tracking;
        public string output, actionId, sourceCaptureId;
        public float started, previousAt, lastSampleAt = -1000;
        public Vector3 initialRoot, previousRoot;
        public Quaternion initialHead;
        public int checks;
        public float maximumRootStep, maximumHeadDegrees, maximumAttentionDegrees;
        public readonly List<ArdyRoomDialogueBridge.SceneTargetResult> results = new List<ArdyRoomDialogueBridge.SceneTargetResult>();
        public readonly List<JObject> phases = new List<JObject>();
        public void Check(bool good, string message)
        { checks++; if (!good) throw new InvalidOperationException(message); }
        public void Terminal(ArdyRoomDialogueBridge.SceneTargetResult result)
        {
            if (result != null) results.Add(result);
        }
        public void Log(string message, string stack, LogType type)
        {
            const string prefix = "[Vision/Action] ";
            if (message == null || !message.StartsWith(prefix, StringComparison.Ordinal)) return;
            try
            {
                var value = JObject.Parse(message.Substring(prefix.Length));
                if (!string.IsNullOrEmpty(sourceCaptureId) && (string)value["captureId"] == sourceCaptureId) phases.Add(value);
            }
            catch { /* Production diagnostics stay data; an unrelated malformed log cannot alter execution. */ }
        }
        public void Sample()
        {
            float now = Time.realtimeSinceStartup;
            Check(now - started < 60f, "Full production approach chain exceeded 60 seconds; actual phase=" + Fact(chat));
            Vector3 root = bridge.avatar.transform.position;
            float step = Vector3.Distance(root, previousRoot), dt = Mathf.Max(.0001f, now - previousAt);
            Check(step < Mathf.Max(.15f, dt * 2.5f), "Avatar root jumped between observed frames.");
            maximumRootStep = Mathf.Max(maximumRootStep, step); previousRoot = root; previousAt = now;
            maximumHeadDegrees = Mathf.Max(maximumHeadDegrees, Quaternion.Angle(initialHead, tracking.HeadFrameRotation));
            var attention = (NevaSceneAttention)Get(chat, "m_SceneAttention");
            if (attention != null) maximumAttentionDegrees = Mathf.Max(maximumAttentionDegrees, attention.CurrentHeadAngles.magnitude);
            if (now - lastSampleAt < .04f) return;
            lastSampleAt = now;
            ((JArray)LastReport["samples"]).Add(new JObject {
                ["realtime"] = now, ["frame"] = Time.frameCount, ["actionId"] = actionId,
                ["scenePhase"] = Fact(chat)["phase"], ["roomPhase"] = bridge.Phase, ["controllerStage"] = bridge.Controller.Stage,
                ["operation"] = bridge.Controller.OperationRevision, ["root"] = JsonValue(root),
                ["headRotation"] = JsonValue(tracking.HeadFrameRotation),
                ["headOwner"] = tracking.AttentionOwnerId, ["headAngles"] = attention != null ? JsonValue(attention.CurrentHeadAngles) : null,
                ["bodyReserved"] = bridge.ReservesBody, ["sceneActionActive"] = (bool)Get(chat, "m_SceneActionActive") });
        }
        public void Save()
        {
            LastReport["checks"] = checks; LastReport["elapsedSeconds"] = Time.realtimeSinceStartup - started;
            LastReport["maximumRootStep"] = maximumRootStep; LastReport["maximumHeadDegrees"] = maximumHeadDegrees;
            LastReport["maximumAttentionDegrees"] = maximumAttentionDegrees;
            LastReport["actualRootTravel"] = Horizontal(bridge.avatar.transform.position - initialRoot);
            LastReport["finalRoot"] = JsonValue(bridge.avatar.transform.position);
            LastReport["phases"] = new JArray(phases.Where(value => actionId == null || (string)value["actionId"] == actionId));
            LastReport["terminalEvents"] = new JArray(results.Where(value => value.actionId == actionId)
                .Select(value => JObject.Parse(JsonUtility.ToJson(value))));
            File.WriteAllText(Path.Combine(output, "report.json"), LastReport.ToString() + "\n");
        }
    }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Production approach regression only runs in Play in the existing isolated validation project.");
        if (chat == null || bridge == null || !bridge.IsConnected || model == null || bridge.avatar == null)
            throw new InvalidOperationException("Connected actual-room fixture, real avatar, and controlled transport are required.");
        var tracking = bridge.avatar.GetComponent<NevaEyeTracking>();
        if (tracking == null || !tracking.IsInitialized || !tracking.isActiveAndEnabled)
            throw new InvalidOperationException("Use actual edit-calibrated head/eyes, not a runtime invented neutral pose.");
        var c = new Context { chat = chat, bridge = bridge, model = model, tracking = tracking,
            output = Path.Combine(Path.GetFullPath(output), "scene-action-production"), started = Time.realtimeSinceStartup, previousAt = Time.realtimeSinceStartup,
            initialRoot = bridge.avatar.transform.position, previousRoot = bridge.avatar.transform.position,
            initialHead = tracking.HeadFrameRotation };
        Directory.CreateDirectory(c.output);
        LastReport = new JObject { ["status"] = "running", ["samples"] = new JArray(), ["serviceUrl"] = bridge.serviceUrl,
            ["initialRoot"] = JsonValue(c.initialRoot), ["modelHttpRequests"] = 0,
            ["scope"] = "One actual BeginAutonomyIntentProbe → controlled concrete observation motive → production room request → controlled approach-object decision → registered head look → fresh target confirmation → actual ARDY native arrival → fresh result capture. Only model replies are controlled; all dispatch/identity/navigation/body/result consumers are production code.",
            ["limitations"] = "Controlled pixel selector and motivation are test data, not proof of Qwen semantic understanding or spontaneous desire. Existing real room and avatar, no actor/user teleport, no scene creation. Formal request is captured but never completed, so no synthesized speech is sent. No stairs or long-duration motion-quality claim." };
        bridge.SceneTargetCompleted += c.Terminal;
        Application.logMessageReceived += c.Log;
        var stack = new Stack<IEnumerator>(); stack.Push(Exercise(c));
        try
        {
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
                    c.Sample();
                }
                catch (Exception error) { LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw; }
                if (stack.Count > 0) yield return current;
            }
            LastReport["status"] = "passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            Application.logMessageReceived -= c.Log;
            bridge.SceneTargetCompleted -= c.Terminal;
            c.Save();
        }
    }

    private static IEnumerator Exercise(Context c)
    {
        c.Check(!c.bridge.ReservesBody && !c.bridge.IsFollowing && !(bool)Get(c.chat, "m_SceneActionActive"),
            "The independent case requires the initial idle room fixture; never displace a running user task.");
        // Reuse the existing fixture's initial chat preparation only. No movement or result state is injected.
        typeof(SceneActionIntegrationRegression).GetMethod("ResetRound", Flags).Invoke(null, new object[] { c.chat });
        yield return Wait(c, () => IdleAttention(c.chat), 3f, "initial attention release");
        c.Check(c.bridge.avatar.GetComponent<Animator>()?.enabled == true, "The actual project idle Animator must be active.");
        int probes = c.model.Probes.Count, decisions = c.model.Decisions.Count, speech = c.model.Speech.Count;
        int initialOperation = c.bridge.Controller.OperationRevision;
        Call(c.chat, "BeginAutonomyIntentProbe", "scene-production-approach-controlled");
        yield return Wait(c, () => c.model.Probes.Count == probes + 1, 6f, "actual autonomous observation request");
        var probe = c.model.Probes.Last();
        var source = c.chat.SceneVisionCapture;
        var observation = source.LatestObservation;
        c.Check(observation.HasImage && observation.HasSurfaceGrounding && probe.Image == observation.ImageDataUrl &&
            probe.Input.Contains(observation.CaptureId), "Real autonomy wire must contain the exact captured RGB and capture identity.");
        c.sourceCaptureId = observation.CaptureId;
        LastReport["source"] = SaveCapture(c.output, "source", observation);
        File.WriteAllText(Path.Combine(c.output, "autonomy-request.json"), probe.Wire + "\n");
        SceneGroundedTarget selected = ChooseTarget(c, observation, out var plan, out int attempts);
        c.Check(selected != null && plan != null, "No supported visible surface inside head range has a legal >0.5 m room route; no authored coordinate was substituted.");
        LastReport["planningAttempts"] = attempts; LastReport["selectedSurfaceId"] = selected.SurfaceId;
        LastReport["selectedPixel"] = JsonValue(selected.ImagePoint); LastReport["selectedWorldPoint"] = JsonValue(selected.WorldPoint);
        LastReport["initialPlannedGround"] = JsonValue(plan.GroundTarget); LastReport["initialRouteLength"] = plan.Route.Length;
        const string intention = "我刚看到前方一处清楚的表面，想先转头确认，再走到它旁边的地面近些观察。";
        var probeReply = new JObject { ["proceed"] = true, ["work_evidence"] = "当前没有待答用户问题。", ["work_status"] = "none",
            ["intent"] = intention, ["novelty"] = "当前新视野提供了这处可见表面，尚未检查近处站位或实际靠近。", ["wait_seconds"] = 45 };
        File.WriteAllText(Path.Combine(c.output, "controlled-autonomy-reply.json"), probeReply + "\n");
        probe.Complete(probeReply.ToString());
        yield return Wait(c, () => c.model.Decisions.Count == decisions + 1, 6f, "autonomy intent to production room planner");
        var room = c.model.Decisions.Last(); var input = JObject.Parse(room.Input);
        c.Check((bool?)input["autonomous"] == true && (string)input["currentUserText"] == "" &&
            (string)input["autonomousIntention"] == intention, "The actual preprobe motive must reach room planning unchanged, not become a fabricated user command.");
        c.Check(room.Image == observation.ImageDataUrl && room.Input.Contains(observation.CaptureId), "Room planning must use the same source observation.");
        File.WriteAllText(Path.Combine(c.output, "room-input.json"), room.Input + "\n");
        File.WriteAllText(Path.Combine(c.output, "room-request.json"), room.Wire + "\n");
        var decision = new JObject { ["intent"] = "approach-object", ["origin"] = "autonomous", ["evidence"] = intention,
            ["visualTarget"] = new JObject { ["captureId"] = observation.CaptureId,
                ["x"] = selected.ImagePoint.x, ["y"] = selected.ImagePoint.y, ["label"] = "当前选中的可见表面" } };
        File.WriteAllText(Path.Combine(c.output, "controlled-room-decision.json"), decision + "\n");
        room.Complete(true, decision.ToString(), "controlled-actual-visible-approach");
        var initialFact = Fact(c.chat); c.actionId = (string)initialFact["actionId"];
        c.Check(!string.IsNullOrEmpty(c.actionId) && (string)initialFact["captureId"] == observation.CaptureId &&
            (string)initialFact["surfaceId"] == selected.SurfaceId && (string)initialFact["intent"] == "approach-object" &&
            (string)initialFact["origin"] == "autonomous" && (bool)Get(c.chat, "m_SceneActionActive"),
            "Actual room completion must register its own visual action identity.");
        var attention = (NevaSceneAttention)Get(c.chat, "m_SceneAttention");
        c.Check(attention != null && attention.OwnerId == c.actionId && c.tracking.AttentionOwnerId == c.actionId &&
            c.bridge.Controller.OperationRevision == initialOperation, "Physical head preparation owns the action before any native walking dispatch.");
        LastReport["actionId"] = c.actionId;

        SceneVisionObservation confirmed = null;
        yield return Wait(c, () => {
            var latest = source.LatestObservation; var fact = Fact(c.chat);
            if (latest.HasImage && latest.CaptureId != observation.CaptureId && (string)fact["confirmedCaptureId"] == latest.CaptureId) confirmed = latest;
            return c.model.Speech.Count == speech + 1;
        }, 14f, "actual looking, confirmation, native submission and formal capture");
        c.Check(confirmed != null && confirmed.CaptureId != observation.CaptureId && c.maximumAttentionDegrees > 3f,
            "Production preparation must physically turn the head and confirm the target in a fresh image.");
        LastReport["confirmed"] = SaveCapture(c.output, "confirmed", confirmed);
        var submission = Fact(c.chat); var formal = c.model.Speech.Last();
        c.Check((string)submission["actionId"] == c.actionId && (string)submission["confirmedCaptureId"] == confirmed.CaptureId &&
            (string)submission["phase"] == "submitted" && (bool?)submission["reached"] == false && c.bridge.ActiveTaskId == c.actionId &&
            c.bridge.TargetKind == "scene-object" && c.bridge.Controller.OperationRevision > initialOperation,
            "Formal dispatch must follow real native submission and must not announce a fabricated arrival.");
        c.Check(IdleAttention(c.chat) && string.IsNullOrEmpty(c.tracking.AttentionOwnerId), "Head ownership must release before native full-body execution.");
        c.Check(formal.Image == confirmed.ImageDataUrl && formal.Context.Contains(c.actionId) &&
            formal.Context.Contains(confirmed.CaptureId) && WireImages(formal.Wire).SequenceEqual(new[] { confirmed.ImageDataUrl }),
            "Submission-time formal request must carry the same action and focused observation only.");
        File.WriteAllText(Path.Combine(c.output, "formal-at-submission.json"), formal.Wire + "\n");
        LastReport["formalAtSubmissionFact"] = submission;
        // Do not complete formal output: the real body/result coroutine is independent of TTS.
        yield return Wait(c, () => c.results.Any(value => value.actionId == c.actionId), 35f, "actual native object arrival");
        var terminals = c.results.Where(value => value.actionId == c.actionId).ToArray();
        c.Check(terminals.Length == 1 && terminals[0].terminal && terminals[0].reached && terminals[0].status == "arrived" &&
            terminals[0].captureId == confirmed.CaptureId && terminals[0].targetId == selected.SurfaceId && terminals[0].targetKind == "scene-object",
            "The exact registered action must produce one actual native arrival with the confirmed capture/target identity.");
        c.Check(Horizontal(c.bridge.avatar.transform.position - c.initialRoot) > .45f && terminals[0].actualStandpointError <= c.bridge.Controller.ArrivalTolerance + .03f,
            "Actor must actually travel to the legal station; logical arrival alone is insufficient.");
        SceneVisionObservation resultImage = null;
        yield return Wait(c, () => {
            var fact = Fact(c.chat); var latest = source.LatestObservation;
            if ((string)fact["actionId"] == c.actionId && latest.HasImage && (string)fact["resultCaptureId"] == latest.CaptureId &&
                latest.CaptureId != confirmed.CaptureId && latest.CaptureId != observation.CaptureId) resultImage = latest;
            return resultImage != null && !c.bridge.ReservesBody;
        }, 6f, "production result observation and actual body release");
        var final = Fact(c.chat);
        c.Check((string)final["actionId"] == c.actionId && (string)final["phase"] == "arrived" && (bool?)final["reached"] == true &&
            (bool?)final["active"] == false && !(bool)Get(c.chat, "m_SceneActionActive"), "Production result consumer must close the exact scene action after actual arrival.");
        c.Check(c.bridge.avatar.GetComponent<Animator>()?.enabled == true && c.results.Count(value => value.actionId == c.actionId) == 1,
            "Actual Animator ownership and a single terminal result are retained.");
        c.Check(c.model.Probes.Count == probes + 1 && c.model.Decisions.Count == decisions + 1 && c.model.Speech.Count == speech + 1,
            "This bounded chain must not leak another model request or complete a spoken reply.");
        string[] phaseOrder = { "grounded", "looking", "confirmed", "submitted", "arrived" };
        int cursor = -1;
        foreach (string phase in phaseOrder)
        {
            int next = c.phases.FindIndex(cursor + 1, value => (string)value["actionId"] == c.actionId && (string)value["phase"] == phase);
            c.Check(next > cursor, "Missing or reordered production phase: " + phase); cursor = next;
        }
        LastReport["result"] = SaveCapture(c.output, "result", resultImage);
        LastReport["actualResultFact"] = final;
        LastReport["nativeTerminal"] = JObject.Parse(JsonUtility.ToJson(terminals[0]));
        LastReport["sourceToResultCaptureIdsDistinct"] = true;
        LastReport["formalWasCompleted"] = false;
        c.Save();
    }

    private static SceneGroundedTarget ChooseTarget(Context c, SceneVisionObservation observation,
        out ArdyRoomObjectApproachPlan selectedPlan, out int attempts)
    {
        selectedPlan = null; attempts = 0;
        Vector3 ground = c.bridge.avatar.transform.position + Vector3.up * c.bridge.importedReference.groundY * c.bridge.avatar.transform.lossyScale.y;
        var tried = new List<SceneGroundedTarget>();
        float[] xs = { .62f, .38f, .70f, .30f, .5f, .78f, .22f };
        float[] ys = { .55f, .42f, .65f, .32f, .74f };
        foreach (float y in ys) foreach (float x in xs)
        {
            if (!c.chat.SceneVisionCapture.TryResolve(observation, new Vector2(x, y), out var target, out _)) continue;
            if (target.WorldPoint.y < ground.y + .35f || target.WorldPoint.y > ground.y + 2.4f) continue;
            Vector3 offset = target.WorldPoint - c.tracking.EyeOrigin;
            Vector3 local = Quaternion.Inverse(c.tracking.HeadFrameRotation) * offset;
            float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
            float angle = new Vector2(yaw, pitch).magnitude;
            if (offset.magnitude < 1.1f || offset.magnitude > 5f || Mathf.Abs(yaw) >= 40f || pitch <= -22f || pitch >= 19f || angle <= 7f || angle >= 23f) continue;
            if (tried.Any(value => value.SurfaceId == target.SurfaceId && Vector3.Distance(value.WorldPoint, target.WorldPoint) < .45f)) continue;
            tried.Add(target);
            if (++attempts > 12) return null;
            if (!ArdyRoomObjectApproachPlanner.TryPlan(c.bridge.Navigation, ground, target.WorldPoint, c.bridge.userHead, .8f,
                out var plan, out _, out _)) continue;
            if (plan.Route.Length <= .5f || plan.Route.Length > 3.5f || Horizontal(plan.GroundTarget - ground) <= .45f) continue;
            if (!target.TryValidate(out _)) return null;
            selectedPlan = plan; return target;
        }
        return null;
    }
    private static IEnumerator Wait(Context c, Func<bool> ready, float seconds, string label)
    {
        float deadline = Time.realtimeSinceStartup + seconds;
        while (!ready())
        {
            c.Check(Time.realtimeSinceStartup < deadline, "Timeout: " + label + "; actual fact=" + Fact(c.chat));
            yield return null;
        }
    }
    private static JObject SaveCapture(string output, string name, SceneVisionObservation observation)
    {
        byte[] bytes = Convert.FromBase64String(observation.ImageDataUrl.Substring(observation.ImageDataUrl.IndexOf(',') + 1));
        File.WriteAllBytes(Path.Combine(output, name + ".jpg"), bytes);
        File.WriteAllText(Path.Combine(output, name + ".metadata.json"), observation.MetadataJson + "\n");
        string hash; using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        return new JObject { ["captureId"] = observation.CaptureId, ["image"] = name + ".jpg", ["metadata"] = name + ".metadata.json", ["sha256"] = hash };
    }
    private static IEnumerable<string> WireImages(string wire) => JObject.Parse(wire)["messages"].OfType<JObject>()
        .SelectMany(message => message["content"] is JArray parts ? parts.OfType<JObject>() : Enumerable.Empty<JObject>())
        .Where(part => (string)part["type"] == "image_url").Select(part => (string)part["image_url"]?["url"]);
    private static float Horizontal(Vector3 value) => new Vector2(value.x, value.z).magnitude;
    private static JObject JsonValue(object value) => JObject.Parse(JsonUtility.ToJson(value));
    private static JObject Fact(ChatSample chat)
        => Get(chat, "m_SceneActionFact") is object fact ? JObject.Parse(JsonUtility.ToJson(fact)) : new JObject();
    private static bool IdleAttention(ChatSample chat)
        => !((NevaSceneAttention)Get(chat, "m_SceneAttention") is NevaSceneAttention attention) || !attention.IsBusy;
    private static object Call(object target, string name, params object[] args) => NevaSceneVisionIntegrationRegression.Call(target, name, args);
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
}
