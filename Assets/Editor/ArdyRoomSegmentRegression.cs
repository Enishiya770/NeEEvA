using System;
using System.IO;
using NeEEvA.Motion;
using UnityEditor;
using UnityEngine;

public static class ArdyRoomSegmentRegression
{
    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Use the isolated validation project only.");
        try
        {
            int checks = RunChecks();
            string output = Environment.GetEnvironmentVariable("ARDY_ROOM_AUDIT_OUTPUT");
            File.WriteAllText(Path.Combine(output, "report.json"), "{\"status\":\"passed\",\"checks\":" + checks + "}");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }

    public static int RunChecks()
    {
        int checks = 0;
        void Check(bool condition, string description)
        { checks++; if (!condition) throw new Exception("Room segment regression: " + description); }
        void Reject(Action action, string description)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, description);
        }
        var corners = new[] { Vector3.zero, new Vector3(0, 0, 6), new Vector3(3, 0, 6) };
        var timeline = ArdyRoomTargetBuilder.Build(corners, 9, Quaternion.identity, Vector3.zero, 1, .65f, 2400, 180, 180);
        Check(timeline.Length > 200, "long route exceeds one request");
        Reject(() => ArdyRoomTargetBuilder.Build(corners, 9, Quaternion.identity, Vector3.zero, 1, .65f, 200, 180, 180), "single request budget stays bounded");
        var plan = ArdyRoomSegmentPlan.Build(timeline, 200);
        Check(plan.Segments.Count >= 3, "long turning route has multiple bounded segments");
        int contiguous = 0;
        foreach (var segment in plan.Segments)
        {
            Check(segment.StartFrame == contiguous, "continuous timeline has no skipped/duplicated frame");
            Check(segment.Targets.Length <= 200 && segment.Targets.Length % 40 == 0, "all requests respect native window and maximum");
            for (int i = 0; i < segment.ContinueAfterFrames; i++)
                Check(segment.Targets[i].rootPosition == timeline[contiguous + i].rootPosition &&
                    segment.Targets[i].headingDegrees == timeline[contiguous + i].headingDegrees, "played prefix keeps full route position and heading");
            for (int i = segment.Targets.Length - 20; i < segment.Targets.Length; i++)
                Check(segment.Targets[i].rootPosition == segment.Targets[segment.Targets.Length - 1].rootPosition,
                    "every request retains one second of stationary emergency/final support");
            for (int i = 1; i < segment.Targets.Length; i++)
                Check(Vector3.Distance(segment.Targets[i].rootPosition, segment.Targets[i - 1].rootPosition) <= .075001f &&
                    Mathf.Abs(Mathf.DeltaAngle(segment.Targets[i - 1].headingDegrees, segment.Targets[i].headingDegrees)) <= 9.001f,
                    "native speed/turn bounds apply to emergency deceleration too");
            contiguous += segment.ContinueAfterFrames;
        }
        Check(contiguous == timeline.Length && plan.Segments[plan.Segments.Count - 1].Final, "only final segment ends full target timeline");
        var shortTimeline = ArdyRoomTargetBuilder.Build(new[] { Vector3.zero, Vector3.forward }, 1,
            Quaternion.identity, Vector3.zero, 1, .65f, 200, 0);
        var shortPlan = ArdyRoomSegmentPlan.Build(shortTimeline, 200);
        Check(shortPlan.Segments.Count == 1, "short route is still one request");
        Check(JsonUtility.ToJson(new Targets { values = shortTimeline }) == JsonUtility.ToJson(new Targets { values = shortPlan.Segments[0].Targets }),
            "short request targets are byte-for-byte unchanged");
        var before = Fixture(200, 0);
        var next = Fixture(200, 120);
        var history = ArdyRoomSegmentPlan.GeneratedHistory(before, 120);
        Check(history.Length == 16 && history[0].rootPosition == before.frames[104].rootPosition &&
            history[15].rootPosition == before.frames[119].rootPosition, "continuation uses exact sixteen source seam frames");
        history[15].globalRotations[0] = Quaternion.Euler(0, 15, 0);
        var headings = ArdyRoomSegmentPlan.ContinueHeading(plan.Segments[1].Targets, history[15]);
        Check(Mathf.Abs(Mathf.DeltaAngle(15, headings[0].headingDegrees)) <= 4.501f, "continuation heading starts from actual source seam, not ideal path yaw");
        for (int i = 1; i < headings.Length; i++)
            Check(Mathf.Abs(Mathf.DeltaAngle(headings[i - 1].headingDegrees, headings[i].headingDegrees)) <= 4.501f &&
                headings[i].rootPosition == plan.Segments[1].Targets[i].rootPosition, "heading convergence preserves the reviewed path and turn rate");
        var combined = ArdyRoomSegmentPlan.Append(before, 120, next, out float step, out float angle);
        Check(combined.frames.Length == 320 && Mathf.Abs(step - .02f) < .0001f && angle == 0, "append reports physical source seam");
        for (int i = 0; i < 120; i++)
            Check(ReferenceEquals(combined.frames[i], before.frames[i]) && ReferenceEquals(combined.rotationClip.frames[i], before.rotationClip.frames[i]),
                "append keeps every old played frame identical");
        Check(combined.frames[120] == next.frames[0] && combined.frames[319] == next.frames[199], "replace old emergency tail with actual continuation");
        next.frames[0].rootPosition += Vector3.right;
        Reject(() => ArdyRoomSegmentPlan.Append(before, 120, next, out _, out _), "teleporting next segment rejected");
        next = Fixture(200, 120); next.rotationClip.frames[0].globalRotations[0] = Quaternion.Euler(0, 90, 0);
        Reject(() => ArdyRoomSegmentPlan.Append(before, 120, next, out _, out _), "rotational seam jump rejected");
        Reject(() => ArdyRoomSegmentPlan.GeneratedHistory(before, 15), "no short or synthetic padding history");
        var far = new ArdyLocomotionTarget[40];
        for (int i = 0; i < far.Length; i++) far[i] = new ArdyLocomotionTarget { rootPosition = new Vector3(20, 0, 0) };
        Reject(() => ArdyRoomSegmentPlan.Build(far, 200), "source coordinate range stays bounded");
        return checks;
    }

    [Serializable] private sealed class Targets { public ArdyLocomotionTarget[] values; }
    private static ArdyLocomotionClip Fixture(int count, int start)
    {
        var clip = new ArdyLocomotionClip { schema = 1, id = "segment-fixture", sourceRootHeight = .95f,
            source = new ArdyLocomotionSource { coordinateSystem = "unity-lh-y-up-z-forward" },
            frames = new ArdyLocomotionFrame[count], targets = new ArdyLocomotionTarget[count],
            rotationClip = new ArdyMotionClip { schema = 1, fps = 20, id = "segment-fixture",
                source = new ArdyMotionSource { coordinateSystem = "unity-lh-y-up-z-forward" },
                jointNames = ArdyLiveProtocol.JointNames, jointParents = ArdyLiveProtocol.JointParents,
                restGlobalRotations = new Quaternion[27], frames = new ArdyMotionFrame[count] } };
        for (int j = 0; j < 27; j++) clip.rotationClip.restGlobalRotations[j] = Quaternion.identity;
        for (int i = 0; i < count; i++)
        {
            clip.frames[i] = new ArdyLocomotionFrame { rootPosition = new Vector3(0, .95f, (start + i) * .02f) };
            clip.targets[i] = new ArdyLocomotionTarget { rootPosition = new Vector3(0, 0, (start + i) * .02f) };
            clip.rotationClip.frames[i] = new ArdyMotionFrame { globalRotations = new Quaternion[27] };
            for (int j = 0; j < 27; j++) clip.rotationClip.frames[i].globalRotations[j] = Quaternion.identity;
        }
        clip.Validate(); return clip;
    }
}
