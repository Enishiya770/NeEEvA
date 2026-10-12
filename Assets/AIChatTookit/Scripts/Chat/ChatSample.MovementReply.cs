using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private int m_MovementReplyStateGeneration = -1;
    private JObject m_MovementReplyState;

    private bool UseMovementReplyPacket(int generation)
    {
        string kind = (string)ReadMovementReplySemantics(generation)?["requestKind"];
        return (kind == "action" || kind == "control") &&
            (m_RoomTaskIntent == "approach" || m_RoomTaskIntent == "follow" || m_RoomTaskIntent == "stop-moving") &&
            !string.IsNullOrEmpty(m_RoomTaskActionId) && ReadRoomEvidenceReplyFacts(generation) == null &&
            generation != m_SpatialGoalTerminalReplyGeneration && (bool?)BuildRoomSearchReportRequirement()["required"] != true &&
            (!SpatialGoalUnfinished(m_SpatialDeviceGoal) || RoomTaskSequenceCurrent() && !SelectedRoomTaskSequenceIsDevice()) && !HasWorkToolInFlight();
    }

    private string BuildMovementReplyContext(int generation)
    {
        var room = ReadCompactRoomTaskFacts(GetComponent<ArdyRoomDialogueBridge>());
        m_MovementReplyState = MovementPlaybackStamp(room, m_RoomTaskIntent, m_RoomTaskActionId);
        m_MovementReplyStateGeneration = generation;
        var facts = new JObject {
            ["movementResultOnly"] = true,
            ["humanInput"] = BuildCommittedReplyHumanInput(generation),
            ["originalUserRequest"] = m_ActionConstraints.CurrentUserText,
            ["currentRequestSemantics"] = ReadMovementReplySemantics(generation),
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
            ["scope"] = "Reply to the complete current movement request using this action's latest execution. Preserve conditions, failure causes and unmet requirements. " +
                "No current visual description is requested; object catalogs and old search traces are not needed for this movement report. " +
                "Movement already started independently. Neither drafting nor rewriting this reply submits, repeats or changes any action. " +
                "Use a brief natural sentence in the authored persona/language; report only the phase evidenced here, not a future phase. " +
                "Use priorTaskResults only for explicitly evidenced earlier steps; never claim the whole user request complete merely because this movement finished. " +
                "Do not say an already completed step will happen shortly or ask the listener to wait for that same completed step."
        };
        string context = CommittedSpatialReplyProtocol.AppendReplyFacts("[Current movement reply]\n", facts);
        UnityEngine.Debug.Log($"[Room/MovementReplyContext] generation={generation} action={m_RoomTaskActionId} characters={context.Length} state={(string)m_MovementReplyState?["expectedReport"]}", this);
        return context;
    }
}
