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
    private bool phaseDeclared, phaseSpeechSuppressed;
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
                else if (c == '>' || c == '》' || c == '＞' || c == '〉') droppingMalformedTool = false;
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
            if (!inCode && thoughtDepth == 0 && (c == '《' || c == '＜' || c == '〈'))
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
        if (Regex.IsMatch(tag.ToString(), @"^<\s*speech\b", RegexOptions.IgnoreCase))
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
            if (closing || phaseDeclared || HasSpeech)
            {
                RejectSpeechPhase("发声阶段必须在正文前声明一次，不能在本轮改换阶段。 ");
                return;
            }
            FlushSpeech();
            var declaration = Regex.Match(token,
                "^<speech\\s+mode\\s*=\\s*(?<q>[\"'])(?<mode>independent|after_action)\\k<q>\\s*/>$",
                RegexOptions.IgnoreCase);
            phaseDeclared = true;
            if (!declaration.Success)
            {
                SpeechDependency = SpeechActionDependency.AfterAction;
                RejectSpeechPhase("发声阶段标记无效；未受理依赖动作。 ");
                return;
            }
            SpeechDependency = declaration.Groups["mode"].Value.Equals("independent", StringComparison.OrdinalIgnoreCase)
                ? SpeechActionDependency.Independent : SpeechActionDependency.AfterAction;
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
