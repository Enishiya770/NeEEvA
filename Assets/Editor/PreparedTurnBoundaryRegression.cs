using System;
using Newtonsoft.Json.Linq;

/// <summary>Exercises the production lease and semantic parser; no microphone/model side effects.</summary>
public static class PreparedTurnBoundaryRegression
{
    private static int checks;
    public static int RunOrThrow()
    {
        checks = 0;
        PreparedTurnBoundaryEvidence source = Source();
        PreparedTurnBoundary candidate = Prepare(source);
        PreparedTurnBoundaryEvidence current = Mature(source);
        ExpectReuse(candidate, current, 7, 12f, true, "exact-covered-quiet");

        // A request begun in the continuous utterance may finish after its quiet tail.
        source.LevelActive = true;
        candidate = Prepare(source);
        current = Mature(source);
        current.LatestSoundAt = 10.8f;
        ExpectReuse(candidate, current, 7, 12f, true, "exact-covered-quiet");
        // The same movement of sound time after an already quiet source is not reusable.
        source.LevelActive = false;
        candidate = Prepare(source);
        ExpectReuse(candidate, current, 7, 12f, false, "renewed-sound");
        current = Mature(source);

        foreach (string corrected in new[] {
            "请把小灯打开，不，别打开。", "请把小灯关掉。", "请把小灯打开？", "请把小灯打开。 ", "", "请把小灯打开。然后"
        })
        {
            var edited = current; edited.Transcript = corrected;
            ExpectReuse(candidate, edited, 7, 12f, false, "exact-transcript-changed");
        }
        var changed = current; changed.TextRevision += 2; // edited away, then back to original
        ExpectReuse(candidate, changed, 7, 12f, false, "exact-transcript-changed");
        changed = current; changed.ActivityRevision++;
        ExpectReuse(candidate, changed, 7, 12f, false, "renewed-sound");
        changed = current; changed.Recording = false;
        ExpectReuse(candidate, changed, 7, 12f, false, "recording-changed");
        changed = current; changed.CaptureSerial++;
        ExpectReuse(candidate, changed, 7, 12f, false, "recording-changed");
        ExpectReuse(candidate, current, 8, 12f, false, "recording-changed");
        changed = current; changed.AudioMs = source.AudioMs - 1;
        ExpectReuse(candidate, changed, 7, 12f, false, "asr-coverage-rolled-back");
        changed = current; changed.Singing = true;
        ExpectReuse(candidate, changed, 7, 12f, false, "singing-or-melody");
        changed = current; changed.MelodyActive = true;
        ExpectReuse(candidate, changed, 7, 12f, false, "singing-or-melody");
        changed = current; changed.LevelActive = true;
        ExpectReuse(candidate, changed, 7, 12f, false, "current-sound-active");
        changed = current; changed.TextActivityActive = true;
        ExpectReuse(candidate, changed, 7, 12f, false, "current-sound-active");
        changed = current; changed.PendingText = true;
        ExpectReuse(candidate, changed, 7, 12f, false, "unconfirmed-asr-edit");
        changed = current; changed.AsrHealthy = false;
        ExpectReuse(candidate, changed, 7, 12f, false, "asr-quiet-coverage-insufficient");
        changed = current; changed.FreshCoverage = false;
        ExpectReuse(candidate, changed, 7, 12f, false, "asr-quiet-coverage-insufficient");
        changed = current; changed.CoversQuietTail = false;
        ExpectReuse(candidate, changed, 7, 12f, false, "asr-quiet-coverage-insufficient");
        changed = current; changed.TailAvailable = false;
        ExpectReuse(candidate, changed, 7, 12f, false, "acoustic-tail-unavailable");
        foreach (float age in new[] { -1f, 1.26f, float.NaN, float.PositiveInfinity })
        {
            changed = current; changed.TailAge = age;
            ExpectReuse(candidate, changed, 7, 12f, false, "acoustic-tail-unavailable");
        }
        foreach (float quiet in new[] { 0f, 0.34f, float.NaN })
        {
            changed = current; changed.QuietSeconds = quiet;
            ExpectReuse(candidate, changed, 7, 12f, false, "quiet-window-not-mature");
        }
        changed = current; changed.MinimumConfidence = 0.99f;
        ExpectReuse(candidate, changed, 7, 12f, false, "boundary-confidence-insufficient");
        ExpectReuse(candidate, current, 7, 12.26f, false, "recorder-snapshot-stale");
        changed = current; changed.SampledAt = 14f;
        ExpectReuse(candidate, changed, 7, 14f, false, "prepared-result-expired");
        // A slow request can arrive fresh, but the original evidence has a separate TTL.
        candidate = Prepare(source, 17.9f);
        changed = current; changed.SampledAt = 18.1f;
        ExpectReuse(candidate, changed, 7, 18.1f, false, "request-expired");
        ExpectCreate(Wire(), source, 10f, 18.1f, false, "request-evidence-expired");

        foreach (string action in new[] { "take_turn", "ask_user", "complete" })
            ExpectCreate(Wire(action), source, 10f, 11f, true, "prepared");
        ExpectCreate(Wire("continue", "open"), source, 10f, 11f, false, "boundary-still-open");
        ExpectCreate(Wire("continue", "closed"), source, 10f, 11f, false, "boundary-still-open");
        ExpectCreate(Wire("interrupt_user", "open"), source, 10f, 11f, false, "boundary-still-open");
        ExpectCreate(Wire("take_turn", "uncertain"), source, 10f, 11f, false, "boundary-still-open");
        foreach (string mode in new[] { "singing", "uncertain" })
            ExpectCreate(Wire(mode: mode), source, 10f, 11f, false, "boundary-not-user-speech");
        foreach (string owner in new[] { "background", "uncertain" })
            ExpectCreate(Wire(owner: owner), source, 10f, 11f, false, "boundary-not-user-speech");
        ExpectCreate(Wire(confidence: 0.77f), source, 10f, 11f, false, "boundary-confidence-insufficient");
        JObject json = JObject.Parse(Wire());
        foreach (string field in new[] { "action", "mode", "source", "turn_state", "reason", "confidence" })
        {
            JObject copy = (JObject)json.DeepClone(); ((JObject)copy["quiet_boundary"]).Remove(field);
            ExpectCreate(copy.ToString(), source, 10f, 11f, false, "quiet-boundary-missing-" + field);
        }
        var missing = (JObject)json.DeepClone(); missing.Remove("quiet_boundary");
        ExpectCreate(missing.ToString(), source, 10f, 11f, false, "quiet-boundary-missing");
        foreach (string field in new[] { "action", "mode", "source", "turn_state", "reason", "confidence" })
        {
            JObject copy = (JObject)json.DeepClone();
            copy["quiet_boundary"][field] = field == "confidence" ? (JToken)"0.9" : 42;
            ExpectCreate(copy.ToString(), source, 10f, 11f, false, "quiet-boundary-type-" + field);
        }
        foreach (string field in new[] { "action", "mode", "source", "turn_state" })
        {
            JObject copy = (JObject)json.DeepClone(); copy["quiet_boundary"][field] = "invalid";
            ExpectCreate(copy.ToString(), source, 10f, 11f, false, "quiet-boundary-enum-" + field);
        }
        ExpectCreate(Wire("complete", "open"), source, 10f, 11f, false, "quiet-boundary-action-state-conflict");
        ExpectCreate(Wire(confidence: 1.1f), source, 10f, 11f, false, "quiet-boundary-range-confidence");
        foreach (string invalid in new[] { "", "{}", Wire().Substring(0, Wire().Length - 3),
            "{\"observed_mode\":\"speech\",\"mode_confidence\":\"0.99\"}" })
            ExpectCreate(invalid, source, 10f, 11f, false, null);
        json["mode_confidence"] = 0.79f;
        ExpectCreate(json.ToString(), source, 10f, 11f, false, "observed-mode-uncertain");
        json["observed_mode"] = "singing";
        ExpectCreate(json.ToString(), source, 10f, 11f, false, "observed-mode-not-speech");
        changed = source; changed.Singing = true;
        ExpectCreate(Wire(), changed, 10f, 11f, false, "source-singing-or-melody");
        changed = source; changed.MelodyActive = true;
        ExpectCreate(Wire(), changed, 10f, 11f, false, "source-singing-or-melody");
        changed = source; changed.AsrHealthy = false;
        ExpectCreate(Wire(), changed, 10f, 11f, false, "source-asr-unavailable");

        // Do not make a phrase blacklist: a completed negative request can be handed off,
        // while an unfinished correction/breath remains open by the semantic decision.
        foreach (string text in new[] { "请不要关灯。", "いえ、開けないでください。", "No, don't turn it on." })
        {
            changed = source; changed.Transcript = text;
            candidate = Prepare(changed);
            ExpectReuse(candidate, Mature(changed), 7, 12f, true, "exact-covered-quiet");
            ExpectCreate(Wire("continue", "open"), changed, 10f, 11f, false, "boundary-still-open");
        }
        TestRealActivityResumption();
        checks += PreparedListeningPolicyRegression.RunOrThrow();
        return checks;
    }

