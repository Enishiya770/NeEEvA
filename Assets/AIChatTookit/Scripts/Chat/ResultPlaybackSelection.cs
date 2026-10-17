using System;
using System.Linq;
using Newtonsoft.Json.Linq;

/// <summary>One model-selected quotation, with program-derived aliases and audio provenance.</summary>
public static class ResultPlaybackSelection
{
    public const string Version = "single-played-quote-v1";
    public const string Contract = "\n[单一已播证据] playbackUnits按实际播放顺序列出原句；selectedPlaybackId只选一次，程序生成两处引句。旧记录无音频单元才填lastRelevantQuote。"+
        "playbackMeaning先解释本操作完整已播含义，再选最后相关命题；不能从回执补写原话，不能把同句的确认词/另一操作当成本结果。收到指令、准备、进行、完成、未来意愿分别判断。"+
        "按实际status结算：failed须报告失败，blocked须报告前置失败导致未启动，不要求desiredState成功。只说前一步失败不能代替后一步的交付；引句必须涉及本行stepRequest。未做、失败、未知有别。"+
        "失败告知不要求技术细节，原因追问另算。failed包括准备期失败。blocked须明确表达本步骤未启动/未完成；没提本步骤或没声称成功，不等于报告了它。"+
        "后续提议/陪伴/重试愿望不撤回已报告结果；只有同操作/时间的否认或矛盾才重开。保留省略、感想及合理自主意愿，不能替代欠报或当成已执行。含义、引句、类型、结算须一致。";

    // The validator keeps the complete receipt. The model selects immutable
    // audio units; byte offsets/timing columns need not be repeated on its wire.
    public static void ProjectWireAudio(JObject result)
    {
        if(!(result?["completedAudio"] is JArray audio) || audio.Count==0)return;
        var units=Units(result);
        if(units.Count!=audio.Count || !JToken.DeepEquals(result["playbackUnits"],units))return;
        result.Remove("completedAudio");
    }

    // Only a versioned wire that intentionally omits the alias is expanded.
    // Legacy contradictory fields are never overwritten or silently repaired.
    public static bool Expand(JObject row, bool activity, JObject result, out string failure)
    {
        failure = null;
        if (row?["playbackContract"] == null) return true;
        if ((string)row["playbackContract"] != Version) { failure = "playback-contract-unknown"; return false; }
        var selection=activity?row["playbackProof"] as JObject:row;
        if(Units(result).Count>0 && selection?["selectedPlaybackId"]==null){failure="playback-audio-selection-required";return false;}
        if(selection?["selectedPlaybackId"]!=null) {
            string key=(string)selection["selectedPlaybackId"];
            var unit=Units(result).OfType<JObject>().FirstOrDefault(u=>(string)u["id"]==key);
            if(key!="" && unit==null){failure="playback-audio-unit-not-current";return false;}
            string actual=(string)unit?["text"]??"";
            if(selection["lastRelevantQuote"]!=null && (string)selection["lastRelevantQuote"]!=actual) {failure="playback-selection-alias-conflict";return false;}
            selection["lastRelevantQuote"]=actual;
        }
        string selected = (string)selection?["lastRelevantQuote"];
        if (selected == null) { failure = "playback-selection-missing"; return false; }
        bool covered = (string)row[activity ? "operation" : "coverage"] == (activity ? "covered" : "reported");
        string alias = covered ? selected : "";
        if (row["quote"] != null && (row["quote"].Type != JTokenType.String || (string)row["quote"] != alias))
        { failure = "playback-selection-alias-conflict"; return false; }
        row["quote"] = alias;
        return true;
    }

