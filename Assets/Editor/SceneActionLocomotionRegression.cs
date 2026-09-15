using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NeEEvA.Motion;
using NeEEvA.Perception;
using UnityEngine;

/// <summary>
/// Embedded isolated-Play verification of controlled, pixel-grounded intentions and native ARDY execution.
/// The caller owns scene/VRM/chat setup and its ordinary coroutine runner. This script never opens a scene,
/// launches services, teleports the actor, fabricates completion, or sends an image/model request.
/// </summary>
public static class SceneActionLocomotionRegression
{
    [Serializable] private sealed class Sample
    {
        public string testCase, actionId, phase, stage;
        public int frame, operation;
        public float realtime;
        public Vector3 root;
        public bool bodyReserved, playing, smoothStopping;
    }
    [Serializable] private sealed class CaseResult
    {
        public string name, captureId, surfaceId, confirmationCaptureId, confirmationReason;
        public Vector2 imagePoint;
        public Vector3 visibleSurfacePoint, plannedGround, initialRoot, finalRoot;
        public float plannedRouteLength, actualRootTravel, elapsedSeconds, stopRootDrift;
        public bool moved, newViewConfirmsSurface, smoothStopObserved, returnedToAnimator;
        public int planningAttempts, sampledFrames, terminalEventCount;
        public ArdyRoomDialogueBridge.SceneTargetResult terminal;
    }
    [Serializable] private sealed class NavigationReset
    {
        public string beforeCase, state, reason;
        public Vector3 requestedGround, requestedFacePoint, initialRoot, finalRoot;
        public int operation;
        public float routeLength, actualRootTravel, elapsedSeconds, endpointError, facingError;
        public bool reached, returnedToAnimator;
        public string scope = "Controlled fixture return to the initial recorded legal station and orientation using actual native ARDY/controller movement and live user clearance. This is test setup, not a visually chosen autonomous intention or an actor teleport.";
    }
    [Serializable] private sealed class Report
    {
        public string status = "running", error, graphics, unityVersion, serviceUrl;
        public string scope = "Controlled intentions selected from actual fresh character RGB pixels and companion depth, real same-level room navigation, native ARDY HTTP/whole-body playback, actual terminal events, explicit owned stop and real target-transform invalidation. Two distinct visible surfaces are required for arrival tests.";
        public string limitations = "No Qwen decision, ASR/TTS, stairs, authored teleport, semantic object-label accuracy or long-duration naturalness claim. Pixel scanning is a test selector only and is not the production autonomous intention policy. Separate native navigationReset legs physically return to the recorded initial station between independent scenarios. Stop uses the production task/operation API.";
        public int checks, frames;
        public float elapsedSeconds, maximumRootStep;
        public List<CaseResult> cases = new List<CaseResult>();
        public List<NavigationReset> navigationResets = new List<NavigationReset>();
        public List<Sample> samples = new List<Sample>();
    }
    private sealed class Context
    {
        public ChatSample chat;
        public ArdyRoomDialogueBridge bridge;
        public NevaSceneVisionCapture source;
        public string output, prefix = "scene-native-" + Guid.NewGuid().ToString("N");
        public Report report;
        public CaseResult activeCase;
        public string resetCase = "";
        public string activeAction = "";
        public Vector3 previousRoot, initialGround, initialForward;
        public float startedAt, previousAt, lastSampleAt = float.NegativeInfinity;
        public readonly Dictionary<string, List<ArdyRoomDialogueBridge.SceneTargetResult>> results =
            new Dictionary<string, List<ArdyRoomDialogueBridge.SceneTargetResult>>();
        public void OnResult(ArdyRoomDialogueBridge.SceneTargetResult value)
        {
            if (value == null || !value.actionId.StartsWith(prefix, StringComparison.Ordinal)) return;
            if (!results.TryGetValue(value.actionId, out var values)) results[value.actionId] = values = new List<ArdyRoomDialogueBridge.SceneTargetResult>();
            values.Add(value);
        }
        public void Check(bool valid, string message)
        { report.checks++; if (!valid) throw new InvalidOperationException(message); }
        public void Sample()
        {
            float now = Time.realtimeSinceStartup;
            Vector3 root = bridge.avatar.transform.position;
            float step = Vector3.Distance(root, previousRoot), dt = Mathf.Max(.0001f, now - previousAt);
            report.maximumRootStep = Mathf.Max(report.maximumRootStep, step);
            Check(step < Mathf.Max(.15f, dt * 2.5f), "Native object approach jumped the avatar root between observed rendered frames.");
            previousRoot = root; previousAt = now; report.frames++;
            if (activeCase != null)
            {
                activeCase.sampledFrames++;
                if (Horizontal(root - activeCase.initialRoot) > .1f) activeCase.moved = true;
            }
            if (now - lastSampleAt < .05f || report.samples.Count >= 5000) return;
            lastSampleAt = now;
            var player = bridge.Controller.Player;
            report.samples.Add(new Sample { testCase = string.IsNullOrEmpty(resetCase) ? activeCase?.name : "navigation-reset-before-" + resetCase, actionId = activeAction,
                phase = bridge.Phase, stage = bridge.Controller.Stage, frame = Time.frameCount,
                operation = bridge.Controller.OperationRevision, realtime = now, root = root,
                bodyReserved = bridge.ReservesBody, playing = player != null && player.IsPlaying,
                smoothStopping = player != null && player.IsStoppingSmoothly });
        }
    }

