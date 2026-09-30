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
        Check(System.Text.RegularExpressions.Regex.IsMatch(JapaneseReply,"\\A"+FirstReplyProtocol.StructuredRegex+"\\z") &&
            System.Text.RegularExpressions.Regex.IsMatch(FirstReplyProtocol.RecordingSingingHead,"\\A"+FirstReplyProtocol.StructuredRegex+"\\z") &&
            !System.Text.RegularExpressions.Regex.IsMatch("<thought>旧格式</thought>"+JapaneseReply,"\\A"+FirstReplyProtocol.StructuredRegex+"\\z"),
            "Configured vLLM grammar must require the same leading route and reject legacy thought preambles");
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
        foreach (string route in new[] { FirstReplyProtocol.ToolsHead, FirstReplyProtocol.RecordingSingingHead })
            for (int split = 0; split <= route.Length; split++)
            {
                int called = 0;
                var parser = new FirstReplyStreamParser(() => true, _ => called++);
                parser.Push(route.Substring(0, split)); parser.Push(route.Substring(split));
                var result = parser.Finish(true, "stop");
                var expected = route == FirstReplyProtocol.ToolsHead ? FirstReplyDisposition.Tools : FirstReplyDisposition.RecordingSinging;
                Check(result.Disposition == expected && called == 0 && result.SafeOutput == "",
                    "Action routing leaked a candidate into public/executable output or changed across packet boundaries");
            }
        foreach (string invalid in new[] {
            "Before the route" + JapaneseReply,
            "<route>tools</route><say>已经打开了。</say>",
            FirstReplyProtocol.RecordingSingingHead + "<say>已经唱过了。</say>",
            FirstReplyProtocol.RecordingSingingHead + "<hum_back/>",
            FirstReplyProtocol.ToolsHead + FirstReplyProtocol.RecordingSingingHead,
            "<route>recording_sing</route>",
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
        foreach (string route in new[] { FirstReplyProtocol.ToolsHead, FirstReplyProtocol.RecordingSingingHead })
        foreach (string ending in new[] { "", "\n", "\n \r\t", "\n\n\n\n\n" })
        {
            var parser = new FirstReplyStreamParser(() => true, _ => { }); parser.Push(route + ending);
            Check((parser.Finish(true, "stop").Disposition != FirstReplyDisposition.Failed) == (ending.Length <= 4),
                "Action-route completion whitespace differs from its finite grammar envelope");
        }
        foreach (string finish in new[] { "length", "error", "", null })
        {
            var parser = new FirstReplyStreamParser(() => true, _ => { }); parser.Push(FirstReplyProtocol.RecordingSingingHead);
            Check(parser.Finish(true, finish).Disposition == FirstReplyDisposition.Failed,
                "Incomplete recording-singing route acquired a planner handoff");
        }
        {
            bool active = true;
            var parser = new FirstReplyStreamParser(() => active, _ => { });
            parser.Push(FirstReplyProtocol.RecordingSingingHead); active = false;
            Check(parser.Finish(true, "stop") == null, "Cancelled recording route acquired a late handoff");
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
