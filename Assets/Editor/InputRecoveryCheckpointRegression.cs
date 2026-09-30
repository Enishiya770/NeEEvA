#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

public static class InputRecoveryCheckpointRegression
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value, string why) { checks++; if (!value) throw new InvalidOperationException(why); }
    static ActorIntentDecision None() => new ActorIntentDecision {
        Domain="dialogue", Kind="none", Action="none", Condition="none", Evidence="", Coverage="none" };

    public static JObject RunChecks()
    {
        checks=0;
        using (var f=new Fixture())
        {
            f.Start(); var old=f.Model.Done;
            f.Model.Part(new SpeechText("绝不能续播的旧草稿。", "zh"));
            f.Chat.NotifyUserStartedSpeaking();
            Check(f.Checkpoint != null && (bool)Get(f.Checkpoint,"Sealed"),
                "real NotifyUserStartedSpeaking cancellation must seal a reachable private-General checkpoint: "+new JArray(f.Events).ToString(Newtonsoft.Json.Formatting.None));
            Check(f.Model.Cancels>0 && f.Queued()=="" && Get(f.Chat,"m_FastLoopTurn")==null,
                "VAD still immediately cancels old output and execution owner");
            old("<say>旧回调试图越权。</say>");
            Check(f.Model.ActorRequests==0 && f.Queued()=="", "cancelled private callback cannot acquire Actor permission");
            f.Recover();
            Check(f.Model.Sends==2 && f.Model.FastRequests==0 && f.Checkpoint==null,
                "real DispatchRecoveredInput resumes General exactly once without a second Fast/Role/Room route");
            Check(f.Model.LastWasContinuation && f.Model.m_DataList.Count(x=>x.role=="user")==1,
                "recovery binds exact accepted history rather than adding another user");
            Check(f.Events.Any(x=>(string)x["event"]=="input.recovery_routed" &&
                (string)x["fields"]?["path"]=="private-general-checkpoint"), "actual resumed stage is observable");
            Check(f.Model.SentContext.Contains(Fixture.Goal) && f.Model.SentContext.Contains("sourceContextIsHistorical") &&
                f.Model.SentContext.Split(new[]{Fixture.ContextMarker},StringSplitOptions.None).Length==2,
                "one original context survives, labelled historical; no nested current perception duplication");
            old("<say>更迟的旧回调。</say>");
            Check(f.Model.ActorRequests==0 && f.Queued()=="", "old callback stays invalid after a fresh owner is acquired");
            f.Model.Part(new SpeechText("我还没有开始回唱，先重新确认边界。","zh"));
            f.Model.Complete("<speech mode=\"independent\"/><lang code=\"zh\"/><say>我还没有开始回唱，先重新确认边界。</say>");
            Check(f.Model.ActorRequests==1 && f.Queued()=="", "resumed General remains private until fresh Actor review");
            f.Model.Actor(None(),"ok");
            Check(f.Queued()=="我还没有开始回唱，先重新确认边界。" &&
                f.Model.m_DataList.Count(x=>x.role=="user")==1 &&
                f.Model.m_DataList.Count(x=>x.role=="assistant")==1,
                "fresh reviewed output is delivered once without duplicate user/old speech");
            Check(!(bool)Call(f.Chat,"TryResumeInterruptedGeneralCheckpoint",Fixture.Goal,null,f.Generation),
                "checkpoint is single-use even after successful publication");
        }
        using (var f=new Fixture())
        {
            f.Start(); f.Chat.NotifyUserStartedSpeaking(); f.Recover();
            string first=f.Model.SentContext;
            Set(f.Sense,"m_LiveRecordingCandidateSessionSerial",23);
            f.Chat.NotifyUserStartedSpeaking();
            Check(f.Checkpoint!=null && (bool)Get(f.Checkpoint,"Sealed"),"second actual noise interruption retains bounded stage progress");
            f.Recover();
            Check(f.Model.Sends==3 && f.Model.FastRequests==0 &&
                f.Model.SentContext.Split(new[]{Fixture.ContextMarker},StringSplitOptions.None).Length==2 &&
                f.Model.SentContext.Split(new[]{"[当前恢复边界]"},StringSplitOptions.None).Length==2 &&
                Math.Abs(f.Model.SentContext.Length-first.Length)<100,
                "repeated recovery keeps a single original context/recovery block rather than nesting old perception");
        }
        foreach (string reason in new[]{"http-error","recognizer-empty","preview-empty","unknown-no-speech","cancelled"})
        using (var f=new Fixture())
        {
            f.Start();f.Chat.NotifyUserStartedSpeaking();f.PrepareRecovery(reason);
            Check(!(bool)Call(f.Chat,"TryResumeInterruptedGeneralCheckpoint",Fixture.Goal,null,f.Generation) && f.Model.Sends==1,
                "ambiguous/failed recognition cannot select a retained route: "+reason);
        }
        foreach (string change in new[]{"model","provider","accepted-revision","accepted-history","input-sequence","user-turn",
            "work-epoch","actor-request-owner","scene-epoch","scene-action","room-owner","capture","input-revision","generation","new-public-output","new-plan","age"})
        using (var f=new Fixture())
        {
            f.Start();f.Chat.NotifyUserStartedSpeaking();f.PrepareRecovery("hallucination-rejected");
            switch(change)
            {
                case "model": Set(f.Chat,"m_ChatSettings",new ChatSetting{m_SpeechToText=f.Sense});break;
                case "provider": Set(f.Chat,"m_ChatSettings",new ChatSetting{m_ChatModel=f.Model});break;
                case "accepted-revision": Set(f.Chat,"m_AcceptedRecordingInputRevision",9L);break;
                case "accepted-history": Set(f.Chat,"m_CurrentInputHistoryUser",new LLM.SendData("user",Fixture.Goal));break;
                case "input-sequence": Set(f.Chat,"m_ActorInputSequence",45L);break;
                case "user-turn": Call(Get(f.Chat,"m_ActionConstraints"),"RecordUserTurn","another user");break;
                case "work-epoch": Set(f.Chat,"m_WorkEpoch",8804);break;
                case "actor-request-owner": Call(f.Chat,"CancelActorIntentRequest");break;
                case "scene-epoch": Set(f.Chat,"m_SceneVisionEpoch",(int)Get(f.Chat,"m_SceneVisionEpoch")+1);break;
                case "scene-action": Set(f.Chat,"m_SceneActionEpoch",(int)Get(f.Chat,"m_SceneActionEpoch")+1);break;
                case "room-owner": Set(f.Chat,"m_RoomActorProposalRevocation",(int)Get(f.Chat,"m_RoomActorProposalRevocation")+1);break;
                case "capture": Set(f.Sense,"m_LiveRecordingCandidateSessionSerial",23);break;
                case "input-revision": Set(f.Chat,"m_InputAudioRevision",7);break;
                case "generation": Set(f.Chat,"m_FormalResponseGeneration",f.Generation+1);break;
                case "new-public-output": Set(f.Chat,"m_RealFirstAudioStarted",true);break;
                case "new-plan": Set(f.Chat,"m_ActorPlan",new ActorPlanRuntime("另一个任务"));break;
                case "age": Set(f.Checkpoint,"CreatedAt",Time.realtimeSinceStartup-31f);break;
            }
            Check(!(bool)Call(f.Chat,"TryResumeInterruptedGeneralCheckpoint",Fixture.Goal,null,f.Generation) &&
                f.Model.Sends==1 && f.Model.ActorRequests==0 && f.Queued()=="",
                "changed owner cannot resume a stage or execute old work: "+change);
        }
        foreach(string before in new[]{"heard","submitted-room","compiling-actor"})
        using(var f=new Fixture())
        {
            f.Start();
            if(before=="heard") ((StringBuilder)Get(f.Chat,"m_AssistantHeardText")).Append("已经听到");
            if(before=="submitted-room") { Set(f.Chat,"m_RoomTaskGeneration",8801); Set(f.Chat,"m_RoomTaskIntent","approach"); Set(f.Chat,"m_RoomTaskActionId","submitted-1"); }
            if(before=="compiling-actor") Set(Get(f.Chat,"m_ActorGeneralCandidate"),"CompilingActor",true);
            Call(f.Chat,"PreservePendingReplyForInputCandidate");
            Check(f.Checkpoint==null,"only unpublished, no-effect General transport is retained: "+before);
        }
        using(var f=new Fixture())
        {
            f.Start();f.Chat.NotifyUserStartedSpeaking();
            Call(f.Chat,"SetCurrentUserInput","换一个任务","换一个任务");
            Check(f.Checkpoint==null,"accepted new user input explicitly clears saved route before any dispatch");
        }
        using(var f=new Fixture())
        {
            f.Start();f.Chat.NotifyUserStartedSpeaking();f.PrepareRecovery("hallucination-rejected");
            Set(f.Chat,"m_ResponseTrace",new ConversationTrace("checkpoint-reentrant",raw=>{
                var value=JObject.Parse(raw);
                if((string)value["event"]=="input.recovery_routed")Set(f.Chat,"m_FormalResponseGeneration",9900);
            }));
            Check((bool)Call(f.Chat,"TryResumeInterruptedGeneralCheckpoint",Fixture.Goal,null,f.Generation) && f.Model.Sends==1,
                "trace callback replacing owner after claim cannot send stale recovery or fall through");
        }
        using(var f=new Fixture())
        {
            Set(f.Chat,"m_InterruptedHeardText","实际已听片段");
            Set(f.Chat,"m_RecoveredPlanCheckpoint",new JObject{["originalGoal"]=Fixture.Goal,["results"]=new JArray(new JObject{["phase"]="performed"})});
            var facts=(JObject)Call(f.Chat,"BuildCompactInputRecoveryFacts",new object[]{null});
            Check((string)facts["userText"]==Fixture.Goal && (string)facts["heardText"]=="实际已听片段" &&
                facts["completedPlanCheckpoint"]!=null && facts["context"]==null,
                "compact input-recovery facts retain actual goal/heard/results without nesting complete perception/context");
        }
        return new JObject{["passed"]=true,["checks"]=checks,["scope"]="real private-General entry, NotifyUserStartedSpeaking cancellation, exact history recovery, fresh General/Actor/public gate; fake provider, no network/PCM/actions"};
    }

    sealed class Fixture:IDisposable
    {
        public const string Goal="请把第二段开头说话去掉，再唱出来。", ContextMarker="checkpoint-original-context-once";
        readonly GameObject host,ui;
        public readonly ChatSample Chat; public readonly InputRecoveryCheckpointModel Model; public readonly SenseVoiceSpeechToText Sense;
        public readonly List<JObject> Events=new List<JObject>();
        public int Generation;
        public object Checkpoint=>Get(Chat,"m_InterruptedGeneralCheckpoint");
        public Fixture()
        {
            host=new GameObject("Input recovery private stage fixture"){hideFlags=HideFlags.HideAndDontSave};
            ui=new GameObject("Fixture response text"){hideFlags=HideFlags.HideAndDontSave};
            Chat=host.AddComponent<ChatSample>();Model=host.AddComponent<InputRecoveryCheckpointModel>();Sense=host.AddComponent<SenseVoiceSpeechToText>();
            Model.m_DataList=new List<LLM.SendData>{new LLM.SendData("user",Goal)};
            Set(Chat,"m_ChatSettings",new ChatSetting{m_ChatModel=Model,m_SpeechToText=Sense});Set(Chat,"m_TextBack",ui.AddComponent<Text>());
            Set(Chat,"m_FastLoop",new FastLoopRuntime());Set(Chat,"m_FormalResponseGeneration",8801);Set(Chat,"m_WorkEpoch",8803);Set(Chat,"m_CurrentSpeechRoundId",8805);
            Set(Chat,"m_LastUserMsg",Goal);Set(Chat,"m_LastUserEvidence",Goal);Set(Chat,"m_AcceptedRecordingInputRevision",8L);
            Set(Chat,"m_CurrentInputHistoryModel",Model);Set(Chat,"m_CurrentInputHistoryUser",Model.m_DataList[0]);
            Set(Chat,"m_ActorHistoryGeneration",8801);Set(Chat,"m_ActorInputGeneration",8801);Set(Chat,"m_ActorInputSequence",44L);
            Set(Chat,"m_InputAudioRevision",5);Set(Chat,"m_UserTurnAwaitingReplySince",Mathf.Max(.01f,Time.realtimeSinceStartup));
            Set(Chat,"m_SentenceBuffer",new SpeechTextBuffer());Set(Chat,"m_AssistantHeardText",new StringBuilder());Set(Chat,"m_ChatHistory",new List<string>{Goal});
            foreach(string name in new[]{"m_EnableActorIntentCompiler","m_EnableFastTaskLoop","m_UseStreaming","m_IsVoiceMode","m_SceneTimePaused","m_RoundInnerCheckDone","m_RoundRepeatCheckDone"})Set(Chat,name,true);
            foreach(string name in new[]{"m_AgentRunning","m_AgentCurrentRoundIsTick","m_AgentRoundInFlight","m_UserSpeechActiveForAutonomy","m_SceneVisionApplicationPaused","m_EnableScreenVision","m_EnableSceneActions","m_LogAgentLoop","m_LogRawLLMOutput","m_LogStreamTimings"})Set(Chat,name,false);
            Set(Sense,"m_LiveRecordingCandidateSessionSerial",22);
            Set(Chat,"m_ResponseTrace",new ConversationTrace("checkpoint-pipeline",raw=>Events.Add(JObject.Parse(raw))));
        }
        public void Start()=>Check((bool)Call(Chat,"TryDispatchActorGeneralDraft",Goal,null,8801,true,ContextMarker+"\n完整目标："+Goal,true),"real private-General leaf dispatched");
        public string PrepareRecovery(string rejection)
        {
            Call(Sense,"PublishFinalTranscriptOutcome",Sense.InputCaptureSessionSerial,"rejected",rejection,"无效识别","无效识别");
            Set(Chat,"m_UserSpeechActiveForAutonomy",false);
            string context=(string)Call(Chat,"ConsumeInterruptedReplyContext");
            Check(context!=null,"original accepted user question survives real input cancellation");
            // The outer StartStreaming allocates this generation/round and audio
            // player in production. This fixture uses the actual allocator but
            // intentionally never starts an audio player or a scene capture.
            Generation=(int)Call(Chat,"BeginFormalResponseGeneration");
            Set(Chat,"m_CurrentSpeechRoundId",(int)Get(Chat,"m_CurrentSpeechRoundId")+1);
            return context;
        }
        public void Recover(){string context=PrepareRecovery("hallucination-rejected");Call(Chat,"DispatchRecoveredInput",Goal,context,null,Generation);}
        public string Queued()=>string.Concat(((IEnumerable)Get(Chat,"m_PendingChunks")).Cast<object>().Select(x=>(string)Get(x,"Text")))+Get(Chat,"m_SentenceBuffer");
        public void Dispose(){UnityEngine.Object.DestroyImmediate(ui);UnityEngine.Object.DestroyImmediate(host);}
    }
    static object Get(object target,string name)=>target.GetType().GetField(name,Flags).GetValue(target);
    static void Set(object target,string name,object value)=>target.GetType().GetField(name,Flags).SetValue(target,value);
    static object Call(object target,string name,params object[] args)=>target.GetType().GetMethods(Flags).Single(x=>x.Name==name && x.GetParameters().Length==args.Length).Invoke(target,args);
}

