using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Delivery evidence never changes an execution result or grants a retry.</summary>
public static class WorkResultDeliveryProtocol
{
    [Serializable] public sealed class Report { public string id, comparison, coverage, quote; }
    public const string Contract = "\n[结果交付结算] delivery.results是当前事项的实际结果版本。按顺序对前12个delivered不为true的结果各填一项delivery_reports，不得漏项/重复；id原样复制版本标识，不能用步骤名或source。每项先用comparison简短对照该对象/动作/时间的结果与实际已播内容，再填quote。已明确说到该项才逐字引用；未说到则quote为空，不能借另一项的说明填满。设备结果不能结算返回，返回不能结算独立问题；同一句仅在确实包含两项意思时才能覆盖两项。引用该结果completedPlaybackText（旧资料缺此字段时用delivery.completedPlaybackText），不能引用请求、工具事实或生成稿。" +
        "coverage独立判定交付：reported=实际已播台词明确告知本项结果；unreported=尚未告知或只说开始/愿意/稍等/别的步骤。只有reported才填结果引句；unreported的quote为空。执行completed/failed均不决定coverage。hasCompletedPlaybackAfterResult=false时必为unreported，不能拿旧播报结算。失败/取消也可已告知：动作结果保持失败，不要求重试或补充未被请求的新解释。delivered=true的相同结果不再欠说明，除非当前请求另有明确追问或新结果版本。" +
        "先逐项检查每个结果与已播文字，再决定事项是否结束：已播明确说明未完成及当前失败结果，就必须列入该失败的delivery_reports；不能只列成功结果。交付不要求动作成功，也不要求说出底层错误名或所有细节。remaining_request_quote只引用当前原请求中确实还欠的独立问题/要求，不能引用已告知的失败步骤来制造新义务；没有则空。" +
        "source=independent-answer表示本轮独立问题，facts.questionQuote是逐字原问。用实际已播答案结算，包括明确且诚实的不确定回答；不能用另一动作结果、确认语或承诺替代。核对对象、行动者和时间：现在完成不等于回答之前是否发生。" +
        "无可执行步骤、全部结果已告知且无独立欠答时closed，不把失败改为成功；有独立答案未交付时继续回答，保留原请求、归属及合法的新自主意图。";

