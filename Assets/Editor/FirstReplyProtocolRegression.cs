using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

// Pure production-parser checks. No Unity runtime, HTTP, model or audio dependency.
public static class FirstReplyProtocolRegression
{
    public static JObject LastReport { get; private set; }
    public const string JapaneseReply = "<route>ordinary</route>\n<lang code=\"ja\"/><speech mode=\"independent\"/><say>空の青さは、光が空気に散らばるためよ。</say>";
    private static int checks;
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new InvalidOperationException(message); }

    public static int RunChecks()
    {
        checks = 0;
        string bilingual = "<route>ordinary</route>\n<lang code=\"zh\"/><speech mode=\"independent\"/><say>可以。这里先解释原因。</say>\n<lang code=\"en\"/><say>Here is the reason.</say>";
        string trilingual = bilingual + "\n<lang code=\"ja\"/><say>理由を順番に説明するね。</say>";
        const string completeEnglish = "Take your time with the project; if you get stuck, tell me what happened and we can work through it together. I'll be right here, waiting for a chance to hear how it goes.";
        string longEnglishReply = JapaneseReply.Replace("code=\"ja\"", "code=\"en\"")
            .Replace("空の青さは、光が空気に散らばるためよ。", completeEnglish);
        Check(completeEnglish.Length > 160 && completeEnglish.EndsWith("how it goes.", StringComparison.Ordinal),
            "Complete-English fixture must pass the removed character limit with its natural ending intact");
        foreach (string content in new[] { JapaneseReply, JapaneseReply + "\n", JapaneseReply + "\n \r\t",
            bilingual, trilingual + "\n", longEnglishReply,
            JapaneseReply.Replace("空の青さは、光が空気に散らばるためよ。", Repeat("\U0001F642", 170)) })
        {
            for (int split = 0; split <= content.Length; split++)
            {
                var runs = new SpeechTextBuffer();
                var parser = new FirstReplyStreamParser(() => true, runs.Append);
                parser.Push(content.Substring(0, split));
                parser.Push(content.Substring(split));
                var complete = parser.Finish(true, "stop");
                Check(complete.Disposition == FirstReplyDisposition.Ordinary && complete.SpeechDelivered &&
                    complete.SpeechCharacters == runs.Length && !complete.SafeOutput.Contains("<route>"),
                    "Ordinary chunks did not produce only complete, explicitly public output");
                var parsed = RoleOutputChannels.Parse(complete.SafeOutput, RoleOutputBoundaryPolicy.ExplicitPublicSay);
                Check(parsed.Speech == runs.ToString() && !parsed.HasAnyActionTag && !parsed.HasInvalidPublicBoundary,
                    "Language/speech spans or safe completion changed across a packet boundary");
                if (content == longEnglishReply)
                    Check(parsed.Speech == completeEnglish,
                        "A complete English answer was cut at a character limit or lost its natural ending");
                Check(parser.Finish(true, "stop") == null, "A completed parser delivered twice");
            }
        }
        {
            int called = 0;
            var parser = new FirstReplyStreamParser(() => true, _ => called++);
            foreach (char c in FirstReplyProtocol.ToolsHead) parser.Push(c.ToString());
            var result = parser.Finish(true, "stop");
            Check(result.Disposition == FirstReplyDisposition.Tools && called == 0 && result.SafeOutput == "",
                "Tool routing leaked a candidate into public or executable output");
        }
        foreach (string invalid in new[] {
            "Before the route" + JapaneseReply,
            "<route>tools</route><say>已经打开了。</say>",
            JapaneseReply + "<motion name=\"wave\"/>",
            JapaneseReply + "<route>tools</route>",
            JapaneseReply.Replace("空の青さ", "<memory_add text=\"x\"/>空の青さ"),
            JapaneseReply.Replace("<lang code=\"ja\"/>", ""),
            JapaneseReply.Replace("independent", "after_action"),
            JapaneseReply.Replace("空の青さ", "`空の青さ`"),
            JapaneseReply.Replace("</say>", ""),
            JapaneseReply.Replace("<say>", ""),
            JapaneseReply + "\n\n\n\n\n",
            trilingual + "\n<lang code=\"zh\"/><say>第四段不能继续发声。</say>",
            JapaneseReply.Replace("空の青さは、光が空気に散らばるためよ。", "\ud800"),
            "<route>ord", "", JapaneseReply + "unframed prose" })
        {
            var parser = new FirstReplyStreamParser(() => true, _ => { });
            parser.Push(invalid);
            var result = parser.Finish(true, "stop");
            Check(result.Disposition == FirstReplyDisposition.Failed && result.SafeOutput == "",
                "Malformed or late tool output escaped fail-closed completion");
        }
        foreach (string finish in new[] { "length", "error", "", null })
        {
            var forwarded = new SpeechTextBuffer();
            var parser = new FirstReplyStreamParser(() => true, forwarded.Append); parser.Push(JapaneseReply);
            var failed = parser.Finish(true, finish);
            Check(failed.Disposition == FirstReplyDisposition.Failed && failed.SafeOutput == "" &&
                failed.SpeechCharacters == forwarded.Length && failed.SpeechCharacters > 0,
                "Incomplete HTTP-200 output became approved, or discarded its forwarded-delta diagnostic evidence");
        }
        {
            var forwarded = new SpeechTextBuffer();
            var parser = new FirstReplyStreamParser(() => true, forwarded.Append);
            foreach (char c in trilingual + "\n<lang code=\"en\"/><say>NEVER_PUBLIC_FOURTH</say>") parser.Push(c.ToString());
            var result = parser.Finish(true, "stop");
            Check(result.Disposition == FirstReplyDisposition.Failed && result.Failure == "first-reply-speech-segment-limit" &&
                !forwarded.ToString().Contains("NEVER_PUBLIC_FOURTH") && result.SpeechCharacters == forwarded.Length,
                "The first excess language segment escaped the production streaming bound");
        }
        {
            var forwarded = new SpeechTextBuffer();
            var parser = new FirstReplyStreamParser(() => true, forwarded.Append);
            string content = JapaneseReply.Replace("空の青さは、光が空気に散らばるためよ。",
                new string('字', FirstReplyProtocol.MaximumResponseCharacters) + "NEVER_PUBLIC_AFTER_TOTAL_LIMIT");
            parser.Push(content);
            var result = parser.Finish(true, "stop");
            Check(result.Disposition == FirstReplyDisposition.Failed && result.Failure == "first-reply-response-too-long" &&
                !forwarded.ToString().Contains("NEVER_PUBLIC_AFTER_TOTAL_LIMIT") && result.SpeechCharacters == forwarded.Length,
                "Removing a per-say limit disabled the total response bound or lost its failure evidence");
        }
        foreach (string ending in new[] { "", "\n", "\n \r\t", "\n\n\n\n\n" })
        {
            var parser = new FirstReplyStreamParser(() => true, _ => { }); parser.Push(FirstReplyProtocol.ToolsHead + ending);
            Check((parser.Finish(true, "stop").Disposition == FirstReplyDisposition.Tools) == (ending.Length <= 4),
                "Tools completion whitespace differs from its finite grammar envelope");
        }
        {
            var parser = new FirstReplyStreamParser(() => true, _ => { }); parser.Push(FirstReplyProtocol.ToolsHead);
            Check(parser.Finish(false, "stop").Disposition == FirstReplyDisposition.Failed,
                "HTTP failure delivered a successful tool decision");
        }
        {
            bool active = true; int calls = 0;
            var parser = new FirstReplyStreamParser(() => active, _ => calls++);
            parser.Push(FirstReplyProtocol.OrdinaryHead); active = false;
            parser.Push(JapaneseReply.Substring(FirstReplyProtocol.OrdinaryHead.Length));
            Check(calls == 0 && parser.Finish(true, "stop") == null,
                "A cancelled route emitted late public deltas/completion");
        }
        {
            bool active = true; int calls = 0;
            var parser = new FirstReplyStreamParser(() => active, _ => { calls++; active = false; });
            parser.Push(JapaneseReply);
            Check(calls == 1 && parser.Finish(true, "stop") == null,
                "A public callback superseding its own lease allowed another callback");
        }
        LastReport = new JObject { ["passed"] = true, ["checks"] = checks,
            ["scope"] = "Pure actual parser across all character splits, language switch, tool fallthrough, invalid metadata/late tools, incomplete response and lease cancellation; no model semantic or audible-delivery claim." };
        return checks;
    }

    private static string Repeat(string value, int count)
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++) text.Append(value);
        return text.ToString();
    }
}
