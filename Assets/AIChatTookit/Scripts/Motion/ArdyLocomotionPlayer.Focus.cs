using System;
using UnityEngine;

namespace NeEEvA.Motion
{
    public sealed partial class ArdyLocomotionPlayer
    {
        // This is a subordinate layer of this player's pose, not another full-body writer.
        // The bridge is responsible for fresh same-entity visibility; a point alone never
        // grants a lease. Exact controller ownership and a short refresh bound are also required.
        public const float SceneFocusLeaseSeconds = 1.5f;
        private ArdyRoomLocomotionController sceneFocusController;
        private NevaEyeTracking sceneFocusTracking;
        private string sceneFocusOwner;
        private int sceneFocusOperation;
        private int sceneFocusLastStepFrame = -1;
        private float sceneFocusExpires;
        private bool sceneFocusTargetActive;
        private Vector3 sceneFocusPoint, sceneFocusEyeOffset;
        private Vector2 sceneFocusAngles;
        private float sceneFocusYaw, sceneFocusUp, sceneFocusDown, sceneFocusSmooth, sceneFocusSpeed;
        public string SceneFocusOwnerId => sceneFocusOwner;
        public Vector2 SceneFocusAngles => sceneFocusAngles;
        public int SceneFocusAppliedFrames { get; private set; }
        public int SceneFocusLastAppliedFrame { get; private set; } = -1;
        public float SceneFocusLastAppliedDegrees { get; private set; }
        public bool SceneFocusActive => SceneFocusOwnershipValid() && sceneFocusTargetActive &&
            Time.realtimeSinceStartup <= sceneFocusExpires;
        public bool SceneFocusReturning => SceneFocusOwnershipValid() && !SceneFocusActive &&
            sceneFocusAngles.sqrMagnitude > .0064f;

        public bool TryBeginSceneFocus(string actionId, ArdyRoomLocomotionController controller, int operation,
            NevaEyeTracking tracking, Vector3 point, float yaw, float up, float down, float smoothing,
            float degreesPerSecond, out string reason)
        {
            reason = ValidateSceneFocusBinding(actionId, controller, operation, tracking, point);
            if (reason != null) return false;
            if (!FiniteFocus(yaw) || !FiniteFocus(up) || !FiniteFocus(down) ||
                !FiniteFocus(smoothing) || !FiniteFocus(degreesPerSecond))
            { reason = "行走关注限幅参数无效。"; return false; }
            if (!string.IsNullOrEmpty(sceneFocusOwner) && SceneFocusOwnershipValid() && sceneFocusOwner != actionId)
            { reason = "当前行走关注属于另一动作。"; return false; }
            bool same = sceneFocusOwner == actionId && sceneFocusController == controller && sceneFocusOperation == operation;
            if (!same) { sceneFocusAngles = Vector2.zero; SceneFocusLastAppliedFrame = -1; sceneFocusLastStepFrame = -1; }
            sceneFocusController = controller; sceneFocusOperation = operation;
            sceneFocusTracking = tracking; sceneFocusOwner = actionId;
            sceneFocusPoint = point; sceneFocusTargetActive = true;
            sceneFocusExpires = Time.realtimeSinceStartup + SceneFocusLeaseSeconds;
            // Calibrated real-eye offset expressed in the anatomical head frame. ControlRig
            // uses normalized rotations; its world frame below is independent of VRoid bone axes.
            sceneFocusEyeOffset = Quaternion.Inverse(tracking.HeadFrameRotation) *
                (tracking.EyeOrigin - tracking.Head.position) / Mathf.Max(.01f, avatarScale);
            sceneFocusYaw = Mathf.Clamp(yaw, 10f, 55f); sceneFocusUp = Mathf.Clamp(up, 5f, 30f);
            sceneFocusDown = Mathf.Clamp(down, 5f, 35f); sceneFocusSmooth = Mathf.Clamp(smoothing, .08f, .5f);
            sceneFocusSpeed = Mathf.Clamp(degreesPerSecond, 30f, 180f);
            return true;
        }

        public bool UpdateSceneFocus(string actionId, ArdyRoomLocomotionController controller, int operation,
            NevaEyeTracking tracking, Vector3 point, out string reason)
        {
            reason = ValidateSceneFocusBinding(actionId, controller, operation, tracking, point);
            if (reason != null) return false;
            if (sceneFocusOwner != actionId || sceneFocusController != controller ||
                sceneFocusOperation != operation || sceneFocusTracking != tracking || !SceneFocusActive)
            { reason = "行走关注已到时或归属已改变；需要新的可见性确认后重新申请。"; return false; }
            sceneFocusPoint = point; sceneFocusExpires = Time.realtimeSinceStartup + SceneFocusLeaseSeconds;
            return true;
        }

        public bool ReleaseSceneFocus(string actionId)
        {
            if (string.IsNullOrEmpty(actionId) || sceneFocusOwner != actionId) return false;
            sceneFocusTargetActive = false;
            // Only this same live player may finish the bounded return-to-baseline overlay.
            // Stop/new operation never uses this permission to write over a replacement pose.
            return true;
        }

