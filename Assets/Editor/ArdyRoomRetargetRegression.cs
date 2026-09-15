using System;
using System.Reflection;
using NeEEvA.Motion;
using UnityEngine;

public static class ArdyRoomRetargetRegression
{
    public static int RunChecks()
    {
        int checks = 0;
        void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException("Room retarget: " + reason); }
        void Reject(Action action, string reason)
        { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } Check(rejected, reason); }
        var history = new ArdyRoomHistoryFrame[16];
        for (int i = 0; i < history.Length; i++)
        {
            history[i] = new ArdyRoomHistoryFrame { rootPosition = new Vector3(0, .95f, 1 + (i - 15) * .65f / 20), globalRotations = new Quaternion[27] };
            for (int j = 0; j < 27; j++) history[i].globalRotations[j] = Quaternion.identity;
        }
        var corners = new[] { new Vector3(0, 0, 1), new Vector3(0, 0, 5) };
        var timeline = ArdyRoomSegmentPlan.BuildMovingTimeline(corners, 4, Quaternion.identity, Vector3.zero, 1, .65f, history, 0);
        Check(timeline.Length % 40 == 0 && timeline.Length <= 2400, "moving targets remain native-window aligned and bounded");
        Check(Mathf.Abs(timeline[0].rootPosition.z - 1 - .65f / 20) < .0001f, "moving seam continues its actual speed instead of restarting from zero");
        for (int i = 1; i < timeline.Length; i++)
            Check(Vector3.Distance(timeline[i].rootPosition, timeline[i - 1].rootPosition) <= .075001f &&
                Mathf.Abs(Mathf.DeltaAngle(timeline[i].headingDegrees, timeline[i - 1].headingDegrees)) <= 9.001f,
                "native position and heading bounds apply to every rolling target");
        for (int i = timeline.Length - 20; i < timeline.Length; i++)
            Check(timeline[i].rootPosition == new Vector3(0, 0, 5), "retarget still reserves terminal support hold");
        var plan = ArdyRoomSegmentPlan.Build(timeline, 200);
        int originalStart = plan.Segments[0].StartFrame;
        plan.OffsetFrames(73);
        Check(plan.Segments[0].StartFrame == originalStart + 73, "replacement may start at an arbitrary unplayed source frame");
        Check(history[15].rootPosition == new Vector3(0, .95f, 1), "target generation never rewrites source seam history");
        Reject(() => ArdyRoomSegmentPlan.BuildMovingTimeline(new[] { corners[0], new Vector3(0, 0, 1.02f) }, .02f,
            Quaternion.identity, Vector3.zero, 1, .65f, history, null), "no instantaneous stop into a too-close target");
        Reject(() => ArdyRoomSegmentPlan.BuildMovingTimeline(new[] { corners[0], new Vector3(0, 0, -2) }, 3,
            Quaternion.identity, Vector3.zero, 1, .65f, history, null), "a moving turnabout must settle first");
        foreach (float seamSpeed in new[] { 0f, .10f })
        {
            var turning = new ArdyRoomHistoryFrame[16];
            for (int i = 0; i < turning.Length; i++)
            {
                turning[i] = new ArdyRoomHistoryFrame {
                    rootPosition = new Vector3((i - 15) * seamSpeed / 20f, .95f, 1), globalRotations = new Quaternion[27] };
                for (int j = 0; j < 27; j++) turning[i].globalRotations[j] = Quaternion.Euler(0, 90, 0);
            }
            var turned = ArdyRoomSegmentPlan.BuildMovingTimeline(corners, 4, Quaternion.identity, Vector3.zero, 1, .65f, turning, 0);
            for (int i = 0; i < 20; i++)
                Check(turned[i].rootPosition == new Vector3(0, 0, 1) &&
                    Mathf.Abs(Mathf.DeltaAngle(90 - (i + 1) * 4.5f, turned[i].headingDegrees)) < .002f,
                    "stationary/slow ninety-degree retarget turns in place before translating");
            Check(turned[20].rootPosition.z > 1 && turned[20].rootPosition.z - 1 < .003f &&
                Mathf.Abs(turned[20].headingDegrees) < .002f, "after turning, translation accelerates from rest in the aligned direction");
        }
        var mediumTurn = new ArdyRoomHistoryFrame[16];
        for (int i = 0; i < mediumTurn.Length; i++)
        {
            mediumTurn[i] = new ArdyRoomHistoryFrame {
                rootPosition = new Vector3((i - 15) * .2f / 20f, .95f, 1), globalRotations = new Quaternion[27] };
            for (int j = 0; j < 27; j++) mediumTurn[i].globalRotations[j] = Quaternion.Euler(0, 90, 0);
        }
        Reject(() => ArdyRoomSegmentPlan.BuildMovingTimeline(corners, 4, Quaternion.identity, Vector3.zero, 1, .65f, mediumTurn, 0),
            "ninety-degree direction changes with remaining momentum require a smooth stop");
        Reject(() => plan.OffsetFrames(-1), "no negative history/frame offset");
        var fixture = new GameObject("ARDY-retarget-evidence-bound-fixture") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            var controller = fixture.AddComponent<ArdyRoomLocomotionController>();
            var record = typeof(ArdyRoomLocomotionController).GetMethod("RecordSegmentEvidence", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(record != null, "bounded diagnostic insertion exists");
            int operation = controller.OperationRevision, segment = controller.SegmentIndex, prepared = controller.PreparedSegmentCount;
            for (int i = 0; i < 80; i++)
                record.Invoke(controller, new object[] { new ArdyRoomLocomotionController.SegmentEvidence {
                    wireRevision = i, outcome = i == 79 ? "generating" : "audited-retarget-appended",
                    submittedAtRealtime = i, completedAtRealtime = i == 79 ? 0 : i + .5f } });
            Check(controller.Segments.Count == 64 && controller.DiscardedSegmentEvidenceCount == 16,
                "continuous follow retains exactly the newest 64 diagnostic records");
            Check(controller.Segments[0].wireRevision == 16 && controller.Segments[63].wireRevision == 79,
                "pruning removes only oldest evidence and keeps the active request");
            Check(controller.OperationRevision == operation && controller.SegmentIndex == segment && controller.PreparedSegmentCount == prepared,
                "diagnostic retention does not rewrite execution progress or operation identity");
            controller.Cancel();
            Check(controller.Segments[63].outcome == "cancelled" && controller.DiscardedSegmentEvidenceCount == 16,
                "bounded queue retains the in-flight record for cancellation evidence");
        }
        finally { UnityEngine.Object.DestroyImmediate(fixture); }
        return checks;
    }
}
