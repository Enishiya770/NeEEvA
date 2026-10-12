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
            Set(chat,"m_AgentRunning",true);Set(chat,"m_SceneTimePaused",false);Set(chat,"m_AgentRoundInFlight",true);
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
            Set(Chat,"m_CurrentSpeechRoundId",Round);Set(Chat,"m_FirstReplyAttemptGeneration",-1);
            Set(Chat,"m_RoomTaskGeneration",-1);Set(Chat,"m_RoomSpeechGuardGeneration",-1);
            // A genuine accepted turn owns the round while the room is running.
            // Pausing is not a test scheduler: RoomTask correctly revokes paused
            // transactions before even dispatching their model request.
            foreach(string name in new[]{"m_EnableFirstReplyRouting","m_AgentRunning","m_AgentRoundInFlight","m_UseStreaming","m_IsVoiceMode","m_MotionOutputEnabled","m_RoomMotionOutputEnabled"})Set(Chat,name,true);
            foreach(string name in new[]{"m_SceneTimePaused","m_AgentCurrentRoundIsTick","m_UserSpeechActiveForAutonomy","m_HoldSpeechForSongMemoryResult","m_HoldSpeechForHumBackResult","m_FormalResponseInFlight","m_StreamComplete","m_TTSSenderDone","m_RoundContinue","m_RoundSilent","m_RoundIsInner","m_RoundInnerCheckDone","m_RoundSilencedForRepeat","m_RoundRepeatCheckDone","m_RoundRepeatHold","m_FirstChunkFlushed","m_FirstDeltaLogged","m_WorkReviewOpen","m_HumBackPending","m_SongSingInFlight","m_SongSearchResultPending","m_SongCatalogResultPending","m_SongMemoryResultPending","m_HumBackResultPending","<IsAISpeaking>k__BackingField"})Set(Chat,name,false);
            foreach(string name in new[]{"m_PendingSelfInspection","m_PendingToolCorrectionFrame","m_PendingSkillStatusFrame","m_SpokenBridgeTextThisTurn","m_CurrentlyPlayingText"})Set(Chat,name,"");
            Set(Chat,"m_SentenceBuffer",new SpeechTextBuffer());Set(Chat,"m_AssistantHeardText",new StringBuilder());
            Set(Chat,"m_ChatHistory",new List<string>{prompt});Set(Chat,"m_SilencedSpeechRoundIds",new HashSet<int>());
            foreach(string name in new[]{"m_PendingChunks","m_PendingClips","m_WorkResponses"})Set(Chat,name,Activator.CreateInstance(Get(Chat,name).GetType()));
            var ledger=new ArdyActionConstraintLedger();ledger.RecordUserTurn(prompt);Set(Chat,"m_ActionConstraints",ledger);
            Set(Chat,"m_LastUserMsg",prompt);Set(Chat,"m_LastUserEvidence",prompt);
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
        yield return FirstReplySemanticGateRegression.RunChecks(output);
        LastReport=new JObject{["status"]="running",["cases"]=new JArray(),["scope"]="Production ChatSample.DispatchFormalStream, typed speech callbacks, completion/fallback and observation receipts; deterministic fake LLM; zero HTTP and no TTS playback."};
        LastReport["semanticGateTransport"] = FirstReplySemanticGateRegression.LastReport;
        using(var f=new Fixture(chat,bridge))
        {
            var checks=RunFixtureChecks(f);
            try
            {
                while(true)
                {
                    bool moved; object current;
                    try { moved=checks.MoveNext();current=moved?checks.Current:null; }
                    catch(Exception ex){LastReport["status"]="failed";LastReport["error"]=ex.ToString();throw;}
                    if(!moved)break;
                    yield return current;
                }
            }
            finally
            {
                (checks as IDisposable)?.Dispose();
                LastReport["checks"]=f.Checks;
                File.WriteAllText(Path.Combine(directory,"report.json"),LastReport.ToString()+"\n");
            }
        }
    }
    private static IEnumerator RunFixtureChecks(Fixture f)
    {
            var chat=f.Chat;var bridge=f.Bridge;
            int operation=bridge.Controller.OperationRevision;
            f.Arm("今天我们只聊聊普通的事情，不要移动。");var p=f.Dispatch();
            f.Check(p.Prompt==f.Prompt&&p.Image==null&&p.CommitHistory,"The first-reply request preserves original text/history intent and receives no current image.");
            f.Check(p.Context.Contains("fixture-transient-context")&&p.Context.Contains("currentImageDeliveredInThisRequest"),"The actual request carries compact runtime context and image absence.");
            var firstRuntime=JObject.Parse(p.Context);
            f.Check((int?)firstRuntime["generation"]==f.Generation&&(int?)firstRuntime["workEpoch"]==f.Epoch&&
                (int?)firstRuntime["userTurn"]==1&&(bool?)firstRuntime["humanInput"]?["currentHumanTurnPresent"]==true&&
                (string)firstRuntime["humanInput"]?["primaryUtterance"]==f.Prompt,
                "First-reply interpretation input lost its same-generation typed human turn or conflated user turn and work epoch.");
            p.Speech(new SpeechText("This is a normal conversation.","en"));p.Done(Ordinary("This is a normal conversation.",true));
            f.NoFallback("Ordinary completion bypasses room planning and the general formal request.");
            f.Check((bool)Get(chat,"m_StreamComplete")&&!(bool)Get(chat,"m_FormalResponseInFlight")&&f.QueuedText()=="This is a normal conversation.","Ordinary public speech enters the real queue exactly once and completes.");
            Add("ordinary actual-entry delivery without planner");

            f.Arm("我不想让你走动，只要告诉我现在能看到什么。");
            const string fullAuditory="[声学候选 uncertain；备选并非额外发言]\n完整听觉资料仍须保留。";
            Set(chat,"m_LastUserEvidence",f.Prompt+fullAuditory);
            Set(chat,"m_EnableScreenVision",true);Set(chat,"m_AgentEyesOpen",true);
            p=f.Dispatch();
            object retainedGoal=Get(chat,"m_SpatialDeviceGoal");
            var retainedObservation=(Get(chat,"m_SceneObservationGoal") as JObject)?.DeepClone();
            var understanding=new JObject{["referent"]="当前原位视野",["resolvedQuestion"]="不移动的情况下，当前能看到什么？",["needsEvidence"]="current-visual"};
            p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Evidence,QuestionUnderstanding=understanding,RequestKind="answer-only"});
            understanding["resolvedQuestion"]="MUTATED_AFTER_HANDOFF";
            var readOnlySemantics=(JObject)Call(chat,"ReadCurrentRequestSemantics",f.Generation);
            f.Check((string)readOnlySemantics?["requestKind"]=="answer-only"&&
                (bool?)readOnlySemantics?["requiresActionOrControl"]==false&&(bool?)readOnlySemantics?["actionAuthorization"]==false&&
                (string)readOnlySemantics?["humanInput"]?["fullInputEvidence"]==f.Prompt+fullAuditory,
                "A read-only handoff must retain complete typed input without acquiring action authority.");
            float evidenceDeadline=Time.realtimeSinceStartup+12f;
            while(f.Model.FormalCalls==0&&Time.realtimeSinceStartup<evidenceDeadline)yield return null;
            f.Check(f.Model.FormalCalls==1&&f.Model.Room.Count==0&&f.Model.FormalStage==FormalResponseStage.CommittedSpatialReply&&
                f.Model.FormalPrompt==f.Prompt&&!f.Model.FormalRecordHistory,
                "Reviewed evidence handoff preserves the whole question and starts one guarded writer with zero planner requests or premature history.");
            f.Check(!string.IsNullOrEmpty(f.Model.FormalImage)&&
                ((NeEEvA.Perception.SceneVisionObservation)Call(chat,"GetFormalSceneObservation",f.Generation))?.HasImage==true,
                "The visual fixture must supply an actual current capture before approving a visual claim.");
            f.Check((string)Get(chat,"m_RoomTaskIntent")=="none"&&(string)Get(chat,"m_RoomTaskDispatch")=="read-only-evidence"&&
                Get(chat,"m_RoomTaskDecisionFailure")==null&&f.Model.RequestContext.Contains("只读场景问答"),
                "Evidence question acquired a movement intent or inherited a planning-failure obligation.");
            var boundReply=CommittedSpatialReplyProtocol.ReadReplyFacts(f.Model.RequestContext);
            var interpretation=boundReply?["readOnlyEvidenceReply"]?["questionUnderstanding"];
            f.Check((string)interpretation?["interpretation"]?["resolvedQuestion"]=="不移动的情况下，当前能看到什么？"&&
                (string)interpretation?["source"]=="independent-first-reply-semantic-review"&&(int?)interpretation?["generation"]==f.Generation&&
                (string)boundReply?["humanInput"]?["primaryUtterance"]==f.Prompt&&
                (string)boundReply?["humanInput"]?["fullInputEvidence"]==f.Prompt+fullAuditory,
                "Writer lost exact original primary/evidence or the source-labelled immutable question interpretation.");
            f.Check(ReferenceEquals(retainedGoal,Get(chat,"m_SpatialDeviceGoal"))&&
                JToken.DeepEquals(retainedObservation,Get(chat,"m_SceneObservationGoal") as JObject),
                "Answer-only routing changed or authorized an existing spatial/observation goal.");
            const string evidenceAnswer="I can describe only the current captured scene; no movement was requested.";
            f.Model.FormalSpeech(new SpeechText(evidenceAnswer,"en",actionDependency:SpeechActionDependency.Independent));
            f.Check(f.QueuedText()=="","Evidence candidate became audible before its room speech review.");
            f.Model.FormalComplete("<speech mode=\"independent\"/>"+evidenceAnswer);
            while(f.Model.Room.Count==0&&Time.realtimeSinceStartup<evidenceDeadline)yield return null;
            f.Check(f.Model.Room.Count==1&&f.Model.RoomReview[0]&&f.QueuedText()=="",
                "Evidence reply bypassed the normal semantic speech review or called an action planner.");
            f.Check(JObject.Parse(f.Model.Room[0])["visualObservation"]?.Type==JTokenType.Object,
                "A visual-scoped approval requires actual image evidence in the review request.");
            var speechReview=JObject.Parse(f.Model.Room[0]);
            f.Check(JToken.DeepEquals(interpretation,speechReview["readOnlyEvidenceReply"]?["questionUnderstanding"])&&
                (string)speechReview["humanInput"]?["fullInputEvidence"]==f.Prompt+fullAuditory,
                "Independent speech review lost the question interpretation or replaced complete original input with it.");
            f.Model.RoomCallbacks[0](true,new JObject{["verdict"]="consistent",["reason"]="The sentence states only current evidence scope; no action claim.",
                ["actualLanguage"]="en",
                ["factScope"]="visual-observation",["resultReport"]="not-applicable",["requestDisposition"]="answer-only",
                ["planningFailureReport"]="not-mentioned"}.ToString(),"received");
            f.Check(f.QueuedText()==evidenceAnswer&&(bool)Get(chat,"m_StreamComplete")&&!(bool)Get(chat,"m_FormalResponseInFlight"),
                "Reviewed visual answer was replaced by a planning-failure template or failed to complete.");
            p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Evidence});
            f.Check(f.Model.FormalCalls==1&&f.Model.Room.Count==1&&bridge.Controller.OperationRevision==operation,
                "Duplicate evidence handoff replayed generation/review or performed an action.");
            Add("fresh evidence question uses one read-only reviewed writer with zero planner or goal mutation");

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

            f.Arm("解释刚才的失败。最后要求：现在向左转九十度。");p=f.Dispatch();
            var currentProof=new JObject{["answerQuote"]="解释刚才的失败。",["actionQuote"]="现在向左转九十度。"};
            p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Tools,RequestKind="mixed",CurrentTurnEvidence=currentProof});p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Tools,RequestKind="answer-only"});
            f.OneFallback("Tools selection falls back once, including duplicate provider completion.");
            var currentSemantics=(JObject)Call(chat,"ReadCurrentRequestSemantics",f.Generation);
            f.Check((string)currentSemantics?["requestKind"]=="mixed"&&(bool?)currentSemantics?["actionAuthorization"]==false&&
                (bool)Call(chat,"CurrentRequestRequiresActionOrControl",f.Generation)&&
                (string)currentSemantics?["humanInput"]?["primaryUtterance"]==f.Prompt,
                "One current completion binds mixed request semantics and the full last instruction without treating it as authorization.");
            currentSemantics["requestKind"]="answer-only";
            f.Check((bool)Call(chat,"CurrentRequestRequiresActionOrControl",f.Generation),
                "Consumers must not be able to mutate a current semantic receipt or let duplicate callbacks overwrite it.");
            var compactSemantics=(JObject)Call(chat,"ReadCompactCurrentRequestSemantics",f.Generation);
            f.Check((string)compactSemantics?["requestKind"]=="mixed"&&compactSemantics["humanInput"]==null&&
                compactSemantics["currentUserText"]==null&&compactSemantics.Count==10&&
                compactSemantics.SelectToken("planningFocus.currentUserText")?.Type==JTokenType.Null&&
                JToken.DeepEquals(compactSemantics["currentTurnEvidence"],currentProof),
                "Downstream semantics must carry the current typed requirement without duplicating whole ASR/history payloads.");
            Add("tools route preserves complete input, typed mixed request, clone isolation and single fallback");

            foreach(string revoke in new[]{"generation","work-epoch","user-turn","model","pause","user-speaking"})
            {
                f.Arm("继续尚未完成的请求。");
                Call(chat,"RememberFirstReplyRequestSemantics",new FirstReplyCompletion{Disposition=FirstReplyDisposition.Tools,RequestKind="control"},f.Generation,f.Model);
                f.Check((bool)Call(chat,"CurrentRequestRequiresActionOrControl",f.Generation),"Current controlled receipt binds before revocation.");
                if(revoke=="generation")Set(chat,"m_FormalResponseGeneration",f.Generation+1);
                else if(revoke=="work-epoch")Set(chat,"m_WorkEpoch",f.Epoch+1);
                else if(revoke=="user-turn")((ArdyActionConstraintLedger)Get(chat,"m_ActionConstraints")).RecordUserTurn("现在换了请求。");
                else if(revoke=="model")f.ReplaceModel();
                else if(revoke=="pause")Set(chat,"m_SceneTimePaused",true);
                else Set(chat,"m_UserSpeechActiveForAutonomy",true);
                f.Check(Call(chat,"ReadCurrentRequestSemantics",f.Generation)==null&&
                    !(bool)Call(chat,"CurrentRequestRequiresActionOrControl",f.Generation),
                    "A stale interpretation must not constrain a replacement turn: "+revoke);
                f.NoFallback("Binding and invalidating interpretation receipts must never execute or restart work.");
            }
            Add("current request semantics expire with generation, work, user, model, pause and speaking ownership");

            f.Arm("看看前方。更正：只讨论怎么观察，不要执行。");p=f.Dispatch();
            p.Done(FirstReplyCompletion.Failed("fixture-disconnected"));p.Done(FirstReplyCompletion.Failed("duplicate"));
            f.OneFallback("No-delta failure falls back once without a second first-reply probe.");
            Add("transport failure before speech uses one original-turn fallback");

            foreach(var pausedRoute in new[]{FirstReplyDisposition.Tools,FirstReplyDisposition.Evidence,FirstReplyDisposition.Ordinary})
            {
                f.Arm("随后暂停的首答请求。");p=f.Dispatch();int cancelCalls=f.Model.CancelCalls;
                Call(chat,"OnApplicationPause",true);
                f.Check((int)Get(chat,"m_FormalResponseGeneration")!=f.Generation&&
                    !(bool)Get(chat,"m_FormalResponseInFlight")&&(bool)Get(chat,"m_StreamComplete")&&
                    (bool)Get(chat,"m_TTSSenderDone")&&!(bool)Get(chat,"m_AgentRoundInFlight")&&
                    (int)Get(chat,"m_FirstReplyActiveGeneration")==-1&&f.Model.CancelCalls>cancelCalls,
                    "Actual application pause immediately cancels and settles a first reply without requiring its callback: "+pausedRoute);
                p.Done(pausedRoute==FirstReplyDisposition.Ordinary?Ordinary("LATE_PAUSED_REPLY.",false):
                    new FirstReplyCompletion{Disposition=pausedRoute});
                p.Speech(new SpeechText("LATE_PAUSED_REPLY.","en"));
                f.NoFallback("Application pause rejects late ordinary, evidence and tools handoffs: "+pausedRoute);
                f.Check(f.QueuedText()==""&&(int)Get(chat,"m_RoomSpeechGuardGeneration")==-1,
                    "Paused first reply cannot create an evidence owner, speech queue or planner request: "+pausedRoute);
                Call(chat,"OnApplicationPause",false);
                p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Evidence});
                f.NoFallback("Resuming does not restore a revoked first-reply lease.");
            }
            f.Arm("恢复之后的新一轮。");p=f.Dispatch();p.Done(Ordinary("A fresh answer after resume.",false));
            f.Check(!(bool)Get(chat,"m_FormalResponseInFlight")&&(bool)Get(chat,"m_StreamComplete"),
                "A new first reply after resume completes without retaining the paused owner.");
            f.NoFallback("A fresh ordinary reply after pause stays on its ordinary route.");
            Add("application pause cancels and settles all first-reply routes; stale callbacks remain revoked after resume");

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
                p.Done(new FirstReplyCompletion{Disposition=FirstReplyDisposition.Evidence});
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

            foreach (string human in new[] { "再试一次来我身边。", "为什么没回来？只解释，不要移动。" })
            {
                f.Arm(human);
                Set(chat, "m_SpatialDeviceGoal", null); Set(chat, "m_PendingDeviceClarification", null); Set(chat, "m_SceneObservationGoal", null);
                Set(chat, "m_WorkReviewOpen", true); Set(chat, "m_WorkStatus", "continue"); Set(chat, "m_WorkNoProgress", false);
                Set(chat, "m_WorkContinuations", 0); Set(chat, "m_WorkRoomTaskEpoch", -1); Set(chat, "m_WorkRoomTaskIntent", "none");
                Call(chat, "RememberFirstReplyRequestSemantics", new FirstReplyCompletion {
                    Disposition = FirstReplyDisposition.Evidence, RequestKind = "answer-only" }, f.Generation, f.Model);
                var step = Call(chat, "CreateWorkRoomStepRequest");
                f.Check(step != null, "A missed read-only route gets one independently planned recovery, not generic feedback.");
                Set(chat, "m_WorkContinuations", 1); Set(chat, "m_AgentCurrentRoundIsTick", true);
                int beforeOperation = bridge.Controller.OperationRevision;
                f.Check((bool)Call(chat, "TryPlanRoomTaskCore", "private recovery tick", null, f.Generation, true, "", step, false),
                    "The production recovery entry accepts the bound work step.");
                float until = Time.realtimeSinceStartup + 5f;
                while (f.Model.Room.Count == 0 && Time.realtimeSinceStartup < until) yield return null;
                f.Check(f.Model.Room.Count == 1 && !f.Model.RoomReview[0] && f.Model.FormalCalls == 0 &&
                    bridge.Controller.OperationRevision == beforeOperation, "Recovery reaches an actual planner request before any speech or movement.");
                var recovery = JObject.Parse(f.Model.Room[0]);
                f.Check((string)recovery["currentUserText"] == human && (bool?)recovery["autonomous"] == false &&
                    (bool?)recovery["workContinuation"]?["reclassifyReadOnlyRequest"] == true,
                    "Recovered planning sees the full current request and restrictions; the review suggestion grants no action authority.");
                if (human.Contains("只解释"))
                {
                    f.Model.RoomCallbacks[0](true, "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\",\"replyScope\":\"spatial\"}", "controlled-no-action");
                    while (f.Model.FormalCalls == 0 && Time.realtimeSinceStartup < until) yield return null;
                    f.Check(f.Model.FormalCalls == 1 && f.Model.FormalStage == FormalResponseStage.CommittedSpatialReply && bridge.Controller.OperationRevision == beforeOperation,
                        "Independent no-action decision remains a grounded answer, never movement from the recovery proposal.");
                }
                Call(chat, "CancelRoomSpeechReview", "recovery-fixture-owner-cancelled");
                f.Model.RoomCallbacks[0](true, "{\"intent\":\"approach\",\"origin\":\"user\",\"evidence\":\"" + human + "\"}", "late-cancelled");
                f.Check(bridge.Controller.OperationRevision == beforeOperation, "Late recovery callback cannot execute after owner cancellation.");
            }
            Add("missed read-only action enters normal planner once; full negation and cancellation remain binding");

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
        yield break;
    }
    private static FirstReplyCompletion Ordinary(string text,bool delivered)=>new FirstReplyCompletion{Disposition=FirstReplyDisposition.Ordinary,SpeechDelivered=delivered,SafeOutput="<speech mode=\"independent\"/><lang code=\"en\"/><say>"+text+"</say>"};
    private static void Add(string name)=>((JArray)LastReport["cases"]).Add(name);
    private static FieldInfo Field(Type type,string name){for(;type!=null;type=type.BaseType){var f=type.GetField(name,Flags);if(f!=null)return f;}throw new MissingFieldException(name);}
    private static object Get(object o,string n)=>Field(o.GetType(),n).GetValue(o);
    private static void Set(object o,string n,object v)=>Field(o.GetType(),n).SetValue(o,v);
    private static object Call(object o,string n,params object[] args)=>o.GetType().GetMethods(Flags).Single(x=>x.Name==n&&x.GetParameters().Length==args.Length).Invoke(o,args);
}

