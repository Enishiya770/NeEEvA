using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NeEEvA.Motion;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

public static partial class ArdyRoomStreamDelayRegression
{
    private static T ControlField<T>(string name) => (T)typeof(ArdyRoomLocomotionController)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);

    private static IEnumerator InjectShortStoppingCorridor()
    {
        float deadline = Time.realtimeSinceStartup + 20;
        while (controller.StreamStepCount < 3 || controller.StreamTelemetry.pending)
        {
            Check(Time.realtimeSinceStartup < deadline && !Failed(), "Healthy native prefix before control fault.");
            Drive(); yield return null;
        }
        int local = ControlField<int>("streamNextFrame") - ControlField<int>("streamBaseFrame") - 1;
        var clip = ControlField<ArdyLocomotionClip>("ownedClip");
        var anchor = ControlField<Vector3>("anchorGround");
        var rotation = ControlField<Quaternion>("anchorRotation");
        var offset = ControlField<Vector3>("neutralHorizontal");
        var forward = clip.rotationClip.SampleDelta(0, local / 20f) * Vector3.forward;
        float heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        var seam = anchor + rotation * clip.frames[local].rootPosition * ControlField<float>("motionScale") -
            rotation * Quaternion.Euler(0, heading, 0) * offset;
        seam.y = Ground().y;
        Vector3 goal = seam + rotation * forward * .02f; goal.y = seam.y;
        Check(navigation.TryPlan(Ground(), goal, out var route, out string error), error);
        Check(ControlField<float>("streamCommittedSpeed") > .25f, "Real committed speed must exceed the stopping corridor limit.");
        ControlField<ArdyRoomStreamTrajectory.PathState>("streamControlPath").Clear();
        Check(controller.TryUpdateContinuousFollow(route, userGround, userGround + Vector3.up * 1.6f,
            Vector3.zero, .65f, false, out error), error);
        // Exercise this seam immediately: the ordinary scheduler can reach the
        // target before dispatch, correctly taking arrival instead of failing.
        controller.StopCoroutine(ControlField<Coroutine>("work"));
        var method = typeof(ArdyRoomLocomotionController).GetMethod("GuardedStreamRun", BindingFlags.Instance | BindingFlags.NonPublic);
        var guarded = (IEnumerator)method.Invoke(controller, new object[] { LocalControlStep(), current.operation });
        typeof(ArdyRoomLocomotionController).GetField("work", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(controller, controller.StartCoroutine(guarded));
        deadline = Time.realtimeSinceStartup + 10;
        while (!controller.StreamIsRecovering && !Failed())
        {
            Check(Time.realtimeSinceStartup < deadline, "Short corridor did not enter the real local control rejection.");
            yield return null;
        }
        Check(File.Exists(controller.LastControlFailureEvidencePath), "Local rejection must persist a complete seam/continuity record.");
        var evidence = JObject.Parse(File.ReadAllText(controller.LastControlFailureEvidencePath));
        Check((string)evidence["code"] == "braking-distance" && (bool)evidence["requestSubmitted"] == false &&
            (string)evidence.SelectToken("telemetry.warmContinuationReason") == "no-continuation" &&
            evidence["lastAcceptedResponse"] is JObject && (int)evidence["operationRevision"] == current.operation,
            "Typed braking rejection, exact operation, native seam and warm-path exit are required.");
        File.Copy(controller.LastControlFailureEvidencePath, Path.Combine(SessionState.GetString(Key + "Output", ""),
            current.name + "-failure-" + controller.StreamControlRecoveryCount + ".json"), true);
    }

    private static IEnumerator LocalControlStep()
    {
        var step = (System.Collections.Generic.IEnumerable<object>)typeof(ArdyRoomLocomotionController)
            .GetMethod("GenerateStreamStep", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(controller, new object[] { current.operation, null });
        foreach (var value in step) yield return value;
    }

    private static IEnumerator ControlScenarios()
    {
        foreach (string mode in new[] { "once", "exhaust", "cancel", "replace" })
        {
            var wait = Reset(); while (wait.MoveNext()) yield return wait.Current;
            BeginCase("control-" + mode, -1, 0, false);
            BeginStream(); wait = WaitForMotion(20); while (wait.MoveNext()) yield return wait.Current;
            current.initialSession = controller.StreamSessionId; current.operation = controller.OperationRevision;
            wait = InjectShortStoppingCorridor(); while (wait.MoveNext()) yield return wait.Current;
            Check(controller.StreamControlRecoveryCount == 1 && controller.StreamRecoveryCount == 1 &&
                controller.StreamUnderrunCount == 0 && controller.StreamQualityRecoveryCount == 0,
                "Local braking recovery has an independent fixed limit and no fabricated service/quality failure.");
            current.observedRecovery = true;
            if (mode == "cancel") { wait = StopAndHold(2); while (wait.MoveNext()) yield return wait.Current; }
            else
            {
                if (mode == "replace")
                {
                    int oldOperation = current.operation;
                    controller.Cancel();
                    float handoffDeadline = Time.realtimeSinceStartup + 3;
                    while (controller.Player.HasPoseOwnership)
                    {
                        Check(Time.realtimeSinceStartup < handoffDeadline, "Replacement handoff remained bounded.");
                        yield return null;
                    }
                    BeginStream();
                    Check(controller.OperationRevision != oldOperation && controller.StreamControlRecoveryCount == 0,
                        "Replacement revokes old recovery ownership and owns a fresh budget.");
                }
                wait = WaitForResumedMotion(25); while (wait.MoveNext()) yield return wait.Current;
                if (mode == "exhaust")
                {
                    wait = InjectShortStoppingCorridor(); while (wait.MoveNext()) yield return wait.Current;
                    Check(Failed() && controller.StreamControlRecoveryCount == 1 && !controller.IsContinuousFollowActive,
                        "Second braking rejection terminates without an unbounded retry.");
                    current.observedExplicitFailure = true; current.terminalStatus = controller.Status;
                    while (controller.Player.HasPoseOwnership) yield return null;
                    wait = HoldInactive(2); while (wait.MoveNext()) yield return wait.Current;
                }
                else
                {
                    wait = RunHealthyFor(2); while (wait.MoveNext()) yield return wait.Current;
                    wait = StopAndHold(1); while (wait.MoveNext()) yield return wait.Current;
                }
            }
            EndCase();
        }
    }
}
