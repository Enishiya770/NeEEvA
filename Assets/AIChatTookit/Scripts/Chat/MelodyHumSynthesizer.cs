using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Local melody echo for the character.
///
/// The preferred path uses TD-PSOLA: a sustained character TTS hum is split into
/// pitch-synchronous grains and overlap-added at the requested melody pitch.  The
/// grains retain the voice's formants, breath and cycle-to-cycle variation.  A
/// small wavetable renderer remains only as a last-resort fallback.
/// </summary>
public static class MelodyHumSynthesizer
{
    private static AudioClip s_CachedCarrier;
    private static CarrierProfile s_CachedCarrierProfile;
    private static AudioClip s_FailedCarrier;

    private sealed class CarrierProfile
    {
        public float[] Samples;
        public readonly List<int> PitchMarks = new List<int>();
        public int PeriodSamples;
        public float SourceHz;
        public float Confidence;
    }

    public static AudioClip CreateHumClip(
        AudioClip carrier,
        float[] midiTimeline,
        float frameSeconds,
        float preferredMedianMidi,
        float maxDurationSeconds,
        float outputGain,
        out string diagnostic)
    {
        diagnostic = "";
        if (midiTimeline == null || midiTimeline.Length == 0)
        {
            diagnostic = "empty melody timeline";
            return null;
        }

        frameSeconds = Mathf.Clamp(frameSeconds, 0.02f, 0.25f);
        maxDurationSeconds = Mathf.Clamp(maxDurationSeconds, 0.5f, 30f);
        outputGain = Mathf.Clamp(outputGain, 0.03f, 0.8f);

        int first;
        int last;
        float melodyMedian;
        if (!TryFindMelodyBounds(midiTimeline, out first, out last, out melodyMedian))
        {
            diagnostic = "melody has too few voiced frames";
            return null;
        }

        // Preserve one timing frame around the phrase while dropping long VAD pre-roll/tail.
        first = Mathf.Max(0, first - 1);
        last = Mathf.Min(midiTimeline.Length - 1, last + 1);
        int maxFrames = Mathf.Max(1, Mathf.FloorToInt(maxDurationSeconds / frameSeconds));
        // “回哼最近一句”优先保留尾部；长演唱不要总是复述开头。
        if (last - first + 1 > maxFrames) first = last - maxFrames + 1;

        // TD-PSOLA copies source grains without resampling, so the output clock must match
        // the carrier clock exactly.  GPT-SoVITS normally returns 32 kHz, but custom clips
        // may use 44.1/48 kHz.
        int sampleRate = carrier != null ? Mathf.Max(8000, carrier.frequency) : 32000;
        CarrierProfile profile;
        bool hasVoiceProfile = TryAnalyzeCarrier(carrier, out profile);

        // Keep the melody in the character's comfortable octave.  When a real carrier is
        // available, its natural TTS pitch is a better anchor than a hard-coded note.
        float pitchAnchor = preferredMedianMidi;
        if (hasVoiceProfile)
        {
            float carrierMidi = HzToMidi(profile.SourceHz);
            pitchAnchor = Mathf.Clamp(carrierMidi, preferredMedianMidi - 5f, preferredMedianMidi + 5f);
        }
        float octaveShift = ChooseOctaveShift(melodyMedian, pitchAnchor);

        float[] output = null;
        if (hasVoiceProfile)
        {
            output = RenderVoicePreservingPsola(
                profile,
                midiTimeline,
                first,
                last,
                frameSeconds,
                octaveShift,
                sampleRate,
                outputGain);
        }

        if (output != null)
        {
            AudioClip clip = CreateClip(output, sampleRate);
            diagnostic =
                $"voice-preserving TD-PSOLA sourceF0={profile.SourceHz:F1}Hz " +
                $"confidence={profile.Confidence:F2}, grains={profile.PitchMarks.Count}, " +
                $"octaveShift={octaveShift:+0;-0;0}";
            return clip;
        }

        // The carrier may be missing or too short/noisy for pitch-synchronous analysis.
        // Keep the old renderer only so a requested action never fails silently.
        float[] wavetable;
        float carrierConfidence;
        bool characterCarrier = TryBuildCarrierWavetable(
            carrier, sampleRate, out wavetable, out carrierConfidence);
        if (!characterCarrier)
            wavetable = BuildFallbackWavetable(512);

        output = RenderWavetable(
            wavetable,
            characterCarrier,
            midiTimeline,
            first,
            last,
            frameSeconds,
            octaveShift,
            sampleRate,
            outputGain);
        if (output == null)
        {
            diagnostic = "fallback renderer failed";
            return null;
        }

        diagnostic = characterCarrier
            ? $"fallback character wavetable confidence={carrierConfidence:F2}, octaveShift={octaveShift:+0;-0;0}"
            : $"fallback synthetic wavetable, octaveShift={octaveShift:+0;-0;0}";
        return CreateClip(output, sampleRate);
    }

