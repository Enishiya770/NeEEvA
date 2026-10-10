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
        var schema = JObject.Parse(FirstReplyProtocol.ReviewSchema);
        Check(schema["required"].ToString().Contains("requestKind") &&
            ((JArray)schema["properties"]["requestKind"]["enum"]).Count == 5,
            "Production grammar must request a bounded current-turn semantic kind before route publication.");
        foreach (string kind in new[] { "action", "control", "mixed", "uncertain" })
        foreach (string claimed in new[] { "ordinary", "evidence", "tools" })
        {
            string wire = new JObject { ["requestKind"] = kind, ["route"] = claimed,
                ["reason"] = "Opaque explanation; application code must not interpret it.",
                ["questionUnderstanding"] = claimed == "evidence" ? new JObject {
                    ["referent"] = "a pending request", ["resolvedQuestion"] = "A read-only interpretation cannot erase the request.",
                    ["needsEvidence"] = "diagnostic" } : null }.ToString();
            Check(FirstReplyProtocol.TryReview(wire, out var normalized, out var interpretation, out var parsedKind) &&
                normalized == "tools" && parsedKind == kind && interpretation == null,
                "An action/control/mixed or unresolved request was demoted by a contradictory publication route: " + kind + "/" + claimed);
            var result = FirstReplyProtocol.ResolveReviewedCompletion(new FirstReplyCompletion { Disposition = FirstReplyDisposition.Evidence },
                claimed, "controlled", null, parsedKind);
            Check(result.Disposition == FirstReplyDisposition.Tools && result.RequestKind == kind && result.QuestionUnderstanding == null &&
                !result.SpeechDelivered && result.SafeOutput == "" && result.Speech.Count == 0,
                "Typed route correction must hand off once without speaking, constructing an action or losing the requirement.");
        }
        foreach (string claimed in new[] { "ordinary", "evidence", "tools" })
            Check(FirstReplyProtocol.TryReview(new JObject { ["requestKind"] = "answer-only", ["route"] = claimed,
                ["reason"] = "Answer publication only.", ["questionUnderstanding"] = null }.ToString(), out var answerRoute, out _, out var answerKind) &&
                answerRoute == claimed && answerKind == "answer-only" && !FirstReplyProtocol.RequiresActionOrControl(answerKind),
                "Read-only questions must retain their ordinary/evidence path; long or formatted answers may still use tools without authorizing action.");
        foreach (JToken invalidKind in new JToken[] { JValue.CreateNull(), new JArray(), new JValue("execute-now"), new JValue(new string('x', 400)) })
            Check(!FirstReplyProtocol.TryReview(new JObject { ["requestKind"] = invalidKind, ["route"] = "evidence",
                ["reason"] = "x", ["questionUnderstanding"] = null }.ToString(), out _, out _, out _),
                "Unknown, null or unbounded typed request kinds must not become trusted current-turn semantics.");
        FirstReplyCompletion PublicCandidate()
        {
            var parser = new FirstReplyStreamParser(() => true, null); parser.Push(JapaneseReply);
            return parser.Finish(true, "stop");
        }
        Check(schema["required"].ToString().Contains("responseDemand") &&
            ((JArray)schema["properties"]["responseDemand"]["enum"]).Count == 3,
            "The production review must classify the user's response demand independently of its candidate.");
        foreach (string kind in new[] { "answer-only", "action", "control", "mixed", "uncertain" })
        foreach (string demand in new[] { "short-conversation", "grounded-answer", "full-response" })
        foreach (string claimed in new[] { "ordinary", "evidence", "tools" })
        foreach (bool sufficient in new[] { true, false })
        {
            string expected = kind != "answer-only" || demand == "full-response" ? "tools" :
                demand == "grounded-answer" || !sufficient ? "evidence" : "ordinary";
            string wire = new JObject { ["requestKind"] = kind, ["responseDemand"] = demand,
                ["candidateSufficient"] = sufficient, ["route"] = claimed,
                ["reason"] = "Opaque unrelated text cannot control routing.", ["questionUnderstanding"] = null }.ToString();
            Check(FirstReplyProtocol.TryCurrentReview(wire, out var route, out var question, out var parsedKind, out var adequacy, out var parsedDemand) &&
                route == expected && parsedDemand == demand,
                "Typed response demand must enforce action priority, full-response capacity and factual review regardless of raw route or sufficiency.");
            var completion = FirstReplyProtocol.ResolveReviewedCompletion(PublicCandidate(), route, "controlled", question, parsedKind, adequacy, parsedDemand);
            Check(completion.Disposition.ToString().ToLowerInvariant() == expected && completion.ResponseDemand == demand &&
                (completion.SpeechCharacters > 0) == (expected == "ordinary"),
                "A grounded or full response escaped as ordinary speech, or an action was erased by the answer demand.");
        }
        foreach (JToken invalid in new JToken[] { JValue.CreateNull(), new JObject(), new JValue("evidence"), new JValue(new string('x', 300)) })
            Check(!FirstReplyProtocol.TryReview(new JObject { ["requestKind"] = "answer-only", ["responseDemand"] = invalid,
                ["candidateSufficient"] = true, ["route"] = "ordinary", ["reason"] = "x", ["questionUnderstanding"] = null }.ToString(),
                out _, out _, out _, out _, out _), "An unknown or malformed demand cannot become trusted routing evidence.");
        foreach (int fields in new[] { 2, 3, 4, 5 })
        {
            var legacy = new JObject { ["route"] = "ordinary", ["reason"] = "Legacy ordinary review cannot publish a current candidate." };
            if (fields >= 3) legacy["questionUnderstanding"] = null;
            if (fields >= 4) legacy["requestKind"] = "answer-only";
            if (fields >= 5) legacy["candidateSufficient"] = true;
            Check(FirstReplyProtocol.TryReview(legacy.ToString(), out var historicalRoute) && historicalRoute == "ordinary",
                "Historical review parsing must remain available without weakening the current request contract.");
            Check(!FirstReplyProtocol.TryCurrentReview(legacy.ToString(), out var route, out var question, out var kind, out var sufficient, out var demand) &&
                route == null && question == null && kind == null && sufficient == null && demand == null &&
                FirstReplyProtocol.ResolveReviewedCompletion(PublicCandidate(), route, "invalid-current-review", question, kind, sufficient, demand).Disposition == FirstReplyDisposition.Failed,
                "A legacy live response must clear every partial output and cannot release ordinary speech.");
        }
        foreach (bool sufficient in new[] { true, false })
        {
            string claimed = sufficient ? "evidence" : "ordinary";
            string wire = new JObject { ["requestKind"] = "answer-only", ["candidateSufficient"] = sufficient,
                ["route"] = claimed, ["reason"] = "Controlled independent candidate assessment.", ["questionUnderstanding"] = null }.ToString();
            Check(FirstReplyProtocol.TryReview(wire, out var projected, out var understood, out var kind, out var parsedSufficiency) &&
                projected == (sufficient ? "ordinary" : "evidence") && parsedSufficiency == sufficient,
                "A contradictory publication route must not override independently typed candidate sufficiency.");
            var completion = FirstReplyProtocol.ResolveReviewedCompletion(PublicCandidate(), projected, "controlled", understood, kind, parsedSufficiency);
            Check(completion.Disposition == (sufficient ? FirstReplyDisposition.Ordinary : FirstReplyDisposition.Evidence) &&
                completion.CandidateSufficient == sufficient && (completion.SpeechCharacters > 0) == sufficient,
                "Only an actual valid public candidate can use typed sufficiency to select ordinary speech.");
        }
        foreach (string kind in new[] { "action", "control", "mixed", "uncertain", "answer-only" })
            Check(FirstReplyProtocol.ResolveReviewedCompletion(PublicCandidate(), "tools", "controlled", null, kind, true).Disposition == FirstReplyDisposition.Tools,
                "Sufficient short candidate cannot override an action requirement or the full long/format response route.");
        foreach (var empty in new[] { new FirstReplyCompletion { Disposition = FirstReplyDisposition.Tools },
            new FirstReplyCompletion { Disposition = FirstReplyDisposition.Evidence }, new FirstReplyCompletion { Disposition = FirstReplyDisposition.Ordinary } })
        {
            var completion = FirstReplyProtocol.ResolveReviewedCompletion(empty, "evidence", "controlled", null, "answer-only", true);
            Check(completion.Disposition == FirstReplyDisposition.Evidence && completion.SafeOutput == "" && completion.SpeechCharacters == 0,
                "A sufficient=true claim must never manufacture ordinary public text from an empty route candidate.");
        }
        foreach (JToken invalid in new JToken[] { JValue.CreateNull(), new JValue("true"), new JValue(1), new JObject() })
            Check(!FirstReplyProtocol.TryReview(new JObject { ["requestKind"] = "answer-only", ["candidateSufficient"] = invalid,
                ["route"] = "ordinary", ["reason"] = "x", ["questionUnderstanding"] = null }.ToString(), out _, out _, out _, out _),
                "Candidate sufficiency must be a typed boolean, never a string, number or unknown object.");
        foreach (string route in new[] { "ordinary", "evidence", "tools" })
            Check(FirstReplyProtocol.TryReview(new JObject { ["route"] = route, ["reason"] = "A semantic verdict." }.ToString(), out var parsedRoute) && parsedRoute == route,
                "The semantic gate retains its explicitly reviewed legacy route.");
        var understanding = new JObject { ["referent"] = "此前助手的失败说明", ["resolvedQuestion"] = "刚才的说明是什么意思？", ["needsEvidence"] = "prior-dialogue" };
        string understandingWire = new JObject { ["route"] = "evidence", ["reason"] = "需要解释此前说法。", ["questionUnderstanding"] = understanding }.ToString();
        Check(FirstReplyProtocol.TryReview(understandingWire, out var understoodRoute, out var parsedUnderstanding) && understoodRoute == "evidence" &&
            JToken.DeepEquals(understanding, parsedUnderstanding), "The independent review lost its bounded question interpretation.");
        var completedUnderstanding = FirstReplyProtocol.ResolveReviewedCompletion(new FirstReplyCompletion { Disposition = FirstReplyDisposition.Tools },
            understoodRoute, "reviewed", parsedUnderstanding);
        parsedUnderstanding["resolvedQuestion"] = "MUTATED_AFTER_COMPLETION";
        Check(completedUnderstanding.Disposition == FirstReplyDisposition.Evidence &&
            (string)completedUnderstanding.QuestionUnderstanding["resolvedQuestion"] == "刚才的说明是什么意思？" &&
            completedUnderstanding.SafeOutput == "" && !completedUnderstanding.SpeechDelivered,
            "Question interpretation mutated, became public speech or acquired tool authority.");
        foreach (string route in new[] { "ordinary", "tools" })
        {
            Check(FirstReplyProtocol.ResolveReviewedCompletion(PublicCandidate(),
                route, "reviewed", understanding).QuestionUnderstanding == null,
                "An interpretation may accompany only an evidence handoff.");
            Check(FirstReplyProtocol.TryReview(new JObject { ["route"] = route, ["reason"] = "x", ["questionUnderstanding"] = null }.ToString(), out _, out var unused) && unused == null &&
                !FirstReplyProtocol.TryReview(new JObject { ["route"] = route, ["reason"] = "x", ["questionUnderstanding"] = understanding }.ToString(), out _),
                "Ordinary/tools understanding must be null and cannot add an unrelated interpretation task.");
        }
        Check(FirstReplyProtocol.TryReview("{\"route\":\"evidence\",\"reason\":\"legacy\"}", out _, out var legacyUnderstanding) && legacyUnderstanding == null,
            "A legacy reviewer fabricated a resolved question.");
        Check(FirstReplyProtocol.TryQuestionUnderstanding(new JObject { ["referent"] = "", ["resolvedQuestion"] = "", ["needsEvidence"] = "uncertain" }, out _),
            "Uncertain reference resolution must remain unknown without forcing a guess.");
        foreach (var malformedUnderstanding in new JToken[] {
            new JArray(), new JObject { ["referent"] = "x", ["resolvedQuestion"] = "y", ["needsEvidence"] = "act-now" },
            new JObject { ["referent"] = "x", ["resolvedQuestion"] = "y", ["needsEvidence"] = "diagnostic", ["action"] = "approach" },
            new JObject { ["referent"] = new string('字', FirstReplyProtocol.MaximumReferentCharacters + 1), ["resolvedQuestion"] = "y", ["needsEvidence"] = "diagnostic" },
            new JObject { ["referent"] = "x", ["resolvedQuestion"] = new string('字', FirstReplyProtocol.MaximumResolvedQuestionCharacters + 1), ["needsEvidence"] = "diagnostic" },
            new JObject { ["referent"] = "", ["resolvedQuestion"] = "", ["needsEvidence"] = "diagnostic" },
            new JObject { ["referent"] = "x", ["resolvedQuestion"] = new JObject(), ["needsEvidence"] = "diagnostic" } })
            Check(!FirstReplyProtocol.TryReview(new JObject { ["route"] = "evidence", ["reason"] = "x", ["questionUnderstanding"] = malformedUnderstanding }.ToString(), out _),
                "Unknown, expanded, empty-certain or oversized interpretation escaped strict review parsing.");
        Check(!FirstReplyProtocol.TryReview("{\"route\":\"evidence\",\"reason\":\"x\",\"questionUnderstanding\":{\"referent\":\"x\",\"referent\":\"y\",\"resolvedQuestion\":\"z\",\"needsEvidence\":\"uncertain\"}}", out _),
            "Nested duplicate understanding fields were accepted.");
        foreach (string invalid in new[] { "", "ordinary", "{\"route\":\"ordinary\"}",
            "{\"route\":\"ordinary\",\"reason\":null}", "{\"route\":\"unknown\",\"reason\":\"x\"}",
            "{\"route\":\"tools\",\"route\":\"ordinary\",\"reason\":\"x\"}",
            "{\"route\":\"ordinary\",\"reason\":\"x\",\"action\":\"approach\"}",
            new JObject { ["route"] = "ordinary", ["reason"] = new string('x', 121) }.ToString() })
            Check(!FirstReplyProtocol.TryReview(invalid, out _), "Malformed, duplicate, oversized or expanded review must not release speech.");
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
        foreach (string head in new[] { FirstReplyProtocol.ToolsHead, FirstReplyProtocol.EvidenceHead })
        {
            int called = 0;
            var parser = new FirstReplyStreamParser(() => true, _ => called++);
            foreach (char c in head) parser.Push(c.ToString());
            var result = parser.Finish(true, "stop");
            Check(result.Disposition == (head == FirstReplyProtocol.ToolsHead ? FirstReplyDisposition.Tools : FirstReplyDisposition.Evidence) &&
                called == 0 && result.SafeOutput == "" && result.SpeechCharacters == 0,
                "A handoff route leaked a candidate into public or executable output");
        }
        foreach (string invalid in new[] {
            "Before the route" + JapaneseReply,
            "<route>tools</route><say>已经打开了。</say>",
            FirstReplyProtocol.EvidenceHead + "<say>现在走过来了。</say>",
            FirstReplyProtocol.EvidenceHead + "<motion name=\"walk\"/>",
            FirstReplyProtocol.EvidenceHead + FirstReplyProtocol.ToolsHead,
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
        foreach (string head in new[] { FirstReplyProtocol.ToolsHead, FirstReplyProtocol.EvidenceHead })
        foreach (string ending in new[] { "", "\n", "\n \r\t", "\n\n\n\n\n" })
        {
            var parser = new FirstReplyStreamParser(() => true, _ => { }); parser.Push(head + ending);
            Check((parser.Finish(true, "stop").Disposition != FirstReplyDisposition.Failed) == (ending.Length <= 4),
                "Handoff completion whitespace differs from its finite grammar envelope");
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
