using System;
using System.Collections;
using System.Reflection;
using System.Linq;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Drive the actual ChatSample watcher deterministically through controlled
// controller facts. This checks scheduling/ownership, not physical navigation.
public static class ActorPlanIntegrationRegression
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;
    public static int RunChecks()
    {
        checks = 0;
        using (var f = new Fixture())
        {
            var plan = f.Plan; var turn = Get(f.Chat, "m_ActorPlanTurn");
            Check(plan.Active && plan.OriginalGoal == f.Draft && plan.RemainingGoal == f.Draft,
                "The actual actor callback registers the entire role goal before the first room request.");
            var step = f.Step("approach", "走到之后再跟着用户");
            object[] accept = { step, f.Generation, null };
            Check((bool)Call(f.Chat, "AcceptActorPlanStep", accept) && plan.BindAction(1, "controlled-walk"),
                "Production step acceptance binds a result to this exact plan revision and action.");
            f.Stage("controlled-walk", "moving");
            var watcher = (IEnumerator)Call(f.Chat, "AwaitActorPlanStep", plan, turn, step, "controlled-walk");
            int requests = f.Model.RoomCalls;
            Check(watcher.MoveNext() && plan.RemainingGoal == f.Draft && f.Model.RoomCalls == requests,
                "An in-progress physical fact neither consumes the goal nor dispatches the second step.");
            f.Stage("controlled-walk", "arrived");
            Check(!watcher.MoveNext() && plan.Revision == 2 && plan.Results.Count == 1 && plan.Active,
                "Actual arrival advances once while the remaining goal keeps the same plan alive.");
            var next = JObject.Parse(f.Model.LastRoomInput)["actorProposal"]?["actorPlan"];
            Check(f.Model.RoomCalls == requests + 1 && (string)next?["remainingGoal"] == step.planRemaining &&
                (int)Get(f.Chat, "m_FormalResponseGeneration") == f.Generation && f.Model.FormalCalls == 0 &&
                Get(f.Chat, "m_FastLoopTurn") != null,
                "The watcher requests the next step in the same formal turn with its real result and full remainder.");
            Check(!watcher.MoveNext() && f.Model.RoomCalls == requests + 1,
                "Exhausted old watcher cannot submit a duplicate next step.");
        }
        using (var f = new Fixture())
        {
            var plan = f.Plan; var turn = Get(f.Chat, "m_ActorPlanTurn");
            var step = f.Step("approach", "关灯");
            Check(plan.Propose(1, step.intent, step.planRemaining, "walk", out _) && plan.BindAction(1,"old-walk"), "Old action is bound.");
            f.Stage("old-walk", "moving");
            var watcher=(IEnumerator)Call(f.Chat,"AwaitActorPlanStep",plan,turn,step,"old-walk");
            Check(watcher.MoveNext(), "The old watcher is genuinely suspended on an unfinished operation.");
            var replacement=new ActorPlanRuntime("新角色目标");
            Set(f.Chat,"m_ActorPlan",replacement);
            Check(!watcher.MoveNext() && replacement.Active && plan.State=="cancelled",
                "A resumed old coroutine cancels only its own instance, never the replacement plan.");
        }
        using (var f = new Fixture())
        {
            var plan=f.Plan;var turn=Get(f.Chat,"m_ActorPlanTurn");var step=f.Step("approach","继续关灯");
            plan.Propose(1,step.intent,step.planRemaining,"walk",out _);plan.BindAction(1,"failed-walk");
            f.Stage("failed-walk","failed");Set(f.Chat,"m_RoomTaskDispatch","not-submitted: blocked");
            int requests=f.Model.RoomCalls;
            var watcher=(IEnumerator)Call(f.Chat,"AwaitActorPlanStep",plan,turn,step,"failed-walk");
            Check(!watcher.MoveNext() && plan.State=="blocked" && plan.RemainingGoal==f.Draft && f.Model.RoomCalls==requests,
                "A failed first step retains the unmet goal and never dispatches the dependent action.");
        }
        using (var f = new Fixture())
        {
            var plan=f.Plan;var turn=Get(f.Chat,"m_ActorPlanTurn");var step=f.Step("approach","");
            plan.Propose(1,step.intent,"","walk",out _);plan.BindAction(1,"arrived-walk");
            f.Stage("arrived-walk","arrived");Call(f.Chat,"RememberActorTaskLanguage",f.Generation,"zh");
            var watcher=(IEnumerator)Call(f.Chat,"AwaitActorPlanStep",plan,turn,step,"arrived-walk");
            Check(!watcher.MoveNext() && plan.State=="completed" && (bool)plan.Results[0]["succeeded"],"Actual arrived fact completes the owned finite step.");
            Check(f.Queued.Contains("已经") && !f.Queued.Contains("没确认") && !f.Queued.Contains("未能"),
                "Arrived success publishes a Chinese arrival receipt, never the missing-formatter failure fallback.");
        }
        foreach (bool valid in new[]{true,false})
        using (var f=new Fixture())
        {
            var plan=f.Plan;plan.Propose(1,"set-object-state","关灯","off",out _);plan.BindAction(1,"off-1");
            plan.Resolve(1,"off-1",true,"performed","verified","lamp-1",true);plan.Results[0]["requestedState"]="off";
            Call(f.Chat,"UpdateActorPlanProposal");Call(f.Chat,"RememberActorTaskLanguage",f.Generation,"zh");
            var none=f.Step("none","");int calls=f.Model.RoomCalls;
            Check((bool)Call(f.Chat,"ConsumeActorPlanStep",none,valid,f.Generation),"Finite none or a failed next planner has a bounded receipt path.");
            Check(f.Queued.Contains("关灯的步骤已经确认完成") && f.Model.RoomCalls==calls && plan.Results.Count==1,
                "A later none/error preserves earlier lamp-off success without replaying it or opening another planner.");
            Check(valid?plan.State=="completed":f.Queued.Contains("后续步骤未能确认"),"No-next-step and later failure remain distinct outcomes.");
        }
        foreach(string continuation in new[]{"ongoing","after_completion",null,"parallel"})
        using(var f=new Fixture(true))
        {
            var bridge=f.Chat.GetComponent<ArdyRoomDialogueBridge>();var plan=f.Plan;var turn=Get(f.Chat,"m_ActorPlanTurn");
            var step=f.Step("follow",continuation=="ongoing"?"":continuation==null?f.Draft:"同时唱刚才的歌");step.planContinuation=continuation;
            object[] accept={step,f.Generation,null};
            Check((bool)Call(f.Chat,"AcceptActorPlanStep",accept),"Same-user follow proposal remains revision bound.");
            f.Stage("retained-follow","follow-waiting");Set(f.Chat,"m_RoomTaskIntent","follow");Set(f.Chat,"m_RoomTaskDispatch","follow-already-active");
            Call(f.Chat,"RememberActorTaskLanguage",f.Generation,"zh");int requests=f.Model.RoomCalls;
            if(continuation=="parallel")
            {
                Check(plan.BindAction(1,"retained-follow"),"Concurrent tail watches this retained live task.");
                var watcher=(IEnumerator)Call(f.Chat,"AwaitActorPlanStep",plan,turn,step,"retained-follow");
                Check(!watcher.MoveNext() && plan.Active && f.Model.RoomCalls==requests+1 && plan.RemainingGoal=="同时唱刚才的歌",
                    "Explicit parallel tail schedules the actual next planner in the same turn after follow-running confirmation.");
                Check((string)plan.Results[0]["outcome"]=="ongoing" && (string)JObject.Parse(f.Model.LastRoomInput)["actorProposal"]["actorPlan"]["remainingGoal"]==step.planRemaining,
                    "The next planner receives both the complete tail and the actual ongoing, not completed, follow result.");
            }
            else
            {
                Check((bool)Call(f.Chat,"ConsumeActorPlanStep",step,true,f.Generation) && plan.State=="ongoing" && f.Model.RoomCalls==requests,
                    "Ongoing/unspecified/dependent follow retains the existing controller without a redundant plan or new motion.");
                Check(continuation=="after_completion" ? f.Queued.Contains("没有结束条件") : !f.Queued.Contains("后面的"),
                    "Only a typed after-completion goal claims a pending tail; echoed plain follow does not invent one.");
                Check(bridge.IsFollowing && bridge.ActiveTaskId=="retained-follow","Receipt does not stop or replace the physical follow owner.");
            }
        }
        using(var f=new Fixture(true))
        {
            var bridge=f.Chat.GetComponent<ArdyRoomDialogueBridge>();var identity=Call(f.Chat,"CaptureRoomFollowIdentity",bridge);
            Check(identity!=null && (bool)Call(f.Chat,"RoomFollowIdentityCurrent",identity),"Retained identity includes the real controller's user binding.");
            Set(bridge,"actionId","new-follow");
            Check(!(bool)Call(f.Chat,"RoomFollowIdentityCurrent",identity),"A replacement task cannot inherit the previous follow decision.");
            Set(bridge,"actionId","retained-follow");bridge.userHead=null;
            Check(!(bool)Call(f.Chat,"RoomFollowIdentityCurrent",identity) && Call(f.Chat,"CaptureRoomFollowIdentity",bridge)==null,
                "A changed user anchor fails even while the old followActive flag has not yet been reset.");
        }
        foreach(string changed in new[]{"same","task","user","generation"})
        using(var f=new Fixture(true,true))
        {
            var bridge=f.Chat.GetComponent<ArdyRoomDialogueBridge>();var plan=f.Plan;
            var input=JObject.Parse(f.Model.LastRoomInput);var source=input["actorProposal"];
            Check((string)(input["visualObservation"] as JObject)?["captureId"]=="stale-follow-frame", "Actual Room request contains the deliberately old observation.");
            var response=new JObject { ["intent"]="follow",["origin"]="actor_draft",["evidence"]=f.Draft,
                ["actorDraftId"]=source["draftId"],["actorDraftRevision"]=source["draftRevision"],
                ["planId"]=source["actorPlan"]["id"],["planRevision"]=source["actorPlan"]["revision"],
                ["planRemaining"]="",["planContinuation"]="ongoing" };
            if(changed=="task")Set(bridge,"actionId","replacement-follow");
            if(changed=="user")bridge.userHead=null;
            if(changed=="generation")Set(f.Chat,"m_FormalResponseGeneration",f.Generation+1);
            var complete=f.Model.RoomComplete;int requests=f.Model.RoomCalls;
            complete(true,response.ToString(),"ok");string queued=f.Queued;
            if(changed=="same")
                Check(plan.State=="ongoing" && (string)Get(f.Chat,"m_RoomTaskDispatch")=="follow-already-active" && bridge.ActiveTaskId=="retained-follow" && f.Model.RoomCalls==requests,
                    "Production Room callback reuses exactly the retained follow before stale-image refresh, without another model call or motion.");
            else if(changed=="generation")
                Check(plan.State=="planning" && queued=="" && f.Model.RoomCalls==requests,"An old generation Room callback cannot claim or stop live following.");
            else
                Check(plan.State=="cancelled" && (string)Get(f.Chat,"m_RoomTaskDispatch")=="decision-unavailable" && bridge.IsFollowing && f.Model.RoomCalls==requests,
                    "Changed task/user identity is refused before refresh; it neither reuses nor cancels the replacement follow.");
            complete(true,response.ToString(),"ok");
            Check(f.Queued==queued && f.Model.RoomCalls==requests,"Duplicate retained-follow callback cannot publish twice or re-enter planning.");
        }
        SubmissionAndHandoffChecks();
        return checks;
    }
    private static void SubmissionAndHandoffChecks()
    {
        using(var f=new Fixture())
        {
            var step=f.Step("set-object-state","");
            Check((bool)Call(f.Chat,"AcceptActorPlanStep",new object[]{step,f.Generation,null}),"The real plan accepts a proposed lamp attempt.");
            Set(f.Chat,"m_RoomTaskIntent",step.intent);Set(f.Chat,"m_RoomTaskActionId","");
            Set(f.Chat,"m_RoomTaskDispatch","not-submitted: existing-body-task-has-priority");
            Call(f.Chat,"RememberActorTaskLanguage",f.Generation,"zh");
            Check((bool)Call(f.Chat,"ConsumeActorPlanStep",step,true,f.Generation) && f.Plan.State=="blocked" && f.Plan.Results.Count==1,
                "Production callback records the pre-submission rejection before closing the plan.");
            Check(f.Plan.Results[0]["actionId"].Type==JTokenType.Null && (bool)f.Plan.Results[0]["submitted"]==false &&
                f.Queued.Contains("身体任务") && f.Queued.Contains("新的动作没有开始"),
                "The user hears the concrete body conflict, without a fake action ID or execution claim.");
            var facts=(JObject)Call(f.Chat,"BuildRecentActionOutcomes");
            var attempts=facts["events"].Where(x=>(string)x["attemptId"]==(string)f.Plan.Results[0]["attemptId"]).ToArray();
            Check(attempts.Length==1 && (string)attempts[0]["failureCategory"]=="body-task-occupied" &&
                attempts[0]["actionId"].Type==JTokenType.Null && (bool)attempts[0]["submitted"]==false,
                "Ordinary role context receives the same exact nonexecuted attempt and rejection. Facts="+facts.ToString(Newtonsoft.Json.Formatting.None));
        }
        foreach(string mode in new[]{"pause","yield"})
        using(var f=new Fixture(true))
        {
            var bridge=f.Chat.GetComponent<ArdyRoomDialogueBridge>();var owner=Call(f.Chat,"CaptureRoomFollowIdentity",bridge);
            var step=f.Step("set-object-state","");step.bodyHandoff=new RoomTaskProtocol.BodyHandoff{taskId="retained-follow",mode=mode};
            bool ready=false;string failure=null;
            var prepare=(IEnumerator)Call(f.Chat,"PrepareActorBodyHandoff",step,owner,(Action<bool,string>)((ok,why)=>{ready=ok;failure=why;}));
            Check(!prepare.MoveNext() && ready && failure==null && !bridge.IsFollowing && !bridge.ReservesBody,
                "The actual handoff coroutine releases an idle retained follow without submitting the lamp.");
            var fact=(JObject)Call(f.Chat,"ActorBodyHandoffFacts");
            Check((string)fact["mode"]==mode && (bool)fact["currentPlanOwnsHandoff"] &&
                !(bool)fact["automaticResumeAllowed"] && f.Plan.State=="planning" && f.Plan.Results.Count==0,
                "Only the chosen bounded handoff is retained; pausing is not completion of a new plan step.");
            var lease=Get(f.Chat,"m_ActorBodyHandoff");var token=Get(lease,"Follow");
            bridge.StopForTask("explicit-new-stop");
            Check(!(bool)Call(bridge,"IsFollowHandoffCurrent",token) && !(bool)((JObject)Call(f.Chat,"ActorBodyHandoffFacts"))["currentPlanOwnsHandoff"],
                "Explicit stop revokes the handoff even after the original follow has been suspended.");
            f.Plan.Propose(1,step.intent,"","lamp",out _);f.Plan.BindAction(1,"owned-lamp");
            f.Plan.Resolve(1,"owned-lamp",true,"performed","verified",null,false);
            Call(f.Chat,"PublishActorPlanClosure",f.Plan,Get(f.Chat,"m_ActorPlanTurn"),true,"finite-plan-completed");
            Check(!bridge.IsFollowing && bridge.ActionId=="explicit-new-stop",
                "Actual successful closure cannot overwrite an explicit stop or restart old following.");
        }
        foreach(string changed in new[]{"generation","task","user"})
        using(var f=new Fixture(true))
        {
            var bridge=f.Chat.GetComponent<ArdyRoomDialogueBridge>();var owner=Call(f.Chat,"CaptureRoomFollowIdentity",bridge);
            var step=f.Step("set-object-state","");step.bodyHandoff=new RoomTaskProtocol.BodyHandoff{taskId="retained-follow",mode="pause"};
            if(changed=="generation")Set(f.Chat,"m_FormalResponseGeneration",f.Generation+1);
            if(changed=="task")Set(bridge,"actionId","replacement-follow");
            if(changed=="user")bridge.userHead=null;
            bool ready=true;string failure=null;
            var prepare=(IEnumerator)Call(f.Chat,"PrepareActorBodyHandoff",step,owner,(Action<bool,string>)((ok,why)=>{ready=ok;failure=why;}));
            Check(!prepare.MoveNext() && !ready && failure=="body-handoff-owner-changed" && bridge.IsFollowing,
                "Stale "+changed+" handoff cannot stop the live follow.");
        }
        foreach(bool replaceDuringWait in new[]{false,true})
        using(var f=new Fixture(true))
        {
            var bridge=f.Chat.GetComponent<ArdyRoomDialogueBridge>();var owner=Call(f.Chat,"CaptureRoomFollowIdentity",bridge);
            var step=f.Step("set-object-state","");step.bodyHandoff=new RoomTaskProtocol.BodyHandoff{taskId="retained-follow",mode="pause"};
            int callbacks=0;bool ready=false;
            Application.LogCallback occupy=(message,stack,type)=>{
                if(message.StartsWith("[Room/Dialogue]") && message.Contains("phase=handoff-paused"))
                    Set(bridge.Controller,"<IsBusy>k__BackingField",true);
            };
            Application.logMessageReceived+=occupy;
            IEnumerator prepare;
            try
            {
                prepare=(IEnumerator)Call(f.Chat,"PrepareActorBodyHandoff",step,owner,(Action<bool,string>)((ok,why)=>{callbacks++;ready=ok;}));
                Check(prepare.MoveNext() && callbacks==0 && !bridge.IsFollowing && f.Plan.State=="planning",
                    "The production coroutine really suspends while controller pose release remains pending; no lamp was submitted.");
            }
            finally { Application.logMessageReceived-=occupy; }
            if(replaceDuringWait)Set(f.Chat,"m_FormalResponseGeneration",f.Generation+1);
            Set(bridge.Controller,"<IsBusy>k__BackingField",false);
            Check(!prepare.MoveNext() && (replaceDuringWait?callbacks==0:callbacks==1&&ready),
                "After asynchronous release, only the same current owner may dispatch its adopted next step.");
            Check(!prepare.MoveNext() && callbacks==(replaceDuringWait?0:1),"An exhausted body-release watcher cannot dispatch twice.");
        }
        using(var f=new Fixture(true))
        {
            var bridge=f.Chat.GetComponent<ArdyRoomDialogueBridge>();var owner=Call(f.Chat,"CaptureRoomFollowIdentity",bridge);
            var step=f.Step("set-object-state","");step.bodyHandoff=new RoomTaskProtocol.BodyHandoff{taskId="retained-follow",mode="pause"};
            var prepare=(IEnumerator)Call(f.Chat,"PrepareActorBodyHandoff",step,owner,(Action<bool,string>)((ok,why)=>{}));prepare.MoveNext();
            f.Plan.Propose(1,step.intent,"","lamp",out _);f.Plan.BindAction(1,"owned-lamp");
            f.Plan.Resolve(1,"owned-lamp",true,"performed","verified",null,false);
            Call(f.Chat,"PublishActorPlanClosure",f.Plan,Get(f.Chat,"m_ActorPlanTurn"),true,"finite-plan-completed");
            Check(!bridge.IsFollowing && !(bool)((JObject)Call(f.Chat,"ActorBodyHandoffFacts"))["automaticResumeAllowed"] &&
                !(bool)((JObject)Call(f.Chat,"ActorBodyHandoffFacts"))["currentPlanOwnsHandoff"],
                "Actual finite-plan closure keeps following paused without inventing a restore action or permission.");
        }
        using(var f=new Fixture(true))
        {
            var bridge=f.Chat.GetComponent<ArdyRoomDialogueBridge>();var owner=Call(f.Chat,"CaptureRoomFollowIdentity",bridge);
            var step=f.Step("approach","走近完成后继续跟着你");step.bodyHandoff=new RoomTaskProtocol.BodyHandoff{taskId="retained-follow",mode="pause"};
            var prepare=(IEnumerator)Call(f.Chat,"PrepareActorBodyHandoff",step,owner,(Action<bool,string>)((ok,why)=>{}));prepare.MoveNext();
            Check((bool)Call(f.Chat,"AcceptActorPlanStep",new object[]{step,f.Generation,null}) && f.Plan.BindAction(1,"new-approach"),
                "A new finite step with an explicitly adopted follow tail uses the ordinary plan acceptance path.");
            f.Stage("new-approach","arrived");int requests=f.Model.RoomCalls;
            var watcher=(IEnumerator)Call(f.Chat,"AwaitActorPlanStep",f.Plan,Get(f.Chat,"m_ActorPlanTurn"),step,"new-approach");
            Check(!watcher.MoveNext() && f.Plan.State=="planning" && !bridge.IsFollowing && f.Model.RoomCalls==requests+1 &&
                (string)JObject.Parse(f.Model.LastRoomInput)["actorProposal"]["actorPlan"]["remainingGoal"]==step.planRemaining,
                "The real result watcher preserves the adopted follow tail for its normal next decision, never resumes from a handoff flag.");
            var target=bridge.ReadCurrentTargetEvidence(true);
            bridge.userHead.position+=Vector3.right*.25f;
            Check(!bridge.FollowForTarget("later-follow",target.currentTargetRevision,out _) && !bridge.IsFollowing && bridge.ActionId=="new-approach",
                "A separately adopted follow still reads current user/root positions and rejects the pre-movement target revision before dispatch.");
        }
    }
    private sealed class Fixture:IDisposable
    {
        private readonly object inner;
        public readonly ChatSample Chat;public readonly FastLoopIntegrationModel Model;
        public readonly string Draft="我先走到你身边，再跟着你。";
        public int Generation=>(int)Get(Chat,"m_FormalResponseGeneration");
        public ActorPlanRuntime Plan=>(ActorPlanRuntime)Get(Chat,"m_ActorPlan");
        public string Queued=>(string)Call(inner,"Queued");
        public Fixture():this(false,false) {}
        public Fixture(bool following,bool staleVision=false)
        {
            inner=Activator.CreateInstance(typeof(FastLoopIntegrationRegression).GetNestedType("Fixture",BindingFlags.NonPublic),true);
            Chat=(ChatSample)Get(inner,"Chat");Model=(FastLoopIntegrationModel)Get(inner,"Model");
            Call(inner,"Arm","嗯。",false);Call(inner,"AttachControlledRoom");Call(inner,"AttachSpeechProvider");
            if(following)
            {
                var bridge=Chat.GetComponent<ArdyRoomDialogueBridge>();bridge.userHead=Chat.transform;
                Set(bridge,"followUser",bridge.userHead);Set(bridge,"followActive",true);Set(bridge,"actionId","retained-follow");Set(bridge,"phase","follow-waiting");
                Set(bridge.Controller,"avatar",bridge.avatar);
            }
            if(staleVision)
            {
                Set(Chat,"m_EnableScreenVision",true);Set(Chat,"m_AgentEyesOpen",true);
                var observation=(SceneVisionObservation)typeof(SceneVisionObservation).GetConstructors(F).Single().Invoke(new object[]{
                    "stale-follow-frame","ready","","",Time.realtimeSinceStartup-100f,0,new Pose(Vector3.zero,Quaternion.identity),60f,2,2,
                    "data:image/png;base64,AA==",0,"","","",null});
                Set(Chat,"m_FormalSceneObservation",observation);Set(Chat,"m_FormalSceneObservationGeneration",Generation);
            }
            var judge=(FastLoopIntegrationModel.PendingActorIntent)Call(inner,"CompileActor",Draft);
            judge.Answer(new ActorIntentDecision{Domain="other_action",Kind="replan",Task="other",Target="other",Action="none",Condition="none",Evidence=Draft,Goal=Draft,Coverage="compound"},"ok");
        }
        public RoomTaskProtocol.Decision Step(string intent,string remaining)=>new RoomTaskProtocol.Decision{intent=intent,origin="actor_draft",evidence=Draft,planId=Plan.Id,planRevision=Plan.Revision,planRemaining=remaining};
        public void Stage(string action,string phase)
        {
            var bridge=Chat.GetComponent<ArdyRoomDialogueBridge>();
            Set(bridge,"actionId",action);Set(bridge,"phase",phase);Set(bridge,"activeAction",phase=="moving");
            Set(Chat,"m_RoomTaskIntent","approach");Set(Chat,"m_RoomTaskActionId",action);Set(Chat,"m_RoomTaskDispatch","attempt-submitted");
        }
        public void Dispose()=>((IDisposable)inner).Dispose();
    }
    private static object Get(object o,string n)=>o.GetType().GetField(n,F).GetValue(o);
    private static void Set(object o,string n,object v)=>o.GetType().GetField(n,F).SetValue(o,v);
    private static object Call(object o,string n,params object[] a){try{return o.GetType().GetMethod(n,F).Invoke(o,a);}catch(TargetInvocationException e){throw e.InnerException??e;}}
    private static void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException(why);}
}
