using System;
using System.Collections;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace UnityEngine
{
    public static class Mathf { public static int Max(int a, int b) => Math.Max(a, b); }
    public static class Debug { public static void Log(object value) { } }
}

namespace UnityEngine.Networking
{
    public class DownloadHandlerBuffer { public string text; }
    public class UploadHandlerRaw { public UploadHandlerRaw(byte[] data) { } }
    public class UnityWebRequest : IDisposable
    {
        public enum Result { Success, ConnectionError }
        public static string NextBody;
        public static long NextStatus = 200;
        public static Result NextResult = Result.Success;
        public static int Sent;
        private readonly string body;
        public UploadHandlerRaw uploadHandler;
        public DownloadHandlerBuffer downloadHandler;
        public int timeout;
        public long responseCode;
        public Result result;
        public bool isDone;
        public UnityWebRequest(string url, string method)
        { body = NextBody; responseCode = NextStatus; result = NextResult; }
        public void SetRequestHeader(string name, string value) { }
        public object SendWebRequest() { Sent++; downloadHandler.text = body; return null; }
        public void Abort() { isDone = true; }
        public void Dispose() { isDone = true; }
    }
}

public class ConversationTrace
{
    public Action<string> OnMark;
    public ConversationTrace Child(string name) => this;
    public void Mark(string name, JObject value = null) => OnMark?.Invoke(name);
}

public partial class ChatQW
{
    public enum BackendType { Local, OpenAICompatible }
    private bool m_EnableRoleDecisionFastLane = true;
    private bool m_LogRequestStats;
    private BackendType m_Backend = BackendType.Local;
    private string api_key = "test-secret", url = "http://local.test";
    private const int k_SlotAuxiliary = 1;
    private string CurrentModelName => "test-qwen";
    private ConversationTrace TraceContext;
    public IEnumerator Pending;
    public Action Diagnostic;
    public void Disable() { m_EnableRoleDecisionFastLane = false; }
    private void AbortPrewarmIfRunning() { }
    private void StartCoroutine(IEnumerator routine) { Pending = routine; routine.MoveNext(); }
    private void RaiseRequestDiagnostic(string wire) => Diagnostic?.Invoke();
    private static void AppendJsonString(StringBuilder sb, string value) =>
        sb.Append(new JValue(value).ToString(Formatting.None));
    private static void AppendMessage(StringBuilder sb, SendData message) =>
        sb.Append(new JObject { ["role"] = message.role, ["content"] = message.content }.ToString(Formatting.None));
    private void AppendReasoningControls(StringBuilder sb, bool enabled) =>
        sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
    private void AppendSlot(StringBuilder sb, int slot) => sb.Append(",\"id_slot\":").Append(slot);
    private class SendData
    {
        public string role, content;
        public SendData(string role, string content) { this.role = role; this.content = content; }
    }
    private class CompletionPerformance
    {
        public void Observe(string body) { }
        public JObject Snapshot() => new JObject();
    }
}

public static class ActorIntentTransportRegression
{
    private static int checks;
    private static readonly MethodInfo Read = typeof(ChatQW).GetMethod("TryReadActorIntentEnvelope",
        BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly MethodInfo Build = typeof(ChatQW).GetMethod("BuildActorIntentRequestJson",
        BindingFlags.NonPublic | BindingFlags.Instance);

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        checks++;
    }

    private static string Intent(string kind = "act", string action = "head.raise",
        string condition = "none", string evidence = "抬头", string domain = "head", string task = "none", string target = "none") => new JObject {
            ["version"] = ActorIntentProtocol.IntentVersion, ["domain"] = domain, ["kind"] = kind, ["action"] = action,
            ["task"] = task, ["target"] = target, ["condition"] = condition, ["evidence"] = evidence, ["motionScope"] = domain == "head" ? "head_preset" : "none",
            ["degrees"] = null, ["deviceState"] = "none", ["targetEntityId"] = "",
            ["goal"] = kind == "none" ? "" : evidence, ["coverage"] = kind == "none" ? "none" : "atomic"
        }.ToString(Formatting.None);

    private static string Signal(string value = "one", string evidence = "一") => new JObject {
        ["version"] = 1, ["signal"] = value, ["evidence"] = evidence
    }.ToString(Formatting.None);

    private static JObject Envelope(string raw) => new JObject {
        ["choices"] = new JArray(new JObject {
            ["finish_reason"] = "stop", ["message"] = new JObject {
                ["role"] = "assistant", ["content"] = raw
            }
        })
    };

