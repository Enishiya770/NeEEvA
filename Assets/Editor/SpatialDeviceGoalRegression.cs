using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Controlled semantic receipts plus actual opaque frames and registered
/// device readback exercise goal ownership/completion. This is not a model-accuracy eval.</summary>
public static class SpatialDeviceGoalRegression
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    private const string Request="Please turn off the small lamp on my left after finding it.";
    private static int checks;
    private static string directory;
    private static GameObject fixture;
    private static readonly List<Material> materials=new List<Material>();
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase)<0)
            throw new InvalidOperationException("Device goal regression requires the isolated real-room Play fixture.");
        checks=0; materials.Clear(); directory=Path.Combine(output,"spatial-device-goal"); Directory.CreateDirectory(directory);
        LastReport=new JObject { ["status"]="running", ["cases"]=new JArray(),
            ["scope"]="Production strict goal protocol and goal lifecycle, controlled independent semantic verdicts, actual rendered fixture lamps and real registered device state reads/writes. Substep outcome fixtures are explicitly synthetic and do not claim locomotion or model semantic accuracy. No user-scene/device edits, hidden inventory search, actor translation, speech or HTTP." };
        string[] fields={"m_EnableSceneActions","m_EnableSceneObjectAwareness","m_AgentRunning","m_EnableScreenVision","m_AgentEyesOpen","m_WorkEpoch","m_SpatialDeviceGoal",
            "m_WorkReviewOpen","m_WorkNoProgress","m_WorkStatus","m_WorkContinuations","m_WorkIntent","m_PendingSelfInspection",
            "m_SelectedSceneObject","m_SelectedSceneDevice","m_SceneResultDevice","m_SceneResultEnvironment","m_SceneResultAvatar","m_SceneObjectStateResult",
            "m_SpatialGoalTerminalReplyGeneration","m_SpatialGoalTerminalProof","m_RoomTaskIntent","m_SceneActionFact",
            "m_AnswerThenResumeObservation","m_AnswerThenResumeGoalId","m_AnswerThenResumeUserTurn","m_ReviewedGoalAnswerObligation"};
        var saved=fields.Select(name=>Get(chat,name)).ToArray();
        var actionOwners=(IDictionary)Get(chat,"m_SpatialGoalActions");var savedOwners=new List<DictionaryEntry>();
        var ownerEnumerator=actionOwners.GetEnumerator();while(ownerEnumerator.MoveNext())savedOwners.Add(ownerEnumerator.Entry);
        var actionOrder=(Queue<string>)Get(chat,"m_SpatialGoalActionOrder");var savedOrder=actionOrder.ToArray();
        var history=(IList)Get(chat,"m_VerifiedSpatialDeviceHistory");var savedHistory=history.Cast<object>().ToArray();history.Clear();
        var ledger=(ArdyActionConstraintLedger)Get(chat,"m_ActionConstraints");
        var settings=Get(chat,"m_ChatSettings");var savedModel=Get(settings,"m_ChatModel");
        string[] ledgerFields={"currentText","revision","<CurrentUserTurn>k__BackingField"};
        var ledgerSaved=ledgerFields.Select(name=>Get(ledger,name)).ToArray();
        var stack=new Stack<IEnumerator>();
        try
        {
            foreach(string name in fields.Take(5)) Set(chat,name,true);
            Check(bridge!=null&&bridge.IsConnected&&!bridge.ReservesBody&&!bridge.IsFollowing,"Real room/avatar is idle for isolated goal checks.");
            ProtocolChecks();
            actionOwners.Clear();actionOrder.Clear();
            Set(chat,"m_SpatialDeviceGoal",null); Set(chat,"m_WorkEpoch",(int)saved[5]+1); ledger.RecordUserTurn(Request);
            var capture=(NevaSceneVisionCapture)Call(chat,"EnsureSceneVisionCapture"); capture.SetObserving(true);
            Call(chat,"EnsureSceneObjectCatalog");
            stack.Push(Exercise(chat,bridge,capture,ledger));
            while(stack.Count>0)
            {
                object next=null;
                try
                {
                    while(stack.Count>0)
                    {
                        var routine=stack.Peek();
                        if(!routine.MoveNext()){(routine as IDisposable)?.Dispose();stack.Pop();continue;}
                        if(routine.Current is IEnumerator nested){stack.Push(nested);continue;}
                        next=routine.Current;break;
                    }
                }
                catch(Exception error){LastReport["status"]="failed";LastReport["error"]=error.ToString();throw;}
                if(stack.Count>0)yield return next;
            }
            LastReport["status"]="passed";
        }
        finally
        {
            while(stack.Count>0)(stack.Pop() as IDisposable)?.Dispose();
            Call(chat,"CancelSpatialGoal","isolated-goal-fixture-finished");
            if(fixture!=null)UnityEngine.Object.DestroyImmediate(fixture);
            foreach(var material in materials)if(material!=null)UnityEngine.Object.DestroyImmediate(material);
            materials.Clear();fixture=null;
            for(int i=0;i<fields.Length;i++)Set(chat,fields[i],saved[i]);
            for(int i=0;i<ledgerFields.Length;i++)Set(ledger,ledgerFields[i],ledgerSaved[i]);
            Set(settings,"m_ChatModel",savedModel);
            actionOwners.Clear();foreach(var entry in savedOwners)actionOwners.Add(entry.Key,entry.Value);
            actionOrder.Clear();foreach(string id in savedOrder)actionOrder.Enqueue(id);
            history.Clear();foreach(var item in savedHistory)history.Add(item);
            LastReport["checks"]=checks;File.WriteAllText(Path.Combine(directory,"report.json"),LastReport.ToString());
        }
    }

    private static RoomTaskProtocol.Decision GoalDecision(string text=Request,string state="off") => new RoomTaskProtocol.Decision {
        intent="turn-in-place",origin="user",evidence=text,
        deviceGoal=new RoomTaskProtocol.DeviceGoalProposal {operation="set",requestQuote=text,targetDescription="the small lamp on my left",desiredState=state}
    };

    private static void ProtocolChecks()
    {
        var wire=new JObject { ["intent"]="turn-in-place",["origin"]="user",["evidence"]=Request,
            ["viewChange"]=new JObject { ["captureId"]="current-frame",["yaw"]=40,["pitch"]=0 },
            ["deviceGoal"]=new JObject { ["operation"]="set",["requestQuote"]=Request,["targetDescription"]="the small lamp on my left",["desiredState"]="off" } };
        Check(RoomTaskProtocol.TryDecision(wire.ToString(),Request,false,out var decision,out _)&&decision.deviceGoal?.desiredState=="off","A turn prerequisite can retain the structured off goal without emitting a device action.");
        var noStep=(JObject)wire.DeepClone();noStep["intent"]="none";noStep.Remove("viewChange");
        Check(RoomTaskProtocol.TryDecision(noStep.ToString(),Request,false,out decision,out _)&&decision.deviceGoal!=null&&!decision.IsSpatial,"A cited no-step goal proposal retains its request for independent review.");
        var old=(JObject)wire.DeepClone();old.Remove("deviceGoal");
        Check(RoomTaskProtocol.TryDecision(old.ToString(),Request,false,out decision,out _)&&decision.deviceGoal==null,"Legacy decisions retain their prior meaning without an implicit goal.");
        var fabricated=(JObject)wire.DeepClone();fabricated["deviceGoal"]["requestQuote"]="An invented request";
        Check(!RoomTaskProtocol.TryDecision(fabricated.ToString(),Request,false,out _,out _),"A invented original quote is rejected before any review or action.");
        Check(!RoomTaskProtocol.TryDecision(wire.ToString(),Request,true,out _,out _),"An autonomous round cannot establish a user device goal.");
        var extra=(JObject)wire.DeepClone();extra["deviceGoal"]["entityId"]="hidden-inventory-id";
        Check(!RoomTaskProtocol.TryDecision(extra.ToString(),Request,false,out _,out _),"Proposal grammar cannot nominate a hidden entity or engine completion result.");
        var conflict=(JObject)wire.DeepClone();conflict["intent"]="set-object-state";conflict.Remove("viewChange");
        conflict["entityTarget"]=new JObject { ["captureId"]="current-frame",["entityId"]="known-id" };conflict["objectState"]="on";
        Check(!RoomTaskProtocol.TryDecision(conflict.ToString(),Request,false,out _,out _),"A state-changing step opposite to its proposed goal is rejected atomically.");
        var cancel=new JObject { ["intent"]="none",["origin"]="user",["evidence"]="Cancel that device task.",
            ["deviceGoal"]=new JObject { ["operation"]="cancel",["requestQuote"]="Cancel that device task." } };
        Check(RoomTaskProtocol.TryDecision(cancel.ToString(),"Cancel that device task.",false,out decision,out _)&&decision.deviceGoal.operation=="cancel","A structured exact-quote cancellation parses independently of language keywords.");
        cancel["deviceGoal"]["desiredState"]="off";
        Check(!RoomTaskProtocol.TryDecision(cancel.ToString(),"Cancel that device task.",false,out _,out _),"Cancellation cannot smuggle a new desired state.");
        Check(RoomTaskProtocol.TryDeviceGoalReview("{\"verdict\":\"approved\",\"reason\":\"Explicit device request\"}",out var review)&&review.IsApproved,"Closed independent-review JSON parses.");
        Check(!RoomTaskProtocol.TryDeviceGoalReview("{\"verdict\":\"approved\",\"reason\":\"x\",\"completed\":true}",out _),"Review cannot declare engine completion or extra authority.");
        Check(!RoomTaskProtocol.TryDeviceGoalReview("{\"verdict\":\"rejected\",\"verdict\":\"approved\",\"reason\":\"x\"}",out _),"Duplicate review keys cannot overwrite rejection.");
        Check(JObject.Parse(RoomTaskProtocol.DecisionSchema)["properties"]["deviceGoal"]!=null&&
            !RoomTaskProtocol.TryDeviceGoalUserUpdateReview("{\"verdict\":\"approved\",\"reason\":\"Missing update relation\"}",out _),
            "Device goal decisions are declared, and a legacy approval cannot authorize a cross-turn goal update.");
        var sources=new JArray(new JObject { ["sourceId"]="observed-device:actual-observation",["kind"]="observed-device",["entityId"]="seen-lamp" },
            new JObject { ["sourceId"]="operated-goal:actual-off-goal",["kind"]="operated-goal",["entityId"]="seen-lamp" });
        var typed=new JObject { ["verdict"]="approved",["reason"]="Current user independently requests this observed target off.",
            ["relation"]="new",["referentKind"]="observed-device",["referentSourceId"]="observed-device:actual-observation" };
        Check(RoomTaskProtocol.TryDeviceGoalReview(typed.ToString(),out review)&&review.HasTypedReferent&&
            RoomTaskProtocol.TryValidateListedDeviceReferent(review,sources,out _),"Typed observation source parses and selects one offered source without synthesizing a goal/entity pair.");
        Check(!RoomTaskProtocol.TryDeviceGoalUserUpdateReview(typed.ToString(),out _),"Typed history selection cannot replace the separate standing-goal update association.");
        var mixed=(JObject)typed.DeepClone();mixed["referentEntityId"]="injected-other-lamp";
        Check(!RoomTaskProtocol.TryDeviceGoalReview(mixed.ToString(),out _),"Typed history selection cannot smuggle a free entity ID.");
        var unknown=(JObject)typed.DeepClone();unknown["referentSourceId"]="observed-device:not-offered";
        Check(RoomTaskProtocol.TryDeviceGoalReview(unknown.ToString(),out review)&&
            !RoomTaskProtocol.TryValidateListedDeviceReferent(review,sources,out _),"A well-shaped but unlisted source is rejected against the exact request list.");
        var wrongKind=(JObject)typed.DeepClone();wrongKind["referentKind"]="operated-goal";
        Check(!RoomTaskProtocol.TryDeviceGoalReview(wrongKind.ToString(),out _),"An observation action source cannot be relabelled as an operated goal.");
        var missing=(JObject)typed.DeepClone();missing["referentSourceId"]="none";
        Check(!RoomTaskProtocol.TryDeviceGoalReview(missing.ToString(),out _),"A claimed same observed source cannot silently become an empty binding.");
        var unbound=(JObject)missing.DeepClone();unbound["referentKind"]="unbound";
        Check(RoomTaskProtocol.TryDeviceGoalReview(unbound.ToString(),out review)&&
            RoomTaskProtocol.TryValidateListedDeviceReferent(review,new JArray(),out _),"An explicitly unresolved/new target can remain a search goal without guessed identity.");
        var pendingTyped=(JObject)typed.DeepClone();pendingTyped["relation"]="clarify";pendingTyped["pendingClarificationId"]="actual-pending-request";
        Check(RoomTaskProtocol.TryPendingDeviceClarificationReview(pendingTyped.ToString(),out review)&&review.HasUserUpdateFields,
            "A combined pending-request review retains its pending ownership separately from a typed observed source.");
        Check(!RoomTaskProtocol.TryDeviceGoalProposalReview(typed.ToString(),out _),"A historical receipt without request assessment cannot route a live proposal.");
        foreach(string assessment in new[]{"requested","not-requested","target-unclear","uncertain"})
        {
            var classified=(JObject)typed.DeepClone();classified["requestAssessment"]=assessment;
            classified["verdict"]=assessment=="requested"?"approved":assessment=="not-requested"?"rejected":"uncertain";
            Check(RoomTaskProtocol.TryDeviceGoalProposalReview(classified.ToString(),out review)&&
                RoomTaskProtocol.RetainsPendingDeviceProposal(review)==(assessment=="target-unclear"),
                "Only a genuine unresolved target retains pending state: "+assessment);
            if(assessment!="requested") { classified["verdict"]="approved";Check(!RoomTaskProtocol.TryDeviceGoalProposalReview(classified.ToString(),out _),"Non-authorizing classification cannot approve a proposal: "+assessment); }
        }
        var wrongProposal=(JObject)typed.DeepClone();wrongProposal["requestAssessment"]="requested";wrongProposal["verdict"]="rejected";
        Check(RoomTaskProtocol.TryDeviceGoalProposalReview(wrongProposal.ToString(),out review)&&!RoomTaskProtocol.RetainsPendingDeviceProposal(review),
            "A wrong object/state interpretation does not turn a real request into the rejected draft's pending identity.");
        var updateWire=new JObject { ["verdict"]="approved",["reason"]="Controlled clear relation",["relation"]="clarify",["referentGoalId"]="goal",["referentEntityId"]="" };
        Check(!RoomTaskProtocol.TryDeviceGoalUserUpdateReview(updateWire.ToString(),out _),"A live update must separately classify its answering obligation.");
        foreach(string mode in new[]{"act","answer","answer-then-resume"})
        { updateWire["responseMode"]=mode;Check(RoomTaskProtocol.TryDeviceGoalUserUpdateReview(updateWire.ToString(),out _),"Clear relation accepts explicit response mode: "+mode); }
        updateWire["relation"]="cancel";
        Check(!RoomTaskProtocol.TryDeviceGoalUserUpdateReview(updateWire.ToString(),out _),"Cancellation cannot also silently resume the cancelled task after answering.");
        var typedUnion=JObject.Parse(RoomTaskProtocol.BuildTypedDeviceGoalReviewSchema(false,sources));
        Check(((JArray)typedUnion["oneOf"]).OfType<JObject>().All(branch=>
            (bool?)branch["additionalProperties"]==false&&((JArray)branch["required"]).Count==6&&branch["properties"]?["requestAssessment"]!=null),
            "Every local constrained-decoder branch is a complete six-field closed review object.");
        var typedSchema=(JObject)typedUnion["oneOf"][0];
        Check(typedSchema["properties"]["referentGoalId"]==null&&typedSchema["properties"]["referentEntityId"]==null&&
            ((JArray)typedSchema["properties"]["referentSourceId"]["enum"]).Values<string>().SequenceEqual(
                new[]{"none","observed-device:actual-observation","operated-goal:actual-off-goal"}),
            "Production grammar only offers this request's exact typed source choices and contains no free entity/goal pair.");
    }

    private static IEnumerator Exercise(ChatSample chat,ArdyRoomDialogueBridge bridge,NevaSceneVisionCapture capture,ArdyActionConstraintLedger ledger)
    {
        Check(capture.TryGetCurrentCameraPose(out Pose pose),"Actual calibrated camera pose is available.");
        fixture=new GameObject("Isolated reviewed device goals");fixture.transform.SetParent(bridge.environmentRoot,false);fixture.transform.SetPositionAndRotation(pose.position,pose.rotation);
        var lamp=Lamp("goal lamp",new Vector3(-.12f,-.02f,.6f));
        var other=Lamp("other lamp",new Vector3(.12f,-.02f,.6f));
        var decision=GoalDecision();
        decision.deviceGoal.targetDescription="unverified bright region inside the right window";
        Check(!Register(chat,decision,Request,"rejected")&&Context(chat).Value<string>("status")=="none","A rejected independent goal review creates no task.");
        Check(!Register(chat,decision,Request,"uncertain")&&Context(chat).Value<string>("status")=="none","An uncertain goal review creates no device authority.");
        Check(Register(chat,decision,Request,"approved"),"Approved current-user goal registers with room/avatar/work ownership.");
        string goalId=Context(chat).Value<string>("goalId");
        Check((bool)Call(chat,"SpatialGoalPreventsWorkClosure"),"An active pending target must prevent LLM work closure; actual goal="+Context(chat).ToString(Newtonsoft.Json.Formatting.None));
        Check(Context(chat).Property("boundEntityId")!=null&&(string)Context(chat)["boundEntityId"]==null,"Registration has not bound an unobserved entity; actual="+(string)Context(chat)["boundEntityId"]);
        Set(chat,"m_WorkReviewOpen",true);Set(chat,"m_WorkNoProgress",false);Set(chat,"m_WorkContinuations",0);Set(chat,"m_PendingSelfInspection","");
        var closure=Activator.CreateInstance(typeof(ChatSample).GetNestedType("AutonomyIntentDecision",Flags),true);Set(closure,"work_status","closed");Set(closure,"intent","Controlled premature closure");
        Call(chat,"ApplyWorkReviewDecision",closure);
        Check((string)Get(chat,"m_WorkStatus")!="closed"&&(string)Get(closure,"work_status")=="continue","The actual work-review gate rejects an LLM closed verdict while the structured goal is unsatisfied.");
        Check(Register(chat,decision,Request,"approved")&&Context(chat).Value<string>("goalId")==goalId,"The same reviewed proposal preserves its stable goal ID.");
        decision.deviceGoal.targetDescription="mutation after registration";
        Check(Context(chat).Value<string>("targetDescription")==Request && Context(chat).Value<string>("targetDescriptionSource").StartsWith("original-user-request"),
            "Even an approved proposal with a wrong window guess preserves the exact human lamp reference as its standing target.");decision=GoalDecision();
        decision.deviceGoal.targetDescription="unverified bright region inside the right window";
        Check(Register(chat,decision,Request,"approved")&&Context(chat).Value<string>("goalId")==goalId,
            "Copied private proposal provenance still supports stable idempotence after the provider mutates its own proposal.");
        SceneVisionObservation view=null;yield return Capture(capture,"both-lamps",value=>view=value);
        Check(capture.TryFindObservedObject(view,lamp.EntityId,out var seen,out string reason)&&seen.VisibleConfirmed,"Goal fixture lamp has real opaque pixels: "+reason);
        Check(capture.TryFindObservedObject(view,other.EntityId,out var seenOther,out reason)&&seenOther.VisibleConfirmed,"Different same-label lamp has distinct real opaque identity: "+reason);
        Call(chat,"ObserveSpatialGoalView",view,"goal-regression-search");Call(chat,"ObserveSpatialGoalView",view,"duplicate");
        Check(((JArray)Context(chat)["searchProgress"]["observedViews"]).Count==1,"Actual search view is recorded once with no duplicate capture callback.");
        var turn=Fact("turn-substep","turn-in-place",view);
        Start(chat,turn);Terminal(chat,turn,"observed");
        Check(Context(chat).Value<string>("status")=="searching"&&(bool)Call(chat,"SpatialGoalPreventsWorkClosure"),"Actual-view prerequisite outcome cannot satisfy a device state goal.");
        var exported=Context(chat);exported["searchProgress"]["completedSteps"][0]["phase"]="caller-mutated";
        Check((string)Context(chat)["searchProgress"]["completedSteps"][0]["phase"]=="observed","Exported progress is copied and cannot mutate the authoritative goal through shared JSON nodes.");
        var reference=Fact("reference-substep","approach-object",view);Set(reference,"targetPurpose","observe-closer");Start(chat,reference);
        Check(Confirmed(chat,reference,seenOther)&&(string)Context(chat)["boundEntityId"]==null,"A visible registered observation reference cannot bind the main target.");Terminal(chat,reference,"arrived");
        Check(Context(chat).Value<string>("status")=="searching","Controlled reference arrival remains a prerequisite, never device completion.");

        var inspect=Fact("main-inspect","inspect-object",view);Set(inspect,"entityId",lamp.EntityId);Start(chat,inspect);
        Check(Confirmed(chat,inspect,seen)&&Context(chat).Value<string>("boundEntityId")==lamp.EntityId,"Only a current direct-target semantic match plus registered device binds the real target.");
        Check((string)Context(chat)["targetDescription"]==Request && (string)Context(chat)["confirmedTarget"]?["entityId"]==lamp.EntityId &&
            (string)Context(chat)["confirmedTarget"]?["captureId"]==view.CaptureId,
            "Actual matched identity gets separate capture provenance without replacing or narrowing the human request.");
        Call(chat,"RememberSceneObject",seen,lamp);Terminal(chat,inspect,"observed");
        Check(Context(chat).Value<string>("status")=="searching"&&lamp.ReadState().on,"A fresh inspection with opposite actual state cannot complete the off objective.");
        var wrongState=new RoomTaskProtocol.Decision {intent="set-object-state",origin="user",objectState="on"};
        Check(!Step(chat,wrongState),"A bound goal rejects an opposite-state step before dispatch.");
        var wrongEntity=new RoomTaskProtocol.Decision {intent="set-object-state",origin="user",objectState="off",entityTarget=new RoomTaskProtocol.EntityTarget {captureId=view.CaptureId,entityId=other.EntityId}};
        Check(!Step(chat,wrongEntity),"A bound goal rejects explicit state changes to another entity.");
        Check(!Step(chat,new RoomTaskProtocol.Decision {intent="follow",origin="user"}),"A device prerequisite cannot silently switch to following the user.");
        var pixelSwap=Fact("pixel-other-device","set-object-state",view);Set(pixelSwap,"entityId",other.EntityId);Set(pixelSwap,"requestedState","off");Start(chat,pixelSwap);
        Check(!Confirmed(chat,pixelSwap,seenOther)&&other.ReadState().on,"Post-resolution gate rejects another real lamp before its state is touched.");Terminal(chat,pixelSwap,"rejected");
        Check(Context(chat).Value<string>("status")=="searching"&&((JArray)Context(chat)["failedCandidates"]).Count==1,"Failed candidate is bounded per-frame progress, without ending the first repair attempt.");
        var disabledMatch=Fact("missing-semantic-match","set-object-state",view);Set(disabledMatch,"semanticTargetConfirmed",false);Set(disabledMatch,"requestedState","off");Start(chat,disabledMatch);
        Check(!Confirmed(chat,disabledMatch,seen),"Device operation cannot borrow a bound goal when its current semantic matcher is disabled or unconfirmed.");Terminal(chat,disabledMatch,"rejected");
        AddCase("bound-goal-prerequisites-and-refusals",Context(chat));

        var apply=Fact("real-off-operation","set-object-state",view);Set(apply,"entityId",lamp.EntityId);Set(apply,"requestedState","off");Start(chat,apply);
        Check(lamp.TrySetPower("real-off-operation",false,out var result,out reason)&&result.changed&&!result.actual.on,"The registered fixture device really changes to off: "+reason);
        SceneVisionObservation after=null;yield return Capture(capture,"real-off-result",value=>after=value);
        Check(capture.TryFindObservedObject(after,lamp.EntityId,out seen,out reason)&&seen.VisibleConfirmed,"The same off device is opaque-visible in a new actual RGB frame: "+reason);
        Set(apply,"confirmedCaptureId",view.CaptureId);Set(apply,"resultCaptureId",after.CaptureId);Set(apply,"stateChanged",true);
        Check(Confirmed(chat,apply,seen),"The still-owned goal validates the same confirmed entity after its operation.");
        Call(chat,"RememberSceneObject",seen,lamp);
        Set(chat,"m_SceneResultDevice",lamp);Set(chat,"m_SceneResultEnvironment",bridge.environmentRoot);Set(chat,"m_SceneResultAvatar",bridge.avatar);
        var receipt=JObject.Parse(JsonUtility.ToJson(result));receipt["freshVisualObservation"]=true;receipt["resultCaptureId"]=after.CaptureId;receipt["visualConfirmation"]="same-entity-in-new-image";
        var unconfirmedReceipt=(JObject)receipt.DeepClone();unconfirmedReceipt["freshVisualObservation"]=false;Set(chat,"m_SceneObjectStateResult",unconfirmedReceipt);Terminal(chat,apply,"performed");
        Check(Context(chat).Value<string>("status")=="searching"&&!lamp.ReadState().on,"Desired actual state alone cannot satisfy a state action lacking its fresh visual result receipt.");
        // Retry uses a real already-correct device result and another physical
        // capture, rather than relabelling the preceding action's receipt.
        var proven=Fact("real-off-proof","set-object-state",after);Set(proven,"entityId",lamp.EntityId);Set(proven,"requestedState","off");Start(chat,proven);
        Check(lamp.TrySetPower("real-off-proof",false,out var alreadyResult,out reason)&&!alreadyResult.changed&&alreadyResult.completed,"A separate real device request reads back an already-correct state without another change: "+reason);
        SceneVisionObservation proofView=null;yield return Capture(capture,"already-off-result",value=>proofView=value);
        Check(capture.TryFindObservedObject(proofView,lamp.EntityId,out seen,out reason),"Already-correct result still obtains another real opaque view: "+reason);
        Call(chat,"RememberSceneObject",seen,lamp);Set(proven,"confirmedCaptureId",after.CaptureId);Set(proven,"resultCaptureId",proofView.CaptureId);
        receipt=JObject.Parse(JsonUtility.ToJson(alreadyResult));receipt["freshVisualObservation"]=true;receipt["resultCaptureId"]=proofView.CaptureId;receipt["visualConfirmation"]="same-entity-in-new-image";
        Set(chat,"m_SceneObjectStateResult",receipt);Terminal(chat,proven,"already-satisfied");
        Check(Context(chat).Value<string>("status")=="satisfied"&&(bool?)Context(chat)["completionEvidenceValid"]==true&&!(bool)Call(chat,"SpatialGoalPreventsWorkClosure"),"Only a matching owned terminal action plus actual same-entity fresh readback satisfies the state predicate.");
        Check((bool)Call(chat,"IsCurrentSpatialGoal",goalId)&&(string)Context(chat)["completionEvidence"]["entityId"]==lamp.EntityId,"Satisfied state retains owner validity for the runner's single terminal feedback.");
        AddCase("real-readback-satisfied",Context(chat));
        lamp.DeviceLight.enabled=true;
        Check(Context(chat).Value<string>("status")=="searching"&&(bool?)Context(chat)["completionEvidence"]["historicalOnly"]==true,"An external device mutation revokes current completion and marks the prior receipt historical.");
        lamp.DeviceLight.enabled=false;
        Check(Context(chat).Value<string>("status")=="searching","Restoring a component does not automatically resurrect a revoked completed proof.");
        SceneVisionObservation recheck=null;yield return Capture(capture,"readback-recheck",value=>recheck=value);
        Check(capture.TryFindObservedObject(recheck,lamp.EntityId,out seen,out reason),"A new real frame can re-establish the same off lamp after invalidation: "+reason);
        var already=Fact("inspect-already-off","inspect-object",recheck);Set(already,"entityId",lamp.EntityId);Start(chat,already);
        Check(Confirmed(chat,already,seen),"Fresh inspection reconfirms the same bound target.");Call(chat,"RememberSceneObject",seen,lamp);Terminal(chat,already,"observed");
        Check(Context(chat).Value<string>("status")=="satisfied"&&!lamp.ReadState().on&&JToken.DeepEquals(receipt,(JObject)Get(chat,"m_SceneObjectStateResult")),"A fresh direct-target inspection can satisfy already-correct state without rewriting power or replacing the device action receipt.");
        AddCase("inspect-already-correct-without-write",Context(chat));

        // The previous state-action receipt belongs to B, while goal completion
        // was genuinely established by a fresh inspection/readback of A above.
        Check(other.TrySetPower("actual-old-other-lamp-on",true,out var otherResult,out reason)&&otherResult.completed,"Different old-result lamp supplies a genuine registered device receipt: "+reason);
        SceneVisionObservation otherView=null;yield return Capture(capture,"old-other-device-result",value=>otherView=value);
        Check(capture.TryFindObservedObject(otherView,other.EntityId,out _,out reason),"Old-result device B is genuinely opaque-visible in its result frame: "+reason);
        var otherReceipt=JObject.Parse(JsonUtility.ToJson(otherResult));otherReceipt["freshVisualObservation"]=true;otherReceipt["resultCaptureId"]=otherView.CaptureId;
        Set(chat,"m_SceneResultDevice",other);Set(chat,"m_SceneObjectStateResult",otherReceipt);Set(chat,"m_SceneActionFact",already);
        Set(chat,"m_RoomTaskIntent","none");Set(chat,"m_SpatialGoalTerminalReplyGeneration",(int)Get(chat,"m_FormalResponseGeneration"));
        Check(Context(chat).Value<string>("status")=="satisfied"&&(string)((JObject)Get(chat,"m_SceneObjectStateResult"))["entityId"]==other.EntityId,"Inspected goal A remains truly satisfied while the retained previous operation receipt is a different device B.");
        var oldDeviceStamp=((JToken)Call(chat,"SceneObjectSpeechStamp")).DeepClone();
        var completionStamp=((JToken)Call(chat,"SpatialGoalCompletionSpeechStamp")).DeepClone();
        var speechSnapshot=Call(chat,"CaptureRoomSpeechSnapshot",bridge,otherView);
        var dependencies=Call(chat,"ResolveRoomSpeechDependencies","none","none");
        var actionDependency=Enum.Parse(dependencies.GetType(),"SceneAction");
        Check((Convert.ToInt32(dependencies)&Convert.ToInt32(actionDependency))!=0,"Final device-goal speech forces scene-action evidence even if the model calls it no-fact ordinary speech.");
        lamp.DeviceLight.enabled=true;
        var changed=(List<string>)Call(chat,"ChangedRoomSpeechFacts",speechSnapshot,dependencies);
        Check(JToken.DeepEquals(oldDeviceStamp,(JToken)Call(chat,"SceneObjectSpeechStamp")),"Changing goal lamp A leaves the retained different-device B result stamp unchanged, reproducing the freshness boundary.");
        Check(changed.Contains("spatialGoalCompletion")&&Context(chat).Value<string>("status")=="searching","Final speech freshness detects externally invalidated inspection completion independently of old device B's receipt; actual changes="+string.Join(",",changed));
        Check(!JToken.DeepEquals(completionStamp,(JToken)Call(chat,"SpatialGoalCompletionSpeechStamp")),"The compact completion proof used across result generation and review changes when inspected target A's real state is externally invalidated.");
        Set(chat,"m_SpatialGoalTerminalProof",completionStamp.DeepClone());Set(chat,"m_WorkStatus","closed");Set(chat,"m_WorkReviewOpen",false);
        Check(!(bool)Call(chat,"ValidateSpatialGoalTerminalProof",(int)Get(chat,"m_FormalResponseGeneration")),"The actual final-result validation rejects a saved satisfied proof after target A changes during generation or review.");
        Check(Context(chat).Value<string>("status")=="blocked"&&(string)Get(chat,"m_WorkStatus")=="blocked"&&!(bool)Get(chat,"m_WorkReviewOpen"),"A released runner's invalidated success ends as explicit blocked work, not global closed plus a zombie searching goal.");
        Check((bool?)Context(chat)["completionEvidenceValid"]==false&&(bool?)Context(chat)["completionEvidence"]["historicalOnly"]==true,"The earlier real completion remains historical without current success authority.");
        lamp.DeviceLight.enabled=false;
        AddCase("inspection-completion-other-device-old-receipt-speech-freshness",Context(chat));

        string newRequest="Please turn on the small lamp on my left.";ledger.RecordUserTurn(newRequest);
        Check((bool)Call(chat,"IsCurrentSpatialGoal",goalId)&&Call(chat,"GetActiveSpatialGoalId")==null,"A new actual user turn preserves the logical goal but invalidates its execution lease.");
        Check(Register(chat,GoalDecision(newRequest,"on"),newRequest,"approved"),"A separately reviewed new user request creates its own goal.");
        Check(!(bool)Call(chat,"ValidateSpatialGoalTerminalProof",(int)Get(chat,"m_FormalResponseGeneration"))&&Context(chat).Value<string>("status")=="active","Old final-result validation rejects its stale sentence without blocking a different replacement goal.");
        string newId=Context(chat).Value<string>("goalId");Terminal(chat,apply,"performed");
        Check(newId!=goalId&&Context(chat).Value<string>("status")=="active"&&(string)Context(chat)["boundEntityId"]==null,"A delayed old terminal callback cannot bind or satisfy the replacement goal.");
        var onlyTerminal=Fact("unowned-terminal","set-object-state",recheck);Set(onlyTerminal,"entityId",lamp.EntityId);Terminal(chat,onlyTerminal,"performed");
        Check(Context(chat).Value<string>("status")=="active","A terminal fact with no registered action start cannot adopt current goal ownership.");
        Call(chat,"BlockSpatialGoal","bounded-search-exhausted");
        Check(Context(chat).Value<string>("status")=="blocked"&&(bool)Call(chat,"IsCurrentSpatialGoal",newId)&&Call(chat,"GetActiveSpatialGoalId")==null,"Blocked outcome is current terminal feedback context, not success or an active loop.");
        Terminal(chat,onlyTerminal,"observed");Check(Context(chat).Value<string>("status")=="blocked","Late results cannot revive a blocked goal.");
        Call(chat,"CancelSpatialGoal","user-cancellation");
        Check(Context(chat).Value<string>("status")=="cancelled"&&!(bool)Call(chat,"IsCurrentSpatialGoal",newId),"Cancellation revokes the goal token rather than claiming success.");
        Check(Register(chat,GoalDecision(newRequest,"on"),newRequest,"approved")&&Get(chat,"m_SpatialGoalRunner")==null,"A reviewed goal can exist in the pre-runner refresh window.");
        string preRunnerGoal=Context(chat).Value<string>("goalId");Set(chat,"m_FormalResponseGeneration",(int)Get(chat,"m_FormalResponseGeneration")+1);
        Check(Context(chat).Value<string>("status")=="active"&&(bool)Call(chat,"IsCurrentSpatialGoal",preRunnerGoal)&&Call(chat,"GetActiveSpatialGoalId")==null,"Formal transaction invalidation pauses authority while retaining the approved pre-runner objective.");
        Terminal(chat,proven,"performed");Check(Call(chat,"GetActiveSpatialGoalId")==null,"A delayed result cannot recover the old formal generation in that pre-runner window.");
        Check(Register(chat,GoalDecision(newRequest,"on"),newRequest,"approved"),"A reviewed current transaction may create a new token after prior invalidation.");
        string visionGoal=Context(chat).Value<string>("goalId");Set(chat,"m_SceneVisionEpoch",(int)Get(chat,"m_SceneVisionEpoch")+1);
        Check(Context(chat).Value<string>("status")=="active"&&(bool)Call(chat,"IsCurrentSpatialGoal",visionGoal)&&Call(chat,"GetActiveSpatialGoalId")==null,"Vision reset invalidates the current execution lease without erasing the pre-runner objective.");
        Check(Register(chat,GoalDecision(newRequest,"on"),newRequest,"approved"),"New current vision transaction can be reviewed anew.");
        string modelGoal=Context(chat).Value<string>("goalId");var settings=Get(chat,"m_ChatSettings");var model=Get(settings,"m_ChatModel");Set(settings,"m_ChatModel",null);
        Check(Context(chat).Value<string>("status")=="cancelled","Provider binding loss invalidates the pre-runner goal.");Set(settings,"m_ChatModel",model);
        Check(!(bool)Call(chat,"IsCurrentSpatialGoal",modelGoal)&&Context(chat).Value<string>("status")=="cancelled","Restoring the old model cannot resurrect an invalidated token.");
        Check(Register(chat,GoalDecision(newRequest,"on"),newRequest,"approved"),"A reviewed replacement can be created after explicit cancellation.");
        string avatarGoal=Context(chat).Value<string>("goalId");var originalAvatar=bridge.avatar;bridge.avatar=null;
        Check(Context(chat).Value<string>("status")=="cancelled","Actual avatar binding loss cancels the session goal.");bridge.avatar=originalAvatar;
        Check(!(bool)Call(chat,"IsCurrentSpatialGoal",avatarGoal)&&Context(chat).Value<string>("status")=="cancelled","Restoring an old binding cannot revive a previously invalidated goal token.");
        UserUpdateChecks(chat,ledger,newRequest,lamp,goalId);
        AddCase("new-user-replacement-old-callback-and-terminal-states",Context(chat));
    }

    private static void UserUpdateChecks(ChatSample chat,ArdyActionConstraintLedger ledger,string request,SceneObjectLightSwitch lamp,string historicGoalId)
    {
        ledger.RecordUserTurn(request);
        var decision=GoalDecision(request,"on");
        var inheritedReview=new RoomTaskProtocol.DeviceGoalReview { verdict="approved",reason="Controlled new request refers to the actual earlier closed device",
            relation="new",referentGoalId=historicGoalId,referentEntityId=lamp.EntityId };
        var registerArgs=new object[]{decision,request,inheritedReview,null};
        Check((bool)Call(chat,"TryRegisterReviewedSpatialGoal",registerArgs),"An independently reviewed same-device request can inherit a proven historical entity.");
        string id=(string)Context(chat)["goalId"];
        Check((string)Context(chat)["boundEntityId"]==lamp.EntityId&&(string)Context(chat)["desiredState"]=="on","The new on goal keeps the verified off target identity without inheriting completed state.");
        var fakeReview=new RoomTaskProtocol.DeviceGoalReview {verdict="approved",reason="Fabricated pair",relation="new",referentGoalId=historicGoalId,referentEntityId="unverified-entity"};
        registerArgs=new object[]{decision,request,fakeReview,null};
        Check(!(bool)Call(chat,"TryResolveReviewedSpatialReferent",new object[]{fakeReview,null,null,null}),"Reviewer output cannot bind an entity absent from the verified history pair.");

        string urge="你自己找找呀。";
        Call(chat,"PauseSpatialGoal","user-started-speaking");ledger.RecordUserTurn(urge);Call(chat,"ResetWorkReview",true);
        Check((string)Context(chat)["goalId"]==id&&(string)Context(chat)["status"]=="suspended"&&Call(chat,"GetActiveSpatialGoalId")==null,"Actual work reset during a new urging turn preserves identity and original authorization while stopping execution.");
        var update=new RoomTaskProtocol.DeviceGoalReview {verdict="approved",reason="Same authorized task continuation",relation="continue",responseMode="act",referentGoalId=id,referentEntityId=lamp.EntityId};
        var updateArgs=new object[]{update,urge,false,null};
        Check((bool)Call(chat,"TryApplySpatialGoalUserUpdate",updateArgs)&&(bool)updateArgs[2],"Independently reviewed urging resumes the original task rather than creating new authorization.");
        Check((string)Context(chat)["goalId"]==id&&(string)Context(chat)["originalRequest"]==request&&Call(chat,"GetActiveSpatialGoalId")!=null,"Resumed execution retains goal ID, original request and target identity under a fresh lease.");
        Call(chat,"SetSpatialGoalAwaitingClarification","same-identity-uncertain");
        Check((string)Context(chat)["status"]=="waiting_clarification"&&Call(chat,"GetActiveSpatialGoalId")==null,"Clarification stops execution without cancelling or satisfying the objective.");
        string answer="对，就是刚才那盏。";ledger.RecordUserTurn(answer);Call(chat,"ResetWorkReview",true);
        update=new RoomTaskProtocol.DeviceGoalReview {verdict="approved",reason="Answer confirms the proven referent",relation="clarify",responseMode="act",referentGoalId=id,referentEntityId=lamp.EntityId};
        updateArgs=new object[]{update,answer,false,null};
        Check((bool)Call(chat,"TryApplySpatialGoalUserUpdate",updateArgs)&&(bool)updateArgs[2]&&(string)Context(chat)["goalId"]==id,"A reviewed clarification answer resumes the exact retained objective.");
        string ordinary="你为什么刚才停下来了？";ledger.RecordUserTurn(ordinary);Call(chat,"ResetWorkReview",true);
        update=new RoomTaskProtocol.DeviceGoalReview {verdict="approved",reason="Question about progress, no cancellation",relation="unrelated",responseMode="answer",referentGoalId="",referentEntityId=""};
        updateArgs=new object[]{update,ordinary,false,null};
        Check((bool)Call(chat,"TryApplySpatialGoalUserUpdate",updateArgs)&&!(bool)updateArgs[2]&&(string)Context(chat)["status"]=="suspended"&&(string)Context(chat)["boundEntityId"]==lamp.EntityId,"Ordinary dialogue retains the task and identity without executing a step during the reply.");
        Check(!(bool)Call(chat,"TryResumeSpatialGoalAfterDialogue"),"A pure question does not resume the device objective merely because dialogue has become idle.");
        string mixed="なぜ止まったの？説明した後、先に右を向いてから元の作業を続けて。";
        ledger.RecordUserTurn(mixed);Call(chat,"ResetWorkReview",true);
        update=new RoomTaskProtocol.DeviceGoalReview {verdict="approved",reason="Question followed by explicit same-task resumption",relation="continue",responseMode="answer-then-resume",referentGoalId=id,referentEntityId=lamp.EntityId};
        updateArgs=new object[]{update,mixed,false,null};
        Check((bool)Call(chat,"TryApplySpatialGoalUserUpdate",updateArgs)&&!(bool)updateArgs[2]&&Call(chat,"GetActiveSpatialGoalId")==null,
            "A reviewed compound question preserves the goal but cannot execute before the answer.");
        var preface=new RoomTaskProtocol.Decision {intent="orient-view",origin="user",evidence="先に右を向いて",viewChange=new RoomTaskProtocol.ViewChange {captureId="will-be-rebound-after-answer",yaw=30,pitch=0}};
        Check((bool)Call(chat,"DeferReviewedObservationUntilAfterAnswer",preface),"A bounded latest-turn quotation preserves an explicit view request for after the answer.");
        preface.evidence=request;
        Check(!(bool)Call(chat,"DeferReviewedObservationUntilAfterAnswer",preface),"The old device request cannot authorize the latest pre-answer view action.");
        AnswerPlaybackReceiptChecks(chat);
        string cancellation="不用了，取消开灯。";ledger.RecordUserTurn(cancellation);Call(chat,"ResetWorkReview",true);
        update=new RoomTaskProtocol.DeviceGoalReview {verdict="approved",reason="Explicit withdrawal",relation="cancel",responseMode="answer",referentGoalId=id,referentEntityId=lamp.EntityId};
        updateArgs=new object[]{update,cancellation,false,null};
        Check((bool)Call(chat,"TryApplySpatialGoalUserUpdate",updateArgs)&&(string)Context(chat)["status"]=="cancelled"&&Call(chat,"GetActiveSpatialGoalId")==null,"Only reviewed cancellation withdraws the persistent objective.");
        AddCase("verified-referent-urging-clarification-dialogue-cancellation",Context(chat));
    }

    private static void AnswerPlaybackReceiptChecks(ChatSample chat)
    {
        string[] fields={"m_LatencyFillerPlayed","m_LatencyFillerText","m_LatencyFillerStartedAt","m_LatencyFillerDuration",
            "m_LatencyFillerFromEou","m_LatencyFillerClip"};
        var saved=fields.Select(name=>Get(chat,name)).ToArray();
        var heard=(System.Text.StringBuilder)Get(chat,"m_AssistantHeardText");string savedHeard=heard.ToString();
        int generation=(int)Get(chat,"m_FormalResponseGeneration");
        var receipt=Get(chat,"m_ReviewedGoalAnswerObligation");
        const string answer="The target is not confirmed yet.";
        try
        {
            heard.Clear();Call(chat,"BindReviewedGoalAnswerSpeech",generation,answer);
            Set(chat,"m_LatencyFillerPlayed",true);Set(chat,"m_LatencyFillerText",answer);
            Set(chat,"m_LatencyFillerStartedAt",Time.realtimeSinceStartup-2f);Set(chat,"m_LatencyFillerDuration",1f);
            Call(chat,"CommitPlayedLatencyFiller");Call(chat,"CompleteReviewedGoalAnswerPlayback",generation);
            Check(heard.ToString()==answer&&!(bool)Get(receipt,"delivered")&&string.IsNullOrEmpty((string)Get(receipt,"playedText")),
                "Even identical filler text committed by the production heard-text path cannot satisfy an unplayed formal answer.");
            Call(chat,"RecordReviewedGoalAnswerPlayedChunk",generation-1,answer);Call(chat,"CompleteReviewedGoalAnswerPlayback",generation-1);
            Check(!(bool)Get(receipt,"delivered")&&string.IsNullOrEmpty((string)Get(receipt,"playedText")),
                "An old-generation completion cannot contribute text or deliver the current answer receipt.");
            Call(chat,"RecordReviewedGoalAnswerPlayedChunk",generation,"The target ");Call(chat,"CompleteReviewedGoalAnswerPlayback",generation);
            Check(!(bool)Get(receipt,"delivered")&&!(bool)Call(chat,"ReviewedGoalAnswerAllowsResume",Get(chat,"m_SpatialDeviceGoal")),
                "A successful but incomplete formal chunk still cannot resume the device task.");
            Call(chat,"RecordReviewedGoalAnswerPlayedChunk",generation-1,"stale text");
            Check((string)Get(receipt,"playedText")=="Thetarget","A late stale chunk cannot contaminate already recorded current answer text.");
            Call(chat,"RecordReviewedGoalAnswerPlayedChunk",generation,"is not confirmed yet.");Call(chat,"CompleteReviewedGoalAnswerPlayback",generation);
            Check((bool)Get(receipt,"delivered")&&(bool)Call(chat,"ReviewedGoalAnswerAllowsResume",Get(chat,"m_SpatialDeviceGoal")),
                "Only all current formal-answer chunks satisfy the receipt despite a preceding filler in heard history.");
            LastReport["answerReceiptScope"]="Controlled completed-chunk calls exercise filler exclusion and generation ownership, not audio playback; the separate runner suite exercises actual PCM completion.";
        }
        finally
        {
            for(int i=0;i<fields.Length;i++)Set(chat,fields[i],saved[i]);heard.Clear();heard.Append(savedHeard);
        }
    }

    private static SceneObjectLightSwitch Lamp(string name,Vector3 position)
    {
        var root=new GameObject(name);root.transform.SetParent(fixture.transform,false);root.transform.localPosition=position;
        root.AddComponent<SceneObjectIdentity>().Configure(SceneObjectLampCatalog.Label,SceneObjectLampCatalog.AuthoredKey);
        var panel=GameObject.CreatePrimitive(PrimitiveType.Quad);panel.transform.SetParent(root.transform,false);panel.transform.localScale=new Vector3(.10f,.10f,1);UnityEngine.Object.DestroyImmediate(panel.GetComponent<Collider>());
        var material=new Material(Shader.Find("Standard"));material.color=Color.white;material.EnableKeyword("_EMISSION");material.SetColor("_EmissionColor",new Color(.4f,.2f,.1f));materials.Add(material);
        var renderer=panel.GetComponent<Renderer>();renderer.sharedMaterial=material;
        var lightObject=new GameObject("registered light");lightObject.transform.SetParent(root.transform,false);var light=lightObject.AddComponent<Light>();light.enabled=true;light.intensity=.2f;light.range=.25f;
        var device=root.AddComponent<SceneObjectLightSwitch>();Check(device.TryBind(root.transform,renderer,light,out string reason),"Isolated explicit lamp profile binds actual mesh/material/Light: "+reason);return device;
    }

    private static object Fact(string id,string intent,SceneVisionObservation observation)
    {
        var fact=Activator.CreateInstance(typeof(ChatSample).GetNestedType("SceneActionFact",Flags),true);
        foreach(var field in new Dictionary<string,object> { ["actionId"]=id,["intent"]=intent,["origin"]="user",["captureId"]=observation.CaptureId,["confirmedCaptureId"]=observation.CaptureId,["resultCaptureId"]=observation.CaptureId,
            ["phase"]="preparing",["observedUtc"]=DateTime.UtcNow.ToString("o"),["objectConfirmed"]=true,["semanticTargetConfirmed"]=true,["targetMatchVerdict"]="matched",["targetPurpose"]="direct-target",["targetDescription"]=Request,["matchCaptureId"]=observation.CaptureId })Set(fact,field.Key,field.Value);
        return fact;
    }
    private static void Start(ChatSample chat,object fact){Set(fact,"phase","preparing");Set(fact,"actualRoot",chat.GetComponent<ArdyRoomDialogueBridge>().avatar.transform.position);Call(chat,"ObserveSpatialGoalAction",fact);}
    private static void Terminal(ChatSample chat,object fact,string phase){Set(fact,"phase",phase);Set(fact,"reason","controlled terminal receipt for goal predicate");Call(chat,"ObserveSpatialGoalAction",fact);}
    private static bool Register(ChatSample chat,RoomTaskProtocol.Decision decision,string text,string verdict){var args=new object[]{decision,text,new RoomTaskProtocol.DeviceGoalReview{verdict=verdict,reason="controlled independent semantic verdict"},null};return(bool)Call(chat,"TryRegisterReviewedSpatialGoal",args);}
    private static bool Step(ChatSample chat,RoomTaskProtocol.Decision decision){var args=new object[]{decision,null};return(bool)Call(chat,"TryValidateSpatialGoalStep",args);}
    private static bool Confirmed(ChatSample chat,object fact,SceneObservedObject observed){var args=new object[]{fact,observed,null};return(bool)Call(chat,"TryValidateSpatialGoalConfirmedTarget",args);}
    private static JObject Context(ChatSample chat)=>(JObject)Call(chat,"BuildSpatialGoalContext");
    private static void AddCase(string name,JObject context)=>((JArray)LastReport["cases"]).Add(new JObject{["name"]=name,["goal"]=context});
    private static IEnumerator Capture(NevaSceneVisionCapture capture,string name,Action<SceneVisionObservation> complete)
    {
        SceneVisionObservation observation=null;capture.RequestCapture(value=>observation=value);float until=Time.realtimeSinceStartup+5;
        while(observation==null){if(Time.realtimeSinceStartup>until)throw new TimeoutException("Device goal capture "+name);yield return null;}
        Check(observation.HasImage&&observation.HasSurfaceGrounding,"Actual RGB/opaque ID geometry captured: "+name);
        File.WriteAllText(Path.Combine(directory,name+".metadata.json"),observation.MetadataJson);File.WriteAllBytes(Path.Combine(directory,name+".jpg"),Convert.FromBase64String(observation.ImageDataUrl.Substring(observation.ImageDataUrl.IndexOf(',')+1)));complete(observation);
    }
    private static void Check(bool value,string reason){checks++;if(!value)throw new InvalidOperationException(reason);}
    private static object Get(object target,string field)=>target.GetType().GetField(field,Flags).GetValue(target);
    private static void Set(object target,string field,object value)=>target.GetType().GetField(field,Flags).SetValue(target,value);
    private static object Call(object target,string method,params object[] args)=>target.GetType().GetMethod(method,Flags).Invoke(target,args);
}
