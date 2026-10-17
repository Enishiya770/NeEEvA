using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class ActivityOriginRegression
{
    const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static readonly JArray checks = new JArray(), traces = new JArray();
    static JObject fixture; static string output;
    static object Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, F).Invoke(o, args);
    static object Get(object o, string name) => o.GetType().GetField(name, F).GetValue(o);
    static void Set(object o, string name, object value) => o.GetType().GetField(name, F).SetValue(o, value);
    static JObject At(int line) => (JObject)((JArray)fixture["events"]).Single(e => (int)e["line"] == line)["data"].DeepClone();
    static void Save(bool done = false) => File.WriteAllText(Path.Combine(output, "activity-origin.json"), new JObject {
        ["completed"] = done, ["passed"] = done && checks.All(c => (bool)c["passed"]),
        ["source"] = fixture["source"], ["history"] = fixture["history"], ["limitations"] = fixture["limitations"],
        ["checks"] = checks, ["lifecycle"] = traces }.ToString());
    static void Check(string name, bool ok, JToken evidence = null) { checks.Add(new JObject { ["name"] = name, ["passed"] = ok, ["evidence"] = evidence?.DeepClone() }); Save(); }
    static IEnumerator Drain(ChatQW model, IEnumerator request) { bool done = false; model.StartCoroutine(FinishRequest(request, () => done = true)); yield return Until(() => done); }
    static IEnumerator FinishRequest(IEnumerator request, Action finished) { yield return request; finished(); }
    static IEnumerator Until(Func<bool> done) { float end = Time.realtimeSinceStartup + 40; while (!done() && Time.realtimeSinceStartup < end) yield return null; if (!done()) throw new TimeoutException("Origin regression timeout"); }
    static void Log(string message, string stack, LogType type) {
        const string prefix = "[Agent/WorkReviewLifecycle] ";
        if (message.StartsWith(prefix, StringComparison.Ordinal)) traces.Add(JObject.Parse(message.Substring(prefix.Length)));
    }

    public static IEnumerator Run(ChatSample chat, ArdyRoomDialogueBridge bridge, string directory)
    {
        output = directory; checks.Clear(); traces.Clear();
        fixture = ActivityEvidencePolicy.Parse(File.ReadAllText("Assets/Editor/Fixtures/ActivityOrigin.20261017-1356.json"));
        var host = new GameObject("Activity provenance and review transport replay"); host.SetActive(false);
        var model = host.AddComponent<ChatQW>(); model.enabled = false; model.m_PrewarmPrefixOnStart = false;
        model.m_Backend = ChatQW.BackendType.Local; host.SetActive(true);
        Set(chat, "m_AgentRunning", false); Set(chat, "m_EnableActivityMonitor", false);
        Application.logMessageReceived += Log;
        try {
            var history = (JArray)fixture["history"];
            Check("full-five-inputs-twelve-chunks-four-openings", history.Count(h => (string)h["role"] == "user") == 5 && history.Count(h => (string)h["role"] == "assistant") == 12 && history.Count(h => (string)h["role"] == "assistant-opening") == 4);
            var frame = Origins(chat, bridge, model);
            Budgets(model, frame);
            Set(model, "url", "http://127.0.0.1:8080/v1/chat/completions");
            yield return NativeMonitor(model, frame, "original-old-follow-handoff");
            foreach (var variant in new[] { new[] { "observe", "继续观察这个区域。", "保存另一份笔记。" }, new[] { "sing", "この歌を続けて。", "別の曲を探して。" }, new[] { "generate", "Keep generating the draft.", "Inspect a different document." } }) {
                var v = (JObject)frame.DeepClone(); var descriptor = (JObject)v["actions"][0]; var action = (JObject)v["facts"]["action:" + (string)descriptor["id"]];
                descriptor["kind"] = variant[0]; descriptor["adapter"] = "generic-fixture"; action["adapter"] = "generic-fixture"; action.Remove("reason");
                (v["evidenceScopes"] as JObject).Remove("execution");
                var binding = v["evidenceScopes"]["action:" + (string)descriptor["id"]]["binding"]; binding["operation"] = variant[0]; binding["entity"] = "artifact-a";
                action["kind"] = variant[0]; action["phase"] = "running"; action["facts"] = new JObject { ["phase"] = "running" };
                action["task"]["step"] = new JObject { ["id"] = "s1", ["kind"] = variant[0] };
                action["task"]["origin"]["request"] = variant[1]; v["facts"]["request"]["text"] = variant[2];
                v["facts"]["action:" + (string)action["id"]] = action.DeepClone(); (v["facts"] as JObject).Remove("execution");
                yield return NativeMonitor(model, v, "general-handoff-" + variant[0]);
            }
            foreach (string request in new[] { "Stop following me now.", "现在停止跟随我。", "今は私について来るのをやめて。" }) {
                var stop = (JObject)frame.DeepClone(); stop["facts"]["request"]["text"] = request;
                yield return NativeMonitor(model, stop, "explicit-current-stop-" + request, false);
            }
            yield return NativeMonitor(model, (JObject)fixture["regressionAdditions"]["realConflict"]["input"].DeepClone(), "independent-evidence-real-conflict", false);
            yield return Transport(model, host);
            Save(true);
            if (checks.Any(c => !(bool)c["passed"])) throw new InvalidOperationException("Activity origin checks failed; see activity-origin.json");
        } finally { Application.logMessageReceived -= Log; model.CancelEphemeralMsg("regression-ended"); model.CancelActivityMonitor(); UnityEngine.Object.Destroy(host); }
    }

    static JObject Origins(ChatSample chat, ArdyRoomDialogueBridge bridge, ChatQW model)
    {
        var frame = At(99323); var live = (JObject)frame["facts"]["action:" + (string)frame["actions"][0]["id"]].DeepClone();
        string id = (string)live["id"], version = (string)live["version"]; int stops = 0;
        var settings = (ChatSetting)Get(chat, "m_ChatSettings"); settings.m_ChatModel = model;
        var ledger = Get(chat, "m_ActionConstraints"); Call(ledger, "ClearGoal");
        Set(ledger, "<CurrentUserTurn>k__BackingField", 4); // Seed recorded turn IDs in the isolated replay.
        Call(ledger, "RecordUserTurn", "跟着我走。");
        int turn = (int)Get(chat, "m_ActionConstraints").GetType().GetProperty("CurrentUserTurn").GetValue(Get(chat, "m_ActionConstraints"));
        Set(chat, "m_WorkEpoch", 5); Set(chat, "m_WorkAutonomousHandoff", false); Set(chat, "m_AgentCurrentRoundIsTick", false);
        var sequence = At(95662); sequence["userTurn"] = turn; sequence["steps"][0]["actionId"] = id; sequence["steps"][0]["status"] = "running";
        Set(chat, "m_RoomTaskSequence", sequence); Set(chat, "m_RoomTaskSequenceModel", model); Set(chat, "m_RoomTaskSequenceBridge", bridge);
        Set(chat, "m_RoomTaskSequenceAvatar", bridge.avatar); Set(chat, "m_RoomTaskSequenceEnvironment", bridge.environmentRoot);
        chat.RegisterMonitoredActivity("recorded-follow", new ChatSample.MonitoredActivity { Read = () => live, Stop = (v, why) => { if (v != (string)live["version"]) return false; stops++; return true; } });
        Set(chat, "m_WorkAutonomousHandoff", true);
        var stable = (JObject)Call(chat, "ActivityTaskContextWithOrigin", live, "recorded-follow");
        Check("later-autonomous-thought-does-not-relabel-user-action", (string)stable["origin"]["initiative"] == "user-work" && (string)stable["origin"]["request"] == "跟着我走。");
        Set(chat, "m_WorkAutonomousHandoff", false);
        // No monitor tick runs between registration and the actual user/reset hooks.
        Call(chat, "RecordAcceptedMotionUserTurn", (string)frame["facts"]["request"]["text"]);
        Call(chat, "ResetWorkReviewState", true, false);
        var task = (JObject)Call(chat, "ActivityTaskContextWithOrigin", live, "recorded-follow");
        Check("old-follow-source-survives-real-turn-reset", (string)task["origin"]["request"] == "跟着我走。" && (int)task["origin"]["userTurn"] == turn && (string)task["requestRelationship"] == "earlier-work-still-running", task);
        Check("historical-status-is-not-live-completion", task["step"]?["status"] == null && (string)task["step"]?["kind"] == "follow" && stops == 0);
        live["version"] = version + ":new-phase";
        Check("live-phase-version-does-not-rewrite-origin", JToken.DeepEquals(task["origin"], ((JObject)Call(chat, "ActivityTaskContextWithOrigin", live, "recorded-follow"))["origin"]));
        live["version"] = version; live["task"] = task.DeepClone();
        frame["facts"]["action:" + id] = live.DeepClone();
        chat.UnregisterMonitoredActivity("recorded-follow");
        live.Remove("task");
        Set(chat, "m_WorkAutonomousHandoff", true);
        chat.RegisterMonitoredActivity("recorded-follow", new ChatSample.MonitoredActivity { Read = () => live });
        var independent = (JObject)Call(chat, "ActivityTaskContextWithOrigin", live, "recorded-follow");
        Check("replacement-executor-cannot-inherit-old-source", (string)independent["origin"]["initiative"] == "autonomous" && (string)independent["origin"]["request"] == null && (string)independent["requestRelationship"] == "same-work", independent);
        Call(chat, "RecordAcceptedMotionUserTurn", "请说说天气。"); Call(chat, "ResetWorkReviewState", true, false);
        var continued = (JObject)Call(chat, "ActivityTaskContextWithOrigin", live, "recorded-follow");
        Check("autonomous-old-activity-keeps-independent-source", (string)continued["origin"]["initiative"] == "autonomous" && (string)continued["requestRelationship"] == "earlier-work-still-running" && stops == 0);
        chat.UnregisterMonitoredActivity("recorded-follow");
        foreach (string key in new[] { "prefix", "prefix:child" }) chat.RegisterMonitoredActivity(key, new ChatSample.MonitoredActivity { Read = () => live });
        chat.UnregisterMonitoredActivity("prefix");
        Check("adapter-key-prefix-does-not-remove-other-owner", ((IDictionary)Get(chat, "m_ActivityOrigins")).Contains(Call(chat, "ActivityOriginKey", "prefix:child", id)));
        chat.UnregisterMonitoredActivity("prefix:child");
        return frame;
    }

    static void Budgets(ChatQW model, JObject repaired)
    {
        foreach (var e in ((JArray)fixture["events"]).Where(e => (string)e["tag"] == "Activity/MonitorFrame")) {
            var frame = (int)e["line"] == 99323 ? repaired : (JObject)e["data"].DeepClone();
            // Other frames also carry the new bounded provenance envelope.
            foreach (var descriptor in ((JArray)frame["actions"]).OfType<JObject>()) {
                var action = (JObject)frame["facts"]["action:" + (string)descriptor["id"]];
                if (action["task"]?["origin"] != null) continue;
                var task = action["task"] as JObject ?? new JObject(); action["task"] = task;
                task["origin"] = new JObject { ["workEpoch"] = frame["owner"]["workEpoch"], ["userTurn"] = frame["owner"]["userTurn"], ["initiative"] = "user-work", ["request"] = frame["facts"]["request"]?["text"] };
                task["requestRelationship"] = "same-work";
                frame["facts"]["action:" + (string)action["id"]] = action.DeepClone();
            }
            string source = frame.ToString(Formatting.None);
            string ready = model.PrepareActivityMonitorFrame(source, out string budget);
            Check("full-monitor-budget-" + e["line"], ready != null, JObject.Parse(budget));
            Check("resident-source-retained-" + e["line"], source == frame.ToString(Formatting.None));
        }
        Check("original-monitor-error-retained", ((string)At(99423)["rawVerdict"]).Contains("回到我身边"), At(99423));
    }

    static IEnumerator NativeMonitor(ChatQW model, JObject frame, string name, bool expectContinue = true)
    {
        string ready = model.PrepareActivityMonitorFrame(frame.ToString(Formatting.None), out string budget);
        bool done = false, ok = false; string raw = null, status = null, error = null; JObject verdict = null;
        if (ready != null) { model.PostActivityMonitor(ready, (a, b, c) => { done = true; ok = a; raw = b; status = c; }); yield return Until(() => done); }
        bool valid = ok && ActivityMonitorProtocol.TryRead(raw, JObject.Parse(ready), out verdict, out error);
        Check("native-" + name, valid && (expectContinue ? (string)verdict["action"]["operation"] == "continue" : new[] { "stop", "reconsider" }.Contains((string)verdict["action"]["operation"]) && (string)verdict["action"]["id"] == (string)frame["actions"][0]["id"] && (string)verdict["action"]["version"] == (string)frame["actions"][0]["version"]), new JObject { ["frame"] = frame, ["raw"] = raw, ["status"] = status, ["error"] = error, ["budget"] = JObject.Parse(budget) });
    }

    static IEnumerator Transport(ChatQW model, GameObject host)
    {
        using (var server = new ReviewServer()) {
            Set(model, "url", server.Url); int callbacks = 0;
            var receipt = new JObject { ["workEpoch"] = 6, ["userTurn"] = 6, ["results"] = new JArray() };
            string wire = (string)Call(model, "BuildWorkReviewRequestJson", "Public current review");
            IEnumerator Request(float started = float.NaN, string input = null) => (IEnumerator)Call(model, "RequestBoundWorkReviewPayload", input ?? wire,
                (int)Get(model, "m_EphemeralGeneration"), new Action<PrivateDecisionResult>(r => callbacks++), 0, float.IsNaN(started) ? Time.realtimeSinceStartup : started, receipt);
            var old = Request();
            Check("old-http-sent", old.MoveNext() && old.Current is AsyncOperation); var op = (AsyncOperation)old.Current;
            yield return Until(() => server.Count >= 1);
            model.CancelEphemeralMsg("accepted-user-turn");
            var next = Request(); next.MoveNext(); var nextOp = (AsyncOperation)next.Current;
            object owned = Get(model, "m_EphemeralRequest");
            server.Release(0); yield return Until(() => op.isDone);
            Check("cancelled-old-no-callback", !old.MoveNext() && callbacks == 0);
            Check("late-old-finally-cannot-clear-new-handle", ReferenceEquals(owned, Get(model, "m_EphemeralRequest")));
            yield return Until(() => server.Count >= 2); server.Release(1); yield return Until(() => nextOp.isDone);
            Check("replacement-response-returns", !next.MoveNext() && callbacks == 1 && Get(model, "m_EphemeralRequest") == null);
            yield return Drain(model, Request()); // Actual HTTP 503.
            yield return Drain(model, Request()); // Actual HTTP 200 with length-truncated JSON.
            yield return Drain(model, Request(Time.realtimeSinceStartup - 21));
            var oversized = JObject.Parse(wire); ((JArray)oversized["messages"]).Add(new JObject { ["role"] = "user", ["content"] = new string('大', 80000) });
            yield return Drain(model, Request(input: oversized.ToString(Formatting.None)));
            var disabled = Request(); disabled.MoveNext(); var disabledOp = (AsyncOperation)disabled.Current;
            yield return Until(() => server.Count >= 6); model.enabled = true; host.SetActive(false); server.Release(5); yield return Until(() => disabledOp.isDone); disabled.MoveNext(); model.enabled = false; host.SetActive(true);
            yield return Drain(model, Request()); // Missing coverage gets exactly one bounded repair.
            var stale = (IEnumerator)Call(model, "RequestBoundWorkReviewPayload", wire, (int)Get(model, "m_EphemeralGeneration") - 1, new Action<PrivateDecisionResult>(r => callbacks++), 0, Time.realtimeSinceStartup, receipt);
            Check("stale-before-send-no-callback", !stale.MoveNext() && callbacks == 6);
            Check("each-created-attempt-has-one-terminal", traces.Where(t => (string)t["stage"] == "created").All(t => traces.Count(x => (string)x["requestId"] == (string)t["requestId"] && (string)x["stage"] == "terminal") == 1), traces);
            Check("cancel-cause-owner-and-observed-transport-retained", traces.Any(t => (string)t["cancelReason"] == "accepted-user-turn" && (int?)t["owner"]?["workEpoch"] == 6) && traces.Any(t => (string)t["stage"] == "transport-completed" && !(bool)t["ownerCurrent"]));
            Check("disabled-model-closes-without-stale-callback", traces.Any(t => (string)t["stage"] == "terminal" && (string)t["cancelReason"] == "model-disabled") && callbacks == 6);
            Check("budget-and-deadline-never-sent", traces.Where(t => (string)t["stage"] == "terminal" && new[] { "context-budget", "total-deadline" }.Contains((string)t["status"])).Count(t => !(bool)t["httpRequestSent"]) == 2 && server.Count == 8);
            Check("bounded-repair-shares-logical-review-distinct-attempts", traces.Where(t => (string)t["stage"] == "terminal").GroupBy(t => (string)t["reviewId"]).Any(g => g.Count() == 2 && g.Select(t => (string)t["requestId"]).Distinct().Count() == 2 && g.Any(t => (int)t["attempt"] == 1 && (string)t["status"] == "received")));
            Check("normal-failure-has-observed-http-terminal", traces.Any(t => (string)t["stage"] == "terminal" && (long?)t["httpStatus"] == 503 && (bool)t["transportFinalKnown"]));
        }
    }

    sealed class ReviewServer : IDisposable
    {
        readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        readonly ManualResetEventSlim[] gates = Enumerable.Range(0, 8).Select(_ => new ManualResetEventSlim()).ToArray();
        volatile bool stopped; int count; public int Count => Volatile.Read(ref count); public string Url { get; }
        public ReviewServer() { listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port; Task.Run((Action)Serve); }
        public void Release(int i) => gates[i].Set();
        void Serve() {
            for (int i = 0; i < 8 && !stopped; i++) try {
                using (var client = listener.AcceptTcpClient()) using (var stream = client.GetStream()) {
                    client.ReceiveTimeout = 10000; client.SendTimeout = 10000;
                    // Reuse the established local HTTP fixture parser/response only.
                    var type = typeof(AgentWorkReviewHttpRegression).GetNestedType("LoopbackServer", BindingFlags.NonPublic);
                    type.GetMethod("ReadRequest", F).Invoke(null, new object[] { stream }); Interlocked.Increment(ref count);
                    if (i < 2 || i == 5) gates[i].Wait(30000);
                    string body = (string)type.GetMethod("Response", F).Invoke(null, new object[] { i == 3 ? "length" : "stop" });
                    if (i != 6) { var envelope = JObject.Parse(body); var decision = JObject.Parse((string)envelope["choices"][0]["message"]["content"]); decision["delivery_reports"] = new JArray(); envelope["choices"][0]["message"]["content"] = decision.ToString(Formatting.None); body = envelope.ToString(Formatting.None); }
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + (i == 2 ? "503 Service Unavailable" : "200 OK") + "\r\nContent-Type: application/json\r\nx-request-id: fixture-" + i + "\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length); stream.Write(bytes, 0, bytes.Length);
                }
            } catch (Exception) { if (stopped) break; }
        }
        public void Dispose() { stopped = true; foreach (var gate in gates) gate.Set(); listener.Stop(); }
    }
}