    public static void BindAudioQuotes(JObject proof,JObject result)
    {
        var units=Units(result);if(units.Count==0)return;
        var properties=(JObject)proof["properties"];
        properties.Property("lastRelevantQuote").Replace(new JProperty("selectedPlaybackId",new JObject{
            ["type"]="string",["enum"]=new JArray(new[]{""}.Concat(units.Select(u=>(string)u["id"])))}));
        ((JArray)proof["required"]).Remove("lastRelevantQuote");((JArray)proof["required"]).Add("selectedPlaybackId");
    }
    public static JArray Units(JObject result)
    {
        string text=(string)result?["completedPlaybackText"]??"";int boundary=(int?)result?["latestCorrectionStart"]??0;
        return new JArray((result?["completedAudio"] as JArray ?? new JArray()).OfType<JObject>()
            .Where(a=>(bool?)a["wholeTextPlayed"]==true && (int?)a["textStart"]>=0 && (int?)a["textLength"]>0 &&
                (int?)a["textLength"]<=600 && (long?)a["textStart"]+(long?)a["textLength"]<=text.Length &&
                (int?)a["textStart"]>=Math.Max(0,boundary) && !string.IsNullOrEmpty((string)a["audioId"]))
            .OrderBy(a=>(int)a["textStart"]).Select(a=>new JObject{
                ["id"]=AudioKey(a),
                ["text"]=text.Substring((int)a["textStart"],(int)a["textLength"])}));
    }
    public static void Annotate(JObject result){if(result!=null && Units(result).Count>0)result["playbackUnits"]=Units(result);}
    private static string AudioKey(JObject audio)=>(string)audio["audioId"]+"@"+(int?)audio["speechVersion"]+":"+(int?)audio["textStart"];

    public static JObject Evidence(JObject result, string quote, string selectedPlaybackId=null)
    {
        string text = (string)result?["completedPlaybackText"] ?? "";
        int start = string.IsNullOrEmpty(quote) ? -1 : text.LastIndexOf(quote, StringComparison.Ordinal);
        if(selectedPlaybackId!=null) {
            var selected=(result?["completedAudio"] as JArray??new JArray()).OfType<JObject>().FirstOrDefault(a=>AudioKey(a)==selectedPlaybackId);
            start=(int?)selected?["textStart"]??-1;
            if(start<0 || start+(quote?.Length??0)>text.Length || text.Substring(start,quote?.Length??0)!=quote)start=-1;
        }
        var audio = new JArray();
        if (start >= 0)
            foreach (var row in (result?["completedAudio"] as JArray ?? new JArray()).OfType<JObject>()) {
                int from = (int?)row["textStart"] ?? -1, length = (int?)row["textLength"] ?? 0;
                if ((bool?)row["wholeTextPlayed"] != true || from < 0 || from >= start + quote.Length || from + length <= start) continue;
                audio.Add(row.DeepClone());
            }
        return new JObject { ["resultId"] = result?["id"], ["playbackRevision"] = result?["playbackRevision"],
            ["selectedPlaybackId"]=selectedPlaybackId,
            ["quote"] = quote ?? "", ["textStart"] = start, ["textLength"] = quote?.Length ?? 0,
            ["completedAudio"] = audio, ["audioMetadataAvailable"] = audio.Count > 0,
            ["scope"] = "Derived from actual completed playback only; quotation relevance is still a semantic judgment. No execution authority." };
    }

    public static JObject Repair(JObject result, JObject rejected, string reason, bool activity)
    {
        string selected = (string)(activity ? rejected?.SelectToken("playbackProof.lastRelevantQuote") : rejected?["lastRelevantQuote"]);
        string selectedId=(string)(activity?rejected?.SelectToken("playbackProof.selectedPlaybackId"):rejected?["selectedPlaybackId"]);
        if(selected==null && selectedId!=null)selected=(string)Units(result).OfType<JObject>().FirstOrDefault(u=>(string)u["id"]==selectedId)?["text"];
        return new JObject { ["reason"] = reason, ["resultId"] = result?["id"],
            ["resultScope"] = WorkResultDeliveryProtocol.ResultScope(result), ["selectedQuote"] = selected ?? "",
            ["otherQuote"] = rejected?["quote"], ["reportAct"] = rejected?["reportAct"],
            ["comparison"] = rejected?["comparison"], ["playbackMeaning"] = rejected?["playbackMeaning"],
            ["declaredDisposition"]=activity?rejected?.SelectToken("playbackProof.finalDisposition"):rejected?["finalDisposition"],
            ["proposedOperation"]=rejected?[activity?"operation":"coverage"],
            ["fieldConflict"] = rejected?["quote"]!=null && selected != (string)rejected["quote"] ? "The two evidence quotations differ. Re-select the last relevant proposition; do not copy the earlier acknowledgment." :
                "Reconcile the selected words' actual meaning with reportAct and disposition; explanations and labels cannot contradict each other.",
            ["instruction"] = "只依据本结果真实已播顺序重新选择、解释并分类一次。确认词的含义不能借用另一句结果；解释已报告结果时不能仍标 acknowledgment。未知保持未知。不要改写执行事实，不重新执行，不凭被拒输出声明已交付。" };
    }
}