        public bool OwnsSceneFocus(string actionId, NevaEyeTracking tracking)
            => !string.IsNullOrEmpty(actionId) && sceneFocusOwner == actionId && sceneFocusTracking == tracking && SceneFocusActive;

        private string ValidateSceneFocusBinding(string actionId, ArdyRoomLocomotionController controller,
            int operation, NevaEyeTracking tracking, Vector3 point)
        {
            if (!Application.isPlaying || !isActiveAndEnabled || string.IsNullOrWhiteSpace(actionId) ||
                actionId.Length > 256 || !ArdyLocomotionClip.Finite(point)) return "行走关注参数或运行状态无效。";
            if (controller == null || !controller.isActiveAndEnabled || controller.Player != this ||
                controller.Avatar != target || controller.OperationRevision != operation || controller.Revision != operation ||
                !controller.OwnsActivePlayerPose || !IsPlaying || IsReturningToAnimation || IsStoppingSmoothly)
                return "本次移动尚未取得当前完整姿态，或已停止、取消、换成其他动作。";
            if (tracking == null || !tracking.IsInitialized || !tracking.isActiveAndEnabled || tracking.Target != target ||
                !string.IsNullOrEmpty(tracking.AttentionOwnerId) && tracking.AttentionOwnerId != actionId)
                return "当前角色眼睛未校准或已有其他关注拥有者。";
            try { CheckRuntime(); } catch (InvalidOperationException error) { return error.Message; }
            return null;
        }

        private bool SceneFocusOwnershipValid()
            => !string.IsNullOrEmpty(sceneFocusOwner) && sceneFocusController != null && sceneFocusTracking != null &&
                ValidateSceneFocusBinding(sceneFocusOwner, sceneFocusController, sceneFocusOperation,
                    sceneFocusTracking, sceneFocusPoint) == null;

        private void ApplySceneFocus()
        {
            if (string.IsNullOrEmpty(sceneFocusOwner)) return;
            if (!SceneFocusOwnershipValid()) { WithdrawSceneFocus(); return; }
            if (Time.realtimeSinceStartup > sceneFocusExpires) sceneFocusTargetActive = false;
            var head = bones.Find(value => value.human == HumanBodyBones.Head);
            if (head == null || head.transform == null) { WithdrawSceneFocus(); return; }
            // Every call follows a complete absolute ARDY sample. Never accumulate yesterday's
            // overlay or restore an old local rotation over a freshly generated pose.
            Quaternion frame = head.transform.rotation * Quaternion.Inverse(head.rest);
            Vector3 origin = head.transform.position + frame * (sceneFocusEyeOffset * avatarScale);
            Vector3 local = Quaternion.Inverse(frame) * (sceneFocusPoint - origin);
            Vector2 wanted = Vector2.zero;
            if (sceneFocusTargetActive && local.sqrMagnitude >= .0225f * avatarScale * avatarScale)
            {
                wanted = new Vector2(Mathf.Atan2(local.x, local.z),
                    Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude)) * Mathf.Rad2Deg;
                wanted.x = Mathf.Clamp(wanted.x, -sceneFocusYaw, sceneFocusYaw);
                wanted.y = Mathf.Clamp(wanted.y, -sceneFocusDown, sceneFocusUp);
            }
            // Diagnostic SampleAt/Play may also apply a pose within a rendered frame. They
            // may reapply the layer to that fresh baseline, but cannot advance smoothing twice.
            float dt = sceneFocusLastStepFrame == Time.frameCount ? 0f : Mathf.Clamp(Time.unscaledDeltaTime, 0f, .05f);
            sceneFocusLastStepFrame = Time.frameCount;
            float blend = 1f - Mathf.Exp(-dt / sceneFocusSmooth);
            sceneFocusAngles = Vector2.MoveTowards(sceneFocusAngles, Vector2.Lerp(sceneFocusAngles, wanted, blend), sceneFocusSpeed * dt);
            if (!sceneFocusTargetActive && sceneFocusAngles.sqrMagnitude < .0064f)
            { WithdrawSceneFocus(); return; }
            Quaternion before = head.transform.rotation;
            Quaternion delta = frame * Quaternion.Euler(-sceneFocusAngles.y, sceneFocusAngles.x, 0) * Quaternion.Inverse(frame);
            head.transform.rotation = delta * before;
            SceneFocusLastAppliedDegrees = Quaternion.Angle(before, head.transform.rotation);
            if (SceneFocusLastAppliedFrame != Time.frameCount) SceneFocusAppliedFrames++;
            SceneFocusLastAppliedFrame = Time.frameCount;
        }

        private void WithdrawSceneFocus()
        {
            if (sceneFocusTracking != null && !string.IsNullOrEmpty(sceneFocusOwner))
                sceneFocusTracking.ReleaseAttentionTarget(sceneFocusOwner);
            sceneFocusOwner = null; sceneFocusController = null; sceneFocusTracking = null;
            sceneFocusTargetActive = false; sceneFocusAngles = Vector2.zero;
        }
        private static bool FiniteFocus(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
