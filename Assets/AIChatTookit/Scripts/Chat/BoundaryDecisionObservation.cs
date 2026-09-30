using Newtonsoft.Json.Linq;

/// <summary>Diagnostic facts only; this class never authorizes closing a recording.</summary>
public static class BoundaryDecisionObservation
{
    public static JObject Fields(string action, float confidence, bool sameText, bool sameActivity,
        bool pendingText, bool newSound, bool covered, int sourceRevision, int currentRevision,
        int sourceAudioMs, int currentAudioMs, float stableSeconds = -1,
        float requiredStableSeconds = -1, float minimumConfidence = -1)
        => new JObject {
            ["action"] = action, ["confidence"] = confidence, ["sameText"] = sameText,
            ["sameActivityRevision"] = sameActivity, ["pendingText"] = pendingText,
            ["newSound"] = newSound, ["freshAudioCoverage"] = covered,
            ["sourceActivityRevision"] = sourceRevision, ["currentActivityRevision"] = currentRevision,
            ["sourceAudioMs"] = sourceAudioMs, ["currentAudioMs"] = currentAudioMs,
            ["textStableSeconds"] = stableSeconds, ["requiredTextStableSeconds"] = requiredStableSeconds,
            ["minimumConfidence"] = minimumConfidence,
            ["scope"] = "consumer-side evidence; not an instruction to interrupt or close capture"
        };
}
