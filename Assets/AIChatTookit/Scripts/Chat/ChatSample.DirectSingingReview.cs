using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // A user-requested action owns this one review. Turning off autonomous ticks
    // does not remove its validation, and a pending goal alone cannot create work.
    private sealed class DirectSingingReviewOwner
    {
        public SingingGoal goal;
        public DeferredSingingRequest request;
        public LLM model;
        public int turn, epoch, generation, speechRound, version, probeGeneration;
        public string materialVersions;
        public bool sent, approved;
        public float deadline;
        public ConversationTrace trace;
    }

    private DirectSingingReviewOwner m_DirectSingingReview;
    private SingingGoal m_LastDirectSingingReviewGoal;
    private Coroutine m_DirectSingingReviewCo;
    private const float DirectSingingReviewIdleTimeout = 10f;
    private const float DirectSingingReviewReplyTimeout = 60f;

    // PTT has no realtime VAD state transition. Its successful microphone start
    // still revokes the previous explicit action before its next ASR result exists.
    public void NotifyManualCaptureStarted()
    {
        BeginSceneExplorationManualInput();
        CancelDirectSingingGoalReview("manual-capture-started");
        if (IsAISpeaking || IsVoiceOutputPlaying) Interrupt();
        else CancelUnheardResponseForUserSpeech();
        if (IsSingingRuntimeActivityActive()) CancelPendingHumBack("manual-capture-started", true);
    }

    private bool TryBeginDirectSingingGoalReview()
    {
        if (m_AgentRunning || m_AgentGracefulShutdownPending || !isActiveAndEnabled ||
            m_UserSpeechActiveForAutonomy || m_DirectSingingReview != null ||
            m_AutonomyProbeInFlight || !HasPendingSingingGoalReview() ||
            m_SingingGoal.origin != "user_request" || m_SingingGoal.autonomous ||
            m_SingingGoal.intent == "observe" || m_SingingGoal.transition == "cancel" ||
            m_DeferredSingingRequest == null || m_ChatSettings?.m_ChatModel == null ||
            ReferenceEquals(m_LastDirectSingingReviewGoal, m_SingingGoal)) return false;

        var owner = new DirectSingingReviewOwner {
            goal = m_SingingGoal, request = m_DeferredSingingRequest,
            model = m_ChatSettings.m_ChatModel, turn = m_SingingGoalTurn,
            epoch = m_WorkEpoch, generation = m_FormalResponseGeneration,
            speechRound = m_CurrentSpeechRoundId, version = m_SingingGoal.version,
            materialVersions = SingingMaterialVersionKey()
        };
        m_DirectSingingReview = owner;
        if (!DirectSingingReviewOwnerCurrent(owner, true))
        { m_DirectSingingReview = null; return false; }
        m_LastDirectSingingReviewGoal = owner.goal;
        owner.trace = m_ResponseTrace?.Child("singing-goal-review");
        owner.trace?.Mark("goal.review_begin", new JObject {
            ["generation"] = owner.generation, ["workEpoch"] = owner.epoch,
            ["goalVersion"] = owner.version, ["trigger"] = "direct-user-singing" });
        Debug.Log($"[Sing/DirectReview] scheduled version={owner.version}; agent=false; explicit_request=true");
        m_DirectSingingReviewCo = StartCoroutine(DirectSingingGoalReviewCo(owner));
        return true;
    }

    private bool DirectSingingReviewOwnerCurrent(DirectSingingReviewOwner owner, bool pending)
    {
        if (owner == null || !ReferenceEquals(owner, m_DirectSingingReview) ||
            m_AgentRunning || m_AgentGracefulShutdownPending || !isActiveAndEnabled ||
            m_UserSpeechActiveForAutonomy || !ReferenceEquals(owner.goal, m_SingingGoal) ||
            owner.turn != m_SingingGoalTurn || owner.epoch != m_WorkEpoch ||
            owner.generation != m_FormalResponseGeneration || owner.speechRound != m_CurrentSpeechRoundId ||
            owner.version != m_SingingGoal.version ||
            !ReferenceEquals(owner.model, m_ChatSettings?.m_ChatModel) ||
            (owner.sent && owner.probeGeneration != m_AutonomyProbeGeneration) ||
            owner.materialVersions != SingingMaterialVersionKey()) return false;
        if (pending && (m_SingingGoal.review != "pending" ||
            !ReferenceEquals(owner.request, m_DeferredSingingRequest))) return false;
        var request = owner.request;
        return request != null && request.userTurn == owner.turn && request.workEpoch == owner.epoch &&
            request.formalGeneration == owner.generation && request.goalVersion == owner.version &&
            ReferenceEquals(request.model, owner.model);
    }

    private bool DirectSingingReviewOutputIdle() => !m_FormalResponseInFlight &&
        !m_AgentRoundInFlight && !IsAISpeaking && !IsVoiceOutputPlaying && m_StreamComplete &&
        m_PendingChunks.Count == 0 && m_PendingClips.Count == 0 && m_TTSSenderDone;

    private IEnumerator DirectSingingGoalReviewCo(DirectSingingReviewOwner owner)
    {
        // Leave the formal provider's callback before using its ephemeral transport.
        yield return null;
        float deadline = Time.realtimeSinceStartup + DirectSingingReviewIdleTimeout;
        while (DirectSingingReviewOwnerCurrent(owner, true) && !DirectSingingReviewOutputIdle() &&
            Time.realtimeSinceStartup < deadline) yield return null;
        if (!DirectSingingReviewOwnerCurrent(owner, true))
        { FinishDirectSingingReview(owner, "owner_changed"); yield break; }
        if (!DirectSingingReviewOutputIdle())
        { FailDirectSingingReview(owner, "output_busy", "歌唱审核没有开始：当前回复尚未结束。此次没有生成或播放歌声。"); yield break; }

        owner.probeGeneration = ++m_AutonomyProbeGeneration;
        owner.sent = true;
        m_AutonomyProbeInFlight = true;
        owner.model.SkillCatalogContext = BuildSkillCatalogContext();
        owner.model.SetActiveSkills();
        string prompt = BuildDedicatedWorkReviewPrompt(BuildPerceptionFrame("direct-user-singing"));
        owner.trace?.Mark("goal.review_dispatch", new JObject { ["image"] = false });
        try
        {
            owner.model.PostWorkReviewMsg(prompt, null,
                response => CompleteDirectSingingGoalReview(owner, response), owner.trace);
        }
        catch (Exception error)
        {
            Debug.LogException(error, this);
            FailDirectSingingReview(owner, "request_failed", "歌唱目标审核未完成，此次没有生成或播放歌声。");
        }
        owner.deadline = Time.realtimeSinceStartup + DirectSingingReviewReplyTimeout;
        while (ReferenceEquals(owner, m_DirectSingingReview) &&
            DirectSingingReviewOwnerCurrent(owner, true) && Time.realtimeSinceStartup < owner.deadline)
            yield return null;
        if (!ReferenceEquals(owner, m_DirectSingingReview)) yield break;
        if (!DirectSingingReviewOwnerCurrent(owner, true))
        { CancelDirectSingingGoalReview("owner_changed"); yield break; }
        FailDirectSingingReview(owner, "review_timeout", "歌唱目标审核超时，此次没有生成或播放歌声。");
    }

    private void CompleteDirectSingingGoalReview(DirectSingingReviewOwner owner, string response)
    {
        if (!DirectSingingReviewOwnerCurrent(owner, true))
        { FinishDirectSingingReview(owner, "owner_changed"); return; }
        if (!DirectSingingReviewOutputIdle())
        { FailDirectSingingReview(owner, "output_changed", "回复状态已改变，此次歌唱审核结果未执行。"); return; }
        owner.trace?.Mark("goal.review_callback");
        if (!TryParseAutonomyIntentDecision(response, out AutonomyIntentDecision decision) ||
            !IsWorkReviewDecision(decision))
        { FailDirectSingingReview(owner, "invalid_or_unavailable_decision", "歌唱目标审核不可用，此次没有生成或播放歌声。"); return; }

        NormalizeAndRecordSingingGoalReview(decision, owner.trace);
        ApplySingingGoalReview(decision.singing_goal_status, decision.singing_goal_evidence);
        owner.approved = m_SingingGoal.review == "approved" && m_SingingGoal.targetReviewed;
        bool took = TryTakeReviewedSingingRequestCore(out AgentHumBackRequest request, owner);
        owner.trace?.Mark("goal.execution_request", new JObject {
            ["outcome"] = took ? "resumed_for_validation" : "not_resumable",
            ["goalStatus"] = m_SingingGoal.review });
        FinishDirectSingingReview(owner, "decision_processed");
        if (!took)
        {
            ClearUserTurnAwaitingReply("singing-review-not-approved");
            HandleSystemNotice(new SystemNotice("singing_review_not_approved", SystemNoticeSeverity.Warning,
                "此次歌唱尚未执行。", m_SingingGoal.reviewEvidence, "Singing", false));
            return;
        }
        // Taking a reviewed request does not authorize its material or renderer.
        // This is the same resolver and execution entry used by Agent mode.
        var notices = m_SubtitleOverlay?.NoticeHistory;
        var priorNotice = notices != null && notices.Count > 0 ? notices[notices.Count - 1] : null;
        string priorResult = m_LastHumBackResult, priorFailure = m_StickyToolFailure;
        QueueReviewedSingingRequest(request, owner.trace);
        if (!IsSingingRuntimeActivityActive())
        {
            ClearUserTurnAwaitingReply("singing-preflight-ended");
            notices = m_SubtitleOverlay?.NoticeHistory;
            var lastNotice = notices != null && notices.Count > 0 ? notices[notices.Count - 1] : null;
            if (ReferenceEquals(priorNotice, lastNotice))
            {
                string result = !string.IsNullOrWhiteSpace(m_LastHumBackResult) && m_LastHumBackResult != priorResult
                    ? m_LastHumBackResult : !string.IsNullOrWhiteSpace(m_StickyToolFailure) && m_StickyToolFailure != priorFailure
                        ? m_StickyToolFailure : "此次没有开始演唱：当前素材或音频执行条件未通过核验。";
                HandleSystemNotice(new SystemNotice("singing_execution_not_started", SystemNoticeSeverity.Error,
                    result, "没有播放歌声，未自动重试或更换素材。", "Singing", false));
            }
        }
        Debug.Log($"[Sing/DirectReview] validated handoff version={owner.version}; " +
            $"runtime_active={IsSingingRuntimeActivityActive()}; agent=false");
    }

    private void FailDirectSingingReview(DirectSingingReviewOwner owner, string reason, string notice)
    {
        if (!ReferenceEquals(owner, m_DirectSingingReview)) return;
        bool ownsRequest = owner.sent && owner.probeGeneration == m_AutonomyProbeGeneration;
        FinishDirectSingingReview(owner, reason);
        if (ownsRequest) owner.model.CancelEphemeralMsg();
        m_WorkStatus = "review_unavailable";
        ClearUserTurnAwaitingReply("singing-review-unavailable");
        HandleSystemNotice(new SystemNotice("singing_review_unavailable", SystemNoticeSeverity.Warning,
            notice, "保留原目标和材料事实，未自动重试。", "Singing", false));
    }

    private void FinishDirectSingingReview(DirectSingingReviewOwner owner, string reason)
    {
        if (!ReferenceEquals(owner, m_DirectSingingReview)) return;
        if (owner.sent && owner.probeGeneration == m_AutonomyProbeGeneration)
            m_AutonomyProbeInFlight = false;
        if (ReferenceEquals(owner.request, m_DeferredSingingRequest)) ClearDeferredSingingRequest();
        m_DirectSingingReview = null;
        m_DirectSingingReviewCo = null;
        EndGoalReviewTrace(owner.trace, reason);
        Debug.Log($"[Sing/DirectReview] finished version={owner.version} reason={reason}");
    }

    private void CancelDirectSingingGoalReview(string reason)
    {
        var owner = m_DirectSingingReview;
        if (owner == null) return;
        var routine = m_DirectSingingReviewCo;
        bool ownsRequest = owner.sent && owner.probeGeneration == m_AutonomyProbeGeneration;
        FinishDirectSingingReview(owner, reason);
        if (routine != null) StopCoroutine(routine);
        if (ReferenceEquals(owner.request, m_DeferredSingingRequest)) ClearDeferredSingingRequest();
        if (ownsRequest) owner.model.CancelEphemeralMsg();
    }
}
