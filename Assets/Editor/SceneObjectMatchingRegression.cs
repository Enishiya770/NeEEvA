using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Production candidate matching with controlled model callbacks and real room RGB.
/// Tests referent repair separately from model reliability; exports one untouched live probe wire.</summary>
public static class SceneObjectMatchingRegression
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static int checks, httpCalls;
    private static string directory;
    public static JObject LastReport { get; private set; }
    private sealed class Pending
    {
        internal bool done;
        internal NevaSceneVisionIntegrationModel.Request request;
        internal RoomTaskProtocol.Decision decision;
    }
    public static IEnumerator RunChecks(ChatSample chat,ArdyRoomDialogueBridge bridge,NevaSceneVisionIntegrationModel model,string output)=>Run(chat,bridge,model,output,false);
    public static IEnumerator RunLiveChecks(ChatSample chat,ArdyRoomDialogueBridge bridge,NevaSceneVisionIntegrationModel model,string output)=>Run(chat,bridge,model,output,true);
    private static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,NevaSceneVisionIntegrationModel model,string output,bool live)
    {
        if(!Application.isBatchMode||!Application.isPlaying||Application.dataPath.IndexOf("unity-naturalness-validation",StringComparison.OrdinalIgnoreCase)<0)
            throw new InvalidOperationException("Object matching requires the existing isolated real-room fixture.");
        checks=0;httpCalls=0;directory=Path.Combine(output,live?"scene-object-matching-live":"scene-object-matching");Directory.CreateDirectory(directory);
        LastReport=new JObject { ["status"]="running",["cases"]=new JArray(),["httpRequests"]=0,["nativeRequests"]=0,
            ["scope"]="Actual room lamp, real character RGB and source-linked candidate panels through production matching and object inspection. Only matching replies are controlled. Tests do not establish real Qwen accuracy. The observe-closer case exercises production matching directly and never dispatches locomotion. One untouched production request is exported separately for a bounded real-model probe." };
        LastReport["firstMatchUsesLiveQwen"]=live;
        if(live)LastReport["scope"]+=" First wrong-pixel repair uses one actual shared-Qwen response forwarded unchanged; the remaining boundary cases keep controlled callbacks.";
        bool awareness=(bool)Get(chat,"m_EnableSceneObjectAwareness"),matching=(bool)Get(chat,"m_EnableSceneObjectTargetMatching");
        var stack=new Stack<IEnumerator>();
        try
        {
            Set(chat,"m_EnableSceneObjectAwareness",true);Set(chat,"m_EnableSceneObjectTargetMatching",true);
            Check(bridge!=null&&bridge.IsConnected&&!bridge.ReservesBody&&!bridge.IsFollowing,"Actual room bridge is idle and connected.");
            Check(SceneObjectLampCatalog.EnsureRegistered(bridge.environmentRoot,out var lamp,out string reason)&&lamp.TryValidate(out reason),"Actual lamp registered: "+reason);
            LastReport["lampEntityId"]=lamp.EntityId;LastReport["initialPowerState"]=JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));
            stack.Push(Exercise(chat,bridge,model,lamp,live));
            while(stack.Count>0)
            {
                object value=null;
                try
                {
                    while(stack.Count>0)
                    {
                        var routine=stack.Peek();
                        if(!routine.MoveNext()){(routine as IDisposable)?.Dispose();stack.Pop();continue;}
                        if(routine.Current is IEnumerator nested){stack.Push(nested);continue;}
                        value=routine.Current;break;
                    }
                }
                catch(Exception error){LastReport["status"]="failed";LastReport["error"]=error.ToString();throw;}
                if(stack.Count>0)yield return value;
            }
            LastReport["status"]="passed";
        }
        finally
        {
            while(stack.Count>0)(stack.Pop() as IDisposable)?.Dispose();
            Call(chat,"CancelSceneAction","matching-fixture-finished");Call(chat,"ClearScheduledAgentWake");
            Set(chat,"m_EnableSceneObjectAwareness",awareness);Set(chat,"m_EnableSceneObjectTargetMatching",matching);
            Set(chat,"m_FormalResponseInFlight",false);Set(chat,"m_AgentRoundInFlight",false);
            if((string)LastReport["status"]=="running")LastReport["status"]="failed";
            LastReport["checks"]=checks;WriteReport();
        }
    }
    private static IEnumerator Exercise(ChatSample chat,ArdyRoomDialogueBridge bridge,NevaSceneVisionIntegrationModel model,SceneObjectLightSwitch lamp,bool live)
    {
        Vector3 root=bridge.avatar.transform.position;Quaternion rotation=bridge.avatar.transform.rotation;
        int operation=bridge.Controller.OperationRevision;var power=JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));
        SceneVisionObservation source=null;Pending pending=null;
        yield return Prepare(chat);yield return Capture(chat,"wrong-pixel-source",value=>source=value);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source,lamp.EntityId,out var lampPixel,out string reason),"Actual small lamp initially has current opaque pixels: "+reason);
        var wrong=FindNeighbor(chat.SceneVisionCapture,source,lampPixel);
        Check(wrong!=null&&wrong.EntityId!=lamp.EntityId,"Wrong initial pixel is an actual nearby ordinary room renderer.");
        yield return Begin(chat,model,source,PixelWire(source,wrong.ImagePoint,"请检查电视柜上发光的小装饰灯。"),value=>pending=value);
        var offered=chat.LastSceneObjectCandidateView;
        var actualLamp=offered.Candidates.FirstOrDefault(value=>value.entityId==lamp.EntityId);
        Check(actualLamp!=null&&offered.Candidates.Any(value=>value.entityId==wrong.EntityId),"Actual matcher sees both wrong background and intended lamp candidates.");
        var matchInput=JObject.Parse(pending.request.Input);
        Check((string)matchInput["initialGeometryEntityId"]==wrong.EntityId&&(string)matchInput["candidates"]["captureId"]==source.CaptureId,
            "Production input retains wrong geometric selection separately from current candidate interpretation.");
        ExportLiveProbe(pending.request,offered,actualLamp.candidateId,source);
        if(live)yield return PostExactMatch(pending.request,source,actualLamp.candidateId);
        else pending.request.Complete(true,Reply("matched",actualLamp.candidateId,"The small lamp candidate matches the requested ornament; the initial surface is background."),"controlled-match");
        yield return Complete(chat,pending,"wrong-pixel-repaired");
        Check((string)Fact(chat)["phase"]=="observed"&&(string)Fact(chat)["entityId"]==lamp.EntityId&&
            (string)Fact(chat)["targetResolutionSource"]=="candidate-reselected"&&(bool?)Fact(chat)["semanticTargetConfirmed"]==true,
            "Wrong initial pixel is corrected to the matched real lamp before actual new-view inspection.");
        Check(HasHistory(chat,lamp.EntityId),"Real successful inspection establishes the lamp's objective seen-object history.");

        yield return Prepare(chat);yield return Capture(chat,"ordinary-inspection-source",value=>source=value);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source,lamp.EntityId,out lampPixel,out reason),"Lamp remains visible before background history case: "+reason);
        wrong=FindNeighbor(chat.SceneVisionCapture,source,lampPixel);Check(wrong!=null,"Nearby ordinary reference is visible again.");
        yield return Begin(chat,model,source,PixelWire(source,wrong.ImagePoint,"请检查灯旁边的电视柜表面。"),value=>pending=value);
        var ordinary=chat.LastSceneObjectCandidateView.Candidates.FirstOrDefault(value=>value.entityId==wrong.EntityId);
        Check(ordinary!=null,"The actual ordinary reference is offered for its own explicit inspection.");
        pending.request.Complete(true,Reply("matched",ordinary.candidateId,"This candidate is the requested neighboring furniture surface."),"controlled-match");
        yield return Complete(chat,pending,"inspect-ordinary-reference");
        Check((string)Fact(chat)["phase"]=="observed"&&HasHistory(chat,wrong.EntityId)&&HasHistory(chat,lamp.EntityId),"Ordinary inspection preserves independently observed lamp history.");
        string ordinaryId=wrong.EntityId;

        yield return Prepare(chat);yield return Capture(chat,"explicit-reselection-source",value=>source=value);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source,ordinaryId,out wrong,out reason),"Old ordinary identity is still genuinely trackable: "+reason);
        var reselect=PixelWire(source,wrong.ImagePoint,"不是这个柜面，请重新找电视柜上发光的小装饰灯。");reselect["targetSelection"]="reselect";
        yield return Begin(chat,model,source,reselect,value=>pending=value);
        Check((bool?)JObject.Parse(pending.request.Input)["explicitReselection"]==true,"Explicit user correction reaches matching independently of geometric identity validity.");
        actualLamp=chat.LastSceneObjectCandidateView.Candidates.FirstOrDefault(value=>value.entityId==lamp.EntityId);Check(actualLamp!=null,"New target review still offers the actual lamp.");
        pending.request.Complete(true,Reply("matched",actualLamp.candidateId,"The corrected intention refers to the small lamp, not the prior furniture selection."),"controlled-match");
        yield return Complete(chat,pending,"explicit-reselection-preserves-history");
        Check((string)Fact(chat)["entityId"]==lamp.EntityId&&HasHistory(chat,lamp.EntityId)&&HasHistory(chat,ordinaryId),"Reselection repairs the referent without erasing previously seen entities.");

        yield return Prepare(chat);yield return Capture(chat,"unknown-reference-source",value=>source=value);
        var unknown=new JObject { ["intent"]="inspect-object",["origin"]="user",["evidence"]="请重新辨认当前看见的物体。",
            ["entityTarget"]=new JObject { ["captureId"]=source.CaptureId,["entityId"]="not-a-known-entity-session-prefix" } };
        yield return Begin(chat,model,source,unknown,value=>pending=value);
        var repaired=chat.LastSceneObjectCandidateView.Candidates.OrderBy(value=>(value.imagePoint-new Vector2(.5f,.5f)).sqrMagnitude).First();
        Check(!string.IsNullOrEmpty(repaired.entityId)&&(string)JObject.Parse(pending.request.Input)["requestedEntityId"]=="not-a-known-entity-session-prefix",
            "Unknown entity handle is evidence of failed referent selection, while candidates come from the current real frame.");
        pending.request.Complete(true,Reply("matched",repaired.candidateId,"This currently visible candidate matches the bounded inspection request."),"controlled-match");
        yield return Complete(chat,pending,"unknown-reference-repaired-from-current-frame");
        Check((string)Fact(chat)["phase"]=="observed"&&(string)Fact(chat)["entityId"]==repaired.entityId,
            "Unknown long identifier can be repaired only via an offered current candidate and a new real view.");

        foreach(var mode in new[]{"uncertain","different-target","invalid-candidate","cancel-late"})
        {
            yield return Prepare(chat);yield return Capture(chat,mode+"-source",value=>source=value);
            Check(chat.SceneVisionCapture.TryFindObservedObject(source,lamp.EntityId,out lampPixel,out reason),"Negative case uses actual current lamp pixels: "+reason);
            bool inspection=mode=="uncertain"||mode=="different-target";
            var decision=PixelWire(source,lampPixel.ImagePoint,inspection?"请检查电视柜上的小装饰灯。":"请把这个小灯的开关切换一下。",inspection?"inspect-object":"set-object-state");
            if(!inspection)decision["objectState"]=lamp.ReadState().on?"off":"on";
            yield return Begin(chat,model,source,decision,value=>pending=value);
            if(mode=="uncertain")
            {
                pending.request.Complete(true,Reply("uncertain","none","The intended small object cannot be identified confidently from this view."),"controlled-match");
                yield return Complete(chat,pending,mode);
                Check((string)Fact(chat)["phase"]=="rejected"&&(bool?)Fact(chat)["semanticTargetConfirmed"]!=true&&
                    (bool?)Fact(chat)["needsCloserObservation"]==true,"Uncertain recognition requests better observation and cannot execute the device operation.");
            }
            else if(mode=="different-target")
            {
                actualLamp=chat.LastSceneObjectCandidateView.Candidates.First(value=>value.entityId==lamp.EntityId);
                pending.request.Complete(true,Reply("different-target",actualLamp.candidateId,"A visible alternative needs a separate fresh target decision; do not execute it now."),"controlled-match");
                yield return Complete(chat,pending,mode);
                var review=(JObject)Call(chat,"SceneObjectTargetContext");
                Check((string)Fact(chat)["phase"]=="rejected"&&(bool?)Fact(chat)["semanticTargetConfirmed"]!=true&&
                    (bool?)review["needsTargetReselection"]==true&&(string)review["proposedEntityId"]==lamp.EntityId,
                    "A different-target proposal is retained only as a fresh-decision cue, never executed as a match.");
            }
            else if(mode=="invalid-candidate")
            {
                pending.request.Complete(true,Reply("matched","c99","Invalid candidate must be rejected even when the model calls it matched."),"controlled-match");
                yield return Complete(chat,pending,mode);
                Check((string)Fact(chat)["phase"]=="rejected"&&(bool?)Fact(chat)["semanticTargetConfirmed"]!=true,
                    "A model-selected ID outside the exact offered short list cannot authorize execution.");
            }
            else
            {
                actualLamp=chat.LastSceneObjectCandidateView.Candidates.First(value=>value.entityId==lamp.EntityId);
                Call(chat,"CancelSceneAction","matching-late-callback-fixture");
                string cancelled=Fact(chat).ToString();
                pending.request.Complete(true,Reply("matched",actualLamp.candidateId,"Late callback must no longer own this action."),"controlled-late");
                yield return null;yield return null;
                Check(Fact(chat).ToString()==cancelled&&(bool)Get(chat,"m_SceneActionActive")==false,
                    "Cancelling during matching revokes late callbacks without replacing the cancellation fact.");
                AddCase(chat,mode,pending.decision);
            }
            Check(JToken.DeepEquals(power,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))),"Negative matching case leaves the actual lamp state unchanged: "+mode);
        }

        // Exercise the production semantic-review coroutine directly. This is intentionally
        // not a navigation test: no waypoint/motion request or synthetic arrival is issued.
        yield return Prepare(chat);yield return Capture(chat,"observe-closer-reference-source",value=>source=value);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source,lamp.EntityId,out lampPixel,out reason),"Lamp image supplies a local observation reference: "+reason);
        wrong=FindNeighbor(chat.SceneVisionCapture,source,lampPixel);Check(wrong!=null,"Larger visible furniture is available as an observation reference.");
        var closer=PixelWire(source,wrong.ImagePoint,"小灯看不清，先靠近电视柜观察它。","approach-object");closer["targetPurpose"]="observe-closer";
        Call(chat,"RecordAcceptedMotionUserTurn",(string)closer["evidence"]);
        Check(RoomTaskProtocol.TryDecision(closer.ToString(),(string)closer["evidence"],false,out var closerDecision),"Observation-subgoal decision is accepted by production parser.");
        var factType=typeof(ChatSample).GetNestedType("SceneActionFact",Flags);var semanticFact=Activator.CreateInstance(factType,true);
        int before=model.Matches.Count;bool reviewDone=false,allow=true;SceneObservedObject reference=null;
        var routine=(IEnumerator)Call(chat,"MatchSceneObjectTarget",closerDecision,source,wrong,semanticFact,new Func<bool>(()=>allow),
            new Action<SceneObservedObject,string>((value,failure)=>{reference=value;reviewDone=true;}));
        chat.StartCoroutine(routine);
        yield return Wait(()=>model.Matches.Count==before+1,5,"Observe-closer production matcher request");
        var referenceRequest=model.Matches.Last();var referenceInput=JObject.Parse(referenceRequest.Input);
        ordinary=chat.LastSceneObjectCandidateView.Candidates.First(value=>value.entityId==wrong.EntityId);
        Check((string)referenceInput["targetPurpose"]=="observe-closer"&&((string)referenceInput["originalGoal"]).Contains("小灯")&&
            ((string)referenceInput["instructionScope"]).Contains("REFERENCE"),"Matching distinguishes the larger observation reference from the still-unconfirmed original small target.");
        referenceRequest.Complete(true,Reply("matched",ordinary.candidateId,"The furniture is the larger reference for obtaining a better view, not identification of the small lamp."),"controlled-match");
        yield return Wait(()=>reviewDone,3,"Observe-closer semantic completion");allow=false;
        var semantic=JObject.Parse(JsonUtility.ToJson(semanticFact));
        Check(reference!=null&&reference.EntityId==wrong.EntityId&&(string)semantic["targetPurpose"]=="observe-closer"&&
            (bool?)semantic["stateChanged"]!=true,"Matched observation reference retains its limited purpose and creates no device receipt.");
        LastReport["observationReferenceReview"]=new JObject { ["scope"]="Direct production matcher only; no navigation or final-target identification claim.",
            ["input"]=referenceInput,["semanticFact"]=semantic };

        var pendingGoal=(JObject)((JObject)Call(chat,"SceneObjectTargetContext"))["observationGoal"];
        string goalId=(string)pendingGoal?["goalId"],originalGoal=(string)pendingGoal?["originalGoal"];
        Check(!string.IsNullOrEmpty(goalId)&&(string)pendingGoal["status"]=="pending"&&
            originalGoal==(string)closer["evidence"]&&(bool?)pendingGoal["needsOriginalTargetRecheck"]==true,
            "Matched observe-closer reference creates an independent pending original observation goal, not a resolved small target.");
        LastReport["observationGoalInitiallyPending"]=pendingGoal.DeepClone();

        // A new user turn does not silently inherit the earlier goal, and an arbitrary goal ID
        // must fail before spending another matcher request or moving attention.
        yield return Prepare(chat);yield return Capture(chat,"unknown-goal-source",value=>source=value);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source,lamp.EntityId,out lampPixel,out reason),"Goal rejection has real current lamp pixels: "+reason);
        var unknownGoal=PixelWire(source,lampPixel.ImagePoint,"请检查这个物体的新目标。");unknownGoal["observationGoalId"]="observation-goal-not-in-this-context";
        yield return RejectWithoutMatch(chat,model,source,unknownGoal,false,"observation-goal-reference-not-pending","unknown-goal-does-not-spend-matcher");
        var historicalGoal=(JObject)((JObject)Call(chat,"SceneObjectTargetContext"))["observationGoal"];
        Check((string)historicalGoal["goalId"]==goalId&&(string)historicalGoal["status"]=="historical"&&
            (bool?)historicalGoal["needsOriginalTargetRecheck"]==false,"New user turn marks the former pending observation goal historical.");

        yield return Prepare(chat);yield return Capture(chat,"autonomous-old-goal-source",value=>source=value);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source,lamp.EntityId,out lampPixel,out reason),"Superseded goal refusal still has a valid current picture: "+reason);
        var oldAutonomy=PixelWire(source,lampPixel.ImagePoint,"自主继续刚才未完成的小灯观察。");
        oldAutonomy["origin"]="autonomous";oldAutonomy["observationGoalId"]=goalId;
        yield return RejectWithoutMatch(chat,model,source,oldAutonomy,true,"newer-user-turn-superseded-observation-goal","autonomy-cannot-revive-superseded-goal");

        yield return Prepare(chat);yield return Capture(chat,"omitted-goal-source",value=>source=value);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source,lamp.EntityId,out lampPixel,out reason),"Omitted-goal fixture begins with actual opaque lamp pixels: "+reason);
        wrong=FindNeighbor(chat.SceneVisionCapture,source,lampPixel);Check(wrong!=null,"A separate actual furniture surface is available for the new request.");
        const string newGoal="请检查这个柜面，与先前观察目标无关。";
        yield return Begin(chat,model,source,PixelWire(source,wrong.ImagePoint,newGoal),value=>pending=value);
        var omissionInput=JObject.Parse(pending.request.Input);
        Check((string)omissionInput["originalGoal"]==newGoal&&omissionInput["observationGoalId"]?.Type==JTokenType.Null,
            "Omitting observationGoalId keeps the new user goal instead of implicitly inheriting historical motivation.");
        ordinary=chat.LastSceneObjectCandidateView.Candidates.First(value=>value.entityId==wrong.EntityId);
        pending.request.Complete(true,Reply("matched",ordinary.candidateId,"The current independent request matches the furniture surface."),"controlled-match");
        yield return Complete(chat,pending,"omitted-goal-is-independent");
        var stillHistorical=(JObject)((JObject)Call(chat,"SceneObjectTargetContext"))["observationGoal"];
        Check((string)stillHistorical["goalId"]==goalId&&(string)stillHistorical["status"]=="historical",
            "A successful independent inspection does not resolve or overwrite the historical original goal.");

        yield return Prepare(chat);yield return Capture(chat,"explicit-original-goal-source",value=>source=value);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source,lamp.EntityId,out lampPixel,out reason),"Original-target continuation starts from current lamp pixels: "+reason);
        var continuation=PixelWire(source,lampPixel.ImagePoint,"现在继续确认先前想看清的那盏小灯。");continuation["observationGoalId"]=goalId;
        yield return Begin(chat,model,source,continuation,value=>pending=value);
        var continuationInput=JObject.Parse(pending.request.Input);
        Check((string)continuationInput["observationGoalId"]==goalId&&(string)continuationInput["originalGoal"]==originalGoal&&
            (string)continuationInput["currentRequest"]==(string)continuation["evidence"],
            "Explicit user continuation reuses the exact original goal while preserving the current request for semantic checking.");
        Check((string)((JObject)Call(chat,"SceneObjectTargetContext"))["observationGoal"]["status"]!="resolved",
            "Offering a goal ID and image does not resolve the goal before matching and fresh physical confirmation.");
        actualLamp=chat.LastSceneObjectCandidateView.Candidates.First(value=>value.entityId==lamp.EntityId);
        pending.request.Complete(true,Reply("matched",actualLamp.candidateId,"This visible lamp matches the explicitly resumed original observation goal."),"controlled-match");
        yield return Complete(chat,pending,"explicit-original-goal-resolved-after-new-view");
        var resolved=(JObject)((JObject)Call(chat,"SceneObjectTargetContext"))["observationGoal"];
        Check((string)Fact(chat)["phase"]=="observed"&&(string)resolved["goalId"]==goalId&&
            (string)resolved["status"]=="resolved"&&(bool?)resolved["needsOriginalTargetRecheck"]==false&&
            (string)resolved["resolvedEntityId"]==lamp.EntityId&&(string)resolved["resolvedCaptureId"]==(string)Fact(chat)["resultCaptureId"]&&
            (string)resolved["resolvedCaptureId"]!=source.CaptureId,
            "Original observation goal resolves only after matched lamp identity is confirmed in the actual new head-view image.");
        LastReport["observationGoalResolved"]=resolved.DeepClone();

        yield return Prepare(chat);yield return Capture(chat,"uncertain-bearing-source",value=>source=value);
        Check(chat.SceneVisionCapture.TryFindObservedObject(source,lamp.EntityId,out lampPixel,out reason),"Uncertain bearing uses a real visible image region: "+reason);
        yield return Begin(chat,model,source,PixelWire(source,lampPixel.ImagePoint,"先看看那个发光的区域。","look-at"),value=>pending=value);
        pending.request.Complete(true,Reply("uncertain","none","The region is visible but its intended object identity remains uncertain."),"controlled-match");
        yield return Complete(chat,pending,"uncertain-look-is-bearing-only");
        Check((string)Fact(chat)["phase"]=="observed"&&(string)Fact(chat)["focusKind"]=="image-region-bearing"&&
            (bool?)Fact(chat)["objectConfirmed"]!=true&&(bool?)Fact(chat)["semanticTargetConfirmed"]!=true&&
            (string)Fact(chat)["resultCaptureId"]!=source.CaptureId&&
            chat.SceneVisionCapture.LatestObservation.CaptureId==(string)Fact(chat)["resultCaptureId"],
            "Uncertain look-at changes the real view and captures again, without claiming a recognized entity.");
        Check(bridge.avatar.transform.position==root&&bridge.avatar.transform.rotation==rotation&&bridge.Controller.OperationRevision==operation&&
            !bridge.ReservesBody&&JToken.DeepEquals(power,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))),
            "All matching/inspection cases leave character root, locomotion operation and actual lamp power unchanged.");
    }

    private static IEnumerator Prepare(ChatSample chat)
    {
        yield return (IEnumerator)typeof(SceneObjectReferenceRegression).GetMethod("Prepare",Flags).Invoke(null,new object[]{chat});
        Set(chat,"m_EnableSceneObjectTargetMatching",true);
    }
    private static IEnumerator Capture(ChatSample chat,string purpose,Action<SceneVisionObservation> completed)
    {
        SceneVisionObservation value=null;Call(chat,"RequestSceneObservation",purpose,new Action<SceneVisionObservation>(x=>value=x));
        yield return Wait(()=>value!=null,5,"Matching real source capture");Check(value.HasImage&&value.HasSurfaceGrounding,"Real RGB and IDs available.");
        Call(chat,"BindFormalSceneObservation",value,(int)Get(chat,"m_FormalResponseGeneration"));completed(value);
    }
    private static IEnumerator Begin(ChatSample chat,NevaSceneVisionIntegrationModel model,SceneVisionObservation source,JObject wire,Action<Pending> completed)
    {
        string command=(string)wire["evidence"];Call(chat,"RecordAcceptedMotionUserTurn",command);
        Check(RoomTaskProtocol.TryDecision(wire.ToString(),command,false,out var decision),"Controlled matching case uses production decision parser.");
        Set(chat,"m_FormalResponseInFlight",true);Set(chat,"m_AgentRoundInFlight",true);
        int before=model.Matches.Count;var value=new Pending {decision=decision};
        Call(chat,"BeginSceneAction",decision,source,(int)Get(chat,"m_FormalResponseGeneration"),new Action(()=>value.done=true));
        yield return Wait(()=>model.Matches.Count>before||value.done,5,"Actual candidate matching request");
        Check(model.Matches.Count==before+1,"Exactly one candidate review is issued before object execution.");
        value.request=model.Matches.Last();Check(!string.IsNullOrEmpty(value.request.Wire)&&!string.IsNullOrEmpty(value.request.Image),"Production review wire contains the real candidate image.");
        completed(value);
    }
    private static IEnumerator Complete(ChatSample chat,Pending value,string name)
    {
        yield return Wait(()=>value.done,16,"Matching action completion: "+name);
        Call(chat,"ClearScheduledAgentWake");Set(chat,"m_FormalResponseInFlight",false);Set(chat,"m_AgentRoundInFlight",false);AddCase(chat,name,value.decision);
    }
    private static IEnumerator RejectWithoutMatch(ChatSample chat,NevaSceneVisionIntegrationModel model,SceneVisionObservation source,
        JObject wire,bool autonomous,string expectedReason,string name)
    {
        string command=(string)wire["evidence"];
        if(!autonomous)Call(chat,"RecordAcceptedMotionUserTurn",command);
        Check(RoomTaskProtocol.TryDecision(wire.ToString(),command,autonomous,out var decision),"Goal-reference rejection uses the production decision parser.");
        Set(chat,"m_FormalResponseInFlight",true);Set(chat,"m_AgentRoundInFlight",true);
        int before=model.Matches.Count;bool done=false;
        Call(chat,"BeginSceneAction",decision,source,(int)Get(chat,"m_FormalResponseGeneration"),new Action(()=>done=true));
        yield return Wait(()=>done,3,"Pre-matcher observation-goal refusal: "+name);
        Check(model.Matches.Count==before&&(string)Fact(chat)["phase"]=="rejected"&&
            ((string)Fact(chat)["reason"]??"").Contains(expectedReason),"Invalid or superseded observation-goal reference is refused before any matcher call.");
        Call(chat,"ClearScheduledAgentWake");Set(chat,"m_FormalResponseInFlight",false);Set(chat,"m_AgentRoundInFlight",false);AddCase(chat,name,decision);
    }
    private static JObject PixelWire(SceneVisionObservation source,Vector2 point,string command,string intent="inspect-object")=>new JObject {
        ["intent"]=intent,["origin"]="user",["evidence"]=command,
        ["visualTarget"]=new JObject { ["captureId"]=source.CaptureId,["x"]=point.x,["y"]=point.y,["label"]=command } };
    private static string Reply(string verdict,string id,string reason)=>new JObject { ["verdict"]=verdict,["candidateId"]=id,["reason"]=reason }.ToString();
    private static SceneObservedObject FindNeighbor(NevaSceneVisionCapture capture,SceneVisionObservation source,SceneObservedObject target)
    {
        foreach(float radius in new[]{.025f,.045f,.065f,.09f})foreach(var direction in new[]{Vector2.left,Vector2.right,Vector2.down,Vector2.up})
        {
            Vector2 point=target.ImagePoint+direction*radius;
            if(capture.TryResolveObservedObject(source,point,out var value,out _)&&value.VisibleConfirmed&&value.EntityId!=target.EntityId&&
                value.Root.GetComponent<SceneObjectLightSwitch>()==null)return value;
        }
        return null;
    }
    private static void ExportLiveProbe(NevaSceneVisionIntegrationModel.Request request,SceneObjectCandidateView view,string expected,SceneVisionObservation source)
    {
        string path=Path.Combine(directory,"match-live-inputs");Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path,"wrong-pixel-lamp.request.json"),request.Wire);
        File.WriteAllText(Path.Combine(path,"candidates.metadata.json"),view.MetadataJson);
        File.WriteAllBytes(Path.Combine(path,"candidates.png"),Convert.FromBase64String(view.ImageDataUrl.Substring(view.ImageDataUrl.IndexOf(',')+1)));
        File.WriteAllText(Path.Combine(path,"manifest.json"),new JObject {
            ["scope"]="Untouched production matching request with actual isolated room RGB/candidate contours. Expected choice is separate test metadata, never added to model wire. No HTTP performed by this test.",
            ["cases"]=new JArray(new JObject { ["name"]="wrong-pixel-lamp",["requestFile"]="wrong-pixel-lamp.request.json",["expectedVerdict"]="matched",
                ["expectedCandidateId"]=expected,["captureId"]=source.CaptureId,["source"]="actual-room-candidate-frame" }) }.ToString());
    }
    private static IEnumerator PostExactMatch(NevaSceneVisionIntegrationModel.Request pending,SceneVisionObservation source,string expected)
    {
        Check(++httpCalls==1,"Explicit live matching is limited to one exact production wire POST, without retries.");
        LastReport["httpRequests"]=httpCalls;
        const string endpoint="http://127.0.0.1:8080/v1/chat/completions";
        float started=Time.realtimeSinceStartup;
        var record=new JObject { ["endpoint"]=endpoint,["sourceCaptureId"]=source.CaptureId,["wireUnmodified"]=true,
            ["sourceAgeSecondsAtSubmit"]=started-source.CapturedAtRealtime,["expectedCandidateId"]=expected };
        LastReport["liveMatch"]=record;WriteReport();
        using(var request=new UnityWebRequest(endpoint,"POST"))
        {
            request.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(pending.Wire));request.downloadHandler=new DownloadHandlerBuffer();
            request.timeout=11;request.redirectLimit=0;request.SetRequestHeader("Content-Type","application/json");
            var operation=request.SendWebRequest();
            while(!operation.isDone)
            {
                if(Time.realtimeSinceStartup-started>12){request.Abort();throw new TimeoutException("Single matching request exceeded its existing fresh-observation budget; no retry.");}
                yield return null;
            }
            string response=request.downloadHandler.text;record["httpStatus"]=request.responseCode;record["elapsedSeconds"]=Time.realtimeSinceStartup-started;
            Check(response!=null&&response.Length<=200000,"Live matching envelope remains bounded.");
            File.WriteAllText(Path.Combine(directory,"live-match-response.json"),response);
            Check(request.result==UnityWebRequest.Result.Success&&request.responseCode==200,"Exact live candidate matching POST succeeds.");
            object[] args={response,null,null};
            bool valid=(bool)typeof(ChatQW).GetMethod("TryReadRoomTaskCompletion",Flags).Invoke(null,args);
            record["completionValid"]=valid;record["completionStatus"]=(string)args[2];record["completionText"]=(string)args[1];
            WriteReport();Check(valid,"Live matching uses the production envelope parser without replacement: "+(string)args[2]);
            pending.Complete(valid,(string)args[1],(string)args[2]);
        }
    }
    private static bool HasHistory(ChatSample chat,string entity)=>(ObjectContext(chat)["knownObjectHistory"] as JArray)?.OfType<JObject>().Any(x=>(string)x["entityId"]==entity)==true;
    private static JObject ObjectContext(ChatSample chat)=>(JObject)JObject.Parse(chat.SceneActionContextJson)["objectContext"];
    private static JObject Fact(ChatSample chat)=>JObject.Parse(JsonUtility.ToJson(Get(chat,"m_SceneActionFact")));
    private static void AddCase(ChatSample chat,string name,RoomTaskProtocol.Decision decision)
    {((JArray)LastReport["cases"]).Add(new JObject { ["name"]=name,["decision"]=JObject.FromObject(decision),["fact"]=Fact(chat),["objectContext"]=ObjectContext(chat) });WriteReport();}
    private static IEnumerator Wait(Func<bool> ready,float seconds,string label)
    {float deadline=Time.realtimeSinceStartup+seconds;while(!ready()){if(Time.realtimeSinceStartup>deadline)throw new TimeoutException(label);yield return null;}}
    private static void Check(bool value,string reason){checks++;if(!value)throw new InvalidOperationException(reason);}
    private static void WriteReport()=>File.WriteAllText(Path.Combine(directory,"report.json"),LastReport.ToString());
    private static object Get(object target,string field)=>target.GetType().GetField(field,Flags).GetValue(target);
    private static void Set(object target,string field,object value)=>target.GetType().GetField(field,Flags).SetValue(target,value);
    private static object Call(object target,string name,params object[] args)
    {
        var method=target.GetType().GetMethods(Flags).Single(value=>value.Name==name&&value.GetParameters().Length==args.Length);
        try{return method.Invoke(target,args);}catch(TargetInvocationException error){throw error.InnerException??error;}
    }
}
