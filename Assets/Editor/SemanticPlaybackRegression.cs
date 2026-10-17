using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class SemanticPlaybackRegression
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static JObject fixture;static string output;static readonly JArray cases=new JArray();
    static object Call(object o,string method,params object[] args)=>o.GetType().GetMethod(method,Flags).Invoke(o,args);
    static object Get(object o,string field)=>o.GetType().GetField(field,Flags).GetValue(o);
    static void Set(object o,string field,object value)=>o.GetType().GetField(field,Flags).SetValue(o,value);
    static IEnumerable<JObject> Events(string tag)=>((JArray)fixture["events"]).OfType<JObject>().Where(e=>(string)e["tag"]==tag);
    static JArray History(int line)=>new JArray(((JArray)fixture["history"]).Where(r=>(int)r["line"]<line).Select(r=>r.DeepClone()));
    static void Save(bool done=false)=>File.WriteAllText(Path.Combine(output,"semantic-playback.json"),new JObject{
        ["completed"]=done,["passed"]=done&&cases.All(c=>(bool)c["passed"]),["source"]=fixture["source"],
        ["history"]=fixture["history"],["limitations"]=fixture["limitations"],["cases"]=cases}.ToString());
    static void Check(string name,bool passed,JToken evidence=null){cases.Add(new JObject{["name"]=name,["passed"]=passed,["evidence"]=evidence});Save();}
    static IEnumerator Until(Func<bool> done,float seconds=12){float until=Time.realtimeSinceStartup+seconds;while(!done()&&Time.realtimeSinceStartup<until)yield return null;}

    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string directory)
    {
        output=directory;cases.Clear();fixture=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/SemanticPlayback.20261016-2323.json"));
        var host=new GameObject("Semantic playback regression");host.SetActive(false);
        var model=host.AddComponent<ChatQW>();model.enabled=false;model.m_PrewarmPrefixOnStart=false;
        model.m_Backend=ChatQW.BackendType.Local;model.m_LocalUrl="http://127.0.0.1:8080/v1/chat/completions";Set(model,"url",model.m_LocalUrl);host.SetActive(true);
        Set(chat,"m_AgentRunning",false);Set(chat,"m_EnableActivityMonitor",true);
        Set(chat,"m_ActivityNextPoll",Time.realtimeSinceStartup+600);
        try {
            Check("five-actual-user-inputs",((JArray)fixture["history"]).Count(r=>(string)r["role"]=="user")==5);
            Check("all-actual-audio-completions",((JArray)fixture["history"]).Count(r=>(string)r["role"]=="assistant")==22);
            QueueLifecycle(chat);AudioEvidence(chat);Planning(model);Budgets(model);
            Check("existing-dialogue-semantics",DialogueSemanticsRegression.RunChecks()>0);
            foreach(var e in Events("Speech/StepBinding")) {
                var d=(JObject)e["data"];var input=StepSpeechPartitionProtocol.Input("Full recorded dialogue follows",(string)d["originalSpeech"],(JObject)d["step"]);
                input["recordedDialogue"]=History((int)e["line"]);
                Check("original-bad-selection-rejected-"+e["line"],!StepSpeechPartitionProtocol.Resolve(input,(string)d["raw"],out _,out _));
                yield return Partition(model,input,"recorded-"+e["line"],(int)d["generation"]==7?"小灯はちゃんと消したし":"照明は正常に点灯しています。",(int)d["generation"]==7?"戻ってきたところ":null);
            }
            foreach(var row in new[]{
                new[]{"灯已经关好了，现在正回到你身边。放心吧。","approach","USER","灯已经关好了","正回到你身边"},
                new[]{"I found the document; I am saving it now. Thanks for waiting.","save","document-a","I found the document","I am saving it now"},
                new[]{"画像は生成できたし、今から渡すね。気に入ってくれると嬉しい。","deliver","artifact-a","画像は生成できたし","今から渡すね"},
                new[]{"観察は終わったわ。これから質問に答えるね。","answer","question-a","観察は終わったわ","これから質問に答えるね"},
                new[]{"文档已保存，正在上传同一份文档。","upload","document-a","文档已保存","正在上传同一份文档"},
                new[]{"I edited the file while you saved your own copy.","save","document-a","I edited the file while you saved your own copy.",null},
                new[]{"我想以后给你唱首歌，你觉得怎么样？","sing","song-a","我想以后给你唱首歌",null}}) {
                var input=StepSpeechPartitionProtocol.Input("Preserve independent results and reasonable personal expression.",row[0],new JObject{
                    ["intent"]=row[1],["target"]=row[2],["actor"]="CHARACTER"});
                input["recordedDialogue"]=History(int.MaxValue);
                yield return Partition(model,input,"general-"+row[1],row[3],row[4]);
            }
            foreach(var e in Events("Agent/WorkReviewRejected"))yield return WorkReview(chat,model,e);
            foreach(var row in new[]{
                new[]{"保存文档后上传它。","save","upload","文档已保存。","zh"},
                new[]{"画像を生成して、送信して。","generate","deliver","画像は生成できたわ。","ja"},
                new[]{"Inspect the document and answer my question.","inspect","answer","I inspected the document.","en"}})
                yield return GeneralDelivery(chat,model,row);
            yield return Language(chat,model);
            yield return Visual(model);
            Save(true);if(cases.Any(c=>!(bool)c["passed"]))throw new InvalidOperationException("Semantic playback replay failed; inspect semantic-playback.json.");
        }finally{model.CancelActiveResponse();model.CancelActivityMonitor();UnityEngine.Object.Destroy(host);}
    }
    static IEnumerator Partition(ChatQW model,JObject input,string name,string keep,string remove)
    {
        bool done=false,ok=false;string raw=null,status=null;
        model.PostRoomPreparationMessage(input.ToString(Formatting.None),(a,b,c)=>{done=true;ok=a;raw=b;status=c;});yield return Until(()=>done);
        string prefix=null,reason=null;bool parsed=ok&&StepSpeechPartitionProtocol.Resolve(input,raw,out prefix,out reason);
        Check("native-partition-"+name,parsed&&prefix.Contains(keep)&&!prefix.Contains("、。")&&(remove==null||!prefix.Contains(remove)),new JObject{
            ["input"]=input,["raw"]=raw,["status"]=status,["reason"]=reason,["prefix"]=prefix});
    }
    static void QueueLifecycle(ChatSample chat)
    {
        var leaseType=typeof(ChatSample).GetNestedType("RoomSpeechPlaybackLease",Flags);
        var chunkType=typeof(ChatSample).GetNestedType("PendingSpeechChunk",Flags);
        var clipType=typeof(ChatSample).GetNestedType("PendingSpeechClip",Flags);
        var lease=Activator.CreateInstance(leaseType,true);Set(lease,"roundId",(int)Get(chat,"m_CurrentSpeechRoundId"));
        var units=(List<ActivityMonitorProtocol.SpeechUnit>)Get(chat,"m_ActivitySpeech");
        var pending=new ActivityMonitorProtocol.SpeechUnit{Id="speech:11:18",Text="歩く動作を準備中で、まだ歩き始めていないの。",Language="ja"};
        var independent=new ActivityMonitorProtocol.SpeechUnit{Id="independent",Text="照明は正常に点灯しています。",Language="ja"};
        var played=new ActivityMonitorProtocol.SpeechUnit{Id="completed",Text="The document was saved.",State="completed"};
        units.AddRange(new[]{pending,independent,played});
        object Chunk(ActivityMonitorProtocol.SpeechUnit unit,bool prefix){var c=Activator.CreateInstance(chunkType,true);Set(c,"ProcessUnit",unit);Set(c,"RoomState",lease);Set(c,"RoomPrefixOnly",prefix);Set(c,"Text",unit.Text);return c;}
        Call(Get(chat,"m_PendingChunks"),"Enqueue",Chunk(pending,false));Call(Get(chat,"m_PendingChunks"),"Enqueue",Chunk(independent,true));
        var clip=Activator.CreateInstance(clipType,true);Set(clip,"RoomState",lease);Set(clip,"ProcessUnit",pending);Call(Get(chat,"m_PendingClips"),"Enqueue",clip);
        Call(chat,"RemoveQueuedRoomSpeech",lease,true);
        Check("removed-queued-and-prefetched-unit-retired-once",pending.State=="superseded"&&pending.Version==1);
        Check("independent-prefix-and-played-history-kept",independent.State=="queued"&&played.State=="completed");
        var stale=new JObject{["id"]=pending.Id,["version"]=0,["replacement"]="Still preparing.",["correction"]="Still preparing."};
        Check("late-monitor-cannot-revive-removed-audio",ActivityMonitorProtocol.Revise(pending,stale,out _)=="stale");
        Check("removed-unit-cannot-return-to-playback",Call(chat,"RefreshActivitySpeech",Chunk(pending,false)).ToString()=="Drop");
        Call(chat,"ClearPendingSpeechChunks","replacement-test");Call(chat,"ClearPendingSpeechClips","replacement-test");
        Check("all-clear-path-retires-unplayed-prefix",independent.State=="superseded");
        Call(chat,"RetireUnplayedActivitySpeech",played,"do-not-erase-history");Check("completed-history-not-retired",played.State=="completed");
    }
    static void AudioEvidence(ChatSample chat)
    {
        Call(chat,"EnsureWorkDeliveryOwner");
        var ledger=(Dictionary<string,JObject>)Get(chat,"m_WorkToolResultReceipts");
        var saved=WorkResultDeliveryProtocol.Result("test-save","completed",new JObject{["operation"]="save",["entityId"]="document-a"});
        var upload=WorkResultDeliveryProtocol.Result("test-upload","failed",new JObject{["operation"]="upload",["entityId"]="document-a"});
        string a=(string)saved["id"],b=(string)upload["id"];ledger[a]=saved;ledger[b]=upload;
        string text="The document was saved.";
        var unit=new ActivityMonitorProtocol.SpeechUnit{Id="actual-audio-probe",Version=2,State="completed",Text=text,StartedAtRealtime=Time.realtimeSinceStartup-1};
        Call(chat,"RecordWorkCompletedSpeechForResult",text,a);Call(chat,"RecordWorkCompletedAudio",text,a,unit,1);
        JObject Result(string id)=>((JArray)((JObject)Call(chat,"ReadWorkDelivery"))["results"]).OfType<JObject>().Single(r=>(string)r["id"]==id);
        var first=Result(a);var second=Result(b);
        Check("per-result-audio-identity-time-and-version",(string)first.SelectToken("completedAudio[0].audioId")==unit.Id&&
            (int?)first.SelectToken("completedAudio[0].speechVersion")==1&&(float)first.SelectToken("completedAudio[0].completedAtRealtime")>=unit.StartedAtRealtime,first);
        Check("another-result-cannot-borrow-supplement",(string)second["completedPlaybackText"]==""&&second["completedAudio"]==null,second);
        string revision=(string)first["playbackRevision"];
        Call(chat,"RecordWorkCompletedSpeechForResult","The upload failed.",b);
        Check("other-result-report-keeps-earlier-evidence",(string)Result(a)["playbackRevision"]==revision);
        var newer=WorkResultDeliveryProtocol.Result("test-save","failed",new JObject{["operation"]="save",["entityId"]="document-a",["attempt"]=2});
        ledger[(string)newer["id"]]=newer;
        Check("new-operation-same-entity-has-no-old-audio",(string)Result((string)newer["id"])["completedPlaybackText"]=="");
        ledger.Clear();
    }

    static void Budgets(ChatQW model)
    {
        foreach(var e in Events("Activity/MonitorFrame")) {
            var original=(JObject)e["data"].DeepClone();int index=0;
            foreach(var frame in (string)original["purpose"]=="speech"?ActivityEvidencePolicy.SpeechOperations(original):new[]{original}) {
                var focused=ActivityEvidencePolicy.Focus(frame);string input=model.PrepareActivityMonitorFrame(focused.ToString(Formatting.None),out var diag);
                Check("actual-final-monitor-budget-"+e["line"]+"-"+index++,input!=null,JObject.Parse(diag));
            }
            if(original["delivery"] is JObject delivery) {
                var enriched=(JObject)original.DeepClone();
                enriched["delivery"]["completedAudio"]=new JArray(new JObject{["audioId"]="recorded-audio-metadata-probe",["speechVersion"]=2,["generation"]=original["owner"]["generation"],
                    ["textStart"]=0,["textLength"]=((string)delivery["completedPlaybackText"]??"").Length,["startedAtRealtime"]=220f,["completedAtRealtime"]=230f,["wholeTextPlayed"]=true});
                string added=model.PrepareActivityMonitorFrame(ActivityEvidencePolicy.Focus(enriched).ToString(Formatting.None),out var metadataBudget);
                Check("new-audio-metadata-final-budget-"+e["line"],added!=null,JObject.Parse(metadataBudget));
            }
            Check("canonical-frame-kept-"+e["line"],JToken.DeepEquals(original,e["data"]));
        }
        foreach(var e in Events("Activity/MonitorFrame").Where(e=>e["data"]["delivery"] is JObject)) {
            var d=(JObject)e["data"]["delivery"];var proof=ActivityEvidencePolicy.PlaybackProofSchema(d);
            if(string.IsNullOrEmpty((string)d["completedPlaybackText"]))Check("empty-playback-no-invented-quote-"+e["line"],proof.SelectToken("properties.lastRelevantQuote.enum")?.ToString()=="[\n  \"\"\n]" || ((JArray)proof.SelectToken("properties.lastRelevantQuote.enum"))?.Count==1);
        }
    }
    static void Planning(ChatQW model)
    {
        var observed=(JObject)Events("SceneSearch/Input").Last()["data"];
        var candidates=(JArray)observed["candidates"].DeepClone();
        var packet=new JObject{["currentUserText"]=((JArray)fixture["history"]).Last(r=>(string)r["role"]=="user")["text"],
            ["visibleObjectCatalog"]=new JObject{["current"]=new JObject{["candidates"]=candidates}},
            ["sceneSpatialMemory"]=new JObject{["objects"]=candidates.DeepClone()},["unknownConstraint"]="Preserve the complete original and independent image checks."};
        var before=packet.DeepClone();
        Func<JObject,bool> protect=r=>(bool)typeof(ChatQW).GetMethod("RoomDecisionUnknownObservationFields",Flags).Invoke(null,new object[]{r});
        var projection=RoomTaskContextPolicy.PlanningObservationView(packet,protect);
        Check("reconstructed-real-observation-sharing",projection["planningObservationSharing"] is JArray rows&&rows.Count>0,new JObject{
            ["beforeCharacters"]=packet.ToString(Formatting.None).Length,["afterCharacters"]=projection.ToString(Formatting.None).Length,["sharing"]=projection["planningObservationSharing"],
            ["scope"]="Recorded real candidate rows in a reconstructed repeated planning packet; historical full planning wire unavailable."});
        Check("runtime-memory-and-full-request-not-mutated",JToken.DeepEquals(packet,before)&&(string)projection["currentUserText"]==(string)packet["currentUserText"]);
        foreach(var row in (projection["planningObservationSharing"] as JArray??new JArray()).OfType<JObject>()) {
            var compact=(JObject)projection.SelectToken((string)row["path"]);var source=(JObject)projection.SelectToken((string)row["source"]);
            var restored=(JObject)compact.DeepClone();restored.Remove("observationFieldsFrom");restored.Remove("inheritedObservationFields");
            foreach(string key in compact["inheritedObservationFields"].Values<string>())restored[key]=source[key].DeepClone();
            Check("exact-observation-restored-"+row["path"],ActivityEvidencePolicy.Equal(restored,packet.SelectToken((string)row["path"])));
        }
        Check("planning-projection-idempotent",ActivityEvidencePolicy.Equal(projection,RoomTaskContextPolicy.PlanningObservationView(projection,protect)));
        string wire=(string)Call(model,"BuildRoomTaskRequestJsonWithImage",packet.ToString(Formatting.None),"data:image/png;base64,AA==",false);
        var budget=(JObject)typeof(ChatQW).GetMethod("MeasureRoomDecisionBudget",Flags).Invoke(null,new object[]{wire,(int)typeof(ChatQW).GetField("RoomDecisionBudget",Flags).GetRawConstantValue()});
        Check("reconstructed-planning-complete-wire-budget",(bool?)budget["allowed"]==true,budget);
        var protectedProjection=RoomTaskContextPolicy.PlanningObservationView(packet,row=>true);
        Check("authored-constraint-records-remain-whole",JToken.DeepEquals(packet["sceneSpatialMemory"],protectedProjection["sceneSpatialMemory"]));
    }
    static IEnumerator WorkReview(ChatSample chat,ChatQW model,JObject rejected)
    {
        int line=(int)rejected["line"];var bad=JObject.Parse((string)rejected["data"]["rawDecision"]);
        // Frozen request was not logged. Match the nearest recorded result frame
        // and reconstruct each version's audio only from preceding completions.
        var frame=(JObject)Events("Activity/MonitorFrame").Where(e=>e["data"]["delivery"] is JObject)
            .OrderBy(e=>Math.Abs((int)e["line"]-line)).First()["data"].DeepClone();
        var results=new JArray();
        foreach(JObject report in bad["delivery_reports"]) {
            string id=(string)report["id"];
            var match=Events("Activity/MonitorFrame").Where(e=>(string)e["data"]["delivery"]?["id"]==id).OrderBy(e=>Math.Abs((int)e["line"]-line)).FirstOrDefault();
            JObject result=match==null?null:(JObject)match["data"]["delivery"].DeepClone();
            if(result==null)continue;
            result["delivered"]=false;result["playbackReviewPending"]=false;
            // Earliest work check preceded its first delivery frame; their audio
            // revision remains the logged one. Later frame mismatches are explicit.
            results.Add(result);
        }
        Check("original-work-bad-quote-rejected-"+line,results.Count==((JArray)bad["delivery_reports"]).Count&&!WorkResultDeliveryProtocol.ValidateCoverage(results,bad,out _));
        var runtime=new JObject{["request"]=frame["facts"]["request"],["execution"]=frame["facts"]["execution"],
            ["delivery"]=new JObject{["results"]=results},["pendingActions"]=new JArray(),["scope"]="Every recorded result here has completed execution; missing audio reporting never requests executing it again."};
        string prompt="[Runtime work review domain: general]\n[Recorded full dialogue and real playback order]\n"+History(line).ToString(Formatting.None)+
            "\n[Task progress runtime]\n"+runtime.ToString(Formatting.None)+"\n[Review instruction]\n"+WorkNextIntentProtocol.Contract+WorkResultDeliveryProtocol.Contract+
            "\nThe previous output was rejected, not executed. Correct its per-result audio quotation and coverage once. Preserve reasonable independent intent. Rejected data:\n"+bad.ToString(Formatting.None);
        string raw=null;bool done=false;model.PostWorkReviewMsg(prompt,value=>{raw=value;done=true;});yield return Until(()=>done,25);
        JObject decision=null;try{decision=JObject.Parse(raw??"");}catch(Exception){}
        string reason=null;bool valid=decision!=null&&WorkResultDeliveryProtocol.ValidateCoverage(results,decision,out reason);
        Check("native-work-review-repair-"+line,valid,new JObject{["decision"]=decision,["raw"]=raw,["reason"]=reason,["results"]=results,["historyRows"]=History(line).Count,
            ["scope"]="Reconstructed from nearby recorded monitor snapshots; original work wire unavailable."});
        if(valid)ApplyRecordedCoverage(chat,results,decision,"recorded-"+line);
    }
    static void ApplyRecordedCoverage(ChatSample chat,JArray results,JObject verdict,string name)
    {
        Call(chat,"EnsureWorkDeliveryOwner");
        var ledger=(Dictionary<string,JObject>)Get(chat,"m_WorkToolResultReceipts");ledger.Clear();
        foreach(string field in new[]{"m_WorkResultPlaybackText","m_WorkDeliveredResults","m_WorkDeliveredPlayback"})Call(Get(chat,field),"Clear");
        foreach(JObject result in results)ledger[(string)result["id"]]=(JObject)result.DeepClone();
        foreach(JObject result in results)Call(chat,"RecordWorkCompletedSpeechForResult",(string)result["completedPlaybackText"],(string)result["id"]);
        var decision=verdict.ToObject(typeof(ChatSample).GetNestedType("AutonomyIntentDecision",Flags));
        bool closed=(bool)Call(chat,"ApplyWorkDelivery",decision,null);
        Check("production-missing-report-stays-speech-only-"+name,!closed&&(bool)Get(decision,"proceed")&&(string)Get(decision,"next_step_id")==""&&(string)Get(decision,"planning_focus")=="other",
            JObject.FromObject(decision));ledger.Clear();
    }

    static IEnumerator GeneralDelivery(ChatSample chat,ChatQW model,string[] row)
    {
        var first=WorkResultDeliveryProtocol.Result("step:first","completed",new JObject{["id"]="first",["operation"]=row[1],["entityId"]="same-document"});
        var second=WorkResultDeliveryProtocol.Result("step:second","completed",new JObject{["id"]="second",["operation"]=row[2],["entityId"]="same-document"});
        var results=new JArray(first,second);
        foreach(JObject result in results){result["delivered"]=false;result["completedPlaybackText"]=row[3];result["hasCompletedPlaybackAfterResult"]=true;result["playbackRevision"]=ActivityEvidencePolicy.Stamp(new JValue(row[3]));}
        string prompt="[Runtime work review domain: general]\n[Recorded prior history]\n"+History(int.MaxValue).ToString(Formatting.None)+
            "\n[Task progress runtime]\n"+new JObject{["currentUserText"]=row[0],["delivery"]=new JObject{["results"]=results}}.ToString(Formatting.None)+
            "\n[Review instruction]\n"+WorkResultDeliveryProtocol.Contract+WorkNextIntentProtocol.Contract;
        bool done=false;string raw=null;model.PostWorkReviewMsg(prompt,v=>{raw=v;done=true;});yield return Until(()=>done,25);
        JObject verdict=null;try{verdict=JObject.Parse(raw??"");}catch(Exception){}
        var reports=verdict?["delivery_reports"] as JArray;
        Check("native-general-delivery-"+row[4],reports!=null&&reports.OfType<JObject>().Any(r=>(string)r["id"]==(string)first["id"]&&(string)r["coverage"]=="reported")&&
            reports.OfType<JObject>().Any(r=>(string)r["id"]==(string)second["id"]&&(string)r["coverage"]=="unreported"),new JObject{["raw"]=raw,["results"]=results});
        if(verdict!=null)ApplyRecordedCoverage(chat,results,verdict,row[4]);
    }

    static IEnumerator Language(ChatSample chat,ChatQW model)
    {
        var settings=(ChatSetting)Get(chat,"m_ChatSettings");var previous=settings.m_ChatModel;settings.m_ChatModel=model;
        Set(chat,"m_UserSpeechActiveForAutonomy",false);Set(chat,"m_SceneTimePaused",false);Call(chat,"EnsureActivityOwner");Set(chat,"m_ActivityNextPoll",Time.realtimeSinceStartup+600);
        try {
            foreach(var row in new[]{new[]{"刚才关掉的灯已经重新打开了。","ja","zh"},new[]{"文档已经保存。","en","zh"},new[]{"The summary has been generated.","zh","en"}}) {
                bool completed=false,failed=false;string reason=null;
                var frame=new JObject{["delivery"]=new JObject{["language"]=row[1]}};
                var verdict=new JObject{["id"]="delivery-probe",["operation"]="supplement",["text"]=row[0]};
                bool check=(bool)Call(chat,"ConfirmActivityDeliveryLanguage",frame,verdict,(Action)(()=>completed=true),(Action<string>)(r=>{reason=r;failed=true;}));
                yield return Until(()=>completed||failed,5);
                Check("wrong-language-never-queued-"+row[1],check&&failed&&!completed&&reason.Contains("actual="+row[2]),new JValue(reason));
            }
            bool published=false,rejected=false;
            var interruptFrame=new JObject{["delivery"]=new JObject{["language"]="ja"}};
            var interruptVerdict=new JObject{["id"]="interrupted-result",["operation"]="supplement",["text"]="刚才关掉的灯已经重新打开了。"};
            Call(chat,"ConfirmActivityDeliveryLanguage",interruptFrame,interruptVerdict,(Action)(()=>published=true),(Action<string>)(_=>rejected=true));
            Set(chat,"m_UserSpeechActiveForAutonomy",true);
            yield return Until(()=>!(bool)Get(chat,"m_ActivityRequestPending"),5);
            Check("user-interruption-retires-language-check-without-publishing",!(bool)Get(chat,"m_ActivityRequestPending")&&!published&&!rejected);
            Set(chat,"m_UserSpeechActiveForAutonomy",false);
        }finally{settings.m_ChatModel=previous;}
    }
    static IEnumerator Visual(ChatQW model)
    {
        // New synthetic scene is labelled as such; it is not the missing old RGB.
        var texture=new Texture2D(64,64);texture.SetPixels(Enumerable.Repeat(Color.red,4096).ToArray());texture.Apply();
        string image="data:image/png;base64,"+Convert.ToBase64String(texture.EncodeToPNG());UnityEngine.Object.Destroy(texture);
        var claims=new JArray(new JObject{["id"]="c0",["speechId"]="v0",["claimQuote"]="The image is red."},
            new JObject{["id"]="c1",["speechId"]="v1",["claimQuote"]="It makes me feel cheerful."},
            new JObject{["id"]="c2",["speechId"]="v2",["claimQuote"]="The room behind the camera has no chair."});
        var input=new JObject{["purpose"]="visual-evidence",["captureId"]="synthetic-red-image",["speechId"]="v0",["claimQuote"]="The image is red.",
            ["speech"]=string.Join(" ",claims.Select(c=>(string)c["claimQuote"])),["claims"]=claims};
        bool done=false,ok=false;string raw=null,status=null;
        model.PostActivityVisualEvidence(input.ToString(Formatting.None),image,(a,b,c)=>{done=true;ok=a;raw=b;status=c;});yield return Until(()=>done);
        JArray evidence=null;bool parsed=ok&&ActivityVisualEvidenceProtocol.TryReadGroup(raw,input,out evidence);
        Check("native-grouped-same-capture-visual-evidence",parsed&&evidence.OfType<JObject>().Any(e=>(string)e["speechId"]=="v0"&&(string)e["assessment"]=="supported")&&
            evidence.OfType<JObject>().All(e=>(string)e["speechId"]!="v1")&&evidence.OfType<JObject>().Any(e=>(string)e["speechId"]=="v2"&&(string)e["assessment"]=="unresolved"),
            new JObject{["raw"]=raw,["status"]=status,["evidence"]=evidence});
    }
}
