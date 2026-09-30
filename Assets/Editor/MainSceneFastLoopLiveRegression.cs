using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>Explicit opt-in trial of the saved main scene with live services; no scene or user-memory writes.</summary>
[InitializeOnLoad]
public static class MainSceneFastLoopLiveRegression
{
    const string Key = "NeEEvA.MainSceneFastLoopTrial.";
    const string ScenePath = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static ChatSample chat;
    static ChatQW model;
    static GPTSoVITSFASTAPI tts;
    static AudioSource source;
    static JObject report, row;
    static JArray rows, traces;
    static Stack<IEnumerator> work;
    static float turnStarted;
    static int frame = -1;

    static MainSceneFastLoopLiveRegression()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) Begin();
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetBool(Key + "Finished", false)) Exit();
        };
        EditorApplication.delayCall += () => {
            if (SessionState.GetBool(Key + "Active", false) && SessionState.GetBool(Key + "Finished", false) && !EditorApplication.isPlaying) Exit();
        };
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Use a separate batch Editor with graphics and existing local services.");
        foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
            if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                throw new InvalidOperationException("Refusing to replace a dirty saved scene.");
        SessionState.SetString(Key + "SceneHash", Hash());
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var chats = SceneObjects<ChatSample>().ToArray();
        if (chats.Length != 1) throw new InvalidOperationException("Expected exactly one main-scene ChatSample.");
        chat = chats[0];
        if (!(bool)Get(chat, "m_EnableFastTaskLoop") || !(bool)Get(chat, "m_EnableActorIntentCompiler") ||
            !(bool)Get(chat, "m_EnableActorSpeechPreparation"))
            throw new InvalidOperationException("Saved scene must already enable the three trial features.");
        var settings = (ChatSetting)Get(chat, "m_ChatSettings");
        // MemoryHub.Awake applies decay and OnApplicationQuit saves. Prevent Awake,
        // not just its Update, by disabling its separate object before Play.
        foreach (var memory in SceneObjects<MonoBehaviour>().Where(x => x.GetType().Name == "MemoryHub"))
        {
            if (memory.gameObject == chat.gameObject) throw new InvalidOperationException("Memory hub needs an independent object for this trial.");
            memory.gameObject.SetActive(false);
        }
        Set(chat, "m_MemoryHub", null); settings.m_SpeechToText = null;
        foreach (var component in SceneObjects<MonoBehaviour>().ToArray())
        {
            string name = component.GetType().Name;
            if (component is STT || name == "RTSpeechHandler" || name == "VoiceInputs") component.enabled = false;
        }
        foreach (string name in new[] { "m_AutoStartHumSVC", "m_AutoStartSVS", "m_EnableMemoryRecall",
            "m_EnableIdleMemoryConsolidation", "m_EnableSubtitleTranslation", "m_PersistSubtitleSettings",
            "m_ContinuousSceneVision", "m_EnableLatencyFiller", "m_AgentRunning", "m_LogRawLLMOutput" }) Set(chat, name, false);
        Set(chat, "m_LogStreamTimings", true);
        var presentation = chat.GetComponent<NeEEvA.Presentation.CompanionPresentation>() ??
            chat.gameObject.AddComponent<NeEEvA.Presentation.CompanionPresentation>();
        presentation.enabled = false; // Avoid creating a desktop overlay window in the unattended trial.
        model = settings.m_ChatModel as ChatQW; tts = settings.m_TextToSpeech as GPTSoVITSFASTAPI;
        if (model == null || tts == null) throw new InvalidOperationException("Actual scene Qwen and GPT-SoVITS components required.");
        model.m_PrewarmPrefixOnStart = false;
        Set(tts, "m_WarmUpLatencyFillerVariants", false);
        foreach (var audio in SceneObjects<AudioSource>()) audio.mute = true;
        SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
        SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 600f);
        EditorApplication.isPlaying = true;
    }

    static IEnumerable<T> SceneObjects<T>() where T : Component =>
        SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<T>(true));

    static void Begin()
    {
        try
        {
            chat = SceneObjects<ChatSample>().Single();
            var settings = (ChatSetting)Get(chat, "m_ChatSettings");
            model = (ChatQW)settings.m_ChatModel; tts = (GPTSoVITSFASTAPI)settings.m_TextToSpeech;
            source = (AudioSource)Get(chat, "m_AudioSource"); source.mute = true;
            Application.runInBackground = true;
            rows = new JArray(); traces = new JArray();
            report = new JObject { ["status"] = "running", ["scene"] = ScenePath,
                ["scope"] = "Saved main scene, real Qwen/TTS/VRM/controllers and production typed input/player. AudioSource sample progress while muted; no microphone, ASR, physical audibility or autonomous agent test. Test-only isolation is never saved.",
                ["sceneHash"] = SessionState.GetString(Key + "SceneHash", ""),
                ["microphoneUsed"] = false, ["audioMuted"] = true, ["memoryWritesEnabled"] = false,
                ["rows"] = rows, ["traces"] = traces, ["semanticDeviations"] = new JArray() };
            Application.logMessageReceived += OnLog;
            model.OnRequestDiagnostic += OnWire;
            work = new Stack<IEnumerator>(); work.Push(Run()); frame = -1;
        }
        catch (Exception error) { Finish(error); }
    }

    static void OnLog(string message, string stack, LogType type)
    {
        const string prefix = "[ConversationTrace] ";
        if (!message.StartsWith(prefix, StringComparison.Ordinal) || traces == null) return;
        try
        {
            var item = JObject.Parse(message.Substring(prefix.Length));
            item["trialCase"] = (string)row?["name"] ?? "startup";
            traces.Add(item);
            if (row == null) return;
            string name = (string)item["event"];
            if (name == "fast_loop.decision") row["fastDecision"] = item["fields"].DeepClone();
            if (name == "actor_intent.decision") row["actorDecision"] = item["fields"].DeepClone();
            if (name == "actor_speech.adopt") row["preparedAudioAdopted"] = true;
            if (name == "audio.formal_playback_advanced" && row["firstAudioSeconds"] == null)
            {
                row["firstAudioSeconds"] = Time.realtimeSinceStartup - turnStarted;
                row["samplePositionAtFirstAudio"] = source != null ? source.timeSamples : -1;
            }
        }
        catch (JsonException) { }
    }

    static void OnWire(string wire)
    {
        if (row == null) return;
        try
        {
            var request = JObject.Parse(wire);
            var messages = request["messages"] as JArray;
            JToken finalContent = messages?.Last?["content"];
            string content = finalContent is JArray parts
                ? string.Concat(parts.Where(part => (string)part["type"] == "text").Select(part => (string)part["text"]))
                : (string)finalContent;
            // Multimodal AppendMessage prepends an attachment-evidence note.
            // Keep the same diagnostic JSON projection without retaining pixels.
            if (!string.IsNullOrEmpty(content) && content[0] != '{')
            {
                int jsonStart = content.IndexOf('{');
                if (jsonStart >= 0) content = content.Substring(jsonStart);
            }
            if (string.IsNullOrEmpty(content) || content[0] != '{') return;
            var input = JObject.Parse(content);
            // Only retain the synthetic current-user field and phase, never the
            // system persona, credential headers or unrelated diagnostic payloads.
            if (input["current_user"]?.Type == JTokenType.String)
            {
                var inputs = row["requestInputs"] as JArray;
                if (inputs == null) row["requestInputs"] = inputs = new JArray();
                inputs.Add(new JObject { ["phase"] = request["response_format"]?["json_schema"]?["name"]?.DeepClone(),
                    ["currentUser"] = input["current_user"].DeepClone(),
                    ["matchesSubmittedInput"] = (string)input["current_user"] == (string)row["input"] });
            }
            if (input["draft"]?.Type == JTokenType.String && !string.IsNullOrEmpty((string)input["draft"]))
            {
                row["actorDraft"] = input["draft"].DeepClone();
                Debug.Log("[MainSceneFastLoop/Draft] " + (string)input["draft"]);
            }
        }
        catch (JsonException) { }
    }

    static FastLoopTask Head => ((FastLoopRuntime)Get(chat, "m_FastLoop")).HeadTask;
    static ActorWaitingTaskRuntime Waiting => (ActorWaitingTaskRuntime)Get(chat, "m_ActorWaiting");
    static NevaSceneAttention Attention => Get(chat, "m_SceneAttention") as NevaSceneAttention;
    static JObject HeadFact() => new JObject { ["id"] = Head?.Id, ["revision"] = Head?.Revision,
        ["state"] = Head?.State, ["candidate"] = Head?.Candidate,
        ["yaw"] = Attention?.CurrentHeadAngles.x, ["pitch"] = Attention?.CurrentHeadAngles.y,
        ["owner"] = Attention?.OwnerId, ["settled"] = Attention?.IsSettled };

    static IEnumerator Run()
    {
        // Let actual Start methods bind the room controller, avatar and audio.
        yield return Delay(2f);
        yield return Until(() => chat.RoomMotionOutputEnabled && chat.SceneVisionCapture?.Target != null,
            45f, "Actual main-scene room/vision binding unavailable");
        var avatar = chat.SceneVisionCapture.Target;
        var eyes = avatar.GetComponent<NevaEyeTracking>();
        Require(eyes != null && eyes.IsInitialized && chat.SceneEyesOpen, "Real scene eyes/head binding required.");
        report["avatar"] = avatar.name; report["roomMotionOutputEnabled"] = chat.RoomMotionOutputEnabled;
        float warmupStart = Time.realtimeSinceStartup;
        yield return Until(() => !(bool)Get(tts, "m_IsWarmingUp"), 90f, "Startup TTS warmup did not finish");
        report["startupWarmupWaitSeconds"] = Time.realtimeSinceStartup - warmupStart;

        StartTurn("greeting-warmup", "晚上好。请用一句简短的日语回应。");
        yield return FinishSpeech();
        StartTurn("ordinary-ja", "今天有点累，能用一句简短的日语安慰我吗？");
        yield return FinishSpeech();
        StartTurn("ordinary-zh", "现在请用一句中文告诉我，休息时可以做什么？");
        yield return FinishSpeech();

        StartTurn("head-right-hold", "现在请把头稍微向右转，并保持这个头部姿势。");
        yield return Until(() => Head?.Candidate == "head.right" && Head.State == "holding", 45f, "Right-head task was not physically held");
        yield return FinishSpeech();
        Require(Attention.IsSettled, "Right task needs actual settled controller.");

        StartTurn("interrupt-head-in-motion", "现在请把头向左转。");
        yield return Until(() => Head?.Candidate == "head.left" && Head.State == "starting", 45f, "No left-head starting window observed");
        string oldId = Head.Id; int oldRevision = Head.Revision;
        chat.NotifyUserStartedSpeaking();
        row["interruptedBeforeSettle"] = !Attention.IsSettled;
        Require(!source.isPlaying && !(bool)Get(chat, "m_FormalResponseInFlight"), "Barge-in must stop old speech and request.");
        yield return Delay(.12f);
        Require(Head.Id == oldId && Head.Revision == oldRevision && Head.Active,
            "Beginning new speech must preserve the independent accepted head task.");
        row["headSurvivedBargeIn"] = true; FinishRow();
        StartTurn("replace-head-after-barge-in", "我改主意了，请把头向右转，并保持。", true);
        yield return Until(() => Head?.Candidate == "head.right" && Head.State == "holding" &&
            (Head.Id != oldId || Head.Revision != oldRevision), 45f, "New intent did not replace old head task");
        yield return FinishSpeech();

        StartTurn("wait-for-user-count-three", "先不要抬头。等我从一数到三以后，你再抬头并保持。数完之前先等着。");
        yield return Until(() => Waiting.Pending?.State == "waiting", 45f, "Count-to-three plan was not registered by live decisions");
        yield return FinishSpeech();
        row["requestedWaitAction"] = "head.raise"; row["selectedWaitAction"] = Waiting.Pending.Action;
        if (Waiting.Pending.Action != "head.raise")
        {
            // Preserve the model's semantic deviation. A follow-up is additional
            // evidence, never a replacement for or regrading of the first answer.
            ((JArray)report["semanticDeviations"]).Add(new JObject { ["case"] = (string)row["name"],
                ["requestedAction"] = "head.raise", ["selectedAction"] = Waiting.Pending.Action,
                ["draft"] = row["actorDraft"]?.DeepClone(), ["requiresClarification"] = true });
            StartTurn("clarify-wait-target", "我确认一下：我希望你在我数到三之后再抬头，而不是继续保持原姿势。如果你接受，请明确说清数到三以后要做的动作，现在先别抬头。");
            yield return Until(() => Waiting.Pending?.State == "waiting" && Waiting.Pending.Action == "head.raise",
                45f, "Live role did not confirm the requested future head action after clarification");
            yield return FinishSpeech();
        }
        string waitingId = Waiting.Pending.Id; string priorHeadId = Head?.Id; int priorHeadRevision = Head?.Revision ?? 0;
        StartTurn("count-one", "一。"); yield return FinishSilentCount();
        Require(Waiting.Pending?.Id == waitingId && Waiting.Pending.State == "waiting" &&
            Head?.Id == priorHeadId && (Head?.Revision ?? 0) == priorHeadRevision, "One must not trigger or replace waiting head motion.");
        StartTurn("count-two", "二。"); yield return FinishSilentCount();
        Require(Waiting.Pending?.Id == waitingId && Waiting.Pending.State == "waiting" &&
            Head?.Id == priorHeadId && (Head?.Revision ?? 0) == priorHeadRevision, "Two must not trigger or replace waiting head motion.");
        StartTurn("count-three-triggers-head", "三。");
        yield return Until(() => Head?.Candidate == "head.raise" && Head.State == "holding", 45f, "Third count did not produce actual held raise");
        yield return FinishSpeech();

        StartTurn("interrupt-real-speech", "请用日语连续讲三句话，描述一个安静的夜晚。这次不要改变头部姿势。");
        yield return Until(() => row["firstAudioSeconds"] != null && source.isPlaying && source.timeSamples > 0,
            60f, "No actual AudioSource progression for speech interruption");
        yield return Delay(.2f);
        row["samplesBeforeInterrupt"] = source.timeSamples;
        int generation = (int)Get(chat, "m_FormalResponseGeneration");
        chat.NotifyUserStartedSpeaking();
        yield return Delay(.2f);
        Require(!source.isPlaying && (int)Get(chat, "m_FormalResponseGeneration") > generation &&
            ((ICollection)Get(chat, "m_PendingChunks")).Count == 0 && Get(chat, "m_ActorSpeechPreparation") == null,
            "Speaking interruption must revoke old audio, generation and prepared cache.");
        row["interruptionStoppedAudio"] = true; FinishRow();
        StartTurn("release-after-interrupt", "不用继续讲了。解除头部保持，恢复自然姿势。", true);
        yield return Until(() => Head == null || !Head.Active, 45f, "Release did not close head task");
        yield return FinishSpeech();
        yield return Delay(.7f);
        Require(!source.isPlaying && Get(chat, "m_ActorSpeechPreparation") == null,
            "Late callbacks must not restart old audio after final turn.");
        report["lateAudioRestarted"] = false;
    }

    static void StartTurn(string name, string input, bool afterSpeaking = false)
    {
        if (afterSpeaking) chat.PrepareUserTurn(input);
        row = new JObject { ["name"] = name, ["input"] = input, ["beforeHead"] = HeadFact(),
            ["preparedAudioAdopted"] = false, ["status"] = "running" };
        rows.Add(row); turnStarted = Time.realtimeSinceStartup;
        var field = (InputField)Get(chat, "m_InputWord"); field.text = input;
        chat.SendData();
        row["generation"] = (int)Get(chat, "m_FormalResponseGeneration");
        Debug.Log("[MainSceneFastLoop/Start] " + name);
    }

    static IEnumerator FinishSpeech()
    {
        yield return Until(() => row["firstAudioSeconds"] != null, 60f, "No production formal AudioSource progression: " + row["name"]);
        yield return Until(() => !(bool)Get(chat, "m_FormalResponseInFlight") && (bool)Get(chat, "m_StreamComplete") &&
            !source.isPlaying && !chat.IsAISpeaking && ((ICollection)Get(chat, "m_PendingChunks")).Count == 0,
            60f, "Speech pipeline did not drain: " + row["name"]);
        FinishRow();
    }

    static IEnumerator FinishSilentCount()
    {
        yield return Until(() => !(bool)Get(chat, "m_FormalResponseInFlight") && (bool)Get(chat, "m_StreamComplete"),
            30f, "Counting event did not resolve the formal turn");
        Require(row["fastDecision"] == null && row["actorDraft"] == null && row["firstAudioSeconds"] == null &&
            !source.isPlaying && ((ICollection)Get(chat, "m_PendingChunks")).Count == 0,
            "A partial counting event must not produce a self-counting reply or new motion proposal.");
        row["countConsumedSilently"] = true; FinishRow();
    }

    static void FinishRow()
    {
        Require((row["requestInputs"] as JArray)?.Count > 0 &&
            ((JArray)row["requestInputs"]).All(input => (bool)input["matchesSubmittedInput"]),
            "Every live decision request must receive this turn's exact submitted input.");
        row["status"] = "passed"; row["afterHead"] = HeadFact();
        row["waitingState"] = Waiting.Pending?.State; row["waitingProgress"] = Waiting.Pending?.Progress;
        row["totalSeconds"] = Time.realtimeSinceStartup - turnStarted;
        Debug.Log("[MainSceneFastLoop/Result] " + row.ToString(Formatting.None));
    }
    static IEnumerator Until(Func<bool> condition, float timeout, string failure)
    {
        float start = Time.realtimeSinceStartup;
        while (!condition()) { if (Time.realtimeSinceStartup - start > timeout) throw new TimeoutException(failure); yield return null; }
    }
    static IEnumerator Delay(float seconds)
    { float start = Time.realtimeSinceStartup; while (Time.realtimeSinceStartup - start < seconds) yield return null; }
    static void Require(bool valid, string reason) { if (!valid) throw new InvalidOperationException(reason); }

    static void Tick()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        try
        {
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", 0)) throw new TimeoutException("Trial deadline");
            if (!EditorApplication.isPlaying || work == null || frame == Time.frameCount) return;
            frame = Time.frameCount;
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
        Application.logMessageReceived -= OnLog; work = null;
        if (model != null) model.OnRequestDiagnostic -= OnWire;
        report = report ?? new JObject();
        report["status"] = error != null ? "failed" :
            (report["semanticDeviations"] as JArray)?.Count > 0 ? "passed_with_semantic_clarification" : "passed";
        report["executionPassed"] = error == null;
        if (error != null) { report["error"] = error.ToString(); if (row != null) { row["status"] = "failed"; row["failure"] = error.Message; } }
        bool unchanged = Hash() == SessionState.GetString(Key + "SceneHash", "");
        report["savedSceneUnchangedDuringTrial"] = unchanged;
        if (!unchanged) report["status"] = "failed";
        Directory.CreateDirectory("Logs/fast-loop");
        File.WriteAllText("Logs/fast-loop/main-scene-live.json", report.ToString());
        Debug.Log("[MainSceneFastLoop/Finish] status=" + report["status"] + " error=" + (error?.Message ?? ""));
        if (chat != null) chat.Interrupt();
        SessionState.SetBool(Key + "Passed", error == null && unchanged); SessionState.SetBool(Key + "Finished", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Exit();
    }
    static void Exit()
    {
        bool passed = SessionState.GetBool(Key + "Passed", false);
        SessionState.SetBool(Key + "Active", false); SessionState.SetBool(Key + "Finished", false);
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single); // Discard test-only edit-mode changes.
        EditorApplication.Exit(passed ? 0 : 1);
    }
    static string Hash()
    { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(ScenePath))).Replace("-", ""); }
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
