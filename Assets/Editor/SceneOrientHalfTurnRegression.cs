using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Controlled signed intentions through the real bridge, native ARDY,
/// playback and character capture in the existing isolated room. No model decision.</summary>
public static class SceneOrientHalfTurnRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static JObject LastReport { get; private set; }
    private sealed class Context
    {
        public ChatSample chat; public ArdyRoomDialogueBridge bridge; public NevaSceneVisionCapture capture;
        public string directory, action; public int checks;
        public readonly Dictionary<string, List<ArdyRoomDialogueBridge.SceneOrientResult>> results =
            new Dictionary<string, List<ArdyRoomDialogueBridge.SceneOrientResult>>();
        public void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        public void OnResult(ArdyRoomDialogueBridge.SceneOrientResult value)
        {
            if (value == null || value.actionId != action) return;
            if (!results.TryGetValue(value.actionId, out var values)) results[value.actionId] = values = new List<ArdyRoomDialogueBridge.SceneOrientResult>();
            values.Add(value);
        }
    }

    public static int RunTargetChecks()
    {
        int count = 0;
        void Check(bool value, string message) { count++; if (!value) throw new InvalidOperationException(message); }
        foreach (float yaw in new[] { -180f, 180f })
        foreach (float initial in new[] { -8f, 0f, 8f })
        {
            // Both signed requests have the same final world direction. Native
            // constraints must nevertheless take the explicitly requested side.
            var targets = ArdyRoomTargetBuilder.Build(new[] { Vector3.zero }, 0, Quaternion.identity,
                Vector3.zero, 1, .65f, 200, initial, 180f, yaw);
            float previous = initial, travel = 0;
            foreach (var value in targets)
            {
                float delta = value.headingDegrees - previous;
                Check(delta * Mathf.Sign(yaw) >= -.0001f && Mathf.Abs(delta) <= 4.5001f,
                    "Signed half-turn target reverses direction or exceeds the existing 90 degree/second rate.");
                Check(value.rootPosition == Vector3.zero, "A signed half-turn target translates the actor.");
                travel += delta; previous = value.headingDegrees;
            }
            Check(travel * Mathf.Sign(yaw) > 150 && Mathf.Abs(Mathf.DeltaAngle(previous, 180f)) < .001f,
                "Signed native constraints do not reach the same rear-facing target via the requested side.");
        }
        foreach (float yaw in new[] { -90f, 90f })
        {
            var normal = ArdyRoomTargetBuilder.Build(new[] { Vector3.zero }, 0, Quaternion.identity, Vector3.zero, 1, .65f, 200, 0, yaw);
            var signed = ArdyRoomTargetBuilder.Build(new[] { Vector3.zero }, 0, Quaternion.identity, Vector3.zero, 1, .65f, 200, 0, yaw, yaw);
            Check(normal.Length == signed.Length && normal.Zip(signed, (a, b) =>
                a.rootPosition == b.rootPosition && a.headingDegrees == b.headingDegrees).All(x => x),
                "Signed small turns change the existing native target trajectory.");
        }
        foreach (float yaw in new[] { float.NaN, 0f, 181f, -181f })
        {
            bool rejected = false;
            try { ArdyRoomTargetBuilder.Build(new[] { Vector3.zero }, 0, Quaternion.identity, Vector3.zero, 1, .65f, 200, 0, 180, yaw); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid signed turn escaped target validation.");
        }
        bool translated = false;
        try { ArdyRoomTargetBuilder.Build(new[] { Vector3.zero, Vector3.forward }, 1, Quaternion.identity, Vector3.zero, 1, .65f, 200, 0, 180, 180); }
        catch (ArgumentException) { translated = true; }
        Check(translated, "Signed in-place override was accepted on a walking route.");
        return count;
    }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Half-turn regression requires the existing isolated real-room Play fixture.");
        var c = new Context { chat = chat, bridge = bridge, capture = chat.SceneVisionCapture,
            directory = Path.Combine(output, "scene-orient-half-turn") };
        Directory.CreateDirectory(c.directory);
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(),
            ["scope"] = "Explicit controlled +180/-180 body intentions and mid-motion cancellation through the production scene bridge, actual native ARDY service, real avatar playback and fresh character-camera images. No Qwen intent decision, speech, desktop capture or actor transform assignment. The last cancelled turn intentionally leaves the actual partial orientation in this disposable fixture.",
            ["limitations"] = "Three bounded controlled episodes do not prove free-language planning or general long-duration motion naturalness." };
        int generation = (int)Get(chat, "m_FormalResponseGeneration");
        bool formal = (bool)Get(chat, "m_FormalResponseInFlight"), agent = (bool)Get(chat, "m_AgentRoundInFlight");
        bool observing = c.capture != null && c.capture.IsObserving;
        var stack = new Stack<IEnumerator>();
        try
        {
            c.Check(bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing && !bridge.Controller.IsBusy,
                "The real room bridge must be connected and idle before signed body-turn tests.");
            c.Check(c.capture != null && c.capture.Target == bridge.avatar && c.capture.isActiveAndEnabled,
                "The fixture must provide the actual calibrated character camera.");
            Set(chat, "m_FormalResponseInFlight", true); Set(chat, "m_AgentRoundInFlight", true);
            c.capture.SetObserving(true); bridge.SceneOrientCompleted += c.OnResult;
            LastReport["targetChecks"] = RunTargetChecks();
            stack.Push(Cases(c));
            while (stack.Count > 0)
            {
                object yielded = null;
                try
                {
                    while (stack.Count > 0)
                    {
                        var routine = stack.Peek();
                        if (!routine.MoveNext()) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
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
            bridge.SceneOrientCompleted -= c.OnResult;
            if (!string.IsNullOrEmpty(c.action) && bridge.ActionId == c.action && bridge.IsOrientingScene)
                bridge.StopMovement("cancelled", "isolated-half-turn-regression-cleanup");
            if (c.capture != null) c.capture.SetObserving(observing);
            if ((int)Get(chat, "m_FormalResponseGeneration") == generation)
            { Set(chat, "m_FormalResponseInFlight", formal); Set(chat, "m_AgentRoundInFlight", agent); }
            LastReport["checks"] = c.checks;
            if ((string)LastReport["status"] == "running") LastReport["status"] = "failed";
            File.WriteAllText(Path.Combine(c.directory, "report.json"), LastReport.ToString() + "\n");
        }
    }

    private static IEnumerator Cases(Context c)
    {
        yield return Case(c, 180f, false, "right-half-turn");
        yield return Case(c, -180f, false, "left-half-turn");
        yield return Case(c, 180f, true, "cancel-right-half-turn");
    }

    private static IEnumerator Case(Context c, float yaw, bool cancel, string name)
    {
        var bridge = c.bridge; var avatar = bridge.avatar; var controller = bridge.Controller;
        var environment = bridge.environmentRoot; var user = bridge.userHead;
        SceneVisionObservation source = null;
        yield return Capture(c, value => source = value);
        SaveImage(c.directory, name + "-before", source);
        Vector3 start = avatar.transform.position, initialForward = avatar.transform.forward, previousForward = initialForward;
        float accumulated = 0, reverseTravel = 0, maximumDrift = 0, peakFrameYaw = 0, started = Time.realtimeSinceStartup;
        bool stopped = false; float stopYaw = 0; int sampled = 0, framesWithMotion = 0;
        c.action = "half-turn-" + name + "-" + Guid.NewGuid().ToString("N");
        var samples = new JArray();
        var item = new JObject { ["name"] = name, ["actionId"] = c.action, ["requestedYaw"] = yaw,
            ["sourceCaptureId"] = source.CaptureId, ["samples"] = samples };
        ((JArray)LastReport["cases"]).Add(item);
        string Validate() => bridge.avatar != avatar || bridge.environmentRoot != environment || bridge.userHead != user ||
            bridge.Controller != controller || c.capture == null || !c.capture.IsObserving || c.capture.Target != avatar ?
                "isolated half-turn owner changed" : null;
        c.Check(bridge.TryOrientScene(c.action, source.CaptureId, yaw, Validate, out string failure), "Native signed body turn submission: " + failure);
        int operation = controller.OperationRevision; float nextSample = 0;
        while (!c.results.ContainsKey(c.action) && Time.realtimeSinceStartup - started < 100f)
        {
            Vector3 forward = avatar.transform.forward;
            float step = Vector3.SignedAngle(previousForward, forward, Vector3.up); previousForward = forward;
            accumulated += step; reverseTravel += Mathf.Max(0, -step * Mathf.Sign(yaw));
            peakFrameYaw = Mathf.Max(peakFrameYaw, Mathf.Abs(step));
            maximumDrift = Mathf.Max(maximumDrift, Vector3.Distance(start, avatar.transform.position)); sampled++;
            if (Mathf.Abs(step) > .02f) framesWithMotion++;
            if (Time.realtimeSinceStartup >= nextSample)
            {
                samples.Add(new JObject { ["frame"] = Time.frameCount, ["seconds"] = Time.realtimeSinceStartup - started,
                    ["accumulatedYaw"] = accumulated, ["drift"] = Vector3.Distance(start, avatar.transform.position), ["phase"] = bridge.Phase });
                nextSample = Time.realtimeSinceStartup + .05f;
            }
            if (cancel && !stopped && accumulated * Mathf.Sign(yaw) >= 25f && controller.Player?.IsPlaying == true)
            {
                stopped = true; stopYaw = accumulated;
                bridge.StopMovement("cancelled", "controlled stop during actual half-turn playback");
            }
            yield return null;
        }
        c.Check(c.results.TryGetValue(c.action, out var results) && results.Count == 1, "The native turn must publish exactly one owned terminal result.");
        var terminal = results[0];
        // Include the final frame even when the bridge reported completion in LateUpdate.
        accumulated += Vector3.SignedAngle(previousForward, avatar.transform.forward, Vector3.up);
        float until = Time.realtimeSinceStartup + 5f;
        while (controller.Player?.HasPoseOwnership == true && Time.realtimeSinceStartup < until) yield return null;
        c.Check(controller.Player == null || !controller.Player.HasPoseOwnership, "The real body must return ownership to idle after the turn or stop.");
        item["terminal"] = JObject.Parse(JsonUtility.ToJson(terminal)); item["accumulatedYaw"] = accumulated;
        item["reverseTravel"] = reverseTravel; item["maximumDrift"] = maximumDrift; item["peakFrameYaw"] = peakFrameYaw;
        item["sampledFrames"] = sampled; item["movingFrames"] = framesWithMotion; item["elapsedSeconds"] = Time.realtimeSinceStartup - started;
        c.Check(sampled > 2 && framesWithMotion > 2 && maximumDrift <= .161f, "Actual frame samples must show gradual body motion within the unchanged in-place drift guard.");
        if (!cancel)
        {
            c.Check(terminal.oriented && terminal.status == "turned" && terminal.controllerOperationRevision == operation,
                "Native half-turn did not actually reach and validate the requested orientation: " + terminal.reason);
            c.Check(accumulated * Mathf.Sign(yaw) >= 150f && accumulated * Mathf.Sign(yaw) <= 210f && reverseTravel < 25f,
                "The actual avatar did not turn through the requested left/right half-revolution.");
            c.Check(Mathf.Abs(terminal.actualYaw - accumulated) < 3f, "Execution facts retain the measured signed half-turn instead of wrapping it to the opposite sign.");
            c.Check(Vector3.Angle(Quaternion.AngleAxis(yaw, Vector3.up) * initialForward, avatar.transform.forward) <= 15f,
                "Rendered body orientation misses the requested rear-facing direction.");
            item["offlineDirectionAudit"] = CheckDirectionCounterexamples(c, controller);
        }
        else
        {
            c.Check(stopped && !terminal.oriented && terminal.status == "cancelled" && stopYaw > 0 && stopYaw < 150,
                "Mid-turn cancellation must occur during actual motion, before the half-turn is complete.");
            Vector3 heldForward = avatar.transform.forward; int completedOperation = controller.OperationRevision;
            until = Time.realtimeSinceStartup + .75f; while (Time.realtimeSinceStartup < until) yield return null;
            c.Check(Vector3.Angle(heldForward, avatar.transform.forward) < 3f && !bridge.IsOrientingScene &&
                controller.OperationRevision == completedOperation && c.results[c.action].Count == 1,
                "A cancelled half-turn must not continue, dispatch another segment, or publish a second completion.");
        }
        SceneVisionObservation next = null; yield return Capture(c, value => next = value);
        c.Check(next.CaptureId != source.CaptureId && next.Frame > source.Frame, "Turn result needs a new actual character-camera observation.");
        item["resultCaptureId"] = next.CaptureId; item["actualCameraChangeDegrees"] = Quaternion.Angle(source.CameraRotation, next.CameraRotation);
        SaveImage(c.directory, name + "-after", next);
        if (!string.IsNullOrEmpty(controller.LastRequestJson)) File.WriteAllText(Path.Combine(c.directory, name + ".request.json"), controller.LastRequestJson);
    }

    private static JObject CheckDirectionCounterexamples(Context c, ArdyRoomLocomotionController controller)
    {
        var native = ArdyLocomotionClip.Parse(controller.LastResponseJson);
        var requested = (JArray)JObject.Parse(controller.LastRequestJson)["targets"];
        var targets = requested.Select(x => new ArdyLocomotionTarget { headingDegrees = (float)x["headingDegrees"] }).ToArray();
        var audit = typeof(ArdyRoomLocomotionController).GetMethod("CheckSignedInPlaceTurn", BindingFlags.Static | BindingFlags.NonPublic);
        var endpoint = typeof(ArdyRoomLocomotionController).GetMethod("CheckFinalHeading", Flags);
        c.Check(audit != null && endpoint != null && targets.Length == native.frames.Length,
            "Offline signed-direction audit uses the production checker and actual submitted native targets.");
        audit.Invoke(null, new object[] { native, targets });
        float planned = targets[targets.Length - 1].headingDegrees - targets[0].headingDegrees;
        var report = new JObject { ["scope"] = "Audit only: clones of this actual native clip retain all other data; only pelvis heading is replaced to construct opposite/multiple turns with the identical final facing. No counterexample is submitted, played, or claimed as a model result.",
            ["actualClipAccepted"] = true, ["requestedTurn"] = planned, ["counterexamples"] = new JArray() };
        float originalHeadingError = controller.LastHeadingError;
        try
        {
            foreach (float fabricatedTurn in new[] { planned - 360f * Mathf.Sign(planned), planned + 360f * Mathf.Sign(planned) })
            {
                var value = ArdyLocomotionClip.Parse(JsonUtility.ToJson(native));
                for (int i = 0; i < value.frames.Length; i++)
                {
                    float heading = targets[0].headingDegrees + fabricatedTurn * i / (value.frames.Length - 1);
                    value.rotationClip.frames[i].globalRotations[0] = Quaternion.AngleAxis(heading, Vector3.up) * value.rotationClip.restGlobalRotations[0];
                }
                value.Validate();
                endpoint.Invoke(controller, new object[] { value, value.frames.Length - 1 });
                bool rejected = false; string reason = null;
                try { audit.Invoke(null, new object[] { value, targets }); }
                catch (TargetInvocationException error)
                { rejected = error.InnerException is InvalidOperationException; reason = error.InnerException?.Message; }
                c.Check(rejected, "Identical final facing must not authorize an opposite-direction or extra-revolution half-turn.");
                ((JArray)report["counterexamples"]).Add(new JObject { ["actualTurnConstructedForAudit"] = fabricatedTurn,
                    ["sameFinalHeadingPassed"] = true, ["signedDirectionRejected"] = rejected, ["reason"] = reason });
            }
        }
        finally { Set(controller, "<LastHeadingError>k__BackingField", originalHeadingError); }
        return report;
    }

    private static IEnumerator Capture(Context c, Action<SceneVisionObservation> completed)
    {
        SceneVisionObservation result = null; c.capture.RequestCapture(value => result = value);
        float until = Time.realtimeSinceStartup + 5f;
        while (result == null && Time.realtimeSinceStartup < until) yield return null;
        c.Check(result?.HasImage == true, "The isolated character camera must return actual pixels."); completed(result);
    }
    private static void SaveImage(string directory, string name, SceneVisionObservation image)
    {
        string data = image.ImageDataUrl; int comma = data.IndexOf(',');
        string extension = data.StartsWith("data:image/jpeg;", StringComparison.Ordinal) ? ".jpg" : ".png";
        File.WriteAllBytes(Path.Combine(directory, name + extension), Convert.FromBase64String(data.Substring(comma + 1)));
    }
    private static object Get(object target, string field) => target.GetType().GetField(field, Flags).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Flags).SetValue(target, value);
}
