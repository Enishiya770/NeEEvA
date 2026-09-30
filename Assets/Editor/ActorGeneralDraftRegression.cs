#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Real General candidate gate and Actor callbacks; no network or physical playback.</summary>
public static class ActorGeneralDraftRegression
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value,string why){checks++;if(!value)throw new InvalidOperationException(why);}
    static ActorIntentDecision None()=>new ActorIntentDecision{Domain="dialogue",Kind="none",Action="none",Condition="none",Evidence="",Coverage="none"};
    public static JObject RunChecks()
    {
        checks=0;
        using(var f=new Fixture())
        {
            f.Start();Check(!f.Model.RecordCandidate && f.Model.Stage==FormalResponseStage.General,"General candidate must not be committed to assistant history");
            f.Model.Part(new SpeechText("你好。","zh"));
            Check(f.Queued()=="" && f.Model.Actor==null,"all General deltas stay private until complete");
            f.Model.Complete("<speech mode=\"independent\"/><lang code=\"zh\"/><say>你好。</say><motion action=\"head_raise\"/>");
            Check(f.Model.Actor!=null && f.Queued()=="" && f.NoPlanOrHead,"raw tags cannot execute before the Actor result");
            Check(f.Model.m_DataList.Count(x=>x.role=="user")==1 && f.Model.Resumed==f.Model.m_DataList[0],"new General user is rebound by exact identity");
            var callback=f.Model.Actor;callback(None(),"ok");
            Check(f.Queued()=="你好。" && f.NoPlanOrHead,"non-action candidate publishes once without replaying raw motion");
            var assistant=f.Model.m_DataList.Last(x=>x.role=="assistant").content;
            Check(!assistant.Contains("motion") && f.Model.m_DataList.Count(x=>x.role=="assistant")==1,"only sanitized approved envelope enters history");
            callback(None(),"duplicate");Check(f.Queued()=="你好。","duplicate actor callback does not publish twice");
        }
        using(var f=new Fixture())
        {
            f.Start();f.Model.Part(new SpeechText("こんにちは。","ja"));f.Model.Part(new SpeechText("这几个字用中文。","zh"));
            f.Model.Complete("<speech mode=\"independent\"/><lang code=\"ja\"/><say>こんにちは。</say><lang code=\"zh\"/><say>这几个字用中文。</say>");
            Check(f.Model.Actor!=null,"real public projection and separate language deltas agree before Actor dispatch");
            f.Model.Actor(None(),"ok");
            var envelope=f.Model.m_DataList.Last(x=>x.role=="assistant").content;
            Check(envelope.Contains("code=\"ja\"") && envelope.Contains("code=\"zh\""),"approved history retains both actual language runs");
            Check(f.Queued()=="こんにちは。这几个字用中文。","language switching preserves complete approved speech order");
            var languages=f.QueuedLanguages();Check(languages.Contains("ja") && languages.Contains("zh"),"queued speech retains Japanese and Chinese instead of one whole-draft language");
        }
        foreach(string invalidation in new[]{"cancel","generation","provider"})
        using(var f=new Fixture())
        {
            f.Start();var complete=f.Model.Done;
            if(invalidation=="cancel")Call(f.Chat,"CancelActorIntentRequest");
            else if(invalidation=="generation")Set(f.Chat,"m_FormalResponseGeneration",8802);
            else Set(f.Chat,"m_ChatSettings",new ChatSetting());
            complete("<lang code=\"zh\"/><speech mode=\"independent\"/><say>我来抬头。</say>");
            Check(f.Model.Actor==null && f.Queued()=="" && f.NoPlanOrHead,"stale General complete cannot acquire actor permission: "+invalidation);
        }
        using(var f=new Fixture())
        {
            f.Start();var callback=f.Model.Done;f.Start();Check(f.Model.Sends==1,"same pending General entry is idempotent");
            f.Model.Part(new SpeechText("你好。","zh"));callback("<lang code=\"zh\"/><speech mode=\"independent\"/><say>你好。</say>");
            callback("<lang code=\"zh\"/><speech mode=\"independent\"/><say>第二次。</say>");
            Check(f.Model.ActorRequests==1,"provider duplicate completion compiles only once");
        }
        using(var f=new Fixture())
        {
            var old=new LLM.SendData("user","上一轮输入");f.Model.m_DataList.Add(old);f.Start(true);
            f.Model.Part(new SpeechText("继续聊。","zh"));f.Model.Complete("<lang code=\"zh\"/><speech mode=\"independent\"/><say>继续聊。</say>");
            Check(f.Model.Actor==null && f.Model.Resumed==null && f.Model.m_DataList.Count(x=>x.role=="user")==1,"continuation cannot claim an unrelated previous user object");
        }
        using(var f=new Fixture())
        {
            var accepted=new LLM.SendData("user","本轮输入");f.Model.m_DataList.Add(accepted);
            Set(f.Chat,"m_CurrentInputHistoryModel",f.Model);Set(f.Chat,"m_CurrentInputHistoryUser",accepted);f.Start(true);
            f.Model.Part(new SpeechText("继续聊。","zh"));f.Model.Complete("<lang code=\"zh\"/><speech mode=\"independent\"/><say>继续聊。</say>");
            Check(f.Model.Actor!=null && ReferenceEquals(accepted,f.Model.Resumed),"continuation keeps the exact current input history owner");
            f.Model.Actor(None(),"ok");Check(f.Model.m_DataList.Count(x=>x.role=="user")==1 && f.Queued()=="继续聊。","approved continuation does not duplicate user history");
        }
        using(var f=new Fixture())
        {
            f.Start();f.Model.Complete("<speech mode=\"independent\"/><motion action=\"head_raise\"/>");
            Check(f.Model.Actor==null && f.NoPlanOrHead,"tool-only legacy candidate is diagnosed, never fabricated into role intent");
        }
        using(var f=new Fixture())
        {
            f.Start();f.Model.Part(new SpeechText("只有前半。","zh"));f.Model.Complete("<lang code=\"zh\"/><speech mode=\"independent\"/><say>只有前半。还有行动。</say>");
            Check(f.Model.Actor==null && f.NoPlanOrHead && !f.Queued().Contains("只有前半"),"incomplete deltas cannot publish a truncated commitment");
        }
        using(var f=new Fixture())
        {
            Call(f.Chat,"RememberActorTaskLanguage",8801,"zh");
            f.Start();var old=f.Model.Done;
            f.Model.Part(new SpeechText("不能发布的旧草稿。","ja"));
            f.Model.Complete("<lang code=\"ja\"/><speech mode=\"independent\"/><say>完整文本与delta不同。</say>");
            const string failure="这一轮我还没能给出有效回复，也没有开始新的动作。";
            Check(f.Queued()==failure && f.QueuedLanguages().All(x=>x=="zh"),"invalid General language cannot overwrite current accepted reply language");
            Check(!f.Queued().Contains("换个说法") && f.NoPlanOrHead,"internal candidate failure does not blame user wording or submit action");
            int messages=f.Model.m_DataList.Count,sends=f.Model.Sends;
            Call(f.Chat,"CompleteActorInterpretationFailure",f.Model,"本轮输入","ja",8801,true,"second-failure");
            f.Start();old("<lang code=\"ja\"/><say>再来一次。</say>");
            Check(f.Queued()==failure && f.Model.m_DataList.Count==messages && f.Model.Sends==sends,
                "same-turn duplicate failure, General reentry and old callback cannot double-publish/history/send");
            Set(f.Chat,"m_CurrentSpeechRoundId",8806);
            Call(f.Chat,"CompleteActorInterpretationFailure",f.Model,"本轮输入","zh",8801,true,"correction-round-failure");
            Check(f.Queued()==failure && f.Model.m_DataList.Count==messages && f.Model.Sends==sends,
                "a correction speech round cannot reopen the same accepted-input failure closure");
            Check(!(bool)Get(f.Chat,"m_FormalResponseInFlight"),"closed General failure does not leave a formal wait running");
            Set(f.Chat,"m_FormalResponseGeneration",8802);Set(f.Chat,"m_CurrentSpeechRoundId",8806);
            Check(!(bool)Call(f.Chat,"ActorReplyFailureAlreadyClosed",f.Model,8802),"a fresh user generation is not suppressed by previous failure closure");
        }
        foreach(bool previousInput in new[]{false,true})
        using(var f=new Fixture())
        {
            const string goal="先关灯，然后唱歌。";
            var plan=new ActorPlanRuntime(goal,"actor_draft","compound");
            plan.Propose(1,"set-object-state","唱歌","lamp:off",out _);plan.BindAction(1,"power-1");
            plan.Resolve(1,"power-1",true,"performed","readback","lamp-1",true,"off");
            plan.Results.Last["requestedState"]="off";
            var runtime=(FastLoopRuntime)Get(f.Chat,"m_FastLoop");
            var turn=Activator.CreateInstance(typeof(ChatSample).GetNestedType("FastLoopTurnContext",BindingFlags.NonPublic),Flags,null,
                new object[]{f.Model,"本轮输入",null,null,8801,previousInput?8802:8803,8805,runtime.OutputRevision,true},null);
            Set(f.Chat,"m_ActorPlan",plan);Set(f.Chat,"m_ActorPlanTurn",turn);
            Call(f.Chat,"RememberActorTaskLanguage",8801,"zh");
            f.Start();Set(f.Chat,"m_ToolCorrectionContinuationPending",true);Set(f.Chat,"m_RoundContinue",true);
            f.Model.Part(new SpeechText("旧的非法候选。","zh"));
            f.Model.Complete("<lang code=\"zh\"/><speech mode=\"independent\"/><say>与delta不同。</say>");
            Check(previousInput ? !f.Queued().Contains("关灯") : f.Queued().Contains("关灯的步骤已经确认完成") &&
                !f.Queued().Contains("没有开始新的动作"),"Failure receipt keeps verified results only from this accepted input.");
            Check(!(bool)Get(f.Chat,"m_ToolCorrectionContinuationPending") && !(bool)Get(f.Chat,"m_RoundContinue"),
                "closing an invalid private candidate consumes its stale legacy correction continuation");
            int sends=f.Model.Sends;string queued=f.Queued();
            Set(f.Chat,"m_CurrentSpeechRoundId",8807);Set(f.Chat,"m_AgentRunning",true);Set(f.Chat,"m_AgentRoundInFlight",true);
            Check(!(bool)Call(f.Chat,"BeginAgentContinuationChain") && f.Model.Sends==sends && f.Queued()==queued,
                "post-closure continuation cannot create another model request or another failure utterance");
        }
        using(var f=new Fixture())
        {
            const string goal="先走近桌上的灯，再确认开关状态，最后决定要不要关掉。";
            var plan=new ActorPlanRuntime(goal);
            Set(f.Chat,"m_ActorPlan",plan);Set(f.Chat,"m_ActorPlanWatchedRevision",-1);
            f.Start();
            Check(plan.State=="cancelled" && plan.PendingActionId==null && f.Model.Sends==1,
                "a planning-only goal with no executor cannot swallow General and stall forever");
            Check(f.Model.SentContext.Contains(goal) && f.Model.SentContext.Contains("remainingGoal") &&
                f.Model.SentContext.Contains("general-entry-without-executable-plan-step"),
                "unavailable execution forwards full original/remaining goal and the cancelled fact without inventing acceptance");
            f.Model.Part(new SpeechText("我现在还没开始移动。","zh"));
            f.Model.Complete("<lang code=\"zh\"/><speech mode=\"independent\"/><say>我现在还没开始移动。</say>");
            Check(f.Model.Actor!=null && f.Queued()=="","planning fallback still passes the same unpublished Actor gate");
            f.Model.Actor(None(),"ok");
            Check(f.Queued()=="我现在还没开始移动。" && !(bool)Get(f.Chat,"m_FormalResponseInFlight"),
                "planning fallback completes the formal wait after the approved reply");
        }
        return new JObject{["passed"]=true,["checks"]=checks,["scope"]="production private General buffer, actor publication, language runs and exact history ownership; fake provider, no network/PCM/avatar"};
    }
    sealed class Fixture:IDisposable
    {
        readonly GameObject host;
        public readonly ChatSample Chat;
        public readonly ActorGeneralDraftModel Model;
        public bool NoPlanOrHead=>Get(Chat,"m_ActorPlan")==null && ((FastLoopRuntime)Get(Chat,"m_FastLoop")).HeadTask==null;
        public Fixture()
        {
            host=new GameObject("Actor General private candidate fixture"){hideFlags=HideFlags.HideAndDontSave};
            Chat=host.AddComponent<ChatSample>();Model=host.AddComponent<ActorGeneralDraftModel>();
            Model.m_DataList=new List<LLM.SendData>();
            Set(Chat,"m_ChatSettings",new ChatSetting{m_ChatModel=Model});Set(Chat,"m_FastLoop",new FastLoopRuntime());
            Set(Chat,"m_FormalResponseGeneration",8801);Set(Chat,"m_WorkEpoch",8803);Set(Chat,"m_CurrentSpeechRoundId",8805);Set(Chat,"m_LastUserMsg","本轮输入");
            foreach(string n in new[]{"m_EnableActorIntentCompiler","m_EnableFastTaskLoop","m_UseStreaming","m_IsVoiceMode","m_SceneTimePaused","m_RoundInnerCheckDone","m_RoundRepeatCheckDone"})Set(Chat,n,true);
            foreach(string n in new[]{"m_AgentRunning","m_AgentCurrentRoundIsTick","m_AgentRoundInFlight","m_UserSpeechActiveForAutonomy","m_SceneVisionApplicationPaused","m_FormalResponseInFlight","m_StreamComplete","m_EnableScreenVision","m_EnableSceneActions","m_LogAgentLoop","m_LogRawLLMOutput","m_LogStreamTimings"})Set(Chat,n,false);
            Set(Chat,"m_SentenceBuffer",new SpeechTextBuffer());Set(Chat,"m_AssistantHeardText",new StringBuilder());Set(Chat,"m_ChatHistory",new List<string>{"本轮输入"});
        }
        public void Start(bool continuation=false)=>Check((bool)Call(Chat,"TryDispatchActorGeneralDraft","本轮输入",null,8801,true,"private context",continuation),"General adapter accepts request");
        public string Queued()=>string.Concat(((IEnumerable)Get(Chat,"m_PendingChunks")).Cast<object>().Select(v=>(string)Get(v,"Text")))+Get(Chat,"m_SentenceBuffer");
        public List<string> QueuedLanguages()
        {
            var result=((IEnumerable)Get(Chat,"m_PendingChunks")).Cast<object>().Select(v=>(string)Get(v,"LanguageCode")).ToList();
            result.AddRange(((SpeechTextBuffer)Get(Chat,"m_SentenceBuffer")).Snapshot().Select(v=>v.LanguageCode));return result;
        }
        public void Dispose()=>UnityEngine.Object.DestroyImmediate(host);
    }
    static object Get(object o,string name)=>o.GetType().GetField(name,Flags).GetValue(o);
    static void Set(object o,string name,object value)=>o.GetType().GetField(name,Flags).SetValue(o,value);
    static object Call(object o,string name,params object[] args)=>o.GetType().GetMethods(Flags).Single(m=>m.Name==name && m.GetParameters().Length==args.Length).Invoke(o,args);
}
public sealed class ActorGeneralDraftModel:LLM,IActorIntentBackend,IFastLoopHistoryBackend,IFastLoopRecoveryHistoryBackend
{
    public Action<SpeechText> Part;
    public Action<string> Done;
    public Action<ActorIntentDecision,string> Actor;
    public int Sends,ActorRequests;
    public bool RecordCandidate;
    public FormalResponseStage Stage;
    public LLM.SendData Resumed;
    public string SentContext;
    public bool SupportsActorIntents=>true;
    // ChatQW completes with RoleOutputChannels' executable projection, not
    // original say/lang wrappers. Use that same production parser/projection;
    // the public deltas separately retain each language span.
    public void Complete(string raw)=>Done(RoleOutputChannels.Parse(raw,
        RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions).ToExecutableText(true,true));
    public void PostActorIntent(string context,string draft,string currentUser,Action<ActorIntentDecision,string> callback,ConversationTrace trace=null){ActorRequests++;Actor=callback;}
    public void PostActorConditionSignal(string context,string currentUser,Action<string,string> callback,ConversationTrace trace=null){}
    public void CancelActorIntent(){}
    public void BeginFastLoopHistory(string prompt,string image,bool commit){}
    public void CompleteFastLoopHistory(){}
    public bool ResumeFastLoopHistory(LLM.SendData accepted,string image){Resumed=accepted;return m_DataList.Contains(accepted) && ReferenceEquals(accepted,m_DataList.LastOrDefault(x=>x.role=="user"));}
    public override void PostSpeechStreamForStage(string prompt,Action<SpeechText> part,Action<string> done,string image,bool history,FormalResponseStage stage)
    {m_DataList.Add(new LLM.SendData("user",prompt));Sends++;Part=part;Done=done;RecordCandidate=history;Stage=stage;SentContext=RequestContext;}
    public override void PostSpeechFeedbackStream(string context,string feedback,Action<SpeechText> part,Action<string> done,string image,bool history)
    {Sends++;Part=part;Done=done;RecordCandidate=history;Stage=FormalResponseStage.General;SentContext=feedback;}
}
#endif
