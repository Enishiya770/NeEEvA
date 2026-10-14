using System.Linq;
using Newtonsoft.Json.Linq;

/// <summary>A next intention has its own source; task receipts never become new instructions.</summary>
public static class WorkNextIntentProtocol
{
    public const string Contract =
        "\n[事项与下一意图分别判定] work_status只描述原用户事项。next_intent_source=none/current-work/autonomous；" +
        "current-work表示仍欠原请求的执行或回答，next_step_id填写taskSequence中尚可执行的步骤id；" +
        "仅欠回答/结果解释或没有步骤表时留空。失败步骤不能凭意图重新变为pending，恢复须有执行器的明确预算和条件。" +
        "failedStepIds中的步骤没有可执行的next_step_id；若只欠失败说明，intent应写说明实际原因和尚未完成部分，planning_focus=other。" +
        "不要把解释失败写成再次执行；有执行器正在恢复时等待真实结果，不同时另起重试。" +
        "完成事件与当前状态要按时间分别判断：任务按约定完成条件实际完成后，环境随后变化不会撤销那次完成。" +
        "一次性的观察、播放、操作、到达都适用；只有原要求明确包含持续保持或持续跟踪，变化才属于原任务。" +
        "已到达后用户又移开，原到达仍完成；已观察后物件变化，原观察仍完成。不能用现在的位置或状态倒推当时未完成。" +
        "用户的禁止/停留要求是后续约束；没有违背证据时，不要把它制造成需行动、确认或反复解释的待办。" +
        "原请求完成后可以根据新的环境/相对位置/交流动机主动行动，此时work_status=closed，next_intent_source=autonomous，" +
        "proceed=true，intent写新的自主意图，next_step_id留空；不需要用户重复命令，不能声称旧任务未完成或重做已完成步骤。" +
        "尚欠独立问题的答案、工具结果或原任务步骤时不能通过改成autonomous绕过；最新取消、停留和其他用户限制始终有效。" +
        "自主意图是未来选择，不是事实来源；涉及用户已经说过/做过/同意过什么，只能依据真实对话或有出处的回忆，不能从人设、愿望、靠近或完成工具倒推出用户事件。" +
        "没有依据时可自由选择不依赖该前提的新话题或保持观察，不得把猜测写成既成事实。" +
        "没有值得推进的下一意图时source=none、proceed=false。planning_focus只表示下一意图需要的信息：" +
        "current-user-body=仅处理角色与当前用户的身体位置/跟随/停止，scene=物件或空间观察，other=回答或其他任务，unknown=不确定。" +
        "这三个字段只说明来源和注意范围，不授权工具，不把已有到达事实改成当前仍在身边，也不把一次性靠近变成持续跟随。";

    public static bool ValidExtension(JObject value)
    {
        return OptionalEnum(value, "next_intent_source", "none", "current-work", "autonomous") &&
            OptionalEnum(value, "planning_focus", "current-user-body", "scene", "other", "unknown") &&
            (value["next_step_id"] == null || value["next_step_id"].Type == JTokenType.String && ((string)value["next_step_id"]).Length <= 64);
    }

    public static string ExtendReviewSchema(string schema)
    {
        var result = JObject.Parse(schema);
        var fields = (JObject)result["properties"];
        fields["next_intent_source"] = new JObject { ["type"] = "string", ["enum"] = new JArray("none", "current-work", "autonomous") };
        fields["next_step_id"] = new JObject { ["type"] = "string", ["maxLength"] = 64 };
        fields["planning_focus"] = new JObject { ["type"] = "string", ["enum"] = new JArray("current-user-body", "scene", "other", "unknown") };
        foreach (string name in new[] { "next_intent_source", "next_step_id", "planning_focus" }) ((JArray)result["required"]).Add(name);
        return result.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static bool OptionalEnum(JObject value, string name, params string[] values) =>
        value[name] == null || value[name].Type == JTokenType.String && values.Contains((string)value[name]);

    public static bool AllStepsCompleted(JObject sequence) => sequence?["steps"] is JArray steps && steps.Count > 0 &&
        steps.OfType<JObject>().Count() == steps.Count && steps.OfType<JObject>().All(s => (string)s["status"] == "completed");

    public static bool HasReadyStep(JObject sequence, string selected = null) => sequence?["steps"] is JArray steps &&
        RoomTaskSequenceProtocol.Ready(steps).Values<string>().Any(id => string.IsNullOrEmpty(selected) || selected == id);

    public static JObject Progress(JObject sequence)
    {
        if (!(sequence?["steps"] is JArray steps)) return null;
        return new JObject {
            ["allStepsCompleted"] = AllStepsCompleted(sequence),
            ["readyStepIds"] = RoomTaskSequenceProtocol.Ready(steps),
            ["failedStepIds"] = new JArray(steps.OfType<JObject>().Where(s => (string)s["status"] == "failed").Select(s => s["id"].DeepClone())),
            ["scope"] = "Execution ledger only. An independent answer can still be owed. Completed events stay completed after the world changes; a new autonomous intention needs its own current decision and action identity. Failed steps require executor-supported bounded recovery, never implicit replay."
        };
    }
}
