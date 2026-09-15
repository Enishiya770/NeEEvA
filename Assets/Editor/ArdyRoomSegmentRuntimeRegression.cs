using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Motion;
using UnityEngine;

/// <summary>Real bounded HTTP/Unity playback, with explicit deadline injection for late-result lifecycle cases.</summary>
public static class ArdyRoomSegmentRuntimeRegression
{
    [Serializable] private sealed class Sample
    {
        public float realtime, clipSeconds;
        public int operationRevision, segment, prepared;
        public bool playing, owned, returning, waiting;
        public string stage, state;
        public Vector3 root;
        public Quaternion rotation;
    }
    [Serializable] private sealed class Evidence
    {
        public int operationRevision, wireRevision, segment, totalSegments, startFrame, requestFrames;
        public string requestId, historyKind, requestPath, responsePath, outcome;
        public float submittedAtRealtime, completedAtRealtime, seamSourceRootStep, seamMaximumJointStep;
    }
    [Serializable] private sealed class Case
    {
        public string name, status = "running", reason;
        public int operationRevision, finalSegmentCount;
        public Vector3 start, end;
        public float maximumRenderedRootStep, maximumSeamJointStep, endpointError;
        public List<Sample> samples = new List<Sample>();
        public List<Evidence> segments = new List<Evidence>();
    }
    [Serializable] private sealed class Report
    {
        public string status = "running", error;
        public string scope = "Real ARDY HTTP, bound room NavMesh/VRM playback at supported 0.15m/s. Deadline cases force only the private deadline frame after a real continuation request starts; generated poses and terrain checks remain unchanged. No ASR, role-model decision, or subjective naturalness claim.";
        public int checks;
        public List<Case> cases = new List<Case>();
    }
    private static readonly FieldInfo NextSeam = typeof(ArdyRoomLocomotionController).GetField("nextSeamFrame", BindingFlags.Instance | BindingFlags.NonPublic);

