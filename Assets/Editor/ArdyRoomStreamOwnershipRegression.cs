using System;
using System.Collections;
using System.IO;
using NeEEvA.Motion;
using UnityEngine;

/// <summary>A waiting stream must not reclaim its same player from another caller.</summary>
public static class ArdyRoomStreamOwnershipRegression
{
    [Serializable] private sealed class Report
    {
        public bool passed, waitingReached, foreignPlaybackPreserved, resumeRejected, cleanupOwnedByFixture;
        public int checks, operation, finalOperation;
        public long foreignPlaybackRevision, finalPlaybackRevision;
        public string error, foreignClip, state, reason;
        public string scope = "A new continuous controller session reaches stream-waiting via the real HTTP capabilities endpoint. A separate caller acquires the SAME locomotion player, then the waiting controller receives a resume command. Only the old stream must end; the foreign clip/revision must remain untouched. No Chat task dispatch or motion-quality claim in this ownership-only case.";
        public float duration;
    }
    public static bool Completed { get; private set; }
    public static int Checks { get; private set; }

    public static IEnumerator RunRuntimeChecks(ArdyRoomLocomotionController controller,
        ArdyLocomotionAvatarReference reference, ArdyLocomotionClip actualClip, string output)
    {
        Completed = false; Checks = 0;
        var report = new Report(); float started = Time.realtimeSinceStartup;
        var routine = Scenario(controller, reference, actualClip, report);
        try
        {
            while (true)
            {
                bool more;
                try { more = routine.MoveNext(); }
                catch (Exception error) { report.error = error.ToString(); throw; }
                if (!more) break;
                yield return routine.Current;
            }
            report.passed = Completed = true;
        }
        finally
        {
            (routine as IDisposable)?.Dispose();
            report.checks = Checks; report.duration = Time.realtimeSinceStartup - started;
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "waiting-player-ownership.json"), JsonUtility.ToJson(report, true));
        }
    }

    private static IEnumerator Scenario(ArdyRoomLocomotionController controller,
        ArdyLocomotionAvatarReference reference, ArdyLocomotionClip actualClip, Report report)
    {
        Check(controller != null && reference != null && actualClip != null, "Missing actual runtime fixture or previously generated clip.");
        Check(!controller.IsContinuousFollowActive && !controller.Player.HasPoseOwnership,
            "Ownership check needs the preceding task and its pose handoff to finish.");
        actualClip.Validate();
        Vector3 ground = controller.Avatar.transform.position + Vector3.up * reference.groundY * controller.Avatar.transform.lossyScale.y;
        Vector3 direction = controller.Avatar.transform.forward; direction.y = 0; direction.Normalize();
        Vector3 user = ground + direction;
        Check(controller.Navigation.TryPlan(ground, ground, out var stationary, out string error), error);
        controller.BeginContinuousFollow(stationary, user, user + Vector3.up * 1.6f, Vector3.zero, 0, false);
        report.operation = controller.OperationRevision;
        float until = Time.realtimeSinceStartup + 5f;
        while (controller.Stage != "stream-waiting")
        {
            Check(controller.IsContinuousFollowActive && Time.realtimeSinceStartup < until,
                "Continuous session failed to enter its real waiting state: " + controller.Status);
            yield return null;
        }
        Check(!controller.Player.HasPoseOwnership, "Waiting stream retained the player before foreign acquisition.");
        report.waitingReached = true;
        // Use a deep copy of real generated poses. Rebase only this fixture playback's
        // origin so injecting an unrelated caller does not teleport the test avatar.
        var foreign = ArdyLocomotionClip.Parse(JsonUtility.ToJson(actualClip));
        foreign.id = "ownership-foreign-real-clip";
        foreign.sourceOrigin = new Vector3(foreign.frames[0].rootPosition.x, 0, foreign.frames[0].rootPosition.z);
        foreign.Validate();
        controller.Player.Play(foreign);
        report.foreignClip = foreign.id; report.foreignPlaybackRevision = controller.Player.PlaybackRevision;
        Check(controller.Player.HasPoseOwnership && controller.Player.IsPlaying, "The independent caller failed to acquire the shared player.");
        Check(controller.Navigation.TryPlan(ground, ground + direction, out var resumed, out error), error);
        // A queued control is allowed to be accepted; the execution fence must reject
        // reclaiming the player before it changes that caller's clip or playback revision.
        controller.TryUpdateContinuousFollow(resumed, user, user + Vector3.up * 1.6f, direction * .4f, .4f, true, out _);
        until = Time.realtimeSinceStartup + .5f;
        while (controller.IsContinuousFollowActive)
        {
            Check(Time.realtimeSinceStartup < until, "Waiting follow did not withdraw after another caller acquired its player.");
            CheckForeign();
            yield return null;
        }
        CheckForeign();
        Check(controller.ExecutionState == ArdyRoomExecutionState.Failed && controller.ExecutionReason == ArdyRoomExecutionReason.OwnershipLost,
            "Waiting stream did not report an ownership-specific failure.");
        report.resumeRejected = true;
        until = Time.realtimeSinceStartup + .15f;
        while (Time.realtimeSinceStartup < until) { CheckForeign(); yield return null; }
        report.finalPlaybackRevision = controller.Player.PlaybackRevision;
        report.finalOperation = controller.OperationRevision;
        report.state = controller.ExecutionState.ToString(); report.reason = controller.ExecutionReason.ToString();
        report.foreignPlaybackPreserved = true;
        // Only the fixture owner now releases its own clip; no old follow callback may do so.
        report.cleanupOwnedByFixture = true;
        controller.Player.RequestSmoothStop(.2f);
        until = Time.realtimeSinceStartup + 1f;
        while (controller.Player.HasPoseOwnership)
        { Check(Time.realtimeSinceStartup < until, "Fixture-owned clip cleanup timed out."); yield return null; }

        void CheckForeign()
        {
            Check(controller.Player.Clip == foreign && controller.Player.PlaybackRevision == report.foreignPlaybackRevision &&
                controller.Player.HasPoseOwnership && !controller.Player.IsReturningToAnimation,
                "Old waiting follow cancelled, replaced or returned the foreign caller's clip.");
        }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Checks++; }
}