    private static bool AcceptEnvelope(string json)
    {
        object[] args = { json, null, null, null };
        return (bool)Read.Invoke(null, args);
    }

    private static void Serve(string raw)
    {
        UnityWebRequest.NextBody = Envelope(raw).ToString(Formatting.None);
        UnityWebRequest.NextStatus = 200;
        UnityWebRequest.NextResult = UnityWebRequest.Result.Success;
    }

    private static void EnvelopeChecks()
    {
        var valid = Envelope(Intent()).ToString(Formatting.None);
        Check(AcceptEnvelope(valid), "complete single assistant envelope accepted");
        foreach (string finish in new[] { "length", "tool_calls", "content_filter", "arbitrary\nsecret" })
        {
            var value = Envelope(Intent()); value["choices"][0]["finish_reason"] = finish;
            Check(!AcceptEnvelope(value.ToString()), "non-stop finish rejected");
        }
        var missing = Envelope(Intent()); ((JObject)missing["choices"][0]).Remove("finish_reason");
        Check(!AcceptEnvelope(missing.ToString()), "missing finish rejected");
        Check(!AcceptEnvelope(valid.Replace("\"finish_reason\":\"stop\"",
            "\"finish_reason\":\"length\",\"finish_reason\":\"stop\"")), "duplicate envelope field rejected");
        Check(!AcceptEnvelope("{}"), "missing choices rejected");
        Check(!AcceptEnvelope("[]"), "array root rejected");
        Check(!AcceptEnvelope(" "), "empty envelope rejected");
        Check(!AcceptEnvelope(new string(' ', 65537)), "oversized envelope rejected");
        var content = Envelope(Intent()); content["choices"][0]["message"]["content"] = new JArray();
        Check(!AcceptEnvelope(content.ToString()), "non-string content rejected");
        var multiple = Envelope(Intent()); ((JArray)multiple["choices"]).Add(multiple["choices"][0].DeepClone());
        Check(!AcceptEnvelope(multiple.ToString()), "multiple alternatives rejected");
        var role = Envelope(Intent()); role["choices"][0]["message"]["role"] = "user";
        Check(!AcceptEnvelope(role.ToString()), "unexpected role rejected");
        foreach (string channel in new[] { "tool_calls", "function_call", "refusal" })
        {
            var value = Envelope(Intent()); value["choices"][0]["message"][channel] = new JObject();
            Check(!AcceptEnvelope(value.ToString()), "unexpected response channel rejected");
        }
        var emptyTools = Envelope(Intent()); emptyTools["choices"][0]["message"]["tool_calls"] = new JArray();
        Check(AcceptEnvelope(emptyTools.ToString()), "empty tool array harmless");
    }

