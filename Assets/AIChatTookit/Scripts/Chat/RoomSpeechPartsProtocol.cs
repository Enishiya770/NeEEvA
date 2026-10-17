using System;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

/// <summary>Audited text ownership, not action authority or a text-based movement trigger.</summary>
public static class RoomSpeechPartsProtocol
{
    public const string Contract =
        "When speechProgressOwner is present, bodyProgressParts is an array of IDs from progressSpeechParts, not rewritten text. " +
        "Select ONLY fragments actually describing the CHARACTER's claims/promises about the specific movement toward/following/stopping relative to USER identified by speechProgressOwner, including future commitments and past completion. " +
        "The runtime replaces selected fragments with current execution reporting. Do NOT select device results, independent answers, observations, questions, courtesy or other actions. " +
        "Return [] if the speech only reports the device and thanks the listener or asks a social question. Never select a fragment just because the user also requested movement. " +
        "A negated current result (I have not arrived yet) is also current-body; quoted/hypothetical actions or statements about a different old task are independent. Do not classify an invitation for USER to move as CHARACTER movement. " +
        "Still review the COMPLETE original speech for factual consistency and task relations before partitioning: ownership never excuses a reversed invitation or an unsupported claim. " +
        "Protected result fragments are excluded from selectable IDs. After the otherClaimsComparison, select IDs only for fragments that comparison identifies as the CHARACTER's owned movement. " +
        "If comparison identifies only courtesy/questions and no movement, bodyProgressParts must be []. Unselected fragments remain unchanged. " +
        "bodyProgressParts只列出台词中角色自身当前移动的分句编号；otherClaimsSupport=none时必须为空数组[]，感谢、道歉、邀请聊天不能被当前移动替换。" +
        "失败结果、当前停止/等待、未来行动承诺是不同命题。‘先停下/暂时等待/いったん止まる/stop for now’本身不承诺恢复或重试；没有明确将来行动句时，不能仅凭‘暂时’判未来承诺冲突。一次尝试未完成同时当前停下是相容的。" +
        "回答独立问题的算术、逻辑与一般知识按其自身依据核对，不能因没有场景或设备回执就判unsupported；可直接验算的算术在otherClaimsComparison写明验算依据。现场物件、位置、动作与设备状态仍须对应现场证据，不能借一般知识虚构。";

    public static bool Required(JObject input) => input?["speechProgressOwner"] is JObject owner &&
        !string.IsNullOrEmpty((string)owner["actionId"]) && (string)owner["targetKind"] == "user" &&
        new[] { "approach", "follow", "stop-moving" }.Contains((string)owner["intent"]);

