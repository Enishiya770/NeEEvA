using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

/// <summary>Owned goal checkpoints. Model proposals are never execution results.</summary>
public sealed partial class ActorPlanRuntime
{
    public string Id { get; private set; }
    public int Revision { get; private set; }
    public string State { get; private set; }
    public string OriginalGoal { get; private set; }
    // Semantic expansion of the adopted draft. It preserves omitted referents
    // and steps for later planning, but never replaces the draft's authority.
    public string CompiledGoal { get; private set; }
    public string SourceOrigin { get; private set; }
    public string RemainingGoal { get; private set; }
    public string EntityId { get; private set; }
    public string Reason { get; private set; }
    public string PendingActionId { get; private set; }
    public string GoalCoverage { get; private set; }
    private JObject deviceCompletion;
    public bool Active => State == "planning" || State == "executing";
    public readonly JArray Results = new JArray();
    private string pendingRemainder, pendingIntent;
    private readonly HashSet<string> completed = new HashSet<string>(StringComparer.Ordinal);
    private string pendingFingerprint;
    public ActorPlanRuntime(string goal, string sourceOrigin = "actor_draft", string coverage = "unknown", string compiledGoal = null)
    {
        Id = Guid.NewGuid().ToString("N"); Revision = 1; State = "planning";
        OriginalGoal = RemainingGoal = goal ?? "";
        CompiledGoal = compiledGoal ?? "";
        SourceOrigin = sourceOrigin;
        GoalCoverage = coverage;
    }
    // A previously unknown entity may be bound by observation before the first
    // operation. Never derive a new completion condition from a completed write.
    public bool AdoptDeviceCompletion(int revision, string entityId, string state, string goalText, string sourceOrigin)
    {
        if (revision != Revision || State != "planning" || bodyTurnCompletion != null || !OnlyObservedEntityBeforeOperation(entityId) ||
            string.IsNullOrWhiteSpace(entityId) || (state != "on" && state != "off") ||
            goalText != OriginalGoal || sourceOrigin != SourceOrigin ||
            (GoalCoverage != "atomic" && !(GoalCoverage == "unknown" && SourceOrigin == "user"))) return false;
        if (deviceCompletion != null) return (string)deviceCompletion["entityId"] == entityId &&
            (string)deviceCompletion["state"] == state;
        GoalCoverage = "atomic";
        deviceCompletion = new JObject { ["kind"] = "device_state", ["planId"] = Id,
            ["adoptedRevision"] = revision, ["entityId"] = entityId, ["state"] = state,
            ["goalText"] = OriginalGoal, ["sourceOrigin"] = SourceOrigin, ["coverage"] = "atomic" };
        EntityId = entityId;
        return true;
    }
    private bool OnlyObservedEntityBeforeOperation(string entityId)
    {
        if (Results.Count == 0) return Revision == 1;
        if (string.IsNullOrEmpty(entityId) || EntityId != entityId) return false;
        foreach (JObject result in Results)
            if ((bool?)result["succeeded"] != true || ((string)result["intent"] != "inspect-object" && (string)result["intent"] != "reobserve-object") ||
                (string)result["phase"] != "observed" || (string)result["entityId"] != entityId) return false;
        return true;
    }
    public JObject DeviceCompletionFacts() => deviceCompletion == null ? null : (JObject)deviceCompletion.DeepClone();
    public bool RestoreCompletedCheckpoint(JObject checkpoint)
    {
        // A changed role goal is a new choice. Do not transplant an old entity
        // or action result into it merely because the user input is unchanged.
        if (checkpoint == null || (string)checkpoint["originalGoal"] != OriginalGoal ||
            (string)checkpoint["sourceOrigin"] != SourceOrigin || Results.Count != 0 || State != "planning") return false;
        string priorCompiledGoal = (string)checkpoint["compiledGoal"] ?? "";
        if (!string.IsNullOrEmpty(CompiledGoal) && !string.IsNullOrEmpty(priorCompiledGoal) &&
            CompiledGoal != priorCompiledGoal) return false;
        if (!CanRestoreSpatialCheckpoint(checkpoint)) return false;
        // A newly adopted condition cannot retrospectively verify an older
        // write recorded without one. Nor may its fingerprint prohibit the
        // fresh readback needed by this new owner, leaving the plan deadlocked.
        if (deviceCompletion != null && !(checkpoint["completionCondition"] is JObject) && checkpoint["results"] is JArray priorResults)
            foreach (JObject result in priorResults)
                if ((bool?)result["succeeded"] == true && (string)result["intent"] == "set-object-state") return false;
        if (deviceCompletion != null && ((!string.IsNullOrEmpty((string)checkpoint["entityId"]) &&
            (string)checkpoint["entityId"] != (string)deviceCompletion["entityId"]) ||
            checkpoint["completionCondition"] is JObject oldCondition &&
            ((string)oldCondition["state"] != (string)deviceCompletion["state"] ||
             (string)oldCondition["entityId"] != (string)deviceCompletion["entityId"]))) return false;
        if (checkpoint["completionCondition"] is JObject condition)
        {
            if (GoalCoverage == "compound" || (string)checkpoint["goalCoverage"] != "atomic" ||
                (string)condition["planId"] != (string)checkpoint["id"] ||
                !ValidCompletionAdoptionHistory(checkpoint, condition) ||
                (string)condition["kind"] != "device_state" || (string)condition["coverage"] != "atomic" ||
                (string)condition["goalText"] != OriginalGoal || (string)condition["sourceOrigin"] != SourceOrigin ||
                string.IsNullOrWhiteSpace((string)condition["entityId"]) ||
                ((string)condition["state"] != "on" && (string)condition["state"] != "off") ||
                (!string.IsNullOrEmpty((string)checkpoint["entityId"]) &&
                 (string)condition["entityId"] != (string)checkpoint["entityId"])) return false;
            if (deviceCompletion == null)
            {
                deviceCompletion = (JObject)condition.DeepClone();
                deviceCompletion["planId"] = Id;
            }
            GoalCoverage = "atomic";
        }
        else if (GoalCoverage == "unknown" && (string)checkpoint["goalCoverage"] == "compound") GoalCoverage = "compound";
        if (checkpoint["results"] is JArray results)
            foreach (JObject result in results)
                if ((bool?)result["succeeded"] == true) Results.Add(result.DeepClone());
        if (checkpoint["completedFingerprints"] is JArray fingerprints)
            foreach (string fingerprint in fingerprints.Values<string>())
                if (!string.IsNullOrEmpty(fingerprint)) completed.Add(fingerprint);
        EntityId = (string)checkpoint["entityId"] ?? (string)deviceCompletion?["entityId"];
        if (string.IsNullOrEmpty(CompiledGoal)) CompiledGoal = priorCompiledGoal;
        RemainingGoal = (string)checkpoint["remainingGoal"] ?? OriginalGoal;
        if (deviceCompletion != null && string.IsNullOrWhiteSpace(RemainingGoal) && !HasVerifiedDeviceCompletionResult())
            RemainingGoal = OriginalGoal;
        if (bodyTurnCompletion != null && string.IsNullOrWhiteSpace(RemainingGoal) && !HasVerifiedBodyTurnCompletion())
            RemainingGoal = OriginalGoal;
        RestorePrerequisiteAttempts(checkpoint);
        Revision = Results.Count + 1;
        return true;
    }
    private static bool ValidCompletionAdoptionHistory(JObject checkpoint, JObject condition)
    {
        int adopted = (int?)condition["adoptedRevision"] ?? 0;
        var results = checkpoint["results"] as JArray;
        if (adopted < 1 || adopted > (results?.Count ?? 0) + 1) return false;
        for (int i = 0; i < adopted - 1; ++i)
            if ((bool?)results[i]["succeeded"] != true || ((string)results[i]["intent"] != "inspect-object" && (string)results[i]["intent"] != "reobserve-object") ||
                (string)results[i]["phase"] != "observed" ||
                (string)results[i]["entityId"] != (string)condition["entityId"]) return false;
        return true;
    }
    public bool HasCompleted(string fingerprint) => !string.IsNullOrEmpty(fingerprint) && completed.Contains(fingerprint);
    public bool CloseWithoutNextStep(int revision, string remainder)
    {
        // A model's none can end planning, but cannot manufacture a successful
        // physical result. Unmet non-spatial goals keep their existing handoff.
        if (revision != Revision || State != "planning" || (deviceCompletion != null && !HasVerifiedDeviceCompletionResult()) ||
            (bodyTurnCompletion != null && !HasVerifiedBodyTurnCompletion()) || !string.IsNullOrWhiteSpace(remainder) ||
            Results.Count == 0) return false;
        RemainingGoal = ""; State = "completed"; Reason = "no-further-adopted-step";
        ++Revision; return true;
    }
    private bool HasVerifiedDeviceCompletionResult()
    {
        if (deviceCompletion == null) return false;
        foreach (JObject result in Results)
            if ((bool?)result["succeeded"] == true && (string)result["intent"] == "set-object-state" &&
                ((string)result["phase"] == "performed" || (string)result["phase"] == "already-satisfied") &&
                (string)result["entityId"] == (string)deviceCompletion["entityId"] &&
                (string)result["verifiedDeviceState"] == (string)deviceCompletion["state"] &&
                (string)result["completionCondition"]?["goalText"] == OriginalGoal) return true;
        return false;
    }
    public void MarkOngoing(string reason)
    {
        State = "ongoing"; Reason = reason;
        if (string.IsNullOrWhiteSpace(RemainingGoal)) RemainingGoal = "The submitted follow task remains ongoing until an explicit stop; no later step has been executed.";
    }
    public bool Propose(int revision, string intent, string remainder, string fingerprint, out string reason)
    {
        reason = "plan-not-current";
        if (revision != Revision || State != "planning") return false;
        if (Results.Count >= 6) { Cancel("plan-step-limit"); reason = Reason; return false; }
        if (string.IsNullOrWhiteSpace(intent) || remainder == null || remainder.Length > 640)
        { reason = "invalid-plan-step"; return false; }
        if (!string.IsNullOrEmpty(fingerprint) && completed.Contains(fingerprint))
        { reason = "plan-step-already-completed"; return false; }
        pendingIntent = intent; pendingRemainder = remainder; pendingFingerprint = fingerprint;
        PendingActionId = null; State = "executing"; reason = "ok"; return true;
    }
    public bool RetryUnsubmitted(int revision)
    {
        if (revision != Revision || State != "executing" || PendingActionId != null) return false;
        State = "planning"; pendingIntent = pendingRemainder = pendingFingerprint = null; return true;
    }
    public bool BindAction(int revision, string actionId)
    {
        if (revision != Revision || State != "executing" || string.IsNullOrEmpty(actionId)) return false;
        if (PendingActionId != null && PendingActionId != actionId) return false;
        PendingActionId = actionId; return true;
    }
    public bool RejectUnsubmitted(int revision, string intent, string reason)
    {
        if (revision != Revision || State != "executing" || PendingActionId != null || pendingIntent != intent ||
            string.IsNullOrWhiteSpace(reason)) return false;
        Results.Add(new JObject { ["step"] = Revision, ["intent"] = intent, ["actionId"] = null,
            ["attemptId"] = Id + ":attempt:" + Revision, ["phase"] = "not-submitted", ["reason"] = reason,
            ["succeeded"] = false, ["submitted"] = false });
        State = "blocked"; Reason = reason;
        return true;
    }
    public bool Resolve(int revision, string actionId, bool success, string phase, string reason, string entityId, bool bindEntity,
        string verifiedDeviceState = null, double? verifiedBodyYaw = null, double? requestedBodyYaw = null)
    {
        if (revision != Revision || State != "executing" || PendingActionId != actionId) return false;
        if (bindEntity && !string.IsNullOrEmpty(entityId) && !string.IsNullOrEmpty(EntityId) && EntityId != entityId)
        { success = false; reason = "plan-target-changed"; }
        Results.Add(new JObject { ["step"] = Revision, ["intent"] = pendingIntent, ["actionId"] = actionId,
            ["phase"] = phase, ["reason"] = reason, ["entityId"] = entityId, ["succeeded"] = success });
        Reason = reason;
        if (!success) { State = "blocked"; return true; }
        if (bindEntity && !string.IsNullOrEmpty(entityId)) EntityId = entityId;
        if (!string.IsNullOrEmpty(pendingFingerprint)) completed.Add(pendingFingerprint);
        bool conditionSatisfied = deviceCompletion != null && (string)deviceCompletion["planId"] == Id &&
            GoalCoverage == "atomic" && pendingIntent == "set-object-state" && bindEntity &&
            entityId == (string)deviceCompletion["entityId"] && verifiedDeviceState == (string)deviceCompletion["state"] &&
            (phase == "performed" || phase == "already-satisfied");
        // An unexpected independent tail cannot be erased even by a matching
        // device state. The original atomic wording is not a new tail.
        conditionSatisfied &= string.IsNullOrWhiteSpace(pendingRemainder) || pendingRemainder == OriginalGoal;
        if (conditionSatisfied)
        {
            ((JObject)Results.Last)["completionCondition"] = deviceCompletion.DeepClone();
            ((JObject)Results.Last)["verifiedDeviceState"] = verifiedDeviceState;
            Reason = "adopted-device-goal-verified";
        }
        bool turnSatisfied = BodyTurnResultSatisfies(pendingIntent, phase, requestedBodyYaw, verifiedBodyYaw) &&
            (string.IsNullOrWhiteSpace(pendingRemainder) || pendingRemainder == OriginalGoal || pendingRemainder == CompiledGoal);
        if (turnSatisfied)
        {
            ((JObject)Results.Last)["bodyTurnCompletion"] = bodyTurnCompletion.DeepClone();
            ((JObject)Results.Last)["requestedBodyYaw"] = requestedBodyYaw;
            ((JObject)Results.Last)["verifiedBodyYaw"] = verifiedBodyYaw;
            Reason = "adopted-body-turn-verified";
        }
        RemainingGoal = conditionSatisfied || turnSatisfied ? "" : (deviceCompletion != null || bodyTurnCompletion != null) && string.IsNullOrWhiteSpace(pendingRemainder)
            ? OriginalGoal : pendingRemainder; ++Revision;
        State = string.IsNullOrWhiteSpace(RemainingGoal) ? "completed" : "planning";
        PendingActionId = null; return true;
    }
    public void Cancel(string reason) { if (!Active && State != "ongoing") return; State = "cancelled"; Reason = reason; ++Revision; }
    public JObject Facts() => new JObject { ["id"] = Id, ["revision"] = Revision, ["state"] = State,
        ["sourceOrigin"] = SourceOrigin, ["pendingActionId"] = PendingActionId,
        ["goalCoverage"] = GoalCoverage, ["completionCondition"] = DeviceCompletionFacts(),
        ["bodyTurnCompletion"] = BodyTurnCompletionFacts(), ["prerequisiteAttempts"] = prerequisiteAttempts.DeepClone(),
        ["completedFingerprints"] = new JArray(completed),
        ["originalGoal"] = OriginalGoal, ["compiledGoal"] = CompiledGoal,
        ["remainingGoal"] = RemainingGoal, ["entityId"] = EntityId,
        ["results"] = Results.DeepClone(), ["reason"] = Reason,
        ["rule"] = "Choose only the next uncompleted step of this same role goal; planRemaining is the complete remainder AFTER the proposed step succeeds, not a success claim. Preserve bound entityId; an observation reference used only to see closer does not bind the intended small object. Never repeat completed actions. After verified finite results, none with empty planRemaining legally closes planning without a new action or a new success claim. Unsupported/non-spatial unfinished goals use none with their complete remaining goal. Continuous follow remains ongoing, not completed; later dependent goals stay unexecuted." };
}
