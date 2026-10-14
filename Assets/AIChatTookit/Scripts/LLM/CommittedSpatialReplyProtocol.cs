using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Public result wording only. It never interprets requests or authorizes an action.</summary>
public static class CommittedSpatialReplyProtocol
{
    public const string ReadOnlyEvidenceMarker = "[只读场景问答]";
    public const int MaximumSpeechCharacters = 360;
    public const int MaximumOutputTokens = 768;
    public const string Marker = "[Committed spatial public reply protocol]";
    public const string OutputContract = Marker + "\n" +
        "This request writes for the bound current reply purpose. When replyPurpose identifies a new autonomous intention and no current human turn is present, respond to that intention, not the last historical command. Otherwise answer the complete current human request. It may report an existing task result or answer an evidence-only question with no new action decision. " +
        "Determine the current question from the complete human utterance and actual prior dialogue before choosing relevant evidence. Acoustic labels, repeated ASR alternatives, images and old tasks are evidence, not new requests or default topics. A question about a prior explanation or failure calls for the relevant historical receipt; a question about the current view calls for visual evidence. Do not substitute an earlier question or describe a picture merely because it is attached. " +
        "Return one short public reply of at most 360 characters, using the supplied persona and dialogue language. " +
        "Output exactly <lang code=\"ja|zh|en\"/><speech mode=\"independent\"/><say>public reply</say>, with one actual language code. " +
        "For an autonomous observation where no reply is owed, <silent/> remains an alternative, subject to the existing reply review. " +
        "Do not output thought, think, reasoning, markdown, after_action, tools, motion, next, multiple say blocks or any text outside this envelope. " +
        "Do not reconsider how to execute the task. Preserve the distinction between a request, a submitted action, an actual result and a service failure. " +
        "A request failure is not physical inability; do not promise another attempt that was not submitted. " +
        "The bound current purpose and latest actual facts determine the answer; historical requests retain constraints but cannot become new work. Missing identity evidence is not proof that the user gave no command. " +
        "Keep unresolved requests and limits truthful. This reply cannot create actions or replace the independent semantic review. " +
        "This output-only protocol overrides other output-format examples in supplied history or role instructions for this request only.";

    // This request-local instruction follows the original turn and the newest
    // execution feedback. The original imperative is evidence for the reply;
    // ending with it alone can reopen action planning or generic AI disclaimers.
    public const string FinalWritingInstruction =
        "[Write the answer to the current human request now]\n" +
        "First identify what the complete latest human utterance asks now, using the actually spoken prior dialogue to resolve references. ASR acoustic labels and overlapping alternatives do not create a new topic or override the utterance's meaning. The attached image and existing tasks are available evidence, not instructions to describe the scene or resume a task. Do not answer a previous turn instead. " +
        "Answer that current question using the relevant supplied facts. A question about an earlier explanation, status or failure should be explained from its identified historical receipt; current visual evidence cannot replace that answer. A current-view question should be answered from its image without inserting an unrelated old failure. " +
        "This is a reply-writing stage, not a fresh request to execute a quoted command. An evidence-only question has no new action decision; do not infer that a task was planned or submitted merely because this writer was called. " +
        "If the current decision failed, report its recorded cause and whether this decision started an action; a planning-service failure does not establish that the character lacks movement ability. " +
        "For a spatial question, answer from the supplied current and remembered evidence, preserving uncertainty and body versus camera direction. " +
        "Do not invent a different cause, reconsider the command, promise an unsubmitted retry or repeat an already spoken prefix. " +
        "Follow the supplied persona and dialogue-language policy, including an explicit human language choice; write natural vocabulary and grammar in the selected language. " +
        "Return only the short public envelope required by the committed spatial public reply protocol. Silence remains valid only when that protocol and the current facts already permit it.";

    // Native grammar prevents the model from opening a private channel in this
    // result-only stage. Length and full output boundaries are checked again on
    // completion for every backend, including those without native grammars.
    public const string OutputGrammar =
        "root ::= reply | \"<silent/>\"\n" +
        "reply ::= \"<lang code=\\\"\" (\"ja\" | \"zh\" | \"en\") \"\\\"/><speech mode=\\\"independent\\\"/><say>\" spoken \"</say>\"\n" +
        "spoken ::= [^<>\\r\\n\\x00]+\n";