    public static JObject Result(string source, string status, JToken facts)
    {
        string serialized = new JObject { ["source"] = source, ["status"] = status, ["facts"] = facts?.DeepClone() }.ToString(Formatting.None);
        string id;
        using (var hash = SHA256.Create()) id = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(serialized))).Replace("-", "").Substring(0, 16);
        return new JObject { ["id"] = id, ["source"] = source, ["status"] = status, ["facts"] = facts?.DeepClone() };
    }

    public static string CoverageRejection(JObject result, Report report, string completed)
    {
        if (report == null) return "report-missing";
        if (result == null || report.id != (string)result["id"]) return "result-version-not-current";
        if (report.coverage != "reported") return report.coverage == "unreported" ? "not-reported" : "coverage-not-classified";
        if (string.IsNullOrWhiteSpace(report.quote)) return "not-reported";
        if ((bool?)result["hasCompletedPlaybackAfterResult"] == false) return "no-completed-playback-after-result";
        return (completed ?? "").Contains(report.quote) ? null : "quote-not-in-result-playback";
    }

    public static bool Covers(JObject result, Report report, string completed) => CoverageRejection(result, report, completed) == null;

    // Page only unresolved results. A bounded review cannot silently erase the
    // rest: CanClose still checks the complete runtime ledger.
    public static string[] ReviewIds(JArray results) => (results ?? new JArray()).OfType<JObject>()
        .Where(r => (bool?)r["delivered"] != true).Select(r => (string)r["id"])
        .Where(id => !string.IsNullOrEmpty(id)).Distinct().Take(12).ToArray();

    public static void BindCoverageSchema(JObject schema, JArray results)
    {
        var ids = ReviewIds(results);
        var reports = schema["properties"]["delivery_reports"];
        reports["minItems"] = ids.Length; reports["maxItems"] = ids.Length;
        if (ids.Length > 0) reports["items"]["properties"]["id"]["enum"] = new JArray(ids);
    }

    public static bool ValidateCoverage(JArray results, JObject decision, out string failure)
    {
        failure = null;
        if (results == null) return true; // Legacy callers without a delivery packet.
        var ids = ReviewIds(results);
        var rows = decision?["delivery_reports"] as JArray;
        if (rows == null || rows.Count != ids.Length || rows.OfType<JObject>().Count() != rows.Count ||
            !ids.All(id => rows.OfType<JObject>().Count(row => (string)row["id"] == id) == 1))
        { failure = "delivery-coverage-incomplete-or-duplicate"; return false; }
        foreach (var row in rows.OfType<JObject>())
        {
            if (string.IsNullOrWhiteSpace((string)row["comparison"]))
            { failure = "delivery-comparison-missing"; return false; }
            if (!new[] { "reported", "unreported" }.Contains((string)row["coverage"]))
            { failure = "delivery-classification-missing"; return false; }
        }
        return true;
    }

    public static JObject StepSummary(JObject facts)
    {
        var summary = (JObject)facts.DeepClone(); summary.Remove("receipt");
        var receipt = facts["receipt"] as JObject;
        if (receipt?["goal"] is JObject goal)
        {
            var outcome = new JObject();
            foreach (string field in new[] { "goalId", "status", "reason", "ownerCurrent", "completionEvidenceValid", "boundEntityId", "desiredState", "operationSummary", "completionEvidence" })
                if (goal[field] != null) outcome[field] = goal[field].DeepClone();
            summary["outcome"] = outcome;
            if (string.IsNullOrEmpty((string)summary["actionId"]) && goal.SelectToken("completionEvidence.actionId") != null)
                summary["actionId"] = goal.SelectToken("completionEvidence.actionId").DeepClone();
        }
        else summary["outcome"] = RoomTaskContextPolicy.DeliveryReceipt(receipt);
        return summary;
    }

    public static bool MustContinue(string status, JArray results, string remainingQuote, string originalRequest)
    {
        bool independent = !string.IsNullOrWhiteSpace(remainingQuote);
        return independent && !(originalRequest ?? "").Contains(remainingQuote) ||
            (status == "closed" || status == "none") && (independent ||
                results != null && results.OfType<JObject>().Any(r => (bool?)r["delivered"] != true));
    }

    public static bool IsSettledActionQuote(string quote, JObject sequence, System.Collections.Generic.IEnumerable<string> deliveredSources)
    {
        string Normal(string text) => (text ?? "").Trim().Trim('。', '.', '！', '!', '？', '?', '，', ',', '；', ';').Trim();
        if (string.IsNullOrWhiteSpace(quote) || !(sequence?["steps"] is JArray steps) || !(sequence["coverage"] is JArray coverage)) return false;
        string normalized = Normal(quote);
        // Only an exactly covered action clause can be discharged. Composite
        // clauses, answer spans and unparsed requests stay with the reviewer.
        var spans = coverage.OfType<JObject>().Where(c => Normal((string)c["quote"]) == normalized).ToArray();
        if (spans.Length != 1 || (string)spans[0]["kind"] != "action" || !(spans[0]["stepIds"] is JArray ids) || ids.Count == 0) return false;
        return ids.Values<string>().All(id => deliveredSources.Contains("step:" + id) && steps.OfType<JObject>().Any(s =>
            (string)s["id"] == id && new[] { "completed", "failed" }.Contains((string)s["status"])));
    }

    public static bool ValidExtension(JObject value)
    {
        var reports = value["delivery_reports"];
        if (reports != null && (!(reports is JArray rows) || rows.Count > 12 || rows.Any(r => !(r is JObject row) ||
            row.Count < 2 || row.Count > 4 || row.Properties().Any(p => p.Name != "id" && p.Name != "quote" && p.Name != "comparison" && p.Name != "coverage") ||
            row["coverage"] != null && (row["coverage"].Type != JTokenType.String || !new[] { "reported", "unreported" }.Contains((string)row["coverage"])) ||
            row["comparison"] != null && (row["comparison"].Type != JTokenType.String || ((string)row["comparison"]).Length > 120) ||
            row["id"]?.Type != JTokenType.String || ((string)row["id"]).Length > 64 ||
            row["quote"]?.Type != JTokenType.String || ((string)row["quote"]).Length > 600))) return false;
        return value["remaining_request_quote"] == null || value["remaining_request_quote"].Type == JTokenType.String && ((string)value["remaining_request_quote"]).Length <= 600;
    }

    public static bool CanClose(JArray results, JObject sequence, string remainingQuote) =>
        results != null && results.Count > 0 && results.OfType<JObject>().All(r => (bool?)r["delivered"] == true) &&
        string.IsNullOrWhiteSpace(remainingQuote) &&
        (!(sequence?["steps"] is JArray steps) || steps.OfType<JObject>().All(s => new[] { "completed", "failed" }.Contains((string)s["status"])));

    public static void ExtendSchema(JObject schema)
    {
        schema["properties"]["delivery_reports"] = new JObject { ["type"] = "array", ["maxItems"] = 12,
            ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("id", "comparison", "coverage", "quote"),
                ["properties"] = new JObject { ["id"] = new JObject { ["type"] = "string", ["maxLength"] = 64 },
                    ["comparison"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 120 },
                    ["coverage"] = new JObject { ["type"] = "string", ["enum"] = new JArray("unreported", "reported") },
                    ["quote"] = new JObject { ["type"] = "string", ["maxLength"] = 600 } } } };
        schema["properties"]["remaining_request_quote"] = new JObject { ["type"] = "string", ["maxLength"] = 600 };
        ((JArray)schema["required"]).Add("delivery_reports"); ((JArray)schema["required"]).Add("remaining_request_quote");
        // Resolve played coverage before generating a continuation. Otherwise
        // an early "continue" biases the later report list toward missing failure.
        var properties = (JObject)schema["properties"];
        var ordered = new JObject { ["delivery_reports"] = properties["delivery_reports"].DeepClone(),
            ["remaining_request_quote"] = properties["remaining_request_quote"].DeepClone() };
        foreach (var field in properties.Properties())
            if (ordered[field.Name] == null) ordered[field.Name] = field.Value.DeepClone();
        schema["properties"] = ordered;
    }
}
