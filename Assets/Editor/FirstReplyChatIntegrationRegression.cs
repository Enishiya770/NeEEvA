using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NeEEvA.Motion;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Exercises the production ChatSample entry and its captured callbacks. All LLM
// methods are intercepted; no HTTP, real TTS, microphone, or action is requested.
public static class FirstReplyChatIntegrationRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    public static JObject LastReport { get; private set; }
    private sealed class Saved { public FieldInfo Field; public object Value; }
    private sealed class Fixture : IDisposable
    {
        public readonly ChatSample Chat;
        public readonly ArdyRoomDialogueBridge Bridge;
        public readonly FirstReplyChatFixtureModel Model;
        public readonly FirstReplyChatFixtureModel Other;
        private readonly GameObject host;
        private readonly ChatSetting settings;
        private readonly LLM originalModel;
        private readonly TTS originalTts;
        private readonly FirstReplyFillerCounterTTS fillerCounter;
        private readonly AudioSource fillerOutput;
        private readonly bool enabled;
        private readonly List<Saved> saved = new List<Saved>();
        private readonly string originalSubtitle;
        private readonly UnityEngine.UI.Text subtitle;
        public int Checks, Generation, Epoch, Round = 996000;
        public string Prompt;
        public Fixture(ChatSample chat,ArdyRoomDialogueBridge bridge)
        {
            Chat=chat;Bridge=bridge;enabled=chat.enabled;
            foreach(var field in typeof(ChatSample).GetFields(Flags))
            {
                var value=field.GetValue(chat);saved.Add(new Saved{Field=field,Value=value});
                // Detach mutable containers before production completion writes to
                // them. The saved originals remain untouched for the live suite.
                if(value is StringBuilder sb)field.SetValue(chat,new StringBuilder(sb.ToString()));
                else if(value is Delegate)field.SetValue(chat,null);
                else if(value!=null&&value.GetType().IsGenericType&&value.GetType().Namespace=="System.Collections.Generic")
                {
                    Type type=value.GetType();var copy=type.GetConstructors().FirstOrDefault(x=>x.GetParameters().Length==1&&x.GetParameters()[0].ParameterType.IsInstanceOfType(value));
                    if(copy!=null)field.SetValue(chat,copy.Invoke(new[]{value}));
                }
            }
            subtitle=(UnityEngine.UI.Text)Get(chat,"m_TextBack");originalSubtitle=subtitle!=null?subtitle.text:null;
            settings=(ChatSetting)Get(chat,"m_ChatSettings");originalModel=settings.m_ChatModel;originalTts=settings.m_TextToSpeech;
            host=new GameObject("First reply zero-network providers");
            Model=host.AddComponent<FirstReplyChatFixtureModel>();Other=host.AddComponent<FirstReplyChatFixtureModel>();
            fillerCounter=host.AddComponent<FirstReplyFillerCounterTTS>();
            fillerOutput=host.AddComponent<AudioSource>();fillerOutput.playOnAwake=false;fillerOutput.enabled=false;
            settings.m_ChatModel=Model;
            Set(chat,"m_AgentRunning",true);Set(chat,"m_SceneTimePaused",true);
            chat.enabled=true;
            Generation=(int)Get(chat,"m_FormalResponseGeneration")+200;
            Epoch=(int)Get(chat,"m_WorkEpoch")+200;
        }
        public void Check(bool ok,string message){Checks++;if(!ok)throw new InvalidOperationException(message);}
        public void Arm(string prompt)
        {
            Prompt=prompt;Generation++;Epoch++;Round++;settings.m_ChatModel=Model;
            Model.Reset();Other.Reset();
            Set(Chat,"m_FormalResponseGeneration",Generation);Set(Chat,"m_WorkEpoch",Epoch);
            Set(Chat,"m_RealtimeFormalTurnGeneration",Generation);Set(Chat,"m_BypassFirstReplyRoutingInRealtime",true);
            Set(Chat,"m_LastUserMsg",prompt);Set(Chat,"m_ActiveSingingGoal",null);Set(Chat,"m_SingingGoal",null);
            Set(Chat,"m_CurrentSpeechRoundId",Round);Set(Chat,"m_FirstReplyAttemptGeneration",-1);
            Set(Chat,"m_RoomTaskGeneration",-1);Set(Chat,"m_RoomSpeechGuardGeneration",-1);
            foreach(string name in new[]{"m_EnableFirstReplyRouting","m_AgentRunning","m_SceneTimePaused","m_UseStreaming","m_IsVoiceMode","m_MotionOutputEnabled","m_RoomMotionOutputEnabled"})Set(Chat,name,true);
            foreach(string name in new[]{"m_AgentCurrentRoundIsTick","m_AgentRoundInFlight","m_UserSpeechActiveForAutonomy","m_HoldSpeechForSongMemoryResult","m_HoldSpeechForHumBackResult","m_FormalResponseInFlight","m_StreamComplete","m_TTSSenderDone","m_RoundContinue","m_RoundSilent","m_RoundIsInner","m_RoundInnerCheckDone","m_RoundSilencedForRepeat","m_RoundRepeatCheckDone","m_RoundRepeatHold","m_FirstChunkFlushed","m_FirstDeltaLogged","m_WorkReviewOpen","m_HumBackPending","m_SongSingInFlight","m_SongSearchResultPending","m_SongCatalogResultPending","m_SongMemoryResultPending","m_HumBackResultPending","<IsAISpeaking>k__BackingField"})Set(Chat,name,false);
            foreach(string name in new[]{"m_PendingSelfInspection","m_PendingToolCorrectionFrame","m_PendingSkillStatusFrame","m_SpokenBridgeTextThisTurn","m_CurrentlyPlayingText"})Set(Chat,name,"");
            Set(Chat,"m_SentenceBuffer",new SpeechTextBuffer());Set(Chat,"m_AssistantHeardText",new StringBuilder());
            Set(Chat,"m_ChatHistory",new List<string>{prompt});Set(Chat,"m_SilencedSpeechRoundIds",new HashSet<int>());
            foreach(string name in new[]{"m_PendingChunks","m_PendingClips","m_WorkResponses"})Set(Chat,name,Activator.CreateInstance(Get(Chat,name).GetType()));
            var ledger=new ArdyActionConstraintLedger();ledger.RecordUserTurn(prompt);Set(Chat,"m_ActionConstraints",ledger);
            Call(Chat,"ClearStagedFormalObservation");
        }
        public FirstReplyChatFixtureModel.Pending Dispatch()
        {
            Call(Chat,"DispatchFormalStream",Prompt,"ignored-old-image",Generation,true,"fixture-transient-context",false);
            Check(Model.First.Count==1,"Actual DispatchFormalStream must issue exactly one first-reply request.");
            return Model.First[0];
        }
        public int ChunkCount=> (int)Get(Chat,"m_PendingChunks").GetType().GetProperty("Count").GetValue(Get(Chat,"m_PendingChunks"));
        public string QueuedText()
        {
            var chunks=(IEnumerable)Get(Chat,"m_PendingChunks");var b=new StringBuilder();
            foreach(var x in chunks)b.Append(Get(x,"Text"));return b.ToString()+Get(Chat,"m_SentenceBuffer");
        }
        public string[] QueuedTexts()=>((IEnumerable)Get(Chat,"m_PendingChunks")).Cast<object>().Select(x=>(string)Get(x,"Text")).ToArray();
        public void NoFallback(string why)=>Check(Model.Room.Count==0&&Model.FormalCalls==0,why);
        public void OneFallback(string why)
        {
            Check(Model.Room.Count==1&&Model.FormalCalls==0&&Model.First.Count==1,why);
            Check((string)JObject.Parse(Model.Room[0])["currentUserText"]==Prompt,"Fallback preserves the complete original user turn, including the final negation.");
        }
        public void ReplaceModel()=>settings.m_ChatModel=Other;
        public void PrepareElapsedFiller()
        {
            settings.m_TextToSpeech=fillerCounter;fillerCounter.Calls=0;
            Set(Chat,"m_AudioSource",fillerOutput);Set(Chat,"m_StalledTurnAction","");
            Set(Chat,"m_StreamStartTime",Time.realtimeSinceStartup-100f);
            Set(Chat,"m_UserFirstAudioTargetSec",.35f);Set(Chat,"m_LatencyFillerThresholdSec",2.8f);
            Set(Chat,"m_LatencyFillerGeneration",Generation);Set(Chat,"m_RealFirstAudioStarted",false);
            Set(Chat,"m_LatencyFillerPlayed",false);
        }
        public void RunElapsedFiller()
        {
            var routine=(IEnumerator)Call(Chat,"MaybePlayLatencyFiller",Generation,"en","thinking");
            try{Check(!routine.MoveNext(),"Fast-forwarded actual filler entry terminates without a timed yield or playback.");}
            finally{(routine as IDisposable)?.Dispose();}
        }
        public int FillerAttempts=>fillerCounter.Calls;
        public bool FillerOutputUntouched=>fillerOutput.clip==null&&!fillerOutput.isPlaying;
        public void Dispose()
        {
            // OnDisable may clear transient work. Restore saved state after that,
            // with the fake still bound so cancellation cannot touch a real model.
            Chat.enabled=enabled;
            foreach(var value in saved)value.Field.SetValue(Chat,value.Value);
            settings.m_ChatModel=originalModel;
            settings.m_TextToSpeech=originalTts;
            if(subtitle!=null&&originalSubtitle!=null)subtitle.text=originalSubtitle;
            UnityEngine.Object.DestroyImmediate(host);
        }
    }
    public static IEnumerator RunChecks(ChatSample chat,ArdyRoomDialogueBridge bridge,string output)
    {
        if(!Application.isBatchMode||!Application.isPlaying||Application.dataPath.IndexOf("unity-naturalness-validation",StringComparison.OrdinalIgnoreCase)<0||
            chat==null||!chat.gameObject.activeInHierarchy||bridge==null||!bridge.IsConnected)
            throw new InvalidOperationException("Use the connected isolated Play fixture only.");
        string directory=Path.Combine(output,"first-reply-chat");Directory.CreateDirectory(directory);
        LastReport=new JObject{["status"]="running",["cases"]=new JArray(),["scope"]="Production ChatSample.DispatchFormalStream, typed speech callbacks, completion/fallback and observation receipts; deterministic fake LLM; zero HTTP and no TTS playback."};
        using(var f=new Fixture(chat,bridge))
        try
        {
            int operation=bridge.Controller.OperationRevision;
            f.Arm("今天我们只聊聊普通的事情，不要移动。");var p=f.Dispatch();
            f.Check(p.Prompt==f.Prompt&&p.Image==null&&p.CommitHistory,"The first-reply request preserves original text/history intent and receives no current image.");
            f.Check(p.Context.Contains("fixture-transient-context")&&p.Context.Contains("currentImageDeliveredInThisRequest"),"The actual request carries compact runtime context and image absence.");
            p.Speech(new SpeechText("This is a normal conversation.","en"));p.Done(Ordinary("This is a normal conversation.",true));
            f.NoFallback("Ordinary completion bypasses room planning and the general formal request.");
            f.Check(f.Model.RoleCalls==0,"Realtime ordinary speech must not first wait for an auxiliary RoleDecision call.");
            f.Check((bool)Get(chat,"m_StreamComplete")&&!(bool)Get(chat,"m_FormalResponseInFlight")&&f.QueuedText()=="This is a normal conversation.","Ordinary public speech enters the real queue exactly once and completes.");
            Add("ordinary actual-entry delivery without planner");

            f.Arm("请用英语说两小行。");p=f.Dispatch();
            // Keep the unrelated maximum-length policy from splitting this
            // short test line before its explicit newline boundary arrives.
            Set(chat,"m_FirstChunkMaxChars",64);
            const string characterStream="We can talk\nabout this now.";
            foreach(char character in characterStream)
                p.Speech(new SpeechText(character.ToString(),"en",actionDependency:SpeechActionDependency.Independent));
            var expectedLines=new[]{"We can talk","about this now."};
            f.Check(f.QueuedTexts().SequenceEqual(expectedLines),"Character-at-a-time typed deltas preserve English word spaces and let newline split the two real speech chunks.");
            p.Done(Ordinary(characterStream,true));p.Done(Ordinary(characterStream,true));
            p.Speech(new SpeechText("STALE_AFTER_COMPLETE", "en",actionDependency:SpeechActionDependency.Independent));
            f.Check(f.QueuedTexts().SequenceEqual(expectedLines)&&((SpeechTextBuffer)Get(chat,"m_SentenceBuffer")).Length==0,"Completion and duplicate/late callbacks do not repeat either queued line or drop its spaces.");
            f.NoFallback("Character-at-a-time ordinary speech still bypasses the planner.");
            Add("single-character typed speech preserves spaces and newline boundaries without duplicate queueing");

            f.Arm("还没有正文的空白流。");p=f.Dispatch();
            p.Speech(new SpeechText(" ","en",actionDependency:SpeechActionDependency.Independent));
            p.Speech(new SpeechText("\n","en",actionDependency:SpeechActionDependency.Independent));
            p.Done(FirstReplyCompletion.Failed("only-whitespace-before-failure"));
            f.OneFallback("Whitespace-only deltas are preserved as formatting but cannot claim public speech and suppress the original-turn fallback.");
            Add("whitespace-only stream failure remains eligible for one fallback");

            f.Arm("跟着我走。最后更正：不要走动，只检查目前状态。");p=f.Dispatch();
            p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Tools});p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Tools});
            f.OneFallback("Tools selection falls back once, including duplicate provider completion.");
            Add("tools route preserves original input and single fallback");

            f.Arm("请你现在走到我身边来。");p=f.Dispatch();
            f.Check(f.Model.RoleCalls==0&&f.Model.Room.Count==0,"Realtime starts only the streaming role request before any action proposal.");
            p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Tools});
            f.OneFallback("Realtime movement handoff must reach the structured room planner rather than re-entering the old general-only bypass.");
            f.Check(f.QueuedText()==""&&f.Model.RoleCalls==0,"A room handoff must neither speak a movement promise nor add a second role request.");
            Add("realtime movement reaches one structured planner with no premature public promise");

            f.Arm("你自己在房间随便逛逛，目的地由你决定。");p=f.Dispatch();
            p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Tools});
            p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Tools});
            f.OneFallback("Realtime delegated exploration must reach exactly one room planner with its complete original purpose.");
            f.Check(f.QueuedText()==""&&f.Model.RoleCalls==0,"Exploration handoff cannot become public speech or a duplicate role request.");
            Add("realtime delegated exploration preserves the full purpose and silent planner handoff");

            f.Arm("请照着刚才的录音再唱一遍。");p=f.Dispatch();
            p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.RecordingSinging});
            p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.RecordingSinging});
            f.Check(f.Model.First.Count==1&&f.Model.RoleCalls==0&&f.Model.Room.Count==0&&f.Model.FormalCalls==1&&
                f.Model.LastFormalStage==FormalResponseStage.RecordingSingingDecision&&f.Model.RecordingDecisionContext!=null&&
                f.Model.RecordingDecisionContext.UserText==f.Prompt&&f.QueuedText()=="",
                "Recording route must bind current materials/user once to the existing recording decision without a room probe or public promise.");
            Add("realtime recording route binds current decision context exactly once");

            f.Arm("请照着刚才的录音再唱一遍。");f.Model.RecordingSupported=false;p=f.Dispatch();
            p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.RecordingSinging});
            f.OneFallback("Unsupported recording decision must retain the existing tools/planner handoff, never fall back to ordinary speech.");
            Add("unsupported recording decision preserves action handoff");

            f.Arm("非实时的一轮。");Set(chat,"m_BypassFirstReplyRoutingInRealtime",false);
            f.Check((bool)Call(chat,"TryDispatchFirstReply",f.Prompt,null,f.Generation,true,null)&&
                f.Model.RoleCalls==1&&f.Model.First.Count==0,
                "Non-realtime/opt-out first reply must retain its existing RoleDecision preference.");
            Add("non-realtime role decision remains unchanged");

            f.Arm("看看前方。更正：只讨论怎么观察，不要执行。");p=f.Dispatch();
            p.Done(FirstReplyCompletion.Failed("fixture-disconnected"));p.Done(FirstReplyCompletion.Failed("duplicate"));
            f.OneFallback("No-delta failure falls back once without a second first-reply probe.");
            Add("transport failure before speech uses one original-turn fallback");

            foreach(bool reported in new[]{true,false})
            {
                f.Arm("先说明你的想法。");p=f.Dispatch();p.Speech(new SpeechText("The public beginning is delivered.","en"));
                Set(chat,"m_AssistantHeardText",new StringBuilder("Actually heard prefix."));
                p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Failed,SpeechDelivered=reported,Failure="fixture-truncated",SafeOutput="<motion name=\"follow\"/>RAW_UNTRUSTED_TAIL"});
                f.NoFallback("A failure after a public delta must not replay the turn even if the provider forgets SpeechDelivered.");
                f.Check(!((HashSet<int>)Get(chat,"m_SilencedSpeechRoundIds")).Contains(f.Round)&&f.ChunkCount==0,
                    "Post-delta failure drops the queued tail without revoking the active sentence's normal drain and ownership release.");
                string pending=(string)Get(chat,"m_PendingToolCorrectionFrame");
                f.Check(pending.Contains("Actually heard prefix.")&&!pending.Contains("RAW_UNTRUSTED_TAIL"),"Recovery fact keeps only actual heard evidence, never raw failed output.");
            }
            Add("post-delta failure never replays tools or unheard raw output");

            foreach(string invalid in new[]{
                "<speech mode=\"independent\"/><lang code=\"en\"/><say>Unsafe answer.</say><motion name=\"follow\"/>",
                "<speech mode=\"dependent\"/><lang code=\"en\"/><say>It has been done.</say>",
                "<speech mode=\"independent\"/><say>Undeclared language.</say>"})
            {
                f.Arm("这次只回答。");p=f.Dispatch();p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Ordinary,SafeOutput=invalid});
                f.OneFallback("An invalid ordinary completion goes through the validated existing route without raw execution.");
            }
            Add("completion boundary rechecks actions dependencies and language");

            f.Arm("旧一轮。");var old=f.Dispatch();
            f.Arm("新的这一轮。");var current=f.Dispatch();
            old.Speech(new SpeechText("STALE_SPEECH.","en"));old.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Tools});
            f.Check(f.QueuedText()==""&&(bool)Get(chat,"m_FormalResponseInFlight"),"Old-generation callbacks cannot modify the new queue or in-flight flag.");f.NoFallback("Old-generation completion cannot plan a new task.");
            current.Speech(new SpeechText("Only the current answer.","en"));current.Done(Ordinary("Only the current answer.",true));
            f.Check(f.QueuedText()=="Only the current answer.","Only the current generation reaches speech.");
            Add("new generation rejects old speech and completion callbacks");

            f.Arm("这一轮随后取消。");p=f.Dispatch();
            Call(chat,"InvalidateFormalResponse","first-reply-fixture-cancel");
            p.Speech(new SpeechText("CANCELLED_REPLY.","en"));p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Tools});
            f.Check(f.Model.CancelCalls>0&&f.QueuedText()==""&&!(bool)Get(chat,"m_FormalResponseInFlight"),"Actual response cancellation revokes the provider and ignores late callbacks.");
            f.NoFallback("A cancelled response cannot restart planning.");
            Add("actual response cancellation rejects late provider callbacks");

            foreach(string revoked in new[]{"work-epoch","user-speaking","model-replaced"})
            {
                f.Arm("还在等这一轮。");p=f.Dispatch();
                if(revoked=="work-epoch")Set(chat,"m_WorkEpoch",f.Epoch+1);
                else if(revoked=="user-speaking")Set(chat,"m_UserSpeechActiveForAutonomy",true);
                else f.ReplaceModel();
                p.Speech(new SpeechText("REVOKED_SPEECH.","en"));p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Tools});
                f.Check(f.QueuedText()==""&&(bool)Get(chat,"m_FormalResponseInFlight"),"Revoked callback is inert: "+revoked);f.NoFallback("Revoked callback cannot fall back: "+revoked);
            }
            Add("work cancellation user speech and model replacement fence callbacks");

            f.Arm("简单聊一句。");
            Set(chat,"m_PendingSelfInspection","UNREAD_INSPECTION_DETAILS");Set(chat,"m_PendingToolCorrectionFrame","UNREAD_EXECUTION_DETAILS");Set(chat,"m_PendingSkillStatusFrame","UNREAD_SKILL_DETAILS");
            Call(chat,"StageFormalObservationFrame","FULL_STAGED_OBSERVATION",f.Generation);p=f.Dispatch();
            f.Check(p.Context.Contains("\"hasUnreadExecutionDetails\":true")&&!p.Context.Contains("UNREAD_")&&!p.Context.Contains("FULL_STAGED_OBSERVATION"),"First-reply context reports unread facts without pretending it consumed their full content.");
            p.Speech(new SpeechText("A brief ordinary answer.","en"));p.Done(Ordinary("A brief ordinary answer.",true));
            f.Check((string)Get(chat,"m_PendingSelfInspection")=="UNREAD_INSPECTION_DETAILS"&&(string)Get(chat,"m_PendingToolCorrectionFrame")=="UNREAD_EXECUTION_DETAILS"&&(string)Get(chat,"m_PendingSkillStatusFrame")=="UNREAD_SKILL_DETAILS","Ordinary completion cannot acknowledge full observations not sent through this request.");
            f.Check(Get(chat,"m_StagedFormalObservation")==null&&Get(chat,"m_ComposedObservationReceipt")==null,"Narrow completion discards its staged receipt without acknowledging pending content.");
            Add("ordinary completion preserves unread full observations");

            foreach(string guard in new[]{"m_AgentCurrentRoundIsTick","m_HoldSpeechForSongMemoryResult","m_HoldSpeechForHumBackResult","m_UserSpeechActiveForAutonomy"})
            {
                f.Arm("受保护的轮次。");Set(chat,guard,true);
                f.Check(!(bool)Call(chat,"TryDispatchFirstReply",f.Prompt,null,f.Generation,true,null)&&f.Model.First.Count==0,"Protected round does not enter ordinary routing: "+guard);
            }
            f.Arm("先普通地回答，别用缓存应声。");f.PrepareElapsedFiller();p=f.Dispatch();
            f.RunElapsedFiller();
            f.Check(f.FillerAttempts==0&&f.FillerOutputUntouched,"Active first-reply generation with no room-planner owner skips cached filler entirely.");
            // Change ownership through the real tools fallback, rather than
            // merely setting room generation to make the guard pass.
            p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Tools});
            f.OneFallback("Tools fallback acquires the current room-planner generation.");
            f.RunElapsedFiller();
            f.Check(f.FillerAttempts==1&&f.FillerOutputUntouched,"The same generation's Tools fallback retains one cached-filler attempt without playing audio.");
            f.Arm("原来的慢速路径。");f.PrepareElapsedFiller();
            Set(chat,"m_FirstReplyAttemptGeneration",f.Generation-1);f.RunElapsedFiller();
            f.Check(f.FillerAttempts==1&&f.FillerOutputUntouched,"A previous generation's first-reply attempt does not suppress the existing slow-path filler.");
            Add("actual cached-filler entry skips direct lane and preserves tools fallback");
            f.Check(bridge.Controller.OperationRevision==operation,"No test emitted raw motion or changed the room controller operation.");
            f.Check(f.Model.UnexpectedCalls+f.Other.UnexpectedCalls==0,"No unstubbed continuation or other model transport was invoked.");
            Add("protected rounds remain on existing route and no action executes");
            LastReport["status"]="passed";
        }
        catch(Exception ex){LastReport["status"]="failed";LastReport["error"]=ex.ToString();throw;}
        finally{LastReport["checks"]=f.Checks;File.WriteAllText(Path.Combine(directory,"report.json"),LastReport.ToString()+"\n");}
        yield break;
    }
    private static FirstReplyCompletion Ordinary(string text,bool delivered)=>new FirstReplyCompletion{Disposition=FirstReplyDisposition.Ordinary,SpeechDelivered=delivered,SafeOutput="<speech mode=\"independent\"/><lang code=\"en\"/><say>"+text+"</say>"};
    private static void Add(string name)=>((JArray)LastReport["cases"]).Add(name);
    private static FieldInfo Field(Type type,string name){for(;type!=null;type=type.BaseType){var f=type.GetField(name,Flags);if(f!=null)return f;}throw new MissingFieldException(name);}
    private static object Get(object o,string n)=>Field(o.GetType(),n).GetValue(o);
    private static void Set(object o,string n,object v)=>Field(o.GetType(),n).SetValue(o,v);
    private static object Call(object o,string n,params object[] args)
    {
        var method=o.GetType().GetMethods(Flags).Single(x=>x.Name==n&&x.GetParameters().Length>=args.Length&&
            x.GetParameters().Skip(args.Length).All(p=>p.IsOptional));
        var parameters=method.GetParameters();var complete=new object[parameters.Length];
        Array.Copy(args,complete,args.Length);
        for(int i=args.Length;i<complete.Length;i++)complete[i]=parameters[i].DefaultValue;
        return method.Invoke(o,complete);
    }
}

