using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Exercises the production SSE byte handler and final request serializer. All
// inputs are synthetic; no network, microphone, TTS, scene or model calls.
public static class ChatLatencyTransportRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;

    [MenuItem("Tools/NeEEvA/Validate chat latency transport")]
    public static void RunInteractive()
    {
        checks = 0;
        CheckStreamingPerformance();
        CheckStableDialoguePrefix();
        CheckStagedObservations();
        Debug.Log("[ChatLatencyTransportRegression] passed checks=" + checks);
    }

    public static void RunBatch()
    {
        var report = new JObject {
            ["scope"] = "Production SSE byte parsing, optional server metrics, current observation ownership and serialized dialogue stability; synthetic inputs, no network/model/TTS/private scene."
        };
        try { RunInteractive(); report["passed"] = true; }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); Debug.LogException(error); }
        report["checks"] = checks;
        report["checkedAtUtc"] = DateTime.UtcNow.ToString("o");
        string path = Path.GetFullPath("Tools/MotionAdapter/reports/chat-latency-transport-regression.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, report.ToString() + "\n");
        EditorApplication.Exit((bool)report["passed"] ? 0 : 1);
    }

    private static void Check(bool condition, string message)
    {
        ++checks;
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class StreamFixture : IDisposable
    {
        private readonly object handler;
        private readonly Type type;
        public string Received = "";
        public StreamFixture()
        {
            type = typeof(ChatQW).GetNestedType("SSEDownloadHandler", Flags);
            handler = Activator.CreateInstance(type, new object[] { new Action<string>(text => Received += text) });
        }
        public void Bytes(byte[] bytes) => type.GetMethod("ReceiveData", Flags).Invoke(handler, new object[] { bytes, bytes.Length });
        public void Event(string json) => Bytes(Encoding.UTF8.GetBytes("data: " + json + "\n\n"));
        public JObject Metrics()
        {
            object metrics = type.GetField("Performance", Flags).GetValue(handler);
            return (JObject)metrics.GetType().GetMethod("Snapshot", Flags).Invoke(metrics, null);
        }
        public void Dispose() => ((IDisposable)handler).Dispose();
    }

    private static void CheckStreamingPerformance()
    {
        const string text = "<lang code=\"ja\"/>青い空、こんにちは。";
        string content = new JObject { ["choices"] = new JArray(new JObject {
            ["delta"] = new JObject { ["content"] = text }
        }) }.ToString(Newtonsoft.Json.Formatting.None);
        byte[] wire = Encoding.UTF8.GetBytes("data: " + content + "\n\n");
        for (int split = 1; split < wire.Length; ++split)
        {
            using (var stream = new StreamFixture())
            {
                var first = new byte[split]; var last = new byte[wire.Length - split];
                Array.Copy(wire, 0, first, 0, first.Length); Array.Copy(wire, split, last, 0, last.Length);
                stream.Bytes(first); stream.Bytes(last);
                Check(stream.Received == text, "UTF-8/SSE boundary corrupted delivered role content");
                stream.Event("{\"choices\":[],\"usage\":{\"prompt_tokens\":20651,\"prompt_tokens_details\":{\"cached_tokens\":11671}}," +
                    "\"timings\":{\"cache_n\":11170,\"prompt_n\":9481,\"prompt_ms\":1644.25,\"predicted_ms\":188.5}}");
                JObject metrics = stream.Metrics();
                Check((long)metrics["promptTokensTotal"] == 20651 && (long)metrics["cachedTokensReported"] == 11671,
                    "Usage total/cache fields were conflated");
                Check((long)metrics["promptEvaluatedTokens"] == 9481 && (long)metrics["cacheTokensAtTiming"] == 11170,
                    "Actual evaluated prompt count was inferred from prefix-match or total usage");
                Check((double)metrics["prefillMilliseconds"] == 1644.25 && stream.Received == text,
                    "Metadata-only event was lost or leaked into speech");
            }
        }
        using (var stream = new StreamFixture())
        {
            stream.Event("{\"choices\":[],\"usage\":{\"prompt_tokens\":42}}");
            JObject metrics = stream.Metrics();
            Check((long)metrics["promptTokensTotal"] == 42 && metrics["promptEvaluatedTokens"].Type == JTokenType.Null &&
                metrics["cachedTokensReported"].Type == JTokenType.Null && metrics["prefillMilliseconds"].Type == JTokenType.Null,
                "A provider without timing/cache metadata was treated as zero or fully recomputed");
            stream.Event("{\"choices\":[],\"timings\":{\"prompt_n\":0,\"prompt_ms\":0,\"cache_n\":0}}");
            stream.Event("{\"choices\":[],\"usage\":{\"prompt_tokens\":50,\"prompt_tokens_details\":{\"cached_tokens\":0}}}");
            metrics = stream.Metrics();
            Check((long)metrics["promptEvaluatedTokens"] == 0 && (double)metrics["prefillMilliseconds"] == 0 &&
                (long)metrics["cachedTokensReported"] == 0, "Explicit zero metrics were lost or overwritten by a later usage event");
        }
        using (var stream = new StreamFixture())
        {
            stream.Event("{\"choices\":[],\"usage\":{\"prompt_tokens\":-1},\"timings\":{\"prompt_n\":1.5,\"prompt_ms\":-1}}");
            JObject metrics = stream.Metrics();
            Check(metrics["promptTokensTotal"].Type == JTokenType.Null && metrics["promptEvaluatedTokens"].Type == JTokenType.Null &&
                metrics["prefillMilliseconds"].Type == JTokenType.Null, "Malformed metrics became plausible performance evidence");
        }
    }

    private static JObject Request(ChatQW model, string context) => JObject.Parse((string)typeof(ChatQW)
        .GetMethod("BuildRequestJsonForMessages", Flags).Invoke(model, new object[] { model.m_DataList, true, context }));

    private static void CheckStableDialoguePrefix()
    {
        var host = new GameObject("InactiveChatLatencyWireFixture"); host.SetActive(false);
        try
        {
            var model = host.AddComponent<ChatQW>();
            const string mixed = "先说一句。\n[分段观测]0–2 speech；2–5 singing\n青い空。静かな夜。\n只听就好，不用回唱。";
            model.m_DataList.Add(new LLM.SendData("system", "Stable persona"));
            model.m_DataList.Add(new LLM.SendData("user", mixed));
            model.ActiveSkillContext = "Stable selected contract";
            JObject first = Request(model, "[感知帧 10:00:00] current facts");
            model.m_DataList.Add(new LLM.SendData("assistant", "听到了。"));
            model.m_DataList.Add(new LLM.SendData("user", "最近天气很好。"));
            JObject second = Request(model, "[感知帧 10:00:05] fresh facts");
            var messages = (JArray)second["messages"];
            Check((string)((JArray)first["messages"])[0]["content"] == (string)messages[0]["content"], "Stable system content changed");
            Check((string)messages[1]["content"] == mixed && model.m_DataList[1].content == mixed,
                "Previous mixed transcript, lyrics or tail request were rewritten for caching");
            string serialized = second.ToString();
            Check(serialized.IndexOf("[感知帧", StringComparison.Ordinal) == serialized.LastIndexOf("[感知帧", StringComparison.Ordinal),
                "A previous request-only observation was retained in subsequent dialogue");
            Check(!serialized.Contains("10:00:00") && serialized.Contains("10:00:05"), "Latest observation reused stale transient state");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void CheckStagedObservations()
    {
        var host = new GameObject("InactiveObservationOwnershipFixture"); host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            void Set(string name, object value) => typeof(ChatSample).GetField(name, Flags).SetValue(chat, value);
            object Call(string name, params object[] args) => typeof(ChatSample).GetMethod(name, Flags).Invoke(chat, args);
            Set("m_AgentRunning", true); Set("m_WorkEpoch", 7); Set("m_FormalResponseGeneration", 21);
            Set("m_EnableMemoryRecall", false);
            Call("StageFormalObservationFrame", "unique-frame", 21);
            string delivered = (string)Call("BuildFormalObservationContext", new object[] { null });
            Check(delivered.Contains("unique-frame"), "Current generation did not receive its staged frame");
            Check(!((string)Call("BuildFormalObservationContext", new object[] { null })).Contains("unique-frame"),
                "Staged frame was consumed twice instead of refreshing the next observation");
            Call("StageFormalObservationFrame", "stale-epoch", 21); Set("m_WorkEpoch", 8);
            Check(!((string)Call("BuildFormalObservationContext", new object[] { null })).Contains("stale-epoch"), "Superseded user epoch leaked stale observations");
            Call("StageFormalObservationFrame", "stale-generation", 21); Set("m_FormalResponseGeneration", 22);
            Check(!((string)Call("BuildFormalObservationContext", new object[] { null })).Contains("stale-generation"), "Cancelled generation leaked staged observations");
            Set("m_PendingSelfInspection", "actual inspection remains pending");
            Set("m_StickyToolFailure", "actual tool failure remains visible");
            string pending = (string)Call("BuildFormalObservationContext", new object[] { null });
            Check(pending.Contains("actual inspection remains pending") && pending.Contains("actual tool failure remains visible"),
                "Compact formal observations removed unfinished work or real tool failures");
            Check((string)typeof(ChatSample).GetField("m_PendingSelfInspection", Flags).GetValue(chat) == "actual inspection remains pending",
                "Building compact observations incorrectly acknowledged pending work");
            Set("m_PendingToolCorrectionFrame", "older correction");
            Call("StageFormalObservationFrame", "older correction", 22);
            Set("m_PendingToolCorrectionFrame", "new correction after staging");
            Call("BuildFormalObservationContext", new object[] { null });
            object receipt = Call("CaptureObservationReceipt");
            Call("AcknowledgeObservations", receipt, 22, "completed synthetic response");
            Check((string)typeof(ChatSample).GetField("m_PendingToolCorrectionFrame", Flags).GetValue(chat) == "new correction after staging",
                "A staged frame acknowledged a later correction which was not delivered in that snapshot");
            Call("BuildFormalObservationContext", new object[] { null });
            receipt = Call("CaptureObservationReceipt");
            Call("AcknowledgeObservations", receipt, 22, "completed synthetic response");
            Check((string)typeof(ChatSample).GetField("m_PendingToolCorrectionFrame", Flags).GetValue(chat) == "",
                "A refreshed formal observation could not acknowledge the actual newly delivered correction");
            Set("m_WorkReviewOpen", true); Set("m_WorkIntent", "必要的公开后续步骤");
            string continuation = (string)Call("BuildWorkContinuationContext");
            Check(continuation.Contains("必要的公开后续步骤") && !continuation.Contains("判定顺序") &&
                !continuation.Contains("[当前事项状态"), "Formal continuation duplicated a review rubric or independently appended another work snapshot");
            string composed = (string)Call("BuildFormalObservationContext", continuation);
            Check(composed.IndexOf("[当前事项状态", StringComparison.Ordinal) >= 0 &&
                composed.IndexOf("[当前事项状态", StringComparison.Ordinal) == composed.LastIndexOf("[当前事项状态", StringComparison.Ordinal),
                "Formal continuation did not receive exactly one compact work snapshot");
            string reviewer = (string)Call("BuildWorkReviewContext");
            Check(reviewer.Contains("判定顺序") && reviewer.Contains("waiting_tool"), "Compacting formal context removed the independent reviewer's judging rubric");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }
}
