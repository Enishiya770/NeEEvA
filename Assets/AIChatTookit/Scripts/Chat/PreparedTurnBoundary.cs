using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>Recorder facts sampled on the Unity thread; this snapshot cannot close a turn.</summary>
public struct PreparedTurnBoundaryEvidence
{
    public int CaptureSerial, TextRevision, ActivityRevision, AudioMs;
    public string Transcript;
    public float SampledAt, LatestSoundAt, QuietSeconds, MinimumQuietSeconds, TailAge;
    public float MinimumConfidence;
    public bool Recording, AsrHealthy, FreshCoverage, CoversQuietTail, PendingText;
    public bool LevelActive, MelodyActive, TextActivityActive, TailAvailable, Singing;
    public bool SoundActive => LevelActive || MelodyActive || TextActivityActive;
}

/// <summary>
/// Conditional semantic preparation from the existing listening request. It is a lease on
/// exact recorder evidence, never a similarity match or a replacement for final ASR.
/// </summary>
public sealed class PreparedTurnBoundary
{
    public const float MaximumReadyAge = 2.5f;
    public const float MaximumRequestAge = 8f;
    public const float MaximumSnapshotAge = 0.25f;
    public const float MaximumTailAge = 1.25f;
    public const float MinimumPreparedConfidence = 0.78f;
    public readonly PreparedTurnBoundaryEvidence Source;
    public readonly int InputRevision;
    public readonly float RequestedAt, PreparedAt;
    public readonly TurnBoundaryDecision Decision;
    public readonly string RequestId;

    private PreparedTurnBoundary(PreparedTurnBoundaryEvidence source, int inputRevision,
        float requestedAt, float preparedAt, TurnBoundaryDecision decision, string requestId)
    {
        Source = source;
        InputRevision = inputRevision;
        RequestedAt = requestedAt;
        PreparedAt = preparedAt;
        Decision = decision;
        RequestId = requestId ?? "none";
    }

    public static bool TryCreate(string response, PreparedTurnBoundaryEvidence source,
        int inputRevision, float requestedAt, float preparedAt,
        out PreparedTurnBoundary candidate, out string reason, string requestId = null)
    {
        candidate = null;
        if (!source.Recording || source.CaptureSerial <= 0 ||
            string.IsNullOrWhiteSpace(source.Transcript))
            return Reject("missing-recording-evidence", out reason);
        if (!Finite(requestedAt) || !Finite(preparedAt) ||
            !Finite(source.SampledAt) || requestedAt < source.SampledAt ||
            requestedAt - source.SampledAt > MaximumSnapshotAge || preparedAt < requestedAt ||
            preparedAt - requestedAt > MaximumRequestAge)
            return Reject("request-evidence-expired", out reason);
        if (!source.AsrHealthy || source.AudioMs <= 0)
            return Reject("source-asr-unavailable", out reason);
        if (source.Singing || source.MelodyActive)
            return Reject("source-singing-or-melody", out reason);
        if (string.IsNullOrWhiteSpace(response)) return Reject("empty-response", out reason);
        try
        {
            int first = response.IndexOf('{'), last = response.LastIndexOf('}');
            if (first < 0 || last <= first) return Reject("invalid-json", out reason);
            JObject json = JObject.Parse(response.Substring(first, last - first + 1));
            JToken mode = json["observed_mode"], confidence = json["mode_confidence"];
            if (mode == null || mode.Type != JTokenType.String || (string)mode != "speech" ||
                confidence == null || (confidence.Type != JTokenType.Float &&
                    confidence.Type != JTokenType.Integer))
                return Reject("observed-mode-not-speech", out reason);
            float modeConfidence = (float)confidence;
            if (!Finite(modeConfidence) || modeConfidence < 0.8f || modeConfidence > 1f)
                return Reject("observed-mode-uncertain", out reason);
            JToken boundary = json["quiet_boundary"];
            TurnBoundaryDecision decision;
            if (!TryParseQuietBoundary(boundary, out decision, out reason)) return false;
            if (!IsPreparedClose(decision, source.MinimumConfidence, out reason)) return false;
            candidate = new PreparedTurnBoundary(source, inputRevision, requestedAt, preparedAt, decision, requestId);
            reason = "prepared";
            return true;
        }
        catch (JsonException) { return Reject("invalid-json", out reason); }
        catch (OverflowException) { return Reject("invalid-number", out reason); }
        catch (ArgumentException) { return Reject("invalid-json", out reason); }
    }

