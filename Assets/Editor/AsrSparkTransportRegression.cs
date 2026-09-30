using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Opt-in actual ASR HTTP/WebSocket transport with synthetic PCM; no microphone, avatar, memory hub or scene save.</summary>
[InitializeOnLoad]
public static class AsrSparkTransportRegression
{
    const string Key = "NeEEvA.AsrSparkTransport.";
    const string MainScene = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    const string Quality = "ProjectSettings/QualitySettings.asset";
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static SenseVoiceSpeechToText asr;
    static GameObject host;
    static AudioClip clip;
    static float[] samples;
    static JObject report, activeRow;
    static JArray rows, transports;
    static Stack<IEnumerator> work;
    static int lastFrame = -1, checks;
    static string phase;
    static long trialClock;
    static bool cancelledStream;
    static int stalePartialCallbacks;

    static AsrSparkTransportRegression()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) Begin();
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                if (SessionState.GetBool(Key + "Finished", false)) Exit();
                else Finish(new InvalidOperationException("Play mode ended before the ASR transport trial completed."));
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
            throw new InvalidOperationException("Use an independent batch Editor; this fixture performs real ASR network requests.");
        foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
            if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                throw new InvalidOperationException("Refusing to replace a dirty saved scene.");
        string endpoint = Environment.GetEnvironmentVariable("NEEEVA_ASR_TRIAL_URL");
        if (string.IsNullOrWhiteSpace(endpoint)) endpoint = "http://192.168.114.163:9881";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new ArgumentException("ASR trial URL must be an HTTP(S) origin without path, credentials, query or fragment.");
        string input = Environment.GetEnvironmentVariable("NEEEVA_ASR_TRIAL_WAV");
        if (string.IsNullOrWhiteSpace(input)) input = "work/spark-voice-migration-20260926/asr-validation-ja.wav";
        input = Path.GetFullPath(input);
        if (!File.Exists(input)) throw new FileNotFoundException("Synthetic ASR trial WAV was not found.", input);
        string name = Environment.GetEnvironmentVariable("NEEEVA_ASR_TRIAL_REPORT");
        if (string.IsNullOrWhiteSpace(name)) name = "spark";
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-zA-Z0-9_-]+$"))
            throw new ArgumentException("Invalid ASR report name.");
        SessionState.SetString(Key + "Endpoint", endpoint.TrimEnd('/'));
        SessionState.SetString(Key + "Input", input);
        SessionState.SetString(Key + "Output", "Logs/asr/unity-transport-" + name + ".json");
        SessionState.SetString(Key + "SceneHash", Hash(MainScene));
        SessionState.SetString(Key + "QualityHash", Hash(Quality));
        SessionState.SetBool(Key + "Background", Application.runInBackground);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SessionState.SetBool(Key + "Active", true);
        SessionState.SetBool(Key + "Finished", false);
        SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 300f);
        EditorApplication.isPlaying = true;
    }

    static void Begin()
    {
        try
        {
            checks = 0; cancelledStream = false; stalePartialCallbacks = 0;
            trialClock = Clock(); rows = new JArray(); transports = new JArray();
            string input = SessionState.GetString(Key + "Input", "");
            samples = ReadMonoPcm16(input);
            report = new JObject {
                ["status"] = "running", ["startedUtc"] = DateTime.UtcNow.ToString("O"),
                ["scope"] = "Synthetic Haruka Japanese WAV through actual Unity SenseVoiceSpeechToText HTTP final and paced WebSocket preview. No physical microphone, scene rendering, dialogue, TTS or end-to-end user-latency claim.",
                ["endpoint"] = SessionState.GetString(Key + "Endpoint", ""), ["inputPath"] = input,
                ["inputSha256"] = Hash(input), ["inputSampleRate"] = 16000, ["inputChannels"] = 1,
                ["inputFrames"] = samples.Length, ["inputSeconds"] = samples.Length / 16000.0,
                ["languageRequested"] = "ja", ["learnSpeaker"] = false, ["audioDumpEnabled"] = false,
                ["autoBindNameEnabled"] = false, ["memoryHubCreated"] = false, ["microphoneUsed"] = false,
                ["sceneSaved"] = false, ["rows"] = rows, ["transportEvents"] = transports,
                ["unityVersion"] = Application.unityVersion
            };
            Require(SceneManager.GetActiveScene().path == "", "ASR trial must remain in an unsaved scene.");
            host = new GameObject("Isolated synthetic ASR network trial");
            host.SetActive(false);
            asr = host.AddComponent<SenseVoiceSpeechToText>();
            Set(asr, "m_ServerSetting", SessionState.GetString(Key + "Endpoint", ""));
            Set(asr, "m_StreamingPreviewURL", "");
            Set(asr, "m_Language", "ja");
            Set(asr, "m_EnableStreamingPreview", true);
            Set(asr, "m_StreamPartialIntervalMs", 850);
            Set(asr, "m_StreamMinAudioMs", 800);
            foreach (string field in new[] { "m_InjectMetaPrefix", "m_InjectSpeakerPrefix", "m_AutoBindIntroducedName",
                "m_DumpPracticeAudio", "m_EnableSingingAnalysis", "m_VerboseLog", "m_LogStreamingPreview" }) Set(asr, field, false);
            asr.OnStreamingTransportChanged += OnTransport;
            host.SetActive(true); // Awake computes the production HTTP endpoint after configuration.
            Require((string)Get(asr, "m_SpeechRecognizeURL") == SessionState.GetString(Key + "Endpoint", "") + "/asr",
                "The actual ASR component did not bind the selected HTTP endpoint.");
            clip = AudioClip.Create("SyntheticHarukaAsrValidation", samples.Length, 1, 16000, false);
            Require(clip.SetData(samples, 0), "Cannot load synthetic PCM into the production AudioClip input.");
            Application.runInBackground = true;
            work = new Stack<IEnumerator>(); work.Push(Run()); lastFrame = -1;
        }
        catch (Exception error) { Finish(error); }
    }

    static IEnumerator Run()
    {
        yield return Final("http-final");
        yield return StreamingFinish();
        yield return StreamingCancel();
        yield return Final("http-successor-after-stream-cancel");
        yield return Delay(1f);
        Require(stalePartialCallbacks == 0, "A cancelled stream published a stale partial after cancellation or its successor.");
        report["cancelledStreamLatePartialCallbacks"] = stalePartialCallbacks;
        report["successorFinalUsable"] = true;
    }

    static IEnumerator Final(string id)
    {
        phase = id;
        var row = new JObject { ["id"] = id, ["api"] = "SpeechToText(AudioClip, callback, learnSpeaker:false)", ["status"] = "running" };
        activeRow = row; rows.Add(row);
        int callbacks = 0;
        string text = null;
        long started = Clock();
        asr.SpeechToText(clip, value => { callbacks++; text = value; }, learnSpeaker: false);
        yield return Until(() => callbacks > 0, 75f, "HTTP final did not call back before the deadline.");
        row["elapsedSeconds"] = Seconds(started); row["callbackText"] = text;
        row["parsedLast"] = LastFields();
        Require(callbacks == 1 && !string.IsNullOrWhiteSpace(text), "HTTP final must publish one nonempty transcript.");
        Require(!string.IsNullOrWhiteSpace(asr.LastText) && !asr.LastNoSpeech,
            "Production response parsing did not expose a speech transcript in LastText/LastNoSpeech.");
        // Speaker/meta injection is disabled. Existing production acoustic or
        // segmented-evidence annotations may still surround a recognized turn.
        Require(text.Contains(asr.LastText), "Final callback lost the transcript parsed into LastText.");
        row["callbackContainsOnlyParsedText"] = text == asr.LastText;
        Require(!string.IsNullOrWhiteSpace(asr.LastLanguage), "Production response parsing did not expose LastLanguage.");
        yield return Delay(.2f);
        Require(callbacks == 1, "HTTP final callback was delivered more than once.");
        row["callbackCount"] = callbacks; row["status"] = "passed";
    }

    static IEnumerator StreamingFinish()
    {
        phase = "ws-paced-finish";
        var row = new JObject { ["id"] = phase, ["status"] = "running", ["chunkFrames"] = 3200,
            ["pacedAudioSeconds"] = samples.Length / 16000.0, ["partials"] = new JArray() };
        activeRow = row; rows.Add(row);
        var partials = (JArray)row["partials"];
        JObject before = LastFields();
        long started = Clock();
        int nonempty = 0;
        Require(asr.BeginStreamingPreview(value => {
            partials.Add(Partial(value, Seconds(started)));
            if (!string.IsNullOrWhiteSpace(value.Text)) nonempty++;
        }), "Production streaming preview was not enabled.");
        int generation = asr.StreamingGeneration;
        row["generation"] = generation;
        for (int offset = 0; offset < samples.Length; offset += 3200)
        {
            int count = Math.Min(3200, samples.Length - offset);
            float[] chunk = new float[count]; Array.Copy(samples, offset, chunk, 0, count);
            Require(asr.TryPushStreamingSamples(chunk, 1, 16000), "Paced preview rejected audio before Finish.");
            yield return Until(() => Seconds(started) >= (offset + count) / 16000.0, 5f, "Paced preview clock stalled.");
        }
        row["feedCompletedSeconds"] = Seconds(started);
        yield return Until(() => nonempty > 0, 20f, "Real paced WebSocket produced no nonempty partial.");
        row["nonemptyPartialsBeforeFinish"] = nonempty;
        row["lastFieldsUnchangedByPreview"] = JToken.DeepEquals(before, LastFields());
        Require((bool)row["lastFieldsUnchangedByPreview"], "Preview mutated accepted final ASR fields.");
        asr.FinishStreamingPreview();
        row["finishCalledSeconds"] = Seconds(started);
        Require(!asr.TryPushStreamingSamples(new float[160], 1, 16000), "Finish must reject subsequent PCM.");
        // Production Finish ends the send loop and cancels its receive worker;
        // it does not promise a server 'stopped' acknowledgement or a WS final.
        yield return Until(StreamWorkerStopped, 10f, "Streaming workers did not stop after Finish.");
        row["finishWorkerStoppedSeconds"] = Seconds(started);
        row["finishServerAcknowledgementRequired"] = false;
        row["partialCount"] = partials.Count; row["status"] = "passed";
        asr.CancelStreamingPreview();
    }

    static IEnumerator StreamingCancel()
    {
        phase = "ws-cancel";
        var row = new JObject { ["id"] = phase, ["status"] = "running", ["partials"] = new JArray() };
        activeRow = row; rows.Add(row);
        var partials = (JArray)row["partials"];
        long started = Clock();
        int nonempty = 0;
        Require(asr.BeginStreamingPreview(value => {
            if (cancelledStream) { stalePartialCallbacks++; return; }
            partials.Add(Partial(value, Seconds(started)));
            if (!string.IsNullOrWhiteSpace(value.Text)) nonempty++;
        }), "Cannot begin the real cancellation preview.");
        int generation = asr.StreamingGeneration;
        row["generationBeforeCancel"] = generation;
        // Observe actual service output before cancelling, so this is not merely
        // an unopened-socket cancellation test.
        int offset = 0;
        while (offset < samples.Length && (nonempty == 0 || offset < 32000))
        {
            int count = Math.Min(3200, samples.Length - offset);
            float[] chunk = new float[count]; Array.Copy(samples, offset, chunk, 0, count);
            Require(asr.TryPushStreamingSamples(chunk, 1, 16000), "Cancellation preview rejected paced audio.");
            offset += count;
            yield return Until(() => Seconds(started) >= offset / 16000.0, 5f, "Cancel-preview clock stalled.");
        }
        yield return Until(() => nonempty > 0, 20f, "Cancellation test never received an actual nonempty partial.");
        row["pushedAudioSeconds"] = offset / 16000.0; row["partialCountBeforeCancel"] = partials.Count;
        cancelledStream = true;
        asr.CancelStreamingPreview();
        row["cancelSeconds"] = Seconds(started);
        row["generationAfterCancel"] = asr.StreamingGeneration;
        Require(asr.StreamingGeneration != generation, "Cancel did not revoke the previous stream generation.");
        Require(!asr.TryPushStreamingSamples(new float[160], 1, 16000), "Cancelled preview still accepted PCM.");
        yield return Delay(.5f);
        Require(stalePartialCallbacks == 0, "Old WebSocket callback fired after Cancel.");
        row["status"] = "passed";
    }

    static bool StreamWorkerStopped()
    {
        var socket = Get(asr, "m_StreamSocket") as ClientWebSocket;
        if (socket == null) return true;
        try { return socket.State == WebSocketState.Closed || socket.State == WebSocketState.Aborted; }
        catch (ObjectDisposedException) { return true; }
    }

    static JObject LastFields() => new JObject {
        ["text"] = asr.LastText, ["language"] = asr.LastLanguage, ["emotion"] = asr.LastEmotion,
        ["audioEvent"] = asr.LastEvent, ["noSpeech"] = asr.LastNoSpeech, ["speakerId"] = asr.LastSpeakerId,
        ["speakerStatus"] = asr.LastSpeakerStatus, ["speakerPersistent"] = asr.LastSpeakerPersistent,
        ["speakerIsNew"] = asr.LastSpeakerIsNew, ["isSinging"] = asr.LastIsSinging
    };

    static JObject Partial(SenseVoiceSpeechToText.StreamingTranscript value, double seconds) => new JObject {
        ["receivedSeconds"] = seconds, ["text"] = value.Text, ["stableText"] = value.StableText,
        ["unstableText"] = value.UnstableText, ["language"] = value.Language, ["audioMs"] = value.AudioMs,
        ["windowStartMs"] = value.WindowStartMs, ["serverElapsedSeconds"] = value.Elapsed,
        ["revision"] = value.Revision, ["activityAvailable"] = value.ActivityAvailable
    };

    static void OnTransport(int generation, string state, string reason)
    {
        transports?.Add(new JObject { ["phase"] = phase, ["generation"] = generation,
            ["state"] = state, ["reason"] = reason, ["trialSeconds"] = Seconds(trialClock) });
    }

    static float[] ReadMonoPcm16(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var reader = new BinaryReader(stream, Encoding.ASCII))
        {
            Require(new string(reader.ReadChars(4)) == "RIFF", "Input must be RIFF WAV.");
            reader.ReadUInt32(); Require(new string(reader.ReadChars(4)) == "WAVE", "Input is not WAVE.");
            int format = -1, channels = -1, rate = -1, bits = -1;
            byte[] pcm = null;
            while (stream.Position + 8 <= stream.Length)
            {
                string id = new string(reader.ReadChars(4)); uint length = reader.ReadUInt32();
                long next = stream.Position + length + (length & 1);
                Require(next <= stream.Length, "Input WAV chunk exceeds file length.");
                if (id == "fmt ")
                {
                    Require(length >= 16, "Input WAV fmt is incomplete.");
                    format = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                    reader.ReadInt32(); reader.ReadUInt16(); bits = reader.ReadUInt16();
                }
                else if (id == "data") pcm = reader.ReadBytes(checked((int)length));
                stream.Position = next;
            }
            Require(format == 1 && channels == 1 && rate == 16000 && bits == 16,
                "Fixture requires uncompressed 16 kHz mono PCM16; do not silently resample the reference.");
            Require(pcm != null && pcm.Length > 32000 && pcm.Length % 2 == 0, "Input WAV must contain aligned speech PCM.");
            var output = new float[pcm.Length / 2];
            for (int i = 0; i < output.Length; i++) output[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) / 32768f;
            return output;
        }
    }

    static IEnumerator Until(Func<bool> condition, float timeout, string failure)
    { long started = Clock(); while (!condition()) { if (Seconds(started) > timeout) throw new TimeoutException(failure); yield return null; } }
    static IEnumerator Delay(float seconds)
    { long started = Clock(); while (Seconds(started) < seconds) yield return null; }
    static long Clock() => System.Diagnostics.Stopwatch.GetTimestamp();
    static double Seconds(long started) => (Clock() - started) / (double)System.Diagnostics.Stopwatch.Frequency;
    static void Require(bool value, string failure) { checks++; if (!value) throw new InvalidOperationException(failure); }

    static void Tick()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        try
        {
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", 0))
                throw new TimeoutException("ASR transport trial global deadline.");
            if (!EditorApplication.isPlaying || work == null || lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount;
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
        if (asr != null) { asr.OnStreamingTransportChanged -= OnTransport; asr.CancelStreamingPreview(); asr.StopAllCoroutines(); }
        report = report ?? new JObject();
        report["status"] = error == null ? "passed" : "failed";
        report["checks"] = checks; report["finishedUtc"] = DateTime.UtcNow.ToString("O");
        report["cancelledStreamLatePartialCallbacks"] = stalePartialCallbacks;
        if (error != null)
        {
            report["error"] = error.ToString();
            if (activeRow != null) { activeRow["status"] = "failed"; activeRow["failure"] = error.Message; }
        }
        bool unchanged = Hash(MainScene) == SessionState.GetString(Key + "SceneHash", "") &&
            Hash(Quality) == SessionState.GetString(Key + "QualityHash", "");
        report["savedSceneAndQualityUnchangedDuringTrial"] = unchanged;
        if (!unchanged) report["status"] = "failed";
        WriteReport();
        SessionState.SetBool(Key + "Passed", error == null && unchanged);
        SessionState.SetBool(Key + "Finished", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Exit();
    }

    static void Exit()
    {
        bool passed = SessionState.GetBool(Key + "Passed", false);
        SessionState.SetBool(Key + "Active", false); SessionState.SetBool(Key + "Finished", false);
        Application.runInBackground = SessionState.GetBool(Key + "Background", Application.runInBackground);
        // Drop the transient scene in memory. Never save or reopen the user's main scene.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        string path = SessionState.GetString(Key + "Output", "Logs/asr/unity-transport-spark.json");
        var final = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        bool unchanged = Hash(MainScene) == SessionState.GetString(Key + "SceneHash", "") &&
            Hash(Quality) == SessionState.GetString(Key + "QualityHash", "");
        final["savedSceneAndQualityUnchangedAfterExit"] = unchanged; passed &= unchanged;
        if (!passed) final["status"] = "failed";
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, final.ToString());
        EditorApplication.Exit(passed ? 0 : 1);
    }

    static void WriteReport()
    {
        string path = SessionState.GetString(Key + "Output", "Logs/asr/unity-transport-spark.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, report.ToString());
        Debug.Log("[AsrSparkTransportRegression] status=" + report["status"] + " output=" + path);
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
