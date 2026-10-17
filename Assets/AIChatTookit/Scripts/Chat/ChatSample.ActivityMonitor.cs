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
    private readonly ActivityMonitorAttempts m_ActivityAttempts = new ActivityMonitorAttempts();
    private string m_ActivityPendingKey;
    private JObject m_ActivityPendingFrame;
    private string m_ActivityLastPurpose;
    private JObject m_ActivityReplyObservation;
    private int m_ActivityReplyObservationGeneration=-1, m_ActivityPublishedGeneration=-1;
    private int m_ActivityPlanningFailureReportedGeneration=-1;
    private int m_ActivityUserTurn=-1;
    private readonly Dictionary<string,int> m_ActivityDeliverySends=new Dictionary<string,int>();
    private readonly List<string> m_ActivityRejectedCandidates=new List<string>();

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
        if(clearSpeech) {m_ActivitySpeech.Clear();m_ActivityReviewed.Clear();m_ActivityReviewOrder.Clear();m_ActivityAttempts.Clear();m_ActivityGeneration=-1;m_ActivityLatePlaybackPrefix="";}
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
        if(m_ActivityEpoch!=m_WorkEpoch || m_ActivityUserTurn!=m_ActionConstraints.CurrentUserTurn) {
            CancelActivityMonitoring(true);m_ActivityEpoch=m_WorkEpoch;
            m_ActivityUserTurn=m_ActionConstraints.CurrentUserTurn;
            m_ActivityActionCorrections=m_ActivitySpeechCorrections=0;m_LastActivityFeedback=null;
            m_ActivityStoppedOwners.Clear();
            m_ActivityDeliverySends.Clear();m_ActivityRejectedCandidates.Clear();
        }
        if(m_ActivityGeneration!=m_FormalResponseGeneration) {
            CancelActivityMonitoring(true);m_ActivityGeneration=m_FormalResponseGeneration;
            m_ActivityVisualAttempts.Clear();m_ActivityVisualEvidence.Clear();
            m_ActivityDeliveryLanguage=null;
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
        if(!correction && SpeechText.NormalizeLanguage(language)!=null)m_ActivityDeliveryLanguage=language;
        BeginSpeechLanguageCheck(unit);
        // Keep live queue entries; only retire old completed units from attention.
        if(m_ActivitySpeech.Count>48) {
            var old=m_ActivitySpeech.FirstOrDefault(u=>u.State=="completed"||u.State=="interrupted"||u.State=="superseded");
            if(old!=null) {
                Debug.Log("[Activity/AttentionRetired] "+new JObject{["id"]=old.Id,["version"]=old.Version,
                    ["state"]=old.State,["reason"]="bounded-attention-capacity",["checkedByRetirement"]=false,
                    ["scope"]="Attention retirement does not certify a check or erase actual playback history."}.ToString(Newtonsoft.Json.Formatting.None),this);
                m_ActivitySpeech.Remove(old);
            }
        }
        return unit;
    }
    private JObject ActivitySpeechStartFacts(bool started=false)
    {
        var facts=ActivityExecutionFacts();facts.Remove("scope");facts.Remove("inspection");
        facts["formalObservation"]=ActivityFormalObservation();
        facts.Remove("priorResults");
        facts["completedResultIds"]=new JArray(((JArray)ReadWorkDelivery()["results"]).OfType<JObject>().Select(r=>(string)r["id"]));
        facts["scope"]=(started?"Frozen execution observations when audio started. ":"Frozen execution observations when this phrase was queued, before audio started. ")+
            "Not proof that its wording was true. Compare with current evidence; later changes do not erase past events.";
        ActivityEvidencePolicy.AddReceipts(facts,null,((JArray)ReadWorkDelivery()["results"]).OfType<JObject>());
        foreach(var item in m_ActivityVisualEvidence)facts["visual:"+item.Key]=item.Value.DeepClone();
        return facts;
    }
    private void ObserveActivitySpeech(ActivityMonitorProtocol.SpeechUnit unit,string state)
    {
        if(unit==null)return;
        if(state=="starting" && unit.ObservedAtSpeechStart==null) {
            unit.ObservedAtSpeechStart=ActivitySpeechStartFacts(true);unit.StartedAtRealtime=Time.realtimeSinceStartup;
        }
        if(state=="completed" && unit.State!="completed") {
            RecordActivityDialogue("assistant",unit.Text,unit.Id);
            RecordWorkCorrectionBoundary(unit);
        }
        unit.State=state;
        Debug.Log("[Activity/SpeechProgress] "+new JObject{["id"]=unit.Id,["version"]=unit.Version,
            ["state"]=state,["text"]=unit.Text,["language"]=unit.Language,["wholeTextPlayed"]=state=="completed",
            ["partialWordAlignmentKnown"]=false}.ToString(Formatting.None),this);
    }

    private void RetireUnplayedActivitySpeech(ActivityMonitorProtocol.SpeechUnit unit,string reason)
    {
        // Already exposed audio is historical evidence and may still need a
        // correction. Only audio removed before starting loses its live lease.
        if(unit==null||unit.State!="queued")return;
        unit.LanguageCheckPending=false;unit.Version++;
        ObserveActivitySpeech(unit,"superseded");
        Debug.Log("[Activity/SpeechRetired] "+new JObject{["id"]=unit.Id,["version"]=unit.Version,
            ["reason"]=reason,["wholeTextPlayed"]=false,["historyRetained"]=true}.ToString(Formatting.None),this);
    }

    private void ClearPendingSpeechChunks(string reason)
    {
        foreach(var chunk in m_PendingChunks)RetireUnplayedActivitySpeech(chunk.ProcessUnit,reason);
        m_PendingChunks.Clear();
    }
    private void ClearPendingSpeechClips(string reason)
    {
        foreach(var clip in m_PendingClips)RetireUnplayedActivitySpeech(clip.ProcessUnit,reason);
        m_PendingClips.Clear();
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
        if(unit?.State=="superseded")return RoomPlaybackCheck.Drop;
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
            ["historyAnswers"]=new JObject { ["taskEvidence"]=ReadRoomTaskSequence()?["answerEvidence"]?.DeepClone(),
                ["visualRecallState"]=ReadSceneRecallFacts()["state"],
                ["availability"]="Partial retained sources only; unselected, empty or missing data never proves nonoccurrence." },
            ["observation"]=new JObject{["eyesOpen"]=SceneEyesOpen,["formalSource"]=ActivityFormalObservation(),
                ["monitorPixelsAvailable"]=false,
                ["descriptionCoverage"]="Partial prior descriptions only; empty means not established, never a negative observation.",
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
            // The live room adapter owns one sampled snapshot for this frame.
            // Do not mix its relation with a second, differently scoped receipt.
            if(item.Key=="room" && state["facts"] is JObject body &&
                facts["execution"] is JObject execution && (string)execution["actionId"]==(string)state["id"])
            {
                // Keep task/entity identity and custom fields from the same
                // owned body; only sampled live fields replace their old values.
                if(execution["body"] is JObject previousBody)
                    foreach(var property in previousBody.Properties()) {
                        if(body[property.Name]==null)body[property.Name]=property.Value.DeepClone();
                        else if(property.Value is JObject previousNode && body[property.Name] is JObject currentNode)
                            foreach(var field in previousNode.Properties())
                                if(currentNode.Property(field.Name)==null)currentNode[field.Name]=field.Value.DeepClone();
                    }
                execution["body"]=body.DeepClone();
            }
            state["cancellable"]=(bool?)state["cancellable"]==true && item.Value.Stop!=null;
            state["adapter"]=item.Key;actions.Add(state);
            var task=ActivityTaskContext(state);
            if(task!=null)state["task"]=task.DeepClone();
            facts["action:"+(string)state["id"]]=state.DeepClone();
        }
        ActivityEvidencePolicy.AddReceipts(facts,null,((JArray)ReadWorkDelivery()["results"]).OfType<JObject>());
        foreach(var recalled in ((JArray)ReadSceneRecallFacts()["records"]).OfType<JObject>())
            // The complete source ID remains in the fact. A short stable key
            // avoids repeating long capture IDs throughout the native schema.
            facts["recalled:"+ActivityEvidencePolicy.Stamp(recalled["sourceCaptureId"])]=PickRoomFields(recalled,
                "sourceCaptureId","capturedAtUtc","question","assessment","finding","entityId","currentlyVisible","noticedAtCapture");
        foreach(var item in m_ActivityVisualEvidence)facts["visual:"+item.Key]=item.Value.DeepClone();
        return facts;
    }
    private JObject ActivityFormalObservation() => m_ActivityReplyObservationGeneration==m_FormalResponseGeneration
        ? (JObject)m_ActivityReplyObservation?.DeepClone()
        : new JObject{["formalPixelsProvided"]=false,["state"]="source-not-provided-to-monitor"};

    private static JObject ActivityEvidenceScopes(JObject facts)
    {
        var scopes=new JObject();
        foreach(var field in facts.Properties()) {
            string domain=field.Name=="request"||field.Name=="constraints"?"request":
                field.Name=="historyAnswers"?"history":
                field.Name=="execution"||field.Name=="planningFailure"||field.Name.StartsWith("action:",StringComparison.Ordinal)||field.Name.StartsWith("result:",StringComparison.Ordinal)?"execution":"other";
            bool substantive=field.Value.Type!=JTokenType.Null;
            scopes[field.Name]=domain=="history"
                ? ActivityMonitorContextPolicy.Scope(domain,substantive?new[]{"uncertainty"}:new string[0])
                : ActivityMonitorContextPolicy.Scope(domain,substantive && (domain=="execution"||domain=="request")
                    ?new[]{"mistake","world-change","uncertainty"}:new string[0]);
            if(field.Name.StartsWith("result:",StringComparison.Ordinal)) {
                scopes[field.Name]["binding"]=field.Value["binding"]?.DeepClone();
                scopes[field.Name]["issues"]=(bool?)field.Value["detailsAvailable"]==true?new JArray("mistake","uncertainty"):new JArray();
            }
            else if(domain=="execution" && substantive)
                scopes[field.Name]["binding"]=ActivityEvidencePolicy.Binding(field.Value as JObject,
                    (string)field.Value["intent"]??(string)field.Value["kind"]??field.Name,(string)field.Value["actionId"]??(string)field.Value["id"]);
            if(field.Name.StartsWith("recalled:",StringComparison.Ordinal))
                scopes[field.Name]=ActivityMonitorContextPolicy.Scope("history",(string)field.Value["assessment"]=="supported"?new[]{"mistake","uncertainty"}:new[]{"uncertainty"});
        }
        // Source association is not image inspection. Prior positive object
        // descriptions cannot prove that an unlisted object is absent now.
        scopes["observation"]=ActivityMonitorContextPolicy.Scope("visual");
        foreach(var field in facts.Properties().Where(p=>p.Name.StartsWith("visual:"))) {
            string assessment=(string)field.Value["assessment"];
            scopes[field.Name]=ActivityMonitorContextPolicy.Scope("visual",assessment=="contradicted"?"mistake":assessment=="unresolved"?"uncertainty":"none");
        }
        return scopes;
    }

    private void ActivityMonitorFailed(string key,string status,string output=null,JObject frame=null)
    {
        bool needsSource=frame!=null&&ActivityMonitorContextPolicy.NeedsNewSource(status,output,frame);
        int attempts=m_ActivityAttempts.Failed(key,Time.realtimeSinceStartup,status,output,needsSource);
        Debug.LogWarning("[Activity/MonitorUnchecked] "+new JObject{["reason"]=status,["attempt"]=attempts,
            ["retryRemaining"]=needsSource?0:Math.Max(0,3-attempts),["requiresNewSource"]=needsSource,["reviewed"]=false,["executionContinues"]=true,
            ["requestId"]=frame?["monitorRequestId"],["owner"]=frame?["owner"]?.DeepClone(),
            ["rawVerdict"]=output??""}.ToString(Formatting.None),this);
        if(needsSource && !string.IsNullOrEmpty(output))TryActivityVisualRecheck(frame,output);
    }
    private JObject ActivityExecutionFacts()
    {
        var current=BuildWorkRoomCompletionObservation();
        var room=current?["currentRoomFacts"] as JObject;
        // A live substep need not have a terminal work receipt yet.
        if(room==null)room=ReadCompactRoomTaskFacts(GetComponent<ArdyRoomDialogueBridge>());
        string actionId = (string)room?["execution"]?["actionId"];
        var scene = BuildSceneActionFacts()["lastAction"] as JObject;
        bool sameScene = !string.IsNullOrEmpty(actionId) && actionId == (string)scene?["actionId"];
        bool sameWork = !string.IsNullOrEmpty(actionId) && actionId == (string)current?["actionId"];
        return new JObject {
            ["actionId"]=actionId,
            ["intent"]=sameScene ? scene["intent"]?.DeepClone() : sameWork ? current["intent"]?.DeepClone() : room?["execution"]?["intent"]?.DeepClone(),
            ["entityId"]=sameScene ? scene["entityId"]?.DeepClone() : null,
            ["body"]=room==null?null:new JObject {
                ["execution"]=PickRoomFields(room["execution"] as JObject,"actionId","phase","reason","targetKind","taskMode","sceneTargetId"),
                ["relation"]=PickRoomFields(room["currentFacts"] as JObject,"currentlyNearAndFacingUser","userTrackingValid","userSupportVerified")},
            ["scope"]="Actual owned outcomes; missing fields stay unknown. Speech and a pending request do not establish completion."
        };
    }
    private static JObject ReadRoomMonitorActivity(ArdyRoomDialogueBridge room)
    {
        if(!room.IsConnected || string.IsNullOrEmpty(room.ActiveTaskId))return null;
        var sampled=ReadMovementPlaybackFacts(room);
        return new JObject {
            ["id"]=room.ActiveTaskId,["version"]=room.Controller.OperationRevision.ToString(),["kind"]=room.TaskMode,
            ["phase"]=room.Phase,["reason"]=room.Status,["cancellable"]=true,
            ["facts"]=new JObject {
                ["execution"]=PickRoomFields(sampled["execution"] as JObject,"actionId","phase","reason","targetKind"),
                ["relation"]=PickRoomFields(sampled["currentFacts"] as JObject,"currentlyNearAndFacingUser",
                    "userTrackingValid","userSupportVerified","observationFrame","observedAtRealtime")}};
    }
    private Dictionary<string,MonitoredActivity> ActivityAdapters()
    {
        var adapters=new Dictionary<string,MonitoredActivity>(m_RegisteredActivities);
        var room=GetComponent<ArdyRoomDialogueBridge>();
        if(room!=null)adapters["room"]=new MonitoredActivity {
            Read=()=>ReadRoomMonitorActivity(room),
            Stop=(version,reason)=> {
                if(room==null || room!=GetComponent<ArdyRoomDialogueBridge>() || !room.IsConnected ||
                    room.Controller.OperationRevision.ToString()!=version || string.IsNullOrEmpty(room.ActiveTaskId))return false;
                if(!room.StopMonitoredActivity(room.ActiveTaskId,version,reason))return false;
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
                if(m_ActivityPendingKey!=null)ActivityMonitorFailed(m_ActivityPendingKey,"watchdog-timeout",frame:m_ActivityPendingFrame);
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
        var delivery=ReadActivityDelivery();
        var units=m_ActivitySpeech.Where(u=>u.State!="superseded" && !u.IsCorrection && !u.CorrectedAfterExposure && u.Corrections<2).ToList();
        // Attend to current/next phrases and a bounded completed backlog. Later
        // queued words enter attention as playback advances; memory is retained.
        var speech=new JArray(units.Where(u=>u.State=="starting"||u.State=="playing"||u.State=="queued").Take(2).Select(ActivitySpeechWindow));
        foreach(var unit in units.Where(u=>u.State=="completed"||u.State=="interrupted").Take(48))speech.Add(ActivitySpeechWindow(unit));
        foreach(var unit in units.Where(u=>m_ActivityVisualEvidence.ContainsKey(u.Id)))
            if(!speech.OfType<JObject>().Any(row=>(string)row["id"]==unit.Id))speech.Add(ActivitySpeechWindow(unit));
        // Delivery of one step cannot hide a simultaneous claim about another.
        if(actions.Count==0&&speech.Count==0&&delivery==null)return;
        string binding=ActivityBinding();
        var frame=new JObject{["evidenceVersion"]=2,["capturedAtRealtime"]=now,["owner"]=new JObject{["generation"]=m_FormalResponseGeneration,["workEpoch"]=m_WorkEpoch,["binding"]=binding,
            ["userTurn"]=m_ActionConstraints.CurrentUserTurn},["facts"]=facts,["actions"]=actions,["speech"]=speech,
            ["evidenceScopes"]=ActivityEvidenceScopes(facts)};
        if(delivery!=null)frame["delivery"]=delivery;
        // Each purpose has a small complete schema. Round-robin selection keeps
        // speech and delivery from starving live actions, even on failed frames.
        var selected=ActivityEvidencePolicy.AttentionFrames(frame)
            .OrderBy(f=>(string)f["purpose"]==m_ActivityLastPurpose?1:0)
            .FirstOrDefault(f=>!m_ActivityReviewed.Contains(ActivityEvidencePolicy.SemanticKey(f)) &&
                m_ActivityAttempts.CanAttempt(ActivityEvidencePolicy.SemanticKey(f),now));
        if(selected==null)return;
        frame=selected;m_ActivityLastPurpose=(string)frame["purpose"];
        string key=ActivityEvidencePolicy.SemanticKey(frame);
        if(m_ActivityReviewed.Contains(key)||!m_ActivityAttempts.CanAttempt(key,now))return;
        var model=m_ChatSettings.m_ChatModel;
        int request=++m_ActivityRequestSerial,epoch=m_WorkEpoch,generation=m_FormalResponseGeneration,turn=m_ActionConstraints.CurrentUserTurn;
        frame["monitorRequestId"]=Guid.NewGuid().ToString("N");
        var feedback=m_ActivityAttempts.Feedback(key);
        if(feedback!=null && frame["delivery"] is JObject deliveryRetry &&
            new[]{"delivery-","playback-"}.Any(prefix=>((string)feedback["rejection"]??"").StartsWith(prefix,StringComparison.Ordinal))) {
            feedback=ActivityDeliveryProtocol.RepairFeedback(feedback);
            JObject previous=null;
            try { previous=JObject.Parse((string)feedback["rejectedVerdict"]??"{}"); } catch(Newtonsoft.Json.JsonException) { }
            feedback["fieldRepair"]=ResultPlaybackSelection.Repair(deliveryRetry,previous?["delivery"] as JObject,
                (string)feedback["rejection"],true);
        }
        if(feedback!=null)frame["previousAttempt"]=feedback;
        string input,diagnostics;
        var before=ActivityMonitorContextPolicy.Sizes(frame);
        // Admission failures consume the same rate/attempt limits as wire sends.
        m_ActivityRequestTimes.Enqueue(now);
        try {input=model.PrepareActivityMonitorFrame(ActivityMonitorContextPolicy.Project(frame,0).ToString(Formatting.None),out diagnostics);}
        catch(Exception error) {ActivityMonitorFailed(key,"prepare-"+error.GetType().Name,frame:frame);return;}
        Debug.Log("[Activity/MonitorBudgetPlan] "+new JObject{["requestId"]=frame["monitorRequestId"],
            ["before"]=before,["projection"]=JObject.Parse(diagnostics)}.ToString(Formatting.None),this);
        if(string.IsNullOrEmpty(input)||input.Length>ActivityMonitorProtocol.MaximumInputCharacters) {
            Debug.LogWarning("[Activity/MonitorUnadmittedFrame] "+frame.ToString(Formatting.None),this);
            ActivityMonitorFailed(key,"complete-request-budget",frame:frame);return;
        }
        frame=ActivityEvidencePolicy.Parse(input); // Validate exactly the evidence sent to the model.
        m_ActivityPendingKey=key;
        m_ActivityPendingFrame=(JObject)frame.DeepClone();
        m_ActivityModel=model;m_ActivityRequestPending=true;m_ActivityRequestDeadline=now+9f;
        Debug.Log("[Activity/MonitorFrame] "+frame.ToString(Formatting.None),this);
        try { model.PostActivityMonitor(input,(ok,output,status)=> {
            if(request!=m_ActivityRequestSerial)return;
            m_ActivityRequestPending=false;m_ActivityNextPoll=Time.realtimeSinceStartup+1f;
            if(status=="activity-monitor-cancelled")return; // Terminal cancellation, not an unchecked model failure.
            if(epoch!=m_WorkEpoch||generation!=m_FormalResponseGeneration||turn!=m_ActionConstraints.CurrentUserTurn||
                model!=m_ChatSettings?.m_ChatModel||binding!=ActivityBinding()||
                m_UserSpeechActiveForAutonomy||m_SceneTimePaused||!isActiveAndEnabled)return;
            JObject decision=null;string rejection=status;
            if(!ok || !ActivityMonitorProtocol.TryRead(output,frame,out decision,out rejection)) {
                ActivityMonitorFailed(key,rejection,output,frame);return;
            }
            if((string)decision["speechAct"]=="factual-claim" && (string)decision["claimDomain"]=="visual" &&
                (string)decision["truthWhenSaid"]=="unknown" &&
                !((JObject)frame["facts"]).Properties().Any(p=>p.Name.StartsWith("visual:",StringComparison.Ordinal))) {
                // A valid "none" with unavailable pixels is not a completed
                // visual check. Seek the same bounded source without stopping work.
                ActivityMonitorFailed(key,"speech-evidence-domain-or-coverage-mismatch",output,frame);return;
            }
            void CompleteCheckedFrame() {
            if(request!=m_ActivityRequestSerial||epoch!=m_WorkEpoch||generation!=m_FormalResponseGeneration||
                turn!=m_ActionConstraints.CurrentUserTurn||model!=m_ChatSettings?.m_ChatModel||binding!=ActivityBinding()||
                m_UserSpeechActiveForAutonomy||m_SceneTimePaused||!isActiveAndEnabled)return;
            var currentAdapters=ActivityAdapters();var liveFacts=ReadActivityFacts(currentAdapters,out var liveActions);
            // Rebuild the same attention projection. Unrelated omitted history
            // must not invalidate a current action/status correction; every
            // cited projected field and the exact executor version still match.
            var liveFrame=ActivityEvidencePolicy.WithLiveFacts(frame,liveFacts,ActivityEvidenceScopes(liveFacts));
            liveFacts=(JObject)ActivityMonitorContextPolicy.Project(ActivityEvidencePolicy.Focus(liveFrame),(int?)frame.SelectToken("projection.pressure")??0)["facts"];
            bool stale=(new[]{decision["speech"] as JObject,decision["action"] as JObject}).Any(row=>
                (row?["evidenceIds"] as JArray)?.Count>0&&!ActivityMonitorProtocol.EvidenceCurrent(row,(JObject)frame["facts"],liveFacts));
            if(stale) {ActivityMonitorFailed(key,"evidence-changed-before-apply",output,frame);return;}
            ApplyActivityDecision(frame,decision,liveFacts,liveActions,currentAdapters);
            string deliveryRejection=ApplyActivityDelivery(frame,decision["delivery"] as JObject);
            if(deliveryRejection!=null) {ActivityMonitorFailed(key,deliveryRejection,output,frame);return;}
            // Only a successful checked frame enters the reviewed cache.
            while(m_ActivityReviewOrder.Count>=32)m_ActivityReviewed.Remove(m_ActivityReviewOrder.Dequeue());
            m_ActivityReviewed.Add(key);m_ActivityReviewOrder.Enqueue(key);
            }
            if(!ConfirmActivityDeliveryLanguage(frame,decision["delivery"] as JObject,CompleteCheckedFrame,
                failure=>ActivityMonitorFailed(key,failure,output,frame)))CompleteCheckedFrame();
        }); } catch(Exception error) {
            CancelActivityMonitoring();
            ActivityMonitorFailed(key,"provider-exception-"+error.GetType().Name,frame:frame);
            Debug.LogWarning("[Activity/Monitor] provider-exception="+error.GetType().Name+" executionContinues=True",this);
        }
    }

    private void ApplyActivityDecision(JObject frame,JObject decision,JObject liveFacts,JArray liveActions,
        Dictionary<string,MonitoredActivity> adapters)
    {
        var speech=(JObject)decision["speech"];var action=(JObject)decision["action"];
        string actionRejection=ActivityMonitorProtocol.ActionEvidenceRejection(action,frame);
        if(actionRejection!=null) {
            Debug.LogWarning("[Activity/ActionRejected] "+actionRejection+" verdict="+action.ToString(Formatting.None),this);
            return;
        }
        string speechResult="continue",actionResult="continue";
        if((string)speech["issue"]!="none" && m_ActivitySpeechCorrections<2 &&
            ActivityMonitorProtocol.EvidenceCurrent(speech,(JObject)frame["facts"],liveFacts)) {
            var unit=m_ActivitySpeech.FirstOrDefault(u=>u.Id==(string)speech["id"]);
            speechResult=ActivityMonitorProtocol.Revise(unit,speech,out string correction);
            if(speechResult!="stale") {
                m_ActivitySpeechCorrections++;
                if(speechResult=="replace-unplayed") {
                    unit.LanguageCheckPending=false;
                    BeginSpeechLanguageCheck(unit);
                }
                if(correction!=null) {
                    string proof=(string)speech.SelectToken("proof.evidenceId");
                    QueueActivityCorrection(correction,unit.Language,proof!=null&&proof.StartsWith("result:")?proof.Substring(7):null);
                }
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
                BeginActivityHandoff(frame,action,current);
            } else actionResult="stale-owner";
        }
        if(speechResult=="continue" && actionResult=="continue")return;
        m_LastActivityFeedback=new JObject{["generation"]=m_FormalResponseGeneration,["workEpoch"]=m_WorkEpoch,
            ["ownerBefore"]=frame["owner"]?.DeepClone(),["handoff"]=m_ActivityHandoff?.DeepClone(),
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

    private void QueueActivityCorrection(string text,string language,string correctedResultId=null)
    {
        bool senderFinished=m_TTSSenderDone;
        bool drained=m_TTSSenderDone && m_PendingChunks.Count==0 && m_PendingClips.Count==0 && !IsAISpeaking;
        // A suppressed candidate owns a permanently retired round. Supplement
        // audio gets a fresh round, never resurrects that candidate's queue.
        if(drained && m_SilencedSpeechRoundIds.Contains(m_CurrentSpeechRoundId)) {
            m_SpeechRoundSequence=Math.Max(m_SpeechRoundSequence,m_CurrentSpeechRoundId);
            m_CurrentSpeechRoundId=++m_SpeechRoundSequence;
        }
        var correctionChunks=new List<PendingSpeechChunk>();
        string correctionGroup=Guid.NewGuid().ToString("N");
        string remaining=text;
        while(remaining.Length>0) {
            int boundary=SpeechChunkPolicy.Boundary(remaining,false,32);
            int length=boundary<0?remaining.Length:boundary+1;
            string phrase=remaining.Substring(0,length);remaining=remaining.Substring(length);
            if(string.IsNullOrWhiteSpace(phrase))continue;
            var unit=RegisterActivitySpeech(phrase,language,true);
            if(unit!=null) {unit.CorrectedResultId=correctedResultId;unit.CorrectionGroup=correctionGroup;}
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
        if(TryBindProcessSpeechStep(buffer,executable,generation,recordHistory,observations))return true;
        bool unboundProgress=m_ProcessSpeechBindingGeneration==generation && m_ProcessSpeechBindingSource==candidate &&
            m_ProcessSpeechBindingPrefix==null && m_ProcessSpeechBindingAction==(string)BuildRoomSpeechProgressOwner(generation)?["actionId"];
        if(m_ProcessSpeechBindingGeneration==generation && m_ProcessSpeechBindingSource==candidate && m_ProcessSpeechBindingPrefix!=null &&
            m_ProcessSpeechBindingAction==(string)BuildRoomSpeechProgressOwner(generation)?["actionId"]) {
            candidate=m_ProcessSpeechBindingPrefix;
            buffer=new SpeechTextBuffer();buffer.Append(new SpeechText(candidate,m_ProcessSpeechBindingLanguage,
                "step-independent-fragments",SpeechActionDependency.Independent));
        }
        bool exactFailure=CurrentRequestRequiresActionOrControl(generation) &&
            (bool?)ReadRoomTaskDecisionFailure()?["newActionSubmittedByThisDecision"]==false &&
            m_ActivityPlanningFailureReportedGeneration!=generation;
        if(exactFailure) {
            // This is an engine-owned failed attempt, not a semantic approval of
            // arbitrary prose. Independent answer obligations remain pending.
            string language=SpatialGoalReportLanguage(buffer.LanguageCode);
            candidate=LocalizeRoomFallback(RoomTaskFallback(GetComponent<ArdyRoomDialogueBridge>()),language);
            buffer=new SpeechTextBuffer();buffer.Append(new SpeechText(candidate,language,"planning-failure-receipt",SpeechActionDependency.Independent));
            m_ActivityPlanningFailureReportedGeneration=generation;
            if(ReadWorkAnswerObligations(true).Count>0) {
                m_WorkSubstepReviewPending=true;ScheduleNextTick(1f,"planning-failure-answer-pending");
            }
            Debug.Log("[Activity/PlanningFailureReceipt] generation="+generation+" pendingAnswers="+ReadWorkAnswerObligations(true).Count,this);
        }
        // The continuous publication path owns the same per-step playback
        // boundary as a reviewed result. Do not leave its successor frozen at
        // writing time merely because no serial semantic review was required.
        if(unboundProgress) {
            // A failed partition must not freeze an unclassified movement
            // promise as an immutable independent result. Preserve these words
            // under the ordinary ongoing monitor, with delivery still pending.
            m_RequestProgressSpeechPrefix=null;m_RequestProgressSpeechState=null;m_RequestProgressSpeechGeneration=-1;
            m_WorkSubstepReviewPending=true;ScheduleNextTick(1f,"speech-step-binding-unchecked");
        }
        string withProgress=unboundProgress?candidate:AppendRoomRequestProgress(candidate,SpatialGoalReportLanguage(buffer.LanguageCode),generation);
        if(withProgress!=candidate) {
            buffer.Append(new SpeechText(withProgress.Substring(candidate.Length),buffer.LanguageCode,
                "owned-step-progress",SpeechActionDependency.Independent));
            candidate=withProgress;
        }
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
        if(unboundProgress)m_RoomSpeechPlayback=null;
        BindReviewedGoalAnswerSpeech(generation,candidate);
        foreach(var part in buffer.Snapshot())OnSpeechStreamDelta(part);
        if(!exactFailure)AcknowledgeObservations(observations,generation,candidate);
        m_FormalResponseInFlight=false;OnStreamComplete(candidate);
        m_ActivityPublishedGeneration=generation;
        Debug.Log("[Activity/Publication] generation="+generation+" mode=ongoing-monitor serialSemanticReview=False",this);
        return true;
    }
}