public sealed class FirstReplyChatFixtureModel:LLM
{
    public sealed class PendingRole { public string Prompt,Image; public Action<bool,string,string> Done; }
    public sealed class Pending
    {
        public string Prompt,Image,Context;public bool CommitHistory;
        public Action<SpeechText> Speech;public Action<FirstReplyCompletion> Done;
    }
    public readonly List<Pending> First=new List<Pending>();public readonly List<string> Room=new List<string>();
    public readonly List<PendingRole> Roles=new List<PendingRole>();
    public readonly List<Action<bool,string,string>> RoomCallbacks=new List<Action<bool,string,string>>();
    public int FormalCalls,UnexpectedCalls,CancelCalls,RoleCalls;
    public bool RecordingSupported=true;
    public FormalResponseStage LastFormalStage;
    public override bool SupportsFirstReplyRouting=>true;
    public override bool SupportsRoleDecisionMessages=>true;
    public override bool SupportsVisualDecisionMessages=>true;
    public override bool SupportsRecordingSingingDecision=>RecordingSupported;
    public override bool SupportsRoomTaskMessages=>true;
    public void Reset(){First.Clear();Room.Clear();Roles.Clear();RoomCallbacks.Clear();FormalCalls=RoleCalls=0;RecordingSupported=true;RecordingDecisionContext=null;AcceptedRoleDecision=null;}
    public override void PostRoleDecisionMsg(string prompt,string imageDataUrl,Action<bool,string,string> callback,ConversationTrace trace)
    {RoleCalls++;Roles.Add(new PendingRole{Prompt=prompt,Image=imageDataUrl,Done=callback});}
    public void AnswerRole(PendingRole pending,JObject value)
    {
        string raw=value.ToString();
        string envelope=new JObject{["choices"]=new JArray(new JObject{["finish_reason"]="stop",["message"]=new JObject{["content"]=raw}})}.ToString();
        object[] args={envelope,null,null,null,null,null};
        bool accepted=(bool)typeof(ChatQW).GetMethod("TryReadRoleDecisionResponse",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);
        AcceptedRoleDecision=accepted&&DecisionCompiler.TryParse(raw,out var decision,out _)?decision:null;
        pending.Done(accepted,(string)args[3],(string)args[4]);
    }
    public override void PostFirstReplyStream(string message,Action<SpeechText> onSpeech,Action<FirstReplyCompletion> onComplete,string imageDataUrl=null,string transientContext=null,bool commitHistory=true)
        =>First.Add(new Pending{Prompt=message,Image=imageDataUrl,Context=transientContext,CommitHistory=commitHistory,Speech=onSpeech,Done=onComplete});
    public override void PostRoomTaskMessage(string input,string imageDataUrl,bool reviewSpeech,Action<bool,string,string> callback){Room.Add(input);RoomCallbacks.Add(callback);}
    public override void PostRoomTaskMessage(string input,bool reviewSpeech,Action<bool,string,string> callback){Room.Add(input);RoomCallbacks.Add(callback);}
    public override void PostSpeechStreamForStage(string message,Action<SpeechText> onSpeech,Action<string> onComplete,string imageDataUrl,bool recordAssistantHistory,FormalResponseStage stage){FormalCalls++;LastFormalStage=stage;}
    public override void PostMsg(string message,Action<string> callback)=>UnexpectedCalls++;
    public override void PostMsgStream(string message,Action<string> onDelta,Action<string> onComplete,string imageDataUrl=null,bool recordAssistantHistory=true)=>UnexpectedCalls++;
    public override void PostSpeechContinuationStream(string context,Action<SpeechText> onSpeech,Action<string> onComplete,string imageDataUrl=null)=>UnexpectedCalls++;
    public override void PostSpeechFeedbackStream(string context,string feedback,Action<SpeechText> onSpeech,Action<string> onComplete,string imageDataUrl=null)=>UnexpectedCalls++;
    public override void PostSpeechFeedbackStream(string context,string feedback,Action<SpeechText> onSpeech,Action<string> onComplete,string imageDataUrl,bool recordAssistantHistory)=>UnexpectedCalls++;
    public override void CancelActiveResponse()=>CancelCalls++;
}

// A counted cache miss: never opens HTTP, creates a clip or touches the source.
public sealed class FirstReplyFillerCounterTTS:TTS
{
    public int Calls;
    public override bool TryPlayLatencyFiller(string languageHint,string contextHint,AudioSource output,
        out string spokenText,out float duration)
    {Calls++;spokenText=string.Empty;duration=0f;return false;}
}
