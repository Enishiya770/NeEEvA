using System;
using Newtonsoft.Json.Linq;

public sealed partial class ActorPlanRuntime
{
    private JObject bodyTurnCompletion;
    private readonly JObject prerequisiteAttempts = new JObject();

    // Only the already compiled atomic role choice can supply this requirement.
    // A later successful move cannot retrospectively declare the original goal.
    public bool AdoptBodyTurnCompletion(double degrees)
    {
        if (State != "planning" || Revision != 1 || Results.Count != 0 || GoalCoverage != "atomic" ||
            SourceOrigin != "actor_draft" || deviceCompletion != null || !Finite(degrees) || degrees < 5 || degrees > 180)
            return false;
        if (bodyTurnCompletion != null) return (double)bodyTurnCompletion["degrees"] == degrees;
        bodyTurnCompletion = new JObject { ["kind"] = "body_turn", ["planId"] = Id,
            ["adoptedRevision"] = Revision, ["goalText"] = OriginalGoal, ["sourceOrigin"] = SourceOrigin,
            ["coverage"] = "atomic", ["degrees"] = degrees,
            ["angleScope"] = "Absolute magnitude only, not a rightward signed yaw. Room chooses left/right from this adopted goal; the program converts its direction enum to signed motion." };
        return true;
    }

    public JObject BodyTurnCompletionFacts() => bodyTurnCompletion == null ? null : (JObject)bodyTurnCompletion.DeepClone();

    public bool MatchesBodyTurnRequest(double yaw) => bodyTurnCompletion == null ||
        Finite(yaw) && Math.Abs(Math.Abs(yaw) - (double)bodyTurnCompletion["degrees"]) <= .01;

    private bool CanRestoreSpatialCheckpoint(JObject checkpoint)
    {
        var prior = checkpoint["bodyTurnCompletion"] as JObject;
        if (prior != null && (bodyTurnCompletion == null || (string)prior["planId"] != (string)checkpoint["id"] ||
            (string)prior["kind"] != "body_turn" || (int?)prior["adoptedRevision"] != 1 ||
            (string)prior["coverage"] != "atomic" || (string)checkpoint["goalCoverage"] != "atomic" ||
            (string)prior["goalText"] != OriginalGoal || (string)prior["sourceOrigin"] != SourceOrigin ||
            !JToken.DeepEquals(prior["degrees"], bodyTurnCompletion["degrees"]))) return false;
        if (prior != null && checkpoint["results"] is JArray priorResults)
            foreach (JObject result in priorResults)
                if (result["bodyTurnCompletion"] is JObject resultCondition && !JToken.DeepEquals(resultCondition, prior)) return false;
        if (bodyTurnCompletion != null && prior == null && checkpoint["results"] is JArray results)
            foreach (JObject result in results)
                if ((bool?)result["succeeded"] == true && (string)result["intent"] == "turn-in-place") return false;
        if (checkpoint["prerequisiteAttempts"] is JObject attempts)
            foreach (var item in attempts.Properties())
                if (item.Value.Type != JTokenType.Integer || (int)item.Value < 0 || (int)item.Value > 2 ||
                    (!item.Name.StartsWith("reobserve-object:", StringComparison.Ordinal) &&
                     !item.Name.StartsWith("approach-object:", StringComparison.Ordinal))) return false;
        return true;
    }

    private void RestorePrerequisiteAttempts(JObject checkpoint)
    {
        if (checkpoint["prerequisiteAttempts"] is JObject attempts)
            foreach (var item in attempts.Properties()) prerequisiteAttempts[item.Name] = item.Value.DeepClone();
    }

    private bool BodyTurnResultSatisfies(string intent, string phase, double? requested, double? measured) =>
        bodyTurnCompletion != null && (string)bodyTurnCompletion["planId"] == Id && GoalCoverage == "atomic" &&
        intent == "turn-in-place" && (phase == "observed" || phase == "turned") && requested.HasValue && measured.HasValue &&
        Finite(requested.Value) && Finite(measured.Value) &&
        Math.Abs(Math.Abs(requested.Value) - (double)bodyTurnCompletion["degrees"]) <= .01 &&
        Math.Sign(requested.Value) == Math.Sign(measured.Value) && Math.Abs(requested.Value - measured.Value) <= 5;

    private bool HasVerifiedBodyTurnCompletion()
    {
        foreach (JObject result in Results)
            if ((bool?)result["succeeded"] == true &&
                (string)result["bodyTurnCompletion"]?["goalText"] == OriginalGoal &&
                BodyTurnResultSatisfies((string)result["intent"], (string)result["phase"],
                    (double?)result["requestedBodyYaw"], (double?)result["verifiedBodyYaw"])) return true;
        return false;
    }

    public bool RegisterPrerequisiteAttempt(string kind, string entityId)
    {
        if (State != "planning" || string.IsNullOrEmpty(entityId) ||
            (kind != "reobserve-object" && kind != "approach-object")) return false;
        string key = kind + ":" + entityId;
        int attempts = (int?)prerequisiteAttempts[key] ?? 0;
        if (attempts >= 2) return false;
        prerequisiteAttempts[key] = attempts + 1;
        return true;
    }

    public bool PreservesPrerequisiteGoal(string intent, string entityId, string remainder) =>
        (intent != "reobserve-object" && (int?)prerequisiteAttempts[intent + ":" + (entityId ?? "")] == null) ||
        remainder == RemainingGoal;

    private static bool Finite(double number) => !double.IsNaN(number) && !double.IsInfinity(number);
}
