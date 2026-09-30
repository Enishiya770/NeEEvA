using System;
using System.Collections;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private sealed class ActorBodyHandoff
    {
        public ActorPlanRuntime Plan;
        public FastLoopTurnContext Turn;
        public ArdyRoomDialogueBridge Bridge;
        public ArdyRoomDialogueBridge.FollowHandoff Follow;
        public string Mode, Status;
        public float ObservedAt;
        public bool Revoked;
    }
    private ActorBodyHandoff m_ActorBodyHandoff;

    private bool ActorBodyHandoffCurrent(ActorBodyHandoff lease) => lease != null && !lease.Revoked &&
        ReferenceEquals(lease, m_ActorBodyHandoff) && ActorPlanOwnerCurrent(lease.Plan, lease.Turn) &&
        RoomMotionOutputEnabled && lease.Bridge != null && lease.Bridge == GetComponent<ArdyRoomDialogueBridge>() &&
        lease.Bridge.IsFollowHandoffCurrent(lease.Follow);

    private JObject ActorBodyHandoffFacts()
    {
        var lease = m_ActorBodyHandoff;
        if (lease == null || lease.Follow == null || lease.Follow.SceneSession != m_SceneTimeSession ||
            Time.realtimeSinceStartup - lease.ObservedAt > 600f) return null;
        return new JObject { ["taskId"] = lease.Follow?.TaskId, ["planId"] = lease.Plan?.Id,
            ["mode"] = lease.Mode, ["status"] = lease.Status,
            ["currentPlanOwnsHandoff"] = ActorBodyHandoffCurrent(lease),
            ["automaticResumeAllowed"] = false,
            ["historicalOnly"] = !ActorBodyHandoffCurrent(lease),
            ["scope"] = "Observed task handoff, not completion or restoration permission. Completed finite work never automatically restores following. A follow tail explicitly retained in the adopted complete goal must use the ordinary next-step planner and current target/path checks; otherwise following stays paused. This history cannot authorize a new follow." };
    }

    private IEnumerator PrepareActorBodyHandoff(RoomTaskProtocol.Decision decision, RoomFollowIdentity prior,
        Action<bool, string> ready)
    {
        var plan = m_ActorPlan; var turn = m_ActorPlanTurn;
        if (!ActorPlanCurrent(plan, turn) || plan.State != "planning" || decision?.bodyHandoff == null ||
            plan.Id != decision.planId || plan.Revision != decision.planRevision || !RoomFollowIdentityCurrent(prior) ||
            prior.TaskId != decision.bodyHandoff.taskId)
        { ready(false, "body-handoff-owner-changed"); yield break; }
        RevokeActorBodyHandoff(m_ActorBodyHandoff?.Plan, "replaced");
        var lease = new ActorBodyHandoff { Plan = plan, Turn = turn, Bridge = prior.Bridge,
            Mode = decision.bodyHandoff.mode, Status = "releasing-body", ObservedAt = Time.realtimeSinceStartup };
        m_ActorBodyHandoff = lease;
        lease.Follow = prior.Bridge.SuspendFollowForPlan(prior.TaskId);
        m_ResponseTrace?.Mark("actor_plan.body_handoff", new JObject { ["planId"] = plan.Id,
            ["followTaskId"] = prior.TaskId, ["mode"] = lease.Mode, ["newActionSubmitted"] = false });
        float deadline = Time.realtimeSinceStartup + 3f;
        while (ActorBodyHandoffCurrent(lease) && (lease.Bridge.ReservesBody || lease.Bridge.Controller.IsBusy ||
            lease.Bridge.Controller.IsMoving) && Time.realtimeSinceStartup < deadline) yield return null;
        if (!ActorBodyHandoffCurrent(lease))
        {
            RevokeActorBodyHandoff(plan, "body-handoff-owner-changed");
            if (ActorPlanCurrent(plan, turn)) ready(false, "body-handoff-owner-changed");
            yield break;
        }
        if (lease.Bridge.ReservesBody || lease.Bridge.Controller.IsBusy || lease.Bridge.Controller.IsMoving)
        { RevokeActorBodyHandoff(plan, "body-handoff-body-not-released"); ready(false, "body-handoff-body-not-released"); yield break; }
        lease.Status = "paused-for-plan";
        ready(true, null);
    }

    private void RevokeActorBodyHandoff(ActorPlanRuntime plan, string reason)
    {
        var lease = m_ActorBodyHandoff;
        if (lease == null || !ReferenceEquals(lease.Plan, plan) || lease.Revoked) return;
        lease.Revoked = true; lease.Status = "paused-without-resume: " + reason;
        if (lease.Bridge != null && lease.Bridge.IsFollowHandoffCurrent(lease.Follow)) lease.Bridge.RevokeFollowHandoff(reason);
    }

}
