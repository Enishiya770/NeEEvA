using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class PlaybackEvidenceRegression
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static JObject fixture;static ChatSample runtime;static string output;static readonly JArray checks=new JArray();
    static object Call(object owner,string name,params object[] args)=>(owner is Type t?t:owner.GetType()).GetMethod(name,F).Invoke(owner is Type?null:owner,args);
    static void Set(object owner,string name,object value)=>owner.GetType().GetField(name,F).SetValue(owner,value);
    static T Get<T>(object owner,string name)=>(T)owner.GetType().GetField(name,F).GetValue(owner);
    static JObject At(int line)=>(JObject)((JArray)fixture["events"]).Single(e=>(int)e["line"]==line)["data"].DeepClone();
    static IEnumerable<JObject> Events(string tag)=>((JArray)fixture["events"]).OfType<JObject>().Where(e=>(string)e["tag"]==tag);
    static void Check(string name,bool passed,JToken evidence=null){checks.Add(new JObject{["name"]=name,["passed"]=passed,["evidence"]=evidence});Save();}
    static void Save()=>File.WriteAllText(Path.Combine(output,"playback-evidence.json"),new JObject{["passed"]=checks.Count>0&&checks.All(c=>(bool)c["passed"]),["checks"]=checks}.ToString(Formatting.Indented));
    static IEnumerator Until(Func<bool> done){float end=Time.realtimeSinceStartup+45;while(!done()&&Time.realtimeSinceStartup<end)yield return null;}
    static JToken UserText(JObject wire){var c=((JArray)wire["messages"]).Last(m=>(string)m["role"]=="user")["content"];return c is JArray a?a.Single(p=>(string)p["type"]=="text")["text"]:c;}
    static JObject BaseWire(){var wire=(JObject)fixture["unadmittedRepair"].DeepClone();((JArray)wire["messages"]).Last.Remove();return wire;}
    static JObject Schema(JArray results){var s=new JObject{["properties"]=new JObject(),["required"]=new JArray()};WorkResultDeliveryProtocol.ExtendSchema(s);WorkResultDeliveryProtocol.BindCoverageSchema(s,results);return s;}

    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string directory)
    {
        output=directory;runtime=chat;checks.Clear();fixture=ActivityEvidencePolicy.Parse(File.ReadAllText("Assets/Editor/Fixtures/PlaybackEvidence.20261017-1744.json"));
        var host=new GameObject("Actual playback and bounded repair replay");host.SetActive(false);var model=host.AddComponent<ChatQW>();model.enabled=false;
        model.m_PrewarmPrefixOnStart=false;model.m_Backend=ChatQW.BackendType.Local;Set(model,"url","http://127.0.0.1:8080/v1/chat/completions");host.SetActive(true);
        Set(chat,"m_AgentRunning",false);Set(chat,"m_EnableActivityMonitor",false);Set(chat,"m_RoomTaskSequence",null);
        try {
            Check("complete-actual-five-inputs-seventeen-chunks-four-openings",((JArray)fixture["dialogue"]["inputSources"]).Count(x=>(string)x["kind"]=="Timing")==5 &&
                ((JArray)fixture["dialogue"]["completedSpeech"]).Count==17 && ((JArray)fixture["dialogue"]["openingReceipts"]).Count==4 && Events("Agent/WorkReviewRejected").Count()==2);
            Check("full-segmented-asr-and-unadmitted-wire-retained",Events("ASR/Final").Any() && ((JArray)fixture["events"]).Count==1819 && fixture["unadmittedRepair"]["messages"].Count()==6);
            ReplayAudio(chat);SchemaAndProjection();CacheChecks();DeferredAutonomy(chat,model);
            foreach(var e in Events("Activity/MonitorFrame")) {
                string ready=model.PrepareActivityMonitorFrame(e["data"].ToString(Formatting.None),out string diagnostic);
                Check("complete-original-monitor-budget-"+e["line"],ready!=null,JObject.Parse(diagnostic));
            }
            var receipt=(JObject)fixture["frozenReceipt"].DeepClone();var rows=(JArray)receipt["results"];
            var wire=BaseWire();var rejected=ActivityEvidencePolicy.Parse((string)At(184217)["rawDecision"]);
            Check("original-empty-record-contradiction-rejected",!WorkResultDeliveryProtocol.ValidateCoverage(rows,(JObject)rejected.DeepClone(),out var reason)&&reason=="playback-disposition-without-spoken-evidence");
            var repairs=WorkResultDeliveryProtocol.RepairRows(rows,rejected,reason);
            Check("repair-only-faulty-result-preserves-valid-sibling",repairs.Count==1&&(string)repairs[0]["resultId"]==(string)rows[1]["id"] &&
                (string)rows[0]["status"]=="completed",repairs);
            object[] args={wire.ToString(Formatting.None),rows,rejected,reason,null};
            string repaired=(string)Call(model,"PrepareWorkReviewRepairWire",args);
            object[] budget={repaired,null};Check("complete-original-repair-wire-admitted",(bool)Call(model,"AdmitWorkReviewWire",budget),(JObject)args[4]);
            var original=(JObject)fixture["unadmittedRepair"].DeepClone();object[] oldBudget={original.ToString(Formatting.None),null};
            Check("original-full-wire-reproduces-budget-failure",!(bool)Call(model,"AdmitWorkReviewWire",oldBudget),(JObject)oldBudget[1]);
            Check("repair-keeps-original-envelope-and-literal-messages",((JArray)JObject.Parse(repaired)["messages"]).Take(4).Zip(((JArray)wire["messages"]).Take(4),JToken.DeepEquals).All(x=>x));
            Check("schema-can-rebind-same-snapshot",JToken.DeepEquals(Schema(rows)["properties"]["delivery_reports"],JObject.Parse(repaired)["response_format"]["schema"]["properties"]["delivery_reports"]));
            var protectedWire=JObject.Parse(repaired);((JArray)protectedWire["messages"]).Add(new JObject{["role"]="user",["content"]=new string('限',40000)});
            object[] overflow={protectedWire.ToString(Formatting.None),null};Check("mandatory-overflow-remains-rejected",!(bool)Call(model,"AdmitWorkReviewWire",overflow),(JObject)overflow[1]);
            // Native replay uses all recorded text/schema, but does not invent the
            // missing original RGB. Full-wire admission above retained its reserve.
            var native=JObject.Parse(repaired);var parts=(JArray)((JArray)native["messages"]).Last(m=>(string)m["role"]=="user")["content"];
            foreach(var image in parts.Where(p=>(string)p["type"]=="image_url").ToArray())image.Remove();
            yield return NativeWork(model,native,receipt,"original-complete-repair-text",v=>
                ((JArray)v["delivery_reports"]).Any(r=>(string)r["id"]==(string)rows[1]["id"]&&(string)r["finalDisposition"]=="unreported"),1);
            yield return CachedMeanings(model,JObject.Parse(repaired),receipt);
            yield return CancelAndOverflow(model,native,protectedWire,receipt);
            yield return EarlierRawRepair(model);
            foreach(var variant in new[]{
                // Single-operation tense controls. Compound generate/deliver and
                // retrieval/edit/save use their separate receipts in the full
                // semantic-playback suite; generation alone never proves sending.
                new[]{"edit-save","zh","保存编辑后的副本，保留原件。","正在准备保存副本。","副本已经保存好了。","正在保存副本。"},
                new[]{"generate","en","Generate the summary.","I am preparing the summary.","The summary has been generated.","I am generating the summary."},
                new[]{"observe","ja","周囲を観察して。","観察の準備中です。","観察が終わりました。","今、観察しています。"},
                new[]{"sing","zh","把这首歌唱完。","我正在准备唱这首歌。","这首歌已经唱完了。","我正在唱这首歌。"}}) {
                yield return GeneralCase(model,variant,false);
                yield return GeneralCase(model,variant,true,true);
                yield return GeneralCase(model,variant,true);
            }
            yield return NativeMonitor(model,At(173470),"actual-progress-is-not-arrival",v=>(string)v["delivery"]["operation"]=="supplement");
            yield return NativeMonitor(model,At(184585),"actual-final-arrival-is-covered",v=>(string)v["delivery"]["operation"]=="covered");
            yield return Visual(model);
            Save();if(checks.Any(c=>!(bool)c["passed"]))throw new InvalidOperationException("Playback evidence regression failed.");
        }finally{UnityEngine.Object.Destroy(host);}
    }

    static void ReplayAudio(ChatSample chat)
    {
        Call(chat,"EnsureWorkDeliveryOwner");
        var results=Get<Dictionary<string,JObject>>(chat,"m_WorkToolResultReceipts");results.Clear();
        var frames=Events("Activity/MonitorFrame").Select(e=>e["data"] as JObject).Where(f=>f?["delivery"] is JObject).ToList();
        var supplements=new Dictionary<string,string>();int count=0;var receiptTrace=new JArray();
        var inputLines=new HashSet<int>(((JArray)fixture["dialogue"]["inputSources"]).Where(e=>(string)e["kind"]=="Timing").Select(e=>(int)e["line"]));
        foreach(var e in ((JArray)fixture["events"]).OfType<JObject>()) {
            string tag=(string)e["tag"];var data=e["data"] as JObject;
            if(inputLines.Contains((int)e["line"])) {Set(chat,"m_WorkEpoch",Get<int>(chat,"m_WorkEpoch")+1);Call(chat,"EnsureWorkDeliveryOwner");supplements.Clear();}
            if(tag=="Work/StepSettled") {
                // Device-step settlement logs carry an empty outer actionId;
                // join using the real work owner and canonical step, not a guess.
                var recorded=frames.FirstOrDefault(f=>(int?)f["owner"]?["workEpoch"]==(int?)data["workEpoch"] &&
                    (string)f["delivery"]?["source"]=="step:"+(string)data["stepId"])?["delivery"] as JObject;
                if(recorded!=null) {
                    results[(string)recorded["id"]]=new JObject{["id"]=recorded["id"],["source"]=recorded["source"],["status"]=recorded["status"],["facts"]=recorded["facts"].DeepClone()};
                    var live=((JArray)((JObject)Call(chat,"ReadWorkDelivery"))["results"]).Single(r=>(string)r["id"]==(string)recorded["id"]);
                    Check("completion-does-not-backfill-earlier-audio-"+e["line"],(string)live["completedPlaybackText"]=="");
                }
            }
            if(tag=="Activity/Delivery"&&(string)data["operation"]=="supplement"&&data["rejection"]?.Type==JTokenType.Null)
                supplements[(string)data["verdict"]["text"]]=(string)data["id"];
            if(tag=="Activity/SpeechProgress"&&(string)data["state"]=="completed") {
                string text=(string)data["text"];supplements.TryGetValue(text,out var scope);
                var unit=new ActivityMonitorProtocol.SpeechUnit{Id=(string)data["id"],Text=text,Language=(string)data["language"],State="completed",Version=(int)data["version"]};
                Set(chat,"m_FormalResponseGeneration",int.Parse(unit.Id.Split(':')[1]));
                Call(chat,"RecordWorkCompletedSpeechForResult",text,scope);Call(chat,"RecordWorkCompletedAudio",text,scope,unit,unit.Version);count++;
                var live=(JObject)Call(chat,"ReadWorkDelivery");receiptTrace.Add(new JObject{["line"]=e["line"],["audioId"]=unit.Id,["scope"]=scope,["delivery"]=live});
            }
            if(tag=="Activity/SpeechRetired") {
                var played=Get<Dictionary<string,string>>(chat,"m_WorkResultPlaybackText");
                Check("retired-draft-does-not-enter-played-ledger-"+e["line"],!played.Values.Any(t=>t.Contains("歩く動作を準備中")));
            }
        }
        Check("all-seventeen-real-completions-replayed-through-runtime",count==17,receiptTrace);
        var current=(JObject)Call(chat,"ReadWorkDelivery");var arrival=((JArray)current["results"]).Single(r=>(string)r["id"]=="C922A42AF47FA5A9");
        Check("arrival-supplement-bound-to-own-result-audio",(string)arrival["completedPlaybackText"]==(string)At(184585)["delivery"]["completedPlaybackText"] &&
            ((JArray)arrival["completedAudio"]).Count==2);
        var lamp=((JArray)current["results"]).Single(r=>(string)r["id"]=="5A34852317336E9C");
        Check("other-result-supplement-does-not-contaminate-lamp",!((string)lamp["completedPlaybackText"]).Contains("もう着きました。"));
        var report=At(184118)["verdict"];var proof=report["playbackProof"];
        var cache=Get<ReviewedDeliveryEvidence>(chat,"m_ReviewedDeliveryEvidence");
        cache.Remember(Get<int>(chat,"m_WorkEpoch"),11,current,new[]{new WorkResultDeliveryProtocol.Report{
            id=(string)lamp["id"],coverage="reported",quote=(string)report["quote"],lastRelevantQuote=(string)proof["lastRelevantQuote"],
            playedRevision=(string)lamp["playbackRevision"],reportAct="outcome-report",finalDisposition="consistent"}});
        Set(chat,"m_FormalResponseInFlight",true);Set(chat,"m_UserSpeechActiveForAutonomy",false);Set(chat,"m_SceneTimePaused",false);Set(chat,"m_WorkReviewOpen",true);
        Call(chat,"ApplySharedWorkDeliveryEvidence");current=(JObject)Call(chat,"ReadWorkDelivery");
        Check("accepted-completed-result-transfers-during-other-output",(bool?)((JArray)current["results"]).Single(r=>(string)r["id"]==(string)lamp["id"])["delivered"]==true && Get<bool>(chat,"m_WorkReviewOpen"));
        Call(chat,"RecordWorkCompletedSpeechForResult","Correction: that report was wrong.",(string)lamp["id"]);
        current=(JObject)Call(chat,"ReadWorkDelivery");
        Check("later-spoken-version-still-reopens-own-audit",(bool?)((JArray)current["results"]).Single(r=>(string)r["id"]==(string)lamp["id"])["playbackReviewPending"]==true);
        Set(chat,"m_FormalResponseInFlight",false);Set(chat,"m_WorkReviewOpen",false);
    }

    static void SchemaAndProjection()
    {
        var rows=(JArray)fixture["frozenReceipt"]["results"];var schema=Schema(rows);WorkResultDeliveryProtocol.BindCoverageSchema(schema,rows);
        var empty=((JArray)schema["properties"]["delivery_reports"]["items"]["anyOf"]).Where(b=>(string)b["properties"]["id"]["enum"][0]==(string)rows[1]["id"]).ToArray();
        Check("empty-playback-schema-only-permits-unreported",empty.Length==1&&JToken.DeepEquals(empty[0]["properties"]["finalDisposition"]["enum"],new JArray("unreported")));
        var unversioned=(JObject)rows[1].DeepClone();unversioned.Remove("playbackRevision");
        Check("known-empty-unversioned-audio-cannot-invent-report",JToken.DeepEquals(Schema(new JArray(unversioned)).SelectToken("properties.delivery_reports.items.anyOf[0].properties.coverage.enum"),new JArray("unreported")));
        Check("known-empty-unversioned-audio-cannot-invent-quote",JToken.DeepEquals(Schema(new JArray(unversioned)).SelectToken("properties.delivery_reports.items.anyOf[0].properties.lastRelevantQuote.enum"),new JArray("")));
        Check("early-operation-binding-does-not-preselect-execution-stage",!schema.SelectTokens("properties.delivery_reports.items.anyOf[*].properties.resultScope.enum[0]").Values<string>().Any(s=>s.Contains("actualStatus=")));
        var meaningInputs=WorkResultDeliveryProtocol.MeaningInputs((JObject)fixture["frozenReceipt"]);
        Check("word-reading-excludes-outcome-and-unplayed-drafts",meaningInputs.Count>0&&!meaningInputs.ToString().Contains("actualStatus=")&&
            meaningInputs.OfType<JObject>().All(r=>r["status"]==null&&r["facts"]==null));
        var newVersion=(JObject)fixture["frozenReceipt"].DeepClone();newVersion["results"][0]["playbackRevision"]="next-version";
        Check("word-cache-identity-includes-audio-version",(string)WorkResultDeliveryProtocol.MeaningInputs(newVersion)[0]["id"]!=(string)meaningInputs[0]["id"]);
        newVersion=(JObject)fixture["frozenReceipt"].DeepClone();newVersion["workEpoch"]=999;
        Check("word-cache-identity-includes-task-owner",(string)WorkResultDeliveryProtocol.MeaningInputs(newVersion)[0]["id"]!=(string)meaningInputs[0]["id"]);
        var frame=At(184141);frame["delivery"]=rows[1].DeepClone();frame["delivery"]["supplementAllowed"]=true;
        var deliverySchema=ActivityDeliveryProtocol.Schema(frame["delivery"] as JObject);
        Check("monitor-empty-playback-schema-no-contradiction",!deliverySchema.ToString().Contains("unresolved")&&!deliverySchema.ToString().Contains("retracted"));
        string before=fixture["frozenRuntime"].ToString();var runtime=(JObject)fixture["frozenRuntime"].DeepClone();
        WorkReviewPlaybackProjection.Apply(new[]{runtime});
        Check("generated-draft-is-not-delivery-authority",runtime["speech"]["lastGeneratedText"]==null&&runtime["speech"]["lastGeneratedTextSource"]!=null&&runtime["speech"]["lastPublicPlayback"]["publicText"]==null);
        Check("projection-keeps-receipts-owner-and-resident",before==fixture["frozenRuntime"].ToString()&&JToken.DeepEquals(runtime["delivery"],fixture["frozenRuntime"]["delivery"])&&JToken.DeepEquals(runtime["workEpoch"],fixture["frozenRuntime"]["workEpoch"]));
        string projected=runtime.ToString();WorkReviewPlaybackProjection.Apply(new[]{runtime});Check("playback-projection-idempotent",projected==runtime.ToString());
        foreach(int line in new[]{173606,184217})Check("original-wrong-review-preserved-"+line,((string)At(line)["rawDecision"]).Contains("unresolved"),At(line));
        foreach(string source in new[]{"step:s1","action:a1","independent-answer","selfInspection"}) {
            var result=WorkResultDeliveryProtocol.Result(source,"returned",new JObject{["questionQuote"]="What are you doing?"});
            var report=new WorkResultDeliveryProtocol.Report{id=(string)result["id"],coverage="reported",quote="I am observing.",reportAct="outcome-report",reportedStage="ongoing"};
            string failure=WorkResultDeliveryProtocol.CoverageRejection(result,report,report.quote);
            Check("ongoing-state-vs-terminal-operation-"+source,source.Contains(":")?failure=="delivery-progress-does-not-report-terminal-outcome":failure==null);
            report.reportedStage="unknown-outcome";report.reportAct="unknown-answer";report.quote="I cannot confirm the result.";
            failure=WorkResultDeliveryProtocol.CoverageRejection(result,report,report.quote);
            Check("unknown-answer-vs-known-success-"+source,source.Contains(":")?failure=="delivery-unknown-does-not-report-known-result":failure==null);
        }
        var legacy=new JObject{["delivery_reports"]=new JArray(new JObject{["id"]="legacy",["quote"]="done",["reportedStage"]=null})};
        Check("legacy-null-stage-remains-compatible",WorkResultDeliveryProtocol.ValidExtension(legacy));
        legacy["delivery_reports"][0]["reportedStage"]="invented-stage";
        Check("invalid-explicit-stage-still-rejected",!WorkResultDeliveryProtocol.ValidExtension(legacy));
    }

    static void CacheChecks()
    {
        var frame=Events("Activity/MonitorFrame").Select(e=>e["data"] as JObject).First(f=>(string)f["purpose"]=="speech"&&((JArray)f["speech"]).Count==1);
        frame=(JObject)frame.DeepClone();frame["speech"][0]["text"]="I would enjoy staying with you.";frame["speech"][0]["state"]="queued";
        var decision=new JObject{["speech"]=new JObject{["issue"]="none"},["speechAct"]="personal-expression",["truthWhenSaid"]="unknown",["claimTime"]="prospective"};
        var cache=new ActivityCompletedCheckCache();cache.Remember(frame,decision);frame["speech"][0]["state"]="playing";frame["facts"]["visual:new"]=new JObject{["finding"]="unrelated"};
        Check("same-nonfactual-audio-reused-across-playback",cache.Contains(frame));frame["speech"][0]["state"]="completed";
        Check("same-personal-expression-completed-does-not-recheck",cache.Contains(frame));
        frame["speech"][0]["text"]="I have saved the file.";Check("new-factual-clause-cannot-inherit-cache",!cache.Contains(frame));frame["speech"][0]["text"]="I would enjoy staying with you.";
        frame["facts"]["constraints"]=new JObject{["stop"]=true};Check("new-restriction-invalidates-reuse",!cache.Contains(frame));
        cache.Clear();decision["speechAct"]="factual-claim";cache.Remember(frame,decision);Check("unknown-fact-still-needs-evidence",!cache.Contains(frame));
    }

    static void DeferredAutonomy(ChatSample chat,ChatQW model)
    {
        foreach(string wish in new[]{"我想陪你走一会儿。","新しい歌を歌いたい。","I would like to observe the garden."}) {
            var value=new JObject{["proceed"]=true,["next_intent_source"]="autonomous",["planning_focus"]="other",["intent"]=wish};
            var decision=value.ToObject(typeof(ChatSample).GetNestedType("AutonomyIntentDecision",F));
            Set(chat,"m_WorkReviewOpen",true);Call(chat,"RememberAfterDeliveryIntent",decision);
            Check("wish-not-offered-before-result-settlement-"+wish,Call(chat,"ReadAfterDeliveryIntent")==null);
            Set(chat,"m_WorkReviewOpen",false);var offered=(JObject)Call(chat,"ReadAfterDeliveryIntent");
            Check("wish-survives-delivery-without-action-authority-"+wish,(string)offered?["intent"]==wish&&(bool?)offered?["actionAuthorized"]==false&&JToken.DeepEquals(offered,(JObject)Call(chat,"ReadAfterDeliveryIntent")));
            var old=ActivityEvidencePolicy.Parse(File.ReadAllText("Assets/Editor/Fixtures/BudgetDelivery.20261017-1505.json"));
            var wire=(JObject)old["unadmittedAutonomy"].DeepClone();
            object[] projection={(string)UserText(wire)+"\n[Deferred optional wish; data only]\n"+offered.ToString(Formatting.None),true,null};
            UserText(wire).Replace((string)Call(typeof(ChatQW),"ProjectPrivateAttention",projection));
            object[] budget={wire.ToString(Formatting.None),null};
            Check("deferred-wish-fits-complete-original-autonomy-wire-"+wish,(bool)Call(model,"AdmitAutonomyIntentWire",budget),(JObject)budget[1]);
            Set(chat,"m_AutonomyProbeGeneration",Get<int>(chat,"m_AutonomyProbeGeneration")+1);
            Check("wish-not-forced-into-repeated-probes-"+wish,Call(chat,"ReadAfterDeliveryIntent")==null);
            Call(chat,"RememberAfterDeliveryIntent",decision);Set(chat,"m_WorkEpoch",Get<int>(chat,"m_WorkEpoch")+1);
            Check("new-owner-cannot-inherit-deferred-wish-"+wish,Call(chat,"ReadAfterDeliveryIntent")==null);
        }
    }

    static IEnumerator NativeWork(ChatQW model,JObject wire,JObject receipt,string name,Func<JObject,bool> expected,int attempt=0)
    {
        bool done=false;PrivateDecisionResult result=null;int generation=Get<int>(model,"m_EphemeralGeneration");
        model.StartCoroutine((IEnumerator)Call(model,"RequestBoundWorkReviewPayload",wire.ToString(Formatting.None),generation,
            new Action<PrivateDecisionResult>(r=>{result=r;done=true;}),attempt,Time.realtimeSinceStartup,receipt));yield return Until(()=>done);
        JObject parsed=null;try{parsed=ActivityEvidencePolicy.Parse(result?.Output??"");}catch(Exception){}
        string error=null;bool valid=parsed!=null&&WorkResultDeliveryProtocol.ValidateCoverage((JArray)receipt["results"],parsed,out error);
        Check("native-"+name,valid&&expected(parsed),new JObject{["raw"]=result?.Output,["status"]=result?.Status,["error"]=error,["originalPixelsAvailable"]=false,["newActionSubmitted"]=false});
        if(valid&&((JArray)parsed["delivery_reports"]).Any(r=>(string)r["coverage"]=="unreported")) {
            var decision=parsed.ToObject(typeof(ChatSample).GetNestedType("AutonomyIntentDecision",F));
            var sequence=new JObject{["steps"]=new JArray(((JArray)receipt["results"]).Select(r=>new JObject{
                ["id"]=((string)r["source"]).Replace("step:",""),["kind"]=r["facts"]?["kind"]??r["facts"]?["operation"],
                ["status"]=r["status"],["after"]=new JArray()}))};
            // The production continuation gate consumes canonical terminal steps;
            // a model intent is diagnostic, never a new execution authority.
            Call(runtime,"SelectWorkContinuationScope",decision,sequence);
            var effective=JObject.FromObject(decision);
            Check("effective-continuation-cannot-reexecute-"+name,Get<bool>(runtime,"m_WorkReplyOnly")&&
                (string)effective["planning_focus"]=="other"&&(string)effective["next_step_id"]=="",new JObject{
                    ["rawIntent"]=parsed["intent"],["rawSource"]=parsed["next_intent_source"],["effective"]=effective,["sequence"]=sequence});
        }
    }
    static IEnumerator CachedMeanings(ChatQW model,JObject wire,JObject receipt)
    {
        int sent=Get<int>(model,"m_WorkPlaybackMeaningRequests"),generation=Get<int>(model,"m_EphemeralGeneration");
        var trace=Call(model,"BeginWorkReviewTrace",wire.ToString(Formatting.None),generation,0,receipt,null);
        string final=null,error=null;JArray readings=null;
        yield return (IEnumerator)Call(model,"PrepareWorkPlaybackMeanings",wire.ToString(Formatting.None),receipt,generation,Time.realtimeSinceStartup,trace,
            new Action<string,JArray,string>((w,r,e)=>{final=w;readings=r;error=e;}));
        Call(model,"EndWorkReviewTrace",trace,"cache-replay-completed");
        Check("same-completed-audio-reuses-word-reading-without-http",sent==Get<int>(model,"m_WorkPlaybackMeaningRequests")&&final!=null&&error==null&&readings.Count>0);
        object[] finalBudget={final??"{}",null};
        bool admitted=final!=null&&(bool)Call(model,"AdmitWorkReviewWire",finalBudget);
        Check("complete-final-literal-bound-wire-with-original-image-reserve",admitted&&(int?)((JObject)finalBudget[1])?["imageCount"]==1,(JObject)finalBudget[1]);
    }
    static IEnumerator CancelAndOverflow(ChatQW model,JObject native,JObject oversized,JObject receipt)
    {
        bool callback=false;int generation=Get<int>(model,"m_EphemeralGeneration");
        model.StartCoroutine((IEnumerator)Call(model,"RequestBoundWorkReviewPayload",native.ToString(Formatting.None),generation,new Action<PrivateDecisionResult>(r=>callback=true),1,Time.realtimeSinceStartup,receipt));
        yield return null;model.CancelEphemeralMsg("user-started-speaking");yield return null;yield return null;
        Check("actual-http-user-cancellation-releases-owner-without-result",!callback&&Get<object>(model,"m_WorkReviewTrace")==null&&Get<object>(model,"m_EphemeralRequest")==null);
        callback=false;yield return (IEnumerator)Call(model,"RequestBoundWorkReviewPayload",native.ToString(Formatting.None),generation,new Action<PrivateDecisionResult>(r=>callback=true),1,Time.realtimeSinceStartup,receipt);
        Check("old-generation-cannot-send-or-complete-review",!callback);
        // The first literal reading is separately cancellable; it must not
        // commit its cache or advance to the final review after ownership ends.
        Get<Dictionary<string,JObject>>(model,"m_WorkPlaybackReadings").Clear();
        int sent=Get<int>(model,"m_WorkPlaybackMeaningRequests");callback=false;
        generation=Get<int>(model,"m_EphemeralGeneration");
        model.StartCoroutine((IEnumerator)Call(model,"RequestBoundWorkReviewPayload",native.ToString(Formatting.None),generation,
            new Action<PrivateDecisionResult>(r=>callback=true),1,Time.realtimeSinceStartup,receipt));
        yield return null;model.CancelEphemeralMsg("user-started-speaking");yield return null;yield return null;
        Check("literal-reading-user-cancellation-releases-owner-without-cache-or-result",!callback&&
            Get<int>(model,"m_WorkPlaybackMeaningRequests")==sent+1&&Get<Dictionary<string,JObject>>(model,"m_WorkPlaybackReadings").Count==0&&
            Get<object>(model,"m_WorkReviewTrace")==null&&Get<object>(model,"m_EphemeralRequest")==null);
        PrivateDecisionResult result=null;yield return (IEnumerator)Call(model,"RequestBoundWorkReviewPayload",oversized.ToString(Formatting.None),Get<int>(model,"m_EphemeralGeneration"),
            new Action<PrivateDecisionResult>(r=>result=r),1,Time.realtimeSinceStartup,receipt);
        Check("complete-repair-budget-rejection-is-explicit-no-http",result?.Status=="context-budget"&&Get<object>(model,"m_EphemeralRequest")==null);
    }
    static IEnumerator GeneralCase(ChatQW model,string[] variant,bool played,bool progress=false)
    {
        var result=WorkResultDeliveryProtocol.Result("step:s1","completed",new JObject{["operation"]=variant[0],["target"]="artifact-1",["status"]="completed"});
        result["completedPlaybackText"]=played?variant[progress?5:4]:"";result["playbackRevision"]=ActivityEvidencePolicy.Stamp(result["completedPlaybackText"]);
        result["hasCompletedPlaybackAfterResult"]=played;result["requiresReportAct"]=true;
        result["taskContext"]=new JObject{["parentRequest"]=variant[2],["step"]=new JObject{["id"]="s1",["kind"]=variant[0],["quote"]=variant[2]}};
        var receipt=new JObject{["workEpoch"]=9,["userTurn"]=9,["results"]=new JArray(result)};
        string prompt="[Task progress runtime]\n"+new JObject{["workEpoch"]=9,["userTurn"]=9,["delivery"]=receipt,
            ["speech"]=new JObject{["generation"]=1,["lastGeneratedText"]=variant[3],["lastPublicPlayback"]=new JObject{["publicText"]=variant[3],["completedPlaybackText"]=result["completedPlaybackText"]}},
            ["request"]=variant[2],["scope"]="The only requested operation has truly completed. No other tool work is due. If unreported, only deliver the actual result; never reexecute it."}.ToString(Formatting.None);
        string wire=(string)Call(model,"BuildWorkReviewRequestJsonWithImage",prompt,null);
        yield return NativeWork(model,JObject.Parse(wire),receipt,variant[0]+(progress?"-progress-is-not-terminal":played?"-actual-played":"-draft-only"),v=>
            (string)v["delivery_reports"][0]["coverage"]==(played&&!progress?"reported":"unreported") &&
            (!played || progress || (string)v["work_status"]=="closed"));
    }
    static IEnumerator EarlierRawRepair(ChatQW model)
    {
        var lamp=(JObject)At(173288)["delivery"].DeepClone();var returned=(JObject)At(173470)["delivery"].DeepClone();
        var firstAudio=(JObject)((JArray)lamp["completedAudio"])[0];
        lamp["completedPlaybackText"]=((string)lamp["completedPlaybackText"]).Substring(0,(int)firstAudio["textLength"]);
        lamp["completedAudio"]=new JArray(firstAudio.DeepClone());lamp.Remove("playbackUnits");
        returned["completedPlaybackText"]="";returned["completedAudio"]=new JArray();returned.Remove("playbackUnits");returned["hasCompletedPlaybackAfterResult"]=false;
        foreach(var row in new[]{lamp,returned})row["playbackRevision"]=ActivityEvidencePolicy.Stamp(row["completedPlaybackText"]);
        var rejected=ActivityEvidencePolicy.Parse((string)At(173606)["rawDecision"]);var results=new JArray(lamp,returned);
        Check("earlier-frozen-audio-reconstruction-matches-original-revisions",((JArray)rejected["delivery_reports"]).All(r=>results.Any(s=>(string)s["id"]==(string)r["id"]&&(string)s["playbackRevision"]==(string)r["playedRevision"])));
        Check("earlier-original-rejection-still-reproduces",!WorkResultDeliveryProtocol.ValidateCoverage(results,(JObject)rejected.DeepClone(),out var reason)&&reason=="playback-disposition-without-spoken-evidence");
        var receipt=new JObject{["workEpoch"]=4,["userTurn"]=4,["results"]=results};
        var transcript=new JArray(((JArray)fixture["dialogue"]["inputSources"]).Where(e=>(string)e["kind"]=="Timing")
            .Concat(((JArray)fixture["dialogue"]["completedSpeech"])).Concat(((JArray)fixture["dialogue"]["openingReceipts"]))
            .Where(e=>(int)e["line"]<173606).OrderBy(e=>(int)e["line"]).Select(e=>e.DeepClone()));
        string prompt="[Task progress runtime]\n"+new JObject{["delivery"]=receipt,["request"]="去把刚才你看到的那盏小灯关掉，然后回到我身边。",
            ["scope"]="Both requested steps truly completed. The return currently has no completed audio. Only its result reporting is due; no repeat movement or operation."}.ToString(Formatting.None)+
            "\n[Actual conversation history; reference only]\n"+transcript.ToString(Formatting.None);
        string wire=(string)Call(model,"BuildWorkReviewRequestJsonWithImage",prompt,null);
        object[] args={wire,results,rejected,reason,null};string repaired=(string)Call(model,"PrepareWorkReviewRepairWire",args);
        Check("earlier-repair-preserves-raw-error-and-full-history",JObject.Parse(repaired)["messages"].ToString().Contains("unresolved")&&transcript.Count>3,(JObject)args[4]);
        yield return NativeWork(model,JObject.Parse(repaired),receipt,"earlier-raw-error-complete-history-reconstructed-wire",v=>
            ((JArray)v["delivery_reports"]).Any(r=>(string)r["id"]==(string)returned["id"]&&(string)r["finalDisposition"]=="unreported"),1);
    }
    static IEnumerator NativeMonitor(ChatQW model,JObject frame,string name,Func<JObject,bool> expected)
    {
        string ready=model.PrepareActivityMonitorFrame(frame.ToString(Formatting.None),out string budget);bool done=false,ok=false;string raw=null,status=null,error=null;JObject verdict=null;
        if(ready!=null){model.PostActivityMonitor(ready,(a,b,c)=>{done=true;ok=a;raw=b;status=c;});yield return Until(()=>done);}
        Check("native-"+name,ok&&ActivityMonitorProtocol.TryRead(raw,JObject.Parse(ready),out verdict,out error)&&expected(verdict),new JObject{["raw"]=raw,["status"]=status,["error"]=error,["budget"]=JObject.Parse(budget)});
    }
    static IEnumerator Visual(ChatQW model)
    {
        var t=new Texture2D(64,64);t.SetPixels(Enumerable.Repeat(Color.red,4096).ToArray());t.Apply();string image="data:image/png;base64,"+Convert.ToBase64String(t.EncodeToPNG());UnityEngine.Object.Destroy(t);
        var input=new JObject{["captureId"]="explicit-synthetic-red-image",["speech"]="The image is red and the book titles are readable.",["claims"]=new JArray(new JObject{["id"]="c0",["speechId"]="test:1",["claimQuote"]="The image is red and the book titles are readable."})};
        bool done=false,ok=false;string raw=null;model.PostActivityVisualEvidence(input.ToString(Formatting.None),image,(a,b,c)=>{done=true;ok=a;raw=b;});yield return Until(()=>done);
        JArray evidence=null;Check("native-complete-visual-gap-within-original-budget",ok&&ActivityVisualEvidenceProtocol.TryReadGroup(raw,input,out evidence)&&evidence.Count==1&&(string)evidence[0]["assessment"]!="supported",new JObject{["raw"]=raw,["evidence"]=evidence});
    }
}
