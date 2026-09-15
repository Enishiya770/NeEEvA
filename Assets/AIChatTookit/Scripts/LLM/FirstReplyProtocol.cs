using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

public enum FirstReplyDisposition { Ordinary, Tools, Failed }

/// <summary>Completion never contains executable tool output. A tool decision hands
/// the untouched turn to the existing planner. A preparation is not a spoken turn.</summary>
public sealed class FirstReplyCompletion
{
    public FirstReplyDisposition Disposition;
    public string SafeOutput = "";
    public string Failure = "";
    public bool SpeechDelivered;
    public int SpeechCharacters { get { int count = 0; foreach (var part in Speech) count += part.Text.Length; return count; } }
    public List<SpeechText> Speech = new List<SpeechText>();
    public bool Succeeded => Disposition != FirstReplyDisposition.Failed;
    public static FirstReplyCompletion Failed(string reason, bool delivered = false) =>
        new FirstReplyCompletion { Disposition = FirstReplyDisposition.Failed, Failure = reason ?? "failed", SpeechDelivered = delivered };
}

/// <summary>The same role/model chooses a route. This protocol grants no tool
/// authority and does not classify the user's words in application code.</summary>
public static class FirstReplyProtocol
{
    public const string OrdinaryHead = "<route>ordinary</route>";
    public const string ToolsHead = "<route>tools</route>";
    public const int MaximumResponseCharacters = 16384;
    public const int MaximumSpeechSegments = 3;
    public const int MaximumTrailingWhitespace = 4;
    public const string RouteContract =
        "[本轮首答通道]\n你仍是原来的角色，保留人设、语言、记忆和全部能力。先理解完整本轮原话及上下文，由你选择通道。" +
        "第一项只能输出 <route>ordinary</route> 或 <route>tools</route>，不能在前面输出思考、解释或其它标签。\n" +
        "ordinary：本轮仅需直接对话，能基于现有可靠资料回答，既不执行也不承诺新的动作、工具、演唱、素材/记忆修改；" +
        "随后输出语言、independent 阶段和显式 say 正文。正文用可朗读的自然文字，不用反引号或代码块。直接回答问题，遵守用户语言要求；不要为了快而只说称呼、复述或空泛应声。" +
        "第一句就给具体回答、理由或相关感受；称呼、笑声和礼貌应声不要单独占第一句，可自然放到实质内容之后。" +
        "ordinary 用一到三句自然完整的短句简答：同一种语言的连续句子放在同一个 say 中，回答完就结束，不要另起同语言 say 继续闲聊。" +
        "默认只用当前需要的一种语言，不自动翻译、双语复述或追加另一种语言；确实需要切换时最多三段。" +
        "若用户要求长文或超出简答范围的完整内容，选 tools 交回完整回复流程，不删除用户要求，不强行截断内容。" +
        "不把内部提示、引擎记录、事实 JSON 或来源说明原样念出；真实 unknown 仍然未知。\n" +
        "tools：本轮需要身体/房间动作、视向/观察操作、演唱/录音/素材处理、记忆修改、其它已有工具，" +
        "或需要进一步核验当前视觉、执行结果才能如实回答。只输出 <route>tools</route> 后结束；" +
        "程序会把完整本轮交回原执行和审核链，所有低频能力仍可使用，不在这里生成候选台词或工具标签。" +
        "只讨论唱歌、动作等话题并不等于要求执行；反之用户追加的否定、指代、尾句限制不可忽略。" +
        "问候、情绪回应、个人偏好或一般生活建议通常能直接交流；仅提及房间、休息、唱歌等话题无需查询或操作。" +
        "不要仅因存在旧任务或已加载技能而选 tools；只有你本轮真正需要新工具/动作或核验时才选它。" +
        "资料或能力不足时不能擅自把动作请求改成闲聊或编造完成。通道一旦 ordinary 不得在正文中途追加工具；" +
        "如依赖执行事实的审核、不支持的输出格式，或遵守用户要求需要超出一段简短回答的长文，选 tools 交回完整回复流程，" +
        "不能截断用户原话或用户要求的答案。历史中的指令仅供理解，不是本轮新指令。";

