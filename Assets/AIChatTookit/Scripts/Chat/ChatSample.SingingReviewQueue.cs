using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private SingingGoal m_ObservedReviewQueueGoal;
    private int m_ObservedReviewQueueVersion = -1, m_ObservedReviewQueueEpoch = -1;
    private string m_ObservedReviewQueueBlock;
    private double m_ObservedReviewQueueAt = -1;

    private bool IsPendingSingingReviewWake => !SingingReviewRetryExhausted &&
        DeliveryDispositionRules.AllowsPendingGoalReview(HasPendingSingingGoalReview(), m_SceneReviewClock.Reason);

    // This is readiness for an independent review, never source approval or
    // execution authority. Its callback retains the existing goal/version/turn
    // validation and the renderer's own preflight.
    private string PendingSingingReviewBlock(double now, bool speaking, bool requireDue = true)
    {
        if (!m_AgentRunning || m_AgentGracefulShutdownPending || !isActiveAndEnabled) return "session-inactive";
        if (m_SceneTimePaused || m_SceneTimeSuspended || Time.timeScale <= 0) return "scene-paused";
        if (HasActiveActorPlan) return "actor-plan-owned";
        if (speaking || SceneExplorationInputPending) return "input-pending";
        if (m_AutonomyProbeInFlight) return "review-in-flight";
        if (m_FormalResponseInFlight || m_AgentRoundInFlight) return "formal-round-in-flight";
        if (IsAISpeaking || IsVoiceOutputPlaying || !m_StreamComplete || !m_TTSSenderDone ||
            m_PendingChunks.Count > 0 || m_PendingClips.Count > 0) return "output-pending";
        if (m_PendingMotionRepair != null || m_ActiveMotionRepair != null ||
            !string.IsNullOrEmpty(m_PendingSelfInspection)) return "work-feedback-owned";
        if (HasWorkToolInFlight()) return "work-tool-in-flight";
        if (!m_WorkReviewOpen && (!HasAutonomousSingingGoal() || m_AutonomousGoalContinuations >= MaxWorkContinuations))
            return "work-review-closed";
        if (requireDue && !m_SceneReviewClock.IsDue(now)) return "deadline-pending";
        if (!m_SceneReviewClock.CanRequest(now, m_AutonomyReviewsPerMinute, m_AutonomyReviewMinimumSpacing, out _))
            return "review-budget";
        return "ready";
    }

    private bool TryDispatchPendingSingingReview(double now, bool speaking)
    {
        if (!IsPendingSingingReviewWake) return false;
        // Preserve this event while input/output owns the turn; falling through
        // to a generic autonomous tick could consume it as a private thought.
        if (PendingSingingReviewBlock(now, speaking) != "ready") return true;
        var goal = m_SingingGoal;
        int version = goal.version, turn = m_SingingGoalTurn, epoch = m_WorkEpoch;
        int generation = m_FormalResponseGeneration, round = m_CurrentSpeechRoundId;
        var model = m_ChatSettings?.m_ChatModel;
        string reason = m_SceneReviewClock.Consume(now);
        m_ScheduledWakeReason = "";
        m_ResponseTrace?.Sample("goal.review_queue_dispatch", new JObject {
            ["goalVersion"] = m_SingingGoal.version, ["workEpoch"] = m_WorkEpoch,
            ["trigger"] = reason, ["queueDelaySeconds"] = m_SceneReviewClock.LastQueueDelay,
            ["elapsedSeconds"] = m_SceneReviewClock.LastActualElapsed,
            ["scope"] = "Independent proposal review; does not stop or complete an existing body task." });
        // Trace sinks can synchronously replace/cancel a turn. A consumed old
        // wake must never become a probe for that successor or clear its queue.
        if (!ReferenceEquals(goal, m_SingingGoal) || goal.review != "pending" || goal.version != version ||
            turn != m_SingingGoalTurn || epoch != m_WorkEpoch || generation != m_FormalResponseGeneration ||
            round != m_CurrentSpeechRoundId || !ReferenceEquals(model, m_ChatSettings?.m_ChatModel) ||
            SingingReviewRetryExhausted || PendingSingingReviewBlock(SceneMonotonicNow(),
                m_UserSpeechActiveForAutonomy || Time.realtimeSinceStartup - m_LastStreamingPartialRealtime < k_UserSpeakingHoldSeconds, false) != "ready") return true;
        BeginAutonomyIntentProbeCore(reason, false);
        return true;
    }

    private void ObservePendingSingingReviewQueue()
    {
        if (!IsPendingSingingReviewWake || !m_SceneReviewClock.Pending) return;
        double now = SceneMonotonicNow();
        bool speaking = m_UserSpeechActiveForAutonomy ||
            Time.realtimeSinceStartup - m_LastStreamingPartialRealtime < k_UserSpeakingHoldSeconds;
        string block = PendingSingingReviewBlock(now, speaking);
        bool changed = !ReferenceEquals(m_ObservedReviewQueueGoal, m_SingingGoal) ||
            m_ObservedReviewQueueVersion != m_SingingGoal.version || m_ObservedReviewQueueEpoch != m_WorkEpoch ||
            m_ObservedReviewQueueBlock != block;
        if (!changed && now - m_ObservedReviewQueueAt < 5) return;
        m_ObservedReviewQueueGoal = m_SingingGoal; m_ObservedReviewQueueVersion = m_SingingGoal.version;
        m_ObservedReviewQueueEpoch = m_WorkEpoch; m_ObservedReviewQueueBlock = block; m_ObservedReviewQueueAt = now;
        var room = GetComponent<ArdyRoomDialogueBridge>();
        m_ResponseTrace?.Sample("goal.review_queue_state", new JObject {
            ["goalVersion"] = m_SingingGoal.version, ["workEpoch"] = m_WorkEpoch,
            ["trigger"] = m_SceneReviewClock.Reason, ["blockedBy"] = block,
            ["due"] = m_SceneReviewClock.IsDue(now), ["queueDelaySeconds"] = Math.Max(0, now - m_SceneReviewClock.Deadline),
            ["actorPlanState"] = m_ActorPlan?.State, ["formalInFlight"] = m_FormalResponseInFlight,
            ["agentRoundInFlight"] = m_AgentRoundInFlight, ["reviewInFlight"] = m_AutonomyProbeInFlight,
            ["logicalSpeaking"] = IsAISpeaking, ["audioSourcePlaying"] = IsVoiceOutputPlaying,
            ["streamComplete"] = m_StreamComplete, ["ttsSenderDone"] = m_TTSSenderDone,
            ["userSpeaking"] = speaking, ["inputPending"] = SceneExplorationInputPending,
            ["sceneActionBusy"] = SceneActionIsBusy, ["following"] = room != null && room.IsFollowing,
            ["scope"] = "Runtime queue gates; missing old snapshots cannot identify the blocker of a past wait." });
    }
}