    private static void DecisionChecks()
    {
        var backend = new ChatQW(); int calls = 0;
        Serve(Intent());
        backend.PostActorIntent("facts", "我先抬头。", "好", (decision, status) => {
            Check(decision != null && decision.Domain == "head" && decision.Kind == "act" && decision.Action == "head.raise" && status == "ok",
                "intent evidence binds draft rather than user"); calls++;
        });
        backend.Pending.MoveNext(); Check(calls == 1, "intent callback once");

        int beforeVerification = UnityWebRequest.Sent;
        Serve(Intent("claim", "none", "none", "已经唱完刚才那段旋律了", "other_action"));
        backend.PostActorIntent("no execution receipt", "我已经唱完刚才那段旋律了。", "好了吗？", (decision, status) => {
            Check(decision != null && decision.Domain == "other_action" && decision.Kind == "claim" &&
                decision.Action == "none" && decision.Condition == "none" && status == "ok",
                "unsupported completed action reaches caller as verification only, never plan");
        });
        backend.Pending.MoveNext();
        Check(UnityWebRequest.Sent == beforeVerification + 1, "transport does not reinterpret claim as a second planning request");

        Serve(Intent("replan", "none", "none", "あなたについていくわ。", "other_action", "follow", "current_user"));
        backend.PostActorIntent("current user known", "あなたについていくわ。", "ついてきて", (decision, status) => {
            Check(decision != null && decision.Task == "follow" && decision.Target == "current_user" && status == "ok",
                "production transport preserves bounded follow proposal and target identity");
        });
        backend.Pending.MoveNext();

        foreach (string invalid in new[] {
            Intent(evidence: "用户原话"),
            Intent().Replace("\"version\":5", "\"version\":1"),
            Intent().Replace("\"version\":5", "\"version\":2"),
            Intent().Replace("\"task\":\"none\",", ""),
            Intent().Replace("\"target\":\"none\",", ""),
            Intent().Replace("\"domain\":\"head\",", ""),
            Intent(kind: "claim", action: "none", evidence: "", domain: "other_action"),
            Intent().Replace("\"kind\":\"act\"", "\"kind\":\"none\",\"kind\":\"act\""),
            "<act>head.raise</act>"
        })
        {
            Serve(invalid); int failures = 0;
            backend.PostActorIntent("facts", "我先抬头。", "用户原话", (decision, status) => {
                Check(decision == null && status != "ok" || decision?.RequiresFullReview == true &&
                    !ActorIntentProtocol.Validate(decision, "我先抬头。", out _), "invalid intent never delivered as executable"); failures++;
            });
            backend.Pending.MoveNext(); Check(failures == 1, "invalid intent completes with failure");
        }

        foreach (string conflict in new[] { Intent(kind: "claim", domain: "perception"),
            Intent(kind: "claim", domain: "other_action"),
            Intent(kind: "claim", action: "none", condition: "user_count_three", domain: "other_action") })
        {
            Serve(conflict);
            backend.PostActorIntent("facts", "我先抬头。", "用户原话", (decision, status) => {
                Check(decision != null && decision.RequiresFullReview &&
                    !ActorIntentProtocol.Validate(decision, "我先抬头。", out _),
                    "Closed but conflicting fields remain non-executable review markers, never a concrete action");
            });
            backend.Pending.MoveNext();
        }

        int beforePerception = UnityWebRequest.Sent;
        Serve(Intent("none", "none", "none", "", "perception"));
        backend.PostActorIntent("no physical task", "音楽に聴き入っていたの。", "歌って", (decision, status) => {
            Check(decision != null && decision.Domain == "perception" && decision.Kind == "none" && status == "ok",
                "ordinary listening reaches caller as non-executing perception");
        });
        backend.Pending.MoveNext();
        Check(UnityWebRequest.Sent == beforePerception + 1, "perception classification adds no second model request");

        foreach (string reviewStatus in new[] { "non_action", "adopted_action", "reported_action", "uncertain" })
        {
            string reviewDraft = "消すね。";
            bool action = reviewStatus == "adopted_action" || reviewStatus == "reported_action";
            Serve(new JObject { ["version"] = 2, ["status"] = reviewStatus,
                ["goal"] = action ? "灯を消す" : "", ["coverage"] = action ? "atomic" : "none",
                ["task"] = reviewStatus == "adopted_action" ? "other" : "none", ["target"] = "none",
                ["trigger"] = "none", ["evidence"] = reviewDraft }.ToString());
            int reviewed = 0;
            backend.PostActorIntentReview("facts", reviewDraft, "消して", (decision, status) => {
                Check(status == "ok" && decision?.Status == reviewStatus, "production review transport returns typed semantic status");
                reviewed++;
            });
            backend.Pending.MoveNext();
            Check(reviewed == 1, "semantic review completes once");
        }
        Serve(new JObject { ["version"] = 2, ["status"] = "adopted_action", ["goal"] = "关灯", ["coverage"] = "atomic", ["task"] = "other", ["target"] = "other", ["trigger"] = "none", ["evidence"] = "用户的话" }.ToString());
        backend.PostActorIntentReview("facts", "我来关灯。", "用户的话", (decision, status) =>
            Check(decision == null && status != "ok", "review never borrows user evidence"));
        backend.Pending.MoveNext();
        Serve(new JObject { ["version"] = 2, ["status"] = "non_action", ["goal"] = "", ["coverage"] = "none", ["task"] = "none", ["target"] = "none", ["trigger"] = "none", ["evidence"] = "听到了" }.ToString());
        int cancelledReviews = 0;
        backend.PostActorIntentReview("facts", "听到了", "你好", (_, __) => cancelledReviews++);
        var staleReview = backend.Pending;
        backend.CancelActorIntent(); staleReview.MoveNext();
        Check(cancelledReviews == 0, "cancelled semantic review cannot deliver a verdict");

        Serve(Signal());
        backend.PostActorConditionSignal("waiting", "一", (signal, status) => {
            Check(signal == "one" && status == "ok", "condition signal delivered"); calls++;
        });
        backend.Pending.MoveNext(); Check(calls == 2, "condition callback once");
        Serve(Signal("three", "三"));
        backend.PostActorConditionSignal("earlier input: 三", "一", (signal, status) => {
            Check(signal == null && status != "ok", "signal evidence only from current user"); calls++;
        });
        backend.Pending.MoveNext(); Check(calls == 3, "invalid signal completes with failure");

        Serve(Intent()); UnityWebRequest.NextStatus = 502;
        backend.PostActorIntent("", "抬头", "", (decision, status) =>
            Check(decision == null && status == "http_failure", "http failure fails closed"));
        backend.Pending.MoveNext();
        Serve(Signal()); UnityWebRequest.NextResult = UnityWebRequest.Result.ConnectionError;
        backend.PostActorConditionSignal("", "一", (signal, status) =>
            Check(signal == null && status == "http_failure", "transport failure fails closed"));
        backend.Pending.MoveNext();
    }

