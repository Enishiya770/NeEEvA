using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private int m_MovementReplyStateGeneration = -1;
    private JObject m_MovementReplyState;

    private bool UseMovementReplyPacket(int generation)
    {
        string kind = (string)ReadMovementReplySemantics(generation)?["requestKind"];
        return (kind == "action" || kind == "control" || ReadAutonomousBodyReplyPurpose(generation) != null) &&
            (m_RoomTaskIntent == "approach" || m_RoomTaskIntent == "follow" || m_RoomTaskIntent == "stop-moving") &&
            !string.IsNullOrEmpty(m_RoomTaskActionId) && ReadRoomEvidenceReplyFacts(generation) == null &&
            generation != m_SpatialGoalTerminalReplyGeneration && (bool?)BuildRoomSearchReportRequirement()["required"] != true &&
            (!SpatialGoalUnfinished(m_SpatialDeviceGoal) || RoomTaskSequenceCurrent() && !SelectedRoomTaskSequenceIsDevice()) && !HasWorkToolInFlight();
    }

    private JObject BuildBodyReplyFacts(int generation)
    {
        var room = ReadCompactRoomTaskFacts(GetComponent<ArdyRoomDialogueBridge>());
        var purpose = ReadAutonomousBodyReplyPurpose(generation);
        return new JObject {
            ["movementResultOnly"] = true,
            ["replyPurpose"] = purpose,
            ["humanInput"] = BuildCommittedReplyHumanInput(generation),
            ["originalUserRequest"] = m_ActionConstraints.CurrentUserText,
            ["originalUserRequestIsHistorical"] = purpose != null,
            ["currentRequestSemantics"] = ReadMovementReplySemantics(generation),
            ["pendingResults"] = CreateObservationReceipt()?.Facts,
            ["priorTaskResults"] = BuildMovementPriorResults(),
            ["historicalDeviceEvents"] = ReadHistoricalDeviceEvents(),
            ["taskSequence"] = ReadRoomTaskSequence(),
            ["recentPublicReplies"] = ReadRecentRoomReplies(),
            ["currentActionReceipt"] = BuildCurrentReplyActionReceipt(),
            ["currentRoomFacts"] = room,
            ["decisionFailure"] = ReadRoomTaskDecisionFailure(),
            ["deviceProposalReview"] = (int?)m_SpatialGoalLatestReview?["generation"] != generation ||
                (int?)m_SpatialGoalLatestReview?["userTurn"] != m_ActionConstraints.CurrentUserTurn ? null : PickRoomFields(m_SpatialGoalLatestReview,
                "generation", "userTurn", "workEpoch", "registered", "requestAssessment", "proposalDiscarded", "reason", "validationFailure"),
            ["previousReplyFailureReceipt"] = ReadPreviousReplyFailureReceipt(),
            ["scope"] = "Use replyPurpose when present; otherwise reply to the complete current movement request. Preserve conditions, failure causes and unmet requirements. " +
                "No current visual description is requested; object catalogs and old search traces are not needed for this movement report. " +
                "Only the owned execution receipt establishes whether an action was submitted. Neither drafting nor rewriting this reply submits, repeats or changes any action. " +
                "Use a brief natural sentence in the authored persona/language; report only the phase evidenced here, not a future phase. " +
                "Use priorTaskResults only for explicitly evidenced earlier steps; never claim the whole user request complete merely because this movement finished. " +
                "Do not say an already completed step will happen shortly or ask the listener to wait for that same completed step."
        };
    }

    private string BuildMovementReplyContext(int generation)
    {
        var facts = BuildBodyReplyFacts(generation);
        m_MovementReplyState = MovementPlaybackStamp(facts["currentRoomFacts"] as JObject, m_RoomTaskIntent, m_RoomTaskActionId);
        m_MovementReplyStateGeneration = generation;
        string context = CommittedSpatialReplyProtocol.AppendReplyFacts("[Current movement reply]\n", facts);
        UnityEngine.Debug.Log($"[Room/MovementReplyContext] generation={generation} action={m_RoomTaskActionId} characters={context.Length} state={(string)m_MovementReplyState?["expectedReport"]}", this);
        return context;
    }
}
