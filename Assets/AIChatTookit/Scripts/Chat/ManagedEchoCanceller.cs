using System;
using UnityEngine;

/// <summary>
/// Captures the exact signal sent to the character's AudioSource.  The audio
/// callback runs on Unity's mixer thread, so the ring buffer is deliberately
/// small and allocation-free on that thread.
/// </summary>
[DisallowMultipleComponent]
public sealed class PlaybackEchoReferenceTap : MonoBehaviour
{
    [SerializeField, Range(2f, 12f)] private float m_HistorySeconds = 6f;

    private readonly object m_Sync = new object();
    private float[] m_Ring;
    private int m_WritePosition;
    private int m_Count;
    private int m_SampleRate;

    public int SampleRate => m_SampleRate;

    private void Awake()
    {
        InitialiseBuffer();
    }

    private void OnEnable()
    {
        if (m_Ring == null || m_Ring.Length == 0) InitialiseBuffer();
    }

    private void InitialiseBuffer()
    {
        m_SampleRate = Mathf.Max(8000, AudioSettings.outputSampleRate);
        int capacity = Mathf.Max(m_SampleRate * 2,
            Mathf.CeilToInt(m_SampleRate * m_HistorySeconds));
        lock (m_Sync)
        {
            m_Ring = new float[capacity];
            m_WritePosition = 0;
            m_Count = 0;
        }
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (data == null || data.Length == 0 || channels <= 0 || m_Ring == null) return;

        int frames = data.Length / channels;
        lock (m_Sync)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                int offset = frame * channels;
                float mono = 0f;
                for (int channel = 0; channel < channels; channel++)
                    mono += data[offset + channel];
                mono /= channels;

                m_Ring[m_WritePosition] = mono;
                m_WritePosition = (m_WritePosition + 1) % m_Ring.Length;
                if (m_Count < m_Ring.Length) m_Count++;
            }
        }
    }

    public bool TryGetRecent(float seconds, out float[] samples, out int sampleRate)
    {
        samples = null;
        sampleRate = m_SampleRate;
        if (m_Ring == null || m_SampleRate <= 0) return false;

        lock (m_Sync)
        {
            int requested = Mathf.CeilToInt(Mathf.Max(0.1f, seconds) * m_SampleRate);
            int length = Mathf.Min(requested, m_Count);
            if (length <= 0) return false;

            samples = new float[length];
            int start = m_WritePosition - length;
            if (start < 0) start += m_Ring.Length;
            int first = Mathf.Min(length, m_Ring.Length - start);
            Array.Copy(m_Ring, start, samples, 0, first);
            if (first < length) Array.Copy(m_Ring, 0, samples, first, length - first);
            return true;
        }
    }
}

/// <summary>
/// A lightweight reverse-reference echo canceller for barge-in probes.
/// It estimates acoustic/output latency using normalized cross-correlation,
/// then removes the delayed playback component while leaving uncorrelated
/// near-end speech intact.  This is intentionally performed off the audio
/// thread and only on the short VAD probe.
/// </summary>
public static class ManagedEchoCanceller
{
    public struct Result
    {
        public bool Applied;
        public float Correlation;
        public float DelayMs;
        public float Gain;
        public float InputRms;
        public float OutputRms;
    }

