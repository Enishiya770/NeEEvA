using System;
using System.Collections.Generic;
using System.Linq;
using NeEEvA.Motion;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    [SerializeField, Tooltip("让说话和行动继续进行；根据实际变化局部纠正，不等待整轮语义审核。")]
    private bool m_EnableActivityMonitor = true;
    private readonly List<ActivityMonitorProtocol.SpeechUnit> m_ActivitySpeech = new List<ActivityMonitorProtocol.SpeechUnit>();
    private readonly List<JObject> m_ActivityDialogue = new List<JObject>();
    private readonly Dictionary<string, MonitoredActivity> m_RegisteredActivities = new Dictionary<string, MonitoredActivity>();
    private readonly HashSet<string> m_ActivityReviewed = new HashSet<string>();
    private readonly Queue<string> m_ActivityReviewOrder = new Queue<string>();
    private readonly Queue<float> m_ActivityRequestTimes = new Queue<float>();
    private readonly HashSet<string> m_ActivityStoppedOwners = new HashSet<string>();
    private int m_ActivitySerial, m_ActivityEpoch = -1, m_ActivityGeneration = -1, m_ActivityRequestSerial;
    private int m_ActivitySpeechCorrections, m_ActivityActionCorrections;
    private int m_RegisteredActivityRevision;
    private bool m_ActivityRequestPending;
    private float m_ActivityNextPoll, m_ActivityRequestDeadline;
    private LLM m_ActivityModel;
    private JObject m_LastActivityFeedback;
    private JObject m_LastInterruptedSpeechEvidence;
    private string m_ActivityLatePlaybackPrefix = "";

    /// <summary>Trusted executors opt in with exact versioned cancellation. No model-selected delegates.</summary>
    public sealed class MonitoredActivity
    {
        public Func<JObject> Read;
        public Func<string, string, bool> Stop;
    }
    public void RegisterMonitoredActivity(string key, MonitoredActivity activity)
    {
        if(string.IsNullOrEmpty(key)||activity?.Read==null)throw new ArgumentException("Activity requires a stable key and observation reader.");
        m_RegisteredActivities[key]=activity;
        ++m_RegisteredActivityRevision;
    }
    public void UnregisterMonitoredActivity(string key) { if(key!=null && m_RegisteredActivities.Remove(key))++m_RegisteredActivityRevision; }

    private string ActivityBinding()
    {
        int Id(UnityEngine.Object value)=>value!=null?value.GetInstanceID():0;
        var room=GetComponent<ArdyRoomDialogueBridge>();
        var motion=GetComponent<ArdyDialogueMotionBridge>();
        return string.Join(":",gameObject.scene.handle,Id(room),Id(room?.Controller),Id(room?.avatar),
            Id(room?.environmentRoot),Id(room?.userHead),Id(motion),Id(motion?.Avatar),
            Id(m_SceneVisionCapture),Id(m_SceneVisionCapture?.Target),m_RegisteredActivityRevision);
    }

    private bool ActivityMonitoringAvailable => m_EnableActivityMonitor && m_ChatSettings?.m_ChatModel?.SupportsActivityMonitor==true;
    private void CancelActivityMonitoring(bool clearSpeech = false)
    {
        ++m_ActivityRequestSerial;m_ActivityRequestPending=false;
        m_ActivityModel?.CancelActivityMonitor();m_ActivityModel=null;
        if(clearSpeech) {m_ActivitySpeech.Clear();m_ActivityReviewed.Clear();m_ActivityReviewOrder.Clear();m_ActivityGeneration=-1;m_ActivityLatePlaybackPrefix="";}
    }
    private void RecordActivityInterruption(string text, string source)
    {
        if(string.IsNullOrEmpty(text))return;
        var unit=m_ActivitySpeech.LastOrDefault(u=>u.State=="playing");
        if(unit!=null)ObserveActivitySpeech(unit,"interrupted");
        m_LastInterruptedSpeechEvidence=new JObject{["generation"]=m_FormalResponseGeneration,["source"]=source,
            ["candidateText"]=text,["wholeTextPlayed"]=false,["exactSpokenPrefixKnown"]=false,
            ["playedSamples"]=m_AudioSource!=null?m_AudioSource.timeSamples:0,
            ["scope"]="Some audio may have played. Candidate text and subtitle progress are NOT an exact heard-text receipt."};
        Debug.Log("[Activity/InterruptedSpeech] "+m_LastInterruptedSpeechEvidence.ToString(Formatting.None),this);
    }
    private void EnsureActivityOwner()
    {
        if(m_ActivityEpoch!=m_WorkEpoch) {
            CancelActivityMonitoring(true);m_ActivityEpoch=m_WorkEpoch;
            m_ActivityActionCorrections=m_ActivitySpeechCorrections=0;m_LastActivityFeedback=null;
            m_ActivityStoppedOwners.Clear();
        }
        if(m_ActivityGeneration!=m_FormalResponseGeneration) {
            CancelActivityMonitoring(true);m_ActivityGeneration=m_FormalResponseGeneration;
            m_ActivitySpeechCorrections=0;m_ActivityLatePlaybackPrefix="";
        }
    }
    private ActivityMonitorProtocol.SpeechUnit RegisterActivitySpeech(string text,string language,bool correction=false)
    {
        if(!ActivityMonitoringAvailable)return null;
        EnsureActivityOwner();
        var unit=new ActivityMonitorProtocol.SpeechUnit {Id="speech:"+m_FormalResponseGeneration+":"+(++m_ActivitySerial),
            Text=text,Language=language??"auto",IsCorrection=correction,QueuedAtRealtime=Time.realtimeSinceStartup,ObservedAtCreation=ActivitySpeechStartFacts()};
        m_ActivitySpeech.Add(unit);
        // Keep live queue entries; only retire old completed units from attention.
        if(m_ActivitySpeech.Count>48) {
            var old=m_ActivitySpeech.FirstOrDefault(u=>u.State=="completed"||u.State=="interrupted");
            if(old!=null)m_ActivitySpeech.Remove(old);
        }
        return unit;
    }
    private JObject ActivitySpeechStartFacts(bool started=false)
    {
        var facts=ActivityExecutionFacts();facts.Remove("scope");facts.Remove("inspection");
        if(facts.ToString(Formatting.None).Length>1800)facts.Remove("priorResults");
        facts["scope"]=(started?"Frozen execution observations when audio started. ":"Frozen execution observations when this phrase was queued, before audio started. ")+
            "Not proof that its wording was true. Compare with current evidence; later changes do not erase past events.";
        return facts;
    }
    private void ObserveActivitySpeech(ActivityMonitorProtocol.SpeechUnit unit,string state)
    {
        if(unit==null)return;
        if(state=="starting" && unit.ObservedAtSpeechStart==null) {
            unit.ObservedAtSpeechStart=ActivitySpeechStartFacts(true);unit.StartedAtRealtime=Time.realtimeSinceStartup;
        }
        if(state=="completed" && unit.State!="completed")RecordActivityDialogue("assistant",unit.Text,unit.Id);
        unit.State=state;
        Debug.Log("[Activity/SpeechProgress] "+new JObject{["id"]=unit.Id,["version"]=unit.Version,
            ["state"]=state,["text"]=unit.Text,["wholeTextPlayed"]=state=="completed",
            ["partialWordAlignmentKnown"]=false}.ToString(Formatting.None),this);
    }

    private void RecordActivityDialogue(string role,string text,string source)
    {
        if(string.IsNullOrWhiteSpace(text))return;
        m_ActivityDialogue.Add(new JObject{["role"]=role,["text"]=text.Length<=1000?text:text.Substring(0,1000),
            ["truncated"]=text.Length>1000,["source"]=source,["userTurn"]=m_ActionConstraints.CurrentUserTurn,
            ["generation"]=m_FormalResponseGeneration,["scope"]=role=="user"?"Accepted user utterance.":
                "Complete audio phrase played; its assertions are not automatically true."});
        if(m_ActivityDialogue.Count>16)m_ActivityDialogue.RemoveAt(0);
    }

    private RoomPlaybackCheck RefreshActivitySpeech(PendingSpeechChunk chunk)
    {
        var unit=chunk.ProcessUnit;
        if(unit==null || chunk.ProcessVersion==unit.Version)return RoomPlaybackCheck.Current;
        if(unit.Exposed)return RoomPlaybackCheck.Drop;
        chunk.Text=unit.Text;chunk.ProcessVersion=unit.Version;
        // A semantic correction has its own fact binding; an old movement
        // template must not overwrite it after it replaces the original unit.
        chunk.RoomState=null;chunk.RoomStateVersion=0;chunk.RoomPrefixOnly=false;
        return RoomPlaybackCheck.Replaced;
    }

    private JObject ReadActivityFacts(Dictionary<string,MonitoredActivity> adapters,out JArray actions)
    {
        actions=new JArray();
        var facts=new JObject {
            ["request"]=new JObject{["text"]=m_ActionConstraints.CurrentUserText,["userTurn"]=m_ActionConstraints.CurrentUserTurn},
            ["constraints"]=JToken.FromObject(m_ActionConstraints.Entries),
            ["intention"]=new JObject{["userPurpose"]=m_ActionConstraints.Purpose,["autonomous"]=m_SceneAutonomyIntention,
                ["currentWork"]=m_WorkIntent,["status"]=m_WorkStatus},
            ["dialogue"]=new JArray(m_ActivityDialogue.Skip(Math.Max(0,m_ActivityDialogue.Count-4)).Select(s=>s.DeepClone())),
            ["execution"]=ActivityExecutionFacts(),
            ["planningFailure"]=ReadRoomTaskDecisionFailure(),
            ["observation"]=new JObject{["eyesOpen"]=SceneEyesOpen,
                ["observedObjects"]=new JArray(m_SceneVisibleMemory.Where(x=>SceneVisibleCatalogHistoryBinding(x.view)&&!string.IsNullOrEmpty(x.description))
                    .OrderByDescending(x=>x.view.observation.CapturedAtRealtime).Take(4).Select(x=>new JObject{
                        ["entityId"]=x.entry.entityId,["description"]=x.description,["captureId"]=x.descriptionCaptureId,
                        ["observedAt"]=x.view.observation.CapturedAtRealtime,
                        ["scope"]="Prior observed description; not proof of current visibility or device state."}))}
        };
        foreach(var item in adapters) {
            JObject state=null;
            try{state=item.Value.Read();}catch(Exception error){Debug.LogWarning("[Activity/Observation] "+error.GetType().Name,this);}
            if(state==null||string.IsNullOrEmpty((string)state["id"])||string.IsNullOrEmpty((string)state["version"]))continue;
            state=(JObject)state.DeepClone();
            state["cancellable"]=(bool?)state["cancellable"]==true && item.Value.Stop!=null;
            state["adapter"]=item.Key;actions.Add(state);
            facts["action:"+(string)state["id"]]=state.DeepClone();
        }
        return facts;
    }
    private JObject ActivityExecutionFacts()
    {
        var current=BuildWorkRoomCompletionObservation();
        var room=current?["currentRoomFacts"] as JObject;
        return new JObject {
            ["actionId"]=current?["actionId"]?.DeepClone(),["intent"]=current?["intent"]?.DeepClone(),
            ["body"]=room==null?null:new JObject {
                ["execution"]=PickRoomFields(room["execution"] as JObject,"actionId","phase","reason","targetKind"),
                ["relation"]=PickRoomFields(room["currentFacts"] as JObject,"currentlyNearAndFacingUser","userTrackingValid","userSupportVerified")},
            ["priorResults"]=RoomTaskContextPolicy.DeliveryReceipt(PrivateProgressResultFacts(BuildMovementPriorResults())),
            ["inspection"]=string.IsNullOrEmpty(m_LastSelfInspection)?null:m_LastSelfInspection,
            ["scope"]="Actual owned outcomes; missing fields stay unknown. Speech and a pending request do not establish completion."
        };
    }
    private Dictionary<string,MonitoredActivity> ActivityAdapters()
    {
        var adapters=new Dictionary<string,MonitoredActivity>(m_RegisteredActivities);
        var room=GetComponent<ArdyRoomDialogueBridge>();
        if(room!=null)adapters["room"]=new MonitoredActivity {
            Read=()=>room.IsConnected && !string.IsNullOrEmpty(room.ActiveTaskId) ? new JObject {
                ["id"]=room.ActiveTaskId,["version"]=room.Controller.OperationRevision.ToString(),["kind"]=room.TaskMode,
                ["phase"]=room.Phase,["reason"]=room.Status,["cancellable"]=true,
                ["facts"]=new JObject{
                    ["execution"]=PickRoomFields(ReadMovementPlaybackFacts(room)["execution"] as JObject,"actionId","phase","reason","targetKind"),
                    ["relation"]=PickRoomFields(ReadMovementPlaybackFacts(room)["currentFacts"] as JObject,
                        "currentlyNearAndFacingUser","userTrackingValid","userSupportVerified")}}:null,
            Stop=(version,reason)=> {
                if(room==null || room!=GetComponent<ArdyRoomDialogueBridge>() || !room.IsConnected ||
                    room.Controller.OperationRevision.ToString()!=version || string.IsNullOrEmpty(room.ActiveTaskId))return false;
                if(!room.StopMonitoredActivity(room.ActiveTaskId,version,reason))return false;
                // Same main-thread transaction, before any producer resumes.
                CancelSceneSearchTask("process-monitor");CancelSpatialGoalRunner("process-monitor");
                PauseSpatialGoal("process-monitor: "+reason);CancelSceneAction("process-monitor");
                return true;
            }
        };
        var motion=GetComponent<ArdyDialogueMotionBridge>();
        if(motion!=null)adapters["gesture"]=new MonitoredActivity{Read=motion.ReadMonitoredActivity,Stop=motion.StopMonitoredActivity};
        if(m_SceneActionActive && m_SceneActionFact!=null && (room==null || room.ActiveTaskId!=m_SceneActionFact.actionId))
            adapters["attention"]=new MonitoredActivity{
                Read=()=>!m_SceneActionActive||m_SceneActionFact==null?null:new JObject{
                    ["id"]=m_SceneActionFact.actionId,["version"]=m_SceneActionEpoch.ToString(),["kind"]=m_SceneActionFact.intent,
                    ["phase"]=m_SceneActionFact.phase,["purpose"]=m_SceneActionFact.targetPurpose,
                    ["target"]=m_SceneActionFact.targetDescription,["cancellable"]=true},
                Stop=(version,reason)=>{
                    if(!m_SceneActionActive || m_SceneActionEpoch.ToString()!=version)return false;
                    CancelSceneSearchTask("process-monitor");CancelSpatialGoalRunner("process-monitor");
                    PauseSpatialGoal("process-monitor: "+reason);CancelSceneAction("process-monitor: "+reason);return true;
                }
            };
        if(IsSingingRuntimeActivityActive())adapters["singing"]=new MonitoredActivity {
            Read=()=>!IsSingingRuntimeActivityActive()?null:new JObject{["id"]="singing:"+m_SongSingGeneration+":"+m_HumBackGeneration,
                ["version"]=m_SongSingGeneration+":"+m_HumBackGeneration,["kind"]="singing",["cancellable"]=true,
                ["state"]=BuildSingingExecutionSnapshot()},
            Stop=(version,reason)=> {
                if(!IsSingingRuntimeActivityActive()||version!=m_SongSingGeneration+":"+m_HumBackGeneration)return false;
                CancelPendingHumBack("process-monitor: "+reason,true);return true;
            }
        };
        return adapters;
    }

    private void TickActivityMonitor()
    {
        if(!ActivityMonitoringAvailable || !isActiveAndEnabled || m_UserSpeechActiveForAutonomy || m_SceneTimePaused) {
            if(m_ActivityRequestPending)CancelActivityMonitoring();return;
        }
        EnsureActivityOwner();
        float now=Time.realtimeSinceStartup;
        if(m_ActivityRequestPending) {
            if(now>m_ActivityRequestDeadline) {
                Debug.LogWarning("[Activity/Monitor] watchdog-timeout executionContinues=True",this);
                CancelActivityMonitoring();
            }
            return;
        }
        if(now<m_ActivityNextPoll)return;
        m_ActivityNextPoll=now+1f;
        while(m_ActivityRequestTimes.Count>0&&now-m_ActivityRequestTimes.Peek()>60f)m_ActivityRequestTimes.Dequeue();
        if(m_ActivityRequestTimes.Count>=12)return;
        var adapters=ActivityAdapters();
        var facts=ReadActivityFacts(adapters,out var actions);
        var units=m_ActivitySpeech.Where(u=>!u.IsCorrection && !u.CorrectedAfterExposure && u.Corrections<2).ToList();
        // Attend to the current/next phrase and one exposed predecessor. Later
        // queued words enter attention as playback advances; memory is retained.
        var speech=new JArray(units.Where(u=>u.State=="starting"||u.State=="playing"||u.State=="queued").Take(2).Select(u=>u.Snapshot()));
        foreach(var unit in units.Where(u=>u.State=="completed"||u.State=="interrupted").Reverse().Take(1))speech.Add(unit.Snapshot());
        if(actions.Count==0&&speech.Count==0)return;
        string binding=ActivityBinding();
        var frame=new JObject{["capturedAtRealtime"]=now,["owner"]=new JObject{["generation"]=m_FormalResponseGeneration,["workEpoch"]=m_WorkEpoch,["binding"]=binding,
            ["userTurn"]=m_ActionConstraints.CurrentUserTurn},["facts"]=facts,["actions"]=actions,["speech"]=speech};
        // Playback advancement is not a reason to rerun the same semantic check.
        var keyFrame=(JObject)frame.DeepClone();
        keyFrame.Remove("capturedAtRealtime");
        foreach(var unit in ((JArray)keyFrame["speech"]).OfType<JObject>()){unit.Remove("state");unit.Remove("wholeTextPlayed");}
        // Raw pose noise does not require a language-model decision every metre/frame.
        foreach(var action in ((JArray)keyFrame["actions"]).OfType<JObject>()) {
            var relation=action.SelectToken("facts.currentFacts") as JObject;
            relation?.Remove("actualUserDistance");relation?.Remove("actualFacingErrorDegrees");
            ((JObject)keyFrame["facts"])["action:"+(string)action["id"]]=action.DeepClone();
        }
        // A conversation append at audio completion and sampling progress are
        // not fresh semantic evidence. Actions still trigger on phase/version,
        // observations on new capture, and speech on newly queued words.
        ((JObject)keyFrame["facts"]).Remove("dialogue");
        foreach(var action in ((JArray)keyFrame["actions"]).OfType<JObject>()) {
            if((string)action["kind"]=="singing") {
                action.Remove("state");
                ((JObject)keyFrame["facts"])["action:"+(string)action["id"]]=action.DeepClone();
            }
        }
        string key=keyFrame.ToString(Formatting.None);
        if(m_ActivityReviewed.Contains(key))return;
        // Keep monitoring a long-running activity; bounded rolling attention
        // must not permanently turn off after the first 32 observations.
        while(m_ActivityReviewOrder.Count>=32)m_ActivityReviewed.Remove(m_ActivityReviewOrder.Dequeue());
        m_ActivityReviewed.Add(key);
        m_ActivityReviewOrder.Enqueue(key);
        string input=frame.ToString(Formatting.None);
        if(input.Length>ActivityMonitorProtocol.MaximumInputCharacters) {
            Debug.LogWarning("[Activity/Monitor] skipped-input-budget characters="+input.Length,this);return;
        }
        var model=m_ChatSettings.m_ChatModel;
        int request=++m_ActivityRequestSerial,epoch=m_WorkEpoch,generation=m_FormalResponseGeneration,turn=m_ActionConstraints.CurrentUserTurn;
        m_ActivityModel=model;m_ActivityRequestPending=true;m_ActivityRequestDeadline=now+9f;
        m_ActivityRequestTimes.Enqueue(now);
        Debug.Log("[Activity/MonitorFrame] "+new JObject{["owner"]=frame["owner"].DeepClone(),["characters"]=input.Length,
            ["speech"]=speech.DeepClone(),["actions"]=actions.DeepClone(),["evidenceIds"]=new JArray(facts.Properties().Select(p=>p.Name))}.ToString(Formatting.None),this);
        try { model.PostActivityMonitor(input,(ok,output,status)=> {
            if(request!=m_ActivityRequestSerial)return;
            m_ActivityRequestPending=false;m_ActivityNextPoll=Time.realtimeSinceStartup+1f;
            if(epoch!=m_WorkEpoch||generation!=m_FormalResponseGeneration||turn!=m_ActionConstraints.CurrentUserTurn||
                model!=m_ChatSettings?.m_ChatModel||binding!=ActivityBinding()||
                m_UserSpeechActiveForAutonomy||m_SceneTimePaused||!isActiveAndEnabled)return;
            JObject decision=null;string rejection=status;
            if(!ok || !ActivityMonitorProtocol.TryRead(output,frame,out decision,out rejection)) {
                Debug.LogWarning("[Activity/Monitor] unavailable="+rejection+" executionContinues=True",this);return;
            }
            var currentAdapters=ActivityAdapters();var liveFacts=ReadActivityFacts(currentAdapters,out var liveActions);
            ApplyActivityDecision(frame,decision,liveFacts,liveActions,currentAdapters);
        }); } catch(Exception error) {
            CancelActivityMonitoring();
            Debug.LogWarning("[Activity/Monitor] provider-exception="+error.GetType().Name+" executionContinues=True",this);
        }
    }

    private void ApplyActivityDecision(JObject frame,JObject decision,JObject liveFacts,JArray liveActions,
        Dictionary<string,MonitoredActivity> adapters)
    {
        var speech=(JObject)decision["speech"];var action=(JObject)decision["action"];
        string speechResult="continue",actionResult="continue";
        if((string)speech["issue"]!="none" && m_ActivitySpeechCorrections<2 &&
            ActivityMonitorProtocol.EvidenceCurrent(speech,(JObject)frame["facts"],liveFacts)) {
            var unit=m_ActivitySpeech.FirstOrDefault(u=>u.Id==(string)speech["id"]);
            speechResult=ActivityMonitorProtocol.Revise(unit,speech,out string correction);
            if(speechResult!="stale") {
                m_ActivitySpeechCorrections++;
                if(correction!=null)QueueActivityCorrection(correction,unit.Language);
            }
        }
        string stopOwner=(string)action["id"]+"@"+(string)action["version"];
        if((string)action["operation"]!="continue" && !m_ActivityStoppedOwners.Contains(stopOwner) &&
            ActivityMonitorProtocol.EvidenceCurrent(action,(JObject)frame["facts"],liveFacts)) {
            var current=liveActions.OfType<JObject>().FirstOrDefault(a=>(string)a["id"]==(string)action["id"]&&
                (string)a["version"]==(string)action["version"]&&(bool?)a["cancellable"]==true);
            bool stopped=false;
            if(current!=null && adapters.TryGetValue((string)current["adapter"],out var adapter)) {
                try {stopped=adapter.Stop((string)action["version"],(string)action["reason"]);}
                catch(Exception error){Debug.LogWarning("[Activity/Stop] executor-error="+error.GetType().Name,this);}
            }
            if(stopped) {
                m_ActivityStoppedOwners.Add(stopOwner);
                m_ActivityActionCorrections++;actionResult="stopped";
                // Keep the original request and failures. A new intention is input
                // to normal planning, never authorization or a completion receipt.
                m_WorkSubstepReviewPending=m_WorkReviewOpen;
                ScheduleNextTick(0.2f,"activity-monitor-feedback");
            } else actionResult="stale-owner";
        }
        if(speechResult=="continue" && actionResult=="continue")return;
        m_LastActivityFeedback=new JObject{["generation"]=m_FormalResponseGeneration,["workEpoch"]=m_WorkEpoch,
            ["speechDisposition"]=speechResult,["actionDisposition"]=actionResult,["decision"]=decision.DeepClone(),
            ["scope"]="Observed process intervention. Model reasons are interpretations. A proposed next intention has NOT executed; completed earlier effects remain."};
        Debug.Log("[Activity/Intervention] "+m_LastActivityFeedback.ToString(Formatting.None),this);
    }
    private string BuildActivityFeedbackContext() =>
        (m_LastActivityFeedback==null ? "" : "\n[持续过程监控反馈；保留原请求及真实执行结果，nextIntent只供重新考虑，不是新授权]\n"+m_LastActivityFeedback.ToString(Formatting.None))+
        (m_LastInterruptedSpeechEvidence==null?"":"\n[最近被打断的语音；仅完整播完的块可作为已交付原话]\n"+m_LastInterruptedSpeechEvidence.ToString(Formatting.None));

    private void BindActivityOrdinaryHistory(int generation, LLM model)
    {
        if(!ActivityMonitoringAvailable || generation!=m_FormalResponseGeneration || model==null)return;
        var history=model.m_DataList.LastOrDefault(m=>m.role=="assistant");
        if(history==null)return;
        BindRoomSpeechPlayback(generation,null,null,model,history);
        m_DeliveryHeardStart=0; // Include any initial chunks that already drained.
    }

    private void QueueActivityCorrection(string text,string language)
    {
        bool senderFinished=m_TTSSenderDone;
        bool drained=m_TTSSenderDone && m_PendingChunks.Count==0 && m_PendingClips.Count==0 && !IsAISpeaking;
        var correctionChunks=new List<PendingSpeechChunk>();
        string remaining=text;
        while(remaining.Length>0) {
            int boundary=SpeechChunkPolicy.Boundary(remaining,false,32);
            int length=boundary<0?remaining.Length:boundary+1;
            string phrase=remaining.Substring(0,length);remaining=remaining.Substring(length);
            if(string.IsNullOrWhiteSpace(phrase))continue;
            var unit=RegisterActivitySpeech(phrase,language,true);
            correctionChunks.Add(new PendingSpeechChunk{RoundId=m_CurrentSpeechRoundId,Text=phrase,LanguageCode=language,ProcessUnit=unit});
        }
        for(int i=correctionChunks.Count-1;i>=0;i--)PrependSpeechChunk(correctionChunks[i]);
        ClearScenePlaybackReview("process-speech-correction");
        if(drained) {
            // No full-response restart, action parser, or replay of earlier text.
            m_StreamComplete=true;m_TTSSenderDone=false;m_AgentRoundInFlight=m_AgentRunning;
            m_ActivityLatePlaybackPrefix=(string)m_LastFormalReplyReceipt?["completedPlaybackText"]??"";
            var history=new LLM.SendData("assistant","");m_ChatSettings.m_ChatModel.m_DataList.Add(history);
            BindRoomSpeechPlayback(m_FormalResponseGeneration,language,null,m_ChatSettings.m_ChatModel,history);
            if(m_ChatSettings.m_TextToSpeech.SupportsStreamingPlayback)StartCoroutine(StreamDirectTTSPlayer(m_FormalResponseGeneration));
            else {StartCoroutine(StreamTTSSender(m_FormalResponseGeneration));StartCoroutine(StreamAudioPlayer(m_FormalResponseGeneration));}
        }
        else if(senderFinished && !m_ChatSettings.m_TextToSpeech.SupportsStreamingPlayback) {
            // Buffered TTS may have completed synthesis while audio is still
            // playing. Restart only the sender, never a second audio player.
            m_TTSSenderDone=false;StartCoroutine(StreamTTSSender(m_FormalResponseGeneration));
        }
    }

    private void EnqueueActivityPreparedClip(PendingSpeechClip clip)
    {
        if(clip.ProcessUnit?.IsCorrection!=true){m_PendingClips.Enqueue(clip);return;}
        var ordered=m_PendingClips.ToList();
        int at=ordered.FindIndex(item=>item.ProcessUnit?.IsCorrection!=true);
        ordered.Insert(at<0?ordered.Count:at,clip);m_PendingClips=new Queue<PendingSpeechClip>(ordered);
    }

    private bool TryPublishProcessMonitoredSpeech(SpeechTextBuffer buffer,string executable,int generation,
        bool recordHistory,ObservationReceipt observations)
    {
        if(!ActivityMonitoringAvailable || generation!=m_FormalResponseGeneration || m_UserSpeechActiveForAutonomy ||
            m_SceneTimePaused || !isActiveAndEnabled || m_RoomTaskFormatError)return false;
        string candidate=buffer.ToString();
        if(!RoomTaskProtocol.IsPlainSpeech(candidate) || string.IsNullOrWhiteSpace(candidate) ||
            buffer.Snapshot().Any(p=>p.ActionDependency==SpeechActionDependency.AfterAction))return false;
        // Necessary local completion/ownership checks remain at publication.
        if(generation==m_SpatialGoalTerminalReplyGeneration && !ValidateSpatialGoalTerminalProof(generation))return false;
        m_RoomSpeechGuardGeneration=-1;
        if(m_PendingSceneFormalCaptureGeneration==generation)m_PendingSceneFormalCaptureGeneration=-1;
        RecordFinalRoomReply(generation,candidate,"process-monitored",GetComponent<ArdyRoomDialogueBridge>());
        if(m_LastFormalReplyReceipt!=null) {
            m_LastFormalReplyReceipt["semanticPreapproval"]=false;
            m_LastFormalReplyReceipt["monitoringMode"]="ongoing-local-correction";
        }
        var history=recordHistory?new LLM.SendData("assistant",candidate):null;
        if(history!=null)m_ChatSettings.m_ChatModel.m_DataList.Add(history);
        BindRoomSpeechPlayback(generation,buffer.LanguageCode,GetComponent<ArdyRoomDialogueBridge>(),m_ChatSettings.m_ChatModel,history);
        BindReviewedGoalAnswerSpeech(generation,candidate);
        foreach(var part in buffer.Snapshot())OnSpeechStreamDelta(part);
        AcknowledgeObservations(observations,generation,candidate);
        m_FormalResponseInFlight=false;OnStreamComplete(candidate);
        Debug.Log("[Activity/Publication] generation="+generation+" mode=ongoing-monitor serialSemanticReview=False",this);
        return true;
    }
}
