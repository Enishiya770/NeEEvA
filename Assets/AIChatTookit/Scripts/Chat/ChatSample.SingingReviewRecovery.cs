using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private const int MaxSingingReviewUnavailableAttempts = 2;
    private SingingGoal m_SingingUnavailableGoal;
    private int m_SingingUnavailableEpoch = -1, m_SingingUnavailableVersion = -1;
    private int m_SingingUnavailableAttempts, m_SingingUnavailableProbeGeneration = -1;
    // Observation only. Unlike the live retry owner this survives a new input,
    // so a later explanation can name the actual failed stage without inventing
    // missing user permission or a voice-conversion service failure.
    private JObject m_LastSingingReviewFailureFact;

    private JObject BuildSingingReviewFailureFact()
    {
        if (m_LastSingingReviewFailureFact == null) return null;
        var fact = (JObject)m_LastSingingReviewFailureFact.DeepClone();
        fact["appliesToCurrentProposal"] = m_SingingGoal != null &&
            (int?)fact["workEpoch"] == m_WorkEpoch && (int?)fact["goalVersion"] == m_SingingGoal.version &&
            (int?)fact["goalTurn"] == m_SingingGoal.turn;
        if (!(bool)fact["appliesToCurrentProposal"] && (string)fact["status"] == "retry_scheduled")
            fact["status"] = "retry_no_longer_owned";
        return fact;
    }

    // A transport/schema failure is not a semantic rejection of the user's goal.
    // Keep its evidence pending, but do not let a default tick bypass this budget.
    private bool SingingReviewRetryExhausted => HasPendingSingingGoalReview() &&
        ReferenceEquals(m_SingingUnavailableGoal, m_SingingGoal) &&
        m_SingingUnavailableEpoch == m_WorkEpoch && m_SingingUnavailableVersion == m_SingingGoal.version &&
        m_SingingUnavailableAttempts >= MaxSingingReviewUnavailableAttempts;

    private bool TryHandleSingingReviewUnavailable(SingingGoal goal, int version, int epoch,
        int probeGeneration, ConversationTrace trace)
    {
        if (goal == null || !ReferenceEquals(goal, m_SingingGoal) || goal.version != version ||
            goal.review != "pending" || epoch != m_WorkEpoch || probeGeneration != m_AutonomyProbeGeneration)
            return false;
        if (!ReferenceEquals(m_SingingUnavailableGoal, goal) || m_SingingUnavailableEpoch != epoch ||
            m_SingingUnavailableVersion != version)
        {
            m_SingingUnavailableGoal = goal;
            m_SingingUnavailableEpoch = epoch;
            m_SingingUnavailableVersion = version;
            m_SingingUnavailableAttempts = 0;
            m_SingingUnavailableProbeGeneration = -1;
        }
        // A duplicated provider callback cannot consume another attempt or publish
        // the same status twice. The request owner also fences its callback.
        if (m_SingingUnavailableProbeGeneration == probeGeneration || SingingReviewRetryExhausted) return true;
        m_SingingUnavailableProbeGeneration = probeGeneration;
        m_SingingUnavailableAttempts++;
        m_WorkStatus = "review_unavailable";
        bool exhausted = SingingReviewRetryExhausted;
        m_LastSingingReviewFailureFact = new JObject {
            ["goalTurn"] = goal.turn, ["goalVersion"] = version, ["workEpoch"] = epoch,
            ["stage"] = "goal_review", ["status"] = exhausted ? "automatic_retry_stopped" : "retry_scheduled",
            ["failure"] = "review_result_invalid_or_unavailable", ["attempts"] = m_SingingUnavailableAttempts,
            ["audioSubmittedAtFailure"] = false,
            ["scope"] = "Historical runtime observation only. The independent review was unavailable; conversion/playback was not submitted by this proposal. This is not waiting for user permission, not a source approval, and not proof that the voice conversion service failed. It grants no action authority and does not block a new user request." };
        trace?.Mark("goal.review_recovery", new JObject {
            ["workEpoch"] = epoch, ["goalVersion"] = version,
            ["attempt"] = m_SingingUnavailableAttempts, ["maximumAttempts"] = MaxSingingReviewUnavailableAttempts,
            ["retryScheduled"] = !exhausted, ["exhausted"] = exhausted,
            ["audioSubmitted"] = false, ["presentation"] = "system-notice-not-role-speech" });
        if (exhausted)
        {
            ClearDeferredSingingRequest();
            ClearScheduledAgentWake();
            ClearUserTurnAwaitingReply("singing-review-unavailable");
        }
        else ScheduleNextTick(2f, "singing-goal-review-retry");

        // ErrorsOnly is the default presentation mode. Show the actual system
        // stage at the first failure; never ask the user to rephrase a code fault.
        HandleSystemNotice(new SystemNotice(exhausted ? "singing_review_unavailable" : "singing_review_retrying",
            SystemNoticeSeverity.Error,
            exhausted ? "歌唱目标审核暂时不可用；本次未提交转换或播放，自动重试已停止。" :
                "歌唱目标审核暂时不可用，正在重试一次；尚未提交转换或播放。",
            "原目标和录音素材仍保留；没有将事项标为完成。", "Singing", false));
        return true;
    }

    private void ClearSingingReviewFailureBudget(SingingGoal goal, int epoch)
    {
        if (!ReferenceEquals(m_SingingUnavailableGoal, goal) || m_SingingUnavailableEpoch != epoch) return;
        if (m_LastSingingReviewFailureFact != null && (int?)m_LastSingingReviewFailureFact["workEpoch"] == epoch &&
            (int?)m_LastSingingReviewFailureFact["goalVersion"] == goal?.version)
            m_LastSingingReviewFailureFact["status"] = "recovered_by_valid_review";
        m_SingingUnavailableGoal = null;
        m_SingingUnavailableAttempts = 0;
        m_SingingUnavailableProbeGeneration = -1;
    }
}
