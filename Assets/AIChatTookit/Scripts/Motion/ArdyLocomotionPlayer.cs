using System;
using System.Collections.Generic;
using UniVRM10;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>
    /// Opt-in full-body experiment, separate from dialogue motion. Owns world translation,
    /// root heading and the complete mapped skeleton. Foot contacts are model predictions, never used
    /// to conceal source sliding with IK. No navigation or collision resolution is implied.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(10000)]
    public sealed class ArdyLocomotionPlayer : MonoBehaviour
    {
        private sealed class Bone
        {
            public HumanBodyBones human;
            public string source;
            public Transform transform;
            public Quaternion rest;
            public int index;
        }
        private sealed class SavedTransform
        {
            public Transform transform;
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 scale;
        }
        private sealed class SavedOwner { public Behaviour behaviour; public bool enabled; }
        private sealed class ReturnOwnershipChangedException : InvalidOperationException
        {
            public ReturnOwnershipChangedException(string message) : base(message) { }
        }
        private readonly List<Bone> bones = new List<Bone>();
        private readonly List<SavedTransform> saved = new List<SavedTransform>();
        private readonly List<SavedOwner> owners = new List<SavedOwner>();
        private readonly List<SavedTransform> returnFrom = new List<SavedTransform>();
        private readonly List<SavedTransform> returnBaseline = new List<SavedTransform>();
        private Vrm10Instance target;
        private Vrm10RuntimeControlRig rig;
        private ArdyLocomotionAvatarReference reference;
        private ArdyLocomotionClip clip;
        private Vector3 startPosition, startLocalPosition, startLocalScale, startHipsHorizontal;
        private Quaternion startRotation, startLocalRotation, sourceToWorldRotation;
        private Transform startParent;
        private float avatarScale, groundWorldY, elapsed;
        private bool hasStart, boundVrmEnabled;
        private Func<Vector3, Vector3, string> rootStepGuard;
        private Vector3 lastAppliedRoot;
        private Quaternion lastAppliedRootRotation;
        private float returnElapsed, returnDuration;
        private bool returnBaselinePrepared;

        public Vrm10Instance Target => target;
        public bool IsPlaying { get; private set; }
        public bool HasPoseOwnership { get; private set; }
        public bool CompletedNaturally { get; private set; }
        public bool IsReturningToAnimation { get; private set; }
        public bool ReturnedToAnimation { get; private set; }
        public int BoundBoneCount => bones.Count;
        public float MotionScale { get; private set; }
        public float TimeSeconds => elapsed;
        public float PlaybackEndSeconds { get; private set; }
        public string Status { get; private set; } = "尚未绑定全身移动验证";
        public ArdyLocomotionClip Clip => clip;
        public long PlaybackRevision { get; private set; }
        public string LastBlockedReason { get; private set; }

        public void Bind(Vrm10Instance avatar, ArdyLocomotionAvatarReference importedReference)
        {
            if (avatar == null || avatar.Humanoid == null) throw new ArgumentException("A VRM Humanoid instance is required.");
            if (importedReference == null) throw new ArgumentNullException(nameof(importedReference));
            importedReference.Validate();
            if (importedReference.avatar != null && importedReference.avatar != avatar.Vrm)
                throw new ArgumentException("The imported reference belongs to another VRM asset.");
            ValidateRoot(avatar.transform);
            foreach (var human in ArdyLocomotionAvatarReference.RequiredBones)
                if (avatar.Humanoid.GetBoneTransform(human) == null)
                    throw new InvalidOperationException("Unsupported avatar: missing " + human);
            RestorePreview();
            bones.Clear();
            target = avatar;
            reference = importedReference;
            boundVrmEnabled = avatar.enabled;
            rig = Application.isPlaying && avatar.isActiveAndEnabled ? avatar.Runtime.ControlRig : null;
            CheckRuntime();
            Add(HumanBodyBones.Hips, "Hips");
            Add(HumanBodyBones.Spine, "Spine"); Add(HumanBodyBones.Chest, "Spine2");
            Add(HumanBodyBones.UpperChest, "Spine3"); Add(HumanBodyBones.Neck, "Neck"); Add(HumanBodyBones.Head, "Head");
            Add(HumanBodyBones.LeftShoulder, "LeftShoulder"); Add(HumanBodyBones.LeftUpperArm, "LeftArm");
            Add(HumanBodyBones.LeftLowerArm, "LeftForeArm"); Add(HumanBodyBones.LeftHand, "LeftHand");
            Add(HumanBodyBones.RightShoulder, "RightShoulder"); Add(HumanBodyBones.RightUpperArm, "RightArm");
            Add(HumanBodyBones.RightLowerArm, "RightForeArm"); Add(HumanBodyBones.RightHand, "RightHand");
            Add(HumanBodyBones.LeftUpperLeg, "LeftUpLeg"); Add(HumanBodyBones.LeftLowerLeg, "LeftLeg");
            Add(HumanBodyBones.LeftFoot, "LeftFoot"); Add(HumanBodyBones.LeftToes, "LeftToeBase");
            Add(HumanBodyBones.RightUpperLeg, "RightUpLeg"); Add(HumanBodyBones.RightLowerLeg, "RightLeg");
            Add(HumanBodyBones.RightFoot, "RightFoot"); Add(HumanBodyBones.RightToes, "RightToeBase");
            bones.Sort((a, b) => Depth(a.transform).CompareTo(Depth(b.transform)));
            Status = "全身移动已绑定 · " + bones.Count + " 骨骼 · " + (rig == null ? "Humanoid" : "ControlRig");

            void Add(HumanBodyBones human, string source)
            {
                var actual = avatar.Humanoid.GetBoneTransform(human);
                if (actual == null) return;
                var tf = rig == null ? actual : rig.GetBoneTransform(human);
                if (tf == null) throw new InvalidOperationException("ControlRig is missing " + human);
                // ControlRig bones were normalized in their own construction frame.
                Quaternion rest = rig == null ? reference.Get(human).rotationInRoot :
                    Quaternion.Inverse(avatar.transform.rotation) * rig.GetBoneTransform(HumanBodyBones.Hips).parent.rotation;
                bones.Add(new Bone { human = human, source = source, transform = tf, rest = rest });
            }
        }

        /// <summary>
        /// Play the complete source clip by default. A caller may provide an inclusive end frame
        /// only after auditing the full clip and deciding that its remaining tail is unnecessary.
        /// The original clip remains intact; natural completion applies to this playback range.
        /// </summary>
        public void Play(ArdyLocomotionClip next, Func<Vector3, Vector3, string> stepGuard = null, int? endFrame = null)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            next.Validate();
            if (endFrame.HasValue && (endFrame.Value < 1 || endFrame.Value >= next.frames.Length))
                throw new ArgumentOutOfRangeException(nameof(endFrame), "The playback end frame must be between 1 and the final source frame, inclusive.");
            int lastPlaybackFrame = endFrame ?? (next.frames.Length - 1);
            if (target == null || bones.Count == 0) throw new InvalidOperationException("Bind the imported full-body reference first.");
            if (!enabled || (Application.isPlaying && !gameObject.activeInHierarchy))
                throw new InvalidOperationException("Enable the locomotion player before playback.");
            ValidateRoot(target.transform); CheckRuntime();
            foreach (var other in FindObjectsOfType<ArdyLocomotionPlayer>(true))
                if (other != this && other.Target == target && other.HasPoseOwnership)
                    throw new InvalidOperationException("Another locomotion player already owns this avatar.");
            var indices = new int[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                indices[i] = Array.IndexOf(next.rotationClip.jointNames, bones[i].source);
                if (indices[i] < 0) throw new ArgumentException("Locomotion source is missing " + bones[i].source);
            }
            // Reject competing physical root owners instead of silently fighting them.
            foreach (var body in target.GetComponentsInChildren<Rigidbody>(true))
                if (!body.isKinematic) throw new InvalidOperationException("Dynamic Rigidbody must not own a locomotion preview avatar.");
            foreach (var agent in target.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true))
                if (agent.enabled) throw new InvalidOperationException("An enabled NavMeshAgent already owns this avatar; use an isolated preview instance.");
            foreach (var animator in target.GetComponentsInChildren<Animator>(true))
                if (animator.enabled && animator.applyRootMotion)
                    throw new InvalidOperationException("Room locomotion cannot restore an Animator that applies root motion; configure the intended root owner before playback.");
            for (var parent = target.transform.parent; parent != null; parent = parent.parent)
                foreach (var animator in parent.GetComponents<Animator>())
                    if (animator.enabled)
                        throw new InvalidOperationException("An ancestor Animator may own this avatar; use an isolated preview instance.");
            Stop();
            clip = next;
            PlaybackEndSeconds = lastPlaybackFrame / next.rotationClip.fps;
            rootStepGuard = stepGuard;
            LastBlockedReason = null;
            PlaybackRevision++;
            for (int i = 0; i < bones.Count; i++) bones[i].index = indices[i];
            try
            {
                CaptureAndPauseOwners();
                var root = target.transform;
                startParent = root.parent;
                startLocalPosition = root.localPosition; startLocalRotation = root.localRotation; startLocalScale = root.localScale;
                startPosition = root.position; startRotation = root.rotation; hasStart = true;
                lastAppliedRoot = startPosition;
                lastAppliedRootRotation = startRotation;
                avatarScale = root.lossyScale.y;
                var neutralHips = reference.Get(HumanBodyBones.Hips).positionInRoot;
                MotionScale = (neutralHips.y - reference.groundY) * avatarScale / clip.sourceRootHeight;
                sourceToWorldRotation = startRotation * Quaternion.Inverse(Quaternion.Euler(0, clip.sourceHeadingDegrees, 0));
                startHipsHorizontal = startPosition + startRotation * (new Vector3(neutralHips.x, 0, neutralHips.z) * avatarScale);
                groundWorldY = startPosition.y + reference.groundY * avatarScale;
                saved.Clear();
                var seen = new HashSet<Transform>();
                foreach (var bone in bones)
                {
                    Save(bone.transform);
                    Save(target.Humanoid.GetBoneTransform(bone.human));
                }
                HasPoseOwnership = true; IsPlaying = true; CompletedNaturally = false; elapsed = 0;
                ApplySample(0);
                if (IsPlaying) Status = "播放全身轨迹 · " + next.id;

                void Save(Transform tf)
                {
                    if (tf == null || !seen.Add(tf)) return;
                    saved.Add(new SavedTransform { transform = tf, position = tf.localPosition, rotation = tf.localRotation, scale = tf.localScale });
                }
            }
            catch { Stop(true); throw; }
        }

        /// <summary>Manual deterministic sample; never auto-starts a clip or claims an idle avatar.</summary>
        public void SampleAt(float seconds)
        {
            if (!HasPoseOwnership || clip == null) throw new InvalidOperationException("Play a clip before sampling.");
            if (IsReturningToAnimation) throw new InvalidOperationException("Cannot sample a clip while returning its body pose to animation.");
            if (!ArdyLocomotionClip.Finite(seconds)) throw new ArgumentException("Sample time must be finite.");
            elapsed = Mathf.Clamp(seconds, 0, PlaybackEndSeconds);
            ApplySample(elapsed);
            // A manual preview/regression has no scheduled VRM LateUpdate to transfer its ControlRig.
            if (rig != null) target.Runtime.Process();
        }

        public Vector3 SourceRootToWorld(Vector3 sourcePosition)
        {
            if (!hasStart || clip == null || !ArdyLocomotionClip.Finite(sourcePosition))
                throw new InvalidOperationException("A finite source point and active preview anchor are required.");
            Vector3 offset = sourcePosition - clip.sourceOrigin; offset.y = 0;
            Vector3 world = startHipsHorizontal + sourceToWorldRotation * (offset * MotionScale);
            world.y = groundWorldY + sourcePosition.y * MotionScale;
            return world;
        }

        public Quaternion SourceHeadingToWorld(float degrees)
        {
            if (!hasStart || clip == null || !ArdyLocomotionClip.Finite(degrees))
                throw new InvalidOperationException("A finite heading and active preview anchor are required.");
            return sourceToWorldRotation * Quaternion.Euler(0, degrees, 0);
        }

        /// <summary>
        /// Project the actually rendered Humanoid, including hips and legs, into the fixed source
        /// anchor. Missing optional links inherit their closest mapped parent; Spine1 is interpolated.
        /// Core hand end/thumb links follow the hand: this is not measured finger articulation.
        /// </summary>
        public ArdyMotionFrame CaptureFullBodyHistoryFrame(Quaternion anchorRotation)
        {
            CheckRuntime();
            ValidateRoot(target.transform);
            if (reference == null || bones.Count == 0) throw new InvalidOperationException("Bind before capturing full-body history.");
            var values = new Quaternion[ArdyLiveProtocol.JointNames.Length];
            var mapped = new bool[values.Length];
            Quaternion inverseAnchor = Quaternion.Inverse(anchorRotation);
            foreach (var bone in bones)
            {
                int index = Array.IndexOf(ArdyLiveProtocol.JointNames, bone.source);
                var actual = target.Humanoid.GetBoneTransform(bone.human);
                if (index < 0 || actual == null) continue;
                values[index] = (inverseAnchor * actual.rotation * Quaternion.Inverse(reference.Get(bone.human).rotationInRoot)).normalized;
                mapped[index] = true;
            }
            for (int i = 0; i < values.Length; i++)
                if (!mapped[i]) values[i] = i == 0 ? Quaternion.identity : values[ArdyLiveProtocol.JointParents[i]];
            values[2] = Quaternion.Slerp(values[1], values[3], .5f);
            values[11] = values[12] = values[10];
            values[17] = values[18] = values[16];
            return new ArdyMotionFrame { globalRotations = values };
        }

        /// <summary>
        /// After natural completion, blend the held final body pose into the Animator's current
        /// pose. The arrived root remains stationary and the clip/revision remains unchanged.
        /// Only the Animator resumes during this handoff; other body producers resume at its end.
        /// </summary>
        public void ReturnToAnimation(float seconds = .4f)
        {
            if (!ArdyLocomotionClip.Finite(seconds) || seconds <= 0)
                throw new ArgumentException("Animation return duration must be finite and positive.");
            if (IsReturningToAnimation) return;
            if (!HasPoseOwnership || !CompletedNaturally || IsPlaying || clip == null)
                throw new InvalidOperationException("Only a naturally completed, still-owned clip can return to animation.");
            try { ValidateReturnState(); }
            catch (ReturnOwnershipChangedException error)
            {
                AbortAnimationReturn(error);
                throw;
            }
            foreach (var owner in owners)
                if (owner.enabled && owner.behaviour is Animator animator && animator.applyRootMotion)
                    throw new InvalidOperationException("Animation return cannot resume an Animator that applies root motion.");

            returnFrom.Clear(); returnBaseline.Clear();
            foreach (var bone in bones)
            {
                returnFrom.Add(CaptureTransform(bone.transform));
                // Seed only the channels that an Animator may leave unanimated. This seed is
                // restored before animation evaluation, never displayed as the return's first pose.
                var seed = saved.Find(item => item.transform == bone.transform);
                if (seed == null) throw new InvalidOperationException("The locomotion pose snapshot is incomplete.");
                returnBaseline.Add(new SavedTransform { transform = seed.transform, position = seed.position,
                    rotation = seed.rotation, scale = seed.scale });
            }
            returnElapsed = 0; returnDuration = seconds; returnBaselinePrepared = false;
            IsReturningToAnimation = true; ReturnedToAnimation = false;
            foreach (var owner in owners)
                if (owner.enabled && owner.behaviour is Animator animator) animator.enabled = true;
            // Enabling an Animator must not expose its binding/reset pose even for the first frame.
            RestoreTransforms(returnFrom);
            Status = "已到达 · 正在平滑衔接当前动画";
        }

        private void Update()
        {
            if (!IsReturningToAnimation) return;
            try
            {
                ValidateReturnState();
                // Unity evaluates Animators after Update. Remove our previous blended result so
                // the next target is the live animation, not a feedback blend of prior output.
                RestoreTransforms(returnBaseline);
                returnBaselinePrepared = true;
            }
            catch (Exception error) { AbortAnimationReturn(error); }
        }

        private void LateUpdate()
        {
            if (!HasPoseOwnership || clip == null) return;
            if (IsReturningToAnimation)
            {
                try { ApplyAnimationReturn(); }
                catch (Exception error) { AbortAnimationReturn(error); }
                return;
            }
            try
            {
                if (IsPlaying) elapsed = Mathf.Min(elapsed + Time.deltaTime, PlaybackEndSeconds);
                ApplySample(elapsed);
                if (IsPlaying && elapsed >= PlaybackEndSeconds)
                {
                    IsPlaying = false; CompletedNaturally = true;
                    Status = "已到播放终点 · 保持生成姿态供检查；停止可交回原动画";
                }
            }
            catch (Exception error) { Stop(); Status = error.Message; Debug.LogException(error, this); }
        }

        private void ValidateReturnState()
        {
            try { CheckRuntime(); }
            catch (InvalidOperationException error) { throw new ReturnOwnershipChangedException(error.Message); }
            foreach (var owner in owners)
            {
                if (owner.behaviour == null)
                    throw new ReturnOwnershipChangedException("A body owner was destroyed during animation return.");
                bool expected = IsReturningToAnimation && owner.enabled && owner.behaviour is Animator;
                if (owner.behaviour.enabled != expected)
                    throw new ReturnOwnershipChangedException("Body ownership changed during animation return.");
                if (expected && owner.behaviour is Animator animator && animator.applyRootMotion)
                    throw new ReturnOwnershipChangedException("Animator root motion was enabled during animation return.");
            }
            ValidateRoot(target.transform);
            if (!target.gameObject.activeInHierarchy)
                throw new InvalidOperationException("The locomotion avatar became inactive during animation return.");
            var root = target.transform;
            if (root.parent != startParent || Vector3.Distance(root.position, lastAppliedRoot) > .01f ||
                Quaternion.Angle(root.rotation, lastAppliedRootRotation) > .5f ||
                Mathf.Abs(root.lossyScale.y - avatarScale) > .001f ||
                Vector3.Distance(root.localScale, startLocalScale) > .001f)
                throw new InvalidOperationException("角色根节点被其他系统改变；已取消动画衔接并释放控制");
            string blocked = rootStepGuard?.Invoke(lastAppliedRoot, lastAppliedRoot);
            if (!string.IsNullOrEmpty(blocked))
                throw new InvalidOperationException("动画衔接已停止 · " + blocked);
        }

        private void ApplyAnimationReturn()
        {
            ValidateReturnState();
            // A caller can start a return after this component's Update. Wait for an actual
            // baseline/Animator evaluation instead of treating the held ARDY pose as its target.
            if (!returnBaselinePrepared) { RestoreTransforms(returnFrom); return; }
            returnBaselinePrepared = false;
            float t = Mathf.SmoothStep(0, 1, Mathf.Clamp01(returnElapsed / returnDuration));
            for (int i = 0; i < returnBaseline.Count; i++)
            {
                var baseline = returnBaseline[i]; var tf = baseline.transform;
                if (tf == null) throw new InvalidOperationException("A body bone was destroyed during animation return.");
                baseline.position = tf.localPosition; baseline.rotation = tf.localRotation; baseline.scale = tf.localScale;
                var from = returnFrom[i];
                tf.localPosition = Vector3.Lerp(from.position, baseline.position, t);
                tf.localRotation = Quaternion.Slerp(from.rotation, baseline.rotation, t);
                tf.localScale = Vector3.Lerp(from.scale, baseline.scale, t);
            }
            if (returnElapsed < returnDuration)
            {
                // The first evaluated return frame has weight zero and exactly retains the last
                // generated pose. Subsequent frames follow the current, advancing Animator pose.
                returnElapsed = Mathf.Min(returnDuration, returnElapsed + Time.deltaTime);
                return;
            }
            // The displayed pose is already the latest animation baseline (weight one). Do not
            // call Stop: its preview semantics would restore the stale pre-locomotion snapshot.
            IsReturningToAnimation = false; ReturnedToAnimation = true; HasPoseOwnership = false;
            rootStepGuard = null; saved.Clear(); returnFrom.Clear(); returnBaseline.Clear();
            foreach (var owner in owners) if (owner.behaviour != null) owner.behaviour.enabled = owner.enabled;
            owners.Clear();
            Status = "已平滑交回原动画 · 保留到达位置";
        }

        private void AbortAnimationReturn(Exception error)
        {
            if (error is ReturnOwnershipChangedException) ReleaseConflictedAnimationReturn();
            else Stop();
            LastBlockedReason = error.Message; Status = error.Message;
            Debug.LogWarning("ARDY animation return stopped: " + error.Message, this);
        }

        private void ReleaseConflictedAnimationReturn()
        {
            // An external owner can have already written a new pose. Do not restore either the
            // old preview snapshot or our last animation baseline over that owner's output.
            var restore = new List<SavedOwner>();
            foreach (var owner in owners)
            {
                if (owner.behaviour == null) continue;
                bool expected = IsReturningToAnimation && owner.enabled && owner.behaviour is Animator;
                // Leave external enable/disable changes intact. Only undo a pause that still
                // matches this handoff's state and that originally suspended an enabled owner.
                if (owner.enabled && !expected && !owner.behaviour.enabled) restore.Add(owner);
            }
            PlaybackRevision++;
            IsPlaying = false; HasPoseOwnership = false;
            IsReturningToAnimation = false; ReturnedToAnimation = false; returnBaselinePrepared = false;
            rootStepGuard = null; returnElapsed = 0;
            saved.Clear(); returnFrom.Clear(); returnBaseline.Clear(); owners.Clear();
            foreach (var owner in restore) if (owner.behaviour != null) owner.behaviour.enabled = true;
        }

        private static SavedTransform CaptureTransform(Transform tf) => new SavedTransform
        { transform = tf, position = tf.localPosition, rotation = tf.localRotation, scale = tf.localScale };

        private static void RestoreTransforms(List<SavedTransform> pose)
        {
            foreach (var item in pose)
                if (item.transform != null)
                { item.transform.localPosition = item.position; item.transform.localRotation = item.rotation; item.transform.localScale = item.scale; }
        }

        private void ApplySample(float seconds)
        {
            CheckRuntime(); ValidateRoot(target.transform);
            foreach (var owner in owners)
                if (owner.behaviour != null && owner.behaviour.enabled)
                    throw new InvalidOperationException("Another system re-enabled a paused locomotion bone owner.");
            var root = target.transform;
            if (root.parent != startParent) throw new InvalidOperationException("Avatar was reparented during locomotion; rebind after placement.");
            if (rootStepGuard != null && (Vector3.Distance(root.position, lastAppliedRoot) > .01f ||
                Quaternion.Angle(root.rotation, lastAppliedRootRotation) > .5f))
            {
                Stop();
                LastBlockedReason = "角色根节点被其他系统移动或转向；已释放房间移动控制";
                Status = LastBlockedReason;
                return;
            }
            var rootDelta = clip.rotationClip.SampleDelta(0, seconds);
            var heading = ExtractYaw(rootDelta);
            Quaternion proposedRotation = sourceToWorldRotation * heading;
            var pelvis = SourceRootToWorld(clip.SampleRoot(seconds));
            var neutral = reference.Get(HumanBodyBones.Hips).positionInRoot;
            Vector3 horizontalOffset = proposedRotation * (new Vector3(neutral.x, 0, neutral.z) * avatarScale);
            Vector3 proposedPosition = new Vector3(pelvis.x - horizontalOffset.x, startPosition.y, pelvis.z - horizontalOffset.z);
            string blocked = rootStepGuard?.Invoke(lastAppliedRoot, proposedPosition);
            if (!string.IsNullOrEmpty(blocked))
            {
                Stop();
                LastBlockedReason = blocked;
                Status = "移动已停止 · " + blocked;
                return;
            }
            root.rotation = proposedRotation;
            root.position = proposedPosition;
            lastAppliedRoot = proposedPosition;
            lastAppliedRootRotation = proposedRotation;
            // Source pelvis yaw is already present in every global rotation. Assign world targets
            // against the fixed source anchor, so turning the GameObject cannot apply it twice.
            foreach (var bone in bones)
                bone.transform.rotation = sourceToWorldRotation * clip.rotationClip.SampleDelta(bone.index, seconds) * bone.rest;
            bones.Find(b => b.human == HumanBodyBones.Hips).transform.position = pelvis;
        }

        /// <summary>Release all body owners and return the saved local pose, retaining the reached position/heading by default.</summary>
        public void Stop(bool restoreStartTransform = false)
        {
            PlaybackRevision++;
            IsPlaying = false;
            rootStepGuard = null;
            // Cancellation/replay during a handoff releases the current animation baseline;
            // ordinary preview Stop retains its existing saved-pose restoration semantics.
            RestoreTransforms(IsReturningToAnimation ? returnBaseline : saved);
            IsReturningToAnimation = false; ReturnedToAnimation = false; returnBaselinePrepared = false;
            returnFrom.Clear(); returnBaseline.Clear(); returnElapsed = 0;
            saved.Clear();
            HasPoseOwnership = false;
            if (restoreStartTransform) RestoreStartTransform();
            foreach (var owner in owners) if (owner.behaviour != null) owner.behaviour.enabled = owner.enabled;
            owners.Clear();
            Status = "全身控制已释放 · " + (restoreStartTransform ? "已还原预览起点" : "保留到达位置");
        }

        public void RestorePreview() => Stop(true);

        /// <summary>Commit a room destination before rebinding; preview callers retain their restore anchor by default.</summary>
        public void DiscardPreviewAnchor()
        {
            if (HasPoseOwnership) throw new InvalidOperationException("Release pose ownership before discarding the preview anchor.");
            hasStart = false;
        }

        private void RestoreStartTransform()
        {
            if (!hasStart || target == null) return;
            var root = target.transform;
            // Never undo someone else's reparenting operation.
            if (root.parent == startParent)
            { root.localPosition = startLocalPosition; root.localRotation = startLocalRotation; root.localScale = startLocalScale; }
            hasStart = false;
        }

        private void CaptureAndPauseOwners()
        {
            owners.Clear();
            var seen = new HashSet<Behaviour>();
            // Pause the live producer first so its cancellation cannot change the saved body pose later.
            foreach (var controller in target.GetComponentsInChildren<ArdyLiveMotionController>(true)) Pause(controller);
            // PreviewScene objects may be omitted by Unity's global object search.
            foreach (var player in target.GetComponentsInChildren<ArdyMotionPlayer>(true)) Pause(player);
            foreach (var player in FindObjectsOfType<ArdyMotionPlayer>(true))
                if (player.Target == target || player.transform.IsChildOf(target.transform)) Pause(player);
            foreach (var animator in target.GetComponentsInChildren<Animator>(true)) Pause(animator);
            if (rig != null) Pause(rig.ControlRigAnimator);
            void Pause(Behaviour value)
            {
                if (value == null || !seen.Add(value)) return;
                owners.Add(new SavedOwner { behaviour = value, enabled = value.enabled });
                value.enabled = false;
            }
        }

        private void CheckRuntime()
        {
            if (target == null) throw new InvalidOperationException("Locomotion avatar was destroyed.");
            if (target.enabled != boundVrmEnabled) throw new InvalidOperationException("VRM enabled state changed; bind again.");
            if (!Application.isPlaying || !target.enabled) return;
            if (!target.isActiveAndEnabled || target.UpdateType != Vrm10Instance.UpdateTypes.LateUpdate)
                throw new InvalidOperationException("Active VRM locomotion requires its existing LateUpdate configuration.");
            if (target.Runtime.ControlRig != rig || target.Runtime.VrmAnimation != null)
                throw new InvalidOperationException("VRM rig or animation ownership changed; use an isolated preview avatar.");
        }

        private static Quaternion ExtractYaw(Quaternion value)
        {
            Vector3 forward = value * Vector3.forward; forward.y = 0;
            if (forward.sqrMagnitude < .0001f) throw new InvalidOperationException("Pelvis heading is undefined for a vertical facing direction.");
            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        private static void ValidateRoot(Transform root)
        {
            var scale = root.lossyScale;
            if (!ArdyLocomotionClip.Finite(root.position) || !ArdyLocomotionClip.Finite(scale) ||
                scale.x <= 0 || Mathf.Abs(scale.x - scale.y) > .001f || Mathf.Abs(scale.z - scale.y) > .001f ||
                Vector3.Angle(root.up, Vector3.up) > .1f)
                throw new InvalidOperationException("Locomotion requires an upright avatar with positive uniform world scale.");
        }

        private static int Depth(Transform tf) { int depth = 0; while (tf.parent != null) { depth++; tf = tf.parent; } return depth; }
        private void OnDisable() => Stop();
        private void OnDestroy() => Stop();
    }
}
