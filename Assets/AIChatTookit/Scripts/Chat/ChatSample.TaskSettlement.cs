using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private int m_ActorSettledWorkEpoch = -1;
    private ActorPlanRuntime m_ActorClosureClaim;
    private int m_ActorClosureClaimRevision = -1;
    // Capability requirements, never a keyword match against the user's words.
    // "other_action" includes spatial tasks as well as nonspatial tools.
    private static bool HasTypedSpatialProposal(JObject proposal)
    {
        if (proposal == null) return false;
        string motion = (string)proposal["motionScope"];
        return (string)proposal["domain"] == "head" ||
            motion == "head_preset" || motion == "head_angle" || motion == "body_turn" || motion == "object_goal" ||
            (string)proposal["deviceState"] == "on" || (string)proposal["deviceState"] == "off" ||
            (string)proposal["task"] == "approach" || (string)proposal["task"] == "follow";
    }

    private bool TryCloseUnroutedSpatialRemainder(FastLoopTurnContext turn)
    {
        var plan = m_ActorPlan;
        if (!ActorPlanOwnerCurrent(plan, turn) || plan.Results.Count == 0 ||
            plan.State != "cancelled" || plan.Reason != "remaining-goal-needs-nonspatial-planner") return false;
        // An independently specified nonspatial remainder (query, memory, etc.)
        // keeps its bounded tool handoff. Only an unchanged whole-goal replay
        // is stopped here; this is not a generic ban on non-recording skills.
        if (plan.RemainingGoal != plan.OriginalGoal && plan.RemainingGoal != plan.CompiledGoal) return false;
        // No next step is not proof of completion. Keep successful results and
        // the exact unresolved remainder, then close this attempt once.
        PublishActorPlanClosure(plan, turn, false, "no-specialized-route-for-remaining-goal");
        return true;
    }

    private void SettleActorUserWork(int generation, int epoch, bool completed, string reason)
    {
        if (generation != m_FormalResponseGeneration || epoch != m_WorkEpoch ||
            HasWorkToolInFlight() || HasPendingSingingGoalReview() || IsActiveSingingGoalUnfinished() ||
            !string.IsNullOrEmpty(m_PendingSelfInspection)) return;
        m_ActorSettledWorkEpoch = epoch;
        m_WorkReviewOpen = false;
        m_WorkStatus = completed ? "closed" : "waiting_user";
        m_WorkIntent = "";
        CloseToolCorrectionForActorFailure();
        m_ResponseTrace?.Mark("work.actor_settled", new JObject {
            ["generation"] = generation, ["workEpoch"] = epoch,
            ["status"] = m_WorkStatus, ["reason"] = reason,
            ["actualTaskCompleted"] = completed,
            ["priorExecutionResultsPreserved"] = true
        });
    }

    private bool ClaimActorPlanClosure(ActorPlanRuntime plan)
    {
        if (ReferenceEquals(m_ActorClosureClaim, plan) && m_ActorClosureClaimRevision == plan.Revision) return false;
        m_ActorClosureClaim = plan; m_ActorClosureClaimRevision = plan.Revision;
        return true;
    }

    private bool TryConsumeSettledActorWorkFeedback(int generation)
    {
        if (generation != m_FormalResponseGeneration || m_ActorSettledWorkEpoch != m_WorkEpoch ||
            HasWorkToolInFlight() || IsActiveSingingGoalUnfinished() || !string.IsNullOrEmpty(m_PendingSelfInspection)) return false;
        // A delayed review/feedback round is not a new user instruction. Finish
        // its response lease quietly without submitting General or any action.
        m_FormalResponseInFlight = false;
        CloseToolCorrectionForActorFailure();
        OnStreamDelta("<silent/>");
        OnStreamComplete("<silent/>");
        return true;
    }
}