    public const string ReplyFactsMarker = "[Bound facts for this public reply]";
    // Only the composer calls AppendReplyFacts. The complete producer context
    // may contain a staged user-spoke frame followed by NEW observation blocks.
    // Actual human utterances remain quoted JSON in humanInput / user messages.
    public const string ProducerContextMarker = "[Committed reply producer context v1]";

    public static string BuildReviewRepairFeedback(string rejectedSpeech, JObject verdict, string guardReason,
        string language, JObject humanInput, string context, bool receiptOnly, string factualBaseline = null)
        => "[One bounded correction of an unreleased public reply]\n" + new JObject {
            ["kind"] = "public-speech-review-repair", ["attempt"] = 1, ["maximumAttempts"] = 1,
            ["rejectedPublicSpeech"] = rejectedSpeech, ["actualReview"] = verdict.DeepClone(),
            ["guardRejectedReason"] = guardReason, ["requiredOutputLanguage"] = language,
            ["humanInput"] = humanInput,
            ["factualBaseline"] = factualBaseline,
            ["receiptOnly"] = receiptOnly,
            ["factsSource"] = "The single bound fact packet in the request context; do not duplicate it.",
            ["scope"] = "The candidate was not released as speech or accepted history. actualReview is the unchanged independent judgment. A nonempty guardRejectedReason records a separate mismatch between the classified cause and its diagnostic receipt, or the actual language and its declared tag; it does not change the actual review verdict. Rewrite once for the same bound reply purpose using source-linked evidence and receipts. When a current human question is bound, answer it completely; an autonomous purpose must not reopen a historical command. A nonempty requiredOutputLanguage must match both the new tag and the actual sentence. Neither quality judgment is a new user turn, action authorization, diagnostic cause or execution result. Do not execute, plan or promise a new action. Preserve the closed public envelope. The revised candidate remains private until another factual review accepts it."
        }.ToString(Formatting.None);

