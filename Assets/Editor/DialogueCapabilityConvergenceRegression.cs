#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Real cross-turn dialogue hooks, repair callbacks and production wire factories.
// Controlled delivery/decisions only: no microphone, model HTTP, movement or playback.
public static class DialogueCapabilityConvergenceRegression
{
    const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const string Goal = "请先确认第二段录音里说话和歌声的边界，只把歌声唱给我听。";
    const string ClarifyUser = "先确认清楚，如果不确定你再问我。";
    const string Question = "你刚才唱的那段，是想要我重新唱一遍吗？";
    const string Answer = "对呀。";
    const string AudioUser = "前面有一部分是我在说话，而不是在唱歌。请确定从哪里开始唱，然后再把歌声唱出来。";
    const string AudioDraft = "我先确认这段录音里讲话和歌声的分界，保留完整歌声，再唱给你听。";
    const string MixedUser = "先走到那盏灯旁把它打开，再确定录音里的歌声范围并回唱。";
    const string MixedDraft = "我先走到灯旁打开它，再确认录音边界，把歌声唱给你听。";
    static int checks;

    public static int RunChecks()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Isolated edit mode only.");
        checks = 0;
        using (var f = new ContextFixture())
        {
            JObject ordinary = JObject.Parse(f.Original.Context), repair = JObject.Parse(f.Repair.Context);
            Check(Heard(ordinary).Contains(Question) && Heard(repair).Contains(Question),
                "c15 completed clarification survives both the c16 initial context and actual control-repair callback.");
            Check(f.Original.CurrentUser == Answer && f.Repair.CurrentUser == Answer &&
                repair["recentDialogue"]["turns"].Any(t => (string)t["user"] == ClarifyUser),
                "The complete short confirmation remains bound to its prior real user/assistant pair.");
            Check(repair["recentDialogue"]["turns"].Any(t => (string)t["user"] == Goal) &&
                ordinary["recentDialogue"]["turns"].Any(t => (string)t["user"] == Goal) &&
                (bool)repair["recordingMaterials"]["observed"] &&
                repair["previousPlanReferent"].Type == JTokenType.Null,
                "Retained recording and unfulfilled request survive without inventing an adopted or cancelled plan.");
            Check(((JArray)repair["recordingMaterials"]["recent"]).Count > 0 &&
                repair["work"]["singingGoal"].Type == JTokenType.Null &&
                repair["work"]["activeSingingGoal"].Type == JTokenType.Null &&
                repair["work"]["lastSingingPlayback"].Type == JTokenType.Null,
                "A retained recording is evidence only: the replay invents no goal approval, revision or completed playback.");
            Check(repair["dialogueRepair"] == null && repair["taskControlRepair"] != null &&
                !repair.ToString().Contains("assistantWordingOmittedForRepair"),
                "Control repair is not the anti-repetition view and cannot silently remove heard questions.");
            var repeat = (JObject)Call(f.Chat, "BuildRepeatedFastDraftRecoveryFacts", Get(f.Chat, "m_FastLoopTurn"));
            Check(Heard(repeat) == "" && repeat.ToString().Contains("assistantWordingOmittedForRepair") &&
                repeat["recentDialogue"]["turns"].Any(t => (string)t["user"] == Goal),
                "Only the dedicated repeated-draft view omits assistant wording while preserving unmet goals.");
            var rebuilt = (JObject)Call(f.Chat, "BuildFastReplyRecoveryFacts", Get(f.Chat, "m_FastLoopTurn"));
            Check(Heard(rebuilt).Contains(Question) &&
                ((List<JObject>)Get(f.Chat, "m_CompletedRoleReplies")).Any(t => ((string)t["text"]).Contains(Question)),
                "Building a repeat-specific snapshot never mutates retained actual dialogue or later recovery facts.");
            int calls = f.Model.Pending.Count;
            Call(f.Chat, "CancelFastLoopRequest", "fixture-control-repair-revoked");
            f.Repair.Answer(new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Speech = "迟到的答复", Language = "zh" }, "ok");
            Check(f.Model.Pending.Count == calls && f.Queued == "" && f.Model.FormalCalls == 0,
                "Keeping semantic context does not grant a stale repair authority to speak or act.");
        }
        foreach (string kind in new[] { "replan", "act" })
        {
            var raw = IntentWire(AudioDraft, "recording_singing", "compound", "none", kind);
            Check(ActorIntentProtocol.TryParse(raw.ToString(), AudioDraft, out var parsed, out _) &&
                parsed.Kind == "replan" && parsed.Task == "recording_singing" && parsed.Coverage == "compound" && parsed.Goal == AudioDraft,
                "Actual actor parser preserves multistep recording scope and maps adopted acts to planning.");
            raw["motionScope"] = "object_goal";
            Check(!ActorIntentProtocol.TryParse(raw.ToString(), AudioDraft, out _, out _),
                "A typed independent object capability contradicts recording-only scope; no audio shortcut.");
        }
        var review = new ActorIntentReviewDecision { Status = "adopted_action", Evidence = AudioDraft, Goal = AudioDraft,
            Task = "recording_singing", Target = "none", Coverage = "compound", Trigger = "current_fact" };
        Check(ActorIntentProtocol.CompileReview(review, AudioDraft, out var reviewed, out _) &&
            reviewed.Coverage == "compound" && reviewed.CurrentFactPrerequisite && reviewed.Goal == AudioDraft,
            "Current recording checks retain both their condition and full multistep coverage.");
        review.Trigger = "deferred";
        Check(!ActorIntentProtocol.CompileReview(review, AudioDraft, out _, out _),
            "Future external triggers are not turned into immediate recording playback.");
        Check(ActorIntentProtocol.TryParse(IntentWire(MixedDraft, "other", "compound", "object_goal", "replan").ToString(),
            MixedDraft, out var mixed, out _) && mixed.Goal == MixedDraft && mixed.Task == "other",
            "Mixed spatial/recording goals retain the entire plan rather than dropping their spatial prefix.");
        var actorBranches = (JArray)ActorIntentProtocol.ResponseFormat()["json_schema"]["schema"]["anyOf"];
        Check(actorBranches.OfType<JObject>().All(b => ((JObject)b["properties"]).Properties().Select(p => p.Name).ToList().IndexOf("task") <
            ((JObject)b["properties"]).Properties().Select(p => p.Name).ToList().IndexOf("coverage")),
            "The actual actor grammar selects capability before step coverage, without altering mixed-goal validity.");
        var roleBranches = (JArray)DecisionCompiler.FastLaneResponseFormat()["json_schema"]["schema"]["anyOf"];
        Check(roleBranches.OfType<JObject>().All(b => ((JObject)b["properties"]).Properties().Select(p => p.Name).ToList().IndexOf("route") <
            ((JObject)b["properties"]).Properties().Select(p => p.Name).ToList().IndexOf("task")),
            "The actual route grammar selects a dedicated capability before optional general-tool task fields.");
        return checks;
    }

    // Four current production requests; synthetic state replays the latest failure
    // pattern and original utterances, not a claim to recover historical HTTP bytes.
    public static JObject Export(string root)
    {
        var folder = Path.Combine(root, "work/loop-convergence-20260928-2212/dialogue");
        Directory.CreateDirectory(folder);
        var host = new GameObject("Inactive dialogue capability wire export"); host.SetActive(false);
        var assets = new List<TextAsset>();
        try
        {
            var q = host.AddComponent<ChatQW>(); q.m_Backend = ChatQW.BackendType.OpenAICompatible;
            q.m_OpenAICompatibleModelName = "qwen3.6-35b-a3b"; q.m_LogRequestStats = false;
            foreach (string name in new[] { "persona", "language", "behavior", "singing" })
            {
                string path = Path.Combine(root, "Assets/AIChatTookit/Prompts/" + name + ".txt");
                if (File.Exists(path)) assets.Add(new TextAsset(File.ReadAllText(path)) { name = name });
            }
            typeof(LLM).GetField("m_PromptFiles", F).SetValue(q, assets.ToArray());
            var cases = new JArray();
            using (var f = new ContextFixture())
            {
                cases.Add(new JObject { ["id"] = "c16-confirmation-after-control-repair", ["phase"] = "fast",
                    ["currentUser"] = Answer, ["context"] = JObject.Parse(f.Repair.Context),
                    ["expected"] = "Preserve the song/clarification objective; no stop control, no empty social acknowledgement. Dedicated recording routing or a specific still-needed boundary clarification is appropriate; no execution is approved.",
                    ["wire"] = JObject.Parse((string)Call(q, "BuildFastLoopRequestJson", f.Repair.Context, Answer, null)) });
            }
            foreach (bool mixed in new[] { false, true })
            {
                object fixture = NewRouteFixture(mixed ? MixedUser : AudioUser);
                try
                {
                    Call(fixture, "RetainSyntheticRecording"); Call(fixture, "EnableActor");
                    string draft = mixed ? MixedDraft : AudioDraft, user = mixed ? MixedUser : AudioUser;
                    Call(fixture, "Begin", "reply", draft, "none", "", "");
                    var chat = (ChatSample)Get(fixture, "Chat");
                    string context = (string)Call(chat, "BuildActorIntentContext");
                    cases.Add(new JObject { ["id"] = mixed ? "mixed-spatial-recording-complete-goal" : "recording-boundary-then-sing",
                        ["phase"] = "actor", ["currentUser"] = user, ["draft"] = draft, ["context"] = JObject.Parse(context),
                        ["expectedTask"] = mixed ? "other" : "recording_singing",
                        ["wire"] = JObject.Parse((string)Call(q, "BuildActorIntentRequestJson", context, draft, user, false)) });
                    if (!mixed)
                    {
                        // Replay a valid older/general Actor classification to test
                        // actual fallback skill-routing context and its real factory.
                        var model = (FastLoopRecordingRouteModel)Get(fixture, "Model");
                        model.Actor(new ActorIntentDecision { Kind = "replan", Domain = "other_action", Task = "other", Target = "none",
                            Action = "none", Condition = "none", Coverage = "compound", Goal = AudioDraft, Evidence = draft }, "controlled-other-verdict");
                        if (model.RoleCalls != 1 || string.IsNullOrEmpty(model.LastRolePrompt))
                            throw new InvalidOperationException("Fallback export did not reach the actual pre-Room skill route.");
                        cases.Add(new JObject { ["id"] = "recording-boundary-general-classifier-recovery", ["phase"] = "role",
                            ["currentUser"] = user, ["routeContext"] = model.LastRolePrompt, ["expectedRoute"] = "recording_singing",
                            ["wire"] = JObject.Parse((string)Call(q, "BuildRoleDecisionRequestJson", model.LastRolePrompt, null)) });
                    }
                }
                finally { ((IDisposable)fixture).Dispose(); }
            }
            foreach (JObject item in cases) item["scope"] = "Current production factory/context; controlled no-action replay of latest failure pattern. No model/service request or audio playback was performed by exporter; not exact historical wire.";
            File.WriteAllText(Path.Combine(folder, "model-cases.json"), cases.ToString());
            return new JObject { ["cases"] = cases.Count, ["path"] = Path.Combine(folder, "model-cases.json"), ["networkRequests"] = 0 };
        }
        finally { UnityEngine.Object.DestroyImmediate(host); foreach (var asset in assets) UnityEngine.Object.DestroyImmediate(asset); }
    }

    static JObject IntentWire(string draft, string task, string coverage, string motion, string kind) => new JObject {
        ["version"] = ActorIntentProtocol.IntentVersion, ["kind"] = kind, ["domain"] = "other_action", ["coverage"] = coverage,
        ["goal"] = draft, ["task"] = task, ["target"] = "other", ["action"] = "none", ["condition"] = "none", ["evidence"] = draft,
        ["motionScope"] = motion, ["degrees"] = null, ["deviceState"] = "none", ["targetEntityId"] = "" };
    static string Heard(JObject facts) => string.Concat((facts["recentDialogue"]?["turns"] as JArray ?? new JArray())
        .OfType<JObject>().SelectMany(t => (t["completedAssistantSpeech"] as JArray ?? new JArray()).OfType<JObject>()).Select(t => (string)t["text"]));
    static object NewRouteFixture(string user) => Activator.CreateInstance(typeof(FastLoopRecordingRouteRegression)
        .GetNestedType("Fixture", BindingFlags.NonPublic), F, null, new object[] { user }, null);
    sealed class ContextFixture : IDisposable
    {
        readonly object fixture;
        public readonly ChatSample Chat;
        public readonly FastLoopIntegrationModel Model;
        public readonly FastLoopIntegrationModel.PendingRequest Original, Repair;
        public string Queued => (string)Call(fixture, "Queued");
        public ContextFixture()
        {
            // The actual c13 boundary failure preceded any registered SingGoal.
            // Retain real pending audio and the heard question; do not fabricate
            // an approved/revised goal or a cancelled generic execution plan.
            object recordingFixture = NewRouteFixture(Goal);
            Call(recordingFixture, "RetainSyntheticRecording");
            fixture = Get(recordingFixture, "original");
            Chat = (ChatSample)Get(fixture, "Chat");
            Model = Chat.gameObject.AddComponent<DialogueCapabilityConvergenceModel>();
            ((ChatSetting)Get(Chat, "m_ChatSettings")).m_ChatModel = Model;
            Set(Chat, "m_EnableAutonomousHumBack", true); Set(Chat, "m_EnableNeuralHumSVC", true);
            Set(Chat, "m_EnableSingingVoiceSynthesis", false);
            Arm(Goal); Dispatch();
            Arm(ClarifyUser);
            var prior = Dispatch();
            prior.Answer(new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Speech = Question, Language = "zh" }, "ok");
            Call(Chat, "RecordCompletedRoleSpeech", Get(Chat, "m_FormalResponseGeneration"), Get(Chat, "m_CurrentSpeechRoundId"), Question);
            Arm(Answer); Set(Chat, "m_EnableActorIntentCompiler", true);
            Original = Dispatch();
            object turn = Get(Chat, "m_FastLoopTurn");
            Set(turn, "ControlResult", new JObject { ["status"] = "not-accepted", ["reason"] = "independent-control-review-declined",
                ["requested"] = "stop_follow", ["reviewed"] = "none", ["executed"] = false });
            if (!(bool)Call(Chat, "TryRepairDeclinedFastControl", turn, "zh", Answer, null, 0))
                throw new InvalidOperationException("Actual control repair did not dispatch.");
            Repair = Model.Pending.Last();
            if (ReferenceEquals(Original, Repair)) throw new InvalidOperationException("Missing real repair backend callback.");
        }
        void Arm(string text) { Call(fixture, "Arm", text, false); Call(Chat, "ResetSingingGoalForUserTurn"); }
        FastLoopIntegrationModel.PendingRequest Dispatch()
        {
            int count = Model.Pending.Count;
            if (!(bool)Call(Chat, "TryDispatchFirstReply", Get(Chat, "m_LastUserMsg"), null,
                Get(Chat, "m_FormalResponseGeneration"), true, "controlled-cross-turn-context") || Model.Pending.Count != count + 1)
                throw new InvalidOperationException("Production shared entry did not dispatch the complete user to the recording-capable backend.");
            return Model.Pending.Last();
        }
        public void Dispose() => ((IDisposable)fixture).Dispose();
    }
    static object Get(object value, string name) => value.GetType().GetField(name, F)?.GetValue(value) ?? value.GetType().GetProperty(name, F)?.GetValue(value);
    static void Set(object value, string name, object data) => value.GetType().GetField(name, F).SetValue(value, data);
    static object Call(object value, string name, params object[] args)
    {
        try { return value.GetType().GetMethods(F).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value, args); }
        catch (TargetInvocationException e) { throw e.InnerException ?? e; }
    }
    static void Check(bool value, string why) { ++checks; if (!value) throw new InvalidOperationException(why); }
}

public sealed class DialogueCapabilityConvergenceModel : FastLoopIntegrationModel
{
    public override bool SupportsRecordingSingingDecision => true;
}
#endif
