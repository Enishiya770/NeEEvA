using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Motion;
using UnityEngine;

/// <summary>Actual ARDY rolling follow in the caller's disposable connected flat-room fixture.</summary>
public static class ArdyRoomFollowRuntimeRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string FollowTask = "runtime-follow-rolling";
    private static readonly FieldInfo FollowLeg = typeof(ArdyRoomDialogueBridge).GetField("followLegCount", Private);
    public static int Checks { get; private set; }
    public static bool Completed { get; private set; }
    [Serializable] private sealed class Sample
    {
        public float realtime, distance, playbackSeconds;
        public int operation, leg, requestedTarget, committedTarget;
        public long playbackRevision;
        public string phase, stage, task, action, retargetStatus;
        public bool following, bodyReserved, playing, poseOwned, returning, needsRestart;
        public Vector3 root, user;
    }
    [Serializable] private sealed class TargetUpdate
    {
        public string stage;
        public Vector3 user, rootAtRequest, rootAtCommit;
        public float requestedAt, acceptedAt, committedAt;
        public int targetRevision;
    }
    [Serializable] private sealed class Report
    {
        public bool passed, preparingPreserved, playingUpdatesContinuous, continuousUserMovement,
            usefulOlderCandidateCommitted, targetChangedDuringRequest, actualLegArrived, stopDuringPausedReturn, stopPreventedRestart;
        public int checks, originalOperation, completedOperation, stoppedOperation;
        public float walkingSpeed = .65f, movement, arrivalDistance, maximumProgressGap, maximumRootStep,
            continuousUserTravel, continuousRootTravel, actualWalkingStartedAt, actualWalkingStartTravel,
            firstInFlightTargetChangeAt;
        public string failure, firstRequest, arrivalRequest, stoppedRequest;
        public string continuousUserProfile = "1 m in 2.5 s: .8 m/s for .75 s, then .4 m over 1.75 s. Geometry is preflighted for the complete path.";
        public string scope = "Actual connected bridge, .65 m/s native ARDY HTTP and guarded flat-floor locomotion. One target update during actual pose-history preparation and two during playback must preserve the same task and controller operation, continuous root progress and body ownership until arrival. Explicit stop during a later generation must prevent stale playback and follow restart. No live ASR, role model, TTS, all-room coverage or subjective motion-quality claim.";
        public List<TargetUpdate> updates = new List<TargetUpdate>();
        public List<Sample> samples = new List<Sample>();
        [NonSerialized] public HashSet<int> observedCommits = new HashSet<int>();
        [NonSerialized] public int precedingTargetRevision;
        [NonSerialized] public string precedingRetargetStatus;
    }
    private sealed class Progress
    {
        public Vector3 anchor, previous;
        public float movedAt, previousAt, startedAt;
        public bool initialized;
    }

    public static IEnumerator RunRuntimeChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, Transform userAnchor)
    {
        Checks = 0; Completed = false; var report = new Report();
        Check(Application.isPlaying && chat != null && bridge != null && bridge.IsConnected && !chat.enabled &&
            userAnchor != null && bridge.userHead == userAnchor, "Use the connected dormant flat-room fixture and its explicit user anchor.");
        Vector3 savedUser = userAnchor.position, savedRoot = bridge.avatar.transform.position;
        Quaternion savedRotation = bridge.avatar.transform.rotation;
        float savedSpeed = bridge.walkingSpeed, savedControllerSpeed = bridge.Controller.WalkingSpeed;
        IEnumerator routine = Scenario(chat, bridge, userAnchor, report);
        try
        {
            while (true)
            {
                bool more;
                try { more = routine.MoveNext(); }
                catch (Exception error) { report.failure = error.ToString(); throw; }
                if (!more) break;
                yield return routine.Current;
            }
            report.passed = Completed = true;
        }
        finally
        {
            (routine as IDisposable)?.Dispose();
            bridge.StopMovement("stopped", "真实跟随回归结束。");
            // Fixture disposal only: a failed check cannot leave a smooth return controlling
            // the avatar while the caller's saved root is restored. Production stop is smooth.
            if (bridge.Controller.Player.HasPoseOwnership) bridge.Controller.Player.Stop();
            bridge.userHead = userAnchor; userAnchor.position = savedUser;
            bridge.walkingSpeed = savedSpeed; bridge.Controller.WalkingSpeed = savedControllerSpeed;
            bridge.avatar.transform.SetPositionAndRotation(savedRoot, savedRotation);
            bridge.InvalidateCurrentTargetAssessment();
            report.checks = Checks;
            string path = Path.GetFullPath("Tools/MotionAdapter/reports/room-follow-runtime-regression.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(report, true) + "\n");
        }
        Debug.Log("[ArdyRoomFollowRuntimeRegression] passed " + Checks + " checks.");
    }

    private static IEnumerator Scenario(ChatSample chat, ArdyRoomDialogueBridge bridge, Transform user, Report report)
    {
        bridge.StopMovement();
        float deadline = Time.realtimeSinceStartup + 3;
        while (bridge.Controller.Player.HasPoseOwnership)
        { Check(Time.realtimeSinceStartup < deadline, "The preceding fixture did not finish returning its body."); yield return null; }
        bridge.walkingSpeed = .65f;
        Vector3[] targets = FindRollingTargets(bridge);
        Vector3 startRoot = bridge.avatar.transform.position;
        user.position = targets[0];
        var evidence = bridge.ReadCurrentTargetEvidence(true);
        Check(bridge.FollowForTarget(FollowTask, evidence.currentTargetRevision, out string failure), failure);
        report.originalOperation = bridge.Controller.OperationRevision;
        deadline = Time.realtimeSinceStartup + 20;
        while (bridge.Controller.ExecutionState != ArdyRoomExecutionState.Preparing || bridge.Controller.Stage != "pose-history")
        { RequireSameTask(bridge, report, deadline, "actual pose-history preparation"); Observe(bridge, report); yield return null; }
        // Move after history has started, not before dispatch. The same operation must survive.
        float historyStarted = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup < historyStarted + .10f)
        { RequireSameTask(bridge, report, deadline, "continuing pose history"); Observe(bridge, report); yield return null; }
        Check(bridge.Controller.ExecutionState == ArdyRoomExecutionState.Preparing, "History ended before the preparing update was injected.");
        var preparing = BeginUpdate(bridge, targets[1], "pose-history", report);
        int previousTargetRevision = bridge.Controller.MovingTargetRevision;
        deadline = Time.realtimeSinceStartup + 3;
        while (bridge.Controller.MovingTargetRevision == previousTargetRevision)
        {
            RequireSameTask(bridge, report, deadline, "accepting the preparing target");
            Check(Vector3.Distance(startRoot, bridge.avatar.transform.position) < .002f, "Updating a preparing target translated the held avatar root.");
            Observe(bridge, report); yield return null;
        }
        Accepted(bridge, preparing);
        Check(bridge.Controller.ExecutionState == ArdyRoomExecutionState.Preparing,
            "The preparing update was not accepted during the existing history collection.");
        deadline = Time.realtimeSinceStartup + 35;
        while (!bridge.Controller.Player.IsPlaying)
        {
            RequireSameTask(bridge, report, deadline, "first prepared-target playback");
            Check(!bridge.Controller.Player.IsReturningToAnimation, "Preparing target movement returned the body before playback.");
            Observe(bridge, report); yield return null;
        }
        Check(bridge.Controller.CommittedTargetRevision >= preparing.targetRevision &&
            bridge.Controller.MovingTargetRevision == preparing.targetRevision &&
            Mathf.Abs(bridge.Controller.WalkingSpeed - .65f) < .0001f,
            "First playback did not commit the latest preparing target at the normal .65 m/s speed.");
        Committed(bridge, preparing);
        report.preparingPreserved = true; report.firstRequest = bridge.Controller.LastRequestJson;
        Check(!string.IsNullOrEmpty(report.firstRequest), "The actual ARDY generation request was not recorded.");

        // Playback also contains the measured standing history and the natural initial turn.
        // A playing-target test must begin after real horizontal walking, not a fixed .2 s
        // after Play; otherwise a successful future-tail commit during a turn looks frozen.
        deadline = Time.realtimeSinceStartup + 12;
        while (HorizontalDistance(startRoot, bridge.avatar.transform.position) < .15f)
        {
            RequireSameTask(bridge, report, deadline, "actual horizontal walking after the initial turn");
            Check(bridge.ReservesBody && bridge.Controller.Player.IsPlaying &&
                bridge.Controller.Player.HasPoseOwnership && !bridge.Controller.Player.IsReturningToAnimation,
                "The initial prepared leg ended without actual walking.");
            Observe(bridge, report); yield return null;
        }
        report.actualWalkingStartedAt = Time.realtimeSinceStartup;
        report.actualWalkingStartTravel = HorizontalDistance(startRoot, bridge.avatar.transform.position);
        var progress = new Progress();
        for (int index = 2; index < 4; index++)
        {
            // The second update follows the first committed tail rather than coalescing both.
            float movedUntil = Time.realtimeSinceStartup + .20f;
            while (Time.realtimeSinceStartup < movedUntil)
            { RequireContinuous(bridge, report, progress, deadline); Observe(bridge, report); yield return null; }
            var update = BeginUpdate(bridge, targets[index], "playback-" + (index - 1), report);
            previousTargetRevision = bridge.Controller.MovingTargetRevision;
            deadline = Time.realtimeSinceStartup + 12;
            while (bridge.Controller.MovingTargetRevision == previousTargetRevision)
            { RequireContinuous(bridge, report, progress, deadline); Observe(bridge, report); yield return null; }
            Accepted(bridge, update);
            if (index == 3)
            {
                // Keep moving while this HTTP request is outstanding. A useful audited old
                // candidate may keep the gait going while its newer destination remains queued.
                Vector3 movingUserStart = user.position, movingRootStart = bridge.avatar.transform.position;
                float movingStarted = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup < movingStarted + 2.5f)
                {
                    // Front-load ordinary walking speed so the >.35 m refresh happens
                    // during the real HTTP request, then slow down within the same 1 m path.
                    // Constant .4 m/s only refreshes every .875 s, after this server's .5 s
                    // response, and therefore cannot exercise the useful older candidate.
                    float elapsed = Time.realtimeSinceStartup - movingStarted;
                    float fraction = elapsed <= .75f ? .8f * elapsed : .6f + .4f * (elapsed - .75f) / 1.75f;
                    user.position = Vector3.Lerp(movingUserStart, targets[4], fraction);
                    RequireContinuous(bridge, report, progress, deadline); Observe(bridge, report); yield return null;
                }
                user.position = targets[4];
                report.continuousUserTravel = Vector3.Distance(movingUserStart, user.position);
                report.continuousRootTravel = Vector3.Distance(movingRootStart, bridge.avatar.transform.position);
                Check(report.continuousUserTravel > .9f && report.continuousRootTravel > .25f,
                    "Continuous user motion did not coexist with actual avatar root progress.");
                report.continuousUserMovement = true;
                // Ensure the final observed head position has reached the production target
                // check before asking whether its prepared trajectory has caught up.
                float finalCheckUntil = Time.realtimeSinceStartup + ArdyRoomFollowPolicy.MovingTargetCheckInterval + .15f;
                while (Time.realtimeSinceStartup < finalCheckUntil)
                { RequireContinuous(bridge, report, progress, deadline); Observe(bridge, report); yield return null; }
                update.targetRevision = bridge.Controller.MovingTargetRevision;
            }
            while (bridge.Controller.CommittedTargetRevision < update.targetRevision)
            { RequireContinuous(bridge, report, progress, deadline); Observe(bridge, report); yield return null; }
            RequireContinuous(bridge, report, progress, deadline);
            Committed(bridge, update);
            Check(Vector3.Distance(update.rootAtRequest, update.rootAtCommit) > .03f,
                "The avatar made no meaningful root progress while its future target was generated.");
        }
        report.playingUpdatesContinuous = true;
        Check(report.usefulOlderCandidateCommitted,
            "This run did not exercise a useful older candidate committing while a newer moving target remained queued.");
        deadline = Time.realtimeSinceStartup + 50;
        bool sawReturn = false;
        while (bridge.Phase != "follow-waiting")
        {
            RequireSameTask(bridge, report, deadline, "arrival at the final rolling target");
            if (bridge.Controller.Player.IsReturningToAnimation)
            {
                Vector3 delta = user.position - bridge.avatar.transform.position; delta.y = 0;
                Check(delta.magnitude <= bridge.approachDistance + .4f &&
                    bridge.Controller.CommittedTargetRevision == bridge.Controller.MovingTargetRevision &&
                    !bridge.Controller.RetargetNeedsRestart, "The body returned to idle before reaching the latest target.");
                sawReturn = true;
            }
            else if (!sawReturn) RequireContinuous(bridge, report, progress, deadline);
            Observe(bridge, report); yield return null;
        }
        report.completedOperation = bridge.Controller.OperationRevision;
        report.arrivalRequest = bridge.Controller.LastRequestJson;
        report.movement = Vector3.Distance(startRoot, bridge.avatar.transform.position);
        var arrived = bridge.ReadCurrentTargetEvidence(); report.arrivalDistance = arrived.actualUserDistance;
        Check(sawReturn && report.movement > 2f && bridge.Controller.ExecutionState == ArdyRoomExecutionState.Arrived &&
            bridge.Controller.Player.ReturnedToAnimation && !bridge.Controller.Player.HasPoseOwnership,
            "Rolling follow did not travel and complete its natural return before waiting.");
        Check(bridge.IsFollowing && !bridge.ReservesBody && bridge.ActiveTaskId == FollowTask &&
            arrived.actualUserDistance >= bridge.approachDistance - .25f &&
            arrived.actualUserDistance <= bridge.approachDistance + .4f && arrived.actualFacingErrorDegrees <= 15,
            "Rolling arrival missed the measured interaction distance/facing or lost its persistent task.");
        report.actualLegArrived = true;
        foreach (string cause in new[] { "user-started-speaking", "new-user-turn", "barge-in" })
        {
            typeof(ChatSample).GetMethod("CancelDialogueMotion", Private).Invoke(chat, new object[] { cause });
            Check(bridge.IsFollowing && bridge.ActiveTaskId == FollowTask, "Ordinary speech cancelled waiting follow.");
        }
        Vector3 heldRoot = bridge.avatar.transform.position;
        float until = Time.realtimeSinceStartup + 1.6f;
        while (Time.realtimeSinceStartup < until)
        {
            RequireSameTask(bridge, report, until + 1, "stable near-user waiting");
            Check(!bridge.ReservesBody && Vector3.Distance(heldRoot, bridge.avatar.transform.position) < .002f,
                "An unchanged near-user target triggered repeated follow legs.");
            Observe(bridge, report); yield return null;
        }
        bridge.StopForTask("runtime-follow-finish");
        Check(!bridge.IsFollowing && bridge.ActiveTaskId == "" && bridge.Phase == "stopped", "Final stop left persistent follow active.");

        // A guarded pause already returns the pose smoothly, but keeps the persistent task.
        // A prepared explicit stop during that .4 s window must end its authorization too.
        user.position = FindShortTarget(bridge);
        evidence = bridge.ReadCurrentTargetEvidence(true);
        Check(bridge.FollowForTarget("runtime-follow-pause-stop", evidence.currentTargetRevision, out failure), failure);
        deadline = Time.realtimeSinceStartup + 35;
        while (!bridge.Controller.Player.IsPlaying || HorizontalDistance(heldRoot, bridge.avatar.transform.position) < .15f)
        {
            Check(bridge.IsFollowing && bridge.Phase != "follow-paused" && Time.realtimeSinceStartup < deadline,
                "The paused-return stop case never began actual root travel: " + bridge.Status);
            Observe(bridge, report); yield return null;
        }
        int capturedStopOperation = bridge.Controller.OperationRevision;
        typeof(ArdyRoomDialogueBridge).GetMethod("PauseFollow", Private).Invoke(bridge,
            new object[] { "follow-paused", "Regression: paused current owned follow leg.", true });
        Check(bridge.IsFollowing && bridge.Controller.Player.IsStoppingSmoothly && bridge.Controller.Player.HasPoseOwnership,
            "The pause fixture did not retain its task while smoothly returning its actual playing pose.");
        heldRoot = bridge.avatar.transform.position;
        long pausedPlaybackRevision = bridge.Controller.Player.PlaybackRevision;
        Check(bridge.StopForTaskIfCurrent("runtime-follow-paused-fast-stop", "runtime-follow-pause-stop",
            capturedStopOperation, out failure, true), failure);
        Check(!bridge.IsFollowing && bridge.ActiveTaskId == "" && bridge.Controller.Player.IsStoppingSmoothly &&
            bridge.Controller.Player.PlaybackRevision == pausedPlaybackRevision,
            "Matching paused-follow stop failed to end the task or restarted the pose return.");
        int pausedOperation = bridge.Controller.OperationRevision;
        user.position += Vector3.right * .5f;
        until = Time.realtimeSinceStartup + 2f;
        while (Time.realtimeSinceStartup < until)
        {
            Check(!bridge.IsFollowing && bridge.Controller.OperationRevision == pausedOperation &&
                !bridge.Controller.IsBusy && !bridge.Controller.Player.IsPlaying &&
                Vector3.Distance(heldRoot, bridge.avatar.transform.position) < .002f,
                "A stop inside the paused-return window resumed follow or moved the held root.");
            Observe(bridge, report); yield return null;
        }
        Check(!bridge.ReservesBody && bridge.Controller.Player.ReturnedToAnimation &&
            !bridge.Controller.Player.HasPoseOwnership, "Paused-follow stop did not finish the existing smooth return.");
        report.stopDuringPausedReturn = true;

        // Network/task cancellation is separate from the player's joint-continuity regression.
        user.position = FindShortTarget(bridge);
        evidence = bridge.ReadCurrentTargetEvidence(true);
        Check(bridge.FollowForTarget("runtime-follow-stop-request", evidence.currentTargetRevision, out failure), failure);
        deadline = Time.realtimeSinceStartup + 35;
        while (bridge.Controller.ExecutionState != ArdyRoomExecutionState.Generating)
        {
            Check(bridge.IsFollowing && bridge.Phase != "follow-paused" && Time.realtimeSinceStartup < deadline,
                "The explicit-stop case never submitted its real generation request: " + bridge.Status);
            Observe(bridge, report); yield return null;
        }
        report.stoppedRequest = bridge.Controller.LastRequestJson;
        Check(!string.IsNullOrEmpty(report.stoppedRequest), "The stop fixture had no in-flight generation request.");
        bridge.StopForTask("runtime-follow-explicit-stop");
        report.stoppedOperation = bridge.Controller.OperationRevision;
        int segmentsAfterStop = bridge.Controller.Segments.Count;
        user.position += Vector3.right * .5f;
        until = Time.realtimeSinceStartup + 4f;
        while (Time.realtimeSinceStartup < until)
        {
            Check(!bridge.IsFollowing && bridge.ActiveTaskId == "" && bridge.Controller.OperationRevision == report.stoppedOperation &&
                !bridge.Controller.IsBusy && bridge.Controller.Segments.Count == segmentsAfterStop &&
                !bridge.Controller.Player.IsPlaying && Vector3.Distance(heldRoot, bridge.avatar.transform.position) < .002f,
                "A stopped follow request restarted generation, stale playback, or root movement.");
            Observe(bridge, report); yield return null;
        }
        Check(!bridge.ReservesBody && !bridge.Controller.Player.HasPoseOwnership,
            "The stopped task failed to finish releasing body ownership.");
        report.stopPreventedRestart = true;
    }

    private static TargetUpdate BeginUpdate(ArdyRoomDialogueBridge bridge, Vector3 target, string stage, Report report)
    {
        Check(ArdyRoomFollowPolicy.TargetMoved(bridge.userHead.position, target), "Fixture target shift is below the production refresh threshold.");
        bridge.userHead.position = target;
        var update = new TargetUpdate { stage = stage, user = target, rootAtRequest = bridge.avatar.transform.position,
            requestedAt = Time.realtimeSinceStartup };
        report.updates.Add(update); return update;
    }
    private static void Accepted(ArdyRoomDialogueBridge bridge, TargetUpdate update)
    { update.targetRevision = bridge.Controller.MovingTargetRevision; update.acceptedAt = Time.realtimeSinceStartup; }
    private static void Committed(ArdyRoomDialogueBridge bridge, TargetUpdate update)
    { update.committedAt = Time.realtimeSinceStartup; update.rootAtCommit = bridge.avatar.transform.position; }

    private static Vector3[] FindRollingTargets(ArdyRoomDialogueBridge bridge)
    {
        Vector3 ground = AvatarGround(bridge), saved = bridge.userHead.position;
        try
        {
            // Select all geometry before any generation; never select by generated success.
            foreach (float radius in new[] { 3.4f, 3.5f, 3.3f })
                for (int turn = 0; turn < 24; turn++)
                {
                    var candidates = new Vector3[5]; bool valid = true;
                    Vector3 direction = Quaternion.Euler(0, turn * 15, 0) * Vector3.forward;
                    for (int index = 0; index < candidates.Length; index++)
                    {
                        float offset = index < 4 ? .45f * index : 2.35f;
                        bridge.userHead.position = candidates[index] = ground + direction * (radius + offset) + Vector3.up * 1.6f;
                        if (!ArdyRoomApproachPlanner.TryPlan(bridge.Navigation, ground, bridge.userHead, bridge.approachDistance,
                            out var plan, out _) || plan.Route.Length < 2.2f || plan.Route.Length > 5.8f)
                        { valid = false; break; }
                    }
                    if (valid)
                    {
                        for (int sample = 1; sample < 20; sample++)
                        {
                            bridge.userHead.position = Vector3.Lerp(candidates[3], candidates[4], sample / 20f);
                            if (!ArdyRoomApproachPlanner.TryPlan(bridge.Navigation, ground, bridge.userHead, bridge.approachDistance, out _, out _))
                            { valid = false; break; }
                        }
                    }
                    if (valid) return candidates;
                }
        }
        finally { bridge.userHead.position = saved; }
        throw new InvalidOperationException("No four-target schedule passed same-floor geometry; use this case in the 12 m flat fixture.");
    }

    private static Vector3 FindShortTarget(ArdyRoomDialogueBridge bridge)
    {
        Vector3 ground = AvatarGround(bridge), saved = bridge.userHead.position;
        try
        {
            foreach (float radius in new[] { 1.9f, 2.2f, 2.6f })
                for (int turn = 0; turn < 24; turn++)
                {
                    Vector3 candidate = ground + Quaternion.Euler(0, turn * 15, 0) * Vector3.forward * radius + Vector3.up * 1.6f;
                    bridge.userHead.position = candidate;
                    if (ArdyRoomApproachPlanner.TryPlan(bridge.Navigation, ground, bridge.userHead, bridge.approachDistance, out var plan, out _) &&
                        plan.Route.Length > .35f && plan.Route.Length < 2.5f) return candidate;
                }
        }
        finally { bridge.userHead.position = saved; }
        throw new InvalidOperationException("No short same-level explicit-stop target passed the geometric preflight.");
    }

    private static Vector3 AvatarGround(ArdyRoomDialogueBridge bridge) => bridge.avatar.transform.position +
        Vector3.up * bridge.importedReference.groundY * bridge.avatar.transform.lossyScale.y;

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    { Vector3 delta = a - b; delta.y = 0; return delta.magnitude; }

    private static void RequireSameTask(ArdyRoomDialogueBridge bridge, Report report, float deadline, string description)
    {
        Check(bridge.IsFollowing && bridge.ActiveTaskId == FollowTask && bridge.Phase != "follow-paused" &&
            bridge.Controller.OperationRevision == report.originalOperation && !bridge.Controller.RetargetNeedsRestart,
            description + " lost/restarted its owned task: " + bridge.Phase + " / " + bridge.Status);
        if (Time.realtimeSinceStartup >= deadline) throw new TimeoutException(description + " timed out: " + bridge.Phase + " / " + bridge.Status);
    }

    private static void RequireContinuous(ArdyRoomDialogueBridge bridge, Report report, Progress progress, float deadline)
    {
        RequireSameTask(bridge, report, deadline, "continuous rolling playback");
        Check(bridge.ReservesBody && bridge.Controller.Player.IsPlaying && bridge.Controller.Player.HasPoseOwnership &&
            !bridge.Controller.Player.IsReturningToAnimation, "Rolling updates stopped playback or returned body ownership before arrival.");
        Vector3 root = bridge.avatar.transform.position; float now = Time.realtimeSinceStartup;
        if (!progress.initialized)
        {
            progress.initialized = true; progress.anchor = progress.previous = root;
            progress.movedAt = progress.previousAt = progress.startedAt = now; return;
        }
        float step = Vector3.Distance(root, progress.previous);
        report.maximumRootStep = Mathf.Max(report.maximumRootStep, step);
        Check(step <= Mathf.Max(.15f, (now - progress.previousAt) * 2.5f), "Rolling update teleported the avatar root.");
        if (Vector3.Distance(root, progress.anchor) >= .02f)
        { progress.anchor = root; progress.movedAt = now; }
        Vector3 remaining = bridge.Controller.RequestedTargetGround - AvatarGround(bridge); remaining.y = 0;
        if (now > progress.startedAt + 1f && remaining.magnitude > .4f)
        {
            report.maximumProgressGap = Mathf.Max(report.maximumProgressGap, now - progress.movedAt);
            Check(now - progress.movedAt < 1f, "Rolling follow held its root for a second away from the latest arrival position.");
        }
        progress.previous = root; progress.previousAt = now;
    }

    private static void Observe(ArdyRoomDialogueBridge bridge, Report report)
    {
        Vector3 delta = bridge.userHead.position - bridge.avatar.transform.position; delta.y = 0;
        var controller = bridge.Controller; var player = controller.Player;
        if (report.precedingRetargetStatus == "generating-future-tail" &&
            controller.MovingTargetRevision > report.precedingTargetRevision && !report.targetChangedDuringRequest)
        { report.targetChangedDuringRequest = true; report.firstInFlightTargetChangeAt = Time.realtimeSinceStartup; }
        report.precedingRetargetStatus = controller.RetargetStatus;
        report.precedingTargetRevision = controller.MovingTargetRevision;
        foreach (var segment in controller.Segments)
            if (segment.outcome == "audited-retarget-appended" && report.observedCommits.Add(segment.wireRevision) &&
                segment.targetRevision < controller.MovingTargetRevision)
                report.usefulOlderCandidateCommitted = true;
        report.samples.Add(new Sample { realtime = Time.realtimeSinceStartup, distance = delta.magnitude,
            operation = controller.OperationRevision, leg = (int)FollowLeg.GetValue(bridge),
            phase = bridge.Phase, stage = controller.Stage, task = bridge.TaskMode, action = bridge.ActiveTaskId,
            following = bridge.IsFollowing, bodyReserved = bridge.ReservesBody, playing = player.IsPlaying,
            poseOwned = player.HasPoseOwnership, returning = player.IsReturningToAnimation,
            playbackSeconds = player.TimeSeconds, playbackRevision = player.PlaybackRevision,
            requestedTarget = controller.MovingTargetRevision, committedTarget = controller.CommittedTargetRevision,
            retargetStatus = controller.RetargetStatus, needsRestart = controller.RetargetNeedsRestart,
            root = bridge.avatar.transform.position, user = bridge.userHead.position });
    }
    private static void Check(bool condition, string message) { Checks++; if (!condition) throw new InvalidOperationException(message); }
}
