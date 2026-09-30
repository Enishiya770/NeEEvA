using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NeEEvA.Perception;

// Host only the dependencies of the production review-handoff partial. The actual
// coordinator and cancellation eligibility run unchanged; no Unity graphics/network.
namespace NeEEvA.Perception
{
    public sealed class SceneVisionObservation { public bool Fresh; }
}
public partial class ChatSample
{
    private sealed class SingingGoal { }
    private sealed class Settings { public object m_ChatModel = new object(); }
    private Settings m_ChatSettings = new Settings();
    private sealed class AutonomyIntentDecision { }
    private int m_AutonomyProbeGeneration, m_WorkEpoch, m_FormalResponseGeneration;
    private bool m_AgentRunning = true, m_AgentGracefulShutdownPending, isActiveAndEnabled = true;
    private bool m_UserSpeechActiveForAutonomy, m_AgentRoundInFlight, m_FormalResponseInFlight;
    private bool IsAISpeaking, IsVoiceOutputPlaying, m_AutonomyProbeInFlight;
    private bool IsFreshSceneObservation(SceneVisionObservation observation) => observation?.Fresh == true;
    private string BuildPerceptionFrame(string trigger) => "current-frame";
    private void StoreDeferredAutonomyThought(AutonomyIntentDecision decision) { }
    private Action<SceneVisionObservation> captureCallback;
    private Action deadline;
    private int captureCount, dispatchCount;
    private SceneVisionObservation dispatchedImage;
    private string dispatchedContext, dispatchedFrame;
    private void RequestSceneObservation(string purpose, Action<SceneVisionObservation> callback)
    { captureCount++; captureCallback = callback; }
    private object SceneObservationDeadline(Func<bool> completed, Action timeout)
    { deadline = () => { if (!completed()) timeout(); }; return null; }
    private void StartCoroutine(object coroutine) { }
    private void DispatchPreparedAgentTickCore(string trigger, string frame, AutonomyIntentDecision decision,
        bool continuation, SceneVisionObservation observation, bool useObservation, string result)
    { dispatchCount++;dispatchedImage=observation;dispatchedContext=result;dispatchedFrame=frame; }

