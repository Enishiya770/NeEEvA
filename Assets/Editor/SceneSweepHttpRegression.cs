using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Actual production room API over synthetic loopback HTTP. No model or scene action.</summary>
public static class SceneSweepHttpRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string CurrentImage = "data:image/png;base64,Q1VSUkVOVF9GSUNUVVJF";
    private const string None = "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}";
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(string output)
    {
        string directory = Path.Combine(output, "scene-sweep-http");
        Directory.CreateDirectory(directory);
        int checks = 0;
        var cases = new JArray();
        LastReport = new JObject { ["status"] = "running", ["cases"] = cases,
            ["scope"] = "Actual ChatQW sweep, dialogue-history and review API requests through synthetic loopback HTTP. Fixture images verify transport ownership only; no real LLM understanding, camera pixels, or execution is claimed." };
        var host = new GameObject("RoomSweepLoopbackFixture"); host.SetActive(false);
        ChatQW model = null;
        using (var server = new LoopbackServer())
        {
            try
            {
                model = host.AddComponent<ChatQW>(); model.enabled = false;
                model.m_Backend = ChatQW.BackendType.Local; model.m_LocalUrl = server.Url;
                model.m_PrewarmPrefixOnStart = false; model.m_LogRequestStats = false;
                model.m_DataList.Clear();
                var accepted = new LLM.SendData("user", "PRIVATE_PROVIDER_HISTORY_CANARY") { imageDataUrl = "data:image/png;base64,UFJJVkFURQ==" };
                model.m_DataList.Add(accepted);
                model.RequestContext = "PRIVATE_FORMAL_CONTEXT_CANARY";
                host.SetActive(true);
                typeof(LLM).GetField("url", Flags).SetValue(model, server.Url);
                Require(model.SupportsRoomObservationHistory &&
                    (string)typeof(LLM).GetField("url", Flags).GetValue(model) == server.Url,
                    "The actual provider supports observation history and targets only loopback.", ref checks);
                var input = new JObject { ["currentUserText"] = "左右看看，然后告诉我灯在哪里。", ["autonomous"] = false,
                    ["visualObservation"] = new JObject { ["captureId"] = "current-loopback", ["source"] = "character-scene-rgb" } };
                string source = input.ToString(Newtonsoft.Json.Formatting.None);
                var history = new List<LLM.RoomObservationImage> {
                    Frame("invalid-loopback", "data:image/png;base64,SU5WQUxJRA==", "{}"),
                    Frame("current-loopback", CurrentImage),
                    Frame("sweep-0", "data:image/png;base64,U1dFRVBfMA=="),
                    Frame("sweep-0", "data:image/png;base64,RFVQTElDQVRF"),
                    Frame("sweep-1", "data:image/png;base64,U1dFRVBfMQ=="),
                    Frame("sweep-2", "data:image/png;base64,U1dFRVBfMg=="),
                    Frame("sweep-3", "data:image/png;base64,U1dFRVBfMw=="),
                    Frame("sweep-4", "data:image/png;base64,U1dFRVBfNA==")
                };
                string[] expected = history.Where(x => x.CaptureId.StartsWith("sweep-", StringComparison.Ordinal))
                    .GroupBy(x => x.CaptureId).Take(4).Select(x => x.First().ImageDataUrl).Concat(new[] { CurrentImage }).ToArray();
                for (int sample = 0; sample < 3; ++sample)
                {
                    bool review = sample == 1, dialogue = sample == 2; bool? ok = null; string completed = null, status = null;
                    string requestSource = source;
                    IReadOnlyList<LLM.RoomObservationImage> requestHistory = history;
                    var dialogueFrame = Frame("dialogue-earlier", "data:image/png;base64,RElBTE9HVUU=");
                    if (dialogue)
                    {
                        var dialogueInput = (JObject)input.DeepClone();
                        dialogueInput["sceneSpatialMemory"] = new JObject { ["dialogueViews"] = new JArray(new JObject {
                            ["captureId"] = dialogueFrame.CaptureId, ["visualObservation"] = JObject.Parse(dialogueFrame.MetadataJson),
                            ["verifiedEntity"] = false, ["actionAuthorization"] = false }) };
                        requestSource = dialogueInput.ToString(Newtonsoft.Json.Formatting.None);
                        requestHistory = new[] { dialogueFrame };
                    }
                    string response = review ? "{\"verdict\":\"consistent\",\"reason\":\"synthetic-fixture\"}" : None;
                    var exchange = server.ServeOne(response);
                    Action<bool, string, string> callback = (acceptedResponse, value, reason) => { ok = acceptedResponse; completed = value; status = reason; };
                    if (review) model.PostRoomTaskMessage(requestSource, CurrentImage, true, callback);
                    else model.PostRoomTaskMessageWithObservationHistory(requestSource, CurrentImage, requestHistory, callback);
                    float deadline = Time.realtimeSinceStartup + 14f;
                    while (!ok.HasValue || !exchange.IsCompleted)
                    {
                        if (exchange.IsFaulted) throw exchange.Exception;
                        if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Room sweep loopback request " + sample);
                        yield return null;
                    }
                    Require(ok == true && completed == response && status == "received",
                        "The actual room API delivers exactly the complete bounded response.", ref checks);
                    var wire = JObject.Parse(exchange.Result); var messages = (JArray)wire["messages"];
                    string[] images = wire.SelectTokens("$..image_url.url").Select(x => (string)x).ToArray();
                    Require(images.SequenceEqual(review ? new[] { CurrentImage } : dialogue ? new[] { dialogueFrame.ImageDataUrl, CurrentImage } : expected),
                        "History images are deduplicated, validated, capped at four, and precede the independent current picture; review inherits none.", ref checks);
                    Require((bool?)wire["stream"] == false && (int?)wire["max_tokens"] == (review ? 240 : 640),
                        "The actual request retains its bounded nonstreaming decision/review contract.", ref checks);
                    Require(!exchange.Result.Contains("PRIVATE_") && model.m_DataList.Count == 1 && ReferenceEquals(model.m_DataList[0], accepted),
                        "Ephemeral sweep and review requests neither read nor mutate accepted provider history.", ref checks);
                    if (!review && !dialogue)
                    {
                        Require(messages.Count == 7 && (string)messages[messages.Count - 1]["role"] == "user" &&
                            messages[messages.Count - 1]["content"].Type == JTokenType.String &&
                            ((string)messages[messages.Count - 1]["content"]).Contains((string)input["currentUserText"]),
                            "The exact current user request remains the final authority after all visual evidence.", ref checks);
                        for (int at = 1; at <= 4; ++at)
                        {
                            string text = string.Join("\n", messages[at]["content"].Where(x => (string)x["type"] == "text").Select(x => (string)x["text"]));
                            Require((string)messages[at]["role"] == "user" && text.Contains("历史视野图像") && text.Contains("sweep-" + (at - 1)) &&
                                text.Contains("不能作为当前可见性") && text.Contains("character-scene-rgb"),
                                "Each prior image carries its own capture metadata and excludes current visibility authority.", ref checks);
                        }
                    }
                    else if (dialogue)
                    {
                        string label = string.Join("\n", messages[1]["content"].Where(x => (string)x["type"] == "text").Select(x => (string)x["text"]));
                        Require(label.Contains("historical-dialogue-view") && label.Contains("identityVerified") && label.Contains("false") &&
                            !label.Contains("historical-turn-sweep") && label.Contains("只可作为搜索线索"),
                            "The actual wire labels dialogue pixels as an unverified historical search hint, never a turn frame or verified object.", ref checks);
                    }
                    else Require(messages.Count == 2 && !exchange.Result.Contains("sweep-") && !exchange.Result.Contains("历史视野图像"),
                        "The next ordinary speech-review API has no persisted sweep state.", ref checks);
                    string file = "request-" + sample + (review ? "-review.json" : "-decision.json");
                    File.WriteAllText(Path.Combine(directory, file), wire.ToString() + "\n");
                    cases.Add(new JObject { ["kind"] = review ? "fresh-review-no-history" : dialogue ? "unverified-dialogue-history" : "bounded-sweep-decision",
                        ["file"] = file, ["images"] = images.Length, ["callbackStatus"] = status, ["historyCountAfter"] = model.m_DataList.Count });
                }
                LastReport["status"] = "passed";
            }
            finally
            {
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
                LastReport["checks"] = checks; LastReport["httpRequests"] = cases.Count;
                if ((string)LastReport["status"] == "running") LastReport["status"] = "failed";
                File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString() + "\n");
            }
        }
    }

    private static LLM.RoomObservationImage Frame(string id, string image, string metadata = null)
        => new LLM.RoomObservationImage(id, metadata ?? new JObject { ["captureId"] = id,
            ["source"] = "character-scene-rgb", ["state"] = "ready", ["hasImage"] = true }.ToString(Newtonsoft.Json.Formatting.None), image);

    private static void Require(bool condition, string message, ref int checks)
    { ++checks; if (!condition) throw new InvalidOperationException(message); }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        public string Url { get; }
        public LoopbackServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = "http://127.0.0.1:" + port + "/v1/chat/completions";
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
        }
        public Task<string> ServeOne(string response) => Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            string wire;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) wire = await reader.ReadToEndAsync();
            byte[] body = Encoding.UTF8.GetBytes(new JObject { ["choices"] = new JArray(new JObject {
                ["finish_reason"] = "stop", ["message"] = new JObject { ["role"] = "assistant", ["content"] = response } }) }.ToString(Newtonsoft.Json.Formatting.None));
            context.Response.StatusCode = 200; context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = body.Length;
            try { await context.Response.OutputStream.WriteAsync(body, 0, body.Length); }
            finally { context.Response.Close(); }
            return wire;
        });
        public void Dispose() { listener.Stop(); listener.Close(); }
    }
}
