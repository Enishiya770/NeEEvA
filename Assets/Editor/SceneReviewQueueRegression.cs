using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AIChat.Agent;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class SceneReviewQueueRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    public static JObject LastReport { get; private set; }
    private sealed class Saved { public object owner, value; public FieldInfo field; }
    private sealed class Context
    {
        public ChatSample chat; public ArdyRoomDialogueBridge bridge; public SceneReviewQueueProbeModel model;
        public AudioSource audio; public AudioClip clip, replacement;
        public SceneReviewQueuePcmTTS directTts; public Coroutine directPlayer;
        public SceneReviewClock clock; public int checks, cases;
        public readonly List<Saved> saved = new List<Saved>();
        public void Check(bool ok, string why) { checks++; if (!ok) throw new InvalidOperationException(why); }
        public void Set(object owner, string name, object value)
        {
            var field = owner.GetType().GetField(name, Flags) ?? throw new MissingFieldException(name);
            if (!saved.Exists(s => s.owner == owner && s.field == field)) saved.Add(new Saved { owner=owner, field=field, value=field.GetValue(owner) });
            field.SetValue(owner, value);
        }
        public void Arm(string label)
        {
            Call(chat, "CancelAutonomyIntentProbe"); clock.Clear(true); audio.Stop();
            Set(chat, "m_AgentRunning", true); Set(chat, "m_AgentGracefulShutdownPending", false);
            Set(chat, "m_UserSpeechActiveForAutonomy", false); Set(chat, "m_LastStreamingPartialRealtime", -999f);
            Set(chat, "m_FormalResponseInFlight", false); Set(chat, "m_AgentRoundInFlight", true);
            Set(chat, "m_StreamComplete", true); Set(chat, "m_TTSSenderDone", true);
            Set(chat, "m_TextOutputDrainedGeneration", -1);
            Set(chat, "m_WorkReviewOpen", false); Set(chat, "m_UserTurnAwaitingReplySince", -1f);
            Set(chat, "m_EnableAutonomyIntentProbe", true); Set(chat, "m_SceneTimePaused", false);
            Set(chat, "m_SceneTimeSuspended", false); Set(chat, "<IsAISpeaking>k__BackingField", true);
            audio.clip=clip; audio.loop=false; audio.Play();
            Call(chat, "RecordSceneActivity", "controlled-review-" + (++cases), "reposition-view", label, "observed", "controlled completed-action notification; no native submission");
            double now = (double)Call(chat, "SceneMonotonicNow");
            clock.Schedule(now - 3, 2, "scene-action-observed", true); // Queue time only; no forged geometry/result payload.
        }
        public void Restore()
        {
            if (directPlayer != null) chat.StopCoroutine(directPlayer);
            if (directTts != null) directTts.CancelStreaming();
            Call(chat, "CancelAutonomyIntentProbe"); audio.Stop(); clock.Clear();
            for (int i=saved.Count-1; i>=0; i--) saved[i].field.SetValue(saved[i].owner, saved[i].value);
        }
    }

    public static int RunPureChecks()
    {
        int n=0; void Check(bool ok,string why){n++;if(!ok)throw new InvalidOperationException(why);}
        var c=new SceneReviewClock();
        c.Schedule(0,30,"default",false); c.Schedule(10,30,"default",false);
        Check(c.Deadline==30 && c.ScheduledAt==0 && c.CoalescedEvents==1,"Repeated normal scheduling preserves the first deadline.");
        c.Schedule(11,2,"scene-action-arrived",true); c.Schedule(12,2,"scene-action-observed",true);
        Check(c.Deadline==13 && c.ScheduledAt==11 && c.Reason=="scene-action-arrived" && c.LatestReason=="scene-action-observed",
            "Arrival and its subsequent observation share one earliest wake, retaining the latest event diagnostics.");
        c.Schedule(13,90,"default",false);
        Check(c.Deadline==13 && c.LatestReason=="scene-action-observed","Public completion cannot overwrite an action-result wake.");
        Check(c.Consume(15)=="scene-action-arrived" && c.LastActualElapsed==4 && c.LastQueueDelay==2,"One consumption records actual busy delay and empties the merged queue.");
        Check(!c.Pending && c.CoalescedEvents==0,"Consumed event does not leave a duplicate wake.");
        for(int i=0;i<6;i++)c.RecordRequest(i*5);
        Check(!c.CanRequest(26,6,2,out double at)&&at==60,"Private overlap retains the existing six-per-minute budget.");
        Check(c.CanRequest(60,6,2,out _),"Budget replenishes without changing its duration.");
        c.Schedule(61,2,"scene-action-observed",true);
        Check(!c.DiscardSupersededSceneAction(7,7) && c.Pending,"The current user's action wake is retained.");
        Check(c.DiscardSupersededSceneAction(7,8) && !c.Pending && c.RequestsInLastMinute(61)==5,
            "Only the obsolete action wake is discarded, preserving the request budget.");
        c.Schedule(61,2,"memory-surfaced",true);
        Check(!c.DiscardSupersededSceneAction(7,8) && c.Reason=="memory-surfaced",
            "A new non-action event is not owned by the discarded action's user turn.");
        return n;
    }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, string output)
    {
        if(!Application.isBatchMode||!Application.isPlaying||Application.dataPath.IndexOf("unity-naturalness-validation",StringComparison.OrdinalIgnoreCase)<0)
            throw new InvalidOperationException("Use the existing isolated real-room Play fixture.");
        string directory=Path.Combine(output,"scene-review-queue"); Directory.CreateDirectory(directory);
        LastReport=new JObject{["status"]="running",["pureChecks"]=RunPureChecks(),["cases"]=new JArray(),
            ["scope"]="Production review scheduler and actual captured room images with controlled completed-action events and recorded private callbacks. A locally generated muted PCM AudioClip really plays; this is not a TTS/model/ASR/native request or an audible character response. No field-based claim of successful room movement."};
        var host=new GameObject("Scene review PCM and callback fixture");
        var dormant=new GameObject("Dormant scene-review provider");dormant.SetActive(false);
        var c=new Context{chat=chat,bridge=bridge,clock=(SceneReviewClock)Get(chat,"m_SceneReviewClock"),
            audio=host.AddComponent<AudioSource>(),model=dormant.AddComponent<SceneReviewQueueProbeModel>()};
        c.audio.mute=true;c.audio.playOnAwake=false;
        c.directTts=host.AddComponent<SceneReviewQueuePcmTTS>();
        c.clip=AudioClip.Create("controlled ten-second PCM",480000,1,48000,false);
        c.replacement=AudioClip.Create("different controlled PCM",480000,1,48000,false);
        var settings=(ChatSetting)Get(chat,"m_ChatSettings");var originalModel=settings.m_ChatModel;
        var originalTts=settings.m_TextToSpeech;
        var stack=new Stack<IEnumerator>();
        try
        {
            c.Check(bridge.IsConnected&&!bridge.ReservesBody&&!bridge.IsFollowing,"Actual room bridge is connected and idle.");
            c.Set(chat,"m_AudioSource",c.audio); c.Set(chat,"m_LogAgentLoop",false);
            settings.m_ChatModel=c.model;
            stack.Push(Cases(c));
            while(stack.Count>0)
            {
                object yielded=null;
                try
                {
                    while(stack.Count>0)
                    {
                        var r=stack.Peek();if(!r.MoveNext()){(stack.Pop()as IDisposable)?.Dispose();continue;}
                        if(r.Current is IEnumerator nested){stack.Push(nested);continue;}
                        yielded=r.Current;break;
                    }
                }
                catch(Exception error){LastReport["error"]=error.ToString();throw;}
                if(stack.Count>0)yield return yielded;
            }
            LastReport["status"]="passed";
        }
        finally
        {
            while(stack.Count>0)(stack.Pop()as IDisposable)?.Dispose();
            c.Restore(); settings.m_ChatModel=originalModel; settings.m_TextToSpeech=originalTts;
            LastReport["checks"]=c.checks; LastReport["privateRequests"]=c.model.probes.Count;
            LastReport["publicRequests"]=c.model.publicRequests;
            if((string)LastReport["status"]=="running")LastReport["status"]="failed";
            File.WriteAllText(Path.Combine(directory,"report.json"),LastReport.ToString()+"\n");
            UnityEngine.Object.DestroyImmediate(c.clip); UnityEngine.Object.DestroyImmediate(c.replacement);
            UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(dormant);
        }
    }

    private static IEnumerator StartProbe(Context c,string label)
    {
        int count=c.model.probes.Count;
        c.Arm(label); yield return null;
        c.Check(c.audio.isPlaying && c.chat.IsVoiceOutputPlaying,"A real AudioSource is playing the controlled clip.");
        // Update may already have started this same due wake; it must not duplicate it.
        if(!(bool)Get(c.chat,"m_AutonomyProbeInFlight") && c.model.probes.Count==count) Call(c.chat,"StepUrge");
        yield return Until(()=>c.model.probes.Count==count+1,5,"private scene review capture");
    }

    private static IEnumerator Cases(Context c)
    {
        c.Arm("guard-matrix");
        c.Check((bool)Call(c.chat,"CanBeginScenePlaybackReview","scene-action-observed"),"Final local clip can host a read-only scene-result review.");
        c.Check(!(bool)Call(c.chat,"CanBeginScenePlaybackReview","default"),"Ordinary speech without an action event cannot start parallel review.");
        foreach(string name in new[]{"m_FormalResponseInFlight","m_UserSpeechActiveForAutonomy","m_SceneActionActive","m_SongSearchInFlight"})
        {
            c.Set(c.chat,name,true);
            c.Check(!(bool)Call(c.chat,"CanBeginScenePlaybackReview","scene-action-observed"),"Busy owner blocks private overlap: "+name);
            c.Set(c.chat,name,false);
        }
        c.Set(c.chat,"m_TTSSenderDone",false);
        c.Check(!(bool)Call(c.chat,"CanBeginScenePlaybackReview","scene-action-observed"),"Pending TTS generation is not an already completed local clip.");
        c.Set(c.chat,"m_TTSSenderDone",true);
        c.audio.Stop();c.Check(!(bool)Call(c.chat,"CanBeginScenePlaybackReview","scene-action-observed"),"Logical speaking alone is not actual playback.");

        int actionTurn=(int)Get(c.chat,"m_SceneActionReviewUserTurn");
        c.Set(c.chat,"m_SceneActionReviewUserTurn",actionTurn-1);
        Call(c.chat,"ScheduleNextTick",2f,"memory-surfaced");
        Call(c.chat,"ValidateSceneReviewQueue");
        c.Check(c.clock.Pending && c.clock.Reason=="memory-surfaced" && c.clock.LatestReason=="memory-surfaced",
            "Scheduling a new event first removes a prior user's action wake; validation cannot swallow the new event.");
        c.Set(c.chat,"m_SceneActionReviewUserTurn",actionTurn);
        Add("superseded action / new event retained");

        yield return StartProbe(c,"hold-read-only");
        var first=c.model.probes[c.model.probes.Count-1];int probes=c.model.probes.Count;
        string beforeWork=(string)Get(c.chat,"m_WorkStatus");int history=c.model.m_DataList.Count;
        first.done("{\"proceed\":true,\"work_status\":\"continue\",\"intent\":\"look again at the completed target\",\"wait_seconds\":30}");
        c.Check(Get(c.chat,"m_ScenePlaybackReview")!=null && c.model.publicRequests==0 && c.audio.isPlaying,
            "A positive private result stays a candidate while the existing clip plays.");
        c.Check((string)Get(c.chat,"m_WorkStatus")==beforeWork && c.model.m_DataList.Count==history,
            "Read-only completion neither applies work status nor writes dialogue history.");
        Call(c.chat,"StepUrge");yield return null;
        c.Check(c.model.probes.Count==probes && c.model.publicRequests==0,"Held candidate cannot create duplicate reviews or a second public round.");
        c.audio.clip=c.replacement;Call(c.chat,"ValidateSceneReviewQueue");
        c.Check(Get(c.chat,"m_ScenePlaybackReview")==null,"Changing the actual output clip invalidates the old candidate.");
        first.done("{\"proceed\":true,\"intent\":\"late callback\",\"wait_seconds\":30}");
        c.Check(c.model.publicRequests==0 && Get(c.chat,"m_ScenePlaybackReview")==null,"Late callback after clip replacement cannot restore the candidate.");
        Add("actual clip / readonly candidate / replacement");

        yield return StartProbe(c,"new-event");var changed=c.model.probes[c.model.probes.Count-1];
        Call(c.chat,"RecordSceneActivity","new-controlled-action","look-at","new-event","observed","new completed result");
        Call(c.chat,"ValidateSceneReviewQueue"); changed.done("{\"proceed\":true,\"intent\":\"stale event\"}");
        c.Check(Get(c.chat,"m_ScenePlaybackReview")==null && c.model.publicRequests==0,"A newer action/event revokes an in-flight old review.");Add("new event revokes old probe");

        yield return StartProbe(c,"new-user");var stale=c.model.probes[c.model.probes.Count-1];
        c.Set(c.chat,"m_InputAudioRevision",(int)Get(c.chat,"m_InputAudioRevision")+1);c.Set(c.chat,"m_UserSpeechActiveForAutonomy",true);
        Call(c.chat,"ValidateSceneReviewQueue");stale.done("{\"proceed\":true,\"intent\":\"obsolete user turn\"}");
        c.Check(Get(c.chat,"m_ScenePlaybackReview")==null && c.model.publicRequests==0,"Actual input revision and speaking gate discard the private result before a new formal request.");Add("new input revokes old probe");

        yield return StartProbe(c,"natural-drain");var quiet=c.model.probes[c.model.probes.Count-1];
        quiet.done("{\"proceed\":false,\"work_status\":\"none\",\"intent\":\"\",\"wait_seconds\":30}");
        string firstCapture=c.chat.SceneVisionCapture.LatestObservation.CaptureId;
        int handed=(int)Get(c.chat,"m_ScenePlaybackReviewsHandedOff");
        c.audio.time=c.clip.length-.08f;
        yield return Until(()=>!c.audio.isPlaying,2,"real clip drains");
        Call(c.chat,"FinishSpeakingNaturally");Call(c.chat,"StepUrge");
        yield return Until(()=>(int)Get(c.chat,"m_ScenePlaybackReviewsHandedOff")==handed+1,5,"fresh-view review handoff after audio");
        c.Check(c.chat.SceneVisionCapture.LatestObservation.CaptureId!=firstCapture && c.model.publicRequests==0,
            "Natural audio completion refreshes the real image and applies one quiet candidate without another model/public request.");
        c.Check(Get(c.chat,"m_ScenePlaybackReview")==null && !(bool)Get(c.chat,"m_AutonomyProbeInFlight"),"Completed handoff releases its private owner.");
        int after=c.model.probes.Count;Call(c.chat,"StepUrge");yield return null;
        c.Check(c.model.probes.Count==after && (int)Get(c.chat,"m_ScenePlaybackReviewsHandedOff")==handed+1,"The consumed result cannot immediately run again.");Add("actual audio drains then new view / one quiet handoff");

        object originalFact=Get(c.chat,"m_SceneActionFact");
        var factType=typeof(ChatSample).GetNestedType("SceneActionFact",BindingFlags.NonPublic);
        object fact=Activator.CreateInstance(factType,true);
        factType.GetField("actionId").SetValue(fact,"controlled-same-action");
        factType.GetField("phase").SetValue(fact,"observed");
        factType.GetField("reason").SetValue(fact,"controlled original evidence");
        c.Set(c.chat,"m_SceneActionFact",fact);
        yield return StartProbe(c,"same-action-facts-changed");
        c.model.probes[c.model.probes.Count-1].done("{\"proceed\":false,\"work_status\":\"closed\",\"wait_seconds\":30}");
        int beforeHandoff=(int)Get(c.chat,"m_ScenePlaybackReviewsHandedOff");
        int activityRevision=(int)Get(c.chat,"m_SceneActivityRevision");
        string workBefore=(string)Get(c.chat,"m_WorkStatus");
        factType.GetField("reason").SetValue(fact,"controlled corrected evidence for the same action");
        c.audio.time=c.clip.length-.08f;yield return Until(()=>!c.audio.isPlaying,2,"changed-fact audio drains");
        Call(c.chat,"FinishSpeakingNaturally");Call(c.chat,"StepUrge");
        c.Check(Get(c.chat,"m_ScenePlaybackReview")==null && (int)Get(c.chat,"m_ScenePlaybackReviewsHandedOff")==beforeHandoff &&
            (string)Get(c.chat,"m_WorkStatus")==workBefore && (int)Get(c.chat,"m_SceneActivityRevision")==activityRevision,
            "Correcting the same action's evidence discards its cached closed decision without changing action/phase revisions or applying stale work status.");
        c.Check(c.clock.Pending && c.clock.Reason=="scene-action-facts-changed" && c.model.publicRequests==0,
            "Changed objective facts queue a normal fresh private review instead of treating a newly captured RGB as model re-verification.");
        c.Set(c.chat,"m_SceneActionFact",originalFact);
        Add("same action / corrected evidence / old closed candidate discarded");

        yield return DirectPlaybackChecks(c);
    }

    private static IEnumerator DirectPlaybackChecks(Context c)
    {
        var settings=(ChatSetting)Get(c.chat,"m_ChatSettings"); settings.m_TextToSpeech=c.directTts;
        c.Set(c.chat,"m_SceneFinalPlayback",null);
        int generation=(int)Get(c.chat,"m_FormalResponseGeneration");

        c.Arm("buffered-final-window"); c.clock.Clear(); c.Set(c.chat,"m_TTSSenderDone",false);
        Call(c.chat,"ObserveFinalScenePlayback",generation,c.directTts,c.clip,true,true);
        object wavOwner=Call(c.chat,"CreateScenePlaybackReview","scene-action-observed",(int)Get(c.chat,"m_AutonomyProbeGeneration"));
        c.Check((bool)Call(c.chat,"ScenePlaybackReviewCurrent",wavOwner),"A complete buffered WAV does not need the direct sender's playback-done flag.");
        c.audio.time=c.clip.length-.08f;
        Call(c.chat,"ObserveFinalScenePlayback",generation,c.directTts,c.clip,true,true);
        yield return Until(()=>!c.audio.isPlaying,2,"buffered WAV actual end");
        Call(c.chat,"CompleteFinalScenePlayback",generation,c.clip,true); c.audio.clip=null;
        c.Check((bool)Call(c.chat,"ScenePlaybackReviewCurrent",wavOwner),"A proven natural WAV drain preserves its owner after the direct player clears the clip.");
        Call(c.chat,"CancelAutonomyIntentProbe");

        c.Arm("early-stop-window"); c.clock.Clear();c.Set(c.chat,"m_TTSSenderDone",false);
        Call(c.chat,"ObserveFinalScenePlayback",generation,c.directTts,c.clip,true,true);
        c.audio.Stop();Call(c.chat,"CompleteFinalScenePlayback",generation,c.clip,true);
        c.Check(Get(c.chat,"m_SceneFinalPlayback")==null,"Early AudioSource.Stop cannot become a successful buffered drain receipt.");
        c.Arm("failed-pcm-window");c.clock.Clear();c.Set(c.chat,"m_TTSSenderDone",false);
        Call(c.chat,"ObserveFinalScenePlayback",generation,c.directTts,c.clip,false,true);
        c.audio.Stop();Call(c.chat,"CompleteFinalScenePlayback",generation,c.clip,false);
        c.Check(Get(c.chat,"m_SceneFinalPlayback")==null,"Failed PCM completion revokes its final-playback window.");
        c.Arm("replaced-direct-window");c.clock.Clear();c.Set(c.chat,"m_TTSSenderDone",false);
        Call(c.chat,"ObserveFinalScenePlayback",generation,c.directTts,c.clip,false,true);
        object replaced=Call(c.chat,"CreateScenePlaybackReview","scene-action-observed",(int)Get(c.chat,"m_AutonomyProbeGeneration"));
        c.audio.clip=c.replacement;c.audio.Play();
        c.Check(!(bool)Call(c.chat,"ScenePlaybackReviewCurrent",replaced),"Replacing the actual direct output clip invalidates its old candidate.");
        Call(c.chat,"CancelAutonomyIntentProbe");
        Add("direct WAV natural cleanup / early stop / PCM failure / replacement");

        int count=c.model.probes.Count;
        c.Arm("production-direct-player");c.audio.Stop();c.Set(c.chat,"m_SceneFinalPlayback",null);
        c.Set(c.chat,"m_TTSSenderDone",false);c.Set(c.chat,"m_StreamComplete",false);
        c.Set(c.chat,"<IsAISpeaking>k__BackingField",false);c.Set(c.chat,"m_RealFirstAudioStarted",true);
        c.Set(c.chat,"m_LatencyFillerPlayed",false);
        var oldQueue=Get(c.chat,"m_PendingChunks");var queue=Activator.CreateInstance(oldQueue.GetType());
        c.Set(c.chat,"m_PendingChunks",queue);
        var chunkType=typeof(ChatSample).GetNestedType("PendingSpeechChunk",BindingFlags.NonPublic);
        object chunk=Activator.CreateInstance(chunkType,true);
        chunkType.GetField("RoundId").SetValue(chunk,190001);
        chunkType.GetField("Text").SetValue(chunk,"これは再生経路の検証です。");
        chunkType.GetField("LanguageCode").SetValue(chunk,"ja");
        queue.GetType().GetMethod("Enqueue").Invoke(queue,new[]{chunk});
        c.directTts.SynthesisComplete=false;c.directTts.Started=false;
        c.directPlayer=c.chat.StartCoroutine((IEnumerator)Call(c.chat,"StreamDirectTTSPlayer",generation));
        yield return Until(()=>c.directTts.Started,2,"controlled PCM provider starts real AudioSource");
        c.Check(!(bool)Get(c.chat,"m_TTSSenderDone") && Get(c.chat,"m_SceneFinalPlayback")==null,
            "Production direct player keeps sender-done false and cannot review while synthesis is still running.");
        c.directTts.SynthesisComplete=true;yield return null;yield return null;
        c.Check(Get(c.chat,"m_SceneFinalPlayback")==null,"Synthesis completion alone cannot promise the final sentence while formal text is still streaming.");
        c.Set(c.chat,"m_StreamComplete",true);
        yield return Until(()=>c.model.probes.Count==count+1,5,"production direct player's final PCM private probe");
        c.Check(!(bool)Get(c.chat,"m_TTSSenderDone") && c.audio.isPlaying,
            "The production direct PCM path starts the private probe before real playback drains, without changing sender semantics.");
        c.model.probes[c.model.probes.Count-1].done("{\"proceed\":false,\"work_status\":\"none\",\"wait_seconds\":30}");
        int handed=(int)Get(c.chat,"m_ScenePlaybackReviewsHandedOff");
        c.audio.time=SceneReviewQueuePcmTTS.PcmDuration-.08f;
        yield return Until(()=>(int)Get(c.chat,"m_ScenePlaybackReviewsHandedOff")==handed+1,6,"production PCM cleanup and fresh-view handoff");
        c.directPlayer=null;
        c.Check(c.directTts.DestroyedOwnedClip && c.audio.clip==null && !(bool)Get(c.chat,"m_AutonomyProbeInFlight") && c.model.publicRequests==0,
            "Production player records real drain, provider destroys its owned clip, and a new-view quiet handoff releases the private owner without another public response.");
        Add("production StreamDirectTTSPlayer / controlled PCM synthesis / actual playback and owned-clip destruction");
    }
    private static void Add(string name)=>((JArray)LastReport["cases"]).Add(name);
    private static IEnumerator Until(Func<bool> ok,float seconds,string why)
    {float end=Time.realtimeSinceStartup+seconds;while(!ok()){if(Time.realtimeSinceStartup>end)throw new TimeoutException(why);yield return null;}}
    private static object Get(object owner,string name)=>owner.GetType().GetField(name,Flags).GetValue(owner);
    private static object Call(object owner,string name,params object[] args)=>owner.GetType().GetMethod(name,Flags).Invoke(owner,args);
}

