using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Motion;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Real post-VRM timing acceptance. The existing geometry/palm harness is reused unchanged.
public static partial class ArdyConstraintRegression
{
    [Serializable] private sealed class TempoPose
    {
        public int frame;
        public double time, realtime;
        public string phase;
        public float leftAngle, rightAngle, headAngle;
        public Quaternion leftRelative, rightRelative, headRelative;
    }
    [Serializable] private sealed class TempoDerivative
    {
        public string joint;
        public int points, directionChanges;
        public double minimumAngle, maximumAngle, maximumGap, minimumGap;
        public double firstCrossing, lastCrossing, measuredFrequencyHz;
        public double peakVelocity, peakAcceleration, velocityP95, accelerationP95;
        public double startAngle, endAngle, firstVelocity, lastVelocity;
        public double angleStepAtStart, angleStepAtEnd;
        public string derivativeScope = "Signed actual hand-relative-forearm/head-relative-neck angle; finite differences on nonuniform saved game-time intervals. External whole-parent motion excluded; mapping/Animator local residuals remain measured.";
    }
    [Serializable] private sealed class TempoCase
    {
        public string fixture, name, plan, outcome, rejection, feedback;
        public ArdyResolvedTiming resolved;
        public double requestAt, firstActingAt = -1, lastActingAt = -1, firstHoldingAt = -1;
        public double preparationLatency, lastSavedAt = -999, dispatchPoseJump, returnAnimatorError;
        public bool completed, durationFixedPreserved, measuredTempoMatched, snapshotIsIndependent, publicEndHonored;
        public double maximumObservedActingSeconds, firstReturningAt = -1, completedAt = -1;
        public string phase;
        public List<TempoPose> samples = new List<TempoPose>();
        public List<TempoDerivative> derivatives = new List<TempoDerivative>();
        public List<TempoPhaseMotion> phaseMotion = new List<TempoPhaseMotion>();
    }
    [Serializable] private sealed class TempoPhaseMotion
    {
        public string joint, phase;
        public int intervals;
        public double peakRelativeAngularVelocity, peakRelativeAngularAcceleration, accumulatedAngle, maximumAdjacentAngle;
        public double transitionAngle, transitionInterval;
        public string meaning = "Actual relative-joint quaternion log differences at saved real timestamps, including preparation/return. Engineering observation; commanded-angle bounds exclude external Animator contributions.";
    }
    [Serializable] private sealed class TempoEvidence
    {
        public string status, unityVersion, timingSourceSha256, tempoHarnessSha256, playerSha256, sessionSha256, planSha256;
        public string route = "Actual Animator / Player / VRM / Observer12000 / test12500; constraints plus adaptive local curves. No manual Tick, audio or network generation.";
        public string baseline = "User-rejected slow reference: legacy amplitude10deg, cycles2, seconds3.2 = nominal0.625Hz. It is not asserted to be natural.";
        public string scope = "Measured angular derivatives and transitions are engineering evidence, not medical safety or subjective naturalness. Analytic Evaluate checks are separately labelled mathematical checks.";
        public bool mathChecksPassed, fixedDurationRejected, oldResourceHashesUnchanged, publicFixtureProvided;
        public string publicFixtureSource, publicFixtureSha256, publicRawTag, publicRawUser;
        public int publicUserTurn;
        public List<string> resourceHashesBefore = new List<string>(), resourceHashesAfter = new List<string>();
        public List<TempoCase> cases = new List<TempoCase>();
    }
    private static TempoEvidence tempoEvidence;
    private static int tempoIndex;
    private static readonly string[] TempoResourceNames = { "left-wave", "right-wave", "nod", "shake-head" };
    private static readonly Dictionary<string, TempoCase> ActiveTempoCases = new Dictionary<string, TempoCase>();

    public static void RunTempoBatch() => BeginBatch(false, true, false, true);

