using System;
using System.Collections;
using Newtonsoft.Json;
using UnityEngine;

public partial class ChatSample
{
    private sealed class SingingPolicyReviewTicket
    {
        public int serial, workEpoch, goalTurn;
        public string userText, candidate;
        public SkillAccess access;
        public float policyChangedAt;
        public LLM model;
        public bool settled;
    }

    private int m_SingingPolicyReviewSerial;
    private int m_SingingPolicyReviewAttemptEpoch = -1;
    private string m_SingingPolicyReviewAttemptCandidate = "", m_SingingPolicyReviewLastFact = "";
    private SingingPolicyReviewTicket m_PendingSingingPolicyReview;
    private int m_SingingPolicyDeferredSpeechRound = -1;

    private bool HasPendingSingingPolicyReview() => m_PendingSingingPolicyReview != null &&
        !m_PendingSingingPolicyReview.settled && IsSingingPolicyTicketCurrent(m_PendingSingingPolicyReview);
    private bool IsSingingPolicyActionDeferredThisRound() => m_SingingPolicyDeferredSpeechRound == m_CurrentSpeechRoundId;

    private bool IsSingingPolicyTicketCurrent(SingingPolicyReviewTicket ticket)
    {
        if (ticket == null || ticket.serial != m_SingingPolicyReviewSerial || ticket.workEpoch != m_WorkEpoch ||
            ticket.goalTurn != m_SingingGoalTurn || !isActiveAndEnabled || m_AgentGracefulShutdownPending ||
            m_UserSpeechActiveForAutonomy || !string.Equals(ticket.userText, StripSingingPerceptionMetadata(m_LastUserMsg).Trim(), StringComparison.Ordinal) ||
            m_ChatSettings == null || !ReferenceEquals(m_ChatSettings.m_ChatModel, ticket.model)) return false;
        SkillRouteState state = GetSkillRouteState("singing");
        return state.Access == ticket.access && state.LastPolicyChangedAt == ticket.policyChangedAt;
    }

    /// <summary>Called at real user/loop boundaries; cancelling a review never changes a permission or audio.</summary>
    private void ResetSingingPolicyReview(string reason)
    {
        ++m_SingingPolicyReviewSerial;
        var ticket = m_PendingSingingPolicyReview;
        m_PendingSingingPolicyReview = null;
        if (ticket == null || ticket.settled) return;
        ticket.settled = true;
        ticket.model?.CancelSingingPolicyReview();
        if (m_LogAgentLoop) Debug.Log("[Singing/PolicyReview] discarded reason=" + reason);
    }

