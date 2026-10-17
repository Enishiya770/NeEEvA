using System;
using System.Linq;
using Newtonsoft.Json.Linq;

public static partial class WorkResultDeliveryProtocol
{
    // This packet deliberately excludes execution status and generated drafts.
    // The later comparison still receives the complete frozen result evidence.
    public static JArray MeaningInputs(JObject receipt)
    {
        var results=receipt?["results"] as JArray??new JArray();var ids=ReviewIds(results);var rows=new JArray();
        foreach(var result in results.OfType<JObject>().Where(r=>ids.Contains((string)r["id"])&&TerminalActionResult(r))) {
            string text=(string)result["completedPlaybackText"];
            if(string.IsNullOrEmpty(text))continue;
            var units=ResultPlaybackSelection.Units(result);
            if(units.Count==0) {
                int boundary=Math.Max(0,(int?)result["latestCorrectionStart"]??0);
                if(boundary>=text.Length)continue;
                units.Add(new JObject{["id"]="",["text"]=text.Substring(boundary)});
            }
            foreach(var unit in units.OfType<JObject>()) {
                var row=new JObject{["resultId"]=result["id"],["selectionId"]=unit["id"],
                    ["playbackRevision"]=result["playbackRevision"],
                    ["text"]=unit["text"],["operation"]=ResultAttribution(result),
                    ["context"]=result.SelectToken("taskContext.parentRequest")?.DeepClone()};
                row["id"]=ActivityEvidencePolicy.Stamp(new JObject{["workEpoch"]=receipt["workEpoch"],
                    ["userTurn"]=receipt["userTurn"],["audio"]=unit["id"],["version"]=result["playbackRevision"],
                    ["text"]=unit["text"],["context"]=row["context"]});rows.Add(row);
            }
        }
        return rows;
    }

    private static JArray BoundReadingBranches(JObject template,JObject result,JArray readings)
    {
        var selected=readings?.OfType<JObject>().Where(r=>(string)r["resultId"]==(string)result["id"]).ToArray();
        if(selected==null||selected.Length==0)return null;
        var branches=new JArray();
        foreach(var reading in selected.Concat(new[]{new JObject{["selectionId"]="",["text"]="",
            ["meaning"]="No selected statement about this result.",["stage"]="non-action"}})) {
            var row=(JObject)template.DeepClone();var p=(JObject)row["properties"];string stage=(string)reading["stage"];
            string act=stage=="terminal-outcome"?"outcome-report":stage=="unknown-outcome"?"unknown-answer":
                stage=="ongoing"||stage=="preparing"?"progress-report":stage=="prospective"?"promise":"other";
            string field=p["selectedPlaybackId"]!=null?"selectedPlaybackId":"lastRelevantQuote";
            p[field]["enum"]=new JArray(reading[field=="selectedPlaybackId"?"selectionId":"text"].DeepClone());
            p["playbackMeaning"]["enum"]=new JArray(reading["meaning"].DeepClone());
            if(stage!="mixed"){p["reportedStage"]["enum"]=new JArray(stage);p["reportAct"]["enum"]=new JArray(act);}
            if(stage!="mixed" && stage!="terminal-outcome" && !(stage=="unknown-outcome"&&!KnownSuccessfulAction(result))) {
                p["coverage"]["enum"]=new JArray("unreported");
                p["finalDisposition"]["enum"]=new JArray("unreported","retracted","unresolved");
            }
            branches.Add(row);
        }
        return branches;
    }

    public static bool ValidateReadings(JObject decision,JArray readings,out string failure)
    {
        failure=null;if(readings==null||readings.Count==0)return true;
        foreach(var row in (decision?["delivery_reports"] as JArray??new JArray()).OfType<JObject>()) {
            var matches=readings.OfType<JObject>().Where(r=>(string)r["resultId"]==(string)row["id"]).ToArray();
            if(matches.Length==0)continue;
            string quote=(string)row["lastRelevantQuote"];
            if(string.IsNullOrEmpty(quote)) {
                if((string)row["coverage"]=="unreported")continue;
            } else if(matches.Any(r=>(string)r["text"]==quote&&((string)r["stage"]=="mixed"||(string)r["stage"]==(string)row["reportedStage"])&&
                (string)r["meaning"]==(string)row["playbackMeaning"]&&
                (row["selectedPlaybackId"]==null||(string)r["selectionId"]==(string)row["selectedPlaybackId"])))continue;
            failure="delivery-literal-reading-mismatch";return false;
        }
        return true;
    }
}
