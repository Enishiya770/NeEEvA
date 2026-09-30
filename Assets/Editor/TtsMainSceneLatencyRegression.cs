using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

/// <summary>Opt-in real TTS timing in the saved rendered scene; no Qwen, microphone or saved scene edits.</summary>
[InitializeOnLoad]
public static class TtsMainSceneLatencyRegression
{
    const string Key = "NeEEvA.TtsMainSceneLatency.";
    const string ScenePath = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    const string QualityPath = "ProjectSettings/QualitySettings.asset";
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const string Chinese = "今天辛苦了，先休息一下吧。";
    const string Japanese = "今日も一日、よく頑張ったね。";
    static ChatSample chat;
    static GPTSoVITSFASTAPI tts;
    static AudioSource source;
    static JObject report;
    static JArray rows, traces, frameSamples;
    static Stack<IEnumerator> work;
    static Request activeRequest;
    static CancellationTokenSource nativeHttpCancellation;
    static Task<JObject> nativeHttpTask;
    static string nativeHttpCase;
    static int frame = -1;
    static long previousFrameClock;
    static double lastTimingStamp = -1;
    static readonly FrameTiming[] Timing = new FrameTiming[1];

    sealed class Request
    {
        public JObject Row;
        public long StartedAt;
        public bool Started, Completed, Success;
        public AudioClip Clip;
        public int MaximumSamples;
    }

