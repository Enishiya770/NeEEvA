using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Actual handoff/room/route/stage callbacks. Providers are controlled; no conversion, microphone or service.</summary>
public static class FastLoopRecordingRouteRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static int RunChecks()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Use isolated edit-mode callback tests.");
        checks = 0;
        foreach (string draft in new[] {
            "抱歉呢，刚才没能把说话的部分完全滤掉。我会再调整一下，只把有旋律的那段旋律唱给你听。",
            "抱歉，刚才没能仔细分辨。请稍等，我确认好旋律的边界再唱给你听。",
            "嗯，没关系，我们慢慢来。我会再仔细确认一下旋律的边界，这次一定只唱你唱的那部分。" })
        foreach (string coverage in new[] { "atomic", "compound" })
        using (var f = new Fixture("请去掉刚才录音中说话的部分，再唱有旋律的那一段。"))
        {
            f.RetainSyntheticRecording(); f.EnableActor();
            f.Begin("reply", draft);
            Check(f.Model.Actor != null && f.Model.Stages.Count == 0 && f.Queued == "",
                "The complete accepted role draft is still private until actual Actor compilation.");
            var callback = f.Model.Actor;
            var proposal = new ActorIntentDecision { Kind = "replan", Domain = "other_action", Action = "none",
                Condition = "none", Evidence = draft, Task = "recording_singing", Target = "none",
                Coverage = coverage, Goal = "按用户限定去掉普通讲话后回唱原录音。" };
            Check(ActorIntentProtocol.Validate(proposal, draft, out _), "Recording-only multistep coverage is accepted by the production protocol.");
            callback(proposal, "controlled-typed-verdict");
            Check(f.Model.RoomCalls == 0 && f.Model.RoleCalls == 0 &&
                f.Model.Stages.SequenceEqual(new[] { FormalResponseStage.RecordingSingingDecision }),
                "An accepted typed recording correction bypasses Room, secondary skill classification and General legacy tags.");
            Check(f.Model.StageContext.UserText == f.Prompt &&
                f.Model.RequestContext.Contains(draft) && f.Model.RequestContext.Contains("recording_singing"),
                "Typed handoff retains complete current user and role adoption rather than replacing them with a task label.");
            Check((string)f.Model.StageContext.CurrentIntent?["roleProposal"]?["coverage"] == coverage,
                "The dedicated stage retains the model's full coverage rather than normalizing a compound goal into one step.");
            Check(!(bool)Call(f.Chat, "get_HasActiveActorPlan") && f.Queued == "" &&
                f.Sense.DescribePracticePhrases().Count == 0,
                "Typed recording ownership cannot leave an empty spatial plan, publish the private promise or prepare an unreviewed source.");
            callback(proposal, "duplicate");
            Call(f.Chat, "CancelFastLoopRequest", "typed-recording-cancel"); callback(proposal, "late");
            Check(f.Model.Stages.Count == 1, "Repeated and revoked Actor callbacks cannot resubmit recording decisions.");
        }
        const string mixedDraft = "我先走到那盏灯旁把它打开，再确认录音边界并唱给你听。";
        using (var f = new Fixture("请走到灯旁打开它，再把我的录音去掉讲话后唱一遍。"))
        {
            f.RetainSyntheticRecording(); f.EnableActor(); f.Begin("reply", mixedDraft);
            var mixed = new ActorIntentDecision { Kind = "replan", Domain = "other_action", Action = "none",
                Condition = "none", Evidence = mixedDraft, Task = "other", Target = "other", MotionScope = "object_goal",
                Coverage = "compound", Goal = "走到灯旁开灯，然后确定录音的歌声范围并回唱。" };
            Check(ActorIntentProtocol.Validate(mixed, mixedDraft, out _), "Mixed spatial and recording goals retain a valid whole-plan representation.");
            f.Model.Actor(mixed, "controlled-mixed-verdict");
            Check(f.Model.RoomCalls == 1 && f.Model.RoleCalls == 0 && f.Model.Stages.Count == 0 && f.Queued == "",
                "The recording-only adapter cannot swallow an adopted independent spatial goal or publish its promise.");
            var plan = (ActorPlanRuntime)Get(f.Chat, "m_ActorPlan");
            Check(plan != null && plan.OriginalGoal == mixedDraft && plan.CompiledGoal == mixed.Goal,
                "Spatial planner ownership retains the entire movement, device and later recording objective.");
            mixed.Task = "recording_singing";
            Check(!ActorIntentProtocol.Validate(mixed, mixedDraft, out _),
                "A contradictory typed spatial capability cannot enter the audio-only adapter merely by changing the task label.");
        }
        using (var f = new Fixture("把我刚才唱的这一段唱给我听。"))
        {
            f.RetainSyntheticRecording();
            int candidates = f.Sense.DescribeQuarantinedSingingCandidates().Count;
            f.Begin("legacy");
            Check(f.Model.RoomCalls == 1 && f.Model.RoleCalls == 0 && f.Model.Stages.Count == 0,
                "Actual legacy handoff first reaches its existing room planner.");
            var room = f.Model.Room;
            room(true, "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", "ok");
            Check(f.Model.RoleCalls == 1 && f.Model.Stages.Count == 0 && f.Model.RecordingDecisionContext == null && f.Queued == "",
                "A real room-none continuation privately requests one structured skill route before General.");
            var route = f.Model.Role;
            f.Model.AnswerRoute("recording_singing");
            Check(f.Model.Stages.SequenceEqual(new[] { FormalResponseStage.RecordingSingingDecision }) && f.Model.RoomCalls == 1,
                "Only the recording_singing verdict enters the existing recording decision stage, without rerunning room planning.");
            var context = f.Model.StageContext;
            Check(context != null && context.UserText == f.Prompt && context.Materials.Count == candidates &&
                context.Materials.Any(m => m.Revision == "pending" && m.HasCleanRange && m.HasExpandedRange),
                "The production stage binds the exact current user and retained pending recording with explicit clean/expanded ranges.");
            string materialPrompt = RecordingSingingDecisionCompiler.Prompt(context);
            var materialFacts = RecordingSingingDecisionCompiler.PromptFacts(context);
            Check(context.Materials.All(m => m.Key == m.ClipRef && materialPrompt.Contains(m.Key)) &&
                materialPrompt.Contains(f.Prompt) && ((JArray)materialFacts["materials"]).OfType<JObject>().Any(m =>
                    (string)m["revision"] == "pending" && ((JArray)m["proposalRanges"]).Values<string>().Contains("clean") &&
                    ((JArray)m["proposalRanges"]).Values<string>().Contains("expanded") && m["recorded_content"]["transcript"] != null),
                "Production recording IR receives source keys, range facts and the complete request.");
            Check(f.Sense.DescribeQuarantinedSingingCandidates().Count == candidates &&
                f.Sense.DescribePracticePhrases().Count == 0 && f.Queued == "" && f.Model.m_DataList.Count == 0,
                "Routing alone neither selects/prepares a range, confirms source, publishes a promise nor writes private history.");
            route(true, "ignored", "tools"); room(true, "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", "duplicate");
            Check(f.Model.Stages.Count == 1 && f.Model.RoleCalls == 1, "Duplicate room/route callbacks cannot repeat the recording stage.");
        }
        using (var f = new Fixture("刚才那段歌听起来怎么样？不用唱。"))
        {
            f.RetainSyntheticRecording(); f.Begin("reply", "歌声的感觉很轻快。");
            Check(f.Model.RoomCalls == 0 && f.Model.RoleCalls == 0 && f.Model.Stages.Count == 0 && f.Model.RecordingDecisionContext == null,
                "An ordinary fast reply stays ordinary even with retained recordings.");
        }
        using (var f = new Fixture("不用跟着我了。接下来把刚才的录音再唱一次。"))
        {
            f.RetainSyntheticRecording();
            f.Begin("recording_singing", control: "stop_follow", target: "missing-old-follow", evidence: "不用跟着我了");
            Check(f.Model.RoomCalls == 0 && f.Model.RoleCalls == 0 &&
                f.Model.Stages.SequenceEqual(new[] { FormalResponseStage.RecordingSingingDecision }),
                "Typed recording route removes Room and extra role routing, even when an unrelated stop target has expired.");
            Check(f.Model.StageContext.UserText == f.Prompt && f.Queued == "" &&
                f.Model.RequestContext.Contains("currentTaskControlResult") && f.Model.RequestContext.Contains("captured-task-no-longer-current"),
                "Mixed stop plus singing preserves all user text and failed-stop facts, without claiming success or granting singing permission.");
            f.Model.Fast(new FastLoopDecision { Route = "recording_singing", Candidate = "none", Evidence = "", Speech = "", Language = "ja" }, "duplicate");
            Check(f.Model.Stages.Count == 1, "Duplicate typed route callbacks cannot create a second recording stage.");
        }
        foreach (bool singing in new[] { true, false })
        using (var f = new Fixture("请处理当前这段录音和我的请求。"))
        {
            f.BeginProposal();
            Check(f.Model.RoleCalls == 1 && f.Model.RoomCalls == 0 && f.Model.Stages.Count == 0,
                "An adopted nonspatial role proposal classifies its skill before any Room request.");
            var route = f.Model.Role;
            f.Model.AnswerRoute(singing ? "recording_singing" : "reply");
            if (!singing)
            {
                Check(f.Model.RoomCalls == 1 && f.Model.Stages.Count == 0,
                    "A non-singing or failed pre-Room probe retains the spatial planner exactly once.");
                f.NoRoomTask();
            }
            Check(f.Model.Stages.Single() == (singing ? FormalResponseStage.RecordingSingingDecision : FormalResponseStage.General),
                "The pre-Room skill result selects the existing stage without bypassing another goal's required planner.");
            if (singing) Check(!(bool)Call(f.Chat, "get_HasActiveActorPlan"),
                "A same-owner nonspatial planning placeholder is released when the typed recording stage takes over.");
            route(true, "ignored", "tools");
            Check(f.Model.Stages.Count == 1 && f.Model.RoleCalls == 1,
                "A late duplicate skill verdict cannot replay an accepted continuation.");
        }
        foreach (bool failed in new[] { false, true })
        using (var f = new Fixture("只讨论刚才的歌，不要唱。"))
        {
            f.RetainSyntheticRecording(); f.Begin("legacy"); f.NoRoomTask();
            if (failed) f.Model.Role(false, "", "controlled-route-error"); else f.Model.AnswerRoute("reply");
            Check(f.Model.Stages.SequenceEqual(new[] { FormalResponseStage.General }) && f.Model.RoleCalls == 1 &&
                f.Model.RecordingDecisionContext == null && f.Model.RoomCalls == 1,
                "Ordinary or failed skill routing continues through General once, never implicitly performs a recording.");
        }
        using (var f = new Fixture("只讨论刚才的录音。"))
        {
            f.Model.ThrowOnRoute = true; f.Begin("legacy"); f.NoRoomTask();
            Check(f.Model.Stages.SequenceEqual(new[] { FormalResponseStage.General }) && f.Model.RoleCalls == 1 &&
                f.Model.RecordingDecisionContext == null, "A synchronous route-provider exception falls back to General exactly once.");
        }
        using (var f = new Fixture("只查询走近我的路线状态。"))
        {
            f.Begin("legacy");
            f.Model.Room(true, "{\"intent\":\"inspect\",\"origin\":\"user\",\"evidence\":\"查询走近我的路线状态\"}", "ok");
            Check((string)Get(f.Chat, "m_RoomTaskIntent") == "inspect" &&
                (string)Get(f.Chat, "m_RoomTaskDispatch") == "read-only-checked" &&
                f.Model.RoleCalls == 0 && f.Model.Stages.SequenceEqual(new[] { FormalResponseStage.CommittedSpatialReply }),
                "A resolved non-none room task keeps its spatial continuation and cannot enter singing routing.");
        }
        foreach (bool reentrant in new[] { false, true })
        using (var f = new Fixture("把刚才的录音再唱一次。"))
        {
            f.Begin("legacy"); f.NoRoomTask();
            if (reentrant) f.Model.DuringCancel = () => f.Model.AnswerRoute("recording_singing");
            Call(f.Chat, "CancelFastLoopRequest", "recording-route-cancel");
            f.Model.AnswerRoute("recording_singing");
            Check(f.Model.Stages.Count == 0 && f.Model.RecordingDecisionContext == null && f.Queued == "",
                "Late and synchronous cancellation callbacks cannot bind recording IR or publish on a revoked handoff.");
        }
        using (var f = new Fixture("把刚才的录音再唱一次。"))
        {
            f.Begin("legacy"); f.NoRoomTask();
            Call(f.Chat, "CancelSceneAction", "room-output-disabled");
            f.Model.AnswerRoute("recording_singing");
            Check(f.Model.Stages.Count == 0 && !(bool)Get(f.Chat, "m_FormalResponseInFlight"),
                "Same-generation scene revocation ends only the old route waiter and never performs singing.");
        }
        Directory.CreateDirectory("Logs/fast-loop");
        File.WriteAllText("Logs/fast-loop/recording-route.json", new JObject {
            ["passed"] = true, ["checks"] = checks, ["networkRequests"] = 0,
            ["scope"] = "Production legacy handoff, actual room-none callback, structured route and RecordingSingingDecision context binding; in-memory synthetic archive; no model accuracy, range selection, source confirmation, conversion, audio playback or user-scene claim."
        }.ToString());
        return checks;
    }
    public static void RunBatch()
    {
        try { RunChecks(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error is TargetInvocationException ? error.InnerException : error); EditorApplication.Exit(1); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly object original;
        public readonly ChatSample Chat;
        public readonly FastLoopRecordingRouteModel Model;
        public readonly string Prompt;
        public SenseVoiceSpeechToText Sense;
        public string Queued => string.Concat(((IEnumerable)Get(Chat, "m_PendingChunks")).Cast<object>().Select(x => (string)Get(x, "Text"))) + Get(Chat, "m_SentenceBuffer");
        public Fixture(string prompt)
        {
            Prompt = prompt;
            original = Activator.CreateInstance(typeof(FastLoopIntegrationRegression).GetNestedType("Fixture", BindingFlags.NonPublic), true);
            Chat = (ChatSample)Get(original, "Chat");
            Call(original, "Arm", prompt, false); Call(original, "AttachControlledRoom"); Call(original, "AttachSpeechProvider");
            // Arm supplies dialogue state only; the production accepted-user hook
            // also binds the text used to validate room-decision evidence.
            Call(Chat, "RecordAcceptedMotionUserTurn", prompt);
            Model = Chat.gameObject.AddComponent<FastLoopRecordingRouteModel>();
            ((ChatSetting)Get(Chat, "m_ChatSettings")).m_ChatModel = Model;
        }
        public void Begin(string route, string speech = "", string control = "none", string target = "", string evidence = "")
        {
            int generation = (int)Get(Chat, "m_FormalResponseGeneration");
            Check((bool)Call(Chat, "TryDispatchFastTaskLoop", Model, Prompt, null, generation, true, null), "Shared production fast-loop entry accepts the turn.");
            Model.Fast(new FastLoopDecision { Version = FastLoopProtocol.CurrentVersion, Route = route, Candidate = "none", Evidence = "", Speech = speech, Language = "ja",
                TaskControl = control, ControlTargetId = target, ControlEvidence = evidence }, "ok");
        }
        public void BeginProposal()
        {
            int generation = (int)Get(Chat, "m_FormalResponseGeneration");
            Check((bool)Call(Chat, "TryDispatchFastTaskLoop", Model, Prompt, null, generation, true, null), "Production turn starts privately.");
            var runtime = (FastLoopRuntime)Get(Chat, "m_FastLoop");
            const string draft = "今からその録音を歌い返すわ。";
            Call(Chat, "HandoffFastLoopToPlanner", Model, generation, runtime.OutputRevision,
                new ActorDraft { Id = "fixture-song-proposal", Revision = 1, Text = draft },
                new ActorIntentDecision { Domain = "other_action", Kind = "replan", Task = "other", Evidence = draft }, "fixture-nonspatial", false);
        }
        public void NoRoomTask()
        {
            var decision = new JObject { ["intent"]="none", ["origin"]="none", ["evidence"]="" };
            if (Get(Chat,"m_ActorPlan") is ActorPlanRuntime plan && plan.Active)
            {
                decision["planId"]=plan.Id; decision["planRevision"]=plan.Revision;
                decision["planRemaining"]=plan.RemainingGoal;
            }
            Model.Room(true,decision.ToString(),"ok");
        }
        public void EnableActor() => typeof(ChatSample).GetField("m_EnableActorIntentCompiler", Flags).SetValue(Chat, true);
        public void RetainSyntheticRecording()
        {
            Sense = Chat.gameObject.AddComponent<SenseVoiceSpeechToText>(); Sense.enabled = false;
            ((ChatSetting)Get(Chat, "m_ChatSettings")).m_SpeechToText = Sense;
            var type = typeof(SenseVoiceSpeechToText);
            byte[] wav = (byte[])type.GetMethod("EncodeMonoPcm16Wav", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null,
                new object[] { Enumerable.Range(0, 64000).Select(i => .1f * Mathf.Sin(i * .08f)).ToArray(), 16000 });
            var responseType = type.GetNestedType("Response", BindingFlags.NonPublic);
            object response = JsonUtility.FromJson("{\"text\":\"ラララ この一段\",\"singing_text\":\"ラララ\",\"singing_probability\":0.48,\"singing_analysis_available\":true,\"singing_start_seconds\":0.2,\"singing_end_seconds\":3.0,\"singing_recovery_start_seconds\":0.2,\"singing_recovery_end_seconds\":3.8,\"pitch_timeline_frame_seconds\":0.1}", responseType);
            responseType.GetField("pitch_timeline_midi").SetValue(response, Enumerable.Repeat(60f, 40).ToArray());
            Sense.BeginLiveRecordingCandidateSession();
            type.GetMethod("ArchiveCompletedCapture", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Sense, new object[] { response, wav, 10f, 1 });
            Check(Sense.DescribeQuarantinedSingingCandidates().Count > 0, "Synthetic evidence remains a pending recording, not confirmed song material.");
        }
        public void Dispose() => ((IDisposable)original).Dispose();
    }
    private static object Get(object value, string field) => value.GetType().GetField(field, Flags).GetValue(value);
    private static object Call(object value, string name, params object[] args) => value.GetType().GetMethods(Flags)
        .Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args);
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
}