    private sealed partial class Fixture
    {
        private Quaternion tempoLeftNeutral, tempoRightNeutral, tempoHeadNeutral;
        private Vector3 tempoLeftAxis, tempoRightAxis, tempoHeadAxis;
        private CaseReport tempoCurrent;
        public Quaternion TempoRelative(bool left) => Quaternion.Inverse(Bone(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm).rotation)
            * Bone(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand).rotation;
        public Quaternion TempoHeadRelative() => Quaternion.Inverse(Bone(HumanBodyBones.Neck).rotation) * Bone(HumanBodyBones.Head).rotation;
        private Vector3 TempoAxis(bool left)
        {
            Vector3 world = Plan.axis == "palm-normal" ? PalmGoal(left) : Plan.axis == "right" ? Root.transform.right : Plan.axis == "forward" ? Root.transform.forward : Root.transform.up;
            return Bone(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm).InverseTransformDirection(world).normalized;
        }
        public void ObserveTempo()
        {
            if (Current == null || !ActiveTempoCases.TryGetValue(Report.id, out var item) || item.name != Current.name) return;
            bool first = tempoCurrent != Current;
            tempoCurrent = Current;
            string phase = Player.ConstraintPhase;
            var left = TempoRelative(true); var right = TempoRelative(false); var head = TempoHeadRelative();
            if (first || phase == "preparing")
            {
                tempoLeftNeutral = left; tempoRightNeutral = right; tempoHeadNeutral = head;
                tempoLeftAxis = TempoAxis(true); tempoRightAxis = TempoAxis(false);
                tempoHeadAxis = Bone(HumanBodyBones.Neck).InverseTransformDirection(Plan.axis == "right" ? Root.transform.right : Plan.axis == "forward" ? Root.transform.forward : Root.transform.up).normalized;
            }
            var observation = Player.ConstraintObservation;
            if (observation?.timing != null)
            {
                item.maximumObservedActingSeconds = Math.Max(item.maximumObservedActingSeconds, observation.actingSeconds);
                item.resolved = observation.timing.Copy();
                // Mutating the returned diagnostic copy must never mutate the running schedule.
                float duration = observation.timing.actualSeconds;
                observation.timing.actualSeconds = -321;
                Check(Player.ConstraintObservation.timing.actualSeconds == duration, "Timing observation leaked a mutable live schedule");
                item.snapshotIsIndependent = true;
            }
            if (phase == "acting")
            {
                if (item.firstActingAt < 0) { item.firstActingAt = Time.timeAsDouble; item.preparationLatency = item.firstActingAt - item.requestAt; }
                item.lastActingAt = Time.timeAsDouble;
            }
            if (phase == "holding" && item.firstHoldingAt < 0) item.firstHoldingAt = Time.timeAsDouble;
            if (phase == "returning" && item.firstReturningAt < 0) item.firstReturningAt = Time.timeAsDouble;
            if (phase == "completed" && item.completedAt < 0) item.completedAt = Time.timeAsDouble;
            if (Time.timeAsDouble - item.lastSavedAt >= 1.0 / 80 || phase != item.phase)
            {
                item.samples.Add(new TempoPose { frame = Time.frameCount, time = Time.timeAsDouble, realtime = Time.realtimeSinceStartupAsDouble,
                    phase = phase, leftAngle = TempoTwist(left * Quaternion.Inverse(tempoLeftNeutral), tempoLeftAxis),
                    rightAngle = TempoTwist(right * Quaternion.Inverse(tempoRightNeutral), tempoRightAxis),
                    headAngle = TempoTwist(head * Quaternion.Inverse(tempoHeadNeutral), tempoHeadAxis),
                    leftRelative = left, rightRelative = right, headRelative = head });
                item.lastSavedAt = Time.timeAsDouble; item.phase = phase;
            }
        }
    }

    private static float TempoTwist(Quaternion q, Vector3 axis)
    {
        double projected = q.x * (double)axis.x + q.y * (double)axis.y + q.z * (double)axis.z;
        double angle = 2 * Math.Atan2(projected, q.w) * 180 / Math.PI;
        while (angle > 180) angle -= 360;
        while (angle < -180) angle += 360;
        return (float)angle;
    }

