using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class AudioSourceRegression
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static readonly JArray checks=new JArray();static string output;static JObject fixture;
    static object Call(object t,string name,params object[] args)=>(t is Type type?type:t.GetType()).GetMethod(name,F).Invoke(t is Type?null:t,args);
    static void Set(object t,string name,object value)=>t.GetType().GetField(name,F).SetValue(t,value);
    static T Get<T>(object t,string name)=>(T)t.GetType().GetField(name,F).GetValue(t);
    static void Check(string name,bool passed,JToken evidence=null) {
        checks.Add(new JObject{["name"]=name,["passed"]=passed,["evidence"]=evidence});Save();
    }
    static void Save()=>File.WriteAllText(Path.Combine(output,"audio-source.json"),new JObject{
        ["passed"]=checks.Count>0&&checks.All(c=>(bool)c["passed"]),["checks"]=checks}.ToString(Formatting.Indented));
    static IEnumerable<JObject> Events(string kind)=>((JArray)fixture["events"]).OfType<JObject>().Where(e=>(string)e["kind"]==kind);
    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string directory)
    {
        output=directory;checks.Clear();fixture=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/AudioSource.20261017-2129.json"));
        Set(chat,"m_AgentRunning",false);Set(chat,"m_EnableActivityMonitor",false);
        Check("six-full-inputs-sixteen-normal-starts-fifteen-completions-four-openings",
            fixture["dialogue"]["inputSources"].Count(e=>(string)e["kind"]=="Timing")==6 &&
            Events("Activity/SpeechProgress").Count(e=>(string)e["data"]?["state"]=="starting")==16 &&
            fixture["dialogue"]["completedSpeech"].Count()==15 && fixture["dialogue"]["openingReceipts"].Count()==4);
        Check("raw-error-outputs-cancellations-and-dsp-retained",Events("Activity/MonitorWire").Any() &&
            Events("TTS/LocalPlaybackTerminal").Any(e=>(int)e["line"]==229804) &&
            Events("Activity/SpeechMeaning").Any(e=>(bool?)e["data"]?["accepted"]==false));
        SampleBoundaries();Queue();ReplayActualAudio(chat);
        PreparationAcknowledgements(chat);
        yield return OpeningHandoff(chat);
        yield return CompletionCancellation(chat);
        // Prior real local WAV test exercises the review probe and the consumer
        // against the same frozen terminal without publishing an early handoff.
        typeof(ClaimPlaybackRegression).GetField("output",F).SetValue(null,output);
        ((JArray)typeof(ClaimPlaybackRegression).GetField("checks",F).GetValue(null)).Clear();
        yield return (IEnumerator)Call(typeof(ClaimPlaybackRegression),"RealPlayback",chat);
        foreach(var c in (JArray)typeof(ClaimPlaybackRegression).GetField("checks",F).GetValue(null))
            Check("real-consumer-"+(string)c["name"],(bool)c["passed"],c["evidence"]?.DeepClone());
        SourceRuntime(chat);Cache();
        var host=new GameObject("Full audio source replay model");host.SetActive(false);var model=host.AddComponent<ChatQW>();model.enabled=false;
        model.m_PrewarmPrefixOnStart=false;model.m_Backend=ChatQW.BackendType.Local;Set(model,"url","http://127.0.0.1:8080/v1/chat/completions");host.SetActive(true);
        try {
            yield return NativeSourceQueue(chat,model);
            foreach(var e in Events("Activity/MonitorFrame")) {
                var f=(JObject)e["data"].DeepClone();
                string ready=model.PrepareActivityMonitorFrame(f.ToString(Formatting.None),out string diag);
                Check("recorded-full-frame-budget-"+e["line"],ready!=null,JObject.Parse(diag));
            }
            foreach(var e in Events("Activity/MonitorFrame").Where(e=>new[]{"speech","speech-meaning"}.Contains((string)e["data"]?["purpose"]))
                .GroupBy(e=>(string)e["data"]?["utterance"]??(string)e["data"]?["speech"]?[0]?["text"]).Select(g=>g.First()))
                yield return Meaning(model,(JObject)e["data"].DeepClone(),"recorded-"+e["line"]);
            var template=Events("Activity/MonitorFrame").First(e=>(string)e["data"]?["purpose"]=="speech")["data"] as JObject;
            foreach(string text in new[]{"我想陪你走一会儿。","一緒に歌いたい。","Would you like me to keep observing?",
                "可能是书架后的光。","I have saved the file, and I would like to generate a summary.",
                "検索は終わりました。今、メモを保存しています。","我正在观察，过一会儿再告诉你结果。",
                "左侧还有书架。","Books on the left, too.","右には窓も。","我先确认一下。","まず確認するね。","Let me check first."}) {
                var f=(JObject)template.DeepClone();f.Remove("speechMeaning");f["speech"][0]["text"]=text;
                yield return Meaning(model,f,"general-"+text);
            }
        } finally {UnityEngine.Object.Destroy(host);}
        Save();if(checks.Any(c=>!(bool)c["passed"]))throw new InvalidOperationException("Audio/source regression failed.");
    }
    static void SampleBoundaries()
    {
        foreach(var e in Events("TTS/LocalPlaybackTerminal")) {
            var r=(JObject)e["data"];double end=(double)r["elapsedDsp"],real=(double)r["elapsedRealtime"];
            var p=new PreparedPlaybackEvidence((int)r["totalSamples"],(int)r["frequency"],0,0,(double)r["pitch"],.02);
            p.Observe((int)r["lastPlayingSample"],end-(double)r["dspGap"],real-(double)r["realtimeGap"],(double)r["pitch"]);
            string reason=(string)r["reason"];
            if(!new[]{"observed-dsp-drain","stopped-before-observed-drain"}.Contains(reason))p.Revoke(reason);
            var result=p.Freeze(end,real,(bool)r["ownerCurrent"],(bool)r["paused"]);
            bool expected=(bool)r["complete"]||(int)e["line"]==229804;
            Check("recorded-terminal-"+e["line"],(bool)result["complete"]==expected,new JObject{["original"]=r.DeepClone(),["current"]=result});
            Check("terminal-frozen-"+e["line"],JToken.DeepEquals(result,p.Freeze(end+50,real+50,true,false)));
        }
        foreach(int rate in new[]{24000,32000,44100,48000})foreach(double pitch in new[]{.75,1,1.25}) {
            double quantum=1024d/48000;int total=rate*2;double duration=2/pitch;
            var p=new PreparedPlaybackEvidence(total,rate,0,0,pitch,quantum);
            double end=duration-quantum;
            p.Observe(total-(int)Math.Ceiling(2*quantum*rate*pitch),end-quantum,end-quantum,pitch);
            Check("sample-quantum-boundary-"+rate+"-"+pitch,(bool)p.Freeze(end,duration,true,false)["complete"]);
            foreach(string reason in new[]{"explicit-stop","paused","new-turn","owner-lost"}) {
                var cancelled=new PreparedPlaybackEvidence(total,rate,0,0,pitch,quantum);
                cancelled.Observe(total-1,duration-quantum,duration-quantum,pitch);cancelled.Revoke(reason);
                Check("revocation-"+reason+"-"+rate+"-"+pitch,!(bool)cancelled.Freeze(duration,duration,true,false)["complete"]);
            }
        }
        var early=new PreparedPlaybackEvidence(64000,32000,0,0,1);early.Observe(32000,1,1,1);
        var frozen=early.Freeze(1.01,1.01,true,false);
        Check("early-stop-cannot-mature-with-later-review",!(bool)frozen["complete"]&&JToken.DeepEquals(frozen,early.Freeze(2,2,true,false)));
    }
    static void PreparationAcknowledgements(ChatSample chat)
    {
        var host=new GameObject("Cached preparation acknowledgement");host.SetActive(false);
        var tts=host.AddComponent<GPTSoVITSFASTAPI>();tts.enabled=false;var source=host.AddComponent<AudioSource>();source.playOnAwake=false;
        var clip=AudioClip.Create("silent cached acknowledgement",6400,1,32000,false);
        var settings=Get<ChatSetting>(chat,"m_ChatSettings");var oldTts=settings.m_TextToSpeech;var oldAudio=Get<AudioSource>(chat,"m_AudioSource");
        var cached=(System.Collections.IDictionary)typeof(GPTSoVITSFASTAPI).GetField("m_LatencyFillerClips",F).GetValue(tts);
        try {
            foreach(var pair in new[]{new[]{"zh","中文"},new[]{"ja","日文"},new[]{"en","英文"}}) {
                var language=Enum.Parse(typeof(GPTSoVITSFASTAPI.Language),pair[1]);Call(tts,"EnsureLatencyFillerEntries",language);
                foreach(var entry in (IEnumerable)cached[language])if((string)entry.GetType().GetField("Context",F).GetValue(entry)=="preparing-request")
                    entry.GetType().GetField("Clip",F).SetValue(entry,clip);
                bool started=tts.TryPlayLatencyFiller(pair[0],"preparing-request",source,out string text,out float duration);
                Check("cached-preparation-language-"+pair[0],started&&text==(pair[0]=="zh"?"我先确认一下。":pair[0]=="ja"?"まず確認するね。":"Let me check first."));source.Stop();
                Check("unavailable-accepted-cache-does-not-borrow-preparation",!tts.TryPlayLatencyFiller(pair[0],"accepted-task",source,out _,out _));
            }
            settings.m_TextToSpeech=tts;Set(chat,"m_AudioSource",source);host.SetActive(true);source.Stop();
            Set(chat,"m_EnableLatencyFiller",true);Set(chat,"m_FormalResponseGeneration",401);Set(chat,"m_AgentCurrentRoundIsTick",false);
            Set(chat,"m_RealFirstAudioStarted",false);Set(chat,"m_LatencyFillerPlayed",false);Set(chat,"m_AcceptedTaskAcknowledgementGeneration",-1);
            Call(chat,"AcknowledgeRequestPreparation",400,"stale");Check("old-planning-owner-cannot-open-speech",!Get<bool>(chat,"m_LatencyFillerPlayed"));
            Call(chat,"AcknowledgeRequestPreparation",401,"planning-owned");Check("planning-confirmation-before-action-acceptance",Get<bool>(chat,"m_LatencyFillerPlayed"));
            string id=Get<string>(chat,"m_OpeningId");Call(chat,"AcknowledgeAcceptedTask",401,"actual-action-owned");
            Check("one-opening-per-turn-not-two-acknowledgements",id==Get<string>(chat,"m_OpeningId"));
            Call(chat,"StopSpeechAudio","test-stop");Call(chat,"CommitPlayedLatencyFiller");
            Set(chat,"m_FormalResponseGeneration",402);Set(chat,"m_AgentCurrentRoundIsTick",true);
            Call(chat,"AcknowledgeRequestPreparation",402,"autonomous-planning");Check("autonomous-decision-not-forced-to-speak-ack",!Get<bool>(chat,"m_LatencyFillerPlayed"));
        }finally{source.Stop();Set(chat,"m_AgentCurrentRoundIsTick",false);Set(chat,"m_AudioSource",oldAudio);settings.m_TextToSpeech=oldTts;
            foreach(System.Collections.DictionaryEntry pair in cached)foreach(var entry in (IEnumerable)pair.Value)entry.GetType().GetField("Clip",F).SetValue(entry,null);
            UnityEngine.Object.Destroy(clip);UnityEngine.Object.Destroy(host);}
    }
    static JObject Input()=>new JObject{["captureId"]="frozen-image",["claims"]=new JArray(new JObject{
        ["id"]="c0",["speechId"]="s1",["claimQuote"]="The screen is visible."})};
    static void Queue()
    {
        var events=new JArray();var q=new ActivitySourceCheckQueue{Changed=e=>events.Add(e)};var input=Input();
        var a=(JObject)input["claims"][0].DeepClone();var b=(JObject)a.DeepClone();b["claimQuote"]="A window is visible.";
        Check("same-group-reversed-claim-order-shares-one-source",ActivitySourceCheckQueue.Key("image",new JArray(a,b),1)==
            ActivitySourceCheckQueue.Key("image",new JArray(b.DeepClone(),a.DeepClone()),1));
        Check("source-key-keeps-version-boundary",ActivitySourceCheckQueue.Key("image",new JArray(a.DeepClone()),1)!=
            ActivitySourceCheckQueue.Key("image",new JArray(a.DeepClone()),2));
        q.Enqueue("claim","owner",input,"original-pixels",0);input["captureId"]="new-image";
        Check("rate-limited-source-retained",q.Take("owner",1,25)==null&&q.Count==1);
        Check("duplicate-source-coalesced",!q.Enqueue("claim","owner",Input(),"other-pixels",2)&&q.Count==1);
        var job=q.Take("owner",26,26);
        Check("source-dispatches-after-rate-window",job!=null&&job.Image=="original-pixels"&&(string)job.Input["captureId"]=="frozen-image");
        q.Close(job,"evidence-received",27);
        Check("source-acquisition-is-not-fact-verification",events.All(e=>(bool)e["reviewed"]==false));
        q.Enqueue("stale","owner",Input(),"pixels",30);q.Maintain("new-owner",31);
        Check("new-owner-cancels-old-source",q.Count==0&&(string)events.Last["state"]=="owner-replaced");
        q.Enqueue("aged","new-owner",Input(),"pixels",32);q.Maintain("new-owner",108);
        Check("bounded-source-lifetime",q.Count==0&&(string)events.Last["state"]=="expired-unchecked");
        Check("expired-identical-source-cannot-loop",!q.Enqueue("aged","new-owner",Input(),"pixels",109));
        for(int i=0;i<5;i++)q.Enqueue("capacity"+i,"new-owner",Input(),"pixels",110);
        Check("bounded-source-capacity",q.Count==4&&(string)events.Last["state"]=="capacity-unchecked");
        q.Cancel("user-started-speaking-unchecked",111);Check("explicit-source-cancellation-terminal",q.Count==0,events);
        var times=Enumerable.Range(0,11).Select(i=>(float)i).ToArray();
        Check("delivery-slot-kept-within-twelve",ActivityMonitorSchedulePolicy.RetryAt(times,20,10)>20&&
            ActivityMonitorSchedulePolicy.RetryAt(times,20,12)==20);
    }
    static IEnumerator OpeningHandoff(ChatSample chat)
    {
        var original=Get<AudioSource>(chat,"m_AudioSource");var host=new GameObject("Owned opening/formal handoff");var source=host.AddComponent<AudioSource>();
        var opening=AudioClip.Create("silent opening",6400,1,32000,false);var formal=AudioClip.Create("silent formal",32000,1,32000,false);
        Set(chat,"m_AudioSource",source);Set(chat,"m_FormalResponseGeneration",101);
        var units=Get<List<ActivityMonitorProtocol.SpeechUnit>>(chat,"m_ActivitySpeech");units.Clear();
        try {
            foreach(bool natural in new[]{true,false}) {
                source.clip=opening;Set(chat,"m_LatencyFillerClip",opening);Set(chat,"m_LatencyFillerPlayed",true);
                Set(chat,"m_LatencyFillerText","引き受けたわ。");Set(chat,"m_LatencyFillerStartedAt",Time.realtimeSinceStartup);
                Call(chat,"BeginOpeningPlaybackEvidence");source.Play();
                yield return new WaitForSecondsRealtime(.08f);Call(chat,"ObserveLatencyFillerProgress");
                if(natural)yield return new WaitForSecondsRealtime(.18f);else Call(chat,"StopSpeechAudio","test-opening-cancel");
                Call(chat,"GetFormalPlaybackPermission",101,0);
                var receipt=(JObject)Get<JObject>(chat,"m_OpeningPlaybackReceipt").DeepClone();
                Check("opening-frozen-before-new-playback-"+natural,(bool)receipt["complete"]==natural,receipt);
                source.clip=formal;source.Play();yield return new WaitForSecondsRealtime(.05f);
                var unit=new ActivityMonitorProtocol.SpeechUnit{Id="speech:101:new:"+natural,Text="今、向かっています。",Language="ja",State="playing"};units.Add(unit);
                Call(chat,"CommitPlayedLatencyFiller");
                Check("old-opening-cannot-interrupt-new-formal-"+natural,unit.State=="playing"&&source.isPlaying);
                Check("old-opening-keeps-own-samples-"+natural,JToken.DeepEquals(receipt,Get<JObject>(chat,"m_OpeningPlaybackReceipt")));
                source.Stop();units.Clear();
            }
            Set(chat,"m_FormalResponseGeneration",102);Call(chat,"ReleaseSpeechPresentation",101,"old-owner");
            Check("old-release-keeps-new-generation",Get<int>(chat,"m_FormalResponseGeneration")==102);
        }finally{source.Stop();Set(chat,"m_AudioSource",original);UnityEngine.Object.Destroy(host);UnityEngine.Object.Destroy(opening);UnityEngine.Object.Destroy(formal);}
    }
    static void SourceRuntime(ChatSample chat)
    {
        var q=Get<ActivitySourceCheckQueue>(chat,"m_ActivitySourceQueue");q.Cancel("test-reset",0);
        var frame=Events("Activity/MonitorFrame").First(e=>(string)e["data"]?["purpose"]=="speech" &&
            e["data"]?["speech"]?[0]?["observedAtSpeechStart"] is JObject start &&
            (bool?)start["formalObservation"]?["formalPixelsProvided"]==true)["data"] as JObject;
        frame=(JObject)frame.DeepClone();var row=(JObject)frame["speech"][0];var observation=(JObject)row["observedAtSpeechStart"]["formalObservation"];
        Set(chat,"m_ActivityReplyObservationGeneration",Get<int>(chat,"m_FormalResponseGeneration"));
        Set(chat,"m_ActivityReplyObservation",observation);Set(chat,"m_ActivityReplyImage","frozen-test-pixels");
        var units=Get<List<ActivityMonitorProtocol.SpeechUnit>>(chat,"m_ActivitySpeech");units.Clear();
        units.Add(new ActivityMonitorProtocol.SpeechUnit{Id=(string)row["id"],Text=(string)row["text"],Version=(int)row["version"],State="completed",ObservedAtSpeechStart=(JObject)row["observedAtSpeechStart"].DeepClone()});
        var times=Get<Queue<float>>(chat,"m_ActivityRequestTimes");times.Clear();for(int i=0;i<11;i++)times.Enqueue(Time.realtimeSinceStartup);
        frame["speechMeaning"]=new JObject{["domain"]="visual",["act"]="factual-claim",["source"]="current-writer-image",["tense"]="present",["quote"]=row["text"]};
        Call(chat,"QueueInterpretedVisualSource",frame);
        Check("production-limited-check-queues-instead-of-dropping",q.Count==1);
        Call(chat,"DispatchActivitySourceCheck",Time.realtimeSinceStartup);Check("production-rate-window-does-not-dispatch",q.Count==1&&!Get<bool>(chat,"m_ActivityRequestPending"));
        Call(chat,"CancelActivityMonitoringForReason",false,"user-started-speaking");Check("production-cancellation-closes-queued-source",q.Count==0);
        times.Clear();units.Clear();
    }
    static IEnumerator CompletionCancellation(ChatSample chat)
    {
        var original=Get<AudioSource>(chat,"m_AudioSource");var host=new GameObject("Natural drain followed by cancellation");var source=host.AddComponent<AudioSource>();
        var clip=AudioClip.Create("frozen receipt",6400,1,32000,false);Set(chat,"m_AudioSource",source);
        Set(chat,"m_FormalResponseGeneration",301);Set(chat,"m_LatencyFillerPlayed",false);
        var units=Get<List<ActivityMonitorProtocol.SpeechUnit>>(chat,"m_ActivitySpeech");units.Clear();
        try {
            foreach(bool ended in new[]{true,false}) {
                var unit=new ActivityMonitorProtocol.SpeechUnit{Id="speech:301:"+ended,Version=2,State="playing",Text="The summary is ready.",Language="en"};units.Add(unit);
                var type=typeof(ChatSample).GetNestedType("PendingSpeechChunk",BindingFlags.NonPublic);var chunk=Activator.CreateInstance(type,true);
                type.GetField("ProcessUnit",F).SetValue(chunk,unit);type.GetField("ProcessVersion",F).SetValue(chunk,2);
                source.clip=clip;Call(chat,"BeginPreparedPlaybackEvidence",clip,301);Call(chat,"BindPreparedPlaybackChunk",chunk,unit.Text);source.Play();
                yield return new WaitForSecondsRealtime(.08f);Call(chat,"ObservePreparedPlaybackEvidence");
                if(ended)yield return new WaitForSecondsRealtime(.18f);
                int before=Get<System.Text.StringBuilder>(chat,"m_AssistantHeardText").Length;
                Call(chat,"RecordActivityInterruption",unit.Text,"user-started-speaking");Call(chat,"StopSpeechAudio","user-started-speaking");
                Call(chat,"FinishPreparedPlaybackEvidence",clip,301);
                var receipt=Get<JObject>(chat,"m_PreparedPlaybackReceipt");
                Check("cancel-at-natural-end-versus-mid-clip-"+ended,(bool)receipt["complete"]==ended&&unit.State==(ended?"completed":"interrupted"),receipt.DeepClone());
                int added=Get<System.Text.StringBuilder>(chat,"m_AssistantHeardText").Length-before;
                Check("whole-audio-published-once-"+ended,added==(ended?unit.Text.Length:0));units.Clear();
            }
        }finally{source.Stop();Set(chat,"m_AudioSource",original);UnityEngine.Object.Destroy(host);UnityEngine.Object.Destroy(clip);}
    }
    static IEnumerator NativeSourceQueue(ChatSample chat,ChatQW model)
    {
        var settings=Get<ChatSetting>(chat,"m_ChatSettings");
        var oldModel=settings.m_ChatModel;settings.m_ChatModel=model;
        Set(chat,"m_EnableActivityMonitor",true);Call(chat,"EnsureActivityOwner");
        Set(chat,"m_ActivityNextPoll",Time.realtimeSinceStartup+120f);
        var texture=new Texture2D(32,32,TextureFormat.RGB24,false);
        texture.SetPixels(Enumerable.Repeat(Color.red,1024).ToArray());texture.Apply();
        string image="data:image/png;base64,"+Convert.ToBase64String(texture.EncodeToPNG());
        var q=Get<ActivitySourceCheckQueue>(chat,"m_ActivitySourceQueue");
        var units=Get<List<ActivityMonitorProtocol.SpeechUnit>>(chat,"m_ActivitySpeech");
        var evidence=Get<Dictionary<string,JObject>>(chat,"m_ActivityVisualEvidence");evidence.Clear();
        Get<HashSet<string>>(chat,"m_ActivityVisualAttempts").Clear();
        Get<Queue<float>>(chat,"m_ActivityRequestTimes").Clear();
        try {
            for(int i=0;i<2;i++) {
                string id="speech:301:visual"+i;
                var observation=new JObject{["formalPixelsProvided"]=true,["captureId"]="native-red-"+i,["observedAt"]=Time.realtimeSinceStartup};
                Set(chat,"m_ActivityReplyObservationGeneration",Get<int>(chat,"m_FormalResponseGeneration"));
                Set(chat,"m_ActivityReplyObservation",observation);Set(chat,"m_ActivityReplyImage",image);
                var unit=new ActivityMonitorProtocol.SpeechUnit{Id=id,Version=0,Text="A red surface is visible.",Language="en",State="completed",
                    ObservedAtSpeechStart=new JObject{["formalObservation"]=observation.DeepClone()}};units.Clear();units.Add(unit);
                var frame=new JObject{["speech"]=new JArray(unit.Snapshot()),["speechMeaning"]=new JObject{
                    ["act"]="factual-claim",["domain"]="visual",["source"]="current-writer-image",["tense"]="present",["quote"]=unit.Text}};
                Call(chat,"QueueInterpretedVisualSource",frame);bool sent=(bool)Call(chat,"DispatchActivitySourceCheck",Time.realtimeSinceStartup);
                Check("production-native-source-dispatched-"+i,sent);
                var input=Get<JObject>(chat,"m_ActivityPendingFrame");
                string wire=(string)Call(model,"BuildActivityVisualRequest",input.ToString(Formatting.None),image);
                var budget=(JObject)Call(typeof(ChatQW),"MeasureActivityMonitorBudget",wire);
                Check("complete-native-source-budget-"+i,(bool)budget["allowed"],budget);
                if(i==1)unit.Version++;
                float deadline=Time.realtimeSinceStartup+12;while(Get<bool>(chat,"m_ActivityRequestPending")&&Time.realtimeSinceStartup<deadline)yield return null;
                Check("production-native-source-terminal-"+i,q.Count==0&&!Get<bool>(chat,"m_ActivityRequestPending"));
                bool stored=evidence.Values.Any(row=>(string)row["speechId"]==id&&(string)row["assessment"]=="supported");
                Check(i==0?"native-same-source-evidence-usable":"changed-unit-cannot-use-inflight-old-evidence",stored==(i==0),new JArray(evidence.Values.Select(v=>v.DeepClone())));
            }
        }finally{Call(chat,"CancelActivityMonitoringForReason",false,"test-finished");Set(chat,"m_EnableActivityMonitor",false);settings.m_ChatModel=oldModel;UnityEngine.Object.Destroy(texture);units.Clear();evidence.Clear();}
    }
    static void Cache()
    {
        var f=new JObject{["owner"]=new JObject{["generation"]=1},["purpose"]="speech",["actions"]=new JArray(),
            ["speech"]=new JArray(new JObject{["id"]="s",["version"]=1,["text"]="The screen is visible.",["state"]="playing",
                ["observedAtSpeechStart"]=new JObject{["formalObservation"]=new JObject{["captureId"]="c",["formalPixelsProvided"]=true}}}),
            ["speechMeaning"]=new JObject{["domain"]="visual",["source"]="current-writer-image",["quote"]="The screen is visible."},
            ["facts"]=new JObject{["visual:s"]=new JObject{["speechId"]="s",["captureId"]="c",["claimQuote"]="The screen is visible.",
                ["assessment"]="supported",["source"]="same-capture-visual-check"}},["evidenceScopes"]=new JObject()};
        var d=new JObject{["speechAct"]="factual-claim",["truthWhenSaid"]="supported",["truthEvidenceIds"]=new JArray("visual:s"),["speech"]=new JObject{["issue"]="none"}};
        var cache=new ActivityCompletedCheckCache();cache.Remember(f,d);Check("same-source-supported-claim-reuses-check",cache.Contains(f));
        f["speech"][0]["state"]="completed";Check("playback-transition-does-not-repeat-same-proof",cache.Contains(f));
        f["facts"]["visual:s"]["captureId"]="other";Check("different-source-invalidates-check",!cache.Contains(f));
        f["facts"]["visual:s"]["captureId"]="c";f["speechMeaning"]["quote"]="I like the screen.";Check("different-claim-cannot-borrow-proof",!cache.Contains(f));
    }
    static void ReplayActualAudio(ChatSample chat)
    {
        Call(chat,"EnsureWorkDeliveryOwner");Get<Dictionary<string,JObject>>(chat,"m_WorkToolResultReceipts").Clear();
        var trace=new JArray();int completed=0;
        var inputs=new HashSet<int>(fixture["dialogue"]["inputSources"].Where(e=>(string)e["kind"]=="Timing").Select(e=>(int)e["line"]));
        foreach(var e in ((JArray)fixture["events"]).OfType<JObject>()) {
            if(inputs.Contains((int)e["line"])) {Set(chat,"m_WorkEpoch",Get<int>(chat,"m_WorkEpoch")+1);Call(chat,"EnsureWorkDeliveryOwner");}
            var d=e["data"] as JObject;
            if((string)e["kind"]!="Activity/SpeechProgress")continue;
            trace.Add(e.DeepClone());
            if((string)d["state"]!="completed")continue;
            var u=new ActivityMonitorProtocol.SpeechUnit{Id=(string)d["id"],Text=(string)d["text"],Version=(int)d["version"],State="completed",Language=(string)d["language"]};
            Set(chat,"m_FormalResponseGeneration",int.Parse(u.Id.Split(':')[1]));
            Call(chat,"RecordWorkCompletedSpeechForResult",u.Text,null);Call(chat,"RecordWorkCompletedAudio",u.Text,null,u,u.Version);completed++;
        }
        File.WriteAllText(Path.Combine(output,"actual-audio-order.json"),trace.ToString(Formatting.Indented));
        Check("full-actual-playback-order-not-generated-candidates",completed==15&&trace.Count==50);
    }
    static IEnumerator Meaning(ChatQW model,JObject frame,string name)
    {
        var input=(string)frame["purpose"]=="speech-meaning"?frame:ActivitySpeechMeaningPolicy.Input(frame);
        JObject meaning=null;var attempts=new JArray();bool valid=false;
        for(int i=0;i<2&&!valid;i++) {
            bool done=false,ok=false;string raw="",status="not-sent";
            string ready=model.PrepareActivityMonitorFrame(input.ToString(Formatting.None),out string diag);
            if(ready!=null) {model.PostActivityMonitor(ready,(a,b,c)=>{done=true;ok=a;raw=b;status=c;});
                float deadline=Time.realtimeSinceStartup+15;while(!done&&Time.realtimeSinceStartup<deadline)yield return null;}
            valid=ok&&ActivitySpeechMeaningPolicy.TryRead(raw,input,out meaning);
            attempts.Add(new JObject{["raw"]=raw,["status"]=status,["valid"]=valid,["budget"]=JObject.Parse(diag)});
            input["previousAttempt"]=new JObject{["rejectedVerdict"]=raw,["fieldRepair"]=ActivitySpeechMeaningPolicy.Rejection(raw,input)};
        }
        Check("literal-native-"+name,valid,attempts);
        if(valid&&name.StartsWith("general-")&&new[]{"我想陪你走一会儿。","一緒に歌いたい。","Would you like me to keep observing?","可能是书架后的光。"}.Contains((string)input["utterance"]))
            Check("autonomy-and-uncertainty-kept-"+name,meaning["claims"].All(c=>(string)c["act"]!="factual-claim"),meaning);
    }
}
