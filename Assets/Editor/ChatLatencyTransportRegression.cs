using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Exercises the production SSE byte handler and final request serializer. All
// inputs are synthetic; no network, microphone, TTS, scene or model calls.
public static class ChatLatencyTransportRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static JObject PublicBoundaryHttpReport { get; private set; }
    public static JObject FeedbackStageHttpReport { get; private set; }

    [MenuItem("Tools/NeEEvA/Validate chat latency transport")]
    public static void RunInteractive()
    {
        checks = 0;
        CheckStreamingPerformance();
        CheckStableDialoguePrefix();
        CheckStagedObservations();
        CheckSceneFactRequestSharing();
        CheckFormalResponseStages();
        CheckFeedbackProviderCompatibility();
        checks += SceneFactCompactionRegression.RunChecks();
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
        const string text = "<lang code=\"ja\"/><speech mode=\"independent\"/><say>青い空、こんにちは。</say>";
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

    // Explicit opt-in by the isolated integration fixture. Only synthetic replies
    // are served on an ephemeral loopback port; no Qwen, TTS or microphone is used.
    // Unlike parser tests this exercises the public production formal and feedback
    // entrypoints, request contract, native UnityWebRequest, SSE and completion.
    public static IEnumerator RunPublicBoundaryHttpChecks(string output)
    {
        var observations = new JArray();
        var host = new GameObject("PublicBoundaryLoopbackFixture"); host.SetActive(false);
        ChatQW model = null;
        int before = checks;
        using (var server = new PublicBoundaryLoopbackServer())
        {
            try
            {
                model = host.AddComponent<ChatQW>();
                model.enabled = false; // Suppress Start/slot initialization while public coroutines remain callable.
                model.m_Backend = ChatQW.BackendType.Local;
                model.m_LocalUrl = server.Url;
                model.m_PrewarmPrefixOnStart = false;
                model.m_LogRequestStats = false;
                typeof(LLM).GetField("url", Flags).SetValue(model, server.Url);
                host.SetActive(true);
                // Awake may assign the endpoint, but it can only assign this same loopback URL.
                Check((string)typeof(LLM).GetField("url", Flags).GetValue(model) == server.Url,
                    "Isolated transport selected a non-loopback provider.");
                const string valid = "<lang code=\"ja\"/><speech mode=\"independent\"/><say>今の状態を確認しているよ。</say><motion name=\"nod\"/>";
                const string barePreamble = "ユーザーは二つの録音を連結したい。先に内部手順を確認する。";
                var outputs = new Dictionary<string, string> {
                    { "naked-protocol", "lang code=ja\n\nspeech mode=after_action\n\n確認しているよ。<sing refs=\"clip:fixture\"/>" },
                    { "unmarked-reasoning", barePreamble + "\n" + valid },
                    { "public-prefix-then-invalid-tail", valid + "裸の内部記録<continue/>" },
                    { "valid-public-action", valid }
                };
                foreach (bool feedback in new[] { false, true })
                foreach (bool typedSpeech in new[] { false, true })
                foreach (var sample in outputs)
                {
                    model.CancelActiveResponse();
                    model.m_DataList.Clear();
                    model.m_DataList.Add(new LLM.SendData("system", "Synthetic persona: speak Japanese."));
                    model.m_DataList.Add(new LLM.SendData("user", "Keep the two-part goal; verify the previous result."));
                    const string facts = "[fixture current facts] original_goal=part1_then_part2 actual_playback=none";
                    var parts = new List<SpeechText>();
                    var plain = new StringBuilder();
                    var errors = new List<string>();
                    string complete = null;
                    Action<string> errorSink = e => errors.Add(e);
                    model.OnOutputFormatError += errorSink;
                    var exchange = server.ServeOne(sample.Value);
                    try
                    {
                        if (feedback)
                        {
                            if (typedSpeech) model.PostSpeechFeedbackStream(facts, "[actual result] action not yet submitted", parts.Add, value => complete = value);
                            else model.PostContinuationStream(facts, value => plain.Append(value), value => complete = value);
                        }
                        else
                        {
                            model.RequestContext = facts;
                            if (typedSpeech) model.PostSpeechStream("Please explain the current result.", parts.Add, value => complete = value);
                            else model.PostMsgStream("Please explain the current result.", value => plain.Append(value), value => complete = value);
                        }
                        float deadline = Time.realtimeSinceStartup + 12f;
                        while (complete == null || !exchange.IsCompleted)
                        {
                            if (exchange.IsFaulted) throw exchange.Exception;
                            if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Loopback public-output transport: " + sample.Key);
                            yield return null;
                        }
                        string wire = exchange.Result;
                        bool hasContract = false;
                        foreach (JObject message in (JArray)JObject.Parse(wire)["messages"])
                            if (message["content"].Type == JTokenType.String &&
                                ((string)message["content"]).Contains(RoleOutputPublicBoundary.OutputContract)) hasContract = true;
                        Check(hasContract,
                            "Actual formal/feedback request omitted the explicit say contract.");
                        Check(wire.Contains("original_goal=part1_then_part2") && wire.Contains("actual_playback=none"),
                            "Public contract injection removed current task/evidence.");
                        var emitted = new StringBuilder(); foreach (SpeechText part in parts) emitted.Append(part.Text);
                        emitted.Append(plain);
                        bool absorbedPreambleCase = sample.Key == "unmarked-reasoning";
                        bool validCase = sample.Key == "valid-public-action" || absorbedPreambleCase;
                        bool publicPrefixCase = sample.Key == "public-prefix-then-invalid-tail";
                        if (validCase)
                        {
                            Check(emitted.ToString() == "今の状態を確認しているよ。" && errors.Count == 0 &&
                                complete.Contains("<motion name=\"nod\"/>") && !complete.Contains("<say"),
                                "Valid public speech or validated tool projection changed through actual transport.");
                            if (typedSpeech) Check(parts.TrueForAll(p => p.LanguageCode == "ja" &&
                                p.ActionDependency == SpeechActionDependency.Independent), "Transport lost language/phase metadata.");
                            if (absorbedPreambleCase)
                                Check(!emitted.ToString().Contains(barePreamble) && !complete.Contains(barePreamble) &&
                                    model.m_DataList.Exists(m => m.role == "system" && m.content.Contains("[public_output_boundary/preamble]")),
                                    "Rescued preamble reached the public stream/actions or lost its non-failure shape observation.");
                        }
                        else
                        {
                            Check(emitted.ToString() == (publicPrefixCase ? "今の状態を確認しているよ。" : "") && complete == "" && errors.Count == 1 &&
                                errors[0].Contains("[public_output_boundary]"),
                                "Malformed model output reached speech or executable completion through actual transport.");
                            Check(!model.m_DataList.Exists(m => m.role == "assistant") &&
                                model.m_DataList.Exists(m => m.role == "system" && m.content.Contains("[public_output_boundary]")),
                                "Malformed response became accepted history or lost its structured correction fact.");
                            string errorFact = model.m_DataList.Find(m => m.role == "system" && m.content.Contains("[public_output_boundary]")).content;
                            if (publicPrefixCase)
                            {
                                var receipt = ReadPublicReceipt(errorFact);
                                Check((string)receipt["text"] == emitted.ToString() &&
                                    (string)receipt["delivery"] == "public-stream-callback" &&
                                    (string)receipt["playback"] == "not-observed-here" &&
                                    (long)receipt["charactersSent"] == emitted.Length &&
                                    errors[0].Contains("[public_stream_prefix]") && !errorFact.Contains("裸の内部記録") &&
                                    !errorFact.Contains("<motion") && !errorFact.Contains("<continue"),
                                    "Correction lost an emitted prefix, claimed heard audio or retained the invalid tail/actions.");
                            }
                            else Check(!errorFact.Contains("[public_stream_prefix]"), "Undelivered parser content was reported as a public stream prefix.");
                        }
                        observations.Add(new JObject { ["case"] = sample.Key, ["request"] = feedback ? (typedSpeech ? "work-feedback" : "continuation") : "formal",
                            ["callback"] = typedSpeech ? "SpeechText" : "string", ["spokenCharacters"] = emitted.Length,
                            ["formatErrors"] = errors.Count, ["projectedActions"] = complete.Contains("<motion"),
                            ["absorbedPreambleCase"] = absorbedPreambleCase,
                            ["rawAssistantHistoryContainsPreamble"] = absorbedPreambleCase &&
                                model.m_DataList.Exists(m => m.role == "assistant" && m.content.Contains(barePreamble)),
                            ["passed"] = true });
                    }
                    finally { model.OnOutputFormatError -= errorSink; model.CancelActiveResponse(); }
                }
                model.m_DataList.Clear();
                model.m_DataList.Add(new LLM.SendData("system", "Synthetic persona."));
                string noCallbackComplete = null;
                var noCallbackExchange = server.ServeOne(valid + "裸の内部記録<continue/>");
                model.PostMsgStream("Completion-only consumer.", null, value => noCallbackComplete = value);
                float noCallbackDeadline = Time.realtimeSinceStartup + 12f;
                while (noCallbackComplete == null || !noCallbackExchange.IsCompleted)
                {
                    if (noCallbackExchange.IsFaulted) throw noCallbackExchange.Exception;
                    if (Time.realtimeSinceStartup > noCallbackDeadline) throw new TimeoutException("Completion-only public boundary transport.");
                    yield return null;
                }
                Check(noCallbackComplete == "" &&
                    !model.m_DataList.Exists(m => m.content.Contains("[public_stream_prefix]")),
                    "A stream without a public delta consumer claimed the parsed prefix was delivered.");
                observations.Add(new JObject { ["case"] = "parsed-prefix-without-public-callback", ["request"] = "formal",
                    ["callback"] = "completion-only", ["spokenCharacters"] = 0, ["projectedActions"] = false, ["passed"] = true });
                // Completion-only/nonstream parsing of an identical valid prefix
                // is not a callback delivery; it must not invent an audible prefix.
                model.m_DataList.Clear();
                var nonstream = RoleOutputChannels.Parse(valid + "裸の内部記録<continue/>",
                    RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
                typeof(ChatQW).GetMethod("ReportRoleOutputFormatFacts", Flags).Invoke(model, new object[] { nonstream, true });
                Check(nonstream.HasSpeech && model.m_DataList.Exists(m => m.content.Contains("[public_output_boundary]")) &&
                    !model.m_DataList.Exists(m => m.content.Contains("[public_stream_prefix]")),
                    "Nonstream parse falsely claimed public callback delivery.");
                CheckBoundedPublicReceipt();
                PublicBoundaryHttpReport = new JObject { ["passed"] = true, ["checks"] = checks - before,
                    ["loopbackRequests"] = observations.Count, ["cases"] = observations,
                    ["scope"] = "Production ChatQW public entrypoints → ephemeral loopback UnityWebRequest/SSE → speech callbacks/executable projection. Absorbed preambles stay out of public speech/actions; existing General raw assistant history is observed, not sanitized by this test. Spatial unapproved-candidate history isolation is covered by the separate feedback-stage test. Synthetic payloads, no real model, TTS or scene actions." };
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, "public-boundary-http.json"), PublicBoundaryHttpReport.ToString(), new UTF8Encoding(false));
                }
            }
            finally
            {
                if (model != null) model.CancelActiveResponse();
                UnityEngine.Object.DestroyImmediate(host);
            }
        }
    }

    // Tests the actual feedback entrypoint, not merely the serializer with a stage
    // supplied by the test. Consecutive requests share one provider and accepted
    // user turn; only the explicit request stage may change its skill projection.
    public static IEnumerator RunFeedbackStageHttpChecks(string output)
    {
        var observations = new JArray();
        var host = new GameObject("FeedbackStageLoopbackFixture"); host.SetActive(false);
        ChatQW model = null; TextAsset singing = null;
        int before = checks;
        using (var server = new PublicBoundaryLoopbackServer())
        {
            try
            {
                model = host.AddComponent<ChatQW>(); model.enabled = false;
                model.m_Backend = ChatQW.BackendType.Local; model.m_LocalUrl = server.Url;
                model.m_PrewarmPrefixOnStart = false; model.m_LogRequestStats = false;
                typeof(LLM).GetField("url", Flags).SetValue(model, server.Url);
                singing = new TextAsset(File.ReadAllText("Assets/AIChatTookit/Prompts/Skills/singing.txt")) { name = "singing" };
                typeof(LLM).GetField("m_SkillFiles", Flags).SetValue(model, new[] { singing });
                model.SkillCatalogContext = "可唱歌、观察和操作已登记设备。"; model.SetActiveSkills("singing");
                string active = model.ActiveSkillContext;
                const string original = "靠近那盏灯后关掉它；刚才的录音已经唱完，不要重复。";
                const string image = "data:image/jpeg;base64,Zml4dHVyZQ==";
                var user = new LLM.SendData("user", original);
                model.m_DataList.Add(new LLM.SendData("system", "Synthetic persona: speak Chinese."));
                model.m_DataList.Add(user);
                model.m_DataList.Add(new LLM.SendData("assistant", "我正在靠近灯。"));
                host.SetActive(true);
                Check((string)typeof(LLM).GetField("url", Flags).GetValue(model) == server.Url,
                    "Feedback-stage fixture selected a non-loopback provider");
                const string response = "<lang code=\"zh\"/><speech mode=\"independent\"/><say>已经靠近，关灯还没有执行。</say>";
                // Legacy five/six-argument entrypoints must still be General. The
                // first spatial candidate is unrecorded; a later accepted reply
                // proves recordAssistantHistory=true is not lost by stage routing.
                for (int sample = 0; sample < 5; ++sample)
                {
                    bool spatial = sample == 1 || sample == 4;
                    bool record = sample == 0 || sample == 4;
                    var stage = spatial ? FormalResponseStage.CommittedSpatialReply : FormalResponseStage.General;
                    string context = "[fixture current observation] capture=feedback-" + sample;
                    string feedback = "[fixture actual execution] actionId=approach-7 phase=arrived lightOn=true switchSubmitted=false result-" + sample;
                    var parts = new List<SpeechText>(); string completed = null;
                    int acceptedBefore = model.m_DataList.Count;
                    var exchange = server.ServeOne(response);
                    if (sample == 0)
                        model.PostSpeechFeedbackStream(context, feedback, parts.Add, value => completed = value, image);
                    else if (sample == 3)
                        model.PostSpeechFeedbackStream(context, feedback, parts.Add, value => completed = value, image, false);
                    else
                        model.PostSpeechFeedbackStreamForStage(context, feedback, parts.Add, value => completed = value, image, record, stage);
                    float deadline = Time.realtimeSinceStartup + 12f;
                    while (completed == null || !exchange.IsCompleted)
                    {
                        if (exchange.IsFaulted) throw exchange.Exception;
                        if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Feedback-stage loopback request " + sample);
                        yield return null;
                    }
                    var request = JObject.Parse(exchange.Result); var messages = (JArray)request["messages"];
                    string skill = (string)messages[1]["content"];
                    Check(spatial ? skill.StartsWith(FormalResponseContextPolicy.SpatialSkillScope, StringComparison.Ordinal) &&
                        !skill.Contains("<sing_goal") : skill == active,
                        "Actual feedback entrypoint dropped its explicit stage or leaked spatial projection into General");
                    int feedbackIndex = messages.Count - (spatial ? 2 : 1);
                    Check((string)messages[feedbackIndex]["role"] == "system" &&
                        (string)messages[feedbackIndex]["content"] == feedback &&
                        (!spatial || ((string)messages[messages.Count - 1]["role"] == "user" &&
                            (string)messages[messages.Count - 1]["content"] == CommittedSpatialReplyProtocol.BuildFinalWritingInstruction(original))) &&
                        exchange.Result.Contains(original) && exchange.Result.Contains(image) && exchange.Result.Contains(context),
                        "Feedback-stage transport changed original evidence or detached its result-writing instruction from the latest execution fact");
                    bool hasPublicContract = false;
                    foreach (JObject message in messages)
                        hasPublicContract |= (string)message["role"] == "system" && message["content"]?.Type == JTokenType.String &&
                            ((string)message["content"]).Contains(spatial
                                ? CommittedSpatialReplyProtocol.OutputContract : RoleOutputPublicBoundary.OutputContract);
                    Check(hasPublicContract,
                        "Feedback-stage decoded system messages omitted the public boundary contract; case=" + sample);
                    Check(parts.Count > 0 && parts.TrueForAll(part => part.LanguageCode == "zh"),
                        "Feedback-stage typed language changed; case=" + sample + " parts=" + parts.Count +
                        " languages=" + BoundedFixtureValue(string.Join(",", parts.ConvertAll(part => part.LanguageCode ?? "<null>"))));
                    Check(completed == "<speech mode=\"independent\"/>已经靠近，关灯还没有执行。",
                        "Feedback-stage completion projection changed; case=" + sample + " actual=" + BoundedFixtureValue(completed));
                    Check(model.m_DataList.Count == acceptedBefore + (record ? 1 : 0) &&
                        model.m_DataList.FindAll(message => message.role == "user").Count == 1 &&
                        ReferenceEquals(model.m_DataList[1], user) && user.content == original &&
                        !model.m_DataList.Exists(message => message.content.Contains("fixture actual execution") || message.content.Contains("fixture current observation")),
                        "Feedback stage wrote an unaudited candidate, fake user or transient execution evidence to history");
                    Check(model.ActiveSkillContext == active && (sample == 0 || !exchange.Result.Contains("result-" + (sample - 1))),
                        "Feedback request stage/results persisted in the shared provider");
                    observations.Add(new JObject { ["case"] = sample, ["stage"] = stage.ToString(),
                        ["entrypoint"] = sample == 0 ? "legacy-five-argument" : sample == 3 ? "legacy-six-argument" : "explicit-stage",
                        ["recordAssistantHistory"] = record, ["acceptedHistoryDelta"] = model.m_DataList.Count - acceptedBefore,
                        ["projectedSkillCharacters"] = skill.Length, ["passed"] = true });
                }
                FeedbackStageHttpReport = new JObject { ["passed"] = true, ["checks"] = checks - before,
                    ["loopbackRequests"] = observations.Count, ["cases"] = observations,
                    ["scope"] = "Actual per-request feedback stage through ChatQW public entrypoints, UnityWebRequest and SSE. Shared provider, General/spatial/General sequence, exact facts and candidate-history control; synthetic loopback only." };
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, "feedback-stage-http.json"), FeedbackStageHttpReport.ToString(), new UTF8Encoding(false));
                }
            }
            finally
            {
                if (model != null) model.CancelActiveResponse();
                UnityEngine.Object.DestroyImmediate(host);
                if (singing != null) UnityEngine.Object.DestroyImmediate(singing);
            }
        }
    }

    private static JObject ReadPublicReceipt(string fact)
    {
        const string marker = "[public_stream_prefix] ";
        foreach (string line in fact.Split('\n'))
            if (line.StartsWith(marker, StringComparison.Ordinal)) return JObject.Parse(line.Substring(marker.Length));
        throw new InvalidOperationException("Missing structured public stream receipt.");
    }

    private static string BoundedFixtureValue(string value)
    {
        if (value == null) return "<null>";
        return value.Length <= 160 ? value : value.Substring(0, 160) + "…";
    }

    private static void CheckBoundedPublicReceipt()
    {
        var type = typeof(ChatQW).GetNestedType("PublicSpeechStreamReceipt", Flags);
        object receipt = Activator.CreateInstance(type, true);
        type.GetMethod("Record", Flags).Invoke(receipt, new object[] { new string('あ', 1023) + "😀suffix", SpeechActionDependency.AfterAction });
        type.GetMethod("Record", Flags).Invoke(receipt, new object[] { new string('い', 2000), SpeechActionDependency.AfterAction });
        string description = (string)type.GetMethod("Describe", Flags).Invoke(receipt, null);
        JObject fact = ReadPublicReceipt(description);
        string text = (string)fact["text"];
        Check(text.Length <= 1024 && !char.IsHighSurrogate(text[text.Length - 1]) &&
            (bool)fact["prefixTruncated"] && (long)fact["charactersSent"] > 3000 &&
            (string)fact["speechDependencies"][0] == "AfterAction" &&
            (string)fact["playback"] == "not-observed-here", "Public receipt grew unbounded, split UTF-16 or claimed dependent speech was heard.");
    }

    private sealed class PublicBoundaryLoopbackServer : IDisposable
    {
        private readonly HttpListener listener = new HttpListener();
        public string Url { get; }
        public PublicBoundaryLoopbackServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = "http://127.0.0.1:" + port + "/v1/chat/completions";
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
        }
        public Task<string> ServeOne(string output) => Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            string wire;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) wire = await reader.ReadToEndAsync();
            context.Response.StatusCode = 200; context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;
            try
            {
                // Semantic chunks are intentionally tiny. SSE boundaries also cut
                // across protocol fields and multibyte public text.
                for (int at = 0; at < output.Length; at += 3)
                {
                    var payload = new JObject { ["choices"] = new JArray(new JObject {
                        ["delta"] = new JObject { ["content"] = output.Substring(at, Math.Min(3, output.Length - at)) } }) };
                    byte[] bytes = Encoding.UTF8.GetBytes("data: " + payload.ToString(Newtonsoft.Json.Formatting.None) + "\n\n");
                    await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    await context.Response.OutputStream.FlushAsync();
                }
                byte[] done = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
                await context.Response.OutputStream.WriteAsync(done, 0, done.Length);
            }
            finally { context.Response.Close(); }
            return wire;
        });
        public void Dispose() { listener.Stop(); listener.Close(); }
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
            var persona = new LLM.SendData("system", "Stable persona");
            var originalUser = new LLM.SendData("user", mixed);
            model.m_DataList.Add(persona); model.m_DataList.Add(originalUser);
            const string stableSkill = "Stable selected contract";
            model.ActiveSkillContext = stableSkill;
            JObject first = Request(model, "[感知帧 10:00:00] current facts");
            model.m_DataList.Add(new LLM.SendData("assistant", "听到了。"));
            model.m_DataList.Add(new LLM.SendData("user", "最近天气很好。"));
            JObject second = Request(model, "[感知帧 10:00:05] fresh facts");
            var messages = (JArray)second["messages"];
            var firstMessages = (JArray)first["messages"];
            var firstDialogue = new List<JObject>(); var dialogue = new List<JObject>();
            foreach (JObject message in firstMessages)
                if ((string)message["role"] == "user" || (string)message["role"] == "assistant") firstDialogue.Add(message);
            foreach (JObject message in messages)
                if ((string)message["role"] == "user" || (string)message["role"] == "assistant") dialogue.Add(message);
            Check((string)firstMessages[0]["content"] == persona.content &&
                (string)messages[0]["content"] == persona.content, "Stable system content changed");
            Check(firstDialogue.Count == 1 && (string)firstDialogue[0]["role"] == "user" &&
                (string)firstDialogue[0]["content"] == mixed && dialogue.Count == 3 &&
                (string)dialogue[0]["role"] == "user" && (string)dialogue[0]["content"] == mixed &&
                (string)dialogue[1]["role"] == "assistant" && (string)dialogue[1]["content"] == "听到了。" &&
                (string)dialogue[2]["role"] == "user" && (string)dialogue[2]["content"] == "最近天气很好。" &&
                ReferenceEquals(model.m_DataList[1], originalUser) && originalUser.content == mixed,
                "Previous mixed transcript, lyrics, tail request or accepted dialogue order were changed for caching");
            int firstDialogueAt = firstMessages.IndexOf(firstDialogue[0]), dialogueAt = messages.IndexOf(dialogue[0]);
            int firstSkillAt = -1, skillAt = -1;
            for (int i = 0; i < firstMessages.Count; ++i)
                if ((string)firstMessages[i]["role"] == "system" && (string)firstMessages[i]["content"] == stableSkill) firstSkillAt = i;
            for (int i = 0; i < messages.Count; ++i)
                if ((string)messages[i]["role"] == "system" && (string)messages[i]["content"] == stableSkill) skillAt = i;
            bool stablePrefix = firstSkillAt >= 0 && firstSkillAt == skillAt && firstSkillAt < firstDialogueAt && skillAt < dialogueAt;
            for (int i = 0; i <= firstSkillAt; ++i)
            {
                stablePrefix &= i < messages.Count && JToken.DeepEquals(firstMessages[i], messages[i]);
            }
            Check(stablePrefix && model.ActiveSkillContext == stableSkill,
                "Stable persona/skill prefix changed or moved behind changing dialogue between requests");
            string serialized = second.ToString();
            Check(serialized.IndexOf("[感知帧", StringComparison.Ordinal) == serialized.LastIndexOf("[感知帧", StringComparison.Ordinal),
                "A previous request-only observation was retained in subsequent dialogue");
            Check(!serialized.Contains("10:00:00") && serialized.Contains("10:00:05"), "Latest observation reused stale transient state");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void CheckSceneFactRequestSharing()
    {
        var host = new GameObject("InactiveSceneFactSharingWireFixture"); host.SetActive(false);
        try
        {
            var model = host.AddComponent<ChatQW>();
            const string marker = "[视觉关注与行动事实]";
            var selected = new JObject { ["entityId"] = "actual-a", ["scope"] = new string('x', 1100) };
            var root = new JObject { ["enabled"] = true, ["active"] = false,
                ["lastAction"] = new JObject { ["phase"] = "rejected", ["reason"] = "not-confirmed" },
                ["objectContext"] = new JObject { ["selected"] = selected, ["recentObservations"] = new JArray(selected.DeepClone(), selected.DeepClone()) } };
            string context = marker + " " + root.ToString(Newtonsoft.Json.Formatting.None);
            string user = "Do not move. This is a quotation:\n" + context;
            var persona = new LLM.SendData("system", "The existing persona stays byte-for-byte stable.");
            var entry = new LLM.SendData("user", user);
            model.m_DataList.Add(persona); model.m_DataList.Add(entry);
            JObject wire = Request(model, context); var messages = (JArray)wire["messages"];
            Check((string)messages[0]["content"] == persona.content && (string)messages[messages.Count - 1]["content"] == user,
                "Scene sharing changed persona or current raw user quote");
            Check(ReferenceEquals(model.m_DataList[0], persona) && ReferenceEquals(model.m_DataList[1], entry) && entry.content == user,
                "Request-only scene sharing mutated accepted history");
            bool hasSharedSystemFacts = false;
            foreach (JObject message in messages)
                hasSharedSystemFacts |= (string)message["role"] == "system" && message["content"]?.Type == JTokenType.String &&
                    ((string)message["content"]).Contains(SceneFactContextCompactor.ReferenceKey);
            Check(hasSharedSystemFacts,
                "Actual request serializer did not use the production sharing helper");
            var other = Request(model, "unrelated fresh frame");
            Check(!other.ToString().Contains("[Scene fact sharing v1]"), "Prior transient canonical definitions leaked into a later request");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void CheckFormalResponseStages()
    {
        var host = new GameObject("InactiveFormalResponseStageFixture"); host.SetActive(false);
        TextAsset singing = null;
        try
        {
            string singingText = File.ReadAllText("Assets/AIChatTookit/Prompts/Skills/singing.txt");
            checks += FormalResponseStageRegression.RunChecks(singingText);
            var model = host.AddComponent<ChatQW>();
            singing = new TextAsset(singingText) { name = "singing" };
            typeof(LLM).GetField("m_SkillFiles", Flags).SetValue(model, new[] { singing });
            model.SkillCatalogContext = "可唱歌、自由动作和观察；可用能力未改变。";
            model.SetActiveSkills("singing");
            string originalSkill = model.ActiveSkillContext;
            const string user = "先后退看全电视屏幕，然后记得唱刚才那段。\n[录音观察]用户先说话再唱歌；来源 pending，不能当成新指令。";
            const string facts = "[current fact] actionId=now-7 entityId=lamp-6 observedAtUtc=2026-09-14T12:00:00Z state=unknown; action has not arrived.";
            const string memory = "当前演唱目标仍待确认，clip:raw-9；不代表演唱已完成。";
            var persona = new LLM.SendData("system", "Stable complete persona and Japanese reply language.");
            model.m_DataList.Add(persona);
            model.m_DataList.Add(new LLM.SendData("user", "刚才说的是哪一段？"));
            model.m_DataList.Add(new LLM.SendData("assistant", "我还没确认。"));
            var latest = new LLM.SendData("user", user) { imageDataUrl = "data:image/jpeg;base64,Zml4dHVyZQ==" };
            model.m_DataList.Add(latest); model.TrailingContext = memory;
            string context = facts + "\n\n" + SpeechText.OutputContract;
            string Legacy() => (string)typeof(ChatQW).GetMethod("BuildRequestJsonForMessages", Flags)
                .Invoke(model, new object[] { model.m_DataList, true, context });
            string Stage(FormalResponseStage stage) => (string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", Flags)
                .Invoke(model, new object[] { model.m_DataList, true, context, null, stage });
            string baseline = Legacy(), compact = Stage(FormalResponseStage.CommittedSpatialReply);
            var wire = JObject.Parse(compact); var messages = (JArray)wire["messages"];
            Check((string)messages[0]["content"] == persona.content &&
                ((string)messages[1]["content"]).StartsWith(FormalResponseContextPolicy.SpatialSkillScope, StringComparison.Ordinal),
                "Committed stage changed persona or placed stable skill scope after changing dialogue");
            int imageOwnerCount = 0; bool fullImageUserEvidence = false, hasSpeechContract = false;
            foreach (JObject message in messages)
            {
                if ((string)message["role"] == "system" && message["content"]?.Type == JTokenType.String)
                    hasSpeechContract |= ((string)message["content"]).Contains(SpeechText.OutputContract);
                if ((string)message["role"] != "user" || !(message["content"] is JArray parts)) continue;
                bool ownsImage = false, ownsWords = false;
                foreach (JObject part in parts)
                {
                    ownsImage |= (string)part["type"] == "image_url" && (string)part["image_url"]?["url"] == latest.imageDataUrl;
                    ownsWords |= (string)part["type"] == "text" && ((string)part["text"] ?? "").Contains(user);
                }
                if (ownsImage) { imageOwnerCount++; fullImageUserEvidence |= ownsWords; }
            }
            Check(imageOwnerCount == 1 && fullImageUserEvidence,
                "Typed stage detached full user/audio evidence from its unique actual image owner");
            Check(compact.Contains(facts) && compact.Contains(memory) && compact.Contains("action has not arrived"),
                "Typed stage removed exact current identity, timestamp, unknown or pending work");
            Check(hasSpeechContract,
                "Typed stage dropped production speech language/channel contract");
            Check((string)messages[messages.Count - 1]["role"] == "user" &&
                (string)messages[messages.Count - 1]["content"] == CommittedSpatialReplyProtocol.BuildFinalWritingInstruction(user),
                "Committed stage lost its wire-only final result-writing instruction");
            Check(!compact.Contains("<sing_goal") && compact.Length < baseline.Length &&
                Stage(FormalResponseStage.General) == baseline && Legacy() == baseline && model.ActiveSkillContext == originalSkill,
                "Stage scope leaked across consecutive shared-provider requests or mutated capability state");
            string projected = FormalResponseContextPolicy.SkillContext(FormalResponseStage.CommittedSpatialReply, originalSkill, model.SkillCatalogContext);
            int oldEstimate = (int)typeof(ChatQW).GetMethod("EstimateRequestTokens", Flags).Invoke(model, new object[] { model.m_DataList, context });
            int newEstimate = (int)typeof(ChatQW).GetMethod("EstimateStageRequestTokens", Flags).Invoke(model, new object[] { model.m_DataList, context, projected });
            int SkillTokens(string value) => (int)typeof(ChatQW).GetMethod("EstimateTokens", Flags).Invoke(null, new object[] { value });
            Check(oldEstimate - newEstimate == SkillTokens(originalSkill) - SkillTokens(projected),
                "History budgeting does not use the exact stage-projected skill payload");
            int generation = (int)typeof(ChatQW).GetField("m_StreamRequestGeneration", Flags).GetValue(model);
            var stale = (System.Collections.IEnumerator)typeof(ChatQW).GetMethod("RequestStageStreamWithObservation", Flags).Invoke(model,
                new object[] { "", generation - 1, null, null, false, context, null, null, null, FormalResponseStage.CommittedSpatialReply });
            Check(!stale.MoveNext() && model.ActiveSkillContext == originalSkill && Legacy() == baseline,
                "Cancelled stage coroutine reached HTTP preparation or changed the next request");
            Check(ReferenceEquals(model.m_DataList[0], persona) && ReferenceEquals(model.m_DataList[3], latest) && latest.content == user,
                "Stage serialization mutated accepted history references");
            const string result = "[actual execution] actionId=now-7 entityId=lamp-6 phase=arrived lightOn=true; no switch operation submitted.";
            var feedbackHistory = new List<LLM.SendData>(model.m_DataList) { new LLM.SendData("assistant", "I am approaching the lamp.") };
            string Feedback(FormalResponseStage stage) => (string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", Flags)
                .Invoke(model, new object[] { feedbackHistory, true, context, result, stage });
            string feedbackBaseline = (string)typeof(ChatQW).GetMethod("BuildRequestJsonForMessagesWithFeedback", Flags)
                .Invoke(model, new object[] { feedbackHistory, true, context, result });
            string feedbackCompact = Feedback(FormalResponseStage.CommittedSpatialReply);
            var feedbackMessages = (JArray)JObject.Parse(feedbackCompact)["messages"];
            Check((string)feedbackMessages[feedbackMessages.Count - 2]["role"] == "system" &&
                (string)feedbackMessages[feedbackMessages.Count - 2]["content"] == result &&
                (string)feedbackMessages[feedbackMessages.Count - 3]["content"] == "I am approaching the lamp." &&
                (string)feedbackMessages[feedbackMessages.Count - 1]["role"] == "user" &&
                (string)feedbackMessages[feedbackMessages.Count - 1]["content"] == CommittedSpatialReplyProtocol.BuildFinalWritingInstruction(user),
                "Spatial feedback displaced or rewrote the current result after its completed assistant action");
            Check(!feedbackCompact.Contains("<sing_goal") && feedbackCompact.Contains(facts) && feedbackCompact.Contains(memory) &&
                feedbackCompact.Contains(latest.imageDataUrl) && feedbackCompact.Length < feedbackBaseline.Length,
                "Feedback stage lost exact evidence or restored the non-executable skill grammar");
            Check(Feedback(FormalResponseStage.General) == feedbackBaseline &&
                Feedback(FormalResponseStage.CommittedSpatialReply) == feedbackCompact &&
                model.ActiveSkillContext == originalSkill && model.m_DataList.Count == 4,
                "Feedback stage projection leaked across successive requests or mutated accepted history");
            Debug.Log("[FormalResponseStageRegression] " + new JObject { ["baselineWireCharacters"] = baseline.Length,
                ["stageWireCharacters"] = compact.Length, ["baselineEstimatedTokens"] = oldEstimate, ["stageEstimatedTokens"] = newEstimate,
                ["feedbackBaselineWireCharacters"] = feedbackBaseline.Length, ["feedbackStageWireCharacters"] = feedbackCompact.Length });
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
            if (singing != null) UnityEngine.Object.DestroyImmediate(singing);
        }
    }

    private static void CheckFeedbackProviderCompatibility()
    {
        var host = new GameObject("InactiveLegacyFeedbackStageFixture"); host.SetActive(false);
        try
        {
            var provider = host.AddComponent<FormalResponseLegacyFeedbackFixture>();
            var parts = new List<SpeechText>();
            string completed = null;
            provider.PostSpeechFeedbackStreamForStage("context", "current-result", parts.Add, value => completed = value,
                "fixture-image", false, FormalResponseStage.CommittedSpatialReply);
            Check(provider.FeedbackCalls == 0 && parts.Count == 0 && completed == "" && provider.m_DataList.Count == 0,
                "Legacy stage fallback published or recorded an unapproved feedback candidate");
            provider.PostSpeechFeedbackStreamForStage("context", "current-result", parts.Add, value => completed = value,
                "fixture-image", true, FormalResponseStage.CommittedSpatialReply);
            Check(provider.FeedbackCalls == 1 && provider.LastContext == "context" && provider.LastFeedback == "current-result" &&
                provider.LastImage == "fixture-image" && completed == "legacy-complete" && provider.m_DataList.Count == 1,
                "Recorded stage fallback did not preserve the legacy feedback provider override");
            provider.PostSpeechFeedbackStreamForStage("next-context", "next-result", parts.Add, value => completed = value,
                null, true, FormalResponseStage.General);
            Check(provider.FeedbackCalls == 2 && provider.LastContext == "next-context" && provider.LastFeedback == "next-result" &&
                provider.LastImage == null && provider.m_DataList.Count == 2,
                "A prior spatial stage changed later legacy-provider feedback/history behavior");
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

public sealed class FormalResponseLegacyFeedbackFixture : LLM
{
    public int FeedbackCalls;
    public string LastContext, LastFeedback, LastImage;
    public override void PostSpeechFeedbackStream(string context, string feedback, Action<SpeechText> onSpeech,
        Action<string> onComplete, string imageDataUrl = null)
    {
        ++FeedbackCalls; LastContext = context; LastFeedback = feedback; LastImage = imageDataUrl;
        m_DataList.Add(new SendData("assistant", "legacy-complete"));
        onComplete?.Invoke("legacy-complete");
    }
}
