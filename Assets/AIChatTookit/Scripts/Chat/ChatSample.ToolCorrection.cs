using System.Collections;
using UnityEngine;

public partial class ChatSample
{
    // A correction must not race the read it just started. This is a short
    // coalescing window, not a second tool timeout or a new model decision.
    private const float ToolCorrectionReadWaitSeconds = 2f;
    private int m_ToolCorrectionWaitTicket;
    private int m_ToolCorrectionOwnerGeneration = -1, m_ToolCorrectionOwnerEpoch = -1,
        m_ToolCorrectionOwnerTicket = -1;

    private void CloseToolCorrectionForActorFailure()
    {
        // The bounded actor failure is the final reply for this accepted input.
        // A legacy parser's pending correction cannot create a second speech
        // round and bypass that closure. Live read results are not cancelled.
        CancelToolCorrectionReadWait();
        m_ToolCorrectionContinuationPending = false;
        m_RoundContinue = false;
        m_RoundWaitForUser = true;
    }

    // Also owns the short scene-capture stage immediately after the read wait.
    // Once the model is dispatched, its regular cancellation path owns busy state.
    private void CancelToolCorrectionReadWait()
    {
        bool ownsBusy = m_ToolCorrectionOwnerTicket == m_ToolCorrectionWaitTicket &&
            m_ToolCorrectionOwnerGeneration == m_FormalResponseGeneration &&
            m_ToolCorrectionOwnerEpoch == m_WorkEpoch;
        ++m_ToolCorrectionWaitTicket;
        ReleaseToolCorrectionWaitOwnership();
        if (!ownsBusy) return;
        m_FormalResponseInFlight = false;
        m_PendingSceneFormalCaptureGeneration = -1;
        m_AgentRoundInFlight = false;
        m_ToolCorrectionContinuationPending = false;
        m_PendingToolCorrectionFrame = "";
        m_RoundContinue = false;
        m_StreamComplete = true;
    }

    private void ReleaseToolCorrectionWaitOwnership()
    {
        m_ToolCorrectionOwnerGeneration = m_ToolCorrectionOwnerEpoch = m_ToolCorrectionOwnerTicket = -1;
    }

    private void CancelOwnedToolCorrectionWait(int ticket)
    {
        // An obsolete coroutine must never clear a newer request's busy state.
        if (ticket == m_ToolCorrectionOwnerTicket && ticket == m_ToolCorrectionWaitTicket)
            CancelToolCorrectionReadWait();
    }

    private bool IsCurrentContinuation(int generation, int epoch) =>
        generation == m_FormalResponseGeneration && epoch == m_WorkEpoch &&
        isActiveAndEnabled && m_AgentRunning && m_AgentRoundInFlight &&
        !m_UserSpeechActiveForAutonomy && !m_WorkNoProgress;

    private bool HasCorrectionReadPending(int search, int catalog) =>
        (search >= 0 && search == m_SongSearchGeneration && m_SongSearchInFlight) ||
        (catalog >= 0 && catalog == m_SongCatalogGeneration && m_SongCatalogInFlight);

    // OnStreamComplete has already consumed the single continuation allowance.
    // Never recurse through OnStreamComplete or consume that allowance again.
    private bool BeginAgentContinuationChain()
    {
        int generation = m_FormalResponseGeneration, epoch = m_WorkEpoch;
        if (ActorReplyFailureAlreadyClosed(m_ChatSettings?.m_ChatModel, generation))
        { CloseToolCorrectionForActorFailure(); return false; }
        int ticket = ++m_ToolCorrectionWaitTicket;
        if (!IsCurrentContinuation(generation, epoch)) return false;
        m_ToolCorrectionOwnerGeneration = generation;
        m_ToolCorrectionOwnerEpoch = epoch;
        m_ToolCorrectionOwnerTicket = ticket;
        int search = m_SongSearchInFlight ? m_SongSearchGeneration : -1;
        int catalog = m_SongCatalogInFlight ? m_SongCatalogGeneration : -1;
        if (m_ToolCorrectionContinuationPending && HasCorrectionReadPending(search, catalog))
        {
            // Playback/review/write operations are deliberately excluded: they
            // have their own continuation owners and may need this decision.
            m_FormalResponseInFlight = true;
            if (m_LogAgentLoop) Debug.Log("[ToolCorrection] 合并在途只读查询结果，最多等待2秒；不重发工具");
            StartCoroutine(WaitForCorrectionRead(ticket, generation, epoch, search, catalog,
                Time.realtimeSinceStartup + ToolCorrectionReadWaitSeconds));
            return true;
        }
        DispatchAgentContinuationChain(generation, epoch, ticket);
        return true;
    }

