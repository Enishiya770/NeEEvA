using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>Explicit live, silent comparison; uses existing Qwen/TTS services and saves no scene.</summary>
[InitializeOnLoad]
public static class ActorSpeechLatencyPlayRegression
{
    private const string Key = "NeEEvA.ActorSpeechLatency.";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static ChatSample chat;
    private static ChatQW model;
    private static GPTSoVITSFASTAPI tts;
    private static JObject report;
    private static JArray rows, traces;
    private static TTS.PreparedSpeechStream prepared;
    private static float started, judgeAt, firstPcmAt, readyAt;
    private static int index, generation, lastFrame;
    private static bool running, parallel;
    private static string chunk;
    private static readonly string[] Texts = {
        "这是一次语音准备时间测量。", "这个故事很有趣，后来的发展是什么？",
        "その話は面白いね。続きはどうなったの？", "听起来你今天经历了不少事情。"
    };

    static ActorSpeechLatencyPlayRegression()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) Initialize();
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) Exit();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Finished", false) && SessionState.GetBool(Key + "Active", false) && !EditorApplication.isPlaying) Exit();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Use batch edit mode with existing local TTS and Spark Qwen available.");
        foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
            if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                throw new InvalidOperationException("Refusing to replace a dirty saved scene.");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
        SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 360f);
        EditorApplication.isPlaying = true;
    }

    private static async void Initialize()
    {
        try
        {
            rows = new JArray(); traces = new JArray(); report = new JObject {
                ["scope"] = "Live production ChatSample actor judgement and private first-chunk preparation, Qwen .175:8888 and existing GPT-SoVITS 127.0.0.1:9880. Silent PCM readiness, no audio player or microphone. Measures draft-to-approved-buffer readiness, not EOU-to-audible speech.",
                ["audioPlayback"] = false, ["serviceConfigurationChanged"] = false,
                ["samples"] = rows, ["traces"] = traces, ["status"] = "running"
            };
            index = 0; running = false; lastFrame = -1;
            var host = new GameObject("Actor speech live silent benchmark"); host.SetActive(false);
            chat = host.AddComponent<ChatSample>(); model = host.AddComponent<ChatQW>();
            tts = host.AddComponent<GPTSoVITSFASTAPI>();
            var button = new GameObject("Unused submit", typeof(RectTransform), typeof(Button));
            var text = new GameObject("Unused subtitle", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            button.transform.SetParent(host.transform, false); text.transform.SetParent(host.transform, false);
            Set(chat, "m_CommitMsgBtn", button.GetComponent<Button>()); Set(chat, "m_TextBack", text.GetComponent<Text>());
            var settings = new ChatSetting { m_ChatModel = model }; Set(chat, "m_ChatSettings", settings);
            foreach (string name in new[] { "m_EnableFastTaskLoop", "m_EnableActorIntentCompiler", "m_SceneTimePaused" }) Set(chat, name, true);
            foreach (string name in new[] { "m_AgentRunning", "m_AgentRoundInFlight", "m_EnableNeuralHumSVC", "m_AutoStartHumSVC",
                "m_EnableSingingVoiceSynthesis", "m_AutoStartSVS", "m_ContinuousSceneVision", "m_EnableSubtitleTranslation",
                "m_PersistSubtitleSettings", "m_IsVoiceMode", "m_EnableSceneObjectAwareness", "m_SceneVisionApplicationPaused",
                "m_UserSpeechActiveForAutonomy", "m_EnableScreenVision", "m_EnableSceneActions", "m_LogRawLLMOutput" }) Set(chat, name, false);
            model.m_Backend = ChatQW.BackendType.OpenAICompatible;
            model.m_OpenAICompatibleUrl = "http://192.168.114.175:8888/v1/chat/completions";
            model.m_OpenAICompatibleModelName = "qwen3.6-35b-a3b";
            model.m_ApplyArdyServiceUrlToSceneBridges = false;
            model.m_PrewarmPrefixOnStart = false;
            Set(model, "m_EnableRoleDecisionFastLane", true);
            Set(tts, "m_PostURL", "http://127.0.0.1:9880/tts");
            Set(tts, "m_ReferWavPath", "Model/41041.wav");
            Set(tts, "m_ReferenceText", "夜が明けたら、私と一緒に、この辺りを散歩しましょう。");
            Set(tts, "m_ReferenceTextLan", GPTSoVITSFASTAPI.Language.日文);
            Set(tts, "m_StreamingPrebufferSeconds", .65f);
            Set(tts, "m_StreamingMode", 3); Set(tts, "m_MinStreamingChunkLength", 12);
            host.SetActive(true);
            await System.Threading.Tasks.Task.Yield();
            await System.Threading.Tasks.Task.Yield();
            // Start saw no TTS, so it did not start unrelated warmup/model jobs.
            settings.m_TextToSpeech = tts;
            Set(model, "url", model.m_OpenAICompatibleUrl);
            BeginSample();
        }
        catch (Exception error) { Finish(error); }
    }

    private static void BeginSample()
    {
        if (index >= Texts.Length * 2) { Finish(null); return; }
        int pair = index / 2;
        parallel = pair % 2 == 0 ? index % 2 == 1 : index % 2 == 0;
        generation = 12000 + index;
        Call(chat, "CancelActorIntentRequest");
        foreach (string field in new[] { "m_PendingChunks", "m_PendingClips", "m_SilencedSpeechRoundIds" }) Call(Get(chat, field), "Clear");
        Get(chat, "m_SentenceBuffer").GetType().GetProperty("Length").SetValue(Get(chat, "m_SentenceBuffer"), 0);
        foreach (string name in new[] { "m_FirstChunkFlushed", "m_StreamComplete", "m_TTSSenderDone", "m_RoundIsInner",
            "m_RoundRepeatCheckDone", "m_RoundRepeatHold", "m_RoundSilencedForRepeat", "m_RoundWaitForUser", "m_FirstDeltaLogged" }) Set(chat, name, false);
        Set(chat, "m_FormalResponseGeneration", generation); Set(chat, "m_CurrentSpeechRoundId", generation);
        Set(chat, "m_EnableActorSpeechPreparation", parallel); Set(chat, "m_FormalResponseInFlight", true);
        Set(chat, "m_ResponseTrace", new ConversationTrace("actor-speech-live", value => {
            var item = JObject.Parse(value); item["sampleIndex"] = index; traces.Add(item);
        }));
        var runtime = (FastLoopRuntime)Get(chat, "m_FastLoop");
        long output = runtime.BeginTurn();
        judgeAt = firstPcmAt = readyAt = -1; chunk = null; prepared = null;
        started = Time.realtimeSinceStartup; running = true;
        string language = pair == 2 ? "ja" : "zh";
        Call(chat, "CompileActorDraft", model, "我在听。", Texts[pair], language, generation, false, output);
        if (parallel) prepared = Get(tts, "m_PreparedPcmSpeech") as TTS.PreparedSpeechStream;
        if (parallel && prepared == null) throw new InvalidOperationException("Parallel path did not start private TTS preparation.");
        Debug.Log($"[ActorSpeech/LiveSample] index={index} parallel={parallel} warmup={pair == 0}");
    }

    private static void Tick()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        try
        {
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", 0)) throw new TimeoutException("Live benchmark deadline.");
            if (!EditorApplication.isPlaying || !running || chat == null || lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount;
            float elapsed = Time.realtimeSinceStartup - started;
            if (elapsed > 35f) throw new TimeoutException("Live sample " + index + " timed out.");
            var queue = (ICollection)Get(chat, "m_PendingChunks");
            if (judgeAt < 0 && !(bool)Get(chat, "m_FormalResponseInFlight"))
            {
                judgeAt = elapsed;
                if (queue.Count == 0) throw new InvalidOperationException("Judge did not publish the expected ordinary draft.");
                object first = Call(queue, "Peek");
                chunk = (string)Get(first, "Text");
                if (!Texts[index / 2].StartsWith(chunk, StringComparison.Ordinal)) throw new InvalidOperationException("Judge changed this ordinary test draft.");
                string language = (string)Get(first, "LanguageCode");
                var speech = tts.ResolveSpeech(new SpeechText(chunk, language));
                if (parallel)
                {
                    object adopted = Call(chat, "TakeActorSpeechPreparation", generation, generation, chunk, speech.LanguageCode, tts);
                    if (adopted == null || !ReferenceEquals(Get(adopted, "Stream"), prepared))
                        throw new InvalidOperationException("Approved private stream was not transferred exactly once.");
                }
                else
                {
                    tts.TraceContext = ((ConversationTrace)Get(chat, "m_ResponseTrace")).Child("serial-tts");
                    prepared = tts.PrepareStreamingSpeech(speech);
                }
                if (prepared == null) throw new InvalidOperationException("TTS preparation unavailable.");
            }
            else if (judgeAt < 0 && queue.Count != 0) throw new InvalidOperationException("Draft leaked to public queue before judgement.");
            if (prepared == null) return;
            object pcm = Get(prepared, "Pcm");
            var operation = (UnityWebRequestAsyncOperation)Get(prepared, "Operation");
            var request = (UnityWebRequest)Get(prepared, "Request");
            long samples = Convert.ToInt64(Get(pcm, "TotalSamplesReceived"));
            if (samples > 0 && firstPcmAt < 0) firstPcmAt = elapsed;
            bool ready = (bool)Get(pcm, "HeaderReady") && !(bool)Get(pcm, "FormatError") && samples > 0 &&
                (operation.isDone || Convert.ToDouble(Get(pcm, "BufferedSeconds")) >= Convert.ToDouble(Get(prepared, "PrebufferSeconds")));
            if (ready && readyAt < 0) readyAt = elapsed;
            if (!operation.isDone || judgeAt < 0) return;
            if (request.responseCode != 200 || request.result != UnityWebRequest.Result.Success || readyAt < 0)
                throw new InvalidOperationException("TTS failed: " + request.responseCode + "/" + request.result);
            var row = new JObject { ["index"] = index, ["pair"] = index / 2, ["warmup"] = index < 2,
                ["mode"] = parallel ? "parallel" : "serial", ["chunk"] = chunk,
                ["judgeSeconds"] = judgeAt, ["firstPcmFromDraftSeconds"] = firstPcmAt,
                ["bufferReadyFromDraftSeconds"] = readyAt, ["approvedBufferSeconds"] = Math.Max(judgeAt, readyAt),
                ["ttsStartedFromDraftSeconds"] = (float)Get(prepared, "StartedAt") - started,
                ["requestCompleteSeconds"] = elapsed, ["samePreparedRequestAdopted"] = parallel,
                ["audioPlayback"] = false };
            rows.Add(row); Debug.Log("[ActorSpeech/LiveResult] " + row.ToString(Formatting.None));
            prepared.Dispose(); prepared = null; running = false; ++index;
            BeginSample();
        }
        catch (Exception error) { Finish(error); }
    }

    private static void Finish(Exception error)
    {
        if (SessionState.GetBool(Key + "Finished", false)) return;
        running = false; prepared?.Dispose(); prepared = null;
        if (chat != null) Call(chat, "CancelActorIntentRequest");
        report = report ?? new JObject(); report["status"] = error == null ? "passed" : "failed";
        if (error != null) report["error"] = error.ToString();
        if (error == null)
        {
            var measured = rows.Children<JObject>().Where(r => !(bool)r["warmup"]).ToArray();
            double serial = measured.Where(r => (string)r["mode"] == "serial").Average(r => (double)r["approvedBufferSeconds"]);
            double overlap = measured.Where(r => (string)r["mode"] == "parallel").Average(r => (double)r["approvedBufferSeconds"]);
            report["measuredPairs"] = measured.Length / 2; report["serialMeanSeconds"] = serial;
            report["parallelMeanSeconds"] = overlap; report["meanReductionSeconds"] = serial - overlap;
        }
        Directory.CreateDirectory("Logs/actor-speech");
        File.WriteAllText("Logs/actor-speech/live-latency.json", report.ToString());
        Debug.Log("[ActorSpeech/Live] " + report.ToString(Formatting.None));
        SessionState.SetBool(Key + "Passed", error == null); SessionState.SetBool(Key + "Finished", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Exit();
    }

    private static void Exit()
    {
        bool passed = SessionState.GetBool(Key + "Passed", false);
        SessionState.SetBool(Key + "Active", false); SessionState.SetBool(Key + "Finished", false);
        EditorApplication.Exit(passed ? 0 : 1);
    }

    private static object Get(object target, string name)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField(name, Flags); if (field != null) return field.GetValue(target);
            var property = type.GetProperty(name, Flags); if (property != null) return property.GetValue(target);
        }
        throw new MissingMemberException(target.GetType().Name, name);
    }
    private static void Set(object target, string name, object value)
    {
        for (Type type = target.GetType(); type != null; type = type.BaseType)
        { var field = type.GetField(name, Flags); if (field != null) { field.SetValue(target, value); return; } }
        throw new MissingFieldException(target.GetType().Name, name);
    }
    private static object Call(object target, string name, params object[] values) => target.GetType()
        .GetMethods(Flags).Single(method => method.Name == name && method.GetParameters().Length == values.Length).Invoke(target, values);
}