    private static bool TryFindMelodyBounds(
        float[] midiTimeline,
        out int first,
        out int last,
        out float median)
    {
        first = -1;
        last = -1;
        median = 0f;
        List<float> voiced = new List<float>();
        for (int i = 0; i < midiTimeline.Length; i++)
        {
            float midi = CleanMidi(midiTimeline[i]);
            if (midi <= 0f) continue;
            if (first < 0) first = i;
            last = i;
            voiced.Add(midi);
        }
        if (first < 0 || voiced.Count < 2) return false;
        voiced.Sort();
        median = voiced[voiced.Count / 2];
        return true;
    }

    private static float ChooseOctaveShift(float melodyMedian, float pitchAnchor)
    {
        float shift = 0f;
        while (melodyMedian + shift < pitchAnchor - 6f) shift += 12f;
        while (melodyMedian + shift > pitchAnchor + 6f) shift -= 12f;
        return shift;
    }

    /// <summary>
    /// Extracts a stable voiced region and a set of phase-aligned pitch marks from the
    /// complete carrier.  Unlike the former single-cycle extraction, every target pulse
    /// receives a different two-period grain from the original character recording.
    /// </summary>
    private static bool TryAnalyzeCarrier(AudioClip carrier, out CarrierProfile profile)
    {
        profile = null;
        if (carrier == null || carrier.samples < 1024 || carrier.channels < 1) return false;

        if (carrier == s_CachedCarrier && s_CachedCarrierProfile != null)
        {
            profile = s_CachedCarrierProfile;
            return true;
        }
        if (carrier == s_FailedCarrier) return false;

        float[] mono;
        if (!TryReadMono(carrier, out mono))
        {
            s_FailedCarrier = carrier;
            return false;
        }
        RemoveDc(mono);

        int sampleRate = Mathf.Max(1, carrier.frequency);
        int voicedStart;
        int voicedEnd;
        if (!TryFindVoicedRegion(mono, sampleRate, out voicedStart, out voicedEnd))
        {
            s_FailedCarrier = carrier;
            return false;
        }

        int period;
        int bestStart;
        int analysisWindow;
        float confidence;
        if (!TryEstimatePitch(
                mono,
                sampleRate,
                voicedStart,
                voicedEnd,
                out period,
                out bestStart,
                out analysisWindow,
                out confidence))
        {
            s_FailedCarrier = carrier;
            return false;
        }

        // A reliable human-like result needs several different grains.  If the carrier is
        // only a click or one/two cycles, falling back is safer than pretending it is a voice.
        List<int> marks = BuildPitchMarks(
            mono,
            voicedStart,
            voicedEnd,
            bestStart + analysisWindow / 2,
            period);
        if (marks.Count < 6)
        {
            s_FailedCarrier = carrier;
            return false;
        }

        profile = new CarrierProfile
        {
            Samples = mono,
            PeriodSamples = period,
            SourceHz = sampleRate / (float)period,
            Confidence = confidence
        };
        profile.PitchMarks.AddRange(marks);
        s_CachedCarrier = carrier;
        s_CachedCarrierProfile = profile;
        s_FailedCarrier = null;
        return true;
    }