public sealed class SceneReviewQueueProbeModel : LLM
{
    public sealed class Probe { public string prompt,image; public Action<string> done; }
    public readonly List<Probe> probes=new List<Probe>();
    public int publicRequests;
    public override bool SupportsVisualDecisionMessages=>true;
    public override void PostAutonomyIntentMsg(string prompt,string imageDataUrl,Action<string> callback)
    { probes.Add(new Probe{prompt=prompt,image=imageDataUrl,done=callback}); }
    public override void PostWorkReviewMsg(string prompt,string imageDataUrl,Action<string> callback)=>PostAutonomyIntentMsg(prompt,imageDataUrl,callback);
    public override void PostSpeechStream(string message,Action<SpeechText> speech,Action<string> complete,string imageDataUrl=null,bool recordAssistantHistory=true)
    { publicRequests++; }
    public override void PostSpeechFeedbackStream(string context,string feedback,Action<SpeechText> speech,Action<string> complete,string imageDataUrl,bool recordAssistantHistory)
    { publicRequests++; }
    public override void CancelEphemeralMsg() { }
}

// No HTTP/TTS synthesis: this provider models the documented PCM callback
// lifecycle while the actual production player drives a real muted AudioSource.
public sealed class SceneReviewQueuePcmTTS : TTS
{
    public const float PcmDuration=8f;
    public bool SynthesisComplete, Started, DestroyedOwnedClip;
    private AudioClip owned; private AudioSource output; private int generation;
    public override bool SupportsStreamingPlayback=>true;
    public override bool CanPrefetchNextSpeech=>SynthesisComplete;
    public override void SpeakStreamingWithPlaybackGate(SpeechText speech,AudioSource target,Action<string> started,
        Action<bool,string,float> completed,Func<StreamingPlaybackPermission> gate)
    { StartCoroutine(Play(speech,target,started,completed,gate,++generation)); }
    private IEnumerator Play(SpeechText speech,AudioSource target,Action<string> started,Action<bool,string,float> completed,
        Func<StreamingPlaybackPermission> gate,int current)
    {
        while(gate!=null && gate()==StreamingPlaybackPermission.Wait)yield return null;
        if(current!=generation || (gate!=null && gate()==StreamingPlaybackPermission.Cancel))yield break;
        output=target;owned=AudioClip.Create("controlled PCM lifecycle",48000*120,1,48000,false);
        target.clip=owned;target.loop=false;target.Play();Started=true;DestroyedOwnedClip=false;started(speech.Text);
        while(current==generation && target.isPlaying && target.time<PcmDuration)yield return null;
        if(current!=generation)yield break;
        bool reached=target.time>=PcmDuration;
        target.Stop();completed(SynthesisComplete && reached,speech.Text,PcmDuration);
        if(target.clip==owned)target.clip=null;
        Destroy(owned);owned=null;DestroyedOwnedClip=true;
    }
    public override void CancelStreaming()
    {
        generation++;StopAllCoroutines();
        if(output!=null && output.clip==owned){output.Stop();output.clip=null;}
        if(owned!=null)Destroy(owned);owned=null;
    }
}
