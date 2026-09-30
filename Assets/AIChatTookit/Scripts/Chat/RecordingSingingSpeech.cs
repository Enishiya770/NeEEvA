using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

public static partial class RecordingSingingDecisionCompiler
{
    private static string RecordingReplySpeech(JObject value, RecordingSingingDecisionContext context,
        string text, string language)
    {
        string Wrap(string words, string code) => string.IsNullOrWhiteSpace(words) ? "" :
            "<lang code=\"" + code + "\"/><say>" + System.Security.SecurityElement.Escape(words) + "</say>";
        const string phase = "<speech mode=\"independent\"/>";
        string resolution = (string)value["resolution"];
        if (!context.RequireGroundedResolution || (resolution != "source_uncertain" && resolution != "range_uncertain"))
            return phase + Wrap(text, language);

        var row = QuoteReferences(context).OfType<JObject>().FirstOrDefault(q =>
            (string)q["origin"] == "current" && (string)q["ref"] == (string)value["evidence_ref"]);
        string quote = (string)row?["text"] ?? "";
        int at = quote.Length == 0 ? -1 : text.IndexOf(quote, StringComparison.Ordinal);
        if (at < 0) return phase + Wrap(text, language);
        string quoteLanguage = RecordingQuoteLanguage(quote, language);
        if (quoteLanguage == language) return phase + Wrap(text, language);
        // This text is a program-authored template with an exact bound user
        // quote. Split only that known insertion, not arbitrary model prose.
        string prefix = text.Substring(0, at), suffix = text.Substring(at + quote.Length);
        if (!prefix.Any(char.IsLetterOrDigit)) { quote = prefix + quote; prefix = ""; }
        if (suffix.Length > 0 && "」』”\"".IndexOf(suffix[0]) >= 0)
        { quote += suffix[0]; suffix = suffix.Substring(1); }
        return phase + Wrap(prefix, language) + Wrap(quote, quoteLanguage) + Wrap(suffix, language);
    }

    private static string RecordingQuoteLanguage(string quote, string fallback)
    {
        bool kana = quote.Any(c => c >= '\u3040' && c <= '\u30ff');
        bool han = quote.Any(c => c >= '\u3400' && c <= '\u9fff');
        if (kana) return "ja";
        // Same strong simplified characters used by the TTS language guard.
        // Han-only names shared with Japanese remain intentionally ambiguous.
        if (quote.Any(c => "们这么说听让还过见为实现样时问间".IndexOf(c) >= 0)) return "zh";
        if (!han && quote.Any(c => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z')) return "en";
        return fallback;
    }
}
