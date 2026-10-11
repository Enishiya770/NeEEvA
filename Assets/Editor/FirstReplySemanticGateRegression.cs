using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Actual provider requests and cancellation over local controlled HTTP. Semantic
// accuracy is checked separately with the exported single-pass model cases.
public static class FirstReplySemanticGateRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static JObject LastReport { get; private set; }
    private static int checks;
    private static void Check(bool condition, string message)
    { ++checks; if (!condition) throw new InvalidOperationException(message); }

    public static IEnumerator RunChecks(string output)
    {
        checks = 0;
        string directory = Path.Combine(output, "first-reply-semantic-transport"); Directory.CreateDirectory(directory);
        var cases = new JArray();
        LastReport = new JObject { ["status"] = "running", ["cases"] = cases,
            ["scope"] = "Real production provider with loopback HTTP, no live model or TTS. Controlled review decisions do not prove semantic accuracy." };
        var host = new GameObject("FirstReplySemanticLoopback"); host.SetActive(false);
        ChatQW model = null;
        using (var server = new Server())
        try
        {
            model = host.AddComponent<ChatQW>(); model.enabled = false;
            model.m_Backend = ChatQW.BackendType.Local; model.m_LocalUrl = server.Url;
            model.m_PrewarmPrefixOnStart = false; model.m_LogRequestStats = false;
            host.SetActive(true); typeof(LLM).GetField("url", Flags).SetValue(model, server.Url);
            foreach (JObject item in FirstReplyTransportRegression.SemanticCases())
            {
                model.m_DataList.Clear();
                int caseRequests = server.Count;
                string user = (string)item["user"], expected = (string)item["expectedRoute"];
                var first = server.Serve((string)item["candidate"], true, true);
                int deltas = 0, completions = 0; FirstReplyCompletion result = null;
                model.PostFirstReplyStream(user, _ => ++deltas, value => { result = value; ++completions; },
                    transientContext: (string)item["context"]);
                yield return Await(first.Received.Task, "ordinary candidate"); yield return null;
                var accepted = model.m_DataList.Find(row => row.role == "user");
                Check(deltas == 0 && completions == 0 && accepted?.content == user &&
                    !model.m_DataList.Exists(row => row.role == "assistant"), "Candidate SSE remains private before completion/review.");
                var review = server.Serve(Verdict(expected), false, true); first.Release.TrySetResult(true);
                yield return Await(review.Received.Task, "held semantic review"); yield return null;
                Check(deltas == 0 && completions == 0 && !model.m_DataList.Exists(row => row.role == "assistant"),
                    "Completed ordinary candidate cannot enter speech/history while its semantic review is held.");
                var wire = JObject.Parse(review.RequestJson);
                var input = JObject.Parse((string)wire["messages"][1]["content"]);
                Check((string)input["latestUserText"] == user && input["candidateSpeech"] is JArray &&
                    (bool?)wire["stream"] == false && (int?)wire["id_slot"] == 1,
                    "Actual second HTTP request carries the complete user independently of the private speech candidate.");
                review.Release.TrySetResult(true); yield return WaitUntil(() => completions != 0, "reviewed completion");
                Check(server.Count == caseRequests + 2 && completions == 1 && result != null &&
                    result.Disposition == (expected == "ordinary" ? FirstReplyDisposition.Ordinary :
                        expected == "evidence" ? FirstReplyDisposition.Evidence : FirstReplyDisposition.Tools) &&
                    (deltas > 0) == (expected == "ordinary") && result.SpeechDelivered == (expected == "ordinary") &&
                    model.m_DataList.FindAll(row => row.role == "assistant").Count == (expected == "ordinary" ? 1 : 0),
                    "Only reviewed ordinary speech is released once; tool handoff discards the candidate and its history.");
                Check(expected == "evidence" ? JToken.DeepEquals(result.QuestionUnderstanding, ControlledUnderstanding()) : result.QuestionUnderstanding == null,
                    "The independently parsed understanding must accompany only an evidence handoff, never speech or tools.");
                Check(result.RequestKind == (expected == "tools" ? "action" : "answer-only"),
                    "The provider must retain the independently typed request requirement with its completion.");
                var handoff = typeof(ChatQW).GetMethod("ClaimFirstReplyUserHandoff", Flags);
                Check(ReferenceEquals(handoff.Invoke(model, null), expected != "ordinary" ? accepted : null) &&
                    handoff.Invoke(model, null) == null && model.m_DataList.FindAll(row => ReferenceEquals(row, accepted)).Count == 1,
                    "Handoff retains the exact accepted user once, without duplicating it or making an ordinary reply executable.");
                File.WriteAllText(Path.Combine(directory, (string)item["id"] + ".request.json"), review.RequestJson, new UTF8Encoding(false));
                cases.Add(new JObject { ["id"] = item["id"], ["controlledRoute"] = expected, ["publicDeltas"] = deltas });
                yield return Await(first.Finished, "candidate socket"); yield return Await(review.Finished, "review socket");
            }

            foreach (string kind in new[] { "action", "control", "mixed", "uncertain" })
            {
                model.m_DataList.Clear(); int typedRequests = server.Count, deltas = 0, completions = 0; FirstReplyCompletion result = null;
                const string user = "原因也请解释；最后一句的当前请求必须完整保留。";
                var first = server.Serve(FirstReplyProtocol.EvidenceHead, true, true);
                model.PostFirstReplyStream(user, _ => ++deltas, value => { result = value; ++completions; });
                yield return Await(first.Received.Task, "contradictory route candidate");
                var review = server.Serve(Verdict("evidence", kind), false, false); first.Release.TrySetResult(true);
                yield return WaitUntil(() => completions != 0, "typed request route correction");
                Check(server.Count == typedRequests + 2 && completions == 1 && deltas == 0 && result?.RequestKind == kind &&
                    result.Disposition == FirstReplyDisposition.Tools && result.QuestionUnderstanding == null &&
                    !result.SpeechDelivered && !model.m_DataList.Exists(row => row.role == "assistant"),
                    "Contradictory evidence publication cannot override the typed action/control requirement or release a read-only answer.");
                var handed = (LLM.SendData)typeof(ChatQW).GetMethod("ClaimFirstReplyUserHandoff", Flags).Invoke(model, null);
                Check(handed?.content == user && typeof(ChatQW).GetMethod("ClaimFirstReplyUserHandoff", Flags).Invoke(model, null) == null,
                    "Corrected routing must preserve the same complete accepted user once, not construct a replacement command.");
                yield return Await(first.Finished, "contradictory candidate socket"); yield return Await(review.Finished, "contradictory review socket");
                cases.Add(new JObject { ["id"] = "typed-" + kind + "-overrides-evidence", ["publicDeltas"] = deltas });
            }

            foreach (string mode in new[] { "sufficient-evidence", "insufficient-ordinary", "full-tools-sufficient",
                "empty-evidence-sufficient", "empty-tools-sufficient", "action-over-sufficient" })
            {
                model.m_DataList.Clear(); int publicationRequests = server.Count, deltas = 0, completions = 0; FirstReplyCompletion result = null;
                bool empty = mode.StartsWith("empty-", StringComparison.Ordinal);
                string candidate = mode == "empty-tools-sufficient" ? FirstReplyProtocol.ToolsHead : empty ?
                    FirstReplyProtocol.EvidenceHead : FirstReplyProtocolRegression.JapaneseReply;
                string rawRoute = mode == "full-tools-sufficient" ? "tools" : mode == "insufficient-ordinary" || mode == "empty-tools-sufficient" ? "ordinary" : "evidence";
                string kind = mode == "action-over-sufficient" ? "action" : "answer-only";
                bool sufficient = mode != "insufficient-ordinary";
                var expected = mode == "sufficient-evidence" ? FirstReplyDisposition.Ordinary :
                    mode == "full-tools-sufficient" || mode == "action-over-sufficient" ? FirstReplyDisposition.Tools : FirstReplyDisposition.Evidence;
                var first = server.Serve(candidate, true, true);
                model.PostFirstReplyStream("完整当前请求由语义审核判定。", _ => ++deltas, value => { result = value; ++completions; });
                yield return Await(first.Received.Task, "typed candidate publication");
                var review = server.Serve(Verdict(rawRoute, kind, sufficient,
                    mode == "full-tools-sufficient" ? "full-response" : "short-conversation"), false, false); first.Release.TrySetResult(true);
                yield return WaitUntil(() => completions != 0, "typed candidate publication result");
                Check(server.Count == publicationRequests + 2 && completions == 1 && result?.Disposition == expected &&
                    result.CandidateSufficient == sufficient && (deltas > 0) == (expected == FirstReplyDisposition.Ordinary) &&
                    model.m_DataList.FindAll(row => row.role == "assistant").Count == (expected == FirstReplyDisposition.Ordinary ? 1 : 0),
                    "Real transport candidate sufficiency must respect actual public text, action requirements, full-response routing and unique history release: " + mode);
                yield return Await(first.Finished, "candidate publication socket"); yield return Await(review.Finished, "publication review socket");
                cases.Add(new JObject { ["id"] = mode, ["rawRoute"] = rawRoute, ["derivedRoute"] = result.Disposition.ToString(),
                    ["candidateSufficient"] = sufficient, ["publicDeltas"] = deltas });
            }

            foreach (string mode in new[] { "grounded-confirmed", "grounded-unproved", "full-over-evidence", "full-over-ordinary",
                "action-over-grounded", "short-over-tools" })
            {
                model.m_DataList.Clear(); int demandRequests = server.Count, deltas = 0, completions = 0; FirstReplyCompletion result = null;
                string demand = mode.StartsWith("full-", StringComparison.Ordinal) ? "full-response" :
                    mode == "short-over-tools" ? "short-conversation" : "grounded-answer";
                string kind = mode == "action-over-grounded" ? "action" : "answer-only";
                string rawRoute = mode == "full-over-evidence" ? "evidence" : mode == "short-over-tools" ? "tools" : "ordinary";
                var expected = kind == "action" || demand == "full-response" ? FirstReplyDisposition.Tools :
                    demand == "grounded-answer" ? FirstReplyDisposition.Evidence : FirstReplyDisposition.Ordinary;
                string user = mode.StartsWith("grounded-", StringComparison.Ordinal) ? "你已经到我身边了吗？" : "完整要求必须由审核独立保留。";
                var first = server.Serve(FirstReplyProtocolRegression.JapaneseReply, true, true);
                model.PostFirstReplyStream(user, _ => ++deltas, value => { result = value; ++completions; }, transientContext:
                    new JObject { ["body"] = new JObject { ["currentlyNearAndFacingUser"] = mode == "grounded-confirmed" } }.ToString(Formatting.None));
                yield return Await(first.Received.Task, "typed response demand candidate");
                var review = server.Serve(Verdict(rawRoute, kind, true, demand), false, false); first.Release.TrySetResult(true);
                yield return WaitUntil(() => completions != 0, "typed response demand completion");
                Check(server.Count == demandRequests + 2 && completions == 1 && result?.Disposition == expected &&
                    result.ResponseDemand == demand && (deltas > 0) == (expected == FirstReplyDisposition.Ordinary) &&
                    model.m_DataList.FindAll(row => row.role == "assistant").Count == (expected == FirstReplyDisposition.Ordinary ? 1 : 0),
                    "Response demand must route both proven and unproved factual questions through review, preserve full content and never erase actions: " + mode);
                yield return Await(first.Finished, "demand candidate socket"); yield return Await(review.Finished, "demand review socket");
                cases.Add(new JObject { ["id"] = mode, ["rawRoute"] = rawRoute, ["responseDemand"] = demand,
                    ["derivedRoute"] = result.Disposition.ToString(), ["publicDeltas"] = deltas });
            }

            foreach (string mode in new[] { "invalid-review", "incomplete-review", "timeout", "evidence-with-empty-ordinary-verdict",
                "tools-with-empty-ordinary-verdict", "tools-invalid-review", "invalid-understanding", "legacy-2", "legacy-3", "legacy-4", "legacy-5" })
            {
                model.m_DataList.Clear(); int deltas = 0, completions = 0; FirstReplyCompletion result = null;
                bool emptyOrdinary = mode.EndsWith("empty-ordinary-verdict", StringComparison.Ordinal);
                var first = server.Serve(mode.StartsWith("tools-", StringComparison.Ordinal) ? FirstReplyProtocol.ToolsHead :
                    emptyOrdinary ? FirstReplyProtocol.EvidenceHead : FirstReplyProtocolRegression.JapaneseReply, true, true);
                model.PostFirstReplyStream("这是普通问题。", _ => ++deltas, value => { result = value; ++completions; });
                yield return Await(first.Received.Task, "failure candidate");
                string response = mode.EndsWith("invalid-review", StringComparison.Ordinal) ? "{\"route\":\"ordinary\"}" :
                    mode == "invalid-understanding" ? Verdict("evidence").Replace("prior-dialogue", "execute-action") : Verdict("ordinary");
                if (mode.StartsWith("legacy-", StringComparison.Ordinal))
                {
                    int fields = int.Parse(mode.Substring("legacy-".Length));
                    var legacy = new JObject { ["route"] = "ordinary", ["reason"] = "Old shape returned to a current request." };
                    if (fields >= 3) legacy["questionUnderstanding"] = null;
                    if (fields >= 4) legacy["requestKind"] = "answer-only";
                    if (fields >= 5) legacy["candidateSufficient"] = true;
                    response = legacy.ToString(Formatting.None);
                }
                var review = server.Serve(response, false,
                    mode == "timeout", mode == "incomplete-review" ? "length" : "stop");
                first.Release.TrySetResult(true); yield return Await(review.Received.Task, "failure review");
                yield return WaitUntil(() => completions != 0, "failed gate completion");
                Check(completions == 1 && result?.Disposition == (emptyOrdinary ? FirstReplyDisposition.Evidence : FirstReplyDisposition.Failed) && !result.SpeechDelivered &&
                    deltas == 0 && !model.m_DataList.Exists(row => row.role == "assistant") &&
                    typeof(ChatQW).GetMethod("ClaimFirstReplyUserHandoff", Flags).Invoke(model, null) != null,
                    "Malformed/incomplete/timed-out review transfers the intact turn; an ordinary verdict without candidate speech requires evidence rewrite, never empty public success or an action handoff.");
                review.Release.TrySetResult(true); yield return Await(review.Finished, "failure socket");
                cases.Add(new JObject { ["id"] = mode, ["failure"] = result.Failure, ["publicDeltas"] = deltas });
            }

            foreach (string candidateRoute in new[] { "ordinary", "evidence", "tools" })
            foreach (bool newTurn in new[] { false, true })
            {
                model.m_DataList.Clear(); int oldDeltas = 0, oldDone = 0, newDone = 0;
                int caseRequests = server.Count;
                var first = server.Serve(candidateRoute == "tools" ? FirstReplyProtocol.ToolsHead :
                    candidateRoute == "evidence" ? FirstReplyProtocol.EvidenceHead : FirstReplyProtocolRegression.JapaneseReply, true, true);
                model.PostFirstReplyStream("旧问题完整原文", _ => ++oldDeltas, _ => ++oldDone);
                yield return Await(first.Received.Task, "cancel candidate");
                var review = server.Serve(Verdict(candidateRoute), false, true); first.Release.TrySetResult(true);
                yield return Await(review.Received.Task, "cancel held review");
                if (newTurn)
                {
                    var next = server.Serve(FirstReplyProtocol.ToolsHead, true, true);
                    model.PostFirstReplyStream("新问题完整原文", _ => throw new Exception("New tools speech"), _ => ++newDone);
                    yield return Await(next.Received.Task, "replacement tools candidate");
                    var nextReview = server.Serve(Verdict("tools"), false, false); next.Release.TrySetResult(true);
                    yield return WaitUntil(() => newDone != 0, "replacement tools completion");
                    yield return Await(next.Finished, "replacement socket");
                    yield return Await(nextReview.Finished, "replacement review socket");
                }
                else model.CancelActiveResponse();
                review.Release.TrySetResult(true); yield return Await(review.Finished, "late old review socket");
                yield return null; yield return null;
                Check(server.Count == caseRequests + (newTurn ? 4 : 2) && oldDeltas == 0 && oldDone == 0 && newDone == (newTurn ? 1 : 0) &&
                    !model.m_DataList.Exists(row => row.role == "assistant") &&
                    model.m_DataList.Exists(row => row.role == "user" && row.content == "旧问题完整原文"),
                    "Cancel/new turn while review is held invalidates all old public, completion and history writes.");
                var claimed = (LLM.SendData)typeof(ChatQW).GetMethod("ClaimFirstReplyUserHandoff", Flags).Invoke(model, null);
                Check(newTurn ? claimed?.content == "新问题完整原文" : claimed == null,
                    "Late review cannot claim or replace the current accepted-user handoff.");
                cases.Add(new JObject { ["id"] = candidateRoute + "-" +
                    (newTurn ? "new-turn-held-review" : "cancel-held-review"), ["oldPublicDeltas"] = oldDeltas });
            }

            foreach (string mode in new[] { "repaired", "exhausted", "cancelled" })
            {
                model.m_DataList.Clear(); int beforeRepair = server.Count, deltas = 0, done = 0; FirstReplyCompletion result = null;
                const string user = "很好，然后来我身边。";
                var bad = JObject.Parse(Verdict("tools", "mixed"));
                bad["currentTurnEvidence"] = new JObject { ["answerQuote"] = "你刚才看到小灯了吗？", ["actionQuote"] = "来我身边" };
                var good = JObject.Parse(Verdict("tools", "action"));
                good["currentTurnEvidence"] = new JObject { ["answerQuote"] = null, ["actionQuote"] = "来我身边" };
                var first = server.Serve(FirstReplyProtocol.ToolsHead, true, true);
                model.PostFirstReplyStream(user, _ => ++deltas, value => { result = value; ++done; });
                yield return Await(first.Received.Task, "grounding candidate");
                var review = server.Serve(bad.ToString(Formatting.None), false, true); first.Release.TrySetResult(true);
                yield return Await(review.Received.Task, "ungrounded review");
                var repair = server.Serve((mode == "exhausted" ? bad : good).ToString(Formatting.None), false, true);
                review.Release.TrySetResult(true);
                yield return Await(repair.Received.Task, "single grounding repair");
                var repairInput = JObject.Parse((string)JObject.Parse(repair.RequestJson)["messages"][1]["content"]);
                Check((string)repairInput["currentUtterance"] == user && repairInput["groundingRepair"] is JObject &&
                    deltas == 0 && done == 0 && !model.m_DataList.Exists(row => row.role == "assistant"),
                    "Invalid historical quotes must trigger one private repair with the intact current user and no public or history release.");
                if (mode == "cancelled") model.CancelActiveResponse();
                repair.Release.TrySetResult(true); yield return Await(repair.Finished, "grounding repair socket");
                if (mode != "cancelled") yield return WaitUntil(() => done != 0, "grounding result");
                else { yield return new WaitForSecondsRealtime(.1f); }
                Check(server.Count == beforeRepair + 3 && deltas == 0 && done == (mode == "cancelled" ? 0 : 1) &&
                    (mode != "repaired" || result?.RequestKind == "action" && result.Disposition == FirstReplyDisposition.Tools && result.CurrentTurnEvidence != null) &&
                    (mode != "exhausted" || result?.Disposition == FirstReplyDisposition.Failed) &&
                    !model.m_DataList.Exists(row => row.role == "assistant"),
                    "Grounding recovery must be bounded to one attempt, preserve verified intent, and respect cancellation: " + mode);
                cases.Add(new JObject { ["id"] = "grounding-" + mode, ["requests"] = server.Count - beforeRepair,
                    ["completions"] = done, ["publicDeltas"] = deltas });
            }

            // Invalid generation output never gets semantic approval after an
            // apparently valid prefix; no review request is issued for it.
            model.m_DataList.Clear(); int failedDone = 0, failedSpeech = 0; FirstReplyCompletion failedResult = null;
            int before = server.Count;
            var broken = server.Serve(FirstReplyProtocolRegression.JapaneseReply, true, false, "length");
            model.PostFirstReplyStream("完整回答这个普通问题。", _ => ++failedSpeech, value => { failedResult = value; ++failedDone; });
            yield return WaitUntil(() => failedDone != 0, "incomplete candidate completion");
            Check(server.Count == before + 1 && failedSpeech == 0 && failedResult?.Disposition == FirstReplyDisposition.Failed &&
                !failedResult.SpeechDelivered && !model.m_DataList.Exists(row => row.role == "assistant"),
                "An incomplete candidate is not reviewed, released or committed despite a valid say prefix.");
            yield return Await(broken.Finished, "incomplete candidate socket");
            cases.Add(new JObject { ["id"] = "incomplete-candidate", ["publicDeltas"] = failedSpeech });
            LastReport["status"] = "passed"; LastReport["httpRequests"] = server.Count;
        }
        finally
        {
            if (model != null) model.CancelActiveResponse();
            UnityEngine.Object.DestroyImmediate(host);
            LastReport["checks"] = checks;
            File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString(), new UTF8Encoding(false));
        }
    }

    private static JObject ControlledUnderstanding() => new JObject { ["referent"] = "此前实际助手说明", ["resolvedQuestion"] = "此前说明是什么意思？", ["needsEvidence"] = "prior-dialogue" };
    private static string Verdict(string route, string kind = null, bool? sufficient = null, string demand = null)
    {
        var value = new JObject { ["requestKind"] = kind ?? (route == "tools" ? "action" : "answer-only"),
            ["candidateSufficient"] = sufficient ?? route == "ordinary", ["route"] = route, ["reason"] = "Controlled fixture decision.",
            ["questionUnderstanding"] = route == "evidence" ? ControlledUnderstanding() : null };
        value["responseDemand"] = demand ?? (route == "evidence" ? "grounded-answer" : "short-conversation");
        return value.ToString(Formatting.None);
    }
    private static IEnumerator WaitUntil(Func<bool> done, string label)
    { float deadline = Time.realtimeSinceStartup + 10; while (!done()) { if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(label); yield return null; } }
    private static IEnumerator Await(Task task, string label)
    { yield return WaitUntil(() => task.IsCompleted, label); if (task.IsFaulted) throw task.Exception; }
    private sealed class Exchange
    {
        public readonly TaskCompletionSource<bool> Received = new TaskCompletionSource<bool>(), Release = new TaskCompletionSource<bool>();
        public Task Finished; public string RequestJson;
    }
    private sealed class Server : IDisposable
    {
        private readonly HttpListener listener = new HttpListener(); public string Url { get; } public int Count;
        public Server()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = "http://127.0.0.1:" + port + "/v1/chat/completions";
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
        }
        public Exchange Serve(string content, bool sse, bool held, string finish = "stop")
        {
            var exchange = new Exchange();
            exchange.Finished = Task.Run(async () => {
                var context = await listener.GetContextAsync(); ++Count;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) exchange.RequestJson = await reader.ReadToEndAsync();
                // Legacy socket fixtures control semantic judgments; bind their
                // synthetic proof to this supplied utterance. Grounding-specific
                // cases provide literal proof themselves, including invalid ones.
                if (!sse)
                    try {
                        var verdict = JObject.Parse(content);
                        if ((string)verdict["reason"] == "Controlled fixture decision." && verdict["currentTurnEvidence"] == null) {
                            var input = JObject.Parse((string)JObject.Parse(exchange.RequestJson)["messages"][1]["content"]);
                            string utterance = (string)input["currentUtterance"];
                            string kind = (string)verdict["requestKind"];
                            int split = Math.Max(1, utterance.Length / 2);
                            verdict["currentTurnEvidence"] = new JObject {
                                ["answerQuote"] = kind == "mixed" ? utterance.Substring(0, split) : null,
                                ["actionQuote"] = kind == "mixed" ? utterance.Substring(split) :
                                    kind == "action" || kind == "control" ? utterance : null };
                            content = verdict.ToString(Formatting.None);
                        }
                    } catch (JsonException) { }

                context.Response.StatusCode = 200; context.Response.ContentType = sse ? "text/event-stream" : "application/json";
                context.Response.SendChunked = sse;
                try
                {
                    if (sse)
                    {
                        byte[] delta = Encoding.UTF8.GetBytes("data: " + new JObject { ["choices"] = new JArray(new JObject {
                            ["index"] = 0, ["delta"] = new JObject { ["content"] = content } }) }.ToString(Formatting.None) + "\n\n");
                        await context.Response.OutputStream.WriteAsync(delta, 0, delta.Length); await context.Response.OutputStream.FlushAsync();
                    }
                    exchange.Received.TrySetResult(true); if (held) await exchange.Release.Task;
                    string final = sse ? "data: " + new JObject { ["choices"] = new JArray(new JObject {
                        ["index"] = 0, ["delta"] = new JObject(), ["finish_reason"] = finish }) }.ToString(Formatting.None) + "\n\ndata: [DONE]\n\n" :
                        new JObject { ["choices"] = new JArray(new JObject { ["finish_reason"] = finish,
                            ["message"] = new JObject { ["role"] = "assistant", ["content"] = content } }) }.ToString(Formatting.None);
                    byte[] body = Encoding.UTF8.GetBytes(final); await context.Response.OutputStream.WriteAsync(body, 0, body.Length);
                }
                catch (HttpListenerException) { if (!held) throw; }
                catch (IOException) { if (!held) throw; }
                finally { context.Response.Close(); }
            });
            return exchange;
        }
        public void Dispose() { listener.Stop(); listener.Close(); }
    }
}
