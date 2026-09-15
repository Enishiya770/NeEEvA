using System;
using System.Text.RegularExpressions;

public enum RoleOutputBoundaryPolicy
{
    // Only for already projected text and explicit legacy adapters, never new model output.
    LegacyCompatible,
    // Ordinary first reply: no executable action may be introduced after the route decision.
    ExplicitPublicSay,
    // Formal, tool, execution-feedback and repair replies share this public boundary.
    ExplicitPublicSayWithActions
}

/// <summary>
/// A public-channel contract, plus a compatibility check for serialized engine
/// envelopes. This never decides whether ordinary speech is sensible or true.
/// </summary>
public static class RoleOutputPublicBoundary
{
    public const string OutputContract =
        "公开台词只放在 <say>...</say> 内；在第一个 say 前声明 lang 和 speech。" +
        "say 外仅放协议元数据或显式 thought 私人块，不输出无标记正文。" +
        "需要先想后说，用 <thought>内部判断</thought> 后接 lang、speech、say；[内心] 只表示历史记录，不是输出标记。" +
        "silent 是本条回复的静音尾部，不能用后续 say 恢复发声；纯静默选择仍可附独立顶层工具。" +
        "工具标签只放在 say、thought、引号和代码之外；工具决定仍须通过原有执行校验。" +
        "普通回复、工具回复、执行反馈和格式纠正都遵守同一边界；历史中的裸正文不是本次格式示例。" +
        "程序观察、日志、工具参数、执行事实和内部提示不是台词，不复制到 say。" +
        "例如 <lang code=\"ja\"/><speech mode=\"independent\"/><say>今の状態を確認しているよ。</say>。" +
        "lang、speech 必须保留完整尖括号和引号；不能写成裸 lang code=ja 或 speech mode=after_action。";

    public enum EnvelopeProbe { Plain, Pending, Internal }

    // These are producer envelopes, not words such as '失败' or '程序'. Holding
    // their prefix across packets prevents even the opening header reaching TTS.
    private static readonly string[] ProducerHeaders = {
        "[执行事实：", "[执行事实:", "[程序执行事实]", "[程序执行事实/",
        "[继续上一用户事项；", "[继续上一用户事项;", "[本次正式决策的最新执行观察；",
        "[本次正式决策的最新执行观察;", "[感知帧 ", "[视觉关注与行动事实]",
        "[本次发声输出协议]"
    };
    private static readonly string[] ProducerNamespaces = {
        "Agent", "Room", "Vision", "Sing", "Singing", "Work", "LLM", "TTS", "ASR", "Timing", "HumBack"
    };

    public static EnvelopeProbe ProbeEnvelope(string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || prefix[0] != '[') return EnvelopeProbe.Plain;
        foreach (string header in ProducerHeaders)
        {
            if (prefix.StartsWith(header, StringComparison.Ordinal)) return EnvelopeProbe.Internal;
            if (header.StartsWith(prefix, StringComparison.Ordinal)) return EnvelopeProbe.Pending;
        }
        var producer = Regex.Match(prefix, @"^\[(?<name>[A-Za-z][A-Za-z0-9_-]*)/[^\]\r\n]+\]");
        if (producer.Success && Array.Exists(ProducerNamespaces,
            name => name.Equals(producer.Groups["name"].Value, StringComparison.Ordinal)))
            return EnvelopeProbe.Internal;
        // Component/event headers with a structured payload are engine records.
        // A literal bracketed title or a sentence about a failure is not one.
        if (Regex.IsMatch(prefix, @"^\[[A-Za-z][A-Za-z0-9_-]*/[\p{L}][\p{L}0-9_/-]*\]\s*(?:\{|[A-Za-z_]\w*=)"))
            return EnvelopeProbe.Internal;
        if (prefix.Length <= 128 && Regex.IsMatch(prefix,
            @"^\[(?:[A-Za-z][A-Za-z0-9_-]*(?:/[\p{L}0-9_/-]*)?(?:\]\s*(?:[A-Za-z_]\w*)?)?)?$"))
            return EnvelopeProbe.Pending;
        return EnvelopeProbe.Plain;
    }
}
