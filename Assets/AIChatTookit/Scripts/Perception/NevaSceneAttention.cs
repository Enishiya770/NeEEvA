using System;
using NeEEvA.Motion;
using UniVRM10;
using UnityEngine;

namespace NeEEvA.Perception
{
    /// <summary>
    /// Bounded stationary attention over the current animated head pose. Scene capture reads this
    /// actual head after eye tracking; this component never moves the character root or user camera.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(12000)]
    public sealed partial class NevaSceneAttention : MonoBehaviour
    {
        [SerializeField] private NevaEyeTracking eyeTracking;
        [Range(10f, 55f)] public float maximumYawDegrees = 50f;
        [Range(5f, 30f)] public float maximumUpDegrees = 25f;
        [Range(5f, 35f)] public float maximumDownDegrees = 30f;
        [Range(.08f, .5f)] public float smoothTime = .16f;
        [Range(30f, 180f)] public float maximumDegreesPerSecond = 120f;
        [Range(.5f, 4f)] public float settleToleranceDegrees = 2f;
        [Range(2f, 30f)] public float maximumHoldSeconds = 10f;

        public Vrm10Instance Target => eyeTracking != null ? eyeTracking.Target : null;
        public string OwnerId { get; private set; }
        public string LastOwnerId { get; private set; }
        public string State { get; private set; } = "idle";
        public string Status { get; private set; } = "尚未开始场景关注。";
        public Vector3 TargetPoint { get; private set; }
        public bool IsSettled => State == "settled";
        public bool IsReturning => State == "returning";
        public bool IsBusy => State == "turning" || IsSettled || IsReturning;
        public float AngularErrorDegrees { get; private set; }
        public Vector2 CurrentHeadAngles => currentAngles;

        private Vector2 currentAngles;
        private Quaternion baselineLocal, appliedLocal;
        private bool hasApplied;
        private float acquisitionDeadline, holdDeadline, settledSince = -1f;
        private Vector3 requestRootPosition;
        private Quaternion requestRootRotation;

        public void Configure(NevaEyeTracking tracking)
        {
            if (tracking == null || !tracking.IsInitialized)
                throw new ArgumentException("Scene attention requires the existing calibrated eye tracking component.");
            ForceRelease("角色关注绑定已更新。", "idle");
            eyeTracking = tracking;
        }

        /// <summary>
        /// Acquires only a stationary head. A successful acquisition holds until matching Release,
        /// with a separate bounded hold deadline so capture/model latency does not consume turn time.
        /// </summary>
        public bool TryLookAt(string actionId, Vector3 worldPoint, out string reason, float timeoutSeconds = 3.5f)
        {
            reason = null;
            if (!Application.isPlaying || !isActiveAndEnabled) reason = "场景关注仅在已启用的 Play 角色上执行。";
            else if (string.IsNullOrEmpty(actionId) || !Finite(worldPoint) || !Finite(timeoutSeconds) || timeoutSeconds <= 0)
                reason = "关注目标或动作标识无效。";
            else reason = CompetingOwnerReason();
            if (reason != null) return false;
            RestoreAppliedHead();
            if (!TryTargetAngles(worldPoint, out _, out reason)) return false;
            if (!string.IsNullOrEmpty(eyeTracking.AttentionOwnerId) && eyeTracking.AttentionOwnerId != OwnerId)
            { reason = "眼睛已有另一个关注动作的控制权。"; return false; }
            ReleaseEyes();
            if (!eyeTracking.TrySetAttentionTarget(actionId, worldPoint))
            { reason = "无法取得当前角色的眼睛关注控制权。"; return false; }
            LastOwnerId = OwnerId; OwnerId = actionId; TargetPoint = worldPoint;
            State = "turning"; Status = "正在转头观察目标。";
            settledSince = -1f; AngularErrorDegrees = 180f;
            acquisitionDeadline = Time.realtimeSinceStartup + Mathf.Clamp(timeoutSeconds, .1f, 10f);
            holdDeadline = float.PositiveInfinity;
            requestRootPosition = Target.transform.position; requestRootRotation = Target.transform.rotation;
            return true;
        }