    /// <summary>Requires isolated Play, a connected bridge, open calibrated scene eyes, and the existing ARDY endpoint.</summary>
    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Native scene action regression requires Play in the existing isolated validation project.");
        if (chat == null || bridge == null || !bridge.IsConnected || bridge.chat != chat || bridge.avatar == null ||
            chat.SceneVisionCapture == null || !chat.SceneVisionCapture.IsObserving)
            throw new InvalidOperationException("Provide connected isolated room/chat/avatar and an observing calibrated scene capture.");
        Directory.CreateDirectory(output);
        var context = new Context { chat = chat, bridge = bridge, source = chat.SceneVisionCapture,
            output = Path.GetFullPath(output), report = new Report { graphics = SystemInfo.graphicsDeviceType.ToString(),
                unityVersion = Application.unityVersion, serviceUrl = bridge.serviceUrl },
            previousRoot = bridge.avatar.transform.position, startedAt = Time.realtimeSinceStartup, previousAt = Time.realtimeSinceStartup };
        bridge.SceneTargetCompleted += context.OnResult;
        var stack = new Stack<IEnumerator>(); stack.Push(Exercise(context));
        try
        {
            while (stack.Count > 0)
            {
                object yielded = null;
                try
                {
                    context.Check(Time.realtimeSinceStartup - context.startedAt < 220f, "Native object-action regression exceeded its bounded runtime.");
                    while (stack.Count > 0)
                    {
                        var routine = stack.Peek();
                        if (!routine.MoveNext()) { (routine as IDisposable)?.Dispose(); stack.Pop(); continue; }
                        if (routine.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        yielded = routine.Current; break;
                    }
                    context.Sample();
                }
                catch (Exception error)
                {
                    context.report.status = "failed"; context.report.error = error.ToString();
                    throw;
                }
                if (stack.Count > 0) yield return yielded;
            }
            context.report.status = "passed";
        }
        finally
        {
            // Disposing the nested case restores the one temporary isolated target transform.
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            if (bridge != null)
            {
                if (bridge.ActiveTaskId.StartsWith(context.prefix, StringComparison.Ordinal))
                    bridge.StopForTaskIfCurrent(context.prefix + "-cleanup", bridge.ActiveTaskId, bridge.Controller.OperationRevision, out _);
                bridge.SceneTargetCompleted -= context.OnResult;
            }
            context.report.elapsedSeconds = Time.realtimeSinceStartup - context.startedAt;
            File.WriteAllText(Path.Combine(context.output, "native-object-actions.json"), JsonUtility.ToJson(context.report, true));
        }
    }