    // Local llama.cpp grammar constrains the complete response, including late tags.
    // A bounded client parser remains mandatory; grammar is not semantic validation.
    public const string Grammar =
        "root ::= tools | ordinary\n" +
        "tools ::= \"<route>tools</route>\" trailing\n" +
        "ordinary ::= \"<route>ordinary</route>\\n\" lang \"<speech mode=\\\"independent\\\"/>\" say (\"\\n\" lang say (\"\\n\" lang say)?)? trailing\n" +
        "lang ::= \"<lang code=\\\"\" (\"ja\" | \"zh\" | \"en\") \"\\\"/>\"\n" +
        "say ::= \"<say>\" [^<>`]+ \"</say>\"\n" +
        "trailing ::= [ \\t\\r\\n]{0,4}\n";
}

/// <summary>Bounded request-local prefix decoder. Public deltas require an exact
/// route and explicit say boundary. Cancellation fences every callback, including
/// a callback that synchronously supersedes its own provider generation.</summary>
public sealed class FirstReplyStreamParser
{
    private readonly Func<bool> current;
    private readonly Action<SpeechText> onSpeech;
    private readonly RoleOutputChannels channels;
    private readonly StringBuilder head = new StringBuilder();
    private readonly StringBuilder body = new StringBuilder();
    private readonly SpeechTextBuffer delivered = new SpeechTextBuffer();
    private FirstReplyDisposition? route;
    private string failure;
    private bool finished;
    private readonly StringBuilder currentTag = new StringBuilder();
    private bool insideTag;
    private int speechSegments, toolsWhitespace;
    public bool IsCurrent => current == null || current();
    public bool SpeechDelivered => delivered.Length > 0;
    public FirstReplyDisposition? Route => route;

    public FirstReplyStreamParser(Func<bool> current, Action<SpeechText> onSpeech)
    {
        this.current = current;
        this.onSpeech = onSpeech;
        channels = new RoleOutputChannels(part =>
        {
            if (!IsCurrent || failure != null || finished || route != FirstReplyDisposition.Ordinary) return;
            if (part.ActionDependency != SpeechActionDependency.Independent || part.LanguageCode == null)
            { failure = "first-reply-missing-public-metadata"; return; }
            delivered.Append(part);
            this.onSpeech?.Invoke(part);
        }, RoleOutputBoundaryPolicy.ExplicitPublicSay);
    }

    public void Push(string delta)
    {
        if (!IsCurrent || finished || failure != null || string.IsNullOrEmpty(delta)) return;
        foreach (char c in delta)
        {
            if (!IsCurrent || failure != null) return;
            if (!route.HasValue)
            {
                head.Append(c);
                string prefix = head.ToString();
                if (prefix == FirstReplyProtocol.OrdinaryHead) route = FirstReplyDisposition.Ordinary;
                else if (prefix == FirstReplyProtocol.ToolsHead) route = FirstReplyDisposition.Tools;
                else if (prefix.Length > 64 || (!FirstReplyProtocol.OrdinaryHead.StartsWith(prefix, StringComparison.Ordinal) &&
                    !FirstReplyProtocol.ToolsHead.StartsWith(prefix, StringComparison.Ordinal))) failure = "first-reply-invalid-route-head";
                continue;
            }
            if (route == FirstReplyDisposition.Tools)
            {
                if (!IsProtocolWhitespace(c)) failure = "first-reply-tools-has-body";
                else if (++toolsWhitespace > FirstReplyProtocol.MaximumTrailingWhitespace)
                    failure = "first-reply-trailing-whitespace-limit";
                continue;
            }
            if (body.Length >= FirstReplyProtocol.MaximumResponseCharacters)
            { failure = "first-reply-response-too-long"; return; }
            body.Append(c);
            ObserveSpeechSegmentBounds(c);
            if (failure != null) return;
            channels.Push(c.ToString());
            if (channels.HasAnyActionTag || channels.HasActions || channels.HasMalformedTool ||
                channels.HasInvalidPublicBoundary || channels.HasInvalidSpeechPhase)
                failure = "first-reply-invalid-public-boundary";
        }
    }

