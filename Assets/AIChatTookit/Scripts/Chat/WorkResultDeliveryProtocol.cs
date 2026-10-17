using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Delivery evidence never changes an execution result or grants a retry.</summary>
public static class WorkResultDeliveryProtocol
{
    [Serializable] public sealed class Report { public string id, comparison, coverage, quote, playedRevision, lastRelevantQuote, finalDisposition, reportAct, resultScope, playbackContract, playbackMeaning, selectedPlaybackId; }
    public const string Contract = "\n[结果交付结算] delivery.results是实际结果版本。对前12个delivered!=true或playbackReviewPending=true的结果逐项填delivery_reports，不漏项/重复；id照抄，不用步骤名或source代替。resultScope明确本行的操作/对象：先定位这个ID自己的facts，逐行独立对比；不能把后一操作的交付要求套到前一操作。pending只表示后句待核对，不是先前交付失效；无关话语或等价说法仍reported/consistent，引用最后相关已播句。只有后句实际撤回或矛盾才retracted/unresolved；不能把已交付项改成从未报告。结合原任务消解省略和指代，返回、保存、生成等自然结果表达不必逐字重复对象/地点，不额外要求固定句式。comparison先解释所选已播引句实际说了什么动作/对象/时间，再与结果比较。不能从回执补进原话没有的完成含义；单独确认、现在前往不能报告另一操作已完成；quote只逐字引本项completedPlaybackText（旧资料缺字段才用delivery.completedPlaybackText），不引请求、工具数据或生成稿。不同步骤/问题分别结算，同句须确实包含两项结果才能共用。" +
        "reportAct按已播话语自身含义分类为outcome-report/unknown-answer/progress-report/promise/acknowledgment/other，只有前两类可reported。完成事实不能把未来承诺变成结果报告。" +
        "coverage：reported=已播明确告知该项；unreported=未告知、仅开始/承诺/稍等/别项，quote为空。hasCompletedPlaybackAfterResult=false必为unreported。完成与失败均须核对，不能只报告成功；失败/取消已告知也可结算，无须重试或说出底层错误名。相同delivered=true结果不再欠说明，新追问/结果版本除外。" +
        "source=independent-answer的facts.questionQuote是本轮独立原问；诚实说明对应问题未知可结算，不能用其他动作/确认/承诺代替。现在完成不证明过去发生。remaining_request_quote只引当前请求仍欠的独立问题/要求，没有则空；已告知的失败不制造新义务。无可执行步骤且所有结果/问题已交付时closed，保留失败状态和合法新自主意图；欠答则继续。" +
        "按实际已播完整顺序审核：playedRevision照抄playbackRevision，lastRelevantQuote引最后涉及本结果的原话。finalDisposition：consistent=最终说法符合结果，retracted=后句撤回正确结果，unresolved=矛盾未解，unreported=未说明。后三者coverage=unreported、quote为空；consistent时quote=lastRelevantQuote。不能摘撤回前的正确一句结算；无关寒暄不撤回结果。" + ResultPlaybackSelection.Contract;

    public static string RepairDetails(JArray results,JObject rejected,string reason) => new JArray(
        (rejected?["delivery_reports"] as JArray ?? new JArray()).OfType<JObject>().Select(row=>
            ResultPlaybackSelection.Repair(results?.OfType<JObject>().FirstOrDefault(r=>(string)r["id"]==(string)row["id"]),row,reason,false))).ToString(Formatting.None);

    public static JObject Result(string source, string status, JToken facts)
    {
        string id=ActivityEvidencePolicy.Stamp(new JObject { ["source"] = source, ["status"] = status, ["facts"] = facts?.DeepClone() });
        return new JObject { ["id"] = id, ["source"] = source, ["status"] = status, ["facts"] = facts?.DeepClone() };
    }

    public static JObject TaskContext(JObject sequence, string request, JObject result)
    {
        string source = (string)result?["source"];
        var step = (sequence?["steps"] as JArray)?.OfType<JObject>().FirstOrDefault(s => source == "step:" + (string)s["id"]);
        return new JObject {
            ["parentRequest"] = (string)sequence?["request"] ?? request ?? "",
            ["step"] = step == null ? null : new JObject(step.Properties().Where(p =>
                new[] { "id", "quote", "kind", "after" }.Contains(p.Name)).Select(p => new JProperty(p.Name, p.Value.DeepClone()))),
            ["scope"] = "Resolve spoken ellipsis against this owned request and step; execution truth comes only from the result receipt."
        };
    }

