using System;
using System.Reflection;
using System.IO;
using NeEEvA.Motion;
using UnityEngine;

/// <summary>Deterministic control geometry and continuity; no scene, service or body owner.</summary>
public static class ArdyRoomStreamTrajectoryRegression
{
    [Serializable] private sealed class RecordedFixture
    { public RecordedSegment[] segments; public RecordedRejection[] rejectedPrefixes; }
    [Serializable] private sealed class RecordedSegment
    { public string requestJson, responseJson, outcome; }
    [Serializable] private sealed class RecordedRequest
    { public RecordedHistory initialHistory; }
    [Serializable] private sealed class RecordedHistory
    { public ArdyRoomHistoryFrame[] frames; }
    [Serializable] private sealed class RecordedRejection
    {
        public Vector3 anchorGround, targetGround, neutralHorizontal;
        public Quaternion anchorRotation;
        public float motionScale;
    }
    [Serializable] private sealed class ReplayResult
    {
        public string input, scope;
        public int steps, verifiedTargets;
        public float[] inputSpeeds, committedSpeeds;
    }

    /// <summary>Requires the existing real room's planner/guard; no model call or scene setup.</summary>
    public static string ReplayRecordedSeams(string file,
        Func<Vector3, Vector3, ArdyRoomRoute> plan, Func<Vector3, Vector3, bool> guard)
    {
        var recorded = JsonUtility.FromJson<RecordedFixture>(File.ReadAllText(file));
        if (recorded?.rejectedPrefixes == null || recorded.rejectedPrefixes.Length != 1 ||
            recorded.segments == null || recorded.segments.Length != 3)
            throw new InvalidOperationException("Expected the bounded native-r5 fixture with two accepted responses and one control rejection.");
        var context = recorded.rejectedPrefixes[0];
        var history = JsonUtility.FromJson<RecordedRequest>(recorded.segments[0].requestJson)?.initialHistory?.frames;
        if (history == null || history.Length != 16) throw new InvalidOperationException("Fixture must retain its exact initial 16-frame history.");
        var state = new ArdyRoomStreamTrajectory.PathState();
        var result = new ReplayResult { input = Path.GetFullPath(file),
            scope = "Deterministic control replay with the saved initial history and two actual native seams. New controls do not retroactively claim to have generated these old responses. Same real-room planner and geometry guard; no native request, seed change or scene mutation.",
            inputSpeeds = new float[3], committedSpeeds = new float[3] };
        for (int step = 0; step < 3; step++)
        {
            Vector3 source; Quaternion rotation; float speed;
            if (step == 0)
            {
                source = history[15].rootPosition; rotation = history[15].globalRotations[0];
                Vector3 delta = source - history[12].rootPosition; delta.y = 0;
                speed = delta.magnitude * context.motionScale / .15f;
            }
            else
            {
                if (recorded.segments[step - 1].outcome != "stream-accepted") throw new InvalidOperationException("Replay history is not an accepted native prefix.");
                var clip = ArdyLocomotionClip.Parse(recorded.segments[step - 1].responseJson);
                source = clip.frames[7].rootPosition; rotation = clip.rotationClip.SampleDelta(0, 7 / 20f);
                speed = state.CommittedSpeed;
            }
            Vector3 forward = rotation * Vector3.forward;
            float heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            Vector3 planeSource = source; planeSource.y = 0;
            Vector3 actualRoot = context.anchorGround + context.anchorRotation * (planeSource * context.motionScale) -
                context.anchorRotation * Quaternion.Euler(0, heading, 0) * context.neutralHorizontal;
            actualRoot.y = context.anchorGround.y;
            ArdyRoomRoute route = plan(actualRoot, context.targetGround);
            if (route == null) throw new InvalidOperationException("Recorded native seam has no real-room route at step " + step);
            var targets = ArdyRoomStreamTrajectory.Build(route, source, heading, speed, .65f,
                context.anchorRotation, context.anchorGround, context.motionScale, rootOffset: context.neutralHorizontal,
                validateRootSegment: guard, continuity: state);
            Vector3 previous = actualRoot;
            foreach (var target in targets)
            {
                Vector3 next = context.anchorGround + context.anchorRotation * (target.rootPosition * context.motionScale) -
                    context.anchorRotation * Quaternion.Euler(0, target.headingDegrees, 0) * context.neutralHorizontal;
                next.y = context.anchorGround.y;
                if (!guard(previous, next)) throw new InvalidOperationException("Recorded-seam controls leave real-room geometry at step " + step);
                previous = next; result.verifiedTargets++;
            }
            result.inputSpeeds[step] = speed; result.committedSpeeds[step] = state.CommittedSpeed; result.steps++;
        }
        return JsonUtility.ToJson(result, true);
    }

