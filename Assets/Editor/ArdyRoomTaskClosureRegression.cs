using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Real connected room and ChatSample closure, with controllable in-process LLM completions.</summary>
public static class ArdyRoomTaskClosureRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    [Serializable] private sealed class Report
    {
        public bool passed;
        public int checks, maxDecisionInputUtf8Bytes;
        public string failure, checkedAtUtc = DateTime.UtcNow.ToString("o");
        public string scope = "Actual connected room, DispatchFormalStream intent callback, read-only current evidence, revision-checked actual rejection, buffered speech, semantic reviewer callback, history and TTS input queue. Fake LLM transport only; no Qwen semantic reliability or audible playback claim. Unsupported targets prevent ARDY generation requests.";
        public List<string> cases = new List<string>();
    }

    public static int RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge)
    {
        checks = 0; var report = new Report();
        Check(Application.isPlaying && chat != null && bridge != null && bridge.IsConnected && !chat.enabled,
            "Use the dormant ChatSample in the real connected Play-mode room integration fixture.");
        using (var fixture = new Fixture(chat, bridge))
        {
            try
            {
                OldRejectionAndNewAssessment(fixture); report.cases.Add("new-position-new-plan-and-history-isolation");
                StalePlanCallbacks(fixture); report.cases.Add("obsolete-generation-and-target-decisions-do-not-dispatch");
                ContradictorySpeech(fixture); report.cases.Add("backwards-speech-withheld-before-review-and-rejected-before-history-TTS");
                ConsistentSpeechPositiveControl(fixture); report.cases.Add("consistent-current-speech-released-exactly-once");
                TargetChangesDuringReview(fixture); report.cases.Add("changed-target-invalidates-review-and-refreshes-fallback-facts");
                InvalidSpeechFallback(fixture); report.cases.Add("invalid-public-speech-fallback-and-single-completion");
                StaleReview(fixture); report.cases.Add("obsolete-review-cannot-release-speech-and-new-stop-owns-its-action");
                OrdinaryTopic(fixture); report.cases.Add("nonspatial-topic-including-valid-user-citation-remains-ordinary-and-prose-room-tag-cannot-dispatch");
                report.passed = true;
            }
            catch (Exception error) { report.failure = error.ToString(); throw; }
            finally
            {
                report.checks = checks;
                report.maxDecisionInputUtf8Bytes = fixture.Model.Decisions.Count == 0 ? 0 :
                    fixture.Model.Decisions.Max(request => System.Text.Encoding.UTF8.GetByteCount(request.Input));
                string path = Path.GetFullPath("Tools/MotionAdapter/reports/room-task-closure-regression.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(report, true) + "\n");
            }
        }
        Debug.Log("[ArdyRoomTaskClosureRegression] passed " + checks + " checks.");
        return checks;
    }

    private sealed class Fixture : IDisposable
    {
        public readonly ChatSample Chat;
        public readonly ArdyRoomDialogueBridge Bridge;
        public readonly ArdyRoomTaskClosureFakeLLM Model;
        private readonly TTS tts;
        private readonly Vector3 savedUser, savedRoot;
        private readonly Quaternion savedRotation;
        private readonly Dictionary<string, object> saved = new Dictionary<string, object>();
        public Fixture(ChatSample chat, ArdyRoomDialogueBridge bridge)
        {
            Chat = chat; Bridge = bridge; savedUser = bridge.userHead.position;
            savedRoot = bridge.avatar.transform.position; savedRotation = bridge.avatar.transform.rotation;
            Model = chat.gameObject.AddComponent<ArdyRoomTaskClosureFakeLLM>();
            tts = chat.gameObject.AddComponent<TTS>();
            foreach (string field in new[] { "m_ChatSettings", "m_ChatHistory", "m_UseStreaming", "m_IsVoiceMode",
                "m_AgentRunning", "m_AgentCurrentRoundIsTick", "m_LogStreamTimings", "m_LogAgentLoop", "m_LogRawLLMOutput",
                "m_EnableSubtitleTranslation", "m_SystemNoticeMode", "m_LastUserTurnTime" }) saved[field] = Get(Chat, field);
            Set(Chat, "m_ChatSettings", new ChatSetting { m_ChatModel = Model, m_TextToSpeech = tts });
            Set(Chat, "m_ChatHistory", new List<string>()); Set(Chat, "m_UseStreaming", true); Set(Chat, "m_IsVoiceMode", true);
            Set(Chat, "m_AgentRunning", false); Set(Chat, "m_AgentCurrentRoundIsTick", false);
            Set(Chat, "m_LogStreamTimings", false); Set(Chat, "m_LogAgentLoop", false); Set(Chat, "m_LogRawLLMOutput", false);
            Set(Chat, "m_EnableSubtitleTranslation", false); Set(Chat, "m_SystemNoticeMode", SystemNoticeMode.Hidden);
        }
        public void Unsupported(float x = 0) => Bridge.userHead.position = savedRoot + Vector3.up * 10f + Vector3.right * x;
        public void RestoreUser() => Bridge.userHead.position = savedUser;
        public ArdyRoomTaskClosureFakeLLM.Auxiliary Begin(string input)
        {
            ClearSpeech(); Set(Chat, "m_UserSpeechActiveForAutonomy", false);
            // All bounded cases run synchronously in one frame. Each Begin represents a fresh
            // accepted user turn, not an autonomous replay of the immediately prior fallback.
            Set(Chat, "m_LastUserTurnTime", Time.realtimeSinceStartup + .01f);
            Call(Chat, "RecordAcceptedMotionUserTurn", input);
            int generation = (int)Call(Chat, "BeginFormalResponseGeneration");
            int before = Model.Decisions.Count;
            Call(Chat, "DispatchFormalStream", input, null, generation, true, "Public synthetic closure fixture", false);
            Check(Model.Decisions.Count == before + 1, "Production formal stream did not enter exactly one spatial decision request.");
            var request = Model.Decisions.Last();
            var payload = JObject.Parse(request.Input);
            var root = payload["currentFacts"]?["actualRoot"] as JObject;
            Check(root != null && root.Count == 3 && new[] { "x", "y", "z" }.All(axis =>
                root[axis] != null && (root[axis].Type == JTokenType.Float || root[axis].Type == JTokenType.Integer) &&
                !double.IsNaN((double)root[axis]) && !double.IsInfinity((double)root[axis])) &&
                root["normalized"] == null && root["magnitude"] == null && root["sqrMagnitude"] == null,
                "Current actualRoot must serialize as three finite coordinates, without recursive Unity Vector3 properties.");
            Check(payload["recentDialogue"] is JArray && payload["historicalAndCurrentRoomFacts"] == null &&
                payload["currentFacts"]?["assessment"]?["budgetChecked"]?.Type == JTokenType.Boolean &&
                payload["currentFacts"]?["assessment"]?["executionGuaranteed"]?.Type == JTokenType.Boolean &&
                !(bool)payload["currentFacts"]["assessment"]["budgetChecked"] &&
                !(bool)payload["currentFacts"]["assessment"]["executionGuaranteed"],
                "Compact decision payload must quote recent dialogue as an array, avoid duplicated historical context and preserve geometry-only assessment limits.");
            return request;
        }
        public void Decide(ArdyRoomTaskClosureFakeLLM.Auxiliary request, string intent)
        {
            string input = (string)JObject.Parse(request.Input)["currentUserText"];
            request.Deliver(true, JsonConvert.SerializeObject(new { intent, origin = intent == "none" ? "none" : "user", evidence = intent == "none" ? "" : input }));
        }
        public void ClearSpeech()
        {
            ((SpeechTextBuffer)Get(Chat, "m_SentenceBuffer")).Length = 0;
            Call(Get(Chat, "m_PendingChunks"), "Clear"); Call(Get(Chat, "m_PendingClips"), "Clear");
            Set(Chat, "m_FirstChunkFlushed", false); Set(Chat, "m_RoundSilencedForRepeat", false);
            Set(Chat, "m_StreamComplete", true);
        }
        public int SpeechCount => ((ICollection)Get(Chat, "m_PendingChunks")).Count;
        public int AssistantCount => Model.m_DataList.Count(x => x.role == "assistant");
        public string AssistantText => string.Join("\n", Model.m_DataList.Where(x => x.role == "assistant").Select(x => x.content));
        public string QueuedSpeech => string.Join("\n", ((IEnumerable)Get(Chat, "m_PendingChunks")).Cast<object>().Select(x => (string)Get(x, "Text")));
        public void CheckNoBodyRequest(int operation)
        {
            Check(Bridge.Controller.OperationRevision == operation && !Bridge.ReservesBody && !Bridge.Controller.IsBusy &&
                Vector3.Distance(savedRoot, Bridge.avatar.transform.position) < .0001f &&
                Quaternion.Angle(savedRotation, Bridge.avatar.transform.rotation) < .05f,
                "Closure fixture dispatched generation or changed the preserved avatar root.");
        }
        public void Dispose()
        {
            Call(Chat, "InvalidateFormalResponse", "room-closure-fixture-finished"); ClearSpeech(); RestoreUser();
            foreach (var item in saved) Set(Chat, item.Key, item.Value);
            UnityEngine.Object.DestroyImmediate(Model); UnityEngine.Object.DestroyImmediate(tts);
        }
    }

    private static void OldRejectionAndNewAssessment(Fixture f)
    {
        f.Unsupported(); int operation = f.Bridge.Controller.OperationRevision;
        var request = f.Begin("请走到我身边。"); f.Decide(request, "approach");
        Check(f.Bridge.Phase == "unreachable" && !string.IsNullOrEmpty(f.Bridge.ActionId), "Structured request did not record actual fresh unsupported-target rejection.");
        string firstAction = f.Bridge.ActionId; int firstTarget = f.Bridge.ReadCurrentTargetEvidence().currentTargetRevision;
        int speechRequests = f.Model.Speech.Count; f.Decide(request, "approach");
        Check(f.Model.Speech.Count == speechRequests && f.Bridge.ActionId == firstAction, "Duplicate decision completion produced another action or reply.");
        f.Model.Speech.Last().Complete("");
        Check(f.SpeechCount > 0 && f.AssistantCount == 1, "Empty candidate failed to produce a factual bounded fallback.");
        f.Unsupported(.5f); request = f.Begin("现在这个位置呢，再试试看。");
        var input = JObject.Parse(request.Input);
        var facts = input["currentFacts"];
        Check((int)facts["currentTargetRevision"] != firstTarget && !(bool)facts["lastAttemptAppliesToCurrentTarget"] &&
            (string)facts["attemptStatus"] == "no-attempt", "New request still treats the prior result as the current attempted target.");
        Check((string)input["lastActionResults"]?["actionId"] == firstAction &&
            (int)input["lastActionResults"]["targetRevision"] == firstTarget &&
            !(bool)input["lastActionResults"]["appliesToCurrentTarget"],
            "Compact payload lost the distinct historical target identity or applied its old rejection to the new target.");
        f.Decide(request, "approach");
        Check(f.Bridge.ActionId != firstAction && f.Bridge.Phase == "unreachable", "New-position request reused the old action instead of checking a fresh attempt.");
        f.Model.Speech.Last().Complete("");
        f.RestoreUser(); request = f.Begin("只检查一下现在能不能靠近，不要移动。");
        f.Decide(request, "inspect");
        Check(f.Bridge.ReadCurrentTargetEvidence().assessment.canPlanRoute &&
            (string)Get(f.Chat, "m_RoomTaskDispatch") == "read-only-checked", "Current-position inspection retained the old support rejection.");
        f.Model.Speech.Last().Complete(""); f.CheckNoBodyRequest(operation);
    }

    private static void StalePlanCallbacks(Fixture f)
    {
        f.Unsupported(); int operation = f.Bridge.Controller.OperationRevision;
        var request = f.Begin("请过来。"); int speechRequests = f.Model.Speech.Count;
        string action = f.Bridge.ActionId;
        Call(f.Chat, "InvalidateFormalResponse", "user-started-speaking"); f.Decide(request, "approach");
        Check(f.Model.Speech.Count == speechRequests && f.Bridge.ActionId == action, "Cancelled generation's auxiliary callback still dispatched or spoke.");
        f.RestoreUser(); request = f.Begin("再走过来试试。"); f.Unsupported(.8f); f.Decide(request, "approach");
        Check(((string)Get(f.Chat, "m_RoomTaskDispatch")).StartsWith("not-submitted", StringComparison.Ordinal) &&
            f.Bridge.ActionId == action, "User moving during planning did not invalidate the prepared target revision.");
        f.Model.Speech.Last().Complete(""); f.CheckNoBodyRequest(operation);
    }

    private static void ContradictorySpeech(Fixture f)
    {
        f.Unsupported(); var request = f.Begin("我往后走一点，你来我身边。"); f.Decide(request, "approach");
        int history = f.AssistantCount; int reviews = f.Model.Reviews.Count;
        var stream = f.Model.Speech.Last(); const string candidate = "我向后退开一点。";
        Check(!stream.RecordAssistantHistory, "Guarded candidate was allowed to auto-write provider assistant history.");
        stream.Emit(candidate);
        Check(f.SpeechCount == 0 && f.AssistantCount == history, "Unaudited streaming candidate reached TTS queue or history.");
        stream.Finish(candidate);
        Check(f.Model.Reviews.Count == reviews + 1 && f.SpeechCount == 0 && f.AssistantCount == history,
            "Candidate completion bypassed semantic review hold.");
        var review = f.Model.Reviews.Last(); review.Deliver(true, "{\"verdict\":\"inconsistent\",\"reason\":\"向后退与本次approach矛盾\"}");
        Check(f.SpeechCount > 0 && f.AssistantCount == history + 1 && !f.QueuedSpeech.Contains(candidate) && !f.AssistantText.Contains(candidate),
            "Rejected backward claim entered accepted history/TTS, or fallback remained silent.");
        int accepted = f.AssistantCount; int queued = f.SpeechCount;
        review.Deliver(true, "{\"verdict\":\"consistent\",\"reason\":\"late duplicate\"}");
        Check(f.AssistantCount == accepted && f.SpeechCount == queued, "Duplicate review completion released another reply.");
    }

    private static void InvalidSpeechFallback(Fixture f)
    {
        f.Unsupported(); var request = f.Begin("再试一次走近。"); f.Decide(request, "approach");
        int reviews = f.Model.Reviews.Count; int history = f.AssistantCount;
        f.Model.Speech.Last().Emit("[body_inspect scope=\"runtime\"]");
        Call(f.Chat, "HandleLLMOutputFormatError", "Synthetic malformed public tool output");
        f.Model.Speech.Last().Finish("[body_inspect scope=\"runtime\"]");
        Check(f.Model.Reviews.Count == reviews && f.AssistantCount == history + 1 && f.SpeechCount > 0 &&
            !f.QueuedSpeech.Contains("body_inspect") && !f.AssistantText.Contains("body_inspect"),
            "Known malformed public tool output was reviewed/released as speech instead of replaced with factual fallback.");
    }

    private static void ConsistentSpeechPositiveControl(Fixture f)
    {
        f.RestoreUser(); var request = f.Begin("只检查当前路线，先不要移动。"); f.Decide(request, "inspect");
        const string candidate = "我检查了一下，当前有可用的路线，但这次没有移动。";
        int history = f.AssistantCount;
        f.Model.Speech.Last().Complete(candidate); var review = f.Model.Reviews.Last();
        Check(f.SpeechCount == 0 && f.AssistantCount == history, "Positive-control candidate bypassed review before being approved.");
        review.Deliver(true, "{\"verdict\":\"consistent\",\"reason\":\"只读可达事实一致且未声称执行\"}");
        Check(f.AssistantCount == history + 1 && f.Model.m_DataList.Last(x => x.role == "assistant").content == candidate &&
            f.QueuedSpeech.Replace("\n", "") == candidate,
            "Consistent speech was replaced by fallback, duplicated or did not reach the real TTS input queue.");
        int queued = f.SpeechCount;
        review.Deliver(true, "{\"verdict\":\"consistent\",\"reason\":\"duplicate positive completion\"}");
        Check(f.AssistantCount == history + 1 && f.SpeechCount == queued, "Consistent positive review released the reply more than once.");
    }

    private static void TargetChangesDuringReview(Fixture f)
    {
        f.Unsupported(); var request = f.Begin("你试着来到我身边。"); f.Decide(request, "approach");
        const string candidate = "当前这个站位的支撑检查没有通过，所以我还没有移动。";
        int history = f.AssistantCount;
        f.Model.Speech.Last().Complete(candidate); var review = f.Model.Reviews.Last();
        int reviewedTarget = f.Bridge.ReadCurrentTargetEvidence().currentTargetRevision;
        f.RestoreUser(); var changed = f.Bridge.ReadCurrentTargetEvidence(true);
        Check(changed.currentTargetRevision != reviewedTarget && changed.assessment.canPlanRoute &&
            !changed.lastAttemptAppliesToCurrentTarget, "Review target-change fixture did not establish a fresh reachable and unattempted target.");
        review.Deliver(true, "{\"verdict\":\"consistent\",\"reason\":\"审核仅针对旧站位\"}");
        string accepted = f.Model.m_DataList.Last(x => x.role == "assistant").content;
        Check(f.AssistantCount == history + 1 && f.SpeechCount > 0 && accepted != candidate && !f.QueuedSpeech.Contains(candidate),
            "Old consistent review was released after the user target changed.");
        Check(!accepted.Contains("当前位置的路线或空间检查没有通过") && !accepted.Contains("当前这个站位的支撑检查没有通过") &&
            !accepted.Contains("已经走到"), "Changed-target fallback reused the old rejection as a current failure or invented a new arrival.");
    }

    private static void StaleReview(Fixture f)
    {
        f.Unsupported(); var request = f.Begin("请走过来。"); f.Decide(request, "approach");
        int history = f.AssistantCount;
        f.Model.Speech.Last().Complete("现在这个位置没能通过检查。"); var review = f.Model.Reviews.Last();
        Call(f.Chat, "InvalidateFormalResponse", "user-started-speaking");
        review.Deliver(true, "{\"verdict\":\"consistent\",\"reason\":\"current facts agree\"}");
        Check(f.AssistantCount == history && f.SpeechCount == 0, "Obsolete speech-review callback released cancelled speech/history.");

        f.Unsupported(); request = f.Begin("再尝试走近我。"); f.Decide(request, "approach");
        const string obsolete = "我还没有走到刚才的位置。";
        f.Model.Speech.Last().Complete(obsolete); review = f.Model.Reviews.Last();
        string previousAction = f.Bridge.ActionId; int operation = f.Bridge.Controller.OperationRevision;
        request = f.Begin("停下，不要移动。"); f.Decide(request, "stop-moving");
        string stopAction = (string)Get(f.Chat, "m_RoomTaskActionId");
        Check(!string.IsNullOrEmpty(stopAction) && stopAction != previousAction && f.Bridge.ActionId == stopAction &&
            f.Bridge.Phase == "stopped" && !f.Bridge.ReservesBody && (string)Get(f.Chat, "m_RoomTaskDispatch") == "stop-submitted",
            "Structured stop did not acquire its own registered identity and actual stopped bridge state.");
        history = f.AssistantCount;
        review.Deliver(true, "{\"verdict\":\"consistent\",\"reason\":\"old approach review\"}");
        Check(f.AssistantCount == history && f.SpeechCount == 0, "Old approach review contaminated the new stop task's speech/history.");
        const string stopped = "我已经停下来了。";
        f.Model.Speech.Last().Complete(stopped);
        f.Model.Reviews.Last().Deliver(true, "{\"verdict\":\"consistent\",\"reason\":\"本次stop已经实际处理\"}");
        Check(f.AssistantCount == history + 1 && f.Model.m_DataList.Last(x => x.role == "assistant").content == stopped &&
            f.QueuedSpeech.Replace("\n", "") == stopped && !f.QueuedSpeech.Contains(obsolete),
            "Current stop confirmation failed to release exactly the reviewed current task speech.");
        f.CheckNoBodyRequest(operation);
    }

    private static void OrdinaryTopic(Fixture f)
    {
        f.RestoreUser(); int operation = f.Bridge.Controller.OperationRevision;
        var request = f.Begin("今天的天气怎么样？"); f.Decide(request, "none");
        int reviews = f.Model.Reviews.Count; string action = f.Bridge.ActionId;
        var stream = f.Model.Speech.Last(); Check(stream.RecordAssistantHistory, "Ordinary nonspatial reply was forced into the spatial history guard.");
        stream.CompleteProjected("<lang code=\"zh\"/><speech mode=\"independent\"/>窗外看起来很明亮。<motion name=\"approach\"/>");
        Check(f.Model.Reviews.Count == reviews && f.SpeechCount > 0 && f.QueuedSpeech.Contains("很明亮") &&
            !f.QueuedSpeech.Contains("motion") && f.Bridge.ActionId == action,
            "Ordinary topic was silenced/reviewed or its incidental room tag obtained execution authority.");
        f.CheckNoBodyRequest(operation);

        request = f.Begin("给我讲一个短故事。");
        request.Deliver(true, "{\"intent\":\"none\",\"origin\":\"user\",\"evidence\":\"给我讲一个短故事\"}");
        Check((string)Get(f.Chat, "m_RoomTaskIntent") == "none" && f.Model.Speech.Last().RecordAssistantHistory,
            "A valid current-user citation on a no-action decision was treated as unresolved spatial work.");
        reviews = f.Model.Reviews.Count;
        f.Model.Speech.Last().CompleteProjected("<lang code=\"zh\"/><speech mode=\"independent\"/>从前有只小猫住在窗边。");
        Check(f.Model.Reviews.Count == reviews && f.QueuedSpeech.Contains("小猫") && f.Bridge.ActionId == action,
            "Grounded origin=user none failed the ordinary speech path or dispatched an action.");
        f.CheckNoBodyRequest(operation);
    }

    private static object Get(object target, string name)
    {
        var field = target.GetType().GetField(name, Flags) ?? throw new MissingFieldException(target.GetType().Name, name);
        return field.GetValue(target);
    }
    private static void Set(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, Flags) ?? throw new MissingFieldException(target.GetType().Name, name);
        field.SetValue(target, value);
    }
    private static object Call(object target, string name, params object[] args)
    {
        var method = target.GetType().GetMethod(name, Flags) ?? throw new MissingMethodException(target.GetType().Name, name);
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static void Check(bool condition, string message) { ++checks; if (!condition) throw new InvalidOperationException(message); }
}

