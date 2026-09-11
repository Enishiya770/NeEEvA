using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Exercises the final ASR callback, request assembly and streaming queue with public
// synthetic speech only. Transport is replaced; runtime routing/parsing is not.
public static class ChatLatencyRegression
{
    private const string Key = "NeEEvA.ChatLatency.PlayMode";
    private const string ExportKey = Key + ".ExportOnly";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static IEnumerator routine;
    private static Fixture fixture;
    private static int checks;
    private static string failure;
    private static bool exportOnly;
    private static readonly JArray scenarios = new JArray();

    [InitializeOnLoadMethod]
    private static void Resume()
    {
        EditorApplication.playModeStateChanged -= Changed;
        EditorApplication.playModeStateChanged += Changed;
        if (SessionState.GetBool(Key, false) && EditorApplication.isPlaying)
            EditorApplication.delayCall += Begin;
    }

    public static void RunBatch() => Start(false);
    // Can be copied to an earlier isolated public runtime to capture comparable
    // requests. This mode never reports a regression pass or a measured speedup.
    public static void RunExportBatch() => Start(true);

    private static void Start(bool onlyExport)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SessionState.SetBool(Key, true); SessionState.SetBool(ExportKey, onlyExport);
        EditorApplication.EnterPlaymode();
    }

    private static void Changed(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Key, false)) Begin();
    }

    private static void Begin()
    {
        if (routine != null) return;
        checks = 0; failure = null; scenarios.Clear(); exportOnly = SessionState.GetBool(ExportKey, false);
        routine = Run(); EditorApplication.update += Step;
    }

    private static void Step()
    {
        bool finished;
        try { finished = !routine.MoveNext(); }
        catch (Exception error) { failure = error.ToString(); finished = true; }
        if (!finished) return;
        EditorApplication.update -= Step; SessionState.SetBool(Key, false);
        JObject incomplete = fixture == null ? null : fixture.Snapshot();
        fixture?.Dispose(); fixture = null;
        string reports = Path.GetFullPath("Tools/MotionAdapter/reports");
        Directory.CreateDirectory(reports);
        var report = new JObject {
            ["passed"] = !exportOnly && failure == null, ["exportOnly"] = exportOnly,
            ["exportSucceeded"] = failure == null, ["checks"] = checks, ["error"] = failure,
            ["scope"] = "Public synthetic final-ASR input, actual production ChatSample orchestration / ChatQW request assembly / RoleOutputChannels / TTS input queue. Scripted deltas and no transport, microphone, private scene, audio clip, physical speaker or wall-clock latency claim.",
            ["scenarios"] = scenarios, ["incompleteScenario"] = incomplete
        };
        File.WriteAllText(Path.Combine(reports, "chat-latency-regression.json"), report + "\n");
        File.WriteAllText(Path.Combine(reports, "chat-latency-public-requests.json"), new JObject {
            ["publicSyntheticInputsOnly"] = true, ["exportOnly"] = exportOnly,
            ["note"] = "Compare final wire requests / tokenizer counts. Character counts are not tokens, cache reuse or user-perceived latency.",
            ["scenarios"] = scenarios.DeepClone()
        } + "\n");
        EditorApplication.Exit(failure == null ? 0 : 1);
    }

    private static IEnumerator Run()
    {
        foreach (IEnumerator scenario in new[] { ConsecutiveChat(), AcousticHearingWithoutRequest(), MixedHearingThenChat(), RestartThenChat(), UnreviewedAction() })
            while (scenario.MoveNext()) yield return scenario.Current;
    }

    private static IEnumerator AcousticHearingWithoutRequest()
    {
        fixture = new Fixture("acoustic_hearing_without_topic_or_action_request"); var f = fixture; yield return null;
        f.Submit("青い空。静かな夜。", true);
        Verify(f.SingingLoaded, "A current acoustic singing observation was lost when the lyric had no singing-topic keyword.");
        Verify(!(bool)Call(f.chat, "HasPendingSingingGoalReview") && !(bool)Get(f.chat, "m_HumBackPending"), "Acoustic observation alone created a singing obligation.");
        f.StreamIndependent("青い空と静かな夜を歌ったんだね。柔らかな雰囲気だね。");
        Save(); yield return null;
    }

    private static IEnumerator ConsecutiveChat()
    {
        fixture = new Fixture("greeting_and_consecutive_chat"); var f = fixture; yield return null;
        foreach (var sample in new[] {
            new[] { "早上好。", "おはよう。今日はどんな気分？" },
            new[] { "只是想和你聊聊天。", "いいよ。ゆっくり話そう。" },
            new[] { "最近天气不错的。", "そうだね。散歩も気持ちよさそう。" }
        })
        {
            f.Submit(sample[0]);
            Verify(!f.SingingLoaded, "Measurement prose alone loaded the detailed singing skill.");
            Verify((string)Get(f.chat, "m_LastUserMsg") == sample[0], "Decorated hearing evidence became the authoritative user utterance.");
            Verify(f.model.LastRequest.ToString().Contains("歌唱概率"), "The low-probability acoustic observation was deleted to obtain a faster route.");
            Verify(CurrentUser(f.model.LastRequest).Contains(sample[0]), "The final formal request lost the user's actual words.");
            AssertSingleObservationFrame(f.model.LastRequest);
            f.StreamIndependent(sample[1]);
        }
        Verify(f.model.Requests.Count == 3 && f.model.Reviews == 0, "Ordinary chat needed a serial model review before the formal response.");
        Save(); yield return null;
    }

    private static IEnumerator MixedHearingThenChat()
    {
        fixture = new Fixture("mixed_lyrics_tail_request_then_weather"); var f = fixture; yield return null;
        const string mixed = "我试着唱一段。青い空。静かな夜。请告诉我歌词里提到了什么，不用回唱。";
        f.Submit(mixed, true, true);
        string payload = f.model.LastRequest.ToString();
        Verify(f.SingingLoaded, "Independent current acoustic evidence did not make the relevant singing skill available.");
        Verify(payload.Contains("青い空") && payload.Contains("静かな夜") && payload.Contains("不用回唱") && payload.Contains("A3-B3-C4"),
            "The actual final request lost lyrics, tail request or melody while separating routing evidence.");
        Verify(payload.Contains("head") && payload.Contains("island") && payload.Contains("tail"), "The final request lost time-ordered mixed recording regions.");
        Verify(!(bool)Call(f.chat, "HasPendingSingingGoalReview") && !(bool)Get(f.chat, "m_HumBackPending"), "Hearing a performance invented a requested singing action.");
        f.StreamIndependent("歌詞には青い空と静かな夜が出てくるね。");
        f.Submit("现在只想聊聊天气。", false);
        Verify((string)Get(f.chat, "m_LastUserMsg") == "现在只想聊聊天气。", "Prior lyrics replaced the new weather question.");
        AssertSingleObservationFrame(f.model.LastRequest);
        f.StreamIndependent("晴れていると外に出たくなるね。風は涼しい？");
        f.Submit("風は涼しいよ。", false); f.StreamIndependent("それなら窓を開けるのも気持ちよさそう。");
        f.Submit("今日は公園へ行くつもり。", false);
        Verify(!f.SingingLoaded, "Routine acoustic measurement text renewed the detailed singing follow-up budget indefinitely.");
        f.StreamIndependent("木陰で少し休むのもよさそうだね。");
        Verify(f.model.Reviews == 0 && !(bool)Get(f.chat, "m_HumBackPending"), "An independent post-song conversation was blocked on action review or submitted singing.");
        Save(); yield return null;
    }

    private static IEnumerator RestartThenChat()
    {
        fixture = new Fixture("realtime_stop_start_then_chat"); var f = fixture; yield return null;
        f.Submit("今天过得很平静。"); f.StreamIndependent("穏やかな日もいいね。何をして過ごしたの？");
        int history = f.model.m_DataList.Count;
        f.chat.StopAgentLoop(); f.chat.StopAllCoroutines();
        f.chat.StartAgentLoop(); f.chat.StopAllCoroutines();
        Verify(f.model.Requests.Count == 1 && f.model.m_DataList.Count == history, "Capture mode restart itself dispatched a second answer or erased preserved dialogue history.");
        f.Submit("现在继续聊聊天气吧。");
        Verify(!f.SingingLoaded, "Measurement text reloaded singing after realtime mode restart.");
        AssertSingleObservationFrame(f.model.LastRequest);
        f.StreamIndependent("うん。今日は空が明るいね。");
        Verify(f.model.Requests.Count == 2 && f.model.Reviews == 0, "The new post-restart chat acquired an extra model stage.");
        Save(); yield return null;
    }

    private static IEnumerator UnreviewedAction()
    {
        fixture = new Fixture("unreviewed_singing_speech_stays_out_of_tts"); var f = fixture; yield return null;
        f.Submit("请把刚才那段唱出来。");
        f.model.Push("<lang code=\"ja\"/><speech mode=\"after_action\"/>今から歌うね。準備できたよ。");
        Verify(f.Queued == 0 && !(bool)Get(f.chat, "m_StreamComplete"), "Action-dependent speech entered TTS while generation and action validation were incomplete.");
        f.model.Push("<sing refs=\"clip:public-missing\"/>");
        f.model.Complete(); f.chat.StopAllCoroutines();
        Verify(f.Queued == 0 && !(bool)Get(f.chat, "m_HumBackPending"), "A rejected public missing-source action released its promise or queued playback.");
        Verify(f.tts.Calls == 0, "An unreviewed action contacted the TTS transport.");
        Save(); yield return null;
    }

    private static string CurrentUser(JObject request) => (string)((JArray)request["messages"])
        .Last(m => (string)m["role"] == "user")["content"];

    private static void AssertSingleObservationFrame(JObject request)
    {
        foreach (JToken user in ((JArray)request["messages"]).Where(m => (string)m["role"] == "user"))
            Verify(!((string)user["content"] ?? "").Contains("[感知帧"), "A transient full observation frame was persisted in user dialogue history.");
        // The stable public behavior rules contain a documented frame example. Count
        // only dynamic messages, not that example in the first system message.
        string wire = string.Join("\n", ((JArray)request["messages"]).Skip(1).Select(m => (string)m["content"] ?? ""));
        int count = System.Text.RegularExpressions.Regex.Matches(wire, System.Text.RegularExpressions.Regex.Escape("[感知帧")).Count;
        Verify(count <= 1, "The same formal request contains multiple full observation frames.");
    }

    private static void Save() { scenarios.Add(fixture.Snapshot()); fixture.Dispose(); fixture = null; }
    private static void Verify(bool condition, string message)
    {
        if (exportOnly) return;
        checks++; if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string name;
        public readonly GameObject host;
        public readonly ChatSample chat;
        public readonly ChatLatencyFakeLLM model;
        public readonly ChatLatencyNoAudioTTS tts;
        public readonly SenseVoiceSpeechToText sense;
        public readonly JArray events = new JArray();
        public int Queued => ((ICollection)Get(chat, "m_PendingChunks")).Count;
        public bool SingingLoaded => ((IEnumerable<string>)Get(chat, "m_ActiveSkillsThisRound")).Contains("singing");

        public Fixture(string scenario)
        {
            name = scenario; host = new GameObject("ChatLatency_" + scenario); host.SetActive(false);
            chat = host.AddComponent<ChatSample>(); chat.enabled = false;
            model = host.AddComponent<ChatLatencyFakeLLM>(); model.enabled = false;
            model.builder = host.AddComponent<ChatQW>(); model.builder.enabled = false;
            model.builder.m_Backend = ChatQW.BackendType.Local;
            sense = host.AddComponent<SenseVoiceSpeechToText>(); sense.enabled = false;
            tts = host.AddComponent<ChatLatencyNoAudioTTS>(); tts.enabled = false;
            var audio = host.AddComponent<AudioSource>(); audio.playOnAwake = false; audio.mute = true;
            var text = new GameObject("Text", typeof(RectTransform), typeof(UnityEngine.UI.Text)); text.transform.SetParent(host.transform);
            var button = new GameObject("Send", typeof(RectTransform), typeof(UnityEngine.UI.Button)); button.transform.SetParent(host.transform);
            var input = new GameObject("Input", typeof(RectTransform), typeof(UnityEngine.UI.InputField)); input.transform.SetParent(host.transform);
            Set(chat, "m_TextBack", text.GetComponent<UnityEngine.UI.Text>()); Set(chat, "m_RecordTips", text.GetComponent<UnityEngine.UI.Text>());
            Set(chat, "m_CommitMsgBtn", button.GetComponent<UnityEngine.UI.Button>()); Set(chat, "m_InputWord", input.GetComponent<UnityEngine.UI.InputField>());
            Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = model, m_TextToSpeech = tts, m_SpeechToText = sense });
            Set(chat, "m_AudioSource", audio); Set(chat, "m_ChatHistory", new List<string>());
            foreach (string field in new[] { "m_PersistSubtitleSettings", "m_EnableSubtitleTranslation", "m_EnableLatencyFiller", "m_PrewarmHumSVCOnSingingSkillLoad", "m_EnableMemoryRecall", "m_EnableScreenVision" }) Set(chat, field, false);
            foreach (string field in new[] { "m_AutoSend", "m_UseStreaming", "m_IsVoiceMode", "m_EnableAgentLoop" }) Set(chat, field, true);
            Set(chat, "m_SystemNoticeMode", SystemNoticeMode.Hidden); Set(chat, "m_LogAgentLoop", true); Set(chat, "m_LogStreamTimings", false);
            Set(chat, "m_MinTickSec", 300f); Set(chat, "m_DefaultTickSec", 300f); Set(chat, "m_FirstTickDelaySec", 300f);
            TextAsset behavior = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/AIChatTookit/Prompts/behavior.txt");
            if (behavior == null) throw new InvalidOperationException("The isolated public behavior asset is missing.");
            model.m_DataList.Add(new LLM.SendData("system", "公開の会話テスト。短い自然な日本語で話す。観測と実行済みの事実を区別する。\n" + behavior.text));
            host.SetActive(true); audio.Stop(); Set(chat, "m_AgentRunning", true);
        }

        public void Submit(string words, bool singing = false, bool mixed = false)
        {
            Set(chat, "m_EouCognitiveSpeechVeto", false); Set(chat, "m_EouCognitiveSingingSupport", false);
            SetSense("LastText", words); SetSense("LastIsSinging", singing); SetSense("LastSingingProbability", singing ? .67f : .071f);
            SetSense("LastPitchStability", singing ? .65f : .12f); SetSense("LastNoteSequence", singing ? "A3-B3-C4" : "");
            SetSense("LastPitchLowNote", singing ? "A3" : ""); SetSense("LastPitchHighNote", singing ? "C4" : "");
            SetSense("LastSingingIslandSeconds", singing ? 6f : 0f); SetSense("LastLanguage", singing ? "ja" : "zh");
            Type segment = typeof(SenseVoiceSpeechToText).GetNestedType("TurnTranscriptSegment", BindingFlags.NonPublic);
            string segments = mixed
                ? "[{\"id\":1,\"start_seconds\":0,\"end_seconds\":2,\"region\":\"head\",\"type\":\"speech\",\"text\":\"我试着唱一段。\"},{\"id\":2,\"start_seconds\":2,\"end_seconds\":5,\"region\":\"island\",\"type\":\"singing_candidate\",\"text\":\"青い空。\"},{\"id\":3,\"start_seconds\":5,\"end_seconds\":8,\"region\":\"island\",\"type\":\"singing_candidate\",\"text\":\"静かな夜。\"},{\"id\":4,\"start_seconds\":8,\"end_seconds\":11,\"region\":\"tail\",\"type\":\"speech\",\"text\":\"请告诉我歌词里提到了什么，不用回唱。\"}]"
                : "[]";
            typeof(SenseVoiceSpeechToText).GetField("m_LastTurnSegments", Flags).SetValue(sense, Newtonsoft.Json.JsonConvert.DeserializeObject(segments, segment.MakeArrayType()));
            typeof(SenseVoiceSpeechToText).GetField("m_LastSegmentedPrimaryText", Flags).SetValue(sense, mixed ? words : "");
            int previous = model.Requests.Count;
            typeof(ChatSample).GetMethod("DealingTextCallback", Flags, null, new[] { typeof(string), typeof(bool) }, null)
                .Invoke(chat, new object[] { sense.BuildLastPerceivedText(), false });
            // Prevent consumers from requesting or playing audio. Queue production and
            // the request's original callbacks remain intact for synchronous assertions.
            chat.StopAllCoroutines();
            if (model.Requests.Count != previous + 1) throw new InvalidOperationException("Final ASR did not submit exactly one synthetic formal request.");
            events.Add(new JObject { ["event"] = "formal_request", ["request"] = model.Requests.Count, ["singingLoaded"] = SingingLoaded });
        }

        public void StreamIndependent(string reply)
        {
            model.Push("<lang code=\"ja\"/><speech mode=\"independent\"/>" + reply);
            bool early = Queued > 0 && !(bool)Get(chat, "m_StreamComplete");
            Verify(early, "The first independent spoken chunk was held until complete generation.");
            events.Add(new JObject { ["event"] = "stream_before_complete", ["request"] = model.Requests.Count, ["queuedChunks"] = Queued, ["firstChunkBeforeComplete"] = early, ["ttsCalls"] = tts.Calls });
            Verify(tts.Calls == 0, "The isolated queue test invoked a TTS transport.");
            model.Complete(); chat.StopAllCoroutines();
            events.Add(new JObject { ["event"] = "generation_complete", ["request"] = model.Requests.Count });
        }

        private void SetSense(string name, object value) => typeof(SenseVoiceSpeechToText).GetProperty(name).SetValue(sense, value);
        public JObject Snapshot() => new JObject {
            ["name"] = name, ["regularRequests"] = model.Requests.Count, ["reviews"] = model.Reviews, ["ttsCalls"] = tts.Calls,
            ["events"] = events.DeepClone(), ["requests"] = new JArray(model.Requests.Select(r => r.DeepClone())),
            ["requestSizes"] = new JArray(model.Requests.Select(r => { var messages = (JArray)r["messages"]; return new JObject {
                ["messages"] = messages.Count, ["contentCharacters"] = messages.Sum(m => ((string)m["content"] ?? "").Length),
                ["systemCharacters"] = messages.Where(m => (string)m["role"] == "system").Sum(m => ((string)m["content"] ?? "").Length),
                ["userCharacters"] = messages.Where(m => (string)m["role"] == "user").Sum(m => ((string)m["content"] ?? "").Length)
            }; }))
        };
        public void Dispose() { if (host != null) { Set(chat, "m_AgentRunning", false); chat.StopAllCoroutines(); UnityEngine.Object.DestroyImmediate(host); } }
    }

    private static object Call(object target, string name, params object[] args) => typeof(ChatSample).GetMethod(name, Flags).Invoke(target, args);
    private static object Get(object target, string name) => typeof(ChatSample).GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => typeof(ChatSample).GetField(name, Flags).SetValue(target, value);
}

