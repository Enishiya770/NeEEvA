using System;
using System.Linq;

public partial class ChatSample
{
    // A semantic session cancellation revokes only the matching singing work.
    // Keep PCM, completed-playback evidence, movement and the current Fast owner.
    private void ApplySingingSessionTaskCancellation(string deletedClip)
    {
        bool Matches(string refs) => deletedClip == null ||
            (refs ?? "").Split(',').Any(s => string.Equals(s.Trim(), deletedClip, StringComparison.Ordinal));
        bool MatchesGoal(SingingGoal goal) => goal != null && Matches(goal.refs);

        if (m_DirectSingingReview != null && MatchesGoal(m_DirectSingingReview.goal))
            CancelDirectSingingGoalReview("singing-session-cancelled");
        if (m_DeferredSingingRequest != null && Matches(m_DeferredSingingRequest.request.ClipRefs))
            ClearDeferredSingingRequest();
        if (deletedClip == null || m_PendingSingingGoalPlayback != null && Matches(m_PendingSingingGoalPlayback.goalRefs))
            CancelPendingHumBack("singing-session-cancelled", true);
        foreach (var goal in new[] { m_SingingGoal, m_ActiveSingingGoal, m_PreviousSingingGoal })
        {
            if (!MatchesGoal(goal) || goal.outcome == "playback_complete_content_unverified" || goal.outcome == "user_confirmed") continue;
            goal.review = "cancelled";
            goal.outcome = "cancelled";
            goal.reviewEvidence = deletedClip == null ? "当前用户已取消歌唱任务。" : "目标演唱段已从本次歌唱候选中移除。";
        }
        if (m_RecordingDecisionRoute?.reviewRecovery != null && MatchesGoal(m_RecordingDecisionRoute.reviewRecovery.goal))
            m_RecordingDecisionRoute.reviewRecovery = null;
        if (MatchesGoal(m_SingingGoal))
        {
            m_ValidatedSingingRendering = "";
            m_ValidatedSingingMaterials = null;
        }
        m_RecordingCandidateReviewFacts = null;
        m_RecordingCandidateInputRevision = -1;
    }
}