    private IEnumerator WaitForCorrectionRead(int ticket, int generation, int epoch,
        int search, int catalog, float deadline)
    {
        while (IsCurrentContinuation(generation, epoch) && ticket == m_ToolCorrectionWaitTicket &&
            m_ToolCorrectionContinuationPending && HasCorrectionReadPending(search, catalog) &&
            Time.realtimeSinceStartup < deadline)
            yield return null;

        if (!IsCurrentContinuation(generation, epoch) || ticket != m_ToolCorrectionWaitTicket ||
            !m_ToolCorrectionContinuationPending)
        {
            CancelOwnedToolCorrectionWait(ticket);
            yield break;
        }

        if (HasCorrectionReadPending(search, catalog))
        {
            // This is an observation of an unfinished read, never an invented
            // failed/successful tool result. The actual callback remains live.
            m_PendingToolCorrectionFrame +=
                "\n[纠错续接观察] 只读查询在2秒合并等待窗口内尚未返回；请求仍在进行，结果未知。" +
                "本次没有取消或重发查询。不能声称已查明歌名或目录；无需重复同一查询或再次承诺演唱。";
            if (m_LogAgentLoop) Debug.Log("[ToolCorrection] 查询仍在进行；按未知结果续接，未等待完整网络超时");
        }
        DispatchAgentContinuationChain(generation, epoch, ticket);
    }

    private void DispatchAgentContinuationChain(int generation, int epoch, int ticket)
    {
        if (!IsCurrentContinuation(generation, epoch) || ticket != m_ToolCorrectionWaitTicket)
        { CancelOwnedToolCorrectionWait(ticket); return; }
        m_RoundIsInner = false;
        m_RoundRepeatCheckDone = false;
        m_RoundRepeatHold = false;
        m_RoundInnerCheckDone = false;
        string requestedSkill = m_PendingAutonomousSkillName;
        bool isToolCorrectionChain = m_ToolCorrectionContinuationPending;
        string chainReason = isToolCorrectionChain ? "tool-correction" :
            string.IsNullOrEmpty(requestedSkill) ? "continue-chain" : "skill-request:" + requestedSkill;
        PrepareActiveSkillsForRound(null, chainReason);
        ClearRoundParsed();
        m_CurrentSpeechRoundId = ++m_SpeechRoundSequence;
        m_DeliveryTrace = m_ResponseTrace?.Child("speech-delivery-chain");
        m_FormalResponseInFlight = true;
        m_PendingSceneFormalCaptureGeneration = generation;
        RequestSceneObservation("continue-chain", observed => {
            if (!IsCurrentContinuation(generation, epoch) || ticket != m_ToolCorrectionWaitTicket)
            { CancelOwnedToolCorrectionWait(ticket); return; }
            m_PendingSceneFormalCaptureGeneration = -1;
            BindFormalSceneObservation(observed, generation);
            // Snapshot after both the read wait and scene capture. Taking it
            // before either can drop a result that arrived in the meantime.
            string frame = BuildPerceptionFrame(chainReason);
            m_PendingAutonomousSkillName = "";
            m_PendingAutonomousSkillReason = "";
            m_ToolCorrectionContinuationPending = false;
            HarvestSongIds(frame);
            m_ChatHistory.Add(frame);
            StageFormalObservationFrame(frame, generation);
            if (m_LogAgentLoop)
                Debug.Log(isToolCorrectionChain
                    ? "[ToolCorrection] 真实失败与最新工具结果已注入 → 链接一次角色纠错帧"
                    : string.IsNullOrEmpty(requestedSkill)
                        ? "[Agent] <continue/> → 链接下一帧(TTS pipeline 不间断)"
                        : $"[LLM技能] {requestedSkill} 详细规则已加载 → 链接自主执行帧");
            ReleaseToolCorrectionWaitOwnership();
            DispatchWorkFeedback(BuildWorkContinuationContext(),
                observed?.HasImage == true ? observed.ImageDataUrl : null, generation);
        });
    }
}
