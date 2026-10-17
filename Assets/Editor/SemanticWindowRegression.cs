using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class SemanticWindowRegression
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static JObject fixture; static string output; static readonly JArray checks=new JArray();
    static object Call(object owner,string name,params object[] args)=>(owner is Type t?t:owner.GetType()).GetMethod(name,F).Invoke(owner is Type?null:owner,args);
    static void Set(object owner,string name,object value)=>owner.GetType().GetField(name,F).SetValue(owner,value);
    static T Get<T>(object owner,string name)=>(T)owner.GetType().GetField(name,F).GetValue(owner);
    static JObject At(int line)=>(JObject)((JArray)fixture["events"]).Single(e=>(int)e["line"]==line)["data"].DeepClone();
    static IEnumerable<JObject> Events(string tag)=>((JArray)fixture["events"]).OfType<JObject>().Where(e=>(string)e["tag"]==tag);
    static void Check(string name,bool passed,JToken evidence=null){checks.Add(new JObject{["name"]=name,["passed"]=passed,["evidence"]=evidence});Save();}
    static void Save()=>File.WriteAllText(Path.Combine(output,"semantic-window.json"),new JObject{["passed"]=checks.Count>0&&checks.All(c=>(bool)c["passed"]),["checks"]=checks}.ToString(Formatting.Indented));
    static IEnumerator Until(Func<bool> done){float end=Time.realtimeSinceStartup+45;while(!done()&&Time.realtimeSinceStartup<end)yield return null;}

    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string directory)
    {
        output=directory;checks.Clear();fixture=ActivityEvidencePolicy.Parse(File.ReadAllText("Assets/Editor/Fixtures/SemanticWindow.20261017-1626.json"));
        var host=new GameObject("Whole semantic window replay");host.SetActive(false);var model=host.AddComponent<ChatQW>();model.enabled=false;
        model.m_PrewarmPrefixOnStart=false;model.m_Backend=ChatQW.BackendType.Local;Set(model,"url","http://127.0.0.1:8080/v1/chat/completions");host.SetActive(true);
        Set(chat,"m_AgentRunning",false);Set(chat,"m_EnableActivityMonitor",false);
        try {
            Check("complete-seven-captures-six-inputs-twenty-one-played-four-openings",
                ((JArray)fixture["dialogue"]["inputSources"]).Count(x=>(string)x["kind"]=="SenseVoice/Evidence")==7 &&
                ((JArray)fixture["dialogue"]["inputSources"]).Count(x=>(string)x["kind"]=="Timing")==6 &&
                ((JArray)fixture["dialogue"]["completedSpeech"]).Count==21 && ((JArray)fixture["dialogue"]["openingReceipts"]).Count==4);
            Check("original-errors-and-order-retained",((string)At(154422)["rawVerdict"]).Contains("关灯、开灯") &&
                ((string)At(156079)["rawVerdict"]).Contains("回到我身边") && Events("Activity/MonitorFrame").Count()==40);
            var full=ReplayWindows(chat);
            VisualGrouping(chat,host);
            foreach(var entry in Events("Activity/MonitorFrame")) {
                string ready=model.PrepareActivityMonitorFrame(entry["data"].ToString(Formatting.None),out string diagnostic);
                Check("complete-recorded-monitor-budget-"+entry["line"],ready!=null,JObject.Parse(diagnostic));
            }
            var frame=At(137442);frame["speech"]=new JArray(full);
            yield return Native(model,frame,"whole-shelf-clause-needs-own-visual-proof",v=>(string)v["speech"]["issue"]!="mistake" &&
                ((string)v["claimQuote"]??"").Contains("予定の棚") && (string)v["claimTime"]=="current-state" &&
                (string)v["grounding"]?["claimDomain"]=="visual","speech-perception-needs-source");
            foreach(var variant in new[]{new[]{"en","The window for the file you will edit is visible in the image."},
                new[]{"zh","刚才保存的文件窗口还在画面里。"}}) {
                var general=(JObject)frame.DeepClone();general["speech"][0]["text"]=variant[1];general["speech"][0]["language"]=variant[0];
                yield return Native(model,general,"main-predicate-time-"+variant[0],v=>(string)v["claimTime"]=="current-state"&&
                    (string)v["grounding"]?["claimDomain"]=="visual","speech-perception-needs-source");
            }
            Clarification(chat,out var clarification);
            var clarify=Events("Activity/MonitorFrame").Select(e=>(JObject)e["data"]).First(e=>(int?)e["owner"]["generation"]==5);
            clarify=(JObject)clarify.DeepClone();clarify["facts"]["input-resolution"]=clarification;
            clarify["evidenceScopes"]["input-resolution"]=ActivityMonitorContextPolicy.Scope("history","mistake","uncertainty");
            clarify["speech"][0]["observedAtCreation"]["inputResolutionEvent"]=new JObject{["capture"]=clarification["capture"],
                ["recognitionOutcome"]=clarification["recognitionOutcome"],["reliablyUnderstood"]=false};
            yield return Native(model,clarify,"clarification-provenance",v=>(string)v["speech"]["issue"]=="none");
            yield return Native(model,At(154402),"old-follow-new-historical-modifier",v=>
                (string)v["action"]["operation"]=="continue" && (string)v["action"]["taskSource"]=="earlier-work-origin" &&
                !((string)v["action"]["requestMeaning"]??"").Contains("关灯、开灯"));
            yield return Native(model,At(155917),"preparation-bound-to-current-step",v=>
                (string)v["action"]["operation"]=="continue" && (string)v["action"]["taskStep"]==ActivityMonitorProtocol.ActionStepBinding(At(155917)) &&
                !((string)v["action"]["reason"]??"").Contains("返回用户"));
            foreach(var variant in new[]{new[]{"read","edit","Inspect the file you saved earlier, then edit its title and save a copy.","edit"},
                new[]{"search","generate","前に調べた資料で要約を作ってから、保存して。","generate"},
                new[]{"prepare","observe","观察刚才生成的画面，然后告诉我差异。","observe"}}) {
                var general=At(155917);string id=(string)general["actions"][0]["id"];
                var action=(JObject)general["facts"]["action:"+id];action["kind"]=variant[0];general["actions"][0]["kind"]=variant[0];
                action["reason"]="Preparing the owned current step.";action["facts"]=new JObject{["phase"]="preparing"};
                action["task"]=new JObject{["relationship"]="prerequisite",["requestedOperation"]=variant[3],["targetEntityId"]="artifact-1",
                    ["step"]=new JObject{["id"]="s1",["kind"]=variant[1],["status"]="running"},
                    ["origin"]=new JObject{["initiative"]="user-work",["request"]=variant[2]},["requestRelationship"]="same-work"};
                general["facts"]["request"]["text"]=variant[2];((JObject)general["facts"]).Remove("execution");((JObject)general["evidenceScopes"]).Remove("execution");
                yield return Native(model,general,"general-prerequisite-"+variant[1],v=>(string)v["action"]["operation"]=="continue");
            }
            foreach(string stop in new[]{"现在停止跟随。","今はついて来ないで。","Stop following me now."}) {
                var stopped=At(154402);stopped["facts"]["request"]["text"]=stop;
                yield return Native(model,stopped,"explicit-stop-"+stop,v=>new[]{"stop","reconsider"}.Contains((string)v["action"]["operation"]));
            }
            var wish=At(137442);wish["speech"][0]["text"]="あなたのそばにいたいな。";wish["speech"][0]["language"]="ja";
            yield return Native(model,wish,"reasonable-new-autonomous-wish",v=>(string)v["speech"]["issue"]=="none");
            SharedDelivery(); BudgetAndPlanning(model);
            Save();if(checks.Any(c=>!(bool)c["passed"]))throw new InvalidOperationException("Whole semantic window replay failed.");
        } finally {UnityEngine.Object.Destroy(host);}
    }

    static JObject ReplayWindows(ChatSample chat)
    {
        foreach(var pair in new[]{new[]{"present","current-state"},new[]{"past","past-event"},
            new[]{"future","prospective"},new[]{"atemporal","not-factual"}}) {
            var value=new JObject{["predicateTense"]=pair[0]};
            Check("one-authoritative-predicate-time-"+pair[1],ActivityMonitorProtocol.ExpandPredicateTime(value)&&(string)value["claimTime"]==pair[1]);
            value["claimTime"]="conflicting-label";Check("contradictory-time-alias-rejected-"+pair[1],!ActivityMonitorProtocol.ExpandPredicateTime(value));
        }
        var list=Get<List<ActivityMonitorProtocol.SpeechUnit>>(chat,"m_ActivitySpeech");list.Clear();
        var frames=Events("Activity/MonitorFrame").Select(e=>(JObject)e["data"]).ToList();
        JObject shelf=null;var trace=new JArray();int epoch=-1;
        foreach(var eventRow in ((JArray)fixture["dialogue"]["completedSpeech"]).OfType<JObject>()) {
            var played=(JObject)eventRow["data"];string id=(string)played["id"];
            var recorded=frames.SelectMany(f=>((JArray)f["speech"]).OfType<JObject>().Select(u=>new{f,u}))
                .FirstOrDefault(x=>(string)x.u["id"]==id && x.u["observedAtSpeechStart"] is JObject);
            int nextEpoch=int.Parse(id.Split(':')[1]);if(nextEpoch!=epoch){list.Clear();epoch=nextEpoch;}
            var unit=recorded?.u.ToObject<ActivityMonitorProtocol.SpeechUnit>()??played.ToObject<ActivityMonitorProtocol.SpeechUnit>();
            unit.Text=(string)played["text"];unit.State="completed";list.Add(unit);
            var window=(JObject)Call(chat,"ActivitySpeechWindow",unit);trace.Add(new JObject{["line"]=eventRow["line"],["window"]=window});
            if(id=="speech:3:5")shelf=(JObject)window.DeepClone();
        }
        Check("full-played-order-replayed-with-source-snapshots",trace.Count==21,trace);
        Check("same-capture-late-evidence-does-not-split-shelf",(string)shelf?["text"]=="その横には、ユウさんにとって大切な本を並べる予定の棚もあります。",shelf);
        var a=(JObject)At(137222)["speech"][0]["observedAtSpeechStart"].DeepClone();var b=(JObject)At(137442)["speech"][0]["observedAtSpeechStart"].DeepClone();
        Check("only-appended-visual-evidence-compatible",!ActivityEvidencePolicy.Equal(a,b)&&ActivitySpeechWindowPolicy.Compatible(a,b));
        foreach(string field in new[]{"actionId","entityId","formalObservation","completedResultIds","body"}) {
            var different=(JObject)b.DeepClone();different[field]=new JObject{["changed"]=true};
            Check("real-source-or-stage-change-preserved-"+field,!ActivitySpeechWindowPolicy.Compatible(a,different));
        }
        foreach(var language in new[]{new[]{"zh","我刚保存的","文档在这里。"},new[]{"ja","そこに置く予定","の本があります。"},new[]{"en","The file that you saved","is here."}}) {
            list.Clear();var first=new ActivityMonitorProtocol.SpeechUnit{Id="a",Text=language[1],State="completed",Language=language[0],ObservedAtSpeechStart=a};
            var last=new ActivityMonitorProtocol.SpeechUnit{Id="b",Text=language[2],State="playing",Language=language[0],ObservedAtSpeechStart=b};list.Add(first);list.Add(last);
            Check("bounded-clause-coalesces-"+language[0],!(bool)Call(chat,"ActivitySpeechReadyForSemanticCheck",first)&&
                ((string)((JObject)Call(chat,"ActivitySpeechWindow",last))["text"]).Contains(language[1]));
            last.State="queued";last.ObservedAtCreation=b;
            Check("queued-suffix-not-reviewed-outside-completed-prefix-"+language[0],!(bool)Call(chat,"ActivitySpeechReadyForSemanticCheck",last));
            last.State="superseded";Check("cancelled-suffix-does-not-hide-prefix-"+language[0],(bool)Call(chat,"ActivitySpeechReadyForSemanticCheck",first));
            last.State="playing";last.IsCorrection=true;Check("correction-not-merged-"+language[0],(string)((JObject)Call(chat,"ActivitySpeechWindow",last))["text"]==language[2]);
        }
        list.Clear();Check("new-owner-does-not-inherit-previous-clause",(string)((JObject)Call(chat,"ActivitySpeechWindow",shelf.ToObject<ActivityMonitorProtocol.SpeechUnit>()))["text"]==(string)shelf["text"]);
        return shelf;
    }
    static void Clarification(ChatSample chat,out JObject evidence)
    {
        Set(chat,"m_WorkEpoch",3);Set(chat,"m_FormalResponseGeneration",5);
        Call(chat,"BindTranscriptClarificationEvidence","[Unresolved audio input]\n"+At(138183).ToString(Formatting.None),5);
        evidence=(JObject)Call(chat,"ReadTranscriptClarificationEvidence");
        Check("minimal-asr-sources-preserved-no-command",(string)evidence?["stream"]?["text"]==(string)At(138183)["stream"]["text"]&&
            (string)evidence?["full"]?["source"]=="full-preview" && (bool?)evidence?["acceptedInstruction"]==false);
        Set(chat,"m_WorkEpoch",4);Check("clarification-does-not-leak-to-next-owner",Call(chat,"ReadTranscriptClarificationEvidence")==null);
    }
    static void VisualGrouping(ChatSample chat,GameObject host)
    {
        var units=Get<List<ActivityMonitorProtocol.SpeechUnit>>(chat,"m_ActivitySpeech");units.Clear();
        var frame=At(136971);var first=frame["speech"][0].ToObject<ActivityMonitorProtocol.SpeechUnit>();units.Add(first);
        foreach(int line in new[]{137222,137442}) {
            var next=At(line)["speech"][0].ToObject<ActivityMonitorProtocol.SpeechUnit>();
            next.ObservedAtCreation=(JObject)next.ObservedAtSpeechStart.DeepClone();next.ObservedAtSpeechStart=null;next.State="queued";units.Add(next);
        }
        var settings=Get<ChatSetting>(chat,"m_ChatSettings");var original=settings.m_ChatModel;
        var probe=host.AddComponent<SemanticWindowVisualProbe>();settings.m_ChatModel=probe;
        Set(chat,"m_FormalResponseGeneration",3);Set(chat,"m_ActivityReplyObservationGeneration",3);
        Set(chat,"m_ActivityReplyObservation",first.ObservedAtSpeechStart["formalObservation"] as JObject);Set(chat,"m_ActivityReplyImage","fixture-source-no-pixels");
        Get<HashSet<string>>(chat,"m_ActivityVisualAttempts").Clear();Get<Dictionary<string,JObject>>(chat,"m_ActivityVisualEvidence").Clear();
        Get<Queue<float>>(chat,"m_ActivityRequestTimes").Clear();
        try {
            var verdict=new JObject{["claimDomain"]="visual",["speechAct"]="factual-claim",["claimQuote"]=first.Text,
                ["perceptionSource"]="current-writer-image",["claimTime"]="current-state"};
            Call(chat,"TryActivityVisualRecheck",frame,verdict.ToString(Formatting.None));
            var claims=probe.Input?["claims"] as JArray;
            Check("production-visual-request-coalesces-complete-next-proposition",claims?.Count==2&&
                (string)claims[1]["speechId"]=="speech:3:5" && (string)claims[1]["claimQuote"]=="その横には、ユウさんにとって大切な本を並べる予定の棚もあります。",probe.Input);
            Check("grouped-visual-call-uses-one-original-budget-slot",probe.Calls==1&&Get<Queue<float>>(chat,"m_ActivityRequestTimes").Count==1);
        } finally {settings.m_ChatModel=original;units.Clear();UnityEngine.Object.Destroy(probe);}
    }
    static void SharedDelivery()
    {
        var delivery=(JObject)At(158215)["delivery"].DeepClone();
        var verdict=(JObject)ActivityEvidencePolicy.Parse((string)At(158328)["rawVerdict"])["delivery"];var proof=verdict["playbackProof"];
        var report=new WorkResultDeliveryProtocol.Report{id=(string)delivery["id"],coverage="reported",quote=(string)verdict["quote"],
            reportAct="outcome-report",playedRevision=(string)proof?["revision"],lastRelevantQuote=(string)proof?["lastRelevantQuote"],
            finalDisposition=(string)proof?["finalDisposition"],selectedPlaybackId=(string)proof?["selectedPlaybackId"]};
        string failure;ResultPlaybackSelection.Expand(verdict,true,delivery,out failure);
        report.quote=(string)verdict["quote"];report.lastRelevantQuote=(string)verdict["playbackProof"]?["lastRelevantQuote"];
        var cache=new ReviewedDeliveryEvidence();var packet=new JObject{["results"]=new JArray(delivery)};
        cache.Remember(8,15,packet,new[]{report});Check("actual-validated-audio-coverage-shared",cache.Take(8,15,delivery)!=null,new JObject{["result"]=delivery,["report"]=JObject.FromObject(report)});
        foreach(string field in new[]{"id","facts","playbackRevision","completedPlaybackText","completedAudio","latestCorrectionStart"}) {
            cache.Remember(8,15,packet,new[]{report});var changed=(JObject)delivery.DeepClone();changed[field]="changed";
            Check("changed-proof-not-reused-"+field,cache.Take(8,15,changed)==null);
        }
        cache.Remember(8,15,packet,new[]{report});Check("new-owner-cannot-use-delivery-check",cache.Take(9,15,delivery)==null);
        cache.Remember(8,15,packet,new[]{report});Check("new-speech-generation-cannot-use-delivery-check",cache.Take(8,17,delivery)==null);
        report.coverage="unreported";cache.Remember(8,15,packet,new[]{report});Check("unreported-not-shared-as-delivered",cache.Take(8,15,delivery)==null);
    }
    static void BudgetAndPlanning(ChatQW model)
    {
        var older=ActivityEvidencePolicy.Parse(File.ReadAllText("Assets/Editor/Fixtures/BudgetDelivery.20261017-1505.json"));
        foreach(bool interest in new[]{true,false}) {
            var wire=(JObject)older[interest?"unadmittedAutonomy":"unadmittedWork"].DeepClone();
            var messages=(JArray)wire["messages"];var content=messages.Last["content"];
            JToken text=content is JArray array?array.Single(x=>(string)x["type"]=="text")["text"]:content;
            object[] args={(string)text,interest,null};text.Replace((string)Call(typeof(ChatQW),"ProjectPrivateAttention",args));
            object[] admission={wire.ToString(Formatting.None),null};bool allowed=(bool)Call(model,interest?"AdmitAutonomyIntentWire":"AdmitWorkReviewWire",admission);
            Check("prior-complete-failure-wire-still-admitted-"+interest,allowed,JObject.FromObject(admission[1]));
            // Combine the complete previously rejected provider envelope with
            // this session's literal accepted inputs and actual played order.
            // This is a stress reconstruction, not the missing original wire.
            var timeline=((JArray)fixture["dialogue"]["inputSources"]).OfType<JObject>().Where(e=>(string)e["kind"]=="Timing")
                .Concat(((JArray)fixture["dialogue"]["completedSpeech"]).OfType<JObject>())
                .Concat(((JArray)fixture["dialogue"]["openingReceipts"]).OfType<JObject>()).OrderBy(e=>(int)e["line"]);
            var allMessages=new JArray(messages.Take(messages.Count-1).Where(m=>(string)m["role"]=="system").Select(m=>m.DeepClone()));
            foreach(var entry in timeline) {
                bool user=(string)entry["kind"]=="Timing";
                string literal=user?(string)entry["text"]:(string)entry["data"]["text"]??(string)entry["data"]["candidateText"];
                allMessages.Add(new JObject{["role"]=user?"user":"assistant",["content"]=literal});
            }
            allMessages.Add(messages.Last.DeepClone());wire["messages"]=allMessages;
            object[] fullAdmission={wire.ToString(Formatting.None),null};
            Check("reconstructed-full-latest-dialogue-budget-"+interest,(bool)Call(model,interest?"AdmitAutonomyIntentWire":"AdmitWorkReviewWire",fullAdmission),
                new JObject{["originalSuccessfulWireAvailable"]=false,["timelineCount"]=timeline.Count(),["budget"]=JObject.FromObject(fullAdmission[1])});
        }
        // Full latest dialogue remains a fixture, not fabricated provider input.
        // Exercise purpose projection with real outcomes plus custom constraints.
        var result=(JObject)At(158745)["delivery"].DeepClone();
        var runtime=new JObject{["priorTaskResults"]=result["facts"].DeepClone(),["currentRoomTask"]=result["facts"].DeepClone(),
            ["userEvidence"]=fixture["dialogue"]["inputSources"].DeepClone(),["sceneSpatialMemory"]=new JObject{["custom"]=new JArray("seen-before")},
            ["unknownConstraint"]=new string('限',30000)};
        var projected=PrivateInterestContextPolicy.Project(runtime);
        Check("new-session-full-input-memory-and-unknown-limits-retained",JToken.DeepEquals(runtime["userEvidence"],projected["userEvidence"])&&
            JToken.DeepEquals(runtime["sceneSpatialMemory"],projected["sceneSpatialMemory"])&&(string)projected["unknownConstraint"]==new string('限',30000));
        var oldContent=older["unadmittedWork"]["messages"].Last["content"];
        string oldPrompt=oldContent is JArray parts?(string)parts.Single(p=>(string)p["type"]=="text")["text"]:(string)oldContent;
        const string marker="[Historical interrupted work; reference only]";
        using(var reader=new JsonTextReader(new StringReader(oldPrompt.Substring(oldPrompt.IndexOf(marker,StringComparison.Ordinal)+marker.Length)))) {
            reader.DateParseHandling=DateParseHandling.None;var history=JObject.Load(reader);
            var input=new JObject{["currentUserText"]="Inspect the file saved earlier, then edit its title and save a copy.",["autonomous"]=false,
                ["previousWorkContext"]=history["snapshot"].DeepClone(),["unknownConstraint"]=new JObject{["keepOriginal"]=true},
                ["sceneSpatialMemory"]=new JObject{["source"]=new JArray("original-observed-anchor")}};
            var wire=new JObject{["messages"]=new JArray(new JObject{["role"]="user",["content"]=input.ToString(Formatting.None)}),
                ["max_tokens"]=640,["response_format"]=new JObject{["schema"]=new JObject{["type"]="object"}}};
            object[] args={wire.ToString(Formatting.None),false,null,null};bool allowed=(bool)Call(typeof(ChatQW),"TryBudgetRoomDecisionRequest",args);
            var diagnostic=(JObject)args[3];
            Check("historical-task-index-before-small-main-planner",allowed&&(string)diagnostic["historyProjection"]=="recallable-task-index-before-planning",
                diagnostic);
            Check("early-planning-projection-retains-limits-and-memory",((string)args[2]).Contains("keepOriginal")&&((string)args[2]).Contains("original-observed-anchor")&&
                ((string)args[2]).Contains("Inspect the file saved earlier"));
        }
        // Established whole-history budget fixtures also cover the final native
        // schema. This session has no original successful provider body/RGB.
        Check("successful-wire-and-original-rgb-not-invented",((string)fixture["scope"]).Contains("not logged"));
    }
    static IEnumerator Native(ChatQW model,JObject frame,string name,Func<JObject,bool> predicate,string expectedSourceRequest=null)
    {
        string ready=model.PrepareActivityMonitorFrame(frame.ToString(Formatting.None),out string budget);
        bool done=false,ok=false;string raw=null,status=null,error=null;JObject verdict=null;
        if(ready!=null){model.PostActivityMonitor(ready,(a,b,c)=>{done=true;ok=a;raw=b;status=c;});yield return Until(()=>done);}
        bool valid=ok&&ActivityMonitorProtocol.TryRead(raw,JObject.Parse(ready),out verdict,out error);
        if(ok && expectedSourceRequest!=null && error==expectedSourceRequest) {
            verdict=ActivityEvidencePolicy.Parse(raw);valid=ActivityMonitorProtocol.ExpandPredicateTime(verdict);
        }
        Check("native-"+name,valid&&predicate(verdict),new JObject{["frame"]=frame,["raw"]=raw,["status"]=status,["rejection"]=error,["budget"]=JObject.Parse(budget)});
    }
}

public sealed class SemanticWindowVisualProbe : LLM
{
    public JObject Input;public int Calls;
    public override void PostActivityVisualEvidence(string input,string image,Action<bool,string,string> callback)
    {Input=ActivityEvidencePolicy.Parse(input);Calls++;callback(false,"","fixture-no-image-inference");}
}
