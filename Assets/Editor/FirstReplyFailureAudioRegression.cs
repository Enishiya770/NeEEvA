using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NeEEvA.Motion;
using Newtonsoft.Json.Linq;
using UnityEngine;

// The transport is controlled, but completion, sentence queues, ownership,
// AudioSource/DSP advancement and natural finish are the production pathways.
public static class FirstReplyFailureAudioRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Prefix = "The valid prefix.";
    private const string Tail = "REVOKED queued tail.";
    public static JObject LastReport { get; private set; }
    private sealed class Saved { public FieldInfo Field; public object Value; }
    private sealed class Fixture : IDisposable
    {
        public readonly ChatSample Chat;
        public readonly FirstReplyChatFixtureModel Model;
        public readonly FirstReplyFailureAudioFixtureTTS Tts;
        public readonly AudioSource Audio;
        private readonly GameObject host;
        private readonly ChatSetting settings;
        private readonly LLM originalModel;
        private readonly TTS originalTts;
        private readonly bool enabled;
        private readonly List<Saved> saved = new List<Saved>();
        private readonly List<Coroutine> players = new List<Coroutine>();
        private readonly UnityEngine.UI.Text subtitle;
        private readonly string subtitleText;
        public int Checks, Generation, Epoch, Round = 998000, Done;
        // A boundary listener, not an actual microphone. The real RTS callback
        // calls Microphone.Start, which this zero-device regression must not do.
        public bool CaptureAwaitingReply;
        public Fixture(ChatSample chat)
        {
            Chat = chat; enabled = chat.enabled;
            foreach (FieldInfo field in typeof(ChatSample).GetFields(Flags))
            {
                object value = field.GetValue(chat); saved.Add(new Saved { Field = field, Value = value });
                if (value is StringBuilder sb) field.SetValue(chat, new StringBuilder(sb.ToString()));
                else if (value is Delegate) field.SetValue(chat, null);
                else if (value != null && value.GetType().IsGenericType && value.GetType().Namespace == "System.Collections.Generic")
                {
                    var copy = value.GetType().GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType.IsInstanceOfType(value));
                    if (copy != null) field.SetValue(chat, copy.Invoke(new[] { value }));
                }
            }
            subtitle = (UnityEngine.UI.Text)Get(chat, "m_TextBack"); subtitleText = subtitle != null ? subtitle.text : null;
            settings = (ChatSetting)Get(chat, "m_ChatSettings"); originalModel = settings.m_ChatModel; originalTts = settings.m_TextToSpeech;
            host = new GameObject("FirstReply failure real audio fixture");
            Model = host.AddComponent<FirstReplyChatFixtureModel>();
            Tts = host.AddComponent<FirstReplyFailureAudioFixtureTTS>();
            Audio = host.AddComponent<AudioSource>(); Audio.mute = true; Audio.playOnAwake = false; Audio.loop = false;
            settings.m_ChatModel = Model; settings.m_TextToSpeech = Tts;
            Set(chat, "m_AudioSource", Audio); Set(chat, "m_SubtitleOverlay", null);
            chat.OnAISpeakDone = () => { Done++; CaptureAwaitingReply = false; };
            Generation = (int)Get(chat, "m_FormalResponseGeneration") + 1000; Epoch = (int)Get(chat, "m_WorkEpoch") + 1000;
            chat.enabled = true;
        }
        public void Check(bool condition, string message) { Checks++; if (!condition) throw new InvalidOperationException(message); }
        public void StopPlayers()
        {
            foreach (Coroutine player in players) if (player != null) Chat.StopCoroutine(player);
            players.Clear(); Call(Chat, "StopDirectStreamingSubtitle"); Tts.CancelStreaming(); Audio.Stop(); Audio.clip = null;
        }
        public void Arm(bool streaming)
        {
            StopPlayers(); Model.Reset(); Tts.Reset(streaming); Generation++; Epoch++; Round++; Done = 0; CaptureAwaitingReply = true;
            Set(Chat, "m_FormalResponseGeneration", Generation); Set(Chat, "m_WorkEpoch", Epoch); Set(Chat, "m_CurrentSpeechRoundId", Round);
            foreach (string name in new[] { "m_FirstReplyAttemptGeneration", "m_RoomTaskGeneration", "m_RoomSpeechGuardGeneration", "m_LastTtsFailureNoticeGeneration", "m_TextOutputDrainedGeneration" }) Set(Chat, name, -1);
            foreach (string name in new[] { "m_EnableFirstReplyRouting", "m_AgentRunning", "m_SceneTimePaused", "m_UseStreaming", "m_IsVoiceMode", "m_AgentRoundInFlight", "m_AgentGracefulShutdownPending" }) Set(Chat, name, true);
            foreach (string name in new[] { "m_AgentCurrentRoundIsTick", "m_UserSpeechActiveForAutonomy", "m_HoldSpeechForSongMemoryResult", "m_HoldSpeechForHumBackResult", "m_FormalResponseInFlight", "m_StreamComplete", "m_TTSSenderDone", "m_RoundContinue", "m_RoundSilent", "m_RoundIsInner", "m_RoundInnerCheckDone", "m_RoundSilencedForRepeat", "m_RoundRepeatCheckDone", "m_RoundRepeatHold", "m_FirstChunkFlushed", "m_FirstDeltaLogged", "m_WorkReviewOpen", "m_HumBackPending", "m_SongSingInFlight", "m_SongSearchResultPending", "m_SongCatalogResultPending", "m_SongMemoryResultPending", "m_HumBackResultPending", "m_RealFirstAudioStarted", "m_LatencyFillerPlayed", "m_LatencyFillerFromEou", "m_EouFillerScheduled", "m_ListeningFormalLatencyArmed", "m_ListeningLatencyArmed", "<IsAISpeaking>k__BackingField" }) Set(Chat, name, false);
            foreach (string name in new[] { "m_PendingSelfInspection", "m_PendingToolCorrectionFrame", "m_PendingSkillStatusFrame", "m_SpokenBridgeTextThisTurn", "m_CurrentlyPlayingText", "m_PendingNonCharacterToolNotice" }) Set(Chat, name, "");
            Set(Chat, "m_FormalBufferedClip", null); Set(Chat, "m_LatencyFillerClip", null);
            Set(Chat, "m_InterClipPauseSec", 0f); Set(Chat, "m_EouTime", 0f); Set(Chat, "m_StreamStartTime", Time.realtimeSinceStartup);
            Set(Chat, "m_UserTurnAwaitingReplySince", Math.Max(.01f, Time.realtimeSinceStartup));
            Set(Chat, "m_LastUserTurnTime", Time.realtimeSinceStartup);
            Set(Chat, "m_FirstChunkMaxChars", 100);
            Set(Chat, "m_SentenceBuffer", new SpeechTextBuffer()); Set(Chat, "m_AssistantHeardText", new StringBuilder());
            Set(Chat, "m_ChatHistory", new List<string> { "Please give one ordinary answer." });
            Set(Chat, "m_SilencedSpeechRoundIds", new HashSet<int>()); Set(Chat, "m_ActuallySpokenRounds", new HashSet<string>());
            foreach (string name in new[] { "m_PendingChunks", "m_PendingClips", "m_WorkResponses", "m_RepeatWatch", "m_RecentAIUtterances" }) Set(Chat, name, Activator.CreateInstance(Get(Chat, name).GetType()));
            var ledger = new ArdyActionConstraintLedger(); ledger.RecordUserTurn("Please give one ordinary answer."); Set(Chat, "m_ActionConstraints", ledger);
            Call(Chat, "ClearStagedFormalObservation");
        }
        public FirstReplyChatFixtureModel.Pending Dispatch()
        {
            Call(Chat, "DispatchFormalStream", "Please give one ordinary answer.", null, Generation, true, "controlled audio failure", false);
            Check(Model.First.Count == 1, "The production dispatch uses FirstReply exactly once.");
            return Model.First[0];
        }
        public void BeginPlayers()
        {
            if (Tts.SupportsStreamingPlayback) players.Add(Chat.StartCoroutine((IEnumerator)Call(Chat, "StreamDirectTTSPlayer", Generation)));
            else
            {
                players.Add(Chat.StartCoroutine((IEnumerator)Call(Chat, "StreamTTSSender", Generation)));
                players.Add(Chat.StartCoroutine((IEnumerator)Call(Chat, "StreamAudioPlayer", Generation)));
            }
        }
        public void AssertDrained(string expected)
        {
            Check(!Chat.IsAISpeaking && !Audio.isPlaying && !(bool)Get(Chat, "m_AgentRoundInFlight") && !(bool)Get(Chat, "m_FormalResponseInFlight"), "Speech, AudioSource, formal and agent ownership all release after drain.");
            Check(Done == 1 && !CaptureAwaitingReply && (float)Get(Chat, "m_UserTurnAwaitingReplySince") < 0f, "Exactly one completion releases the waiting-reply boundary; no actual microphone is opened.");
            Check(Tts.RequestTexts.SequenceEqual(new[] { expected }), "Only the owned validated sentence was synthesized; the revoked queue never reached TTS.");
            Check(((List<string>)Get(Chat, "m_ChatHistory")).Any(x => x == expected), "Naturally completed prefix is retained as actual heard text.");
            Check(Model.Room.Count == 0 && Model.FormalCalls == 0 && Model.UnexpectedCalls == 0, "Post-delta failure never retries the input or tools.");
        }
        public void Dispose()
        {
            StopPlayers(); Chat.enabled = enabled;
            foreach (Saved value in saved) value.Field.SetValue(Chat, value.Value);
            settings.m_ChatModel = originalModel; settings.m_TextToSpeech = originalTts;
            if (subtitle != null) subtitle.text = subtitleText;
            Tts.DestroyClips(); UnityEngine.Object.DestroyImmediate(host);
        }
    }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0 || chat == null || bridge == null || !bridge.IsConnected)
            throw new InvalidOperationException("Only run in the connected isolated Unity Play fixture.");
        string directory = Path.Combine(output, "first-reply-failure-audio"); Directory.CreateDirectory(directory);
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(), ["scope"] = "Production DispatchFormalStream and both playback coroutines, controlled in-memory TTS, real muted AudioSource with DSP progress. No network, microphone or human audibility claim." };
        var stack = new Stack<IEnumerator>();
        using (var f = new Fixture(chat))
        try
        {
            stack.Push(Cases(f));
            while (stack.Count > 0)
            {
                object yielded = null;
                try
                {
                    while (stack.Count > 0)
                    {
                        var e = stack.Peek();
                        if (!e.MoveNext()) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                        if (e.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        yielded = e.Current; break;
                    }
                }
                catch (Exception error) { LastReport["error"] = error.ToString(); throw; }
                if (stack.Count > 0) yield return yielded;
            }
            LastReport["status"] = "passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            LastReport["checks"] = f.Checks;
            if ((string)LastReport["status"] == "running") LastReport["status"] = "failed";
            File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString() + "\n");
        }
    }

    private static IEnumerator Cases(Fixture f)
    {
        foreach (bool streaming in new[] { true, false })
        foreach (string phase in new[] { "awaiting-first-pcm", "playing", "audio-drained-before-model-completion" })
        {
            f.Arm(streaming); var request = f.Dispatch(); request.Speech(Speech(Prefix)); f.BeginPlayers();
            yield return Until(() => f.Tts.RequestTexts.Count == 1, 2f, "owned synthesis request");
            if (phase != "awaiting-first-pcm")
            {
                f.Tts.Release = true;
                yield return Until(() => f.Chat.IsAISpeaking && f.Audio.timeSamples > 0, 2f, "actual prefix DSP progress");
                if (phase == "audio-drained-before-model-completion")
                    yield return Until(() => !f.Audio.isPlaying && ((StringBuilder)Get(f.Chat, "m_AssistantHeardText")).ToString() == Prefix, 3f, "prefix drained while awaiting model");
            }
            request.Speech(Speech(Tail));
            request.Done(new FirstReplyCompletion { Disposition = FirstReplyDisposition.Failed, SpeechDelivered = true, Failure = "controlled-post-delta-failure", SafeOutput = "<motion name=\"follow\"/>RAW_UNTRUSTED_TAIL" });
            if (phase != "audio-drained-before-model-completion")
                f.Check(f.Done == 0 && (bool)Get(f.Chat, "m_AgentRoundInFlight") && f.CaptureAwaitingReply, "A synthesis/playback owner must drain before the failed producer releases the round: " + phase);
            f.Check(!((HashSet<int>)Get(f.Chat, "m_SilencedSpeechRoundIds")).Contains(f.Round), "Failure does not revoke the already owned validated sentence.");
            f.Tts.Release = true;
            yield return Until(() => f.Done > 0 && !f.Audio.isPlaying && !f.Chat.IsAISpeaking, 4f, "post-failure natural drain " + phase);
            yield return null; yield return null;
            f.AssertDrained(Prefix);
            request.Done(FirstReplyCompletion.Failed("duplicate-late-failure", true)); request.Speech(Speech("LATE_REPLAY."));
            yield return null; yield return null;
            f.Check(f.Done == 1 && f.Tts.RequestTexts.Count == 1 && !f.Audio.isPlaying, "Duplicate completion and late deltas cannot replay or finish a second time.");
            Add((streaming ? "PCM" : "WAV") + " / " + phase + " releases exactly once");
            yield return NextValid(f, streaming, request);
        }

        foreach (bool streaming in new[] { true, false })
        {
            f.Arm(streaming); f.Tts.FailBeforeStart = true;
            var failed = f.Dispatch(); failed.Speech(Speech(Prefix)); f.BeginPlayers();
            yield return Until(() => f.Tts.RequestTexts.Count == 1, 2f, "synthesis owner before no-audio failure");
            failed.Done(FirstReplyCompletion.Failed("model-failed-before-pcm", true));
            f.Check(f.Done == 0, "An unresolved owned synthesis request cannot release the round early.");
            f.Tts.Release = true;
            yield return Until(() => f.Done > 0 && !f.Chat.IsAISpeaking, 3f, "TTS failure without audio releases ownership");
            yield return null; yield return null;
            f.Check(f.Done == 1 && !(bool)Get(f.Chat, "m_AgentRoundInFlight") && !(bool)Get(f.Chat, "m_FormalResponseInFlight") && !f.CaptureAwaitingReply && (float)Get(f.Chat, "m_UserTurnAwaitingReplySince") < 0f,
                "No-audio synthesis failure still releases speaking, agent and waiting state exactly once.");
            f.Check(!f.Audio.isPlaying && !((List<string>)Get(f.Chat, "m_ChatHistory")).Any(x => x == Prefix), "An unheard prefix is not recorded as speech.");
            Add((streaming ? "PCM" : "WAV") + " / owned synthesis fails without any audio");
            yield return NextValid(f, streaming, failed);
        }

        foreach (bool streaming in new[] { true, false })
        {
            f.Arm(streaming); var interruptedDrain = f.Dispatch();
            interruptedDrain.Speech(Speech(Prefix)); f.BeginPlayers();
            yield return Until(() => f.Tts.RequestTexts.Count == 1, 2f, "pending synthesis before failed-drain interruption");
            interruptedDrain.Done(FirstReplyCompletion.Failed("failed-producer-owned-synthesis-still-pending", true));
            f.Check(f.Done == 0 && !f.Audio.isPlaying && !f.Chat.IsAISpeaking &&
                (int)Get(f.Chat, "m_FirstReplyFailureDrainGeneration") == f.Generation,
                "Post-delta failure retains its unresolved pre-PCM drain owner before interruption.");
            int oldGeneration = f.Generation, cancellations = f.Tts.CancelCalls;
            f.Chat.Interrupt();
            f.Check((int)Get(f.Chat, "m_FormalResponseGeneration") != oldGeneration &&
                (int)Get(f.Chat, "m_FirstReplyFailureDrainGeneration") != oldGeneration && f.Tts.CancelCalls > cancellations,
                "Interrupt recognizes a failed producer's pre-PCM drain, advances generation and cancels its owned TTS.");
            f.Check(f.Done == 1 && !f.Chat.IsAISpeaking && !f.Audio.isPlaying &&
                !(bool)Get(f.Chat, "m_AgentRoundInFlight") && !(bool)Get(f.Chat, "m_FormalResponseInFlight") &&
                !f.CaptureAwaitingReply && (float)Get(f.Chat, "m_UserTurnAwaitingReplySince") < 0f,
                "Interrupt releases the pre-PCM failed drain and waiting boundary exactly once.");
            // The old provider was canceled, so releasing its fixture gate cannot
            // manufacture a late clip. The old model callbacks are also inert.
            f.Tts.Release = true;
            interruptedDrain.Speech(Speech("STALE_DRAIN_DELTA."));
            interruptedDrain.Done(FirstReplyCompletion.Failed("late-failed-drain-completion", true));
            yield return null; yield return null;
            f.Check(f.Done == 1 && !f.Audio.isPlaying && f.Audio.clip == null,
                "Late failed-drain callbacks and released old synthesis cannot replay or complete twice.");
            Add((streaming ? "PCM" : "WAV") + " / interrupt failed drain while first PCM is still pending");
            yield return NextValid(f, streaming, interruptedDrain);
        }

        // Actual user interruption invalidates the old response and its player;
        // late old callbacks must not release the new response's speaking flag.
        foreach (bool streaming in new[] { true, false })
        {
            f.Arm(streaming); var old = f.Dispatch(); old.Speech(Speech(Prefix)); f.BeginPlayers(); f.Tts.Release = true;
            yield return Until(() => f.Chat.IsAISpeaking && f.Audio.timeSamples > 0, 2f, "interrupt prefix progress");
            f.Chat.Interrupt();
            f.Check(f.Done == 1 && !f.Chat.IsAISpeaking && !(bool)Get(f.Chat, "m_AgentRoundInFlight"), "Actual Interrupt releases old ownership once.");
            yield return NextValid(f, streaming, old);
            Add((streaming ? "PCM" : "WAV") + " / user interruption plus stale generation callbacks");
        }
    }

    private static IEnumerator NextValid(Fixture f, bool streaming, FirstReplyChatFixtureModel.Pending old)
    {
        const string next = "A healthy next reply.";
        f.Arm(streaming); var current = f.Dispatch(); current.Speech(Speech(next)); f.BeginPlayers(); f.Tts.Release = true;
        yield return Until(() => f.Chat.IsAISpeaking && f.Audio.timeSamples > 0, 2f, "next reply starts normally");
        old.Speech(Speech("STALE_OLD_ROUND.")); old.Done(FirstReplyCompletion.Failed("stale-after-new-generation", true));
        f.Check(f.Done == 0 && f.Chat.IsAISpeaking && (bool)Get(f.Chat, "m_AgentRoundInFlight") && (bool)Get(f.Chat, "m_FormalResponseInFlight"), "Old-generation callbacks cannot complete or stop the new speaking response.");
        current.Done(new FirstReplyCompletion { Disposition = FirstReplyDisposition.Ordinary, SpeechDelivered = true, SafeOutput = "<lang code=\"en\"/><speech mode=\"independent\"/><say>" + next + "</say>" });
        yield return Until(() => f.Done > 0 && !f.Audio.isPlaying && !f.Chat.IsAISpeaking, 4f, "next valid response naturally completes");
        yield return null; yield return null; f.AssertDrained(next);
        Add((streaming ? "PCM" : "WAV") + " / next valid response plays and completes");
    }
    private static SpeechText Speech(string text) => new SpeechText(text, "en", actionDependency: SpeechActionDependency.Independent);
    private static void Add(string name) => ((JArray)LastReport["cases"]).Add(name);
    private static IEnumerator Until(Func<bool> condition, float seconds, string reason)
    {
        float until = Time.realtimeSinceStartup + seconds;
        while (!condition()) { if (Time.realtimeSinceStartup > until) throw new TimeoutException(reason); yield return null; }
    }
    private static FieldInfo Field(Type type, string name)
    {
        for (; type != null; type = type.BaseType) { var f = type.GetField(name, Flags); if (f != null) return f; }
        throw new MissingFieldException(name);
    }
    private static object Get(object obj, string name) => Field(obj.GetType(), name).GetValue(obj);
    private static void Set(object obj, string name, object value) => Field(obj.GetType(), name).SetValue(obj, value);
    private static object Call(object obj, string name, params object[] args) => obj.GetType().GetMethod(name, Flags).Invoke(obj, args);
}

