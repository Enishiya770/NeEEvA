using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

// Full recorded history + actual outcomes, production serializers/coroutines,
// and separately labelled controlled transport / synthetic generalizations.
public static class TaskContinuityRegression
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static int checks;
    private static bool completed;
    private static readonly JArray rows=new JArray();
    private static void Check(bool ok,string text){checks++;if(!ok)throw new InvalidOperationException("Continuity: "+text);}
    private static object Call(object obj,string name,params object[] args)=>obj.GetType().GetMethod(name,Flags).Invoke(obj,args);
    private static object Get(object obj,string name)=>obj.GetType().GetField(name,Flags).GetValue(obj);
    private static void Set(object obj,string name,object value)=>obj.GetType().GetField(name,Flags).SetValue(obj,value);
    private static void Url(ChatQW model,string value){for(Type t=model.GetType();t!=null;t=t.BaseType){var f=t.GetField("url",Flags);if(f!=null){f.SetValue(model,value);break;}}}
    private static string Words(JObject row)=>((string)row["text"]).Split(new[]{" ["},StringSplitOptions.None)[0];
    private static IEnumerator Until(Func<bool> done,string reason,float seconds=12){float start=Time.realtimeSinceStartup;while(!done()){if(Time.realtimeSinceStartup-start>=seconds)throw new TimeoutException(reason);yield return null;}}
    private static void Save(string output,JObject fixture)=>File.WriteAllText(Path.Combine(output,"continuity.json"),new JObject{
        ["passed"]=completed&&rows.All(r=>(bool?)r["passed"]!=false),["completed"]=completed,["checks"]=checks,["sourceHash"]=fixture["sha256"],
        ["recordedEvents"]=((JArray)fixture["events"]).Count,["dialogue"]=fixture["dialogue"].DeepClone(),["cases"]=rows}.ToString());
    private static JObject SpeechFrame()=>new JObject{["evidenceVersion"]=2,["monitorRequestId"]=Guid.NewGuid().ToString("N"),
        ["owner"]=new JObject{["generation"]=1,["workEpoch"]=1},["purpose"]="speech",
        ["speech"]=new JArray(new ActivityMonitorProtocol.SpeechUnit{Id="phrase",Text="I like this room.",Language="en"}.Snapshot()),
        ["actions"]=new JArray(),["facts"]=new JObject{["request"]=new JObject{["text"]="Look around."}},
        ["evidenceScopes"]=new JObject{["request"]=ActivityMonitorContextPolicy.Scope("request","mistake")}};

    private static void Checks(ChatSample chat,ChatQW model,JObject fixture,string output)
    {
        var events=((JArray)fixture["events"]).OfType<JObject>().ToArray();
        Check(events.Length==3752,"All captured session events are retained.");
        Check(((JArray)fixture["dialogue"]).Count(r=>(string)r["role"]=="user")==14,"All 14 actual user turns retained.");
        Check(((JArray)fixture["dialogue"]).Count(r=>(bool?)r["actuallyPlayed"]==true)==67,"All 67 completed audio chunks retain playback order.");
        var rejected=JObject.Parse((string)events.First(e=>(string)e["kind"]=="Room/DecisionRepair"&&(int?)e["data"]?["generation"]==8)["data"]["detail"]["rejectedDecision"]);
        string request=Words(((JArray)fixture["dialogue"]).OfType<JObject>().First(r=>(int)r["line"]==99826));
        Call(Get(chat,"m_ActionConstraints"),"RecordUserTurn",request);
        Set(chat,"m_RoomTaskSequence",null);
        Check(RoomTaskProtocol.TryDecision(RoomTaskSequenceProtocol.DecisionOnly(rejected.ToString()),request,false,out var decision,out _),"Recorded gen8 draft reaches the real sequence guard.");
        object[] args={rejected.ToString(),decision,null};
        Check(!(bool)Call(chat,"ValidateRoomTaskSequence",args)&&(string)args[2]=="invalid-task-sequence-action-kind","Original mismatched prerequisite remains rejected.");
        Check(Get(chat,"m_RoomTaskSequence")==null,"Rejected draft cannot commit its parent plan.");
        var feedback=RoomTaskSequenceProtocol.SelectionFeedback(rejected.ToString(),null);
        Check(feedback!=null&&feedback.ToString().Contains("device"),"Finite repair names the conflicting selection/operation.");
        var schemaRepair=new JObject{["decisionRepair"]=new JObject{["attempt"]=1,["maximumAttempts"]=1,
            ["rejectionReason"]="invalid-task-sequence-action-kind",["sequenceSelectionFeedback"]=feedback.DeepClone()}};
        foreach(string kind in new[]{"observation","approach","follow","stop-moving","other"}) {
            schemaRepair["decisionRepair"]["sequenceSelectionFeedback"]["selectedStep"]["kind"]=kind;
            var narrowed=JObject.Parse(RoomTaskSequenceProtocol.RepairExecutionSchema(RoomTaskProtocol.DecisionSchema,schemaRepair));
            Check(!narrowed.Descendants().OfType<JProperty>().Any(p=>p.Name=="deviceGoal")&&
                ((JArray)narrowed["oneOf"]).OfType<JObject>().Any(b=>((JArray)b["properties"]["intent"]["enum"]).Values<string>().Contains("none")),
                "Finite structural repair defers extra operations while preserving abstention for "+kind);
        }
        schemaRepair["decisionRepair"]["sequenceSelectionFeedback"]["selectedStep"]["kind"]="device-state";
        Check(RoomTaskSequenceProtocol.RepairExecutionSchema(RoomTaskProtocol.DecisionSchema,schemaRepair)==RoomTaskProtocol.DecisionSchema&&
            RoomTaskSequenceProtocol.RepairExecutionSchema(RoomTaskProtocol.DecisionSchema,new JObject())==RoomTaskProtocol.DecisionSchema,
            "Initial planning and correctly selected device goals retain their complete action menu.");
        var repairSource=new JObject{["currentUserText"]=request,["decisionRepair"]=new JObject{
            ["rejectedDecision"]=rejected.ToString(Formatting.None),["sequenceSelectionFeedback"]=feedback.DeepClone(),
            ["rejectionReason"]="invalid-task-sequence-action-kind",["maxAttempts"]=1}};
        var repairView=JObject.Parse((string)Call(model,"PrepareRoomPlanningWorkingMemory",repairSource.ToString(Formatting.None)));
        Check(repairView["decisionRepair"]["rejectedDecision"]==null&&repairSource["decisionRepair"]["rejectedDecision"]!=null&&
            JToken.DeepEquals(feedback,repairView["decisionRepair"]["sequenceSelectionFeedback"])&&(int)repairView["decisionRepair"]["maxAttempts"]==1,
            "Planner repair retains the mismatch and finite bound, while the original diagnostic draft is not replayed as a continuation template.");
        var repaired=(JObject)rejected.DeepClone();repaired.Remove("deviceGoal");
        Check(RoomTaskProtocol.TryDecision(RoomTaskSequenceProtocol.DecisionOnly(repaired.ToString()),request,false,out decision,out _),"Observation-only repair parses.");
        args=new object[]{repaired.ToString(),decision,null};
        Check((bool)Call(chat,"ValidateRoomTaskSequence",args),"Current prerequisite can commit with all later operations retained.");
        var plan=(JObject)Get(chat,"m_RoomTaskSequence");
        Check(((JArray)plan["steps"]).Count==3&&(string)plan["steps"][1]["desiredState"]=="off"&&RoomTaskSequenceProtocol.Ready((JArray)plan["steps"]).Count==1,"Future operation remains pending behind prerequisite.");
        Set(chat,"m_WorkReviewOpen",false);
        Set(chat,"m_WorkRequestSemantics",new JObject{["currentUserText"]=request,["requiresActionOrControl"]=true,["userTurn"]=4});
        Call(chat,"RememberUnfinishedWorkBeforeReset",true);
        Check((string)((JObject)Get(chat,"m_PreviousUnfinishedWork"))["request"]==request,"Closed scheduler still preserves parent purpose and actual outcomes.");
        Set(chat,"m_WorkRequestSemantics",new JObject{["currentUserText"]="那该怎么办呢？",["requiresActionOrControl"]=false});
        Call(chat,"RememberUnfinishedWorkBeforeReset",true);
        Check((string)((JObject)Get(chat,"m_PreviousUnfinishedWork"))["request"]==request,"Read-only correction does not erase the unfinished parent context.");
        Call(chat,"RememberUnfinishedWorkBeforeReset",false);
        Check(Get(chat,"m_PreviousUnfinishedWork")==null,"True nonopening reset clears historical handoff.");
        Set(chat,"m_WorkRequestSemantics",null);Set(chat,"m_RoomTaskSequence",null);
        rows.Add(new JObject{["name"]="actual-gen8-atomic-repair-and-parent-history",["passed"]=true,["sourceLine"]=100271,["rejected"]=rejected,["validRepair"]=repaired,["feedback"]=feedback});

        var units=(List<ActivityMonitorProtocol.SpeechUnit>)Get(chat,"m_ActivitySpeech");units.Clear();
        var frozen=new JObject{["body"]=new JObject{["phase"]="moving"},["formalObservation"]=new JObject{["captureId"]="frame"}};
        var first=new ActivityMonitorProtocol.SpeechUnit{Id="prefix",Text="I have already ",Language="en",State="completed",ObservedAtSpeechStart=(JObject)frozen.DeepClone()};
        var second=new ActivityMonitorProtocol.SpeechUnit{Id="tail",Text="arrived.",Language="en",State="playing",ObservedAtSpeechStart=(JObject)frozen.DeepClone()};
        units.Add(first);units.Add(second);
        var window=(JObject)Call(chat,"ActivitySpeechWindow",second);
        Check((string)window["text"]=="I have already arrived."&&first.Text=="I have already "&&second.Text=="arrived.","Cross-chunk semantic clause preserves actual audio units.");
        first.Text="I have already";
        Check((string)((JObject)Call(chat,"ActivitySpeechWindow",second))["text"]=="I have already arrived.","Trimmed English audio boundaries cannot merge adjacent words.");
        second.ObservedAtSpeechStart["body"]["phase"]="arrived";
        Check((string)((JObject)Call(chat,"ActivitySpeechWindow",second))["text"]=="arrived.","Cannot retroactively apply a later completion to an earlier prefix.");
        second.ObservedAtSpeechStart=(JObject)frozen.DeepClone();first.Text="Hello. ";
        Check((string)((JObject)Call(chat,"ActivitySpeechWindow",second))["text"]==second.Text,"Complete sentences stay separate.");
        first.Text="I have";second.Text="already";second.State="completed";
        var third=new ActivityMonitorProtocol.SpeechUnit{Id="third",Text="arrived.",Language="en",State="playing",ObservedAtSpeechStart=(JObject)frozen.DeepClone()};units.Add(third);
        Check((string)((JObject)Call(chat,"ActivitySpeechWindow",third))["text"]=="I have already arrived.","One unfinished proposition can span more than two audio chunks without losing its subject.");
        third.IsCorrection=true;
        Check((string)((JObject)Call(chat,"ActivitySpeechWindow",third))["text"]==third.Text,"An appended correction does not merge back into the erroneous clause.");units.Clear();
        var concurrent=SpeechFrame();concurrent["delivery"]=new JObject{["id"]="completed-save",["language"]="en",["facts"]=new JObject{["status"]="completed"}};
        concurrent["actions"]=new JArray(new JObject{["id"]="next-step",["version"]="1",["kind"]="read",["cancellable"]=true});
        var lanes=ActivityEvidencePolicy.AttentionFrames(concurrent).ToArray();
        Check(lanes.Any(f=>(string)f["purpose"]=="speech")&&lanes.Any(f=>(string)f["purpose"]=="delivery")&&lanes.Any(f=>(string)f["purpose"]=="action"),"Completed-step delivery cannot hide another spoken claim or live action.");
        rows.Add(new JObject{["name"]="chunk-boundary-and-concurrent-delivery",["passed"]=true,["lanes"]=new JArray(lanes)});
        var retry=SpeechFrame();retry["previousAttempt"]=new JObject{["rejection"]="speech-qualification-not-quoted",
            ["rejectedVerdict"]="old ungrounded full draft",["scope"]="Use a literal uncertainty expression if actually present; otherwise reclassify."};
        var retryView=ActivityMonitorContextPolicy.Project(retry,0);
        Check(retryView["previousAttempt"]["rejectedVerdict"]==null&&retryView["previousAttempt"]["scope"]!=null&&retry["previousAttempt"]["rejectedVerdict"]!=null,
            "Bounded retries retain specific correction feedback without replaying an invalid draft or erasing its diagnostic record.");

        int admitted=0;
        foreach(var e in events.Where(e=>(string)e["kind"]=="Activity/MonitorFrame")) {
            var full=(JObject)e["data"].DeepClone();full["evidenceVersion"]=2;
            foreach(var frame in ActivityEvidencePolicy.AttentionFrames(full)) {
                string projected=model.PrepareActivityMonitorFrame(frame.ToString(Formatting.None),out string diagnostic);
                Check(projected!=null,"Complete recorded monitor request fits unchanged purpose budget at line "+e["line"]);admitted++;
            }
        }
        rows.Add(new JObject{["name"]="all-recorded-monitor-purposes-full-wire",["passed"]=true,["requests"]=admitted});

        // Production work packet with the entire actual dialogue and runtime
        // event facts; it is a reconstruction, not a claimed original wire dump.
        var fact=events.First(e=>(string)e["kind"]=="Activity/MonitorFrame"&&(int?)e["data"]?["owner"]?["generation"]==33)["data"]["facts"].DeepClone();
        fact["fullRecordedDialogue"]=fixture["dialogue"].DeepClone();fact["unknownConstraint"]=new JObject{["doNot"]="discard original or overwrite a separate result"};
        string marker=CommittedSpatialReplyProtocol.ProducerContextMarker+"\n[Task progress runtime]";
        string prompt=marker+new JObject{["first"]=fact.DeepClone(),["second"]=fact.DeepClone(),["third"]=fact.DeepClone()}.ToString(Formatting.None);
        object[] projection={prompt,true,null};string compact=(string)Call(model,"ProjectPrivateAttention",projection);
        var budget=(JObject)projection[2];
        Check((bool)budget["privateReferencesRoundTripVerified"]&&(int)budget["sharedSubtrees"]>=2,"Private fact references round-trip all actual outcomes and unknown restrictions.");
        Check(compact.Length<prompt.Length&&compact.Contains("discard original")&&prompt.Contains("second"),"Projection reduces duplicate producers without mutating source memory.");
        rows.Add(new JObject{["name"]="reconstructed-full-history-private-projection",["passed"]=true,["diagnostics"]=budget});

        fact["fullRecordedDialogue"]=new JArray(((JArray)fixture["dialogue"]).OfType<JObject>().Select(row=>new JObject{
            ["role"]=row["role"],["text"]=(string)row["role"]=="user"?Words(row):(string)row["text"],
            ["actuallyPlayed"]=row["actuallyPlayed"], ["sourceLine"]=row["line"]}));
        string autonomyPrefix="[内部自主意图预判；不要把本条写成正式回复，不要调用工具，不要输出控制标签]\n";
        prompt=autonomyPrefix+"[Autonomy runtime facts]\n"+new JObject{["first"]=fact.DeepClone(),["second"]=fact.DeepClone(),["third"]=fact.DeepClone()}.ToString(Formatting.None);
        foreach(var row in ((JArray)fixture["dialogue"]).OfType<JObject>())model.m_DataList.Add(new LLM.SendData((string)row["role"],(string)row["role"]=="user"?Words(row):(string)row["text"]));
        string wire=(string)Call(model,"BuildAutonomyIntentRequestJsonWithImage",prompt,null);
        object[] admission={wire,null};bool allowed=(bool)Call(model,"AdmitAutonomyIntentWire",admission);
        File.WriteAllText(Path.Combine(output,"full-history-autonomy.request.json"),wire);
        Check(allowed,"Complete reconstructed autonomy wire fits unchanged budget with all accepted turns and real outcomes.");
        rows.Add(new JObject{["name"]="full-history-autonomy-final-wire",["passed"]=true,["budget"]=(JObject)admission[1],["scope"]="Production serializer and admission; reconstructed producer packet, complete accepted ASR text and actual audio history. Original acoustic auxiliary records retained in source fixture."});
        model.m_DataList.Clear();
        prompt=autonomyPrefix+"[Autonomy runtime facts]\n"+new JObject{["customRestriction"]=new string('界',26000)}.ToString(Formatting.None);
        wire=(string)Call(model,"BuildAutonomyIntentRequestJsonWithImage",prompt,null);admission=new object[]{wire,null};
        Check(!(bool)Call(model,"AdmitAutonomyIntentWire",admission)&&wire.Contains(new string('界',26000)),"Mandatory unknown oversized information remains intact and fails locally, never bypasses the cap.");
        rows.Add(new JObject{["name"]="mandatory-budget-rejection-preserved",["passed"]=true,["budget"]=(JObject)admission[1]});
    }

    private static async Task ServeOnce(TcpListener listener,Task gate,string content)
    {
        using(var client=await listener.AcceptTcpClientAsync())using(var stream=client.GetStream()) {
            var header=new StringBuilder();int b;while((b=stream.ReadByte())>=0){header.Append((char)b);if(header.ToString().EndsWith("\r\n\r\n"))break;}
            int count=0;foreach(string line in header.ToString().Split('\n'))if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase))int.TryParse(line.Substring(15).Trim(),out count);
            for(int i=0;i<count;i++)if(stream.ReadByte()<0)break;
            await gate;
            byte[] body=Encoding.UTF8.GetBytes(new JObject{["choices"]=new JArray(new JObject{["message"]=new JObject{["role"]="assistant",["content"]=content},["finish_reason"]="stop"})}.ToString(Formatting.None));
            byte[] head=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head,0,head.Length);await stream.WriteAsync(body,0,body.Length);
        }
    }
    private static IEnumerator Transport(ChatQW model)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        Url(model,"http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/v1/chat/completions");
        var release=new TaskCompletionSource<bool>();
        var server=ServeOnce(listener,release.Task,"{}");
        int callbacks=0;string status=null;bool result=false;
        try {
            model.PostActivityMonitor(SpeechFrame().ToString(),(ok,text,s)=>{callbacks++;status=s;result=ok;});
            yield return null;
            int generation=(int)Get(model,"m_ActivityMonitorGeneration");
            var request=Get(model,"m_ActivityMonitorRequest");
            Check(request!=null,"Real Unity monitor HTTP is in flight.");
            // A separate endpoint immediately rejects the speech HTTP; we only
            // care that starting its actual coroutine does not cancel monitoring.
            Url(model,"http://127.0.0.1:1/v1/chat/completions");
            model.PostSpeechContinuationStream("Continue the accepted task briefly.",_=>{},_=>{});
            Check(generation==(int)Get(model,"m_ActivityMonitorGeneration")&&ReferenceEquals(request,Get(model,"m_ActivityMonitorRequest"))&&callbacks==0,"Formal continuation leaves the concurrent monitor owner intact.");
            release.SetResult(true);
            yield return Until(()=>callbacks>0,"Monitor completes after concurrent speech handoff.");
            Check(callbacks==1&&result,"Monitor completion delivered once, no fake timeout.");
            model.CancelActiveResponse();Check(callbacks==1,"Settled owner cannot emit another callback.");
            Url(model,"http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/v1/chat/completions");
            var hold=new TaskCompletionSource<bool>();var cancelledServer=ServeOnce(listener,hold.Task,"{}");
            callbacks=0;model.PostActivityMonitor(SpeechFrame().ToString(),(ok,text,s)=>{callbacks++;status=s;});
            yield return null;model.CancelActiveResponse();
            Check(callbacks==1&&status=="activity-monitor-cancelled"&&Get(model,"m_ActivityMonitorRequest")==null,"True cancellation notifies upper pending state immediately.");
            hold.SetResult(true);
            yield return null;yield return null;
            Check(callbacks==1,"Aborted request cannot later settle a new owner.");
            rows.Add(new JObject{["name"]="real-http-monitor-continue-and-cancel",["passed"]=true,["scope"]="Production ChatQW + UnityWebRequest, controlled local HTTP response, no semantic model substitution claim."});
        }finally{release.TrySetResult(true);model.CancelActiveResponse();listener.Stop();}
        // The intentionally aborted peer can observe a socket close; not model failure.
        Check(server.IsCompleted&&!server.IsFaulted,"Successful HTTP fixture completed.");
    }

    private static IEnumerator Native(ChatSample chat,ChatQW model,JObject fixture,string output)
    {
        Url(model,model.m_LocalUrl);
        var all=((JArray)fixture["dialogue"]).OfType<JObject>().ToArray();
        var cases=new[]{
            (name:"actual-observation-gap-followup",line:121260,user:"那该怎么办呢？",expected:"tools",focus:"general",context:"Earlier parent request: find the cabinet lamp, turn it off, then return. Original planning rejected; no device operation occurred. One body turn and partial observation occurred. The just-spoken answer admits the left half was not inspected. User is continuing the unresolved task, not cancelling it."),
            (name:"explicit-cancel-explain-only-zh",line:135636,user:"先取消刚才的任务，只解释为什么没看全，不要再转身。",expected:"tools",focus:"general",context:"Parent task unfinished. This new utterance cancels its execution and requests an explanation; cancellation must not restart a search."),
            (name:"explicit-body-angle-en",line:132155,user:"Turn your whole body 30 degrees left, not just your head.",expected:"tools",focus:"current-view",context:"Current body idle. This is a new explicit rotation, with no object or observation task."),
            (name:"inspect-edit-continuity-en",line:0,user:"Then what should we do?",expected:"tools",focus:"general",context:"Accepted request: inspect the whole report, correct errors, save a separate copy, retain original. Inspection only read its first page. Assistant just admitted the remaining pages are unchecked. Edit/save have NOT started. User is continuing this unresolved task."),
            (name:"explain-only-ja",line:0,user:"作業は再開しないで。残りを確認できていない理由だけ説明して。",expected:"evidence",focus:"general",context:"The user previously requested inspecting data and generating a chart. Only part was inspected; generation was not started. Current work is already stopped. Explain only; do not resume."),
        };
        foreach(var item in cases) {
            var history=all.Where(r=>item.line>0&&(int)r["line"]<item.line).Select(r=>new LLM.SendData((string)r["role"],(string)r["role"]=="user"?Words(r):(string)r["text"])).ToList();
            if(item.line==0){history.Add(new LLM.SendData("user",item.context));history.Add(new LLM.SendData("assistant","I only checked part of it; the remaining part has not been inspected."));}
            string wire=FirstReplyTransportRegression.CaptureReviewWire(model,item.user,FirstReplyProtocol.OrdinaryHead+"\n<lang code=\"en\"/><speech mode=\"independent\"/><say>I have checked everything.</say>",new JObject{["actualOutcomeContext"]=item.context}.ToString(),history);
            File.WriteAllText(Path.Combine(output,item.name+".request.json"),wire);
            string route=null,kind=null,focus=null,status=null;JObject proof=null,question=null;bool? sufficient=null;
            Action<string,JObject,string,bool?,string,string,JObject,string> callback=(r,q,k,s,d,t,p,f)=>{route=r;kind=k;focus=f;status=t;proof=p;question=q;sufficient=s;};
            yield return (IEnumerator)Call(model,"RequestFirstReplyReview",wire,(int)Get(model,"m_StreamRequestGeneration"),callback,0,false);
            bool nextStepQuestion=item.name=="actual-observation-gap-followup"||item.name=="inspect-edit-continuity-en";
            bool passed=(route==item.expected||nextStepQuestion&&route=="evidence"&&question!=null&&sufficient==false)&&focus==item.focus;
            rows.Add(new JObject{["name"]=item.name,["passed"]=passed,["route"]=route,["kind"]=kind,["focus"]=focus,["status"]=status,["proof"]=proof,["questionUnderstanding"]=question,["candidateSufficient"]=sufficient,["priorMessages"]=history.Count,["actualOrVariant"]=item.name.StartsWith("actual-")?"Recorded turn and complete earlier playback/history":"Explicitly labelled generalization"});
            Save(output,fixture);
        }
        var recordedRepair=((JArray)fixture["events"]).OfType<JObject>().First(e=>(string)e["kind"]=="Room/DecisionRepair"&&(int?)e["data"]?["generation"]==8);
        string originalRequest=Words(all.First(r=>(int)r["line"]==99826));
        string rejectedDecision=(string)recordedRepair["data"]["detail"]["rejectedDecision"];
        var repairFeedback=(JObject)recordedRepair["data"]["detail"].DeepClone();
        repairFeedback["violatedConstraint"]=RoomTaskProtocol.DecisionRepairConstraint((string)repairFeedback["rejectionReason"]);
        repairFeedback["sequenceSelectionFeedback"]=RoomTaskSequenceProtocol.SelectionFeedback(rejectedDecision,null);
        repairFeedback["sequenceStructureFeedback"]=RoomTaskSequenceProtocol.StructureFeedback(rejectedDecision);
        var repairInput=new JObject{["planningContextVersion"]="working-memory-v1",["currentUserText"]=originalRequest,["latestUserText"]=originalRequest,
            ["autonomous"]=false,["taskSequenceRequired"]=true,["taskSequence"]=null,["ongoingActivityMonitoring"]=true,
            ["recentDialogue"]=new JArray(all.Where(r=>(int)r["line"]<99826).Select(r=>new JObject{["role"]=r["role"],["text"]=(string)r["role"]=="user"?Words(r):(string)r["text"]})),
            ["decisionRepair"]=repairFeedback,["currentFacts"]=new JObject{["connected"]=true,["activeActionId"]=null},
            ["visualObservation"]=null,["scope"]="Replay of actual rejected gen8 plan and complete prior dialogue. That proposal submitted no action. Original pixels were not logged; no current visual identity is supplied or inferred. A new proposal still requires normal grounding and executor checks before any action."};
        string repairRaw=null,repairStatus=null;bool repairDone=false,repairOk=false;
        File.WriteAllText(Path.Combine(output,"actual-gen8-repair.request.json"),(string)Call(model,"BuildRoomTaskRequestJsonWithObservationHistory",repairInput.ToString(Formatting.None),null,null,false));
        model.PostRoomTaskMessage(repairInput.ToString(Formatting.None),false,(a,b,c)=>{repairOk=a;repairRaw=b;repairStatus=c;repairDone=true;});
        yield return Until(()=>repairDone,"One finite native repair of the actual rejected compound request.",40f);
        RoomTaskProtocol.Decision repairedDecision=null;JArray nativePlan=null;string planFailure=null,nativeSelection=null;
        bool planPassed=repairOk&&RoomTaskProtocol.TryDecision(RoomTaskSequenceProtocol.DecisionOnly(repairRaw),originalRequest,false,out repairedDecision,out planFailure)&&
            RoomTaskSequenceProtocol.TryPlan(repairRaw,originalRequest,out nativePlan,out planFailure);
        // The controller initializes new candidate steps before selection.
        if(planPassed) {
            foreach(var step in nativePlan.OfType<JObject>())step["status"]="pending";
            planPassed=RoomTaskSequenceProtocol.TrySelect(repairRaw,repairedDecision,nativePlan,out nativeSelection,out planFailure);
        }
        rows.Add(new JObject{["name"]="actual-gen8-native-bounded-repair",["passed"]=planPassed,["input"]=repairInput,["output"]=repairRaw,
            ["structurePassed"]=planPassed,["selectedOperation"] = repairedDecision?.intent,["protectedAbstention"]=planPassed&&nativeSelection==null,
            ["visualGroundingReplayed"]=false,
            ["status"]=repairStatus,["validation"]=planFailure,["submittedAction"]=false,["scope"]="Real model output through production planner and plan/selection validation, not a supplied ideal repair. Missing original pixels prevent an original-scene execution claim."});Save(output,fixture);
        var texture=new Texture2D(64,64,TextureFormat.RGB24,false);texture.SetPixels(Enumerable.Repeat(Color.red,4096).ToArray());texture.Apply();
        string image="data:image/png;base64,"+Convert.ToBase64String(texture.EncodeToPNG());UnityEngine.Object.Destroy(texture);
        foreach(var item in new[]{(text:"This image is blue.",expected:"contradicted"),(text:"I like this image.",expected:"not-visual"),(text:"The hidden other side of this object has no marks.",expected:"unresolved")}) {
            var input=new JObject{["monitorRequestId"]=Guid.NewGuid().ToString("N"),["speechId"]="synthetic-image",["speech"]=item.text,["claimQuote"]=item.text,["captureId"]="synthetic-solid-red"};
            bool done=false,ok=false;string result=null,status=null;
            model.PostActivityVisualEvidence(input.ToString(),image,(a,b,c)=>{ok=a;result=b;status=c;done=true;});
            yield return Until(()=>done,"Bounded native image check settles.");
            bool valid=ok&&ActivityVisualEvidenceProtocol.TryRead(result,input,out _);
            bool semanticPassed=valid&&(string)JObject.Parse(result)["assessment"]==item.expected;
            // A blank synthetic image has no identified 3D object with a back.
            // Abstention on that unresolved referent is safe, but is NOT counted
            // as a semantic success or as an established visual observation.
            bool protectedAbstention=valid&&item.expected=="unresolved"&&(string)JObject.Parse(result)["assessment"]=="not-visual";
            bool passed=semanticPassed||protectedAbstention;
            rows.Add(new JObject{["name"]="synthetic-same-capture-"+item.expected,["passed"]=passed,["semanticPassed"]=semanticPassed,["protectedAbstention"]=protectedAbstention,["input"]=input,["result"]=result,["status"]=status});Save(output,fixture);
        }
        var settings=(ChatSetting)Get(chat,"m_ChatSettings");var old=settings.m_ChatModel;settings.m_ChatModel=model;
        try {
            Set(chat,"m_EnableActivityMonitor",true);Call(chat,"EnsureActivityOwner");
            Set(chat,"m_ActivityNextPoll",Time.realtimeSinceStartup+120f);
            int generation=(int)Get(chat,"m_FormalResponseGeneration");
            var source=new JObject{["captureId"]="same-capture-integration",["formalPixelsProvided"]=true,["observedAt"]=Time.realtimeSinceStartup};
            Set(chat,"m_ActivityReplyImage",image);Set(chat,"m_ActivityReplyObservationGeneration",generation);Set(chat,"m_ActivityReplyObservation",source);
            var frame=SpeechFrame();frame["speech"][0]["text"]="This image is blue.";
            frame["speech"][0]["observedAtCreation"]=new JObject{["formalObservation"]=source.DeepClone()};
            Call(chat,"TryActivityVisualRecheck",frame,"{\"claimDomain\":\"visual\",\"speechAct\":\"factual-claim\",\"claimQuote\":\"This image is blue.\"}");
            yield return Until(()=>!(bool)Get(chat,"m_ActivityRequestPending"),"Same-source image recheck returns to ChatSample.");
            object[] factsArgs={new Dictionary<string,ChatSample.MonitoredActivity>(),null};
            var facts=(JObject)Call(chat,"ReadActivityFacts",factsArgs);
            Check((string)facts["visual:phrase"]?["assessment"]=="contradicted","Recheck result enters live monitoring facts, not just private cache.");
            frame["facts"]=facts;frame["evidenceScopes"]=JObject.FromObject(Call(chat,"ActivityEvidenceScopes",facts));
            string raw=null,status=null;bool done=false,ok=false;
            string input=model.PrepareActivityMonitorFrame(frame.ToString(Formatting.None),out string budget);
            Check(input!=null,"Image-derived proof still fits complete monitor budget.");frame=JObject.Parse(input);
            model.PostActivityMonitor(input,(a,b,c)=>{ok=a;raw=b;status=c;done=true;});
            yield return Until(()=>done,"Speech check consumes same-source visual evidence.");
            JObject verdict=null;string rejection=null;
            bool valid=ok&&ActivityMonitorProtocol.TryRead(raw,frame,out verdict,out rejection);
            bool passed=valid&&(string)verdict["speech"]["issue"]=="mistake";
            rows.Add(new JObject{["name"]="synthetic-visual-source-to-live-speech-proof",["passed"]=passed,["frame"]=frame,["raw"]=raw,["rejection"]=rejection,["status"]=status,["budget"]=JObject.Parse(budget)});Save(output,fixture);
        }finally{Call(chat,"CancelActivityMonitoring",true);Set(chat,"m_EnableActivityMonitor",false);settings.m_ChatModel=old;}
    }
    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string output)
    {
        checks=0;completed=false;rows.Clear();var fixture=ActivityEvidencePolicy.Parse(File.ReadAllText("Assets/Editor/Fixtures/ActivityMonitor.continuity-20261016-0043.json"));
        var host=new GameObject("Task continuity production regression");host.SetActive(false);
        var model=host.AddComponent<ChatQW>();model.enabled=false;model.m_PrewarmPrefixOnStart=false;model.m_Backend=ChatQW.BackendType.Local;model.m_LocalUrl="http://127.0.0.1:8080/v1/chat/completions";Url(model,model.m_LocalUrl);host.SetActive(true);
        Set(chat,"m_AgentRunning",false);Set(chat,"m_EnableActivityMonitor",false);
        try {
            Checks(chat,model,fixture,output);Save(output,fixture);
            yield return Transport(model);Save(output,fixture);
            yield return Native(chat,model,fixture,output);
            yield return ConversationTtsTransportRegression.Run(output);
            rows.Add(new JObject{["name"]="real-tts-http-and-playback",["passed"]=ConversationTtsTransportRegression.LastReport.All(r=>(bool)r["passed"]),["cases"]=ConversationTtsTransportRegression.LastReport.DeepClone()});
            Check(rows.All(r=>(bool?)r["passed"]!=false),"Native semantic or transport case failed; inspect continuity.json.");completed=true;Save(output,fixture);
        }finally{model.CancelActiveResponse();Save(output,fixture);UnityEngine.Object.Destroy(host);}
    }
}
