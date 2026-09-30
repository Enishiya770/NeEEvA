using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Controlled verdict only. Every other private handoff, source resolver, queue,
// renderer coroutine and text-drain gate under test is production ChatSample.
public sealed class SingingQueueHandoffFakeLLM : LLM
{
    public Action<string> reply;
    public int requests;
    public override void PostWorkReviewMsg(string prompt, string image, Action<string> callback, ConversationTrace trace)
    { requests++; reply = callback; }
    public override void PostMsg(string text, Action<string> callback) { }
    public override void PostMsgStream(string text, Action<string> delta, Action<string> complete,
        string imageDataUrl = null, bool recordAssistantHistory = true) { }
    public override void CancelEphemeralMsg() { }
}

public sealed class SingingQueueHandoffFakeVoice : GPTSoVITSFASTAPI
{
    public override void WarmUp() { }
}

public static class SingingQueueHandoffRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static int LastChecks => checks;

    // No network, no play mode required. Exercises the full production resolver
    // and proves that the late handoff enters the real renderer preflight.
    public static int RunOrThrow()
    {
        checks = 0;
        using (var f = new Fixture(false))
        {
            f.SilentProposal();
            Require((bool)Get(f.Chat, "m_TTSSenderDone") && !(bool)Get(f.Chat, "m_AgentRoundInFlight"),
                "silent-only completion did not drain before the independent review");
            Require(Get(f.Chat, "m_DeferredSingingRequest") != null && !(bool)Get(f.Chat, "m_HumBackPending"),
                "pending review must retain the explicit request without queuing audio");
            int revision = f.Revision, serial = (int)Get(f.Chat, "m_SingingQueueSerial");
            f.Approve();
            object taken = f.Take();
            Require(taken != null, "approved current request was not consumed");
            Call(f.Chat, "QueueReviewedSingingRequest", taken, null);
            Require((int)Get(f.Chat, "m_SingingQueueSerial") == serial + 1,
                "approved request did not pass the real QueueHumBack source resolver");
            Require((bool)Get(f.Chat, "m_SingingRequestSubmittedSinceUserTurn") &&
                ((string)Get(f.Chat, "m_LastHumBackResult")).Contains("没有可用的音频播放器"),
                "late silent-only handoff never entered real TryBeginPendingHumBack");
            Require(!(bool)Get(f.Chat, "m_HumBackPending") && !(bool)Call(f.Chat, "IsSingingRuntimeActivityActive"),
                "failed renderer preflight left a permanently pending queue");
            Require(f.Revision == revision && f.Take() == null, "handoff mutated material or consumed the request twice");
        }
        foreach (string fence in new[] { "pending", "rejected", "generation", "epoch", "version",
            "model", "disabled", "cancel", "new-turn", "source-range-changed" })
        using (var f = new Fixture(false))
        {
            f.SilentProposal();
            if (fence != "pending" && fence != "rejected") f.Approve();
            if (fence == "rejected") Call(f.Chat, "ApplySingingGoalReview", "waiting_user", "controlled rejection");
            if (fence == "generation") Set(f.Chat, "m_FormalResponseGeneration", 10);
            if (fence == "epoch") Set(f.Chat, "m_WorkEpoch", (int)Get(f.Chat, "m_WorkEpoch") + 1);
            if (fence == "version") { object goal = Get(f.Chat, "m_SingingGoal"); var field = goal.GetType().GetField("version"); field.SetValue(goal, (int)field.GetValue(goal) + 1); }
            if (fence == "model") ((ChatSetting)Get(f.Chat, "m_ChatSettings")).m_ChatModel = f.Host.AddComponent<SingingQueueHandoffFakeLLM>();
            if (fence == "disabled") f.Chat.enabled = false;
            if (fence == "cancel") Call(f.Chat, "CancelPendingHumBack", "regression-cancel", false);
            if (fence == "new-turn") f.Begin("先别唱。");
            if (fence == "source-range-changed")
                Require(f.Sense.TryPrepareSingingClip(f.First, false, "expanded", out _, out _), "fixture range change failed");
            object taken = f.Take();
            int serial = (int)Get(f.Chat, "m_SingingQueueSerial");
            if (taken != null) Call(f.Chat, "QueueReviewedSingingRequest", taken, null);
            Require((int)Get(f.Chat, "m_SingingQueueSerial") == serial && !(bool)Get(f.Chat, "m_HumBackPending"),
                "invalid request escaped approval/source/version fence: " + fence);
            if (fence != "source-range-changed") Require(taken == null, "stale request was consumed: " + fence);
        }
        return checks;
    }

    // Root's isolated Unity runner drives this IEnumerator in play mode. The
    // only endpoint is this fixture's ephemeral 127.0.0.1 listener, returning
    // synthetic PCM. No production model, ASR, SVC or reference file is contacted.
    public static IEnumerator RunChecks(string output)
    {
        if (!Application.isPlaying) throw new InvalidOperationException("RunChecks needs isolated play mode; RunOrThrow is the offline edit suite.");
        RunOrThrow();
        var rows = new JArray();
        foreach (string scenario in new[] { "late-silent", "text-drain", "cancel-in-flight", "cancel-review", "new-generation" })
        using (var server = new FakeConversion())
        using (var f = new Fixture(true))
        {
            f.ConfigureRenderer(server.Url);
            // Allow normal component Start to finish before constructing the
            // accepted user turn; no tick or external service is enabled.
            yield return null;
            f.SilentProposal();
            Call(f.Chat, "BeginAutonomyIntentProbe", "singing-goal-review");
            Require(f.Model.requests == 1 && f.Model.reply != null, "production Agent review callback not captured: " + scenario);
            Require(server.Conversions == 0 && !(bool)Get(f.Chat, "m_HumBackPending"),
                "audio conversion started before approval: " + scenario);
            var callback = f.Model.reply;
            int beforeGeneration = (int)Get(f.Chat, "m_HumBackGeneration");
            if (scenario == "cancel-review")
            {
                Call(f.Chat, "CancelAutonomyIntentProbe");
                Call(f.Chat, "CancelPendingHumBack", "cancel-before-review", false);
            }
            if (scenario == "new-generation") Set(f.Chat, "m_FormalResponseGeneration", 10);
            if (scenario == "text-drain")
            {
                Set(f.Chat, "m_TTSSenderDone", false);
                Set(f.Chat, "m_TextOutputDrainedGeneration", -1);
            }
            server.HoldConversions = scenario == "cancel-in-flight";
            callback(f.Response());
            if (scenario == "cancel-review" || scenario == "new-generation")
            {
                callback(f.Response());
                yield return null; yield return null;
                Require(server.Conversions == 0 && !(bool)Get(f.Chat, "m_HumBackPending") &&
                    (int)Get(f.Chat, "m_HumBackGeneration") == beforeGeneration,
                    "stale/duplicate review created singing execution: " + scenario);
                rows.Add(new JObject { ["case"] = scenario, ["conversions"] = server.Conversions, ["passed"] = true });
                continue;
            }
            Require((int)Get(f.Chat, "m_HumBackGeneration") == beforeGeneration + 1 &&
                !(bool)Get(f.Chat, "m_HumBackPending"),
                "approved callback did not start exactly one production renderer: " + scenario);
            // The exact provider callback is invoked twice, not merely a mock
            // deduplication helper. It may not consume/requeue the old request.
            int serial = (int)Get(f.Chat, "m_SingingQueueSerial");
            callback(f.Response());
            Require((int)Get(f.Chat, "m_SingingQueueSerial") == serial &&
                (int)Get(f.Chat, "m_HumBackGeneration") == beforeGeneration + 1,
                "duplicate review restarted the singing transaction: " + scenario);
            yield return WaitFor(() => server.Conversions == 1, "loopback conversion was not submitted: " + scenario);

            if (scenario == "cancel-in-flight")
            {
                Call(f.Chat, "CancelPendingHumBack", "regression-in-flight", false);
                server.Release();
                float until = Time.realtimeSinceStartup + .2f;
                while (Time.realtimeSinceStartup < until) yield return null;
                Require(!(bool)Call(f.Chat, "IsSingingRuntimeActivityActive") &&
                    f.Audio.clip == null && !f.Audio.isPlaying && server.Conversions == 1,
                    "late cancelled conversion recreated playback");
            }
            else if (scenario == "text-drain")
            {
                yield return WaitFor(() => (int)Get(f.Chat, "m_HumStreamConvertedSegments") == 1,
                    "parallel preparation did not buffer the converted clip");
                Require((bool)Get(f.Chat, "m_HumStreamWaitForTextOutputDrain") &&
                    ((ICollection)Get(f.Chat, "m_HumStreamReadyClips")).Count == 1 &&
                    ((ICollection)Get(f.Chat, "m_HumStreamScheduledClips")).Count == 0 &&
                    f.Audio.clip == null && !f.Audio.isPlaying,
                    "prepared singing escaped the text-output drain gate");
                Set(f.Chat, "m_TTSSenderDone", true);
                Set(f.Chat, "m_TextOutputDrainedGeneration", (int)Get(f.Chat, "m_FormalResponseGeneration"));
                yield return WaitFor(() => ((ICollection)Get(f.Chat, "m_HumStreamScheduledClips")).Count > 0,
                    "drained text never released the same prepared clip");
                Require(server.Conversions == 1 && !(bool)Get(f.Chat, "m_HumStreamWaitForTextOutputDrain"),
                    "text drain caused a second conversion instead of adopting prepared audio");
                Call(f.Chat, "CancelPendingHumBack", "regression-finished", false);
            }
            else
            {
                yield return WaitFor(() => ((ICollection)Get(f.Chat, "m_HumStreamScheduledClips")).Count > 0,
                    "late idle handoff never reached the real playback scheduler");
                Require(server.Conversions == 1 && f.Audio.clip != null, "idle playback did not use its single converted clip");
                Call(f.Chat, "CancelPendingHumBack", "regression-finished", false);
            }
            rows.Add(new JObject { ["case"] = scenario, ["conversions"] = server.Conversions, ["passed"] = true });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        File.WriteAllText(output, new JObject {
            ["passed"] = true, ["checks"] = checks, ["rows"] = rows,
            ["scope"] = "Production silent completion, Agent review callback, full source resolver, queue, UnityWebRequest conversion path and AudioSource scheduler. Controlled review plus synthetic PCM on a private loopback listener; no live model, remote conversion, acoustic quality or semantic accuracy claim."
        }.ToString());
    }

    private static IEnumerator WaitFor(Func<bool> ready, string failure)
    {
        float deadline = Time.realtimeSinceStartup + 8f;
        while (!ready() && Time.realtimeSinceStartup < deadline) yield return null;
        Require(ready(), failure);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Type type = typeof(AgentSingingGoalRegression).GetNestedType("Fixture", BindingFlags.NonPublic);
        private readonly object value;
        public GameObject Host => (GameObject)type.GetField("host").GetValue(value);
        public ChatSample Chat => (ChatSample)type.GetField("chat").GetValue(value);
        public SenseVoiceSpeechToText Sense => (SenseVoiceSpeechToText)type.GetField("sense").GetValue(value);
        public string First => (string)type.GetField("first").GetValue(value);
        public int Revision => Sense.DescribePracticePhrases().Single(p => p.ClipRef == First).Revision;
        public SingingQueueHandoffFakeLLM Model { get; }
        public AudioSource Audio { get; private set; }
        private const string User = "把第一段按当前范围唱给我听。";

        public Fixture(bool active)
        {
            value = Activator.CreateInstance(type, true);
            Model = Host.AddComponent<SingingQueueHandoffFakeLLM>(); Model.enabled = false;
            ((ChatSetting)Get(Chat, "m_ChatSettings")).m_ChatModel = Model;
            var text = new GameObject("QueueText", typeof(RectTransform), typeof(UnityEngine.UI.Text)); text.transform.SetParent(Host.transform);
            var button = new GameObject("QueueButton", typeof(RectTransform), typeof(UnityEngine.UI.Button)); button.transform.SetParent(Host.transform);
            var input = new GameObject("QueueInput", typeof(RectTransform), typeof(UnityEngine.UI.InputField)); input.transform.SetParent(Host.transform);
            Set(Chat, "m_TextBack", text.GetComponent<UnityEngine.UI.Text>());
            Set(Chat, "m_CommitMsgBtn", button.GetComponent<UnityEngine.UI.Button>());
            Set(Chat, "m_InputWord", input.GetComponent<UnityEngine.UI.InputField>());
            foreach (string field in new[] { "m_PersistSubtitleSettings", "m_EnableSubtitleTranslation", "m_PrewarmHumSVCOnSingingSkillLoad",
                "m_AutoStartHumSVC", "m_AutoStartSVS", "m_EnableScreenVision", "m_EnableMemoryRecall", "m_AgentRunning" })
                Set(Chat, field, false);
            typeof(SenseVoiceSpeechToText).GetField("m_DumpPracticeAudio", Flags).SetValue(Sense, false);
            Set(Chat, "m_StreamComplete", true); Set(Chat, "m_TTSSenderDone", true);
            Sense.enabled = false;
            if (active) Host.SetActive(true);
        }
        public void Begin(string text) => type.GetMethod("Begin").Invoke(value, new object[] { text });
        public void SilentProposal()
        {
            Begin(User);
            Set(Chat, "m_AgentRunning", true); Set(Chat, "m_AgentAwaitingFirstAcceptedUserTurn", false);
            Set(Chat, "m_CurrentSpeechRoundId", 12); Set(Chat, "m_SpeechRoundSequence", 12);
            Set(Chat, "m_FormalResponseGeneration", 9); Set(Chat, "m_AgentRoundInFlight", true);
            string raw = "<speech mode=\"after_action\"/><silent/>" +
                $"<sing_goal refs=\"{First}\" revisions=\"{Revision}\" range=\"current\" origin=\"user_request\" expected=\"第一段当前范围\"/>" +
                $"<sing refs=\"{First}\" range=\"current\"/>";
            Call(Chat, "OnStreamComplete", raw);
        }
        public void Approve() => typeof(AgentSingingGoalRegression).GetMethod("ApproveIndependentTarget", Flags)
            .Invoke(null, new object[] { value, null });
        public object Take() { object[] args = { null }; return (bool)Call(Chat, "TryTakeReviewedSingingRequest", args) ? args[0] : null; }
        public string Response() => new JObject {
            ["proceed"] = false, ["work_status"] = "waiting_user", ["work_evidence"] = "Controlled handoff test.",
            ["singing_goal_status"] = "approved", ["singing_goal_evidence"] = "Controlled independently matched source and current range.",
            ["singing_goal_expected"] = new JObject { ["origin"] = "user_request", ["request_quote"] = User,
                ["refs"] = First, ["range"] = "current", ["start_seconds"] = JValue.CreateNull(), ["end_seconds"] = JValue.CreateNull() },
            ["intent"] = "完成明确演唱", ["novelty"] = "", ["wait_seconds"] = 60
        }.ToString();
        public void ConfigureRenderer(string endpoint)
        {
            Audio = Host.AddComponent<AudioSource>(); Audio.playOnAwake = false; Audio.mute = true;
            Set(Chat, "m_AudioSource", Audio);
            var voice = Host.AddComponent<SingingQueueHandoffFakeVoice>(); voice.enabled = false;
            typeof(GPTSoVITSFASTAPI).GetField("m_VoiceConversionReferencePath", Flags).SetValue(voice, "fixture-reference-unused.wav");
            ((ChatSetting)Get(Chat, "m_ChatSettings")).m_TextToSpeech = voice;
            Set(Chat, "m_HumSVCURL", endpoint); Set(Chat, "m_EnableNeuralHumSVC", true);
            Set(Chat, "m_EnableSingingVoiceSynthesis", false); Set(Chat, "m_AllowLegacyHumFallback", false);
            Set(Chat, "m_EnableStreamedFullSongSVC", true);
        }
        public void Dispose()
        {
            Call(Chat, "CancelAutonomyIntentProbe"); Call(Chat, "CancelPendingHumBack", "fixture-dispose", false);
            Chat.StopAllCoroutines();
            ((IDisposable)value).Dispose();
        }
    }

    private sealed class FakeConversion : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly ManualResetEventSlim release = new ManualResetEventSlim(false);
        private readonly byte[] wav;
        private int conversions;
        private volatile bool disposed;
        public volatile bool HoldConversions;
        public int Conversions => Volatile.Read(ref conversions);
        public string Url { get; }

        public FakeConversion()
        {
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string root = "http://127.0.0.1:" + port + "/";
            Url = root + "convert";
            wav = MakeWav();
            _ = Task.Run(AcceptLoop);
        }
        private async Task AcceptLoop()
        {
            while (!disposed)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch (Exception) { if (disposed) return; throw; }
                _ = Task.Run(() => Reply(client));
            }
        }
        private void Reply(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream(); stream.ReadTimeout = 10000; stream.WriteTimeout = 10000;
                var header = new StringBuilder();
                while (header.Length < 65536)
                {
                    int b = stream.ReadByte(); if (b < 0) return;
                    header.Append((char)b);
                    int n = header.Length;
                    if (n >= 4 && header[n - 4] == '\r' && header[n - 3] == '\n' &&
                        header[n - 2] == '\r' && header[n - 1] == '\n') break;
                }
                string headers = header.ToString();
                if (!headers.EndsWith("\r\n\r\n", StringComparison.Ordinal)) return;
                int length = 0;
                foreach (string line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(line.Substring(15).Trim(), out length);
                if (length < 0 || length > 2 * 1024 * 1024) return;
                if (headers.IndexOf("Expect: 100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    byte[] interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    stream.Write(interim, 0, interim.Length);
                }
                var buffer = new byte[8192];
                while (length > 0) { int read = stream.Read(buffer, 0, Math.Min(length, buffer.Length)); if (read <= 0) return; length -= read; }
                bool convert = headers.StartsWith("POST /convert ", StringComparison.Ordinal);
                byte[] data = Encoding.UTF8.GetBytes("{\"ok\":true,\"worker_status\":\"ready\"}");
                if (convert)
                {
                    Interlocked.Increment(ref conversions);
                    if (HoldConversions) release.Wait(TimeSpan.FromSeconds(10));
                    data = wav;
                }
                if (disposed) return;
                byte[] responseHeader = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: " +
                    (convert ? "audio/wav" : "application/json") + "\r\nContent-Length: " + data.Length + "\r\n\r\n");
                stream.Write(responseHeader, 0, responseHeader.Length);
                stream.Write(data, 0, data.Length);
            }
            catch (Exception) { /* A production cancellation may close the client socket. */ }
        }
        public void Release() => release.Set();
        public void Dispose() { disposed = true; release.Set(); listener.Stop(); }
        private static byte[] MakeWav()
        {
            const int rate = 16000, samples = 16000;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1);
                writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
                for (int i = 0; i < samples; i++) writer.Write((short)(800 * Math.Sin(i * .086)));
                return stream.ToArray();
            }
        }
    }
    private static object Call(object target, string name, params object[] args) =>
        typeof(ChatSample).GetMethod(name, Flags).Invoke(target, args);
    private static object Get(object target, string name) => typeof(ChatSample).GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => typeof(ChatSample).GetField(name, Flags).SetValue(target, value);
    private static void Require(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
}