    private static void OwnershipChecks()
    {
        var backend = new ChatQW(); int calls = 0;
        Serve(Intent()); backend.PostActorIntent("", "抬头", "", (d, r) => calls++);
        var oldIntent = backend.Pending;
        Serve(Signal()); backend.PostActorConditionSignal("", "一", (s, r) => calls++);
        var nextSignal = backend.Pending;
        oldIntent.MoveNext(); Check(calls == 0, "new signal suppresses old intent");
        nextSignal.MoveNext(); Check(calls == 1, "new signal retains ownership");

        Serve(Signal()); backend.PostActorConditionSignal("", "一", (s, r) => calls++);
        var oldSignal = backend.Pending;
        Serve(Intent()); backend.PostActorIntent("", "抬头", "", (d, r) => calls++);
        var nextIntent = backend.Pending;
        oldSignal.MoveNext(); Check(calls == 1, "new intent suppresses old signal");
        backend.CancelActorIntent(); nextIntent.MoveNext();
        Check(calls == 1, "old completion cannot erase cancellable newer request");

        int sent = UnityWebRequest.Sent;
        backend.Diagnostic = backend.CancelActorIntent;
        backend.PostActorIntent("", "抬头", "", (d, r) => calls++);
        Check(UnityWebRequest.Sent == sent && calls == 1, "diagnostic cancellation prevents send");
        backend.Diagnostic = null;
        var trace = new ConversationTrace { OnMark = name => {
            if (name == "actor_intent.accepted") backend.CancelActorIntent();
        }};
        Serve(Intent()); backend.PostActorIntent("", "抬头", "", (d, r) => calls++, trace);
        backend.Pending.MoveNext(); Check(calls == 1, "accepted diagnostic cannot publish revoked result");

        backend.Disable(); sent = UnityWebRequest.Sent;
        backend.PostActorIntent("", "抬头", "", (d, r) =>
            Check(d == null && r == "actor_intent_disabled", "disabled intent resolves immediately"));
        backend.PostActorConditionSignal("", "一", (s, r) =>
            Check(s == null && r == "actor_intent_disabled", "disabled signal resolves immediately"));
        Check(UnityWebRequest.Sent == sent, "disabled backend sends nothing");
    }

    private static void WireChecks()
    {
        var backend = new ChatQW();
        foreach (bool signal in new[] { false, true })
        {
            var wire = JObject.Parse((string)Build.Invoke(backend, new object[] {
                "current facts", "我先\"抬头\"。", "一", signal
            }));
            Check(!(bool)wire["stream"] && !(bool)wire["enable_thinking"] && (int)wire["temperature"] == 0 &&
                (int)wire["max_tokens"] == 384 && (int)wire["id_slot"] == 1, "bounded auxiliary request");
            Check((string)wire["messages"][0]["content"] ==
                (signal ? ActorIntentProtocol.SignalContract : ActorIntentProtocol.SystemPrompt), "exact production judge contract");
            Check(JToken.DeepEquals(JObject.Parse((string)wire["messages"][1]["content"]),
                ActorIntentProtocol.BuildUser("current facts", signal ? "" : "我先\"抬头\"。", "一")),
                "explicit user data and draft preserved");
            Check(JToken.DeepEquals(wire["response_format"], signal ? ActorIntentProtocol.SignalResponseFormat() :
                ActorIntentProtocol.ResponseFormat()), "matching production response schema");
            Check(!wire.ToString().Contains("test-secret"), "wire diagnostic excludes credentials");
        }
    }

    public static void Main()
    {
        EnvelopeChecks(); DecisionChecks(); OwnershipChecks(); WireChecks();
        Console.WriteLine("ActorIntent backend production source: " + checks + " checks passed.");
    }
}
