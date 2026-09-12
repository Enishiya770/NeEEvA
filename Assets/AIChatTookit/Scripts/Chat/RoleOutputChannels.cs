using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Request-local routing, not a semantic filter. Ordinary prose is speech;
/// explicit private blocks, silent tails and tool tags never reach TTS.
/// </summary>
public sealed class RoleOutputChannels
{
    private readonly StringBuilder speech = new StringBuilder();
    private readonly StringBuilder actions = new StringBuilder();
    private readonly List<string> actionTokens = new List<string>();
    private readonly StringBuilder tag = new StringBuilder();
    private readonly StringBuilder suspicious = new StringBuilder();
    private bool droppingMalformedTool;
    private char malformedQuote;
    private char malformedEnd;
    public bool HasMalformedTool { get; private set; }
    private static readonly string[] ToolNames = { "sing", "clip_confirm", "clip_revise", "clip_drop",
        "hum_back", "song_sing", "song_remember", "song_catalog", "song_search", "song_rename", "song_forget",
        "memory_add", "memory_update", "memory_link", "skill_request", "look", "unlook", "next", "continue", "motion", "body_inspect", "sing_goal", "speech" };
    private bool silent, inSay, inCode;
    private int thoughtDepth;
    private char quote;
    private readonly Action<SpeechText> onSpeech;
    private readonly StringBuilder pendingSpeech = new StringBuilder();
    private string languageCode;
    public bool HasInvalidLanguage { get; private set; }
    public bool HasUndeclaredSpeech { get; private set; }
    public SpeechActionDependency SpeechDependency { get; private set; }
    public bool HasInvalidSpeechPhase { get; private set; }
    public string SpeechPhaseError { get; private set; }
    public bool HasSingingValidationActions { get; private set; }
    // A speech-phase error does not reject independently validated motion tags.
    // Keep that distinction in both model feedback and user-facing diagnostics.
    public string SpeechPhaseExecutionFact => !HasInvalidSpeechPhase ? "" :
        "本条回复的发声阶段校验失败：" + SpeechPhaseError +
        " 依赖校验的台词仍受发声门控；歌唱/素材变更标签未受理。" +
        "房间及上身 motion 不因该格式错误撤回，是否受理或完成以各自程序事实为准。";
    public string SpeechPhaseCorrection => !HasInvalidSpeechPhase ? "" :
        (SpeechDependency == SpeechActionDependency.AfterAction && !HasSingingValidationActions
            ? "本轮没有提交演唱或素材变更，不能使用 after_action。"
            : "发声阶段必须在正文前声明一次；本轮不能中途切换。") +
        "普通聊天及房间/上身 motion 使用 independent；只有本轮确实提交演唱或素材变更才用 after_action。" +
        "先读当前房间/身体执行事实，再自然说明意图或实际结果；不要为了纠正发声阶段重复已受理的 motion，" +
        "也不要把格式错误说成房间移动失败或已完成。";
    private bool phaseDeclared, phaseSpeechSuppressed;
    private int independentWrapperDepth;
    private static readonly string[] SingingValidationActions = { "sing_goal", "sing", "hum_back", "song_sing",
        "clip_confirm", "clip_revise", "clip_drop", "practice_confirm", "practice_revise", "practice_drop",
        "song_remember", "song_rename", "song_forget" };

    public RoleOutputChannels(Action<SpeechText> onSpeech = null)
    {
        this.onSpeech = onSpeech;
    }
    public int PrivateCharacters { get; private set; }
    public bool HasSpeech => speech.ToString().Trim().Length > 0;
    public bool HasActions => actions.Length > 0;
    public string Speech => speech.ToString();

