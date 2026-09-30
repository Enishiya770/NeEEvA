using System;
using System.Collections.Generic;

/// <summary>A head task survives dialogue turns. Only its runtime owns state transitions.</summary>
public sealed class FastLoopTask
{
    public string Id;
    public int Revision;
    public string State;
    public string Candidate;
    public string Reason;
    public string Source;
    public string SourceId;
    public int SourceRevision;
    public bool Active => State == "starting" || State == "holding";
}

/// <summary>Provider-independent, single-owner state. Call from the owning application thread.</summary>
public sealed class FastLoopRuntime
{
    private FastLoopTask m_HeadTask;
    private sealed class ActorAcceptance
    {
        public int Revision;
        public string Text, Kind, Action, Condition;
    }
    private readonly Dictionary<string, ActorAcceptance> m_ActorAcceptances =
        new Dictionary<string, ActorAcceptance>(StringComparer.Ordinal);
    // Public-field DTOs are detached snapshots, never mutable runtime storage.
    public FastLoopTask HeadTask => Snapshot(m_HeadTask);
    public long OutputRevision { get; private set; }

    // Starting to listen invalidates pending speech, not a persistent posture.
    public long BeginTurn() => RevokeOutput();
    public long RevokeOutput() => ++OutputRevision;

    public bool TryApply(FastLoopDecision decision, string currentUser,
        string expectedTaskId, int expectedTaskRevision, out FastLoopTask task, out string reason)
    {
        task = HeadTask;
        if (!FastLoopProtocol.Validate(decision, currentUser, out reason)) return false;
        if (!SnapshotMatches(expectedTaskId, expectedTaskRevision))
        { reason = "stale_task_snapshot"; return false; }
        if (decision.Route == "stop_voice") { RevokeOutput(); reason = ""; return true; }
        if (decision.Route != "head") { reason = ""; return true; }
        return ApplyHead(decision.Candidate, "user", "", 0, out task, out reason);
    }

    /// <summary>Accepts a present actor decision using its own draft provenance, never a forged user quote.</summary>
    public bool TryApplyActor(ActorIntentDecision decision, ActorDraft draft,
        string expectedTaskId, int expectedRevision, out FastLoopTask task, out string reason)
    {
        task = HeadTask;
        if (!ActorIntentProtocol.ValidDraft(draft)) { reason = "invalid_actor_draft"; return false; }
        if (!ActorIntentProtocol.Validate(decision, draft.Text, out reason)) return false;
        if (decision.Kind != "act" && decision.Kind != "cancel")
        { reason = "actor_kind_not_executable"; return false; }
        if (m_ActorAcceptances.TryGetValue(draft.Id, out var accepted))
        {
            if (draft.Revision < accepted.Revision) { reason = "actor_draft_stale"; return false; }
            if (draft.Revision == accepted.Revision)
            {
                reason = accepted.Text == draft.Text && accepted.Kind == decision.Kind &&
                    accepted.Action == decision.Action && accepted.Condition == decision.Condition
                    ? "actor_draft_already_applied" : "draft_identity_conflict";
                return false;
            }
        }
        if (!SnapshotMatches(expectedTaskId, expectedRevision))
        { reason = "stale_task_snapshot"; return false; }
        if (!ApplyHead(decision.Action, "actor_draft", draft.Id, draft.Revision, out task, out reason)) return false;
        m_ActorAcceptances[draft.Id] = new ActorAcceptance { Revision = draft.Revision, Text = draft.Text,
            Kind = decision.Kind, Action = decision.Action, Condition = decision.Condition };
        return true;
    }

    private bool ApplyHead(string candidate, string source, string sourceId, int sourceRevision,
        out FastLoopTask task, out string reason)
    {
        task = HeadTask;
        if (candidate == "head.release")
        {
            if (m_HeadTask == null || !m_HeadTask.Active) { reason = "no_active_head_task"; return false; }
            CancelHead("explicit-release");
            m_HeadTask.Source = source; m_HeadTask.SourceId = sourceId; m_HeadTask.SourceRevision = sourceRevision;
            task = HeadTask;
            reason = "";
            return true;
        }
        if (candidate == "head.hold" && m_HeadTask != null && m_HeadTask.State == "holding")
        {
            // An explicit reaffirmation preserves the established physical owner.
            reason = "";
            return true;
        }
        if (m_HeadTask == null || !m_HeadTask.Active)
        {
            m_HeadTask = new FastLoopTask { Id = Guid.NewGuid().ToString("N"), Revision = 1,
                State = "starting", Candidate = candidate, Reason = "requested",
                Source = source, SourceId = sourceId, SourceRevision = sourceRevision };
        }
        else
        {
            m_HeadTask = new FastLoopTask { Id = m_HeadTask.Id, Revision = m_HeadTask.Revision + 1,
                State = "starting", Candidate = candidate, Reason = "corrected",
                Source = source, SourceId = sourceId, SourceRevision = sourceRevision };
        }
        task = HeadTask;
        reason = "";
        return true;
    }

    public bool Confirm(string id, int revision)
    {
        if (!Current(id, revision) || m_HeadTask.State != "starting") return false;
        m_HeadTask = CopyWithState(m_HeadTask, "holding", "confirmed");
        return true;
    }

    public bool Fail(string id, int revision, string reason)
    {
        if (!Current(id, revision) || !m_HeadTask.Active) return false;
        m_HeadTask = CopyWithState(m_HeadTask, "failed", reason ?? "failed");
        return true;
    }

    public bool CancelHead(string reason)
    {
        if (m_HeadTask == null || !m_HeadTask.Active) return false;
        m_HeadTask = new FastLoopTask { Id = m_HeadTask.Id, Revision = m_HeadTask.Revision + 1,
            State = "cancelled", Candidate = "head.release", Reason = reason ?? "cancelled",
            Source = m_HeadTask.Source, SourceId = m_HeadTask.SourceId, SourceRevision = m_HeadTask.SourceRevision };
        return true;
    }

    public bool CanPublishCompletion(string taskId, int revision, long outputRevision) =>
        outputRevision == OutputRevision && Current(taskId, revision) && m_HeadTask.State == "holding";

    private bool SnapshotMatches(string id, int revision) => m_HeadTask == null
        ? string.IsNullOrEmpty(id) && revision == 0
        : Current(id, revision);

    private bool Current(string id, int revision) => m_HeadTask != null &&
        !string.IsNullOrEmpty(id) && string.Equals(m_HeadTask.Id, id, StringComparison.Ordinal) && m_HeadTask.Revision == revision;

    private static FastLoopTask Snapshot(FastLoopTask source) => source == null ? null :
        CopyWithState(source, source.State, source.Reason);

    private static FastLoopTask CopyWithState(FastLoopTask source, string state, string reason) =>
        new FastLoopTask { Id = source.Id, Revision = source.Revision, State = state,
            Candidate = source.Candidate, Reason = reason,
            Source = source.Source, SourceId = source.SourceId, SourceRevision = source.SourceRevision };
}