    private static bool TryReadMono(AudioClip carrier, out float[] mono)
    {
        mono = null;
        float[] interleaved = new float[carrier.samples * carrier.channels];
        if (!carrier.GetData(interleaved, 0)) return false;

        mono = new float[carrier.samples];
        for (int i = 0; i < carrier.samples; i++)
        {
            float sum = 0f;
            int offset = i * carrier.channels;
            for (int channel = 0; channel < carrier.channels; channel++)
                sum += interleaved[offset + channel];
            mono[i] = sum / carrier.channels;
        }
        return true;
    }

    private static void RemoveDc(float[] samples)
    {
        if (samples == null || samples.Length == 0) return;
        double sum = 0.0;
        for (int i = 0; i < samples.Length; i++) sum += samples[i];
        float mean = (float)(sum / samples.Length);
        for (int i = 0; i < samples.Length; i++) samples[i] -= mean;
    }

    private static bool TryFindVoicedRegion(
        float[] samples,
        int sampleRate,
        out int voicedStart,
        out int voicedEnd)
    {
        voicedStart = 0;
        voicedEnd = 0;
        int frame = Mathf.Clamp(Mathf.RoundToInt(sampleRate * 0.025f), 128, 2048);
        int hop = Mathf.Max(32, frame / 2);
        if (samples.Length < frame * 2) return false;

        int frameCount = 1 + (samples.Length - frame) / hop;
        float[] rms = new float[frameCount];
        float maxRms = 0f;
        for (int f = 0; f < frameCount; f++)
        {
            int start = f * hop;
            double energy = 0.0;
            for (int i = 0; i < frame; i++)
            {
                float value = samples[start + i];
                energy += value * value;
            }
            rms[f] = Mathf.Sqrt((float)(energy / frame));
            maxRms = Mathf.Max(maxRms, rms[f]);
        }
        if (maxRms < 0.001f) return false;

        float threshold = Mathf.Max(0.0008f, maxRms * 0.16f);
        int bestFirst = -1;
        int bestLast = -1;
        int runFirst = -1;
        int quietFrames = 0;
        for (int f = 0; f < frameCount; f++)
        {
            bool active = rms[f] >= threshold;
            if (active)
            {
                if (runFirst < 0) runFirst = f;
                quietFrames = 0;
            }
            else if (runFirst >= 0)
            {
                quietFrames++;
                // Bridge one short dip inside a sustained nasal vowel.
                if (quietFrames <= 1) continue;
                int runLast = f - quietFrames;
                if (bestFirst < 0 || runLast - runFirst > bestLast - bestFirst)
                {
                    bestFirst = runFirst;
                    bestLast = runLast;
                }
                runFirst = -1;
                quietFrames = 0;
            }
        }
        if (runFirst >= 0)
        {
            int runLast = frameCount - 1 - quietFrames;
            if (bestFirst < 0 || runLast - runFirst > bestLast - bestFirst)
            {
                bestFirst = runFirst;
                bestLast = runLast;
            }
        }
        if (bestFirst < 0 || bestLast <= bestFirst) return false;

        voicedStart = Mathf.Clamp(bestFirst * hop, 0, samples.Length - 1);
        voicedEnd = Mathf.Clamp(bestLast * hop + frame, voicedStart + 1, samples.Length);

        // Discard TTS onset/release; their changing articulation makes long loops sound spoken.
        int trim = Mathf.Min(Mathf.RoundToInt(sampleRate * 0.035f), (voicedEnd - voicedStart) / 6);
        voicedStart += trim;
        voicedEnd -= trim;
        return voicedEnd - voicedStart >= Mathf.RoundToInt(sampleRate * 0.10f);
    }

