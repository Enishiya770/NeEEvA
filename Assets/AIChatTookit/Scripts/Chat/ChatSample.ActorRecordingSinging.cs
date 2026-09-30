using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    // This route expresses the role's adoption only. The dedicated recording IR
    // still independently selects source/range, followed by the existing review.
    private bool TryDispatchActorRecordingSinging(FastLoopTurnContext turn, ActorDraft draft,
        ActorIntentDecision decision, string language, int generation)
    {
        if (decision?.Task != "recording_singing") return false;
        if (!FastLoopTurnCurrent(turn) || generation != turn.Generation || turn.HandedOff ||
            draft == null || !ActorIntentProtocol.Validate(decision, draft.Text, out _) ||
            decision.RequiresFullReview || decision.Domain != "other_action" || decision.Kind != "replan" ||
            (decision.Coverage != "atomic" && decision.Coverage != "compound") ||
            decision.Condition != "none" || decision.Action != "none") return false;
        if (!turn.Model.SupportsRecordingSingingDecision)
        {
            CompleteActorInterpretationFailure(turn.Model, turn.Prompt, language, generation,
                turn.RecordHistory, "typed-recording-stage-unavailable");
            return true;
        }
        m_ResponseTrace?.Mark("recording_decision.actor_route", new JObject {
            ["generation"] = generation, ["draftId"] = draft.Id, ["draftRevision"] = draft.Revision,
            ["stage"] = "RecordingSingingDecision", ["sourceSelected"] = false,
            ["goalCoverage"] = decision.Coverage, ["completeGoalRetained"] = true,
            ["rangeSelected"] = false, ["executionAuthorized"] = false });
        // Trace subscribers are allowed to cancel/reenter synchronously.
        if (!FastLoopTurnCurrent(turn)) return true;
        HandoffFastLoopToPlanner(turn.Model, generation, turn.OutputRevision, draft, decision,
            "actor-recording-singing", recordingSinging: true);
        return true;
    }

    // Skill routing can discover a recording goal after a general actor plan
    // was registered. Only hand over our still-unsubmitted planning owner; an
    // executing, ongoing or unrelated spatial task retains its own lifecycle.
    private bool ReleasePlanningOwnerForRecording(FastLoopTurnContext turn)
    {
        if (!FastLoopTurnCurrent(turn)) return false;
        var plan = m_ActorPlan;
        if (plan == null || !plan.Active) return true;
        if (!ReferenceEquals(m_ActorPlanTurn, turn) || !ActorPlanOwnerCurrent(plan, turn) ||
            plan.State != "planning" || !string.IsNullOrEmpty(plan.PendingActionId)) return false;
        CancelActorPlan("typed-recording-stage-took-planning-ownership");
        if (!FastLoopTurnCurrent(turn) || !ReferenceEquals(plan, m_ActorPlan)) return false;
        UpdateActorPlanProposal();
        return true;
    }
}