    private static ArdyControlPlan TempoPlan(int index)
    {
        var p = new ArdyControlPlan { left = "forward", right = "forward", leftBend = 40, rightBend = 40,
            leftPalm = "partner", rightPalm = "partner", joint = "wrists", axis = "palm-normal", amplitude = 10, cycles = 2,
            seconds = 3.2f, end = "hold", timingPolicy = index == 0 ? "legacy" : "adaptive-v1", tempo = "natural" };
        if (index == 1) p.tempo = "gentle";
        if (index == 3) p.tempo = "brisk";
        if (index == 4) { p.joint = "head"; p.axis = "right"; p.amplitude = 8; p.cycles = 1; p.tempo = "gentle"; }
        if (index == 5) p.timingDurationFixed = true;
        return p;
    }
    private static string TempoName(int index) => new[] { "legacy-slow-wrists", "adaptive-gentle-wrists", "adaptive-natural-wrists", "adaptive-brisk-wrists", "adaptive-gentle-head", "adaptive-explicit-duration-wrists" }[index];

    private static void BeginTempo(Fixture f, string name, ArdyControlPlan plan, bool viaChat = true)
    {
        var before = new[] { f.TempoRelative(true), f.TempoRelative(false), f.TempoHeadRelative() };
        var item = new TempoCase { fixture = f.Report.id, name = name, plan = JsonUtility.ToJson(plan), requestAt = Time.timeAsDouble };
        tempoEvidence.cases.Add(item); ActiveTempoCases[f.Report.id] = item;
        f.Begin(name, plan, viaBridge:viaChat, viaController:!viaChat);
        item.dispatchPoseJump = Math.Max(Quaternion.Angle(before[0], f.TempoRelative(true)), Math.Max(Quaternion.Angle(before[1], f.TempoRelative(false)), Quaternion.Angle(before[2], f.TempoHeadRelative())));
        Check(item.dispatchPoseJump < .05, "Dispatch changed the actual rendered pose before an update");
        nextCapture = Time.time;
    }

