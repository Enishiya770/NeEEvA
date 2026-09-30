using NeEEvA.Perception;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private ReviewedDecisionHandoff<SceneVisionObservation> m_ReviewedDecisionHandoff;
    private SingingGoal m_LastCancellationReplyGoal;

    private string BuildCancelledGoalReplyContext() =>
        "\n[当前用户轮的程序结果：歌唱目标已按用户要求取消，未执行歌唱。此前待验证台词没有外放。]\n" +
        "这是取消生效后的一次回复机会，不是重开歌唱任务，也不是新的用户发言。" +
        "请根据最新完整用户输入重新决定是否回应其中的新话题或保持沉默。" +
        "不得重放旧承诺，不要再次取消同一目标；普通回复可声明 independent。" +
        "这是新的正式角色决定，心理活动仍可保留，但心理活动不能当作已说出口的话。";

    private void DispatchPreparedAgentTick(string triggerReason, string frame,
        AutonomyIntentDecision decision = null, bool workContinuation = false,
        SceneVisionObservation sceneObservation = null, bool useSceneObservation = false,
        string reviewedOutcomeContext = null)
    {
        m_ReviewedDecisionHandoff?.Cancel("superseded");
        if (m_UserSpeechActiveForAutonomy) StoreDeferredAutonomyThought(decision);
        var trace = m_ResponseTrace?.Child("review-handoff");
        int generation = m_AutonomyProbeGeneration, epoch = m_WorkEpoch;
        int formalGeneration = m_FormalResponseGeneration;
        var model = m_ChatSettings?.m_ChatModel;
        bool refreshing = false;
        ReviewedDecisionHandoff<SceneVisionObservation> handoff = null;
        handoff = new ReviewedDecisionHandoff<SceneVisionObservation>(
            () => generation == m_AutonomyProbeGeneration && epoch == m_WorkEpoch &&
                formalGeneration == m_FormalResponseGeneration && ReferenceEquals(model, m_ChatSettings?.m_ChatModel) &&
                m_AgentRunning && !m_AgentGracefulShutdownPending && isActiveAndEnabled &&
                !m_UserSpeechActiveForAutonomy && !m_AgentRoundInFlight && !m_FormalResponseInFlight &&
                !IsAISpeaking && !IsVoiceOutputPlaying,
            observation => IsFreshSceneObservation(observation),
            (observation, refreshed) => {
                // No old capture, target coordinates or public text are replayed. The formal
                // role gets current facts and must revalidate any visual action as usual.
                string nextFrame = refreshed ? BuildPerceptionFrame(triggerReason) : frame;
                if (refreshed)
                    nextFrame += "\n[审核之后已重新请求当前画面。审核候选不代表动作获准；必须依据本轮有效画面重新判断视觉目标。若当前图像不可用，不得沿用旧图像确认物体或动作。]";
                DispatchPreparedAgentTickCore(triggerReason, nextFrame, decision, workContinuation,
                    observation, useSceneObservation, reviewedOutcomeContext);
            },
            outcome => {
                trace?.Mark("review.handoff_end", new JObject { ["outcome"] = outcome,
                    ["refreshed"] = refreshing, ["workEpoch"] = epoch,
                    ["resultReply"] = !string.IsNullOrEmpty(reviewedOutcomeContext) });
                if (!ReferenceEquals(m_ReviewedDecisionHandoff, handoff)) return;
                m_ReviewedDecisionHandoff = null;
                if (refreshing && generation == m_AutonomyProbeGeneration) m_AutonomyProbeInFlight = false;
            });
        m_ReviewedDecisionHandoff = handoff;
        trace?.Mark("review.handoff_begin", new JObject { ["trigger"] = triggerReason,
            ["workEpoch"] = epoch, ["resultReply"] = !string.IsNullOrEmpty(reviewedOutcomeContext) });
        handoff.Start(useSceneObservation ? sceneObservation : null, callback => {
            refreshing = true;
            m_AutonomyProbeInFlight = true;
            trace?.Mark("review.handoff_refresh", new JObject { ["reason"] = "observation_expired_or_invalid" });
            RequestSceneObservation("review-handoff", callback);
            // A lifecycle change may intentionally suppress the capture callback.
            StartCoroutine(SceneObservationDeadline(() => !ReferenceEquals(m_ReviewedDecisionHandoff, handoff),
                () => handoff.Cancel("refresh_timeout")));
        });
    }
}
