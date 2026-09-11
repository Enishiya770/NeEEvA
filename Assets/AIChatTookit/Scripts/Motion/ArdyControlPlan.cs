using System;
using System.Globalization;

namespace NeEEvA.Motion
{
    /// <summary>Geometric arm goals and a bounded local rotation; never a motion preset or ARDY prediction.</summary>
    [Serializable]
    public sealed class ArdyControlPlan
    {
        public string left = "none", right = "none";
        public float leftBend = 8, rightBend = 8;
        // Derived by the XML parser only when a palm goal leaves this elbow unspecified.
        public bool leftBendAuto, rightBendAuto;
        // keep adds no palm constraint, preserving the pre-palm execution behavior.
        public string leftPalm = "keep", rightPalm = "keep";
        public string joint = "none", axis = "up";
        public float amplitude = 10;
        public int cycles = 2;
        public float seconds = 3.2f;
        public string end = "idle";
        // Only the high-level compiler selects adaptive timing. Existing compose plans stay legacy.
        public string timingPolicy = "legacy", tempo = "natural";
        public bool timingDurationFixed;

        public void Validate()
        {
            if (!IsArmGoal(left) || !IsArmGoal(right))
                throw new ArgumentException("Arm goals must be none, current, forward, outward, up, down, forward-up or outward-up.");
            Range(leftBend, 0, 110, nameof(leftBend));
            Range(rightBend, 0, 110, nameof(rightBend));
            if (!IsPalmGoal(leftPalm) || !IsPalmGoal(rightPalm))
                throw new ArgumentException("Palm goals must be keep, partner, up, down, inward or outward.");
            if ((leftPalm != "keep" && left == "none") || (rightPalm != "keep" && right == "none"))
                throw new ArgumentException("A palm goal needs its corresponding arm direction or current-pose hold.");
            if ((leftBendAuto && (left == "none" || left == "current" || leftPalm == "keep")) ||
                (rightBendAuto && (right == "none" || right == "current" || rightPalm == "keep")))
                throw new ArgumentException("Automatic elbow solving requires a directional arm goal and an explicit palm goal.");
            if (joint != "none" && joint != "left-wrist" && joint != "right-wrist" && joint != "wrists" && joint != "head")
                throw new ArgumentException("Unknown controlled joint.");
            if (axis != "up" && axis != "right" && axis != "forward" && axis != "palm-normal")
                throw new ArgumentException("Rotation axis must be character-relative up, right, forward or the constrained palm-normal.");
            if (axis == "palm-normal" &&
                ((joint != "left-wrist" && joint != "right-wrist" && joint != "wrists") ||
                 ((joint == "left-wrist" || joint == "wrists") && leftPalm == "keep") ||
                 ((joint == "right-wrist" || joint == "wrists") && rightPalm == "keep")))
                throw new ArgumentException("palm-normal is only for wrist motion with an explicit palm goal for every moving hand.");
            Range(amplitude, 1, joint == "head" ? 12 : 20, nameof(amplitude));
            if (cycles < 1 || cycles > 3) throw new ArgumentException("cycles must be 1 to 3.");
            Range(seconds, 1, 6, nameof(seconds));
            if (timingPolicy != "legacy" && timingPolicy != "adaptive-v1")
                throw new ArgumentException("Timing policy must be legacy or adaptive-v1.");
            if (tempo != "gentle" && tempo != "natural" && tempo != "brisk")
                throw new ArgumentException("Tempo must be gentle, natural or brisk.");
            if (timingDurationFixed && timingPolicy != "adaptive-v1")
                throw new ArgumentException("A fixed adaptive duration requires adaptive-v1 timing.");
            if ((timingPolicy == "legacy" || (timingDurationFixed && joint != "none")) && cycles / (double)seconds > 1.5)
                throw new ArgumentException("Local motion frequency must not exceed 1.5 cycles per second.");
            if (end != "idle" && end != "hold") throw new ArgumentException("end must be idle or hold.");
            if (left == "none" && right == "none" && joint == "none")
                throw new ArgumentException("A control plan needs an arm goal or local joint motion.");
            if ((joint == "left-wrist" || joint == "wrists") && left == "none")
                throw new ArgumentException("Left wrist motion needs an explicit left arm goal or current-pose hold.");
            if ((joint == "right-wrist" || joint == "wrists") && right == "none")
                throw new ArgumentException("Right wrist motion needs an explicit right arm goal or current-pose hold.");
        }

        public ArdyControlPlan Copy() => new ArdyControlPlan {
            left = left, right = right, leftBend = leftBend, rightBend = rightBend,
            leftPalm = leftPalm, rightPalm = rightPalm,
            leftBendAuto = leftBendAuto, rightBendAuto = rightBendAuto,
            joint = joint, axis = axis, amplitude = amplitude, cycles = cycles, seconds = seconds, end = end,
            timingPolicy = timingPolicy, tempo = tempo, timingDurationFixed = timingDurationFixed
        };

        public override string ToString()
        {
            string result = string.Format(CultureInfo.InvariantCulture,
                "left={0} (bend {1:0.##}, palm {10}, auto {12}), right={2} (bend {3:0.##}, palm {11}, auto {13}), joint={4}, axis={5}, amplitude={6:0.##}, cycles={7}, seconds={8:0.##}, end={9}",
                left, leftBend, right, rightBend, joint, axis, amplitude, cycles, seconds, end, leftPalm, rightPalm, leftBendAuto, rightBendAuto);
            return timingPolicy == "legacy" ? result : result + ", timing=" + timingPolicy + ", tempo=" + tempo + ", fixedSeconds=" + timingDurationFixed;
        }

        private static bool IsArmGoal(string value) => value == "none" || value == "current" || value == "forward" ||
            value == "outward" || value == "up" || value == "down" || value == "forward-up" || value == "outward-up";

        private static bool IsPalmGoal(string value) => value == "keep" || value == "partner" || value == "up" ||
            value == "down" || value == "inward" || value == "outward";

        private static void Range(float value, float minimum, float maximum, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < minimum || value > maximum)
                throw new ArgumentException(name + " must be finite and within " + minimum.ToString(CultureInfo.InvariantCulture) +
                    " to " + maximum.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }
}
