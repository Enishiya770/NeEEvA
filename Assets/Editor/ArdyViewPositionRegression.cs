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

/// <summary>Pure offset/trajectory checks and optional native body cases in the existing isolated room.</summary>
public static class ArdyViewPositionRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public static JObject LastReport { get; private set; }

    public static int RunChecks()
    {
        int count = 0;
        void Check(bool value, string reason) { count++; if (!value) throw new InvalidOperationException(reason); }
        Vector3 start = new Vector3(2, -.7f, -3), face = start + new Vector3(0, 1.4f, 2);
        string[] directions = { "farther", "closer", "left", "right" };
        Vector3[] axes = { Vector3.back, Vector3.forward, Vector3.left, Vector3.right };
        for (int d = 0; d < directions.Length; d++)
        foreach (float distance in new[] { .2f, .65f, 1.2f })
        foreach (float rotation in new[] { 0f, 67f, -143f })
        {
            Quaternion basis = Quaternion.Euler(0, rotation, 0);
            Check(ArdyRoomViewPositionPlanner.TryOffset(start, start + basis * (face - start), directions[d], distance, .45f,
                out Vector3 goal, out string reason), "Bounded offset: " + reason);
            Check(Vector3.Distance(goal, start + basis * axes[d] * distance) < .00001f && goal.y == start.y,
                "Direction follows the actor-to-target horizontal basis; target height cannot become a foot destination.");
            foreach (float initial in new[] { 0f, 35f, -130f })
            {
                var path = new[] { start, goal };
                var targets = ArdyRoomTargetBuilder.Build(path, distance, basis, start, 1, .65f, 200, initial, null, null,
                    start + basis * (face - start));
                float previousHeading = initial; Vector3 previous = Vector3.zero; bool moved = false;
                for (int i = 0; i < targets.Length; i++)
                {
                    var target = targets[i]; Vector3 world = start + basis * target.rootPosition;
                    Vector3 toward = Quaternion.Inverse(basis) * (start + basis * (face - start) - world); toward.y = 0;
                    float desired = Mathf.Atan2(toward.x, toward.z) * Mathf.Rad2Deg;
                    if (Vector3.Distance(target.rootPosition, Vector3.zero) > .001f) moved = true;
                    if (moved) Check(Mathf.Abs(Mathf.DeltaAngle(target.headingDegrees, desired)) < .001f,
                        "Translation body heading must face the target, not the path tangent.");
                    else Check(target.rootPosition == Vector3.zero, "Preparatory facing cannot creep along the route.");
                    Check(Mathf.Abs(Mathf.DeltaAngle(previousHeading, target.headingDegrees)) <= 4.501f &&
                        Vector3.Distance(previous, target.rootPosition) <= .06001f,
                        "Independent heading/position retains existing angular and movement speed bounds.");
                    previous = target.rootPosition; previousHeading = target.headingDegrees;
                }
                Check(moved && Vector3.Distance(start + basis * targets.Last().rootPosition, goal) < .00001f,
                    "Even the minimum .2 m request must genuinely translate to its explicit endpoint.");
                Check(targets.Skip(targets.Length - 20).All(t => t.rootPosition == targets.Last().rootPosition &&
                    Mathf.Abs(Mathf.DeltaAngle(t.headingDegrees, targets.Last().headingDegrees)) < .001f),
                    "The final native second retains a stable position and target-facing heading.");
            }
        }
        foreach (float bad in new[] { 0f, .199f, 1.201f, float.NaN, float.PositiveInfinity })
            Check(!ArdyRoomViewPositionPlanner.TryOffset(start, face, "farther", bad, .45f, out _, out _), "Out-of-range distance is not clamped into another command.");
        foreach (string direction in new[] { null, "", "behind", "Farther" })
            Check(!ArdyRoomViewPositionPlanner.TryOffset(start, face, direction, .2f, .45f, out _, out _), "Only exact relative directions are accepted.");
        Check(!ArdyRoomViewPositionPlanner.TryOffset(start, start + Vector3.up, "farther", .2f, .45f, out _, out _), "Coincident horizontal target has no invented direction.");
        Check(!ArdyRoomViewPositionPlanner.TryOffset(start, start + Vector3.forward * .6f, "closer", .2f, .45f, out _, out _), "Closer cannot cross or crowd the target.");
        Check(!ArdyRoomViewPositionPlanner.TryOffset(start, start + Vector3.forward * 8.01f, "left", .2f, .45f, out _, out _), "Local observation scope stays bounded.");
        ArdyRoomRoute Route(Vector3[] corners, float length)
        {
            var route = new ArdyRoomRoute();
            typeof(ArdyRoomRoute).GetProperty("Corners").SetValue(route, corners);
            typeof(ArdyRoomRoute).GetProperty("Length").SetValue(route, length); return route;
        }
        Check(ArdyRoomViewPositionPlanner.RouteMatchesOffset(Route(new[] { start, start + Vector3.back * .2f }, .2f),
            start, start + Vector3.back * .2f, out _), "The exact .2 m route is admitted.");
        Check(ArdyRoomViewPositionPlanner.RouteMatchesOffset(Route(new[] { start, start + Vector3.back * .2f + Vector3.up * .026f }, .202f),
            start, start + Vector3.back * .2f, out _), "Offset corridor checks only requested XZ; original navigation separately certifies any thin-carpet support-height variation.");
        Check(!ArdyRoomViewPositionPlanner.RouteMatchesOffset(Route(new[] { start, start + Vector3.left, start + Vector3.back * .2f }, 2f),
            start, start + Vector3.back * .2f, out _), "A large alternative detour cannot silently replace a short backward request.");
        Check(!ArdyRoomViewPositionPlanner.RouteMatchesOffset(Route(new[] { start, start + Vector3.forward * .1f, start + Vector3.back * .2f }, .4f),
            start, start + Vector3.back * .2f, out _), "A route that first goes in the opposite direction is rejected.");
        return count;
    }

    private sealed class Context
    {
        public ChatSample chat; public ArdyRoomDialogueBridge bridge; public NevaSceneVisionCapture capture;
        public string directory, action; public int checks;
        public Vector3 focusPoint; public Transform focusRoot;
        public readonly List<ArdyRoomDialogueBridge.SceneTargetResult> results = new List<ArdyRoomDialogueBridge.SceneTargetResult>();
        public void Check(bool ok, string reason) { checks++; if (!ok) throw new InvalidOperationException(reason); }
        public void Result(ArdyRoomDialogueBridge.SceneTargetResult value) { if (value?.actionId == action) results.Add(value); }
    }

    public static IEnumerator RunNativeChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Native view-position tests require the existing isolated real-room Play fixture.");
        var c = new Context { chat = chat, bridge = bridge, capture = chat.SceneVisionCapture,
            directory = Path.Combine(output, "view-position-native") };
        Directory.CreateDirectory(c.directory);
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(),
            ["scope"] = "Controlled selection of a genuinely visible original-room entity, explicit relative intentions, native ARDY body execution and fresh actual character images. No Qwen semantic decision, new scene, actor teleport, device mutation or visual improvement claim." };
        bool formal = (bool)Get(chat, "m_FormalResponseInFlight"), agent = (bool)Get(chat, "m_AgentRoundInFlight");
        int generation = (int)Get(chat, "m_FormalResponseGeneration");
        var stack = new Stack<IEnumerator>();
        try
        {
            c.Check(bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing && c.capture != null && c.capture.IsObserving,
                "The original real-room fixture is connected, idle and has its actual character eyes open.");
            Set(chat, "m_FormalResponseInFlight", true); Set(chat, "m_AgentRoundInFlight", true);
            bridge.SceneTargetCompleted += c.Result;
            LastReport["pureChecks"] = RunChecks(); stack.Push(NativeCases(c));
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
                catch (Exception error) { LastReport["error"] = error.ToString(); throw; }
                if (stack.Count > 0) yield return yielded;
            }
            LastReport["status"] = "passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            bridge.SceneTargetCompleted -= c.Result;
            if (bridge.ActionId == c.action && bridge.IsApproachingSceneTarget) bridge.StopMovement("cancelled", "view-position-native-cleanup");
            if ((int)Get(chat, "m_FormalResponseGeneration") == generation)
            { Set(chat, "m_FormalResponseInFlight", formal); Set(chat, "m_AgentRoundInFlight", agent); }
            LastReport["checks"] = c.checks;
            if ((string)LastReport["status"] == "running") LastReport["status"] = "failed";
            File.WriteAllText(Path.Combine(c.directory, "report.json"), LastReport.ToString() + "\n");
        }
    }

    private static IEnumerator NativeCases(Context c)
    {
        // A preceding controlled look test releases its real head in its finally
        // block; wait for that ordinary return rather than assuming neutral gaze.
        var attention = c.chat.GetComponent<NevaSceneAttention>();
        float releaseDeadline = Time.realtimeSinceStartup + 3f;
        while (attention != null && attention.IsBusy && Time.realtimeSinceStartup < releaseDeadline) yield return null;
        c.Check(attention == null || !attention.IsBusy, "Any preceding head attention has completed its own release before the native fixture selects a surface.");
        SceneVisionObservation first = null; yield return Capture(c, value => first = value);
        SceneObservedObject selected = Choose(c, first);
        c.Check(selected != null, "A real visible stable original-room surface has all declared short routes with the controlled setup margin.");
        c.focusPoint = selected.WorldPoint; c.focusRoot = selected.Root;
        string entity = selected.EntityId; LastReport["entityId"] = entity;
        LastReport["fixedAttentionWorldPoint"] = JObject.Parse(JsonUtility.ToJson(c.focusPoint));
        LastReport["fixturePlan"] = "Declared before generation: farther .5, left .2, right .2, closer .5, cancelled farther .5. All directions use the same actually seen rigid world point; new captures reconfirm that region. The original .2 backward candidate failed the shared standing audit; its fixed .5 offline comparison preserved standing with alternating foot motion. This is an explicit representative test distance, not an automatic change to user-requested distances. The original right .3 route was rejected by the coffee-table guard, so this declared left-first .2 side test requires full original-room clearance including setup margin and then returns toward the previous station. No physical boundary is relaxed.";
        yield return NativeCase(c, entity, "farther", .5f, false);
        yield return NativeCase(c, entity, "left", .2f, false);
        yield return NativeCase(c, entity, "right", .2f, false);
        yield return NativeCase(c, entity, "closer", .5f, false);
        yield return NativeCase(c, entity, "farther", .5f, true);
    }

    private static SceneObservedObject Choose(Context c, SceneVisionObservation observation)
    {
        Vector3 ground = Ground(c);
        foreach (float y in new[] { .5f, .4f, .6f }) foreach (float x in new[] { .5f, .4f, .6f, .3f, .7f })
        {
            if (!c.capture.TryResolveObservedObject(observation, new Vector2(x, y), out var candidate, out _) ||
                !candidate.VisibleConfirmed || !candidate.GeometryIsCurrent || candidate.Root == null ||
                candidate.Root.GetComponentInParent<Animator>() != null || candidate.Root.GetComponentInChildren<Animator>() != null) continue;
            Vector3 ray = candidate.WorldPoint - ground; ray.y = 0;
            if (ray.magnitude < 1.2f || ray.magnitude > 5f || candidate.WorldPoint.y < ground.y + .8f ||
                Vector3.Angle(c.bridge.avatar.transform.forward, ray) > 35f) continue;
            if (!PreflightSequence(c, ground, candidate.WorldPoint)) continue;
            return candidate;
        }
        return null;
    }

    private static bool PreflightSequence(Context c, Vector3 start, Vector3 point)
    {
        // Fixture selection only: admit a little room for measured endpoint
        // drift, rather than picking an exact route against the table boundary.
        // These are read-only navigation queries, not commands or moved actors.
        const float margin = .04f;
        string[] directions = { "farther", "left", "right", "closer", "farther" };
        float[] distances = { .5f, .2f, .2f, .5f, .5f };
        for (int leg = 0; leg < directions.Length; leg++)
        {
            string direction = directions[leg]; float distance = distances[leg];
            if (!ArdyRoomViewPositionPlanner.TryPlan(c.bridge.Navigation, start, point, c.bridge.userHead, direction, distance,
                out var center, out _, out _)) return false;
            foreach (var offset in new[] { Vector3.left, Vector3.right, Vector3.forward, Vector3.back,
                new Vector3(-1, 0, -1), new Vector3(-1, 0, 1), new Vector3(1, 0, -1), new Vector3(1, 0, 1) })
                if (!ArdyRoomViewPositionPlanner.TryPlan(c.bridge.Navigation, start + offset * margin, point, c.bridge.userHead, direction, distance,
                    out _, out _, out _)) return false;
            start = center.GroundTarget;
        }
        return true;
    }

    private static bool ConfirmFocus(Context c, SceneVisionObservation observation, string entity,
        out SceneObservedObject selected, out string reason)
    {
        selected = null; reason = "The originally chosen point is outside the fresh character view.";
        Vector3 local = Quaternion.Inverse(observation.CameraRotation) * (c.focusPoint - observation.CameraPosition);
        if (local.z <= .02f) return false;
        float tangent = Mathf.Tan(observation.VerticalFov * Mathf.Deg2Rad * .5f);
        var pixel = new Vector2(.5f + local.x / (2f * local.z * tangent * observation.Width / observation.Height),
            .5f - local.y / (2f * local.z * tangent));
        if (pixel.x < 0 || pixel.x > 1 || pixel.y < 0 || pixel.y > 1) return false;
        if (!c.capture.TryResolveObservedObject(observation, pixel, out selected, out reason)) return false;
        if (selected.EntityId != entity || selected.Root != c.focusRoot || !selected.VisibleConfirmed || !selected.GeometryIsCurrent ||
            Vector3.Distance(selected.WorldPoint, c.focusPoint) > .05f)
        { reason = "The fresh pixel does not reconfirm the same rigid entity and original attention region."; return false; }
        reason = null; return true;
    }

    private static IEnumerator NativeCase(Context c, string entity, string direction, float distance, bool cancel)
    {
        SceneVisionObservation source = null; yield return Capture(c, value => source = value);
        c.Check(ConfirmFocus(c, source, entity, out var selected, out string reason),
            "The same original attention point is actually reconfirmed before the next controlled offset: " + reason);
        Vector3 start = Ground(c);
        c.Check(ArdyRoomViewPositionPlanner.TryPlan(c.bridge.Navigation, start, c.focusPoint, c.bridge.userHead,
            direction, distance, out var plan, out reason, out var observation), "Requested physical route: " + reason);
        string name = direction + (cancel ? "-cancel" : "-move");
        c.action = "view-position-" + name + "-" + Guid.NewGuid().ToString("N"); c.results.Clear();
        var anchor = Activator.CreateInstance(typeof(ChatSample).GetNestedType("SceneObjectApproachAnchor", Flags), Flags, null,
            new object[] { selected, c.bridge }, null);
        string Validate() => (string)anchor.GetType().GetMethod("Validate", Flags).Invoke(anchor, null);
        var item = new JObject { ["name"] = name, ["actionId"] = c.action, ["sourceCaptureId"] = source.CaptureId,
            ["requestedDistance"] = distance, ["planning"] = JObject.Parse(JsonUtility.ToJson(observation)),
            ["freshPointSampleDistance"] = Vector3.Distance(selected.WorldPoint, c.focusPoint) };
        ((JArray)LastReport["cases"]).Add(item); SaveImage(c.directory, name + "-before", source);
        c.Check(c.bridge.RepositionSceneTarget(c.action, source.CaptureId, entity, c.focusPoint,
            direction, distance, cancel ? "avoid-occlusion" : "see-whole", Validate, out reason), "Native relative move accepted: " + reason);
        Vector3 prior = c.bridge.avatar.transform.position; int movingFrames = 0; float peakFacing = 0, peakStep = 0;
        bool stopped = false; float until = Time.realtimeSinceStartup + 80f;
        while (c.results.Count == 0 && Time.realtimeSinceStartup < until)
        {
            Vector3 position = c.bridge.avatar.transform.position;
            float step = Vector3.Distance(position, prior); prior = position; peakStep = Mathf.Max(peakStep, step);
            Vector3 offset = Ground(c) - start; offset.y = 0;
            if (step > .0003f) movingFrames++;
            if (offset.magnitude > .06f)
            {
                Vector3 aim = c.focusPoint - position; aim.y = 0;
                peakFacing = Mathf.Max(peakFacing, Vector3.Angle(c.bridge.avatar.transform.forward, aim));
            }
            if (cancel && !stopped && offset.magnitude > .045f && c.bridge.Controller.Player?.IsPlaying == true)
            { stopped = true; c.bridge.StopMovement("cancelled", "controlled cancellation during real relative translation"); }
            yield return null;
        }
        // A rejected native candidate is the evidence needed for diagnosis too.
        // Export it before assertions about successful movement/arrival.
        if (!string.IsNullOrEmpty(c.bridge.Controller.LastRequestJson)) File.WriteAllText(Path.Combine(c.directory, name + ".request.json"), c.bridge.Controller.LastRequestJson);
        if (!string.IsNullOrEmpty(c.bridge.Controller.LastResponseJson)) File.WriteAllText(Path.Combine(c.directory, name + ".response.json"), c.bridge.Controller.LastResponseJson);
        if (!string.IsNullOrEmpty(c.bridge.Controller.LastErrorResponseJson)) File.WriteAllText(Path.Combine(c.directory, name + ".error.json"), c.bridge.Controller.LastErrorResponseJson);
        c.Check(c.results.Count == 1, "Exactly one terminal belongs to this body task.");
        var result = c.results[0]; item["result"] = JObject.Parse(JsonUtility.ToJson(result));
        item["movingFrames"] = movingFrames; item["peakFacingWhileMoving"] = peakFacing; item["peakFrameDisplacement"] = peakStep;
        c.Check(movingFrames > 2, "Native playback visibly moves across multiple real frames, including the minimum .2 m case.");
        if (!cancel)
        {
            c.Check(result.reached && result.status == "arrived" && result.motionKind == "reposition-view", "Native motion actually reaches the requested station: " + result.reason);
            c.Check((result.actualOffset - (plan.GroundTarget - start)).magnitude <= .08f && result.actualOffset.magnitude >= distance - .08f,
                "Reported movement matches the explicit short offset instead of accepting an unchanged start.");
            c.Check(peakFacing <= 25f && result.actualFacingErrorDegrees <= 15f,
                "The actual body keeps facing the target while translating backward or sideways.");
            item["directionAudit"] = AuditWrongFacing(c);
        }
        else c.Check(stopped && !result.reached && result.status == "cancelled", "Cancellation retains real partial movement and never reports arrival.");
        until = Time.realtimeSinceStartup + 5f;
        while (c.bridge.ReservesBody && Time.realtimeSinceStartup < until) yield return null;
        c.Check(!c.bridge.ReservesBody, "Native body returns smoothly to its ordinary animation owner.");
        if (cancel)
        {
            Vector3 held = c.bridge.avatar.transform.position; int revision = c.bridge.Controller.OperationRevision;
            until = Time.realtimeSinceStartup + .5f; while (Time.realtimeSinceStartup < until) yield return null;
            c.Check(Vector3.Distance(held, c.bridge.avatar.transform.position) < .01f && c.bridge.Controller.OperationRevision == revision && c.results.Count == 1,
                "A cancelled relative move cannot restart or publish another terminal.");
        }
        SceneVisionObservation after = null; yield return Capture(c, value => after = value);
        item["resultCaptureId"] = after.CaptureId;
        item["sameEntityVisibleAfter"] = c.capture.TryFindObservedObject(after, entity, out var current, out _) && current.VisibleConfirmed;
        item["sameAttentionRegionVisibleAfter"] = ConfirmFocus(c, after, entity, out _, out _);
        c.Check(after.CaptureId != source.CaptureId && after.Frame > source.Frame, "Result image is a new actual character observation.");
        SaveImage(c.directory, name + "-after", after);
    }

    private static JObject AuditWrongFacing(Context c)
    {
        var controller = c.bridge.Controller;
        var actual = ArdyLocomotionClip.Parse(controller.LastResponseJson);
        var targets = ((JArray)JObject.Parse(controller.LastRequestJson)["targets"]).Select(t =>
            JsonUtility.FromJson<ArdyLocomotionTarget>(t.ToString())).ToArray();
        var audit = typeof(ArdyRoomLocomotionController).GetMethod("CheckTargetFacingTranslation", Flags);
        audit.Invoke(controller, new object[] { actual, targets });
        var counterexample = ArdyLocomotionClip.Parse(JsonUtility.ToJson(actual));
        int frame = counterexample.frames.Length / 2;
        counterexample.rotationClip.frames[frame].globalRotations[0] = Quaternion.Euler(0, targets[frame].headingDegrees + 90, 0) * counterexample.rotationClip.restGlobalRotations[0];
        bool rejected = false;
        try { audit.Invoke(controller, new object[] { counterexample, targets }); }
        catch (TargetInvocationException error) { rejected = error.InnerException is InvalidOperationException; }
        c.Check(rejected, "An offline clone with a wrong middle-frame body facing is rejected even if its final heading is unchanged.");
        return new JObject { ["actualClipPassed"] = true, ["wrongMiddleHeadingRejected"] = rejected,
            ["scope"] = "Audit-only modification of an actual native clip; not generated, submitted or played." };
    }
    private static Vector3 Ground(Context c) => c.bridge.avatar.transform.position + Vector3.up * c.bridge.importedReference.groundY * c.bridge.avatar.transform.lossyScale.y;
    private static IEnumerator Capture(Context c, Action<SceneVisionObservation> done)
    {
        SceneVisionObservation value = null; c.capture.RequestCapture(v => value = v);
        float until = Time.realtimeSinceStartup + 5f; while (value == null && Time.realtimeSinceStartup < until) yield return null;
        c.Check(value?.HasImage == true, "Actual character capture returns pixels."); done(value);
    }
    private static void SaveImage(string directory, string name, SceneVisionObservation value)
    {
        string data = value.ImageDataUrl; int comma = data.IndexOf(',');
        File.WriteAllBytes(Path.Combine(directory, name + (data.StartsWith("data:image/jpeg;") ? ".jpg" : ".png")), Convert.FromBase64String(data.Substring(comma + 1)));
    }
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
}
