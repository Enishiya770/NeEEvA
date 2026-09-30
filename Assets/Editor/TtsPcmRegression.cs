using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Deterministic PCM correctness checks against the production private download handler.</summary>
public static class TtsPcmRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static JObject LastReport { get; private set; }

    public static void RunBatch()
    {
        if (!Application.isBatchMode || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("PCM regression requires batch edit mode; it never enters Play mode.");
        checks = 0;
        var cases = new JArray();
        LastReport = new JObject {
            ["status"] = "running", ["cases"] = cases,
            ["scope"] = "Production PcmStreamingDownloadHandler via reflection, deterministic generated PCM16 WAV bytes and simulated audio-reader calls. No network, AudioSource, real DSP scheduling or speaker/audibility test.",
            ["networkRequests"] = 0, ["audioSources"] = 0
        };
        int failures = 0;
        Case(cases, ref failures, "fragmented-header-and-odd-byte-pcm", FragmentedHeaderAndOddBytes);
        Case(cases, ref failures, "partial-read-preserves-data-and-recovers", PartialReadAndRecovery);
        Case(cases, ref failures, "consecutive-underruns-consumed-once", ConsecutiveUnderruns);
        Case(cases, ref failures, "underrun-snapshot-first-gap-and-reset", UnderrunSnapshotAndReset);
        Case(cases, ref failures, "underrun-zero-position-survives-eof", UnderrunAtZeroAndEof);
        Case(cases, ref failures, "stereo-underrun-index-counts-interleaved-samples", StereoUnderrunSnapshot);
        Case(cases, ref failures, "short-wav-eof-tail-is-not-underrun", ShortEofTail);
        Case(cases, ref failures, "long-wav-crosses-download-and-reader-boundaries", LongWav);
        Case(cases, ref failures, "stereo-interleaving-and-duration", StereoWav);
        LastReport["checks"] = checks;
        LastReport["failedCases"] = failures;
        LastReport["status"] = failures == 0 ? "passed" : "failed";
        string directory = Path.GetFullPath("Logs/tts-latency");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "pcm-regression.json"), LastReport.ToString(Formatting.Indented));
        Debug.Log("[TTS/PCMRegression] " + LastReport.ToString(Formatting.None));
        EditorApplication.Exit(failures == 0 ? 0 : 1);
    }

    private static void Case(JArray cases, ref int failures, string name, Action test)
    {
        int before = checks;
        var result = new JObject { ["name"] = name };
        try { test(); result["status"] = "passed"; }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            result["status"] = "failed";
            result["error"] = error.GetType().Name + ": " + error.Message;
            failures++;
        }
        result["checks"] = checks - before;
        cases.Add(result);
    }

    private static void FragmentedHeaderAndOddBytes()
    {
        short[] samples = { short.MinValue, -12345, -1, 0, 1, 2345, short.MaxValue };
        byte[] wav = Wave(samples, 32000, 1);
        using (var h = new Handler())
        {
            h.Feed(wav, 0, 13); h.Feed(wav, 13, 17); h.Feed(wav, 30, 13);
            Require(!h.Value<bool>("HeaderReady") && h.Value<long>("TotalSamplesReceived") == 0,
                "An incomplete 43-byte header cannot become PCM.");
            h.Feed(wav, 43, 2); // Final header byte plus one pending PCM byte.
            Require(h.Value<bool>("HeaderReady") && !h.Value<bool>("FormatError") &&
                h.Value<long>("TotalSamplesReceived") == 0, "One odd PCM byte waits for its partner.");
            FeedPattern(h, wav, 45, new[] { 1, 3, 1, 5, 2 });
            h.Complete();
            int cursor = 0;
            Aligned(h.Read(3), samples, ref cursor);
            Aligned(h.Read(6), samples, ref cursor);
            Require(cursor == samples.Length && h.Value<int>("BufferedSampleCount") == 0 &&
                h.Value<long>("TotalSamplesReceived") == samples.Length,
                "Odd download boundaries preserve every signed PCM sample exactly once.");
            Require(h.Value<long>("LastRealOutputSamplePosition") == samples.Length && h.Signals() == 0,
                "EOF padding does not move the final real sample or create an underrun.");
        }
    }

    private static void PartialReadAndRecovery()
    {
        short[] samples = { -30000, 500, 25000, -123, 32767 };
        byte[] wav = Wave(samples, 32000, 1);
        using (var h = new Handler())
        {
            h.Feed(wav, 0, 44 + 6);
            float[] first = h.Read(5);
            int cursor = 0;
            Aligned(first, new short[] { samples[0], samples[1], samples[2] }, ref cursor);
            Require(h.Value<int>("BufferedSampleCount") == 0 && h.Value<long>("TotalOutputSamplesWritten") == 5 &&
                h.Value<long>("LastRealOutputSamplePosition") == 3,
                "Partial read consumes the available three samples before filling its two-sample shortage.");
            Near(h.Value<float>("InsertedSilenceSeconds"), 2f / 32000, "Only the actual shortage is counted as inserted silence.");
            Require(h.Signals() == 1 && h.Signals() == 0, "Partial starvation delivers one consumable signal.");
            h.Feed(wav, 50, 4);
            cursor = 0;
            Aligned(h.Read(3), new short[] { samples[3], samples[4] }, ref cursor);
            Require(h.Value<long>("LastRealOutputSamplePosition") == 7 && h.Value<long>("TotalOutputSamplesWritten") == 8,
                "Recovered PCM follows the earlier output silence without replaying or losing samples.");
            Near(h.Value<float>("InsertedSilenceSeconds"), 3f / 32000, "Recovery counts only its one new missing sample.");
            Require(h.Signals() == 1, "A second partial shortage reports its own event.");
            h.Complete();
            AllZero(h.Read(9));
            Require(h.Signals() == 0 && h.Value<long>("LastRealOutputSamplePosition") == 7,
                "Completed empty reads cannot create a new underrun or a fictitious real endpoint.");
            Near(h.Value<float>("InsertedSilenceSeconds"), 3f / 32000, "EOF padding does not inflate starvation statistics.");
        }
    }

    private static void ConsecutiveUnderruns()
    {
        short[] samples = { 1024, -2048, 3000, -4000, 5000 };
        byte[] wav = Wave(samples, 16000, 1);
        using (var h = new Handler())
        {
            h.Feed(wav, 0, 48);
            int cursor = 0;
            Aligned(h.Read(4), new short[] { samples[0], samples[1] }, ref cursor);
            AllZero(h.Read(3));
            Require(h.Signals() == 2 && h.Signals() == 0,
                "Two consecutive incomplete-input callback shortages can be consumed once as two signals.");
            Near(h.Value<float>("InsertedSilenceSeconds"), 5f / 16000, "Consecutive callbacks count missing samples, not full callbacks.");
            Require(h.Value<long>("LastRealOutputSamplePosition") == 2,
                "An all-silent starvation callback cannot advance the last real sample.");
            h.Feed(wav, 48, 6); h.Complete();
            cursor = 0;
            Aligned(h.Read(4), new short[] { samples[2], samples[3], samples[4] }, ref cursor);
            Require(h.Value<long>("LastRealOutputSamplePosition") == 10 && h.Signals() == 0,
                "The final tail resumes at the correct absolute output position and EOF avoids a false third underrun.");
            Near(h.Value<float>("InsertedSilenceSeconds"), 5f / 16000, "A complete short tail does not add starvation silence.");
        }
    }

    private static void ShortEofTail()
    {
        short[] samples = { short.MinValue, 0, short.MaxValue };
        byte[] wav = Wave(samples, 24000, 1);
        using (var h = new Handler())
        {
            h.Feed(wav, 0, wav.Length);
            // Exercise the real download completion callback, not its helper.
            h.Complete();
            int cursor = 0;
            Aligned(h.Read(1024), samples, ref cursor);
            Require(h.Signals() == 0 && h.Value<long>("LastRealOutputSamplePosition") == 3,
                "CompleteContent makes a real three-sample WAV tail immediately readable without underrun.");
            AllZero(h.Read(257));
            Require(h.Signals() == 0 && h.Value<long>("LastRealOutputSamplePosition") == 3,
                "Subsequent completed empty reads remain padding, not starvation.");
            Near(h.Value<float>("InsertedSilenceSeconds"), 0, "Short WAV padding never raises adaptive-underrun metrics.");
            Near(h.Value<float>("AudioDuration"), 3f / 24000, "Duration uses actual received audio, excluding output padding.");
        }
    }

    private static void UnderrunSnapshotAndReset()
    {
        short[] samples = { -30000, 500, 25000, -123, 32767 };
        byte[] wav = Wave(samples, 32000, 1);
        using (var h = new Handler())
        {
            h.Feed(wav, 0, 50);
            int cursor = 0;
            Aligned(h.Read(5), new short[] { samples[0], samples[1], samples[2] }, ref cursor);
            AllZero(h.Read(4));
            Require(h.Snapshot(out long first) == 2 && first == 3,
                "A partial callback reports its first missing sample, and later empty callbacks cannot overwrite that earliest gap.");
            Require(h.Snapshot(out first) == 0 && first == -1,
                "Consuming the snapshot clears both the pending count and earliest gap in the same operation.");

            h.Feed(wav, 50, 4);
            cursor = 0;
            Aligned(h.Read(3), new short[] { samples[3], samples[4] }, ref cursor);
            Require(h.Snapshot(out first) == 1 && first == 11,
                "After consumption, the next partial gap uses the absolute output position: nine earlier samples plus two recovered samples.");
            Require(h.Snapshot(out first) == 0 && first == -1,
                "A fresh snapshot is empty after the recovered callback's gap has been consumed.");

            AllZero(h.Read(2));
            Require(h.Signals() == 1 && h.Snapshot(out first) == 0 && first == -1,
                "The legacy count-only wrapper also consumes the associated gap, so it cannot leak into a later snapshot.");
            h.Complete();
            AllZero(h.Read(3));
            Require(h.Snapshot(out first) == 0 && first == -1,
                "EOF padding cannot create either a signal or a new gap index after an earlier starvation was consumed.");
        }
    }

    private static void UnderrunAtZeroAndEof()
    {
        byte[] wav = Wave(new short[] { 1000, -1000 }, 32000, 1);
        using (var h = new Handler())
        {
            h.Feed(wav, 0, 44);
            Require(h.Snapshot(out long first) == 0 && first == -1,
                "A ready WAV header without an audio-reader request is not an underrun.");
            AllZero(h.Read(4));
            h.Feed(wav, 44, 4);
            h.Complete();
            int cursor = 0;
            Aligned(h.Read(5), new short[] { 1000, -1000 }, ref cursor);
            Require(h.Snapshot(out first) == 1 && first == 0,
                "Sample zero is a valid first gap; later PCM and EOF padding preserve that unconsumed starvation without adding another.");
            Require(h.Snapshot(out first) == 0 && first == -1,
                "The zero-position gap is cleared after consumption, with minus one reserved for no pending gap.");
            AllZero(h.Read(4));
            Require(h.Snapshot(out first) == 0 && first == -1,
                "Repeated completed reads do not recreate a consumed zero-position gap.");
        }
    }

    private static void StereoUnderrunSnapshot()
    {
        short[] samples = { -1000, 1000, -2000, 2000, -3000, 3000, -4000, 4000 };
        byte[] wav = Wave(samples, 48000, 2);
        using (var h = new Handler())
        {
            h.Feed(wav, 0, 56); // Three stereo frames, six interleaved samples.
            int cursor = 0;
            Aligned(h.Read(4), samples, ref cursor);
            Require(h.Snapshot(out long first) == 0 && first == -1,
                "A stereo callback filled completely from available PCM does not report a gap.");
            cursor = 0;
            Aligned(h.Read(4), new short[] { samples[4], samples[5] }, ref cursor);
            AllZero(h.Read(2));
            Require(h.Snapshot(out first) == 2 && first == 6,
                "The earliest stereo shortage starts at interleaved sample six, not frame three, and survives the next callback.");
            h.Feed(wav, 56, 4);
            cursor = 0;
            Aligned(h.Read(4), new short[] { samples[6], samples[7] }, ref cursor);
            Require(h.Snapshot(out first) == 1 && first == 12,
                "Recovered stereo PCM advances two sample positions from output sample ten before the next shortage.");
            h.Complete();
            AllZero(h.Read(4));
            Require(h.Snapshot(out first) == 0 && first == -1,
                "Stereo EOF padding has no pending gap after the last real starvation was consumed.");
        }
    }

    private static void LongWav()
    {
        var samples = new short[100003];
        for (int i = 0; i < samples.Length; i++) samples[i] = unchecked((short)(i * 73 + 17));
        byte[] wav = Wave(samples, 44100, 1);
        Require(wav.Length > 3 * 65536, "The long WAV crosses several production download-buffer capacities.");
        using (var h = new Handler())
        {
            FeedPattern(h, wav, 0, new[] { 7, 65535, 3, 65536, 257, 8191 });
            h.Complete();
            int cursor = 0, read = 0;
            int[] sizes = { 257, 1024, 4093, 127 };
            while (cursor < samples.Length) Aligned(h.Read(sizes[read++ % sizes.Length]), samples, ref cursor);
            Require(h.Value<long>("TotalSamplesReceived") == samples.Length && h.Value<int>("BufferedSampleCount") == 0 &&
                h.Value<long>("LastRealOutputSamplePosition") == samples.Length && h.Signals() == 0,
                "Long WAV data crosses download and reader boundaries with exact order, length and final real endpoint.");
            Near(h.Value<float>("InsertedSilenceSeconds"), 0, "Long completed WAV has no starvation silence.");
        }
    }

    private static void StereoWav()
    {
        short[] samples = { -32768, 32767, -1000, 1000, -2000, 2000, 0, 0 };
        byte[] wav = Wave(samples, 48000, 2);
        using (var h = new Handler())
        {
            FeedPattern(h, wav, 0, new[] { 3, 5, 7 }); h.Complete();
            Require(h.Value<int>("Channels") == 2 && h.Value<int>("SampleRate") == 48000,
                "The standard WAV channel and sample-rate metadata are preserved.");
            Near(h.Value<float>("BufferedSeconds"), 4f / 48000, "Stereo duration counts frames, not interleaved samples.");
            int cursor = 0;
            Aligned(h.Read(6), samples, ref cursor); Aligned(h.Read(6), samples, ref cursor);
            Require(cursor == samples.Length && h.Value<long>("LastRealOutputSamplePosition") == 8 && h.Signals() == 0,
                "Stereo left/right order and final partial read remain intact.");
        }
    }

    private static byte[] Wave(short[] samples, int sampleRate, short channels)
    {
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new byte[] { 82, 73, 70, 70 }); writer.Write(36 + samples.Length * 2);
            writer.Write(new byte[] { 87, 65, 86, 69, 102, 109, 116, 32 }); writer.Write(16);
            writer.Write((short)1); writer.Write(channels); writer.Write(sampleRate);
            writer.Write(sampleRate * channels * 2); writer.Write((short)(channels * 2)); writer.Write((short)16);
            writer.Write(new byte[] { 100, 97, 116, 97 }); writer.Write(samples.Length * 2);
            foreach (short sample in samples) writer.Write(sample);
            return stream.ToArray();
        }
    }

    private static void FeedPattern(Handler handler, byte[] bytes, int start, int[] sizes)
    {
        int index = 0;
        while (start < bytes.Length)
        {
            int count = Math.Min(sizes[index++ % sizes.Length], bytes.Length - start);
            handler.Feed(bytes, start, count); start += count;
        }
    }

    private static void Aligned(float[] actual, short[] expected, ref int cursor)
    {
        for (int i = 0; i < actual.Length; i++)
        {
            float value = cursor < expected.Length ? expected[cursor++] / 32768f : 0;
            if (actual[i] != value) throw new InvalidOperationException("PCM order/value mismatch at output index " + i + ": " + actual[i] + " != " + value);
        }
        Require(true, "All real samples and any trailing padding match the expected PCM sequence.");
    }

    private static void AllZero(float[] values)
    {
        foreach (float value in values) if (value != 0) throw new InvalidOperationException("Expected zero-filled completed/empty output.");
        Require(true, "The complete empty output is zero-filled.");
    }

    private static void Near(float actual, float expected, string message) => Require(Math.Abs(actual - expected) < .0000001f, message);
    private static void Require(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }

    private sealed class Handler : IDisposable
    {
        private readonly Type type = typeof(GPTSoVITSFASTAPI).GetNestedType("PcmStreamingDownloadHandler", BindingFlags.NonPublic);
        private readonly object instance;
        public Handler()
        {
            if (type == null) throw new MissingMemberException("Production PCM download handler was not found.");
            instance = Activator.CreateInstance(type, true);
        }
        public T Value<T>(string name) => (T)type.GetProperty(name, Flags).GetValue(instance);
        public void Feed(byte[] bytes, int start, int count)
        {
            var part = new byte[count]; Array.Copy(bytes, start, part, 0, count);
            Require((bool)type.GetMethod("ReceiveData", Flags).Invoke(instance, new object[] { part, count }),
                "The production handler accepts a valid WAV fragment.");
        }
        public float[] Read(int count)
        {
            var output = new float[count];
            for (int i = 0; i < count; i++) output[i] = float.NaN;
            type.GetMethod("ReadSamples", Flags).Invoke(instance, new object[] { output });
            return output;
        }
        public void Complete() => type.GetMethod("CompleteContent", Flags).Invoke(instance, null);
        public int Signals() => (int)type.GetMethod("ConsumeUnderrunSignals", Flags).Invoke(instance, null);
        public int Snapshot(out long firstOutputSample)
        {
            var arguments = new object[] { -1L };
            int count = (int)type.GetMethod("ConsumeUnderrunSnapshot", Flags).Invoke(instance, arguments);
            firstOutputSample = (long)arguments[0];
            return count;
        }
        public void Dispose() => ((IDisposable)instance).Dispose();
    }
}