    private static void TestRealActivityResumption()
    {
        var activity = new TurnActivityEvidence(); activity.Reset(8f);
        for (int i = 0; i < 38; i++) activity.ObserveLevel(0.03f, 8f + i * 0.051f, 0.01f, 0.004f);
        var source = Source(); source.ActivityRevision = activity.ActivityRevision;
        source.LevelActive = activity.LevelActive; source.LatestSoundAt = activity.LatestSoundAt;
        Check(source.LevelActive, "fixture continuously voiced");
        var candidate = Prepare(source);
        for (int i = 0; i < 12; i++) activity.ObserveLevel(0.03f, 10f + i * 0.051f, 0.01f, 0.004f);
        Check(activity.ActivityRevision == source.ActivityRevision, "continuous tail preserves activity lease");
        for (int i = 0; i < 12; i++) activity.ObserveLevel(0.001f, 10.7f + i * 0.051f, 0.01f, 0.004f);
        for (int i = 0; i < 10; i++) activity.ObserveLevel(0.03f, 11.4f + i * 0.051f, 0.01f, 0.004f);
        Check(activity.ActivityRevision != source.ActivityRevision, "breath and resumed voice revoke activity lease");
        var current = Mature(source); current.ActivityRevision = activity.ActivityRevision;
        current.LatestSoundAt = activity.LatestSoundAt;
        ExpectReuse(candidate, current, 7, 12f, false, "renewed-sound");
    }

