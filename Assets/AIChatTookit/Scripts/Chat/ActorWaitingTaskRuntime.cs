using System;
using System.Collections.Generic;

/// <summary>Detached snapshot of an explicit future action, not an already running head task.</summary>
public sealed class ActorWaitingTask
{
    public string Id;
    public int Revision;
    public string State;
    public string Action;
    public string Condition;
    public string DraftId;
    public int DraftRevision;
    public string DraftText;
    public string Evidence;
    public int Progress;
    public long ArmedAfterSequence;
    public long LastSequence;
    public string Reason;
}

/// <summary>Dialogue-independent condition state; callers classify events without keyword heuristics.</summary>
public sealed class ActorWaitingTaskRuntime
{
    private ActorWaitingTask m_Pending;
    private readonly Dictionary<string, int> m_RegisteredDraftRevisions = new Dictionary<string, int>(StringComparer.Ordinal);
    public ActorWaitingTask Pending => Copy(m_Pending);

    public bool Register(ActorIntentDecision decision, ActorDraft draft, long sequence,
        out ActorWaitingTask task, out string reason)
    {
        task = Pending;
        if (!ActorIntentProtocol.ValidDraft(draft)) { reason = "invalid_actor_draft"; return false; }
        if (!ActorIntentProtocol.Validate(decision, draft.Text, out reason)) return false;
        if (decision.Kind != "wait") { reason = "actor_kind_not_waiting"; return false; }
        if (sequence < 0) { reason = "invalid_event_sequence"; return false; }
        // Re-delivery of the same immutable draft is idempotent, including after
        // it has triggered/cancelled. It cannot re-arm an already consumed promise.
        if (m_Pending != null && m_Pending.DraftId == draft.Id && m_Pending.DraftRevision == draft.Revision)
        {
            if (m_Pending.DraftText != draft.Text || m_Pending.Action != decision.Action || m_Pending.Condition != decision.Condition)
            { reason = "draft_identity_conflict"; return false; }
            task = Pending; reason = ""; return true;
        }
        if (m_RegisteredDraftRevisions.TryGetValue(draft.Id, out int registered) && draft.Revision <= registered)
        { reason = draft.Revision < registered ? "actor_draft_stale" : "actor_draft_already_registered"; return false; }
        if (m_Pending != null && sequence < m_Pending.LastSequence)
        { reason = "stale_event_sequence"; return false; }
        // A later dialogue may restate the same promise after observing one or
        // two. Consume that draft without resetting the already armed condition
        // or replacing the original evidence that will authorize its execution.
        // Consumed restatements cannot resurrect this task after cancellation.
        if (m_Pending != null && m_Pending.State == "waiting" &&
            m_Pending.Action == decision.Action && m_Pending.Condition == decision.Condition)
        {
            m_RegisteredDraftRevisions[draft.Id] = draft.Revision;
            task = Pending; reason = ""; return true;
        }
        if (m_Pending != null && m_Pending.State == "waiting")
        { m_Pending.State = "cancelled"; m_Pending.Reason = "replaced"; }
        m_Pending = new ActorWaitingTask {
            Id = Guid.NewGuid().ToString("N"), Revision = 1, State = "waiting", Action = decision.Action,
            Condition = decision.Condition, DraftId = draft.Id, DraftRevision = draft.Revision, DraftText = draft.Text,
            Evidence = decision.Evidence,
            Progress = 0, ArmedAfterSequence = sequence, LastSequence = sequence, Reason = "registered"
        };
        m_RegisteredDraftRevisions[draft.Id] = draft.Revision;
        task = Pending; reason = ""; return true;
    }

    public bool Cancel(string reason)
    {
        if (m_Pending == null || m_Pending.State != "waiting") return false;
        m_Pending.State = "cancelled"; m_Pending.Reason = reason ?? "cancelled";
        return true;
    }

    /// <summary>Returns true only once, when this event transitions waiting to triggered.</summary>
    public bool Observe(string taskId, int revision, long sequence, string signal, out ActorWaitingTask fired)
    {
        fired = null;
        if (m_Pending == null || m_Pending.State != "waiting" || m_Pending.Id != taskId ||
            m_Pending.Revision != revision || sequence <= m_Pending.ArmedAfterSequence || sequence <= m_Pending.LastSequence)
            return false;
        if (signal != "none" && signal != "one" && signal != "two" && signal != "three" &&
            signal != "sequence_complete" && signal != "cancel") return false;
        m_Pending.LastSequence = sequence;
        if (signal == "cancel") { Cancel("condition-cancelled"); return false; }
        if (signal == "one") m_Pending.Progress = 1;
        else if (signal == "two" && m_Pending.Progress == 1) m_Pending.Progress = 2;
        bool trigger = signal == "sequence_complete" || (signal == "three" && m_Pending.Progress == 2);
        if (!trigger) return false;
        m_Pending.Progress = 3; m_Pending.State = "triggered"; m_Pending.Reason = "condition-met";
        fired = Pending;
        return true;
    }

    private static ActorWaitingTask Copy(ActorWaitingTask value) => value == null ? null : new ActorWaitingTask {
        Id = value.Id, Revision = value.Revision, State = value.State, Action = value.Action, Condition = value.Condition,
        DraftId = value.DraftId, DraftRevision = value.DraftRevision, DraftText = value.DraftText, Progress = value.Progress,
        Evidence = value.Evidence,
        ArmedAfterSequence = value.ArmedAfterSequence, LastSequence = value.LastSequence, Reason = value.Reason
    };
}
