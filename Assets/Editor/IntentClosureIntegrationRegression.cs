using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class IntentClosureIntegrationRegression
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;
    public static int RunChecks()
    {
        checks = 0;
        const string goal = "向左转九十度，找到那盏装饰灯，靠近它并关掉。";
        using (var f = new Fixture(goal))
        {
            f.DispatchLegacy();
            var plan = (ActorPlanRuntime)Get(f.Chat, "m_ActorPlan");
            var source = RoomTaskProtocol.ReadActorProposal(JObject.Parse(f.Model.LastRoomInput)["actorProposal"]);
            Check(plan != null && plan.OriginalGoal == goal && source?.Origin == "user", "Legacy room handoff retains the complete user goal with user authority.");
            var decision = new JObject { ["intent"] = "turn-in-place", ["origin"] = "user", ["evidence"] = "向左转九十度",
                ["viewChange"] = new JObject { ["captureId"] = "view-1", ["yaw"] = -90, ["pitch"] = 0 },
                ["planId"] = plan.Id, ["planRevision"] = plan.Revision, ["planRemaining"] = "找到那盏装饰灯，靠近它并关掉。" };
            Check(RoomTaskProtocol.TryDecision(decision.ToString(), goal, false, source, out var step, out _), "A user step keeps plan metadata without impersonating an actor draft.");
            decision["origin"] = "actor_draft";
            Check(!RoomTaskProtocol.TryDecision(decision.ToString(), goal, false, source, out _, out _), "A user plan cannot acquire actor origin.");
            object[] accept = { step, f.Generation, null };
            Check((bool)Call(f.Chat, "AcceptActorPlanStep", accept) && plan.BindAction(1, "turned"), "Real plan acceptance records the first step and exact action id.");
            Check(plan.Resolve(1, "turned", true, "turned", "fresh view", null, false) && plan.Active &&
                plan.RemainingGoal.Contains("关掉"), "Successful turn does not complete the remaining device goal.");
            var restored = new ActorPlanRuntime(goal, "user");
            Check(restored.RestoreCompletedCheckpoint(plan.Facts()) && restored.Results.Count == 1 &&
                !restored.Propose(restored.Revision, step.intent, "关掉", "turn-in-place:::-90,0", out _),
                "An invalid-input recovery retains completed relative turns and cannot submit the same step again.");
            var changed = new ActorPlanRuntime("我决定去看书。", "actor_draft");
            Check(!changed.RestoreCompletedCheckpoint(plan.Facts()) && changed.EntityId == null,
                "A different autonomous role choice cannot inherit the old device identity or completed effects.");
            source.PlanRevision = plan.Revision;
            var none = new JObject { ["intent"] = "none", ["origin"] = "none", ["evidence"] = "",
                ["planId"] = plan.Id, ["planRevision"] = plan.Revision, ["planRemaining"] = plan.RemainingGoal };
            Check(RoomTaskProtocol.TryDecision(none.ToString(), goal, false, source, out _, out _), "A retained user goal still permits no adopted action.");
        }
        using (var f = new Fixture(goal))
        {
            var model = f.Chat.gameObject.AddComponent<IntentRecoveryIntegrationModel>();
            ((ChatSetting)Get(f.Chat, "m_ChatSettings")).m_ChatModel = model;
            var accepted = new LLM.SendData("user", goal); model.m_DataList.Add(accepted);
            Set(f.Chat, "m_RecoveredHistoryUser", accepted);
            Set(f.Chat, "m_InterruptedActorInputObserved", true);
            Set(f.Chat, "m_EnableActorIntentCompiler", true);
            BindVerifiedTurnRecovery(f.Chat, goal);
            Call(f.Chat, "DispatchRecoveredInput", goal, "已完成左转；只考虑找灯和关闭。", null, f.Generation);
            Check(model.Pending.Count == 1 && model.FormalCalls == 0 && model.Resumes == 1,
                "Recovery enters the real normal FastLoop entry, never the legacy feedback speech stream.");
            Check(model.Pending[0].CurrentUser == goal && HasVerifiedTurnRecovery(JObject.Parse(model.Pending[0].Context), goal),
                "Recovery passes the entire original user request and the verified completed step, remainder and heard prefix as structured facts.");
            Check((bool)JObject.Parse(model.Pending[0].Context)["inputRecovery"]["sameUserEvent"],
                "Recovery facts remain a structured object for initial and repeated-draft requests.");
            Check((int)Get(f.Chat,"m_ActorConditionObservedGeneration") == f.Generation,
                "Recovery is not a second counting/condition event.");
            model.Pending[0].Answer(new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Language = "zh", Speech = "我听到了。" }, "ok");
            Check(model.ActorPending.Count == 1 && f.Queued() == "", "Recovered text is withheld for the same actor compiler as an ordinary reply.");
            model.ActorPending[0].Answer(new ActorIntentDecision { Domain="perception",Kind="none",Action="none",Condition="none",Task="none",Target="none",Coverage="none",Goal="",Evidence="" }, "ok");
            Check(f.Queued().Contains("我听到了") && model.m_DataList.Count(x=>x.role=="user") == 1 &&
                model.m_DataList.Count(x=>x.role=="assistant") == 1, "Approved recovered reply is published once without duplicate user history.");
        }
        using (var f = new Fixture(goal))
        {
            var qwen = f.Chat.gameObject.AddComponent<ChatQW>();
            var accepted = new LLM.SendData("user", goal); qwen.m_DataList.Add(accepted);
            Check(qwen.ResumeFastLoopHistory(accepted,null) && qwen.m_DataList.Count == 1,
                "Production provider rebinds the exact accepted history object without appending it.");
            Check(ReferenceEquals(Call(qwen,"ClaimFirstReplyUserHandoff"),accepted) && Call(qwen,"ClaimFirstReplyUserHandoff")==null,
                "Recovered legacy handoff consumes the same history lease exactly once.");
            var repeated = new LLM.SendData("user",goal); qwen.m_DataList.Add(repeated);
            Check(!qwen.ResumeFastLoopHistory(accepted,null) && qwen.ResumeFastLoopHistory(repeated,null),
                "Equal text cannot let an old accepted user seize a newer turn.");
        }
        using (var f = new Fixture(goal))
        {
            var model = f.Chat.gameObject.AddComponent<IntentRecoveryIntegrationModel>();
            model.AllowInitialHistory = true;
            ((ChatSetting)Get(f.Chat, "m_ChatSettings")).m_ChatModel = model;
            var previous = new LLM.SendData("user", "上一轮问题"); model.m_DataList.Add(previous);
            Set(f.Chat, "m_UserTurnAwaitingReplySince", 1f);
            Call(f.Chat, "PreservePendingReplyForInputCandidate");
            Check(Get(f.Chat,"m_InterruptedHistoryUser") == null && (bool)Get(f.Chat,"m_InterruptedHistoryNotStarted"),
                "Input interrupted before observation cannot borrow the previous question's history owner.");
            string context = (string)Call(f.Chat,"ConsumeInterruptedReplyContext");
            Call(f.Chat,"DispatchRecoveredInput",goal,context,null,f.Generation);
            Check(model.InitialHistoryCalls == 1 && model.Resumes == 1 && model.m_DataList.Count == 2 &&
                model.m_DataList[1].content == goal && !ReferenceEquals(previous,model.ResumedUser),
                "Recovery commits the as-yet-unrecorded accepted input exactly once and rebinds its own object.");
        }
        using (var f = new Fixture(goal))
        {
            var model=f.Chat.gameObject.AddComponent<IntentRecoveryIntegrationModel>();
            ((ChatSetting)Get(f.Chat,"m_ChatSettings")).m_ChatModel=model;
            var accepted=new LLM.SendData("user",goal); model.m_DataList.Add(accepted);
            Set(f.Chat,"m_RecoveredHistoryUser",accepted);
            Set(f.Chat,"m_InterruptedActorInputObserved",true);
            const string prior="我已经听到你的要求，这次会先确认眼前的实际情况，再考虑还没有完成的目标，避免重复已经执行过的步骤。";
            ((System.Collections.Generic.List<JObject>)Get(f.Chat,"m_CompletedRoleReplies")).Add(
                new JObject {["generation"]=f.Generation-1,["speechRound"]=-1,["text"]=prior});
            BindVerifiedTurnRecovery(f.Chat, goal);
            Call(f.Chat,"DispatchRecoveredInput",goal,"已完成左转；用户已听到前缀。",null,f.Generation);
            model.Pending[0].Answer(new FastLoopDecision {Route="reply",Candidate="none",Evidence="",Language="zh",Speech=prior},"ok");
            Check(model.Pending.Count==2 && JObject.Parse(model.Pending[1].Context)["dialogueRepair"]!=null &&
                HasVerifiedTurnRecovery(JObject.Parse(model.Pending[1].Context), goal) &&
                JObject.Parse(model.Pending[1].Context)["inputRecovery"]["context"] == null,
                "Actual repeated-draft review retains verified completed effects and heard speech without nesting raw perception/context.");
        }
        foreach (bool consumed in new[] { false, true })
        using (var f = new Fixture("三"))
        {
            var model = f.Chat.gameObject.AddComponent<IntentRecoveryIntegrationModel>();
            ((ChatSetting)Get(f.Chat,"m_ChatSettings")).m_ChatModel = model;
            var accepted = new LLM.SendData("user","三"); model.m_DataList.Add(accepted);
            Set(f.Chat,"m_RecoveredHistoryUser",accepted);
            Set(f.Chat,"m_InterruptedActorInputObserved",true);
            Set(f.Chat,"m_EnableActorIntentCompiler",true);
            Set(f.Chat,"m_ActorInputSequence",3L);
            var waiting=(ActorWaitingTaskRuntime)Get(f.Chat,"m_ActorWaiting");
            const string promise="等你数到三再抬头";
            Check(waiting.Register(new ActorIntentDecision {Domain="head",Kind="wait",Action="head.raise",MotionScope="head_preset",
                Condition="user_count_three",Evidence=promise,Task="none",Target="none"},
                new ActorDraft {Id="recovery-count",Revision=1,Text=promise},0,out var task,out _),"Recovery fixture arms a real finite condition.");
            waiting.Observe(task.Id,task.Revision,1,"one",out _);
            waiting.Observe(task.Id,task.Revision,2,"two",out _);
            if(consumed) waiting.Observe(task.Id,task.Revision,3,"none",out _);
            Call(f.Chat,"DispatchRecoveredInput","三","恢复原输入",null,f.Generation);
            Check((long)Get(f.Chat,"m_ActorInputSequence")==3 &&
                model.SignalPending.Count==(consumed?0:1) && model.Pending.Count==(consumed?1:0),
                "Recovery rejudges only an unconsumed condition event, keeping the original sequence: "+consumed);
        }
        return checks;
    }
    private static void BindVerifiedTurnRecovery(ChatSample chat, string goal)
    {
        var plan = new ActorPlanRuntime(goal, "user");
        Check(plan.Propose(1, "turn-in-place", "找到那盏装饰灯，靠近它并关掉。", "turn-in-place:::-90,0", out _) &&
            plan.BindAction(1, "verified-left-turn") &&
            plan.Resolve(1, "verified-left-turn", true, "turned", "已完成左转；真实控制器结果", null, false),
            "Recovery fixture creates its completed turn through the real plan result transition.");
        Set(chat, "m_InterruptedPlanFacts", plan.Facts());
        Set(chat, "m_InterruptedHeardText", "用户已听到前缀。");
    }
    private static bool HasVerifiedTurnRecovery(JObject facts, string goal)
    {
        var recovery = facts["inputRecovery"];
        var plan = recovery?["completedPlanCheckpoint"];
        var step = (plan?["results"] as JArray)?.First;
        return (string)recovery?["userText"] == goal && (string)recovery?["heardText"] == "用户已听到前缀。" &&
            (string)plan?["originalGoal"] == goal && ((string)plan?["remainingGoal"] ?? "").Contains("关掉") &&
            (string)step?["actionId"] == "verified-left-turn" && (string)step?["phase"] == "turned" &&
            (bool?)step?["succeeded"] == true &&
            (plan?["completedFingerprints"] as JArray)?.Values<string>().Contains("turn-in-place:::-90,0") == true;
    }
    private sealed class Fixture:IDisposable
    {
        private readonly object inner;
        public readonly ChatSample Chat;
        public readonly FastLoopIntegrationModel Model;
        public int Generation => (int)Get(Chat,"m_FormalResponseGeneration");
        public Fixture(string goal)
        {
            inner=Activator.CreateInstance(typeof(FastLoopIntegrationRegression).GetNestedType("Fixture",BindingFlags.NonPublic),true);
            Chat=(ChatSample)Get(inner,"Chat"); Model=(FastLoopIntegrationModel)Get(inner,"Model");
            Call(inner,"Arm",goal,false); Call(inner,"AttachControlledRoom"); Call(inner,"AttachSpeechProvider");
            Set(Chat,"m_EnableActorIntentCompiler",true);
            ((NeEEvA.Motion.ArdyActionConstraintLedger)Get(Chat,"m_ActionConstraints")).RecordUserTurn(goal);
        }
        public void DispatchLegacy() => ((FastLoopIntegrationModel.PendingRequest)Call(inner,"Dispatch",new object[]{null})).Answer(
            new FastLoopDecision { Route="legacy",Candidate="none",Evidence="",Language="zh",Speech="" },"ok");
        public string Queued() => (string)Call(inner,"Queued");
        public void Dispose()=>((IDisposable)inner).Dispose();
    }
    private static object Get(object o,string n)=>o.GetType().GetField(n,F).GetValue(o);
    private static void Set(object o,string n,object v)=>o.GetType().GetField(n,F).SetValue(o,v);
    private static object Call(object o,string n,params object[] a){try{return o.GetType().GetMethod(n,F).Invoke(o,a);}catch(TargetInvocationException e){throw e.InnerException??e;}}
    private static void Check(bool ok,string why){checks++;if(!ok)throw new InvalidOperationException(why);}
}

public sealed class IntentRecoveryIntegrationModel : FastLoopIntegrationModel, IFastLoopRecoveryHistoryBackend, IFastLoopHistoryBackend
{
    public int Resumes;
    public bool AllowInitialHistory;
    public int InitialHistoryCalls;
    public LLM.SendData ResumedUser;
    public bool ResumeFastLoopHistory(LLM.SendData user,string image)
    { if(user==null || !m_DataList.Contains(user) || !ReferenceEquals(m_DataList.LastOrDefault(x=>x.role=="user"),user)) return false; Resumes++; ResumedUser=user; return true; }
    public void BeginFastLoopHistory(string prompt,string image,bool commit)
    { if(!AllowInitialHistory) throw new InvalidOperationException("Recovery duplicated its accepted input."); InitialHistoryCalls++; if(commit)m_DataList.Add(new LLM.SendData("user",prompt)); }
    public void CompleteFastLoopHistory() { }
}