    private static bool TryEstimatePitch(
        float[] samples,
        int sampleRate,
        int voicedStart,
        int voicedEnd,
        out int bestPeriod,
        out int bestStart,
        out int bestWindow,
        out float confidence)
    {
        bestPeriod = 0;
        bestStart = 0;
        bestWindow = 0;
        confidence = 0f;

        int minLag = Mathf.Max(8, sampleRate / 500);
        int maxLag = Mathf.Min(sampleRate / 75, (voicedEnd - voicedStart) / 4);
        int window = Mathf.Clamp(
            Mathf.RoundToInt(sampleRate * 0.075f),
            maxLag * 3,
            voicedEnd - voicedStart);
        if (maxLag <= minLag || window < maxLag * 2) return false;

        int available = voicedEnd - voicedStart - window;
        int step = Mathf.Max(1, window / 2);
        float bestScore = float.NegativeInfinity;
        int candidates = 0;

        for (int start = voicedStart; start <= voicedStart + available && candidates < 24; start += step, candidates++)
        {
            double mean = 0.0;
            for (int i = 0; i < window; i++) mean += samples[start + i];
            mean /= window;

            double energy = 0.0;
            for (int i = 0; i < window; i++)
            {
                double value = samples[start + i] - mean;
                energy += value * value;
            }
            if (energy / window < 1e-7) continue;

            for (int lag = minLag; lag <= maxLag; lag++)
            {
                int count = window - lag;
                double dot = 0.0;
                double leftEnergy = 0.0;
                double rightEnergy = 0.0;
                for (int i = 0; i < count; i++)
                {
                    double left = samples[start + i] - mean;
                    double right = samples[start + i + lag] - mean;
                    dot += left * right;
                    leftEnergy += left * left;
                    rightEnergy += right * right;
                }
                double denominator = Math.Sqrt(leftEnergy * rightEnergy);
                float correlation = denominator > 1e-12 ? (float)(dot / denominator) : 0f;

                // Nearly equal correlations commonly occur at 1x/2x/3x the real period.
                // A gentle long-lag penalty selects the fundamental-sized grain and avoids
                // octave-down errors without overruling genuinely better evidence.
                float lagPenalty = 0.025f * (lag - minLag) / Mathf.Max(1f, maxLag - minLag);
                float score = correlation - lagPenalty;
                if (score <= bestScore) continue;
                bestScore = score;
                confidence = correlation;
                bestPeriod = lag;
                bestStart = start;
                bestWindow = window;
            }
        }

        return bestPeriod >= 8 && confidence >= 0.46f;
    }

    private static List<int> BuildPitchMarks(
        float[] samples,
        int voicedStart,
        int voicedEnd,
        int seed,
        int period)
    {
        List<int> marks = new List<int>();
        int radius = Mathf.Max(2, Mathf.RoundToInt(period * 0.18f));
        seed = FindPositivePeak(samples, seed, radius, voicedStart, voicedEnd);

        List<int> backwards = new List<int>();
        int previous = seed;
        while (previous - period - radius > voicedStart + period)
        {
            int mark = FindPositivePeak(
                samples,
                previous - period,
                radius,
                voicedStart + period,
                voicedEnd - period);
            if (previous - mark < period / 2) break;
            backwards.Add(mark);
            previous = mark;
        }
        for (int i = backwards.Count - 1; i >= 0; i--) marks.Add(backwards[i]);

        if (seed >= voicedStart + period && seed < voicedEnd - period) marks.Add(seed);
        previous = seed;
        while (previous + period + radius < voicedEnd - period)
        {
            int mark = FindPositivePeak(
                samples,
                previous + period,
                radius,
                voicedStart + period,
                voicedEnd - period);
            if (mark - previous < period / 2) break;
            marks.Add(mark);
            previous = mark;
        }
        return marks;
    }

