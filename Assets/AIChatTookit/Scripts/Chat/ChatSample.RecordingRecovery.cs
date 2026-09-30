using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // This budget is inside the existing work-chain budget, never a new allowance
    // for each proposal version. The owner already fences user turn/epoch/model.
    private const int MaximumRecordingReviewCorrections = 2;
    private sealed class RecordingReviewRecovery
    {
        public SingingGoal goal;
        public int version, baseVersion, attempts, preparedGeneration = -1, dispatchedGeneration = -1;
        public bool dispatched, reconsiderationConsumed;
        public JObject decision;
    }

    private static JObject SingingReviewDecisionFacts(AutonomyIntentDecision decision) => decision == null ? null : new JObject {
        ["status"] = decision.singing_goal_status,
        ["reason"] = decision.singing_goal_evidence,
        ["responseRoute"] = decision.response_route,
        ["expected"] = decision.singing_goal_expected?.DeepClone(),
        ["workStatus"] = decision.work_status,
        ["workReason"] = decision.work_evidence,
        ["proceed"] = decision.proceed
    };

    private void NormalizeAndRecordSingingGoalReview(AutonomyIntentDecision decision, ConversationTrace trace)
    {
        bool pending = HasPendingSingingGoalReview();
        JObject reported = pending ? SingingReviewDecisionFacts(decision) : null;
        NormalizeSingingGoalReview(decision);
        if (!pending) return;
        // A semantic take binding is a proposal, not an uncorrectable fact.
        // An independent rejection releases only that current binding. Existing
        // bounded recovery builds fresh candidate context and still needs review.
        if (decision.singing_goal_status == "revise" &&
            RejectSingingSessionBinding(m_SingingGoal.refs, decision.singing_goal_evidence))
        {
            m_RecordingCandidateReviewFacts = null;
            m_RecordingCandidateInputRevision = -1;
            if (HasCurrentRecordingDecisionRoute()) m_RecordingDecisionRoute.model.RecordingDecisionContext = null;
            trace?.Mark("singing_session.binding_rejected", new JObject {
                ["refs"] = m_SingingGoal.refs, ["goalVersion"] = m_SingingGoal.version,
                ["reason"] = decision.singing_goal_evidence, ["requiresNewIndependentReview"] = true,
                ["audioExecuted"] = false, ["userTaskCancelled"] = false });
        }
        var fact = new JObject {
            ["userTurn"] = m_SingingGoalTurn, ["workEpoch"] = m_WorkEpoch,
            ["goalVersion"] = m_SingingGoal.version, ["baseVersion"] = m_SingingGoal.baseVersion,
            ["reported"] = reported, ["validated"] = SingingReviewDecisionFacts(decision),
            ["proposal"] = SingingGoalObservation(m_SingingGoal),
            ["scope"] = "Independent review result and subsequent local validation. Expected is the reviewer's proposed interpretation, not a source fact, new role choice or execution permission."
        };
        trace?.Mark("goal.review_decision", fact);
        Debug.Log("[Sing/ReviewDecision] " + fact.ToString(Formatting.None));
        RetainSingingReviewRejection(fact);
        if (!HasCurrentRecordingDecisionRoute() || m_SingingGoal.turn != m_SingingGoalTurn ||
            m_SingingGoal.proposalWorkEpoch != m_WorkEpoch) return;
        var owner = m_RecordingDecisionRoute;
        int attempts = owner.reviewRecovery?.attempts ?? 0;
        owner.reviewRecovery = new RecordingReviewRecovery {
            goal = m_SingingGoal, version = m_SingingGoal.version, baseVersion = m_SingingGoal.baseVersion,
            attempts = attempts, decision = (JObject)fact.DeepClone()
        };
    }

    private bool CurrentRecordingReviewRecovery(out RecordingReviewRecovery recovery)
    {
        recovery = HasCurrentRecordingDecisionRoute() ? m_RecordingDecisionRoute.reviewRecovery : null;
        return recovery != null && ReferenceEquals(recovery.goal, m_SingingGoal) &&
            recovery.version == m_SingingGoal.version &&
            (m_SingingGoal.review == "approved" ? ReferenceEquals(m_ActiveSingingGoal, m_SingingGoal) :
                recovery.baseVersion == (m_ActiveSingingGoal?.version ?? 0)) &&
            m_SingingGoal.turn == m_SingingGoalTurn && m_SingingGoal.proposalWorkEpoch == m_WorkEpoch &&
            m_SingingGoal.review == (string)recovery.decision?["validated"]?["status"] &&
            (m_SingingGoal.review == "revise" || m_SingingGoal.review == "approved" || m_SingingGoal.review == "waiting_user") &&
            m_SingingGoal.outcome != "cancelled" && m_SingingGoal.outcome != "playback_complete_content_unverified" &&
            m_SingingGoal.outcome != "user_confirmed" && !m_WorkNoProgress;
    }

    private bool PrepareReviewedRecordingFeedback(AutonomyIntentDecision decision, bool continuation,
        bool hasFeedback, int generation)
    {
        m_ReviewedRecordingFeedbackGeneration = -1;
        if (continuation && hasFeedback && decision?.proceed == true &&
            CurrentRecordingReviewRecovery(out var recovery))
        {
            if (recovery.attempts >= MaximumRecordingReviewCorrections)
            {
                m_ResponseTrace?.Mark("recording_decision.review_recovery_exhausted", new JObject {
                    ["goalVersion"] = recovery.version, ["attempts"] = recovery.attempts,
                    ["maximumAttempts"] = MaximumRecordingReviewCorrections, ["audioSubmitted"] = false });
                StopWorkWithoutProgress("本轮录唱目标已用尽独立审核后的两次结构化修正；保留原始请求、素材和审核分歧，没有转交一般标签路径或播放。");
                m_AgentRoundInFlight = m_AgentCurrentRoundIsTick = m_FormalResponseInFlight = false;
                return false;
            }
            // Domain ownership is a local fact. A review's legacy/general hint
            // cannot dissolve this same recording proposal into a General leaf.
            recovery.preparedGeneration = generation;
            recovery.dispatched = false;
            return true;
        }
        m_ReviewedRecordingFeedbackGeneration = continuation && hasFeedback &&
            decision?.response_route == "recording_singing" && !IsActiveSingingGoalUnfinished()
            ? generation : -1;
        return true;
    }

    private bool ConsumeRecordingReviewRecovery(int generation, bool sceneGuarded)
    {
        if (sceneGuarded || !CurrentRecordingReviewRecovery(out var recovery) ||
            recovery.preparedGeneration != generation || recovery.dispatched ||
            generation != m_FormalResponseGeneration || HasWorkToolInFlight()) return false;
        recovery.dispatched = true;
        recovery.dispatchedGeneration = generation;
        recovery.attempts++;
        m_ResponseTrace?.Mark("recording_decision.review_recovery", new JObject {
            ["generation"] = generation, ["goalVersion"] = recovery.version,
            ["baseVersion"] = recovery.baseVersion, ["attempt"] = recovery.attempts,
            ["maximumAttempts"] = MaximumRecordingReviewCorrections,
            ["reviewReportedRoute"] = recovery.decision?["reported"]?["responseRoute"],
            ["route"] = "recording_singing", ["approved"] = false });
        return true;
    }

    private JObject BuildRecordingReviewRecoveryFacts()
    {
        if (!CurrentRecordingReviewRecovery(out var recovery)) return null;
        var fact = (JObject)recovery.decision.DeepClone();
        fact["correctionAttempt"] = recovery.attempts;
        fact["maximumCorrections"] = MaximumRecordingReviewCorrections;
        fact["prior_rejections"] = BuildSingingRejectionHistoryFacts();
        return fact;
    }

    private bool ConsumeRecordingReviewReconsideration()
    {
        // A new decision/version alone is not new evidence. Only changed retained
        // source facts can justify reconsidering the identical rejected target.
        if (!CurrentRecordingReviewRecovery(out var recovery) || m_SingingGoal.review != "revise" ||
            !recovery.dispatched || recovery.dispatchedGeneration != m_FormalResponseGeneration ||
            recovery.reconsiderationConsumed || !HasChangedRejectedSingingSourceEvidence()) return false;
        recovery.reconsiderationConsumed = true;
        m_ResponseTrace?.Mark("recording_decision.review_reconsideration", new JObject {
            ["priorGoalVersion"] = recovery.version, ["attempt"] = recovery.attempts,
            ["sameTarget"] = true, ["requiresNewIndependentReview"] = true });
        return true;
    }
}
