using System.Linq;
using Newtonsoft.Json.Linq;

// A failed prerequisite ends this attempt at its unstarted successors. The
// goal/graph remain resumable; this is neither execution nor successful work.
public static class TaskAttemptSettlementPolicy
{
    public static bool Terminal(string status) => status=="completed" || status=="failed" || status=="blocked";
    public static void Reconcile(JObject sequence)
    {
        if(!(sequence?["steps"] is JArray steps))return;
        for(int pass=0;pass<steps.Count;pass++) {
            bool changed=false;
            foreach(var step in steps.OfType<JObject>().Where(s=>(string)s["status"]=="pending")) {
                var blocked=new JArray((step["after"] as JArray??new JArray()).Values<string>().Where(id=>
                    steps.OfType<JObject>().Any(p=>(string)p["id"]==id && new[]{"failed","blocked"}.Contains((string)p["status"]))));
                if(blocked.Count==0)continue;
                step["status"]="blocked";
                step["receipt"]=new JObject { ["phase"]="not-started",["reason"]="prerequisite-attempt-failed",
                    ["blockedByStepIds"]=blocked,["operationAttempted"]=false,["goalRetained"]=true,
                    ["scope"]="This dependent step did not start because a prerequisite failed in this attempt. Not success, cancellation, or a failed execution of this step. Explain the blocked remainder; retain the goal for a separately reviewed continuation." };
                changed=true;
            }
            if(!changed)break;
        }
    }
    public static bool AllAttemptsSettled(JArray steps,JArray results) => steps.OfType<JObject>().All(s=>
        Terminal((string)s["status"]) && ((string)s["status"]!="blocked" ||
            results.OfType<JObject>().Any(r=>(string)r["source"]=="step:"+(string)s["id"] &&
                (string)r["status"]=="blocked" && (bool?)r["delivered"]==true)));
}