    private bool QueueSingingPolicyReview(AgentSingingPolicyRequest proposal, out string result)
    {
        result = "";
        // A non-null proposal cannot authorize its own accompanying claim, including malformed or stale proposals.
        // Do this before every validation exit so a rejected "已关闭" never becomes a delivered fact.
        if (proposal != null)
        {
            m_SingingPolicyDeferredSpeechRound = m_CurrentSpeechRoundId;
            m_SingingRejectedSpeechRound = m_CurrentSpeechRoundId;
        }
        if (proposal == null || !ValidateSingingPolicyFields(proposal.Value, out result)) return false;
        if (m_AgentCurrentRoundIsTick || m_UserSpeechActiveForAutonomy || m_AgentGracefulShutdownPending || !isActiveAndEnabled)
        { result = "当前不是可核验的真实用户权限请求；没有改变权限。"; return false; }
        var model = m_ChatSettings?.m_ChatModel;
        string userText = StripSingingPerceptionMetadata(m_LastUserMsg).Trim();
        if (model == null || string.IsNullOrWhiteSpace(userText))
        { result = "缺少当前用户原话或核验模型；没有改变权限。"; return false; }
        if (m_SingingPolicyReviewAttemptEpoch == m_WorkEpoch)
        {
            result = HasPendingSingingPolicyReview() ? "本用户轮的权限提案正在独立核验；没有改变权限。" : m_SingingPolicyReviewLastFact;
            if (proposal.Value != m_SingingPolicyReviewAttemptCandidate)
            { result = "本用户轮已经提交另一权限值；不通过反复改值获取授权，当前权限保持。"; return false; }
            return true; // Idempotent proposal status, not another grant or model request.
        }

        ResetSingingPolicyReview("new-policy-proposal");
        SkillRouteState state = GetSkillRouteState("singing");
        var ticket = new SingingPolicyReviewTicket {
            serial = m_SingingPolicyReviewSerial, workEpoch = m_WorkEpoch, goalTurn = m_SingingGoalTurn,
            userText = userText, candidate = proposal.Value, access = state.Access,
            policyChangedAt = state.LastPolicyChangedAt, model = model
        };
        // Establish every owner and fact before invoking a provider: a provider may complete synchronously.
        m_PendingSingingPolicyReview = ticket;
        m_SingingPolicyReviewAttemptEpoch = m_WorkEpoch;
        m_SingingPolicyReviewAttemptCandidate = proposal.Value;
        m_SingingPolicyReviewLastFact = "权限提案待独立核验；权限尚未改变，不能声称已禁用/恢复/限制自主歌唱。";
        result = m_SingingPolicyReviewLastFact;
        PublishSingingPolicyReviewFact("pending", ticket.candidate, false, "", "", false);
        var observation = new SingingPolicyReviewRequest {
            UserText = userText, Candidate = ticket.candidate, CurrentAccess = state.Access.ToString(),
            ReferencedSkill = ResolveRecentSkillReference(m_LastReferencedSkillName, m_LastReferencedSkillAt,
                Time.realtimeSinceStartup, m_SkillReferenceWindowSeconds)
        };
        try
        {
            model.PostSingingPolicyReview(observation, review => CompleteSingingPolicyReview(ticket, review));
            if (!ticket.settled) StartCoroutine(SingingPolicyReviewDeadline(ticket));
        }
        catch (Exception)
        { CompleteSingingPolicyReview(ticket, SingingPolicyReviewResult.Failed("policy-review-dispatch-failed")); }
        return true;
    }

    private IEnumerator SingingPolicyReviewDeadline(SingingPolicyReviewTicket ticket)
    {
        float deadline = Time.realtimeSinceStartup + 15f;
        while (!ticket.settled && IsSingingPolicyTicketCurrent(ticket) && Time.realtimeSinceStartup < deadline)
            yield return null;
        if (ticket.settled) yield break;
        bool current = IsSingingPolicyTicketCurrent(ticket);
        CompleteSingingPolicyReview(ticket, SingingPolicyReviewResult.Failed(current ? "policy-review-timeout" : "policy-review-superseded"));
        if (current) ticket.model?.CancelSingingPolicyReview();
    }

    private void CompleteSingingPolicyReview(SingingPolicyReviewTicket ticket, SingingPolicyReviewResult review)
    {
        if (ticket == null || ticket.settled) return;
        bool current = ReferenceEquals(m_PendingSingingPolicyReview, ticket) && IsSingingPolicyTicketCurrent(ticket);
        ticket.settled = true;
        if (ReferenceEquals(m_PendingSingingPolicyReview, ticket)) m_PendingSingingPolicyReview = null;
        if (!current)
        {
            if (m_LogAgentLoop) Debug.Log("[Singing/PolicyReview] stale result discarded; policy unchanged");
            return;
        }
        bool valid = review != null && review.Valid && review.FinishReason == "stop" && review.Candidate == ticket.candidate &&
            !string.IsNullOrWhiteSpace(review.Evidence) && review.Evidence.Length <= 256;
        bool grant = valid && review.Granted && !string.IsNullOrWhiteSpace(review.UserQuote) && review.UserQuote.Length <= 512 &&
            ticket.userText.IndexOf(review.UserQuote, StringComparison.Ordinal) >= 0;
        string outcome = grant ? "applied" : valid && !review.Granted ? "not-granted" : "review-failed";
        string detail = grant ? CommitReviewedSingingPolicy(ticket) : valid && !review.Granted
            ? "核验未确认当前用户要求该权限变更，权限保持原状；不能声称已禁用、恢复或限制自主歌唱。"
            : "未获得有效的权限核验结果，权限保持原状；这不能证明用户同意或不同意，也不能声称已经执行。";
        m_SingingPolicyReviewLastFact = detail;
        PublishSingingPolicyReviewFact(outcome, ticket.candidate, grant, review?.UserQuote ?? "",
            valid ? review.Evidence : review?.Failure ?? "invalid-review", true);
    }