    private static int FindPositivePeak(
        float[] samples,
        int expected,
        int radius,
        int minimum,
        int maximum)
    {
        int begin = Mathf.Clamp(expected - radius, minimum, maximum - 1);
        int end = Mathf.Clamp(expected + radius, begin + 1, maximum);
        int best = begin;
        float bestValue = samples[begin];
        for (int i = begin + 1; i < end; i++)
        {
            if (samples[i] <= bestValue) continue;
            bestValue = samples[i];
            best = i;
        }
        return best;
    }

    private static float[] RenderVoicePreservingPsola(
        CarrierProfile profile,
        float[] midiTimeline,
        int first,
        int last,
        float frameSeconds,
        float octaveShift,
        int sampleRate,
        float outputGain)
    {
        if (profile == null || profile.Samples == null || profile.PitchMarks.Count < 6)
            return null;

        int frameCount = last - first + 1;
        int outputSamples = Mathf.Max(1, Mathf.CeilToInt(frameCount * frameSeconds * sampleRate));
        float[] accumulation = new float[outputSamples];
        float[] weights = new float[outputSamples];
        float[] localMidi = BuildLocalMidi(midiTimeline, first, last, octaveShift);
        int grainIndex = 0;

        int frame = 0;
        while (frame < frameCount)
        {
            while (frame < frameCount && localMidi[frame] <= 0f) frame++;
            if (frame >= frameCount) break;
            int segmentFirstFrame = frame;
            while (frame < frameCount && localMidi[frame] > 0f) frame++;
            int segmentLastFrame = frame - 1;

            double targetSample = segmentFirstFrame * frameSeconds * sampleRate;
            double segmentEnd = (segmentLastFrame + 1) * frameSeconds * sampleRate;
            int safety = 0;
            while (targetSample < segmentEnd && safety++ < outputSamples)
            {
                float localTime = (float)(targetSample / sampleRate);
                float midi = SampleMidi(localMidi, localTime / frameSeconds);
                if (midi <= 0f) midi = localMidi[segmentFirstFrame];
                float hz = Mathf.Clamp(MidiToHz(midi), 70f, 800f);
                int destinationCenter = Mathf.RoundToInt((float)targetSample);
                int sourceCenter = profile.PitchMarks[PingPongIndex(grainIndex, profile.PitchMarks.Count)];
                AddPsolaGrain(
                    profile.Samples,
                    sourceCenter,
                    profile.PeriodSamples,
                    accumulation,
                    weights,
                    destinationCenter);
                grainIndex++;
                targetSample += sampleRate / (double)hz;
            }

            ApplyPhraseEnvelope(
                accumulation,
                weights,
                Mathf.RoundToInt(segmentFirstFrame * frameSeconds * sampleRate),
                Mathf.Min(outputSamples, Mathf.RoundToInt((segmentLastFrame + 1) * frameSeconds * sampleRate)),
                sampleRate);
        }

        float peak = 0f;
        for (int i = 0; i < outputSamples; i++)
        {
            if (weights[i] > 1e-5f) accumulation[i] /= weights[i];
            else accumulation[i] = 0f;
            peak = Mathf.Max(peak, Mathf.Abs(accumulation[i]));
        }
        if (peak < 1e-5f) return null;

        // Match the old inspector control: outputGain is the requested peak, not a multiplier
        // applied to whatever arbitrary level the TTS backend happened to return.
        float scale = outputGain / peak;
        for (int i = 0; i < outputSamples; i++) accumulation[i] *= scale;
        ApplyEdgeFade(accumulation, sampleRate, 0.035f);
        return accumulation;
    }

    private static float[] BuildLocalMidi(
        float[] source,
        int first,
        int last,
        float octaveShift)
    {
        int count = last - first + 1;
        float[] result = new float[count];
        for (int i = 0; i < count; i++)
        {
            float midi = CleanMidi(source[first + i]);
            result[i] = midi > 0f ? midi + octaveShift : 0f;
        }

        // Pitch trackers occasionally drop one isolated frame inside a held note.  Treating
        // that as a breath creates machine-gun gaps, so bridge only a single-frame dropout.
        for (int i = 1; i < count - 1; i++)
        {
            if (result[i] > 0f || result[i - 1] <= 0f || result[i + 1] <= 0f) continue;
            if (Mathf.Abs(result[i - 1] - result[i + 1]) <= 5f)
                result[i] = (result[i - 1] + result[i + 1]) * 0.5f;
        }
        return result;
    }