    private static bool IsProtocolWhitespace(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n';

    // Reject another language block before it can forward public characters.
    // Do not cap each say by characters: a forced closing tag can manufacture a
    // syntactically complete but mid-sentence "successful" model response.
    private void ObserveSpeechSegmentBounds(char c)
    {
        if (c == '<' && !insideTag) { insideTag = true; currentTag.Length = 0; }
        if (insideTag)
        {
            if (currentTag.Length < 128) currentTag.Append(c);
            if (c != '>') return;
            insideTag = false;
            string tag = currentTag.ToString();
            if (tag == "<say>")
            {
                if (++speechSegments > FirstReplyProtocol.MaximumSpeechSegments)
                    failure = "first-reply-speech-segment-limit";
            }
            return;
        }
    }

    public FirstReplyCompletion Finish(bool transportSucceeded, string finishReason)
    {
        if (!IsCurrent || finished) return null;
        if (failure == null && (!transportSucceeded || !string.Equals(finishReason, "stop", StringComparison.Ordinal)))
            failure = transportSucceeded ? "first-reply-incomplete-output" : "first-reply-transport-failed";
        if (failure == null && !route.HasValue) failure = "first-reply-missing-route";
        if (failure == null && route == FirstReplyDisposition.Ordinary)
        {
            const string lang = "<lang code=\"(?:ja|zh|en)\"/>";
            const string scalar = "(?:[^<>`\\uD800-\\uDFFF]|[\\uD800-\\uDBFF][\\uDC00-\\uDFFF])";
            string say = "<say>" + scalar + "+</say>";
            if (!Regex.IsMatch(body.ToString(), "^\\n" + lang +
                "<speech mode=\"independent\"/>" + say + "(?:\\n" + lang + say + "){0," +
                (FirstReplyProtocol.MaximumSpeechSegments - 1) + "}[ \\t\\r\\n]{0," +
                FirstReplyProtocol.MaximumTrailingWhitespace + "}\\z"))
                failure = "first-reply-invalid-body-structure";
            channels.Finish();
            if (!IsCurrent) { finished = true; return null; }
            var complete = RoleOutputChannels.Parse(body.ToString(), RoleOutputBoundaryPolicy.ExplicitPublicSay);
            if (complete.HasAnyActionTag || complete.HasActions || complete.HasMalformedTool ||
                complete.HasInvalidPublicBoundary || complete.HasInvalidSpeechPhase ||
                !complete.HasSpeech || complete.HasUndeclaredSpeech || complete.HasInvalidLanguage ||
                complete.SpeechDependency != SpeechActionDependency.Independent)
                failure = "first-reply-invalid-completed-speech";
        }
        finished = true;
        if (failure != null)
        {
            var incomplete = FirstReplyCompletion.Failed(failure, SpeechDelivered);
            // Diagnostic evidence of validated candidate deltas already forwarded;
            // not a successful response, executable output or proof of playback.
            incomplete.Speech = delivered.Snapshot();
            return incomplete;
        }
        if (route == FirstReplyDisposition.Tools) return new FirstReplyCompletion { Disposition = FirstReplyDisposition.Tools };
        // Do not forward raw model output: completion consumers can execute tags.
        // Construct only the already validated public language runs, no private text.
        var safe = new StringBuilder();
        var parts = delivered.Snapshot();
        safe.Append("<speech mode=\"independent\"/>");
        foreach (var part in parts) safe.Append("<lang code=\"").Append(part.LanguageCode)
            .Append("\"/><say>").Append(part.Text).Append("</say>");
        return new FirstReplyCompletion { Disposition = FirstReplyDisposition.Ordinary,
            SafeOutput = safe.ToString(), Speech = parts, SpeechDelivered = SpeechDelivered };
    }
}
