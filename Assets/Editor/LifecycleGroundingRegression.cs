using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Actual log replay plus production producers. No inferred speech is substituted
// for completed audio and no taskSequence is injected before the base producer.
public static class LifecycleGroundingRegression
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static object Call(object o,string n,params object[] a)=>o.GetType().GetMethod(n,Flags).Invoke(o,a);
    static object Get(object o,string n)=>o.GetType().GetField(n,Flags).GetValue(o);
    static void Set(object o,string n,object v)=>o.GetType().GetField(n,Flags).SetValue(o,v);
    static JObject fixture; static readonly JArray cases=new JArray(); static string directory;
    static void Save(bool done=false)=>File.WriteAllText(Path.Combine(directory,"lifecycle-grounding.json"),new JObject{
        ["completed"]=done,["passed"]=done&&cases.All(c=>(bool)c["passed"]),["sourceSha256"]=fixture["sha256"],
        ["actualDialogue"]=fixture["dialogue"],["cases"]=cases}.ToString());
    static void Check(string n,bool ok,JToken proof=null){cases.Add(new JObject{["name"]=n,["passed"]=ok,["evidence"]=proof});Save();}
    static JObject Event(int line)=>(JObject)((JArray)fixture["events"]).First(e=>(int)e["line"]==line)["data"].DeepClone();
    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string output)
    {
        directory=output;cases.Clear();fixture=ActivityEvidencePolicy.Parse(File.ReadAllText("Assets/Editor/Fixtures/LifecycleGrounding.20261016-1739.json"));
        var host=new GameObject("Lifecycle grounding native replay");host.SetActive(false);
        var model=host.AddComponent<ChatQW>();model.enabled=false;model.m_PrewarmPrefixOnStart=false;
        model.m_Backend=ChatQW.BackendType.Local;model.m_LocalUrl="http://127.0.0.1:8080/v1/chat/completions";
        for(Type t=model.GetType();t!=null;t=t.BaseType){var f=t.GetField("url",Flags);if(f!=null){f.SetValue(model,model.m_LocalUrl);break;}}
        host.SetActive(true);
        try {
            var facts=(JObject)Call(chat,"BuildSpatialGoalExecutionFacts");
            Check("production-base-has-no-injected-sequence",facts.Property("taskSequence")==null);
            foreach(var value in new JToken[]{null,JValue.CreateNull(),new JValue(false),new JValue("unavailable"),new JArray(),new JObject{["steps"]=JValue.CreateNull()}}) {
                var source=(JObject)facts.DeepClone();if(value!=null)source["taskSequence"]=value.DeepClone();
                var projected=SpatialGoalResultContextPolicy.ProjectResultFacts(source);
                Check("optional-sequence-"+(value?.Type.ToString()??"absent"),projected!=null&&SpatialGoalResultContextPolicy.RequiredReport(source)==SpatialGoalResultContextPolicy.RequiredReport(projected));
            }
            Call(chat,"RememberCompletedDeviceStep");
            Check("production-archive-survives-base-producer",Get(chat,"m_WorkDeviceResult") is JObject);
            // Terminal release runs even without an optional plan. A stale
            // reporting generation cannot clear or dispatch a replacement turn.
            var leaseType=typeof(ChatSample).GetNestedType("SpatialGoalRunnerLease",Flags);
            foreach(string outcome in new[]{"satisfied","blocked"}) {
                var lease=Activator.CreateInstance(leaseType);Set(lease,"goalId","regression-"+outcome);
                Set(lease,"generation",(int)Get(chat,"m_FormalResponseGeneration")-1);Set(lease,"model",model);
                Set(chat,"m_SpatialGoalRunner",lease);
                Call(chat,"EndSpatialGoalRunner",lease,"recorded-"+outcome,outcome);
                Check("terminal-releases-"+outcome,Get(chat,"m_SpatialGoalRunner")==null&&(bool)Get(lease,"ending"));
                var replacement=Activator.CreateInstance(leaseType);Set(chat,"m_SpatialGoalRunner",replacement);
                Call(chat,"EndSpatialGoalRunner",lease,"old-callback",outcome);
                Check("terminal-old-owner-keeps-replacement-"+outcome,ReferenceEquals(Get(chat,"m_SpatialGoalRunner"),replacement));Set(chat,"m_SpatialGoalRunner",null);
            }
            var previous=Event(150586);previous["steps"][0]["status"]="failed";previous["steps"][0]["goalId"]="old-goal";
            previous["steps"][0]["receipt"]=new JObject{["phase"]="blocked",["reason"]="search-budget"};previous["steps"][1]["status"]="pending";
            var before=previous.DeepClone();var resumed=TaskContinuationContextPolicy.Resume(previous,"s1","你怎么不动了，继续呀。",55,8);
            Check("reviewed-resumption-retains-parent-and-pending-return",resumed!=null&&(string)resumed["steps"][1]["status"]=="pending"&&
                (string)resumed["steps"][0]["previousAttempt"]["receipt"]["reason"]=="search-budget"&&JToken.DeepEquals(previous,before),resumed);
            foreach(string kind in new[]{"observation","other"}) {
                var generic=(JObject)previous.DeepClone();generic["steps"][0]["kind"]=kind;generic["steps"][0]["status"]="completed";
                generic["steps"][1]["kind"]="other";
                var r=TaskContinuationContextPolicy.Resume(generic,"s2","Continue with saving the result.",56,9);
                Check("generic-resumption-"+kind,r!=null&&(string)r["steps"][0]["status"]=="completed"&&r["steps"][0]["receipt"]!=null);
            }
            Check("unknown-step-cannot-resume",TaskContinuationContextPolicy.Resume(previous,"absent","continue",5,9)==null);
            string repeated=new JObject{["scope"]=new string('x',700),["point"]=new JObject{["x"]=1,["y"]=2}}.ToString(Formatting.None);
            string raw="{\"tiny\":1.401298E-45,\"negativeZero\":-0.0,\"precise\":0.12345678901234567890123456789012345,\"first\":"+repeated+",\"second\":"+repeated+"}";
            string compact=SceneFactContextCompactor.CompactJson(raw,out var sharing);
            Check("lossless-sharing-preserves-unity-numbers",sharing.applied&&sharing.roundTripVerified&&compact.Contains("1.401298E-45")&&compact.Contains("-0.0")&&compact.Contains("0.12345678901234567890123456789012345"),JObject.FromObject(sharing));
            foreach(var e in ((JArray)fixture["events"]).OfType<JObject>().Where(e=>(string)e["kind"]=="Activity/MonitorFrame")) {
                int ordinal=0;
                foreach(var f in (string)e["data"]["purpose"]=="speech"?ActivityEvidencePolicy.SpeechOperations((JObject)e["data"]):new[]{(JObject)e["data"]}) {
                    string prepared=model.PrepareActivityMonitorFrame(ActivityEvidencePolicy.Focus(f).ToString(Formatting.None),out string diagnostic);
                    Check("actual-monitor-wire-"+e["line"]+"-"+ordinal++,prepared!=null,JObject.Parse(diagnostic));
                }
            }
            var attention=new JObject{["currentUserText"]="现在只是把灯关掉了，还没有走回来哦。",["autonomous"]=false,
                ["autonomousAttention"]=new JObject{["source"]="current-private-intent-review",["kind"]="current-user-body",["actionAuthorization"]=false,
                    ["workEpoch"]=8,["userTurn"]=8,["intention"]="走回用户身边"},
                ["workContinuation"]=new JObject{["source"]="approved-same-user-work-next-step",["workEpoch"]=8,["userTurn"]=8,
                    ["nextStepIntention"]="走回用户身边",["originalRequest"]="现在只是把灯关掉了，还没有走回来哦。"}};
            Check("current-work-body-attention-without-first-reply-classification",RoomTaskContextPolicy.ContinuedBodyAttention(attention));
            attention["autonomousAttention"]["userTurn"]=7;
            Check("stale-work-attention-rejected",!RoomTaskContextPolicy.ContinuedBodyAttention(attention));
            foreach(var request in new[]{new[]{"你怎么不动了，继续呀。","true"},new[]{"为什么没动？只解释，不要继续。","false"},new[]{"不用关灯了，留在这里。","false"}})
                yield return ParentReview(model,request[0],request[1]=="true");
            yield return ParentSuccessor(model,"你怎么不动了，继续呀。",true);
            yield return ParentSuccessor(model,"先别回来，留在原地。",false);
            var bad=Event(163203)["decision"] as JObject;var badFrame=Event(162922);
            bad["claimEntity"]="user";
            Check("actual-wrong-arrival-correction-rejected",!ActivityMonitorProtocol.TryRead(bad.ToString(Formatting.None),badFrame,out _,out string rejected),new JValue(rejected));
            // Native interpretation retains original multi-turn dialogue and
            // all real receipts. Past object arrival never becomes user arrival.
            yield return Monitor(model,badFrame,"original-future-return", "none");
            foreach(var pair in new[]{new[]{"I am coming to you now. Please wait a moment.","en"},new[]{"我这就过来，再稍等一下。","zh"}}) {
                var f=(JObject)badFrame.DeepClone();f["speech"][0]["text"]=pair[0];f["speech"][0]["language"]=pair[1];
                yield return Monitor(model,f,"future-return-"+pair[1],"none");
            }
            var unrelated=(JObject)badFrame.DeepClone();unrelated["speech"][0]["text"]="I would like to make the document clearer.";unrelated["speech"][0]["language"]="en";
            yield return Monitor(model,unrelated,"reasonable-editor-intention","none");
            Save(true);
            if(cases.Any(c=>!(bool)c["passed"]))throw new InvalidOperationException("Lifecycle grounding replay failed; inspect report.");
        } finally {model.CancelActiveResponse();model.CancelActivityMonitor();UnityEngine.Object.Destroy(host);}
    }
    static IEnumerator ParentReview(ChatQW model,string request,bool expected)
    {
        var parent=Event(150586);string original=(string)parent["request"];
        var history=new JArray(((JArray)fixture["dialogue"]).OfType<JObject>().Where(e=>(int)e["line"]<152946).Select(e=>new JObject{
            ["role"]=(string)e["kind"]=="Timing"?"user":"assistant",["text"]=e["data"] is JObject speech?speech["text"]:e["text"],
            ["sourceLine"]=e["line"]}));
        var input=new JObject{["mode"]="goal-user-update",["currentUserText"]=request,["latestUserText"]=request,["parentTask"]=parent,
            ["existingGoal"]=new JObject{["goalId"]="device-goal-060628f83f2b4e6e912644e50642273a",["status"]="blocked",
                ["originalRequest"]=original,["targetDescription"]="刚才看到的那盏小灯",["desiredState"]="off",["boundEntityId"]=null},
            ["verifiedDeviceHistory"]=new JArray(),["verifiedObservedDeviceHistory"]=new JArray(),["referentSources"]=new JArray(),["recentDialogue"]=history};
        bool done=false,ok=false;string raw="",status="";
        model.PostRoomDeviceGoalReviewMessage(input.ToString(Formatting.None),(a,b,c)=>{done=true;ok=a;raw=b;status=c;});
        float until=Time.realtimeSinceStartup+25;while(!done&&Time.realtimeSinceStartup<until)yield return null;
        RoomTaskProtocol.DeviceGoalReview review=null;
        bool parsed=ok&&RoomTaskProtocol.TryDeviceGoalUserUpdateReview(raw,out review);
        Check("parent-review-"+request,parsed&&review.resumeParentTask==expected,new JObject{["raw"]=raw,["status"]=status,["historyRows"]=history.Count});
    }
    static IEnumerator ParentSuccessor(ChatQW model,string latest,bool expected)
    {
        var parent=Event(150586);var steps=(JArray)parent["steps"];
        steps[0]["status"]="completed";steps[0]["receipt"]=new JObject{["status"]="satisfied",["completionEvidenceValid"]=true};
        steps[1]["status"]="pending";var selected=(JObject)steps[1];
        var input=new JObject{["roomControlReview"]=RoomControlReviewProtocol.Continuation,["currentUserText"]=latest,
            ["resumedParentRequest"]=parent["request"],["selectedStep"]=selected,["steps"]=steps,["priorResultSpeechCompleted"]=false};
        bool done=false,ok=false;string raw="",status="";
        model.PostRoomPreparationMessage(input.ToString(Formatting.None),(a,b,c)=>{done=true;ok=a;raw=b;status=c;});
        float until=Time.realtimeSinceStartup+25;while(!done&&Time.realtimeSinceStartup<until)yield return null;
        bool accepted=ok&&RoomControlReviewProtocol.AcceptsContinuation(raw,(string)selected["id"],(string)parent["request"],(string)selected["quote"]);
        Check("native-resumed-parent-successor-"+latest,done&&ok&&accepted==expected,new JObject{["raw"]=raw,["status"]=status,
            ["scope"]="Original recorded parent plus controlled completed predecessor; latest cancellation variant must override historical continuation."});
    }
    static IEnumerator Monitor(ChatQW model,JObject source,string name,string expected)
    {
        var frame=ActivityEvidencePolicy.Focus(source);var attempts=new ActivityMonitorAttempts();string key=ActivityEvidencePolicy.SemanticKey(frame);string raw="",status="";JObject verdict=null;string reason="";
        for(int i=0;i<3;i++) {
            string input=model.PrepareActivityMonitorFrame(frame.ToString(Formatting.None),out var budget);
            if(input==null){reason="budget";break;}
            bool done=false,ok=false;model.PostActivityMonitor(input,(a,b,c)=>{done=true;ok=a;raw=b;status=c;});
            float until=Time.realtimeSinceStartup+25;while(!done&&Time.realtimeSinceStartup<until)yield return null;
            if(done&&ok&&ActivityMonitorProtocol.TryRead(raw,JObject.Parse(input),out verdict,out reason))break;
            attempts.Failed(key,Time.realtimeSinceStartup,reason??status,raw,false);
            frame["previousAttempt"]=attempts.Feedback(key);
        }
        Check(name,verdict!=null&&(string)verdict["speech"]["issue"]==expected,new JObject{["raw"]=raw,["reason"]=reason,["status"]=status});
    }
}
