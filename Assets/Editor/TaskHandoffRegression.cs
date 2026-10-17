using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class TaskHandoffRegression
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static int checks;
    private static readonly JArray cases=new JArray();
    private static object Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,Flags).Invoke(o,args);
    private static object Get(object o,string name)=>o.GetType().GetField(name,Flags).GetValue(o);
    private static void Set(object o,string name,object value)=>o.GetType().GetField(name,Flags).SetValue(o,value);
    private static void Check(bool value,string reason){checks++;if(!value)throw new InvalidOperationException("Task handoff: "+reason);}
    private static IEnumerator Until(Func<bool> done){float start=Time.realtimeSinceStartup;while(!done()){if(Time.realtimeSinceStartup-start>40)throw new TimeoutException("Handoff model callback");yield return null;}}
    private static void Save(string output,JObject fixture,bool completed=false)=>File.WriteAllText(Path.Combine(output,"task-handoff.json"),new JObject{
        ["completed"]=completed,["passed"]=completed&&cases.All(c=>(bool?)c["passed"]!=false),["checks"]=checks,
        ["sourceHash"]=fixture["sha256"],["dialogue"]=fixture["dialogue"].DeepClone(),["recordedEvents"]=((JArray)fixture["events"]).Count,["cases"]=cases}.ToString());
    private static JObject Event(JObject fixture,string kind,int line=0)=>((JArray)fixture["events"]).OfType<JObject>().First(e=>(string)e["kind"]==kind&&(line==0||(int)e["line"]==line));
    private static JObject Own(ChatSample chat,ArdyRoomDialogueBridge bridge,ChatQW model,string request,string kind,string status="pending")
    {
        Call(chat,"RecordAcceptedMotionUserTurn",request);
        int turn=(int)Get(chat,"m_ActionConstraints").GetType().GetProperty("CurrentUserTurn").GetValue(Get(chat,"m_ActionConstraints"));
        int epoch=(int)Get(chat,"m_WorkEpoch")+1;Set(chat,"m_WorkEpoch",epoch);
        var sequence=new JObject{["request"]=request,["workEpoch"]=epoch,["userTurn"]=turn,["steps"]=new JArray(new JObject{
            ["id"]="s1",["kind"]=kind,["quote"]=request,["status"]=status,["after"]=new JArray(),["actionId"]="",
            ["receipt"]=status=="completed"?new JObject{["phase"]="completed",["operation"]="save",["entityId"]="document-copy",["observedUtc"]="2026-10-16T01:40:01Z"}:null})};
        Set(chat,"m_RoomTaskSequence",sequence);Set(chat,"m_RoomTaskSequenceModel",model);Set(chat,"m_RoomTaskSequenceBridge",bridge);
        Set(chat,"m_RoomTaskSequenceAvatar",bridge.avatar);Set(chat,"m_RoomTaskSequenceEnvironment",bridge.environmentRoot);
        Set(chat,"m_RoomTaskIntent","none");Set(chat,"m_RoomTaskActionId","");Call(chat,"RecordWorkRoomTaskContext");
        Set(chat,"m_WorkRequestSemantics",null);Set(chat,"m_WorkReviewOpen",true);Set(chat,"m_WorkStatus","continue");
        Set(chat,"m_WorkNoProgress",false);Set(chat,"m_WorkContinuations",0);Set(chat,"m_WorkReplyOnly",false);Set(chat,"m_WorkNextStepId","s1");
        return sequence;
    }

    private static void Runtime(ChatSample chat,ArdyRoomDialogueBridge bridge,ChatQW model,JObject fixture)
    {
        Check(WorkNextIntentProtocol.HistoricalStatus(new JObject{["taskSequence"]=null})=="unknown","A JSON null task table is unknown, not completed or a parsing exception.");
        foreach(var item in new[]{(kind:"device-state",request:"把刚才关掉的设备重新打开。"),(kind:"observation",request:"残りの部分を確認してください。"),(kind:"other",request:"Inspect the source, then save an edited copy without changing the original.")}){
            var sequence=Own(chat,bridge,model,item.request,item.kind);
            Check(Call(chat,"CreateWorkRoomStepRequest")!=null,"Owned ready step without actionId is executable: "+item.kind);
            Check((bool)Call(chat,"ShouldGuardWorkSceneFeedback",(int)Get(chat,"m_FormalResponseGeneration")),"Unstarted canonical work cannot silently use General reply: "+item.kind);
            Call(chat,"RememberUnfinishedWorkBeforeReset",true);
            var history=(JObject)Get(chat,"m_PreviousUnfinishedWork");
            Check((string)history["request"]==item.request&&(bool)history["currentStepNotStarted"]&&WorkNextIntentProtocol.HistoricalStatus(history)=="unfinished",
                "Classifier failure preserves real parent/step/nonexecution: "+item.kind);
            Set(chat,"m_WorkContinuations",3);Check(Call(chat,"CreateWorkRoomStepRequest")==null,"Continuation limit is unchanged.");Set(chat,"m_WorkContinuations",0);
            sequence["userTurn"]=(int)sequence["userTurn"]+1;Check(Call(chat,"CreateWorkRoomStepRequest")==null,"A stale sequence cannot authorize a none-intent continuation.");
        }
        var complete=Own(chat,bridge,model,"Save the edited copy, then report its location.","other","completed");
        var second=(JObject)complete["steps"][0].DeepClone();second["id"]="s2";second["receipt"]["operation"]="locate";((JArray)complete["steps"]).Add(second);
        Call(chat,"RecordWorkCompletedSpeech","The copy was saved. The result is in the output folder.");
        var results=(JArray)((JObject)Call(chat,"ReadWorkDelivery"))["results"];string firstId=(string)results[0]["id"],secondId=(string)results[1]["id"];
        var quotes=(Dictionary<string,string>)Get(chat,"m_WorkDeliveredResults");var revisions=(Dictionary<string,string>)Get(chat,"m_WorkDeliveredPlayback");
        quotes[firstId]="The copy was saved.";revisions[firstId]=(string)results[0]["playbackRevision"];
        ((Dictionary<string,int>)Get(chat,"m_WorkDeliveredPlaybackLengths"))[firstId]=((string)results[0]["completedPlaybackText"]).Length;
        Call(chat,"RecordWorkCompletedSpeechForResult","The output folder is next to the source folder.",secondId);
        results=(JArray)((JObject)Call(chat,"ReadWorkDelivery"))["results"];
        Check((bool)results[0]["delivered"]&&!(bool)results[0]["playbackReviewPending"],"Scoped other-result playback does not reopen a settled result.");
        Call(chat,"RecordWorkCompletedSpeech","I like how this turned out.");results=(JArray)((JObject)Call(chat,"ReadWorkDelivery"))["results"];
        Check((bool)results[0]["delivered"]&&(bool)results[0]["playbackReviewPending"],"Unclassified later speech requests review without declaring earlier coverage lost.");
        Check(WorkResultDeliveryProtocol.CanClose(new JArray(results[0].DeepClone()),complete,""),"Pending background audit alone does not force another public result report.");
        Call(chat,"RecordWorkCompletedSpeech","Actually, the copy was not saved.");results=(JArray)((JObject)Call(chat,"ReadWorkDelivery"))["results"];
        var retract=new WorkResultDeliveryProtocol.Report{id=firstId,coverage="unreported",playedRevision=(string)results[0]["playbackRevision"],lastRelevantQuote="Actually, the copy was not saved.",finalDisposition="retracted"};
        Check(WorkResultDeliveryProtocol.RetractionProven((JObject)results[0],retract,(string)results[0]["completedPlaybackText"]),"A scoped actual retraction remains reviewable.");
        retract.lastRelevantQuote="The result is in the output folder.";
        Check(!WorkResultDeliveryProtocol.RetractionProven((JObject)results[0],retract,(string)results[0]["completedPlaybackText"]),"An already audited different sentence is not a new retraction.");
        retract.lastRelevantQuote="Actually, the copy was not saved.";
        retract.finalDisposition="unreported";Check(!WorkResultDeliveryProtocol.RetractionProven((JObject)results[0],retract,(string)results[0]["completedPlaybackText"]),"Previously covered is not never reported.");
        foreach(string quote in new[]{"すぐ","马上","soon","right away","立刻","後で"})Check(ActivityMonitorProtocol.ScopeOnlyQualifier(quote),"Time alone is not uncertainty: "+quote);
        foreach(string quote in new[]{"多分すぐ","可能马上","maybe soon"})Check(!ActivityMonitorProtocol.ScopeOnlyQualifier(quote),"Actual uncertainty remains allowed: "+quote);
        var independent=Own(chat,bridge,model,"说明上次为什么失败，然后保存副本。","other");
        independent["coverage"]=new JArray(new JObject{["quote"]="说明上次为什么失败",["kind"]="answer",["stepIds"]=new JArray()},
            new JObject{["quote"]="然后保存副本。",["kind"]="action",["stepIds"]=new JArray("s1")});
        independent["answerObligations"]=ReplyAnswerObligationProtocol.FromPlan((string)independent["request"],(JArray)independent["coverage"],null);
        Check(((JArray)Call(chat,"ReadWorkAnswerObligations",true)).Count==1,"Canonical independent question survives failed first-reply classification.");
        Check(((JArray)((JObject)Call(chat,"ReadWorkDelivery"))["results"]).OfType<JObject>().Any(r=>(string)r["source"]=="independent-answer"),"Missing first classification cannot erase an owed explanation.");
        var annotations=new JObject{["navigation"]=new JObject{["constraint"]="Keep the original document unchanged.", ["x"]=1}};
        Check(JToken.DeepEquals(TaskContinuationContextPolicy.Outcome(annotations),annotations),"Unknown constraints inside diagnostic containers survive projection.");
        cases.Add(new JObject{["name"]="owned-unstarted-cross-turn-and-scoped-playback",["passed"]=true});

        var budgets=new JArray();
        foreach(var e in ((JArray)fixture["events"]).OfType<JObject>().Where(e=>(string)e["kind"]=="Activity/MonitorFrame")){
            var source=(JObject)e["data"].DeepClone();string before=source.ToString(Formatting.None);
            string projected=model.PrepareActivityMonitorFrame(before,out string diagnostic);
            budgets.Add(new JObject{["line"]=e["line"],["passed"]=projected!=null,["diagnostic"]=JObject.Parse(diagnostic)});
            Check(before==source.ToString(Formatting.None),"Monitor attention cannot mutate original evidence.");
        }
        cases.Add(new JObject{["name"]="all-recorded-monitor-final-requests",["passed"]=budgets.All(r=>(bool)r["passed"]),["requests"]=budgets});
        var tooLarge=(JObject)Event(fixture,"Activity/MonitorFrame")["data"].DeepClone();tooLarge["facts"]["constraints"]=new JArray(new string('界',20000));
        Check(model.PrepareActivityMonitorFrame(tooLarge.ToString(),out _) == null,"Unknown mandatory restrictions must still reject over budget.");
        var simultaneous=(JObject)Event(fixture,"Activity/MonitorFrame",8836)["data"].DeepClone();
        var action=((JArray)fixture["events"]).OfType<JObject>().First(e=>(string)e["kind"]=="Activity/MonitorFrame"&&(e["data"]?["actions"] as JArray)?.Count>0)["data"];
        simultaneous["actions"]=action["actions"].DeepClone();
        foreach(var property in ((JObject)action["facts"]).Properties().Where(p=>p.Name.StartsWith("action:"))) {
            simultaneous["facts"][property.Name]=property.Value.DeepClone();
            simultaneous["evidenceScopes"][property.Name]=action["evidenceScopes"][property.Name]?.DeepClone();
        }
        var purposes=ActivityEvidencePolicy.AttentionFrames(simultaneous).ToArray();
        Check(purposes.Any(f=>(string)f["purpose"]=="speech")&&purposes.Any(f=>(string)f["purpose"]=="action"),"Parallel work gives both consumers a turn.");
        foreach(var frame in purposes) {
            string projected=model.PrepareActivityMonitorFrame(frame.ToString(),out string diagnostic);
            Check(projected!=null,"Complete parallel monitor schema fits: "+(string)frame["purpose"]+" "+diagnostic);
        }
    }

    private static IEnumerator Native(ChatSample chat,ChatQW model,JObject fixture,string output)
    {
        yield return Coverage(chat,model,fixture,output);
        yield return Chronology(model,fixture,output);
        var dialogue=((JArray)fixture["dialogue"]).OfType<JObject>().ToList();
        var pending=(JObject)Event(fixture,"Work/TaskSequence",14267)["data"].DeepClone();
        foreach(var item in new[]{(name:"actual-why-not-moving",text:"你怎么不动啊？",parent:(string)pending["request"],language:"zh"),
            (name:"save-followup-en",text:"Why haven't you done it?",parent:"Inspect the source, then save an edited copy without changing the original.",language:"en"),
            (name:"generate-followup-ja",text:"どうしてまだできていないの？",parent:"見本を確認してから新しい画像を生成してください。",language:"ja")}){
            var history=dialogue.Where(r=>(int)r["line"]<17027).Select(r=>new LLM.SendData((string)r["role"],(string)r["text"])).ToList();
            var parent=(JObject)pending.DeepClone();parent["request"]=item.parent;
            if(item.name!="actual-why-not-moving")foreach(var step in ((JArray)parent["steps"]).OfType<JObject>()){step["kind"]="other";step["quote"]=item.parent;step["desiredState"]=null;}
            var context=new JObject{["humanInput"]=new JObject{["primaryUtterance"]=item.text},["previousUnfinishedWork"]=new JObject{
                ["request"]=item.parent,["taskSequence"]=parent,["currentStepNotStarted"]=true,["executionProgress"]=WorkNextIntentProtocol.Progress(parent),
                ["executionAtInterruption"]=new JObject{["actionId"]="old-follow-stop",["phase"]="stopped",["reason"]="Earlier task stopped; approved successor not submitted."}},
                ["work"]=new JObject{["open"]=true},["scope"]="Recorded history for actual case; labelled task variants preserve all prior dialogue and replace only parent purpose. No completed receipt for this parent."};
            var current=new LLM.SendData("user",item.text);
            string wire=(string)Call(model,"BuildFirstReplyReviewWire",current,history,context.ToString(Formatting.None),new FirstReplyCompletion());
            Check(wire!=null,"Full-history follow-up review must fit: "+item.name);File.WriteAllText(Path.Combine(output,item.name+".request.json"),wire);
            string route=null,status=null;JObject question=null;
            Action<string,JObject,string,bool?,string,string,JObject,string> callback=(r,q,k,s,d,t,p,f)=>{route=r;question=q;status=t;};
            yield return (IEnumerator)Call(model,"RequestFirstReplyReview",wire,(int)Get(model,"m_StreamRequestGeneration"),callback,0,false);
            // Routing may name the pending task abstractly. The bound parent and
            // next native work review below establish which operation is pending.
            bool refers=question!=null&&(string)question["needsEvidence"]=="diagnostic";
            cases.Add(new JObject{["name"]=item.name,["passed"]=route=="evidence"&&refers,["route"]=route,["question"]=question,["status"]=status,["inputHistoryCount"]=history.Count});Save(output,fixture);
            Call(chat,"RecordAcceptedMotionUserTurn",item.text);
            Set(chat,"m_WorkEpoch",(int)Get(chat,"m_WorkEpoch")+1);Set(chat,"m_RoomTaskSequence",null);Set(chat,"m_WorkRequestSemantics",null);
            Set(chat,"m_PreviousUnfinishedWork",context["previousUnfinishedWork"]);Set(chat,"m_WorkReviewOpen",true);Set(chat,"m_WorkStatus","unreviewed");
            Set(chat,"m_LastUserMsg",item.text);Set(chat,"m_WorkContinuations",0);
            model.m_DataList.Clear();model.m_DataList.AddRange(history);model.m_DataList.Add(current);
            string frame=(string)Call(chat,"BuildWorkReviewFrame","actual-unstarted-followup");
            string prompt=(string)Call(chat,"BuildDedicatedWorkReviewPrompt",frame);
            string workWire=(string)Call(model,"BuildWorkReviewRequestJson",prompt);
            Check(workWire!=null,"Follow-up work review final request is built.");
            File.WriteAllText(Path.Combine(output,item.name+".work-request.json"),workWire);
            bool finished=false;string workRaw=null;
            model.PostWorkReviewMsg(prompt,value=>{workRaw=value;finished=true;});yield return Until(()=>finished);
            JObject work=null;try{work=JObject.Parse(workRaw??"");}catch(JsonException){}
            cases.Add(new JObject{["name"]=item.name+"-work-status",["passed"]=(string)work?["previous_work_status"]=="unfinished" && (string)work?["next_step_id"]=="",
                ["raw"]=workRaw,["historyCount"]=history.Count,["actionSubmitted"]=false});Save(output,fixture);
        }
        foreach(var e in ((JArray)fixture["events"]).OfType<JObject>().Where(e=>(string)e["kind"]=="Activity/MonitorFrame"&&e["data"]?["delivery"]!=null)){
            var frame=(JObject)e["data"].DeepClone();var delivery=(JObject)frame["delivery"];
            if((string)delivery["id"]!="6038ABABF1D42428")continue;
            var task=((JArray)fixture["events"]).OfType<JObject>().Last(row=>(string)row["kind"]=="Work/TaskSequence" &&
                (int)row["line"]<(int)e["line"] && (int?)row["data"]?["workEpoch"]==(int?)frame["owner"]?["workEpoch"]);
            delivery["taskContext"]=WorkResultDeliveryProtocol.TaskContext((JObject)task["data"],null,delivery);
            frame["replayedTaskContextSourceLine"]=task["line"].DeepClone();
            var attemptState=new ActivityMonitorAttempts();var attempts=new JArray();
            bool parsed=false;string raw=null,status=null,rejection=null,budget=null;JObject decision=null;
            for(int attempt=0;attempt<3&&!parsed;attempt++) {
                var feedback=attemptState.Feedback("delivery");if(feedback!=null)frame["previousAttempt"]=feedback;
                string input=model.PrepareActivityMonitorFrame(frame.ToString(Formatting.None),out budget);Check(input!=null,"Actual return delivery fits complete request.");
                bool done=false,ok=false;
                model.PostActivityMonitor(input,(a,b,c)=>{ok=a;raw=b;status=c;done=true;});yield return Until(()=>done);
                parsed=ok&&ActivityMonitorProtocol.TryRead(raw,JObject.Parse(input),out decision,out rejection);
                attempts.Add(new JObject{["attempt"]=attempt+1,["accepted"]=parsed,["raw"]=raw,["rejection"]=rejection});
                if(!parsed)attemptState.Failed("delivery",Time.realtimeSinceStartup,rejection??status,raw);
            }
            bool passed=parsed&&(string)decision["delivery"]?["operation"]=="covered";
            cases.Add(new JObject{["name"]="actual-return-delivery-"+(int)e["line"],["passed"]=passed,["frame"]=frame,["raw"]=raw,["status"]=status,["rejection"]=rejection,["attempts"]=attempts,["budget"]=JObject.Parse(budget)});Save(output,fixture);
        }
    }

    private static IEnumerator Chronology(ChatQW model,JObject fixture,string output)
    {
        var dialogue=((JArray)fixture["dialogue"]).OfType<JObject>().ToArray();
        var events=((JArray)fixture["events"]).OfType<JObject>().ToArray();
        var expected=new[]{"ordinary","evidence","tools","tools","tools","evidence","tools","ordinary"};int ordinal=0;
        foreach(var user in dialogue.Where(r=>(string)r["role"]=="user")) {
            int line=(int)user["line"];
            var history=dialogue.Where(r=>(int)r["line"]<line).Select(r=>new LLM.SendData((string)r["role"],(string)r["text"])).ToList();
            var source=events.LastOrDefault(e=>(int)e["line"]<line&&(string)e["kind"]=="Work/TaskSequence");
            JObject parent=null;
            if(source!=null) {
                var sequence=(JObject)source["data"].DeepClone();
                foreach(var settled in events.Where(e=>(int)e["line"]>(int)source["line"]&&(int)e["line"]<line&&(string)e["kind"]=="Work/StepSettled")) {
                    var receipt=settled["data"];
                    if((int?)receipt["workEpoch"]!=(int?)sequence["workEpoch"]||(int?)receipt["userTurn"]!=(int?)sequence["userTurn"])continue;
                    var step=((JArray)sequence["steps"]).OfType<JObject>().FirstOrDefault(s=>(string)s["id"]==(string)receipt["stepId"]);
                    if(step!=null){step["status"]=receipt["status"].DeepClone();step["receipt"]=receipt.DeepClone();}
                }
                parent=new JObject{["request"]=sequence["request"].DeepClone(),["taskSequence"]=sequence,
                    ["executionProgress"]=WorkNextIntentProtocol.Progress(sequence),["sourceLine"]=source["line"].DeepClone()};
            }
            string raw=(string)user["text"];int acoustic=raw.IndexOf(" [同轮分轨观测",StringComparison.Ordinal);
            string primary=acoustic<0?raw:raw.Substring(0,acoustic);
            var context=new JObject{["humanInput"]=new JObject{["primaryUtterance"]=primary},["previousUnfinishedWork"]=parent,
                ["scope"]="Complete ordered prior dialogue and actual engine settlement receipts. No future turn, generated promise or old same-entity operation may complete this parent."};
            var draft=new FirstReplyCompletion();
            if(expected[ordinal]=="ordinary") {
                var spoken=dialogue.FirstOrDefault(r=>(int)r["line"]>line&&(string)r["role"]=="assistant");
                if(spoken!=null)draft.Speech.Add(new SpeechText((string)spoken["text"],"ja"));
            }
            string wire=(string)Call(model,"BuildFirstReplyReviewWire",new LLM.SendData("user",raw),history,context.ToString(Formatting.None),draft);
            Check(wire!=null,"Full actual human input and chronological parent fit first-review budget at line "+line);
            File.WriteAllText(Path.Combine(output,"chronology-"+line+".request.json"),wire);
            string route=null,status=null;JObject question=null;
            Action<string,JObject,string,bool?,string,string,JObject,string> callback=(r,q,k,s,d,t,p,f)=>{route=r;question=q;status=t;};
            yield return (IEnumerator)Call(model,"RequestFirstReplyReview",wire,(int)Get(model,"m_StreamRequestGeneration"),callback,0,false);
            cases.Add(new JObject{["name"]="chronology-"+line,["passed"]=route==expected[ordinal++],["route"]=route,["question"]=question,
                ["status"]=status,["historyCount"]=history.Count,["parentStatus"]=WorkNextIntentProtocol.HistoricalStatus(parent),["actualHumanText"]=raw});Save(output,fixture);
        }
    }

    private static IEnumerator Coverage(ChatSample chat,ChatQW model,JObject fixture,string output)
    {
        var recorded=Event(fixture,"Room/DecisionRepair")["data"]["detail"];
        string actual=(string)recorded["rejectedDecision"];
        string request=(string)JObject.Parse(actual)["evidence"];
        var trials=new List<(string name,string request,string decision,bool allowed)>{("actual-connective-gap",request,actual,true)};
        foreach(var item in new[]{(name:"save-polite-en",prefix:"Please, ",action:"save a copy.",allowed:true),
            (name:"save-negated-zh",prefix:"不要",action:"保存副本。",allowed:false),
            (name:"save-condition-ja",prefix:"確認が終わるまでは、",action:"保存してください。",allowed:false),
            (name:"generate-condition-en",prefix:"Only after I approve, ",action:"generate a new image.",allowed:false),
            (name:"inspect-polite-ja",prefix:"ええと、",action:"内容を確認してください。",allowed:true)}) {
            var plan=new JObject{["taskPlan"]=new JArray(new JObject{["id"]="s1",["quote"]=item.action,["kind"]="other",["desiredState"]=null,["after"]=new JArray()}),
                ["taskCoverage"]=new JArray(new JObject{["quote"]=item.action,["kind"]="action",["stepIds"]=new JArray("s1")})};
            trials.Add((item.name,item.prefix+item.action,plan.ToString(),item.allowed));
        }
        foreach(var item in trials) {
            Check(RoomTaskSequenceProtocol.TryCoverageGapCandidate(item.decision,item.request,out string candidate,out var gaps),"Gap candidate is structurally verifiable: "+item.name);
            Check(JToken.DeepEquals(JObject.Parse(candidate)["taskPlan"],JObject.Parse(item.decision)["taskPlan"]),"Gap review cannot rewrite operations.");
            var input=RoomControlReviewProtocol.CoverageInput(item.request,candidate);input["proposedContextGaps"]=gaps;
            bool done=false,ok=false;string raw=null,status=null;float start=Time.realtimeSinceStartup;
            model.PostRoomPreparationMessage(input.ToString(),(a,b,c)=>{ok=a;raw=b;status=c;done=true;});yield return Until(()=>done);
            bool accepted=ok&&RoomControlReviewProtocol.AcceptsCoverage(raw);
            cases.Add(new JObject{["name"]=item.name,["passed"]=ok&&accepted==item.allowed,["accepted"]=accepted,["raw"]=raw,
                ["input"]=input,["status"]=status,["elapsedSeconds"]=Time.realtimeSinceStartup-start,["executionSubmitted"]=false});Save(output,fixture);
        }
    }

    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string output)
    {
        checks=0;cases.Clear();var fixture=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/TaskHandoff.20261016-1042.json"));
        var host=new GameObject("Handoff regression model");host.SetActive(false);var model=host.AddComponent<ChatQW>();
        model.enabled=false;model.m_PrewarmPrefixOnStart=false;model.m_Backend=ChatQW.BackendType.Local;model.m_LocalUrl="http://127.0.0.1:8080/v1/chat/completions";
        for(Type t=model.GetType();t!=null;t=t.BaseType){var f=t.GetField("url",Flags);if(f!=null){f.SetValue(model,model.m_LocalUrl);break;}}host.SetActive(true);
        var settings=(ChatSetting)Get(chat,"m_ChatSettings");var old=settings.m_ChatModel;settings.m_ChatModel=model;
        Set(chat,"m_AgentRunning",false);Set(chat,"m_EnableActivityMonitor",false);Set(chat,"m_UseStreaming",true);Set(chat,"m_IsVoiceMode",true);
        try{Runtime(chat,bridge,model,fixture);Save(output,fixture);yield return Native(chat,model,fixture,output);
            Check(cases.All(c=>(bool?)c["passed"]!=false),"One or more semantic/budget cases failed; inspect task-handoff.json.");Save(output,fixture,true);
        }finally{model.CancelActiveResponse();settings.m_ChatModel=old;UnityEngine.Object.Destroy(host);}
    }
}
