using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

public static class PersonalSingingLearningResults
{
    public static string Describe(JObject value)
    {
        var lines = new List<string> { "候选仅作观察，正式裁切未启用。" };
        if (value == null) return string.Join("\n", lines);
        var result = value["result"] as JObject ?? value;
        string job = (string)value["status"];
        if (value["job_id"] != null) lines.Add("任务：" + State(job));
        if (value["recording_count"] != null) lines.Add("已存原音：" + (int)value["recording_count"] + " 段");
        if (value["independent_group_count"] != null) lines.Add("独立内容／来源组：" + (int)value["independent_group_count"]);
        if (value["label_seconds"] is JObject labels)
        {
            lines.Add("人工已知歌唱 " + Seconds(labels, "singing") + " 秒");
            lines.Add("人工讲话 " + Seconds(labels, "speech") + " 秒，环境音 " + Seconds(labels, "environment") + " 秒");
            lines.Add("排除／未决 " + Seconds(labels, "excluded") + " 秒");
        }
        var training = value["latest_training"] as JObject ?? result;
        if (training["status"] != null) lines.Add("学习：" + State((string)training["status"]));
        var candidate = value["current_candidate"] as JObject;
        bool provisional = (string)training["model_stage"] == "provisional" || (string)candidate?["model_stage"] == "provisional";
        if (provisional) lines.Add("已按你确认的例子试学；下次教学判断会使用个人试学版本。准确率还未经独立验证。");
        var fusion = candidate?["temporal_fusion"] as JObject ?? training["temporal_fusion"] as JObject;
        var sequence = candidate?["sequence_boundary"] as JObject ?? training["sequence_boundary"] as JObject;
        var localBoundary = candidate?["local_boundary"] as JObject ?? training["local_boundary"] as JObject;
        var categoryEvidence = candidate?["category_evidence"] as JObject ?? training["category_evidence"] as JObject;
        string categoryEvidenceStatus = (string)categoryEvidence?["status"];
        string localBoundaryStatus = (string)localBoundary?["status"];
        string sequenceStatus = (string)sequence?["status"];
        string fusionStatus = (string)fusion?["status"];
        if (categoryEvidenceStatus == "eligible")
            lines.Add("已结合说话、唱歌与环境音的区别细化区间；仍交给你检查确认。");
        else if (categoryEvidenceStatus == "shadow_rejected")
            lines.Add("已比较三类声音及其变化；仍有误收或漏唱，暂保留现有判断。");
        else if (localBoundaryStatus == "eligible")
            lines.Add("已分别学习开唱与结束位置；区间仍交给你检查确认。");
        else if (localBoundaryStatus == "shadow_rejected")
            lines.Add("已比较开唱与结束位置；误收或漏唱尚未改善到可采用，暂保留现有判断。");
        else if (sequenceStatus == "eligible")
            lines.Add("已用乐句连续性细化起止；区间仍交给你检查确认。");
        else if (sequenceStatus == "shadow_rejected")
            lines.Add("已比较更细的起止和连续性；仍有漏判或误收，暂保留现有判断。");
        else if (fusionStatus == "eligible")
            lines.Add("已加入音高变化和持续性辅助判断；区间仍交给你检查确认。");
        else if (fusionStatus == "shadow_rejected")
            lines.Add("音高融合已试学，但误收与漏唱仍有取舍；暂保留现有判断。");
        else if (fusionStatus == "failed" || fusionStatus == "deferred")
            lines.Add("音高辅助试学尚未完成；现有判断仍可使用。");
        if ((bool?)candidate?["invalidated"] == true || (bool?)result["invalidated"] == true)
            lines.Add("旧训练标注已修订，候选需重新学习。旧报告仍保留。");
        else if ((bool?)candidate?["out_of_date"] == true || (bool?)training["stale"] == true)
            lines.Add("新增资料尚未加入该候选；可继续试判，更新学习后再比较。");
        var reasons = value["personalization_reasons"] as JArray ?? training["reasons"] as JArray ?? value["readiness_reasons"] as JArray;
        if (reasons != null)
        {
            var seen = new HashSet<string>();
            foreach (var reason in reasons)
            {
                string text = Reason((string)reason);
                if (seen.Add(text)) lines.Add(text);
            }
        }
        if (result["validation_duration_confusion"] is JObject confusion)
        {
            lines.Add("开发留出结果（不是独立盲测）：");
            lines.Add("误收非歌声 " + Seconds(confusion, "fp_seconds") + " 秒");
            lines.Add("漏掉歌声 " + Seconds(confusion, "fn_seconds") + " 秒");
            lines.Add("保留歌声 " + Seconds(confusion, "tp_seconds") + " 秒");
        }
        if (result["model_id"] != null) lines.Add("已保存个人判断版本，仅用于可纠正的教学判断。");
        if (value["error"] is JObject error) lines.Add("任务未完成：" + Short((string)error["message"] ?? (string)error["code"]));
        if (job == "queued" || job == "running") lines.Add("可继续普通对话；后台任务不占用正式回复链。");
        return string.Join("\n", lines);
    }
    private static string Seconds(JObject row, string key) => ((double?)row[key] ?? 0).ToString("0.00", CultureInfo.InvariantCulture);
    private static string Short(string value) => string.IsNullOrEmpty(value) ? "请重试或查看服务状态" : value.Length > 160 ? value.Substring(0, 160) + "…" : value;
    private static string State(string value) => value == "queued" ? "已排队" : value == "running" ? "处理中" : value == "completed" ? "已结束" : value == "failed" ? "失败，可重试" : value == "interrupted" ? "已中断，可重试" : value == "candidate" ? "候选已保存" : value == "insufficient_data" ? "已确认材料不足" : "尚无可用候选";
    private static string Reason(string value)
    {
        if (value == "need_confirmed_singing_example") return "再教我一小段明确的本人歌唱，就能开始比较学习。";
        if (value == "need_confirmed_non_singing_example") return "再教我一小段讲话或环境音，让我学会它和歌唱的区别。";
        if (value == "need_at_least_3_independent_groups") return "还需至少 3 个独立内容／来源组；同歌重唱只算同组。";
        if (value == "train_needs_more_human_confirmed_groups") return "用于学习的已确认独立组不足，至少需 2 组。";
        if (value == "validation_needs_more_human_confirmed_groups") return "还缺独立的已确认留出组。";
        if (value != null && value.EndsWith("missing_positive_known_duration", StringComparison.Ordinal)) return "学习或留出部分还缺明确的本人歌唱区间。";
        if (value != null && value.EndsWith("missing_negative_known_duration", StringComparison.Ordinal)) return "学习或留出部分还缺明确的本人讲话／非歌唱区间。";
        return "资料还未满足学习条件；确认声源、标签及特征状态后重试。";
    }
}
