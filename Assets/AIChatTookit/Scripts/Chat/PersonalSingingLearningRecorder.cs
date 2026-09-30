using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Recorder-owned capture tickets. Never called by partial ASR or crop recovery.
// Keep AudioClip access on Unity's thread; listeners receive a sealed WAV snapshot.
public sealed class PersonalSingingLearningRecorder
{
    private readonly string root = Guid.NewGuid().ToString("N");
    private int serial;
    private bool sealedCapture;
    private bool pitchCancelled;
    private int pitchSamples;
    public bool WantsPitch => !sealedCapture && !pitchCancelled && serial > 0 &&
        PersonalSingingLearningCapture.IsCaptureActive(root, serial);

    public void Begin()
    {
        PersonalSingingLearningCapture.AbortCapture(root, serial);
        ++serial;
        sealedCapture = false;
        pitchCancelled = false; pitchSamples = 0;
        PersonalSingingLearningCapture.BeginCapture(root, serial);
    }

    public void CancelPitch()
    {
        pitchCancelled = true;
        PersonalSingingLearningCapture.CancelPitch(root, serial);
    }

    public void PushPitch(float[] samples, int channels, int rate)
    {
        if (!WantsPitch) return;
        if (channels != 1 || rate != 16000 || samples == null || pitchSamples + samples.Length > 16000 * 120)
        { CancelPitch(); return; }
        // Same quantization as the authoritative original WAV; chunks are <= 1 second.
        for (int start = 0; start < samples.Length; start += 16000)
        {
            int count = Math.Min(16000, samples.Length - start);
            var bytes = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                float value = samples[start + i];
                if (float.IsNaN(value) || float.IsInfinity(value)) { CancelPitch(); return; }
                short pcm = (short)(Math.Max(-1f, Math.Min(1f, value)) * 32767);
                bytes[2 * i] = (byte)pcm; bytes[2 * i + 1] = (byte)(pcm >> 8);
            }
            PersonalSingingLearningCapture.PublishPitch(bytes, root, serial, pitchSamples);
            pitchSamples += count;
        }
    }

    public void Seal(AudioClip clip, JObject lineage = null)
    {
        if (sealedCapture || serial <= 0) return;
        sealedCapture = true;
        if (!PersonalSingingLearningCapture.IsCaptureActive(root, serial) ||
            clip == null || clip.samples <= 0 || clip.frequency <= 0)
        { PersonalSingingLearningCapture.AbortCapture(root, serial); return; }
        try
        {
            // At most the same 120 s supported by the acoustic provider. Do not
            // truncate long captures and then call that truncated buffer original.
            if (clip.samples / (double)clip.frequency > 120)
            {
                Debug.LogWarning("[PersonalSingingLearning] 本次录音超过 120 秒，未加入学习列表。");
                return;
            }
            var regions = lineage?["regions"] as JArray;
            string parent = regions != null && regions.Count > 0 ? (string)regions[0]["sourceId"] : null;
            parent = parent ?? root + ":" + serial;
            float[] samples = new float[checked(clip.samples * clip.channels)];
            if (!clip.GetData(samples, 0)) throw new InvalidOperationException("无法读取原始录音采样");
            PersonalSingingLearningCapture.Publish(EncodePcm16(samples, clip.channels, clip.frequency), root, serial,
                parent, lineage?.ToString(Formatting.None) ?? "");
        }
        catch (Exception error)
        {
            // Learning must not prevent the existing ASR/EOU handoff.
            Debug.LogWarning("[PersonalSingingLearning] 保存学习录音失败：" + error.Message);
        }
        finally { PersonalSingingLearningCapture.AbortCapture(root, serial); }
    }

    public static byte[] EncodePcm16(float[] samples, int channels, int rate)
    {
        if (samples == null || channels < 1 || channels > 2 || rate < 8000 || rate > 96000 ||
            samples.Length == 0 || samples.Length % channels != 0)
            throw new ArgumentException("invalid_original_pcm");
        int bytes = checked(samples.Length * 2);
        if (bytes > 24 * 1024 * 1024 - 44) throw new ArgumentException("original_pcm_too_large");
        using (var stream = new MemoryStream(bytes + 44))
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(bytes + 36);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((ushort)1); writer.Write((ushort)channels); writer.Write(rate);
            writer.Write(checked(rate * channels * 2)); writer.Write((ushort)(channels * 2)); writer.Write((ushort)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(bytes);
            foreach (float value in samples)
            {
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentException("nonfinite_original_pcm");
                writer.Write((short)(Math.Max(-1f, Math.Min(1f, value)) * 32767));
            }
            writer.Flush(); return stream.ToArray();
        }
    }
}
