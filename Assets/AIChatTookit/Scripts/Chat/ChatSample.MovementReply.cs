using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private int m_MovementReplyStateGeneration = -1;
    private JObject m_MovementReplyState;

    private bool UseMovementReplyPacket(int generation)
    {
        string kind = (string)ReadMovementReplySemantics(generation)?["requestKind"];
        return (kind == "action" || kind == "control" || ReadAutonomousReplyPurpose(generation) != null) &&
            (m_RoomTaskIntent == "approach" || m_RoomTaskIntent == "follow" || m_RoomTaskIntent == "stop-moving") &&
            !string.IsNullOrEmpty(m_RoomTaskActionId) && ReadRoomEvidenceReplyFacts(generation) == null &&
            generation != m_SpatialGoalTerminalReplyGeneration && (bool?)BuildRoomSearchReportRequirement()["required"] != true &&
            (!SpatialGoalUnfinished(m_SpatialDeviceGoal) || RoomTaskSequenceCurrent() && !SelectedRoomTaskSequenceIsDevice()) && !HasWorkToolInFlight();
    }

    private JObject BuildFocusedReplyFacts(int generation)
    {
        var room = ReadCompactRoomTaskFacts(GetComponent<ArdyRoomDialogueBridge>());
        var purpose = ReadAutonomousReplyPurpose(generation);
        return new JObject {
            ["movementResultOnly"] = m_RoomTaskIntent == "approach" || m_RoomTaskIntent == "follow" || m_RoomTaskIntent == "stop-moving",
            ["replyPurpose"] = purpose,
            ["attentionState"] = ReadAttentionState("writer"),
            ["interactionTime"] = purpose == null ? null : BuildAttentionInteractionTime(),
            ["humanInput"] = BuildCommittedReplyHumanInput(generation),
            ["originalUserRequest"] = m_ActionConstraints.CurrentUserText,
            ["originalUserRequestIsHistorical"] = purpose != null,
            ["currentRequestSemantics"] = ReadMovementReplySemantics(generation),
            ["pendingResults"] = CreateObservationReceipt()?.Facts,
            ["priorTaskResults"] = BuildMovementPriorResults(),
            ["historicalDeviceEvents"] = ReadHistoricalDeviceEvents(),
            ["eventContext"] = BuildReplyEventContext(generation),
            ["taskSequence"] = purpose == null ? ReadRoomTaskSequence() : null,
            ["recentPublicReplies"] = ReadRecentRoomReplies(),
            ["currentActionReceipt"] = BuildCurrentReplyActionReceipt(),
            ["currentRoomFacts"] = room,
            ["decisionFailure"] = ReadRoomTaskDecisionFailure(),
            ["deviceProposalReview"] = (int?)m_SpatialGoalLatestReview?["generation"] != generation ||
                (int?)m_SpatialGoalLatestReview?["userTurn"] != m_ActionConstraints.CurrentUserTurn ? null : PickRoomFields(m_SpatialGoalLatestReview,
                "generation", "userTurn", "workEpoch", "registered", "requestAssessment", "proposalDiscarded", "reason", "validationFailure"),
            ["previousReplyFailureReceipt"] = ReadPreviousReplyFailureReceipt(),
            ["scope"] = "Use replyPurpose when present; otherwise reply to the complete current movement request. Preserve conditions, failure causes and unmet requirements. " +
                "Object catalogs and old search traces are not needed for a receipt or ordinary conversation. Missing visual evidence cannot support a new visual claim. " +
                "Only the owned execution receipt establishes whether an action was submitted. Neither drafting nor rewriting this reply submits, repeats or changes any action. " +
                "Use a brief natural sentence in the authored persona/language; report only the phase evidenced here, not a future phase. " +
                "Use priorTaskResults only for explicitly evidenced earlier steps; never claim the whole user request complete merely because this movement finished. " +
                "Do not say an already completed step will happen shortly or ask the listener to wait for that same completed step."
        };
    }

    private string BuildFocusedReplyContext(int generation)
    {
        var facts = BuildFocusedReplyFacts(generation);
        m_MovementReplyState = MovementPlaybackStamp(facts["currentRoomFacts"] as JObject, m_RoomTaskIntent, m_RoomTaskActionId);
        m_MovementReplyStateGeneration = generation;
        string context = CommittedSpatialReplyProtocol.AppendReplyFacts("[Current focused reply]\n", facts);
        UnityEngine.Debug.Log($"[Room/FocusedReplyContext] generation={generation} action={m_RoomTaskActionId} characters={context.Length} state={(string)m_MovementReplyState?["expectedReport"]}", this);
        return context;
    }
}