    private static PreparedTurnBoundaryEvidence Source() => new PreparedTurnBoundaryEvidence
    {
        CaptureSerial = 4, TextRevision = 10, ActivityRevision = 2, AudioMs = 1000,
        Transcript = "请把小灯打开。", SampledAt = 10f, LatestSoundAt = 9.5f,
        QuietSeconds = 0.5f, MinimumQuietSeconds = 0.35f, MinimumConfidence = 0.78f,
        Recording = true, AsrHealthy = true, FreshCoverage = true, CoversQuietTail = true,
        TailAvailable = true, TailAge = 0.1f,
    };
    private static PreparedTurnBoundaryEvidence Mature(PreparedTurnBoundaryEvidence source)
    {
        source.SampledAt = 12f; source.AudioMs = 2000; source.QuietSeconds = 0.6f;
        source.LevelActive = source.MelodyActive = source.TextActivityActive = false;
        return source;
    }
    private static string Wire(string action = "complete", string state = "closed", string mode = "speech",
        string owner = "user", float confidence = 0.95f) => new JObject {
        ["observed_mode"] = "speech", ["mode_confidence"] = 0.95f,
        ["quiet_boundary"] = new JObject { ["action"] = action, ["confidence"] = confidence,
            ["mode"] = mode, ["source"] = owner, ["turn_state"] = state, ["reason"] = "完整请求" },
    }.ToString();
    private static PreparedTurnBoundary Prepare(PreparedTurnBoundaryEvidence source, float ready = 11f)
    {
        PreparedTurnBoundary candidate; string reason;
        Check(PreparedTurnBoundary.TryCreate(Wire(), source, 7, 10f, ready, out candidate, out reason), "prepare " + reason);
        return candidate;
    }
    private static void ExpectCreate(string raw, PreparedTurnBoundaryEvidence source, float request, float ready,
        bool expected, string expectedReason)
    {
        PreparedTurnBoundary candidate; string reason;
        bool actual = PreparedTurnBoundary.TryCreate(raw, source, 7, request, ready, out candidate, out reason);
        Check(actual == expected && (expectedReason == null || reason == expectedReason), "create " + reason);
    }
    private static void ExpectReuse(PreparedTurnBoundary candidate, PreparedTurnBoundaryEvidence current,
        int revision, float now, bool expected, string expectedReason)
    {
        string reason; bool actual = candidate.CanReuse(current, revision, now, out reason);
        Check(actual == expected && reason == expectedReason, "reuse " + reason + " expected " + expectedReason);
    }
    private static void Check(bool success, string label)
    {
        checks++; if (!success) throw new InvalidOperationException("Prepared boundary regression: " + label);
    }
}
