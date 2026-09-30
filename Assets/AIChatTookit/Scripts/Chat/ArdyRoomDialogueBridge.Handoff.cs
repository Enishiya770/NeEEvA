using System;
using UnityEngine;

public sealed partial class ArdyRoomDialogueBridge
{
    public sealed class FollowHandoff
    {
        public string TaskId { get; internal set; }
        public string Status { get; internal set; }
        internal int Epoch, SceneSession;
        internal UnityEngine.Object Controller, Avatar, Navigation;
        internal ChatSample Chat;
        internal Transform Environment, User;
    }
    private int followHandoffEpoch;
    private FollowHandoff pendingFollowHandoff;

    public FollowHandoff SuspendFollowForPlan(string expectedTaskId)
    {
        if (!HasCurrentFollowBinding || ActiveTaskId != expectedTaskId || string.IsNullOrEmpty(expectedTaskId) ||
            controller.OperationRevision != operationRevision || activeAction && !hasControllerOperation ||
            !activeAction && !controller.IsReturningOwnedPose && (controller.IsBusy || controller.IsMoving ||
                controller.Player != null && controller.Player.HasPoseOwnership)) return null;
        RevokeFollowHandoff("replaced");
        var lease = new FollowHandoff { TaskId = expectedTaskId,
            Epoch = followHandoffEpoch, SceneSession = chat != null ? chat.ActionOutcomeSceneSession : 0,
            Controller = controller, Avatar = avatar, Navigation = navigation, Chat = chat,
            Environment = environmentRoot, User = userHead, Status = "paused" };
        pendingFollowHandoff = lease;
        // Cancel the controller through its normal smooth-return path. The new
        // action still waits for pose release; no body/safety guard is bypassed.
        StopMovement("handoff-paused", "角色为当前已采纳目标暂让身体；原跟随已暂停，新动作尚未提交。");
        return IsFollowHandoffCurrent(lease) ? lease : null;
    }

    public bool IsFollowHandoffCurrent(FollowHandoff lease) => lease != null &&
        ReferenceEquals(lease, pendingFollowHandoff) && lease.Epoch == followHandoffEpoch &&
        IsConnected && lease.Controller == controller && lease.Avatar == avatar && lease.Navigation == navigation &&
        lease.Chat == chat && chat == subscribedChat && controller.Avatar == avatar && navigation.EnvironmentRoot == environmentRoot &&
        controller.OperationRevision == operationRevision &&
        lease.Environment == environmentRoot && lease.User == userHead &&
        lease.SceneSession == (chat != null ? chat.ActionOutcomeSceneSession : 0);

    public void RevokeFollowHandoff(string reason)
    {
        ++followHandoffEpoch;
        if (pendingFollowHandoff != null) pendingFollowHandoff.Status = "paused-without-resume: " + reason;
        pendingFollowHandoff = null;
    }

}
