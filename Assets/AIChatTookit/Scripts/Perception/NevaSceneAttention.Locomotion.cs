using NeEEvA.Motion;
using UnityEngine;

namespace NeEEvA.Perception
{
    public sealed partial class NevaSceneAttention
    {
        private ArdyLocomotionPlayer locomotionFocusPlayer;
        private ArdyRoomLocomotionController locomotionFocusController;
        private int locomotionFocusOperation;
        public bool LocomotionFocusActive => locomotionFocusPlayer != null &&
            locomotionFocusPlayer.OwnsSceneFocus(OwnerId, eyeTracking);
        public int LocomotionFocusAppliedFrames => locomotionFocusPlayer != null ? locomotionFocusPlayer.SceneFocusAppliedFrames : 0;
        public int LocomotionFocusLastAppliedFrame => locomotionFocusPlayer != null ? locomotionFocusPlayer.SceneFocusLastAppliedFrame : -1;
        public float LocomotionFocusAppliedDegrees => locomotionFocusPlayer != null ? locomotionFocusPlayer.SceneFocusLastAppliedDegrees : 0f;

        /// <summary>Called only after the caller's current-image same-entity check. The head
        /// is applied by the exact locomotion player; this component never bypasses its ordinary
        /// full-VRM/body-owner guard to become a competing bone writer.</summary>
        public bool TryBeginLocomotionFocus(string actionId, Vector3 worldPoint,
            ArdyRoomLocomotionController controller, int operationRevision, out string reason)
        {
            reason = null;
            if (!Application.isPlaying || !isActiveAndEnabled || eyeTracking == null || !eyeTracking.IsInitialized ||
                controller == null || controller.Player == null || controller.Avatar != Target)
            { reason = "行走关注的角色、校准或移动控制器不可用。"; return false; }
            if (IsBusy && !string.IsNullOrEmpty(OwnerId) && OwnerId != actionId)
            { reason = "另一个关注动作尚未释放头眼控制。"; return false; }
            var player = controller.Player;
            if (!player.TryBeginSceneFocus(actionId, controller, operationRevision, eyeTracking, worldPoint,
                maximumYawDegrees, maximumUpDegrees, maximumDownDegrees, smoothTime, maximumDegreesPerSecond, out reason)) return false;
            if (!eyeTracking.TrySetLocomotionAttentionTarget(actionId, worldPoint, player))
            { player.ReleaseSceneFocus(actionId); reason = "行走关注未能取得同一动作的眼睛控制。"; return false; }
            // ARDY owns the current pose. Even equality with a former stationary local rotation
            // is not permission to undo that writer's head before starting its scoped layer.
            hasApplied = false;
            locomotionFocusPlayer = player; locomotionFocusController = controller; locomotionFocusOperation = operationRevision;
            LastOwnerId = OwnerId; OwnerId = actionId; TargetPoint = worldPoint;
            currentAngles = player.SceneFocusAngles; State = "turning";
            Status = "正在行走中关注已确认的目标区域；等待实际头部视角更新。";
            AngularErrorDegrees = 180f; settledSince = -1f;
            return true;
        }

        public bool UpdateLocomotionFocusPoint(string actionId, Vector3 worldPoint,
            ArdyRoomLocomotionController controller, int operationRevision, out string reason)
        {
            reason = null;
            if (actionId != OwnerId || locomotionFocusPlayer == null || controller != locomotionFocusController ||
                operationRevision != locomotionFocusOperation || !isActiveAndEnabled)
            { reason = "行走关注动作或移动批次已改变。"; return false; }
            if (!locomotionFocusPlayer.UpdateSceneFocus(actionId, controller, operationRevision, eyeTracking, worldPoint, out reason)) return false;
            if (!eyeTracking.TrySetLocomotionAttentionTarget(actionId, worldPoint, locomotionFocusPlayer))
            { ReleaseLocomotionFocus(actionId, "眼睛关注控制已改变。"); reason = Status; return false; }
            TargetPoint = worldPoint;
            return true;
        }

        public bool ReleaseLocomotionFocus(string actionId, string reason = "行走关注已释放。")
        {
            if (string.IsNullOrEmpty(actionId) || actionId != OwnerId || locomotionFocusPlayer == null) return false;
            locomotionFocusPlayer.ReleaseSceneFocus(actionId);
            ReleaseEyes(); locomotionFocusPlayer = null; locomotionFocusController = null;
            hasApplied = false; currentAngles = Vector2.zero;
            LastOwnerId = OwnerId; OwnerId = null; State = "idle"; Status = reason; settledSince = -1f;
            return true;
        }

        private bool TickLocomotionFocus()
        {
            if (locomotionFocusPlayer == null) return false;
            if (!LocomotionFocusActive || eyeTracking.AttentionOwnerId != OwnerId)
            { ReleaseLocomotionFocus(OwnerId, "行走关注已到时、停止或归属已改变。"); return true; }
            hasApplied = false; // Player, then VRM, wrote this pose. Never run stationary restore.
            currentAngles = locomotionFocusPlayer.SceneFocusAngles;
            if (locomotionFocusPlayer.SceneFocusLastAppliedFrame != Time.frameCount)
            { State = "turning"; Status = "等待本帧行走姿态实际应用。"; return true; }
            AngularErrorDegrees = Vector3.Angle(eyeTracking.HeadFrameRotation * Vector3.forward, TargetPoint - eyeTracking.EyeOrigin);
            bool centered = AngularErrorDegrees <= Mathf.Clamp(settleToleranceDegrees, .5f, 4f);
            State = centered ? "settled" : "turning";
            Status = centered ? "行走中的实际头部视角已朝向目标；可见性以最近实采图为准。" :
                "行走中正以自然头部限幅跟随目标区域；可见性以最近实采图为准。";
            return true;
        }
    }
}