    // Diagnose the exact rejected field without logging raw private text. The existing
    // semantic parser remains authoritative after the schema checks, so this does not
    // broaden the accepted action/state combinations.
    private static bool TryParseQuietBoundary(JToken token, out TurnBoundaryDecision decision, out string reason)
    {
        decision = null;
        if (token == null) return Reject("quiet-boundary-missing", out reason);
        if (token.Type != JTokenType.Object) return Reject("quiet-boundary-not-object", out reason);
        foreach (string field in new[] { "action", "mode", "source", "turn_state", "reason", "confidence" })
        {
            JToken value = token[field];
            if (value == null) return Reject("quiet-boundary-missing-" + field, out reason);
            bool correctType = field == "confidence"
                ? value.Type == JTokenType.Float || value.Type == JTokenType.Integer
                : value.Type == JTokenType.String;
            if (!correctType) return Reject("quiet-boundary-type-" + field, out reason);
        }
        string action = (string)token["action"], mode = (string)token["mode"], owner = (string)token["source"], state = (string)token["turn_state"];
        if (!TurnBoundaryDecision.IsAction(action)) return Reject("quiet-boundary-enum-action", out reason);
        if (mode != "speech" && mode != "singing" && mode != "uncertain") return Reject("quiet-boundary-enum-mode", out reason);
        if (owner != "user" && owner != "background" && owner != "uncertain") return Reject("quiet-boundary-enum-source", out reason);
        if (state != "open" && state != "closed" && state != "uncertain") return Reject("quiet-boundary-enum-turn_state", out reason);
        float confidence = (float)token["confidence"];
        if (!Finite(confidence) || confidence < 0f || confidence > 1f) return Reject("quiet-boundary-range-confidence", out reason);
        if (!TurnBoundaryDecision.TryParse(token.ToString(Formatting.None), out decision))
            return Reject("quiet-boundary-action-state-conflict", out reason);
        reason = "quiet-boundary-valid";
        return true;
    }

    // This can be checked while the current continuous utterance is still active. A
    // resumption after a pause changes ActivityRevision and revokes the lease permanently.
    public bool MatchesLease(PreparedTurnBoundaryEvidence current, int inputRevision,
        float now, out string reason)
    {
        if (!Finite(now) || !Finite(current.SampledAt) || now < current.SampledAt ||
            now - current.SampledAt > MaximumSnapshotAge)
            return Reject("recorder-snapshot-stale", out reason);
        if (!current.Recording || current.CaptureSerial != Source.CaptureSerial ||
            inputRevision != InputRevision)
            return Reject("recording-changed", out reason);
        if (current.TextRevision != Source.TextRevision ||
            !string.Equals(current.Transcript, Source.Transcript, StringComparison.Ordinal))
            return Reject("exact-transcript-changed", out reason);
        if (current.ActivityRevision != Source.ActivityRevision ||
            (!Source.SoundActive && Source.LatestSoundAt >= 0f &&
                current.LatestSoundAt - Source.LatestSoundAt >= 0.12f))
            return Reject("renewed-sound", out reason);
        if (current.AudioMs < Source.AudioMs) return Reject("asr-coverage-rolled-back", out reason);
        if (now < PreparedAt || now - PreparedAt > MaximumReadyAge)
            return Reject("prepared-result-expired", out reason);
        if (now - RequestedAt > MaximumRequestAge) return Reject("request-expired", out reason);
        reason = "lease-current";
        return true;
    }

    public bool CanReuse(PreparedTurnBoundaryEvidence current, int inputRevision,
        float now, out string reason)
    {
        if (!MatchesLease(current, inputRevision, now, out reason)) return false;
        if (current.Singing || current.MelodyActive)
            return Reject("singing-or-melody", out reason);
        if (current.LevelActive || current.TextActivityActive)
            return Reject("current-sound-active", out reason);
        if (current.PendingText) return Reject("unconfirmed-asr-edit", out reason);
        if (!current.AsrHealthy || !current.FreshCoverage || !current.CoversQuietTail)
            return Reject("asr-quiet-coverage-insufficient", out reason);
        if (!current.TailAvailable || !Finite(current.TailAge) ||
            current.TailAge < 0f || current.TailAge > MaximumTailAge)
            return Reject("acoustic-tail-unavailable", out reason);
        if (!Finite(current.QuietSeconds) || !Finite(current.MinimumQuietSeconds) ||
            current.MinimumQuietSeconds <= 0f || current.QuietSeconds < current.MinimumQuietSeconds)
            return Reject("quiet-window-not-mature", out reason);
        if (!IsPreparedClose(Decision, current.MinimumConfidence, out reason)) return false;
        reason = "exact-covered-quiet";
        return true;
    }

    private static bool IsPreparedClose(TurnBoundaryDecision decision, float minimum, out string reason)
    {
        if (decision.mode != "speech" || decision.source != "user")
            return Reject("boundary-not-user-speech", out reason);
        if (decision.turn_state != "closed" ||
            (decision.action != "complete" && decision.action != "take_turn" && decision.action != "ask_user"))
            return Reject("boundary-still-open", out reason);
        if (!Finite(minimum) || !Finite(decision.confidence) || decision.confidence > 1f ||
            decision.confidence < Math.Max(MinimumPreparedConfidence, minimum))
            return Reject("boundary-confidence-insufficient", out reason);
        reason = "closed-user-speech";
        return true;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Reject(string value, out string reason) { reason = value; return false; }
}