public sealed class FastLoopRecordingRouteModel : LLM, IFastLoopDecisionBackend, IFastTaskControlBackend, IActorIntentBackend
{
    public Action<FastLoopDecision, string> Fast;
    public Action<bool, string, string> Room, Role;
    public Action DuringCancel;
    public Action<string, string> Control;
    public Action<ActorIntentDecision, string> Actor;
    public int RoomCalls, RoleCalls;
    public string LastRoomPrompt, LastRolePrompt;
    public bool ThrowOnRoute;
    public readonly System.Collections.Generic.List<FormalResponseStage> Stages = new System.Collections.Generic.List<FormalResponseStage>();
    public RecordingSingingDecisionContext StageContext;
    public bool SupportsFastLoopDecisions => true;
    public bool SupportsActorIntents => true;
    public void PostActorIntent(string context, string draft, string currentUser,
        Action<ActorIntentDecision, string> callback, ConversationTrace trace = null) => Actor = callback;
    public void PostActorConditionSignal(string context, string currentUser,
        Action<string, string> callback, ConversationTrace trace = null) => callback("none", "controlled");
    public void CancelActorIntent() { }
    public override bool SupportsRoomTaskMessages => true;
    public override bool SupportsRoleDecisionMessages => true;
    public override bool SupportsRecordingSingingDecision => true;
    public void PostFastLoopDecision(string context, string currentUser, Action<FastLoopDecision, string> callback, ConversationTrace trace = null, string imageDataUrl = null) => Fast = callback;
    public void CancelFastLoopDecision() { }
    public void PostFastTaskControlDecision(string context, string currentUser, Action<string, string> callback, ConversationTrace trace = null) => Control = callback;
    public override void PostRoomTaskMessage(string input, string imageDataUrl, bool reviewSpeech, Action<bool, string, string> callback)
    { RoomCalls++; LastRoomPrompt = input; Room = callback; }
    public override void PostRoleDecisionMsg(string prompt, string imageDataUrl, Action<bool, string, string> callback, ConversationTrace trace)
    {
        RoleCalls++; LastRolePrompt = prompt; AcceptedRoleDecision = null; Role = callback;
        if (ThrowOnRoute) throw new InvalidOperationException("controlled-provider-exception");
    }
    public void AnswerRoute(string route)
    {
        string json = new JObject { ["version"] = 3, ["task"] = "none", ["attention"] = "unchanged", ["route"] = route,
            ["user_evidence"] = "", ["thought"] = "", ["speech"] = new JObject { ["text"] = "", ["language"] = "ja" } }.ToString();
        if (!DecisionCompiler.TryParse(json, out var decision, out string error)) throw new InvalidOperationException(error);
        AcceptedRoleDecision = decision;
        Role(true, "", route == "recording_singing" ? "tools" : "ok-silent");
    }
    public override void CancelActiveResponse() { DuringCancel?.Invoke(); }
    public override void PostSpeechStreamForStage(string message, Action<SpeechText> onSpeech, Action<string> onComplete,
        string imageDataUrl, bool recordAssistantHistory, FormalResponseStage stage)
    { Stages.Add(stage); StageContext = RecordingDecisionContext; }
}