    private static IEnumerator Exercise(Context context)
    {
        context.Check(!context.bridge.IsFollowing && !context.bridge.ReservesBody, "Existing room/body task must be released before controlled native object actions.");
        Vector3 actualGround = AvatarGround(context.bridge);
        context.Check(context.bridge.Navigation.TryValidatePlanStart(actualGround, out context.initialGround,
            out string reason, out _), "Record initial legal fixture station: " + reason);
        context.initialForward = context.bridge.avatar.transform.forward; context.initialForward.y = 0;
        context.Check(context.initialForward.sqrMagnitude > .5f, "Record actual initial horizontal facing.");
        context.initialForward.Normalize();
        yield return RunCase(context, "arrival-first", null, "arrival");
        string firstSurface = context.report.cases[0].surfaceId;
        yield return ReturnToInitialStation(context, "arrival-second-distinct-surface");
        yield return RunCase(context, "arrival-second-distinct-surface", firstSurface, "arrival");
        context.Check(context.report.cases[0].surfaceId != context.report.cases[1].surfaceId, "The two actual arrivals must address distinct observed surfaces.");
        yield return ReturnToInitialStation(context, "owned-stop-while-moving");
        yield return RunCase(context, "owned-stop-while-moving", null, "stop");
        yield return ReturnToInitialStation(context, "target-geometry-changed-while-moving");
        yield return RunCase(context, "target-geometry-changed-while-moving", null, "invalidate");
    }

    private static IEnumerator ReturnToInitialStation(Context context, string nextCase)
    {
        var bridge = context.bridge;
        var controller = bridge.Controller;
        yield return Wait(context, () => !bridge.ReservesBody && !controller.IsBusy && !controller.IsMoving, 5f,
            "release before physical fixture return");
        context.Check(!bridge.IsFollowing && !bridge.IsApproachingSceneTarget && string.IsNullOrEmpty(bridge.ActiveTaskId),
            "Fixture return must not replace an active intention.");
        context.activeCase = null; context.resetCase = nextCase;
        context.activeAction = context.prefix + "-fixture-return-" + nextCase;
        var record = new NavigationReset { beforeCase = nextCase, requestedGround = context.initialGround,
            requestedFacePoint = context.initialGround + context.initialForward * 2f,
            initialRoot = bridge.avatar.transform.position };
        context.report.navigationResets.Add(record);
        float started = Time.realtimeSinceStartup;
        var userAnchor = bridge.userHead;
        float userRadius = bridge.Navigation.AgentRadius + ArdyRoomApproachPlanner.UserBodyRadius + ArdyRoomApproachPlanner.UserClearance;
        context.Check(bridge.Navigation.TryPlan(AvatarGround(bridge), context.initialGround,
            out var route, out string failure), "Physical fixture return route: " + failure);
        context.Check(ArdyRoomApproachPlanner.TryGetUserGround(bridge.Navigation, userAnchor, out var userGround,
            out failure), "Physical fixture return user support: " + failure);
        context.Check(Mathf.Abs(userGround.y - route.GroundY) <= bridge.Navigation.MaxRouteHeightVariation &&
            ArdyRoomApproachPlanner.RouteKeepsUserClearance(route, userGround, userRadius),
            "Physical fixture return must keep the actual user's same-floor body clearance.");
        record.routeLength = route.Length;
        var previousGuard = controller.AdditionalStepGuard;
        Func<Vector3, Vector3, string> resetGuard = (from, to) => {
            if (!bridge.IsConnected || bridge.userHead != userAnchor)
                return "Fixture return room/user binding changed.";
            if (!ArdyRoomApproachPlanner.TryGetUserGround(bridge.Navigation, userAnchor, out var currentGround, out string reason)) return reason;
            if (Mathf.Abs(currentGround.y - route.GroundY) > bridge.Navigation.MaxRouteHeightVariation)
                return "Fixture return user changed floors.";
            if (!ArdyRoomApproachPlanner.StepKeepsUserClearance(from, to, currentGround, userRadius))
                return "Fixture return user entered root-step clearance.";
            return previousGuard?.Invoke(from, to);
        };
        controller.AdditionalStepGuard = resetGuard;
        int operation = -1;
        try
        {
            controller.MoveTo(context.initialGround, record.requestedFacePoint);
            operation = record.operation = controller.OperationRevision;
            yield return Wait(context, () => controller.OperationRevision != operation ||
                controller.ExecutionState == ArdyRoomExecutionState.Arrived || controller.ExecutionState == ArdyRoomExecutionState.Blocked ||
                controller.ExecutionState == ArdyRoomExecutionState.Failed || controller.ExecutionState == ArdyRoomExecutionState.Cancelled,
                45f, "actual native fixture return");
            record.state = controller.ExecutionState.ToString(); record.reason = controller.Status;
            context.Check(controller.OperationRevision == operation && controller.ExecutionState == ArdyRoomExecutionState.Arrived,
                "Native fixture return did not actually arrive: " + controller.Status);
            record.endpointError = Vector3.Distance(AvatarGround(bridge), context.initialGround);
            Vector3 forward = bridge.avatar.transform.forward; forward.y = 0;
            record.facingError = Vector3.Angle(forward, context.initialForward);
            context.Check(record.endpointError <= controller.ArrivalTolerance + .03f && record.facingError < 15f,
                "Physical fixture return did not restore the measured station/facing within native tolerances.");
            record.reached = true;
            yield return Wait(context, () => !controller.IsBusy && !controller.IsMoving &&
                (controller.Player == null || !controller.Player.HasPoseOwnership), 5f, "fixture return Animator release");
            record.returnedToAnimator = bridge.avatar.GetComponent<Animator>()?.enabled == true;
            context.Check(record.returnedToAnimator, "Native fixture return did not restore real Animator ownership.");
            // Give the calibrated head view its ordinary frame updates after the actual whole-body turn.
            yield return null;
            yield return null;
        }
        finally
        {
            record.finalRoot = bridge.avatar.transform.position;
            record.actualRootTravel = Horizontal(record.finalRoot - record.initialRoot);
            record.elapsedSeconds = Time.realtimeSinceStartup - started;
            if (operation >= 0 && controller.OperationRevision == operation &&
                (controller.IsBusy || controller.IsMoving) && controller.ExecutionState != ArdyRoomExecutionState.Arrived)
                controller.Cancel();
            if (controller.AdditionalStepGuard == resetGuard) controller.AdditionalStepGuard = previousGuard;
            context.resetCase = "";
        }
    }

