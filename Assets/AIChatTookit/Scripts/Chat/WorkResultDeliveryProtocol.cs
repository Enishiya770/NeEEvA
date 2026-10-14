using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Delivery evidence never changes an execution result or grants a retry.</summary>
public static class WorkResultDeliveryProtocol
{
    [Serializable] public sealed class Report { public string id, quote; }
    public const string Contract = "\n[结果交付结算] delivery.results是当前事项的实际结果版本。delivery_reports的id必须原样复制results[].id的版本标识，绝不能使用步骤名s1/s2或source。只列本次能在delivery.completedPlaybackText中逐字引用、语义上已明确告知该结果的{id,quote}；未播草稿、稍等或正在做不能结算终局结果。" +
        "hasCompletedPlaybackAfterResult=false表示该版本出现后尚无完整播报，不能拿旧播报结算。失败/取消也可已告知：动作结果保持失败，不要求重试或补充未被请求的新解释。delivered=true的相同结果不再欠说明，除非当前请求另有明确追问或新结果版本。" +
        "先逐项检查每个结果与已播文字，再决定事项是否结束：已播明确说明未完成及当前失败结果，就必须列入该失败的delivery_reports；不能只列成功结果。交付不要求动作成功，也不要求说出底层错误名或所有细节。remaining_request_quote只引用当前原请求中确实还欠的独立问题/要求，不能引用已告知的失败步骤来制造新义务；没有则空。" +
        "无可执行步骤、全部结果已告知且无独立欠答时closed，不把失败改为成功；有独立答案未交付时继续回答，保留原请求、归属及合法的新自主意图。";

    public static JObject Result(string source, string status, JToken facts)
    {
        string serialized = new JObject { ["source"] = source, ["status"] = status, ["facts"] = facts?.DeepClone() }.ToString(Formatting.None);
        string id;
        using (var hash = SHA256.Create()) id = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(serialized))).Replace("-", "").Substring(0, 16);
        return new JObject { ["id"] = id, ["source"] = source, ["status"] = status, ["facts"] = facts?.DeepClone() };
    }

    public static bool Covers(JObject result, Report report, string completed) => result != null && report != null &&
        (bool?)result["hasCompletedPlaybackAfterResult"] != false &&
        report.id == (string)result["id"] && !string.IsNullOrWhiteSpace(report.quote) &&
        (completed ?? "").Contains(report.quote);

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
        if (reports != null && (!(reports is JArray rows) || rows.Count > 12 || rows.Any(r => !(r is JObject row) || row.Count != 2 ||
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
            ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("id", "quote"),
                ["properties"] = new JObject { ["id"] = new JObject { ["type"] = "string", ["maxLength"] = 64 },
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
