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
        foreach (float yaw in new[] { -180.01f, -180f, -125f, -50.01f, -50f, 50f, 50.01f, 125f, 180f, 180.01f })
        foreach (string intent in new[] { "orient-view", "turn-in-place" })
        {
            var proposal = new JObject { ["intent"] = intent, ["origin"] = "user", ["evidence"] = "controlled signed turn",
                ["viewChange"] = new JObject { ["captureId"] = "actual-capture-at-dispatch", ["yaw"] = yaw, ["pitch"] = 0 } };
            bool valid = RoomTaskProtocol.TryDecision(proposal.ToString(), "controlled signed turn", false, out var decision, out string reason);
            bool expected = Mathf.Abs(yaw) <= (intent == "orient-view" ? 50f : 180f);
            Check(valid == expected && (!valid || (decision.intent == intent && decision.viewChange.yaw == yaw)),
                "Planner angle boundaries must reject the wrong intent without clamping, changing sign or rewriting the action: " + intent + "/" + yaw + "/" + reason);
        }
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
            ["scope"] = "Explicit controlled +180/-180 body intentions, bounded intermediate RGB/ID captures, hidden known-object memory and cancellation boundaries through the production scene bridge, actual native ARDY service, real avatar playback and fresh character-camera images. No Qwen intent decision, speech, desktop capture or actor transform assignment. Temporary known-object geometry is placed only in this disposable fixture.",
            ["limitations"] = "Seven bounded controlled episodes do not prove free-language planning or general long-duration motion naturalness." };
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
        yield return SweepCase(c, "complete");
        yield return SweepCase(c, "cancel");
        yield return SweepCase(c, "generation-replaced");
        yield return SweepCase(c, "eyes-closed");
    }

    private static IEnumerator SweepCase(Context c, string boundary)
    {
        var chat = c.chat; var bridge = c.bridge; var controller = bridge.Controller;
        SceneVisionObservation source = null;
        int generation = (int)Get(chat, "m_FormalResponseGeneration");
        bool eyes = (bool)Get(chat, "m_AgentEyesOpen"), complete = false, invalidated = false;
        bool awareness = (bool)Get(chat, "m_EnableSceneObjectAwareness");
        var initialForward = bridge.avatar.transform.forward;
        var item = new JObject { ["name"] = "production-turn-sweep-" + boundary,
            ["scope"] = "Actual production BeginSceneViewChange, native 180 degree playback and character RGB captures. Controlled callback replay tests stale ownership; no model request." };
        ((JArray)LastReport["cases"]).Add(item);
        object lease = null; SceneVisionObservation late = null;
        GameObject panel = null; Material material = null; string knownId = null;
        JToken selectedBefore = null, stateBefore = null;
        float started = Time.realtimeSinceStartup;
        try
        {
            if (boundary == "complete")
            {
                Set(chat, "m_EnableSceneObjectAwareness", true); Call(chat, "EnsureSceneObjectCatalog");
                c.Check(c.capture.TryGetCurrentCameraPose(out Pose pose), "The native sweep known-object fixture needs the actual camera pose.");
                panel = GameObject.CreatePrimitive(PrimitiveType.Quad); panel.name = "Isolated sweep known object";
                panel.transform.SetParent(bridge.environmentRoot, false);
                panel.transform.SetPositionAndRotation(pose.position + pose.rotation *
                    (Quaternion.Euler(0, 35f, 0) * Vector3.forward), pose.rotation);
                panel.transform.localScale = new Vector3(.25f, .25f, 1);
                UnityEngine.Object.DestroyImmediate(panel.GetComponent<Collider>());
                material = new Material(Shader.Find("Unlit/Color")) { color = new Color(.9f, .25f, .1f, 1) };
                panel.GetComponent<Renderer>().sharedMaterial = material;
                var identity = panel.AddComponent<SceneObjectIdentity>(); identity.Configure("途中已见测试物件");
                knownId = identity.EntityId;
            }
            yield return Capture(c, value => source = value); item["sourceCaptureId"] = source.CaptureId;
            if (knownId != null)
            {
                c.Check(c.capture.TryFindObservedObject(source, knownId, out var seen, out string reason) && seen.VisibleConfirmed,
                    "The controlled known object must actually appear in the pre-turn RGB/ID frame: " + reason);
                Call(chat, "RememberSceneObject", seen, null);
                selectedBefore = ((JObject)Get(chat, "m_SelectedSceneObject"))?.DeepClone();
                stateBefore = ((JObject)Get(chat, "m_SceneObjectStateResult"))?.DeepClone();
                item["knownEntityId"] = knownId; SaveImage(c.directory, "sweep-native-known-before", source);
            }
            Call(chat, "BeginSceneViewChange", new RoomTaskProtocol.Decision {
                intent = "turn-in-place", origin = "user", evidence = "isolated actual turn-sweep boundary",
                viewChange = new RoomTaskProtocol.ViewChange { captureId = source.CaptureId, yaw = 180f, pitch = 0f }
            }, source, generation, (Action)(() => complete = true));
            lease = Get(chat, "m_SceneSweep");
            c.Check(lease != null && bridge.IsOrientingScene, "Production turn did not start its bounded sweep owner.");
            c.action = (string)Get(lease, "actionId"); item["actionId"] = c.action;
            while (Time.realtimeSinceStartup - started < 100f &&
                ((bool)Get(chat, "m_SceneActionActive") || bridge.IsOrientingScene))
            {
                var retained = (IList)Get(lease, "frames");
                if (retained.Count > 0 && late == null)
                    late = (SceneVisionObservation)Get(retained[0], "observation");
                float travel = Vector3.Angle(initialForward, bridge.avatar.transform.forward);
                if (boundary != "complete" && !invalidated && late != null && travel >= 55f && controller.Player?.IsPlaying == true)
                {
                    invalidated = true; item["invalidatedAtYaw"] = travel;
                    if (boundary == "cancel") Call(chat, "CancelSceneAction", "isolated-sweep-cancel");
                    else if (boundary == "generation-replaced") Set(chat, "m_FormalResponseGeneration", generation + 1);
                    else Set(chat, "m_AgentEyesOpen", false);
                }
                yield return null;
            }
            c.Check(!(bool)Get(chat, "m_SceneActionActive") && !bridge.IsOrientingScene,
                "Sweep boundary retained a live body/action owner.");
            float until = Time.realtimeSinceStartup + 5f;
            while (controller.Player?.HasPoseOwnership == true && Time.realtimeSinceStartup < until) yield return null;
            c.Check(controller.Player == null || !controller.Player.HasPoseOwnership,
                "Sweep did not return native pose ownership after the terminal result.");
            if (boundary == "complete")
            {
                var terminal = (SceneVisionObservation)Get(lease, "terminal");
                c.Check(complete && (bool)Get(lease, "completed") && terminal?.HasImage == true,
                    "The real completed turn did not retain a fresh terminal image.");
                var context = (JObject)Call(chat, "BuildSceneSweepContext", terminal);
                var images = (IReadOnlyList<SceneVisionObservation>)Call(chat, "GetSceneSweepImages", terminal);
                c.Check(images.Count >= 2 && images.Count <= 4 && (bool)context["available"],
                    "Native playback did not acquire bounded real intermediate images.");
                c.Check(context["currentCaptureId"].ToString() == terminal.CaptureId &&
                    ((JArray)context["frames"]).Count == images.Count, "Historical/current image mapping is inconsistent.");
                float previous = -1;
                for (int i = 0; i < images.Count; i++)
                {
                    var image = images[i]; var frame = (JObject)context["frames"][i];
                    c.Check(image.HasImage && image.CaptureId != terminal.CaptureId && image.Frame > source.Frame &&
                        image.Frame < terminal.Frame && image.CapturedAtRealtime < terminal.CapturedAtRealtime,
                        "A sweep image is not an actual intermediate native-motion frame.");
                    if (previous >= 0) c.Check(image.CapturedAtRealtime - previous >= .48f,
                        "Sweep exceeded its two captures/second time bound.");
                    previous = image.CapturedAtRealtime;
                    c.Check((string)frame["visualObservation"]["captureId"] == image.CaptureId &&
                        (string)frame["imageRole"] == "historical-turn-sweep", "Sweep metadata does not identify its exact historical pixels.");
                    SaveImage(c.directory, "sweep-native-middle-" + i, image);
                }
                Set(chat, "m_FormalResponseGeneration", generation + 1);
                c.Check(((IReadOnlyList<SceneVisionObservation>)Call(chat, "GetSceneSweepImages", terminal)).Count == images.Count,
                    "The next planner generation lost a valid completed sweep.");
                Set(chat, "m_FormalResponseGeneration", generation);
                c.Check(((IReadOnlyList<SceneVisionObservation>)Call(chat, "GetSceneSweepImages", source)).Count == 0,
                    "A pre-turn current image was combined with later historical samples.");
                item["sweepContext"] = context; item["intermediateCaptures"] = images.Count;
                item["terminalCaptureId"] = terminal.CaptureId; SaveImage(c.directory, "sweep-native-terminal", terminal);
                c.Check(!c.capture.TryFindObservedObject(terminal, knownId, out _, out _),
                    "The known fixture remains visible at the completed rear-facing view; the memory scenario is not exercised.");
                var confirmations = ((JArray)context["frames"]).OfType<JObject>()
                    .SelectMany(frame => ((JArray)frame["knownEntitiesObservedInThisFrame"]).OfType<JObject>())
                    .Where(value => (string)value["entityId"] == knownId).ToArray();
                c.Check(confirmations.Length > 0, "Actual intermediate turn pixels did not refresh the known object's memory.");
                var memory = (JObject)Call(chat, "BuildSceneSpatialMemory", terminal);
                var remembered = ((JArray)memory["objects"]).OfType<JObject>().Single(value => (string)value["entityId"] == knownId);
                c.Check(confirmations.Any(value => (string)value["captureId"] == (string)remembered["captureId"]) &&
                    (string)remembered["captureId"] != source.CaptureId && (string)remembered["captureId"] != terminal.CaptureId,
                    "Hidden-object memory did not retain its genuine intermediate capture.");
                c.Check((bool)remembered["positionMemoryValid"] && remembered["relativeToCurrentView"] is JObject &&
                    remembered["relativeToCurrentBody"] is JObject && (bool)remembered["requiresReobservation"] &&
                    !(bool)remembered["actionAuthorization"] && (string)remembered["currentVisibility"] == "not-established",
                    "Intermediate memory cannot supply a valid geometric search bearing without granting current visibility or action.");
                c.Check(JToken.DeepEquals(selectedBefore, (JObject)Get(chat, "m_SelectedSceneObject")) &&
                    JToken.DeepEquals(stateBefore, (JObject)Get(chat, "m_SceneObjectStateResult")),
                    "Turn sampling replaced object selection or device state evidence.");
                item["hiddenKnownObjectMemory"] = remembered.DeepClone(); item["selectedAndDeviceResultPreserved"] = true;
            }
            else
            {
                c.Check(invalidated && late != null && !complete, "The controlled stale boundary did not interrupt actual sampled motion.");
                c.Check(Get(chat, "m_SceneSweep") == null && ((IList)Get(lease, "frames")).Count == 0,
                    "Cancelled/replaced/closed sweep retained historical image delivery.");
                // Replay a genuine captured intermediate image after owner invalidation.
                // It meets the frame/time checks and must be fenced before memory updates.
                int operation = controller.OperationRevision;
                int viewSamples = ((IList)Get(chat, "m_SceneSpatialViewSamples")).Count;
                Call(chat, "AcceptSceneSweepFrame", lease, late, late.CapturedAtRealtime - .001f, 55f);
                c.Check(Get(chat, "m_SceneSweep") == null && ((IList)Get(lease, "frames")).Count == 0 &&
                    controller.OperationRevision == operation && !bridge.IsOrientingScene &&
                    ((IList)Get(chat, "m_SceneSpatialViewSamples")).Count == viewSamples,
                    "A late capture callback revived stale sweep memory/images or a body operation.");
                item["lateCallbackDiscarded"] = true;
            }
            item["elapsedSeconds"] = Time.realtimeSinceStartup - started;
        }
        finally
        {
            Set(chat, "m_AgentEyesOpen", eyes); Set(chat, "m_FormalResponseGeneration", generation);
            Call(chat, "CancelSceneAction", "isolated-sweep-case-cleanup");
            if (panel != null) UnityEngine.Object.DestroyImmediate(panel);
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
            Set(chat, "m_EnableSceneObjectAwareness", awareness);
            c.capture.SetObserving(true);
        }
    }

    private static IEnumerator Case(Context c, float yaw, bool cancel, string name)
    {
        var bridge = c.bridge; var avatar = bridge.avatar; var controller = bridge.Controller;
        var environment = bridge.environmentRoot; var user = bridge.userHead;
        SceneVisionObservation source = null;
        yield return Capture(c, value => source = value);
        SaveImage(c.directory, name + "-before", source);
        if (name == "right-half-turn") ExportPlannerTurnCases(c, source);
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

    private static void ExportPlannerTurnCases(Context c, SceneVisionObservation source)
    {
        string directory = Path.Combine(c.directory, "planner-turn-preconditions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "image-data-url.txt"), source.ImageDataUrl);
        var cases = new JArray();
        foreach (var fixture in new[] {
            new { id = "left-body-125", request = "请原地向左转125度，看看那边。", intent = "turn-in-place", yaw = -125 },
            new { id = "right-body-125", request = "请原地向右转125度，看看那边。", intent = "turn-in-place", yaw = 125 },
            new { id = "left-head-45", request = "请只把视线向左转45度，不要转动身体。", intent = "orient-view", yaw = -45 }
        })
        {
            string input = (string)Call(c.chat, "BuildRoomDecisionInputWithObservation", fixture.request, false, "", c.bridge, source);
            var parsed = JObject.Parse(input);
            c.Check((string)parsed["visualObservation"]["captureId"] == source.CaptureId &&
                (string)parsed["currentUserText"] == fixture.request && (bool?)parsed["autonomous"] == false,
                "Turn replay preserves the exact production context, controlled request and actual current character capture.");
            File.WriteAllText(Path.Combine(directory, fixture.id + ".input.json"), input);
            cases.Add(new JObject { ["id"] = fixture.id, ["input"] = fixture.id + ".input.json",
                ["image"] = "image-data-url.txt", ["expectedIntent"] = fixture.intent,
                ["expectedYaw"] = fixture.yaw, ["expectedPitch"] = 0, ["captureId"] = source.CaptureId });
        }
        File.WriteAllText(Path.Combine(directory, "manifest.json"), new JObject { ["cases"] = cases,
            ["scope"] = "Frozen actual Unity character RGB and unmodified production decision inputs for controlled user requests. Export only; no model request or proposed action dispatch." }.ToString() + "\n");
        LastReport["plannerTurnReplayCases"] = cases.Count;
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
    private static object Call(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, Flags).Invoke(target, args);
}
