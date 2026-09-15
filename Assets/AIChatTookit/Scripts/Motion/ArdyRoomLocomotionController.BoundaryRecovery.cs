using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace NeEEvA.Motion
{
    public sealed partial class ArdyRoomLocomotionController
    {
        private ArdyRoomBoundaryRecovery boundaryRecovery;
        private int boundaryRecoveryEndFrame;
        private bool boundaryRecoveryWaitingForRelease;
        private long boundaryRecoveryIdlePlayerRevision;
        private float boundaryRecoveryHeading;
        private readonly Dictionary<ArdyMotionPlayer, int> boundaryGestureSerials = new Dictionary<ArdyMotionPlayer, int>();
        private readonly Dictionary<ArdyLiveMotionController, long> boundaryProducerRevisions = new Dictionary<ArdyLiveMotionController, long>();
        public bool IsNavigationRecoveryActive => boundaryRecovery != null;

        /// <summary>Acquire one operation for a certified, native short step. Never snaps a transform.</summary>
        public bool TryBeginNavigationRecovery(Vector3 facePoint, out string failure)
        {
            failure = null;
            int acquiredToken = -1;
            if (!Application.isPlaying || !isActiveAndEnabled || navigation == null || avatar == null)
            { failure = "边缘恢复需要已绑定的运行中房间角色。"; return false; }
            try
            {
                string unavailable = AvatarUnavailableReason();
                if (unavailable != null) throw new InvalidOperationException(unavailable);
                if (!ArdyLocomotionClip.Finite(facePoint)) throw new ArgumentException("恢复面向点无效。");
                RejectForeignPlayerOwner();
                if (!navigation.TryPlanBoundaryRecovery(AvatarGround(), out var certificate, out failure)) return false;
                CancelCore(false);
                int token = ++revision;
                acquiredToken = token;
                OperationRevision = token;
                boundaryRecovery = certificate;
                boundaryRecoveryWaitingForRelease = true;
                boundaryRecoveryEndFrame = -1;
                Route = certificate.Route; TargetGround = certificate.Target;
                // A correction is a small step, not a new turn towards the user. The follow
                // planner resumes facing controls only after the normal start gate passes.
                WorldFacePoint = null;
                ResetMovingTarget(TargetGround, null);
                LastErrorResponseJson = LastRequestJson = LastResponseJson = null;
                LastArrivalTiming = null;
                SegmentIndex = SegmentCount = PreparedSegmentCount = 0;
                segmentEvidence.Clear(); DiscardedSegmentEvidenceCount = 0;
                PlannedDurationSeconds = completedTimelineSeconds = 0;
                continuationRetry = 0; segmentPlan = null; timelineFinal = true;
                nextSeamFrame = -1; continuationLate = WaitingForSegment = false;
                LastEndpointError = LastHeadingError = 0;
                Stage = "navigation-recovery"; IsBusy = true;
                SetStatus("角色起点在导航边缘；正在审核有限的向内步态恢复", ArdyRoomExecutionState.Preparing);
                work = StartCoroutine(GuardedRun(GenerateBoundaryRecovery(token), token));
                return true;
            }
            catch (Exception error)
            {
                failure = error.Message;
                if (acquiredToken >= 0 && Current(acquiredToken))
                {
                    CancelCore(false);
                    ExecutionState = ArdyRoomExecutionState.Failed;
                    ExecutionReason = ArdyRoomExecutionReason.GenerationFailed;
                    Status = "有限恢复未启动：" + failure;
                }
                return false;
            }
        }

        private IEnumerator GenerateBoundaryRecovery(int token)
        {
            // CancelCore requests a smooth stop of our previous body owner. The old
            // pose must be released before collecting the new actual history.
            bool returningPrevious = player.HasPoseOwnership;
            var previousClip = player.Clip;
            long previousRevision = player.PlaybackRevision;
            while (Current(token) && player != null && player.HasPoseOwnership)
            {
                if (player.Clip != ownedClip || player.PlaybackRevision != ownedPlayerRevision)
                    throw new ExecutionException("恢复前身体控制被其他操作替换。", ArdyRoomExecutionReason.OwnershipLost);
                yield return null;
            }
            if (!Current(token)) yield break;
            if (player.Clip != previousClip || player.PlaybackRevision != previousRevision ||
                returningPrevious && !player.ReturnedToAnimation)
                throw new ExecutionException("恢复前的身体释放被其他操作中断；不会继续恢复。", ArdyRoomExecutionReason.OwnershipLost);
            ownedClip = null;
            boundaryRecoveryWaitingForRelease = false;
            boundaryRecoveryIdlePlayerRevision = player.PlaybackRevision;
            CheckBoundaryGestureOwners(true);
            if (!navigation.TryPlanBoundaryRecovery(AvatarGround(), out var current, out string reason))
                throw new ExecutionException("停稳后的边缘恢复审核失败：" + reason, ArdyRoomExecutionReason.NavigationRejected);
            boundaryRecovery = current; Route = current.Route; TargetGround = current.Target;
            var generation = GenerateAndPlay(token);
            while (Current(token) && generation.MoveNext()) yield return generation.Current;
        }

        private void CheckBoundaryRecoveryWaitingOwner()
        {
            if (boundaryRecovery != null && !boundaryRecoveryWaitingForRelease &&
                (player == null || player.HasPoseOwnership || player.PlaybackRevision != boundaryRecoveryIdlePlayerRevision))
                throw new ExecutionException("等待恢复生成期间身体控制被其他操作改变；旧恢复结果不会接管新动作。", ArdyRoomExecutionReason.OwnershipLost);
            if (boundaryRecovery != null && !boundaryRecoveryWaitingForRelease) CheckBoundaryGestureOwners(false);
        }

        private void CheckBoundaryGestureOwners(bool capture)
        {
            if (capture) { boundaryGestureSerials.Clear(); boundaryProducerRevisions.Clear(); }
            var current = new HashSet<ArdyMotionPlayer>(avatar.GetComponentsInChildren<ArdyMotionPlayer>(true));
            foreach (var gesture in FindObjectsOfType<ArdyMotionPlayer>(true))
                if (gesture.Target == avatar || gesture.transform.IsChildOf(avatar.transform)) current.Add(gesture);
            if (!capture && current.Count != boundaryGestureSerials.Count)
                throw new ExecutionException("恢复等待期间手势播放器绑定发生变化。", ArdyRoomExecutionReason.OwnershipLost);
            foreach (var gesture in current)
            {
                if (gesture.isActiveAndEnabled && gesture.IsPlaying || !capture &&
                    (!boundaryGestureSerials.TryGetValue(gesture, out int serial) || serial != gesture.MotionPlaybackSerial))
                    throw new ExecutionException("其他手势已接管身体；有限恢复不会打断该动作。", ArdyRoomExecutionReason.OwnershipLost);
                if (capture) boundaryGestureSerials[gesture] = gesture.MotionPlaybackSerial;
            }
            var producers = avatar.GetComponentsInChildren<ArdyLiveMotionController>(true);
            if (!capture && producers.Length != boundaryProducerRevisions.Count)
                throw new ExecutionException("恢复等待期间手势生成器绑定发生变化。", ArdyRoomExecutionReason.OwnershipLost);
            foreach (var producer in producers)
            {
                if (!string.IsNullOrEmpty(producer.DescribeActiveMotion()) || !capture &&
                    (!boundaryProducerRevisions.TryGetValue(producer, out long serial) || serial != producer.Revision))
                    throw new ExecutionException("其他手势正在准备或播放；有限恢复不会覆盖该任务。", ArdyRoomExecutionReason.OwnershipLost);
                if (capture) boundaryProducerRevisions[producer] = producer.Revision;
            }
        }

        private ArdyLocomotionTarget[] MakeBoundaryRecoveryTargets(float initialHeading)
        {
            boundaryRecoveryHeading = initialHeading;
            var targets = new ArdyLocomotionTarget[40];
            for (int i = 0; i < targets.Length; i++)
            {
                float progress = Mathf.SmoothStep(0, 1, Mathf.Clamp01((i + 1) / 20f));
                Vector3 point = Vector3.Lerp(boundaryRecovery.Start, boundaryRecovery.Target, progress);
                Vector3 pelvis = point + anchorRotation * Quaternion.Euler(0, initialHeading, 0) * neutralHorizontal;
                Vector3 source = Quaternion.Inverse(anchorRotation) * (pelvis - anchorGround) / motionScale;
                source.y = 0;
                targets[i] = new ArdyLocomotionTarget { rootPosition = source, headingDegrees = initialHeading };
            }
            return targets;
        }

        private Vector3[] AuditBoundaryRecovery(ArdyLocomotionClip value, Vector3 previous)
        {
            var roots = new List<Vector3>();
            boundaryRecoveryEndFrame = -1;
            for (int i = 0; i < value.frames.Length; i++)
            {
                Vector3 next = GeneratedRoot(value, i);
                CheckHeading(value, i, boundaryRecoveryHeading);
                string reason = GuardStep(previous, next);
                if (reason != null)
                    throw new ExecutionException("有限恢复候选第 " + i + " 帧超出审核范围：" + reason, ArdyRoomExecutionReason.GeneratedPathRejected);
                roots.Add(next); previous = next;
                LastEndpointError = Vector2.Distance(new Vector2(next.x, next.z), new Vector2(TargetGround.x, TargetGround.z));
                Vector3 ground = next; ground.y = floorY;
                if (i >= 4 && LastEndpointError <= .02f &&
                    navigation.TryValidatePlanStart(ground, out _, out _, out _))
                {
                    // Only this audited prefix is played. The remaining generated tail is
                    // retained as candidate evidence, never claimed to be displayed.
                    boundaryRecoveryEndFrame = i;
                    return roots.ToArray();
                }
            }
            throw new ExecutionException("有限恢复候选没有可靠回到可重新规划的位置；未移动角色。", ArdyRoomExecutionReason.GeneratedPathRejected);
        }
    }
}
