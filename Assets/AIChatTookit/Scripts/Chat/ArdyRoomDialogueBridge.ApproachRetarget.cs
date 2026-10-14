using NeEEvA.Motion;
using UnityEngine;

public sealed partial class ArdyRoomDialogueBridge
{
    // One-shot approach retains its action identity and a finite update budget.
    // Continuous following has a separate policy and is never enabled here.
    private const int MaxApproachRetargets = 3;
    private const float ApproachRetargetWindow = 12f, ApproachRetargetTravel = 3f;
    private int approachRetargetCount, approachRetargetUpdates, approachRetargetRestarts, approachRetargetOperationBase;
    private float approachRetargetStartedAt = -1, approachRetargetTravel;
    private bool approachRetargetPending, approachRetargetSettling;
    private string approachRetargetStatus = "none";

    private void ResetApproachRetarget()
    {
        approachRetargetCount = 0; approachRetargetStartedAt = -1; approachRetargetTravel = 0;
        approachRetargetUpdates = approachRetargetRestarts = approachRetargetOperationBase = 0;
        approachRetargetPending = approachRetargetSettling = false; approachRetargetStatus = "none";
    }

    private string NoticeApproachTargetShift(float displacement)
    {
        if (followActive || approachRetargetSettling) return null;
        SyncApproachRetargetAttempts();
        if (approachRetargetStartedAt >= 0 && Time.realtimeSinceStartup - approachRetargetStartedAt > ApproachRetargetWindow)
        { approachRetargetStatus = "exhausted"; return "本次路线调整没有在有限时间内完成，距离或朝向仍未满足，已停止。"; }
        bool endpointInvalid = plan != null && displacement > .01f &&
            !ArdyRoomApproachPlanner.ProjectedGoalSatisfied(plan, userHead.position, approachDistance);
        bool finalError = false;
        if (activeAction && controller.ExecutionState == ArdyRoomExecutionState.Arrived)
        {
            MeasureUser(out float distance, out float facing);
            finalError = !ArdyRoomApproachPlanner.GoalSatisfied(distance, facing, approachDistance);
        }
        if (displacement <= .35f && !endpointInvalid && !finalError) return null;
        if (!activeAction || navigationRecoveryActive || plan == null)
            return "用户位置改变，当前起点恢复无法继续，本次走近已停止。";
        if (approachRetargetTravel + displacement > ApproachRetargetTravel)
        {
            approachRetargetStatus = "exhausted";
            return "本次走近的有限调整已用完，实际距离或朝向仍未满足，已停止。";
        }
        if (approachRetargetStartedAt < 0) approachRetargetStartedAt = Time.realtimeSinceStartup;
        approachRetargetPending = true;
        return null;
    }

    private bool TickApproachRetarget()
    {
        SyncApproachRetargetAttempts();
        if (controller.MovingTargetBudgetExhausted)
        { approachRetargetStatus = "exhausted"; StopMovement("blocked", "本次实际路线调整已用完，距离或朝向仍未满足，已停止。"); return true; }
        if (approachRetargetUpdates > 0 && controller.RetargetNeedsRestart && !approachRetargetSettling)
            approachRetargetSettling = true;
        if (!approachRetargetPending && !approachRetargetSettling) return false;
        if (controller.OperationRevision != operationRevision)
        {
            activeAction = false; approachRetargetPending = approachRetargetSettling = false;
            SetFact("cancelled", "移动已由其他动作接管，旧目标调整已结束。"); return true;
        }
        if (Time.realtimeSinceStartup - approachRetargetStartedAt > ApproachRetargetWindow)
        { approachRetargetStatus = "exhausted"; StopMovement("blocked", "本次路线调整没有在有限时间内完成，已停止，距离或朝向仍未满足。"); return true; }
        if (approachRetargetSettling)
        {
            SetFact("approach-replanning", "距离或朝向需要调整，我正在停稳并重新检查到你身边的路线。");
            if (controller.IsBusy || controller.Player != null && controller.Player.HasPoseOwnership) return true;
            float movedWhileSettling = ArdyRoomApproachPlanner.UserGroundDisplacement(plan.FacePoint, userHead.position);
            if (approachRetargetTravel + movedWhileSettling > ApproachRetargetTravel)
            { approachRetargetStatus = "exhausted"; StopMovement("blocked", "用户移动超过本次走近的有限调整范围，已停止，尚未到达新的位置。"); return true; }
            approachRetargetTravel += movedWhileSettling;
            if (approachRetargetCount >= MaxApproachRetargets)
            { approachRetargetStatus = "exhausted"; StopMovement("blocked", "本次实际路线调整已用完，距离或朝向仍未满足，已停止。"); return true; }
            approachRetargetCount++; approachRetargetRestarts++;
            approachRetargetSettling = approachRetargetPending = false;
            approachRetargetStatus = "restarted";
            BeginApproachLeg(actionId); // Same logical action; fresh controller operation and measured start.
            return true;
        }
        Vector3 ground = avatar.transform.position + Vector3.up * importedReference.groundY * avatar.transform.lossyScale.y;
        if (!ArdyRoomApproachPlanner.TryPlan(navigation, ground, userHead, approachDistance, out var updated, out string failure, out var observation))
        {
            approachRetargetStatus = "route-rejected";
            RecordPlanningRejection(observation, failure);
            StopMovement("blocked", "调整后的路线检查没有通过，本次走近尚未完成。" + failure);
            return true;
        }
        float shift = ArdyRoomApproachPlanner.UserGroundDisplacement(plan.FacePoint, userHead.position);
        if (approachRetargetTravel + shift > ApproachRetargetTravel)
        { approachRetargetStatus = "exhausted"; StopMovement("blocked", "用户移动超过本次走近的有限调整范围，已停止，尚未到达新的位置。"); return true; }
        approachRetargetUpdates++; approachRetargetTravel += shift;
        approachRetargetPending = false;
        // GuardUser continues checking every candidate and played frame against
        // the live user disk. Updating a target never approves an old trajectory.
        plan = updated; attemptObservation = observation;
        if (controller.TryUpdateMovingTarget(plan.GroundTarget, plan.FacePoint, out _))
        {
            approachRetargetStatus = "retargeted";
            SetFact("approach-replanning", "距离或朝向需要调整，我正在调整到你身边的路线。");
        }
        else
        {
            controller.Cancel(); operationRevision = controller.OperationRevision;
            approachRetargetSettling = true; approachRetargetStatus = "settling";
            SetFact("approach-replanning", "距离或朝向需要调整，我正在停稳并重新检查到你身边的路线。");
        }
        return true;
    }

    private void SyncApproachRetargetAttempts()
    {
        if (controller != null && controller.OperationRevision == operationRevision)
            approachRetargetCount = approachRetargetOperationBase + controller.MovingTargetAttempts;
    }
}