    private static IEnumerator RunCase(Context context, string name, string excludedSurface, string kind)
    {
        var bridge = context.bridge;
        yield return Wait(context, () => !bridge.ReservesBody && !bridge.Controller.IsBusy && !bridge.Controller.IsMoving, 5f, "release before " + name);
        SceneVisionObservation observation = null;
        context.source.RequestCapture(value => observation = value);
        yield return Wait(context, () => observation != null, 5f, "fresh RGB/depth for " + name);
        context.Check(observation.HasImage && observation.HasSurfaceGrounding, "Fresh captured pixels/depth unavailable: " + observation?.Reason + " " + observation?.SurfaceGroundingReason);
        var item = new CaseResult { name = name, captureId = observation.CaptureId, initialRoot = bridge.avatar.transform.position };
        context.activeCase = item; context.report.cases.Add(item);
        SceneGroundedTarget target = Select(context, observation, excludedSurface, item, out var selectedPlan);
        SaveImage(context.output, name + "-before", observation);
        context.Check(target != null, "No bounded reachable visible surface for " + name + "; actual room/view cannot satisfy this case. No synthetic target or success was substituted.");
        item.surfaceId = target.SurfaceId; item.imagePoint = target.ImagePoint; item.visibleSurfacePoint = target.WorldPoint;
        item.plannedGround = selectedPlan.GroundTarget; item.plannedRouteLength = selectedPlan.Route.Length;
        string action = context.prefix + "-" + name; context.activeAction = action;
        float started = Time.realtimeSinceStartup;
        context.Check(bridge.ApproachSceneTarget(action, target.CaptureId, target.SurfaceId, target.WorldPoint,
            () => target.TryValidateGeometry(out var reason) ? null : reason, out string failure), "Actual native object task rejected: " + failure);
        context.Check(bridge.TargetKind == "scene-object" && bridge.TaskMode == "approach-object" && bridge.ActiveTaskId == action,
            "Object intention lost its actual body/task ownership.");
        yield return Wait(context, () => context.results.ContainsKey(action) || bridge.Controller.Player != null &&
            bridge.Controller.Player.IsPlaying && Horizontal(bridge.avatar.transform.position - item.initialRoot) >= .12f,
            45f, "actual native movement for " + name);
        context.Check(!context.results.ContainsKey(action), "Native task terminated before material movement: " + TerminalReason(context, action));
        context.Check(item.moved || Horizontal(bridge.avatar.transform.position - item.initialRoot) >= .1f, "Root did not actually travel.");

        Transform changed = null;
        Vector3 originalPosition = default;
        try
        {
            if (kind == "stop")
            {
                Vector3 stoppedRoot = bridge.avatar.transform.position;
                context.Check(bridge.StopForTaskIfCurrent(context.prefix + "-explicit-stop", action, bridge.Controller.OperationRevision, out failure),
                    "Matching object task stop rejected: " + failure);
                item.smoothStopObserved = bridge.Controller.Player != null && bridge.Controller.Player.IsStoppingSmoothly;
                context.Check(item.smoothStopObserved, "Actual moving object task did not start the shared smooth stop.");
                yield return HoldStopped(context, item, stoppedRoot, action, 1.2f);
            }
            else if (kind == "invalidate")
            {
                Renderer renderer = UnityEngine.Object.FindObjectsOfType<Renderer>().SingleOrDefault(value =>
                    "surface-" + value.GetInstanceID().ToString("x") == target.SurfaceId);
                context.Check(renderer != null && !renderer.transform.IsChildOf(bridge.avatar.transform), "Grounded surface identity unavailable for isolated geometry invalidation.");
                changed = renderer.transform; originalPosition = changed.position;
                // Perturb exactly the observed static surface in the disposable Play copy, then restore it.
                // This is a fixture change, never an actor/root teleport or fabricated callback failure.
                changed.position += Vector3.up * .03f; Physics.SyncTransforms();
                context.Check(!target.TryValidateGeometry(out _), "Real target-transform change did not invalidate captured geometry.");
                yield return Wait(context, () => context.results.ContainsKey(action), 2f, "target geometry cancellation");
                Vector3 stoppedRoot = bridge.avatar.transform.position;
                item.smoothStopObserved = bridge.Controller.Player != null && bridge.Controller.Player.IsStoppingSmoothly;
                yield return HoldStopped(context, item, stoppedRoot, action, 1.2f);
            }
            else
            {
                yield return Wait(context, () => context.results.ContainsKey(action), 45f, "actual native arrival for " + name);
            }
            var terminal = context.results[action];
            item.terminalEventCount = terminal.Count; item.terminal = terminal[0];
            context.Check(terminal.Count == 1 && item.terminal.actionId == action && item.terminal.captureId == observation.CaptureId &&
                item.terminal.targetId == target.SurfaceId && item.terminal.targetKind == "scene-object" && item.terminal.terminal,
                "Terminal result was duplicated or lost target/capture/action identity.");
            if (kind == "arrival")
            {
                context.Check(item.terminal.status == "arrived" && item.terminal.reached, "Native controller did not actually reach the object station: " + item.terminal.reason);
                context.Check(Vector3.Distance(item.terminal.plannedGround, selectedPlan.GroundTarget) < .02f &&
                    Mathf.Abs(item.terminal.plannedGround.y - selectedPlan.Route.GroundY) < .04f,
                    "Executed destination differs from the selected same-level floor station.");
                SceneVisionObservation after = null;
                context.source.RequestCapture(value => after = value);
                yield return Wait(context, () => after != null, 5f, "post-arrival RGB");
                context.Check(after.HasImage && after.CaptureId != observation.CaptureId, "Arrival did not produce a new actual view.");
                item.confirmationCaptureId = after.CaptureId;
                item.newViewConfirmsSurface = context.source.TryConfirmVisible(after, target, out _, out string confirmReason);
                item.confirmationReason = confirmReason;
                SaveImage(context.output, name + "-after", after);
                // Arrival and renewed visibility are separate facts; the core real-motion assertion above
                // remains honest if gaze/occlusion means this exact point is outside the final picture.
            }
            else context.Check(!item.terminal.reached && (kind == "stop" ? item.terminal.status == "stopped" :
                item.terminal.status == "cancelled" || item.terminal.status == "blocked"), "Stop/geometry change became a false arrival: " + item.terminal.status);
            yield return Wait(context, () => !bridge.ReservesBody && !bridge.Controller.IsBusy &&
                (bridge.Controller.Player == null || !bridge.Controller.Player.HasPoseOwnership), 5f, "actual body return for " + name);
            item.returnedToAnimator = bridge.avatar.GetComponent<Animator>()?.enabled == true;
            context.Check(item.returnedToAnimator, "Native task did not return ownership to the real Animator.");
            context.Check(context.results[action].Count == 1, "Late native worker produced a duplicate terminal result.");
            item.finalRoot = bridge.avatar.transform.position; item.actualRootTravel = Horizontal(item.finalRoot - item.initialRoot);
            item.elapsedSeconds = Time.realtimeSinceStartup - started;
        }
        finally
        {
            if (changed != null) { changed.position = originalPosition; Physics.SyncTransforms(); }
            item.finalRoot = bridge.avatar.transform.position;
            item.actualRootTravel = Horizontal(item.finalRoot - item.initialRoot);
            item.elapsedSeconds = Time.realtimeSinceStartup - started;
        }
    }

