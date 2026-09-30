using Newtonsoft.Json.Linq;

public static partial class RecordingSingingDecisionCompiler
{
    public const string RecordingGoalTransitionContract =
        "\n仅当recovery_binding存在时，goal_transition表达对现有未完成目标的关系：continue保持原refs、范围、成功条件和音乐目标；" +
        "revise修正同一事项、replace明确换事项。revise/replace必须request_ref=source绑定本轮完整请求，" +
        "程序固定base_version并交独立审核；reviewRecovery.expected只是审核意见，不能代替用户原始意图或变更授权。" +
        "沿旧请求恢复只可continue，不能据旧请求缩减或替换目标。reply/handoff的goal_transition为none。";

    private static void AddRecordingGoalTransitionSchema(JObject properties, RecordingSingingDecisionContext context, string decision)
    {
        if (RecoveryFacts(context) == null) return;
        properties["goal_transition"] = new JObject { ["type"] = "string", ["enum"] = decision == "perform"
            ? new JArray("continue", "revise", "replace") : new JArray("none") };
    }

    private static bool TryCompileRecordingGoalTransition(JObject value, RecordingSingingDecisionContext context,
        string refs, string range, string expected, string intent, string evidence, bool recovering,
        out bool continuing, out string attributes, out string error)
    {
        continuing = false; attributes = ""; error = "";
        bool bound = RecoveryFacts(context) != null;
        string transition = (string)value["goal_transition"];
        if (!bound)
        {
            if (value.Property("goal_transition") != null) { error = "goal-transition-without-active-binding"; return false; }
            return true;
        }
        transition = transition ?? "continue"; // Previously saved decisions remain compatible for an unchanged target only.
        if (transition == "continue")
        {
            if (refs != context.Recovery.Refs || range != context.Recovery.Range || expected != context.Recovery.Expected || intent != "perform")
            { error = "current-goal-change-needs-explicit-transition"; return false; }
            continuing = true;
        }
        else if (transition == "revise" || transition == "replace")
        {
            if (recovering || (value.Property("request_ref") != null && (string)value["request_ref"] != "source") ||
                string.IsNullOrWhiteSpace(evidence) || evidence != context.UserText)
            { error = "goal-transition-requires-complete-current-request"; return false; }
            attributes = " change_quote=" + Attribute(evidence);
        }
        else { error = "invalid-recording-goal-transition"; return false; }
        attributes = " transition=\"" + transition + "\" base_version=\"" + context.Recovery.GoalVersion + "\"" + attributes;
        return true;
    }
}
