using System;
using UnityEngine;

namespace NeEEvA.Motion
{
    [Serializable]
    public sealed class ArdyObservedArm
    {
        public bool available, targetSpecified, withinGoal;
        public string goal = "any", reason;
        public Vector3 shoulderCharacter, elbowCharacter, wristCharacter;
        public Vector3 wristFromShoulderNormalized, wristFromHeadNormalized, wristFromChestNormalized;
        public float armLengthMeters, extensionRatio, directionErrorDegrees, wristHeadDistanceNormalized;
        public ArdyObservedArm Copy() => (ArdyObservedArm)MemberwiseClone();
    }

    /// <summary>Post-VRM actual Humanoid positions, never source clip positions or ControlRig targets.</summary>
    [Serializable]
    public sealed class ArdyMotionPoseSample
    {
        public int frame, playbackSerial;
        public double timeSeconds;
        public bool available, headAvailable, chestAvailable, hipsAvailable;
        public string reason;
        public Vector3 rootWorldPosition, rootScale, headCharacter, chestCharacter, hipsCharacter;
        public Quaternion rootWorldRotation, headRotationCharacter;
        public ArdyObservedArm left = new ArdyObservedArm(), right = new ArdyObservedArm();
        public ArdyMotionPoseSample Copy()
        {
            var result = (ArdyMotionPoseSample)MemberwiseClone();
            result.left = left?.Copy(); result.right = right?.Copy(); return result;
        }
    }

    [Serializable]
    public sealed class ArdyMotionObservation
    {
        public string actionId, status = "unassessed", reason, finishReason;
        public string measurementSource = "actual-humanoid-after-player-blending-and-VRM";
        public string coordinateSystem = "Character rotation axes, +Y up,+Z forward,+X right; positions in world metres relative to avatar root; arm-relative quantities divided by actual two-segment arm length.";
        public string meaning = "reached means all specified necessary wrist regions were observed simultaneously for the required duration at least once. It does not mean complete semantics, finger shape, naturalness, current holding or task completion.";
        public bool active, finished, cancelled, hasGoals, everReached, currentlyWithinGoal;
        public bool fingerGoalsSupported = false, palmGoalsAssessed = false;
        public int sampleCount, validGoalSampleCount, skippedFrames, lastFrame = -1, playbackSerial;
        public int observationGapCount, unavailableGoalSampleCount;
        public double validCoverageSeconds;
        public double elapsedSeconds, observedSpanSeconds, stableSeconds, maximumStableSeconds, reachedAtSeconds = -1;
        public ArdyMotionGoal goal;
        public ArdyMotionPoseSample actual;
        public ArdyMotionObservation Copy()
        {
            var result = (ArdyMotionObservation)MemberwiseClone();
            result.goal = goal?.Copy(); result.actual = actual?.Copy(); return result;
        }
    }

    /// <summary>Explicit necessary geometry, independent of generated text and target rotations.</summary>
    public static class ArdyMotionGoalMeasurement
    {
        public const float DirectionToleranceDegrees = 30;
        public const float MinimumExtensionRatio = .55f;
        public const float NearHeadRadiusArmLengths = .55f;
        public const float AboveHeadHeightArmLengths = .12f;

        public static bool Evaluate(ArdyMotionPoseSample sample, ArdyMotionGoal goal, out bool known)
        {
            if (sample == null) { known = false; return false; }
            goal = goal ?? new ArdyMotionGoal(); goal.Validate();
            bool left = EvaluateArm(sample, sample.left, goal.leftGoal, true, out bool leftKnown);
            bool right = EvaluateArm(sample, sample.right, goal.rightGoal, false, out bool rightKnown);
            known = sample.available && leftKnown && rightKnown;
            return known && left && right;
        }
        private static bool EvaluateArm(ArdyMotionPoseSample sample, ArdyObservedArm arm, string goal, bool left, out bool known)
        {
            known = true;
            if (arm == null) { known = goal == "any"; return known; }
            arm.goal = goal; arm.targetSpecified = goal != "any"; arm.withinGoal = false;
            if (goal == "any") { arm.reason = "not-targeted"; return true; }
            if (!sample.available || !arm.available || !Finite(arm.armLengthMeters) || arm.armLengthMeters < .001f
                || !Finite(arm.wristFromShoulderNormalized) || !Finite(arm.extensionRatio))
            { known = false; arm.reason = "missing-or-invalid-actual-arm-bones"; return false; }
            if (goal == "near-head" || goal == "above-head")
            {
                if (!sample.headAvailable || !Finite(arm.wristFromHeadNormalized) || !Finite(arm.wristHeadDistanceNormalized))
                { known = false; arm.reason = "missing-or-invalid-actual-head"; return false; }
                if (goal == "above-head") arm.withinGoal = arm.wristFromHeadNormalized.y >= AboveHeadHeightArmLengths;
                else
                    arm.withinGoal = arm.wristHeadDistanceNormalized <= NearHeadRadiusArmLengths
                        && arm.wristFromHeadNormalized.y >= -.30f && arm.wristFromHeadNormalized.y <= .55f
                        && arm.wristFromHeadNormalized.x * (left ? -1 : 1) >= .08f;
                arm.reason = arm.withinGoal ? "within-specified-head-region" : "outside-specified-head-region";
            }
            else
            {
                Vector3 expected = goal == "down" ? Vector3.down : goal == "forward" ? Vector3.forward : left ? Vector3.left : Vector3.right;
                arm.directionErrorDegrees = Vector3.Angle(arm.wristFromShoulderNormalized, expected);
                arm.withinGoal = arm.extensionRatio >= MinimumExtensionRatio && arm.directionErrorDegrees <= DirectionToleranceDegrees;
                arm.reason = arm.withinGoal ? "within-specified-arm-region" : arm.extensionRatio < MinimumExtensionRatio ? "insufficient-shoulder-wrist-extension" : "arm-direction-outside-region";
            }
            return arm.withinGoal;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
