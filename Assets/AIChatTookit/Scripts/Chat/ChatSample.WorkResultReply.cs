using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private bool UseWorkResultReplyPacket(int generation)
    {
        string kind = (string)ReadMovementReplySemantics(generation)?["requestKind"];
        return m_WorkReplyOnly && m_WorkReviewOpen && m_WorkStatus == "continue" &&
            m_AgentCurrentRoundIsTick && ShouldGuardWorkSceneFeedback(generation) && RoomTaskSequenceCurrent() &&
            !HasWorkToolInFlight() && !SceneActionIsBusy && (kind == "action" || kind == "control") &&
            ReadRoomEvidenceReplyFacts(generation) == null;
    }

    // Result delivery is selected by the work ledger, not words such as lamp or
    // return. Mixed questions retain their evidence route rather than silently
    // losing an independent answer. Runtime memory is never changed here.
    private JObject BuildWorkResultReplyFacts(int generation) => new JObject {
        ["workResultOnly"] = true,
        ["attentionState"] = ReadAttentionState("work-result"),
        ["humanInput"] = BuildCommittedReplyHumanInput(generation),
        ["originalUserRequest"] = m_ActionConstraints.CurrentUserText,
        ["currentRequestSemantics"] = ReadMovementReplySemantics(generation),
        ["workEpoch"] = m_WorkEpoch, ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
        ["taskSequence"] = ReadRoomTaskSequence(),
        ["currentExecution"] = BuildWorkResultExecution(),
        ["eventContext"] = BuildReplyEventContext(generation),
        ["priorTaskResults"] = BuildMovementPriorResults(),
        ["pendingResults"] = CreateObservationReceipt()?.Facts,
        ["workProgress"] = m_WorkProgressFact,
        ["lastMotionExecution"] = m_LastMotionExecutionFeedback == null ? null : MotionFeedbackJson(m_LastMotionExecutionFeedback),
        ["recentPublicReplies"] = ReadRecentRoomReplies(),
        ["decisionFailure"] = ReadRoomTaskDecisionFailure(),
        ["previousReplyFailureReceipt"] = ReadPreviousReplyFailureReceipt(),
        ["newActionSubmitted"] = false,
        ["scope"] = "Deliver the still-owed result or diagnosis for the complete original request. " +
            "Use each step's own receipt, identity and event time. Completed results remain completed; " +
            "a failed step is not pending and this reply neither retries nor promises another attempt. " +
            "Report the concrete remaining failure and any bounded recovery already attempted. " +
            "Preserve observedStateStatement's meaning in the dialogue language: an explicit runtime failure is established, not a guess or an unknown final result. " +
            "Deliver a pending inspection answer if present. Earlier accepted words are not proof of playback or execution. " +
            "No new perception, tool selection, or whole-scene description is requested by this result-only stage."
    };

    private JObject BuildWorkResultExecution()
    {
        var result = BuildWorkRoomCompletionObservation();
        if (result != null)
            result["observedStateStatement"] = MovementProgressText(result["currentRoomFacts"] as JObject,
                (string)result["intent"], (string)result["actionId"], "zh", false);
        return result;
    }

    private string BuildWorkResultReplyContext(int generation)
    {
        var facts = BuildWorkResultReplyFacts(generation);
        string context = CommittedSpatialReplyProtocol.AppendReplyFacts("[Current work result]\n", facts);
        UnityEngine.Debug.Log("[Work/ResultReplyContext] " + new JObject {
            ["generation"] = generation, ["workEpoch"] = m_WorkEpoch,
            ["characters"] = context.Length, ["newActionSubmitted"] = false,
            ["fields"] = new JObject(System.Linq.Enumerable.Select(facts.Properties(), p =>
                new JProperty(p.Name, p.Value.ToString(Formatting.None).Length)))
        }.ToString(Formatting.None), this);
        return context;
    }

    private JObject CurrentSpeechMovementRequirement(int generation) => UseWorkResultReplyPacket(generation)
        ? BuildWorkRoomCompletionObservation()?["movementReportRequirement"] as JObject
        : RoomMovementResultPolicy.Requirement(ReadCompactRoomTaskFacts(GetComponent<ArdyRoomDialogueBridge>()), m_RoomTaskIntent, m_RoomTaskActionId);
}
