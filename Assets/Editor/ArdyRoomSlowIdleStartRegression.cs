using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NeEEvA.Motion;
using UnityEngine;

/// <summary>Actual player-loop slow idle captures, native follow generation, moving target and explicit stop.</summary>
public static class ArdyRoomSlowIdleStartRegression
{
    [Serializable] private sealed class Report
    {
        public bool passed, measuredSlowIdle, initialRequestSent, played, userMoved, stopPreventedRestart;
        public int checks, operation, capturedSlowFrames, acceptedSteps;
        public float minimumSlowFrameSeconds, maximumSlowFrameSeconds, avatarTravel, userTravel, stopDrift;
        public string error, historyCode, requestJson, responseJson;
        public string scope = "Disposable connected VRM/flat-room fixture. A real Update stall of 220 ms produces consecutively rendered slow idle frames; no pose samples or timestamps are injected. Production bridge submits continuous follow to the actual ARDY endpoint. After initial history dispatch, the stall is removed, the real generated motion follows a moving user, then explicit stop must fence playback. This does not assert motion quality or sustained walking at 4 FPS.";
        public List<float> measuredFrameSeconds = new List<float>();
    }
    public static int Checks { get; private set; }
    public static int RunRefreshChecks(ArdyRoomDialogueBridge bridge, Transform user)
    {
        Checks = 0;
        var navigation = bridge.Navigation;
        Vector3 savedUser = user.position;
        Vector3 start = bridge.avatar.transform.position + Vector3.up * bridge.importedReference.groundY * bridge.avatar.transform.lossyScale.y;
        GameObject obstacle = null;
        try
        {
            user.position = start + Vector3.forward * 2.8f + Vector3.up * 1.6f;
            Check(ArdyRoomApproachPlanner.TryPlan(navigation, start, user, bridge.approachDistance, out var baseline, out string failure), failure);
            Check(ArdyRoomApproachPlanner.TryRefreshFollowPlan(navigation, start, user, bridge.approachDistance, baseline,
                out var same, out failure, out var observation) && observation.stage == "follow-refresh" && observation.candidateCount == 1,
                "Stationary follow repeated the 24-candidate search instead of auditing its existing station: " + failure);
            user.position += Vector3.right * .4f;
            Check(ArdyRoomApproachPlanner.TryRefreshFollowPlan(navigation, start, user, bridge.approachDistance, same,
                out var moved, out failure, out observation) && observation.stage == "follow-refresh" &&
                Horizontal(moved.UserGround, same.UserGround) > .35f && Horizontal(moved.GroundTarget, same.GroundTarget) > .35f &&
                ArdyRoomApproachPlanner.RouteKeepsUserClearance(moved.Route, moved.UserGround, moved.UserClearanceRadius),
                "Fast follow refresh reused the stationary user target or skipped route clearance: " + failure);
            obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube); obstacle.name = "Dynamic follow-refresh blocker";
            obstacle.transform.SetParent(bridge.environmentRoot, true);
            obstacle.transform.position = moved.GroundTarget + Vector3.up * .7f;
            obstacle.transform.localScale = Vector3.one;
            // Environment roots may be scaled floor primitives; keep an explicit world size.
            var scale = obstacle.transform.lossyScale;
            obstacle.transform.localScale = new Vector3(.45f / scale.x, 1.4f / scale.y, .45f / scale.z);
            Physics.SyncTransforms();
            bool found = ArdyRoomApproachPlanner.TryRefreshFollowPlan(navigation, start, user, bridge.approachDistance, moved,
                out var changed, out failure, out observation);
            Check(!found || observation.stage != "follow-refresh" && Horizontal(changed.GroundTarget, moved.GroundTarget) > .2f &&
                ArdyRoomApproachPlanner.RouteKeepsUserClearance(changed.Route, changed.UserGround, changed.UserClearanceRadius),
                "A dynamic blocker was ignored by the one-station audit or fallback.");
            Check(observation.candidateCount > 1 || observation.stage == "start", "Invalid station did not invoke the ordinary bounded planner.");
            UnityEngine.Object.DestroyImmediate(obstacle); obstacle = null; Physics.SyncTransforms();
            user.position = start + Vector3.up * 6f;
            Check(!ArdyRoomApproachPlanner.TryRefreshFollowPlan(navigation, start, user, bridge.approachDistance, moved,
                out _, out failure, out _), "Fast refresh reused an old valid floor after the current user lost support.");
            Check(!ArdyRoomApproachPlanner.TryRefreshFollowPlan(navigation, start, null, bridge.approachDistance, moved,
                out _, out _, out _), "Fast refresh substituted an old user when the explicit anchor disappeared.");
            return Checks;
        }
        finally
        {
            if (obstacle != null) UnityEngine.Object.DestroyImmediate(obstacle);
            user.position = savedUser; Physics.SyncTransforms(); bridge.InvalidateCurrentTargetAssessment();
        }
    }
    [Serializable] private sealed class SlowPlaybackObservation
    {
        public bool setupReachedRealPlayback, stayedActive, observedRecovery, explicitFailure;
        public float seconds, rootTravel, minimumFrameSeconds = float.MaxValue, maximumFrameSeconds;
        public int frames, acceptedStepsBefore, acceptedStepsAfter, recoveriesBefore, recoveriesAfter;
        public string error, finalStage, finalStatus;
        public string scope = "Separate bounded stress observation: actual native follow starts at normal frame cadence, then real 220 ms Update stalls and a moving explicit user run for at most four seconds. Survival is an observation, not the primary regression pass condition or a sustained smoothness claim.";
    }

    public static IEnumerator RunSlowPlaybackObservation(ChatSample chat, ArdyRoomDialogueBridge bridge, Transform user, string output)
    {
        var report = new SlowPlaybackObservation();
        var controller = bridge.Controller;
        var host = new GameObject("Bounded slow playback stress");
        var delay = host.AddComponent<ArdySlowIdleFrameDelay>(); delay.enabled = false;
        Vector3 savedUser = user.position;
        var routine = SlowPlayback(bridge, user, delay, report);
        try
        {
            while (true)
            {
                bool more;
                try { more = routine.MoveNext(); }
                catch (Exception error) { report.error = error.ToString(); break; }
                if (!more) break;
                yield return routine.Current;
            }
        }
        finally
        {
            (routine as IDisposable)?.Dispose(); delay.enabled = false;
            report.finalStage = controller.Stage; report.finalStatus = controller.Status;
            report.acceptedStepsAfter = controller.StreamStepCount; report.recoveriesAfter = controller.StreamRecoveryCount;
            report.observedRecovery = report.recoveriesAfter > report.recoveriesBefore;
            report.stayedActive = report.setupReachedRealPlayback && bridge.IsFollowing && controller.IsContinuousFollowActive;
            report.explicitFailure = controller.ExecutionState == ArdyRoomExecutionState.Failed || controller.ExecutionState == ArdyRoomExecutionState.Blocked;
            bridge.StopMovement("stopped", "隔离慢帧持续播放观察结束。");
            if (controller.Player.HasPoseOwnership) controller.Player.Stop();
            user.position = savedUser; UnityEngine.Object.Destroy(host);
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "follow-slow-playback-observation.json"), JsonUtility.ToJson(report, true));
        }
    }

    private static IEnumerator SlowPlayback(ArdyRoomDialogueBridge bridge, Transform user, ArdySlowIdleFrameDelay delay, SlowPlaybackObservation report)
    {
        var controller = bridge.Controller; bridge.StopMovement();
        float until = Time.realtimeSinceStartup + 3;
        while (controller.Player.HasPoseOwnership)
        { if (Time.realtimeSinceStartup > until) throw new TimeoutException("Prior body handoff"); yield return null; }
        Vector3 direction = bridge.avatar.transform.forward; direction.y = 0; direction.Normalize();
        user.position = bridge.avatar.transform.position + direction * 2.5f + Vector3.up * 1.6f;
        bridge.InvalidateCurrentTargetAssessment();
        var evidence = bridge.ReadCurrentTargetEvidence(true);
        if (!bridge.FollowForTarget("slow-playback-observation", evidence.currentTargetRevision, out string failure))
            throw new InvalidOperationException(failure);
        until = Time.realtimeSinceStartup + 35;
        while (!controller.Player.IsPlaying)
        {
            if (!bridge.IsFollowing || Time.realtimeSinceStartup > until) throw new InvalidOperationException(controller.Status);
            yield return null;
        }
        report.setupReachedRealPlayback = true;
        report.acceptedStepsBefore = controller.StreamStepCount; report.recoveriesBefore = controller.StreamRecoveryCount;
        Vector3 startRoot = bridge.avatar.transform.position, startUser = user.position;
        float started = Time.realtimeSinceStartup; delay.enabled = true;
        while (Time.realtimeSinceStartup - started < 4 && bridge.IsFollowing && controller.IsContinuousFollowActive)
        {
            user.position = startUser + direction * Mathf.Min(1.4f, (Time.realtimeSinceStartup - started) * .35f);
            yield return null;
            report.frames++; report.seconds = Time.realtimeSinceStartup - started;
            report.rootTravel = Horizontal(startRoot, bridge.avatar.transform.position);
            report.minimumFrameSeconds = Mathf.Min(report.minimumFrameSeconds, Time.unscaledDeltaTime);
            report.maximumFrameSeconds = Mathf.Max(report.maximumFrameSeconds, Time.unscaledDeltaTime);
        }
    }

    public static IEnumerator RunRuntimeChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, Transform user, string output)
    {
        Checks = 0; var report = new Report();
        Check(Application.isPlaying && chat != null && !chat.enabled && bridge != null && bridge.IsConnected &&
            user != null && bridge.userHead == user, "Requires the isolated dormant chat, connected bridge and explicit user.");
        int savedRate = Application.targetFrameRate, savedVsync = QualitySettings.vSyncCount;
        Vector3 savedUser = user.position;
        var delayHost = new GameObject("Measured slow idle frame fixture");
        var delay = delayHost.AddComponent<ArdySlowIdleFrameDelay>(); delay.enabled = false;
        IEnumerator routine = Scenario(bridge, user, delay, report);
        try
        {
            QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60;
            while (true)
            {
                bool more;
                try { more = routine.MoveNext(); }
                catch (Exception error) { report.error = error.ToString(); throw; }
                if (!more) break;
                yield return routine.Current;
            }
            report.passed = true;
        }
        finally
        {
            (routine as IDisposable)?.Dispose(); delay.enabled = false;
            report.acceptedSteps = bridge.Controller.StreamStepCount;
            report.requestJson = bridge.Controller.LastRequestJson; report.responseJson = bridge.Controller.LastResponseJson;
            bridge.StopMovement("stopped", "隔离慢帧跟随回归结束。");
            if (bridge.Controller.Player.HasPoseOwnership) bridge.Controller.Player.Stop();
            user.position = savedUser;
            QualitySettings.vSyncCount = savedVsync; Application.targetFrameRate = savedRate;
            UnityEngine.Object.Destroy(delayHost);
            report.checks = Checks; Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "follow-slow-idle-start.json"), JsonUtility.ToJson(report, true));
        }
    }

    private static IEnumerator Scenario(ArdyRoomDialogueBridge bridge, Transform user, ArdySlowIdleFrameDelay delay, Report report)
    {
        var controller = bridge.Controller;
        bridge.StopMovement();
        float deadline = Time.realtimeSinceStartup + 4;
        while (controller.Player.HasPoseOwnership)
        { Check(Time.realtimeSinceStartup < deadline, "Prior movement did not release its body."); yield return null; }
        Vector3 initialRoot = bridge.avatar.transform.position;
        Vector3 direction = bridge.avatar.transform.forward; direction.y = 0; direction.Normalize();
        user.position = initialRoot + direction * 2.6f + Vector3.up * 1.6f;
        bridge.InvalidateCurrentTargetAssessment();
        delay.enabled = true;
        int previousFrame = Time.frameCount;
        while (report.measuredFrameSeconds.Count < 5)
        {
            yield return null;
            if (Time.frameCount == previousFrame) continue;
            previousFrame = Time.frameCount;
            report.measuredFrameSeconds.Add(Time.unscaledDeltaTime);
        }
        report.minimumSlowFrameSeconds = float.MaxValue;
        foreach (float seconds in report.measuredFrameSeconds)
        {
            report.minimumSlowFrameSeconds = Mathf.Min(report.minimumSlowFrameSeconds, seconds);
            report.maximumSlowFrameSeconds = Mathf.Max(report.maximumSlowFrameSeconds, seconds);
            if (seconds > .15f && seconds < .35f) report.capturedSlowFrames++;
        }
        Check(report.capturedSlowFrames >= 4, "Fixture did not reproduce consecutive >150 ms actual frames.");
        report.measuredSlowIdle = true;
        var evidence = bridge.ReadCurrentTargetEvidence(true);
        Check(bridge.FollowForTarget("slow-idle-actual-follow", evidence.currentTargetRevision, out string failure), failure);
        deadline = Time.realtimeSinceStartup + 16;
        while (string.IsNullOrEmpty(controller.LastRequestJson))
        {
            Check(bridge.IsFollowing && controller.IsContinuousFollowActive && Time.realtimeSinceStartup < deadline,
                "Actual slow-frame follow failed before the native request: " + controller.Status);
            yield return null;
        }
        report.operation = controller.OperationRevision; report.initialRequestSent = true;
        report.historyCode = controller.StreamHistoryObservation?.code;
        Check(controller.StreamHistoryObservation?.success == true && controller.LastRequestJson.Contains("initialHistory"),
            "Initial native request did not carry admitted actual history.");
        delay.enabled = false;
        deadline = Time.realtimeSinceStartup + 45;
        while (!controller.Player.IsPlaying || Horizontal(initialRoot, bridge.avatar.transform.position) < .15f)
        {
            Check(bridge.IsFollowing && controller.IsContinuousFollowActive && Time.realtimeSinceStartup < deadline,
                "Native stream did not produce actual guarded root progress: " + controller.Status);
            yield return null;
        }
        report.played = true;
        Vector3 userStart = user.position; float movingAt = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - movingAt < 1.5f)
        {
            user.position = userStart + direction * Mathf.Min(.6f, (Time.realtimeSinceStartup - movingAt) * .4f);
            Check(bridge.IsFollowing && controller.IsContinuousFollowActive && controller.OperationRevision == report.operation,
                "A moving user replaced or lost the active follow operation: " + controller.Status);
            yield return null;
        }
        report.userTravel = Horizontal(userStart, user.position);
        report.avatarTravel = Horizontal(initialRoot, bridge.avatar.transform.position);
        Check(report.userTravel > .4f && report.avatarTravel > .2f && controller.StreamStepCount >= 3,
            "Follow did not combine meaningful user movement and real generated avatar movement.");
        report.userMoved = true;
        bridge.StopMovement("stopped", "隔离回归显式停止跟随。");
        Vector3 stopRoot = bridge.avatar.transform.position;
        deadline = Time.realtimeSinceStartup + 1.6f;
        while (Time.realtimeSinceStartup < deadline)
        {
            Check(!bridge.IsFollowing && !controller.IsContinuousFollowActive && !controller.Player.IsPlaying,
                "Explicit stop allowed old follow work to restart.");
            report.stopDrift = Mathf.Max(report.stopDrift, Horizontal(stopRoot, bridge.avatar.transform.position));
            yield return null;
        }
        Check(!controller.Player.HasPoseOwnership && report.stopDrift < .02f, "Stopped follow retained the body or continued root travel.");
        report.stopPreventedRestart = true;
    }
    private static float Horizontal(Vector3 a, Vector3 b) { a.y = b.y = 0; return Vector3.Distance(a, b); }
    private static void Check(bool value, string message) { Checks++; if (!value) throw new InvalidOperationException(message); }
}

[DefaultExecutionOrder(-30000)]
public sealed class ArdySlowIdleFrameDelay : MonoBehaviour
{
    private void Update() { System.Threading.Thread.Sleep(220); }
}