    public string Push(string delta)
    {
        var emitted = new StringBuilder();
        foreach (char c in delta ?? "")
        {
            if (droppingMalformedTool)
            {
                if (malformedQuote != '\0') { if (c == malformedQuote) malformedQuote = '\0'; }
                else if (c == '"' || c == '\'') malformedQuote = c;
                else if (malformedEnd != '\0' ? c == malformedEnd : c == '>' || c == '》' || c == '＞' || c == '〉') droppingMalformedTool = false;
                continue;
            }
            if (suspicious.Length > 0)
            {
                suspicious.Append(c);
                string body = suspicious.ToString().Substring(1).TrimStart();
                var parts = Regex.Match(body, @"^(?<name>[a-zA-Z_]*)(?<rest>[\s\S]*)$");
                string name = parts.Groups["name"].Value;
                string rest = parts.Groups["rest"].Value;
                bool exact = Array.Exists(ToolNames, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                if (exact && Regex.IsMatch(rest, @"^\s+[a-zA-Z_]\w*\s*=$"))
                {
                    HasMalformedTool = true;
                    droppingMalformedTool = true;
                    malformedEnd = suspicious[0] == '[' ? ']' : '\0';
                    suspicious.Length = 0;
                    continue;
                }
                bool possible = rest.Length == 0 && Array.Exists(ToolNames, n => n.StartsWith(name, StringComparison.OrdinalIgnoreCase));
                possible |= exact && Regex.IsMatch(rest, @"^\s+([a-zA-Z_]\w*\s*)?$");
                if (!possible)
                {
                    foreach (char plain in suspicious.ToString()) Emit(plain, emitted);
                    suspicious.Length = 0;
                }
                continue;
            }
            if (tag.Length > 0)
            {
                tag.Append(c);
                if (quote != '\0') { if (c == quote) quote = '\0'; }
                else if (c == '"' || c == '\'') quote = c;
                else if (c == '>')
                {
                    ConsumeTag(tag.ToString(), emitted);
                    tag.Length = 0;
                }
                continue;
            }
            // Quoted protocol examples are neither speech nor executable tools.
            if (c == '`') { inCode = !inCode; continue; }
            // Hold only a possible known tool name + attribute assignment. Normal
            // book titles such as 《One Last Kiss》 are emitted unchanged.
            if (!inCode && thoughtDepth == 0 && (c == '《' || c == '＜' || c == '〈' || (c == '[' && !inSay && !silent)))
            { suspicious.Append(c); continue; }
            if (c == '<' && !inCode) { tag.Append(c); continue; }
            Emit(c, emitted);
        }
        FlushSpeech();
        return emitted.ToString();
    }

    private void Emit(char c, StringBuilder emitted)
    {
        if (!silent && thoughtDepth == 0 && !inCode && !phaseSpeechSuppressed)
        {
            speech.Append(c); emitted.Append(c);
            pendingSpeech.Append(c);
            if (languageCode == null && char.IsLetterOrDigit(c)) HasUndeclaredSpeech = true;
        }
        else if (!char.IsWhiteSpace(c)) PrivateCharacters++;
    }

    private void FlushSpeech()
    {
        if (pendingSpeech.Length == 0) return;
        var part = new SpeechText(pendingSpeech.ToString(), languageCode, actionDependency: SpeechDependency);
        pendingSpeech.Length = 0;
        onSpeech?.Invoke(part);
    }

    public string Finish()
    {
        var emitted = new StringBuilder();
        foreach (char c in suspicious.ToString()) Emit(c, emitted);
        suspicious.Length = 0;
        // An unfinished metadata tag is discarded, never read aloud.
        if (Regex.IsMatch(tag.ToString(), @"^<\s*lang\b", RegexOptions.IgnoreCase))
            HasInvalidLanguage = true;
        if (!silent && thoughtDepth == 0 && !inCode && !inSay &&
            Regex.IsMatch(tag.ToString(), @"^<\s*speech\b", RegexOptions.IgnoreCase))
            RejectSpeechPhase("发声阶段标记未闭合；未受理依赖动作。 ");
        if (phaseDeclared && SpeechDependency == SpeechActionDependency.AfterAction && HasSpeech &&
            !HasSingingValidationActions)
            RejectSpeechPhase("声明台词等待动作校验，但没有提交对应演唱或素材动作；空承诺未受理。 ");
        FlushSpeech();
        return emitted.ToString();
    }

    private void ConsumeTag(string token, StringBuilder emitted)
    {
        var match = Regex.Match(token, @"^<(?<close>/)?(?<name>[a-zA-Z_][\w-]*)\b[\s\S]*>$");
        if (!match.Success) return;
        string name = match.Groups["name"].Value.ToLowerInvariant();
        bool closing = match.Groups["close"].Success;
        if (name == "thought" || name == "think")
        {
            if (closing) thoughtDepth = Math.Max(0, thoughtDepth - 1);
            else if (!token.EndsWith("/>", StringComparison.Ordinal)) thoughtDepth++;
            return;
        }
        if (thoughtDepth > 0) return;
        if (name == "speech")
        {
            // A front-loaded contract, not a promise keyword heuristic. Once independent
            // speech starts, this response cannot add a dependent singing action later.
            if (silent || inCode || inSay) return;
            // Some providers write the explicit independent declaration as an XML
            // wrapper. Only this unambiguous spelling is compatible; it never
            // changes the dependency of speech or repairs an after_action promise.
            if (closing && independentWrapperDepth > 0 && !HasInvalidSpeechPhase &&
                Regex.IsMatch(token, @"^</speech\s*>$", RegexOptions.IgnoreCase))
            {
                independentWrapperDepth--;
                return;
            }
            var declaration = Regex.Match(token,
                "^<speech\\s+mode\\s*=\\s*(?<q>[\"'])(?<mode>independent|after_action)\\k<q>\\s*(?<slash>/)?>$",
                RegexOptions.IgnoreCase);
            bool isIndependent = declaration.Success &&
                declaration.Groups["mode"].Value.Equals("independent", StringComparison.OrdinalIgnoreCase);
            bool validDeclaration = declaration.Success && (isIndependent || declaration.Groups["slash"].Success);
            SpeechActionDependency declaredDependency = isIndependent
                ? SpeechActionDependency.Independent : SpeechActionDependency.AfterAction;
            // Idempotent metadata before the first public character is harmless.
            // Repeating it after prose, switching modes, or using unknown attributes
            // remains an error, including when a previous error already suppressed speech.
            bool repeatsFrontDeclaration = phaseDeclared && validDeclaration && !HasSpeech &&
                !HasInvalidSpeechPhase && declaredDependency == SpeechDependency;
            if (closing || HasSpeech || (phaseDeclared && !repeatsFrontDeclaration))
            {
                RejectSpeechPhase("发声阶段必须在正文前声明一次，不能在本轮改换阶段。 ");
                return;
            }
            FlushSpeech();
            phaseDeclared = true;
            if (!validDeclaration)
            {
                SpeechDependency = SpeechActionDependency.AfterAction;
                RejectSpeechPhase("发声阶段标记无效；未受理依赖动作。 ");
                return;
            }
            SpeechDependency = declaredDependency;
            if (isIndependent && !declaration.Groups["slash"].Success) independentWrapperDepth++;
            if (SpeechDependency == SpeechActionDependency.Independent && HasSingingValidationActions)
                RejectSpeechPhase("本轮已经包含待校验演唱或素材动作，不能声明为独立闲聊。 ");
            return;
        }
        if (name == "lang")
        {
            // A declaration is metadata, never an action. Private declarations cannot
            // alter the language of subsequent audible text.
            if (silent || inCode) return;
            var declaration = Regex.Match(token,
                "^<lang\\s+code\\s*=\\s*(?<q>[\"'])(?<code>[a-zA-Z-]+)\\k<q>\\s*/>$",
                RegexOptions.IgnoreCase);
            string code = declaration.Success
                ? SpeechText.NormalizeLanguage(declaration.Groups["code"].Value) : null;
            FlushSpeech();
            languageCode = code;
            if (code == null) HasInvalidLanguage = true;
            return;
        }
        if (name == "say")
        {
            // Compatibility wrapper only. Closing say does not mute plain prose,
            // and a later say cannot undo an explicit silent tail in this reply.
            inSay = !closing && !token.EndsWith("/>", StringComparison.Ordinal);
            if (closing && !silent && speech.Length > 0)
                Emit('\n', emitted);
            return;
        }
        if (name == "silent")
        {
            if (!closing) silent = true;
            return;
        }
        // Action-looking text inside a spoken quotation is not an executable call.
        // Unknown top-level tags are left for the existing tool validator to report.
        if (!inSay && !closing)
        {
            bool dependsOnValidation = Array.Exists(SingingValidationActions, n => n == name);
            if (dependsOnValidation)
            {
                HasSingingValidationActions = true;
                if (SpeechDependency == SpeechActionDependency.Independent)
                {
                    RejectSpeechPhase("independent 阶段不能追加演唱或素材变更；这些动作未执行。若仍决定行动，请在新决策前声明 after_action。 ");
                    return;
                }
            }
            actions.Append(token);
            actionTokens.Add(token);
        }
    }

    private void RejectSpeechPhase(string reason)
    {
        FlushSpeech();
        HasInvalidSpeechPhase = true;
        if (string.IsNullOrEmpty(SpeechPhaseError)) SpeechPhaseError = reason.Trim();
        // Earlier independent speech may already be audible; stop further prose and
        // remove dependent tags from the executable projection, never replay raw tags.
        phaseSpeechSuppressed = true;
    }

    public string ToExecutableText(bool includeActions = true, bool includeSpeechPhase = false)
    {
        string executableActions = includeActions && !HasMalformedTool ? actions.ToString() : "";
        if (includeActions && !HasMalformedTool && HasInvalidSpeechPhase)
        {
            var safe = new StringBuilder();
            foreach (string token in actionTokens)
            {
                string name = Regex.Match(token, @"^<([a-zA-Z_][\w-]*)").Groups[1].Value;
                if (!Array.Exists(SingingValidationActions, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                    safe.Append(token);
            }
            executableActions = safe.ToString();
        }
        bool discardedDependentSpeech = HasInvalidSpeechPhase && SpeechDependency != SpeechActionDependency.Independent;
        string prefix = "";
        if (includeSpeechPhase && (phaseDeclared || HasInvalidSpeechPhase) && HasSpeech)
            prefix = "<speech mode=\"" + (SpeechDependency == SpeechActionDependency.Independent ? "independent" : "after_action") +
                (HasInvalidSpeechPhase ? "\" rejected=\"true" : "") + "\"/>";
        return prefix + (!discardedDependentSpeech && HasSpeech ? speech.ToString().TrimEnd() : "<silent/>") + executableActions;
    }

    public static RoleOutputChannels Parse(string full)
    {
        var result = new RoleOutputChannels();
        result.Push(full);
        result.Finish();
        return result;
    }
}

/// <summary>
/// One current output-format observation, not permanent instructions accumulated
/// after every failed reply. Other execution history and user text are untouched.
/// </summary>
public static class RoleOutputFormatFactHistory
{
    public const string Prefix = "[程序执行事实/当前输出格式] ";
    public const string TruncationFact = "上一条回复因生成长度上限被截断，其中工具动作没有执行；" +
        "不能把未完成输出当作成功。可根据当前用户语境重新决定、询问或暂不行动。";
    private static readonly string[] LegacyPrefixes = {
        "[程序执行事实] 本条回复的发声阶段校验失败：",
        "[程序执行事实] 检测到方括号或全角括号等错误工具属性语法；",
        "[程序执行事实] 上一条回复因生成长度上限被截断，"
    };

    public static void ReplaceCurrent<T>(IList<T> messages, Func<T, string> role,
        Func<T, string> content, Func<string, T> createSystemMessage, string currentFailure)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(role(messages[i]), "system", StringComparison.Ordinal)) continue;
            string text = content(messages[i]) ?? "";
            if (text.StartsWith(Prefix, StringComparison.Ordinal) ||
                Array.Exists(LegacyPrefixes, p => text.StartsWith(p, StringComparison.Ordinal)))
                messages.RemoveAt(i);
        }
        if (!string.IsNullOrWhiteSpace(currentFailure))
            messages.Add(createSystemMessage(Prefix + "仅描述最近一次模型输出；房间/身体执行结果仍以对应动作事实为准。\n" + currentFailure));
    }
}
