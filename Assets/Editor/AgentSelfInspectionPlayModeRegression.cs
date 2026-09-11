using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NeEEvA.Motion;
using UnityEditor;
using UnityEngine;

public static class AgentSelfInspectionPlayModeRegression
{
    private const string Key = "NeEEvA.AgentSelfInspection.PlayMode";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static GameObject host;
    private static IEnumerator test;
    private static string failure;

    [InitializeOnLoadMethod]
    private static void Resume()
    {
        EditorApplication.playModeStateChanged -= Changed;
        EditorApplication.playModeStateChanged += Changed;
        if (SessionState.GetBool(Key, false) && EditorApplication.isPlaying)
            EditorApplication.delayCall += Begin;
    }
    public static void RunBatch()
    {
        SessionState.SetBool(Key, true);
        EditorApplication.EnterPlaymode();
    }
    private static void Changed(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Key, false)) Begin();
    }
    private static void Begin()
    {
        if (test != null) return;
        checks = 0; test = Run();
        EditorApplication.update += Step;
    }
    private static void Step()
    {
        bool done = false;
        try { done = !test.MoveNext(); }
        catch (Exception error) { failure = error.ToString(); done = true; }
        if (!done) return;
        EditorApplication.update -= Step;
        SessionState.SetBool(Key, false);
        if (host != null) UnityEngine.Object.DestroyImmediate(host);
        var report = new JObject { ["passed"] = failure == null, ["checks"] = checks,
            ["error"] = failure, ["scope"] = "Real Play Mode, production FireTick -> probe callback -> formal feedback -> tool parsing -> timer -> result feedback; fake model/TTS transport, real registered resource inspection. No private scene or physical motion." };
        string path = Path.GetFullPath("Tools/MotionAdapter/reports/agent-self-inspection-playmode.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, report.ToString() + "\n");
        EditorApplication.Exit(failure == null ? 0 : 1);
    }

    private static IEnumerator Run()
    {
        host = new GameObject("SelfInspectionPlayModeFixture"); host.SetActive(false);
        var chat = host.AddComponent<ChatSample>(); chat.enabled = false;
        var model = host.AddComponent<AgentInspectionPlayFakeLLM>(); model.enabled = false;
        var tts = host.AddComponent<TTS>(); tts.enabled = false;
        var audio = host.AddComponent<AudioSource>(); audio.playOnAwake = false;
        var controller = host.AddComponent<ArdyLiveMotionController>(); controller.enabled = false;
        var text = new GameObject("Text", typeof(RectTransform), typeof(UnityEngine.UI.Text)); text.transform.SetParent(host.transform);
        Set(chat, "m_TextBack", text.GetComponent<UnityEngine.UI.Text>());
        var button = new GameObject("Send", typeof(RectTransform), typeof(UnityEngine.UI.Button)); button.transform.SetParent(host.transform);
        Set(chat, "m_CommitMsgBtn", button.GetComponent<UnityEngine.UI.Button>());
        Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = model, m_TextToSpeech = tts });
        Set(chat, "m_AudioSource", audio); Set(chat, "m_ChatHistory", new List<string>());
        Set(chat, "m_PersistSubtitleSettings", false); Set(chat, "m_EnableSubtitleTranslation", false);
        Set(chat, "m_SystemNoticeMode", SystemNoticeMode.Hidden); Set(chat, "m_EnableLatencyFiller", false);
        Set(chat, "m_LogAgentLoop", true); Set(chat, "m_LogStreamTimings", false);
        Set(chat, "m_MinTickSec", .1f); Set(chat, "m_DefaultTickSec", 300f);
        host.SetActive(true);
        audio.Stop();
        yield return null;
        Set(chat, "m_AgentRunning", true);
        Call(chat, "SetCurrentUserInput", "你有哪些预制动作？请确认后告诉我。", "public");
        Call(chat, "RecordWorkResponse", "ちょっと待っててね。<motion name=\"nod\"/><next in=\"5s\"/>");
        int reads = 0;
        chat.BodyInspectionContextRequested += () => { reads++; return controller.InspectCapabilities(); };
        // Completion is still owed even after the idle monologue cap.
        Set(chat, "m_ConsecutiveAITurns", 50);
        Call(chat, "FireTick", "scheduled");
        Check(model.probes == 1 && model.feedbackRequests == 0, "Pending user work not reviewed beyond idle cap: probes=" + model.probes + ", feedback=" + model.feedbackRequests +
            ", open=" + Get(chat, "m_WorkReviewOpen") + ", formal=" + Get(chat, "m_FormalResponseInFlight") + ", busy=" + Call(chat, "HasWorkToolInFlight"));
        model.Decide(WorkReview("读取当前身体动作资源并回答", false));
        Check(model.feedbackRequests == 1 && model.userRequests == 0, "Follow-up did not continue original request through feedback transport");
        Check(model.feedback.Contains("不是主动闲聊") && (int)Get(chat, "m_WorkContinuations") == 1, "Work identity or program budget lost");
        model.Complete("<silent/><body_inspect scope=\"motion\"/><continue/>");
        Check(reads == 1, "Actual inspection not invoked");
        double deadline = EditorApplication.timeSinceStartup + 8;
        while (model.feedbackRequests < 2 && EditorApplication.timeSinceStartup < deadline) yield return null;
        Check(model.feedbackRequests == 2 && model.probes == 1, "Completed inspection failed to wake directly or re-entered idle novelty");
        Check(model.feedback.Contains("registered-resources") && model.feedback.Contains("left-wave"), "Actual resource results absent from feedback");
        Check(!string.IsNullOrEmpty((string)Get(chat, "m_PendingSelfInspection")), "An unanswered dispatched request prematurely consumed its inspection result");
        // A duplicate tool in the result reply must not create another result/wake.
        model.Complete("<silent/><body_inspect scope=\"motion\"/>");
        yield return null;
        Check(reads == 1 && (string)Get(chat, "m_PendingSelfInspection") == "", "Duplicate tool re-read or requeued results");
        Set(chat, "m_AgentRoundInFlight", false);
        Set(chat, "m_FormalResponseInFlight", false);
        Set(chat, "m_UserSpeechActiveForAutonomy", true);
        Call(chat, "FireTick", "scheduled");
        Check(model.probes == 2, "Private probe did not run while user spoke");
        model.Decide(WorkReview("说明检查结果", true));
        Check(model.feedbackRequests == 2, "Private thought interrupted user with formal output");
        Set(chat, "m_UserSpeechActiveForAutonomy", false);
        Call(chat, "SetCurrentUserInput", "检查声音队列", "public");
        Call(chat, "RecordWorkResponse", "我会确认。<next in=\"5s\"/>");
        Call(chat, "FireTick", "scheduled");
        model.Decide("malformed progress result");
        Check(model.feedbackRequests == 3 && (int)Get(chat, "m_WorkContinuations") == 1,
            "Malformed work review escaped bounded feedback recovery");
        Call(chat, "InvalidateFormalResponse", "test-new-settings");
        Set(chat, "m_AgentRoundInFlight", false); Set(chat, "m_EnableAutonomyIntentProbe", false);
        Set(chat, "m_ConsecutiveAITurns", 50);
        Call(chat, "FireTick", "scheduled");
        Check(model.feedbackRequests == 3 && model.userRequests == 0, "Disabled probe bypassed the idle monologue cap");
        Set(chat, "m_AgentRunning", false);
    }
    private static string WorkReview(string intent, bool proceed) => new JObject {
        ["work_status"] = "continue", ["proceed"] = proceed, ["intent"] = intent,
        ["work_evidence"] = "当前真实请求的检查结果仍需读取和说明。", ["wait_seconds"] = 45,
        ["singing_goal_status"] = "none", ["singing_goal_evidence"] = "",
        ["singing_goal_expected"] = new JObject { ["origin"] = "none", ["request_quote"] = "", ["refs"] = "", ["range"] = "none",
            ["start_seconds"] = null, ["end_seconds"] = null }
    }.ToString();
    private static object Call(object target, string method, params object[] args) => typeof(ChatSample).GetMethod(method, Flags).Invoke(target, args);
    private static object Get(object target, string field) => typeof(ChatSample).GetField(field, Flags).GetValue(target);
    private static void Set(object target, string field, object value) => typeof(ChatSample).GetField(field, Flags).SetValue(target, value);
    private static void Check(bool ok, string message) { checks++; if (!ok) throw new InvalidOperationException(message); }
}

public sealed class AgentInspectionPlayFakeLLM : LLM
{
    public int probes, feedbackRequests, userRequests;
    public string feedback;
    private Action<string> probe, completion;
    private Action<SpeechText> speech;
    public override void PostEphemeralMsg(string prompt, Action<string> callback) { probes++; probe = callback; }
    public override void PostSpeechFeedbackStream(string context, string facts, Action<SpeechText> delta, Action<string> done, string imageDataUrl = null)
    { feedbackRequests++; feedback = facts; speech = delta; completion = done; }
    public override void PostSpeechStream(string prompt, Action<SpeechText> delta, Action<string> done, string imageDataUrl = null, bool recordAssistantHistory = true)
    { userRequests++; throw new InvalidOperationException("Work must not create a fake user message"); }
    public void Decide(string result) => probe?.Invoke(result);
    public void Complete(string value)
    {
        var channels = new RoleOutputChannels(speech); channels.Push(value); channels.Finish();
        completion?.Invoke(channels.ToExecutableText());
    }
}
