using System;
using Newtonsoft.Json.Linq;

/// <summary>Limits silent preparation; it never decides whether a recorder may close.</summary>
public sealed class PreparedListeningPolicy
{
    public const float StableSeconds = 0.20f;
    public const float FastRefreshInterval = 1.0f;
    public const float NormalRefreshInterval = 4.0f;
    public const int MaximumRequestsPerCapture = 3;
    public const int MinimumTranscriptChars = 2;
    private int capture, inputRevision, count;
    private PreparedTurnBoundaryEvidence lastSource;
    private float lastRequestedAt = -100f;
    public int Count => count;

    public bool NeedsLatest(PreparedTurnBoundaryEvidence source, int revision)
    {
        if (!source.Recording || source.CaptureSerial <= 0 || source.Singing || source.MelodyActive ||
            !source.AsrHealthy || source.AudioMs <= 0 || string.IsNullOrWhiteSpace(source.Transcript) ||
            source.Transcript.Trim().Length < MinimumTranscriptChars) return false;
        if (source.CaptureSerial != capture || revision != inputRevision) return true;
        return count < MaximumRequestsPerCapture && (count == 0 ||
            source.TextRevision != lastSource.TextRevision ||
            source.ActivityRevision != lastSource.ActivityRevision ||
            !string.Equals(source.Transcript, lastSource.Transcript, StringComparison.Ordinal));
    }

    public bool TryDelay(PreparedTurnBoundaryEvidence source, int revision, float now,
        float latestWireAt, float configuredInterval, out float delay, out string reason)
    {
        delay = 0f;
        if (!NeedsLatest(source, revision))
        {
            reason = source.CaptureSerial == capture && revision == inputRevision &&
                count >= MaximumRequestsPerCapture ? "capture-budget-exhausted" : "no-new-eligible-evidence";
            return false;
        }
        bool sameCapture = source.CaptureSerial == capture && revision == inputRevision;
        int sent = sameCapture ? count : 0;
        float interval = sent <= 1 ? FastRefreshInterval : Math.Max(NormalRefreshInterval, configuredInterval);
        float earliest = Math.Max(latestWireAt + FastRefreshInterval,
            sameCapture && sent > 0 ? lastRequestedAt + interval : now);
        delay = Math.Max(StableSeconds, earliest - now);
        reason = sent == 0 ? "first-stable-input" : sent == 1 ? "bounded-latest-refresh" : "normal-latest-refresh";
        return true;
    }

    public bool MarkSent(PreparedTurnBoundaryEvidence source, int revision, float now)
    {
        if (!NeedsLatest(source, revision)) return false;
        if (source.CaptureSerial != capture || revision != inputRevision)
        { capture = source.CaptureSerial; inputRevision = revision; count = 0; }
        count++; lastSource = source; lastRequestedAt = now;
        return true;
    }
}

/// <summary>Small schema shared by the prompt, transport, and validation tests.</summary>
public static class PreparedListeningProtocol
{
    public const string ConditionalScope =
        "这是语义轮次边界的条件准备，不是当前用户已经说完的事实声明。" +
        "评估 quiet_boundary 时请采用明确假设：这份原文之后没有修改、没有新声音，且ASR已覆盖成熟静音尾部。" +
        "这个假设只提供声学条件，不会补全缺失的语义。先核对末尾是否仍有未完成的纠正、否定或条件，" +
        "必要行为、对象或主句尚未明确时应继续倾听，不得借停顿、前半句或旧意图代填。完整性有合理歧义时选 continue/open。" +
        "只有本段当前意图已经表达完整，才可 closed 交给回应/行动规划；不必预测用户以后是否还会另起一句。" +
        "closed 不代表实际发声、工具执行或任务成功。continue 指继续倾听未完成的本段话，不是继续执行动作。" +
        "是否停止录音仍由程序核验最新真实证据；本请求只输出嵌套 quiet_boundary JSON，禁止实际发声、操作或记忆写入。";

