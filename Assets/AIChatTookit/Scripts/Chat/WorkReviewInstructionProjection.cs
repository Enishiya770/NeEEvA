using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Only remove an exact producer-owned instruction, never quoted user evidence,
// the goal JSON, original audio chronology or unknown/custom instructions.
public static class WorkReviewInstructionProjection
{
    public const string OriginalReportScope = "Original reply's reporting criterion, not a fresh obligation. Compare expectedReport and owner-bound outcomes against " +
        "speech.lastPublicPlayback.completedPlaybackText and actual dialogue. A criterion already covered by completed playback is fulfilled; " +
        "other pending results do not make it unreported again. Continue only genuinely unfinished steps or distinct unreported results. " +
        "No action is authorized by this historical reporting contract.";
    public const string ReportScope = "Original reply criterion only: compare expectedReport/owned outcome with actual completed playback. Covered results need no repeat; separate unfinished steps/unreported results remain due. This grants no action.";
    public static void ProjectRecord(JObject record)
    {
        foreach(var property in record.Descendants().OfType<JProperty>().ToArray())
            if((property.Name=="resultReportRequirement" || property.Name=="movementReportRequirement") &&
                property.Value is JObject requirement && (string)requirement["scope"]==OriginalReportScope)
                requirement["scope"]=ReportScope;
    }
    public static string Project(string context)
    {
        if(context==null || !context.Contains("[Runtime work review domain: general]\n"))return context;
        string contract=ChatSample.SingingGoalContract;
        int start=context.IndexOf(contract,StringComparison.Ordinal);
        if(start<0)return context;
        int goal=context.IndexOf("\n[Sing/Goal] ",start+contract.Length,StringComparison.Ordinal);
        if(goal<0 || !string.IsNullOrWhiteSpace(context.Substring(start+contract.Length,goal-start-contract.Length)))return context;
        try {
            using(var reader=new JsonTextReader(new StringReader(context.Substring(goal+"\n[Sing/Goal] ".Length)))) {
                reader.DateParseHandling=DateParseHandling.None;
                var state=JObject.Load(reader);
                if(state["goal"]?.Type!=JTokenType.Null || state["active_goal"]?.Type!=JTokenType.Null ||
                    (bool?)state["active_goal_unfinished"]!=false)return context;
            }
        } catch(JsonException) {return context;}
        return context.Remove(start,contract.Length).Insert(start,
            "\n[歌唱目标状态] 当前无待审草案或未完歌唱目标；下列真实用户时序和已保留素材仍可用于理解请求。没有草案不证明用户要求已完成；新的自主提议仍可在后续当前决策中产生。\n");
    }
}
