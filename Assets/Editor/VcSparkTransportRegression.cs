using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

/// <summary>Real VC WAV transport and muted Unity playback, deliberately below character singing authorization.</summary>
[InitializeOnLoad]
public static class VcSparkTransportRegression
{
    const string Key = "NeEEvA.VcSparkTransport.";
    const string MainScene = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    const string Quality = "ProjectSettings/QualitySettings.asset";
    static AudioSource source;
    static AudioClip downloaded;
    static byte[] input;
    static JObject report, activeRow;
    static JArray rows;
    static Stack<IEnumerator> work;
    static readonly List<AudioClip> clips = new List<AudioClip>();
    static UnityWebRequest activeRequest;
    static int generation, frame = -1, checks;
    static string endpoint, reference;

    static VcSparkTransportRegression()
    {
        EditorApplication.update += Tick;
        EditorApplication.playModeStateChanged += state => {
            if (!SessionState.GetBool(Key + "Active", false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode) Begin();
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                if (SessionState.GetBool(Key + "Finished", false)) Exit();
                else Finish(new InvalidOperationException("Play mode ended before VC transport completed."));
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
            throw new InvalidOperationException("Use an independent batch Editor with audio enabled and an already running VC service.");
        foreach (var setup in EditorSceneManager.GetSceneManagerSetup())
            if (!string.IsNullOrEmpty(setup.path) && SceneManager.GetSceneByPath(setup.path).isDirty)
                throw new InvalidOperationException("Refusing to replace a dirty saved scene.");
        string url = Env("NEEEVA_VC_TRIAL_URL", "http://192.168.114.163:9882/convert");
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || (uri.Scheme != "http" && uri.Scheme != "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/convert")
            throw new ArgumentException("VC URL must be an HTTP(S) /convert endpoint without credentials or query.");
        string wav = Path.GetFullPath(Env("NEEEVA_VC_TRIAL_WAV", "work/spark-voice-migration-20260926/asr-validation-ja.wav"));
        if (!File.Exists(wav)) throw new FileNotFoundException("Synthetic VC input is missing.", wav);
        string name = Env("NEEEVA_VC_TRIAL_REPORT", "spark");
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-zA-Z0-9_-]+$")) throw new ArgumentException("Invalid report name.");
        SessionState.SetString(Key + "Url", url);
        SessionState.SetString(Key + "Reference", Env("NEEEVA_VC_TRIAL_REFERENCE", "/home/spark_root_001/neeeva-voice/vc/release/refs/41041.wav"));
        SessionState.SetString(Key + "Input", wav);
        SessionState.SetString(Key + "Output", "Logs/vc/unity-transport-" + name + ".json");
        SessionState.SetString(Key + "SceneHash", Hash(File.ReadAllBytes(MainScene)));
        SessionState.SetString(Key + "QualityHash", Hash(File.ReadAllBytes(Quality)));
        SessionState.SetBool(Key + "Background", Application.runInBackground);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SessionState.SetBool(Key + "Active", true); SessionState.SetBool(Key + "Finished", false);
        SessionState.SetFloat(Key + "Deadline", (float)EditorApplication.timeSinceStartup + 1500f);
        EditorApplication.isPlaying = true;
    }

    static void Begin()
    {
        try
        {
            checks = 0; generation = 0; clips.Clear(); rows = new JArray();
            endpoint = SessionState.GetString(Key + "Url", ""); reference = SessionState.GetString(Key + "Reference", "");
            string path = SessionState.GetString(Key + "Input", ""); input = File.ReadAllBytes(path);
            Require(input.Length > 44 && System.Text.Encoding.ASCII.GetString(input, 0, 4) == "RIFF" &&
                System.Text.Encoding.ASCII.GetString(input, 8, 4) == "WAVE", "Input must be a nonempty WAV.");
            Require(SceneManager.GetActiveScene().path == "", "VC test must use an unsaved scene.");
            var host = new GameObject("Isolated synthetic VC transport trial");
            host.AddComponent<AudioListener>(); source = host.AddComponent<AudioSource>();
            source.playOnAwake = false; source.mute = true; source.loop = false; source.spatialBlend = 0; source.pitch = 1;
            Application.runInBackground = true;
            report = new JObject {
                ["status"] = "running", ["startedUtc"] = DateTime.UtcNow.ToString("O"),
                ["scope"] = "Actual remote VC WAV -> UnityWebRequest/DownloadHandlerAudioClip -> muted AudioSource progression/completion and transport-owner cancellation. Default input is synthetic Haruka speech. Not a singing-quality, physical audibility, LLM intention, ChatSample authorization or autonomous-new-song test.",
                ["endpoint"] = endpoint, ["referencePath"] = reference, ["inputPath"] = path,
                ["inputSha256"] = Hash(input), ["inputBytes"] = input.Length, ["sourceProvenance"] = "caller-selected WAV; launcher defaults to synthetic Haruka ASR fixture",
                ["productionAudioDecoder"] = "DownloadHandlerAudioClip.GetContent, same decoder used by ChatSample.RequestNeuralHumBack",
                ["productionChatSampleFlowInvoked"] = false, ["cancellationOwner"] = "isolated test transport generation, not ChatSample singing task",
                ["microphoneUsed"] = false, ["llmUsed"] = false, ["memoryOrPlaylistWrites"] = false,
                ["sceneSaved"] = false, ["audioMuted"] = true, ["unityVersion"] = Application.unityVersion, ["rows"] = rows
            };
            work = new Stack<IEnumerator>(); work.Push(Run()); frame = -1;
        }
        catch (Exception error) { Finish(error); }
    }

    static IEnumerator Run()
    {
        yield return Convert("convert-complete");
        AudioClip first = downloaded;
        yield return Play(first, "natural-playback", true);
        yield return Play(first, "cancel-playback", false);
        yield return CancelConversion();
        yield return Convert("convert-successor");
        Require(downloaded != first, "Successor must own its separately decoded clip.");
        yield return Play(downloaded, "successor-playback", false);
        report["successorUsable"] = true;
    }

    static UnityWebRequest Request(string requestId)
    {
        // Same public multipart fields and WAV DownloadHandler as the production
        // full-clip RequestNeuralHumBack adapter, without fabricating its goals.
        var form = new WWWForm();
        form.AddBinaryData("source_audio", input, "synthetic-validation.wav", "audio/wav");
        form.AddField("target_path", reference); form.AddField("request_id", requestId);
        form.AddField("diffusion_steps", 20); form.AddField("auto_f0_adjust", "true");
        form.AddField("semitone_shift", 0); form.AddField("performance_seed", 1234);
        form.AddField("rms_mix_rate", "0.85"); form.AddField("protect", "0.33");
        form.AddField("interpretation", "0"); form.AddField("index_rate", "0.75"); form.AddField("max_seconds", "120");
        var request = UnityWebRequest.Post(endpoint, form);
        request.downloadHandler = new DownloadHandlerAudioClip(endpoint, AudioType.WAV);
        request.timeout = 600;
        return request;
    }

    static IEnumerator Convert(string id)
    {
        string requestId = "unity-vc-" + Guid.NewGuid().ToString("N");
        var row = Row(id); row["requestId"] = requestId;
        int owner = ++generation;
        long started = Clock();
        using (var request = Request(requestId))
        {
            activeRequest = request; request.SendWebRequest();
            yield return Until(() => request.isDone, 610f, "VC request did not complete.");
            activeRequest = null;
            row["requestSeconds"] = Seconds(started); row["httpStatus"] = request.responseCode;
            row["transportResult"] = request.result.ToString();
            Require(owner == generation, "Superseded conversion cannot publish an AudioClip.");
            Require(request.result == UnityWebRequest.Result.Success && request.responseCode == 200,
                "VC transport failed: " + request.responseCode + "/" + request.error);
            Require(string.Equals(request.GetResponseHeader("X-SVC-Complete"), "true", StringComparison.OrdinalIgnoreCase),
                "HTTP success alone is insufficient: server did not mark the WAV complete.");
            foreach (string header in new[] { "Backend", "Device", "Source-Seconds", "Output-Seconds", "Elapsed-Seconds", "CPU-Fallback" })
                row[header] = request.GetResponseHeader("X-SVC-" + header) ?? "";
            AudioClip value = DownloadHandlerAudioClip.GetContent(request);
            Require(value != null && value.samples > 0 && value.length > .1f, "Production WAV decoder returned no usable clip.");
            clips.Add(value);
            var pcm = new float[value.samples * value.channels];
            Require(value.GetData(pcm, 0), "Decoded clip PCM is not readable.");
            double energy = 0; float peak = 0;
            foreach (float sample in pcm)
            {
                if (float.IsNaN(sample) || float.IsInfinity(sample)) throw new InvalidDataException("Decoded WAV contains non-finite PCM.");
                peak = Mathf.Max(peak, Mathf.Abs(sample)); energy += sample * (double)sample;
            }
            Require(peak > 1e-5f, "Decoded output is entirely silent.");
            Require(double.TryParse((string)row["Output-Seconds"], NumberStyles.Float, CultureInfo.InvariantCulture, out double outputSeconds) &&
                Math.Abs(value.length - outputSeconds) <= .15, "Decoded duration does not match server completeness metadata.");
            row["decodedSeconds"] = value.length; row["sampleRate"] = value.frequency; row["channels"] = value.channels;
            row["peak"] = peak; row["rms"] = Math.Sqrt(energy / pcm.Length);
            row["decodedFrames"] = value.samples;
            downloaded = value; row["status"] = "passed";
        }
    }

    static IEnumerator Play(AudioClip value, string id, bool naturalEnd)
    {
        var row = Row(id); int owner = ++generation;
        source.Stop(); source.clip = value; source.loop = false; source.Play();
        long started = Clock(); int maximumFrames = 0; bool playingObserved = false;
        while (Seconds(started) < (naturalEnd ? value.length + 5 : .6))
        {
            Require(owner == generation && source.clip == value, "Playback ownership changed unexpectedly.");
            maximumFrames = Math.Max(maximumFrames, source.timeSamples);
            playingObserved |= source.isPlaying;
            if (naturalEnd && playingObserved && !source.isPlaying) break;
            yield return null;
        }
        row["observedSeconds"] = Seconds(started); row["maximumTimeSamples"] = maximumFrames;
        row["playingObserved"] = playingObserved; row["clipFrames"] = value.samples;
        Require(playingObserved && maximumFrames > value.frequency / 20, "AudioSource never advanced through actual decoded PCM.");
        if (naturalEnd)
        {
            Require(!source.isPlaying && maximumFrames >= value.samples * .8 && Seconds(started) >= value.length - .3,
                "AudioSource stopped before the decoded clip was substantially played.");
            row["naturalCompletion"] = true;
        }
        else
        {
            ++generation; source.Stop(); source.clip = null;
            long cancelledAt = Clock();
            while (Seconds(cancelledAt) < 1.0)
            {
                Require(!source.isPlaying && source.clip == null, "Cancelled playback restarted or rebound its old clip.");
                yield return null;
            }
            row["cancelledPlaybackRemainedStopped"] = true;
        }
        row["status"] = "passed";
    }

    static IEnumerator CancelConversion()
    {
        var row = Row("cancel-inflight-conversion"); string requestId = "unity-vc-" + Guid.NewGuid().ToString("N");
        row["requestId"] = requestId;
        source.Stop(); source.clip = null;
        int owner = ++generation; long started = Clock();
        using (var request = Request(requestId))
        {
            activeRequest = request; request.SendWebRequest();
            yield return Until(() => request.uploadProgress >= 1f || request.isDone, 30f, "Cancel fixture upload did not finish.");
            yield return Delay(.25f); // Allow the real route to register its request ID.
            Require(!request.isDone, "Conversion finished before cancellation; this run cannot prove in-flight cancellation.");
            ++generation;
            string cancelUrl = endpoint.Substring(0, endpoint.LastIndexOf('/')) + "/cancel/" + UnityWebRequest.EscapeURL(requestId);
            using (var cancel = UnityWebRequest.PostWwwForm(cancelUrl, ""))
            {
                cancel.timeout = 10; cancel.SendWebRequest();
                yield return Until(() => cancel.isDone, 12f, "VC cancellation acknowledgement timed out.");
                row["cancelHttpStatus"] = cancel.responseCode;
                Require(cancel.result == UnityWebRequest.Result.Success, "VC cancellation endpoint failed.");
                JObject receipt = JObject.Parse(cancel.downloadHandler.text);
                Require((bool?)receipt["cancelled"] == true, "The server did not find the in-flight request to cancel.");
                row["serverAcceptedCancellation"] = true;
            }
            if (!request.isDone) request.Abort();
            yield return Until(() => request.isDone, 10f, "Cancelled Unity download did not terminate.");
            activeRequest = null;
            row["oldHttpStatus"] = request.responseCode; row["oldTransportResult"] = request.result.ToString();
            row["elapsedSeconds"] = Seconds(started);
            Require(owner != generation && !source.isPlaying && source.clip == null,
                "Cancelled conversion retained playback authority.");
            // Do not decode/publish a result belonging to the revoked owner, even
            // if bytes raced with cancellation. This is the test transport gate.
            row["oldResultDecodedOrPlayed"] = false;
        }
        yield return Delay(1f);
        Require(!source.isPlaying && source.clip == null, "Cancelled conversion later resumed playback.");
        row["status"] = "passed";
    }

    static JObject Row(string id)
    { activeRow = new JObject { ["id"] = id, ["status"] = "running" }; rows.Add(activeRow); return activeRow; }
    static IEnumerator Until(Func<bool> condition, float timeout, string failure)
    { long start = Clock(); while (!condition()) { if (Seconds(start) > timeout) throw new TimeoutException(failure); yield return null; } }
    static IEnumerator Delay(float seconds)
    { long start = Clock(); while (Seconds(start) < seconds) yield return null; }
    static long Clock() => System.Diagnostics.Stopwatch.GetTimestamp();
    static double Seconds(long start) => (Clock() - start) / (double)System.Diagnostics.Stopwatch.Frequency;
    static void Require(bool valid, string message) { checks++; if (!valid) throw new InvalidOperationException(message); }
    static string Env(string name, string fallback) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? fallback : Environment.GetEnvironmentVariable(name);
    static string Hash(byte[] bytes)
    { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""); }