    private string CommitReviewedSingingPolicy(SingingPolicyReviewTicket ticket)
    {
        SkillRouteDefinition definition = FindSkillDefinition("singing");
        SkillRouteState state = GetSkillRouteState("singing");
        state.LastPolicyUserText = ticket.userText;
        state.LastPolicyChangedAt = Time.realtimeSinceStartup;
        string result;
        if (ticket.candidate == "enable")
        {
            state.Access = SkillAccess.Available; state.SoftSuppressedUntil = -999f;
            state.RoundsRemaining = definition?.FollowupRounds ?? 1;
            state.LastSignalAt = Time.realtimeSinceStartup;
            state.ActiveThisRound = m_ActiveSkillsThisRound.Contains("singing");
            result = "独立核验当前用户要求后，歌唱权限已恢复；没有自动续播旧动作。";
        }
        else if (ticket.candidate == "disable")
        {
            state.Access = SkillAccess.UserDisabled; state.RoundsRemaining = 0;
            state.ActiveThisRound = state.WasActive = false;
            m_ActiveSkillsThisRound.Remove("singing");
            CancelSkillRuntimeActivity("singing", "singing-policy-reviewed-disable");
            result = "独立核验当前用户要求后，歌唱权限已禁用；录音、曲库和未完成目标均未删除。";
        }
        else
        {
            state.Access = SkillAccess.SoftSuppressed;
            state.SoftSuppressedUntil = Time.realtimeSinceStartup + (definition?.SoftSuppressSeconds ?? 180f);
            state.RoundsRemaining = 0;
            result = "独立核验当前用户要求后，暂不自主发起歌唱；用户明确点歌仍允许，现有播放未因此中断。";
        }
        m_LastReferencedSkillName = "singing"; m_LastReferencedSkillAt = Time.realtimeSinceStartup;
        m_ChatSettings.m_ChatModel.SkillCatalogContext = BuildSkillCatalogContext();
        return result;
    }

    private void PublishSingingPolicyReviewFact(string outcome, string candidate, bool applied, string quote, string evidence, bool requestReview)
    {
        string fact = JsonConvert.SerializeObject(new {
            source = "independent-singing-policy-review", userTurn = m_WorkEpoch, outcome, candidate, applied,
            currentAccess = GetSkillRouteState("singing").Access.ToString(), userQuote = quote, evidence,
            scope = "Only this permission-review operation's observed result. This operation does not submit a song, release an old promise, or cancel a persistent singing target. Other singing operations and playback are described by their own execution facts."
        });
        m_PendingSkillStatusFrame += "\n[Singing/PolicyResult] " + fact + "\n";
        if (m_LogAgentLoop) Debug.Log("[Singing/PolicyReview] " + fact);
        if (requestReview) ScheduleNextTick(1f, "singing-policy-result");
    }

    /// <summary>Planning budget, durable goal and user permission have independent lifetimes.</summary>
    private void UpdateSingingPlanningLeaseAfterDecision(bool wantsContinue)
    {
        if (wantsContinue)
        {
            if (m_ActiveSkillsThisRound.Contains("singing"))
                AcquireSkillExecutionLease("singing", "continue-chain 尚未完成歌唱相关决定");
            return;
        }
        if (IsSingingRuntimeActivityActive()) return;
        bool unfinished = IsActiveSingingGoalUnfinished() || IsSingingGoalAwaitingExecution();
        bool pending = HasPendingSingingPolicyReview() || HasPendingSingingGoalReview();
        bool rejected = m_SingingRejectedSpeechRound == m_CurrentSpeechRoundId;
        if ((unfinished || pending || rejected) && !m_WorkNoProgress)
        {
            // Keep the remaining lease; never refresh its rounds/expiry merely because a task remains unfinished.
            if (m_LogAgentLoop && m_SkillExecutionLeases.ContainsKey("singing"))
                Debug.Log($"[LLM技能/Lease] singing 规划暂停，保留剩余租约；unfinished_goal={unfinished} pending_review={pending} rejected={rejected}；没有改变权限或取消目标");
            return;
        }
        ReleaseSkillExecutionLease("singing", unfinished || pending || rejected
            ? "规划暂停或达到恢复上限；权限和持久目标未改变"
            : "本轮没有待执行歌唱或待审核提案；释放临时规划资源，权限不变");
    }
}