public sealed class ChatLatencyFakeLLM : LLM
{
    public ChatQW builder;
    public int Reviews;
    public readonly List<JObject> Requests = new List<JObject>();
    public JObject LastRequest => Requests.Last();
    private RoleOutputChannels channels;
    private Action<string> completion;
    private string raw = "";
    public override void PostWorkReviewMsg(string prompt, Action<string> callback) { Reviews++; }
    public override void PostEphemeralMsg(string prompt, Action<string> callback) { Reviews++; }
    public override void PostSpeechStream(string prompt, Action<SpeechText> delta, Action<string> done, string imageDataUrl = null, bool recordAssistantHistory = true)
    {
        m_DataList.Add(new SendData("user", prompt));
        // The fixture intercepts only transport. Preserve every per-request public
        // context from the production routing target when using ChatQW as serializer.
        builder.ActiveSkillContext = ActiveSkillContext; builder.SkillCatalogContext = SkillCatalogContext;
        builder.TrailingContext = TrailingContext; builder.SpokenPrefix = SpokenPrefix;
        MethodInfo build = typeof(ChatQW).GetMethod("BuildRequestJsonForMessagesWithFeedback", BindingFlags.Instance | BindingFlags.NonPublic);
        Requests.Add(JObject.Parse((string)build.Invoke(builder, new object[] { m_DataList, true, RequestContext, null })));
        channels = new RoleOutputChannels(delta); completion = done; raw = "";
    }
    public void Push(string text) { raw += text; channels.Push(text); }
    public void Complete()
    {
        channels.Finish(); m_DataList.Add(new SendData("assistant", raw));
        // Reflection keeps the exporter compilable against the earlier public runtime,
        // while current production receives its explicit speech-phase projection.
        MethodInfo projection = typeof(RoleOutputChannels).GetMethod("ToExecutableText");
        completion((string)projection.Invoke(channels, projection.GetParameters().Select(p => (object)true).ToArray()));
    }
}

public sealed class ChatLatencyNoAudioTTS : TTS
{
    public int Calls;
    public override void Speak(string text, Action<AudioClip, string> callback) { Calls++; throw new InvalidOperationException("This isolated suite must stop at the TTS queue."); }
    public override void Speak(string text, Action<AudioClip> callback) { Calls++; throw new InvalidOperationException("This isolated suite must not synthesize audio."); }
}