    public static JObject ReportingScope(JObject sequence, string stepId)
    {
        if(!(sequence?["steps"] is JArray steps) || string.IsNullOrEmpty(stepId))return null;
        var step=steps.OfType<JObject>().SingleOrDefault(s=>(string)s["id"]==stepId);
        if(step==null)return null;
        return new JObject{["stepId"]=stepId,["operation"]=step["kind"], ["quote"]=step["quote"],
            ["status"]=step["status"], ["actionId"]=step["actionId"],
            ["otherSteps"]=new JArray(steps.OfType<JObject>().Where(s=>(string)s["id"]!=stepId).Select(s=>new JObject{
                ["id"]=s["id"],["kind"]=s["kind"],["quote"]=s["quote"],["status"]=s["status"],["actionId"]=s["actionId"]})),
            ["scope"]="Report only this operation's actual result. Other steps retain their separate status; this result cannot establish their completion or promise them. Their own live receipts and playback monitor will report them."};
    }

    public static string CoverageRejection(JObject result, Report report, string completed)
    {
        if (report == null) return "report-missing";
        if (result == null || report.id != (string)result["id"]) return "result-version-not-current";
        if (report.coverage != "reported") return report.coverage == "unreported" ? "not-reported" : "coverage-not-classified";
        if((report.reportAct!=null || (bool?)result["requiresReportAct"]==true) && !new[]{"outcome-report","unknown-answer"}.Contains(report.reportAct))return "delivery-not-an-outcome-report";
        if (string.IsNullOrWhiteSpace(report.quote)) return "not-reported";
        if ((bool?)result["hasCompletedPlaybackAfterResult"] == false) return "no-completed-playback-after-result";
        if (!(completed ?? "").Contains(report.quote)) return "quote-not-in-result-playback";
        return ActivityEvidencePolicy.PlaybackRejection(result,new JObject{["revision"]=report.playedRevision,
            ["lastRelevantQuote"]=report.lastRelevantQuote,["finalDisposition"]=report.finalDisposition},report.quote,completed);
    }

    public static bool Covers(JObject result, Report report, string completed) => CoverageRejection(result, report, completed) == null;

    public static bool RetractionProven(JObject result, Report report, string completed) =>
        (bool?)result?["delivered"] == true && report?.coverage == "unreported" &&
        new[] { "retracted", "unresolved" }.Contains(report.finalDisposition) &&
        report.playedRevision == (string)result["playbackRevision"] &&
        ActivityEvidencePolicy.PlaybackSourceRejection(result,new JObject{["revision"]=report.playedRevision,
            ["lastRelevantQuote"]=report.lastRelevantQuote,["finalDisposition"]=report.finalDisposition},completed)==null &&
        NewRetractionQuote(result, report.lastRelevantQuote, completed) &&
        report.lastRelevantQuote != (string)result["deliveredQuote"];

    public static bool NewRetractionQuote(JObject result, string quote, string completed)
    {
        if (string.IsNullOrWhiteSpace(quote)) return false;
        int boundary = (int?)result?["reviewedPlaybackLength"] ?? 0;
        return boundary >= 0 && boundary <= (completed ?? "").Length &&
            (completed ?? "").Substring(boundary).Contains(quote);
    }

    // Page only unresolved results. A bounded review cannot silently erase the
    // rest: CanClose still checks the complete runtime ledger.
    public static string[] ReviewIds(JArray results) => (results ?? new JArray()).OfType<JObject>()
        .Where(r => (bool?)r["delivered"] != true || (bool?)r["playbackReviewPending"] == true).Select(r => (string)r["id"])
        .Where(id => !string.IsNullOrEmpty(id)).Distinct().Take(12).ToArray();

    public static string ResultScope(JObject result)
    {
        var facts=result?["facts"] as JObject;
        string Value(JToken token)=>token==null||token.Type==JTokenType.Null?"":token.Type==JTokenType.String?(string)token:token.ToString(Formatting.None);
        string operation=Value(facts?["operation"]??facts?["kind"]??facts?["intent"]??result?["source"]);
        string target=Value(facts?["entityId"]??facts?.SelectToken("outcome.boundEntityId")??facts?.SelectToken("outcome.targetKind")??facts?["target"]);
        string state=Value(facts?["desiredState"]??facts?.SelectToken("outcome.desiredState"));
        // Short identity aid, not substituted facts or action permission.
        string stepQuote=Value(result?.SelectToken("taskContext.step.quote"));
        return string.Join(" | ",new[]{"actualStatus="+Value(result?["status"]),operation,target,
            string.IsNullOrEmpty(state)?"":"requestedState="+state,
            string.IsNullOrEmpty(target)&&!string.IsNullOrEmpty(stepQuote)?"stepRequest="+stepQuote:""}.Where(v=>!string.IsNullOrEmpty(v)));
    }