    private static void SampleTempoStages(float elapsed)
    {
        foreach (var f in fixtures) f.ObserveTempo();
        switch (stage)
        {
            case 0:
                if (elapsed < .5f) return;
                tempoEvidence = new TempoEvidence { unityVersion = Application.unityVersion,
                    timingSourceSha256 = Hash("Assets/AIChatTookit/Scripts/Motion/ArdyAdaptiveTiming.cs"),
                    tempoHarnessSha256 = Hash("Assets/Editor/ArdyConstraintTempoRegression.cs"),
                    playerSha256 = report.playerSha256, sessionSha256 = report.sessionSha256, planSha256 = report.planSha256 };
                tempoEvidence.resourceHashesBefore = TempoResourceHashes();
                CheckTempoMath();
                foreach (var f in fixtures) { f.EnsureBridge(true); f.SetPartner(new Vector3(0, f.InRoot(f.Bone(HumanBodyBones.Head)).y, 2)); }
                tempoIndex = 0;
                foreach (var f in fixtures) BeginTempo(f, TempoName(tempoIndex), TempoPlan(tempoIndex));
                Advance(); break;
            case 1:
                CaptureTempoIfDue(); CheckNoFailure();
                if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 10, "Tempo curve did not complete its actual target/curve"); return; }
                foreach (var f in fixtures) { FinishTempoCase(f, true); f.Live.Cancel("tempo-case-completed"); }
                Advance(); break;
            case 2:
                CaptureTempoIfDue();
                if (elapsed < .7f) return;
                CheckReturned();
                foreach (var f in fixtures) ActiveTempoCases[f.Report.id].returnAnimatorError = f.Current.returnToAnimatorMaxErrorDegrees;
                tempoIndex++;
                if (tempoIndex < 6)
                {
                    foreach (var f in fixtures) BeginTempo(f, TempoName(tempoIndex), TempoPlan(tempoIndex));
                    stage = 1; stageStarted = Time.time; SaveReport(); return;
                }
                CheckTempoOrdering(); CheckFixedTempoRejected();
                foreach (var f in fixtures) BeginTempo(f, "adaptive-hold-source", TempoPlan(2));
                Advance(); break;
            case 3:
                CheckNoFailure();
                if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 10, "Tempo hold source did not complete"); return; }
                foreach (var f in fixtures)
                {
                    FinishTempoCase(f, true);
                    var p = TempoPlan(3); p.left = p.right = "current";
                    BeginTempo(f, "held-pose-to-brisk-current", p);
                }
                Advance(); break;
            case 4:
                CheckNoFailure();
                if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 10, "Held-current tempo transition did not complete"); return; }
                foreach (var f in fixtures) { FinishTempoCase(f, true); f.Live.Cancel("tempo-hold-explicit-stop"); }
                Advance(); break;
            case 5:
                if (elapsed < .7f) return; CheckReturned();
                foreach (var f in fixtures) BeginTempo(f, "adaptive-interrupt-source", TempoPlan(2));
                Advance(); break;
            case 6:
                CheckNoFailure();
                if (!fixtures.All(f => f.Player.ConstraintPhase == "acting" && f.Player.ConstraintObservation.actingSeconds >= .45f))
                { Check(elapsed < 6, "Interrupt source did not become active"); return; }
                foreach (var f in fixtures)
                {
                    FinishTempoCase(f, false); var p = TempoPlan(3); p.left = p.right = "current";
                    BeginTempo(f, "active-replaced-by-brisk-current", p);
                }
                Advance(); break;
            case 7:
                CheckNoFailure();
                if (!fixtures.All(f => f.Player.IsHoldingPose)) { Check(elapsed < 10, "Active replacement failed to complete"); return; }
                foreach (var f in fixtures) { FinishTempoCase(f, true); f.Live.Cancel("tempo-finished"); }
                Advance(); break;
            case 8:
                if (elapsed < .7f) return; CheckReturned();
                if (BeginOptionalTempoPublic()) { Advance(); return; }
                FinishTempoBatch(); break;
            case 9:
                CaptureTempoIfDue(); CheckNoFailure();
                foreach (var f in fixtures) if (f.Plan.end == "idle") Check(!f.Player.IsHoldingPose, "Original public end=idle was silently changed to hold");
                if (fixtures.Any(f => f.Plan.end == "hold" ? !f.Player.IsHoldingPose : f.Player.IsConstrained || f.Player.ConstraintPhase != "completed"))
                { Check(elapsed < 12, "Frozen public tempo plan did not honor its actual end condition"); return; }
                foreach (var f in fixtures)
                {
                    FinishTempoCase(f, true); var item = ActiveTempoCases[f.Report.id];
                    if (f.Plan.end == "idle")
                    {
                        Check(item.firstReturningAt >= 0 && item.completedAt >= item.firstReturningAt && item.firstHoldingAt < 0,
                            "Public idle completion omitted actual acting-to-return-to-completed observations");
                        f.CheckUntouched(true); item.returnAnimatorError = f.Current.returnToAnimatorMaxErrorDegrees;
                    }
                    else f.Live.Cancel("public-tempo-completed");
                    item.publicEndHonored = true;
                }
                Advance(); break;
            case 10:
                if (elapsed < .7f) return; CheckReturned();
                foreach (var f in fixtures) ActiveTempoCases[f.Report.id].returnAnimatorError = f.Current.returnToAnimatorMaxErrorDegrees;
                FinishTempoBatch(); break;
        }
    }

    private static void CaptureTempoIfDue()
    {
        if (Time.time < nextCapture || fixtures[0].Current == null) return;
        // One avatar per frame keeps the brisk curve temporally observable; all four are numerically sampled.
        if (tempoIndex == 0 || tempoIndex == 2 || tempoIndex == 3 || tempoIndex == 4 || stage == 9) Capture(fixtures[0]);
        nextCapture = Time.time + .04f;
    }

    private static void FinishTempoCase(Fixture f, bool complete)
    {
        var item = ActiveTempoCases[f.Report.id];
        // The old end check projects a hand's root-space rotation onto a fixed
        // neutral. Allowed forearm roll can cross its zero without any extra
        // wrist cycle. Keep those legacy statistics, but validate the actual
        // local joint curve below with the same exact cycle/amplitude rules.
        f.EndCase(false); item.completed = complete; item.outcome = complete ? "actual-curve-completed" : "intentionally-interrupted";
        Check(item.resolved != null && item.snapshotIsIndependent, "Missing real timing observation");
        if (complete)
        {
            Check(item.firstActingAt >= 0, "No actual acting measurements");
            Check(item.maximumObservedActingSeconds >= item.resolved.actualSeconds - .001, "Complete requested action duration was not actually observed");
            Check(f.Current.enteredActing && f.Current.actingFrames >= 20, "No sufficiently sampled actual acting phase");
            if (f.Plan.timingPolicy == "adaptive-v1")
            {
                Check(item.resolved.preparationSeconds >= .2 && item.resolved.preparationSeconds <= 2.7, "Adaptive preparation outside its resolved budget");
                Check(item.resolved.velocityBound <= item.resolved.velocityLimit + .01 && item.resolved.accelerationBound <= item.resolved.accelerationLimit + .01,
                    "Resolved local curve exceeded its own full-curve engineering bound");
                item.durationFixedPreserved = !f.Plan.timingDurationFixed || Math.Abs(item.resolved.actualSeconds - f.Plan.seconds) < 1e-5;
                Check(item.durationFixedPreserved, "Explicit seconds were silently changed");
            }
        }
        var joints = f.Plan.joint == "head" ? new[] { "head" } : f.Plan.joint == "left-wrist" ? new[] { "left" } : f.Plan.joint == "right-wrist" ? new[] { "right" } : new[] { "left", "right" };
        foreach (string joint in joints)
        {
            var derivative = MeasureTempo(item, joint); item.derivatives.Add(derivative);
            if (!complete) continue;
            Check(derivative.points >= 20, "Actual curve is undersampled");
            Check(derivative.maximumGap <= .12, "Actual capture gap is too large for rhythm acceptance");
            Check(derivative.directionChanges == f.Plan.cycles * 2 - 1, "Actual relative-joint motion did not make the exact requested cycles");
            double tolerance = joint == "head" ? 2.5 : 1.5;
            Check(Math.Abs(derivative.minimumAngle + f.Plan.amplitude) <= tolerance && Math.Abs(derivative.maximumAngle - f.Plan.amplitude) <= tolerance,
                "Actual relative-joint curve did not reach its requested positive/negative amplitude");
            if (f.Plan.cycles >= 2)
            {
                double expected = item.resolved.resolvedFrequencyHz;
                Check(Math.Abs(derivative.measuredFrequencyHz - expected) <= expected * .12 + .015, "Actual rhythm frequency differs from resolved timing");
                item.measuredTempoMatched = true;
            }
            // Measurements are retained independently. Local mapping residuals and sampling
            // precision are not converted into a fabricated exact derivative guarantee.
            Check(derivative.peakVelocity > 0 && derivative.peakAcceleration > 0, "Actual derivatives were not measured");
        }
    }

    private static TempoDerivative MeasureTempo(TempoCase item, string joint)
    {
        Func<TempoPose, double> angle = p => joint == "left" ? p.leftAngle : joint == "right" ? p.rightAngle : p.headAngle;
        var points = item.samples.Where(p => p.phase == "acting").ToArray();
        var output = new TempoDerivative { joint = joint, points = points.Length, minimumGap = double.MaxValue, measuredFrequencyHz = -1 };
        if (points.Length < 3) return output;
        output.minimumAngle = points.Min(angle); output.maximumAngle = points.Max(angle); output.startAngle = angle(points[0]); output.endAngle = angle(points[points.Length - 1]);
        var velocity = new List<double>(); var acceleration = new List<double>(); var velocityTimes = new List<double>(); var crossings = new List<double>();
        int lastSign = 0; double lastStableTime = 0, lastStableAngle = 0;
        for (int i = 0; i < points.Length; i++)
        {
            double value = angle(points[i]); int sign = Math.Abs(value) < .15 ? 0 : Math.Sign(value);
            if (sign != 0 && lastSign != 0 && sign != lastSign)
            {
                output.directionChanges++;
                // Confirm both sides beyond the same0.15deg hysteresis. Envelope
                // floating-point noise near zero cannot invent a half-cycle.
                crossings.Add(lastStableTime + (points[i].time - lastStableTime) * Math.Abs(lastStableAngle) / Math.Max(1e-9, Math.Abs(value - lastStableAngle)));
            }
            if (sign != 0) { lastSign = sign; lastStableTime = points[i].time; lastStableAngle = value; }
            if (i == 0) continue;
            double dt = points[i].time - points[i - 1].time;
            Check(dt > 0, "Nonmonotonic real tempo sample times");
            output.maximumGap = Math.Max(output.maximumGap, dt); output.minimumGap = Math.Min(output.minimumGap, dt);
            double prev = angle(points[i - 1]);
            velocity.Add((value - prev) / dt); velocityTimes.Add((points[i].time + points[i - 1].time) / 2);
        }
        for (int i = 1; i < velocity.Count; i++) acceleration.Add((velocity[i] - velocity[i - 1]) / (velocityTimes[i] - velocityTimes[i - 1]));
        output.peakVelocity = velocity.Select(Math.Abs).Max(); output.peakAcceleration = acceleration.Select(Math.Abs).Max();
        output.velocityP95 = Percentile95(velocity); output.accelerationP95 = Percentile95(acceleration);
        output.firstVelocity = velocity[0]; output.lastVelocity = velocity[velocity.Count - 1];
        if (crossings.Count >= 3) { output.firstCrossing = crossings[0]; output.lastCrossing = crossings[crossings.Count - 1]; output.measuredFrequencyHz = (crossings.Count - 1) / (2 * (output.lastCrossing - output.firstCrossing)); }
        var firstIndex = item.samples.FindIndex(p => p == points[0]); var lastIndex = item.samples.FindIndex(p => p == points[points.Length - 1]);
        if (firstIndex > 0) output.angleStepAtStart = Math.Abs(angle(points[0]) - angle(item.samples[firstIndex - 1]));
        if (lastIndex + 1 < item.samples.Count) output.angleStepAtEnd = Math.Abs(angle(item.samples[lastIndex + 1]) - angle(points[points.Length - 1]));
        return output;
    }
    private static double Percentile95(List<double> values) { var a = values.Select(Math.Abs).OrderBy(v => v).ToArray(); return a[Math.Min(a.Length - 1, (int)Math.Floor(a.Length * .95))]; }

    private static List<TempoPhaseMotion> MeasureTempoPhases(TempoCase item)
    {
        var result = new List<TempoPhaseMotion>();
        foreach (string joint in new[] { "left", "right", "head" })
        {
            Func<TempoPose, Quaternion> rotation = p => joint == "left" ? p.leftRelative : joint == "right" ? p.rightRelative : p.headRelative;
            var phases = new Dictionary<string, TempoPhaseMotion>(); Vector3 previousVelocity = Vector3.zero; double previousMidpoint = 0;
            for (int i = 1; i < item.samples.Count; i++)
            {
                var a = item.samples[i - 1]; var b = item.samples[i]; double dt = b.time - a.time;
                if (dt <= 0) continue;
                if (!phases.TryGetValue(b.phase, out var phase)) { phase = new TempoPhaseMotion { joint = joint, phase = b.phase }; phases[b.phase] = phase; }
                Quaternion delta = rotation(b) * Quaternion.Inverse(rotation(a));
                if (delta.w < 0) delta = new Quaternion(-delta.x, -delta.y, -delta.z, -delta.w);
                double length = Math.Sqrt(delta.x * (double)delta.x + delta.y * (double)delta.y + delta.z * (double)delta.z);
                double angle = 2 * Math.Atan2(length, delta.w) * 180 / Math.PI;
                Vector3 velocity = length < 1e-12 ? Vector3.zero : new Vector3(delta.x, delta.y, delta.z) * (float)(angle / length / dt);
                double midpoint = (a.time + b.time) / 2;
                phase.intervals++; phase.accumulatedAngle += angle; phase.maximumAdjacentAngle = Math.Max(phase.maximumAdjacentAngle, angle);
                phase.peakRelativeAngularVelocity = Math.Max(phase.peakRelativeAngularVelocity, velocity.magnitude);
                if (i > 1) phase.peakRelativeAngularAcceleration = Math.Max(phase.peakRelativeAngularAcceleration, (velocity - previousVelocity).magnitude / (midpoint - previousMidpoint));
                if (a.phase != b.phase) { phase.transitionAngle = angle; phase.transitionInterval = dt; }
                previousVelocity = velocity; previousMidpoint = midpoint;
            }
            result.AddRange(phases.Values);
        }
        return result;
    }

    private static void CheckTempoMath()
    {
        foreach (int index in new[] { 1, 2, 3, 4, 5 })
        {
            var p = TempoPlan(index); var t = ArdyAdaptiveTiming.Resolve(p);
            var zero = ArdyAdaptiveTiming.Evaluate(t, 0); var end = ArdyAdaptiveTiming.Evaluate(t, t.actualSeconds);
            Check(zero.value == 0 && zero.velocity == 0 && zero.acceleration == 0 && end.value == 0 && end.velocity == 0 && end.acceleration == 0, "Adaptive mathematical endpoint is not C2 zero");
            for (int i = 0; i <= 2048; i++)
            {
                var v = ArdyAdaptiveTiming.Evaluate(t, t.actualSeconds * i / 2048.0);
                Check(Math.Abs(v.velocity) <= t.velocityBound + .001 && Math.Abs(v.acceleration) <= t.accelerationBound + .001, "Mathematical sampled curve exceeded certified bounds");
            }
        }
        tempoEvidence.mathChecksPassed = true;
    }

    private static void CheckFixedTempoRejected()
    {
        var p = TempoPlan(4); p.amplitude = 12; p.seconds = 1; p.timingDurationFixed = true;
        foreach (var f in fixtures)
        {
            var item = new TempoCase { fixture = f.Report.id, name = "explicit-one-second-head-limit-rejection", plan = JsonUtility.ToJson(p), requestAt = Time.timeAsDouble };
            tempoEvidence.cases.Add(item);
            try { f.Live.RequestControlPlan(p, ++f.Generation); }
            catch (ArgumentException e) { item.rejection = e.Message; }
            catch (InvalidOperationException e) { item.rejection = e.Message; }
            Check(!string.IsNullOrEmpty(item.rejection) && item.rejection.Contains("adaptive-timing-unreachable"), "Infeasible explicit duration was not rejected");
            Check(!f.Player.IsConstrained, "Rejected explicit duration queued a live constraint");
            item.feedback = f.Live.DescribeMotionContext(); var context = JObject.Parse(item.feedback);
            Check(JsonNull(context, "controlPlan") && ContextPlanMatches(context, p) && !string.IsNullOrEmpty((string)context["lastControlError"]), "Rejected tempo lost precise Controller feedback");
            item.outcome = "expected-explicit-duration-rejection";
        }
        tempoEvidence.fixedDurationRejected = true;
    }

    private static void CheckTempoOrdering()
    {
        foreach (var fixture in fixtures)
        {
            double old = tempoEvidence.cases.First(c => c.fixture == fixture.Report.id && c.name == TempoName(0)).derivatives[0].measuredFrequencyHz;
            double gentle = tempoEvidence.cases.First(c => c.fixture == fixture.Report.id && c.name == TempoName(1)).derivatives[0].measuredFrequencyHz;
            double natural = tempoEvidence.cases.First(c => c.fixture == fixture.Report.id && c.name == TempoName(2)).derivatives[0].measuredFrequencyHz;
            double brisk = tempoEvidence.cases.First(c => c.fixture == fixture.Report.id && c.name == TempoName(3)).derivatives[0].measuredFrequencyHz;
            Check(natural > old * 1.25 && brisk > natural * 1.05 && natural > gentle * 1.2, "Adaptive choices did not materially change actual wrist cadence");
        }
    }

    private static bool BeginOptionalTempoPublic()
    {
        string path = Environment.GetEnvironmentVariable("ARDY_PUBLIC_TEMPO_FIXTURE");
        if (string.IsNullOrWhiteSpace(path)) return false;
        Check(File.Exists(path), "Frozen public tempo fixture is missing");
        var json = JObject.Parse(File.ReadAllText(path)); string tag = (string)json["rawMotionTag"], rawUser = (string)json["rawUser"];
        Check(!string.IsNullOrWhiteSpace(tag) && !string.IsNullOrWhiteSpace(rawUser) && (int)json["userTurn"] == 2,
            "Frozen public fixture must contain unchanged rawMotionTag, rawUser and first userTurn2");
        tempoEvidence.publicFixtureProvided = true; tempoEvidence.publicFixtureSource = path; tempoEvidence.publicFixtureSha256 = HashAbsoluteTempo(path); tempoEvidence.publicRawTag = tag;
        tempoEvidence.publicRawUser = rawUser; tempoEvidence.publicUserTurn = 2;
        File.Copy(path, Path.Combine(Output, "public-tempo-input.json"), true);
        foreach (var f in fixtures)
        {
            var ledger = (ArdyActionConstraintLedger)typeof(ChatSample).GetField("m_ActionConstraints", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(f.Chat);
            Check(ledger.CurrentUserTurn == 1, "First configured Chat ledger does not have the expected source1");
            typeof(ChatSample).GetMethod("RecordAcceptedMotionUserTurn", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(f.Chat, new object[] { rawUser });
            Check(ledger.CurrentUserTurn == 2 && ledger.CurrentUserText == rawUser, "Actual user source was not recorded before response generation");
            int generation = (int)typeof(ChatSample).GetMethod("BeginFormalResponseGeneration", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(f.Chat, null);
            string executable = tag;
            Check(DialogueMotionProtocol.TryExtract(ref executable, generation, 1, out var intent, out string rejection), "Original public tempo tag rejected: " + rejection);
            Check(intent.Name == "plan" && intent.ActionPlan != null, "Public fixture was not a real high-level plan");
            DialogueMotionIntent? compiled = null;
            Action<DialogueMotionIntent> listener = value => compiled = value;
            f.Chat.MotionIntentRequested += listener;
            var before = new[] { f.TempoRelative(true), f.TempoRelative(false), f.TempoHeadRelative() };
            try { typeof(ChatSample).GetMethod("DispatchDialogueMotion", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(f.Chat, new object[] { (DialogueMotionIntent?)intent, false }); }
            finally { f.Chat.MotionIntentRequested -= listener; }
            Check(compiled.HasValue && compiled.Value.ControlPlan != null && f.Player.IsConstrained, "Actual Chat source ledger/compiler/Bridge did not execute the original public plan");
            var plan = compiled.Value.ControlPlan;
            Check(plan.timingPolicy == "adaptive-v1", "Public plan did not select adaptive execution");
            Check(plan.axis == "palm-normal" || plan.joint == "head" || (plan.leftPalm == "keep" && plan.rightPalm == "keep"),
                "Public fixture axis needs an adaptive independent palm reference; do not silently alter its tag");
            var item = new TempoCase { fixture = f.Report.id, name = "public-qwen-original-tag-adaptive", plan = JsonUtility.ToJson(plan), requestAt = Time.timeAsDouble };
            tempoEvidence.cases.Add(item); ActiveTempoCases[f.Report.id] = item;
            f.PrepareConstraintCase(item.name, plan);
            item.dispatchPoseJump = Math.Max(Quaternion.Angle(before[0], f.TempoRelative(true)), Math.Max(Quaternion.Angle(before[1], f.TempoRelative(false)), Quaternion.Angle(before[2], f.TempoHeadRelative())));
            Check(item.dispatchPoseJump < .05, "Public dispatch changed rendered pose before an update");
            File.WriteAllText(Path.Combine(Output, "public-tempo-compiled-plan-" + f.Report.id + ".json"), JsonUtility.ToJson(plan, true));
        }
        nextCapture = Time.time;
        return true;
    }
    private static string HashAbsoluteTempo(string path) { using (var sha = System.Security.Cryptography.SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static List<string> TempoResourceHashes() => TempoResourceNames.Select(name => name + ":" + Hash("Assets/AIChatTookit/Resources/ARDY/" + name + ".json")).ToList();
    private static void FinishTempoBatch()
    {
        tempoEvidence.resourceHashesAfter = TempoResourceHashes();
        tempoEvidence.oldResourceHashesUnchanged = tempoEvidence.resourceHashesBefore.SequenceEqual(tempoEvidence.resourceHashesAfter);
        Check(tempoEvidence.oldResourceHashesUnchanged, "Tempo tests changed accepted local motion resources");
        Check(fixtures[0].Report.mouthMaximum - fixtures[0].Report.mouthMinimum > 30 && fixtures[0].Report.blinkMaximum - fixtures[0].Report.blinkMinimum > 50,
            "Actual expression updates did not coexist with tempo changes");
        Finish(null);
    }
    private static void SaveTempoReport()
    {
        if (tempoEvidence == null) return;
        tempoEvidence.status = report.status;
        foreach (var item in tempoEvidence.cases) item.phaseMotion = MeasureTempoPhases(item);
        var json = JObject.Parse(JsonUtility.ToJson(tempoEvidence, true));
        for (int i = 0; i < tempoEvidence.cases.Count; i++)
            if (tempoEvidence.cases[i].resolved == null) json["cases"][i]["resolved"] = JValue.CreateNull();
        File.WriteAllText(Path.Combine(Output, "tempo-regression.json"), json.ToString());
    }
}
