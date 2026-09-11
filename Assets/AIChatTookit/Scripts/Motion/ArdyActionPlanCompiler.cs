using System;
using System.Globalization;

namespace NeEEvA.Motion
{
    [Serializable]
    public sealed class ArdyCompiledActionPlan
    {
        public string name, description;
        public ArdyControlPlan controlPlan;
    }

    /// <summary>Compiles already merged, source-checked semantic values. Purpose never selects a pose or a clip.</summary>
    public static class ArdyActionPlanCompiler
    {
        public static ArdyCompiledActionPlan Compile(ArdyActionPlan action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            action.Validate();
            if (action.Effective("mode", "oscillate") == "free")
                return new ArdyCompiledActionPlan { name = "generate", description = action.text };
            bool hold = action.mode == "hold";
            var control = new ArdyControlPlan {
                left = action.Effective("left", "none"), right = action.Effective("right", "none"),
                leftPalm = action.Effective("leftPalm", "keep"), rightPalm = action.Effective("rightPalm", "keep"),
                joint = action.Effective("joint", "none"), end = action.Effective("end", "idle"),
                tempo = action.Effective("tempo", "natural"), timingPolicy = "adaptive-v1",
                timingDurationFixed = action.HasField("seconds")
            };
            if (!hold && control.joint == "none")
                throw new ArgumentException("oscillate needs an explicit moving joint; use hold for a static arm goal.");
            if ((control.left == "none" && action.HasField("leftBend")) ||
                (control.right == "none" && action.HasField("rightBend")))
                throw new ArgumentException("A fixed elbow angle requires a controlled arm direction; none cannot satisfy an elbow constraint.");
            control.axis = ResolveAxis(action.Effective("axis", "auto"), control);
            control.leftBend = action.HasField("leftBend") ? ArdyActionPlan.Number(action.leftBend, 0, 110, "leftBend") : 8;
            control.rightBend = action.HasField("rightBend") ? ArdyActionPlan.Number(action.rightBend, 0, 110, "rightBend") : 8;
            control.leftBendAuto = !action.HasField("leftBend") && control.left != "none" && control.left != "current" && control.leftPalm != "keep";
            control.rightBendAuto = !action.HasField("rightBend") && control.right != "none" && control.right != "current" && control.rightPalm != "keep";
            string size = action.Effective("size", "small");
            control.amplitude = control.joint == "head" ? (size == "large" ? 10 : size == "medium" ? 7 : 4)
                : (size == "large" ? 20 : size == "medium" ? 15 : 10);
            if (action.HasField("amplitude")) control.amplitude = ArdyActionPlan.Number(action.amplitude, 1, control.joint == "head" ? 12 : 20, "amplitude");
            control.cycles = action.HasField("cycles") ? int.Parse(action.cycles, CultureInfo.InvariantCulture)
                : ArdyActionPlan.RepeatCount(action.Effective("repeat", "twice"));
            if (action.HasField("seconds")) control.seconds = ArdyActionPlan.Number(action.seconds, 1, 6, "seconds");
            control.Validate();
            // Reject an impossible commanded schedule before the caller commits a
            // new constraint ledger. Measured pose/mesh reachability remains runtime work.
            ArdyAdaptiveTiming.Resolve(control);
            return new ArdyCompiledActionPlan { name = "compose", description = "", controlPlan = control };
        }

        private static string ResolveAxis(string axis, ArdyControlPlan plan)
        {
            if (axis != "auto") return axis;
            if (plan.joint == "none") return "up"; // Static placeholder, no rotation will be executed.
            if (plan.joint == "head") throw new ArgumentException("Head motion needs an explicit character-relative axis.");
            bool left = plan.joint == "left-wrist" || plan.joint == "wrists";
            bool right = plan.joint == "right-wrist" || plan.joint == "wrists";
            if ((left && plan.leftPalm == "keep") || (right && plan.rightPalm == "keep"))
                throw new ArgumentException("Automatic wrist axis needs an explicit palm goal for each moving hand.");
            return "palm-normal";
        }
    }
}