        /// <summary>Changes the actual view relative to its currently displayed head frame.
        /// Unknown directions need no invented object. Natural total neck limits remain enforced.</summary>
        public bool TryOrientView(string actionId, float yaw, float pitch, out string reason)
        {
            reason = null;
            if (!Finite(yaw) || !Finite(pitch) || Mathf.Abs(yaw) > 50f || Mathf.Abs(pitch) > 25f ||
                Mathf.Max(Mathf.Abs(yaw), Mathf.Abs(pitch)) < 3f)
            { reason = "视向变化角度超出本次有界范围或过小。"; return false; }
            reason = CompetingOwnerReason();
            if (reason != null) return false;
            // Snapshot the real displayed frame before TryLookAt removes its old overlay.
            Vector3 direction = eyeTracking.HeadFrameRotation * Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward;
            Vector3 point = eyeTracking.EyeOrigin + direction * 3f;
            return TryLookAt(actionId, point, out reason, 4f);
        }

        /// <summary>Keep a settled view through the following fresh-image decision, owner fenced.
        /// A body writer, explicit stop, closed eyes or lifecycle invalidation still releases it.</summary>
        public bool RetainView(string actionId, float seconds = 20f)
        {
            if (OwnerId != actionId || !IsSettled || !Finite(seconds)) return false;
            holdDeadline = Time.realtimeSinceStartup + Mathf.Clamp(seconds, 2f, 30f);
            Status = "保持实际观察视向，等待下一次判断。";
            return true;
        }

        /// <summary>Old asynchronous completions cannot release a newer attention request.</summary>
        public bool Release(string actionId, string reason = "released")
        {
            if (string.IsNullOrEmpty(actionId) || actionId != OwnerId || !IsBusy) return false;
            BeginReturn(reason); return true;
        }

        public void Cancel(string reason = "cancelled")
        {
            if (IsBusy) BeginReturn(reason);
        }

        private void BeginReturn(string reason)
        {
            if (locomotionFocusPlayer != null)
            { ReleaseLocomotionFocus(OwnerId, reason); return; }
            ReleaseEyes(); State = "returning"; Status = reason; settledSince = -1f;
        }

        private void LateUpdate()
        {
            if (TickLocomotionFocus()) return;
            if (!IsBusy) return;
            string conflict = CompetingOwnerReason();
            // ARDY LateUpdate runs before this component. Even an identical-looking new body
            // pose belongs to that writer and must not be mistaken for our previous overlay.
            if (conflict != null)
            {
                bool otherPoseOwner = Target != null && Target.isActiveAndEnabled || HasArdyPoseOwner();
                if (otherPoseOwner) hasApplied = false;
                ForceRelease(conflict, "yielded"); return;
            }
            // Keep the last displayed head pose intact through Update/coroutine consumers.
            // Restoring in Update exposes a transient neutral pose to image-freshness checks.
            // Remove our old overlay only immediately before the next LateUpdate overlay;
            // an Animator's intervening absolute head write remains the new baseline.
            RestoreAppliedHead();
            if (Vector3.Distance(Target.transform.position, requestRootPosition) > .02f ||
                Quaternion.Angle(Target.transform.rotation, requestRootRotation) > 2f)
            { ForceRelease("角色身体已移动或转向，已让出场景关注。", "yielded"); return; }

            Vector2 wanted = Vector2.zero;
            if (!IsReturning)
            {
                if (eyeTracking.AttentionOwnerId != OwnerId)
                { ForceRelease("眼睛关注控制权已改变。", "yielded"); return; }
                if (!TryTargetAngles(TargetPoint, out wanted, out string unavailable))
                { BeginReturn(unavailable); wanted = Vector2.zero; }
                else if (Time.realtimeSinceStartup > (IsSettled ? holdDeadline : acquisitionDeadline))
                { BeginReturn(IsSettled ? "目标关注保持已到时。" : "转头未在限时内稳定。" ); wanted = Vector2.zero; }
            }

            float dt = Mathf.Clamp(Time.unscaledDeltaTime, 0f, .05f);
            float blend = 1f - Mathf.Exp(-dt / Mathf.Clamp(smoothTime, .08f, .5f));
            Vector2 smooth = Vector2.Lerp(currentAngles, wanted, blend);
            currentAngles = Vector2.MoveTowards(currentAngles, smooth, Mathf.Clamp(maximumDegreesPerSecond, 30f, 180f) * dt);
            if (IsReturning && currentAngles.magnitude < .08f)
            {
                currentAngles = Vector2.zero; LastOwnerId = OwnerId; OwnerId = null; State = "idle";
                return;
            }

            var head = eyeTracking.Head;
            baselineLocal = head.localRotation;
            Quaternion frame = eyeTracking.HeadFrameRotation;
            Quaternion delta = frame * Quaternion.Euler(-currentAngles.y, currentAngles.x, 0f) * Quaternion.Inverse(frame);
            head.rotation = delta * head.rotation;
            appliedLocal = head.localRotation; hasApplied = true;
            if (IsReturning) return;
            AngularErrorDegrees = Vector3.Angle(eyeTracking.HeadFrameRotation * Vector3.forward, TargetPoint - eyeTracking.EyeOrigin);
            if (AngularErrorDegrees <= Mathf.Clamp(settleToleranceDegrees, .5f, 4f))
            {
                if (settledSince < 0f) settledSince = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - settledSince >= .12f && !IsSettled)
                {
                    State = "settled"; Status = "实际头部视角已稳定，可采集新画面。";
                    if (float.IsPositiveInfinity(holdDeadline))
                        holdDeadline = Time.realtimeSinceStartup + Mathf.Clamp(maximumHoldSeconds, 2f, 30f);
                }
            }
            else
            {
                settledSince = -1f;
                // Keep the original acquisition bound if an animated baseline never settles.
                if (IsSettled) { State = "turning"; acquisitionDeadline = Mathf.Min(holdDeadline, Time.realtimeSinceStartup + 1f); }
            }
        }