    public static AudioClip Cancel(
        AudioClip microphoneClip,
        PlaybackEchoReferenceTap referenceTap,
        float maxDelayMs,
        float minCorrelation,
        float strength,
        out Result result)
    {
        result = new Result();
        if (microphoneClip == null || referenceTap == null) return null;

        int frames = microphoneClip.samples;
        int channels = Mathf.Max(1, microphoneClip.channels);
        int sampleRate = Mathf.Max(8000, microphoneClip.frequency);
        if (frames < sampleRate / 5) return null;

        float[] interleaved = new float[frames * channels];
        if (!microphoneClip.GetData(interleaved, 0)) return null;
        float[] mic = Downmix(interleaved, frames, channels);

        float delaySeconds = Mathf.Clamp(maxDelayMs, 50f, 800f) / 1000f;
        float historySeconds = microphoneClip.length + delaySeconds + 0.25f;
        if (!referenceTap.TryGetRecent(historySeconds, out float[] reference, out int referenceRate))
            return null;

        float[] resampledReference = Resample(reference, referenceRate, sampleRate);
        int maxDelaySamples = Mathf.RoundToInt(delaySeconds * sampleRate);
        int requiredReferenceLength = mic.Length + maxDelaySamples;
        if (resampledReference.Length < requiredReferenceLength)
        {
            // At the beginning of a TTS utterance the tap may contain less history
            // than the microphone probe. Missing history represents silence, so
            // left-padding preserves end-time alignment and still lets AEC engage.
            float[] padded = new float[requiredReferenceLength];
            Array.Copy(
                resampledReference,
                0,
                padded,
                requiredReferenceLength - resampledReference.Length,
                resampledReference.Length);
            resampledReference = padded;
        }

        float[] micFiltered = HighPass(mic);
        float[] referenceFiltered = HighPass(resampledReference);
        int availableDelay = Mathf.Min(maxDelaySamples, resampledReference.Length - mic.Length);
        int delayStep = Mathf.Max(1, sampleRate / 500); // 2 ms coarse search
        int analysisStride = 2;

        float bestCorrelation = 0f;
        int bestDelay = 0;
        for (int delay = 0; delay <= availableDelay; delay += delayStep)
        {
            int referenceStart = resampledReference.Length - mic.Length - delay;
            double dot = 0d;
            double micEnergy = 0d;
            double referenceEnergy = 0d;
            for (int i = 0; i < mic.Length; i += analysisStride)
            {
                float a = micFiltered[i];
                float b = referenceFiltered[referenceStart + i];
                dot += a * b;
                micEnergy += a * a;
                referenceEnergy += b * b;
            }

            if (micEnergy < 1e-9d || referenceEnergy < 1e-9d) continue;
            float correlation = (float)(Math.Abs(dot) / Math.Sqrt(micEnergy * referenceEnergy));
            if (correlation > bestCorrelation)
            {
                bestCorrelation = correlation;
                bestDelay = delay;
            }
        }

        result.Correlation = bestCorrelation;
        result.DelayMs = 1000f * bestDelay / sampleRate;
        result.InputRms = Rms(mic);
        if (bestCorrelation < Mathf.Clamp01(minCorrelation)) return null;

        int bestReferenceStart = resampledReference.Length - mic.Length - bestDelay;
        double micMean = 0d;
        double referenceMean = 0d;
        for (int i = 0; i < mic.Length; i++)
        {
            micMean += mic[i];
            referenceMean += resampledReference[bestReferenceStart + i];
        }
        micMean /= mic.Length;
        referenceMean /= mic.Length;

        double gainNumerator = 0d;
        double gainDenominator = 0d;
        for (int i = 0; i < mic.Length; i++)
        {
            double a = mic[i] - micMean;
            double b = resampledReference[bestReferenceStart + i] - referenceMean;
            gainNumerator += a * b;
            gainDenominator += b * b;
        }
        if (gainDenominator < 1e-9d) return null;

        float gain = Mathf.Clamp((float)(gainNumerator / gainDenominator), -2f, 2f);
        float cancellationStrength = Mathf.Clamp01(strength);
        float[] output = new float[mic.Length];
        for (int i = 0; i < output.Length; i++)
        {
            float echo = resampledReference[bestReferenceStart + i] - (float)referenceMean;
            output[i] = Mathf.Clamp(mic[i] - gain * cancellationStrength * echo, -1f, 1f);
        }

        result.Applied = true;
        result.Gain = gain;
        result.OutputRms = Rms(output);
        AudioClip cleaned = AudioClip.Create(
            microphoneClip.name + "_aec",
            output.Length,
            1,
            sampleRate,
            false);
        cleaned.SetData(output, 0);
        return cleaned;
    }

    private static float[] Downmix(float[] input, int frames, int channels)
    {
        if (channels == 1) return input;
        float[] output = new float[frames];
        for (int frame = 0; frame < frames; frame++)
        {
            float sum = 0f;
            int offset = frame * channels;
            for (int channel = 0; channel < channels; channel++) sum += input[offset + channel];
            output[frame] = sum / channels;
        }
        return output;
    }

    private static float[] Resample(float[] input, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate) return input;
        int outputLength = Mathf.Max(1,
            Mathf.RoundToInt(input.Length * (float)targetRate / Mathf.Max(1, sourceRate)));
        float[] output = new float[outputLength];
        float scale = (input.Length - 1f) / Mathf.Max(1, outputLength - 1);
        for (int i = 0; i < outputLength; i++)
        {
            float position = i * scale;
            int left = Mathf.FloorToInt(position);
            int right = Mathf.Min(left + 1, input.Length - 1);
            output[i] = Mathf.Lerp(input[left], input[right], position - left);
        }
        return output;
    }

    private static float[] HighPass(float[] input)
    {
        float[] output = new float[input.Length];
        if (input.Length == 0) return output;
        float previousInput = input[0];
        float previousOutput = 0f;
        for (int i = 1; i < input.Length; i++)
        {
            float current = input[i] - previousInput + 0.995f * previousOutput;
            output[i] = current;
            previousInput = input[i];
            previousOutput = current;
        }
        return output;
    }

    private static float Rms(float[] samples)
    {
        if (samples == null || samples.Length == 0) return 0f;
        double energy = 0d;
        for (int i = 0; i < samples.Length; i++) energy += samples[i] * samples[i];
        return (float)Math.Sqrt(energy / samples.Length);
    }
}