public sealed class InputRecoveryCheckpointModel:LLM,IActorIntentBackend,IFastLoopDecisionBackend,IFastLoopHistoryBackend,IFastLoopRecoveryHistoryBackend
{
    public Action<SpeechText> Part; public Action<string> Done; public Action<ActorIntentDecision,string> Actor;
    public int Sends,FastRequests,ActorRequests,Cancels; public string SentContext; public bool LastWasContinuation;
    public bool SupportsActorIntents=>true; public bool SupportsFastLoopDecisions=>true;
    public void Complete(string raw)=>Done(RoleOutputChannels.Parse(raw,RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions).ToExecutableText(true,true));
    public void PostFastLoopDecision(string context,string user,Action<FastLoopDecision,string> callback,ConversationTrace trace=null,string imageDataUrl=null){FastRequests++;}
    public void CancelFastLoopDecision(){}
    public void PostActorIntent(string context,string draft,string user,Action<ActorIntentDecision,string> callback,ConversationTrace trace=null){ActorRequests++;Actor=callback;}
    public void PostActorConditionSignal(string context,string user,Action<string,string> callback,ConversationTrace trace=null){}
    public void CancelActorIntent(){}
    public void BeginFastLoopHistory(string prompt,string image,bool commit){}
    public void CompleteFastLoopHistory(){}
    public bool ResumeFastLoopHistory(LLM.SendData accepted,string image)=>m_DataList.Contains(accepted) && ReferenceEquals(accepted,m_DataList.LastOrDefault(x=>x.role=="user"));
    public override void CancelActiveResponse(){Cancels++;}
    public override void PostSpeechStreamForStage(string prompt,Action<SpeechText> part,Action<string> done,string image,bool history,FormalResponseStage stage)
    {Sends++;Part=part;Done=done;SentContext=RequestContext;LastWasContinuation=false;}
    public override void PostSpeechFeedbackStream(string context,string feedback,Action<SpeechText> part,Action<string> done,string image,bool history)
    {Sends++;Part=part;Done=done;SentContext=feedback;LastWasContinuation=true;}
}
#endif