public sealed class FirstReplyFailureAudioFixtureTTS : TTS
{
    public bool Streaming, Release, FailBeforeStart;
    public int CancelCalls { get; private set; }
    public readonly List<string> RequestTexts = new List<string>();
    private readonly List<AudioClip> clips = new List<AudioClip>();
    private readonly List<Coroutine> routines = new List<Coroutine>();
    private AudioSource source;
    private AudioClip issued;
    private int revision;
    public override bool SupportsStreamingPlayback => Streaming;
    public void Reset(bool streaming) { CancelStreaming(); Streaming = streaming; Release = false; FailBeforeStart = false; RequestTexts.Clear(); }
    public override void Speak(SpeechText speech, Action<AudioClip, string> callback)
    { RequestTexts.Add(speech.Text); routines.Add(StartCoroutine(Wav(speech, callback, revision))); }
    private IEnumerator Wav(SpeechText speech, Action<AudioClip, string> callback, int owner)
    {
        while (!Release && owner == revision) yield return null;
        if (owner == revision) callback(FailBeforeStart ? null : MakeClip(), speech.Text);
    }
    public override void SpeakStreamingWithPlaybackGate(SpeechText speech, AudioSource output, Action<string> onStarted,
        Action<bool, string, float> onCompleted, Func<StreamingPlaybackPermission> gate)
    { RequestTexts.Add(speech.Text); routines.Add(StartCoroutine(Pcm(speech, output, onStarted, onCompleted, gate, revision))); }
    private IEnumerator Pcm(SpeechText speech, AudioSource output, Action<string> started, Action<bool, string, float> done, Func<StreamingPlaybackPermission> gate, int owner)
    {
        while (!Release && owner == revision) yield return null;
        if (owner != revision) yield break;
        if (FailBeforeStart) { done(false, speech.Text, 0f); yield break; }
        while (gate != null && gate() == StreamingPlaybackPermission.Wait && owner == revision) yield return null;
        if (owner != revision) yield break;
        if (gate != null && gate() == StreamingPlaybackPermission.Cancel) { done(false, speech.Text, 0f); yield break; }
        source = output; issued = MakeClip(); AudioClip owned = issued;
        source.clip = owned; source.loop = false; source.Play(); started(speech.Text);
        while (owner == revision && source != null && source.clip == owned && source.isPlaying)
        {
            if (gate != null && gate() == StreamingPlaybackPermission.Cancel) { source.Stop(); done(false, speech.Text, owned.length); yield break; }
            yield return null;
        }
        if (owner == revision) done(source != null && source.clip == owned, speech.Text, owned.length);
    }
    private AudioClip MakeClip()
    {
        const int rate = 24000, count = 9600;
        AudioClip clip = AudioClip.Create("Controlled valid speech PCM", count, 1, rate, false);
        var pcm = new float[count]; for (int i = 0; i < count; i++) pcm[i] = .015f * (float)Math.Sin(2 * Math.PI * 220 * i / rate);
        clip.SetData(pcm, 0); clips.Add(clip); return clip;
    }
    public override void CancelStreaming()
    {
        CancelCalls++; revision++; foreach (Coroutine routine in routines) if (routine != null) StopCoroutine(routine); routines.Clear();
        if (source != null && source.clip == issued) { source.Stop(); source.clip = null; }
        source = null; issued = null;
    }
    public void DestroyClips() { foreach (AudioClip clip in clips) if (clip != null) UnityEngine.Object.DestroyImmediate(clip); clips.Clear(); }
}
