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
        "This request writes a public answer to the complete current human request. It may report an existing task result or answer an evidence-only question with no new action decision. " +
        "Determine the current question from the complete human utterance and actual prior dialogue before choosing relevant evidence. Acoustic labels, repeated ASR alternatives, images and old tasks are evidence, not new requests or default topics. A question about a prior explanation or failure calls for the relevant historical receipt; a question about the current view calls for visual evidence. Do not substitute an earlier question or describe a picture merely because it is attached. " +
        "Return one short public reply of at most 360 characters, using the supplied persona and dialogue language. " +
        "Output exactly <lang code=\"ja|zh|en\"/><speech mode=\"independent\"/><say>public reply</say>, with one actual language code. " +
        "For an autonomous observation where no reply is owed, <silent/> remains an alternative, subject to the existing reply review. " +
        "Do not output thought, think, reasoning, markdown, after_action, tools, motion, next, multiple say blocks or any text outside this envelope. " +
        "Do not reconsider how to execute the task. Preserve the distinction between a request, a submitted action, an actual result and a service failure. " +
        "A request failure is not physical inability; do not promise another attempt that was not submitted. " +
        "The latest actual facts and complete human request determine the answer. Missing identity evidence is not proof that the user gave no command. " +
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
        // Repeat the exact user record after the image/facts, without rewriting
        // ASR or treating an old command as new authority during a result reply.
        var reference = new JObject {
            ["quotedHumanTurn"] = currentHumanTurn ?? "",
            ["replyFacts"] = ReadReplyFacts(context),
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