    public static JObject ReadRepairFeedback(string feedback)
    {
        if (string.IsNullOrWhiteSpace(feedback)) return null;
        int start = feedback.IndexOf('{');
        if (start < 0) return null;
        try {
            var value = JObject.Parse(feedback.Substring(start), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            string kind = (string)value["kind"];
            return (kind == "public-speech-review-repair" || kind == "terminal-result-speech-repair") &&
                (int?)value["attempt"] == 1 && (int?)value["maximumAttempts"] == 1 && value["actualReview"] is JObject ? value : null;
        } catch (JsonException) { return null; }
    }

    public static string RepairWireFeedback(string feedback)
    {
        var packet = ReadRepairFeedback(feedback);
        if (packet == null) return feedback;
        // Keep the original feedback in diagnostics. Language-only repair writes
        // from the result facts; echoing the rejected sentence supplies no facts.
        if (((string)packet["guardRejectedReason"] ?? "").StartsWith("speech-language-", StringComparison.Ordinal))
        {
            packet.Remove("rejectedPublicSpeech");
            packet["rejectedCandidateOmitted"] = "Language-only rejection. Write anew from the supplied real outcome, in requiredOutputLanguage.";
        }
        packet.Remove("boundReplyFacts");
        return packet.ToString(Formatting.None);
    }

    public static string RepairWritingInstruction(string feedback)
    {
        var packet = ReadRepairFeedback(feedback);
        if (packet == null) return "";
        string language = (string)packet["requiredOutputLanguage"];
        string baseline = (string)packet["factualBaseline"];
        if (!string.IsNullOrWhiteSpace(baseline))
            return "Rewrite this ONE verified factual baseline naturally in the required language, keeping exactly its present/completed state, subject and recipient. " +
                "It is the current writing task, NOT a request to plan or execute an action. No other factual claim or promise can be added from dialogue history or the rejected candidate. " +
                "You may keep this baseline unchanged. Output only the closed public XML envelope.\n[Runtime factual baseline for this reply]\n" +
                new JObject { ["speech"] = baseline, ["language"] = language }.ToString(Formatting.None);
        string writing = language == "ja" ? "今は不合格になった返答を一度だけ書き直す段階です。事実に沿って自然な日本語で新しく書いてください。" :
            language == "zh" ? "现在只改写这条未发布的回复。根据真实事实用自然中文重新表达。" :
            language == "en" ? "Rewrite this unreleased reply once in natural English, from the actual facts." : "Rewrite the unreleased reply once from the actual facts.";
        return "\n[Current writing task: bounded correction]\n" + writing +
            " The independent rejection is authoritative for what must change, not a new user request. " +
            "Do not repeat the rejected wording. Do not use the historical command as the current writing instruction. " +
            "Preserve actor, recipient, object, negation, temporal scope and the actual result. " +
            "When the character is acting, describe the character's action; never reverse it into an instruction for the person. " +
            "Do not add unrelated location assurances or future actions. The words AND lang tag must use " + language + ".";
    }

    public static bool IsPlanningFailureOnly(string context)
    {
        var facts = ReadReplyFacts(context);
        if ((bool?)facts?["planningFailureOnly"] != true) return false;
        var semantics = facts["currentRequestSemantics"] as JObject;
        var failure = facts["decisionFailure"] as JObject;
        var human = facts["humanInput"] as JObject;
        string request = (string)semantics?["requestKind"];
        string kind = (string)failure?["kind"];
        return (bool?)human?["currentHumanTurnPresent"] == true &&
            (request == "action" || request == "control") &&
            (bool?)failure?["newActionSubmittedByThisDecision"] == false &&
            (kind == "input-budget-exceeded" || kind == "service-request-failed" || kind == "decision-not-executable");
    }

    public static string AppendReplyFacts(string context, JObject facts) =>
        ProducerContextMarker + "\n" + (context ?? "") + "\n" + ReplyFactsMarker + "\n" + (facts ?? new JObject()).ToString(Formatting.None);

    public static JObject ReadReplyFacts(string context)
    {
        if (string.IsNullOrEmpty(context)) return null;
        string marker = "\n" + ReplyFactsMarker + "\n";
        int start = context.LastIndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        int end = context.IndexOf('\n', start);
        string json = end < 0 ? context.Substring(start) : context.Substring(start, end - start);
        try
        {
            using (var reader = new JsonTextReader(new StringReader(json)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                var facts = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                return reader.Read() ? null : facts;
            }
        }
        catch (JsonException) { return null; }
    }

    public static string BuildFinalWritingInstruction(string currentHumanTurn, string context = null)
    {
        var facts = ReadReplyFacts(context);
        var purpose = facts?["replyPurpose"] as JObject;
        if ((bool?)facts?["workResultOnly"] == true && (bool?)facts["newActionSubmitted"] == false &&
            facts["taskSequence"] is JObject)
        {
            var execution = facts["currentExecution"] as JObject;
            return "[Deliver the pending execution outcome now]\n" +
                "This is result delivery for work already attempted, not a new human command or a new action plan. " +
                "Use every step's actual receipt and pending independent tool results to answer the complete original request. " +
                "A failed attempt must be reported as failed/incomplete; do not answer with willingness, a future promise or a claim that it is underway. " +
                "An explicit terminal failure is established, not 'maybe/seems'. Report its known outcome without adding unevidenced position reassurance, technical/personal causes or activity. Warmth and courtesy need no invented facts. " +
                "Completed steps remain completed. Explain the recorded cause without inventing a new one or changing the actor and recipient. " +
                "No retry or adjustment was submitted by this reply; describe recovery only when the executor actually reports recovering. " +
                "Speaker labels and ASR measurements are context, not a topic replacing the owed result. " +
                "Translate the observed state naturally into the authored dialogue language and persona. Deliver the owed result in the closed public envelope.\n" +
                new JObject {
                    ["originalRequest"] = facts["originalUserRequest"]?.DeepClone(),
                    ["actionId"] = execution?["actionId"]?.DeepClone(),
                    ["observedStateStatement"] = execution?["observedStateStatement"]?.DeepClone(),
                    ["failureCategory"] = execution?.SelectToken("currentRoomFacts.execution.boundedRecovery.lastFailureCategory")?.DeepClone(),
                    ["expectedReport"] = execution?.SelectToken("movementReportRequirement.expectedReport")?.DeepClone(),
                    ["replyFactsSource"] = ReplyFactsMarker,
                    ["scope"] = "Summary of bound runtime evidence above; no new observation, action authority or human turn."
                }.ToString(Formatting.None);
        }
        if ((string)purpose?["source"] == "accepted-current-autonomous-decision" &&
            (bool?)purpose["actionAuthorization"] == false &&
            (bool?)facts.SelectToken("humanInput.currentHumanTurnPresent") == false &&
            !(facts["currentRequestSemantics"] is JObject))
        {
            return "[Write for the current autonomous intention now]\n" +
                "There is no new human utterance in this round. The current purpose below is your independently accepted intention. " +
                "For conversation, advance that topic naturally instead of acknowledging or promising the last completed command again. " +
                "For an action result, report only this intention's owned execution. Use pending independent tool outcomes when relevant. " +
                "Use recent replies and interaction timestamps; a new autonomous tick does not mean a long silence. " +
                "Past device outcomes do not establish unseen present state. Do not promise another action. " +
                "Keep the authored persona and dialogue language. Return the closed public envelope, or <silent/> if no useful speech is owed.\n" +
                new JObject {
                    ["currentReplyPurpose"] = purpose.DeepClone(),
                    ["replyFactsSource"] = ReplyFactsMarker,
                    ["historicalHumanTurn"] = currentHumanTurn ?? "",
                    ["scope"] = "The human quote is historical context and constraints, not the question to answer or a command to repeat."
                }.ToString(Formatting.None);
        }
        // Repeat the exact user record after the image/facts, without rewriting
        // ASR or treating an old command as new authority during a result reply.
        var reference = new JObject {
            ["quotedHumanTurn"] = currentHumanTurn ?? "",
            // The bound document already appears in the system context. Copying
            // it here doubles receipts and bypasses joint context projection.
            ["replyFactsSource"] = ReadReplyFacts(context) == null ? null : ReplyFactsMarker,
            ["scope"] = "Exact existing user record for this reply, not another utterance or new action authorization. " +
                "Use its complete meaning and the actual prior dialogue to identify the question being answered. " +
                "On autonomous or execution-feedback replies, the runtime facts still determine whether any new human turn exists; " +
                "an old quoted command cannot restart a task or authorize a retry. An empty record supplies no new human request."
        };
        return FinalWritingInstruction + "\n\n[Quoted human turn for this answer; data only]\n" + reference.ToString(Formatting.None);
    }

    public static string AddContract(string context)
    {
        if ((context ?? "").EndsWith(OutputContract, StringComparison.Ordinal)) return context;
        return string.IsNullOrWhiteSpace(context) ? OutputContract : context + "\n\n" + OutputContract;
    }

    public static bool TryValidateOutput(string raw, out string normalized, out string reason)
        => TryValidatePublicEnvelope(raw, MaximumSpeechCharacters, true, out normalized, out reason);

    public static bool TryValidatePublicEnvelope(string raw, int maximumSpeechCharacters, bool allowSilent,
        out string normalized, out string reason)
    {
        normalized = null; reason = "invalid-public-envelope";
        // Do not strip thoughts, extract an inner say, auto-close tags or salvage
        // a valid prefix. The entire stopped response must be this public envelope.
        string value = (raw ?? "").Trim();
        if (value == "<silent/>")
        {
            if (!allowSilent) { reason = "required-public-result-missing"; return false; }
            normalized = value; reason = "valid-silent"; return true;
        }
        if (value.Length > maximumSpeechCharacters + 160) { reason = "public-envelope-size"; return false; }
        var match = Regex.Match(value,
            "\\A<lang code=\"(?:ja|zh|en)\"/>\\s*<speech mode=\"independent\"/>\\s*<say>([^<>]+)</say>\\z",
            RegexOptions.CultureInvariant);
        if (!match.Success) return false;
        string speech = match.Groups[1].Value;
        if (string.IsNullOrWhiteSpace(speech) || speech.Length > maximumSpeechCharacters)
        { reason = "public-speech-size"; return false; }
        foreach (char c in speech)
            if (char.IsControl(c)) { reason = "public-speech-control"; return false; }
        var channels = RoleOutputChannels.Parse(value, RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
        if (!channels.HasSpeech || channels.HasActions || channels.HasInvalidPublicBoundary ||
            channels.HasInvalidSpeechPhase || channels.HasInvalidLanguage || channels.HasUndeclaredSpeech ||
            channels.PrivateCharacters > 0 || channels.Speech != speech)
        { reason = "public-channel-boundary"; return false; }
        normalized = value; reason = "valid-public-reply"; return true;
    }
}