    private static float SampleMidi(float[] midi, float framePosition)
    {
        if (midi == null || midi.Length == 0) return 0f;
        int left = Mathf.Clamp(Mathf.FloorToInt(framePosition), 0, midi.Length - 1);
        int right = Mathf.Min(left + 1, midi.Length - 1);
        float current = midi[left];
        float next = midi[right];
        if (current <= 0f) return next;
        if (next <= 0f) return current;
        return Mathf.Lerp(current, next, Mathf.Clamp01(framePosition - left));
    }

    private static int PingPongIndex(int index, int count)
    {
        if (count <= 1) return 0;
        int span = (count - 1) * 2;
        int position = index % span;
        return position < count ? position : span - position;
    }

    private static void AddPsolaGrain(
        float[] source,
        int sourceCenter,
        int sourcePeriod,
        float[] destination,
        float[] weights,
        int destinationCenter)
    {
        int half = Mathf.Max(16, sourcePeriod);
        for (int offset = -half; offset <= half; offset++)
        {
            int sourceIndex = sourceCenter + offset;
            int destinationIndex = destinationCenter + offset;
            if (sourceIndex < 0 || sourceIndex >= source.Length ||
                destinationIndex < 0 || destinationIndex >= destination.Length)
                continue;

            float phase = (offset + half) / (float)(half * 2);
            float window = 0.5f - 0.5f * Mathf.Cos(phase * Mathf.PI * 2f);
            destination[destinationIndex] += source[sourceIndex] * window;
            weights[destinationIndex] += window;
        }
    }

    private static void ApplyPhraseEnvelope(
        float[] accumulation,
        float[] weights,
        int begin,
        int end,
        int sampleRate)
    {
        begin = Mathf.Clamp(begin, 0, accumulation.Length);
        end = Mathf.Clamp(end, begin, accumulation.Length);
        int length = end - begin;
        if (length <= 0) return;

        int attack = Mathf.Min(length / 2, Mathf.RoundToInt(sampleRate * 0.065f));
        int release = Mathf.Min(length / 2, Mathf.RoundToInt(sampleRate * 0.095f));
        for (int i = 0; i < length; i++)
        {
            float envelope = 1f;
            if (attack > 0 && i < attack)
                envelope = Mathf.Min(envelope, SmoothStep(i / (float)attack));
            int remaining = length - 1 - i;
            if (release > 0 && remaining < release)
                envelope = Mathf.Min(envelope, SmoothStep(remaining / (float)release));
            accumulation[begin + i] *= envelope;
            // Keep normalization independent from the musical envelope.
        }
    }

    private static float SmoothStep(float value)
    {
        value = Mathf.Clamp01(value);
        return value * value * (3f - 2f * value);
    }

