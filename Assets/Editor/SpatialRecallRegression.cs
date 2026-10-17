using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class SpatialRecallRegression
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    private static int checks;private static readonly JArray cases=new JArray();private static string directory;
    private static object Call(object o,string n,params object[] args)=>o.GetType().GetMethod(n,Flags).Invoke(o,args);
    private static object Get(object o,string n)=>o.GetType().GetField(n,Flags).GetValue(o);
    private static void Set(object o,string n,object v)=>o.GetType().GetField(n,Flags).SetValue(o,v);
    private static void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException("Spatial recall: "+why);}
    private static IEnumerator Wait(Func<bool> done,float seconds=30){float until=Time.realtimeSinceStartup+seconds;while(!done()){if(Time.realtimeSinceStartup>until)throw new TimeoutException("Recall regression callback");yield return null;}}
    private static void Save(JObject fixture,bool done=false)=>File.WriteAllText(Path.Combine(directory,"spatial-recall.json"),new JObject{
        ["completed"]=done,["passed"]=done&&cases.All(c=>(bool?)c["passed"]!=false),["checks"]=checks,["sha256"]=fixture["sha256"],
        ["dialogue"]=fixture["dialogue"].DeepClone(),["cases"]=cases}.ToString());
    private static IEnumerable<JObject> Events(JObject fixture,string kind)=>((JArray)fixture["events"]).OfType<JObject>().Where(e=>(string)e["kind"]==kind);

    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string output)
    {
        checks=0;cases.Clear();directory=output;
        var fixture=ActivityEvidencePolicy.Parse(File.ReadAllText("Assets/Editor/Fixtures/SpatialRecall.20261016-1258.json"));
        var host=new GameObject("Spatial recall native regression");host.SetActive(false);var model=host.AddComponent<ChatQW>();
        model.enabled=false;model.m_PrewarmPrefixOnStart=false;model.m_Backend=ChatQW.BackendType.Local;model.m_LocalUrl="http://127.0.0.1:8080/v1/chat/completions";
        for(Type t=model.GetType();t!=null;t=t.BaseType){var f=t.GetField("url",Flags);if(f!=null){f.SetValue(model,model.m_LocalUrl);break;}}host.SetActive(true);
        var settings=(ChatSetting)Get(chat,"m_ChatSettings");var old=settings.m_ChatModel;settings.m_ChatModel=model;
        Set(chat,"m_AgentRunning",false);Set(chat,"m_EnableActivityMonitor",false);Set(chat,"m_EnableSceneObjectAwareness",true);
        bool completed=false;
        try {
            Protocols(model,fixture);Save(fixture);
            yield return Dialogue(model,fixture);Save(fixture);
            yield return Delivery(model,fixture);Save(fixture);
            yield return SceneSpatialMemoryRegression.RunChecks(chat,bridge,output);
            cases.Add(new JObject{["name"]="actual-pixel-memory-and-self-motion",["passed"]=(string)SceneSpatialMemoryRegression.LastReport["status"]=="passed",
                ["report"]=SceneSpatialMemoryRegression.LastReport.DeepClone()});
            Check(cases.All(c=>(bool?)c["passed"]!=false),"Native semantic cases failed; inspect retained outputs.");completed=true;Save(fixture,true);
        } finally{Save(fixture,completed);model.CancelActiveResponse();settings.m_ChatModel=old;UnityEngine.Object.Destroy(host);}
    }

    private static void Protocols(ChatQW model,JObject fixture)
    {
        foreach(string time in new[]{"earlier","compare"}) {
            var q=new JObject{["referent"]="previous object",["resolvedQuestion"]="What was there before?",["timeScope"]=time,["needsEvidence"]="current-visual"};
            Check(FirstReplyProtocol.TryQuestionUnderstanding(q,out var parsed)&&(string)parsed["needsEvidence"]=="memory-or-direction","Typed past scope cannot use current-only evidence.");
            Check(FirstReplyProtocol.ResolveReviewedCompletion(null,"tools","ok",parsed,"action",false,"full-response").QuestionUnderstanding!=null,"Action route preserves historical evidence need.");
        }
        var budgets=new JArray();
        foreach(var e in Events(fixture,"LLM/FirstReplyUnadmittedFacts")) {
            string raw=e["expanded"].ToString(Formatting.None);
            string compact=SceneFactContextCompactor.CompactJson(raw,out var report);
            Check(report.roundTripVerified&&report.applied,"Actual repeated full hearing is reversibly shared.");
            Check(compact.Length<=FirstReplyProtocol.MaximumReviewInputCharacters,"Actual failed routing packet fits unchanged cap: "+compact.Length);
            budgets.Add(new JObject{["line"]=e["line"],["before"]=raw.Length,["after"]=compact.Length,["report"]=JObject.FromObject(report)});
        }
        var monitor=new JArray();
        foreach(var e in Events(fixture,"Activity/MonitorFrame")) {
            var frame=(JObject)e["data"].DeepClone();
            if(frame["facts"]?["historyAnswers"]?.Type==JTokenType.Null) {
                frame["facts"]["historyAnswers"]=new JObject{["state"]="not-provided",["records"]=new JArray()};
                frame["evidenceScopes"]["historyAnswers"]=ActivityMonitorContextPolicy.Scope("history","uncertainty");
            }
            string projected=model.PrepareActivityMonitorFrame(frame.ToString(Formatting.None),out string diagnostic);
            Check(projected!=null,"Full recorded concurrent monitoring packet fits: "+e["line"]);
            monitor.Add(new JObject{["line"]=e["line"],["budget"]=JObject.Parse(diagnostic)});
        }
        // A missing history source has unknown semantics, not 'nothing happened'.
        var bad=Events(fixture,"Activity/MonitorWire").First(e=>(int)e["line"]==74848);
        var f=(JObject)Events(fixture,"Activity/MonitorFrame").Last(e=>(int)e["line"]<74848)["data"].DeepClone();
        f["facts"]["historyAnswers"]=new JObject{["state"]="not-provided",["records"]=new JArray()};
        f["evidenceScopes"]["historyAnswers"]=ActivityMonitorContextPolicy.Scope("history","uncertainty");
        string rawOutput=(string)bad["data"]?["output"]??(string)bad["data"]?["rawVerdict"];
        if(rawOutput!=null)Check(!ActivityMonitorProtocol.TryRead(rawOutput,f,out _,out _),"Original contradictory unknown/noop is no longer accepted.");
        var unbound=(JObject)f.DeepClone();
        unbound["facts"]=new JObject{["historyAnswers"]=new JObject{["state"]="unknown"}};
        unbound["evidenceScopes"]=new JObject{["historyAnswers"]=ActivityMonitorContextPolicy.Scope("history","uncertainty")};
        unbound["speech"]=new JArray(new ActivityMonitorProtocol.SpeechUnit{Id="history-unit",Text="You never saved it earlier."}.Snapshot());
        unbound.Remove("previousAttempt");unbound.Remove("delivery");
        var unboundSchema=JObject.Parse(ActivityMonitorProtocol.SchemaForFrame(unbound));
        Check(!((JArray)unboundSchema.SelectToken("properties.perceptionSource.enum")).Values<string>().Contains("current-writer-image"),
            "Historical uncertainty cannot invent a current writer image source.");
        unbound["speech"][0]["observedAtCreation"]=new JObject{["formalObservation"]=new JObject{["formalPixelsProvided"]=true,["captureId"]="writer-image"}};
        var boundSchema=JObject.Parse(ActivityMonitorProtocol.SchemaForFrame(unbound));
        Check(((JArray)boundSchema.SelectToken("properties.perceptionSource.enum")).Values<string>().Contains("current-writer-image"),
            "Actual writer image association remains available even before monitor pixels are provided.");
        var source=new JObject{["captureRole"]="historical",["captureId"]="old",["candidates"]=new JArray(new JObject{["referenceId"]="a"})};
        var result=new JObject{["captureRole"]="historical",["captureId"]="old",["observations"]=new JArray(new JObject{["referenceId"]="a",["description"]="a small red box"}),["status"]="supported",["referenceId"]="a",["finding"]="The small red box is visible."};
        Check(SceneRecallProtocol.TryRead(result.ToString(),source,out _,out _),"Bound old-image note accepted without action authority.");
        result["captureId"]="other";Check(!SceneRecallProtocol.TryRead(result.ToString(),source,out _,out _),"Cross-capture description rejected.");
        result["captureId"]="old";result["referenceId"]="invented";Check(!SceneRecallProtocol.TryRead(result.ToString(),source,out _,out _),"Invented identity rejected.");
        var attention=new JObject{["currentCaptureId"]="now",["currentObjects"]=new JArray(Enumerable.Range(0,6).Select(i=>new JObject{
            ["entityId"]="new"+i,["sourceCaptureId"]="now",["historical"]=false,["currentOpaquePixels"]=true,["modelDescription"]="bright new object"})),
            ["historicalObjects"]=new JArray(new JObject{["entityId"]="old-box",["sourceCaptureId"]="before-walk",["historical"]=true,["modelDescription"]="old red box"})};
        foreach(var purpose in new[]{new JObject{["replyEvidenceNeed"]="memory-or-direction"},
            new JObject{["currentRequestSemantics"]=new JObject{["questionUnderstanding"]=new JObject{["needsEvidence"]="memory-or-direction"}}}}) {
            var projected=(JObject)attention.DeepClone();
            Call(model,"ApplySceneAttention",new[]{projected,purpose},1,300,true);
            Check(((JArray)projected["historicalObjects"]).Count==1&&((JArray)projected["currentObjects"]).Count==0,"New current objects cannot consume the history question's only ordinary attention slot in either reply or planning.");
        }
        cases.Add(new JObject{["name"]="recorded-budget-source-and-protocol",["passed"]=true,["routingBudgets"]=budgets,["monitorBudgets"]=monitor});
    }

    private static IEnumerator Dialogue(ChatQW model,JObject fixture)
    {
        var history=new List<LLM.SendData>();int ordinal=0;
        foreach(var row in ((JArray)fixture["dialogue"]).OfType<JObject>()) {
            string text=(string)row["text"];
            if((string)row["role"]=="assistant"){history.Add(new LLM.SendData("assistant",text));continue;}
            ordinal++;
            var context=new JObject{["humanInput"]=new JObject{["primaryUtterance"]=text},["sceneHistoryAvailability"]="Earlier RGB retained; current pose changed after walking to user. Earlier images have not yet been semantically indexed."};
            if(ordinal>=3)context["priorMovement"]=new JObject{["phase"]="arrived",["targetKind"]="user",["historyNotCurrentVisibility"]=true};
            var current=new LLM.SendData("user",text);
            string wire=(string)Call(model,"BuildFirstReplyReviewWire",current,history,context.ToString(Formatting.None),new FirstReplyCompletion());
            Check(wire!=null,"Full chronological history routing fits.");
            string route=null,status=null;JObject question=null;
            Action<string,JObject,string,bool?,string,string,JObject,string> callback=(r,q,k,s,d,t,p,f)=>{route=r;question=q;status=t;};
            yield return (IEnumerator)Call(model,"RequestFirstReplyReview",wire,(int)Get(model,"m_StreamRequestGeneration"),callback,0,false);
            bool passed=ordinal==1?route=="evidence":ordinal==2?route=="tools":ordinal==3||ordinal==4?
                route=="evidence"&&(string)question?["needsEvidence"]=="memory-or-direction":ordinal==6?route=="tools"&&(string)question?["needsEvidence"]=="memory-or-direction":route!=null;
            cases.Add(new JObject{["name"]="actual-dialogue-"+ordinal,["passed"]=passed,["line"]=row["line"],["text"]=text,["route"]=route,["question"]=question,
                ["status"]=status,["priorPlayedChunks"]=history.Count});history.Add(current);Save(fixture);
        }
        foreach(string question in new[]{"What was beside the blue painting before you walked over here?","さっき向きを変える前、棚の上に何が見えた？","之前看到的红色盒子和现在的是同一个吗？"}) {
            string wire=(string)Call(model,"BuildFirstReplyReviewWire",new LLM.SendData("user",question),history,new JObject{["humanInput"]=new JObject{["primaryUtterance"]=question}}.ToString(),new FirstReplyCompletion());
            string route=null;JObject interpretation=null;
            Action<string,JObject,string,bool?,string,string,JObject,string> callback=(r,q,k,s,d,t,p,f)=>{route=r;interpretation=q;};
            yield return (IEnumerator)Call(model,"RequestFirstReplyReview",wire,(int)Get(model,"m_StreamRequestGeneration"),callback,0,false);
            cases.Add(new JObject{["name"]="nondevice-history-"+question,["passed"]=route=="evidence"&&(string)interpretation?["needsEvidence"]=="memory-or-direction",["question"]=interpretation});Save(fixture);
        }
    }

    private static IEnumerator Delivery(ChatQW model,JObject fixture)
    {
        var actual=(JObject)Events(fixture,"Activity/MonitorFrame").Last(e=>e["data"]?["delivery"] is JObject)["data"].DeepClone();
        foreach(string text in new[]{(string)actual["delivery"]["completedPlaybackText"],"I'll check it carefully and then continue.","我会确认保存是否成功，然后继续。","保存失败了，文件没有写入。"}) {
            var frame=(JObject)actual.DeepClone();var delivery=(JObject)frame["delivery"];
            bool save=text.Contains("保存"),reported=text.StartsWith("保存失败",StringComparison.Ordinal);
            if(save) {
                delivery["status"]="failed";delivery["language"]="zh";
                delivery["facts"]=new JObject{["id"]="s1",["kind"]="save",["outcome"]=new JObject{
                    ["actionId"]="save-request-1",["intent"]="save",["entityId"]="document-a",["phase"]="failed",["reason"]="permission denied; no file written"}};
                delivery["taskContext"]=new JObject{["parentRequest"]="保存修改后的文档，然后告诉我结果。",["step"]=new JObject{["id"]="s1",["kind"]="save",["quote"]="保存修改后的文档"}};
                frame["evidenceScopes"]=new JObject{["result:"+(string)delivery["id"]]=ActivityMonitorContextPolicy.Scope("execution","mistake","uncertainty")};
            }
            delivery["completedPlaybackText"]=text;delivery["playbackRevision"]=ActivityEvidencePolicy.Stamp(new JValue(text));delivery["requiresReportAct"]=true;
            string raw=null,status=null,rejection=null,budget=null;JObject decision=null;bool accepted=false;
            var attempts=new ActivityMonitorAttempts();var outputs=new JArray();string key=ActivityEvidencePolicy.SemanticKey(frame);
            for(int attempt=0;attempt<3&&!accepted;attempt++) {
                var feedback=attempts.Feedback(key);if(feedback!=null)frame["previousAttempt"]=feedback;
                string input=model.PrepareActivityMonitorFrame(frame.ToString(),out budget);Check(input!=null,"Delivery complete repair wire fits.");
                bool done=false,ok=false;
                model.PostActivityMonitor(input,(a,b,c)=>{ok=a;raw=b;status=c;done=true;});yield return Wait(()=>done);
                accepted=ok&&ActivityMonitorProtocol.TryRead(raw,JObject.Parse(input),out decision,out rejection);
                outputs.Add(new JObject{["raw"]=raw,["status"]=status,["rejection"]=rejection});
                if(!accepted)attempts.Failed(key,Time.realtimeSinceStartup,rejection??status,raw,false);
            }
            bool passed=accepted&&(string)decision["delivery"]?["operation"]==(reported?"covered":"supplement");
            cases.Add(new JObject{["name"]="delivery-tense-"+text,["passed"]=passed,["raw"]=raw,["attempts"]=outputs,["status"]=status,["rejection"]=rejection,["budget"]=JObject.Parse(budget)});Save(fixture);
        }
    }

    public static IEnumerator OrdinaryViewRecall(ChatSample chat,ArdyRoomDialogueBridge bridge,NevaSceneVisionCapture capture,
        NevaSceneAttention attention,SceneVisionObservation original,Renderer target)
    {
        var avatar=bridge.avatar.transform;var position=avatar.position;var rotation=avatar.rotation;
        var targetPosition=target.transform.position;int generation=(int)Get(chat,"m_FormalResponseGeneration");
        bool running=(bool)Get(chat,"m_AgentRunning");
        try {
            // No RememberSceneObject, look-at, matcher or device operation has run.
            Call(chat,"ObserveKnownSceneObjects",original,"ordinary-formal-image");Call(chat,"RememberSceneDialogueView",original);
            string question="刚才转身前看到的三个橙色方块中，上方较小的那个还记得吗？只回忆，不要行动。";
            Call(chat,"RecordAcceptedMotionUserTurn",question);
            avatar.SetPositionAndRotation(position+rotation*new Vector3(.25f,0,0),Quaternion.AngleAxis(75,Vector3.up)*rotation);
            yield return null;yield return null;
            SceneVisionObservation current=null;
            Call(chat,"RequestSceneObservation","ordinary-recall-moved",new Action<SceneVisionObservation>(v=>current=v));yield return Wait(()=>current!=null,6);
            bool done=false;
            Check((bool)Call(chat,"TryBeginSceneRecall",current,new JObject{["needsEvidence"]="memory-or-direction",["timeScope"]="earlier",
                ["referent"]="上方较小的橙色方块",["resolvedQuestion"]="回忆转身前画面中三个橙色方块里上方较小的一个。"},generation,new Action(()=>done=true)),"History question enters bounded recall.");
            yield return Wait(()=>done);
            current=null;Call(chat,"RequestSceneObservation","ordinary-recall-current-pose",new Action<SceneVisionObservation>(v=>current=v));yield return Wait(()=>current!=null,6);
            var facts=(JObject)Call(chat,"ReadSceneRecallFacts",current);
            File.WriteAllText(Path.Combine(directory,"ordinary-recall-facts.json"),facts.ToString());
            var recall=((JArray)facts["records"]).OfType<JObject>().FirstOrDefault(r=>(string)r["sourceCaptureId"]==original.CaptureId&&(string)r["assessment"]=="supported");
            Check(recall!=null,"Actual old pixels recall an ordinary unselected object.");
            string targetId=target.GetComponent<SceneObjectIdentity>().EntityId;
            Check((string)recall["entityId"]==targetId,"Relational recall selects the small upper panel, not another landmark.");
            Check((bool?)recall["positionMemoryValid"]==true&&recall["currentlyVisible"].Type==JTokenType.Null&&recall["noticedAtCapture"].Type==JTokenType.Null,
                "Source-bound recognition is not current visibility or prior conscious attention.");
            Check(!((JArray)((JObject)Call(chat,"BuildSceneObjectFacts"))["knownObjectHistory"]).OfType<JObject>().Any(r=>(string)r["entityId"]==targetId),"Recall description grants no selected/confirmed entity authority.");
            string reference=(string)recall["referenceId"];
            var args=new object[]{reference,current,true,0f,0f,null};
            Check((bool)Call(chat,"ResolveSceneRecallOrientation",args),"Reorientation resolves a current body bearing to frozen pixels.");
            var direction=(JObject)args[5];float yaw=(float)args[3];
            Vector3 offset=targetPosition-current.BodyPosition;
            float expected=Vector3.SignedAngle(Vector3.ProjectOnPlane(current.BodyRotation*Vector3.forward,Vector3.up),Vector3.ProjectOnPlane(offset,Vector3.up),Vector3.up);
            // Surface point may be centimetres from panel center. Compare against
            // the exact sampled point's stored bearing rather than object pivots.
            Check(Mathf.Abs(Mathf.DeltaAngle(yaw,(float)recall["bearing"]["relativeToCurrentBody"]["yawDegrees"]))<.02f&&Mathf.Abs(Mathf.DeltaAngle(yaw,expected))<6f,
                "Body angle uses translated current body, not a stale screen direction.");
            Check(attention.TryOrientView("spatial-memory-view-turn",-25,0,out var reason),"Actual head-only movement: "+reason);
            yield return Wait(()=>attention.IsSettled,6);
            SceneVisionObservation headed=null;Call(chat,"RequestSceneObservation","recall-head-pose",new Action<SceneVisionObservation>(v=>headed=v));yield return Wait(()=>headed!=null,6);
            var headedFacts=(JObject)Call(chat,"ReadSceneRecallFacts",headed);var h=((JArray)headedFacts["records"]).OfType<JObject>().First(r=>(string)r["referenceId"]==reference);
            Check(Mathf.Abs(Mathf.DeltaAngle((float)h["bearing"]["relativeToCurrentBody"]["yawDegrees"],yaw))<3f,
                "Head motion does not rotate the body's remembered bearing.");
            Check(Mathf.Abs(Mathf.DeltaAngle((float)h["bearing"]["relativeToCurrentView"]["yawDegreesRight"],(float)h["bearing"]["relativeToCurrentBody"]["yawDegrees"]))>10f,
                "Camera and body frames remain distinct after real head motion.");
            // Exercise actual reorientation and post-turn verification, not only
            // arithmetic. Restore body heading but retain the translated origin.
            attention.Release("spatial-memory-view-turn","prepare actual reorientation");yield return Wait(()=>!attention.IsBusy,6);
            avatar.rotation=rotation;yield return null;yield return null;
            const string turnQuestion="回头确认刚才三个橙色方块中上方较小的那个；只转头，不移动身体。";
            Call(chat,"RecordAcceptedMotionUserTurn",turnQuestion);
            SceneVisionObservation beforeTurn=null;Call(chat,"RequestSceneObservation","recall-before-return",new Action<SceneVisionObservation>(v=>beforeTurn=v));yield return Wait(()=>beforeTurn!=null,6);
            var planner=((ChatSetting)Get(chat,"m_ChatSettings")).m_ChatModel;
            string planningInput=(string)Call(chat,"BuildRoomDecisionInputWithObservation",turnQuestion,false,turnQuestion,bridge,beforeTurn);
            File.WriteAllText(Path.Combine(directory,"recall-planning-input.json"),planningInput);
            var oldImages=((IEnumerable<SceneVisionObservation>)Call(chat,"GetScenePlanningHistoryImages",beforeTurn))
                .Select(v=>new LLM.RoomObservationImage(v.CaptureId,v.MetadataJson,v.ImageDataUrl)).ToList();
            bool planned=false,planOk=false;string planOutput=null,planStatus=null;
            planner.PostRoomTaskMessageWithObservationHistory(planningInput,beforeTurn.ImageDataUrl,oldImages,(ok,json,status)=>{
                planOk=ok;planOutput=json;planStatus=status;planned=true;});yield return Wait(()=>planned);
            File.WriteAllText(Path.Combine(directory,"recall-planning-output.json"),new JObject{["ok"]=planOk,["status"]=planStatus,["output"]=planOutput}.ToString());
            RoomTaskProtocol.Decision choice=null;
            Check(planOk&&RoomTaskProtocol.TryDecision(RoomTaskSequenceProtocol.DecisionOnly(planOutput),turnQuestion,false,out choice)&&choice.intent=="orient-view"&&choice.viewChange.historyReferenceId==reference,
                "Native full planner chooses the remembered object point and respects head-only movement.");
            Check(RoomTaskSequenceProtocol.TryPlan(planOutput,turnQuestion,out var plannedSteps,out _),"Native plan covers the entire request and constraint.");
            foreach(var step in plannedSteps.OfType<JObject>())step["status"]="pending";
            Check(RoomTaskSequenceProtocol.TrySelect(planOutput,choice,plannedSteps,out _,out _),"Native plan selects its ready observation step.");
            // The planner may consume several seconds; get a fresh pose while
            // preserving its original historical target selection.
            beforeTurn=null;Call(chat,"RequestSceneObservation","recall-after-native-planning",new Action<SceneVisionObservation>(v=>beforeTurn=v));yield return Wait(()=>beforeTurn!=null,6);
            choice.viewChange.captureId=beforeTurn.CaptureId;
            bool turned=false;Call(chat,"BeginSceneViewChange",choice,beforeTurn,generation,new Action(()=>turned=true));yield return Wait(()=>turned,22);
            var action=(JObject)JObject.Parse(chat.SceneActionContextJson)["lastAction"];
            File.WriteAllText(Path.Combine(directory,"actual-reorientation.json"),action.ToString());
            Check((string)action["phase"]=="observed"&&(string)action["viewVerification"]=="supported"&&
                (string)action["resultCaptureId"]!=beforeTurn.CaptureId&&(float)action["actualViewChangeDegrees"]>1f,
                "Actual head turn uses recalled point, obtains a new frame, and separately confirms the target.");
            Call(chat,"RecordAcceptedMotionUserTurn","请回头看看刚才的方向有没有紫色大象玩偶，只转头确认。");
            SceneVisionObservation beforeMissing=null;Call(chat,"RequestSceneObservation","recall-before-missing",new Action<SceneVisionObservation>(v=>beforeMissing=v));yield return Wait(()=>beforeMissing!=null,6);
            bool missingTurn=false;Call(chat,"BeginSceneViewChange",new RoomTaskProtocol.Decision{intent="orient-view",origin="user",
                viewChange=new RoomTaskProtocol.ViewChange{captureId=beforeMissing.CaptureId,yaw=1,pitch=0,historyReferenceId=original.CaptureId}},
                beforeMissing,generation,new Action(()=>missingTurn=true));yield return Wait(()=>missingTurn,22);
            var missingAction=(JObject)JObject.Parse(chat.SceneActionContextJson)["lastAction"];
            Check((string)missingAction["phase"]=="observed-target-unconfirmed"&&(string)missingAction["resultCaptureId"]!=beforeMissing.CaptureId,
                "Completed turn and new image cannot complete finding an unconfirmed target.");
            target.transform.position+=Vector3.right*.1f;
            var moved=(JObject)Call(chat,"ReadSceneRecallFacts",headed);
            Check(!((JArray)moved["records"]).OfType<JObject>().Any(r=>(string)r["referenceId"]==reference&&(bool?)r["positionMemoryValid"]==true),"Hidden geometry movement revokes old position without supplying a new one.");
            target.transform.position=targetPosition;
            Check(!(bool)Call(chat,"ResolveSceneRecallOrientation",new object[]{reference,headed,true,0f,0f,null}),"Returning a moved transform cannot resurrect an invalidated old point.");
            var room=bridge.environmentRoot;bridge.environmentRoot=null;
            try{Check(((JArray)((JObject)Call(chat,"ReadSceneRecallFacts",headed))["records"]).Count==0,"A different room/owner cannot read recalled sources.");}
            finally{bridge.environmentRoot=room;}
            Check(!(bool)Call(chat,"TryBeginSceneRecall",headed,new JObject{["needsEvidence"]="memory-or-direction"},generation,new Action(()=>{})),"Same generation cannot repeat the recall round.");
            // Replace a request while its native image call is in flight. An old
            // coroutine must neither publish its callback nor cancel the new one.
            const string aliasQuestion="Do you remember that small orange tile from earlier?";
            Call(chat,"RecordAcceptedMotionUserTurn",aliasQuestion);
            var aliasUnderstanding=new JObject{["needsEvidence"]="memory-or-direction",["referent"]="small upper orange square",
                ["resolvedQuestion"]="Recall the smaller orange square above the other two before turning.",["timeScope"]="earlier"};
            Set(chat,"m_FormalResponseGeneration",generation+1);bool oldDone=false,newDone=false;
            Call(chat,"TryBeginSceneRecall",headed,aliasUnderstanding,generation+1,new Action(()=>oldDone=true));
            Set(chat,"m_FormalResponseGeneration",generation+2);
            Call(chat,"TryBeginSceneRecall",headed,aliasUnderstanding,generation+2,new Action(()=>newDone=true));
            yield return Wait(()=>newDone);Check(!oldDone,"Cancelled old native recall cannot resume a replaced reply.");
            var alias=(JObject)Call(chat,"ReadSceneRecallFacts",headed);
            var aliasRecord=((JArray)alias["records"]).FirstOrDefault(r=>(string)r["question"]==aliasQuestion&&
                (string)r["sourceCaptureId"]==original.CaptureId&&(string)r["entityId"]==targetId&&(string)r["assessment"]=="supported");
            Check(aliasRecord!=null,"English alias and resolved referent select the same observed entity after request replacement.");
            Check((bool?)aliasRecord["positionMemoryValid"]==false,"Reinterpreting old pixels cannot revive a point revoked after object motion.");
            // Reuse the complete production monitor frames with actual new recall
            // facts; admission includes their full final schema and reserves.
            var evidenceArgs=new object[]{Call(chat,"ActivityAdapters"),null};
            var recalledFacts=(JObject)Call(chat,"ReadActivityFacts",evidenceArgs);
            var scopes=(JObject)Call(chat,"ActivityEvidenceScopes",recalledFacts);
            var native=(ChatQW)((ChatSetting)Get(chat,"m_ChatSettings")).m_ChatModel;
            var fixture=ActivityEvidencePolicy.Parse(File.ReadAllText("Assets/Editor/Fixtures/SpatialRecall.20261016-1258.json"));
            foreach(var e in Events(fixture,"Activity/MonitorFrame")) {
                var merged=(JObject)e["data"].DeepClone();
                foreach(var field in recalledFacts.Properties().Where(p=>p.Name.StartsWith("recalled:",StringComparison.Ordinal))) {
                    merged["facts"][field.Name]=field.Value.DeepClone();merged["evidenceScopes"][field.Name]=scopes[field.Name].DeepClone();
                }
                merged=ActivityEvidencePolicy.Focus(merged);
                string wire=native.PrepareActivityMonitorFrame(merged.ToString(),out var mergedBudget);
                if(wire==null)File.WriteAllText(Path.Combine(directory,"merged-recall-budget-failure.json"),new JObject{["line"]=e["line"],["frame"]=merged,["budget"]=JObject.Parse(mergedBudget)}.ToString());
                Check(wire!=null,"Actual concurrent monitor still fits after native historical recall: "+e["line"]);
            }
            Call(chat,"RecordAcceptedMotionUserTurn","前の画像に紫色の象のぬいぐるみが見えましたか？記憶だけ答えて。移動しないで。");
            Set(chat,"m_FormalResponseGeneration",generation+3);bool unseenDone=false;
            Call(chat,"TryBeginSceneRecall",headed,new JObject{["needsEvidence"]="memory-or-direction"},generation+3,new Action(()=>unseenDone=true));
            yield return Wait(()=>unseenDone);
            var unseen=(JObject)Call(chat,"ReadSceneRecallFacts",headed);
            var unseenRecords=((JArray)unseen["records"]).Where(r=>((string)r["question"]).Contains("紫色")).ToArray();
            Check(unseenRecords.Length>0&&unseenRecords.All(r=>(string)r["assessment"]!="supported"),"Unseen object remains unconfirmed; old pixels do not fabricate an entity or absence everywhere.");
            cases.Add(new JObject{["name"]="ordinary-view-unselected-object-relational-recall-self-motion",["passed"]=true,["facts"]=facts,["reoriented"]=direction,["headPoseFacts"]=headedFacts});
            cases.Add(new JObject{["name"]="native-request-replacement-alias-and-unseen-object",["passed"]=true,["alias"]=alias,["unseen"]=unseen});
        }finally{
            attention.Release("spatial-memory-view-turn","recall fixture completed");target.transform.position=targetPosition;avatar.SetPositionAndRotation(position,rotation);Set(chat,"m_AgentRunning",running);Set(chat,"m_FormalResponseGeneration",generation);
        }
        yield return Wait(()=>!attention.IsBusy,6);
    }
}