    public static void AddSchema(JObject schema, JObject input)
    {
        if (!Required(input)) return;
        var eligible = Fragments(input).OfType<JObject>().Where(p => (bool?)p["protectedResult"] != true).Select(p => (string)p["id"]).ToArray();
        ((JArray)schema["required"]).Add("bodyProgressParts");
        schema["properties"]["bodyProgressParts"] = new JObject {
            ["type"] = "array", ["maxItems"] = eligible.Length, ["uniqueItems"] = true,
            ["description"] = "IDs of CHARACTER movement clauses only. Empty [] if otherClaimsSupport is none. Keep courtesy, questions and independent answers.",
            ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray(eligible.Length == 0 ? new[] { "none" } : eligible) }
        };
    }

    public static JArray Fragments(JObject input)
    {
        string speech = (string)input["publicSpeech"] ?? "", evidence = (string)input.SelectToken("independentSpeechClaim.evidence") ?? "";
        var quotes = new List<string> { evidence, (string)input.SelectToken("independentSpeechClaim.pendingSameResultQuote") };
        if (input.SelectToken("independentSpeechClaim.otherClaims") is JArray other) quotes.AddRange(other.Values<string>());
        var spans = quotes.Where(q => !string.IsNullOrEmpty(q) && speech.Contains(q))
            .Select(q => new { start = speech.IndexOf(q, StringComparison.Ordinal), end = speech.IndexOf(q, StringComparison.Ordinal) + q.Length }).ToArray();
        int protectedStart = string.IsNullOrEmpty(evidence) ? -1 : speech.IndexOf(evidence, StringComparison.Ordinal);
        var ends = new SortedSet<int> { speech.Length };
        for (int i = 0; i < speech.Length; i++)
            if ("。！？!?；;，,、".IndexOf(speech[i]) >= 0 || speech[i] == '.' &&
                !(i > 0 && i + 1 < speech.Length && char.IsDigit(speech[i - 1]) && char.IsDigit(speech[i + 1])))
                if (!spans.Any(s => s.start < i + 1 && s.end > i + 1)) ends.Add(i + 1);
        foreach (var span in spans) ends.Add(span.end);
        // Attach trailing punctuation/space to its clause instead of generating
        // meaningless punctuation-only classifications or forcing paraphrases.
        var adjusted = new SortedSet<int>();
        foreach (int end in ends) {
            int at = end;
            while (at < speech.Length && (char.IsWhiteSpace(speech[at]) || "。！？!?.,，;；、".IndexOf(speech[at]) >= 0)) at++;
            adjusted.Add(at);
        }
        var result = new JArray(); int start = 0;
        foreach (int end in adjusted) {
            if (end <= start) continue;
            result.Add(new JObject { ["id"] = "p" + result.Count, ["text"] = speech.Substring(start, end - start),
                ["protectedResult"] = protectedStart >= 0 && start < protectedStart + evidence.Length && end > protectedStart });
            start = end;
        }
        return result;
    }

    public static bool Resolve(JObject input, JObject review, out string failure)
    {
        failure = "speech-parts-invalid-selection";
        if (!(review["bodyProgressParts"] is JArray chosen) || chosen.Any(t => t.Type != JTokenType.String)) return false;
        var ids = chosen.Values<string>().ToArray(); var fragments = Fragments(input);
        // A reviewer that found no extra assertion or own-action commitment
        // cannot simultaneously mark an independent courtesy/question as motion.
        if ((string)review["verdict"] == "consistent" && (string)review["otherClaimsSupport"] == "none" && ids.Length > 0)
        { failure = "speech-parts-contradict-claim-support"; return false; }
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => !fragments.OfType<JObject>().Any(p =>
            (string)p["id"] == id && (bool?)p["protectedResult"] != true))) return false;
        review["speechParts"] = new JArray(fragments.OfType<JObject>().Select(p => new JObject {
            ["owner"] = ids.Contains((string)p["id"]) ? "current-body" : "independent", ["text"] = p["text"].DeepClone() }));
        review.Remove("bodyProgressParts");
        return TryRead(review, (string)input["publicSpeech"], out _, out failure);
    }

    public static bool TryRead(JObject review, string speech, out string independent, out string failure)
    {
        independent = null; failure = "speech-parts-invalid";
        if (!(review?["speechParts"] is JArray parts) || parts.Count == 0 || parts.Count > Math.Max(12, speech?.Length ?? 0)) return false;
        string all = "", kept = ""; bool removed = false;
        bool english = (string)review["actualLanguage"] == "en" ||
            review["actualLanguage"] == null && (speech ?? "").All(c => c < 128);
        foreach (var token in parts)
        {
            if (!(token is JObject part) || part.Count != 2 || part["text"]?.Type != JTokenType.String ||
                part["owner"]?.Type != JTokenType.String || string.IsNullOrEmpty((string)part["text"]) ||
                !new[] { "independent", "current-body" }.Contains((string)part["owner"])) return false;
            all += (string)part["text"];
            if ((string)part["owner"] == "independent")
            {
                if (removed && kept.Length > 0) kept = EndIndependentClause(kept, english);
                kept += (string)part["text"]; removed = false;
            }
            else removed = true;
        }
        if (all != speech) { failure = "speech-parts-incomplete-coverage"; return false; }
        independent = (removed && kept.Length > 0 ? EndIndependentClause(kept, english) : kept).Trim();
        failure = null; return true;
    }

    private static string EndIndependentClause(string text, bool english)
    {
        string trimmed = text.TrimEnd();
        if (trimmed.Length == 0) return text;
        // Only repair punctuation left by removing a joined movement clause;
        // never rewrite an independently reviewed word or infer a new assertion.
        if (",，;；、".IndexOf(trimmed[trimmed.Length - 1]) >= 0)
            return trimmed.TrimEnd(',', '，', ';', '；', '、') + (english ? ". " : "。");
        if (".。！？!?".IndexOf(trimmed[trimmed.Length - 1]) < 0)
            return trimmed + (english ? ". " : "。");
        return text;
    }
}
