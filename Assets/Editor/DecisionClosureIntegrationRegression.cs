using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class DecisionClosureIntegrationRegression
{
    private const string Key = "NeEEvA.DecisionClosureRegression";
    internal static string Output;
    static DecisionClosureIntegrationRegression()
    {
        EditorApplication.playModeStateChanged += state => {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Key, false)) return;
            SessionState.SetBool(Key, false);
            Output = SessionState.GetString(Key + ".output", "");
            new GameObject("DecisionClosureRunner").AddComponent<DecisionClosureRegressionRunner>();
        };
    }
    public static void RunBatch()
    {
        var args = Environment.GetCommandLineArgs();
        string output = Path.GetFullPath("Logs/decision-closure-regression");
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-closureReport") output = Path.GetFullPath(args[i + 1]);
        Directory.CreateDirectory(output);
        SessionState.SetString(Key + ".output", output);
        SessionState.SetBool(Key, true);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorApplication.isPlaying = true;
    }
}

public sealed class DecisionClosureRegressionRunner : MonoBehaviour
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private int checks;
    private void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    private static object Call(object target, string name, params object[] args) =>
        (target is Type type ? type : target.GetType()).GetMethod(name, Flags).Invoke(target is Type ? null : target, args);
    private static void Set(object target, string name, object value) {
        for (Type t = target.GetType(); t != null; t = t.BaseType) {
            var field = t.GetField(name, Flags); if (field != null) { field.SetValue(target, value); return; }
        }
        throw new MissingFieldException(name);
    }
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private IEnumerator Start()
    {
        var report = new JObject();
        try { RunChecks(); report["passed"] = true; }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); Debug.LogException(error); }
        var stack = new System.Collections.Generic.Stack<IEnumerator>();
        if ((bool)report["passed"]) stack.Push(PlaybackChecks());
        while (stack.Count > 0)
        {
            object current = null;
            try {
                var step = stack.Peek();
                if (!step.MoveNext()) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                current = step.Current;
                if (current is IEnumerator nested) { stack.Push(nested); continue; }
            } catch (Exception error) {
                report["passed"] = false; report["error"] = error.ToString(); Debug.LogException(error); break;
            }
            yield return current;
        }
        while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
        report["checks"] = checks;
        report["scope"] = "Actual HTTP wire captured before network send; production playback coroutines with controlled TTS and muted AudioSource. No microphone, network TTS, user scene or human-audibility claim.";
        File.WriteAllText(Path.Combine(DecisionClosureIntegrationRegression.Output, "integration.json"), report.ToString());
        EditorApplication.Exit((bool)report["passed"] ? 0 : 1);
    }
    private IEnumerator Until(Func<bool> ready, string description)
    {
        float deadline = Time.realtimeSinceStartup + 5f;
        while (!ready() && Time.realtimeSinceStartup < deadline) yield return null;
        Check(ready(), "timeout: " + description);
    }
    private IEnumerator PlaybackChecks()
    {
        foreach (bool streaming in new[] { true, false })
        {
            var host = new GameObject("MutedRoundRevocation"); host.SetActive(false);
            var chat = host.AddComponent<ChatSample>(); chat.enabled = false;
            var providerHost = new GameObject("ControlledSpeechProvider");
            var tts = providerHost.AddComponent<FirstReplyFailureAudioFixtureTTS>(); tts.Reset(streaming); tts.Release = true;
            var audio = providerHost.AddComponent<AudioSource>(); audio.mute = true; audio.playOnAwake = false;
            providerHost.AddComponent<AudioListener>();
            var textHost = new GameObject("FixtureText", typeof(RectTransform), typeof(UnityEngine.UI.Text));
            var buttonHost = new GameObject("FixtureButton", typeof(RectTransform), typeof(UnityEngine.UI.Button));
            Set(chat, "m_TextBack", textHost.GetComponent<UnityEngine.UI.Text>());
            Set(chat, "m_CommitMsgBtn", buttonHost.GetComponent<UnityEngine.UI.Button>());
            Set(chat, "m_ChatSettings", new ChatSetting { m_TextToSpeech = tts });
            Set(chat, "m_AudioSource", audio); Set(chat, "m_EnableSubtitleTranslation", false); Set(chat, "m_PersistSubtitleSettings", false);
            Set(chat, "m_FormalResponseGeneration", 700); Set(chat, "m_CurrentSpeechRoundId", 41);
            Set(chat, "m_StreamComplete", true); Set(chat, "m_TTSSenderDone", true); Set(chat, "m_AgentRunning", false);
            Set(chat, "m_InterClipPauseSec", 0f); Set(chat, "m_SceneTimePaused", false);
            Set(chat, "m_ChatHistory", new System.Collections.Generic.List<string>());
            int done = 0; chat.OnAISpeakDone = () => done++;
            host.SetActive(true); Set(chat, "m_SubtitleOverlay", null);
            var clipType = typeof(ChatSample).GetNestedType(streaming ? "PendingSpeechChunk" : "PendingSpeechClip", Flags);
            var queue = Get(chat, streaming ? "m_PendingChunks" : "m_PendingClips");
            var clips = new System.Collections.Generic.List<AudioClip>();
            void Enqueue(int round, string text) {
                var item = Activator.CreateInstance(clipType, true); Set(item, "RoundId", round); Set(item, "Text", text); Set(item, "LanguageCode", "zh");
                if (!streaming) {
                    var clip = AudioClip.Create("Muted regression speech", 24000, 1, 24000, false);
                    clip.SetData(Enumerable.Repeat(.01f, 24000).ToArray(), 0); clips.Add(clip); Set(item, "Clip", clip);
                }
                Call(queue, "Enqueue", item);
            }
            Enqueue(41, "这一句将在播放途中撤回。"); Enqueue(41, "这一句不能继续播。"); Enqueue(42, "另一轮应正常播完。");
            chat.StartCoroutine((IEnumerator)Call(chat, streaming ? "StreamDirectTTSPlayer" : "StreamAudioPlayer", 700));
            try {
                yield return Until(() => (bool)Call(chat, "HasRoundSpeechStarted", 700, 41), "first round sample progress");
                Call(chat, "DiscardPendingSpeechForRound", 41, "regression-in-flight");
                yield return Until(() => done > 0, "pipeline drained after round revocation");
                Check(done == 1 && !chat.IsAISpeaking && !audio.isPlaying, "revocation did not drain exactly once");
                Check((string)Call(chat, "CompletedRoleSpeechForRound", 700, 41) == "", "interrupted chunk counted as completed");
                Check((string)Call(chat, "CompletedRoleSpeechForRound", 700, 42) == "另一轮应正常播完。", "revocation discarded other round");
                Check(!tts.RequestTexts.Contains("这一句不能继续播。"), "revoked queue reached provider");
            } finally {
                chat.StopAllCoroutines(); tts.CancelStreaming(); audio.Stop(); tts.DestroyClips();
                foreach (var clip in clips) if (clip != null) DestroyImmediate(clip);
                DestroyImmediate(host); DestroyImmediate(providerHost); DestroyImmediate(textHost); DestroyImmediate(buttonHost);
            }
        }
    }
    private void RunChecks()
    {
        var host = new GameObject("ClosureFixture"); host.SetActive(false);
        var model = host.AddComponent<ChatQW>(); model.enabled = false;
        model.m_Backend = ChatQW.BackendType.OpenAICompatible;
        model.m_OpenAICompatibleModelName = "qwen3.6-35b-a3b";
        Set(model, "url", "http://127.0.0.1:1/never-send");
        var promptFiles = new[] { new TextAsset("你是温和的陪伴角色，默认用日语回复。") { name = "persona" },
            new TextAsset(File.ReadAllText("Assets/AIChatTookit/Prompts/language.txt")) { name = "language" },
            new TextAsset(File.ReadAllText("Assets/AIChatTookit/Prompts/behavior.txt")) { name = "behavior" } };
        Set(model, "m_PromptFiles", promptFiles);
        host.SetActive(true);
        const string image = "data:image/png;base64,CURRENT_SYNTHETIC_IMAGE";
        const string oldImage = "data:image/png;base64,OLD_SYNTHETIC_IMAGE";
        const string user = "把刚才唱的这一段唱给我听。";
        Action Reset = () => {
            model.CancelActiveResponse(); model.m_DataList.Clear();
            model.m_DataList.Add(new LLM.SendData("system", model.BuildSystemPrompt()));
            model.m_DataList.Add(new LLM.SendData("user", "旧的一轮") { imageDataUrl = oldImage });
            model.m_DataList.Add(new LLM.SendData("assistant", "<lang code=\"ja\"/><speech mode=\"independent\"/><say>うん。</say>"));
            model.RequestContext = "[本轮实际事实] 尚未播放；保留这条事实。" +
                "\n[真实录音素材] [{\"clip\":\"clip:synthetic\",\"revision\":2,\"playback\":\"ready\",\"description\":\"本轮刚才的旋律\"}]";
            model.ActiveSkillContext = "[已加载技能: singing]\n" + File.ReadAllText("Assets/AIChatTookit/Prompts/Skills/singing.txt");
        };
        string wire = null;
        Action<string> capture = json => { wire = json; model.CancelActiveResponse(); };
        model.OnRequestDiagnostic += capture;
        foreach (var stage in new[] { FormalResponseStage.General, FormalResponseStage.CommittedSpatialReply, FormalResponseStage.RecordingSingingDecision })
        {
            Reset(); wire = null;
            model.RecordingDecisionContext = new RecordingSingingDecisionContext { UserText = user, Facts = "未播放" };
            model.RecordingDecisionContext.Materials.Add(new RecordingSingingMaterial { Key = "material_1", ClipRef = "clip:synthetic", Revision = "2", PlaybackStatus = "ready", Description = "本轮刚才的旋律" });
            int callbacks = 0;
            model.PostSpeechStreamForStage(user, p => callbacks++, p => callbacks++, image, true, stage);
            Check(wire != null, "public stage produced no HTTP wire: " + stage);
            var json = JObject.Parse(wire);
            var images = json.SelectTokens("$..image_url.url").Select(t => (string)t).ToArray();
            Check(images.Length == 1 && images[0] == image && !wire.Contains(oldImage), "current image lost or duplicated: " + stage);
            Check(wire.Contains(user) && wire.Contains("尚未播放；保留这条事实"), "user/facts lost: " + stage);
            Check(callbacks == 0, "cancelled request called a consumer");
            Check(model.m_DataList.Count(m => m.role == "user" && m.content == user) == 1, "user duplicated");
            if (stage != FormalResponseStage.General) Check((string)json["response_format"]?["type"] == "json_schema", "structured stage lost schema");
            if (stage == FormalResponseStage.RecordingSingingDecision) {
                Check(!wire.Contains("<sing_goal") && wire.Contains("material_1"), "singing still asks model to write tags");
            }
            File.WriteAllText(Path.Combine(DecisionClosureIntegrationRegression.Output, "wire-" + stage + ".json"), wire);
        }
        // Re-enter the ordinary stage after singing: no stale schema or binding leaks.
        Reset(); wire = null;
        model.PostSpeechStreamForStage("普通对话", p => {}, p => {}, null, true, FormalResponseStage.General);
        Check(wire != null && !wire.Contains("recording_singing_decision") && !wire.Contains(image), "prior schema/image leaked");
        model.OnRequestDiagnostic -= capture;
        Check(model.RecordingDecisionContext == null, "request snapshot was not consumed");

        string roleWire = (string)Call(model, "BuildRoleDecisionRequestJson", "用户要求用中文回应。", null);
        File.WriteAllText(Path.Combine(DecisionClosureIntegrationRegression.Output, "wire-role.json"), roleWire);
        Check((string)JObject.Parse(roleWire)["response_format"]["json_schema"]["name"] == "role_decision_v3", "role schema missing");
        foreach (string final in new[] { "好吧，没有唱出来。测试到此为止，拜拜。", "不要唱", "", "那我继续唱喽" }) {
            object[] args = { final, "重复的partial。重复的partial。请你现在唱。", true, null };
            Check((string)Call(typeof(ChatSample), "ResolveAuthoritativeUserSemanticText", args) == final, "partial overwrote final");
        }
        var chatHost = new GameObject("InactiveWorkFixture"); chatHost.SetActive(false);
        var chat = chatHost.AddComponent<ChatSample>();
        Set(chat, "m_LastUserMsg", "测试就到此为止，拜拜。"); Set(chat, "m_WorkReviewOpen", true); Set(chat, "m_HumBackResultPending", true);
        Check(!(bool)Call(chat, "ApplyRoleWorkCancellation", "不在当前原话中"), "ungrounded cancellation accepted");
        Check((bool)Call(chat, "ApplyRoleWorkCancellation", "测试就到此为止"), "grounded cancellation rejected");
        Check(!(bool)Call(chat, "ShouldReviewUserWork"), "cancelled work still scheduled for review");
        // HTTP transport success is not SVC readiness.
        Check(!(bool)Call(chat, "RecordSvcHealthObservation", true, true, "{\"ok\":false,\"rvc_missing\":[\"model\",\"index\",\"vendor\"]}"), "unready 200 accepted");
        Check((bool)Get(chat, "m_ObservedSvcReachable") && !(bool)Get(chat, "m_ObservedSvcHealthy"), "health conflates reachability");
        Check(!(bool)Call(chat, "RecordSvcHealthObservation", true, true, "{}"), "unknown health accepted");
        Check(!(bool)Call(chat, "RecordSvcHealthObservation", true, false, "{\"ok\":true}"), "failed transport accepted");
        Check((bool)Call(chat, "RecordSvcHealthObservation", true, true, "{\"ok\":true}"), "valid health rejected");
        Call(chat, "RecordSvcWarmupObservation", false, "{\"ok\":false,\"detail\":\"missing model\"}");
        Check(!(bool)Get(chat, "m_ObservedSvcWorkerReady"), "explicit warmup failure lost");

        // Queues belong to individual rounds even when a continuation shares a pipeline.
        var chunkType = typeof(ChatSample).GetNestedType("PendingSpeechChunk", Flags);
        object Chunk(int round, string text) { var item = Activator.CreateInstance(chunkType, true); Set(item, "RoundId", round); Set(item, "Text", text); return item; }
        var queue = Get(chat, "m_PendingChunks");
        Call(queue, "Enqueue", Chunk(20, "keep")); Call(queue, "Enqueue", Chunk(21, "revoke"));
        Call(chat, "RegisterActuallySpokenRound", 7, 21);
        Check((bool)Call(chat, "HasRoundSpeechStarted", 7, 21), "actual playback lost when agent inactive");
        Check((string)Call(chat, "CompletedRoleSpeechForRound", 7, 21) == "", "started audio invented full text");
        Call(chat, "RecordCompletedRoleSpeech", 7, 21, "私に歌ってほしいのかしら？");
        Call(chat, "DiscardPendingSpeechForRound", 21, "regression");
        queue = Get(chat, "m_PendingChunks");
        Check((int)queue.GetType().GetProperty("Count").GetValue(queue) == 1 && (int)Get(Call(queue, "Peek"), "RoundId") == 20, "rejection affected another round");
        Check((string)Call(chat, "CompletedRoleSpeechForRound", 7, 21) == "私に歌ってほしいのかしら？", "rejection erased played speech");
        Check(Call(chat, "BuildRecentRoleDialogue").ToString().Contains("私に歌って"), "context lost actual prior question");

        // Fresh, explicitly non-speech tail can release a pending old-text revision.
        TurnActivityEvidence Quiet(bool vad, int speechMs, float end) {
            var e = new TurnActivityEvidence(); e.Reset(0);
            e.ObserveAsr("旧词", "", 1000, 1, true, 0, 0, 0, .001f, true, 0, -1, 1);
            e.ObserveAsr("修订词", "", 2500, 2.5f, true, 0, 0, 0, .001f, vad, speechMs, -1, end);
            return e;
        }
        var quiet = Quiet(true, 0, 2.5f);
        Check(quiet.PendingText && quiet.CanClosePendingTextOnQuietTail(2.5f, 1.5f), "fresh quiet cannot release old revision");
        Check(quiet.PendingText, "capture closure promoted partial to authority");
        Check(!quiet.CanClosePendingTextOnQuietTail(5f, 1.5f), "stale tail permits closure");
        Check(!Quiet(false, 0, 2.5f).CanClosePendingTextOnQuietTail(2.5f, 1.5f), "missing VAD permits closure");
        Check(!Quiet(true, 200, 2.5f).CanClosePendingTextOnQuietTail(2.5f, 1.5f), "speech tail permits closure");
        Check(!Quiet(true, 0, 1f).CanClosePendingTextOnQuietTail(2.5f, 1.5f), "uncovered audio permits closure");
        for (float t=2.55f; t<3f; t+=.06f) quiet.ObserveLevel(.04f, t, .002f, .001f);
        Check(!quiet.CanClosePendingTextOnQuietTail(3f, 1.5f), "renewed sound permits closure");

        // A reviewed recording continuation consumes a real snapshot and retains current vision.
        Reset(); wire = null;
        model.OnRequestDiagnostic += capture;
        model.RecordingDecisionContext = new RecordingSingingDecisionContext { UserText = user, Facts = "尚未播放；保留这条事实。" };
        model.RecordingDecisionContext.Materials.Add(new RecordingSingingMaterial { Key = "material_1", ClipRef = "clip:synthetic", Revision = "2", PlaybackStatus = "ready" });
        Call(model, "BeginSpeechContinuation", "继续处理原请求", null, (Action<string>)(p => {}), image, (Action<SpeechText>)(p => {}), "审核已通过", true);
        Check(wire != null && wire.Contains("recording_singing_decision") && wire.Contains(image), "review continuation did not enter recording IR with vision");
        Check(model.RecordingDecisionContext == null, "continuation snapshot leaked");
        model.OnRequestDiagnostic -= capture;

        // Optional provider timings: absence stays null; explicit zero stays zero.
        var perfType = typeof(ChatQW).GetNestedType("CompletionPerformance", Flags);
        var perf = Activator.CreateInstance(perfType, true);
        Call(perf, "Observe", "{\"usage\":{\"prompt_tokens\":10}}");
        Check(((JObject)Call(perf, "Snapshot"))["queueMilliseconds"].Type == JTokenType.Null, "missing queue fabricated");
        Call(perf, "Observe", "{\"timings\":{\"queue_ms\":0,\"prompt_ms\":12.5,\"cache_n\":8}}");
        Check((double)((JObject)Call(perf, "Snapshot"))["queueMilliseconds"] == 0, "reported queue lost");
        DestroyImmediate(chatHost); DestroyImmediate(host);
        foreach (var asset in promptFiles) DestroyImmediate(asset);
    }
}
