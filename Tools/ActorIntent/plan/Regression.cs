using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class ActorPlanRegression
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    public static void Main(string[] args)
    {
        HandoffChecks();
        var conflict = new JObject { ["version"]=4,["goal"]="拒绝移动，只听用户说话",["coverage"]="atomic",
            ["domain"]="dialogue",["kind"]="act",["task"]="none",["target"]="none",["action"]="none",["condition"]="none",["evidence"]="我不想走动，只想听你说话。" };
        Check(!ActorIntentProtocol.TryParse(conflict.ToString(),(string)conflict["evidence"],out _,out _), "inconsistent semantic enums are not executable");
        Check(ActorIntentProtocol.TryCompile(conflict.ToString(),(string)conflict["evidence"],out var review,out _) &&
            review.RequiresFullReview && !ActorIntentProtocol.Validate(review,(string)conflict["evidence"],out _) && !review.TargetIsProgramBound,
            "inconsistent adoption remains non-executable until semantic review, including original refusal");
        conflict["kind"]="claim";
        Check(ActorIntentProtocol.TryCompile(conflict.ToString(),(string)conflict["evidence"],out review,out _) && review.RequiresFullReview && !ActorIntentProtocol.Validate(review,(string)conflict["evidence"],out _),
            "recovery of a past claim cannot authorize a planner action");
        conflict["forged"]="new permission";
        Check(!ActorIntentProtocol.TryCompile(conflict.ToString(),(string)conflict["evidence"],out _,out _), "recovery still rejects open or malformed wire");
        var plan = new ActorPlanRuntime("先走近那盏灯，再关掉它。");
        Check(plan.Propose(1, "approach-object", "关闭同一盏灯", "approach:lamp", out _), "first step accepted");
        Check(plan.RemainingGoal.Contains("走近"), "submission does not consume the goal");
        Check(plan.BindAction(1, "walk-1"), "actual action bound");
        Check(!plan.Resolve(0, "walk-1", true, "arrived", "", "lamp", true), "old revision cannot complete");
        Check(!plan.Resolve(1, "another", true, "arrived", "", "lamp", true), "wrong action cannot complete");
        Check(plan.Resolve(1, "walk-1", true, "arrived", "actual arrival", "lamp", true), "actual arrival advances");
        Check(plan.Active && plan.RemainingGoal == "关闭同一盏灯" && plan.EntityId == "lamp", "remaining goal and identity persist");
        Check(!plan.Propose(2, "approach-object", "关灯", "approach:lamp", out _), "completed action cannot repeat");
        Check(plan.Propose(2, "set-object-state", "", "lamp:off", out _) && plan.BindAction(2, "power-2"), "second step bound");
        Check(!plan.Resolve(1, "walk-1", true, "arrived", "duplicate", "lamp", true), "late arrival cannot skip second step");
        Check(plan.Resolve(2, "power-2", true, "performed", "read back", "lamp", true) && plan.State == "completed", "only actual second result completes");
        var replaced = new ActorPlanRuntime("旧目标"); var replacement = new ActorPlanRuntime("新目标");
        replaced.Cancel("replaced"); replaced.Cancel("old coroutine returned");
        Check(replacement.Active, "stale watcher cancels its own plan only");
        var failed = new ActorPlanRuntime("关灯");
        failed.Propose(1, "set-object-state", "唱歌", "off", out _); failed.BindAction(1, "fail");
        Check(failed.Resolve(1, "fail", false, "rejected", "out-of-reach", "lamp", false) && !failed.Active && failed.RemainingGoal == "关灯", "failure retains unmet goal and stops continuation");
        var reference = new ActorPlanRuntime("靠近柜子看清小灯，然后关小灯");
        reference.Propose(1, "approach-object", "确认小灯并关掉", "reference", out _); reference.BindAction(1,"ref");
        reference.Resolve(1,"ref",true,"arrived","","cabinet",false);
        Check(reference.EntityId == null, "observe-closer reference cannot bind intended small object");
        var ongoing = new ActorPlanRuntime("一直跟随"); ongoing.Propose(1,"follow","","follow::",out _); ongoing.BindAction(1,"follow-owned");
        ongoing.Resolve(1,"follow-owned",true,"follow-waiting","caught up, not finished",null,false); ongoing.MarkOngoing("continuous task");
        Check(ongoing.State == "ongoing" && ongoing.RemainingGoal.Length > 0 && ongoing.HasCompleted("follow::"), "continuous follow is retained, never falsely completed or reissued");
        var source = new RoomTaskProtocol.ActorProposalSource { Id = "draft-1", Revision = 1, Text = "先走近那盏灯，再关掉它。", Purpose = "plan", PlanId = "plan-1", PlanRevision = 2, BoundEntityId = "lamp-1" };
        var step = new JObject { ["intent"] = "set-object-state", ["origin"] = "actor_draft", ["evidence"] = "再关掉它",
            ["actorDraftId"] = source.Id, ["actorDraftRevision"] = 1, ["planId"] = source.PlanId, ["planRevision"] = 2,
            ["planRemaining"] = "", ["entityTarget"] = new JObject { ["captureId"] = "fresh", ["entityId"] = "lamp-1" }, ["objectState"] = "off" };
        Check(RoomTaskProtocol.TryDecision(step.ToString(Formatting.None), "嗯", false, source, out var decision, out _), "same bound entity is executable proposal");
        step["entityTarget"]["entityId"] = "lamp-2";
        Check(!RoomTaskProtocol.TryDecision(step.ToString(Formatting.None), "嗯", false, source, out _, out _), "next step cannot silently switch lamps");
        step["entityTarget"]["entityId"] = "lamp-1"; step["planRevision"] = 1;
        Check(!RoomTaskProtocol.TryDecision(step.ToString(Formatting.None), "嗯", false, source, out _, out _), "old plan revision cannot execute");
        step["planRevision"] = 2; step.Remove("planRemaining");
        Check(!RoomTaskProtocol.TryDecision(step.ToString(Formatting.None), "嗯", false, source, out _, out _), "missing remainder cannot silently finish goal");
        Check(!RoomTaskProtocol.TryDecision(step.ToString(Formatting.None), "嗯", false, null, out _, out _), "unbound model plan has no authority");
        var close = new JObject { ["intent"]="none", ["origin"]="actor_draft", ["evidence"]="再关掉它",
            ["actorDraftId"]=source.Id, ["actorDraftRevision"]=1, ["planId"]=source.PlanId,
            ["planRevision"]=2, ["planRemaining"]="" };
        Check(RoomTaskProtocol.TryDecision(close.ToString(),"嗯",false,source,out decision,out _) && decision.origin=="none" && decision.evidence=="",
            "Exact bound actor citation on none legally closes planning without gaining action authority.");
        close["actorDraftId"]="other";
        Check(!RoomTaskProtocol.TryDecision(close.ToString(),"嗯",false,source,out _,out _),"No-action still rejects wrong actor identity.");
        close["actorDraftId"]=source.Id; close["planRevision"]=1;
        Check(!RoomTaskProtocol.TryDecision(close.ToString(),"嗯",false,source,out _,out _),"No-action still rejects stale plan revision.");
        close["planRevision"]=2; close["evidence"]="历史里才出现的句子";
        Check(!RoomTaskProtocol.TryDecision(close.ToString(),"嗯",false,source,out _,out _),"No-action still rejects historical evidence.");
        close["evidence"]="再关掉它"; close["visualTarget"]=new JObject();
        Check(!RoomTaskProtocol.TryDecision(close.ToString(),"嗯",false,source,out _,out _),"No-action cannot smuggle target fields.");
        close.Remove("visualTarget"); close["intent"]="follow"; close["planContinuation"]="parallel"; close["planRemaining"]="同时唱刚才的歌";
        Check(RoomTaskProtocol.TryDecision(close.ToString(),"嗯",false,source,out decision,out _) && decision.planContinuation=="parallel",
            "Typed independent goal survives follow parse.");
        close["planRemaining"]="";
        Check(!RoomTaskProtocol.TryDecision(close.ToString(),"嗯",false,source,out _,out _),"Parallel continuation requires a preserved dependent goal.");
        close["planContinuation"]="ongoing";
        Check(RoomTaskProtocol.TryDecision(close.ToString(),"嗯",false,source,out decision,out _),"Plain ongoing follow does not invent a tail.");
        close.Remove("planContinuation");
        Check(RoomTaskProtocol.TryDecision(close.ToString(),"嗯",false,source,out decision,out _) && decision.planContinuation==null,"Older follow payload is unspecified, not silently parallel.");
        close["intent"]="none"; close["planContinuation"]="ongoing";
        Check(!RoomTaskProtocol.TryDecision(close.ToString(),"嗯",false,source,out _,out _),"Continuation is scoped to plan-owned follow only.");
        var closure = new ActorPlanRuntime("关灯");
        Check(!closure.CloseWithoutNextStep(1,""),"A none with no results cannot manufacture successful execution.");
        closure.Propose(1,"set-object-state","关灯","off",out _);closure.BindAction(1,"off-1");closure.Resolve(1,"off-1",true,"performed","verified","lamp-1",true);
        Check(!closure.CloseWithoutNextStep(1,"") && !closure.CloseWithoutNextStep(2,"唱歌"),"Closure cannot drop a real non-spatial remainder or stale revision.");
        Check(closure.CloseWithoutNextStep(2,"") && closure.State=="completed" && closure.Results.Count==1 && closure.EntityId=="lamp-1",
            "A finite plan closes once, preserving actual result and entity without inventing another result.");
        Check(!closure.CloseWithoutNextStep(3,""),"Duplicate closure has no authority.");
        var dependent = new ActorPlanRuntime("跟随结束后唱歌");dependent.Propose(1,"follow","唱歌","follow",out _);dependent.BindAction(1,"follow-1");dependent.Resolve(1,"follow-1",true,"moving","running",null,false);dependent.MarkOngoing("awaiting finite end condition");
        Check(dependent.State=="ongoing" && dependent.RemainingGoal=="唱歌","An unsupported follow end condition preserves the exact tail without executing it.");
        foreach (JObject branch in JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null, source))["oneOf"])
        {
            Check(((JArray)branch["required"]).Values<string>().Contains("planRemaining"), "all scoped branches retain remainder");
            if (((JArray)branch["properties"]["intent"]["enum"]).Values<string>().Contains("follow"))
                Check(((JArray)branch["required"]).Values<string>().Contains("planContinuation") && ((JArray)branch["properties"]["intent"]["enum"]).Count==1,
                    "Production follow grammar requires its typed continuation without advertising it on other actions.");
        }
        DeviceCompletionChecks();
        ObservedDeviceCompletionChecks();
        CompiledGoalChecks();
        Console.WriteLine("Actor plan PASS: " + checks + " checks");
        if (args.Length > 0) Export(args[0]);
        if (args.Length > 1) Evaluate(args[1]);
    }
    static void ObservedDeviceCompletionChecks()
    {
        const string goal="那你有看到桌子上那个小的发光装置吗？能去把它关掉吗？";
        ActorPlanRuntime Observed(string origin="user", string coverage="unknown", string intent="inspect-object")
        {
            var p=new ActorPlanRuntime(goal,origin,coverage);
            Check(p.Propose(1,intent,goal,"observe",out _) && p.BindAction(1,"observe-1"),"observation has its own actual action owner");
            p.Resolve(1,"observe-1",true,intent=="inspect-object"?"observed":"arrived","measured","lamp-1",true);
            return p;
        }
        var p=Observed();
        Check(!p.AdoptDeviceCompletion(1,"lamp-1","off",goal,"user") && !p.AdoptDeviceCompletion(2,"lamp-2","off",goal,"user"),
            "late condition must bind the current observation and same entity");
        var source=RoomTaskProtocol.ReadActorProposal(new JObject{["source"]="current-user-goal",["goalId"]=p.Id,["goalRevision"]=1,["text"]=goal,["purpose"]="plan",["actorPlan"]=p.Facts()});
        Check(source.PlanOnlyObservedBoundEntity && source.PlanRevision==2,"room source retains only observed target lineage");
        var wire=new JObject{["intent"]="set-object-state",["origin"]="user",["evidence"]="能去把它关掉吗",["planId"]=p.Id,["planRevision"]=2,["planRemaining"]=goal,
            ["entityTarget"]=new JObject{["captureId"]="fresh",["entityId"]="lamp-1"},["objectState"]="off",
            ["planCompletion"]=new JObject{["coverage"]="atomic",["goalText"]=goal,["entityId"]="lamp-1",["state"]="off"}};
        Check(RoomTaskProtocol.TryDecision(wire.ToString(),goal,false,source,out var d,out _),"production room parser accepts pre-operation condition after identifying an unknown lamp");
        Check(p.AdoptDeviceCompletion(d.planRevision,d.planCompletion.entityId,d.planCompletion.state,d.planCompletion.goalText,d.origin),"condition is adopted before any device write");
        Check(p.Propose(2,"set-object-state",goal,"off:lamp-1",out _) && p.BindAction(2,"off-2"),"actual operation follows observed target");
        Check(p.Resolve(2,"off-2",true,"performed","readback","lamp-1",true,"off") && p.State=="completed" && p.Results.Count==2 && p.RemainingGoal=="",
            "inspect→set verified result closes original goal without General or duplicate operation");
        var restored=new ActorPlanRuntime(goal,"user");
        Check(restored.RestoreCompletedCheckpoint(p.Facts()) && restored.CloseWithoutNextStep(restored.Revision,""),"late pre-operation condition survives interrupted same-goal checkpoint");
        var forged=p.Facts(); forged["results"][0]["intent"]="set-object-state";
        Check(!new ActorPlanRuntime(goal,"user").RestoreCompletedCheckpoint(forged),"completed writes cannot be relabelled as observation prerequisites");
        Check(!Observed("actor_draft","compound").AdoptDeviceCompletion(2,"lamp-1","off",goal,"actor_draft"),"observation cannot erase an explicit compound role goal");
        Check(!Observed("user","unknown","approach-object").AdoptDeviceCompletion(2,"lamp-1","off",goal,"user"),"new closure cannot reinterpret already executed movement as a mere observation");
        Check(!Observed("actor_draft","unknown").AdoptDeviceCompletion(2,"lamp-1","off",goal,"actor_draft"),"unknown actor scope still requires semantic adoption");
        var distant=Observed();
        var distantSource=RoomTaskProtocol.ReadActorProposal(new JObject{["source"]="current-user-goal",["goalId"]=distant.Id,["goalRevision"]=1,["text"]=goal,["purpose"]="plan",["actorPlan"]=distant.Facts()});
        wire["planId"]=distant.Id; wire["intent"]="approach-object";wire.Remove("objectState");wire["planRemaining"]="关闭同一盏灯";
        Check(RoomTaskProtocol.TryDecision(wire.ToString(),goal,false,distantSource,out d,out _),"distance prerequisite may declare final device goal before approaching");
        Check(distant.AdoptDeviceCompletion(2,"lamp-1","off",goal,"user"),"approach cannot infer the final condition after moving");
        distant.Propose(2,"approach-object","关闭同一盏灯","approach",out _);distant.BindAction(2,"walk");distant.Resolve(2,"walk",true,"arrived","measured","lamp-1",true);
        Check(distant.State=="planning"&&!distant.CloseWithoutNextStep(3,""),"arrival never fulfils the pending device goal");
        distant.Propose(3,"set-object-state",goal,"off",out _);distant.BindAction(3,"power");distant.Resolve(3,"power",true,"performed","readback","lamp-1",true,"off");
        Check(distant.State=="completed"&&distant.Results.Count==3,"inspect→approach→verified set closes final goal once");
        var actorObserved=Observed("actor_draft","atomic");
        var actorSource=RoomTaskProtocol.ReadActorProposal(new JObject{["source"]="unpublished-current-role-draft",["draftId"]="draft-a",["draftRevision"]=1,["text"]=goal,["purpose"]="plan",["executed"]=false,["published"]=false,["coverage"]="atomic",["actorPlan"]=actorObserved.Facts()});
        wire["planId"]=actorObserved.Id;wire["origin"]="actor_draft";wire["actorDraftId"]="draft-a";wire["actorDraftRevision"]=1;
        Check(RoomTaskProtocol.TryDecision(wire.ToString(),"请照做",false,actorSource,out d,out _),"already adopted atomic role goal can bind its newly observed device before approach");
        Check(actorObserved.AdoptDeviceCompletion(2,"lamp-1","off",goal,"actor_draft"),"runtime uses unchanged role source for late observed binding");
        var oldWrite=new ActorPlanRuntime(goal,"user");oldWrite.Propose(1,"set-object-state",goal,"off",out _);oldWrite.BindAction(1,"old-off");oldWrite.Resolve(1,"old-off",true,"performed","old untyped result","lamp-1",true);
        var newlyTyped=new ActorPlanRuntime(goal,"user");newlyTyped.AdoptDeviceCompletion(1,"lamp-1","off",goal,"user");
        Check(!newlyTyped.RestoreCompletedCheckpoint(oldWrite.Facts())&&newlyTyped.Results.Count==0&&!newlyTyped.HasCompleted("off"),
            "new condition cannot borrow unverifiable old write or its replay fence and deadlock current readback");
    }
    static void CompiledGoalChecks()
    {
        const string draft="好，我现在就转过去。", full="向右原地转身90度，再确认刚才的小灯并把它打开。";
        var plan=new ActorPlanRuntime(draft,"actor_draft","compound",full);
        Check(plan.OriginalGoal==draft && plan.CompiledGoal==full && plan.RemainingGoal==draft,
            "The full semantic goal is retained separately without replacing the original role authority.");
        plan.Propose(1,"turn-in-place","确认同一小灯并打开","turn:90",out _);plan.BindAction(1,"turn-1");
        plan.Resolve(1,"turn-1",true,"turned","actual turn",null,false);
        var checkpoint=plan.Facts();
        Check((string)checkpoint["originalGoal"]==draft && (string)checkpoint["compiledGoal"]==full &&
            (string)checkpoint["remainingGoal"]=="确认同一小灯并打开", "Step updates/checkpoints retain both original draft and complete interpretation.");
        var recovered=new ActorPlanRuntime(draft,"actor_draft","compound");
        Check(recovered.RestoreCompletedCheckpoint(checkpoint) && recovered.CompiledGoal==full &&
            recovered.OriginalGoal==draft && recovered.RemainingGoal=="确认同一小灯并打开" && recovered.Results.Count==1,
            "Same-goal recovery keeps the full interpreted angle and later device goal instead of only the abbreviated draft.");
        var same=new ActorPlanRuntime(draft,"actor_draft","compound",full);
        Check(same.RestoreCompletedCheckpoint(checkpoint),"An identical current interpretation may restore its own checkpoint.");
        var differentDraft=new ActorPlanRuntime("我改主意，去看窗外。","actor_draft","atomic",full);
        Check(!differentDraft.RestoreCompletedCheckpoint(checkpoint) && differentDraft.Results.Count==0 && differentDraft.EntityId==null,
            "A new role draft cannot borrow old execution results merely by repeating its semantic summary.");
        var differentMeaning=new ActorPlanRuntime(draft,"actor_draft","atomic","转向用户并继续聊天。");
        Check(!differentMeaning.RestoreCompletedCheckpoint(checkpoint) && differentMeaning.Results.Count==0 &&
            differentMeaning.CompiledGoal=="转向用户并继续聊天。", "Identical shorthand with a new explicit meaning cannot borrow a prior compound checkpoint.");
        var legacy=checkpoint.DeepClone() as JObject;legacy.Remove("compiledGoal");
        var legacyRecovery=new ActorPlanRuntime(draft,"actor_draft","compound",full);
        Check(legacyRecovery.RestoreCompletedCheckpoint(legacy) && legacyRecovery.CompiledGoal==full,
            "Older checkpoints do not erase a current complete interpretation when no historical summary exists.");
    }
    static void DeviceCompletionChecks()
    {
        const string goal = "我这就把那盏灯重新打开。";
        ActorPlanRuntime Adopt(string coverage = "atomic")
        {
            var p = new ActorPlanRuntime(goal, "actor_draft", coverage);
            Check(p.AdoptDeviceCompletion(1,"lamp-1","on",goal,"actor_draft") == (coverage == "atomic"),
                "Only explicitly atomic actor adoption owns a device completion condition: " + coverage);
            return p;
        }
        var complete = Adopt();
        Check(!complete.AdoptDeviceCompletion(1,"lamp-2","on",goal,"actor_draft") &&
            !complete.AdoptDeviceCompletion(1,"lamp-1","off",goal,"actor_draft"),"An adopted target/state cannot be rewritten in place.");
        complete.Propose(1,"set-object-state",goal,"lamp-1:on",out _); complete.BindAction(1,"power");
        Check(!complete.Resolve(0,"power",true,"performed","readback","lamp-1",true,"on") &&
            !complete.Resolve(1,"wrong-owner",true,"performed","readback","lamp-1",true,"on"),"Device completion still checks revision and action identity.");
        complete.Resolve(1,"power",true,"performed","readback","lamp-1",true,"on");
        Check(complete.State=="completed" && complete.RemainingGoal=="" && complete.Results.Count==1 &&
            (string)complete.Results[0]["verifiedDeviceState"]=="on", "Matching initial atomic condition closes repeated original wording without another planner.");
        Check(!complete.AdoptDeviceCompletion(2,"lamp-1","off",goal,"actor_draft"),"No completion condition may be invented after execution.");
        var recovered = new ActorPlanRuntime(goal,"actor_draft");
        Check(recovered.RestoreCompletedCheckpoint(complete.Facts()) && recovered.GoalCoverage=="atomic" &&
            (string)recovered.DeviceCompletionFacts()["planId"]==recovered.Id &&
            recovered.CloseWithoutNextStep(recovered.Revision,""),
            "Same accepted goal restores its verified condition under the new plan owner for historical closure only.");
        var conflicting=Adopt();
        var badCheckpoint=complete.Facts();badCheckpoint["completionCondition"]["state"]="off";
        Check(!conflicting.RestoreCompletedCheckpoint(badCheckpoint),"A checkpoint cannot replace a newly adopted target state.");
        var unfinished=Adopt();unfinished.Propose(1,"inspect-object","", "read",out _);unfinished.BindAction(1,"read");
        unfinished.Resolve(1,"read",true,"observed","read","lamp-1",true);
        var restoreUnfinished=new ActorPlanRuntime(goal,"actor_draft");
        Check(restoreUnfinished.RestoreCompletedCheckpoint(unfinished.Facts()) && restoreUnfinished.RemainingGoal==goal &&
            restoreUnfinished.DeviceCompletionFacts()!=null && !restoreUnfinished.CloseWithoutNextStep(restoreUnfinished.Revision,""),
            "Recovery preserves an unmet typed condition; inspection or empty remainder cannot manufacture device completion.");
        var userCondition=new ActorPlanRuntime(goal,"user");userCondition.AdoptDeviceCompletion(1,"lamp-1","on",goal,"user");
        userCondition.Propose(1,"inspect-object",goal,"read",out _);userCondition.BindAction(1,"read");userCondition.Resolve(1,"read",true,"observed","read","lamp-1",true);
        var restoredUser=new ActorPlanRuntime(goal,"user");
        Check(restoredUser.RestoreCompletedCheckpoint(userCondition.Facts()) && restoredUser.GoalCoverage=="atomic" &&
            restoredUser.DeviceCompletionFacts()!=null && !restoredUser.CloseWithoutNextStep(restoredUser.Revision,""),
            "User-plan recovery beyond revision one restores the original condition without another model adoption.");
        foreach(string invalid in new[]{"missing-readback","wrong-state","wrong-entity","unconfirmed","extra-tail","inspection-only"})
        {
            var p=Adopt(); string tail=invalid=="extra-tail"?"然后唱刚才的录音":goal;
            p.Propose(1,invalid=="inspection-only"?"inspect-object":"set-object-state",tail,"step",out _);p.BindAction(1,"power");
            p.Resolve(1,"power",true,invalid=="unconfirmed"?"state-applied-unconfirmed":"performed","observed",
                invalid=="wrong-entity"?"lamp-2":"lamp-1",true,
                invalid=="missing-readback"?null:invalid=="wrong-state"?"off":"on");
            Check(p.State!="completed" && !string.IsNullOrWhiteSpace(p.RemainingGoal),"Unsafe result cannot complete adopted device goal: "+invalid);
            Check(!p.CloseWithoutNextStep(p.Revision,""),"none cannot bypass the unverified device completion condition: "+invalid);
        }
        foreach(string coverage in new[]{"compound","unknown"})
        {
            var p=Adopt(coverage);p.Propose(1,"set-object-state","然后转身唱歌","step",out _);p.BindAction(1,"power");
            p.Resolve(1,"power",true,"performed","ok","lamp-1",true,"on");
            Check(p.State=="planning" && p.RemainingGoal=="然后转身唱歌","Successful device step never erases compound/unknown adopted remainder.");
        }
        var already=Adopt();already.Propose(1,"set-object-state",goal,"on",out _);already.BindAction(1,"same");
        already.Resolve(1,"same",true,"already-satisfied","already on","lamp-1",true,"on");
        Check(already.State=="completed" && (string)already.Results[0]["phase"]=="already-satisfied","Goal can already be satisfied without manufacturing a new switch operation.");
        var source=new RoomTaskProtocol.ActorProposalSource { Id="input-1",Revision=1,Origin="user",Purpose="plan",Text=goal,PlanId="plan-1",PlanRevision=1 };
        var wire=new JObject { ["intent"]="set-object-state",["origin"]="user",["evidence"]=goal,
            ["planId"]=source.PlanId,["planRevision"]=1,["planRemaining"]=goal,["objectState"]="on",
            ["entityTarget"]=new JObject{["captureId"]="fresh",["entityId"]="lamp-1"},
            ["planCompletion"]=new JObject{["coverage"]="atomic",["goalText"]=goal,["entityId"]="lamp-1",["state"]="on"} };
        Check(RoomTaskProtocol.TryDecision(wire.ToString(),goal,false,source,out var d,out _) && d.planCompletion?.entityId=="lamp-1",
            "First full-source user decision may adopt a typed exact-entity finite condition before any result.");
        Check(JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null,source))["properties"]["planCompletion"]!=null,
            "Initial user schema advertises optional device completion.");
        foreach(string invalid in new[]{"revision","results","compound","tail","goal","entity","state","null","visual"})
        {
            var bad=(JObject)wire.DeepClone();source.PlanRevision=1;source.PlanResultCount=0;source.Coverage=null;
            if(invalid=="revision"){source.PlanRevision=2;bad["planRevision"]=2;}
            else if(invalid=="results") source.PlanResultCount=1;
            else if(invalid=="compound") source.Coverage="compound";
            else if(invalid=="tail") bad["planRemaining"]="唱歌";
            else if(invalid=="goal") bad["planCompletion"]["goalText"]="历史目标";
            else if(invalid=="entity") bad["planCompletion"]["entityId"]="lamp-2";
            else if(invalid=="state") bad["planCompletion"]["state"]="off";
            else if(invalid=="null") bad["planCompletion"]=null;
            else {bad.Remove("entityTarget");bad["visualTarget"]=new JObject{["captureId"]="fresh",["x"]=.5,["y"]=.5,["label"]="灯"};}
            Check(!RoomTaskProtocol.TryDecision(bad.ToString(),goal,false,source,out _,out _),"Invalid/late/partial completion declaration rejected: "+invalid);
        }
    }
    static void Export(string path)
    {
        var cases = new JArray();
        for (int i=0;i<3;i++)
        {
            var source = new RoomTaskProtocol.ActorProposalSource { Id="role-owned", Revision=1, Text="我先走到你身边，再一直跟着你。", Purpose="plan", PlanId="plan-owned", PlanRevision=i+1 };
            var facts = new JObject { ["id"]=source.PlanId,["revision"]=source.PlanRevision,["originalGoal"]=source.Text,
                ["remainingGoal"]=i==0?source.Text:i==1?"持续跟随当前用户":"唱出刚才的录音",
                ["results"]=i==0?new JArray():new JArray(new JObject{["intent"]="approach",["phase"]="arrived",["succeeded"]=true}),
                ["rule"]="Only choose next uncompleted step; preserve remaining goal. No image available; cannot identify visual objects." };
            if(i==2)source.Text="我先走到你身边，再唱刚才的录音。";
            var input=new JObject { ["currentUserText"]="嗯。",["autonomous"]=false,
                ["actorProposal"]=new JObject{["source"]="unpublished-current-role-draft",["draftId"]=source.Id,["draftRevision"]=1,["text"]=source.Text,["purpose"]="plan",["executed"]=false,["published"]=false,["actorPlan"]=facts},
                ["currentFacts"]=new JObject{["currentTargetRevision"]=2,["hasCurrentUserTarget"]=true},["visualObservation"]=null };
            cases.Add(new JObject{["id"]="room-plan-"+(i+1),["scope"]="room",["expectedIntent"]=i==0?"approach":i==1?"follow":"none",
                ["messages"]=new JArray(new JObject{["role"]="system",["content"]=RoomTaskProtocol.DecisionContract},new JObject{["role"]="user",["content"]=input.ToString(Formatting.None)}),
                ["response_format"]=new JObject{["type"]="json_schema",["json_schema"]=new JObject{["name"]="room_plan",["strict"]=true,["schema"]=JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null,source))}}});
        }
        File.WriteAllText(path,new JObject{["cases"]=cases}.ToString(Formatting.Indented));
    }
    static void HandoffChecks()
    {
        var source = new RoomTaskProtocol.ActorProposalSource { Id="handoff-role", Revision=1, Text="我先开灯，再继续跟着你。",
            Origin="actor_draft", Purpose="plan", PlanId="handoff-plan", PlanRevision=1 };
        var value = new JObject { ["intent"]="set-object-state", ["origin"]="actor_draft", ["evidence"]=source.Text,
            ["actorDraftId"]=source.Id,["actorDraftRevision"]=1,["planId"]=source.PlanId,["planRevision"]=1,["planRemaining"]="",
            ["entityTarget"]=new JObject { ["captureId"]="frame",["entityId"]="lamp" },["objectState"]="on",
            ["bodyHandoff"]=new JObject { ["taskId"]="owned-follow",["mode"]="pause" } };
        foreach (string mode in new[]{"pause","yield"})
        {
            value["bodyHandoff"]["mode"]=mode;
            Check(RoomTaskProtocol.TryDecision(value.ToString(),"开灯",false,source,out var d,out _,false,"owned-follow") &&
                d.bodyHandoff.taskId=="owned-follow" && d.bodyHandoff.mode==mode,
                "A current adopted plan can explicitly arrange the exact following task without claiming a user stop.");
        }
        foreach (string task in new[]{null,"replacement-follow"})
            Check(!RoomTaskProtocol.TryDecision(value.ToString(),"开灯",false,source,out _,out _,false,task),
                "Missing/replaced follow cannot inherit a handoff permission.");
        value["bodyHandoff"]["mode"]="cancel_everything";
        Check(!RoomTaskProtocol.TryDecision(value.ToString(),"开灯",false,source,out _,out _,false,"owned-follow"),"Unknown body handoff mode is rejected.");
        value["bodyHandoff"]["mode"]="pause_resume";
        Check(!RoomTaskProtocol.TryDecision(value.ToString(),"开灯",false,source,out _,out _,false,"owned-follow"),
            "The old automatic-restoration mode is not a valid execution contract.");
        var with = JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null,source,false,"owned-follow"));
        var without = JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null,source));
        Check(with["properties"]["bodyHandoff"]["properties"]["taskId"]["enum"].Single().Value<string>()=="owned-follow" &&
            without["properties"]["bodyHandoff"]==null,"Wire exposes only this request's live follow identity.");
        value.Remove("bodyHandoff");
        Check(!RoomTaskProtocol.TryDecision(value.ToString(),"开灯",false,source,out _,out string missingHandoff,false,"owned-follow") &&
            missingHandoff=="body-handoff-required","An adopted mutually exclusive body action cannot silently omit task scheduling.");
        var lampBranches=with["oneOf"].Where(b=>b["properties"]["intent"]["enum"].Values<string>().Contains("set-object-state")).ToArray();
        Check(lampBranches.Length>0 && lampBranches.All(b=>b["required"].Values<string>().Contains("bodyHandoff")),
            "Every mutually exclusive lamp branch requires an explicit model-selected handoff.");
        Check(RoomTaskProtocol.TryDecision(value.ToString(),"开灯",false,source,out _,out _,false,null),
            "No-follow operation keeps its existing grammar and does not invent a handoff.");
        var attempt=new ActorPlanRuntime("开灯");
        Check(attempt.Propose(1,"set-object-state","","lamp",out _) &&
            attempt.RejectUnsubmitted(1,"set-object-state","not-submitted: existing-body-task-has-priority"),"Production plan retains a submission rejection.");
        Check(attempt.State=="blocked" && attempt.PendingActionId==null && attempt.Results.Count==1 &&
            attempt.Results[0]["actionId"].Type==JTokenType.Null && (bool)attempt.Results[0]["submitted"]==false &&
            (string)attempt.Results[0]["attemptId"]==attempt.Id+":attempt:1" && attempt.RemainingGoal=="开灯",
            "Rejection has a separate attempt identity, no invented executed action and full unmet goal.");
        Check(!attempt.RejectUnsubmitted(1,"set-object-state","duplicate") && attempt.Results.Count==1,
            "A duplicate rejection cannot publish a second attempt result.");
        var submitted=new ActorPlanRuntime("开灯");submitted.Propose(1,"set-object-state","","lamp",out _);submitted.BindAction(1,"real-action");
        Check(!submitted.RejectUnsubmitted(1,"set-object-state","late-rejection"),"An actually bound action cannot become a not-submitted attempt.");
    }

    static void Evaluate(string path)
    {
        var output=new JArray();
        foreach(string line in File.ReadLines(path))
        {
            var row=JObject.Parse(line); string draft=(string)row["draft"];
            bool valid=ActorIntentProtocol.TryCompile((string)row["raw"],draft,out var d,out var reason);
            string route=!valid?"rejected":d.Kind=="none"?"reply":d.Kind=="claim"?"verify-only":
                d.Kind=="replan"||d.Domain=="head"&&d.Kind=="act"&&(d.Evidence??"").Trim()!=draft.Trim()?"complete-planner":d.Kind;
            output.Add(new JObject{["id"]=row["id"],["valid"]=valid,["route"]=route,["reason"]=reason,["goal"]=d?.Goal,["kind"]=d?.Kind,["domain"]=d?.Domain,["target"]=d?.Target});
        }
        File.WriteAllText(path+".compiled.json",output.ToString(Formatting.Indented));
    }
}
