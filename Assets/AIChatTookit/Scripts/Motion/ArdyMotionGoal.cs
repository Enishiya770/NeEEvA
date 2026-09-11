using System;

namespace NeEEvA.Motion
{
    /// <summary>Necessary geometric wrist observations, not a motion command or full semantic specification.</summary>
    [Serializable]
    public sealed class ArdyMotionGoal
    {
        public string leftGoal = "any", rightGoal = "any";
        public float requiredStableSeconds = .2f;
        public bool HasTargets => leftGoal != "any" || rightGoal != "any";
        public ArdyMotionGoal Copy() => new ArdyMotionGoal { leftGoal = leftGoal, rightGoal = rightGoal, requiredStableSeconds = requiredStableSeconds };
        public void Validate()
        {
            if (!Supported(leftGoal) || !Supported(rightGoal))
                throw new ArgumentException("Motion goals must be any, down, forward, outward, near-head or above-head; finger goals are unsupported.");
            if (float.IsNaN(requiredStableSeconds) || float.IsInfinity(requiredStableSeconds) || requiredStableSeconds < .2f || requiredStableSeconds > 2)
                throw new ArgumentException("A measured motion goal needs0.2 to2 seconds of continuous observation.");
        }
        private static bool Supported(string goal) => goal == "any" || goal == "down" || goal == "forward" || goal == "outward" || goal == "near-head" || goal == "above-head";
    }
}
