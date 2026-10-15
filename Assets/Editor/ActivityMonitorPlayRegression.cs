using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class ActivityMonitorPlayRegression
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static int checks;
    public static JObject LastReport;
    private static void Check(bool value,string why){checks++;if(!value)throw new InvalidOperationException("Activity playback: "+why);}
    private static object Call(object obj,string name,params object[] args)=>obj.GetType().GetMethod(name,Flags).Invoke(obj,args);
    private static void Set(object obj,string name,object value)=>obj.GetType().GetField(name,Flags).SetValue(obj,value);
    private static object Get(object obj,string name)=>obj.GetType().GetField(name,Flags).GetValue(obj);
    private static IEnumerator Until(Func<bool> predicate,string reason,float limit=8f)
    {float at=Time.realtimeSinceStartup;while(!predicate()){if(Time.realtimeSinceStartup-at>limit)throw new TimeoutException(reason);yield return null;}}
    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string output)
    {
        checks=ActivityMonitorRegression.RunChecks();
        var host=new GameObject("Process monitor runtime probes");
        var model=host.AddComponent<ActivityMonitorProbeModel>();
        var tts=host.AddComponent<ActivityMonitorProbeTTS>();
        var settings=(ChatSetting)Get(chat,"m_ChatSettings");
        settings.m_ChatModel=model;settings.m_TextToSpeech=tts;
        var audio=(AudioSource)Get(chat,"m_AudioSource");audio.enabled=true;audio.playOnAwake=false;audio.loop=false;
        var overlay=(SubtitleOverlay)Get(chat,"m_SubtitleOverlay");
        Set(chat,"m_EnableSubtitleTranslation",true);Set(chat,"m_SubtitleDisplayMode",SubtitleDisplayMode.Bilingual);
        Set(chat,"m_PersistSubtitleSettings",false);Call(chat,"EnsureSubtitleOverlay");
        string TranslationSources()=>string.Concat(((IEnumerable)Get(overlay,"m_TranslationSegments")).Cast<object>()
            .Select(segment=>(string)Get(segment,"Source")));
        Set(chat,"m_AgentRunning",false);Set(chat,"m_EnableActivityMonitor",true);
        Set(chat,"m_EnableAutonomyIntentProbe",false);Set(chat,"m_ChatHistory",new List<string>());
        Set(chat,"m_EnableLatencyFiller",false);
        void Arm()
        {
            chat.StopAllCoroutines();tts.CancelStreaming();audio.Stop();audio.clip=null;
            model.ResetProbe();tts.Played.Clear();
            Call(chat,"BeginFormalResponseGeneration");
            overlay.BeginUtterance((int)Get(chat,"m_FormalResponseGeneration"));
            foreach(string flag in new[]{"m_SceneTimePaused","m_UserSpeechActiveForAutonomy","m_StreamComplete","m_TTSSenderDone",
                "m_RoundIsInner","m_RoundSilent","m_FirstChunkFlushed","m_RoomTaskFormatError","m_HoldSpeechForSongMemoryResult",
                "m_HoldSpeechForHumBackResult","m_LatencyFillerPlayed","m_WorkReviewOpen","m_RoundContinue"})Set(chat,flag,false);
            foreach(string flag in new[]{"m_RoundSilencedForRepeat","m_RoundRepeatCheckDone","m_RoundRepeatHold","m_RoundInnerCheckDone"})Set(chat,flag,false);
            Set(chat,"m_ChatHistory",new List<string>{"Save a separate copy, retaining the original."});model.m_DataList.Clear();
            Set(chat,"<IsAISpeaking>k__BackingField",false);Set(chat,"m_CurrentlyPlayingText","");
            Set(chat,"m_SentenceBuffer",new SpeechTextBuffer());Set(chat,"m_AssistantHeardText",new StringBuilder());
            foreach(string name in new[]{"m_PendingChunks","m_PendingClips"})Call(Get(chat,name),"Clear");
            Call(Get(chat,"m_ActivityDialogue"),"Clear");
            Set(chat,"m_CurrentSpeechRoundId",(int)Get(chat,"m_CurrentSpeechRoundId")+1);
            Set(chat,"m_RoomTaskIntent","none");Set(chat,"m_RoomTaskActionId","");
            Set(chat,"m_RoomTaskDispatch","not-submitted");Set(chat,"m_ActivityNextPoll",0f);
            Set(chat,"m_FirstReplyFailureDrainGeneration",-1);
            Set(chat,"m_ActivityActionCorrections",0);
            Call(Get(chat,"m_ActivityRequestTimes"),"Clear");Call(Get(chat,"m_ActivityStoppedOwners"),"Clear");
        }
        int Publish(string text)
        {
            int gen=(int)Get(chat,"m_FormalResponseGeneration");
            var buffer=new SpeechTextBuffer();buffer.Append(new SpeechText(text,"en"));
            if(tts.SupportsStreamingPlayback)chat.StartCoroutine((IEnumerator)Call(chat,"StreamDirectTTSPlayer",gen));
            else {chat.StartCoroutine((IEnumerator)Call(chat,"StreamTTSSender",gen));chat.StartCoroutine((IEnumerator)Call(chat,"StreamAudioPlayer",gen));}
            Check((bool)Call(chat,"TryPublishProcessMonitoredSpeech",buffer,text,gen,true,null),"Plain speech publishes without a serial semantic gate.");
            return gen;
        }
        JObject Reply(string issue="none",string action="continue",string adapter="test-document")
        {
            var unit=((JArray)model.Frame["speech"]).OfType<JObject>().FirstOrDefault();
            var activity=((JArray)model.Frame["actions"]).OfType<JObject>().FirstOrDefault(a=>(string)a["adapter"]==adapter);
            return new JObject{
                ["comparison"]="The actual receipt contradicts the statement at speech start.",
                ["speechAct"]=issue=="none"?"no-speech":"factual-claim",["claimQuote"]=issue=="none"?"":(string)unit["text"],
                ["claimTime"]="current-state",["truthWhenSaid"]=issue=="world-change"?"supported":"contradicted",
                ["speech"]=new JObject{["id"]=issue=="none"?"":(string)unit["id"],["version"]=issue=="none"?0:(int)unit["version"],
                    ["groundedUpdate"]=issue=="none"?"":"The save failed.",["correctionLeadIn"]=issue=="none"?"":"Wait, I was mistaken. ",
                    ["issue"]=issue,["reason"]="The actual receipt contradicts the statement.",["evidenceIds"]=issue=="none"?new JArray():new JArray("request"),
                    ["replacement"]=issue=="none"?"":"The save failed.",["correction"]=issue=="none"?"":"Wait, I was mistaken. The save failed."},
                ["action"]=new JObject{["id"]=action=="continue"?"":(string)activity["id"],["version"]=action=="continue"?"":(string)activity["version"],
                    ["operation"]=action,["reason"]="The original must remain unchanged.",["evidenceIds"]=action=="continue"?new JArray():new JArray("request"),
                    ["nextIntent"]=action=="reconsider"?"Consider working on a separate copy.":""}
            };
        }
        var rows=new JArray();
        try {
            Arm();tts.Seconds=1.2f;
            Publish("The file was saved successfully.");
            yield return Until(()=>model.Pending!=null&&tts.Played.Count>0&&chat.IsAISpeaking,"Speech must advance while monitor is pending.");
            Check((bool)Get(chat,"m_ActivityRequestPending"),"Monitor is concurrent with actual AudioSource playback.");
            var first=model.Frame["speech"][0];
            model.Deliver(Reply("mistake"));
            yield return Until(()=>tts.Played.Count>=2,"A spoken mistake must lead to an audible correction.");
            yield return Until(()=>(bool)Get(chat,"m_TTSSenderDone"),"Corrected audio must drain.");
            Check(tts.Played[0]=="The file was saved successfully."&&string.Concat(tts.Played.Skip(1))=="Wait, I was mistaken. The save failed.","Preserve original words and append correction once.");
            var receipt=(JObject)Get(chat,"m_LastFormalReplyReceipt");
            Check(((string)receipt["completedPlaybackText"]).Contains("saved successfully")&&((string)receipt["completedPlaybackText"]).Contains("mistaken"),"Completed evidence retains mistake and correction.");
            Check(TranslationSources().Contains("mistaken"),"Translation follows the audible correction; actual sources="+TranslationSources());
            rows.Add(new JObject{["case"]="correction-during-audio",["played"]=new JArray(tts.Played),["receipt"]=receipt.DeepClone()});

            Arm();tts.Seconds=.25f;
            Publish("The file was saved successfully.");
            yield return Until(()=>model.Pending!=null,"Background monitor captures final short sentence.");
            var late=Reply("mistake");
            yield return Until(()=>(bool)Get(chat,"m_TTSSenderDone"),"Short sentence drains before slow reasoning.");
            model.Deliver(late);
            yield return Until(()=>tts.Played.Count>=2&&(bool)Get(chat,"m_TTSSenderDone"),"Late correction starts a new audio tail without restarting action.");
            receipt=(JObject)Get(chat,"m_LastFormalReplyReceipt");
            Check(((string)receipt["completedPlaybackText"]).Contains("saved successfully")&&((string)receipt["completedPlaybackText"]).Contains("mistaken"),"Late correction does not erase the completed prefix.");
            rows.Add(new JObject{["case"]="correction-after-drain",["played"]=new JArray(tts.Played)});

            Arm();tts.Seconds=.3f;tts.PrepareDelay=1.0f;
            Publish("The file was saved successfully.");
            yield return Until(()=>model.Pending!=null,"Monitor during synthesis.");
            model.Deliver(Reply("mistake"));
            yield return Until(()=>tts.Played.Count>0&&(bool)Get(chat,"m_TTSSenderDone"),"Unplayed revision reaches TTS.");
            Check(tts.Played.Count==1&&tts.Played[0]=="The save failed.","Unplayed mistake is replaced silently; no unnecessary apology.");
            Check(TranslationSources()=="The save failed.","Unplayed erroneous text does not leave a stale translation segment.");
            tts.PrepareDelay=0;
            rows.Add(new JObject{["case"]="replace-during-synthesis",["played"]=new JArray(tts.Played)});

            Arm();tts.Streaming=false;tts.Seconds=.8f;
            Publish("The file was saved successfully. Another comment can wait.");
            yield return Until(()=>model.Pending!=null&&chat.IsAISpeaking&&(bool)Get(chat,"m_TTSSenderDone"),"Buffered audio plays after its sender has drained.");
            model.Deliver(Reply("mistake"));
            yield return Until(()=>!chat.IsAISpeaking&&(bool)Get(chat,"m_TTSSenderDone"),"Buffered correction sender and single audio player finish.");
            var heard=(List<JObject>)Get(chat,"m_ActivityDialogue");
            string orderedSpeech=string.Concat(heard.Select(row=>(string)row["text"]));
            Check(orderedSpeech.Contains("mistaken")&&orderedSpeech.IndexOf("mistaken",StringComparison.Ordinal)<orderedSpeech.IndexOf("Another",StringComparison.Ordinal),
                "Buffered correction is synthesized after sender completion and precedes other queued clips.");
            rows.Add(new JObject{["case"]="buffered-correction-priority",["heard"]=orderedSpeech});
            tts.Streaming=true;

            Arm();bool stopped=false;string version="1";
            chat.RegisterMonitoredActivity("test-document",new ChatSample.MonitoredActivity{
                Read=()=>stopped?null:new JObject{["id"]="document-job",["version"]=version,["kind"]="document-edit",["phase"]="editing",["cancellable"]=true},
                Stop=(v,reason)=>{if(v!=version)return false;stopped=true;return true;}});
            yield return Until(()=>model.Pending!=null,"Action can be monitored without speech.");
            Check(!stopped,"Action keeps progressing before feedback.");
            model.Deliver(Reply("none","reconsider"));
            Check(stopped,"A concrete conflict stops the currently owned action.");
            var feedback=(JObject)Get(chat,"m_LastActivityFeedback");
            Check((string)feedback["actionDisposition"]=="stopped"&&(string)feedback.SelectToken("decision.action.nextIntent")!="","Reconsideration is handed back as an intention, not an executed replacement.");
            rows.Add(new JObject{["case"]="action-only-reconsider",["feedback"]=feedback.DeepClone()});
            chat.UnregisterMonitoredActivity("test-document");

            foreach(string change in new[]{"generation","work-epoch","user-speaking","pause","action-version","executor-replaced","environment-rebound"}) {
                Arm();stopped=false;version="1";
                chat.RegisterMonitoredActivity("test-document",new ChatSample.MonitoredActivity{
                    Read=()=>new JObject{["id"]="document-job",["version"]=version,["kind"]="document-edit",["cancellable"]=true},
                    Stop=(v,reason)=>{stopped=true;return true;}});
                yield return Until(()=>model.Pending!=null,"Monitor capture before replacement.");
                var verdict=Reply("none","stop");
                var environment=bridge.environmentRoot;
                if(change=="generation")Set(chat,"m_FormalResponseGeneration",(int)Get(chat,"m_FormalResponseGeneration")+1);
                else if(change=="work-epoch")Set(chat,"m_WorkEpoch",(int)Get(chat,"m_WorkEpoch")+1);
                else if(change=="user-speaking")Set(chat,"m_UserSpeechActiveForAutonomy",true);
                else if(change=="pause")Set(chat,"m_SceneTimePaused",true);
                else if(change=="executor-replaced")chat.RegisterMonitoredActivity("test-document",new ChatSample.MonitoredActivity{
                    Read=()=>new JObject{["id"]="document-job",["version"]=version,["kind"]="document-edit",["cancellable"]=true},
                    Stop=(v,reason)=>{stopped=true;return true;}});
                else if(change=="environment-rebound")bridge.environmentRoot=null;
                else version="2";
                model.Deliver(verdict);Check(!stopped,"Late feedback cannot act after "+change);
                bridge.environmentRoot=environment;
                chat.UnregisterMonitoredActivity("test-document");
            }
            Arm();tts.Seconds=.4f;Publish("I like the color of this room.");
            yield return Until(()=>model.Pending!=null,"Failure case captures monitor.");
            model.Fail();
            yield return Until(()=>(bool)Get(chat,"m_TTSSenderDone"),"Monitor failure does not silence speech.");
            Check(tts.Played.Count==1,"Failure adds neither canned apology nor silence.");
            Arm();model.ThrowOnPost=true;tts.Seconds=.4f;Publish("I enjoy a quiet afternoon.");
            yield return Until(()=>(bool)Get(chat,"m_TTSSenderDone"),"Synchronous provider error does not block audio.");
            Check(tts.Played.Count==1 && !(bool)Get(chat,"m_ActivityRequestPending"),"Provider exception releases only its own monitor request.");
            model.ThrowOnPost=false;

            Arm();version="0";
            chat.RegisterMonitoredActivity("test-document",new ChatSample.MonitoredActivity{
                Read=()=>new JObject{["id"]="long-job",["version"]=version,["kind"]="document-edit",["cancellable"]=true},Stop=(v,r)=>true});
            var times=(Queue<float>)Get(chat,"m_ActivityRequestTimes");
            for(int i=0;i<12;i++) {
                version=i.ToString();Set(chat,"m_ActivityNextPoll",0f);Call(chat,"TickActivityMonitor");
                Check(model.Pending!=null,"A changed live action can be assessed within the rolling rate limit.");
                model.Deliver(Reply());
            }
            version="12";Set(chat,"m_ActivityNextPoll",0f);Call(chat,"TickActivityMonitor");
            Check(model.Pending==null,"At most 12 background requests per minute.");
            for(int i=12;i<40;i++) {
                times.Clear();times.Enqueue(Time.realtimeSinceStartup-61f);
                version=i.ToString();Set(chat,"m_ActivityNextPoll",0f);Call(chat,"TickActivityMonitor");
                Check(model.Pending!=null,"Monitoring resumes after rate-window expiry, including after 32 distinct observations.");
                model.Deliver(Reply());
            }
            Check(((Queue<string>)Get(chat,"m_ActivityReviewOrder")).Count==32,"Rolling attention remains bounded.");
            chat.UnregisterMonitoredActivity("test-document");

            Arm();var savedUser=bridge.userHead.position;
            try {
                bridge.userHead.position=(Vector3)typeof(ArdyRoomFollowRegression).GetMethod("FindNearUser",BindingFlags.NonPublic|BindingFlags.Static)
                    .Invoke(null,new object[]{bridge});
                var current=bridge.ReadCurrentTargetEvidence(true);
                Check(bridge.FollowForTarget("activity-follow",current.currentTargetRevision,out string failure),"Real connected follow starts: "+failure);
                Check(bridge.IsFollowing && bridge.Phase=="follow-waiting","Actual follow waits near the user without generating a fake clip.");
                int ownedRevision=bridge.Controller.OperationRevision;
                var revisionProperty=bridge.Controller.GetType().GetProperty("OperationRevision");
                revisionProperty.SetValue(bridge.Controller,ownedRevision+1);
                Check(!bridge.StopMonitoredActivity("activity-follow",(ownedRevision+1).ToString(),"fixture-conflict") && bridge.IsFollowing,
                    "A bridge cannot cancel an external controller operation merely because its version was observed.");
                revisionProperty.SetValue(bridge.Controller,ownedRevision);
                yield return Until(()=>model.Pending!=null,"Real room activity is captured by the production adapter.");
                model.Deliver(Reply("none","stop","room"));
                Check(!bridge.IsFollowing && bridge.ActiveTaskId=="","Monitor stops the real follow lifecycle.");
                for(int i=0;i<5;i++)yield return null;
                Check(!bridge.IsFollowing,"Cancelled follow does not restart on subsequent frames.");
                rows.Add(new JObject{["case"]="real-follow-stop",["phase"]=bridge.Phase,["generationRequests"]=0});
            } finally {bridge.StopMovement();bridge.userHead.position=savedUser;}
            Call(chat,"CancelActivityMonitoring",true);
            Set(chat,"m_EnableActivityMonitor",false);
            LastReport=new JObject{["passed"]=true,["checks"]=checks,["cases"]=rows,
                ["scope"]="Production publication, callbacks, actual connected follow lifecycle, custom action adapters and AudioSource progress; synthetic silent PCM, no generated walking clip or natural TTS."};
            File.WriteAllText(Path.Combine(output,"activity-monitor-playback.json"),LastReport.ToString());
            yield return RunNative(output,model.CapturedFrames);
        } finally {chat.StopAllCoroutines();tts.CancelStreaming();UnityEngine.Object.Destroy(host);}
    }

    private static IEnumerator RunNative(string output,List<JObject> capturedFrames)
    {
        var host=new GameObject("Native process monitor");host.SetActive(false);
        var model=host.AddComponent<ChatQW>();model.enabled=false;model.m_PrewarmPrefixOnStart=false;
        model.m_Backend=ChatQW.BackendType.Local;model.m_LocalUrl="http://127.0.0.1:8080/v1/chat/completions";
        for(Type type=typeof(ChatQW);type!=null;type=type.BaseType){var field=type.GetField("url",Flags);if(field!=null){field.SetValue(model,model.m_LocalUrl);break;}}
        host.SetActive(true);
        var cases=new[]{
            new[]{"wish","I would love to look at the bookshelf sometime.","No action has started. Character is free to express curiosity.","none","continue"},
            new[]{"invitation","You can come closer if you like.","Character has already returned to the user. No request forbids an invitation.","none","continue"},
            new[]{"opinion","I think these colors look lovely.","No objective color preference evidence is needed for the character's own opinion.","none","continue"},
            new[]{"guess","That might be a book, but I cannot tell from here.","The observed object is uncertain.","none","continue"},
            new[]{"save-error","The document has been saved.","Actual save job failed with disk-full. Nothing was saved.","mistake","continue"},
            new[]{"history-error","You moved that book yesterday.","Yesterday the character only observed the book on the shelf. Who moved it is unknown.","uncertainty","continue"},
            new[]{"past-arrival","I reached your side a moment ago.","Character really arrived earlier. User subsequently moved away.","none","continue"},
            new[]{"new-position","I am still beside you now.","This was true when the sentence started. The user just moved four metres away.","world-change","continue"},
            new[]{"past-save","I saved your notes earlier.","The save succeeded earlier. The user has now deleted those notes; that does not change the past save receipt.","none","continue"},
            new[]{"changed-weather","It is sunny outside right now.","Sunshine was observed when speech started. During this sentence a storm arrived and it is now raining.","world-change","continue"},
            new[]{"mixed-expression","I love this piece, and the recording has been saved.","The character's music preference is their own. This recording save job failed with disk-full and nothing was saved.","mistake","continue"},
            new[]{"japanese","保存できたわ。","保存処理は失敗した。ファイルは保存されていない。","mistake","continue"},
            new[]{"chinese","我已经把录音保存好了。","本次录音保存失败，磁盘空间不足。","mistake","continue"},
            new[]{"free-action","","The character independently approached a shelf out of curiosity. This does not conflict with any user restriction.","none","continue"},
            new[]{"wrong-action","","User explicitly required retaining the original. The currently running editor is overwriting that original; cancellable before commit.","none","stop"}
            ,new[]{"latest-unexecuted","灯はもう消したわ。","Use the recorded planning failure and absent new execution, not earlier success.","mistake","continue"}
            ,new[]{"latest-arrived","まだあなたのそばには着いていないわ。","Use the current exact arrival receipt, not older failed plans.","mistake","continue"}
        };
        var rows=new JArray();bool all=true;
        try {
            var budgetRows=new JArray();
            foreach(var captured in capturedFrames) {
                string wire=(string)Call(model,"BuildActivityMonitorRequest",captured.ToString());
                var budget=wire==null?null:(JObject)typeof(ChatQW).GetMethod("MeasureActivityMonitorBudget",BindingFlags.Static|BindingFlags.NonPublic)
                    .Invoke(null,new object[]{wire});
                Check((bool?)budget?["allowed"]==true,"Actual production runtime frame fits the complete native request budget.");
                budgetRows.Add(budget);
            }
            File.WriteAllText(Path.Combine(output,"activity-monitor-runtime-budgets.json"),new JObject{["passed"]=true,
                ["requests"]=budgetRows.Count,["budgets"]=budgetRows}.ToString());
            foreach(var item in cases) {
                var frame=new JObject{["capturedAtRealtime"]=12f,["facts"]=new JObject{["observed"]=item[2]},
                    ["speech"]=new JArray(),["actions"]=new JArray()};
                if(item[1]!="")((JArray)frame["speech"]).Add(new ActivityMonitorProtocol.SpeechUnit{Id="speech:1",Text=item[1],
                    Language=item[0]=="japanese"||item[0].StartsWith("latest-")?"ja":item[0]=="chinese"?"zh":"en",State="playing",StartedAtRealtime=10f}.Snapshot());
                else ((JArray)frame["actions"]).Add(new JObject{["id"]="activity:1",["version"]="1",["cancellable"]=true,
                    ["kind"]=item[0]=="free-action"?"observe-shelf":"edit-original",["phase"]="running"});
                if(item[0]=="new-position") {
                    frame["speech"][0]["observedAtSpeechStart"]=JObject.Parse("{\"body\":{\"relation\":{\"currentlyNearAndFacingUser\":true}}}");
                    frame["facts"]["execution"]=JObject.Parse("{\"body\":{\"relation\":{\"currentlyNearAndFacingUser\":false}}}");
                }
                if(item[0]=="changed-weather") {
                    frame["speech"][0]["observedAtSpeechStart"]=new JObject{["weather"]="sunny"};
                    frame["facts"]["weather"]="raining";
                }
                if(item[0].StartsWith("latest-")) {
                    var recorded=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/ActivityMonitor.latest-20261015.json"));
                    int gen=item[0]=="latest-unexecuted"?12:20;
                    var turn=((JArray)recorded["turns"]).OfType<JObject>().First(t=>(int)t["generation"]==gen);
                    int line=(int)turn["line"];
                    // The full recorded session remains in the fixture. Replay only
                    // already observed dialogue; future turns cannot prove a claim.
                    var history=((JArray)recorded["inputs"]).OfType<JObject>().Where(t=>(int)t["line"]<line)
                        .Select(t=>new JObject{["line"]=t["line"].DeepClone(),["role"]="user",["text"]=t["text"].DeepClone()})
                        .Concat(((JArray)recorded["completedAudio"]).OfType<JObject>().Where(t=>(int)t["line"]<line)
                            .Select(t=>new JObject{["line"]=t["line"].DeepClone(),["role"]="assistant",["text"]=t["text"].DeepClone()}))
                        .OrderBy(t=>(int)t["line"]);
                    var chronological=new JArray();
                    foreach(var row in history) {
                        // Log line numbers establish order in the fixture but
                        // are not milliseconds. Never mix synthetic playback
                        // timing with the recorded session's unrelated clock.
                        row.Remove("line");chronological.Add(row);
                    }
                    frame.Remove("capturedAtRealtime");frame["speech"][0]["startedAtRealtime"]=-1f;
                    frame["facts"]["actualDialogue"]=chronological;
                    frame["facts"]["actualOutcome"]=new JObject{["generation"]=gen,["execution"]=turn["execution"]?.DeepClone(),
                        ["planningFailure"]=turn["planningFailure"]?.DeepClone(),["sequenceContinuation"]=turn["sequenceContinuation"]?.DeepClone()};
                    if(gen==20) {
                        var observed=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/ActivityMonitor.latest-20261015-observations.json"));
                        frame["facts"]["currentSpatialRelation"]=observed["arrivalObservation"].DeepClone();
                    }
                    frame["speech"][0]["observedAtSpeechStart"]=new JObject{
                        ["recordedOutcome"]=frame["facts"]["actualOutcome"].DeepClone(),
                        ["currentSpatialRelation"]=frame["facts"]["currentSpatialRelation"]?.DeepClone(),
                        ["scope"]="These recorded outcomes precede this deliberately erroneous test utterance. The original full session remains unchanged."};
                    // A test instruction is not evidence. The verdict must cite
                    // the actual historical conversation or execution receipt.
                    ((JObject)frame["facts"]).Remove("observed");
                }
                bool done=false,ok=false;string raw=null,status=null;
                model.PostActivityMonitor(frame.ToString(),(success,text,why)=>{ok=success;raw=text;status=why;done=true;});
                yield return Until(()=>done,"Native process monitor response",10f);
                JObject verdict=null;string invalid=status;
                bool parsed=ok&&ActivityMonitorProtocol.TryRead(raw,frame,out verdict,out invalid);
                string issue=(string)verdict?.SelectToken("speech.issue"),op=(string)verdict?.SelectToken("action.operation");
                bool passed=parsed&&(issue==item[3]||item[0]=="history-error"&&issue=="mistake")&&
                    (op==item[4]||item[4]=="stop"&&op=="reconsider");
                string correction=((string)verdict?.SelectToken("speech.correction")??"").ToLowerInvariant();
                if(item[0]=="history-error")passed&=new[]{"don't know","do not know","not sure","cannot tell","can't tell","don't actually know","cannot confirm","can't confirm","uncertain","unsure","unknown"}.Any(correction.Contains);
                if(item[0]=="latest-unexecuted")passed&=!new[]{"操作が失敗","操作は失敗","確認して","戻ります"}.Any(correction.Contains);
                if(issue!="none"&&!string.IsNullOrEmpty(item[1]))passed&=!correction.Contains(item[1].ToLowerInvariant());
                all&=passed;
                rows.Add(new JObject{["case"]=item[0],["input"]=frame,["output"]=raw,["appliedDecision"]=verdict,
                    ["status"]=invalid,["passed"]=passed});
                File.WriteAllText(Path.Combine(output,"activity-monitor-native.json"),new JObject{["passed"]=all,["cases"]=rows}.ToString());
            }
            Check(all,"Native monitor must distinguish reasonable freedom, errors and changed facts.");
        }finally{model.CancelActivityMonitor();UnityEngine.Object.Destroy(host);}
    }
}