    static void Tick()
    {
        if (!SessionState.GetBool(Key + "Active", false) || SessionState.GetBool(Key + "Finished", false)) return;
        try
        {
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Key + "Deadline", 0)) throw new TimeoutException("VC trial deadline.");
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
        work = null; ++generation;
        if (activeRequest != null) { activeRequest.Abort(); activeRequest.Dispose(); activeRequest = null; }
        if (source != null) { source.Stop(); source.clip = null; }
        foreach (var value in clips) if (value != null) UnityEngine.Object.Destroy(value);
        clips.Clear(); report = report ?? new JObject();
        report["status"] = error == null ? "passed" : "failed"; report["checks"] = checks;
        report["finishedUtc"] = DateTime.UtcNow.ToString("O");
        if (error != null) { report["error"] = error.ToString(); if (activeRow != null) activeRow["status"] = "failed"; }
        bool unchanged = Unchanged(); report["savedSceneAndQualityUnchangedDuringTrial"] = unchanged;
        if (!unchanged) report["status"] = "failed";
        string path = SessionState.GetString(Key + "Output", "Logs/vc/unity-transport-spark.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, report.ToString());
        SessionState.SetBool(Key + "Passed", error == null && unchanged); SessionState.SetBool(Key + "Finished", true);
        if (EditorApplication.isPlaying) EditorApplication.isPlaying = false; else Exit();
    }

    static bool Unchanged() => Hash(File.ReadAllBytes(MainScene)) == SessionState.GetString(Key + "SceneHash", "") &&
        Hash(File.ReadAllBytes(Quality)) == SessionState.GetString(Key + "QualityHash", "");
    static void Exit()
    {
        bool passed = SessionState.GetBool(Key + "Passed", false);
        SessionState.SetBool(Key + "Active", false); SessionState.SetBool(Key + "Finished", false);
        Application.runInBackground = SessionState.GetBool(Key + "Background", Application.runInBackground);
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        string path = SessionState.GetString(Key + "Output", "Logs/vc/unity-transport-spark.json");
        var final = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        bool unchanged = Unchanged(); passed &= unchanged; final["savedSceneAndQualityUnchangedAfterExit"] = unchanged;
        if (!passed) final["status"] = "failed";
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, final.ToString());
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
