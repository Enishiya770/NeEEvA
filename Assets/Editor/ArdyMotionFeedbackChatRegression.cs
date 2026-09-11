using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Motion;
using UnityEditor;
using UnityEngine;

/// <summary>Real ChatSample/parser plus an in-process LLM transport; no network or scene configuration.</summary>
public static class ArdyMotionFeedbackChatRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;
    private static readonly List<string> cases = new List<string>();
    [Serializable] private sealed class Report
    {
        public bool passed;
        public int checks;
        public string[] cases;
        public string failure, checkedAtUtc = DateTime.UtcNow.ToString("o"), unityVersion = Application.unityVersion;
        public string scope = "Actual C# parser, inactive ChatSample identity/feedback lifecycle, same-LLM PostSpeechContinuationStream callbacks and existing collected speech/action callback; fake transport only, no model-semantic or physical-motion claim.";
    }
    private sealed class Fixture : IDisposable
    {
        public readonly GameObject host;
        public readonly ChatSample chat;
        public readonly ArdyMotionFeedbackFakeLLM model;
        public readonly List<DialogueMotionIntent> actions = new List<DialogueMotionIntent>();
        public Fixture()
        {
            host = new GameObject("InactiveMotionFeedbackChatFixture"); host.SetActive(false);
            chat = host.AddComponent<ChatSample>(); model = host.AddComponent<ArdyMotionFeedbackFakeLLM>();
            var text = new GameObject("Text", typeof(RectTransform), typeof(UnityEngine.UI.Text));
            text.transform.SetParent(host.transform, false);
            Set(chat, "m_TextBack", text.GetComponent<UnityEngine.UI.Text>());
            Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = model });
            // Real scenes serialize this list. AddComponent on an inactive test
            // host has no scene serialization or Awake/Start initialization.
            Set(chat, "m_ChatHistory", new List<string>());
            Set(chat, "m_PersistSubtitleSettings", false); Set(chat, "m_EnableSubtitleTranslation", false);
            Set(chat, "m_SystemNoticeMode", SystemNoticeMode.Hidden);
            Set(chat, "m_LogAgentLoop", false); Set(chat, "m_LogStreamTimings", false);
            chat.MotionIntentRequested += action => actions.Add(action);
            chat.ConfigureMotionOutput(true, true);
            Call(chat, "RecordAcceptedMotionUserTurn", "Public test: raise both hands beside the head.");
            Call(chat, "BeginFormalResponseGeneration");
        }
        public DialogueMotionIntent Submit(string tag)
        {
            object[] args = { tag };
            var intent = (DialogueMotionIntent?)Call(chat, "ExtractDialogueMotion", args);
            Call(chat, "DispatchDialogueMotion", intent, false);
            return actions.Count == 0 ? default : actions[actions.Count - 1];
        }
        public object Pending => Get(chat, "m_PendingMotionRepair");
        public void Issue() { Call(chat, "IssueMotionRepair", Pending); }
        public void Dispose() { UnityEngine.Object.DestroyImmediate(host); }
    }
    private const string Generated = "<motion name=\"generate\" text=\"Bring both hands beside the head.\" leftGoal=\"near-head\" rightGoal=\"near-head\"/>";
    private const string Fixed = "<motion name=\"generate\" text=\"Lift both hands beside the head with elbows bent outward.\"/>";

    public static void RunInteractive()
    {
        checks = 0; cases.Clear(); var report = new Report();
        try
        {
            ParserAndIdentity(); ActualContinuation(); LengthAndCapability(); GoalAndBudget();
            FeedbackFences(); CallbackFences(); SpeechGuard(); ReplayAndUnknown();
            EmptyRepairOutcomes(); RejectedExecutionNotice(); VisibleFailureNotice(); OrdinarySilent();
            report.passed = true;
        }
        catch (Exception error) { report.failure = error.ToString(); throw; }
        finally
        {
            report.checks = checks; report.cases = cases.ToArray();
            string path = Path.GetFullPath("Tools/MotionAdapter/reports/motion-feedback-chat-regression.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, JsonUtility.ToJson(report, true) + "\n");
        }
        Debug.Log("[ArdyMotionFeedbackChatRegression] passed " + checks + " checks.");
    }
    public static void RunBatch()
    {
        try { RunInteractive(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    private static void ParserAndIdentity()
    {
        using (var f = new Fixture())
        {
            Check(!f.chat.SemanticMotionPlanningEnabled, "Semantic experiment became the default");
            var first = f.Submit(Generated);
            Check(first.ActionId.Length > 32 && first.ParentActionId == "" && first.RepairAttempt == 0, "Chat failed to assign initial identity");
            f.Submit(Generated); Check(f.actions.Count == 1, "Identical command gained another identity or dispatch");
            first.Goal.leftGoal = "down";
            var feedback = Failed(first); f.chat.NotifyMotionFeedback(feedback);
            Check(((ArdyMotionExecutionFeedback)Get(f.chat, "m_LastMotionExecutionFeedback")).goal.leftGoal == "near-head", "Listener mutated registered goal snapshot");
            string context = (string)Call(f.chat, "BuildMotionRequestContext", "public");
            Check(context.Contains(DialogueMotionProtocol.MotionFeedbackOutputContract) && context.Contains("near-head"), "Feedback capability/facts absent from current prompt");
        }
        foreach (string tag in new[] {
            "<motion name=\"nod\" actionId=\"fake\"/>",
            "<motion name=\"generate\" text=\"Raise the right arm.\" repairAttempt=\"0\"/>",
            "<motion name=\"compose\" left=\"forward\" leftGoal=\"forward\"/>",
            "<motion name=\"replay\" ref=\"last\" text=\"new motion\"/>",
            "<motion name=\"generate\" text=\"Raise the hand.\" leftGoal=\"finger\"/>" })
        {
            string raw = tag; Check(!DialogueMotionProtocol.TryExtract(ref raw, 1, 1, out _, out string error) && error.Length > 0, "Strict protocol admitted " + tag);
            Check(raw.Length == 0, "Rejected control fields leaked to speech");
        }
        cases.Add("strict IDs/goal/replay schema, immutable per-action snapshots, deduplication, default legacy contract");
    }

    private static void ActualContinuation()
    {
        using (var f = new Fixture())
        {
            var initial = f.Submit(Generated); f.chat.NotifyMotionFeedback(Failed(initial));
            Check(f.Pending != null && f.model.requests == 0, "Feedback issued a model request inline");
            Call(f.chat, "BeginFormalResponseGeneration"); // autonomous speech generation, same body action/user
            f.Issue(); Check(f.model.requests == 1 && f.model.messageRequests == 0, "Repair did not use exactly one same-model continuation");
            Check(f.model.feedbackRequests == 1 && !f.model.regularContext.Contains("originalRejectedCommand") &&
                f.model.executionFeedback.Contains("originalRejectedCommand"), "Completed-action feedback was not separated from ordinary request context");
            Check(f.model.context.Contains(initial.ActionId) && f.model.context.Contains(initial.Description) && f.model.context.Contains("immutableGoal"), "Repair lost source intent/goal");
            f.model.DeliverProjected(Fixed);
            Check(f.actions.Count == 2, "Actual continuation callback did not dispatch a valid correction");
            var revised = f.actions[1];
            Check(revised.ParentActionId == initial.ActionId && revised.ActionId != initial.ActionId && revised.RepairAttempt == 1,
                "Correction lost identity chain or budget");
            Check(revised.Goal.leftGoal == "near-head" && revised.Goal.rightGoal == "near-head", "Omitted correction goals were not inherited");
            Check(!(bool)Get(f.chat, "m_FormalResponseInFlight") && !(bool)Property(f.chat, "CurrentMotionResponseRejected"), "Successful correction remained in failed/in-flight state");
            Check(((SubtitleOverlay)Get(f.chat, "m_SubtitleOverlay")).NoticeHistory.Count == 0, "Pure repaired action became an empty-response failure");
            Check(!((IEnumerable)Get(f.chat, "m_ChatHistory")).GetEnumerator().MoveNext(), "Projected silent plus valid motion added an empty history entry");
            Check(!((IEnumerable)Get(f.chat, "m_PendingChunks")).GetEnumerator().MoveNext() &&
                !((IEnumerable)Get(f.chat, "m_PendingClips")).GetEnumerator().MoveNext(), "Projected action-only repair entered empty TTS queues");
            f.model.Deliver(Fixed); Check(f.actions.Count == 2, "Duplicate model callback dispatched twice");
            f.chat.NotifyMotionFeedback(Failed(revised)); Check(f.Pending == null && f.model.requests == 1, "Second failure replenished repair budget");
        }
        cases.Add("real PostSpeechContinuationStream → capture → collected callback → dispatch; cross-autonomous-generation feedback; one repair budget");
    }

    private static void LengthAndCapability()
    {
        using (var f = new Fixture())
        {
            string description = "Raise both hands beside the head, " + new string('x', 275) + ".";
            string tag = "<motion name=\"generate\" text=\"" + description + "\" leftGoal=\"near-head\" rightGoal=\"near-head\"/>";
            Call(f.chat, "CallBackWithSpeech", new List<SpeechText>(), "Done." + tag);
            Check(f.actions.Count == 0 && f.Pending != null, "Invalid long action was dispatched or not queued for repair");
            Check((bool)Property(f.chat, "CurrentMotionResponseRejected"), "Rejected response was allowed to continue speech");
            f.Issue(); Check(f.model.context.Contains(description) && f.model.context.Contains("originalRejectedCommand"), "Rejected long description lost its meaning before repair");
            f.model.Deliver(Fixed); Check(f.actions.Count == 1 && f.actions[0].RepairAttempt == 1, "Equivalent shortened motion failed to dispatch");
            Check(f.actions[0].Goal.rightGoal == "near-head", "Long-description repair lost goal");
        }
        using (var f = new Fixture())
        {
            f.chat.ConfigureMotionOutput(true, false);
            f.Submit(Generated); Check(f.actions.Count == 0 && f.Pending != null, "Disabled generated route bypassed gate or hid failure");
            f.Issue(); f.model.Deliver(Generated);
            Check(f.actions.Count == 0 && f.Pending == null && f.model.requests == 1, "Repair enabled unavailable capability or looped");
        }
        foreach (string goalAttributes in new[] { "", " rightGoal=\"forward\"" })
        using (var f = new Fixture())
        {
            f.Submit("<motion name=\"generate\" text=\"Wag only the right index finger.\"" + goalAttributes + "/>");
            Check(f.actions.Count == 0 && f.Pending != null, "Known unsupported single-finger action was accepted with metadata: " + goalAttributes);
        }
        cases.Add("overlength original role-command retention; equivalent compression; capability-disabled route; known finger boundary");
    }

    private static void GoalAndBudget()
    {
        using (var f = new Fixture())
        {
            var initial = f.Submit(Generated); f.chat.NotifyMotionFeedback(Failed(initial)); f.Issue();
            f.model.Deliver("<motion name=\"generate\" text=\"Lower both arms.\" leftGoal=\"any\" rightGoal=\"any\"/>");
            Check(f.actions.Count == 1 && f.Pending == null, "Correction lowered original goals or created a second retry");
            var fact = (ArdyMotionExecutionFeedback)Get(f.chat, "m_LastMotionExecutionFeedback");
            Check(fact.status == "rejected" && fact.repairAttempt == 1 && fact.goal.leftGoal == "near-head", "Rejected correction rewrote target facts");
        }
        using (var f = new Fixture())
        {
            var initial = f.Submit(Generated); f.chat.NotifyMotionFeedback(Failed(initial)); f.Issue();
            f.model.Deliver("<motion name=\"right-wave\"/>");
            Check(f.actions.Count == 1 && f.Pending == null, "Correction silently substituted a basic gesture");
        }
        using (var f = new Fixture())
        {
            var initial = f.Submit(Generated); f.chat.NotifyMotionFeedback(Failed(initial)); f.Issue();
            f.model.Deliver("<motion name=\"none\"/>");
            Check(f.actions.Count == 2 && f.actions[1].Name == "none" && f.Pending == null, "Honest stop was forbidden or requested another action");
        }
        cases.Add("goal lowering and route substitution rejected; stop allowed; failed revision cannot recurse");
    }

    private static void FeedbackFences()
    {
        foreach (string mode in new[] { "new-motion", "new-user", "speaking", "disconnect", "wrong-id", "wrong-generation", "wrong-attempt", "unknown", "completed" })
        using (var f = new Fixture())
        {
            var initial = f.Submit(Generated); var fact = Failed(initial);
            switch (mode)
            {
                case "new-motion": f.Submit("<motion name=\"nod\"/>"); break;
                case "new-user": Call(f.chat, "RecordAcceptedMotionUserTurn", "A new public request."); break;
                case "speaking": Call(f.chat, "CancelDialogueMotion", "user-started-speaking"); break;
                case "disconnect": f.chat.ConfigureMotionOutput(false, false); f.chat.ConfigureMotionOutput(true, true); break;
                case "wrong-id": fact.actionId = "invented"; break;
                case "wrong-generation": fact.responseGeneration++; break;
                case "wrong-attempt": fact.repairAttempt++; break;
                case "unknown": fact.observation.status = "unknown"; break;
                case "completed": fact.status = "completed"; fact.observation.status = "reached"; break;
            }
            f.chat.NotifyMotionFeedback(fact); Check(f.Pending == null && f.model.requests == 0, "Improper feedback correction: " + mode);
        }
        cases.Add("feedback fences: current action/binding/user turn and immutable identity; unknown/completed never treated as failure");
    }

    private static void CallbackFences()
    {
        foreach (string mode in new[] { "new-motion", "new-user", "speaking", "disconnect", "new-generation" })
        using (var f = new Fixture())
        {
            var initial = f.Submit(Generated); f.chat.NotifyMotionFeedback(Failed(initial)); f.Issue();
            switch (mode)
            {
                case "new-motion": f.Submit("<motion name=\"nod\"/>"); break;
                case "new-user": Call(f.chat, "RecordAcceptedMotionUserTurn", "New source."); break;
                case "speaking": Call(f.chat, "CancelDialogueMotion", "user-started-speaking"); break;
                case "disconnect": f.chat.ConfigureMotionOutput(false, false); f.chat.ConfigureMotionOutput(true, true); break;
                case "new-generation": Call(f.chat, "BeginFormalResponseGeneration"); break;
            }
            int count = f.actions.Count; f.model.Deliver(Fixed);
            Check(f.actions.Count == count && f.Pending == null, "Late same-model callback gained newer authority: " + mode);
        }
        cases.Add("actual delayed model callbacks fenced after replacement, user speech, user turn, disconnect and new model generation");
    }

    private static void SpeechGuard()
    {
        using (var f = new Fixture())
        {
            Set(f.chat, "m_CurrentSpeechRoundId", 17);
            f.Submit("<motion name=\"generate\" text=\"" + new string('x', 241) + "\"/>");
            var buffer = (SpeechTextBuffer)Get(f.chat, "m_SentenceBuffer"); buffer.Append("I have completed it.");
            Call(f.chat, "FlushCompleteSentences", true);
            Check(buffer.Length == 0 && !((IEnumerable)Get(f.chat, "m_PendingChunks")).GetEnumerator().MoveNext(), "Rejected response queued more false completion speech");
            Check(((HashSet<int>)Get(f.chat, "m_SilencedSpeechRoundIds")).Contains(17), "Rejected response audio round was not silenced");
        }
        using (var f = new Fixture())
        {
            object[] args = { "I cannot perform that exact action." }; Call(f.chat, "ExtractDialogueMotion", args);
            Check(f.Pending == null && f.actions.Count == 0, "An explanation without a motion invented a corrective action");
        }
        cases.Add("unplayed failed-round TTS discarded; no command in an explanation does not force an action");
    }

    private static void ReplayAndUnknown()
    {
        using (var f = new Fixture())
        {
            var replay = f.Submit("<motion name=\"replay\" ref=\"last\"/>");
            var fact = Failed(replay); fact.status = "completed"; fact.canRepair = false;
            fact.goal = new ArdyMotionGoal { leftGoal = "above-head" }; fact.observation.status = "reached";
            f.chat.NotifyMotionFeedback(fact);
            Check(((ArdyMotionExecutionFeedback)Get(f.chat, "m_LastMotionExecutionFeedback")).goal.leftGoal == "above-head",
                "Replay erased the goal resolved from its saved controller record");
            Check(f.Pending == null, "Replay observation launched automatic new generation");
        }
        using (var f = new Fixture())
        {
            var replay = f.Submit("<motion name=\"replay\" ref=\"last\"/>");
            Check(replay.Name == "replay" && replay.ReplayReference == "last" && replay.ActionId.Length > 0, "Replay reference/identity lost in dispatch");
            var rejection = Failed(replay); rejection.status = "rejected"; rejection.reason = "No saved motion."; rejection.canRepair = false;
            f.chat.NotifyMotionFeedback(rejection);
            Check(f.Pending == null && f.actions.Count == 1, "Unknown replay reference secretly generated a replacement");
            string context = (string)Call(f.chat, "BuildMotionRequestContext", "");
            Check(context.Contains("No saved motion."), "Unknown replay failure was hidden from next conversation");
            var empty = new ArdyMotionExecutionFeedback { name = "nod", status = "accepted" };
            var serializer = typeof(ChatSample).GetMethod("MotionFeedbackJson", BindingFlags.Static | BindingFlags.NonPublic);
            string json = (string)serializer.Invoke(null, new object[] { empty });
            Check(json.Contains("\"goal\":null") && json.Contains("\"observation\":null"), "Null feedback fields became invented default observations");
        }
        cases.Add("exact replay dispatch reference; unknown replay facts without substitute; real JsonUtility null serialization");
    }

    private static void EmptyRepairOutcomes()
    {
        string[] rejected = {
            "<motion name=\"compose\" left=\"none\" right=\"none\" palm=\"partner\" joint=\"head\" axis=\"right\" amplitude=\"10\" cycles=\"1\" seconds=\"1.5\" end=\"idle\"/>",
            "<motion name=\"bow\"/>",
            "<motion name=\"compose\" right=\"forward\" rightGoal=\"forward\"/>",
            "<motion name=\"compose\" text=\"Nod gently.\" joint=\"head\" axis=\"right\"/>",
            "<motion name=\"generate\" text=\"" + new string('x', 309) + "\"/>" };
        foreach (string invalid in rejected)
        foreach (string empty in new[] { "", "<silent/>", "<think>Public synthetic private-channel fixture.</think>", "<silent/><next in=\"20\"/>" })
        using (var f = new Fixture())
        {
            Set(f.chat, "m_AgentRunning", true); Set(f.chat, "m_AgentRoundInFlight", true);
            Set(f.chat, "m_RoundWaitForUser", true); Set(f.chat, "m_UserTurnAwaitingReplySince", 11f);
            Set(f.chat, "m_CurrentSpeechRoundId", 18);
            string previous = (string)Get(f.chat, "m_LastAIMsgPlain");
            Call(f.chat, "OnStreamComplete", "Public original response that must not be recorded as spoken." + invalid);
            Check(f.Pending != null && f.actions.Count == 0, "Original invalid motion was accepted or no repair was queued");
            Check((float)Get(f.chat, "m_UserTurnAwaitingReplySince") == 11f, "Suppressed original response was falsely marked as answered");
            Check((string)Get(f.chat, "m_LastAIMsgPlain") == previous &&
                !((IEnumerable)Get(f.chat, "m_ChatHistory")).GetEnumerator().MoveNext() &&
                !((IEnumerable)Get(f.chat, "m_RecentAIUtterances")).GetEnumerator().MoveNext(), "Suppressed original response entered spoken/utterance history");
            f.Issue(); f.model.DeliverProjected(empty);
            Check(f.model.requests == 1 && f.Pending == null && Get(f.chat, "m_ActiveMotionRepair") == null,
                "Empty correction retained an active retry or replenished its budget");
            Check(f.actions.Count == 0 && !(bool)Get(f.chat, "m_FormalResponseInFlight"), "Empty/private/next-only correction was treated as an executable action");
            Check(!((IEnumerable)Get(f.chat, "m_ChatHistory")).GetEnumerator().MoveNext() &&
                !((IEnumerable)Get(f.chat, "m_PendingChunks")).GetEnumerator().MoveNext(), "Empty correction wrote speech/history or queued TTS");
            var overlay = (SubtitleOverlay)Get(f.chat, "m_SubtitleOverlay");
            var notices = overlay.NoticeHistory;
            Check(notices.Count == 1 && notices[0].Code == "motion_revision_empty" && notices[0].Severity == SystemNoticeSeverity.Error,
                "Empty correction did not produce an explicit user-visible failure class");
            Check((float)Get(f.chat, "m_UserTurnAwaitingReplySince") < 0 && Get(f.chat, "m_LastMotionRepairResult") != null,
                "Empty correction neither settled as failure nor retained its no-solution result");
            f.model.DeliverProjected(Fixed); Check(f.actions.Count == 0, "Late callback after empty failure revived correction");
        }
        cases.Add("five rejected-command forms → empty/silent/private-mapped/next-only correction: explicit failure; no invented speech/history; budget/fence preserved");
    }

    private static void RejectedExecutionNotice()
    {
        foreach (string tag in new[] {
            "<motion name=\"compose\" joint=\"head\" axis=\"right\" amplitude=\"8\" cycles=\"1\"/>",
            "<motion name=\"replay\" ref=\"last\"/>" })
        using (var f = new Fixture())
        {
            Set(f.chat, "m_UserTurnAwaitingReplySince", 12f);
            var action = f.Submit(tag); var fact = Failed(action);
            fact.status = "rejected"; fact.reason = "Public fixture: the requested control/replay is unavailable.";
            fact.canRepair = false; f.chat.NotifyMotionFeedback(fact);
            var notices = ((SubtitleOverlay)Get(f.chat, "m_SubtitleOverlay")).NoticeHistory;
            Check(f.Pending == null && f.model.requests == 0 && f.actions.Count == 1, "Compose/replay execution rejection launched a substitute");
            Check(notices.Count == 1 && notices[0].Code == "motion_execution_rejected" && notices[0].Severity == SystemNoticeSeverity.Error,
                "Non-repaired execution rejection silently swallowed the turn");
            Check((float)Get(f.chat, "m_UserTurnAwaitingReplySince") < 0, "Explicit execution failure left an endless unanswered turn");
        }
        cases.Add("compose and unknown replay execution rejection: explicit system failure with no blind regeneration");
    }

    private static void VisibleFailureNotice()
    {
        using (var f = new Fixture())
        {
            var display = new GameObject("ActiveSystemFailureDisplay", typeof(RectTransform), typeof(Canvas));
            try
            {
                var panel = new GameObject("CharacterPanel", typeof(RectTransform)); panel.transform.SetParent(display.transform, false);
                var original = (UnityEngine.UI.Text)Get(f.chat, "m_TextBack"); original.transform.SetParent(panel.transform, false);
                var overlay = display.AddComponent<SubtitleOverlay>(); Set(f.chat, "m_SubtitleOverlay", overlay);
                Set(f.chat, "m_SystemNoticeMode", SystemNoticeMode.ErrorsOnly);
                Set(f.chat, "m_SubtitleDisplayMode", SubtitleDisplayMode.OriginalOnly);
                f.Submit("<motion name=\"bow\"/>"); f.Issue(); f.model.DeliverProjected("<silent/>");
                var notice = (UnityEngine.UI.Text)Get(overlay, "m_SystemNoticeText");
                Check(overlay.NoticeMode == SystemNoticeMode.ErrorsOnly && notice != null && notice.gameObject.activeInHierarchy && notice.text.Length > 0,
                    "ErrorsOnly system failure was only a hidden log/history record");
                string rendered = notice.text;
                original.text = ""; Call(f.chat, "SetAnimator", "state", 0);
                Check(notice.gameObject.activeInHierarchy && notice.text == rendered && notice != original,
                    "Normal character text/Animator cleanup overwrote the separate system notice");
                Check(!((IEnumerable)Get(f.chat, "m_ChatHistory")).GetEnumerator().MoveNext() &&
                    !((IEnumerable)Get(f.chat, "m_PendingChunks")).GetEnumerator().MoveNext(), "System notice masqueraded as character speech");
            }
            finally { UnityEngine.Object.DestroyImmediate(display); }
        }
        cases.Add("ErrorsOnly actual active system Text visible and separate from cleared character Text; no TTS/history insertion");
    }

    private static void OrdinarySilent()
    {
        using (var f = new Fixture())
        {
            Set(f.chat, "m_AgentRunning", true); Set(f.chat, "m_AgentRoundInFlight", true); Set(f.chat, "m_RoundWaitForUser", true);
            Call(f.chat, "OnStreamComplete", "<silent/>");
            var overlay = (SubtitleOverlay)Get(f.chat, "m_SubtitleOverlay");
            Check(f.Pending == null && f.actions.Count == 0 && (overlay == null || overlay.NoticeHistory.Count == 0),
                "Ordinary autonomous silence was reclassified as a motion repair failure");
        }
        cases.Add("ordinary non-repair autonomous silent remains allowed");
    }
    private static ArdyMotionExecutionFeedback Failed(DialogueMotionIntent intent) => new ArdyMotionExecutionFeedback {
        actionId = intent.ActionId, parentActionId = intent.ParentActionId, responseGeneration = intent.ResponseGeneration,
        repairAttempt = intent.RepairAttempt, name = intent.Name, description = intent.Description, status = "goal-unmet",
        reason = "Actual right wrist did not reach the requested region.", canRepair = true, goal = intent.Goal?.Copy(),
        observation = new ArdyMotionObservation { actionId = intent.ActionId, status = "not-reached", finished = true, hasGoals = true } };
    private static object Get(object value, string field) => value.GetType().GetField(field, Flags).GetValue(value);
    private static object Property(object value, string property) => value.GetType().GetProperty(property, Flags).GetValue(value);
    private static void Set(object value, string field, object contents) => value.GetType().GetField(field, Flags).SetValue(value, contents);
    private static object Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, Flags).Invoke(value, args);
    private static void Check(bool condition, string message) { ++checks; if (!condition) throw new InvalidOperationException(message); }
}

public sealed class ArdyMotionFeedbackFakeLLM : LLM
{
    public int requests, messageRequests, feedbackRequests;
    public string context, regularContext, executionFeedback;
    private Action<SpeechText> delta;
    private Action<string> completion;
    public override void PostSpeechContinuationStream(string value, Action<SpeechText> onSpeech, Action<string> onComplete, string imageDataUrl = null)
    { ++requests; context = value; delta = onSpeech; completion = onComplete; }
    public override void PostSpeechMessage(string message, Action<List<SpeechText>, string> onComplete)
    { ++messageRequests; throw new InvalidOperationException("Repair must not insert a new user message."); }
    public override void PostSpeechFeedbackStream(string value, string feedback, Action<SpeechText> onSpeech, Action<string> onComplete, string imageDataUrl = null)
    {
        ++feedbackRequests; regularContext = value; executionFeedback = feedback;
        base.PostSpeechFeedbackStream(value, feedback, onSpeech, onComplete, imageDataUrl);
    }
    public void Deliver(string text)
    {
        delta?.Invoke(new SpeechText(text)); completion?.Invoke(text);
    }
    public void DeliverProjected(string text)
    {
        var channels = new RoleOutputChannels(delta); channels.Push(text); channels.Finish();
        completion?.Invoke(channels.ToExecutableText());
    }
}