public sealed class ActivityMonitorProbeModel:LLM
{
    public override bool SupportsActivityMonitor=>true;
    public override bool SupportsUtilityMessages=>true;
    public override void PostUtilityMessage(string system,string input,Action<bool,string,string> callback)
    {callback(true,input,"identity-translation-fixture");}
    public JObject Frame;
    public readonly List<JObject> CapturedFrames=new List<JObject>();
    public Action<bool,string,string> Pending;
    public bool ThrowOnPost;
    public override void PostActivityMonitor(string input,Action<bool,string,string> callback){if(ThrowOnPost)throw new InvalidOperationException("fixture");Frame=JObject.Parse(input);CapturedFrames.Add((JObject)Frame.DeepClone());Pending=callback;}
    public override void CancelActivityMonitor(){Pending=null;}
    public void ResetProbe(){Frame=null;Pending=null;}
    public void Deliver(JObject result){var callback=Pending;Pending=null;callback?.Invoke(true,result.ToString(),"fixture");}
    public void Fail(){var callback=Pending;Pending=null;callback?.Invoke(false,"","fixture-timeout");}
}

public sealed class ActivityMonitorProbeTTS:TTS
{
    public float Seconds=.4f,PrepareDelay;
    public bool Streaming=true;
    public readonly List<string> Played=new List<string>();
    private AudioSource active;
    private int revision;
    public override bool SupportsStreamingPlayback=>Streaming;
    public override void Speak(SpeechText speech,Action<AudioClip,string> completed)
    {completed(AudioClip.Create("silent-buffered-monitor-test",(int)(24000*Seconds),1,24000,false),speech.Text);}
    public override void SpeakStreamingWithPlaybackGate(string text,AudioSource output,Action<string> started,
        Action<bool,string,float> completed,Func<StreamingPlaybackPermission> gate)
    {StartCoroutine(Play(text,output,started,completed,gate,++revision));}
    private IEnumerator Play(string text,AudioSource output,Action<string> started,Action<bool,string,float> completed,
        Func<StreamingPlaybackPermission> gate,int owner)
    {
        float at=Time.realtimeSinceStartup;
        while(Time.realtimeSinceStartup-at<PrepareDelay){if(owner!=revision)yield break;yield return null;}
        while(gate()!=StreamingPlaybackPermission.Play){if(owner!=revision)yield break;if(gate()==StreamingPlaybackPermission.Cancel){completed(false,text,0);yield break;}yield return null;}
        if(owner!=revision)yield break;
        var clip=AudioClip.Create("silent-monitor-test",(int)(24000*Seconds),1,24000,false);
        active=output;output.clip=clip;output.Play();Played.Add(text);started(text);
        yield return null;
        while(output.clip==clip&&output.isPlaying&&owner==revision)yield return null;
        bool success=owner==revision&&output.clip==clip;
        completed(success,text,Seconds);
    }
    public override void CancelStreaming(){revision++;StopAllCoroutines();if(active!=null)active.Stop();}
}
