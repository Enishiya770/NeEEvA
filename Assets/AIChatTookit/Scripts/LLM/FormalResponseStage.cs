using System;
using System.Collections.Generic;
using System.Text;

/// <summary>Authority of one formal response, never a mutable provider mode.</summary>
public enum FormalResponseStage
{
    General,
    CommittedSpatialReply,
    FirstReplyRouting
}

/// <summary>Only executable skill instructions are projected. User/audio evidence,
/// memory, personality and actual pending/completed work remain separate and intact.</summary>
public static class FormalResponseContextPolicy
{
    public const string SpatialSkillScope =
        "[本次回复的能力范围]\n空间任务已由本轮独立决策提交；这次只依据最新实际结果回应，不再次发出工具或动作。" +
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
        "本请求只决定普通对话或交回完整工具与审核流程，不执行这些能力，所以只列概览而不重复工具语法。" +
        "省略语法不表示不支持、已执行或取消了该能力。需要任何工具、动作或新的事实核验时选择 tools。" +
        "完整原话、声学证据、当前结果与未知、未完成事项仍以附带事实为准；不能把用户唱过误解为要求回唱。";
}
