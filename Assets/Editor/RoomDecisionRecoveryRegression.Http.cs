using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class RoomDecisionRecoveryRegression
{
    public static JObject HttpReport { get; private set; }
    private const string SuccessfulDecision = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"intent\\\":\\\"none\\\",\\\"origin\\\":\\\"none\\\",\\\"evidence\\\":\\\"\\\"}\"}}]}";

    public static IEnumerator RunHttpChecks()
    {
        HttpReport = new JObject { ["status"] = "running", ["scope"] = "Native UnityWebRequest and public decision entrypoints against synthetic ephemeral loopback HTTP. No model, motion, TTS or private scene." };
        var cases = new JArray(); HttpReport["cases"] = cases;
        int beforeChecks = checks;
        foreach (string name in new[] { "transient-then-success", "context-then-success", "context-history-budget-stop", "unknown-400", "schema-400", "transient-exhausted", "review-no-retry", "cancel-backoff", "replace-backoff", "large-first-admission", "protected-local-refusal", "preparation-local-refusal" })
        {
            var host = new GameObject("RoomRecoveryHttp_" + name); host.SetActive(false);
            ChatQW provider = null;
            int calls = 0, replacementCalls = 0;
            bool accepted = false, replacementAccepted = false, failureObserved = false;
            string status = null;
            Application.LogCallback log = (message, trace, type) => {
                if (message.StartsWith("[Room/TaskHttpFailure] ", StringComparison.Ordinal)) failureObserved = true;
            };
            Application.logMessageReceived += log;
            using (var server = new RecoveryLoopbackServer(name))
            {
                try
                {
                    provider = host.AddComponent<ChatQW>(); provider.enabled = false;
                    provider.m_Backend = ChatQW.BackendType.Local; provider.m_LocalUrl = server.Url;
                    provider.m_PrewarmPrefixOnStart = false; provider.m_LogRequestStats = false;
                    typeof(LLM).GetField("url", Flags).SetValue(provider, server.Url);
                    host.SetActive(true);
                    Require((string)typeof(LLM).GetField("url", Flags).GetValue(provider) == server.Url,
                        "Loopback decision fixture cannot select an external backend.");
                    string input = BuildSource().ToString(Formatting.None);
                    if (name == "context-history-budget-stop")
                    {
                        var protectedSource = BuildSource();
                        protectedSource["unprojectableCurrentPolicy"] = new string('密', 22000);
                        input = protectedSource.ToString(Formatting.None);
                    }
                    if (name == "large-first-admission" || name == "protected-local-refusal" || name == "preparation-local-refusal")
                    {
                        var large = RoomDecisionBudgetRegression.BuildLargeInput();
                        if (name != "large-first-admission") large["currentUserText"] = new string('密', 80000);
                        input = large.ToString(Formatting.None);
                    }
                    const string pixels = "data:image/png;base64,CURRENT_PIXELS_CANARY";
                    Action<bool, string, string> callback = (ok, output, state) => {
                        calls++; accepted = ok; status = state;
                        Require(ok || output == "", "Rejected HTTP never releases partial action JSON to its caller.");
                    };
                    if (name == "transient-then-success" || name == "context-history-budget-stop")
                    {
                        var metadata = new JObject { ["captureId"] = "history-capture", ["source"] = "character-scene-rgb", ["state"] = "ready", ["hasImage"] = true };
                        provider.PostRoomTaskMessageWithObservationHistory(input, pixels,
                            new[] { new LLM.RoomObservationImage("history-capture", metadata.ToString(Formatting.None), "data:image/png;base64,HISTORICAL_PIXELS_CANARY") }, callback);
                    }
                    else if (name == "preparation-local-refusal") provider.PostRoomPreparationMessage(input, callback);
                    else provider.PostRoomTaskMessage(input, pixels, name == "review-no-retry", callback);

                    float deadline = Time.realtimeSinceStartup + 8;
                    if (name == "cancel-backoff" || name == "replace-backoff")
                    {
                        while (!failureObserved)
                        {
                            server.ThrowIfFailed();
                            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(name + " did not reach a failed first attempt.");
                            yield return null;
                        }
                        Require(calls == 0 && server.Count == 1, "A retry plan does not prematurely callback or dispatch the old decision.");
                        if (name == "cancel-backoff") provider.CancelRoomTaskPlanningMessage();
                        else
                        {
                            var replacement = BuildSource(); replacement["currentUserText"] = "不要移动，先回答我。";
                            provider.PostRoomTaskMessage(replacement.ToString(Formatting.None), pixels, false, (ok, output, state) => { replacementCalls++; replacementAccepted = ok; });
                            while (replacementCalls == 0)
                            {
                                server.ThrowIfFailed();
                                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Replacement decision did not settle.");
                                yield return null;
                            }
                        }
                    }
                    else
                    {
                        while (calls == 0)
                        {
                            server.ThrowIfFailed();
                            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Decision did not settle: " + name);
                            yield return null;
                        }
                    }
                    // Past the real retry delay: detect accidental old-owner submissions/callbacks.
                    float quietUntil = Time.realtimeSinceStartup + .8f;
                    while (Time.realtimeSinceStartup < quietUntil) { server.ThrowIfFailed(); yield return null; }
                    string[] requests = server.Requests;
                    if (name == "protected-local-refusal" || name == "preparation-local-refusal")
                    {
                        Require(calls == 1 && !accepted && requests.Length == 0, "Unprojectable protected content publishes exactly one local failure and no HTTP request.");
                        Require(ChatQW.TryReadRoomTaskFailureStatus(status, out var localFacts) && (int?)localFacts["attempts"] == 0 &&
                            (bool?)localFacts["httpRequestSent"] == false && (string)localFacts["category"] == "context-budget",
                            "Real public decision callback reports preflight budget refusal truthfully.");
                    }
                    else if (name == "large-first-admission")
                    {
                        Require(calls == 1 && accepted && requests.Length == 1, "A large request is projected before its only actual HTTP and publishes one decision.");
                        var actual = MainInput(JObject.Parse(requests[0]));
                        Require(actual["decisionEvidenceProjection"] != null && (string)actual["currentUserText"] == "来我身边。\nCome here, please. 😀",
                            "The actual first wire carries explicit projection metadata and intact original user authority.");
                        var measure = (JObject)typeof(ChatQW).GetMethod("MeasureRoomDecisionBudget", Flags).Invoke(null, new object[] { requests[0], 49152 });
                        Require((bool)measure["allowed"] && (int)measure["imageCount"] == 1,
                            "The actual admitted first wire fits the complete payload estimate including its retained current image.");
                    }
                    else if (name == "cancel-backoff")
                        Require(calls == 0 && requests.Length == 1, "Cancellation during backoff suppresses the second HTTP and every old callback.");
                    else if (name == "replace-backoff")
                    {
                        Require(calls == 0 && replacementCalls == 1 && replacementAccepted && requests.Length == 2,
                            "A new turn owns the only second HTTP and callback; the old failed turn cannot resume.");
                        Require(MainInput(JObject.Parse(requests[1]))["currentUserText"].Value<string>() == "不要移动，先回答我。",
                            "The second HTTP contains replacement authority, never a retry of the cancelled movement request.");
                    }
                    else if (name == "transient-then-success" || name == "context-then-success")
                    {
                        Require(calls == 1 && accepted && status == "received" && requests.Length == 2,
                            "A recovered request publishes exactly one accepted decision after two actual HTTP submissions.");
                        if (name == "transient-then-success")
                        {
                            Require(requests[0] == requests[1], "A transient retry preserves the immutable payload exactly.");
                            var actual = JObject.Parse(requests[1]);
                            Require(actual.SelectTokens("$..image_url.url").Count() == 2 &&
                                actual["messages"][1].ToString(Formatting.None).Contains("history-capture") &&
                                JToken.DeepEquals(MainInput(actual), RoomTaskContextPolicy.PlanningObservationView(BuildSource())),
                                "A real recovered two-image request preserves the original purpose-scoped authority, historical labels, current/historical pixels and native schema byte for byte.");
                        }
                        else
                        {
                            var original = JObject.Parse(requests[0]); var reduced = JObject.Parse(requests[1]);
                            var source = MainInput(original); var restored = MainInput(reduced);
                            var table = (JObject)restored["requestEvidenceTable"];
                            Require(table != null && requests[1].Length < requests[0].Length, "The actual second HTTP contains a smaller valid evidence projection.");
                            Expand(restored, table); restored.Remove("requestEvidenceTable"); restored.Remove("requestEvidenceEncoding");
                            Require(JToken.DeepEquals(source, restored) && JToken.DeepEquals(original["response_format"], reduced["response_format"]),
                                "Actual recovered HTTP preserves all authority/current geometry/history and native schema under lossless expansion.");
                            Require(original.SelectTokens("$..image_url.url").Select(x => (string)x).SequenceEqual(reduced.SelectTokens("$..image_url.url").Select(x => (string)x)) &&
                                reduced.SelectTokens("$..image_url.url").Count() == 1 &&
                                JToken.DeepEquals(((JArray)original["messages"]).Last, ((JArray)reduced["messages"]).Last),
                                "Recoverable context retry retains current image bytes and the exact final authority suffix.");
                        }
                    }
                    else
                    {
                        int expected = name == "transient-exhausted" ? 2 : 1;
                        Require(calls == 1 && !accepted && requests.Length == expected,
                            "Permanent/review rejection is single-shot; transient recovery is capped at two attempts.");
                        Require(ChatQW.TryReadRoomTaskFailureStatus(status, out var facts) && (int?)facts["attempts"] == expected &&
                            (bool?)facts["retryAttempted"] == (expected > 1), "Failure speech receives actual attempt facts, never a planned-but-unsent retry.");
                        if (name == "context-history-budget-stop")
                        {
                            var actual = JObject.Parse(requests[0]);
                            var expectedSource = BuildSource(); expectedSource["unprojectableCurrentPolicy"] = new string('密', 22000);
                            Require(status.EndsWith("recovery=context-not-reducible", StringComparison.Ordinal) &&
                                actual.SelectTokens("$..image_url.url").Count() == 2 &&
                                actual["messages"][1].ToString(Formatting.None).Contains("history-capture") &&
                                JToken.DeepEquals(MainInput(actual), RoomTaskContextPolicy.PlanningObservationView(expectedSource)),
                                "When protected two-image/schema/authority content cannot meet the tighter budget, stop after the failed first HTTP without stripping history or replaying an over-budget request.");
                        }
                    }
                    cases.Add(new JObject { ["case"] = name, ["passed"] = true, ["requests"] = requests.Length,
                        ["callbacks"] = calls, ["replacementCallbacks"] = replacementCalls, ["accepted"] = accepted,
                        ["status"] = status, ["firstRequestCharacters"] = requests.Length > 0 ? new JValue(requests[0].Length) : JValue.CreateNull(),
                        ["secondRequestCharacters"] = requests.Length > 1 ? new JValue(requests[1].Length) : JValue.CreateNull() });
                }
                finally
                {
                    Application.logMessageReceived -= log;
                    if (provider != null) provider.CancelActiveResponse();
                    UnityEngine.Object.Destroy(host);
                }
            }
        }
        HttpReport["checks"] = checks - beforeChecks;
        HttpReport["status"] = "passed";
    }

    private sealed class RecoveryLoopbackServer : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        private readonly List<string> requests = new List<string>();
        private readonly Task worker;
        private volatile bool stopping;
        public string Url { get; }
        public int Count { get { lock (requests) return requests.Count; } }
        public string[] Requests { get { lock (requests) return requests.ToArray(); } }
        public RecoveryLoopbackServer(string name)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = "http://127.0.0.1:" + port + "/v1/chat/completions";
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
            worker = Task.Run(async () => {
                try
                {
                    while (!stopping)
                    {
                        var context = await listener.GetContextAsync();
                        string body;
                        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
                        int attempt;
                        lock (requests) { requests.Add(body); attempt = requests.Count; }
                        int code = 200; string response = SuccessfulDecision;
                        if (name == "unknown-400") { code = 400; response = Error("unknown_request_error", "request rejected"); }
                        else if (name == "schema-400") { code = 400; response = Error("invalid_schema", "invalid json schema"); }
                        else if ((name == "context-then-success" || name == "context-history-budget-stop") && attempt == 1) { code = 400; response = Error("context_length_exceeded", "request exceeds the available context size"); }
                        else if ((name == "transient-then-success" || name == "cancel-backoff" || name == "replace-backoff" || name == "review-no-retry") && attempt == 1 || name == "transient-exhausted")
                        { code = 503; response = Error("server_busy", "no slots available"); }
                        byte[] bytes = Encoding.UTF8.GetBytes(response);
                        context.Response.StatusCode = code; context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = bytes.Length;
                        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                        context.Response.Close();
                    }
                }
                catch (Exception) { if (!stopping) throw; }
            });
        }
        public void ThrowIfFailed() { if (worker.IsFaulted) throw worker.Exception; }
        private static string Error(string code, string message) => new JObject { ["error"] = new JObject { ["code"] = code, ["message"] = message } }.ToString(Formatting.None);
        public void Dispose() { stopping = true; listener.Stop(); listener.Close(); }
    }
}