    private static float[] RenderWavetable(
        float[] wavetable,
        bool characterCarrier,
        float[] midiTimeline,
        int first,
        int last,
        float frameSeconds,
        float octaveShift,
        int sampleRate,
        float outputGain)
    {
        int frameCount = last - first + 1;
        int outputSamples = Mathf.Max(1, Mathf.CeilToInt(frameCount * frameSeconds * sampleRate));
        float[] output = new float[outputSamples];
        double phase = 0.0;
        const float attackSeconds = 0.035f;
        const float releaseSeconds = 0.055f;

        for (int sample = 0; sample < outputSamples; sample++)
        {
            float localTime = sample / (float)sampleRate;
            float framePosition = localTime / frameSeconds;
            int localFrame = Mathf.Clamp(Mathf.FloorToInt(framePosition), 0, frameCount - 1);
            float fraction = Mathf.Clamp01(framePosition - localFrame);
            int sourceFrame = first + localFrame;
            float midi = CleanMidi(midiTimeline[sourceFrame]);
            if (midi <= 0f) continue;

            float nextMidi = sourceFrame < last ? CleanMidi(midiTimeline[sourceFrame + 1]) : midi;
            if (nextMidi > 0f) midi = Mathf.Lerp(midi, nextMidi, fraction);
            midi += octaveShift;

            float hz = MidiToHz(midi);
            phase += hz / sampleRate;
            phase -= Math.Floor(phase);
            float wave = SampleLoop(wavetable, (float)phase);

            float envelope = 1f;
            bool previousRest = sourceFrame <= first || CleanMidi(midiTimeline[sourceFrame - 1]) <= 0f;
            bool nextRest = sourceFrame >= last || nextMidi <= 0f;
            float timeIntoFrame = fraction * frameSeconds;
            float timeUntilFrameEnd = (1f - fraction) * frameSeconds;
            if (previousRest) envelope = Mathf.Min(envelope, timeIntoFrame / attackSeconds);
            if (nextRest) envelope = Mathf.Min(envelope, timeUntilFrameEnd / releaseSeconds);

            if (!characterCarrier)
                wave = 0.90f * wave + 0.10f * Mathf.Sin((float)(phase * Math.PI * 4.0));
            output[sample] = wave * Mathf.Clamp01(envelope) * outputGain;
        }

        ApplyEdgeFade(output, sampleRate, 0.025f);
        return output;
    }

    private static AudioClip CreateClip(float[] output, int sampleRate)
    {
        AudioClip clip = AudioClip.Create(
            "NeEEvA_HumBack", output.Length, 1, sampleRate, false);
        clip.SetData(output, 0);
        return clip;
    }

    private static float CleanMidi(float value)
    {
        return value > 1f && !float.IsNaN(value) && !float.IsInfinity(value) ? value : 0f;
    }

    private static float MidiToHz(float midi)
    {
        return 440f * Mathf.Pow(2f, (midi - 69f) / 12f);
    }

    private static float HzToMidi(float hz)
    {
        return hz > 0f ? 69f + 12f * Mathf.Log(hz / 440f, 2f) : 69f;
    }

