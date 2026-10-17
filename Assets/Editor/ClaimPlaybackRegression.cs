using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class ClaimPlaybackRegression
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static JObject fixture;static string output;static readonly JArray checks=new JArray();
    static object Call(object target,string name,params object[] args)=>(target is Type t?t:target.GetType()).GetMethod(name,F).Invoke(target is Type?null:target,args);
    static void Set(object target,string name,object value)=>target.GetType().GetField(name,F).SetValue(target,value);
    static JObject At(int line)=>(JObject)((JArray)fixture["events"]).Single(e=>(int)e["line"]==line)["data"].DeepClone();
    static void Check(string name,bool passed,JToken evidence=null){checks.Add(new JObject{["name"]=name,["passed"]=passed,["evidence"]=evidence});Save();}
    static void Save()=>File.WriteAllText(Path.Combine(output,"claim-playback.json"),new JObject{["passed"]=checks.Count>0&&checks.All(c=>(bool)c["passed"]),["checks"]=checks}.ToString(Formatting.Indented));
    static IEnumerator Until(Func<bool> done){float until=Time.realtimeSinceStartup+20;while(!done()&&Time.realtimeSinceStartup<until)yield return null;}
    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string directory)
    {
        output=directory;checks.Clear();fixture=ActivityEvidencePolicy.Parse(File.ReadAllText("Assets/Editor/Fixtures/ClaimPlayback.20261017-1941.json"));
        var host=new GameObject("Claim time and playback completion replay");host.SetActive(false);var model=host.AddComponent<ChatQW>();model.enabled=false;
        model.m_PrewarmPrefixOnStart=false;model.m_Backend=ChatQW.BackendType.Local;Set(model,"url","http://127.0.0.1:8080/v1/chat/completions");host.SetActive(true);
        Set(chat,"m_AgentRunning",false);Set(chat,"m_EnableActivityMonitor",false);
        try {
            Check("all-seven-captures-six-inputs-sixteen-audio-four-openings-retained",
                fixture["dialogue"]["inputSources"].Count(e=>(string)e["kind"]=="ASR/Final")==7 &&
                fixture["dialogue"]["inputSources"].Count(e=>(string)e["kind"]=="Timing")==6 &&
                fixture["dialogue"]["completedSpeech"].Count()==16 && fixture["dialogue"]["openingReceipts"].Count()==4);
            Playback();SourceBinding();VisualBindings(chat);ReplayAudio(chat);yield return RealPlayback(chat);
            foreach(var e in ((JArray)fixture["events"]).Where(e=>new[]{"Activity/MonitorFrame","Activity/MonitorUnadmittedFrame"}.Contains((string)e["kind"]))) {
                var original=(JObject)e["data"].DeepClone();
                var packet=(string)original["purpose"]=="speech"?ActivitySpeechMeaningPolicy.Input(original):original;
                string ready=model.PrepareActivityMonitorFrame(packet.ToString(Formatting.None),out string diag);
                Check("complete-recorded-frame-budget-"+e["line"],ready!=null,JObject.Parse(diag));
            }
            yield return Native(model,At(196100),"historical-lamp-not-current-image-denial",v=>
                (string)v["claimTime"]=="past-event" && (string)v["perceptionSource"]=="earlier-observation" &&
                (string)v["speech"]["issue"]=="none");
            foreach(string text in new[]{"今、あなたのところへ向かっているわ。","我正在往你那里走。","I am walking towards you."}) {
                var frame=At(213794);frame.Remove("previousAttempt");frame["speech"][0]["text"]=text;
                frame["speech"][0]["language"]=text.StartsWith("I")?"en":text.StartsWith("我")?"zh":"ja";
                yield return Native(model,frame,"ongoing-is-factual-"+text,v=>(string)v["claimTime"]=="current-state"&&
                    (string)v["speechAct"]=="factual-claim"&&(string)v["speech"]["issue"]!="mistake");
            }
            foreach(string text in new[]{"我想陪你走一会儿。","一緒に歌いたい。","Would you like me to observe the garden?"}) {
                var frame=At(213794);frame["speech"][0]["text"]=text;
                yield return Native(model,frame,"reasonable-wish-remains-free-"+text,v=>(string)v["speech"]["issue"]=="none"&&
                    new[]{"personal-expression","invitation-or-question"}.Contains((string)v["speechAct"]));
            }
            yield return Native(model,At(202748),"actual-off-return-supplement-covered",v=>(string)v["delivery"]["operation"]=="covered");
            yield return Native(model,At(213251),"actual-on-return-supplement-covered",v=>(string)v["delivery"]["operation"]=="covered");
            foreach(var pair in new[]{new[]{"generate","我正在生成摘要。"},new[]{"observe","今、周囲を観察しています。"},new[]{"export","I am exporting the file."}}) {
                var f=At(213794);f.Remove("previousAttempt");f["speech"][0]["text"]=pair[1];
                f["facts"]["request"]["text"]="Complete the current "+pair[0]+" task.";
                foreach(var prop in f.Descendants().OfType<JProperty>().Where(p=>p.Value.Type==JTokenType.String && (string)p.Value=="approach").ToArray())prop.Value=pair[0];
                yield return Native(model,f,"general-ongoing-"+pair[0],v=>(string)v["speechAct"]=="factual-claim"&&(string)v["speech"]["issue"]!="mistake");
            }
            foreach(string text in new[]{"文件已保存，我正在生成摘要。","保存は終わりました。一緒に歌いたい。","I have saved the file, and I am exporting it now.","Maybe the book is behind the screen."})
                yield return LiteralOnly(model,text);
            foreach(var e in ((JArray)fixture["events"]).Where(e=>(string)e["kind"]=="Activity/MonitorFrame" && (string)e["data"]?["purpose"]=="speech")
                .GroupBy(e=>ActivityEvidencePolicy.Stamp(e["data"]["speech"][0]["text"])).Select(g=>g.First()))
                yield return RecordedMeaning(model,(JObject)e["data"],(int)e["line"]);
            yield return Visual(model);
            Save();if(checks.Any(c=>!(bool)c["passed"]))throw new InvalidOperationException("Claim/playback regression failed.");
        }finally{UnityEngine.Object.Destroy(host);}
    }
    static void ReplayAudio(ChatSample chat)
    {
        object Call(string method,params object[] args)=>chat.GetType().GetMethod(method,F).Invoke(chat,args);
        T Get<T>(string name)=>(T)chat.GetType().GetField(name,F).GetValue(chat);
        Call("EnsureWorkDeliveryOwner");var results=Get<Dictionary<string,JObject>>("m_WorkToolResultReceipts");results.Clear();
        var events=(JArray)fixture["events"];
        var frames=events.Where(e=>(string)e["kind"]=="Activity/MonitorFrame").Select(e=>e["data"] as JObject).Where(f=>f?["delivery"] is JObject).ToArray();
        var inputs=new HashSet<int>(((JArray)fixture["dialogue"]["inputSources"]).Where(e=>(string)e["kind"]=="Timing").Select(e=>(int)e["line"]));
        var supplements=new Dictionary<string,string>();var trace=new JArray();int completed=0;
        foreach(var e in events.OfType<JObject>()) {
            string kind=(string)e["kind"];var d=e["data"] as JObject;
            if(inputs.Contains((int)e["line"])) {Set(chat,"m_WorkEpoch",Get<int>("m_WorkEpoch")+1);Call("EnsureWorkDeliveryOwner");supplements.Clear();}
            if(kind=="Work/StepSettled") {
                var recorded=frames.FirstOrDefault(f=>(int?)f["owner"]?["workEpoch"]==(int?)d["workEpoch"]&&(string)f["delivery"]?["source"]=="step:"+(string)d["stepId"])?["delivery"] as JObject;
                if(recorded!=null)results[(string)recorded["id"]]=new JObject{["id"]=recorded["id"],["source"]=recorded["source"],["status"]=recorded["status"],["facts"]=recorded["facts"].DeepClone()};
            }
            if(kind=="Activity/Delivery"&&(string)d["operation"]=="supplement"&&d["rejection"]?.Type==JTokenType.Null)supplements[(string)d["verdict"]["text"]]=(string)d["id"];
            if(kind!="Activity/SpeechProgress")continue;
            string state=(string)d["state"],text=(string)d["text"];
            if(state=="completed") {
                supplements.TryGetValue(text,out string scope);
                var unit=new ActivityMonitorProtocol.SpeechUnit{Id=(string)d["id"],Text=text,Language=(string)d["language"],State=state,Version=(int)d["version"]};
                Set(chat,"m_FormalResponseGeneration",int.Parse(unit.Id.Split(':')[1]));
                Call("RecordWorkCompletedSpeechForResult",text,scope);Call("RecordWorkCompletedAudio",text,scope,unit,unit.Version);completed++;
                trace.Add(new JObject{["line"]=e["line"],["id"]=unit.Id,["result"]=scope,["receipt"]=(JObject)Call("ReadWorkDelivery")});
            }
        }
        Check("full-six-turn-audio-order-replayed",completed==16,trace);
        var live=(JObject)Call("ReadWorkDelivery");
        var expected=At(213251)["delivery"];
        var actual=((JArray)live["results"]).FirstOrDefault(r=>(string)r["id"]==(string)expected["id"]);
        Check("last-result-runtime-audio-matches-recorded-order",actual!=null &&
            (string)actual["completedPlaybackText"]==(string)expected["completedPlaybackText"],new JObject{["actual"]=actual,["expected"]=expected});
        Check("interrupted-progress-preserved-as-incomplete-in-fixture",events.Any(e=>(string)e["kind"]=="Activity/SpeechProgress" &&
            (string)e["data"]?["state"]=="interrupted" && (bool?)e["data"]?["wholeTextPlayed"]==false));
    }
    static IEnumerator RealPlayback(ChatSample chat)
    {
        object Call(string method,params object[] args)=>chat.GetType().GetMethod(method,F).Invoke(chat,args);
        var field=chat.GetType().GetField("m_AudioSource",F);var original=field.GetValue(chat);
        var host=new GameObject("Silent local playback receipt check");var source=host.AddComponent<AudioSource>();
        var clip=AudioClip.Create("silent-completion-test",16000,1,32000,false);
        source.clip=clip;field.SetValue(chat,source);int generation=(int)chat.GetType().GetField("m_FormalResponseGeneration",F).GetValue(chat);
        var finalType=typeof(ChatSample).GetNestedType("SceneFinalPlaybackWindow",BindingFlags.NonPublic);
        var reviewType=typeof(ChatSample).GetNestedType("ScenePlaybackReview",BindingFlags.NonPublic);
        object final=Activator.CreateInstance(finalType,true),review=Activator.CreateInstance(reviewType,true);
        void Member(object obj,string key,object value)=>obj.GetType().GetField(key,F).SetValue(obj,value);
        var chatSettings=chat.GetType().GetField("m_ChatSettings",F).GetValue(chat);
        var provider=chatSettings.GetType().GetField("m_TextToSpeech",F).GetValue(chatSettings);
        Member(final,"generation",generation);Member(final,"provider",provider);Member(final,"audio",source);Member(final,"clip",clip);Member(final,"buffered",true);
        Member(review,"formalGeneration",generation);Member(review,"audio",source);Member(review,"finalPlayback",final);
        Set(chat,"m_SceneFinalPlayback",final);
        try {
            Call("BeginPreparedPlaybackEvidence",clip,generation);source.Play();
            yield return new WaitForSecondsRealtime(.15f);Call("ObservePreparedPlaybackEvidence");
            yield return new WaitForSecondsRealtime(.45f);
            Check("pending-review-uses-same-natural-drain-evidence",(bool)Call("ScenePlaybackAudioCurrent",review));
            Check("probing-does-not-publish-drained-handoff",!(bool)finalType.GetField("drained",F).GetValue(final));
            Check("real-audio-source-tail-gap-completes",!source.isPlaying&&(bool)Call("FinishPreparedPlaybackEvidence",clip,generation));
            Call("CompleteFinalScenePlayback",generation,clip,true);
            Check("review-and-player-share-successful-terminal",(bool)finalType.GetField("drained",F).GetValue(final));
            Member(final,"drained",false);
            Call("BeginPreparedPlaybackEvidence",clip,generation);source.Play();
            yield return new WaitForSecondsRealtime(.1f);Call("ObservePreparedPlaybackEvidence");Call("StopSpeechAudio","test-user-interruption");
            yield return new WaitForSecondsRealtime(.5f);
            Check("real-explicit-stop-stays-incomplete",!(bool)Call("FinishPreparedPlaybackEvidence",clip,generation));
            Check("pending-review-cannot-outlive-explicit-stop",!(bool)Call("ScenePlaybackAudioCurrent",review));
        }finally{source.Stop();Set(chat,"m_SceneFinalPlayback",null);field.SetValue(chat,original);UnityEngine.Object.Destroy(clip);UnityEngine.Object.Destroy(host);}
    }
    static IEnumerator LiteralOnly(ChatQW model,string text)
    {
        var f=At(213794);f["speech"][0]["text"]=text;f["facts"]["request"]["text"]="Save the document, then generate and export the summary.";
        foreach(var prop in f.Descendants().OfType<JProperty>().Where(p=>p.Value.Type==JTokenType.String && new[]{"approach","on"}.Contains((string)p.Value)).ToArray())prop.Value=(string)prop.Value=="on"?"save":text.Contains("export")?"export":"generate";
        var input=ActivitySpeechMeaningPolicy.Input(f);bool done=false,ok=false;string raw=null;JObject result=null;
        string ready=model.PrepareActivityMonitorFrame(input.ToString(Formatting.None),out string diag);
        if(ready!=null){model.PostActivityMonitor(ready,(a,b,c)=>{done=true;ok=a;raw=b;});yield return Until(()=>done);}
        bool valid=ok&&ActivitySpeechMeaningPolicy.TryRead(raw,input,out result);
        if(!valid && ok) {
            string rejected=raw;
            input["previousAttempt"]=new JObject{["rejectedVerdict"]=raw,["fieldRepair"]=ActivitySpeechMeaningPolicy.Rejection(raw,input)};
            done=false;ok=false;ready=model.PrepareActivityMonitorFrame(input.ToString(Formatting.None),out diag);
            if(ready!=null){model.PostActivityMonitor(ready,(a,b,c)=>{done=true;ok=a;raw=b;});yield return Until(()=>done);}
            valid=ok&&ActivitySpeechMeaningPolicy.TryRead(raw,input,out result);
            Check("bounded-literal-repair-"+text,valid,new JObject{["rejected"]=rejected,["raw"]=raw,["budget"]=JObject.Parse(diag)});
        }
        Check("general-literal-"+text,valid,new JObject{["raw"]=raw,["budget"]=JObject.Parse(diag)});
        if(valid) {
            var frames=ActivitySpeechMeaningPolicy.Frames(f,result).ToArray();
            Check("all-general-clauses-kept-"+text,frames.Length==(text.StartsWith("Maybe")?1:2),result);
            foreach(var packet in frames) {
                string full=model.PrepareActivityMonitorFrame(packet.ToString(Formatting.None),out string budget);
                Check("general-complete-evidence-budget-"+text+packet["claimIndex"],full!=null,JObject.Parse(budget));
            }
        }
    }
    static IEnumerator RecordedMeaning(ChatQW model,JObject frame,int line)
    {
        var input=ActivitySpeechMeaningPolicy.Input(frame);JObject meaning=null;string raw=null;var attempts=new JArray();bool valid=false;
        for(int i=0;i<2&&!valid;i++) {
            bool done=false,ok=false;string ready=model.PrepareActivityMonitorFrame(input.ToString(Formatting.None),out string diag);
            if(ready!=null){model.PostActivityMonitor(ready,(a,b,c)=>{done=true;ok=a;raw=b;});yield return Until(()=>done);}
            valid=ok&&ActivitySpeechMeaningPolicy.TryRead(raw,input,out meaning);
            attempts.Add(new JObject{["raw"]=raw,["budget"]=JObject.Parse(diag),["valid"]=valid});
            if(!valid)input["previousAttempt"]=new JObject{["rejectedVerdict"]=raw,["fieldRepair"]=ActivitySpeechMeaningPolicy.Rejection(raw,input)};
        }
        Check("recorded-complete-utterance-"+line,valid,attempts);
        if(valid)foreach(var f in ActivitySpeechMeaningPolicy.Frames(frame,meaning)) {
            string ready=model.PrepareActivityMonitorFrame(f.ToString(Formatting.None),out string diag);
            Check("recorded-complete-claim-request-"+line+"-"+f["claimIndex"],ready!=null,JObject.Parse(diag));
        }
    }
    static void VisualBindings(ChatSample chat)
    {
        var frame=At(189742);string id=(string)frame["speech"][0]["id"];
        var rows=new JArray(new JObject{["speechId"]=id,["captureId"]="same-picture",["claimQuote"]="The screen is red.",["assessment"]="supported",["finding"]="A red screen is visible."},
            new JObject{["speechId"]=id,["captureId"]="same-picture",["claimQuote"]="The book titles are readable.",["assessment"]="unresolved",["finding"]="Book titles cannot be read."});
        chat.GetType().GetMethod("RememberActivityVisualEvidence",F).Invoke(chat,new object[]{rows});
        var store=(Dictionary<string,JObject>)chat.GetType().GetField("m_ActivityVisualEvidence",F).GetValue(chat);
        Check("one-audio-unit-keeps-two-distinct-visual-propositions",store.Count==2);
        foreach(var item in store){frame["facts"]["visual:"+item.Key]=item.Value.DeepClone();frame["evidenceScopes"]["visual:"+item.Key]=ActivityMonitorContextPolicy.Scope("visual","uncertainty");}
        foreach(var row in rows) {
            var selected=(JObject)frame.DeepClone();selected["speechMeaning"]=new JObject{["domain"]="visual",["quote"]=row["claimQuote"]};
            selected=ActivityMonitorContextPolicy.Project(selected,0);
            var evidence=((JObject)selected["facts"]).Properties().Where(p=>p.Name.StartsWith("visual:")).ToArray();
            Check("visual-proof-isolated-to-its-own-proposition-"+row["claimQuote"],evidence.Length==1 && evidence[0].Name=="visual:"+id &&
                (string)evidence[0].Value["finding"]==(string)row["finding"]);
        }
        store.Clear();
        var unresolved=At(213794);unresolved.Remove("reviewOperation");
        unresolved["speechMeaning"]=new JObject{["quote"]=unresolved["speech"][0]["text"],["predicate"]="向かっている",["tense"]="present",
            ["act"]="factual-claim",["domain"]="execution",["stage"]="ongoing",["subject"]="character",["source"]="not-applicable",["qualifier"]="",["operation"]="unresolved"};
        var schema=JObject.Parse(ActivityMonitorProtocol.SchemaForFrame(ActivityMonitorContextPolicy.Project(unresolved,0)));
        Check("unresolved-operation-cannot-borrow-another-outcome-to-correct",JToken.DeepEquals(schema.SelectToken("properties.speech.properties.issue.enum"),new JArray("none")) &&
            schema.SelectTokens("properties.grounding.anyOf[*].properties.truthWhenSaid.enum[*]").Values<string>().All(v=>v=="unknown"));
    }
    static IEnumerator Visual(ChatQW model)
    {
        var t=new Texture2D(64,64);t.SetPixels(Enumerable.Repeat(Color.red,4096).ToArray());t.Apply();
        string image="data:image/png;base64,"+Convert.ToBase64String(t.EncodeToPNG());UnityEngine.Object.Destroy(t);
        var input=new JObject{["captureId"]="synthetic-red-control",["speech"]="The image is red and the book titles are readable.",
            ["claims"]=new JArray(new JObject{["id"]="c0",["speechId"]="synthetic:1",["claimQuote"]="The image is red"},
                new JObject{["id"]="c1",["speechId"]="synthetic:1",["claimQuote"]="the book titles are readable."})};
        bool done=false,ok=false;string raw=null;JArray evidence=null;
        model.PostActivityVisualEvidence(input.ToString(Formatting.None),image,(a,b,c)=>{done=true;ok=a;raw=b;});yield return Until(()=>done);
        Check("native-visual-distinct-proposition-assessment",ok&&ActivityVisualEvidenceProtocol.TryReadGroup(raw,input,out evidence)&&
            evidence.Count==2&&(string)evidence[0]["assessment"]=="supported"&&(string)evidence[1]["assessment"]!="supported",new JObject{["raw"]=raw,["evidence"]=evidence});
    }
    static void Playback()
    {
        PreparedPlaybackEvidence New()=>new PreparedPlaybackEvidence(88960,32000,10,20,1);
        var p=New();p.Observe(83968,12.624,22.624,1);
        Check("recorded-tail-gap-natural-dsp-drain",(bool)p.Finish(12.81,22.81,true,false)["complete"],p.Finish(12.81,22.81,true,false));
        p=New();p.Observe(83968,12.624,22.624,1);p.Revoke("user-started-speaking");
        Check("explicit-stop-cannot-become-complete-after-clock-advances",!(bool)p.Finish(13,23,true,false)["complete"]);
        p=New();p.Observe(40000,11.25,21.25,1);
        Check("early-stop-remains-incomplete",!(bool)p.Finish(11.3,21.3,true,false)["complete"]);
        Check("unknown-long-gap-is-not-invented-playback",!(bool)p.Finish(20,30,true,false)["complete"]);
        p=New();p.Observe(83968,12.624,22.624,1);
        Check("replacement-owner-cannot-complete-old-audio",!(bool)p.Finish(12.81,22.81,false,false)["complete"]);
        Check("paused-audio-not-complete",!(bool)p.Finish(12.81,22.81,true,true)["complete"]);
        p.Revoke("application-paused");Check("resume-does-not-erase-pause",!(bool)p.Finish(12.81,22.81,true,false)["complete"]);
        p=New();Check("elapsed-time-without-advancement-is-not-delivery",!(bool)p.Finish(13,23,true,false)["complete"]);
        p=New();p.Observe(40000,11.25,21.25,1);p.Observe(20000,12,22,1);
        Check("seek-or-restart-invalidates-completion",!(bool)p.Finish(13,23,true,false)["complete"]);
    }
    static void SourceBinding()
    {
        var frame=At(196100);var bad=new JObject{["claimTime"]="past-event",["claimDomain"]="visual",["perceptionSource"]="current-writer-image"};
        Check("historical-question-cannot-use-incidental-current-image",!ActivityClaimSourcePolicy.CurrentImageAllowed(frame,bad));
        for(int i=0;i<=2;i++)Check("source-binding-survives-projection-"+i,!ActivityClaimSourcePolicy.CurrentImageAllowed(ActivityMonitorContextPolicy.Project(frame,i),bad));
        var misread=(JObject)frame.DeepClone();misread["speechMeaning"]=new JObject{["domain"]="visual",["quote"]=misread["speech"][0]["text"]};
        for(int i=0;i<=2;i++)Check("source-binding-survives-wrong-domain-projection-"+i,
            !ActivityClaimSourcePolicy.CurrentImageAllowed(ActivityMonitorContextPolicy.Project(misread,i),bad));
        bad["claimTime"]="current-state";Check("fresh-current-visual-claim-still-checkable",ActivityClaimSourcePolicy.CurrentImageAllowed(frame,bad));
        bad["claimTime"]="past-event";bad["claimDomain"]="history";Check("history-domain-cannot-use-current-image",!ActivityClaimSourcePolicy.CurrentImageAllowed(frame,bad));
        frame["facts"]["request"]["text"]="I have just inspected this new image.";bad["claimDomain"]="visual";
        Check("past-tense-new-inspection-not-globally-banned",ActivityClaimSourcePolicy.CurrentImageAllowed(frame,bad));
        var input=ActivitySpeechMeaningPolicy.Input(frame);input["utterance"]="你之前没有保存过这份笔记。";
        var reading=new JObject{["coverage"]="complete",["claims"]=new JArray(new JObject{
            ["quote"]=input["utterance"],["predicate"]="没有保存过",["tense"]="past",["act"]="factual-claim",["qualifier"]="",
            ["subject"]="user",["domain"]="execution",["stage"]="not-performed",["operation"]="not-applicable",["source"]="earlier-observation"})};
        Check("historical-source-not-silently-erased-by-execution-label",!ActivitySpeechMeaningPolicy.TryRead(reading.ToString(),input,out _));
        reading["claims"][0]["domain"]="history";
        Check("historical-action-retains-source-and-user-subject",ActivitySpeechMeaningPolicy.TryRead(reading.ToString(),input,out var understood)&&
            (string)understood["claims"][0]["source"]=="earlier-observation"&&(string)understood["claims"][0]["subject"]=="user");
        var history=(JObject)frame.DeepClone();history["speech"][0]["text"]=input["utterance"];
        history["facts"]["historyAnswers"]=new JObject{["eventHistory"]=new JArray()};
        history["evidenceScopes"]["historyAnswers"]=ActivityMonitorContextPolicy.Scope("history","uncertainty");
        history=ActivitySpeechMeaningPolicy.Frames(history,understood).Single();
        history["previousAttempt"]=new JObject{["historicalObservation"]=true,["requireHistoricalUnknownConsistency"]=true};
        var grammar=JObject.Parse(ActivityMonitorProtocol.SchemaForFrame(ActivityMonitorContextPolicy.Project(history,0)));
        Check("historical-repair-has-no-impossible-native-grammar-branches",!grammar.DescendantsAndSelf().OfType<JProperty>()
            .Any(p=>new[]{"anyOf","enum"}.Contains(p.Name)&&p.Value is JArray a&&a.Count==0));
        var unrelated=(JObject)history.DeepClone();unrelated.Remove("previousAttempt");unrelated["speechMeaning"]["domain"]="other";
        var noAuthority=JObject.Parse(ActivityMonitorProtocol.SchemaForFrame(ActivityMonitorContextPolicy.Project(unrelated,0)));
        Check("request-cannot-authorize-correction-of-unrelated-assertion",noAuthority.DescendantsAndSelf().OfType<JProperty>()
            .Where(p=>p.Name=="issue").All(p=>JToken.DeepEquals(p.Value["enum"],new JArray("none"))));
        var saved=new JObject{["actionId"]="save-1",["operation"]="save",["entity"]="document",["actor"]="character"};
        var exporting=new JObject{["actionId"]="export-2",["intent"]="export",["body"]=new JObject{["phase"]="running"}};
        var snapshot=(JObject)exporting.DeepClone();snapshot["completedResultIds"]=new JArray("saved");
        var operation=new JObject{["source"]="result:saved",["claimKey"]="save",["binding"]=saved.DeepClone()};
        var bound=new JObject{["evidenceVersion"]=2,["purpose"]="speech",["actions"]=new JArray(),
            ["facts"]=new JObject{["execution"]=exporting,["result:saved"]=new JObject{["status"]="completed"}},
            ["evidenceScopes"]=new JObject{["result:saved"]=new JObject{["domain"]="execution",["issues"]=new JArray("mistake"),["binding"]=saved}},
            ["speech"]=new JArray(new JObject{["id"]="speech:save",["text"]="The file is saved.",["observedAtSpeechStart"]=snapshot}),
            ["speechMeaning"]=new JObject{["domain"]="execution",["operationBinding"]=operation},
            ["reviewOperation"]=new JObject{["evidenceId"]="result:saved",["binding"]=saved.DeepClone()}};
        for(int pressure=0;pressure<=2;pressure++) {
            var projected=ActivityMonitorContextPolicy.Project(bound,pressure);
            Check("different-step-state-omitted-without-losing-frozen-result-"+pressure,
                projected["facts"]["execution"]==null&&projected["facts"]["speech-start"]==null&&
                (string)projected["facts"]["result:saved"]["status"]=="completed"&&
                projected["speech"][0]["observedAtSpeechStart"]["completedResultIds"].Values<string>().Contains("saved"));
        }
        Check("projection-leaves-runtime-other-operation-intact",bound["facts"]["execution"]!=null&&snapshot["actionId"]!=null);
    }
    static IEnumerator Native(ChatQW model,JObject source,string name,Func<JObject,bool> expected)
    {
        var frame=(JObject)source.DeepClone();JObject verdict=null;string raw=null,status=null,error=null;bool done=false,ok=false;
        if((string)frame["purpose"]=="speech") {
            var linguistic=ActivitySpeechMeaningPolicy.Input(frame);JObject meaning=null;
            var attempts=new JArray();bool understood=false;
            for(int i=0;i<2&&!understood;i++) {
                string read=model.PrepareActivityMonitorFrame(linguistic.ToString(Formatting.None),out string readBudget);done=false;ok=false;
                if(read!=null){model.PostActivityMonitor(read,(a,b,c)=>{done=true;ok=a;raw=b;status=c;});yield return Until(()=>done);}
                understood=ok&&ActivitySpeechMeaningPolicy.TryRead(raw,linguistic,out meaning);
                attempts.Add(new JObject{["input"]=linguistic.DeepClone(),["raw"]=raw,["budget"]=JObject.Parse(readBudget),["accepted"]=understood});
                if(!understood)linguistic["previousAttempt"]=new JObject{["rejectedVerdict"]=raw,["fieldRepair"]=ActivitySpeechMeaningPolicy.Rejection(raw,linguistic)};
            }
            Check("native-literal-"+name,understood,attempts);
            if(!understood)yield break;
            var selected=ActivitySpeechMeaningPolicy.Frames(frame,meaning).ToArray();
            Check("atomic-claim-count-"+name,selected.Length==1,meaning);
            frame=selected[0];done=false;ok=false;raw=null;status=null;
        }
        string ready=model.PrepareActivityMonitorFrame(frame.ToString(Formatting.None),out string budget);
        if(ready!=null) {
            frame=JObject.Parse(ready);model.PostActivityMonitor(ready,(a,b,c)=>{done=true;ok=a;raw=b;status=c;});yield return Until(()=>done);
        }
        bool valid=ok&&ActivityMonitorProtocol.TryRead(raw,frame,out verdict,out error)&&expected(verdict);
        Check("native-"+name,valid,new JObject{["raw"]=raw,["status"]=status,["error"]=error,["budget"]=JObject.Parse(budget),["decision"]=verdict});
        if(valid && ActivitySpeechMeaningPolicy.KnownOngoingTruth(frame)=="supported" && (string)verdict["speech"]["issue"]=="none") {
            frame["speech"][0]["state"]="completed";
            var cache=new ActivityCompletedCheckCache();cache.Remember(frame,verdict);
            var restored=(JObject)frame.DeepClone();var unit=(JObject)restored["speech"][0];
            unit["observedAtSpeechStart"]=ActivityEvidencePolicy.FrozenSpeech(unit,restored);
            ((JObject)restored["facts"]).Remove("speech-start");((JObject)restored["evidenceScopes"]).Remove("speech-start");
            Check("supported-progress-cache-survives-wire-alias-"+name,cache.Contains(restored));
            restored["facts"]["observation"]=new JObject{["differentFrame"]=true};
            Check("unrelated-image-cannot-repeat-progress-check-"+name,cache.Contains(restored));
            restored["speech"][0]["version"]=(int)restored["speech"][0]["version"]+1;
            Check("new-word-version-still-needs-check-"+name,!cache.Contains(restored));
        }
    }
}
