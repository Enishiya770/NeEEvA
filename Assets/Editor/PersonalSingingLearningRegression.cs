using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

// Runs without Unity objects, microphone, network, model fitting or human labels.
public static class PersonalSingingLearningRegression
{
    public static int Run(string root)
    {
        int count = 0;
        void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); count++; }
        var leases = new PersonalLearningCaptureLeases();
        leases.Begin("recorder", 1, "person-a", "session-a", "song-a");
        Check(!leases.TrySeal("recorder", 1, out _), "default-off never admits a capture");
        leases.SetEnabled(true);
        Check(!leases.TrySeal("recorder", 1, out _), "enabling after recording started cannot admit its final PCM");
        leases.Begin("recorder", 2, "person-a", "session-a", "song-a");
        leases.Begin("recorder", 2, "person-b", "session-b", "song-b");
        Check(leases.TrySeal("recorder", 2, out var frozen) && frozen.Profile == "person-a" && frozen.Session == "session-a" && frozen.Group == "song-a", "repeated Begin cannot change the capture owner");
        Check(!leases.TrySeal("recorder", 2, out _), "same capture cannot be queued twice");
        leases.Begin("recorder", 3, "person-a", "session-a", "song-a"); leases.SetEnabled(false); leases.SetEnabled(true);
        Check(!leases.Contains("recorder", 3) && !leases.TrySeal("recorder", 3, out _), "off revokes old captures even after reenable");
        leases.Begin("recorder", 4, "person-a", "session-a", "song-a"); leases.Abort("recorder", 4);
        Check(!leases.Contains("recorder", 4), "aborted microphone capture releases its lease");
        for (int i = 0; i < 80; i++) { leases.Begin("failed", i + 1, "a", "s", "g"); leases.Abort("failed", i + 1); }
        leases.Begin("failed", 100, "a", "s", "g");
        Check(leases.Contains("failed", 100), "repeated aborted starts cannot fill the lease capacity");
        leases.Begin("missing", 1, "", "s", "g"); Check(!leases.Contains("missing", 1), "missing profile cannot collect");
        leases.Begin("missing", 2, "a", "", "g"); Check(!leases.Contains("missing", 2), "missing session cannot collect");

        byte[] stereo = Wave(2, 16000, 80);
        Check(PersonalSingingLearningProtocol.TryWave(stereo, out int rate, out int channels, out int frames, out int offset, out _) && rate == 16000 && channels == 2 && frames == 80 && offset == 44, "original stereo sample count remains per-channel frames");
        Check(BitConverter.ToInt16(stereo, offset + 2) != BitConverter.ToInt16(stereo, offset), "fixture stereo channels differ; preview must retain both");
        var truncated = new byte[stereo.Length - 1]; Array.Copy(stereo, truncated, truncated.Length);
        Check(!PersonalSingingLearningProtocol.TryWave(truncated, out _, out _, out _, out _, out _), "truncated PCM cannot enter annotation");
        string hash = PersonalSingingLearningProtocol.Sha256(stereo); stereo[45] ^= 1;
        Check(hash != PersonalSingingLearningProtocol.Sha256(stereo), "raw source hash changes with a real PCM byte");
        Check(PersonalSingingLearningProtocol.TryRange("0.003", "0.004", true, 16000, 80, out int start, out int end, out _) && start == 48 && end == 64, "seconds map to original half-open integer samples");
        Check(!PersonalSingingLearningProtocol.TryRange("", "0.004", true, 16000, 80, out _, out _, out _), "empty fields cannot reuse a prior selection");
        Check(!PersonalSingingLearningProtocol.TryRange("0.004", "0.003", true, 16000, 80, out _, out _, out _), "reversed selection rejected");
        Check(!PersonalSingingLearningProtocol.TryRange("0", "9999999999999999999999999", true, 16000, 80, out _, out _, out _), "extreme typed seconds fail before multiplication overflow");
        Check(!PersonalSingingLearningProtocol.TryRange("0", "1,2", false, 16000, 80, out _, out _, out _), "ambiguous grouped numeric input is not silently reinterpreted");
        Check(!PersonalSingingLearningProtocol.TryRange("1.5", "10", false, 16000, 80, out _, out _, out _), "sample coordinates must be integers");
        var ranges = new JArray(new JObject { ["start_sample"] = 20, ["end_sample"] = 40, ["label"] = "singing", ["tolerance_samples"] = 2 });
        Check(PersonalSingingLearningProtocol.ValidateRanges(ranges, 80, false, out _), "partial human annotations permitted without inventing gap labels");
        Check(!PersonalSingingLearningProtocol.ValidateRanges(ranges, 80, true, out _), "partial annotation is not full coverage");
        ranges.Add(new JObject { ["start_sample"] = 39, ["end_sample"] = 60, ["label"] = "speech" });
        Check(!PersonalSingingLearningProtocol.ValidateRanges(ranges, 80, false, out _), "overlapping human draft is rejected");
        Check(Array.IndexOf(PersonalSingingLearningProtocol.Labels, "environment") >= 0 && Array.IndexOf(PersonalSingingLearningProtocol.Sources, "unknown") >= 0, "environment and source identity remain separate concepts");

        var result = new JObject { ["job_id"] = "synthetic", ["status"] = "completed", ["result"] = new JObject { ["status"] = "candidate", ["model_id"] = "test-only", ["validation_duration_confusion"] = new JObject { ["tp_seconds"] = 1, ["fp_seconds"] = 2, ["fn_seconds"] = 3, ["tn_seconds"] = 4 } } };
        string display = PersonalSingingLearningResults.Describe(result);
        Check(display.Contains("误收非歌声 2.00 秒") && display.Contains("漏掉歌声 3.00 秒") && display.Contains("正式裁切未启用") && !display.Contains("\"job_id\""), "human-facing results report concrete errors without claiming production accuracy");
        display = PersonalSingingLearningResults.Describe(new JObject { ["readiness_reasons"] = new JArray("need_at_least_3_independent_groups", "validation_missing_positive_known_duration") });
        Check(display.Contains("至少 3 个") && display.Contains("缺明确的本人歌唱"), "insufficient-data results explain the actual missing evidence");

        int delivered = 0, aborted = 0;
        Action<string, int> badStart = (_, __) => throw new Exception("controlled subscriber failure");
        Action<string, int> goodStart = (_, __) => delivered++;
        Action<byte[], string, int, string, string> badEnd = (_, __, ___, ____, _____) => throw new Exception("controlled subscriber failure");
        Action<byte[], string, int, string, string> goodEnd = (_, __, ___, ____, _____) => delivered++;
        Action<string, int> abort = (_, __) => aborted++;
        Func<bool> badReady = () => throw new Exception("controlled query failure"); Func<bool> goodReady = () => true;
        Func<string, int, bool> badActive = (_, __) => throw new Exception("controlled query failure"); Func<string, int, bool> goodActive = (_, __) => true;
        try
        {
            PersonalSingingLearningCapture.Started += badStart; PersonalSingingLearningCapture.Started += goodStart;
            PersonalSingingLearningCapture.Completed += badEnd; PersonalSingingLearningCapture.Completed += goodEnd;
            PersonalSingingLearningCapture.Aborted += abort;
            PersonalSingingLearningCapture.Readiness += badReady; PersonalSingingLearningCapture.Readiness += goodReady;
            PersonalSingingLearningCapture.ActiveCapture += badActive; PersonalSingingLearningCapture.ActiveCapture += goodActive;
            Check(PersonalSingingLearningCapture.WantsCapture && PersonalSingingLearningCapture.IsCaptureActive("fixture", 1), "throwing UI query cannot break recorder readiness");
            PersonalSingingLearningCapture.BeginCapture("fixture", 1); PersonalSingingLearningCapture.Publish(stereo, "fixture", 1, null, null);
            Check(delivered == 2, "throwing auxiliary listeners do not block remaining listeners or ASR caller");
            PersonalSingingLearningCapture.Publish(null, "fixture", 2, null, null);
            Check(aborted == 1, "invalid raw capture explicitly aborts rather than leaking its start lease");
        }
        finally
        {
            PersonalSingingLearningCapture.Started -= badStart; PersonalSingingLearningCapture.Started -= goodStart;
            PersonalSingingLearningCapture.Completed -= badEnd; PersonalSingingLearningCapture.Completed -= goodEnd;
            PersonalSingingLearningCapture.Aborted -= abort;
            PersonalSingingLearningCapture.Readiness -= badReady; PersonalSingingLearningCapture.Readiness -= goodReady;
            PersonalSingingLearningCapture.ActiveCapture -= badActive; PersonalSingingLearningCapture.ActiveCapture -= goodActive;
        }
        return count;
    }
    private static byte[] Wave(int channels, int rate, int frames)
    {
        using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * channels * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16); writer.Write((short)1); writer.Write((short)channels); writer.Write(rate); writer.Write(rate * channels * 2);
            writer.Write((short)(channels * 2)); writer.Write((short)16); writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(frames * channels * 2);
            for (int i = 0; i < frames * channels; i++) writer.Write((short)(i * 7 - 500));
            return stream.ToArray();
        }
    }
    public static int RunRuntime(string root)
    {
        int count = 0;
        void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); count++; }
        var go = new UnityEngine.GameObject("Personal learning inactive owner checks"); go.SetActive(false);
        var client = go.AddComponent<PersonalSingingLearningClient>();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        object Read(string name) => typeof(PersonalSingingLearningClient).GetField(name, flags).GetValue(client);
        void Write(string name, object value) => typeof(PersonalSingingLearningClient).GetField(name, flags).SetValue(client, value);
        try
        {
            Write("<ProfileId>k__BackingField", "person-a"); Write("<SessionId>k__BackingField", "old-session");
            Write("<ContentGroup>k__BackingField", "old-song"); Write("<ContentGroupName>k__BackingField", "旧歌曲");
            Write("outboxReady", true);
            var leases = (PersonalLearningCaptureLeases)Read("leases"); leases.SetEnabled(true); leases.Begin("fixture", 1, "person-a", "old-session", "old-song");
            client.NewSession("新歌曲");
            Check(client.SessionId == null && client.ContentGroup == null && !client.CollectionEnabled, "actual NewSession invalidates the old session even if no request can start");
            Check(!leases.Contains("fixture", 1), "actual session switch revokes old recording owner");
            client.SetCollection(true); Check(!client.CollectionEnabled, "new-session failure cannot enable collection into the previous group");
            int preview = (int)Read("previewGeneration"); client.StopPreview();
            Check((int)Read("previewGeneration") > preview, "actual stop preview revokes pending download playback permission");
            var itemType = typeof(PersonalSingingLearningClient).GetNestedType("OutboxItem", System.Reflection.BindingFlags.NonPublic);
            var item = Activator.CreateInstance(itemType); itemType.GetField("State").SetValue(item, "上传中");
            var queue = (System.Collections.IList)Read("outbox"); queue.Add(item); Write("uploadRunning", true);
            typeof(PersonalSingingLearningClient).GetMethod("OnDisable", flags).Invoke(client, null);
            Check(!(bool)Read("uploadRunning") && (string)itemType.GetField("State").GetValue(item) == "待上传", "actual disable leaves the saved upload eligible for idempotent retry on reenable");
            queue.Clear();
            Check(Read("previewClip") == null && Read("interactiveRequest") == null && Read("uploadRequest") == null, "owner checks created no audio playback or network request");
            string directory = Path.Combine(root, "work/personal-singing-learning-20260929/client-owner-fixture", Guid.NewGuid().ToString("N"));
            var originalManifest = new JObject { ["recording_id"] = "synthetic-persist" };
            var raw = Wave(1, 16000, 80);
            itemType.GetField("Manifest").SetValue(item, originalManifest);
            itemType.GetField("Memory").SetValue(item, raw);
            itemType.GetField("Bytes").SetValue(item, (long)raw.Length);
            itemType.GetField("Profile").SetValue(item, "synthetic-profile");
            itemType.GetField("Url").SetValue(item, "http://127.0.0.1:9884");
            itemType.GetField("WavPath").SetValue(item, Path.Combine(directory, "original.wav"));
            itemType.GetField("MetaPath").SetValue(item, Path.Combine(directory, "original.json"));
            var detached = (JObject)typeof(PersonalSingingLearningClient).GetMethod("Persist", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new[] { item });
            Check(originalManifest["audio_sha256"] == null && (string)detached["audio_sha256"] == PersonalSingingLearningProtocol.Sha256(raw), "real background persistence returns detached metadata without mutating the UI manifest");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
        return count;
    }
}