    private static bool TryBuildCarrierWavetable(
        AudioClip carrier,
        int targetSampleRate,
        out float[] wavetable,
        out float confidence)
    {
        wavetable = null;
        confidence = 0f;
        if (carrier == null || carrier.samples < 512 || carrier.channels < 1) return false;

        float[] mono;
        if (!TryReadMono(carrier, out mono)) return false;
        int sourceRate = Mathf.Max(1, carrier.frequency);
        int minLag = Mathf.Max(8, sourceRate / 500);
        int maxLag = Mathf.Min(sourceRate / 75, mono.Length / 4);
        int window = Mathf.Clamp(Mathf.RoundToInt(sourceRate * 0.09f), maxLag * 3, mono.Length);
        if (maxLag <= minLag || window < maxLag * 2) return false;

        int searchBegin = Mathf.Clamp(Mathf.RoundToInt(mono.Length * 0.08f), 0, mono.Length - window);
        int searchEnd = Mathf.Max(searchBegin, mono.Length - window - searchBegin);
        int step = Mathf.Max(1, window / 2);
        int bestStart = -1;
        int bestLag = 0;
        float bestCorrelation = 0f;
        int candidates = 0;

        for (int start = searchBegin; start <= searchEnd && candidates < 18; start += step, candidates++)
        {
            double mean = 0.0;
            for (int i = 0; i < window; i++) mean += mono[start + i];
            mean /= window;
            double rms = 0.0;
            for (int i = 0; i < window; i++)
            {
                double value = mono[start + i] - mean;
                rms += value * value;
            }
            if (rms / window < 1e-6) continue;

            for (int lag = minLag; lag <= maxLag; lag++)
            {
                int count = window - lag;
                double dot = 0.0;
                double leftEnergy = 0.0;
                double rightEnergy = 0.0;
                for (int i = 0; i < count; i++)
                {
                    double left = mono[start + i] - mean;
                    double right = mono[start + i + lag] - mean;
                    dot += left * right;
                    leftEnergy += left * left;
                    rightEnergy += right * right;
                }
                double denominator = Math.Sqrt(leftEnergy * rightEnergy);
                float correlation = denominator > 1e-12 ? (float)(dot / denominator) : 0f;
                if (correlation <= bestCorrelation) continue;
                bestCorrelation = correlation;
                bestStart = start;
                bestLag = lag;
            }
        }

        confidence = bestCorrelation;
        if (bestStart < 0 || bestLag < 8 || bestCorrelation < 0.42f) return false;

        int cycleStart = bestStart;
        float strongestSlope = 0f;
        int cycleSearchEnd = Mathf.Min(bestStart + window - bestLag - 1, mono.Length - bestLag - 1);
        for (int i = bestStart + 1; i < cycleSearchEnd; i++)
        {
            if (mono[i - 1] > 0f || mono[i] < 0f) continue;
            float slope = mono[i] - mono[i - 1];
            if (slope <= strongestSlope) continue;
            strongestSlope = slope;
            cycleStart = i;
        }
        if (cycleStart + bestLag >= mono.Length) return false;

        float[] sourceCycle = new float[bestLag];
        float meanCycle = 0f;
        for (int i = 0; i < bestLag; i++) meanCycle += mono[cycleStart + i];
        meanCycle /= bestLag;
        float peak = 0f;
        for (int i = 0; i < bestLag; i++)
        {
            sourceCycle[i] = mono[cycleStart + i] - meanCycle;
            peak = Mathf.Max(peak, Mathf.Abs(sourceCycle[i]));
        }
        if (peak < 1e-4f) return false;

        int tableSize = Mathf.Clamp(
            Mathf.RoundToInt(bestLag * targetSampleRate / (float)sourceRate),
            32,
            2048);
        wavetable = new float[tableSize];
        for (int i = 0; i < tableSize; i++)
        {
            float position = i * bestLag / (float)tableSize;
            int left = Mathf.FloorToInt(position) % bestLag;
            int right = (left + 1) % bestLag;
            wavetable[i] = Mathf.Lerp(
                sourceCycle[left],
                sourceCycle[right],
                position - Mathf.Floor(position)) / peak;
        }
        float seam = wavetable[wavetable.Length - 1] - wavetable[0];
        for (int i = 0; i < wavetable.Length; i++)
            wavetable[i] -= seam * i / Mathf.Max(1f, wavetable.Length - 1f);
        return true;
    }

    private static float[] BuildFallbackWavetable(int size)
    {
        float[] table = new float[Mathf.Max(64, size)];
        for (int i = 0; i < table.Length; i++)
        {
            float phase = i / (float)table.Length * Mathf.PI * 2f;
            table[i] = 0.78f * Mathf.Sin(phase) +
                       0.16f * Mathf.Sin(phase * 2f) +
                       0.06f * Mathf.Sin(phase * 3f);
        }
        return table;
    }

    private static float SampleLoop(float[] table, float phase)
    {
        if (table == null || table.Length == 0) return 0f;
        float position = Mathf.Repeat(phase, 1f) * table.Length;
        int left = Mathf.FloorToInt(position) % table.Length;
        int right = (left + 1) % table.Length;
        return Mathf.Lerp(table[left], table[right], position - Mathf.Floor(position));
    }

    private static void ApplyEdgeFade(float[] samples, int sampleRate, float seconds)
    {
        if (samples == null || samples.Length == 0) return;
        int fade = Mathf.Min(
            samples.Length / 2,
            Mathf.Max(1, Mathf.RoundToInt(sampleRate * seconds)));
        for (int i = 0; i < fade; i++)
        {
            float gain = SmoothStep(i / (float)fade);
            samples[i] *= gain;
            samples[samples.Length - 1 - i] *= gain;
        }
    }
}
