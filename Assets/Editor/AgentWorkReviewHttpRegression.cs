using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Real UnityWebRequest against an isolated loopback fixture; never calls the configured LLM.</summary>
public static class AgentWorkReviewHttpRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static GameObject host;
    private static ChatQW model;
    private static LoopbackServer server;
    private static IEnumerator run;
    private static readonly Stack<IEnumerator> stack = new Stack<IEnumerator>();
    private static AsyncOperation waiting;
    private static double started;
    private static int checks;
    private static bool unexpectedPrivateLog;

    public static void RunBatch()
    {
        try
        {
            checks = 0; unexpectedPrivateLog = false;
            host = new GameObject("InactiveWorkReviewHttpFixture"); host.SetActive(false);
            model = host.AddComponent<ChatQW>();
            model.m_Backend = ChatQW.BackendType.Local;
            model.m_LogRequestStats = true;
            model.m_DataList.Add(new LLM.SendData("system", "Public review fixture"));
            model.m_DataList.Add(new LLM.SendData("user", "Check the first public clip."));
            server = new LoopbackServer();
            typeof(LLM).GetField("url", Flags).SetValue(model, server.Url);
            Application.logMessageReceived += CaptureLog;
            run = Exercise(); stack.Push(run);
            started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
        }
        catch (Exception error) { Finish(error); }
    }

    private static void Tick()
    {
        try
        {
            if (EditorApplication.timeSinceStartup - started > 45) throw new TimeoutException("Loopback work-review regression timed out");
            if (waiting != null)
            {
                if (!waiting.isDone) return;
                waiting = null;
            }
            if (stack.Count == 0) { Finish(null); return; }
            IEnumerator current = stack.Peek();
            if (!current.MoveNext()) { (current as IDisposable)?.Dispose(); stack.Pop(); return; }
            if (current.Current is IEnumerator nested) stack.Push(nested);
            else if (current.Current is AsyncOperation operation) waiting = operation;
        }
        catch (Exception error) { Finish(error); }
    }

    private static IEnumerator Exercise()
    {
        int historyCount = model.m_DataList.Count;
        int callbacks = 0;
        IEnumerator cancelled = Request(value => callbacks++);
        Check(cancelled.MoveNext() && cancelled.Current is AsyncOperation, "Production request did not start an HTTP operation");
        AsyncOperation first = (AsyncOperation)cancelled.Current;
        while (!server.FirstRequestRead) yield return null;
        model.CancelEphemeralMsg();
        server.ReleaseFirst();
        while (!first.isDone) yield return null;
        Check(!cancelled.MoveNext() && callbacks == 0, "Cancelled response invoked a stale callback");
        (cancelled as IDisposable)?.Dispose();

        string accepted = null;
        yield return Request(value => { callbacks++; accepted = value; });
        Check(callbacks == 1 && !string.IsNullOrWhiteSpace(accepted) && (string)JObject.Parse(accepted)["work_status"] == "continue",
            "Next request generation failed after cancellation");
        Check(typeof(ChatQW).GetField("m_EphemeralRequest", Flags).GetValue(model) == null, "Completed request still owns the auxiliary handle");

        string truncated = null;
        yield return Request(value => { callbacks++; truncated = value; });
        Check(callbacks == 2 && truncated == "", "HTTP success with finish_reason=length released an approval");

        string failed = null;
        yield return Request(value => { callbacks++; failed = value; });
        Check(callbacks == 3 && failed == "", "HTTP failure was treated as progress/approval");
        Check(model.m_DataList.Count == historyCount, "Review transport wrote ephemeral input/output into dialogue history");
        Check(!unexpectedPrivateLog, "Review diagnostics exposed model or HTTP response content");
        Check(server.RequestCount == 4, "Unexpected request retry or missing HTTP request");
        foreach (string raw in server.GetRequests())
        {
            var request = JObject.Parse(raw);
            Check((int)request["max_tokens"] == 1024 && request["response_format"]["schema"] != null,
                "Actual wire omitted the independent decision budget/schema");
            var messages = (JArray)request["messages"];
            Check((string)messages[messages.Count - 1]["content"] == "Public current work observation", "Actual wire rewrote the current review input");
        }
        yield return ExerciseFormalResponses();
        string brokenReview = null;
        yield return Request(value => brokenReview = value);
        Check(brokenReview == "", "HTTP-200 broken review transport still approved a decision from its partial body");
        int beforeMetadata = model.m_DataList.Count;
        string metadataOnly = null;
        var metadataDeltas = new List<string>();
        yield return FormalRequest(value => metadataOnly = value, metadataDeltas.Add);
        Check(metadataOnly == "" && metadataDeltas.Count == 0 && model.m_DataList.Count == beforeMetadata,
            "Metadata-only SSE response became a delivered silence or polluted assistant history");
        Check(server.RequestCount == 18, "Missing formal/review request or unexpected retry");
    }

    private static IEnumerator ExerciseFormalResponses()
    {
        int before = model.m_DataList.Count;
        string output = null; var deltas = new List<string>();
        yield return FormalRequest(value => output = value, deltas.Add);
        Check(output == "" && deltas.Count == 0 && model.m_DataList.Count == before,
            "HTTP-200 empty SSE content became silent/delivered or polluted assistant history");
        output = null;
        yield return FormalRequest(value => output = value, deltas.Add);
        Check(output == "<silent/>" && deltas.Contains("<silent/>") && model.m_DataList.Count == before + 1,
            "Explicit SSE silent decision was discarded");
        output = null;
        yield return FormalRequest(value => output = value, null);
        Check(output != null && output.Contains("<body_inspect scope=\"runtime\"/>"), "Pure SSE tool output was discarded");
        output = null;
        yield return FormalRequest(value => output = value, null);
        Check(output != null && output.Contains("I will check.") && !output.Contains("body_inspect"),
            "Length-truncated SSE response dispatched a tool or lost already generated speech");
        output = null;
        yield return FormalRequest(value => output = value, null);
        Check(output == "", "SSE HTTP failure became a delivered silent response");
        output = null;
        yield return FormalRequest(value => output = value, null);
        Check(output == "", "Length-truncated tool-only SSE response became a delivered silence");

        before = model.m_DataList.Count;
        foreach (string mode in new[] { "empty", "invalid-envelope", "http-failure" })
        {
            output = null;
            yield return model.Request("public fixture", value => output = value);
            Check(output == "" && model.m_DataList.Count == before, "Nonstream " + mode + " failed to report empty or polluted history");
        }
        output = null;
        yield return model.Request("public fixture", value => output = value);
        Check(output == "<silent/>", "Nonstream explicit silent decision was discarded");
        output = null;
        yield return model.Request("public fixture", value => output = value);
        Check(output != null && output.Contains("body_inspect"), "Nonstream pure tool output was discarded");
        output = null;
        yield return FormalRequest(value => output = value, null);
        Check(output == "", "HTTP-200 broken SSE transport still dispatched a tool from its partial body");
    }

    private static IEnumerator FormalRequest(Action<string> callback, Action<string> delta)
    {
        int generation = (int)typeof(ChatQW).GetField("m_StreamRequestGeneration", Flags).GetValue(model);
        return (IEnumerator)typeof(ChatQW).GetMethod("RequestStream", Flags).Invoke(model,
            new object[] { "", generation, delta, callback, true, "Public formal speech contract", null,
                "Public latest execution feedback" });
    }

    private static IEnumerator Request(Action<string> callback)
    {
        int generation = (int)typeof(ChatQW).GetField("m_EphemeralGeneration", Flags).GetValue(model);
        return (IEnumerator)typeof(ChatQW).GetMethod("RequestWorkReview", Flags).Invoke(model,
            new object[] { "Public current work observation", generation, callback });
    }

    private static void CaptureLog(string message, string trace, LogType type)
    {
        if (message.Contains("PRIVATE_WIRE_SENTINEL") || message.Contains("PRIVATE_HTTP_SENTINEL")) unexpectedPrivateLog = true;
    }

    private static void Finish(Exception error)
    {
        EditorApplication.update -= Tick;
        Application.logMessageReceived -= CaptureLog;
        if (model != null) model.CancelEphemeralMsg();
        if (model != null) model.CancelActiveResponse();
        server?.Dispose();
        while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
        waiting = null;
        if (host != null) UnityEngine.Object.DestroyImmediate(host);
        var report = new JObject {
            ["passed"] = error == null, ["checks"] = checks, ["checkedAtUtc"] = DateTime.UtcNow.ToString("o"),
            ["scope"] = "Production UnityWebRequest against loopback fixture: review cancellation/schema/privacy/history; formal SSE/nonstream empty vs explicit silent/tool output, truncation and HTTP failure. No real LLM or private scene."
        };
        if (error != null) { report["error"] = error.ToString(); Debug.LogException(error); }
        string path = Path.GetFullPath("Tools/MotionAdapter/reports/agent-work-review-http-regression.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, report.ToString() + "\n");
        Debug.Log("[AgentWorkReviewHttpRegression] passed=" + (error == null) + " checks=" + checks);
        EditorApplication.Exit(error == null ? 0 : 1);
    }

    private static void Check(bool passed, string message)
    {
        checks++;
        if (!passed) throw new InvalidOperationException(message);
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly ManualResetEventSlim releaseFirst = new ManualResetEventSlim();
        private readonly List<string> requests = new List<string>();
        private volatile bool stopped;
        private volatile bool firstRequestRead;
        public bool FirstRequestRead => firstRequestRead;
        public int RequestCount { get { lock (requests) return requests.Count; } }
        public string Url { get; }

        public LoopbackServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/v1/chat/completions";
            Task.Run((Action)Serve);
        }

        public string[] GetRequests() { lock (requests) return requests.ToArray(); }
        public void ReleaseFirst() => releaseFirst.Set();

        private void Serve()
        {
            for (int index = 0; index < 18 && !stopped; index++)
            {
                try
                {
                    using (TcpClient client = listener.AcceptTcpClient())
                    using (NetworkStream stream = client.GetStream())
                    {
                        client.ReceiveTimeout = 10000; client.SendTimeout = 10000;
                        string body = ReadRequest(stream);
                        lock (requests) requests.Add(body);
                        if (index == 0) { firstRequestRead = true; releaseFirst.Wait(15000); }
                        string content;
                        switch (index)
                        {
                            case 3: content = "PRIVATE_HTTP_SENTINEL"; break;
                            case 4: content = Sse("", "stop"); break;
                            case 5: content = Sse("<silent/>", "stop"); break;
                            case 6: content = Sse("<body_inspect scope=\"runtime\"/>", "stop"); break;
                            case 7: content = Sse("<lang code=\"en\"/>I will check.<body_inspect scope=\"runtime\"/>", "length"); break;
                            case 8: content = "Public fixture SSE failure"; break;
                            case 9: content = Sse("<body_inspect scope=\"runtime\"/>", "length"); break;
                            case 10: content = FormalEnvelope(" "); break;
                            case 11: content = "not a JSON envelope"; break;
                            case 12: content = "Public fixture nonstream failure"; break;
                            case 13: content = FormalEnvelope("<silent/>"); break;
                            case 14: content = FormalEnvelope("<body_inspect scope=\"runtime\"/>"); break;
                            case 15: content = Sse("<body_inspect scope=\"runtime\"/>", "stop"); break;
                            case 16: content = Response("stop"); break;
                            case 17: content = Sse("<lang code=\"ja\"/><say></say>", "stop"); break;
                            default: content = Response(index == 2 ? "length" : "stop"); break;
                        }
                        byte[] bytes = Encoding.UTF8.GetBytes(content);
                        string status = index == 3 || index == 8 || index == 12 ? "503 Service Unavailable" : "200 OK";
                        string type = (index >= 4 && index <= 9) || index == 15 || index == 17 ? "text/event-stream" : "application/json";
                        int length = bytes.Length + (index == 15 || index == 16 ? 17 : 0); // Force transport failure after valid-looking response data.
                        byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + length + "\r\nConnection: close\r\n\r\n");
                        stream.Write(header, 0, header.Length); stream.Write(bytes, 0, bytes.Length);
                    }
                }
                catch (Exception) { if (stopped) break; } // The cancelled socket is expected to close.
            }
        }

        private static string ReadRequest(NetworkStream stream)
        {
            var header = new List<byte>();
            while (header.Count < 65536)
            {
                int next = stream.ReadByte(); if (next < 0) throw new EndOfStreamException();
                header.Add((byte)next);
                int n = header.Count;
                if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10) break;
            }
            string text = Encoding.ASCII.GetString(header.ToArray());
            int length = 0;
            foreach (string line in text.Split('\n'))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line.Substring("Content-Length:".Length).Trim());
            if (length <= 0 || length > 2000000) throw new InvalidDataException("Missing or excessive fixture payload size");
            if (text.IndexOf("Expect: 100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                byte[] interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"); stream.Write(interim, 0, interim.Length);
            }
            var body = new byte[length]; int at = 0;
            while (at < length) { int count = stream.Read(body, at, length - at); if (count <= 0) throw new EndOfStreamException(); at += count; }
            return Encoding.UTF8.GetString(body);
        }

        private static string Response(string finish)
        {
            string decision = new JObject {
                ["work_evidence"] = "PRIVATE_WIRE_SENTINEL: the requested check has not run.",
                ["work_status"] = "continue", ["proceed"] = true, ["intent"] = "Run the allowed check.",
                ["singing_goal_status"] = "none", ["singing_goal_evidence"] = "",
                ["singing_goal_expected"] = new JObject {
                    ["origin"] = "none", ["request_quote"] = "",
                    ["refs"] = "", ["range"] = "none", ["start_seconds"] = null, ["end_seconds"] = null
                },
                ["wait_seconds"] = 5
            }.ToString(Newtonsoft.Json.Formatting.None);
            return new JObject { ["choices"] = new JArray(new JObject {
                ["finish_reason"] = finish, ["message"] = new JObject { ["content"] = decision }
            }) }.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string Sse(string content, string finish)
        {
            return "data: " + new JObject { ["choices"] = new JArray(new JObject {
                ["finish_reason"] = finish, ["delta"] = new JObject { ["content"] = content }
            }) }.ToString(Newtonsoft.Json.Formatting.None) + "\n\ndata: [DONE]\n\n";
        }

        private static string FormalEnvelope(string content) => new JObject {
            ["choices"] = new JArray(new JObject { ["finish_reason"] = "stop", ["message"] = new JObject { ["content"] = content } })
        }.ToString(Newtonsoft.Json.Formatting.None);

        public void Dispose()
        {
            stopped = true; releaseFirst.Set(); listener.Stop();
        }
    }
}