public sealed class ArdyRoomTaskClosureFakeLLM : LLM
{
    public sealed class Auxiliary
    {
        public string Input;
        public Action<bool, string, string> Callback;
        public void Deliver(bool ok, string json) => Callback(ok, json, "controlled-fixture");
    }
    public sealed class SpeechRequest
    {
        public bool RecordAssistantHistory;
        public Action<SpeechText> Delta;
        public Action<string> Completion;
        public ArdyRoomTaskClosureFakeLLM Owner;
        public void Emit(string text) => Delta(new SpeechText(text, "zh", "fixture", SpeechActionDependency.Independent));
        public void Finish(string executable)
        {
            if (RecordAssistantHistory) Owner.m_DataList.Add(new SendData("assistant", executable));
            Completion(executable);
        }
        public void Complete(string text) { Emit(text); Finish(text); }
        public void CompleteProjected(string output)
        {
            var channels = new RoleOutputChannels(Delta); channels.Push(output); channels.Finish(); Finish(channels.ToExecutableText());
        }
    }
    public readonly List<Auxiliary> Decisions = new List<Auxiliary>(), Reviews = new List<Auxiliary>();
    public readonly List<SpeechRequest> Speech = new List<SpeechRequest>();
    public override bool SupportsRoomTaskMessages => true;
    public override void PostRoomTaskMessage(string input, bool reviewSpeech, Action<bool, string, string> callback)
        => (reviewSpeech ? Reviews : Decisions).Add(new Auxiliary { Input = input, Callback = callback });
    public override void PostSpeechStream(string message, Action<SpeechText> onSpeech, Action<string> onComplete,
        string imageDataUrl = null, bool recordAssistantHistory = true)
    {
        m_DataList.Add(new SendData("user", message));
        Speech.Add(new SpeechRequest { Delta = onSpeech, Completion = onComplete, RecordAssistantHistory = recordAssistantHistory, Owner = this });
    }
    // Preserve callback handles after cancellation deliberately: production fences, not the test
    // transport, must reject a late successful callback.
    public override void CancelActiveResponse() { }
}