        private bool TryTargetAngles(Vector3 point, out Vector2 angles, out string reason)
        {
            angles = Vector2.zero; reason = null;
            Vector3 offset = point - eyeTracking.EyeOrigin;
            float scale = Mathf.Max(.01f, Target.transform.lossyScale.magnitude / Mathf.Sqrt(3f));
            if (offset.sqrMagnitude < .25f * .25f * scale * scale)
            { reason = "目标离头部过近，未强行转头。"; return false; }
            Vector3 local = Quaternion.Inverse(eyeTracking.HeadFrameRotation) * offset;
            angles = new Vector2(Mathf.Atan2(local.x, local.z), Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude)) * Mathf.Rad2Deg;
            if (Mathf.Abs(angles.x) > Mathf.Clamp(maximumYawDegrees, 10f, 55f) ||
                angles.y > Mathf.Clamp(maximumUpDegrees, 5f, 30f) || angles.y < -Mathf.Clamp(maximumDownDegrees, 5f, 35f))
            { reason = "目标超出本次小幅转头范围，需要先改变身体朝向。"; return false; }
            return true;
        }

        private string CompetingOwnerReason()
        {
            if (eyeTracking == null || !eyeTracking.IsInitialized || !eyeTracking.isActiveAndEnabled ||
                Target == null || !Target.gameObject.activeInHierarchy) return "角色头部或眼睛校准当前不可用。";
            if (Target.isActiveAndEnabled) return "完整 VRM 更新正在控制骨骼，场景关注已让出控制。";
            if (HasArdyPoseOwner()) return "ARDY 动作或全身移动正在控制身体，场景关注已让出控制。";
            return null;
        }

        private bool HasArdyPoseOwner()
        {
            if (Target == null) return false;
            foreach (var player in FindObjectsOfType<ArdyLocomotionPlayer>(true))
                if (player.Target == Target && player.HasPoseOwnership) return true;
            foreach (var player in FindObjectsOfType<ArdyMotionPlayer>(true))
                if (player.Target == Target && player.isActiveAndEnabled && player.IsPlaying) return true;
            return false;
        }

        private void RestoreAppliedHead()
        {
            if (!hasApplied) return;
            if (eyeTracking != null && eyeTracking.Head != null &&
                Quaternion.Angle(eyeTracking.Head.localRotation, appliedLocal) < .05f)
                eyeTracking.Head.localRotation = baselineLocal;
            hasApplied = false;
        }

        private void ReleaseEyes()
        {
            if (eyeTracking != null && !string.IsNullOrEmpty(OwnerId)) eyeTracking.ReleaseAttentionTarget(OwnerId);
        }

        private void ForceRelease(string reason, string state)
        {
            ReleaseLocomotionFocus(OwnerId, reason);
            RestoreAppliedHead(); ReleaseEyes(); currentAngles = Vector2.zero;
            LastOwnerId = OwnerId; OwnerId = null; State = state; Status = reason; settledSince = -1f;
        }

        private void OnDisable() => ForceRelease("场景关注组件已停止。", "idle");
        private void OnDestroy() => ForceRelease("场景关注组件已销毁。", "idle");
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
