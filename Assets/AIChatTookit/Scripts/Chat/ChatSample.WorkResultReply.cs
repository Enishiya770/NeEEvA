using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;

public partial class ChatSample
{
    private bool UseWorkResultReplyPacket(int generation)
    {
        string kind = (string)ReadMovementReplySemantics(generation)?["requestKind"];
        var sequence = ReadRoomTaskSequence();
        bool plannedActionOnly = kind == null && sequence?["steps"] is JArray steps && steps.Count > 0 &&
            sequence["coverage"] is JArray coverage && coverage.Count > 0 &&
            !coverage.OfType<JObject>().Any(row => (string)row["kind"] == "answer") &&
            (sequence["answerObligations"] as JArray)?.Count == 0;
        return m_WorkReplyOnly && m_WorkReviewOpen && m_WorkStatus == "continue" &&
            m_AgentCurrentRoundIsTick && ShouldGuardWorkSceneFeedback(generation) && RoomTaskSequenceCurrent() &&
            !HasWorkToolInFlight() && !SceneActionIsBusy && (kind == "action" || kind == "control" || plannedActionOnly) &&
            ReadRoomEvidenceReplyFacts(generation) == null;
    }

    // Result delivery is selected by the work ledger, not words such as lamp or
    // return. Mixed questions retain their evidence route rather than silently
    // losing an independent answer. Runtime memory is never changed here.
    private JObject BuildWorkResultReplyFacts(int generation)
    {
        var delivery=ReadWorkDelivery();
        var sequence=ReadRoomTaskSequence();
        // The ledger contains the canonical identity, status and outcome for
        // each report. Retain plan coverage/questions, without copying every
        // search trace and full receipt into a reply-only request again.
        if(sequence?["steps"] is JArray steps)
            foreach(var step in steps.OfType<JObject>()) {
                if(new[]{"completed","failed"}.Contains((string)step["status"])) {
                    step.Remove("receipt");
                    step["resultSource"]="delivery.results source=step:"+(string)step["id"];
                }
            }
        return new JObject {
        ["workResultOnly"] = true,
        ["attentionState"] = ReadAttentionState("work-result"),
        ["humanInput"] = BuildCommittedReplyHumanInput(generation),
        ["originalUserRequest"] = m_ActionConstraints.CurrentUserText,
        ["currentRequestSemantics"] = ReadMovementReplySemantics(generation),
        ["workEpoch"] = m_WorkEpoch, ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
        ["taskSequence"] = sequence,
        ["delivery"] = delivery,
        ["reportingNow"] = new JObject {
            ["resultIds"] = new JArray(((JArray)delivery["results"]).OfType<JObject>().Where(r=>
                (bool?)r["delivered"]!=true || (bool?)r["playbackReviewPending"]==true).Select(r=>r["id"].DeepClone())),
            ["purpose"] = "先自然告知这些项已经发生的真实结果。不是重新接受请求，也不是复述之前的开场或承诺。"+
                "可以保留感受和合理新愿望，但它们不代替结果报告；没有新执行回执时不得把愿望说成已经开始。"
        },
        ["currentExecution"] = BuildWorkResultExecution(),
        ["eventContext"] = BuildReplyEventContext(generation),
        ["pendingNotes"] = new JArray((CreateObservationReceipt()?.Facts??new JArray()).OfType<JObject>().Where(note=>
            !new[]{nameof(m_PendingSelfInspection),nameof(m_LastSongSearchResult),nameof(m_LastSongCatalogResult),
                nameof(m_LastSongMemoryResult),nameof(m_LastSpeakerManageResult),nameof(m_LastPracticeDropResult),
                nameof(m_LastPracticeEditResult),nameof(m_LastHumBackResult)}.Contains((string)note["kind"]))),
        ["resultSource"] = "Versioned delivery.results and taskSequence.answerEvidence; runtime history remains available outside this report-only projection.",
        ["recentPublicReplies"] = ReadRecentRoomReplies(),
        ["decisionFailure"] = ReadRoomTaskDecisionFailure(),
        ["previousReplyFailureReceipt"] = ReadPreviousReplyFailureReceipt(),
        ["newActionSubmitted"] = false,
        ["scope"] = "Deliver the still-owed result or diagnosis for the complete original request. " +
            "delivery identifies already heard versus still owed results. Do not repeat settled results or replay the earlier starting acknowledgement. " +
            "Describe completed events at their recorded time; an old receipt is not a fresh inspection of the current world. " +
            "Use each step's own receipt, identity and event time. Completed results remain completed; " +
            "a failed step is not pending and this reply neither retries nor promises another attempt. " +
            "Report the concrete remaining failure and any bounded recovery already attempted. " +
            "Preserve observedStateStatement's meaning in the dialogue language: an explicit runtime failure is established, not a guess or an unknown final result. " +
            "Deliver a pending inspection answer if present. Earlier accepted words are not proof of playback or execution. " +
            "No new perception, tool selection, or whole-scene description is requested by this result-only stage."
        };
    }

    private JObject BuildWorkResultExecution()
    {
        var result = BuildWorkRoomCompletionObservation();
        if (result != null)
            result["observedStateStatement"] = MovementProgressText(result["currentRoomFacts"] as JObject,
                (string)result["intent"], (string)result["actionId"], "zh", false);
        return ResultDeliveryContextPolicy.Execution(result);
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
