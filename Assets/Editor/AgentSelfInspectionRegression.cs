using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NeEEvA.Motion;
using UnityEditor;
using UnityEngine;

public static class AgentSelfInspectionRegression
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int checks;
    private static readonly JObject report = new JObject();

    public static void RunBatch()
    {
        bool passed = false;
        try
        {
            WorkReview(); Inspection(); Feedback();
            ArdyDialogueMotionRegression.RunInteractive();
            ArdyMotionFeedbackChatRegression.RunInteractive();
            ArdySemanticMotionRegression.RunInteractive();
            passed = true;
        }
        catch (Exception error) { report["error"] = error.ToString(); Debug.LogException(error); }
        finally
        {
            report["passed"] = passed; report["checks"] = checks;
            report["scope"] = "Actual production parser, work review, inspection resource reads, callback fences and existing chat regressions. Model transport simulated; no physical demonstration or private scene.";
            string path = Path.GetFullPath("Tools/MotionAdapter/reports/agent-self-inspection-regression.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, report.ToString() + "\n");
        }
        EditorApplication.Exit(passed ? 0 : 1);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly GameObject host;
        public readonly ChatSample chat;
        public readonly AgentInspectionFakeLLM model;
        public Fixture()
        {
            host = new GameObject("InactiveSelfInspectionFixture"); host.SetActive(false);
            chat = host.AddComponent<ChatSample>(); model = host.AddComponent<AgentInspectionFakeLLM>();
            var text = new GameObject("Text", typeof(RectTransform), typeof(UnityEngine.UI.Text));
            text.transform.SetParent(host.transform, false);
            Set(chat, "m_TextBack", text.GetComponent<UnityEngine.UI.Text>());
            Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = model });
            Set(chat, "m_ChatHistory", new List<string>());
            Set(chat, "m_PersistSubtitleSettings", false); Set(chat, "m_EnableSubtitleTranslation", false);
            Set(chat, "m_SystemNoticeMode", SystemNoticeMode.Hidden); Set(chat, "m_LogAgentLoop", false);
            Set(chat, "m_AgentRunning", true);
            Call(chat, "SetCurrentUserInput", "你有哪些预制动作？请确认后告诉我。", "public");
        }
        public void Dispose() { Set(chat, "m_AgentRunning", false); UnityEngine.Object.DestroyImmediate(host); }
    }

    private static object Decision(string status, bool proceed = false, string intent = "检查可用基础动作并回答原问题")
    {
        var type = typeof(ChatSample).GetNestedType("AutonomyIntentDecision", BindingFlags.NonPublic);
        object value = Activator.CreateInstance(type, true);
        type.GetField("work_status", Flags).SetValue(value, status);
        type.GetField("proceed", Flags).SetValue(value, proceed);
        type.GetField("intent", Flags).SetValue(value, intent);
        return value;
    }

    private static void WorkReview()
    {
        using (var f = new Fixture())
        {
            const string reply = "ちょっと待っててね。<motion name=\"nod\"/><next in=\"5s\" focus=\"平静\"/>";
            Call(f.chat, "RecordWorkResponse", reply);
            string prompt = (string)Call(f.chat, "BuildAutonomyIntentProbePrompt", "scheduled", "[感知帧] 没有新的用户消息。");
            Check(prompt.Contains(reply.Replace("\"", "\\\"")) && prompt.Contains("预制动作") && prompt.Contains("work_status"), "Review lost original request or promise/action evidence");
            Check(prompt.Contains("不讨论主动闲聊或新意"), "Pending work remains subject to novelty");
            report["publicProbePrompt"] = prompt;
            report["publicFormalContract"] = (string)Call(f.chat, "BuildMotionRequestContext", "");
            Check((bool)Call(f.chat, "ApplyWorkReviewDecision", Decision("continue")), "No-novelty pending work was denied");
            Check((bool)Call(f.chat, "ApplyWorkReviewDecision", Decision("continue", false, "")), "Pending status with omitted wording was silently dropped");
            Check((string)Get(f.chat, "m_WorkStatus") == "continue", "Pending status not retained");
            Check((bool)Call(f.chat, "ApplyWorkReviewDecision", Decision("waiting_tool")), "Phantom operation silently waited forever");
            Set(f.chat, "m_SongCatalogInFlight", true);
            object busy = Decision("continue", true);
            Check(!(bool)Call(f.chat, "ApplyWorkReviewDecision", busy) && !(bool)busy.GetType().GetField("proceed").GetValue(busy), "Real in-flight operation duplicated");
            Set(f.chat, "m_SongCatalogInFlight", false);
            Set(f.chat, "m_WorkContinuations", 3);
            Check(!(bool)Call(f.chat, "ApplyWorkReviewDecision", Decision("continue")), "Continuation budget reset by model");
            Check((string)Get(f.chat, "m_WorkStatus") == "blocked" && !(bool)Get(f.chat, "m_WorkReviewOpen"), "Exhaustion masqueraded as success");
            Call(f.chat, "SetCurrentUserInput", "不用查了。", "public cancellation");
            Check((int)Get(f.chat, "m_WorkContinuations") == 0 && ((Queue<string>)Get(f.chat, "m_WorkResponses")).Count == 0, "New input retained stale promise/budget");
            Check(!(bool)Call(f.chat, "ApplyWorkReviewDecision", Decision("closed")) && !(bool)Get(f.chat, "m_WorkReviewOpen"), "Closed request reopened");
        }
        using (var f = new Fixture())
        {
            Check(!(bool)Call(f.chat, "ApplyWorkReviewDecision", Decision("waiting_user")) && !(bool)Get(f.chat, "m_WorkReviewOpen"), "User clarification waiting forced speech");
            Call(f.chat, "SetCurrentUserInput", "谢谢，晚安。", "public");
            Check(!(bool)Call(f.chat, "ApplyWorkReviewDecision", Decision("none")), "Ordinary silence became mandatory action");
            Check((bool)Call(f.chat, "ShouldBypassAutonomyIntentProbe", "self-inspection-result"), "Inspection result subjected to idle novelty");
            Check((string)Call(f.chat, "ResolveScheduledWakeReason", "self-inspection-result", "clock") == "self-inspection-result", "Timer lost result identity");
        }
    }

    private static void Inspection()
    {
        const string tag = "<body_inspect scope=\"motion\"/>";
        foreach (string input in new[] { "<thought>" + tag + "</thought>", "`" + tag + "`", "<say>" + tag + "</say>", "<body_inspect scope=\"motion\"", "＜body_inspect scope=\"motion\"/＞" })
        {
            using (var f = new Fixture())
            {
                string executable = RoleOutputChannels.Parse(input).ToExecutableText();
                object[] args = { executable, true }; Call(f.chat, "ExtractSelfInspection", args);
                Check(string.IsNullOrEmpty((string)Get(f.chat, "m_PendingSelfInspection")), "Private/quoted/incomplete inspection executed");
            }
        }
        for (int split = 0; split <= tag.Length; split++)
        {
            var channel = new RoleOutputChannels(); channel.Push(tag.Substring(0, split)); channel.Push(tag.Substring(split)); channel.Finish();
            Check(!channel.HasSpeech && channel.ToExecutableText().Contains(tag), "Stream boundary leaked or lost tool");
        }
        using (var f = new Fixture())
        {
            int reads = 0;
            f.chat.BodyInspectionContextRequested += () => { reads++; return "{\"source\":\"fixture\",\"bound\":false}"; };
            object[] args = { "確認します。" + tag, true }; Call(f.chat, "ExtractSelfInspection", args);
            Check((string)args[0] == "確認します。" && reads == 1, "Inspection not executed exactly once or prose changed");
            string result = (string)Get(f.chat, "m_PendingSelfInspection");
            Check(result.Contains("local-runtime-inspection") && result.Contains("readOnly"), "Inspection evidence missing provenance");
            Check(!(bool)Call(f.chat, "ApplyWorkReviewDecision", Decision("closed")) && (bool)Get(f.chat, "m_WorkReviewOpen"), "Unread result silently closed");
            args = new object[] { tag, true }; Call(f.chat, "ExtractSelfInspection", args);
            Check(reads == 1 && result == (string)Get(f.chat, "m_PendingSelfInspection"), "Repeated inspection created new work");
            Set(f.chat, "m_PendingSelfInspection", "");
            args = new object[] { tag, true }; Call(f.chat, "ExtractSelfInspection", args);
            Check(reads == 1 && (string)Get(f.chat, "m_PendingSelfInspection") == "", "Delivered result was requeued");
            args = new object[] { "<body_inspect scope=\"motion\" path=\"../secrets\"/>", true }; Call(f.chat, "ExtractSelfInspection", args);
            Check(reads == 1, "Arbitrary attribute bypassed schema");
            Call(f.chat, "SetCurrentUserInput", "新问题", "public");
            args = new object[] { tag, false }; Call(f.chat, "ExtractSelfInspection", args);
            Check(reads == 1, "User-speech guard failed");
            f.chat.BodyInspectionContextRequested += () => throw new Exception("private provider detail");
            args = new object[] { tag, true }; Call(f.chat, "ExtractSelfInspection", args);
            Check(!((string)Get(f.chat, "m_LastSelfInspection")).Contains("private provider detail"), "Provider error leaked raw internals");
            var controller = f.host.AddComponent<ArdyLiveMotionController>();
            var actual = JObject.Parse(controller.InspectCapabilities());
            Check(actual["basicMotions"] is JArray motions && motions.Count == 4, "Real resource registry wrong");
            foreach (JObject entry in actual["basicMotions"])
                Check((bool)entry["resourcePresent"] && (bool)entry["resourceValid"] && !(bool)entry["available"], "Resource validity confused with bound availability");
            Check((string)actual["generationServiceHealth"] == "not-checked", "Inspection invented remote health");
            report["actualUnboundResourceInspection"] = actual;
        }
    }

    private static void Feedback()
    {
        using (var f = new Fixture())
        {
            int generation = (int)Call(f.chat, "BeginFormalResponseGeneration");
            Call(f.chat, "DispatchWorkFeedback", "fresh inspection result", null, generation);
            Check(f.model.feedbackRequests == 1 && f.model.regularRequests == 0 &&
                f.model.feedback.Contains("fresh inspection result") && f.model.feedback.Contains("registered-runtime-components"),
                "Result inserted as fake user or lost latest runtime observations in after-assistant feedback");
            Call(f.chat, "BeginFormalResponseGeneration");
            f.model.Complete("<body_inspect scope=\"runtime\"/>");
            Check((string)Get(f.chat, "m_PendingSelfInspection") == "", "Stale model callback executed tool");
            Check(f.model.m_DataList.Count == 0, "Transient inspection mutated user history");
        }
    }

    private static object Call(object target, string method, params object[] args) => typeof(ChatSample).GetMethod(method, Flags).Invoke(target, args);
    private static object Get(object target, string field) => typeof(ChatSample).GetField(field, Flags).GetValue(target);
    private static void Set(object target, string field, object value) => typeof(ChatSample).GetField(field, Flags).SetValue(target, value);
    private static void Check(bool success, string message) { checks++; if (!success) throw new InvalidOperationException(message); }
}

public sealed class AgentInspectionFakeLLM : LLM
{
    public int feedbackRequests, regularRequests;
    public string feedback;
    private Action<string> complete;
    public override void PostSpeechFeedbackStream(string context, string facts, Action<SpeechText> delta, Action<string> done, string imageDataUrl = null)
    { feedbackRequests++; feedback = facts; complete = done; }
    public override void PostSpeechStream(string prompt, Action<SpeechText> delta, Action<string> done, string imageDataUrl = null, bool recordAssistantHistory = true)
    { regularRequests++; throw new InvalidOperationException("Inspection must not create a fake user message."); }
    public void Complete(string text) => complete?.Invoke(text);
}
