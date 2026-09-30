#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Production callback/permission boundaries; no model, network, speaker or avatar movement.</summary>
public static class ActorIntentReviewRegression
{
    const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static int checks;
    static void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException(why);}
    public static JObject RunChecks()
    {
        checks=0;
        const string hello="听到了哦，涅瓦在这里呢。";
        using(var f=new Fixture(hello))
        {
            f.Model.Actor(NonAction(),"ok");
            Check(f.Model.Reviews.Count==0 && f.Queued()==hello,"valid ordinary reply takes no review request and preserves original draft");
        }
        foreach(string draft in new[]{"嗯，好的。我会一直跟着你的，请多指教了。", "嗯，没关系，我们慢慢来。我会再仔细确认一下旋律的边界，这次一定只唱你唱的那部分。"})
        using(var f=new Fixture(draft))
        {
            f.Model.Actor(NonAction(),"ok");
            Check(f.Model.Reviews.Count==1 && f.Queued()=="" && f.NoPlanOrHead,"observed false-none promise remains private and gets one independent review");
            var answer=f.Model.Reviews[0];
            answer(new ActorIntentReviewDecision{Status="adopted_action",Goal=draft,Evidence=draft,Task="other",Target="none",Coverage="atomic"},"ok");
            Check(Get(f.Chat,"m_ActorPlan")!=null && !f.Queued().Contains(draft),"independent adoption hands off complete goal, never publishes a none promise");
            int requests=f.Model.FormalRequests;
            answer(Review("non_action",draft),"duplicate");
            Check(f.Model.Reviews.Count==1 && f.Model.FormalRequests==requests && !f.Queued().Contains(draft),"duplicate prospective review cannot publish or replan a second time");
        }
        foreach(string draft in new[]{"我会跟着你只是一个例句。","我会唱歌，但现在不唱。","假如我会瞬移，就不用走了。","I will not follow you."})
        using(var f=new Fixture(draft))
        {
            f.Model.Actor(NonAction(),"ok");
            Check(f.Model.Reviews.Count==1 && f.NoPlanOrHead && f.Queued()=="","ambiguous prospective wording is only a review trigger");
            f.Model.Reviews[0](Review("non_action",draft),"ok");
            Check(f.Queued()==draft && f.NoPlanOrHead,"semantic refusal/quote/ability/hypothesis remains dialogue");
        }
        using(var f=new Fixture("我会等门打开后再跟着你。"))
        {
            f.Model.Actor(new ActorIntentDecision{Domain="other_action",Kind="replan",Task="follow",Target="current_user",Coverage="atomic",
                Goal="等门开后跟随用户",Evidence=f.Draft,Action="none",Condition="none"},"ok");
            Check(f.Model.Reviews.Count==1 && f.Queued()=="" && f.NoPlanOrHead,"schema-valid but condition-bearing follow still waits for semantic trigger review");
            f.Model.Reviews[0](new ActorIntentReviewDecision{Status="adopted_action",Goal="等门开后跟随用户",Evidence=f.Draft,
                Task="follow",Target="current_user",Coverage="atomic",Trigger="deferred"},"ok");
            Check(f.NoPlanOrHead && f.Model.FormalRequests==0 && !f.Queued().Contains("等门"),
                "reviewed future trigger cannot become immediate user-follow submission or a public waiting promise");
        }
        using(var f=new Fixture("我会一直跟着你的。"))
        {
            f.Model.Actor(new ActorIntentDecision{Domain="other_action",Kind="act",Task="follow",Target="current_user",Coverage="compound",
                Goal="持续跟随用户",Evidence=f.Draft,Action="none",Condition="none",RequiresFullReview=true,ReviewReason="compound_goal_requires_planner"},"ok");
            f.Model.Reviews[0](Review("non_action",f.Draft),"ok");
            Check(f.NoPlanOrHead && !f.Queued().Contains(f.Draft) && f.Queued().Contains("没有开始新的动作"),
                "observed initial typed follow versus non-action review disagreement closes once without publishing false promise");
        }
        using(var f=new Fixture(hello))
        {
            f.Model.Actor(Conflict(hello),"ok");
            Check(f.Model.Reviews.Count==1 && f.Queued()=="" && f.NoPlanOrHead,"conflicting greeting has no publication or execution while review is pending");
            var answer=f.Model.Reviews[0];answer(Review("non_action",hello),"ok");
            Check(f.Queued()==hello && f.NoPlanOrHead,"review restores ordinary greeting without movement");
            answer(Review("adopted_action",hello),"late duplicate");
            Check(f.Queued()==hello && f.NoPlanOrHead,"duplicate reviewer callback cannot upgrade action scope");
        }
        using(var f=new Fixture(hello))
        {
            f.Model.Actor(null,"actor_goal_required");
            f.Model.Reviews[0](Review("non_action",hello),"ok");
            Check(f.Queued()==hello,"wire goal conflict recovers conversational reply, not movement correction");
        }
        // Actual 2026-09-26 fixed-set model output: an unsupported door trigger
        // was mislabeled as the native count-three condition. Exercise the full
        // compiler callback, not only the private numeric-anchor helper.
        const string doorDraft="等门打开后，我再抬头。";
        using(var f=new Fixture(doorDraft))
        {
            string raw=new JObject{["version"]=ActorIntentProtocol.IntentVersion,["kind"]="wait",["domain"]="head",["coverage"]="atomic",
                ["goal"]="等门打开后，再执行抬头动作。",["task"]="none",["target"]="none",["action"]="head.raise",
                ["condition"]="user_count_three",["evidence"]=doorDraft, ["motionScope"]="head_preset",
                ["degrees"]=null,["deviceState"]="none",["targetEntityId"]=""}.ToString();
            Check(ActorIntentProtocol.TryCompile(raw,doorDraft,out var wrongWait,out _) && !wrongWait.RequiresFullReview,
                "observed wrong condition is schema-valid, so runtime source validation remains necessary");
            f.Model.Actor(wrongWait,"ok");
            Check(((ActorWaitingTaskRuntime)Get(f.Chat,"m_ActorWaiting")).Pending==null && f.NoPlanOrHead,
                "door condition cannot arm count-three or a replacement action plan");
            Check(f.Model.Reviews.Count==0 && f.Model.FormalRequests==0,
                "unsupported condition is handled without a second free-text execution request");
            Check(f.Queued()=="这个等待条件还没有可靠的触发方式，我没有安排或开始这项动作。",
                "real callback publishes unsupported-trigger fact instead of the false wait promise");
        }
        foreach(string draft in new[]{"我现在不想走过去，只想继续聊聊。","この部屋には飾り灯なんて見当たらないわよ。"})
        using(var f=new Fixture(draft))
        {
            f.Model.Actor(Conflict(draft),"ok");f.Model.Reviews[0](Review("non_action",draft),"ok");
            Check(f.Queued()==draft && f.NoPlanOrHead,"refusal or visual absence never becomes physical planning through recovery");
        }
        foreach(bool past in new[]{false,true})
        {
            string draft=past?"已经把灯关掉了。":"消すね。";
            using(var f=new Fixture(draft))
            {
                f.Model.Actor(Claim(draft),"ok");
                Check(f.Model.Reviews.Count==1 && f.NoPlanOrHead,"claim temporal review starts without execution");
                f.Model.Reviews[0](Review(past?"reported_action":"adopted_action",draft),"ok");
                if(past)Check(f.NoPlanOrHead,"reported completed action remains verification-only and cannot be backfilled");
                else Check(Get(f.Chat,"m_ActorPlan")!=null && f.Runtime.HeadTask==null,"future decision can enter full planning after independent semantic adoption");
            }
        }
        using(var f=new Fixture("先走近灯，再关掉。"))
        {
            f.Model.Actor(Conflict(f.Draft),"ok");f.Model.Reviews[0](Review("uncertain",f.Draft),"ok");
            Check(f.NoPlanOrHead && f.Queued()=="这一轮我还没能给出有效回复，也没有开始新的动作。","unresolved interpretation states no new submission, not execution failure or a request to rephrase");
        }
        foreach(string cancellation in new[]{"cancel","new-generation","new-provider"})
        using(var f=new Fixture(hello))
        {
            f.Model.Actor(Conflict(hello),"ok");var answer=f.Model.Reviews[0];
            if(cancellation=="cancel")Call(f.Chat,"CancelActorIntentRequest");
            else if(cancellation=="new-generation")Set(f.Chat,"m_FormalResponseGeneration",9999);
            else Set(f.Chat,"m_ChatSettings",new ChatSetting());
            answer(Review("adopted_action",hello),"stale");
            Check(f.Queued()=="" && f.NoPlanOrHead,"stale semantic review loses ownership: "+cancellation);
        }
        using(var f=new Fixture(hello))
        {
            f.Model.DuringReview=()=>Call(f.Chat,"CancelActorIntentRequest");
            f.Model.Actor(Conflict(hello),"ok");
            f.Model.Reviews[0](Review("adopted_action",hello),"late");
            Check(f.NoPlanOrHead && f.Queued()=="","provider synchronous cancellation cannot reauthorize review");
        }
        using(var f=new Fixture(hello))
        {
            f.Model.ThrowReview=true;f.Model.Actor(Conflict(hello),"ok");
            Check(f.NoPlanOrHead && f.Queued()=="这一轮我还没能给出有效回复，也没有开始新的动作。","throwing review provider produces bounded factual interpretation failure");
        }
        foreach (string task in new[] { "follow", "approach" })
        using (var f = new Fixture("好，我先走到你身边，再一直跟着你。"))
        {
            f.Model.Actor(Conflict(f.Draft), "requires-review");
            var review = new ActorIntentReviewDecision { Status = "adopted_action", Task = task,
                Target = "current_user", Coverage = "compound", Trigger = "none",
                Goal = "先走到当前用户身边，然后持续跟随当前用户", Evidence = f.Draft };
            f.Model.Reviews[0](review, "compound-shortcut-shape");
            var plan = (ActorPlanRuntime)Get(f.Chat, "m_ActorPlan");
            Check(plan != null && plan.GoalCoverage == "compound" && plan.CompiledGoal == review.Goal &&
                plan.OriginalGoal == f.Draft && plan.Results.Count == 0,
                "Reviewed compound user task preserves its complete goal in the actual planner, with no shortcut execution.");
            f.Model.Reviews[0](review, "duplicate");
            Check(ReferenceEquals(plan, Get(f.Chat, "m_ActorPlan")) && f.Model.Reviews.Count == 1,
                "Compound routing normalization consumes one reviewed owner and makes no extra model review.");
        }
        foreach (string cancellation in new[] { "cancel", "new-generation", "new-provider" })
        using (var f = new Fixture("我先走近，再跟着你。"))
        {
            f.Model.Actor(Conflict(f.Draft), "requires-review");
            if (cancellation == "cancel") Call(f.Chat, "CancelActorIntentRequest");
            else if (cancellation == "new-generation") Set(f.Chat, "m_FormalResponseGeneration", 9999);
            else Set(f.Chat, "m_ChatSettings", new ChatSetting());
            f.Model.Reviews[0](new ActorIntentReviewDecision { Status = "adopted_action", Task = "follow",
                Target = "current_user", Coverage = "compound", Trigger = "none", Goal = f.Draft, Evidence = f.Draft }, "late-compound");
            Check(f.NoPlanOrHead && f.Queued() == "", "A stale compound review cannot gain planner authority: " + cancellation);
        }
        return new JObject{["passed"]=true,["checks"]=checks,["scope"]="real ChatSample entry/callbacks with controlled backend; no network, PCM or physical motion"};
    }
    static ActorIntentDecision NonAction()=>new ActorIntentDecision{Domain="dialogue",Kind="none",Action="none",Condition="none",Evidence="",Coverage="none"};
    static ActorIntentDecision Conflict(string draft)=>new ActorIntentDecision{Domain="dialogue",Kind="act",Action="none",Condition="none",Evidence=draft,Goal="解释当前情况",RequiresFullReview=true,ReviewReason="actor_domain_kind_mismatch"};
    static ActorIntentDecision Claim(string draft)=>new ActorIntentDecision{Domain="other_action",Kind="claim",Action="none",Condition="none",Evidence=draft,Goal="关闭灯"};
    static ActorIntentReviewDecision Review(string status,string draft)=>new ActorIntentReviewDecision{Status=status,Goal=status=="adopted_action"||status=="reported_action"?"关闭灯":"",Evidence=status=="adopted_action"||status=="reported_action"?draft:""};
    sealed class Fixture:IDisposable
    {
        readonly GameObject host;
        public readonly ChatSample Chat;
        public readonly ActorIntentReviewModel Model;
        public readonly string Draft;
        public FastLoopRuntime Runtime=>(FastLoopRuntime)Get(Chat,"m_FastLoop");
        public bool NoPlanOrHead=>Get(Chat,"m_ActorPlan")==null && Runtime.HeadTask==null;
        public Fixture(string draft)
        {
            Draft=draft;host=new GameObject("Actor intent semantic review fixture"){hideFlags=HideFlags.HideAndDontSave};
            Chat=host.AddComponent<ChatSample>();Model=host.AddComponent<ActorIntentReviewModel>();
            Set(Chat,"m_ChatSettings",new ChatSetting{m_ChatModel=Model});Set(Chat,"m_FastLoop",new FastLoopRuntime());
            Set(Chat,"m_FormalResponseGeneration",7101);Set(Chat,"m_WorkEpoch",7201);Set(Chat,"m_CurrentSpeechRoundId",7301);Set(Chat,"m_LastUserMsg","你好，现在是什么情况？");
            Set(Chat,"m_FirstReplyAttemptGeneration",-1);Set(Chat,"m_FormalTransportDispatchGeneration",-1);
            foreach(string n in new[]{"m_EnableActorIntentCompiler","m_EnableFastTaskLoop","m_EnableFirstReplyRouting","m_UseStreaming","m_IsVoiceMode","m_SceneTimePaused","m_RoundInnerCheckDone","m_RoundRepeatCheckDone"})Set(Chat,n,true);
            foreach(string n in new[]{"m_AgentRunning","m_AgentCurrentRoundIsTick","m_AgentRoundInFlight","m_UserSpeechActiveForAutonomy","m_SceneVisionApplicationPaused","m_FormalResponseInFlight","m_StreamComplete","m_EnableScreenVision","m_EnableSceneActions","m_LogAgentLoop","m_LogRawLLMOutput","m_LogStreamTimings"})Set(Chat,n,false);
            Set(Chat,"m_SentenceBuffer",new SpeechTextBuffer());Set(Chat,"m_AssistantHeardText",new StringBuilder());Set(Chat,"m_ChatHistory",new List<string>{"你好，现在是什么情况？"});
            Check((bool)Call(Chat,"TryDispatchFirstReply","你好，现在是什么情况？",null,7101,true,null),"real shared entry accepted");
            Model.Fast(new FastLoopDecision{Version=FastLoopProtocol.CurrentVersion,Route="reply",Candidate="none",Evidence="",Speech=draft,Language="zh"},"ok");
            Check(Model.Actor!=null,"real reply route enters unpublished-draft compiler");
        }
        public string Queued()=>string.Concat(((IEnumerable)Get(Chat,"m_PendingChunks")).Cast<object>().Select(v=>(string)Get(v,"Text")))+Get(Chat,"m_SentenceBuffer");
        public void Dispose()=>UnityEngine.Object.DestroyImmediate(host);
    }
    static object Get(object o,string n)=>o.GetType().GetField(n,Flags).GetValue(o);
    static void Set(object o,string n,object v)=>o.GetType().GetField(n,Flags).SetValue(o,v);
    static object Call(object o,string n,params object[] args)=>o.GetType().GetMethods(Flags).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(o,args);
}
public sealed class ActorIntentReviewModel:LLM,IFastLoopDecisionBackend,IActorIntentBackend,IActorIntentReviewBackend
{
    public Action<FastLoopDecision,string> Fast;
    public Action<ActorIntentDecision,string> Actor;
    public readonly List<Action<ActorIntentReviewDecision,string>> Reviews=new List<Action<ActorIntentReviewDecision,string>>();
    public Action DuringReview;
    public bool ThrowReview;
    public int FormalRequests;
    public bool SupportsFastLoopDecisions=>true;
    public bool SupportsActorIntents=>true;
    public override bool SupportsFirstReplyRouting=>true;
    public override bool SupportsRoleDecisionMessages=>true;
    public void PostFastLoopDecision(string context,string currentUser,Action<FastLoopDecision,string> callback,ConversationTrace trace=null,string imageDataUrl=null)=>Fast=callback;
    public void CancelFastLoopDecision(){}
    public void PostActorIntent(string context,string draft,string currentUser,Action<ActorIntentDecision,string> callback,ConversationTrace trace=null)=>Actor=callback;
    public void PostActorIntentReview(string context,string draft,string currentUser,Action<ActorIntentReviewDecision,string> callback,ConversationTrace trace=null){if(ThrowReview)throw new InvalidOperationException("controlled");Reviews.Add(callback);DuringReview?.Invoke();}
    public void PostActorConditionSignal(string context,string currentUser,Action<string,string> callback,ConversationTrace trace=null){}
    public void CancelActorIntent(){}
    public override void PostRoleDecisionMsg(string prompt,string image,Action<bool,string,string> callback,ConversationTrace trace){FormalRequests++;}
    public override void PostSpeechStreamForStage(string message,Action<SpeechText> speech,Action<string> complete,string image,bool history,FormalResponseStage stage){FormalRequests++;}
}
#endif
