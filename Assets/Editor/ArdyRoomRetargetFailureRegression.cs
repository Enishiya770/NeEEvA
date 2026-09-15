using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Motion;
using UnityEngine;

/// <summary>Independent lifecycle checks against real room requests; no main scene or service lifecycle changes.</summary>
public static class ArdyRoomRetargetFailureRegression
{
    [Serializable] private sealed class Case
    {
        public string name, status = "running", error;
        public int operationRevision, targetRevision, committedTargetRevision, requests, revisionBeforeCancel, cancelledRevision;
        public float rootTravelAfterCancel;
        public string state, retargetStatus;
        public bool needsRestart, bodyOwned;
    }
    [Serializable] private sealed class Report
    {
        public string status = "running", error;
        public string scope = "Real generation and controller APIs in an isolated room. Only the old prefetch deadline is injected in the second case. No relaxed geometry, seam, ownership or generation validation.";
        public int checks;
        public List<Case> cases = new List<Case>();
    }
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo Work = typeof(ArdyRoomLocomotionController).GetField("work", Private);
    private static readonly FieldInfo Deadline = typeof(ArdyRoomLocomotionController).GetField("nextSeamFrame", Private);

    public static IEnumerator RunRuntimeChecks(ArdyRoomLocomotionController controller, Vector3 target,
        string outputDirectory = null)
    {
        if (!Application.isPlaying || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0 ||
            controller == null || controller.Avatar == null || controller.Player.HasPoseOwnership || controller.IsBusy)
            throw new InvalidOperationException("Use a dormant bound controller in the isolated Unity validation scene.");
        string output = outputDirectory ?? Path.Combine(Environment.GetEnvironmentVariable("ARDY_ROOM_AUDIT_OUTPUT") ??
            "Tools/MotionAdapter/reports", "retarget-failures");
        Directory.CreateDirectory(output);
        var report = new Report();
        Case current = null;
        Vector3 original = controller.Avatar.transform.position;
        Quaternion originalRotation = controller.Avatar.transform.rotation;
        float speed = controller.WalkingSpeed;
        void Check(bool condition, string reason)
        {
            report.checks++;
            if (condition) return;
            report.status = "failed"; report.error = reason;
            if (current != null) { current.status = "failed"; current.error = reason; }
            throw new InvalidOperationException("Retarget lifecycle: " + reason);
        }
        IEnumerator Until(Func<bool> condition, float seconds, string reason)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!condition()) { Check(Time.realtimeSinceStartup < deadline, reason); yield return null; }
        }
        IEnumerator Delay(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }
        void Begin(string name)
        {
            Check(!controller.Player.HasPoseOwnership && !controller.IsBusy, "A previous fixture still owns the body or generation lane.");
            controller.Avatar.transform.SetPositionAndRotation(original, originalRotation);
            current = new Case { name = name }; report.cases.Add(current);
            controller.MoveTo(target); current.operationRevision = controller.OperationRevision;
        }
        void Finish()
        {
            current.targetRevision = controller.MovingTargetRevision; current.committedTargetRevision = controller.CommittedTargetRevision;
            current.requests = controller.Segments.Count; current.state = controller.ExecutionState.ToString();
            current.retargetStatus = controller.RetargetStatus; current.needsRestart = controller.RetargetNeedsRestart;
            current.bodyOwned = controller.Player.HasPoseOwnership;
            foreach (var evidence in controller.Segments)
                File.WriteAllText(Path.Combine(output, current.name + "-wire-" + evidence.wireRevision + ".json"), JsonUtility.ToJson(evidence, true));
            if (current.status == "running") current.status = "passed";
            File.WriteAllText(Path.Combine(output, "report.json"), JsonUtility.ToJson(report, true));
        }
        try
        {
            controller.WalkingSpeed = .65f;
            Begin("initial-candidate-cannot-move-away-from-latest");
            var wait = Until(() => controller.Stage == "generation" && controller.Segments.Count > 0, 20,
                "Initial real generation request never started.");
            while (wait.MoveNext()) yield return wait.Current;
            Check(controller.Navigation.TryProjectGround(controller.Avatar.transform.position, out Vector3 here, out string reason),
                "Fixture start is not on supported ground: " + reason);
            Check(controller.TryUpdateMovingTarget(here, null, out reason), "Preparing did not retain and accept a latest valid target: " + reason);
            wait = Until(() => controller.RetargetNeedsRestart, 20, "A candidate moving away from the latest target was not rejected before playback.");
            while (wait.MoveNext()) yield return wait.Current;
            Check(controller.OperationRevision == current.operationRevision && !controller.Player.HasPoseOwnership && !controller.IsBusy &&
                Vector3.Distance(original, controller.Avatar.transform.position) < .005f,
                "Initial superseded result moved the body or changed its operation identity.");
            Finish();

            Begin("old-prefetch-deadline-cannot-abort-new-tail");
            wait = Until(() => controller.IsMoving && Work.GetValue(controller) == null, 25, "Initial reviewed timeline never became fully prepared.");
            while (wait.MoveNext()) yield return wait.Current;
            Check(controller.Navigation.TryProjectGround(original, out Vector3 startGround, out reason), "Cannot obtain initial floor for updated path.");
            Vector3 update = Vector3.Lerp(startGround, target, .85f);
            Deadline.SetValue(controller, 1);
            Check(controller.TryUpdateMovingTarget(update, null, out reason), "Fixture had no usable future seam: " + reason);
            Check((int)Deadline.GetValue(controller) == -1, "Retarget worker inherited an old fixed-route deadline.");
            int version = controller.MovingTargetRevision;
            wait = Until(() => controller.CommittedTargetRevision == version || controller.RetargetNeedsRestart, 15,
                "Updated target never committed or reported a bounded failure.");
            while (wait.MoveNext()) yield return wait.Current;
            Check(controller.CommittedTargetRevision == version && !controller.RetargetNeedsRestart &&
                controller.OperationRevision == current.operationRevision && controller.Player.IsPlaying,
                "Old timer killed an otherwise current future-tail candidate.");
            controller.Cancel();
            wait = Until(() => !controller.Player.HasPoseOwnership, 3, "Cancel did not release the smooth return.");
            while (wait.MoveNext()) yield return wait.Current;
            Finish();

            Begin("cancel-retarget-http-discards-late-result");
            wait = Until(() => controller.IsMoving && Work.GetValue(controller) == null, 25, "Initial motion not available for cancellation fixture.");
            while (wait.MoveNext()) yield return wait.Current;
            int count = controller.Segments.Count;
            Check(controller.TryUpdateMovingTarget(update, null, out reason), "Cannot start real retarget request: " + reason);
            wait = Until(() => controller.Segments.Count > count && controller.Segments[controller.Segments.Count - 1].outcome == "generating", 5,
                "No in-flight retarget request available to cancel.");
            while (wait.MoveNext()) yield return wait.Current;
            count = controller.Segments.Count;
            Vector3 stopped = controller.Avatar.transform.position;
            current.revisionBeforeCancel = controller.Revision;
            controller.Cancel();
            current.cancelledRevision = controller.Revision;
            Check(current.cancelledRevision > current.revisionBeforeCancel, "Cancel did not advance the request invalidation fence.");
            Check(controller.IsReturningOwnedPose, "Controller did not retain ownership during its explicit smooth stop.");
            wait = Delay(1.5f);
            while (wait.MoveNext()) yield return wait.Current;
            current.rootTravelAfterCancel = Vector3.Distance(stopped, controller.Avatar.transform.position);
            Check(controller.OperationRevision == current.operationRevision && controller.ExecutionState == ArdyRoomExecutionState.Cancelled &&
                controller.Revision == current.cancelledRevision &&
                controller.Segments.Count == count && controller.Segments[count - 1].outcome == "cancelled" &&
                !controller.Player.HasPoseOwnership && !controller.IsMoving && !controller.IsBusy && current.rootTravelAfterCancel < .005f,
                "Cancelled future generation appended or reclaimed playback after smooth stopping.");
            Finish(); report.status = "passed";
        }
        finally
        {
            if (current != null && current.status != "passed") Finish();
            if (report.status == "running") report.status = "incomplete";
            File.WriteAllText(Path.Combine(output, "report.json"), JsonUtility.ToJson(report, true));
            controller.Cancel();
            // Explicit isolated-fixture teardown; never allow its return coroutine to keep
            // writing a root that this test must restore for the next independent scenario.
            if (controller.Player.HasPoseOwnership) controller.Player.Stop();
            controller.WalkingSpeed = speed;
            controller.Avatar.transform.SetPositionAndRotation(original, originalRotation);
        }
    }
}