    public static IEnumerator RunRuntimeChecks(ArdyRoomLocomotionController controller, Vector3 target,
        string outputDirectory = null)
    {
        if (!Application.isPlaying || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Use a bound avatar in the isolated Unity validation project.");
        if (controller == null || controller.Avatar == null || controller.IsMoving || controller.IsBusy)
            throw new InvalidOperationException("Segment runtime checks need a dormant configured controller.");
        string output = outputDirectory ?? Path.Combine(Environment.GetEnvironmentVariable("ARDY_ROOM_AUDIT_OUTPUT") ??
            "Tools/MotionAdapter/reports", "segments");
        Directory.CreateDirectory(output);
        var report = new Report();
        var start = controller.Avatar.transform.position;
        var rotation = controller.Avatar.transform.rotation;
        float speed = controller.WalkingSpeed;
        Case current = null;
        Vector3 previousRoot = start;
        Quaternion[] previousBones = null;
        void Check(bool condition, string reason)
        {
            report.checks++;
            if (condition) return;
            report.status = "failed"; report.error = reason;
            if (current != null) { current.status = "failed"; current.reason = reason; }
            throw new InvalidOperationException("Room segment runtime: " + reason);
        }
        void Observe()
        {
            if (current == null) return;
            var p = controller.Player;
            Vector3 root = controller.Avatar.transform.position;
            current.maximumRenderedRootStep = Mathf.Max(current.maximumRenderedRootStep, Vector3.Distance(previousRoot, root));
            previousRoot = root;
            var bones = p.CaptureFullBodyHistoryFrame(Quaternion.identity).globalRotations;
            if (previousBones != null && controller.SegmentCount > 1 && p.TimeSeconds >= 5.85f && p.TimeSeconds <= 6.2f)
                for (int j = 0; j < bones.Length; j++)
                    current.maximumSeamJointStep = Mathf.Max(current.maximumSeamJointStep, Quaternion.Angle(previousBones[j], bones[j]));
            previousBones = bones;
            current.samples.Add(new Sample { realtime = Time.realtimeSinceStartup, clipSeconds = p.TimeSeconds,
                operationRevision = controller.OperationRevision, segment = controller.SegmentIndex, prepared = controller.PreparedSegmentCount,
                playing = p.IsPlaying, owned = p.HasPoseOwnership, returning = p.IsReturningToAnimation, waiting = controller.WaitingForSegment,
                stage = controller.Stage, state = controller.ExecutionState.ToString(), root = root, rotation = controller.Avatar.transform.rotation });
            Check(controller.OperationRevision == current.operationRevision, "Outer operation revision changed inside a segmented task.");
        }
        IEnumerator Until(Func<bool> condition, float timeout, string reason)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!condition())
            {
                Check(Time.realtimeSinceStartup < deadline, reason);
                Observe(); yield return null;
            }
            Observe();
        }
        IEnumerator Delay(float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until) { Observe(); yield return null; }
        }
        void Begin(string name)
        {
            controller.Cancel();
            controller.Avatar.transform.SetPositionAndRotation(start, rotation);
            previousRoot = start; previousBones = null;
            current = new Case { name = name, start = start };
            report.cases.Add(current);
            controller.MoveTo(target);
            current.operationRevision = controller.OperationRevision;
        }
        void Finish()
        {
            current.end = controller.Avatar.transform.position;
            current.finalSegmentCount = controller.SegmentCount;
            current.endpointError = controller.LastEndpointError;
            foreach (var item in controller.Segments)
            {
                string prefix = current.name + "-wire-" + item.wireRevision;
                var evidence = new Evidence { operationRevision = item.operationRevision, wireRevision = item.wireRevision,
                    segment = item.segment, totalSegments = item.totalSegments, startFrame = item.startFrame, requestFrames = item.requestFrames,
                    requestId = item.requestId, historyKind = item.historyKind, outcome = item.outcome,
                    submittedAtRealtime = item.submittedAtRealtime, completedAtRealtime = item.completedAtRealtime,
                    seamSourceRootStep = item.seamSourceRootStep, seamMaximumJointStep = item.seamMaximumJointStep };
                if (item.requestJson != null) { evidence.requestPath = prefix + "-request.json"; File.WriteAllText(Path.Combine(output, evidence.requestPath), item.requestJson); }
                if (item.responseJson != null) { evidence.responsePath = prefix + "-response.json"; File.WriteAllText(Path.Combine(output, evidence.responsePath), item.responseJson); }
                current.segments.Add(evidence);
            }
            if (current.status == "running") current.status = "passed";
            File.WriteAllText(Path.Combine(output, "report.json"), JsonUtility.ToJson(report, true));
        }
        try
        {
            controller.WalkingSpeed = .15f;
            Begin("continuous-arrival");
            var wait = Until(() => controller.ExecutionState == ArdyRoomExecutionState.Arrived ||
                controller.ExecutionState == ArdyRoomExecutionState.Failed || controller.ExecutionState == ArdyRoomExecutionState.Blocked, 55,
                "Continuous route did not reach a terminal state.");
            while (wait.MoveNext()) yield return wait.Current;
            Check(controller.ExecutionState == ArdyRoomExecutionState.Arrived, "Continuous long route did not arrive: " + controller.Status);
            Check(controller.SegmentCount > 1 && controller.Segments.Count >= 2, "Fixture did not require multiple bounded requests.");
            Check(controller.LastClip.frames.Length > 200 && controller.Segments[0].historyKind == "unity-full-body-projection-v1" &&
                controller.Segments[1].historyKind == "audited-generated-full-body-v1", "Continuation did not use explicit audited source history.");
            Check(controller.LastEndpointError <= controller.ArrivalTolerance && controller.LastSeamRootStep <= .10f &&
                controller.LastSeamMaximumJointStep <= 35, "Actual generated seam or final endpoint violates continuity acceptance.");
            foreach (var sample in current.samples)
                Check(!sample.returning || sample.segment == controller.SegmentCount, "Normal multi-segment route returned to idle before its final segment.");
            Finish();

            Begin("cancel-second-segment");
            wait = Until(() => controller.SegmentIndex >= 2 && controller.Player.IsPlaying, 35, "Second segment never became active.");
            while (wait.MoveNext()) yield return wait.Current;
            Vector3 cancelledRoot = controller.Avatar.transform.position;
            int requestCount = controller.Segments.Count;
            controller.Cancel();
            wait = Delay(1);
            while (wait.MoveNext()) yield return wait.Current;
            Check(!controller.Player.HasPoseOwnership && !controller.IsBusy && !controller.IsMoving &&
                controller.ExecutionState == ArdyRoomExecutionState.Cancelled && Vector3.Distance(cancelledRoot, controller.Avatar.transform.position) < .005f &&
                controller.Segments.Count == requestCount, "Cancel allowed a pending or future segment to restart.");
            Finish();

            Begin("late-continuation-recaptures-actual-pose");
            wait = Until(() => controller.Segments.Count >= 2 && controller.Segments[1].outcome == "generating", 25,
                "A real continuation request was not available for deadline injection.");
            while (wait.MoveNext()) yield return wait.Current;
            Check(NextSeam != null, "Deadline injection field not found.");
            NextSeam.SetValue(controller, 1);
            wait = Until(() => controller.WaitingForSegment, 2, "The injected deadline did not invalidate continuation.");
            while (wait.MoveNext()) yield return wait.Current;
            wait = Until(() => controller.Segments.Count >= 3, 25, "Late continuation did not replan from a stopped pose.");
            while (wait.MoveNext()) yield return wait.Current;
            Check(controller.Segments[1].outcome == "discarded-after-seam" && controller.Segments[2].historyKind == "unity-full-body-projection-v1",
                "Late predicted history was reused instead of recapturing the actual stopped character.");
            wait = Until(() => controller.ExecutionState == ArdyRoomExecutionState.Arrived ||
                controller.ExecutionState == ArdyRoomExecutionState.Failed || controller.ExecutionState == ArdyRoomExecutionState.Blocked, 40,
                "Replanned continuation did not finish.");
            while (wait.MoveNext()) yield return wait.Current;
            Check(controller.ExecutionState == ArdyRoomExecutionState.Arrived, "Replanned route failed: " + controller.Status);
            Finish();

            Begin("external-player-stop-during-late-wait");
            wait = Until(() => controller.Segments.Count >= 2 && controller.Segments[1].outcome == "generating", 25,
                "Continuation was not available for external-owner cancellation test.");
            while (wait.MoveNext()) yield return wait.Current;
            NextSeam.SetValue(controller, 1);
            wait = Until(() => controller.WaitingForSegment, 2, "External-stop fixture did not enter late wait.");
            while (wait.MoveNext()) yield return wait.Current;
            controller.Player.Stop(); cancelledRoot = controller.Avatar.transform.position;
            requestCount = controller.Segments.Count;
            wait = Delay(1);
            while (wait.MoveNext()) yield return wait.Current;
            Check(!controller.Player.HasPoseOwnership && !controller.IsBusy && !controller.IsMoving && controller.Segments.Count == requestCount &&
                Vector3.Distance(cancelledRoot, controller.Avatar.transform.position) < .005f &&
                controller.ExecutionReason == ArdyRoomExecutionReason.OwnershipLost, "External player.Stop was incorrectly treated as natural settling and restarted generation.");
            Finish();
            report.status = "passed";
        }
        finally
        {
            if (current != null && current.segments.Count == 0) Finish();
            if (report.status == "running") report.status = "incomplete";
            File.WriteAllText(Path.Combine(output, "report.json"), JsonUtility.ToJson(report, true));
            controller.Cancel(); controller.WalkingSpeed = speed;
            if (controller.Avatar != null) controller.Avatar.transform.SetPositionAndRotation(start, rotation);
        }
    }
}