    public static JObject CreateSchema() => new JObject
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JObject
        {
            ["quiet_boundary"] = new JObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JObject
                {
                    ["action"] = Enum("continue", "take_turn", "ask_user", "complete"),
                    ["confidence"] = Probability(), ["mode"] = Enum("speech", "singing", "uncertain"),
                    ["source"] = Enum("user", "background", "uncertain"),
                    ["turn_state"] = Enum("open", "closed", "uncertain"),
                    ["reason"] = new JObject { ["type"] = "string", ["maxLength"] = 16 },
                },
                ["required"] = new JArray("action", "confidence", "mode", "source", "turn_state", "reason"),
            },
            ["observed_mode"] = Enum("speech", "singing", "uncertain"),
            ["mode_confidence"] = Probability(),
            ["draft"] = new JObject { ["type"] = "string", ["maxLength"] = 20 },
            ["language"] = Enum("ja", "zh", "en"), ["confidence"] = Probability(),
        },
        ["required"] = new JArray("quiet_boundary", "observed_mode", "mode_confidence", "draft", "language", "confidence"),
    };

    public static string BuildPrompt(string transcript) =>
        "[内部实时倾听：尚未提交的条件准备；本请求不发声、不执行动作、不写历史]\n" +
        "截至采样时已识别的原文快照:\n" + transcript + "\n\n" +
        "先输出 quiet_boundary，采用假设：这份原文之后没有修改、没有新声音，ASR已覆盖成熟静音尾部。" +
        "判断分两步：先检查末尾有没有尚未补完的自我纠正、否定、条件或其他必要成分。" +
        "已经声明要改口，不等于修正后的意图已经说清；否定态度已经出现，也不等于否定的行为/对象已经明确。" +
        "若需替用户猜测后续才能知道修正后的请求，就 continue/open；不能用前半句的完整性或旧意图覆盖未完成的更正。" +
        "静音假设不会把残句变完整，完整性有合理歧义就继续听。" +
        "通过上述检查后，再判断这是否是一个独立完整的言语行为：问候、回答、请求本来可以很短。" +
        "语义清楚完整且没有未完成修正时，才可 closed；需要工具核验、请求停止动作或无需口头回答并不妨碍语义闭合。" +
        "这是对本段语义的判断，不是对目前是否录音或用户以后会不会另起一句的预测。" +
        "closed 时可选择 complete/take_turn，表示交给后续处理，不代表已经执行请求或必须立即发声。" +
        "continue 专指继续倾听用户尚未表达完整的本段话，不是让动作继续。" +
        "仍缺少必要内容、纠错/否定停在半句、换气前后语义未完、歌唱中途或完整性不确定时，应 continue/open。" +
        "完整的否定请求也可以闭合；不能根据单个词或标点判断。禁止 interrupt_user。" +
        "observed_mode 依据本轮真实输入，过去的跟唱约定不是此刻歌唱的证据。" +
        "draft 可以为空且不影响 closed 判断；仅在无需工具核验时写不超过20字的候选台词，不能声称动作已完成；language 必须对应 draft 的实际语言。" +
        "只输出紧凑 JSON，不要 Markdown 或控制标签，字段顺序如下：" +
        "{\"quiet_boundary\":{\"action\":\"continue|take_turn|ask_user|complete\",\"confidence\":0.0," +
        "\"mode\":\"speech|singing|uncertain\",\"source\":\"user|background|uncertain\"," +
        "\"turn_state\":\"open|closed|uncertain\",\"reason\":\"不超过16字\"}," +
        "\"observed_mode\":\"speech|singing|uncertain\",\"mode_confidence\":0.0," +
        "\"draft\":\"\",\"language\":\"ja|zh|en\",\"confidence\":0.0}";

    private static JObject Enum(params string[] values) => new JObject { ["type"] = "string", ["enum"] = new JArray(values) };
    private static JObject Probability() => new JObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1 };
}