    public void RunHandoffChecks(Action<bool,string> check)
    {
        void Reset()
        {
            m_ReviewedDecisionHandoff?.Cancel("fixture-reset");
            captureCount=dispatchCount=0;captureCallback=null;deadline=null;Events.Clear();
            m_AgentRunning=true;m_AgentGracefulShutdownPending=false;isActiveAndEnabled=true;
            m_UserSpeechActiveForAutonomy=m_AgentRoundInFlight=m_FormalResponseInFlight=false;
            IsAISpeaking=IsVoiceOutputPlaying=m_AutonomyProbeInFlight=false;
            m_ResponseTrace=new ConversationTrace("test",Events.Add);
        }
        string End() => (string)JObject.Parse(Events.Last())["fields"]["outcome"];
        var old=new SceneVisionObservation { Fresh=false };
        var fresh=new SceneVisionObservation { Fresh=true };
        Reset();DispatchPreparedAgentTick("goal-review","old-frame",sceneObservation:old,useSceneObservation:true);
        check(captureCount==1&&dispatchCount==0&&m_AutonomyProbeInFlight,"expired review captures once, no new LLM review");
        var callback=captureCallback;callback(fresh);callback(fresh);
        check(dispatchCount==1&&ReferenceEquals(dispatchedImage,fresh),"one fresh formal dispatch despite duplicate callback");
        check(!m_AutonomyProbeInFlight&&m_ReviewedDecisionHandoff==null,"refresh releases scheduling gate");
        check(dispatchedFrame.StartsWith("current-frame"),"old perception frame not reused");
        check(End()=="dispatched_fresh","successful handoff terminal");

        Reset();DispatchPreparedAgentTick("review","frame",sceneObservation:old,useSceneObservation:true);
        captureCallback(null);
        check(dispatchCount==1&&dispatchedImage==null&&End()=="dispatched_without_image","missing image allows text decision without old visual authorization");
        check(captureCount==1,"failed refresh does not restart review loop");
        Reset();DispatchPreparedAgentTick("review","frame",sceneObservation:old,useSceneObservation:true);
        captureCallback(old);
        check(dispatchCount==1&&dispatchedImage==null,"still stale refresh cannot forward stale pixels");

        foreach(var invalidation in new[]{"user","epoch","formal","model","shutdown","disabled"})
        {
            Reset();DispatchPreparedAgentTick("review","frame",sceneObservation:old,useSceneObservation:true);
            if(invalidation=="user")m_UserSpeechActiveForAutonomy=true;
            if(invalidation=="epoch")m_WorkEpoch++;
            if(invalidation=="formal")m_FormalResponseGeneration++;
            if(invalidation=="model")m_ChatSettings.m_ChatModel=new object();
            if(invalidation=="shutdown")m_AgentGracefulShutdownPending=true;
            if(invalidation=="disabled")isActiveAndEnabled=false;
            captureCallback(fresh);
            check(dispatchCount==0&&End()=="owner_changed","late refresh must not speak after "+invalidation);
        }
        Reset();DispatchPreparedAgentTick("review","frame",sceneObservation:old,useSceneObservation:true);
        deadline();captureCallback(fresh);
        check(dispatchCount==0&&!m_AutonomyProbeInFlight&&End()=="refresh_timeout","lost capture callback has bounded terminal and no late dispatch");

        Reset();DispatchPreparedAgentTick("old","frame",sceneObservation:old,useSceneObservation:true);
        var oldCallback=captureCallback;
        DispatchPreparedAgentTick("new","frame",sceneObservation:old,useSceneObservation:true);
        oldCallback(fresh);
        check(dispatchCount==0&&m_AutonomyProbeInFlight,"superseded callback cannot clear new handoff gate");
        captureCallback(fresh);check(dispatchCount==1&&!m_AutonomyProbeInFlight,"new owner alone dispatches");

        Reset();var result=BuildCancelledGoalReplyContext();
        DispatchPreparedAgentTick("cancel","frame",sceneObservation:fresh,useSceneObservation:true,reviewedOutcomeContext:result);
        check(dispatchCount==1&&captureCount==0&&dispatchedContext==result,"cancel result reaches fresh formal decision even without work continuation");
        check(result.Contains("没有外放")&&result.Contains("不得重放旧承诺"),"cancel context describes undelivered old speech, not replay permission");
    }
}

public static class ClosureRegression
{
    private static int count;
    private static void Check(bool ok,string label) { count++;if(!ok)throw new Exception(label); }
    public static void Run()
    {
        new ChatSample().RunHandoffChecks(Check);
        Check(ReviewedDecisionHandoff<object>.NeedsCancellationReply(true,"cancel","cancelled",true,true,2,-1),"withheld cancelled user reply gets one result decision");
        Check(!ReviewedDecisionHandoff<object>.NeedsCancellationReply(true,"cancel","cancelled",true,true,2,2),"no duplicate cancellation reply");
        Check(!ReviewedDecisionHandoff<object>.NeedsCancellationReply(false,"cancel","cancelled",true,true,2,-1),"old already reviewed goal cannot revive reply");
        Check(!ReviewedDecisionHandoff<object>.NeedsCancellationReply(true,"cancel","pending",true,true,2,-1),"pending cancellation cannot authorize result reply");
        Check(!ReviewedDecisionHandoff<object>.NeedsCancellationReply(true,"cancel","cancelled",false,true,2,-1),"already independent speech needs no forced repeat");
        Check(!ReviewedDecisionHandoff<object>.NeedsCancellationReply(true,"cancel","cancelled",true,false,2,-1),"another speech round cannot revive withheld reply");
        Check(!ReviewedDecisionHandoff<object>.NeedsCancellationReply(true,"create","approved",true,true,2,-1),"approval alone never fabricates a sing action");
        var facts=BoundaryDecisionObservation.Fields("take_turn",.9f,false,true,true,false,false,4,4,100,200,3,2,.7f);
        Check(!(bool)facts["sameText"]&&(bool)facts["pendingText"]&&!(bool)facts["freshAudioCoverage"],"consumer evidence distinguishes simultaneous blockers");
        Check((int)facts["sourceAudioMs"]==100&&(int)facts["currentAudioMs"]==200,"coverage has source/current ownership");
        Console.WriteLine("PASS: "+count+" actual-handoff/cancellation/EOU-observation checks");
    }
}
