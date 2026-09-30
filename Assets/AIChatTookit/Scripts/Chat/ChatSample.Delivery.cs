using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    // Reset per streaming generation; only successful formal PCM/WAV completion increments.
    // In particular, CommitPlayedLatencyFiller must not modify this counter.
    private int m_FormalCompletedCharacters;
    private ConversationTrace m_DeliveryTrace;
    private ConversationTrace m_GoalReviewTrace;

    private void ObserveDeliveryEnd(string path, bool intentionalQuiet, bool formalPlayed)
    {
        bool pending = HasPendingSingingGoalReview() || HasPendingSingingPolicyReview() ||
            IsSingingPolicyActionDeferredThisRound();
        bool rejected = CurrentMotionResponseRejected || m_SingingRejectedSpeechRound == m_CurrentSpeechRoundId ||
            m_ToolCorrectionExhaustedThisRound;
        var outcome = DeliveryDispositionRules.Classify(formalPlayed, pending, rejected,
            m_SingingSpeechWithheld, intentionalQuiet, m_RoundSilencedForRepeat);
        var trace = m_DeliveryTrace ?? m_ResponseTrace?.Child("delivery-end");
        trace?.Mark("delivery.round_closed", new JObject {
            ["path"] = path, ["outcome"] = outcome.ToString(), ["formalPlaybackCompleted"] = formalPlayed,
            ["awaitingValidation"] = pending, ["withheld"] = m_SingingSpeechWithheld,
            ["rejected"] = rejected, ["formalCompletedCharacters"] = m_FormalCompletedCharacters,
            ["releasesWait"] = DeliveryDispositionRules.ReleasesWait(outcome),
            ["scope"] = "runtime delivery status; not physical audibility or task completion" });
        if (DeliveryDispositionRules.ReleasesWait(outcome))
            ClearUserTurnAwaitingReply("delivery-" + outcome.ToString().ToLowerInvariant());
    }

    private void TraceSpeechGate(string outcome, string reason)
    {
        // Per-response span; bounded to the first observation of each gate reason.
        (m_DeliveryTrace ?? m_ResponseTrace)?.Mark("speech." + outcome + "." + reason,
            new JObject { ["speechRound"] = m_CurrentSpeechRoundId, ["reason"] = reason,
                ["scope"] = "speech gating, not synthesis or playback" });
    }

    private void EndGoalReviewTrace(ConversationTrace trace, string outcome, JObject fields = null)
    {
        fields = fields ?? new JObject();
        fields["outcome"] = outcome;
        trace?.Mark("goal.review_end", fields);
        if (ReferenceEquals(m_GoalReviewTrace, trace)) m_GoalReviewTrace = null;
    }
}