    public static void BindCoverageSchema(JObject schema, JArray results)
    {
        var ids = ReviewIds(results);
        var reports = schema["properties"]["delivery_reports"];
        reports["minItems"] = ids.Length; reports["maxItems"] = ids.Length;
        if (ids.Length > 0) {
            var prototype=(JObject)reports["items"].DeepClone();
            var alternatives=new JArray();
            foreach(string id in ids) {
                var result=results.OfType<JObject>().First(r=>(string)r["id"]==id);
                var row=(JObject)prototype.DeepClone();var properties=(JObject)row["properties"];
                properties["id"]["enum"]=new JArray(id);
                properties["resultScope"]=new JObject{["type"]="string",["enum"]=new JArray(ResultScope(result))};
                ((JArray)row["required"]).Add("resultScope");
                if(result["playbackRevision"]!=null) {
                    var definition=ActivityEvidencePolicy.PlaybackProofSchema(result);
                    ResultPlaybackSelection.BindAudioQuotes(definition,result);
                    var proof=definition["properties"];
                    properties["playedRevision"]=proof["revision"].DeepClone();
                    if(proof["selectedPlaybackId"]!=null) {
                        properties.Remove("lastRelevantQuote");((JArray)row["required"]).Remove("lastRelevantQuote");
                        properties["selectedPlaybackId"]=proof["selectedPlaybackId"].DeepClone();((JArray)row["required"]).Add("selectedPlaybackId");
                    } else properties["lastRelevantQuote"]=proof["lastRelevantQuote"].DeepClone();
                    if(string.IsNullOrEmpty((string)result["completedPlaybackText"])) {
                        properties["coverage"]["enum"]=new JArray("unreported");
                        properties["finalDisposition"]["enum"]=new JArray("unreported");
                    }
                }
                properties["playbackContract"]=new JObject{["type"]="string",["enum"]=new JArray(ResultPlaybackSelection.Version)};
                properties["playbackMeaning"]=new JObject{["type"]="string",["minLength"]=1,["maxLength"]=160};
                ((JArray)row["required"]).Add("playbackContract");((JArray)row["required"]).Add("playbackMeaning");
                ((JArray)row["required"]).Remove("quote");
                // One quoted proposition. The compatibility quote is derived only
                // after parsing, never independently sampled a second time.
                row["properties"]=new JObject(new[]{"id","resultScope","playbackContract","playbackMeaning","selectedPlaybackId","lastRelevantQuote","comparison","coverage","reportAct","playedRevision","finalDisposition"}
                    .Where(k=>properties[k]!=null).Select(k=>new JProperty(k,properties[k].DeepClone())));
                foreach(string coverage in new[]{"reported","unreported"}) {
                    var branch=(JObject)row.DeepClone();var p=(JObject)branch["properties"];
                    p["coverage"]["enum"]=new JArray(coverage);
                    p["finalDisposition"]["enum"]=coverage=="reported"?new JArray("consistent"):new JArray("retracted","unresolved","unreported");
                    if(coverage=="reported")p["reportAct"]["enum"]=new JArray("outcome-report","unknown-answer");
                    if(coverage=="unreported"||!string.IsNullOrEmpty((string)result["completedPlaybackText"]))alternatives.Add(branch);
                }
            }
            reports["items"]=new JObject{["anyOf"]=alternatives};
        }
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
            var result=results.OfType<JObject>().First(r=>(string)r["id"]==(string)row["id"]);
            if(!ResultPlaybackSelection.Expand(row,false,result,out failure))return false;
            if(row["resultScope"]!=null&&(string)row["resultScope"]!=ResultScope(result)) {failure="delivery-result-scope-mismatch";return false;}
            string source=ActivityEvidencePolicy.PlaybackSourceRejection(result,new JObject{["revision"]=row["playedRevision"],
                ["lastRelevantQuote"]=row["lastRelevantQuote"],["finalDisposition"]=row["finalDisposition"]},(string)result["completedPlaybackText"]);
            if(source!=null){failure=source;return false;}
            if (string.IsNullOrWhiteSpace((string)row["comparison"]))
            { failure = "delivery-comparison-missing"; return false; }
            if (!new[] { "reported", "unreported" }.Contains((string)row["coverage"]))
            { failure = "delivery-classification-missing"; return false; }
            if ((string)row["coverage"]=="reported" && result["playbackRevision"]!=null) {
                string invalid=CoverageRejection(result,row.ToObject<Report>(),(string)result["completedPlaybackText"]);
                if(invalid!=null){failure=invalid;return false;}
            } else if((string)row["coverage"]=="unreported" && (!string.IsNullOrEmpty((string)row["quote"]) || (string)row["finalDisposition"]=="consistent")) {
                failure="delivery-coverage-disposition-conflict";return false;
            }
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
            (string)s["id"] == id && TaskAttemptSettlementPolicy.Terminal((string)s["status"])));
    }

    public static bool ValidExtension(JObject value)
    {
        var reports = value["delivery_reports"];
        if (reports != null && (!(reports is JArray rows) || rows.Count > 12 || rows.Any(r => !(r is JObject row) ||
            row.Count < 2 || row.Count > 12 || row.Properties().Any(p => !new[]{"id","quote","comparison","coverage","playedRevision","lastRelevantQuote","finalDisposition","reportAct","resultScope","playbackContract","playbackMeaning","selectedPlaybackId"}.Contains(p.Name)) ||
            row["selectedPlaybackId"]!=null && (row["selectedPlaybackId"].Type!=JTokenType.String || ((string)row["selectedPlaybackId"]).Length>200) ||
            row["playbackContract"]!=null && (row["playbackContract"].Type!=JTokenType.String || (string)row["playbackContract"]!=ResultPlaybackSelection.Version ||
                row["playbackMeaning"]?.Type!=JTokenType.String || string.IsNullOrWhiteSpace((string)row["playbackMeaning"])) ||
            row["playbackMeaning"]!=null && (row["playbackMeaning"].Type!=JTokenType.String || ((string)row["playbackMeaning"]).Length>160) ||
            row["reportAct"]!=null && !new[]{"outcome-report","unknown-answer","progress-report","promise","acknowledgment","other"}.Contains((string)row["reportAct"]) ||
            row["coverage"] != null && (row["coverage"].Type != JTokenType.String || !new[] { "reported", "unreported" }.Contains((string)row["coverage"])) ||
            row["comparison"] != null && (row["comparison"].Type != JTokenType.String || ((string)row["comparison"]).Length > 120) ||
            row["id"]?.Type != JTokenType.String || ((string)row["id"]).Length > 64 ||
            (row["quote"]?.Type != JTokenType.String && (string)row["playbackContract"]!=ResultPlaybackSelection.Version) || ((string)row["quote"]??"").Length > 600 ||
            row["playedRevision"]!=null && (row["playedRevision"].Type!=JTokenType.String || ((string)row["playedRevision"]).Length>16) ||
            row["lastRelevantQuote"]!=null && (row["lastRelevantQuote"].Type!=JTokenType.String || ((string)row["lastRelevantQuote"]).Length>600) ||
            row["finalDisposition"]!=null && !new[]{"consistent","retracted","unresolved","unreported"}.Contains((string)row["finalDisposition"])))) return false;
        return value["remaining_request_quote"] == null || value["remaining_request_quote"].Type == JTokenType.String && ((string)value["remaining_request_quote"]).Length <= 600;
    }

    public static bool CanClose(JArray results, JObject sequence, string remainingQuote) =>
        results != null && results.Count > 0 && results.OfType<JObject>().All(r => (bool?)r["delivered"] == true && (bool?)r["playbackReviewPending"]!=true) &&
        string.IsNullOrWhiteSpace(remainingQuote) &&
        (!(sequence?["steps"] is JArray steps) || TaskAttemptSettlementPolicy.AllAttemptsSettled(steps,results));

    public static void ExtendSchema(JObject schema)
    {
        schema["properties"]["delivery_reports"] = new JObject { ["type"] = "array", ["maxItems"] = 12,
            ["items"] = new JObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("id", "comparison", "reportAct", "coverage", "playedRevision", "lastRelevantQuote", "finalDisposition", "quote"),
                ["properties"] = new JObject { ["id"] = new JObject { ["type"] = "string", ["maxLength"] = 64 },
                    ["reportAct"] = new JObject { ["type"]="string",["enum"]=new JArray("outcome-report","unknown-answer","progress-report","promise","acknowledgment","other") },
                    ["comparison"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 120 },
                    ["coverage"] = new JObject { ["type"] = "string", ["enum"] = new JArray("unreported", "reported") },
                    ["playedRevision"] = new JObject { ["type"] = "string", ["maxLength"] = 16 },
                    ["lastRelevantQuote"] = new JObject { ["type"] = "string", ["maxLength"] = 600 },
                    ["finalDisposition"] = new JObject { ["type"] = "string", ["enum"] = new JArray("consistent","retracted","unresolved","unreported") },
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
