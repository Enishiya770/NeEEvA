using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

// Production admission/queue/history/cancellation gates, plus the real plan
// watcher. Controller facts are controlled; no microphone, model, TTS or audio.
public static class ActorPlanAcceptanceRegression
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static int RunChecks()
    {
        checks = 0;
        using (var f = new Fixture())
        {
            f.Bind("walk", "moving", "继续下一步");
            Set(f.Bridge, "activeAction", false);
            Check(!f.Publish() && f.Queued == "" && f.Model.m_DataList.Count == 0,
                "A bound action id without the executor's active fact cannot publish acceptance.");
            Set(f.Bridge, "activeAction", true);
            bool inFlight = (bool)Get(f.Chat, "m_FormalResponseInFlight");
            Check(f.Publish() && f.Queued == "我已经开始尝试这一步了。",
                "Actual moving fact makes one bounded acknowledgement immediately available to the existing TTS queue.");
            Check(f.Plan.Active && ReferenceEquals(Get(f.Chat, "m_FastLoopTurn"), f.Turn) &&
                (bool)Get(f.Chat, "m_FormalResponseInFlight") == inFlight && !(bool)Get(f.Chat, "m_StreamComplete"),
                "Acknowledgement leaves the formal owner, unfinished stream and plan alive.");
            int ackRound = f.AckRound;
            Check(ackRound != f.OwnerRound && (bool)Call(f.Chat, "ValidateFastHeadSpeech", ackRound),
                "Acceptance uses its own valid speech round rather than the final result's round.");
            Check(!f.Publish() && f.ChunkCount == 1 && f.Model.m_DataList.Count == 2,
                "Repeated admission cannot enqueue or commit acceptance twice.");
            int requests = f.Model.RoomCalls;
            var watcher = (IEnumerator)Call(f.Chat, "AwaitActorPlanStep", f.Plan, f.Turn, f.Step, "walk");
            Check(watcher.MoveNext() && f.Model.RoomCalls == requests && f.Plan.RemainingGoal == f.Plan.OriginalGoal && f.ChunkCount == 1,
                "The real watcher still awaits execution while the first speech chunk is already queued.");
        }
        using (var f = new Fixture())
        {
            f.Bind("walk", "moving", ""); Check(f.Publish(), "Accepted initial step.");
            int round = f.AckRound;
            Check(f.Plan.Resolve(1, "walk", true, "arrived", "actual", null, false), "Actual result completes the plan.");
            Call(f.Chat, "CompleteFastLoopReply", f.Model, "原用户输入", "这一步已经完成。", "zh", f.Generation, true);
            Check(Get(f.Chat, "m_FastLoopTurn") == null && f.Plan.State == "completed" &&
                (bool)Call(f.Chat, "ValidateFastHeadSpeech", round),
                "Clearing the completed formal turn does not invalidate the historical acceptance fact.");
            var assistants = f.Model.m_DataList.Where(x => x.role == "assistant").ToArray();
            Check(f.Model.m_DataList.Count(x => x.role == "user") == 1 && assistants.Length == 1 &&
                assistants[0].content.Contains("开始尝试") && assistants[0].content.Contains("已经完成"),
                "Final receipt extends the same assistant history once without a duplicate user entry.");
            string before = assistants[0].content;
            Check((bool)Call(f.Chat, "TryCompleteActorPlanAcceptanceHistory", f.Model, f.Generation, "重复终态") && assistants[0].content == before,
                "Repeated history closure cannot append another completion.");
            Check(f.ChunkCount == 2 && f.Queued.Contains("已经完成"), "Final text is queued once after the distinct acceptance text.");
        }
        using (var f = new Fixture())
        {
            f.Bind("walk", "moving", "下一步"); Check(f.Publish(), "Accepted prior to cancellation.");
            int ack = f.AckRound;
            f.AddChunk(f.OwnerRound, "新的 General 修正。");
            Call(f.Chat, "CancelActorPlan", "remaining-goal-needs-nonspatial-planner");
            Check(f.Queued == "新的 General 修正。" && !(bool)Call(f.Chat, "ValidateFastHeadSpeech", ack) &&
                (bool)Call(f.Chat, "ValidateFastHeadSpeech", f.OwnerRound),
                "Cancellation discards only acceptance; same-turn General/final speech remains publishable.");
            Check(f.Model.m_DataList.All(x => x.role != "assistant"), "Unplayed revoked acknowledgement is removed by object identity.");
            Check(!f.Publish(), "Late accepted callback cannot resurrect a cancelled plan.");
            Call(f.Chat, "CompleteFastLoopReply", f.Model, "原用户输入", "后续需要另行确认。", "zh", f.Generation, true);
            Check(f.Model.m_DataList.Count(x => x.role == "user") == 1 &&
                f.Model.m_DataList.Count(x => x.role == "assistant") == 1 && f.Queued.Contains("后续需要另行确认"),
                "A same-generation correction after revocation retains exactly one user and one assistant history entry.");
        }
        foreach (string mode in new[] { "generation", "epoch", "output", "replacement" })
        using (var f = new Fixture())
        {
            f.Bind("walk", "moving", "下一步"); Check(f.Publish(), "Accepted before " + mode);
            int ack = f.AckRound;
            if (mode == "generation") Set(f.Chat, "m_FormalResponseGeneration", f.Generation + 1);
            if (mode == "epoch") Set(f.Chat, "m_WorkEpoch", (int)Get(f.Chat, "m_WorkEpoch") + 1);
            if (mode == "output") ((FastLoopRuntime)Get(f.Chat, "m_FastLoop")).RevokeOutput();
            if (mode == "replacement") Set(f.Chat, "m_ActorPlan", new ActorPlanRuntime("新任务"));
            Check(!(bool)Call(f.Chat, "ValidateFastHeadSpeech", ack) && f.Queued == "" && !f.Publish(),
                "Stale " + mode + " rejects queued and late acceptance without touching its successor.");
        }
        using (var f = new Fixture())
        {
            f.Bind("walk", "moving", "后续");
            bool entered = false;
            Set(f.Chat, "m_ResponseTrace", new ConversationTrace("ack-reentry", json => {
                if (entered || (string)JObject.Parse(json)["event"] != "actor_plan.accepted_receipt") return;
                entered = true; Call(f.Chat, "CancelActorPlan", "trace-cancel");
            }));
            Check(!f.Publish() && entered && f.Queued == "" && f.Model.m_DataList.Count == 0,
                "Synchronous trace cancellation leaves no stale queued receipt or history.");
        }
        foreach (string phase in new[] { "failed", "blocked", "follow-paused", "unknown" })
        using (var f = new Fixture())
        {
            f.Bind("walk", phase, "后续");
            Check(!f.Publish() && f.Queued == "", "No acceptance for failure or unknown executor phase: " + phase);
        }
        foreach (string phase in new[] { "turning-body", "looking", "state-applied" })
        using (var f = new Fixture())
        {
            string intent = phase == "turning-body" ? "turn-in-place" : phase == "looking" ? "orient-view" : "set-object-state";
            f.BindVisual(intent, "verifying");
            Check(!f.Publish(), "A visual decision still being verified has not been accepted.");
            f.SetVisualPhase(phase);
            Check(f.Publish() && f.Plan.Active && f.ChunkCount == 1,
                "Actual execution fact can admit acceptance after initial verification: " + phase);
        }
        foreach (string phase in new[] { "observed", "performed", "already-satisfied", "turned", "arrived" })
        using (var f = new Fixture())
        {
            f.BindVisual("set-object-state", phase);
            Check(!f.Publish() && f.ChunkCount == 0 && f.Model.m_DataList.Count == 0,
                "An already terminal result goes directly to final receipt, never a late starting acknowledgement: " + phase);
        }
        foreach (string intent in new[] { "inspect-object", "reobserve-object" })
        using (var f = new Fixture())
        {
            f.BindVisual(intent, "observed");
            Check(!f.Publish(), "A status word without same-entity observation evidence cannot announce a confirmed target.");
            f.BindObservedTarget();
            Check(f.Publish() && f.Queued == "我已经确认了目标。" && f.Plan.Active,
                "Confirmed prerequisite is queued before the next serial planning request, without claiming ongoing work or closing the adopted goal.");
            Check(!f.Publish() && f.ChunkCount == 1, "Observed prerequisite receipt is issued only once.");
            Call(f.Chat, "CancelActorPlan", "new-input");
            Check(f.ChunkCount == 0 && !f.Publish(), "New input revokes an unplayed observation receipt with its owner.");
        }
        using (var f = new Fixture())
        {
            f.BindVisual("reobserve-object", "observed"); f.BindObservedTarget(); f.Step.planRemaining = "";
            Check(!f.Publish(), "A completed sole observation uses the final receipt, not an extra progress utterance.");
        }
        return checks;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly object inner;
        public readonly ChatSample Chat;
        public readonly FastLoopIntegrationModel Model;
        public readonly ActorPlanRuntime Plan;
        public readonly object Turn;
        public readonly ArdyRoomDialogueBridge Bridge;
        public RoomTaskProtocol.Decision Step;
        public int Generation => (int)Get(Chat, "m_FormalResponseGeneration");
        public int OwnerRound => (int)Get(Chat, "m_CurrentSpeechRoundId");
        public int AckRound => (int)Get(Get(Chat, "m_ActorPlanAcceptance"), "Round");
        public int ChunkCount => (int)Get(Get(Chat, "m_PendingChunks"), "Count");
        public string Queued => string.Concat(((IEnumerable)Get(Chat, "m_PendingChunks")).Cast<object>().Select(x => (string)Get(x, "Text")));
        public Fixture()
        {
            inner = Activator.CreateInstance(typeof(ActorPlanIntegrationRegression).GetNestedType("Fixture", BindingFlags.NonPublic), true);
            Chat = (ChatSample)Get(inner, "Chat"); Model = (FastLoopIntegrationModel)Get(inner, "Model");
            Plan = (ActorPlanRuntime)Get(Chat, "m_ActorPlan"); Turn = Get(Chat, "m_ActorPlanTurn");
            Bridge = Chat.GetComponent<ArdyRoomDialogueBridge>();
            Call(Chat, "RememberActorTaskLanguage", Generation, "zh");
        }
        public void Bind(string id, string phase, string remainder)
        {
            Step = (RoomTaskProtocol.Decision)Call(inner, "Step", "approach", remainder);
            Check(Plan.Propose(Plan.Revision, "approach", remainder, "walk", out _) && Plan.BindAction(Plan.Revision, id), "Plan binding setup.");
            Call(inner, "Stage", id, phase);
            Set(Bridge, "activeAction", true);
            Set(Chat, "m_RoomTaskGeneration", Generation);
        }
        public bool Publish() => (bool)Call(Chat, "TryPublishActorPlanAcceptance", Plan, Turn, Step, Plan.PendingActionId);
        public void BindVisual(string intent, string phase)
        {
            Step = (RoomTaskProtocol.Decision)Call(inner, "Step", intent, "后续动作");
            Check(Plan.Propose(Plan.Revision, intent, "后续动作", "visual", out _) && Plan.BindAction(Plan.Revision, "visual"), "Visual plan binding setup.");
            Set(Chat, "m_RoomTaskGeneration", Generation); Set(Chat, "m_RoomTaskIntent", intent);
            Set(Chat, "m_RoomTaskActionId", "visual"); Set(Chat, "m_RoomTaskDispatch", "visual-submitted");
            var type = typeof(ChatSample).GetNestedType("SceneActionFact", BindingFlags.NonPublic);
            var fact = Activator.CreateInstance(type, true);
            Set(fact, "actionId", "visual"); Set(fact, "intent", intent); Set(fact, "phase", phase);
            Set(Chat, "m_SceneActionFact", fact); Set(Chat, "m_SceneActionActive", true);
        }
        public void SetVisualPhase(string phase) => Set(Get(Chat, "m_SceneActionFact"), "phase", phase);
        public void BindObservedTarget()
        {
            var fact = Get(Chat, "m_SceneActionFact");
            Set(fact, "objectConfirmed", true); Set(fact, "entityId", "observed-lamp");
            Set(fact, "confirmedCaptureId", "actual-new-image");
        }
        public void AddChunk(int round, string text)
        {
            var type = typeof(ChatSample).GetNestedType("PendingSpeechChunk", BindingFlags.NonPublic);
            var chunk = Activator.CreateInstance(type, true);
            Set(chunk, "RoundId", round); Set(chunk, "Text", text); Set(chunk, "LanguageCode", "zh");
            Call(Get(Chat, "m_PendingChunks"), "Enqueue", chunk);
        }
        public void Dispose() => ((IDisposable)inner).Dispose();
    }
    private static object Get(object o, string n) => o.GetType().GetField(n,F)?.GetValue(o) ?? o.GetType().GetProperty(n,F)?.GetValue(o);
    private static void Set(object o, string n, object v) => o.GetType().GetField(n,F).SetValue(o,v);
    private static object Call(object o,string n,params object[] a) { try { return o.GetType().GetMethod(n,F).Invoke(o,a); } catch(TargetInvocationException e) { throw e.InnerException??e; } }
    private static void Check(bool ok, string why) { checks++; if(!ok) throw new InvalidOperationException(why); }
}