    public static int RunChecks()
    {
        int checks = 0;
        void Check(bool value, string reason)
        { checks++; if (!value) throw new InvalidOperationException("Room stream trajectory: " + reason); }

        Vector3 Root(ArdyLocomotionTarget target, Quaternion rotation, Vector3 anchor, float scale, Vector3 offset)
        {
            Vector3 value = anchor + rotation * (target.rootPosition * scale) -
                rotation * Quaternion.Euler(0, target.headingDegrees, 0) * offset;
            value.y = anchor.y; return value;
        }

        void Audit(ArdyLocomotionTarget[] targets, Vector3 initialSource, float heading,
            Quaternion rotation, Vector3 anchor, float scale, Vector3 offset)
        {
            Check(targets.Length == 40, "one native H40 target window");
            initialSource.y = 0;
            Vector3 previous = Root(new ArdyLocomotionTarget { rootPosition = initialSource, headingDegrees = heading }, rotation, anchor, scale, offset);
            Vector3 previousSource = initialSource;
            foreach (var target in targets)
            {
                Vector3 point = Root(target, rotation, anchor, scale, offset);
                Check(Finite(target.rootPosition.x) && Finite(target.rootPosition.y) && Finite(target.rootPosition.z) && Finite(target.headingDegrees), "finite control");
                Check(Mathf.Abs(target.rootPosition.y) < .00001f, "source ground target");
                Check(Vector3.Distance(previousSource, target.rootPosition) <= .075001f, "source service speed limit");
                Check(Vector3.Distance(previous, point) <= 1.2f / 20f + .00001f, "bounded world root displacement");
                Check(Mathf.Abs(Mathf.DeltaAngle(heading, target.headingDegrees)) <= 4.501f, "bounded angular rate across all piece joins");
                previous = point; previousSource = target.rootPosition; heading = target.headingDegrees;
            }
        }

        void Reject(Action action, string code)
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidOperationException error)
            { rejected = true; Check(error.Message.Contains(code), "precise feasibility rejection: " + error.Message); }
            Check(rejected, "expected rejection " + code);
        }

        var straightRoute = Route(Vector3.zero, Vector3.forward * 5f);
        var straight = ArdyRoomStreamTrajectory.Build(straightRoute, new Vector3(0, .95f, 0), 0, .65f, .65f,
            Quaternion.identity, Vector3.zero, 1);
        Check(Mathf.Abs(straight[0].rootPosition.z - .65f / 20f) < .00001f, "straight continuation keeps initial velocity");
        Audit(straight, Vector3.zero, 0, Quaternion.identity, Vector3.zero, 1, Vector3.zero);

        // A wide rounding corridor and a narrow route around furniture. A rejected arc
        // may use the original polyline, but cannot send an unchecked diagonal.
        var corners = new[] { Vector3.zero, Vector3.forward * 1.2f, new Vector3(1.2f, 0, 1.2f) };
        foreach (float width in new[] { .04f, .25f })
        foreach (float speed in new[] { .65f, 1f, 1.2f })
        {
            int queries = 0;
            bool Guard(Vector3 a, Vector3 b)
            {
                queries++;
                for (int j = 0; j <= 8; j++)
                    if (DistanceToRoute(corners, Vector3.Lerp(a, b, j / 8f)) > width) return false;
                return true;
            }
            var targets = ArdyRoomStreamTrajectory.Build(Route(corners), new Vector3(0, .95f, 0), 0, speed, speed,
                Quaternion.identity, Vector3.zero, 1, validateRootSegment: Guard);
            Check(queries <= 96, "bounded geometry work");
            Audit(targets, Vector3.zero, 0, Quaternion.identity, Vector3.zero, 1, Vector3.zero);
            Vector3 before = Vector3.zero;
            foreach (var target in targets)
            {
                Check(DistanceToRoute(corners, target.rootPosition) <= width + .00001f, "control remains in checked corridor");
                for (int j = 0; j <= 8; j++)
                    Check(DistanceToRoute(corners, Vector3.Lerp(before, target.rootPosition, j / 8f)) <= width + .00001f,
                        "inter-control chord stays in corridor, not just its endpoints");
                before = target.rootPosition;
            }
        }

        var exact = ArdyRoomStreamTrajectory.Build(Route(corners), new Vector3(0, .95f, 0), 0, .65f, .65f,
            Quaternion.identity, Vector3.zero, 1, validateRootSegment: (_, __) => false);
        foreach (var t in exact) Check(DistanceToRoute(corners, t.rootPosition) < .00001f, "unsafe arcs fall back to original route");

        var revised = Route(Vector3.zero, Quaternion.Euler(0, 8, 0) * Vector3.forward * 4f);
        var merge = ArdyRoomStreamTrajectory.Build(revised, new Vector3(0, .95f, 0), 0, .65f, .65f,
            Quaternion.identity, Vector3.zero, 1, validateRootSegment: (_, __) => true);
        Audit(merge, Vector3.zero, 0, Quaternion.identity, Vector3.zero, 1, Vector3.zero);
        Check(merge[0].rootPosition.z > .025f, "initial curve preserves forward momentum");
        Check(Mathf.Abs(merge[0].headingDegrees) < 4.501f, "initial curve does not teleport heading");
        Reject(() => ArdyRoomStreamTrajectory.Build(revised, new Vector3(0, .95f, 0), 0, .65f, .65f,
            Quaternion.identity, Vector3.zero, 1, validateRootSegment: (_, __) => false), "route-control-initial-turn");

        // An endpoint closer than the braking distance cannot reset speed or authorize overshoot.
        Reject(() => ArdyRoomStreamTrajectory.Build(Route(Vector3.zero, Vector3.forward * .05f),
            new Vector3(0, .95f, 0), 0, .65f, .65f, Quaternion.identity, Vector3.zero, 1), "route-control-braking-distance");
        Reject(() => ArdyRoomStreamTrajectory.Build(Route(Vector3.zero, Vector3.back * 4f),
            new Vector3(0, .95f, 0), 0, .65f, .65f, Quaternion.identity, Vector3.zero, 1,
            validateRootSegment: (_, __) => true), "route-control-initial-turn");

        var micro = ArdyRoomStreamTrajectory.Build(Route(Vector3.zero, Vector3.right * 2f), Vector3.zero, 0, .0048f, .65f,
            Quaternion.identity, Vector3.zero, 1, validateRootSegment: (a, b) => Vector3.Distance(a, b) <= .5f);
        Check(micro[0].rootPosition.z > .000001f && micro[0].rootPosition.z < .00002f,
            "a real millimetre/s root velocity receives its checked physical braking distance before a turn");
        Audit(micro, Vector3.zero, 0, Quaternion.identity, Vector3.zero, 1, Vector3.zero);

        foreach (float lookaheadLength in new[] { .65f, .5f })
        {
            var rolling = ArdyRoomStreamTrajectory.Build(Route(Vector3.zero, Vector3.forward * lookaheadLength),
                new Vector3(0, .95f, 0), 0, .25f, .25f, Quaternion.identity, Vector3.zero, 1, terminalSpeed: .25f);
            Vector3 prior = Vector3.zero;
            foreach (var sample in rolling)
            {
                Check(Mathf.Abs(Vector3.Distance(prior, sample.rootPosition) * 20f - .25f) < .0001f,
                    "moving lookahead covering the horizon does not insert a periodic stop");
                prior = sample.rootPosition;
            }
        }
        var shortRolling = ArdyRoomStreamTrajectory.Build(Route(Vector3.zero, Vector3.forward * .2f),
            new Vector3(0, .95f, 0), 0, .25f, .25f, Quaternion.identity, Vector3.zero, 1, terminalSpeed: .25f);
        Check(shortRolling[39].rootPosition.z <= .200001f && Vector3.Distance(shortRolling[38].rootPosition, shortRolling[39].rootPosition) < .00001f,
            "finite safe lookahead is a real stop when it cannot cover two seconds");

        var stop = ArdyRoomStreamTrajectory.Build(Route(Vector3.zero, Vector3.forward * .5f),
            new Vector3(0, .95f, 0), 0, .65f, .65f, Quaternion.identity, Vector3.zero, 1);
        float previousSpeed = .65f; Vector3 previousPoint = Vector3.zero;
        foreach (var target in stop)
        {
            float sampleSpeed = Vector3.Distance(previousPoint, target.rootPosition) * 20f;
            Check(previousSpeed - sampleSpeed <= .0601f, "comfortable braking remains at most 1.2 m/s squared");
            Check(target.rootPosition.z <= .500001f, "stopping endpoint is never overrun");
            previousSpeed = sampleSpeed; previousPoint = target.rootPosition;
        }

        foreach (float scale in new[] { .6f, 1f, 1.4f })
        {
            Quaternion rotation = Quaternion.Euler(0, 37f, 0);
            Vector3 anchor = new Vector3(2, -.7f, -1), offset = new Vector3(.025f, 0, .035f);
            Vector3 root = anchor - rotation * offset;
            var route = Route(root, root + rotation * Vector3.forward * 6f);
            var targets = ArdyRoomStreamTrajectory.Build(route, new Vector3(0, .95f, 0), 0, 0, 1.2f,
                rotation, anchor, scale, rootOffset: offset);
            Audit(targets, Vector3.zero, 0, rotation, anchor, scale, offset);
            var turn = ArdyRoomStreamTrajectory.Build(Route(root, root + rotation * Vector3.back * 2), new Vector3(0, .95f, 0),
                0, 0, .65f, rotation, anchor, scale, rootOffset: offset);
            foreach (var sample in turn) Check(Vector3.Distance(Root(sample, rotation, anchor, scale, offset), root) < .00001f,
                "opposite-heading first window turns without root translation; pelvis offset rotates around root");
            Audit(turn, Vector3.zero, 0, rotation, anchor, scale, offset);
            Check(Mathf.Abs(Mathf.DeltaAngle(0, turn[39].headingDegrees)) > 150f, "turn is meaningful progress before translation");
        }

        // Receding-horizon updates must not rebuild an increasingly tight corner as the
        // native seam approaches it. Include a small measured position/yaw disagreement.
        var stableCorners = new[] { Vector3.zero, new Vector3(.04f, 0, .43f), new Vector3(.17f, 0, .66f),
            new Vector3(.37f, 0, .76f), new Vector3(1.4f, 0, .76f), new Vector3(1.9f, 0, .65f) };
        var state = new ArdyRoomStreamTrajectory.PathState();
        Vector3 seam = Vector3.zero;
        float seamHeading = 0, seamSpeed = 0;
        for (int step = 0; step < 16; step++)
        {
            int next = ClosestSegment(stableCorners, seam);
            var currentCorners = new Vector3[stableCorners.Length - next + 1]; currentCorners[0] = seam;
            for (int i = next; i < stableCorners.Length; i++) currentCorners[i - next + 1] = stableCorners[i];
            int queries = 0;
            bool StableGuard(Vector3 a, Vector3 b)
            {
                queries++;
                for (int j = 0; j <= 8; j++)
                    if (DistanceToRoute(stableCorners, Vector3.Lerp(a, b, j / 8f)) > .12f) return false;
                return true;
            }
            var values = ArdyRoomStreamTrajectory.Build(Route(currentCorners), seam, seamHeading, seamSpeed, .65f,
                Quaternion.identity, Vector3.zero, 1, validateRootSegment: StableGuard, continuity: state);
            Check(queries <= 96, "continuation uses the same finite geometry budget");
            Audit(values, seam, seamHeading, Quaternion.identity, Vector3.zero, 1, Vector3.zero);
            seam = values[7].rootPosition; seamSpeed = state.CommittedSpeed;
            seamHeading = values[7].headingDegrees;
            Check(state.HasPlan && seamSpeed >= 0, "accepted-prefix continuation exposes its instantaneous speed");
            if (step == 1) { seam.z -= .008f; seamHeading += .7f; }
            if (Vector3.Distance(seam, stableCorners[stableCorners.Length - 1]) < .03f && seamSpeed < .03f) break;
        }
        Check(seam.x > .4f, "native-sized seam error must not block before the first furniture corner");

        // No shared NavMesh waypoint is necessary in an open room: keep the current
        // tangent while the moving user's endpoint and requested speed keep changing.
        state.Clear(); seam = Vector3.zero; seamHeading = 0; seamSpeed = 0;
        for (int step = 0; step < 24; step++)
        {
            float desired = step < 5 ? .65f : step < 14 ? 1f : .35f;
            Vector3 goal = seam + Quaternion.Euler(0, Mathf.Sin(step * .15f) * 30, 0) * Vector3.forward * 3f;
            int queries = 0; float largestQuery = 0;
            bool OpenRoomGuard(Vector3 a, Vector3 b)
            {
                queries++; largestQuery = Mathf.Max(largestQuery, Vector3.Distance(a, b));
                return Vector3.Distance(a, b) <= .5f;
            }
            var values = ArdyRoomStreamTrajectory.Build(Route(seam, goal), seam, seamHeading, seamSpeed, desired,
                Quaternion.identity, Vector3.zero, 1, terminalSpeed: desired, validateRootSegment: OpenRoomGuard, continuity: state);
            Audit(values, seam, seamHeading, Quaternion.identity, Vector3.zero, 1, Vector3.zero);
            Check(queries <= 96 && largestQuery <= .25001f, "long clear connectors respect the live guard step limit and shared budget");
            if (step > 0) Check(state.LastPlanningMode == "warm-continuation", "a clear multi-metre join must not discard continuity as a false step-distance collision");
            seam = values[7].rootPosition; seamHeading = values[7].headingDegrees; seamSpeed = state.CommittedSpeed;
        }
        Check(seam.z > 3, "continuous open-room goal/speed changes keep making progress");

        // Fixed soak-r9 mechanism: the actual accepted root was about 1.1 cm ahead
        // while committed control speed was .187 m/s near a planned stop. These are
        // controlled geometry/kinematics, not a claim to replay the native history.
        // Nearest-position progress would leave ~1.08 cm (hard speed ~.161 m/s)
        // instead of the committed-time ~2.19 cm braking distance.
        foreach (float headingError in new[] { 0f, .7f, -1.2f })
        {
            var aheadState = new ArdyRoomStreamTrajectory.PathState();
            const float entrySpeed = .507f;
            float stopDistance = entrySpeed * entrySpeed / (2f * .8f);
            Vector3 bend = Vector3.forward * stopDistance;
            var before = ArdyRoomStreamTrajectory.Build(Route(Vector3.zero, bend, bend + Vector3.right * 3),
                Vector3.zero, 0, entrySpeed, entrySpeed, Quaternion.identity, Vector3.zero, 1,
                validateRootSegment: (_, __) => false, continuity: aheadState);
            Vector3 measured = before[7].rootPosition + Vector3.forward * .011f;
            float committedSpeed = aheadState.CommittedSpeed;
            Check(Mathf.Abs(committedSpeed - .187f) < .001f, "r9 mechanism fixture retains the recorded committed speed");
            Check(Mathf.Sqrt(2 * 1.2f * (bend.z - measured.z)) < committedSpeed,
                "nearest-position progress demonstrably consumes the required braking distance");
            int queries = 0;
            bool CheckedRoom(Vector3 a, Vector3 b) { queries++; return Vector3.Distance(a, b) <= .5f; }
            var continued = ArdyRoomStreamTrajectory.Build(Route(measured, bend, bend + Vector3.right * 3),
                measured, headingError, committedSpeed, entrySpeed, Quaternion.identity, Vector3.zero, 1,
                validateRootSegment: CheckedRoom, continuity: aheadState);
            Check(aheadState.LastPlanningMode == "warm-continuation" && queries <= 96,
                "time-based checked continuation survives fixed forward tracking error without fresh curvature");
            Audit(continued, measured, headingError, Quaternion.identity, Vector3.zero, 1, Vector3.zero);
            float firstMovement = Vector3.Distance(measured, continued[0].rootPosition);
            Check(firstMovement > .005f && firstMovement <= committedSpeed * .05f + .00001f,
                "positive committed velocity physically brakes rather than resetting or snapping");
            Check(aheadState.LastHardInitialSpeed >= committedSpeed - .0001f,
                "unchanged hard braking envelope admits the preserved committed-time stop");
        }

        // A moving user's abrupt near or reverse target retains real momentum.
        // In this open checked fixture a planned stop/turn may be needed; a narrow
        // real room can still reject that same geometry without weakening its guard.
        foreach (Vector3 relativeGoal in new[] { Vector3.back * 2, Vector3.forward * .1f, Vector3.left * .2f })
        {
            var changedState = new ArdyRoomStreamTrajectory.PathState();
            var initial = ArdyRoomStreamTrajectory.Build(Route(Vector3.zero, Vector3.forward * 4),
                Vector3.zero, 0, .2f, .2f, Quaternion.identity, Vector3.zero, 1,
                validateRootSegment: (a, b) => Vector3.Distance(a, b) <= .5f, continuity: changedState);
            Vector3 measured = initial[7].rootPosition;
            int queries = 0;
            var changed = ArdyRoomStreamTrajectory.Build(Route(measured, measured + relativeGoal),
                measured, initial[7].headingDegrees, changedState.CommittedSpeed, .2f,
                Quaternion.identity, Vector3.zero, 1,
                validateRootSegment: (a, b) => { queries++; return Vector3.Distance(a, b) <= .5f; }, continuity: changedState);
            Check(queries <= 96, "sudden changed-goal connector has bounded room checks");
            Audit(changed, measured, 0, Quaternion.identity, Vector3.zero, 1, Vector3.zero);
            Check(changed[0].rootPosition.z > measured.z + .005f,
                "reverse/near target cannot instantly erase positive velocity or reverse heading");
        }

        var speedState = new ArdyRoomStreamTrajectory.PathState();
        var deceleration = ArdyRoomStreamTrajectory.Build(Route(Vector3.zero, Vector3.forward * .2f), Vector3.zero, 0, .65f, .65f,
            Quaternion.identity, Vector3.zero, 1, continuity: speedState);
        float lastMean = Vector3.Distance(deceleration[7].rootPosition, deceleration[6].rootPosition) * 20;
        Check(lastMean - speedState.CommittedSpeed > .015f && lastMean - speedState.CommittedSpeed <= .0301f,
            "50ms mean speed is not reused as instantaneous speed under nominal or hard braking");
        state.Clear(); Check(!state.HasPlan && state.CommittedSpeed == 0, "session cancellation discards geometry and speed state");
        var rejectedState = new ArdyRoomStreamTrajectory.PathState();
        bool typedBraking = false;
        try {
            ArdyRoomStreamTrajectory.Build(Route(Vector3.zero, Vector3.forward * .02f), Vector3.zero, 0, .49f, .65f,
                Quaternion.identity, Vector3.zero, 1, continuity: rejectedState);
        } catch (ArdyRoomStreamTrajectory.KinematicsException error) {
            typedBraking = error.Code == "braking-distance";
        }
        Check(typedBraking && rejectedState.LastPlanningMode == "new-path" && rejectedState.LastWarmExitReason == "no-continuation" &&
            rejectedState.LastHardInitialSpeed > .2f && rejectedState.LastHardInitialSpeed < .23f,
            "Recorded .490 m/s braking mechanism remains rejected and identifies the exact warm exit/hard speed.");
        rejectedState.Clear();
        Check(rejectedState.LastWarmExitReason == null && rejectedState.LastHardInitialSpeed == 0,
            "A new operation cannot inherit a former control diagnostic.");
        return checks;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    private static float DistanceToRoute(Vector3[] corners, Vector3 point)
    {
        float distance = float.PositiveInfinity;
        for (int i = 1; i < corners.Length; i++)
        {
            Vector3 delta = corners[i] - corners[i - 1];
            float t = delta.sqrMagnitude < .0000001f ? 0 : Mathf.Clamp01(Vector3.Dot(point - corners[i - 1], delta) / delta.sqrMagnitude);
            distance = Mathf.Min(distance, Vector3.Distance(point, corners[i - 1] + delta * t));
        }
        return distance;
    }

    private static int ClosestSegment(Vector3[] corners, Vector3 point)
    {
        int chosen = 1; float distance = float.PositiveInfinity;
        for (int i = 1; i < corners.Length; i++)
        {
            Vector3 delta = corners[i] - corners[i - 1];
            float t = delta.sqrMagnitude < .0000001f ? 0 : Mathf.Clamp01(Vector3.Dot(point - corners[i - 1], delta) / delta.sqrMagnitude);
            float error = Vector3.Distance(point, corners[i - 1] + delta * t);
            if (error < distance) { distance = error; chosen = i; }
        }
        return chosen;
    }

    private static ArdyRoomRoute Route(params Vector3[] corners)
    {
        float length = 0;
        for (int i = 1; i < corners.Length; i++) length += Vector3.Distance(corners[i - 1], corners[i]);
        var route = new ArdyRoomRoute();
        typeof(ArdyRoomRoute).GetProperty("Corners", BindingFlags.Instance | BindingFlags.Public).SetValue(route, corners);
        typeof(ArdyRoomRoute).GetProperty("Length", BindingFlags.Instance | BindingFlags.Public).SetValue(route, length);
        typeof(ArdyRoomRoute).GetProperty("GroundY", BindingFlags.Instance | BindingFlags.Public).SetValue(route, corners[0].y);
        return route;
    }
}
