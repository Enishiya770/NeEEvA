using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class IdentityAttentionRegression
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private const string Fixture = "Assets/Editor/Fixtures/IdentityAttention.latest-1738-20261014.json";
    private static int checks;
    public static JObject LastReport { get; private set; }
    private static void Check(bool ok, string message) { checks++; if (!ok) throw new InvalidOperationException(message); }
    private static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Flags).Invoke(target, args);
    public static int RunChecks()
    {
        checks = 0;
        var fixture = JObject.Parse(File.ReadAllText(Fixture));
        Check(((JArray)fixture["turns"]).Count == 7, "All seven complete real user turns retained.");
        var old = new JObject { ["sourceId"]="operated-goal:old", ["kind"]="operated-goal", ["entityId"]="entity-a",
            ["originalRequest"]="关掉左侧那台设备，再过来。", ["targetDescription"]="关掉左侧那台设备，再过来。", ["desiredState"]="off",
            ["customConstraint"]="Only this entity; not its neighbour." };
        string oldJson = old.ToString();
        var projected = RoomTaskContextPolicy.HistoricalReferent(old);
        Check(old.ToString()==oldJson && projected["desiredState"]==null && (string)projected["historicalRequestedState"]=="off" &&
            (string)projected["historicalTargetConstraint"]==(string)old["targetDescription"] &&
            (string)projected["customConstraint"]==(string)old["customConstraint"], "Old constraints remain exact but cannot be mistaken for current operation fields.");
        var steps = new JArray(new JObject { ["id"]="s1", ["quote"]="保存文档", ["kind"]="other", ["after"]=new JArray(), ["status"]="pending" },
            new JObject { ["id"]="s2", ["quote"]="过来", ["kind"]="approach", ["after"]=new JArray("s1"), ["status"]="pending" });
        Check(RoomTaskSequenceProtocol.TrySelect("{\"taskStep\":null}",new RoomTaskProtocol.Decision {intent="none"},steps,out var id,out _) && id==null &&
            (string)steps[0]["status"]=="pending", "Declining execution is a valid non-action; pending non-device task remains pending.");
        Check(!RoomTaskSequenceProtocol.TrySelect("{\"taskStep\":null}",new RoomTaskProtocol.Decision {intent="approach"},steps,out _,out _), "Null selection never authorizes an action.");
        Check(!RoomTaskSequenceProtocol.TrySelect("{\"taskStep\":\"s2\"}",new RoomTaskProtocol.Decision {intent="approach"},steps,out _,out _), "A failed/unfinished prerequisite still blocks return.");
        var host = new GameObject("IdentityAttentionPure");host.SetActive(false);
        var budgets = new JArray();
        try
        {
            var model=host.AddComponent<ChatQW>();model.m_Backend=ChatQW.BackendType.Local;
            var chat=host.AddComponent<ChatSample>();
            foreach(JObject row in fixture["searches"])
            {
                var input=(JObject)row["input"].DeepClone(); string before=input.ToString();
                input["unknownTaskConstraint"]="Never substitute another object; preserve all conditional user instructions.";
                var view=SceneSearchContextPolicy.Project(input,out var diagnostic);
                Check(JToken.DeepEquals(view["requestChoices"],input["requestChoices"]) &&
                    JToken.DeepEquals(view["task"],input["task"]) && view.SelectToken("visibleCatalog.memory").Count()==input.SelectToken("visibleCatalog.memory").Count(),
                    "Search preserves exact offers, actual task and every remembered entity.");
                Check((string)view["unknownTaskConstraint"]==(string)input["unknownTaskConstraint"],"Unknown constraint survives search projection.");
                foreach(var property in view.Descendants().OfType<JProperty>().Where(p=>p.Name=="currentPoseRef"))
                {
                    var original=input.SelectToken(property.Parent.Path) as JObject;
                    var shared=input[(string)property.Value] as JObject;
                    Check(original!=null && shared!=null && new[]{"captureId","capturedAtUtc","hasBodyPose","cameraYawRelativeToBodyDegrees","cameraPitchRelativeToBodyDegreesUp"}
                        .All(key=>JToken.DeepEquals(original[key],shared[key])),"Every shared pose preserves exact capture, time and measured relative direction.");
                }
                string wire=(string)Call(model,"BuildSceneSearchRequestJson",input.ToString(Formatting.None),"data:image/png;base64,AA==");
                var args=new object[]{wire,null}; bool allowed=(bool)Call(model,"AdmitSceneSearchWire",args);
                var beforeWire=JObject.Parse(wire);
                string beforeInput=SceneFactContextCompactor.CompactJson(input.ToString(Formatting.None),out _);
                foreach(JObject part in (JArray)beforeWire["messages"][1]["content"])
                    if((string)part["type"]=="text")part["text"]=beforeInput;
                var beforeArgs=new object[]{beforeWire.ToString(Formatting.None),null};Call(model,"AdmitSceneSearchWire",beforeArgs);
                var budgetRow=new JObject { ["line"]=row["line"], ["allowed"]=allowed, ["budget"]=(JObject)args[1], ["beforeBudget"]=(JObject)beforeArgs[1], ["projection"]=diagnostic };
                // The log omitted some envelope fields. Keep an opaque, mandatory
                // reserve for at least the missing recorded text footprint; do not
                // pretend that this reproduces the unlogged semantic content.
                int reserve=Math.Max(0,((int?)row.SelectToken("recordedBudget.estimatedTextUnits")??0)-(int)((JObject)beforeArgs[1])["estimatedTextUnits"]);
                var stress=(JObject)input.DeepClone();stress["unloggedEnvelopeReserve"]=new string('填',reserve);
                string stressWire=(string)Call(model,"BuildSceneSearchRequestJson",stress.ToString(Formatting.None),"data:image/png;base64,AA==");
                var stressArgs=new object[]{stressWire,null};bool stressAllowed=(bool)Call(model,"AdmitSceneSearchWire",stressArgs);
                budgetRow["unloggedEnvelopeReserveUnits"]=reserve;budgetRow["recordedFootprintStressBudget"]=(JObject)stressArgs[1];
                Check(stressAllowed && (reserve==0 || stressWire.Contains(new string('填',reserve))),"Unknown reserved envelope footprint must remain intact and fit unchanged final admission.");
                budgets.Add(budgetRow);
                Check(allowed,"Full reconstructed latest search must fit unchanged admission: "+args[1]);
                Check(before==row["input"].ToString(),"Projection never mutates retained input memory.");
            }
            var oversized=(JObject)fixture["searches"].Last["input"].DeepClone();
            oversized["unknownMandatoryRestriction"]=new string('约',50000);
            string oversizedWire=(string)Call(model,"BuildSceneSearchRequestJson",oversized.ToString(Formatting.None),"data:image/png;base64,AA==");
            var oversizedArgs=new object[]{oversizedWire,null};
            Check(!(bool)Call(model,"AdmitSceneSearchWire",oversizedArgs) && oversizedWire.Contains(new string('约',50000)),
                "Oversized mandatory evidence is preserved and still rejected by unchanged final admission.");
            var verdict=new JObject { ["verdict"]="consistent", ["resultReport"]="moving", ["movementDependencies"]=new JObject {
                ["source"]="independent-public-words", ["currentGeometryRequired"]=false,["currentGeometryQuote"]="",["ongoingActivityQuote"]="正在跟随你" } };
            string activity=Call(chat,"ResolveReviewedRoomSpeechDependencies","follow","user-target",verdict).ToString();
            verdict["reason"]="Actual ongoing activity";
            Check(RoomTaskProtocol.ReviewAllows(verdict.ToString()),"Independent activity metadata passes the final production verdict parser.");
            Check(activity.Contains("UserActivity")&&!activity.Contains("UserTarget"),"Pure ongoing activity retains execution checks without exact-coordinate invalidation.");
            verdict["movementDependencies"]["currentGeometryRequired"]=true;
            Check(Call(chat,"ResolveReviewedRoomSpeechDependencies","follow","user-target",verdict).ToString().Contains("UserTarget"),"Current proximity still requires current coordinates.");
            verdict.Remove("movementDependencies");
            Check(Call(chat,"ResolveReviewedRoomSpeechDependencies","follow","user-target",verdict).ToString().Contains("UserTarget"),"Missing independent dependency proof remains strict.");
            var preserved=RoomTaskContextPolicy.DeliveryReceipt(new JObject { ["lastAction"]=new JObject {
                ["actionId"]="save-1",["phase"]="failed",["reason"]="disk-full",["viewBefore"]=new JObject { ["x"]=1 },["customConstraint"]="Keep backup" } });
            Check(preserved["lastAction"]["viewBefore"]==null && (string)preserved["lastAction"]["reason"]=="disk-full" &&
                (string)preserved["lastAction"]["customConstraint"]=="Keep backup", "Delivery projection retains non-device failure, identity and unknown restrictions.");
            var extended = new JObject { ["lastAction"]=new JObject { ["viewBefore"]=new JObject { ["x"]=1,["restriction"]="Never turn toward the door" } } };
            Check(JToken.DeepEquals(extended,RoomTaskContextPolicy.DeliveryReceipt(extended)),"Unknown nested restrictions remain intact.");
            var focusReview=new JObject { ["currentTurnEvidence"]=new JObject { ["answerQuote"]=null,["actionQuote"]="Follow me, avoiding the doorway." },
                ["requestKind"]="action",["responseDemand"]="short-conversation",["candidateSufficient"]=false,["route"]="tools",["reason"]="Body movement with a scene restriction",
                ["questionUnderstanding"]=null,["planningFocus"]="current-user-body",["sceneConstraintQuote"]="avoiding the doorway" };
            Check(FirstReplyProtocol.TryGroundedReview(focusReview.ToString(),"Follow me, avoiding the doorway.",out _,out _,out _,out _,out _,out _) &&
                FirstReplyProtocol.ReviewedPlanningFocus(focusReview.ToString())=="general","Grounded scene restriction prevents a mistaken body-only projection.");
            focusReview["sceneConstraintQuote"]="invented restriction";
            Check(!FirstReplyProtocol.TryGroundedReview(focusReview.ToString(),"Follow me, avoiding the doorway.",out _,out _,out _,out _,out _,out _),"Attention proof must quote the actual current request.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
        LastReport=new JObject { ["passed"]=true,["checks"]=checks,["searchBudgets"]=budgets,
            ["scope"]="Recorded actual search rows and complete seven-turn dialogue; missing full HTTP envelopes reconstructed. Production serializers, unchanged budget and protocol guards; no inference or execution." };
        return checks;
    }

    public static IEnumerator RunNative(ChatQW model, JObject spec, string output)
    {
        var rows=new JArray(); var report=new JObject { ["passed"]=false,["cases"]=rows,
            ["scope"]="Actual local model through production serializers/planning/goal review. Complete recorded dialogue prefixes and actual receipts retained; HTTP envelopes reconstructed. No world actions or audio playback." };
        foreach(JObject item in spec["proposalReplays"] as JArray ?? new JArray())
        {
            var input=(JObject)item["input"].DeepClone(); string text=null,status=null;bool done=false,ok=false;float start=Time.realtimeSinceStartup;
            File.WriteAllText(Path.Combine(output,(string)item["name"]+"-plan-input.json"),input.ToString());
            model.PostRoomTaskMessage(input.ToString(),false,(a,b,c)=>{ok=a;text=b;status=c;done=true;});
            while(!done&&Time.realtimeSinceStartup-start<40)yield return null;
            if(!done)model.CancelRoomTaskPlanningMessage();
            RoomTaskProtocol.Decision decision=null;
            bool parsed=ok&&RoomTaskProtocol.TryDecision(RoomTaskSequenceProtocol.DecisionOnly(text),(string)input["currentUserText"],false,out decision,out _);
            bool expectsProposal=(bool)item["proposalExpected"];
            var result=new JObject { ["name"]=item["name"], ["plan"]=text,["planStatus"]=status,["parsed"]=parsed };
            bool passed=parsed;
            if(expectsProposal)
            {
                passed &= decision?.deviceGoal?.operation=="set" && decision.deviceGoal.desiredState==(string)item["desiredState"];
                var sequence=input["taskSequence"] as JObject;
                JArray steps=sequence?["steps"] as JArray;
                if(steps==null) {
                    bool fullPlan=RoomTaskSequenceProtocol.TryPlan(text,(string)input["currentUserText"],out steps,out _);
                    passed &= fullPlan;
                    if(fullPlan)foreach(JObject step in steps)step["status"]="pending";
                    sequence=new JObject { ["request"]=input["currentUserText"], ["steps"]=steps };
                }
                passed &= parsed && steps!=null && RoomTaskSequenceProtocol.TrySelect(text,decision,steps,out _,out _);
                if(passed)
                {
                    var review=new JObject { ["mode"]="new-proposal",["currentUserText"]=input["currentUserText"].DeepClone(),
                        ["proposal"]=JObject.FromObject(decision.deviceGoal),["taskSequence"]=sequence.DeepClone(),
                        ["taskStep"]=JObject.Parse(text)["taskStep"], ["recentDialogue"]=input["recentDialogue"].DeepClone(),
                        ["existingGoal"]=input["spatialGoal"]?.DeepClone(),["referentSources"]=input["referentSources"].DeepClone(),
                        ["verifiedDeviceHistory"]=input["verifiedDeviceHistory"]?.DeepClone() };
                    result["actualReviewInput"]=review.DeepClone();done=false;ok=false;text=null;start=Time.realtimeSinceStartup;
                    model.PostRoomDeviceGoalReviewMessage(review.ToString(),(a,b,c)=>{ok=a;text=b;status=c;done=true;});
                    while(!done&&Time.realtimeSinceStartup-start<30)yield return null;
                    if(!done)model.CancelRoomDeviceGoalReviewMessage();
                    RoomTaskProtocol.DeviceGoalReview meaning=null;
                    passed &= ok&&RoomTaskProtocol.TryDeviceGoalProposalReview(text,out meaning)&&meaning.IsApproved&&
                        meaning.referentSourceId==(string)item["expectedSource"]&&RoomTaskProtocol.TryValidateListedDeviceReferent(meaning,(JArray)review["referentSources"],out _);
                    result["review"]=text;result["reviewStatus"]=status;
                }
            }
            else
            {
                passed &= decision?.intent=="none";
                var forbiddenProposal=decision?.deviceGoal!=null ? JObject.FromObject(decision.deviceGoal) : item["priorActualRejectedProposal"] as JObject;
                result["plannerProposedForbiddenOperation"]=decision?.deviceGoal!=null;
                if(forbiddenProposal!=null)
                {
                    // Exercise the actual independent authority boundary with a
                    // real observed bad draft, even after the planner improves.
                    var review=new JObject { ["mode"]="new-proposal",["currentUserText"]=input["currentUserText"].DeepClone(),
                        ["proposal"]=forbiddenProposal.DeepClone(),["recentDialogue"]=input["recentDialogue"].DeepClone(),
                        ["referentSources"]=input["referentSources"].DeepClone(),["verifiedDeviceHistory"]=input["verifiedDeviceHistory"]?.DeepClone() };
                    result["actualNegativeReviewInput"]=review;result["priorDraftSource"]=item["priorDraftSource"]?.DeepClone();
                    done=false;ok=false;text=null;start=Time.realtimeSinceStartup;
                    model.PostRoomDeviceGoalReviewMessage(review.ToString(),(a,b,c)=>{ok=a;text=b;status=c;done=true;});
                    while(!done&&Time.realtimeSinceStartup-start<30)yield return null;
                    if(!done)model.CancelRoomDeviceGoalReviewMessage();
                    passed &= ok&&RoomTaskProtocol.TryDeviceGoalProposalReview(text,out var denial)&&denial.verdict=="rejected"&&denial.requestAssessment=="not-requested";
                    result["negativeReview"]=text;result["negativeReviewStatus"]=status;
                }
            }
            result["passed"]=passed;rows.Add(result);
            File.WriteAllText(Path.Combine(output,"identity-attention-native.json"),report.ToString());
        }
        foreach(JObject item in spec["focusReplays"] as JArray ?? new JArray())
        {
            var current=new LLM.SendData("user",(string)item["request"]);
            var history=((JArray)item["history"]).OfType<JObject>().Select(m=>new LLM.SendData((string)m["role"],(string)m["content"])).ToList();history.Add(current);
            string wire=(string)Call(model,"BuildFirstReplyReviewWire",current,history,"{}",new FirstReplyCompletion());
            File.WriteAllText(Path.Combine(output,(string)item["name"]+"-focus-request.json"),wire);
            string focus=null,kind=null;JObject proof=null;
            int generation=(int)typeof(ChatQW).GetField("m_StreamRequestGeneration",Flags).GetValue(model);
            Action<string,JObject,string,bool?,string,string,JObject,string> callback=(route,q,k,s,d,status,p,f)=>{focus=f;kind=k;proof=p;};
            yield return (IEnumerator)Call(model,"RequestFirstReplyReview",wire,generation,callback,0,false);
            bool passed=focus==(string)item["expectedFocus"] && (item["expectedKind"]==null || kind==(string)item["expectedKind"]);
            var row=new JObject { ["name"]=item["name"],["focus"]=focus,["requestKind"]=kind,["currentTurnEvidence"]=proof };
            if(passed&&focus=="current-user-body")
            {
                var input=(JObject)item["planningInput"].DeepClone();
                input["currentRequestSemantics"]=new JObject { ["source"]="independent-first-reply-current-request-review",
                    ["requestKind"]=kind,["actionAuthorization"]=false,["currentTurnEvidence"]=proof,
                    ["planningFocus"]=new JObject { ["kind"]=focus,["currentUserText"]=item["request"].DeepClone() } };
                string planWire=(string)Call(model,"BuildRoomTaskRequestJson",input.ToString(),false);
                File.WriteAllText(Path.Combine(output,(string)item["name"]+"-plan-request.json"),planWire);
                passed &= planWire.Contains("planningEvidenceScope") && !planWire.Contains("data:image/");
                string raw=null,status=null;bool done=false,ok=false;float start=Time.realtimeSinceStartup;
                model.PostRoomTaskMessage(input.ToString(),false,(a,b,c)=>{ok=a;raw=b;status=c;done=true;});
                while(!done&&Time.realtimeSinceStartup-start<40)yield return null;
                if(!done)model.CancelRoomTaskPlanningMessage();
                passed &= ok&&RoomTaskProtocol.TryDecision(RoomTaskSequenceProtocol.DecisionOnly(raw),(string)item["request"],false,out var decision,out _)&&decision.intent==(string)item["expectedIntent"];
                row["plan"]=raw;row["status"]=status;
            }
            row["passed"]=passed;rows.Add(row);File.WriteAllText(Path.Combine(output,"identity-attention-native.json"),report.ToString());
        }
        report["passed"]=rows.All(r=>(bool)r["passed"]);File.WriteAllText(Path.Combine(output,"identity-attention-native.json"),report.ToString());
        if(!(bool)report["passed"])throw new InvalidOperationException("Identity/attention native replay failed; see report.");
    }
}