    private static SceneGroundedTarget Select(Context context, SceneVisionObservation observation, string excludedSurface,
        CaseResult item, out ArdyRoomObjectApproachPlan selected)
    {
        selected = null;
        var bridge = context.bridge;
        Vector3 ground = bridge.avatar.transform.position + Vector3.up * bridge.importedReference.groundY * bridge.avatar.transform.lossyScale.y;
        var candidates = new List<SceneGroundedTarget>();
        // A fixed normalized pixel lattice, ordered near image center. No object names/hierarchy are selected.
        float[] xs = { .5f, .35f, .65f, .2f, .8f, .1f, .9f };
        float[] ys = { .55f, .7f, .4f, .8f, .25f, .15f };
        foreach (float y in ys) foreach (float x in xs)
        {
            if (!context.source.TryResolve(observation, new Vector2(x, y), out var target, out _)) continue;
            if (target.SurfaceId == excludedSurface || target.WorldPoint.y < ground.y + .35f ||
                target.WorldPoint.y > ground.y + 2.4f || Horizontal(target.WorldPoint - ground) < 1.05f ||
                Horizontal(target.WorldPoint - ground) > 5f) continue;
            if (candidates.Any(value => value.SurfaceId == target.SurfaceId && Vector3.Distance(value.WorldPoint, target.WorldPoint) < .55f)) continue;
            candidates.Add(target);
        }
        foreach (var target in candidates.OrderBy(value => Horizontal(value.WorldPoint - ground)))
        {
            if (++item.planningAttempts > 12) break;
            if (!ArdyRoomObjectApproachPlanner.TryPlan(bridge.Navigation, ground, target.WorldPoint, bridge.userHead, .8f,
                out var plan, out _, out _)) continue;
            if (plan.Route.Length < .45f || plan.Route.Length > 3.8f || Horizontal(plan.GroundTarget - ground) < .35f) continue;
            context.Check(target.TryValidate(out string reason), "Selected captured geometry became invalid during bounded planning: " + reason);
            selected = plan; return target;
        }
        return null;
    }