    static TtsMainSceneLatencyRegression()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) Begin();
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                if (SessionState.GetBool(Key + "Finished", false)) Exit();
                else Finish(new InvalidOperationException("Play mode ended before the TTS trial completed."));
            }
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) &&
                !EditorApplication.isPlaying) Exit();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Use a separate batch Editor with graphics and the existing TTS service.");
        foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
            if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                throw new InvalidOperationException("Refusing to replace a dirty saved scene.");
        string requestedMode = Environment.GetEnvironmentVariable("NEEEVA_TTS_MAIN_SCENE_MODE");
        if (string.IsNullOrWhiteSpace(requestedMode)) requestedMode = SessionState.GetString(Key + "Mode", "default");
        string mode = requestedMode.Trim().ToLowerInvariant();
        bool networkProbe = mode == "network-probe";
        if (networkProbe) mode = "default";
        if (mode == "baseline") mode = "default";
        if (mode != "default" && mode != "fps60") throw new ArgumentException("Mode must be default, baseline, fps60 or network-probe.");
        string requestedProbe = Environment.GetEnvironmentVariable("NEEEVA_TTS_NETWORK_PROBE");
        networkProbe |= string.IsNullOrWhiteSpace(requestedProbe)
            ? SessionState.GetBool(Key + "NetworkProbe", false)
            : requestedProbe == "1" || requestedProbe.Equals("true", StringComparison.OrdinalIgnoreCase);
        string variant = networkProbe ? (mode == "default" ? "network-probe" : mode + "-network-probe") : mode;
        string reportName = Environment.GetEnvironmentVariable("NEEEVA_TTS_TRIAL_REPORT");
        if (!string.IsNullOrWhiteSpace(reportName))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(reportName, "^[a-zA-Z0-9_-]+$"))
                throw new ArgumentException("Unsafe trial report name.");
            variant = reportName;
        }
        SessionState.SetBool(Key + "Extended", Environment.GetEnvironmentVariable("NEEEVA_TTS_TRIAL_EXTENDED") == "1");
        SessionState.SetString(Key + "Mode", mode);
        SessionState.SetBool(Key + "NetworkProbe", networkProbe);
        SessionState.SetString(Key + "Output", "Logs/tts/main-scene-latency-" + variant + ".json");
        SessionState.SetString(Key + "SceneHash", Hash(ScenePath));
        SessionState.SetString(Key + "QualityHash", Hash(QualityPath));
        SessionState.SetInt(Key + "OriginalVSync", QualitySettings.vSyncCount);
        SessionState.SetInt(Key + "OriginalTargetFps", Application.targetFrameRate);
        SessionState.SetBool(Key + "OriginalBackground", Application.runInBackground);
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        chat = SceneObjects<ChatSample>().Single();
        var settings = (ChatSetting)Get(chat, "m_ChatSettings");
        tts = settings.m_TextToSpeech as GPTSoVITSFASTAPI;
        if (tts == null) throw new InvalidOperationException("The main scene must use its real GPTSoVITSFASTAPI.");
        string trialUrl = Environment.GetEnvironmentVariable("NEEEVA_TTS_TRIAL_URL");
        if (!string.IsNullOrWhiteSpace(trialUrl))
        {
            if (!Uri.TryCreate(trialUrl, UriKind.Absolute, out Uri endpoint) ||
                (endpoint.Scheme != "http" && endpoint.Scheme != "https") ||
                !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
                throw new ArgumentException("Trial URL must be HTTP(S) without credentials, query or fragment.");
            Set(tts, "m_PostURL", trialUrl);
        }
        string trialReference = Environment.GetEnvironmentVariable("NEEEVA_TTS_TRIAL_REFERENCE");
        if (!string.IsNullOrWhiteSpace(trialReference)) Set(tts, "m_ReferWavPath", trialReference);
        foreach (var memory in SceneObjects<MonoBehaviour>().Where(x => x != null && x.GetType().Name == "MemoryHub"))
        {
            if (memory.gameObject == chat.gameObject || memory.gameObject == tts.gameObject)
                throw new InvalidOperationException("MemoryHub must have its own object so Awake can be prevented.");
            memory.gameObject.SetActive(false);
        }
        Set(chat, "m_MemoryHub", null);
        settings.m_SpeechToText = null;
        foreach (var component in SceneObjects<MonoBehaviour>().Where(x => x != null).ToArray())
        {
            string name = component.GetType().Name;
            if (component is STT || component is LLM || component is EventSystem ||
                name == "RTSpeechHandler" || name == "VoiceInputs" || name == "UnityWakeOnVoice" ||
                name == "CompanionXRInteraction") component.enabled = false;
        }
        foreach (string name in new[] { "m_AutoStartHumSVC", "m_AutoStartSVS", "m_EnableMemoryRecall",
            "m_EnableIdleMemoryConsolidation", "m_EnableSubtitleTranslation", "m_PersistSubtitleSettings",
            "m_ContinuousSceneVision", "m_EnableLatencyFiller", "m_EnableAgentLoop", "m_AgentRunning",
            "m_EnableAutonomyIntentProbe", "m_LogRawLLMOutput" }) Set(chat, name, false);
        var presentation = chat.GetComponent<NeEEvA.Presentation.CompanionPresentation>() ??
            chat.gameObject.AddComponent<NeEEvA.Presentation.CompanionPresentation>();
        presentation.enabled = false;
        if (settings.m_ChatModel is ChatQW qwen) qwen.m_PrewarmPrefixOnStart = false;
        Set(tts, "m_WarmUpLatencyFillerVariants", false);
        foreach (var audio in SceneObjects<AudioSource>()) audio.mute = true;
        SessionState.SetBool(Key + "Active", true);
        SessionState.SetBool(Key + "Finished", false);
        SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 600f);
        EditorApplication.isPlaying = true;
    }

    static IEnumerable<T> SceneObjects<T>() where T : Component =>
        SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<T>(true));

    static void Begin()
    {
        try
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                throw new InvalidOperationException("This test requires rendering; omit -nographics.");
            chat = SceneObjects<ChatSample>().Single();
            var settings = (ChatSetting)Get(chat, "m_ChatSettings");
            tts = (GPTSoVITSFASTAPI)settings.m_TextToSpeech;
            source = (AudioSource)Get(chat, "m_AudioSource");
            Require(source != null && tts.isActiveAndEnabled, "Actual scene TTS and AudioSource must be active.");
            source.mute = true;
            source.playOnAwake = false;
            source.Stop();
            Application.runInBackground = true;
            string mode = SessionState.GetString(Key + "Mode", "default");
            if (mode == "fps60") { QualitySettings.vSyncCount = 0; Application.targetFrameRate = 60; }
            rows = new JArray(); traces = new JArray(); frameSamples = new JArray();
            AudioSettings.GetDSPBufferSize(out int dspLength, out int dspCount);
            report = new JObject {
                ["status"] = "running", ["mode"] = mode, ["scene"] = ScenePath,
                ["scope"] = "Real scene rendering and GPT-SoVITS with fixed SpeechText. Muted AudioSource sample progression, not physical audibility. No Qwen/ASR/microphone/user-memory writes. Scene isolation is memory-only.",
                ["startedUtc"] = DateTime.UtcNow.ToString("O"), ["sceneHash"] = SessionState.GetString(Key + "SceneHash", ""),
                ["savedQualityHash"] = SessionState.GetString(Key + "QualityHash", ""),
                ["microphoneUsed"] = false, ["qwenUsed"] = false, ["audioMuted"] = true, ["memoryWritesEnabled"] = false,
                ["networkProbeEnabled"] = SessionState.GetBool(Key + "NetworkProbe", false),
                ["extended"] = SessionState.GetBool(Key + "Extended", false),
                ["ttsEndpoint"] = (string)Get(tts, "m_PostURL"),
                ["ttsReference"] = (string)Get(tts, "m_ReferWavPath"),
                ["nativeHttpRows"] = new JArray(),
                ["frameSettings"] = new JObject {
                    ["originalVSyncCount"] = SessionState.GetInt(Key + "OriginalVSync", -1),
                    ["originalTargetFrameRate"] = SessionState.GetInt(Key + "OriginalTargetFps", -1),
                    ["vSyncCount"] = QualitySettings.vSyncCount, ["targetFrameRate"] = Application.targetFrameRate,
                    ["requestedOverride"] = mode == "fps60", ["runInBackground"] = Application.runInBackground,
                    ["timeScale"] = Time.timeScale, ["renderingEnabled"] = true },
                ["audioConfiguration"] = new JObject {
                    ["outputSampleRate"] = AudioSettings.outputSampleRate, ["dspBufferLengthFrames"] = dspLength,
                    ["dspBufferCount"] = dspCount, ["speakerMode"] = AudioSettings.speakerMode.ToString(),
                    ["dspBufferMilliseconds"] = dspLength * 1000.0 / Math.Max(1, AudioSettings.outputSampleRate),
                    ["callbackProbe"] = "No additional audio filter installed. Production PCMReaderCallback maximum length is included in each playback trace." },
                ["renderDevice"] = SystemInfo.graphicsDeviceName, ["unityVersion"] = Application.unityVersion,
                ["rows"] = rows, ["traces"] = traces, ["frameSamples"] = frameSamples
            };
            work = new Stack<IEnumerator>(); work.Push(Run());
            frame = -1; previousFrameClock = 0; lastTimingStamp = -1;
        }
        catch (Exception error) { Finish(error); }
    }

    static IEnumerator Run()
    {
        yield return Delay(2f); // Allow actual scene Start/Awake and render binding to settle.
        float warmupStart = Time.realtimeSinceStartup;
        yield return Until(() => !(bool)Get(tts, "m_IsWarmingUp"), 120f, "Scene startup TTS warmup did not finish.");
        report["startupWarmupWaitSeconds"] = Time.realtimeSinceStartup - warmupStart;
        report["warmupExcludedFromMeasurements"] = true;
        foreach (var memory in SceneObjects<MonoBehaviour>().Where(x => x != null && x.GetType().Name == "MemoryHub"))
            Require(!memory.gameObject.activeInHierarchy, "Memory hub unexpectedly active.");
        if (SessionState.GetBool(Key + "NetworkProbe", false))
        {
            // No ordinary playback is started until each worker has read and
            // disposed its entire response (or completed cancellation).
            yield return NativeHttpProbe("native-ja", Japanese, "ja");
            yield return NativeHttpProbe("native-zh", Chinese, "zh");
        }
        yield return Normal("zh-1", Chinese, "zh");
        yield return Normal("ja-1", Japanese, "ja");
        yield return Normal("zh-2", Chinese, "zh");
        yield return Normal("ja-2", Japanese, "ja");
        if (SessionState.GetBool(Key + "Extended", false))
        {
            yield return Normal("long-zh", "今天的事情已经忙完了，我们可以慢慢聊一会儿。你不用急着把所有事情都说清楚，我会认真听着，也会在你需要的时候陪你一起想办法。", "zh");
            yield return Normal("long-ja", "今日はもう仕事が終わったんだね。少し休んでから、ゆっくり話そう。急いですべてを説明しなくても大丈夫。あなたの話を聞きながら、一緒に考えていくよ。", "ja");
        }

        var cancelled = StartRequest("cancel-during-playback", "这是一段用于中途取消的测试语音。我们会在声音开始之后停止它，再播放下一句。", "zh", true);
        yield return Until(() => cancelled.Row["firstAudioSourceAdvanceSeconds"] != null && source.isPlaying,
            45f, "Cancellation case never reached actual AudioSource progress.");
        yield return Delay(.15f);
        Require(source.isPlaying && !cancelled.Completed && source.clip == cancelled.Clip,
            "Cancellation must occur during the owned clip, not after completion.");
        cancelled.Row["cancelAtSeconds"] = Seconds(cancelled.StartedAt);
        cancelled.Row["samplesBeforeCancel"] = source.timeSamples;
        tts.CancelStreaming();
        cancelled.Row["stoppedSynchronously"] = !source.isPlaying;
        Require(!source.isPlaying, "CancelStreaming must stop its AudioSource immediately.");

        // Start the successor before yielding to the old completion, exercising the
        // provider's ownership fence instead of waiting away the race.
        var successor = StartRequest("after-cancel", "已经停止上一段，现在播放新的句子。", "zh", false);
        yield return Until(() => cancelled.Completed, 10f, "Cancelled stream never completed its callback.");
        Require(!cancelled.Success, "Cancelled stream must not report success.");
        EndRequest(cancelled, true);
        yield return Until(() => successor.Completed, 60f, "Successor TTS did not finish.");
        ValidateCompleted(successor);
        EndRequest(successor, false);
        yield return Delay(.5f);
        Require(!source.isPlaying, "Old callbacks must not restart audio after the successor finishes.");
        report["lateAudioRestarted"] = false;
        report["frameSummary"] = SummarizeFrames(frameSamples);
        report["timingSummary"] = new JObject {
            ["normalCases"] = 4,
            ["firstPcmSeconds"] = Summary(rows.OfType<JObject>().Where(IsNormal).Select(x => Number(x, "firstPcmSeconds"))),
            ["bufferReadySeconds"] = Summary(rows.OfType<JObject>().Where(IsNormal).Select(x => Number(x, "bufferReadySeconds"))),
            ["firstAudioSourceAdvanceSeconds"] = Summary(rows.OfType<JObject>().Where(IsNormal).Select(x => Number(x, "firstAudioSourceAdvanceSeconds"))),
            ["scope"] = "Four fixed short utterances, two per language; descriptive sample, not a population estimate." };
    }

    static bool IsNormal(JObject item) => ((string)item["name"])?.StartsWith("zh-", StringComparison.Ordinal) == true ||
        ((string)item["name"])?.StartsWith("ja-", StringComparison.Ordinal) == true;

    static IEnumerator NativeHttpProbe(string name, string text, string language)
    {
        Require(activeRequest == null && !source.isPlaying && tts.CanPrefetchNextSpeech,
            "Native HTTP probe must run while the production TTS provider is idle.");
        // Unity objects, ResolveSpeech and JsonUtility are only accessed here on
        // the Editor/main thread. The worker receives immutable .NET values.
        SpeechText speech = tts.ResolveSpeech(new SpeechText(text, language));
        int streamingMode = (int)Get(tts, "m_StreamingMode");
        var factory = typeof(GPTSoVITSFASTAPI).GetMethod("CreateSpeechRequest", Flags, null,
            new[] { typeof(string), typeof(string), typeof(int) }, null);
        Require(factory != null, "Production TTS request factory unavailable.");
        object requestData = factory.Invoke(tts, new object[] { speech.Text, speech.LanguageCode, streamingMode });
        byte[] payload = Encoding.UTF8.GetBytes(JsonUtility.ToJson(requestData));
        string endpoint = (string)Get(tts, "m_PostURL");
        var parameters = JObject.Parse(Encoding.UTF8.GetString(payload));
        nativeHttpCase = name;
        var cancellation = new CancellationTokenSource();
        nativeHttpCancellation = cancellation;
        cancellation.CancelAfter(45000);
        CancellationToken token = cancellation.Token;
        nativeHttpTask = Task.Run(() => RunNativeHttp(endpoint, payload, token));
        long started = Clock();
        bool timedOut = false;
        while (!nativeHttpTask.IsCompleted)
        {
            if (Seconds(started) > 45 && !timedOut) { timedOut = true; cancellation.Cancel(); }
            if (Seconds(started) > 50) throw new TimeoutException("Native HTTP worker did not finish after cancellation.");
            yield return null;
        }
        Task<JObject> completed = nativeHttpTask;
        nativeHttpTask = null;
        nativeHttpCancellation = null;
        cancellation.Dispose();
        nativeHttpCase = null;
        // GetResult is nonblocking now. Thread errors become a measured probe
        // failure before any further TTS request is allowed to start.
        JObject result = completed.GetAwaiter().GetResult();
        result["name"] = name; result["text"] = speech.Text; result["language"] = speech.LanguageCode;
        result["streamingMode"] = streamingMode;
        result["minChunkLength"] = parameters["min_chunk_length"]?.DeepClone();
        result["payloadBytes"] = payload.Length;
        using (var hash = SHA256.Create()) result["productionPayloadSha256"] = BitConverter.ToString(hash.ComputeHash(payload)).Replace("-", "");
        result["payloadSource"] = "Actual GPTSoVITSFASTAPI.CreateSpeechRequest and JsonUtility.ToJson, both called on the main thread; full payload omitted.";
        result["mainThreadWaitSeconds"] = Seconds(started);
        result["frameSummary"] = SummarizeFrames(new JArray(frameSamples.OfType<JObject>()
            .Where(x => (string)x["case"] == name).Select(x => x.DeepClone())));
        ((JArray)report["nativeHttpRows"]).Add(result);
        Debug.Log("[TtsMainSceneLatency/NativeHttp] " + name + " status=" + result["status"] +
            " firstPcm=" + result["firstPcmSeconds"] + " total=" + result["completeSeconds"]);
        Require((string)result["status"] == "passed", "Native HTTP probe failed: " + name + " " + (string)result["errorType"]);
        yield return Delay(.3f);
    }

    // Worker-only method: no Unity objects, logging, clocks, JSON serialization
    // APIs or shared report mutations. Stopwatch and all HTTP reads are .NET.
    static JObject RunNativeHttp(string endpoint, byte[] payload, CancellationToken cancellation)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var reads = new JArray();
        var result = new JObject {
            ["status"] = "running", ["startedUtc"] = DateTime.UtcNow.ToString("O"),
            ["transport"] = "HttpWebRequest on a background Task; direct connection without a proxy",
            ["scope"] = "Full streaming response while the real Unity scene renders; no AudioClip, playback or UnityWebRequest.",
            ["pcmDefinition"] = "Bytes after the same fixed 44-byte WAV header consumed by the production handler; first PCM requires one complete 16-bit sample.",
            ["reads"] = reads
        };
        long received = 0;
        int readCount = 0, headerCopied = 0;
        double? firstPcm = null;
        byte[] header = new byte[44];
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.ContentLength = payload.Length;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.AllowWriteStreamBuffering = false;
            request.KeepAlive = false;
            request.Proxy = null;
            request.ServicePoint.Expect100Continue = false;
            using (cancellation.Register(() => request.Abort()))
            {
                cancellation.ThrowIfCancellationRequested();
                using (Stream body = request.GetRequestStream()) body.Write(payload, 0, payload.Length);
                result["requestBodySentSeconds"] = timer.Elapsed.TotalSeconds;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    result["httpStatus"] = (int)response.StatusCode;
                    result["headerSeconds"] = timer.Elapsed.TotalSeconds;
                    using (Stream input = response.GetResponseStream())
                    {
                        byte[] buffer = new byte[64 * 1024];
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            double elapsed = timer.Elapsed.TotalSeconds;
                            if (received == 0) result["firstBodyBytesSeconds"] = elapsed;
                            if (headerCopied < header.Length)
                            {
                                int copy = Math.Min(header.Length - headerCopied, read);
                                Buffer.BlockCopy(buffer, 0, header, headerCopied, copy);
                                headerCopied += copy;
                            }
                            received += read; ++readCount;
                            if (received >= 46 && !firstPcm.HasValue) firstPcm = elapsed;
                            if (reads.Count < 4096) reads.Add(new JObject {
                                ["seconds"] = elapsed, ["bytes"] = read, ["totalBytes"] = received });
                        }
                    }
                }
            }
            result["completeSeconds"] = timer.Elapsed.TotalSeconds;
            bool riff = headerCopied == 44 && Encoding.ASCII.GetString(header, 0, 4) == "RIFF" &&
                Encoding.ASCII.GetString(header, 8, 4) == "WAVE";
            int channels = riff ? BitConverter.ToUInt16(header, 22) : 0;
            int rate = riff ? BitConverter.ToInt32(header, 24) : 0;
            int bits = riff ? BitConverter.ToUInt16(header, 34) : 0;
            int format = riff ? BitConverter.ToUInt16(header, 20) : 0;
            result["channels"] = channels; result["rate"] = rate; result["bits"] = bits;
            result["pcmBytes"] = Math.Max(0, received - 44);
            if (!riff || format != 1 || bits != 16 || channels <= 0 || rate <= 0 || received <= 44 || !firstPcm.HasValue)
                throw new InvalidDataException("Response is not the production handler's streaming PCM16 WAV format.");
            double duration = (received - 44) / (double)(channels * rate * (bits / 8));
            result["audioSeconds"] = duration;
            result["realtimeFactor"] = timer.Elapsed.TotalSeconds / duration;
            result["status"] = "passed";
        }
        catch (Exception error)
        {
            result["status"] = "failed";
            result["errorType"] = error.GetType().Name; // Never retain endpoint/credential-bearing exception messages.
            result["cancelled"] = cancellation.IsCancellationRequested;
            result["completeSeconds"] = timer.Elapsed.TotalSeconds;
            if (error is WebException web && web.Response is HttpWebResponse response)
            { result["httpStatus"] = (int)response.StatusCode; response.Dispose(); }
        }
        result["totalBytes"] = received;
        result["readCount"] = readCount;
        result["firstPcmSeconds"] = firstPcm.HasValue ? new JValue(firstPcm.Value) : JValue.CreateNull();
        return result;
    }

    static IEnumerator Normal(string name, string text, string language)
    {
        var request = StartRequest(name, text, language, false);
        yield return Until(() => request.Completed, 60f, "TTS completion timeout: " + name);
        ValidateCompleted(request);
        EndRequest(request, false);
        yield return Delay(.3f);
    }

    static Request StartRequest(string name, string text, string language, bool expectCancel)
    {
        var speech = tts.ResolveSpeech(new SpeechText(text, language));
        var request = new Request {
            StartedAt = Clock(),
            Row = new JObject { ["name"] = name, ["text"] = text, ["language"] = speech.LanguageCode,
                ["status"] = "running", ["expectedCancellation"] = expectCancel,
                ["startedUtc"] = DateTime.UtcNow.ToString("O"), ["frameSamples"] = new JArray() }
        };
        rows.Add(request.Row); activeRequest = request;
        tts.TraceContext = new ConversationTrace("tts-main-scene-trial", line => Trace(request, line));
        tts.SpeakStreamingWithPlaybackGate(speech, source, ignored => {
            request.Started = true;
            request.Clip = source.clip;
            request.Row["playCalledSeconds"] = Seconds(request.StartedAt);
            request.Row["clipSampleRate"] = request.Clip != null ? request.Clip.frequency : 0;
            request.Row["clipChannels"] = request.Clip != null ? request.Clip.channels : 0;
            request.Row["clipCapacitySamples"] = request.Clip != null ? request.Clip.samples : 0;
        }, (success, ignored, duration) => {
            request.Completed = true; request.Success = success;
            request.Row["callbackCompletedSeconds"] = Seconds(request.StartedAt);
            request.Row["succeeded"] = success; request.Row["audioDurationSeconds"] = duration;
        }, () => TTS.StreamingPlaybackPermission.Play);
        return request;
    }

    static void Trace(Request request, string line)
    {
        var item = JObject.Parse(line);
        item["trialCase"] = request.Row["name"].DeepClone();
        if (traces.Count < 2048) traces.Add(item);
        string name = (string)item["event"];
        if (name == "tts.first_pcm" && request.Row["firstPcmSeconds"] == null) request.Row["firstPcmSeconds"] = Seconds(request.StartedAt);
        if (name == "tts.buffer_ready" && request.Row["bufferReadySeconds"] == null) request.Row["bufferReadySeconds"] = Seconds(request.StartedAt);
        if (name == "tts.send") request.Row["send"] = item["fields"]?.DeepClone();
        if (name == "tts.receive_complete") request.Row["receive"] = item["fields"]?.DeepClone();
        if (name == "tts.playback_completed") request.Row["playback"] = item["fields"]?.DeepClone();
        if (name == "audio.stream_clip_created") request.Row["streamClipCreated"] = item["fields"]?.DeepClone();
    }

    static void ValidateCompleted(Request request)
    {
        Require(request.Success && request.Started && request.Row["firstAudioSourceAdvanceSeconds"] != null,
            "TTS must finish successfully after observable sample progress: " + request.Row["name"]);
        Require(request.Row["firstPcmSeconds"] != null && request.Row["bufferReadySeconds"] != null,
            "Production PCM and buffer-ready traces are required: " + request.Row["name"]);
        int gapChecks = 0;
        foreach (var pause in traces.OfType<JObject>().Where(x => (string)x["event"] == "tts.rebuffer_start" &&
            (string)x["trialCase"] == (string)request.Row["name"]))
        {
            var fields = (JObject)pause["fields"];
            Require(fields["firstGapFrame"] != null && (long)fields["firstGapFrame"] >= 0 &&
                (long)fields["playbackFrame"] + (long)fields["pauseLeadFrames"] >= (long)fields["firstGapFrame"],
                "Rebuffer pause must wait until playback approaches the actual PCM gap.");
            ++gapChecks;
        }
        request.Row["rebufferPositionChecks"] = gapChecks;
    }

    static void EndRequest(Request request, bool cancelled)
    {
        request.Row["status"] = cancelled ? "cancelled-as-requested" : "passed";
        request.Row["maximumObservedAudioSourceSamples"] = request.MaximumSamples;
        request.Row["frameSummary"] = SummarizeFrames((JArray)request.Row["frameSamples"]);
        if (ReferenceEquals(activeRequest, request)) activeRequest = null;
        Debug.Log("[TtsMainSceneLatency/Case] " + request.Row["name"] + " status=" + request.Row["status"] +
            " firstPcm=" + request.Row["firstPcmSeconds"] + " firstAdvance=" + request.Row["firstAudioSourceAdvanceSeconds"]);
    }

    static void ObserveFrame()
    {
        long now = Clock();
        var sample = new JObject { ["frame"] = Time.frameCount, ["unscaledFrameMs"] = Time.unscaledDeltaTime * 1000.0,
            ["case"] = nativeHttpCase ?? (string)activeRequest?.Row["name"] ?? "startup-or-gap" };
        if (previousFrameClock != 0) sample["observedFrameIntervalMs"] = (now - previousFrameClock) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        previousFrameClock = now;
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, Timing) > 0 && Timing[0].frameStartTimestamp != lastTimingStamp)
        {
            lastTimingStamp = Timing[0].frameStartTimestamp;
            if (Timing[0].cpuFrameTime > 0) sample["cpuFrameMs"] = Timing[0].cpuFrameTime;
            if (Timing[0].gpuFrameTime > 0) sample["gpuFrameMs"] = Timing[0].gpuFrameTime;
        }
        if (frameSamples.Count < 40000) frameSamples.Add(sample);
        var request = activeRequest;
        if (request == null || request.Completed) return;
        var samples = (JArray)request.Row["frameSamples"];
        if (samples.Count < 10000) samples.Add(sample.DeepClone());
        if (!request.Started || request.Clip == null || source.clip != request.Clip || !source.isPlaying) return;
        int position = source.timeSamples;
        request.MaximumSamples = Math.Max(request.MaximumSamples, position);
        if (position > 0 && request.Row["firstAudioSourceAdvanceSeconds"] == null)
        {
            request.Row["firstAudioSourceAdvanceSeconds"] = Seconds(request.StartedAt);
            request.Row["firstObservedSamples"] = position;
            request.Row["firstObservedDspTime"] = AudioSettings.dspTime;
        }
    }

    static JObject SummarizeFrames(JArray samples) => new JObject {
        ["sampleCount"] = samples.Count,
        ["unscaledFrameMs"] = Summary(samples.OfType<JObject>().Select(x => Number(x, "unscaledFrameMs"))),
        ["observedFrameIntervalMs"] = Summary(samples.OfType<JObject>().Select(x => Number(x, "observedFrameIntervalMs"))),
        ["cpuFrameMs"] = Summary(samples.OfType<JObject>().Select(x => Number(x, "cpuFrameMs"))),
        ["gpuFrameMs"] = Summary(samples.OfType<JObject>().Select(x => Number(x, "gpuFrameMs"))),
        ["scope"] = "One observation per Unity frame. Wall interval includes Editor scheduling. CPU/GPU timing may be unavailable; empty count is not zero cost." };

    static double? Number(JObject value, string name) => value[name]?.Type == JTokenType.Float || value[name]?.Type == JTokenType.Integer
        ? (double?)value[name] : null;
    static JObject Summary(IEnumerable<double?> values)
    {
        double[] data = values.Where(x => x.HasValue && !double.IsNaN(x.Value) && !double.IsInfinity(x.Value)).Select(x => x.Value).OrderBy(x => x).ToArray();
        if (data.Length == 0) return new JObject { ["count"] = 0 };
        double median = data.Length % 2 == 0 ? (data[data.Length / 2 - 1] + data[data.Length / 2]) * .5 : data[data.Length / 2];
        return new JObject { ["count"] = data.Length, ["min"] = data[0], ["median"] = median,
            ["p95"] = data[Math.Min(data.Length - 1, (int)Math.Ceiling(data.Length * .95) - 1)],
            ["max"] = data[data.Length - 1], ["mean"] = data.Average() };
    }

    static IEnumerator Until(Func<bool> condition, float timeout, string failure)
    { long started = Clock(); while (!condition()) { if (Seconds(started) > timeout) throw new TimeoutException(failure); yield return null; } }
    static IEnumerator Delay(float seconds)
    { long started = Clock(); while (Seconds(started) < seconds) yield return null; }
    static long Clock() => System.Diagnostics.Stopwatch.GetTimestamp();
    static double Seconds(long started) => (Clock() - started) / (double)System.Diagnostics.Stopwatch.Frequency;
    static void Require(bool valid, string reason) { if (!valid) throw new InvalidOperationException(reason); }

    static void Tick()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        try
        {
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", 0)) throw new TimeoutException("TTS scene trial deadline.");
            if (!EditorApplication.isPlaying || work == null || frame == Time.frameCount) return;
            frame = Time.frameCount;
            ObserveFrame();
            while (work.Count > 0)
            {
                if (!work.Peek().MoveNext()) { work.Pop(); continue; }
                if (work.Peek().Current is IEnumerator nested) { work.Push(nested); continue; }
                return;
            }
            Finish(null);
        }
        catch (Exception error) { Finish(error); }
    }

    static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        work = null;
        nativeHttpCancellation?.Cancel();
        nativeHttpCase = null;
        if (tts != null) tts.CancelStreaming();
        if (source != null) source.Stop();
        report = report ?? new JObject();
        report["status"] = error == null ? "passed" : "failed";
        if (error != null)
        {
            report["error"] = error.ToString();
            if (activeRequest != null) { activeRequest.Row["status"] = "failed"; activeRequest.Row["failure"] = error.Message; }
        }
        if (frameSamples != null) report["frameSummary"] = SummarizeFrames(frameSamples);
        bool unchanged = Hash(ScenePath) == SessionState.GetString(Key + "SceneHash", "") &&
            Hash(QualityPath) == SessionState.GetString(Key + "QualityHash", "");
        report["savedSceneUnchangedDuringTrial"] = Hash(ScenePath) == SessionState.GetString(Key + "SceneHash", "");
        report["savedQualityUnchangedDuringTrial"] = Hash(QualityPath) == SessionState.GetString(Key + "QualityHash", "");
        if (!unchanged) report["status"] = "failed";
        string output = SessionState.GetString(Key + "Output", "Logs/tts/main-scene-latency-default.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output, report.ToString());
        Debug.Log("[TtsMainSceneLatency/Finish] status=" + report["status"] + " output=" + output);
        SessionState.SetBool(Key + "Passed", error == null && unchanged);
        SessionState.SetBool(Key + "Finished", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Exit();
    }

    static void Exit()
    {
        bool passed = SessionState.GetBool(Key + "Passed", false);
        SessionState.SetBool(Key + "Active", false);
        SessionState.SetBool(Key + "Finished", false);
        QualitySettings.vSyncCount = SessionState.GetInt(Key + "OriginalVSync", QualitySettings.vSyncCount);
        Application.targetFrameRate = SessionState.GetInt(Key + "OriginalTargetFps", Application.targetFrameRate);
        Application.runInBackground = SessionState.GetBool(Key + "OriginalBackground", Application.runInBackground);
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single); // Discard all test-only changes; never save.
        string output = SessionState.GetString(Key + "Output", "Logs/tts/main-scene-latency-default.json");
        var final = File.Exists(output) ? JObject.Parse(File.ReadAllText(output)) : new JObject();
        bool sceneSame = Hash(ScenePath) == SessionState.GetString(Key + "SceneHash", "");
        bool qualitySame = Hash(QualityPath) == SessionState.GetString(Key + "QualityHash", "");
        final["savedSceneUnchangedAfterExit"] = sceneSame;
        final["savedQualityUnchangedAfterExit"] = qualitySame;
        passed &= sceneSame && qualitySame;
        if (!passed) final["status"] = "failed";
        File.WriteAllText(output, final.ToString());
        EditorApplication.Exit(passed ? 0 : 1);
    }

    static string Hash(string path)
    { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", ""); }
    static object Get(object target, string name)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        { var field = type.GetField(name, Flags); if (field != null) return field.GetValue(target); }
        throw new MissingFieldException(target.GetType().Name, name);
    }
    static void Set(object target, string name, object value)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        { var field = type.GetField(name, Flags); if (field != null) { field.SetValue(target, value); return; } }
        throw new MissingFieldException(target.GetType().Name, name);
    }
}
