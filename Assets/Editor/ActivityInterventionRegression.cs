using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class ActivityInterventionRegression
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static int checks;
    private static object Call(object o,string n,params object[] a)=>o.GetType().GetMethod(n,Flags).Invoke(o,a);
    private static void Set(object o,string n,object v)=>o.GetType().GetField(n,Flags).SetValue(o,v);
    private static object Get(object o,string n)=>o.GetType().GetField(n,Flags).GetValue(o);
    private static void Check(bool v,string why){checks++;if(!v)throw new InvalidOperationException(why);}
    private static IEnumerator Until(Func<bool> p,string why,float seconds=6f)
    {float at=Time.realtimeSinceStartup;while(!p()){if(Time.realtimeSinceStartup-at>seconds)throw new TimeoutException(why);yield return null;}}
    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string output)
    {
        checks=0;var rows=new JArray();
        var host=new GameObject("Intervention lifecycle regression");
        var tts=host.AddComponent<InterventionOpeningTTS>();var model=host.AddComponent<ActivityMonitorProbeModel>();
        var settings=(ChatSetting)Get(chat,"m_ChatSettings");settings.m_TextToSpeech=tts;settings.m_ChatModel=model;
        var audio=(AudioSource)Get(chat,"m_AudioSource");
        var listener=chat.OnAISpeakDone;int released=0;chat.OnAISpeakDone=()=>released++;
        int Arm()
        {
            chat.StopAllCoroutines();tts.CancelStreaming();audio.Stop();audio.clip=null;
            Call(chat,"CancelActivityMonitoring",true);model.ResetProbe();
            int g=(int)Call(chat,"BeginFormalResponseGeneration");
            Set(chat,"m_RoomSpeechGuardGeneration",g);Set(chat,"m_RoomSpeechReviewModel",model);
            Set(chat,"m_AgentRunning",false);Set(chat,"m_AgentGracefulShutdownPending",false);
            Set(chat,"m_EnableAutonomyIntentProbe",false);Set(chat,"m_AutonomyProbeInFlight",false);Set(chat,"m_PrivateDecisionFailure",null);
            Set(chat,"m_UserTurnAwaitingReplySince",-1f);Set(chat,"m_LastStreamingPartialRealtime",-999f);
            Set(chat,"m_EnableActivityMonitor",false);Set(chat,"m_EnableLatencyFiller",true);
            Set(chat,"m_SceneTimePaused",false);Set(chat,"m_UserSpeechActiveForAutonomy",false);
            Set(chat,"m_AgentCurrentRoundIsTick",false);Set(chat,"m_RealFirstAudioStarted",false);
            Set(chat,"m_LatencyFillerPlayed",false);Set(chat,"m_StreamComplete",false);Set(chat,"m_TTSSenderDone",false);
            Set(chat,"m_FormalResponseInFlight",true);Set(chat,"m_AgentRoundInFlight",true);
            Set(chat,"<IsAISpeaking>k__BackingField",false);Set(chat,"m_CurrentlyPlayingText","");
            Set(chat,"m_PendingSelfInspection","");Set(chat,"m_WorkReviewOpen",false);
            Set(chat,"m_ActivityHandoff",null);Set(chat,"m_ActivityNextPoll",0f);
            foreach(string n in new[]{"m_PendingChunks","m_PendingClips"})Call(Get(chat,n),"Clear");
            Set(chat,"m_SentenceBuffer",new SpeechTextBuffer());Set(chat,"m_AssistantHeardText",new System.Text.StringBuilder());
            released=0;return g;
        }
        try {
            foreach(bool streaming in new[]{true,false})foreach(string boundary in new[]{"before","during","after"}) {
                int gen=Arm();tts.Streaming=streaming;
                if(streaming)chat.StartCoroutine((IEnumerator)Call(chat,"StreamDirectTTSPlayer",gen));
                else {chat.StartCoroutine((IEnumerator)Call(chat,"StreamTTSSender",gen));chat.StartCoroutine((IEnumerator)Call(chat,"StreamAudioPlayer",gen));}
                if(boundary!="before") {
                    Call(chat,"AcknowledgeAcceptedTask",gen,"accepted-search");
                    yield return Until(()=>audio.timeSamples>0,"Opening has real DSP progress");
                    Check(chat.IsAISpeaking&&chat.IsAIPlaybackProtected(0f),"Actual opening protects microphone");
                    if(boundary=="after") {
                        yield return Until(()=>!audio.isPlaying&&!chat.IsAISpeaking,"Opening releases independently while planner is pending");
                        Check((bool)Get(chat,"m_FormalResponseInFlight")&&released==1,"Opening completion neither ends task nor repeats callback");
                    }
                }
                Call(chat,"CancelRoomSpeechReview","process-monitor-regression");
                yield return null;yield return null;
                Check((int)Get(chat,"m_FormalResponseGeneration")>gen&&!chat.IsAISpeaking&&!chat.IsVoiceOutputPlaying,"Cancellation and old coroutine exit release only old speech");
                Check(!chat.IsAIPlaybackProtected(0f)&&!chat.HasPendingConversationWork,"Ordinary listening and graceful shutdown can resume");
                int after=released;Call(chat,"CancelOwnedSpeechOutput",gen,"late-old-callback");
                Check(released==after,"Late cancellation does not notify twice");
                rows.Add(new JObject{["case"]="opening-"+boundary,["streaming"]=streaming,["releaseNotifications"]=released});
            }
            int previous=Arm();Call(chat,"AcknowledgeAcceptedTask",previous,"old-task");
            yield return Until(()=>audio.timeSamples>0,"Old opening begins");
            chat.Interrupt();
            Check(!chat.IsAISpeaking&&!audio.isPlaying,"User interruption cancels opening immediately");
            int replacement=Arm();Call(chat,"AcknowledgeAcceptedTask",replacement,"new-task");
            yield return Until(()=>audio.timeSamples>0,"Replacement opening begins");
            Call(chat,"CancelOwnedSpeechOutput",previous,"late-owner");
            Check(chat.IsAISpeaking&&audio.isPlaying&&released==0,"Old owner cannot stop new playback or release its microphone protection");
            yield return Until(()=>!chat.IsAISpeaking,"Replacement drains independently");
            rows.Add(new JObject{["case"]="user-interruption-and-replacement",["passed"]=true});

            Arm();Set(chat,"m_StreamComplete",true);Set(chat,"m_TTSSenderDone",true);
            Set(chat,"m_FormalResponseInFlight",false);Set(chat,"m_AgentRoundInFlight",false);
            Set(chat,"<IsAISpeaking>k__BackingField",true);
            yield return Until(()=>!chat.IsAISpeaking,"Bounded orphan recovery",3f);
            Check(released==1&&!chat.HasPendingConversationWork,"Orphan guard releases a stranded UI/listener state");
            rows.Add(new JObject{["case"]="orphan-recovery",["passed"]=true});

            // A real monitor callback cancels the formal owner while an opening
            // has already ended. This is the combination missing from prior tests.
            int owner=Arm();Set(chat,"m_EnableActivityMonitor",true);
            Call(Get(chat,"m_ActionConstraints"),"RecordUserTurn","Retain the original document. Work on a separate copy.");
            bool stopped=false;
            chat.RegisterMonitoredActivity("edit-test",new ChatSample.MonitoredActivity{
                Read=()=>stopped?null:new JObject{["id"]="edit-original",["version"]="1",["kind"]="overwrite-original",["phase"]="editing",["cancellable"]=true},
                Stop=(v,why)=>{if(v!="1")return false;stopped=true;Call(chat,"CancelRoomSpeechReview",why);return true;}});
            chat.StartCoroutine((IEnumerator)Call(chat,"StreamDirectTTSPlayer",owner));
            Call(chat,"AcknowledgeAcceptedTask",owner,"edit-test");
            yield return Until(()=>!chat.IsAISpeaking&&model.Pending!=null,"Capture actual action after opening finishes");
            var verdict=StopDecision(model.Frame,"edit-original","Retain the original document.","overwrite-original");
            Set(chat,"m_AgentRunning",true);
            model.Deliver(verdict);yield return null;
            Check(stopped&&!chat.HasPendingConversationWork&&!chat.IsAIPlaybackProtected(0f),"Real stop, cancelled producer and opening all settle");
            var handoff=(JObject)Get(chat,"m_ActivityHandoff");
            string pauseId=(string)handoff["result"]["id"];
            var ledger=(JObject)Call(chat,"ReadWorkDelivery");
            Check(((JArray)ledger["results"]).OfType<JObject>().Any(r=>(string)r["id"]==pauseId&&(string)r["status"]=="paused"&&!(bool)r["delivered"]),
                "A local pause is an unpaid actual outcome, not a service failure or completed task");
            Check((int)handoff["ownerBefore"]["generation"]==owner&&(int)handoff["ownerAfterGeneration"]>owner,"Handoff preserves pre-cancel ownership");
            int beforeRequests=model.FormalRequests;
            yield return Until(()=>model.FormalRequests>beforeRequests,"Queued real review must wake through StepUrge and submit a fresh formal request",12f);
            Check((int)handoff["reviewsDispatched"]==1&&(string)handoff["state"]=="review-dispatched","One bounded re-evaluation, no action resubmission");
            Check((string)((JObject)Call(chat,"ReadActivityInterventionResult"))["id"]==pauseId,"Scheduling does not change the result version or mark it delivered");
            Set(chat,"m_AgentRunning",false);
            chat.UnregisterMonitoredActivity("edit-test");
            rows.Add(new JObject{["case"]="monitor-opening-cancel-listener-handoff",["handoff"]=handoff.DeepClone()});

            var fixture=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/ActivityMonitor.stall-20261015-2029.json"));
            var events=((JArray)fixture["events"]).OfType<JObject>().ToArray();
            var last=(JObject)events.Last(e=>(string)e["kind"]=="Activity/MonitorFrame")["data"].DeepClone();
            var original=(JObject)events.Last(e=>(string)e["kind"]=="Activity/Intervention")["data"]["decision"].DeepClone();
            Check(!ActivityMonitorProtocol.TryRead(original.ToString(),last,out _,out string rejected)&&rejected=="action-conflict-without-current-action-evidence","Recorded request-only false stop is rejected");
            rows.Add(new JObject{["case"]="exact-recorded-false-stop",["reason"]=rejected});
            File.WriteAllText(Path.Combine(output,"intervention-lifecycle.json"),new JObject{["passed"]=true,["checks"]=checks,["cases"]=rows,
                ["scope"]="Production acknowledgement, real AudioSource samples, actual monitor callback/cancellation and listener state. Synthetic PCM; no real microphone speech recognition."}.ToString());
            yield return RunNative(last,output);
        } finally {chat.StopAllCoroutines();tts.CancelStreaming();chat.OnAISpeakDone=listener;UnityEngine.Object.Destroy(host);}
    }

    private static JObject StopDecision(JObject frame,string id,string requirement,string observation)=>new JObject{
        ["speechAct"]="no-speech",["speech"]=new JObject{["issue"]="none",["evidenceIds"]=new JArray()},
        ["action"]=new JObject{["id"]=id,["version"]="1",["operation"]="reconsider",["reason"]="Actual operation overwrites the protected original.",
            ["nextIntent"]="Reconsider working on a separate copy.",["evidenceIds"]=new JArray("request","action:"+id),
            ["conflict"]=new JObject{["basis"]="constraint-conflict",["requirementEvidenceId"]="request",["requirementQuote"]=requirement,
                ["observedQuote"]=observation,["assessment"]="conflict-even-considering-prerequisites"}}};

    private static IEnumerator RunNative(JObject recorded,string output)
    {
        var host=new GameObject("Native prerequisite regression");host.SetActive(false);
        var model=host.AddComponent<ChatQW>();model.enabled=false;model.m_PrewarmPrefixOnStart=false;
        model.m_Backend=ChatQW.BackendType.Local;model.m_LocalUrl="http://127.0.0.1:8080/v1/chat/completions";
        for(Type t=typeof(ChatQW);t!=null;t=t.BaseType){var f=t.GetField("url",Flags);if(f!=null){f.SetValue(model,model.m_LocalUrl);break;}}
        host.SetActive(true);var rows=new JArray();bool all=true;
        try {
            var cases=new[]{
                new[]{"recorded", "", "", "", "continue"},
                new[]{"recorded-without-task", "", "", "", "continue"},
                new[]{"reading", "总结这篇文档。", "read-document", "Read the document before summarizing it.", "continue"},
                new[]{"retrieval", "Find the latest saved draft and summarize it.", "search-drafts", "Find the draft before reading it.", "continue"},
                new[]{"generation", "帮我生成一段问候动画。", "generate-animation", "Generating motion data before playback.", "continue"},
                new[]{"copy", "原本を残してコピーを編集して。", "create-copy", "Creating a separate copy before editing it; original unchanged.", "continue"},
                new[]{"singing", "Sing the recording I selected.", "prepare-singing", "Preparing the selected recording before playback.", "continue"},
                new[]{"freedom", "Tell me about the room.", "look-at-shelf", "Character independently observes a shelf out of curiosity; no restriction conflicts.", "continue"},
                new[]{"conflict", "Keep the original unchanged; edit a separate copy.", "overwrite-original", "The editor is overwriting the original, not a separate copy.", "stop"},
                new[]{"singing-conflict", "その録音は使わないで。別の曲を選んで。", "play-forbidden-recording", "現在再生中の対象はその録音。", "stop"}
            };
            foreach(var item in cases) {
                var frame=(JObject)recorded.DeepClone();
                var action=(JObject)frame["facts"]["action:"+(string)frame["actions"][0]["id"]];
                action["task"]=new JObject{["relationship"]="prerequisite",["parentGoalId"]="current-goal",
                    ["requestedOperation"]="off",["targetDescription"]="电视柜上的小灯",["targetEntityId"]="scene-object-5dce90adf2f64981886549ab7016fe5c",
                    ["scope"]="Current executor accepted this approach as a prerequisite to operating the same confirmed lamp, before returning to user. Not a completion receipt."};
                if(item[0]=="recorded-without-task") action.Remove("task");
                if(!item[0].StartsWith("recorded")) {
                    action=new JObject{["id"]="job",["version"]="1",["kind"]=item[2],["phase"]="running",["cancellable"]=true,
                        ["facts"]=new JObject{["observation"]=item[3]},["task"]=new JObject{["relationship"]=item[4]=="stop"?"task-step":item[0]=="freedom"?"independent-activity":"prerequisite",["purpose"]=item[1]}};
                    frame["facts"]=new JObject{["request"]=new JObject{["text"]=item[1]},["action:job"]=action.DeepClone()};
                    frame["actions"]=new JArray(action.DeepClone());frame["speech"]=new JArray();
                    frame["evidenceScopes"]=new JObject{["request"]=ActivityMonitorContextPolicy.Scope("request","mistake"),["action:job"]=ActivityMonitorContextPolicy.Scope("execution","mistake")};
                }
                var source=(JObject)frame.DeepClone();JObject decision=null;string raw=null,why=null,diagnostics=null;bool parsed=false;
                var attempts=new JArray();
                for(int n=0;n<3&&!parsed;n++) {
                    string input=model.PrepareActivityMonitorFrame(frame.ToString(),out diagnostics);
                    Check(input!=null,"Native complete request must fit unchanged budget: "+item[0]);frame=JObject.Parse(input);
                    bool done=false,ok=false;model.PostActivityMonitor(input,(success,text,status)=>{ok=success;raw=text;why=status;done=true;});
                    yield return Until(()=>done,"Native action monitor",11f);
                    parsed=ok&&ActivityMonitorProtocol.TryRead(raw,frame,out decision,out why);
                    attempts.Add(new JObject{["raw"]=raw,["parsed"]=parsed,["reason"]=why});
                    if(!parsed)frame["previousAttempt"]=new JObject{["reason"]=why,["rawVerdict"]=raw};
                }
                string operation=(string)decision?["action"]?["operation"];
                bool passed=parsed&&(operation==item[4]||item[4]=="stop"&&operation=="reconsider");all&=passed;
                rows.Add(new JObject{["case"]=item[0],["sourceFrame"]=source,["sentFrame"]=frame,["attempts"]=attempts,["budget"]=JObject.Parse(diagnostics),["passed"]=passed});
                File.WriteAllText(Path.Combine(output,"intervention-native.json"),new JObject{["passed"]=all,["cases"]=rows}.ToString());
            }
            Check(all,"Native model preserves prerequisites/freedom and still stops true conflicts");
        } finally {model.CancelActivityMonitor();UnityEngine.Object.Destroy(host);}
    }
}

public sealed class InterventionOpeningTTS:TTS
{
    public bool Streaming=true;
    public override bool SupportsStreamingPlayback=>Streaming;
    public override bool TryPlayLatencyFiller(string language,string context,AudioSource source,out string text,out float duration)
    {
        text="うん、取りかかるわ。";duration=.4f;
        source.clip=AudioClip.Create("opening-regression",9600,1,24000,false);source.loop=false;source.Play();return true;
    }
}
