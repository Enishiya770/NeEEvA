using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Production first-motion scheduling and formal dispatch, with controlled engine
/// result fields in the existing isolated real-room fixture. No model/native/ASR/TTS request.</summary>
public static class RoomMotionReadinessRegression
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    public static JObject LastReport { get; private set; }
    private sealed class Saved { public object target; public FieldInfo field; public object value; }
    private sealed class Context
    {
        public ChatSample chat; public ArdyRoomDialogueBridge bridge; public ArdyRoomLocomotionController controller;
        public RoomMotionReadinessProbeModel model; public ArdyActionConstraintLedger ledger;
        public readonly List<Saved> saved=new List<Saved>(); public int checks,operation,generation,turn,epoch;
        public void Check(bool ok,string reason){checks++;if(!ok)throw new InvalidOperationException(reason);}
        public void Set(object target,string field,object value)
        {
            var info=target.GetType().GetField(field,Flags);if(info==null)throw new MissingFieldException(target.GetType().Name,field);
            if(!saved.Exists(x=>ReferenceEquals(x.target,target)&&x.field==info))saved.Add(new Saved{target=target,field=info,value=info.GetValue(target)});
            info.SetValue(target,value);
        }
        public void Arm(string intent,string dispatch,string action,bool assigned=false)
        {
            generation=(int)Get(chat,"m_FormalResponseGeneration")+1;
            epoch=(int)Get(chat,"m_WorkEpoch");turn=ledger.CurrentUserTurn;
            Set(chat,"m_FormalResponseGeneration",generation);Set(chat,"m_UserSpeechActiveForAutonomy",false);
            Set(chat,"m_RoomTaskGeneration",generation);Set(chat,"m_RoomTaskIntent",intent);
            Set(chat,"m_RoomTaskDispatch",dispatch);Set(chat,"m_RoomTaskActionId",action);
            Set(chat,"m_FormalResponseInFlight",true);Set(chat,"m_AgentRoundInFlight",true);
            Set(chat,"m_StreamComplete",false);Set(chat,"m_TTSSenderDone",false);
            Set(bridge,"actionId",action);Set(bridge,"phase","preparing");Set(bridge,"reason","controlled pending first-motion result");
            Set(bridge,"activeAction",false);Set(bridge,"followActive",false);Set(bridge,"hasControllerOperation",false);
            Set(controller,"<ExecutionState>k__BackingField",ArdyRoomExecutionState.Preparing);
            Set(controller,"<StreamStepCount>k__BackingField",0);Set(controller,"streamSessionId",null);
            Set(controller,"<Stage>k__BackingField","stream-prebuffering");
            if(assigned)Adopt();
        }
        public void Adopt()
        {
            operation++;
            Set(controller,"<OperationRevision>k__BackingField",operation);Set(controller,"revision",operation);
            Set(bridge,"operationRevision",operation);Set(bridge,"hasControllerOperation",true);
        }
        public void Dispatch()
        {
            int expected=generation;
            Action formal=()=>Formal(expected);
            Call(chat,"DispatchRoomMotionReplyWhenReady",bridge,generation,epoch,turn,formal);
        }
        public void Formal(int expected)=>Call(chat,"DispatchFormalStream","Controlled first-motion readiness probe",null,expected,false,
            "[controlled current execution phase] "+bridge.Phase+Call(chat,"RoomMotionReadinessReplyContext"),true);
        public bool PipelineReleased => !(bool)Get(chat,"m_FormalResponseInFlight")&&!(bool)Get(chat,"m_AgentRoundInFlight")&&
            (bool)Get(chat,"m_StreamComplete")&&(bool)Get(chat,"m_TTSSenderDone");
        public bool PipelinePending => (bool)Get(chat,"m_FormalResponseInFlight")&&(bool)Get(chat,"m_AgentRoundInFlight")&&
            !(bool)Get(chat,"m_StreamComplete")&&!(bool)Get(chat,"m_TTSSenderDone");
        public void Accept()
        {Set(controller,"<StreamStepCount>k__BackingField",1);Set(controller,"streamSessionId","controlled-audited-prefix");}
        public void Restore()
        {
            // Invalidate any suspended waiter before restoring fields from the real fixture.
            var revision=chat.GetType().GetField("m_RoomMotionReadinessRevision",Flags);
            revision.SetValue(chat,(int)revision.GetValue(chat)+1);
            chat.GetType().GetField("<IsWaitingForRoomMotionReadiness>k__BackingField",Flags).SetValue(chat,false);
            for(int i=saved.Count-1;i>=0;i--)saved[i].field.SetValue(saved[i].target,saved[i].value);
        }
    }

    public static IEnumerator RunChecks(ChatSample chat,ArdyRoomDialogueBridge bridge,string output)
    {
        if(!Application.isBatchMode||!Application.isPlaying||Application.dataPath.IndexOf("unity-naturalness-validation",StringComparison.OrdinalIgnoreCase)<0)
            throw new InvalidOperationException("Motion readiness checks require the existing isolated real-room Play fixture.");
        string directory=Path.Combine(output,"room-motion-readiness");Directory.CreateDirectory(directory);
        LastReport=new JObject{["status"]="running",["httpRequests"]=0,["nativeRequests"]=0,["cases"]=new JArray(),
            ["scope"]="Production scheduling gate and DispatchFormalStream, with explicit controlled pending/accepted/failed/cancelled engine fields on the isolated actual room bridge and a recording-only provider. No trajectory submission, model reply, audio or semantic success is simulated."};
        var c=new Context{chat=chat,bridge=bridge,controller=bridge.Controller,ledger=(ArdyActionConstraintLedger)Get(chat,"m_ActionConstraints")};
        c.operation=c.controller.OperationRevision+1000;
        var modelHost=new GameObject("Dormant readiness transport recorder");modelHost.SetActive(false);
        c.model=modelHost.AddComponent<RoomMotionReadinessProbeModel>();
        var settings=(ChatSetting)Get(chat,"m_ChatSettings");var originalModel=settings.m_ChatModel;
        var stack=new Stack<IEnumerator>();
        try
        {
            c.Check(bridge.IsConnected&&!bridge.ReservesBody&&!bridge.IsFollowing,"Actual room bridge is idle and connected.");
            foreach(string field in new[]{"m_RoomSpeechGuardGeneration","m_RoomTaskFormatError",
                "m_StagedFormalObservation","m_StagedFormalObservationEpoch","m_StagedFormalObservationGeneration",
                "m_StagedObservationReceipt","m_ComposedObservationReceipt","m_ComposedObservationGeneration",
                "m_FormalSceneObservation","m_FormalSceneObservationGeneration","m_PendingSceneFormalCaptureGeneration",
                "m_SceneVisionState","m_LatencyFillerGeneration","m_EouFillerGeneration","m_EouFillerScheduled",
                "<RoomMotionReadinessStatus>k__BackingField"}) c.Set(chat,field,Get(chat,field));
            settings.m_ChatModel=c.model;stack.Push(Cases(c));
            while(stack.Count>0)
            {
                object yielded=null;
                try
                {
                    while(stack.Count>0)
                    {
                        var routine=stack.Peek();if(!routine.MoveNext()){(stack.Pop()as IDisposable)?.Dispose();continue;}
                        if(routine.Current is IEnumerator nested){stack.Push(nested);continue;}
                        yielded=routine.Current;break;
                    }
                }
                catch(Exception error){LastReport["status"]="failed";LastReport["error"]=error.ToString();throw;}
                if(stack.Count>0)yield return yielded;
            }
            LastReport["status"]="passed";
        }
        finally
        {
            while(stack.Count>0)(stack.Pop()as IDisposable)?.Dispose();
            c.Restore();settings.m_ChatModel=originalModel;
            LastReport["checks"]=c.checks;LastReport["formalRequests"]=c.model.requests;
            UnityEngine.Object.DestroyImmediate(modelHost);
            if((string)LastReport["status"]=="running")LastReport["status"]="failed";
            File.WriteAllText(Path.Combine(directory,"report.json"),LastReport.ToString()+"\n");
        }
    }

    private static IEnumerator Cases(Context c)
    {
        foreach(var spec in new[]{new[]{"follow","follow-submitted"},new[]{"approach","attempt-submitted"},new[]{"approach-object","object-attempt-submitted"}})
        {
            c.Arm(spec[0],spec[1],"controlled-readiness-"+spec[0]);int before=c.model.requests;
            c.Dispatch();yield return Wait(.1f);
            c.Check(c.chat.IsWaitingForRoomMotionReadiness&&c.model.requests==before,"Pending "+spec[0]+" does not enqueue the long formal prefill.");
            c.Adopt();yield return Wait(.05f);
            c.Check(c.model.requests==before,"Assigning the deferred real operation is not itself motion readiness.");
            c.Accept();yield return Wait(.1f);
            c.Check(c.model.requests==before+1&&!c.chat.IsWaitingForRoomMotionReadiness,"The first actually adopted audited result releases one formal dispatch: "+spec[0]);
            c.Check(c.model.lastContext.Contains("first-audited-stream-prefix-ready"),"Formal prompt receives the current readiness scope, not an arrival claim.");
            yield return Wait(.05f);c.Check(c.model.requests==before+1,"A completed wait cannot dispatch twice.");
            Add(spec[0]+" pending→deferred-operation→audited-prefix→formal");
        }
        foreach(string terminal in new[]{"failed","cancelled","follow-waiting"})
        {
            c.Arm("follow","follow-submitted","controlled-"+terminal);int before=c.model.requests;c.Dispatch();
            c.Set(c.bridge,"phase",terminal);yield return Wait(.1f);
            c.Check(c.model.requests==before+1&&!c.chat.IsWaitingForRoomMotionReadiness,"A truthful terminal/waiting result releases the reply without movement: "+terminal);
            c.Check(c.model.lastContext.Contains(terminal),"Reply context is rebuilt after the result: "+terminal);Add(terminal);
        }
        c.Arm("follow","follow-submitted","old-user-round");int count=c.model.requests;c.Dispatch();
        c.Set(c.ledger,"<CurrentUserTurn>k__BackingField",c.ledger.CurrentUserTurn+1);yield return Wait(.1f);
        c.Check(c.model.requests==count&&!c.chat.IsWaitingForRoomMotionReadiness,"New user turn invalidates the old wait without dispatching old speech.");
        c.Arm("none","not-submitted","");c.Dispatch();
        c.Check(c.model.requests==count+1,"New ordinary reply never waits on the old action.");Add("new user supersedes waiter");

        c.Arm("follow","follow-submitted","old-before-stop");count=c.model.requests;c.Dispatch();
        c.Arm("stop-moving","stop-submitted","new-stop");c.Dispatch();yield return Wait(.1f);
        c.Check(c.model.requests==count+1&&!c.chat.IsWaitingForRoomMotionReadiness,"Stop reply bypasses startup waiting and old completion stays revoked.");Add("stop bypass");
        c.Check(c.PipelinePending,"The old waiter cannot clear a newer stop reply's active pipeline.");

        c.Arm("follow","follow-submitted","replaced-operation",true);count=c.model.requests;c.Dispatch();
        c.Set(c.controller,"<OperationRevision>k__BackingField",c.operation+1);yield return Wait(.1f);
        c.Check(c.model.requests==count&&!c.chat.IsWaitingForRoomMotionReadiness,"Another controller operation cannot release this action's old speech.");Add("operation replacement");
        c.Check(c.PipelineReleased&&c.controller.OperationRevision==c.operation+1,"Own-generation cancellation releases unsent pipeline flags without cancelling the replacement move.");
        c.Arm("follow","follow-submitted","provider-invalidated",true);count=c.model.requests;c.Dispatch();
        var settings=(ChatSetting)Get(c.chat,"m_ChatSettings");settings.m_ChatModel=null;yield return Wait(.1f);
        c.Check(c.model.requests==count&&c.PipelineReleased,"Provider invalidation cannot leave a never-dispatched formal request latched.");
        settings.m_ChatModel=c.model;Add("provider owner invalidation releases pipeline");
        c.Arm("follow","follow-submitted","epoch-invalidated",true);count=c.model.requests;c.Dispatch();
        c.Set(c.chat,"m_WorkEpoch",(int)Get(c.chat,"m_WorkEpoch")+1);yield return Wait(.1f);
        c.Check(c.model.requests==count,"Work-epoch invalidation suppresses an obsolete wait.");Add("work epoch cancellation");
        c.Arm("follow","follow-already-active","ongoing-follow");count=c.model.requests;c.Dispatch();
        c.Check(c.model.requests==count+1&&!c.chat.IsWaitingForRoomMotionReadiness,"Talking during an existing follow is not delayed.");Add("ongoing follow conversation");
        c.model.shared=false;c.Arm("follow","follow-submitted","independent-model");count=c.model.requests;c.Dispatch();
        c.Check(c.model.requests==count+1,"An independent provider does not acquire a shared-feature wait.");c.model.shared=true;

        yield return ActualObservationCases(c);

        c.Arm("follow","follow-submitted","bounded-no-result",true);count=c.model.requests;float at=Time.realtimeSinceStartup;c.Dispatch();
        float deadline=at+24f;while(c.model.requests==count&&Time.realtimeSinceStartup<deadline)yield return null;
        c.Check(c.model.requests==count+1&&Time.realtimeSinceStartup-at>=22f,"No-result waiting ends at the explicit bounded budget.");
        c.Check(c.chat.RoomMotionReadinessStatus=="bounded-wait-expired-still-preparing"&&c.bridge.Phase=="preparing"&&
            c.controller.OperationRevision==c.operation,"Timeout releases speech truthfully without cancelling/retrying the still-pending move.");
        Add("22 second bound leaves movement unchanged");
    }
    private static IEnumerator ActualObservationCases(Context c)
    {
        c.Arm("follow","follow-submitted","actual-result-image",true);
        c.Set(c.chat,"m_AgentRunning",true);c.Set(c.chat,"m_EnableScreenVision",true);c.Set(c.chat,"m_AgentEyesOpen",true);
        c.model.visual=true;
        SceneVisionObservation source=null;
        Call(c.chat,"RequestSceneObservation","readiness-regression-source",new Action<SceneVisionObservation>(x=>source=x));
        yield return Until(()=>source!=null,5f,"Actual source observation");
        c.Check(source.HasImage,"The readiness fixture captures the actual isolated character view.");
        Call(c.chat,"BindFormalSceneObservation",source,c.generation);
        int before=c.model.requests;c.Dispatch();yield return Wait(.1f);c.Accept();
        yield return Until(()=>c.model.requests>before,5f,"Result-view observation before formal dispatch");
        var refreshed=(SceneVisionObservation)Get(c.chat,"m_FormalSceneObservation");
        c.Check(refreshed?.HasImage==true&&refreshed.CaptureId!=source.CaptureId&&refreshed.Frame>source.Frame&&
            c.model.lastImage==refreshed.ImageDataUrl,"After a real wait, production formal dispatch receives a newly captured actual room image.");
        ((JArray)LastReport["cases"]).Add(new JObject{["name"]="actual result-view refresh",["sourceCaptureId"]=source.CaptureId,
            ["resultCaptureId"]=refreshed.CaptureId,["sourceFrame"]=source.Frame,["resultFrame"]=refreshed.Frame});

        c.Arm("follow","follow-submitted","cancelled-result-image",true);before=c.model.requests;
        var capture=c.chat.SceneVisionCapture;c.Set(capture,"nextCaptureAt",Time.realtimeSinceStartup+30f);
        c.Dispatch();c.Accept();
        yield return Until(()=>(int)Get(c.chat,"m_PendingSceneFormalCaptureGeneration")==c.generation,3f,"Pending result capture owner");
        c.Check((int)Get(c.chat,"m_PendingSceneFormalCaptureGeneration")==c.generation&&c.model.requests==before,
            "Accepted motion can wait for an actual result-view frame before speech dispatch.");
        // A newer request can be dispatched outside this gate (ordinary continuation).
        // The old capture may complete, but must not clear or overwrite that new owner.
        c.Arm("none","not-submitted","");c.Set(c.chat,"m_PendingSceneFormalCaptureGeneration",-1);
        Call(c.chat,"BindFormalSceneObservation",refreshed,c.generation);c.Formal(c.generation);
        c.Set(capture,"nextCaptureAt",Time.realtimeSinceStartup);
        yield return Until(()=>!c.chat.IsWaitingForRoomMotionReadiness&&capture.LatestObservation?.HasImage==true&&
            capture.LatestObservation.CaptureId!=refreshed.CaptureId,5f,"Old readback completing after a newer formal generation");
        c.Check(c.model.requests==before+1&&c.PipelinePending,"A stale result-view waiter cannot clear a newer formal request, even without a new readiness revision.");
        c.Check(ReferenceEquals(Get(c.chat,"m_FormalSceneObservation"),refreshed)&&
            (int)Get(c.chat,"m_FormalSceneObservationGeneration")==c.generation,"A cancelled old readback cannot overwrite the new generation's bound observation.");
        Add("new generation revokes pending actual result capture");

        c.Arm("follow","follow-submitted","unavailable-result-image",true);before=c.model.requests;
        Call(c.chat,"BindFormalSceneObservation",refreshed,c.generation);c.model.visual=false;c.Dispatch();yield return Wait(.05f);c.Accept();
        yield return Until(()=>c.model.requests>before,1f,"Unavailable result-view reply");
        c.Check(Get(c.chat,"m_FormalSceneObservation")==null&&string.IsNullOrEmpty(c.model.lastImage),
            "Unavailable result capture clears the old image and formal speech receives no image attachment.");
        Add("result capture unavailable does not reuse old pixels");
    }
    private static IEnumerator Until(Func<bool> ready,float limit,string label)
    {float deadline=Time.realtimeSinceStartup+limit;while(!ready()&&Time.realtimeSinceStartup<deadline)yield return null;if(!ready())throw new TimeoutException(label);}
    private static IEnumerator Wait(float seconds){float end=Time.realtimeSinceStartup+seconds;while(Time.realtimeSinceStartup<end)yield return null;}
    private static void Add(string name)=>((JArray)LastReport["cases"]).Add(name);
    private static object Get(object target,string field)=>target.GetType().GetField(field,Flags).GetValue(target);
    private static object Call(object target,string method,params object[] args)
    {try{return target.GetType().GetMethod(method,Flags).Invoke(target,args);}catch(TargetInvocationException error){throw error.InnerException??error;}}
}

public sealed class RoomMotionReadinessProbeModel : LLM
{
    public bool shared=true,visual;public int requests;public string lastContext,lastImage;
    public override bool RequiresRoomMotionReadinessBeforeSpeech=>shared;
    public override bool SupportsVisualDecisionMessages=>visual;
    public override void PostSpeechStream(string message,Action<SpeechText> delta,Action<string> complete,
        string imageDataUrl=null,bool recordAssistantHistory=true)
    {requests++;lastContext=RequestContext??"";lastImage=imageDataUrl;}
}
