using System;
using Newtonsoft.Json.Linq;

/// <summary>Bounded preparation timing with corrections, resumption, and cancelled work.</summary>
public static class PreparedListeningPolicyRegression
{
    private static int checks;
    public static int RunOrThrow()
    {
        checks = 0;
        var policy = new PreparedListeningPolicy();
        var evidence = new PreparedTurnBoundaryEvidence
        {
            CaptureSerial = 1, TextRevision = 1, ActivityRevision = 1, Transcript = "停下。",
            Recording = true, AsrHealthy = true, AudioMs = 600,
        };
        float delay; string reason;
        Check(policy.TryDelay(evidence, 10, 10f, -100f, 4f, out delay, out reason) && Near(delay, .2f),
            "short stop first stable preparation is eligible at .2s");
        Check(policy.MarkSent(evidence, 10, 10.2f), "first dispatch consumes one capture slot");
        Check(!policy.NeedsLatest(evidence, 10), "unchanged text is not repeatedly submitted");
        evidence.AudioMs += 500;
        Check(!policy.NeedsLatest(evidence, 10), "new coverage alone does not multiply model requests");
        evidence.Transcript = "停下。不，继续跟随我。"; evidence.TextRevision++;
        Check(policy.TryDelay(evidence, 10, 10.6f, 10.2f, 4f, out delay, out reason) && Near(delay, .6f),
            "one correction refresh may run after one second without legacy four-second delay");
        Check(policy.MarkSent(evidence, 10, 11.2f), "latest correction consumes second slot");
        // Even cancelled requests remain spent: no reset API exists for the same capture.
        evidence.Transcript = "等等，不要跟随。"; evidence.TextRevision++;
        Check(policy.TryDelay(evidence, 10, 11.4f, 11.2f, 4f, out delay, out reason) && Near(delay, 3.8f),
            "third latest request returns to four-second spacing after cancellation/correction");
        Check(policy.MarkSent(evidence, 10, 15.2f), "third and final ordinary request");
        evidence.Transcript = "请继续。"; evidence.TextRevision++;
        Check(!policy.TryDelay(evidence, 10, 19.2f, 15.2f, 4f, out delay, out reason) &&
            reason == "capture-budget-exhausted", "long rapidly edited capture cannot flood requests");

        evidence.CaptureSerial++;
        Check(policy.TryDelay(evidence, 11, 15.3f, 15.2f, 4f, out delay, out reason) && Near(delay, .9f),
            "new recording resets capture budget but still respects global wire floor");
        Check(policy.MarkSent(evidence, 11, 16.2f), "new recording dispatch");
        evidence.ActivityRevision++;
        Check(policy.NeedsLatest(evidence, 11), "real resumed voice can reprepare even before ASR words change");
        evidence.Singing = true;
        Check(!policy.NeedsLatest(evidence, 11), "singing cannot enter ordinary boundary preparation");
        evidence.Singing = false; evidence.MelodyActive = true;
        Check(!policy.NeedsLatest(evidence, 11), "melodic evidence blocks ordinary preparation");
        evidence.MelodyActive = false; evidence.Recording = false;
        Check(!policy.NeedsLatest(evidence, 11), "closed recording cannot restart low-priority work");
        evidence.Recording = true; evidence.AsrHealthy = false;
        Check(!policy.NeedsLatest(evidence, 11), "failed ASR cannot be worked around by preparation");

        JObject schema = PreparedListeningProtocol.CreateSchema();
        Check(schema["properties"]["quiet_boundary"]["properties"]["action"]["enum"].ToString().Contains("complete"), "schema exposes semantic closure");
        Check(!schema.ToString().Contains("interrupt_user"), "conditional schema excludes interruption");
        Check((string)schema["required"][0] == "quiet_boundary" &&
            (string)schema["properties"]["inner_reaction"]["type"] == "string" &&
            (int)schema["properties"]["inner_reaction"]["maxLength"] == 40 &&
            (string)schema["required"][4] == "intention" && (string)schema["required"][6] == "inner_reaction",
            "one bounded reaction is required without displacing the boundary");
        string prompt = PreparedListeningProtocol.BuildPrompt("不，别关灯。");
        Check(prompt.Contains("不，别关灯。") && prompt.Contains("没有新声音") && prompt.Contains("完整的否定请求也可以闭合"),
            "full correction and new-sound contract preserved");
        Check(prompt.Contains("可直接展示给用户的角色心里话") && prompt.Contains("可留空，不必每次表态") &&
            prompt.Contains("不表示用户已说完") && prompt.Contains("不得声称任务或动作已经完成"),
            "public character reaction remains independent of draft, closure, and execution");
        Check(prompt.Contains("按实际句子的语言填写") &&
            prompt.Contains("\"inner_reaction\":\"\",\"draft\":\"\""),
            "reaction-only responses have an explicit language and empty-draft wire contract");
        Check((int)schema["properties"]["draft"]["maxLength"] == 0 &&
            schema["properties"]["intention"]["properties"]["evidence"] != null &&
            prompt.Contains("本阶段准备意图而非未来答复") && prompt.Contains("精确原文"),
            "one request prepares evidenced intentions instead of speculative spoken commitments");
        Check(prompt.Contains("open/uncertain 时只能用 tentative 或 none") &&
            prompt.Contains("新文字到达后旧反应立即撤回"), "unfinished semantics cannot become an interpretation preview");
        string singing = PreparedListeningProtocol.BuildPrompt("接下来唱第二段", "singing", "acoustic=melody");
        Check(singing.Contains("acoustic=melody") && singing.Contains("不是意图原文证据") &&
            singing.Contains("接下来唱第二段"), "same protocol separates exact transcript from acoustic observation");
        Check(prompt.Contains("state 与 quiet_boundary 是两项独立判断") &&
            prompt.Contains("domain分类的是当前表达所涉及的目标能力/主题") &&
            prompt.Contains("不列尚未发生的演示") && prompt.Contains("不在内容两侧额外添加引号"),
            "intent completeness, target domain and verbatim evidence have independent contracts");
        return checks;
    }
    private static bool Near(float a, float b) => Math.Abs(a - b) < .001f;
    private static void Check(bool success, string label)
    { checks++; if (!success) throw new InvalidOperationException("Prepared listening policy: " + label); }
}
