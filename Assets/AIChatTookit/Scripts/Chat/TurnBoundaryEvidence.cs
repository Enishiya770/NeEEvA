using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Only the wire format is constrained; the role chooses the conversational action.</summary>
[Serializable]
public sealed class TurnBoundaryDecision
{
    public string action;
    public float confidence = -1f;
    public string mode;
    public string source;
    public string turn_state;
    public string reason;

    public static bool TryParse(string text, out TurnBoundaryDecision decision)
    {
        decision = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        int first = text.IndexOf('{');
        int last = text.LastIndexOf('}');
        if (first < 0 || last <= first) return false;
        try
        {
            JObject json = JObject.Parse(text.Substring(first, last - first + 1));
            foreach (string field in new[] { "action", "mode", "source", "turn_state", "reason" })
                if (json[field] == null || json[field].Type != JTokenType.String) return false;
            JToken confidence = json["confidence"];
            if (confidence == null ||
                (confidence.Type != JTokenType.Float && confidence.Type != JTokenType.Integer))
                return false;
            var value = new TurnBoundaryDecision
            {
                action = (string)json["action"], confidence = (float)confidence,
                mode = (string)json["mode"], source = (string)json["source"],
                turn_state = (string)json["turn_state"],
                reason = (string)json["reason"],
            };
            if (value == null || !IsAction(value.action) ||
                float.IsNaN(value.confidence) || float.IsInfinity(value.confidence) ||
                value.confidence < 0f || value.confidence > 1f ||
                (value.mode != "speech" && value.mode != "singing" && value.mode != "uncertain") ||
                (value.source != "user" && value.source != "background" && value.source != "uncertain") ||
                (value.turn_state != "open" && value.turn_state != "closed" &&
                 value.turn_state != "uncertain") ||
                (value.action == "take_turn" && value.turn_state == "open") ||
                (value.action == "complete" && value.turn_state != "closed") ||
                (value.action == "ask_user" && value.turn_state == "open"))
                return false;
            value.reason = value.reason ?? "";
            decision = value;
            return true;
        }
        catch (JsonException) { return false; }
        catch (OverflowException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public static bool IsAction(string action)
    {
        return action == "continue" || action == "take_turn" || action == "interrupt_user" ||
               action == "ask_user" || action == "complete";
    }

    // Explicit conversational choices are not certainty claims. Only legacy complete
    // retains its evidence-confidence gate; asking must remain available under uncertainty.
    public static bool CanClose(string action, float confidence, float completeMinimum)
    {
        if (float.IsNaN(confidence) || float.IsInfinity(confidence) ||
            confidence < 0f || confidence > 1f) return false;
        return action == "take_turn" || action == "ask_user" || action == "interrupt_user" ||
               (action == "complete" && confidence >= completeMinimum);
    }
}

/// <summary>
/// Rolling digital-level evidence, not an endpoint detector. No transcript, LLM or
/// recorder side effects. A held drop survives above the ambient floor; renewed sound
/// invalidates it. dB is relative to PCM amplitude, not calibrated physical dB SPL.
/// </summary>
public sealed class TurnBoundaryAcoustics
{
    private struct Sample { public float Time, Rms; }
    private readonly List<Sample> samples = new List<Sample>();
    private float nextSample;
    public float DropSince { get; private set; } = -1f;
    public float QuietSince { get; private set; } = -1f;
    public float ReferenceRms { get; private set; }
    public float RecentRms { get; private set; }
    public float DropDb { get; private set; }
    public int ActivityRevision { get; private set; }

    public void Reset()
    {
        samples.Clear();
        nextSample = 0f;
        DropSince = QuietSince = -1f;
        ReferenceRms = RecentRms = DropDb = 0f;
        ActivityRevision = 0;
    }

    public static float RelativeDropDb(float before, float after)
    {
        return 20f * Mathf.Log10(Mathf.Max(0.000001f, before) /
                               Mathf.Max(0.000001f, after));
    }

    public void Observe(float rms, float now, float quietThreshold, float dropThresholdDb)
    {
        if (now < nextSample || float.IsNaN(rms) || float.IsInfinity(rms)) return;
        nextSample = now + 0.05f;
        rms = Mathf.Max(0f, rms);
        samples.Add(new Sample { Time = now, Rms = rms });
        samples.RemoveAll(s => s.Time < now - 1.8f);
        if (rms <= quietThreshold)
        {
            if (QuietSince < 0f) QuietSince = now;
        }
        else if (QuietSince >= 0f)
        {
            if (now - QuietSince >= 0.25f) ActivityRevision++;
            QuietSince = -1f;
        }

        var before = new List<float>();
        var recent = new List<float>();
        foreach (Sample sample in samples)
        {
            if (sample.Time >= now - 0.25f) recent.Add(sample.Rms);
            else if (sample.Time <= now - 0.40f) before.Add(sample.Rms);
        }
        if (recent.Count < 3) return;
        RecentRms = Quantile(recent, 0.5f);
        if (DropSince < 0f)
        {
            if (before.Count < 8) return;
            ReferenceRms = Quantile(before, 0.65f);
        }
        DropDb = RelativeDropDb(ReferenceRms, RecentRms);
        if (DropSince >= 0f && DropDb < dropThresholdDb * 0.5f)
        {
            if (now - DropSince >= 0.25f) ActivityRevision++;
            DropSince = -1f;
        }
        else if (DropSince < 0f && DropDb >= dropThresholdDb &&
                 ReferenceRms > quietThreshold)
            DropSince = now;
    }

    private static float Quantile(List<float> values, float fraction)
    {
        values.Sort();
        return values[Mathf.Clamp(Mathf.RoundToInt((values.Count - 1) * fraction), 0, values.Count - 1)];
    }
}

/// <summary>
/// One recorder-side activity clock. Low energy is evidence, not an audio filter.
/// ASR advances and recent, above-noise periodic sound can protect quiet speech/humming.
/// A single level spike or an old singing classification cannot restart this clock.
/// </summary>
public sealed class TurnActivityEvidence
{
    private struct Level { public float Time, Rms; }
    private readonly List<Level> levels = new List<Level>();
    private float nextSample, highSince = -1f, quietSince = -1f;
    private float melodyReceivedAt = -999f, melodyRms;
    private bool melodySupported;
    private float textActivityAt = -999f;
    private string acceptedText = "", candidateText = "";
    private int candidateAudioMs = -1, lastAsrAudioMs = -1;
    private float lastAsrAudioEndTime = -1f;
    public int Revision { get; private set; }
    public int ActivityRevision { get; private set; }
    public float RecentRms { get; private set; }
    public float LastTextAt { get; private set; } = -1f;
    public int LastTextAudioMs { get; private set; }
    public float LastAsrAt { get; private set; } = -1f;
    public bool LevelActive { get; private set; }
    public bool MelodyActive { get; private set; }
    public bool TextActivityActive { get; private set; }
    public bool PendingText { get; private set; }
    public bool TailAvailable { get; private set; }
    public float TailAge(float now) => Mathf.Max(0f, now - melodyReceivedAt);
    public float QuietSince => quietSince;
    public float LatestSoundAt => quietSince;
    public float QuietSeconds(float now) => quietSince < 0f ? 0f : Mathf.Max(0f, now - quietSince);
    public bool AsrHealthy(float now) => LastAsrAt >= 0f && now - LastAsrAt <= 2.5f;
    // A correction of OLD audio must not demand another packet of the same quiet.
    // The timestamp proves coverage only; PendingText still protects changed semantics.
    public bool CoversQuietTail => lastAsrAudioMs >= LastTextAudioMs + 250 ||
        (quietSince >= 0f && lastAsrAudioEndTime >= quietSince + 0.25f);
    public bool CoversRecentAudio(int capturedAudioMs, int maximumLagMs) =>
        lastAsrAudioMs >= 0 && capturedAudioMs - lastAsrAudioMs <= Math.Max(0, maximumLagMs);

    public void Reset(float now)
    {
        levels.Clear(); nextSample = now; highSince = -1f; quietSince = now;
        melodyReceivedAt = -999f; melodyRms = 0f; melodySupported = false;
        acceptedText = candidateText = ""; candidateAudioMs = lastAsrAudioMs = -1;
        lastAsrAudioEndTime = -1f;
        Revision = ActivityRevision = 0; textActivityAt = -999f;
        RecentRms = 0f; LastTextAt = LastAsrAt = -1f; LastTextAudioMs = 0;
        LevelActive = MelodyActive = TextActivityActive = PendingText = TailAvailable = false;
    }

    private void MarkActivity(float now)
    {
        if (quietSince >= now) return; // late evidence cannot move a sound into the future/past
        // Count meaningful resumptions, not every sample of one continuing vowel.
        if (quietSince >= 0f && now - quietSince >= 0.12f) { Revision++; ActivityRevision++; }
        quietSince = now;
    }

    public void ObserveLevel(float rms, float now, float quietReference, float noiseUpper)
    {
        if (now < nextSample || float.IsNaN(rms) || float.IsInfinity(rms)) return;
        nextSample = now + 0.05f;
        levels.Add(new Level { Time = now, Rms = Mathf.Max(0f, rms) });
        levels.RemoveAll(s => s.Time < now - 0.20f);
        var values = new List<float>();
        foreach (Level value in levels) values.Add(value.Rms);
        values.Sort(); RecentRms = values[values.Count / 2];
        if (RecentRms > quietReference)
        {
            if (highSince < 0f) highSince = now;
        }
        else highSince = -1f;
        LevelActive = highSince >= 0f && now - highSince >= 0.15f;
        MelodyActive = TailAvailable && melodySupported && TailAge(now) <= 1.25f &&
            RecentRms >= Mathf.Max(noiseUpper * 1.2f, melodyRms * 0.5f);
        TextActivityActive = now - textActivityAt <= 1.1f && RecentRms > noiseUpper * 1.05f;
        if (LevelActive || MelodyActive) MarkActivity(now);
        // Never extend a VAD timestamp on each Unity frame or on each ASR revision.
        if (TextActivityActive) MarkActivity(textActivityAt);
    }

    public bool ObserveAsr(string textKey, string stableKey, int audioMs, float now,
        bool tailAvailable, float tailRms, float tailPeriodicity, float tailVoicedRatio,
        float noiseUpper, bool tailVadAvailable = false, int tailSpeechMs = 0,
        int tailSpeechEndAgeMs = -1, float audioEndTime = -1f)
    {
        if (audioMs <= lastAsrAudioMs) return false; // repeated packets are not progress
        lastAsrAudioMs = audioMs; LastAsrAt = now;
        lastAsrAudioEndTime = audioEndTime >= 0f && audioEndTime <= now ? audioEndTime : -1f;
        TailAvailable = tailAvailable;
        melodyReceivedAt = now; melodyRms = tailRms;
        // Periodicity is not speaker identity. Require energy above measured noise too.
        melodySupported = tailAvailable && tailRms >= Mathf.Max(0.001f, noiseUpper * 1.35f) &&
            tailPeriodicity >= 0.70f && tailVoicedRatio >= 0.60f;
        if (tailAvailable && tailVadAvailable && tailSpeechMs >= 120 &&
            tailSpeechEndAgeMs >= 0 && audioEndTime >= 0f && audioEndTime <= now &&
            now - audioEndTime <= 2f && tailRms > noiseUpper * 1.15f &&
            RecentRms > noiseUpper * 1.05f)
        {
            textActivityAt = Mathf.Max(textActivityAt, audioEndTime - tailSpeechEndAgeMs / 1000f);
            MarkActivity(textActivityAt);
            TextActivityActive = now - textActivityAt <= 1.1f;
        }
        textKey = textKey ?? ""; stableKey = stableKey ?? "";
        if (textKey.Length == 0 || textKey == acceptedText)
        {
            PendingText = false; candidateText = ""; return false;
        }
        // A rollback alone does not mean a new word was spoken.
        if (acceptedText.StartsWith(textKey, StringComparison.Ordinal))
        {
            PendingText = false; candidateText = ""; return false;
        }
        bool repeated = candidateText == textKey && audioMs > candidateAudioMs;
        bool stableGrowth = stableKey.Length > acceptedText.Length &&
            stableKey.StartsWith(acceptedText, StringComparison.Ordinal) &&
            textKey.StartsWith(stableKey, StringComparison.Ordinal);
        bool accept = acceptedText.Length == 0 || LevelActive || MelodyActive || repeated || stableGrowth;
        candidateText = textKey; candidateAudioMs = audioMs; PendingText = !accept;
        if (!accept) return false;
        acceptedText = textKey; candidateText = "";
        LastTextAt = now; LastTextAudioMs = audioMs;
        Revision++; // even one genuine new character invalidates an older decision
        // Text changes only invalidate semantics. Soft speech is supported by the
        // independent tail VAD above, including when ASR has not produced new words.
        return true;
    }

    // A full-clip result may corroborate a tentative partial without waiting for a
    // second identical partial. Never turn disagreement or newer sound into agreement.
    public bool ConfirmPendingText(string fullTextKey, string latestTextKey, float now)
    {
        if (!PendingText || string.IsNullOrEmpty(fullTextKey) ||
            fullTextKey != candidateText || fullTextKey != latestTextKey) return false;
        acceptedText = candidateText;
        candidateText = "";
        PendingText = false;
        LastTextAt = now;
        LastTextAudioMs = candidateAudioMs;
        Revision++;
        return true;
    }
}
