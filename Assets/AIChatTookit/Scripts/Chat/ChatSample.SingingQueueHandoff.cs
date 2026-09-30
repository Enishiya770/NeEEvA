using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    // Called only after the current review has consumed its single-use deferred
    // request. QueueHumBack still owns all source/range/version/renderer checks.
    // A silent-only formal reply may have drained before its asynchronous review
    // returns, so that earlier drain cannot be relied on to wake this new queue.
    private void QueueReviewedSingingRequest(AgentHumBackRequest request, ConversationTrace reviewTrace)
    {
        int previousQueue = m_SingingQueueSerial;
        QueueHumBack(request);
        bool acceptedNewQueue = m_SingingQueueSerial != previousQueue;
        bool attemptedIdleStart = acceptedNewQueue && m_HumBackPending;
        if (attemptedIdleStart) TryBeginPendingHumBack();

        // QueueHumBack may already have begun parallel preparation while speech
        // drains. Never restart that producer, nor wake unrelated existing work
        // after a rejected/duplicate request. Playback keeps its normal drain gate.
        string state = !acceptedNewQueue ? "not-newly-queued" :
            m_HumBackPending ? "waiting-text-output-drain" :
            IsSingingRuntimeActivityActive() ? "preparing-or-playing" : "preflight-ended";
        reviewTrace?.Mark("goal.queue_handoff", new JObject {
            ["queueSerial"] = acceptedNewQueue ? (int?)m_SingingQueueSerial : null,
            ["acceptedNewQueue"] = acceptedNewQueue,
            ["idleStartAttempted"] = attemptedIdleStart,
            ["state"] = state,
            ["scope"] = "validated queue dispatch; not evidence of conversion or audible playback"
        });
        if (m_LogAgentLoop)
            UnityEngine.Debug.Log($"[Sing/Handoff] queue={m_SingingQueueSerial} " +
                $"new={acceptedNewQueue} idle_start_attempted={attemptedIdleStart} state={state}");
    }
}
