using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;

public static partial class RoomSpeechFactScopeRegression
{
    private static IEnumerator CheckPreparedStopDirectHandoff(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model)
    {
        bool controls=model.ControlReviewsEnabled;model.ControlReviewsEnabled=true;
        try {
            foreach(int path in new[]{0,1,2}) {
                bool stopSelected=path!=0, quick=path!=2;
                const string words="停止跟随，然后检查当前位置。";
                yield return PrepareRoomPlanning(chat,bridge,words);
                int generation=(int)Get(chat,"m_FormalResponseGeneration");
                Set(bridge,"followActive",true);Set(bridge,"actionId","prior-follow-for-inspection");
                Set(bridge,"operationRevision",bridge.Controller.OperationRevision);
                if(quick) {
                    Call(chat,"BeginSceneVisionStopPreparation",generation);
                    model.ControlReviews.Last().Complete(true,new JObject { ["explicitStopNow"]=true,["bodyDirective"]="other-task",
                        ["force"]="command-now",["reason"]="Explicit stop before an independent inspection." }.ToString(),"controlled-stop");
                }
                string stopId=bridge.ActionId; int operation=bridge.Controller.OperationRevision;
                int speech=model.Speech.Count,planning=model.Decisions.Count;
                var plan=new JArray(
                    new JObject { ["id"]="s1",["quote"]="停止跟随",["kind"]="stop-moving",["desiredState"]=null,["after"]=new JArray() },
                    new JObject { ["id"]="s2",["quote"]="检查当前位置",["kind"]="observation",["desiredState"]=null,["after"]=new JArray("s1") });
                JObject Decision(bool stop) => new JObject { ["intent"]=stop?"stop-moving":"inspect",["origin"]="user",
                    ["evidence"]=stop?"停止跟随":"检查当前位置",["taskStep"]=stop?"s1":"s2",["taskPlan"]=plan.DeepClone(),
                    ["taskCoverage"]=new JArray(plan.Select(s=>new JObject { ["quote"]=(string)s["id"]=="s2"?"然后检查当前位置":(string)s["quote"],
                        ["kind"]="action",["stepIds"]=new JArray(s["id"].DeepClone()) })) };
                model.Decisions.Last(r=>r.Kind=="room").Complete(true,Decision(stopSelected).ToString(),"controlled-whole-request");
                if(!quick) {stopId=bridge.ActionId;operation=bridge.Controller.OperationRevision;}
                if(stopSelected) {
                    yield return Wait(()=>model.Decisions.Count==planning+1,"Remaining step planned directly after completed stop");
                    Require(model.Speech.Count==speech,"A settled stop prerequisite does not start an extra writer/reviewer/work round.");
                    var continuation=Decision(false);continuation.Remove("taskPlan");continuation.Remove("taskCoverage");
                    model.Decisions.Last().Complete(true,continuation.ToString(),"controlled-next-inspection");
                }
                yield return Wait(()=>model.Speech.Count==speech+1,"The complete next action reaches its normal report");
                var sequence=(JObject)Call(chat,"ReadRoomTaskSequence");
                Require((string)Get(chat,"m_RoomTaskIntent")=="inspect" && (string)sequence.SelectToken("steps[0].status")=="completed" &&
                    (string)sequence.SelectToken("steps[0].actionId")==stopId && (string)sequence.SelectToken("steps[1].status")=="completed" &&
                    bridge.Controller.OperationRevision==operation,"The actual stop receipt unlocks a non-device inspection without replacing the full plan or repeating cancellation.");
                Call(chat,"InvalidateFormalResponse","prepared-stop-inspection-finished");
            }
        } finally {model.ControlReviewsEnabled=controls;}
    }
}