    private static IEnumerator HoldStopped(Context context, CaseResult item, Vector3 stoppedRoot, string action, float seconds)
    {
        float until = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < until)
        {
            item.stopRootDrift = Mathf.Max(item.stopRootDrift, Vector3.Distance(stoppedRoot, context.bridge.avatar.transform.position));
            context.Check(item.stopRootDrift < .001f && context.bridge.ActiveTaskId != action && !context.bridge.IsApproachingSceneTarget,
                "Stopped object task resumed or translated during pose return.");
            if (context.bridge.Controller.Player != null && context.bridge.Controller.Player.IsStoppingSmoothly) item.smoothStopObserved = true;
            yield return null;
        }
    }

    private static IEnumerator Wait(Context context, Func<bool> predicate, float seconds, string description)
    {
        float deadline = Time.realtimeSinceStartup + seconds;
        while (!predicate())
        {
            if (Time.realtimeSinceStartup >= deadline) throw new TimeoutException(description + "; phase=" + context.bridge.Phase + "; " + context.bridge.Status);
            yield return null;
        }
    }
    private static string TerminalReason(Context context, string action)
        => context.results.TryGetValue(action, out var values) ? values[0].status + ": " + values[0].reason : "none";
    private static float Horizontal(Vector3 value) => new Vector2(value.x, value.z).magnitude;
    private static Vector3 AvatarGround(ArdyRoomDialogueBridge bridge)
        => bridge.avatar.transform.position + Vector3.up * bridge.importedReference.groundY * bridge.avatar.transform.lossyScale.y;
    private static void SaveImage(string output, string name, SceneVisionObservation observation)
    {
        File.WriteAllText(Path.Combine(output, name + ".json"), observation.MetadataJson);
        File.WriteAllBytes(Path.Combine(output, name + ".jpg"), Convert.FromBase64String(observation.ImageDataUrl.Substring(observation.ImageDataUrl.IndexOf(',') + 1)));
    }
}
