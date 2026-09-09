using System;
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
    private readonly StringBuilder tag = new StringBuilder();
    private readonly StringBuilder suspicious = new StringBuilder();
    private bool droppingMalformedTool;
    private char malformedQuote;
    public bool HasMalformedTool { get; private set; }
    private static readonly string[] ToolNames = { "sing", "clip_confirm", "clip_revise", "clip_drop",
        "hum_back", "song_sing", "song_remember", "song_catalog", "song_search", "song_rename", "song_forget",
        "memory_add", "memory_update", "memory_link", "skill_request", "look", "unlook", "next", "continue" };
    private bool silent, inSay, inCode;
    private int thoughtDepth;
    private char quote;
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
        return emitted.ToString();
    }

    private void Emit(char c, StringBuilder emitted)
    {
        if (!silent && thoughtDepth == 0 && !inCode) { speech.Append(c); emitted.Append(c); }
        else if (!char.IsWhiteSpace(c)) PrivateCharacters++;
    }

    public string Finish()
    {
        var emitted = new StringBuilder();
        foreach (char c in suspicious.ToString()) Emit(c, emitted);
        suspicious.Length = 0;
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
        if (name == "say")
        {
            // Compatibility wrapper only. Closing say does not mute plain prose,
            // and a later say cannot undo an explicit silent tail in this reply.
            inSay = !closing && !token.EndsWith("/>", StringComparison.Ordinal);
            if (closing && !silent && speech.Length > 0)
            { speech.Append('\n'); emitted.Append('\n'); }
            return;
        }
        if (name == "silent")
        {
            if (!closing) silent = true;
            return;
        }
        // Action-looking text inside a spoken quotation is not an executable call.
        // Unknown top-level tags are left for the existing tool validator to report.
        if (!inSay && !closing) actions.Append(token);
    }

    public string ToExecutableText(bool includeActions = true)
    {
        return (HasSpeech ? speech.ToString().TrimEnd() : "<silent/>") +
            (includeActions && !HasMalformedTool ? actions.ToString() : "");
    }

    public static RoleOutputChannels Parse(string full)
    {
        var result = new RoleOutputChannels();
        result.Push(full);
        result.Finish();
        return result;
    }
}
