using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class TaskSemanticsRegression
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string Fixture = "Assets/Editor/Fixtures/TaskSemantics.latest-1918-20261014.json";
    public static JObject LastReport { get; private set; }
    private static object Call(object target, string method, params object[] args) => typeof(ChatQW).GetMethod(method, Flags).Invoke(target,args);
    public static int RunChecks()
    {
        int checks=0;
        void Check(bool ok,string message) { checks++; if(!ok)throw new InvalidOperationException("Task semantics: "+message); }
        JObject f;
        using(var reader=new JsonTextReader(new StringReader(File.ReadAllText(Fixture))) { DateParseHandling=DateParseHandling.None })f=JObject.Load(reader);
        Check(f["turns"].Count()==7,"Complete latest seven user turns retained.");
        Check(f["turns"][1]["public"].Any(p=>(string)p["played"]==""),"Silent failed drafts remain distinct from actual played history.");
        var input=(JObject)f["planningInput"].DeepClone();string original=input.ToString();
        var projection=RoomTaskContextPolicy.PlanningObservationView(input);
        Check(input.ToString()==original,"Projection never mutates retained full memory.");
        foreach(string path in new[]{"visibleObjectCatalog.current.candidates","visibleObjectCatalog.memory","visibleObjectCatalog.historicalViews"})
            Check(projection.SelectToken(path).Count()==input.SelectToken(path).Count(),"Every observed and remembered row survives: "+path);
        Check(projection.Descendants().OfType<JProperty>().Any(p=>p.Name=="currentPoseRef" && (string)p.Value=="planningCatalogPose"),"Real string timestamps activate sharing; a vacuous no-reference check cannot pass.");
        foreach(var reference in projection.Descendants().OfType<JProperty>().Where(p=>p.Name=="currentPoseRef" && (string)p.Value=="planningCatalogPose")) {
            var originalPose=(JObject)input.SelectToken(reference.Parent.Path);
            Check(((JObject)projection["planningCatalogPose"]).Properties().All(p=>JToken.DeepEquals(originalPose[p.Name],p.Value)),"Every shared pose reference resolves to exact original capture/time/angle metadata.");
        }
        input["unknownMandatoryConstraint"]="Preserve my backup; never substitute a different object.";
        Check(JToken.DeepEquals(RoomTaskContextPolicy.PlanningObservationView(input)["unknownMandatoryConstraint"],input["unknownMandatoryConstraint"]),"Unknown restrictions survive.");
        var failure=(JObject)f["failureInput"].DeepClone();var failureView=RoomTaskContextPolicy.ProjectPlanningFailureReview(failure);
        Check(RoomTaskContextPolicy.PlanningFailureReview(failure) && JToken.DeepEquals(failure["decisionFailure"],failureView["decisionFailure"]),"Original actual proposal rejection survives failure projection.");
        foreach(string kind in new[]{"mixed","answer-only"}) {
            var mixed=(JObject)failure.DeepClone();mixed["currentRequestSemantics"]["requestKind"]=kind;
            Check(!RoomTaskContextPolicy.PlanningFailureReview(mixed),"Independent questions keep their facts: "+kind);
        }
        var host=new GameObject("TaskSemanticsPure");host.SetActive(false);
        try {
            var model=host.AddComponent<ChatQW>();model.m_Backend=ChatQW.BackendType.Local;
            string wire=(string)Call(model,"BuildRoomTaskRequestJson",input.ToString(),false);
            var schema=JObject.Parse((string)Call(null,"BuildRoomDecisionSchemaForInput",input.ToString()));
            var anchors=((JObject)schema).Descendants().OfType<JProperty>().Where(p=>p.Name=="targetDescription"&&p.Value is JObject&&p.Value["type"]?.ToString()=="string").ToArray();
            Check(anchors.Length>0 && anchors.All(p=>p.Value["enum"]?.First?.ToString()==(string)input["currentUserText"]),"New proposal grammar binds CURRENT full request, not the old off imperative.");
            var args=new object[]{wire,false,null,null};bool admitted=(bool)Call(null,"TryBudgetRoomDecisionRequest",args);
            LastReport=new JObject { ["budget"]=(JObject)args[3] };
            Check(admitted,"Complete reconstructed planning packet fits unchanged final budget.");
            string imageWire=(string)Call(model,"BuildRoomTaskRequestJsonWithImage",input.ToString(),"data:image/png;base64,AA==",false);
            var beforeWire=JObject.Parse(imageWire);
            string beforeInput=(string)Call(model,"PrepareRoomPlanningWorkingMemory",input.ToString());
            foreach(JObject part in (JArray)beforeWire["messages"][1]["content"])
                if((string)part["type"]=="text")part["text"]=beforeInput;
            var beforeMeasure=(JObject)Call(null,"MeasureRoomDecisionBudget",beforeWire.ToString(Formatting.None),49152);
            // The old system text is actually measured in the log; don't add
            // its newly removed exact duplication back as missing evidence.
            int oldSystem=(int)f.SelectToken("recordedPlanningBudget.original.messageText[0].estimatedTextUnits");
            int representedBefore=(int)beforeMeasure["estimatedTextUnits"]-(int)beforeMeasure["messageText"][0]["estimatedTextUnits"]+oldSystem;
            Check(((string)beforeWire["messages"][2]["content"]).Contains((string)f["currentTurnReminder"]),"Recorded reminder saving matches the actual final current-turn producer.");
            representedBefore+=(int)(long)Call(null,"EstimateRoomDecisionTextUnits",(string)f["originalCurrentTurnReminder"])-(int)(long)Call(null,"EstimateRoomDecisionTextUnits",(string)f["currentTurnReminder"]);
            int reserve=Math.Max(0,(int)f.SelectToken("recordedPlanningBudget.original.estimatedTextUnits")-representedBefore);
            var stress=(JObject)input.DeepClone();stress["unloggedEnvelopeReserve"]=new string('填',reserve);
            string stressWire=(string)Call(model,"BuildRoomTaskRequestJsonWithImage",stress.ToString(),"data:image/png;base64,AA==",false);
            var stressArgs=new object[]{stressWire,false,null,null};bool stressAllowed=(bool)Call(null,"TryBudgetRoomDecisionRequest",stressArgs);
            LastReport["recordedFootprintStress"]=new JObject { ["unloggedEnvelopeReserveUnits"]=reserve,["beforeMeasure"]=beforeMeasure,["allowed"]=stressAllowed,["budget"]=(JObject)stressArgs[3],
                ["scope"]="Reconstructed complete packet plus opaque mandatory reserve to cover missing logged envelope footprint; not a verbatim replay of unlogged HTTP content." };
            Check(stressAllowed && (reserve==0||((string)stressArgs[2]).Contains(new string('填',reserve))),"Latest recorded full footprint fits final admission with missing evidence reserve intact: "+LastReport["recordedFootprintStress"]);
            var oversized=(JObject)input.DeepClone();oversized["unknownMandatoryConstraint"]=new string('约',62000);
            string big=(string)Call(model,"BuildRoomTaskRequestJson",oversized.ToString(),false);
            var bigArgs=new object[]{big,false,null,null};
            Check(!(bool)Call(null,"TryBudgetRoomDecisionRequest",bigArgs)&&big.Contains(new string('约',62000)),"Oversized protected content is preserved and rejected, never silently truncated.");
            string failureWire=(string)Call(model,"BuildRoomTaskRequestJsonWithImage",failure.ToString(),"data:image/png;base64,AA==",true);
            Check(!failureWire.Contains("data:image/")&&failureWire.Contains("current-planning-failure-receipt"),"Failure-only review carries current receipt, not a scene image.");
            foreach(JObject row in f["controls"]) {
                var body=RoomControlReviewProtocol.BodyInput((string)row["request"],"follow-owner",11,"follow-waiting",true,false);
                string prepared=(string)Call(model,"BuildRoomPreparationRequestJson",body.ToString());
                Check(prepared.Length<12000&&!prepared.Contains("visibleObjectCatalog"),"Closed recording preparation uses the bounded body review: "+row["name"]);
            }
            var extractionInput=new JObject { ["publicSpeech"]="我已经到你身边了，但我马上到你身边。",["movementReportRequirement"]=new JObject { ["required"]=true,["expectedReport"]="arrived" } };
            var repair=new JObject { ["failureDetail"]="ongoingActivityQuote:requires-distinct-affirmative-current-activity",["validationIssues"]=new JArray("ongoingActivityQuote:requires-distinct-affirmative-current-activity") };
            string repairWire=(string)Call(model,"BuildSpeechClaimRequest",extractionInput.ToString(),repair);
            var repairBudget=(JObject)Call(null,"MeasureRoomDecisionBudget",repairWire,4096);
            Check((bool)repairBudget["allowed"],"Words-only one-repair envelope still fits unchanged 4096 text limit.");
            LastReport["speechClaimRepairBudget"]=repairBudget;
            foreach(string text in new[]{"我正在尝试走近你。","I am trying to reach you.","まだあなたについて歩いているわ。"}) {
                var inconsistent=new JObject { ["completedMovementQuote"]="",["ongoingActivityQuote"]=text,["currentGeometryQuote"]="",
                    ["pendingSameResultQuote"]="",["targetQuote"]="",["resultReport"]="moving",["evidence"]=text,["actualLanguage"]="en",["otherClaims"]=new JArray(),
                    ["eventClaims"]=new JArray(new JObject { ["quote"]=text,["actor"]="CHARACTER",["operation"]="approach",["state"]="",["outcome"]="completed",["time"]="latest-event" }) };
                Check(!RoomSpeechClaimProtocol.TryReadDetailed(inconsistent.ToString(),text,out _,out var conflict)&&conflict=="eventClaims:current-activity-is-not-a-finished-event",
                    "The actual false completed-event extraction cannot contaminate factual review: "+text);
                inconsistent["eventClaims"]=new JArray();
                Check(RoomSpeechClaimProtocol.TryRead(inconsistent.ToString(),text,out _),"A current activity remains independently reviewable.");
                string both=text+" I saved the file.";
                inconsistent["eventClaims"]=new JArray(new JObject { ["quote"]="I saved the file.",["actor"]="CHARACTER",["operation"]="save",["state"]="",["outcome"]="completed",["time"]="historical-event" });
                Check(RoomSpeechClaimProtocol.TryRead(inconsistent.ToString(),both,out _),"A distinct real past event is retained alongside current movement.");
            }
            foreach(string text in new[]{"もう着いてるわよ。","已经到了。","I've arrived."}) {
                var claim=new JObject { ["completedMovementQuote"]=text,["pendingSameResultQuote"]="",["targetQuote"]="",["resultReport"]="omitted",["evidence"]="",["actualLanguage"]="ja",["otherClaims"]=new JArray() };
                Check(!RoomSpeechClaimProtocol.TryReadDetailed(claim.ToString(),text,out _,out var reason)&&reason=="completedMovementQuote:arrival-state-contradiction","An extracted completion cannot disappear as omitted: "+text);
                claim["resultReport"]="arrived";claim["evidence"]=text;
                Check(RoomSpeechClaimProtocol.TryRead(claim.ToString(),text,out _),"Grounded completion remains for independent fact review.");
            }
        } finally { UnityEngine.Object.DestroyImmediate(host); }
        LastReport["checks"]=checks;LastReport["passed"]=true;return checks;
    }

    public static IEnumerator RunNative(ChatQW model,JObject spec,string output)
    {
        var rows=new JArray();var report=new JObject { ["passed"]=false,["cases"]=rows };
        foreach(JObject row in spec["controls"]) {
            string request=(string)row["request"];
            var input=RoomControlReviewProtocol.BodyInput(request,"follow-owner",11,"follow-waiting",true,true);
            input["replacementTransaction"]=new JObject { ["source"]="independently-approved-current-task",["currentUserText"]=request,
                ["conflict"]="Earlier body classification and approved current task disagree. Re-read the complete human request independently." };
            input["recentDialogue"]=row["history"].DeepClone();
            bool done=false,ok=false;string text=null,status=null;float start=Time.realtimeSinceStartup;
            model.PostRoomPreparationMessage(input.ToString(),(a,b,c)=>{ok=a;text=b;status=c;done=true;});
            while(!done&&Time.realtimeSinceStartup-start<15)yield return null;
            if(!done)model.CancelRoomPreparationMessage();
            bool passed=ok&&RoomControlReviewProtocol.TryBodyDecision(text,request,true,out var decision,out _)&&decision.intent==(string)row["expectedIntent"];
            rows.Add(new JObject { ["name"]=row["name"],["passed"]=passed,["input"]=input,["output"]=text,["status"]=status });
            File.WriteAllText(Path.Combine(output,"task-semantics-native.json"),report.ToString());
        }
        foreach(JObject row in spec["failureReviews"]) {
            bool done=false,ok=false;string text=null,status=null;float start=Time.realtimeSinceStartup;
            model.PostRoomTaskMessage(row["input"].ToString(),true,(a,b,c)=>{ok=a;text=b;status=c;done=true;});
            while(!done&&Time.realtimeSinceStartup-start<30)yield return null;
            if(!done)model.CancelRoomTaskPlanningMessage();
            bool allowed=ok&&RoomTaskProtocol.ReviewAllows(text);
            bool passed=ok&&allowed==(bool)row["allowed"] && (row["expectedFailure"]?.Type!=JTokenType.String || (string)JObject.Parse(text)["planningFailureReport"]==(string)row["expectedFailure"]);
            rows.Add(new JObject { ["name"]=row["name"],["passed"]=passed,["output"]=text,["status"]=status });
            File.WriteAllText(Path.Combine(output,"task-semantics-native.json"),report.ToString());
        }
        report["passed"]=rows.All(r=>(bool)r["passed"]);File.WriteAllText(Path.Combine(output,"task-semantics-native.json"),report.ToString());
        if(!(bool)report["passed"])throw new InvalidOperationException("Task semantics native failed: "+string.Join(",",rows.Where(r=>!(bool)r["passed"]).Select(r=>(string)r["name"])));
    }
}