public sealed class FirstReplyChatFixtureModel:LLM
{
    public sealed class Pending
    {
        public string Prompt,Image,Context;public bool CommitHistory;
        public Action<SpeechText> Speech;public Action<FirstReplyCompletion> Done;
    }
    public readonly List<Pending> First=new List<Pending>();public readonly List<string> Room=new List<string>();
    public readonly List<bool> RoomReview=new List<bool>();
    public readonly List<Action<bool,string,string>> RoomCallbacks=new List<Action<bool,string,string>>();
    public FormalResponseStage FormalStage;public string FormalPrompt,FormalImage;public bool FormalRecordHistory;
    public Action<SpeechText> FormalSpeech;public Action<string> FormalComplete;
    public int FormalCalls,UnexpectedCalls,CancelCalls;
    public override bool SupportsFirstReplyRouting=>true;
    public override bool SupportsRoomTaskMessages=>true;
    public override bool SupportsVisualDecisionMessages=>true;
    public void Reset(){First.Clear();Room.Clear();RoomReview.Clear();RoomCallbacks.Clear();FormalCalls=0;FormalSpeech=null;FormalComplete=null;FormalImage=null;}
    public override void PostFirstReplyStream(string message,Action<SpeechText> onSpeech,Action<FirstReplyCompletion> onComplete,string imageDataUrl=null,string transientContext=null,bool commitHistory=true)
        =>First.Add(new Pending{Prompt=message,Image=imageDataUrl,Context=transientContext,CommitHistory=commitHistory,Speech=onSpeech,Done=onComplete});
    private void RecordRoom(string input,bool reviewSpeech,Action<bool,string,string> callback){Room.Add(input);RoomReview.Add(reviewSpeech);RoomCallbacks.Add(callback);}
    public override void PostRoomTaskMessage(string input,string imageDataUrl,bool reviewSpeech,Action<bool,string,string> callback)=>RecordRoom(input,reviewSpeech,callback);
    public override void PostRoomTaskMessage(string input,bool reviewSpeech,Action<bool,string,string> callback)=>RecordRoom(input,reviewSpeech,callback);
    public override void PostSpeechStreamForStage(string message,Action<SpeechText> onSpeech,Action<string> onComplete,string imageDataUrl,bool recordAssistantHistory,FormalResponseStage stage)
    {FormalCalls++;FormalStage=stage;FormalPrompt=message;FormalImage=imageDataUrl;FormalRecordHistory=recordAssistantHistory;FormalSpeech=onSpeech;FormalComplete=onComplete;}
    public override void PostSpeechFeedbackStreamForStage(string context,string feedback,Action<SpeechText> onSpeech,Action<string> onComplete,string imageDataUrl,bool recordAssistantHistory,FormalResponseStage stage)
    {FormalCalls++;FormalStage=stage;FormalPrompt=context+feedback;FormalImage=imageDataUrl;FormalRecordHistory=recordAssistantHistory;FormalSpeech=onSpeech;FormalComplete=onComplete;}
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
