using System;
using System.Collections.Generic;
using System.Text;

/// <summary>Spoken text and its request-local language intent. Null means legacy detection.</summary>
public readonly struct SpeechText
{
    // Kept near the current user input for long role/skill prompts; auxiliary JSON
    // tasks do not receive this spoken-output contract.
    public const string OutputContract = "[本次发声输出协议]\n" +
        "每段正文使用声明语种的自然表达。中文输入和记忆是语义资料，不是日语台词原稿；" +
        "日语的普通词、专业术语和人名都用日语表达，不照抄中文措辞，不附中文别名或括号注音。" +
        "先理解内容，再按要说的语言组织文字。\n" +
        "发声前先输出 <lang code=\"ja\"/>、<lang code=\"zh\"/> 或 <lang code=\"en\"/>，切换语种时再声明。" +
        "只有汉字的短句也必须声明；‘只说某几个字’限制的是可见台词，内部标记仍需提供。" +
        "心里话和工具按原规则处理。";

    public readonly string Text;
    public readonly string LanguageCode;
    public readonly string LanguageSource;

    public SpeechText(string text, string languageCode = null, string languageSource = null)
    {
        Text = text ?? "";
        LanguageCode = NormalizeLanguage(languageCode);
        LanguageSource = languageSource ?? (LanguageCode == null ? "auto-fallback" : "declared");
    }

    public static string NormalizeLanguage(string code)
    {
        switch ((code ?? "").Trim().ToLowerInvariant())
        {
            case "zh": return "zh";
            case "ja": return "ja";
            case "en": return "en";
            default: return null;
        }
    }
}

/// <summary>
/// Keeps language spans attached to text while streaming gates hold it or the sentence
/// splitter removes a prefix. No language state is shared with another request or TTS job.
/// </summary>
public sealed class SpeechTextBuffer
{
    private sealed class Run
    {
        public string Language;
        public int Length;
    }

    private readonly StringBuilder text = new StringBuilder();
    private readonly List<Run> runs = new List<Run>();

    public int Length
    {
        get { return text.Length; }
        set
        {
            if (value != 0) throw new ArgumentOutOfRangeException(nameof(value));
            text.Length = 0;
            runs.Clear();
        }
    }

    public string LanguageCode => runs.Count == 0 ? null : runs[0].Language;
    public int FirstLanguageBoundary => runs.Count > 1 ? runs[0].Length : -1;

    public void Append(string value) { Append(new SpeechText(value)); }

    public void Append(SpeechText value)
    {
        if (string.IsNullOrEmpty(value.Text)) return;
        text.Append(value.Text);
        if (runs.Count > 0 && runs[runs.Count - 1].Language == value.LanguageCode)
            runs[runs.Count - 1].Length += value.Text.Length;
        else runs.Add(new Run { Language = value.LanguageCode, Length = value.Text.Length });
    }

    public void Remove(int start, int count)
    {
        // All consumers remove a consumed prefix, never edit the model's words.
        if (start != 0 || count < 0 || count > Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        text.Remove(0, count);
        while (count > 0)
        {
            int take = Math.Min(count, runs[0].Length);
            runs[0].Length -= take;
            count -= take;
            if (runs[0].Length == 0) runs.RemoveAt(0);
        }
    }

    public override string ToString() { return text.ToString(); }

    public List<SpeechText> Snapshot()
    {
        var result = new List<SpeechText>();
        int at = 0;
        foreach (Run run in runs)
        {
            result.Add(new SpeechText(text.ToString(at, run.Length), run.Language));
            at += run.Length;
        }
        return result;
    }
}
