using System;
using System.Collections.Generic;
using System.Text;

/// <summary>Authority of one formal response, never a mutable provider mode.</summary>
public enum FormalResponseStage
{
    General,
    CommittedSpatialReply,
    FirstReplyRouting,
    SpatialGoalResult
}

/// <summary>Only executable skill instructions are projected. User/audio evidence,
/// memory, personality and actual pending/completed work remain separate and intact.</summary>
public static class FormalResponseContextPolicy
{
    public const string SpatialSkillScope =
        "[本次回复的能力范围]\n这次仅组织公开回答，既可能是已有任务的结果回复，也可能是没有新动作决定的只读问答；是否规划、提交或执行动作只以对应事实为准，不再次发出工具或动作。" +
        "先结合完整本轮原话和此前实际交付的对话确定用户现在的问题。声学类别、ASR重复备选、场景图片和旧任务都是证据，不是新请求或默认话题；不要用它们替代本轮语义。询问先前说法、状态或故障原因时，用相关历史回执解释；询问当前所见时，才围绕当前视觉证据回答。" +
        "以下能力仍然存在；省略详细执行语法只适用于这一次回复，不代表失去能力、取消或完成了别的事项。" +
        "原话、录音/演唱观察、记忆及当前事项中的未完成唱歌或动作意图仍然有效；用户唱过不等于要求你唱，" +
        "未实际播放或执行的事项不得说成已经完成。需要后续处理的意图可以如实保留或说明。";

    public static string SkillContext(FormalResponseStage stage, string active, string catalog)
    {
        if (stage != FormalResponseStage.CommittedSpatialReply && stage != FormalResponseStage.FirstReplyRouting) return active;
        var result = new StringBuilder(stage == FormalResponseStage.FirstReplyRouting
            ? FirstReplySkillScope : SpatialSkillScope);
        if (!string.IsNullOrWhiteSpace(catalog)) result.Append("\n\n").Append(catalog);
        var names = new List<string>();
        const string marker = "[已加载技能: ";
        int cursor = 0;
        while (!string.IsNullOrEmpty(active) && (cursor = active.IndexOf(marker, cursor, StringComparison.Ordinal)) >= 0)
        {
            // These headers are authored by LLM.SetActiveSkills, not inferred from
            // user/audio content. Malformed or unknown free-form context stays intact.
            int end = active.IndexOf(']', cursor + marker.Length);
            if (end < 0) return active;
            string name = active.Substring(cursor + marker.Length, end - cursor - marker.Length);
            if (name.Length == 0 || name.Length > 120 || name.IndexOfAny(new[] {'\r', '\n'}) >= 0) return active;
            if (!names.Contains(name)) names.Add(name);
            cursor = end + 1;
        }
        if (names.Count == 0 && !string.IsNullOrWhiteSpace(active) &&
            !string.Equals(active.Trim(), (catalog ?? "").Trim(), StringComparison.Ordinal)) return active;
        if (names.Count > 0) result.Append("\n已加载并仍可用于后续适当阶段的技能：").Append(string.Join("、", names));
        return result.ToString();
    }

    public const string FirstReplySkillScope =
        "[本次首答的能力范围]\n你仍能使用全部已有能力：对话、观察/转头、场景移动/跟随/停止、" +
        "自由身体动作、物件检查及已登记设备操作、演唱/回唱、录音素材处理、歌曲与长期记忆。" +
        "本请求只选择普通对话ordinary、只读证据回答evidence或完整工具流程tools，不执行这些能力，所以只列概览而不重复工具语法。" +
        "省略语法不表示不支持、已执行或取消了该能力。只看已有或原位新鲜视野、记忆和既有执行/故障事实来回答可选evidence；真正要求动作、换视角搜索、操作工具或改变任务授权时选tools。" +
        "完整原话、声学证据、当前结果与未知、未完成事项仍以附带事实为准；不能把用户唱过误解为要求回唱。";
}
