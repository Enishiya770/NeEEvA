using System.Collections;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private SpeechText? m_PendingRecordingFailureReceipt;
    private RecordingFailureReceiptOwner m_RecordingFailureReceiptOwner;
    private sealed class RecordingFailureReceiptOwner
    {
        public LLM model;
        public long inputRevision;
        public int epoch, turn, generation;
        public bool dispatched;
        public string reason;
    }

    private void ScheduleRecordingFailureReceipt(string reason)
    {
        bool singingGoal = m_SingingGoal != null && m_SingingGoal.turn == m_SingingGoalTurn &&
            m_SingingGoal.proposalWorkEpoch == m_WorkEpoch && m_SingingGoal.intent != "observe" &&
            m_SingingGoal.outcome != "playback_complete_content_unverified" && m_SingingGoal.outcome != "user_confirmed";
        if (!singingGoal && !HasCurrentRecordingDecisionRoute()) return;
        if (m_RecordingFailureReceiptOwner != null &&
            m_RecordingFailureReceiptOwner.inputRevision == m_AcceptedRecordingInputRevision &&
            m_RecordingFailureReceiptOwner.epoch == m_WorkEpoch) return;
        var owner = new RecordingFailureReceiptOwner { model = m_ChatSettings?.m_ChatModel,
            inputRevision = m_AcceptedRecordingInputRevision, epoch = m_WorkEpoch,
            turn = m_SingingGoalTurn, generation = m_FormalResponseGeneration, reason = reason };
        m_RecordingFailureReceiptOwner = owner;
        if (isActiveAndEnabled && owner.model != null && m_ChatSettings?.m_TextToSpeech != null)
            StartCoroutine(RecordingFailureReceiptAfterReview(owner));
    }

    private IEnumerator RecordingFailureReceiptAfterReview(RecordingFailureReceiptOwner owner)
    {
        // Let the rejected formal/review callback finish clearing its old speech
        // round. The receipt has an independent round and cannot release its promise.
        yield return null;
        TryDispatchRecordingFailureReceipt(owner);
    }

    private bool TryDispatchRecordingFailureReceipt(RecordingFailureReceiptOwner owner)
    {
        if (owner == null || !ReferenceEquals(owner, m_RecordingFailureReceiptOwner) || owner.dispatched ||
            !m_WorkNoProgress || !isActiveAndEnabled || m_UserSpeechActiveForAutonomy || m_AgentGracefulShutdownPending ||
            owner.inputRevision != m_AcceptedRecordingInputRevision || owner.epoch != m_WorkEpoch ||
            owner.turn != m_SingingGoalTurn || owner.generation != m_FormalResponseGeneration ||
            !ReferenceEquals(owner.model, m_ChatSettings?.m_ChatModel) || owner.model == null ||
            m_ChatSettings?.m_TextToSpeech == null) return false;
        owner.dispatched = true;
        ClearScheduledAgentWake();
        m_AgentCurrentRoundIsTick = false;
        m_AgentRoundInFlight = m_AgentRunning;
        ClearRoundParsed();
        m_PendingRecordingFailureReceipt = new SpeechText(
            "这次回唱的素材和目标仍未核对通过，后续演唱没有执行，这轮自动重试已经停止了。", "zh",
            "recording-failure", SpeechActionDependency.Independent);
        StartStreaming("", false, continueExistingUserTurn: true);
        return true;
    }

    private bool CompletePendingRecordingFailureReceipt(SpeechText? receipt, int generation)
    {
        if (receipt == null) return false;
        var owner = m_RecordingFailureReceiptOwner;
        bool Current() => owner != null && ReferenceEquals(owner, m_RecordingFailureReceiptOwner) && owner.dispatched &&
            owner.inputRevision == m_AcceptedRecordingInputRevision && owner.epoch == m_WorkEpoch &&
            owner.turn == m_SingingGoalTurn && m_WorkNoProgress && isActiveAndEnabled && !m_UserSpeechActiveForAutonomy &&
            !m_AgentGracefulShutdownPending && generation == owner.generation + 1 && generation == m_FormalResponseGeneration &&
            ReferenceEquals(owner.model, m_ChatSettings?.m_ChatModel);
        bool Abandon()
        {
            if (generation == m_FormalResponseGeneration)
            {
                // No semantic completion for a successor input. Only close this
                // still-owned empty streaming pipeline so its players can drain.
                m_StreamComplete = m_TTSSenderDone = true;
                m_FormalResponseInFlight = false;
            }
            return true;
        }
        if (!Current()) return Abandon();
        m_RoundWaitForUser = true;
        m_ResponseTrace?.Mark("recording_decision.failure_receipt", new JObject {
            ["inputRevision"] = m_RecordingFailureReceiptOwner?.inputRevision,
            ["workEpoch"] = m_RecordingFailureReceiptOwner?.epoch,
            ["generation"] = generation, ["reason"] = m_RecordingFailureReceiptOwner?.reason,
            ["modelRequested"] = false, ["audioActionSubmitted"] = false,
            ["scope"] = "One program-authored independent failure receipt; prior played sound is unchanged and the blocked singing goal remains unfinished." });
        if (!Current()) return Abandon();
        CompleteFastLoopReply(owner.model, "", receipt.Value.Text, receipt.Value.LanguageCode, generation, false);
        return true;
    }
}
