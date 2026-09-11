using System;
using System.Collections.Generic;
using UniVRM10;
using UnityEngine;

namespace NeEEvA.Motion
{
    public enum ArdyMotionMask { LeftArm, RightArm, Head, UpperBody }

    /// <summary>
    /// Apply body motion after Animator evaluation and before VRM constraints/look-at/spring bones.
    /// Restore the underlying pose on the next Update so unanimated bones cannot accumulate offsets.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]
    public sealed class ArdyMotionPlayer : MonoBehaviour
    {
        [SerializeField] private Vrm10Instance target;
        private Transform interactionTarget;
        [Min(0.05f)] public float blendInSeconds = 0.3f;
        [Min(0.05f)] public float blendOutSeconds = 0.3f;

        private sealed class Bone
        {
            public HumanBodyBones humanoid;
            public string sourceName;
            public Transform transform;
            public Quaternion restInRoot;
            public Quaternion baseline, lastApplied, transitionFrom, transitionDeltaFrom;
            public bool hasApplied, hasRendered, previousMask, blendAsDelta;
            public int sourceIndex;
        }

        private readonly List<Bone> bones = new List<Bone>();
        private ArdyMotionClip clip;
        private ArdyConstraintSession constraintSession;
        private ArdyConstraintPose renderedConstraintPose;
        private ArdyConstraintObservation lastConstraintObservation;
        private int constraintObservedFrame = -1;
        private ArdyMotionMask mask;
        private float time, transitionTime;
        private bool stopping;
        private bool usesControlRig;
        private Vrm10Instance boundTarget;
        private Vrm10RuntimeControlRig boundRig;
        private bool boundVrmEnabled, isBound;
        private Bone chestAnchor;
        private int sourceChestIndex;
        private bool streamingSession, streamComplete;
        private float streamUnderrunSeconds;
        public Vrm10Instance Target => target;
        public Transform AvatarRoot => target != null ? target.transform : null;
        public int BoundBoneCount => isBound ? bones.Count : 0;
        public bool IsPlaying => clip != null || constraintSession != null;
        public bool IsConstrained => constraintSession != null;
        public bool IsHoldingPose => !stopping && constraintSession != null && constraintSession.IsHolding;
        public string ConstraintPhase => constraintSession != null ? constraintSession.Phase : lastConstraintObservation?.phase;
        public string ConstraintFailure => constraintSession != null ? constraintSession.Failure : lastConstraintObservation?.failure;
        public ArdyConstraintObservation ConstraintObservation => constraintSession != null
            ? constraintSession.Observation.Copy() : lastConstraintObservation?.Copy();
        public bool CanAppendStream => IsPlaying && streamingSession && !streamComplete && !stopping;
        public float BufferedSeconds => clip == null ? 0 : Mathf.Max(0, clip.Duration - time);
        public string StreamFailure { get; private set; }
        public string Status { get; private set; } = "尚未绑定角色";
        public int MotionPlaybackSerial { get; private set; }
        public bool MotionPlaybackCompletedNaturally { get; private set; }
        public bool MotionPlaybackWasInterrupted { get; private set; }
        private int generatedAppliedFrame = -1, generatedAppliedSerial = -1;

        private void InvalidateMotionObservation()
        {
            unchecked { MotionPlaybackSerial++; }
            generatedAppliedFrame = generatedAppliedSerial = -1;
            MotionPlaybackCompletedNaturally = MotionPlaybackWasInterrupted = false;
        }

        /// <summary>
        /// Call after VRM LateUpdate. Reads actual Humanoid bones, never the normalized ControlRig.
        /// Only a fresh clip/stream frame is eligible; network waits, constraints and return-to-idle
        /// frames are excluded. This captures the visible blended pose, not a source FK prediction.
        /// </summary>
        public bool TryCaptureActualRenderedMotion(out ArdyMotionPoseSample sample)
        {
            sample = null;
            if (!isActiveAndEnabled || !isBound || target == null || target != boundTarget
                || generatedAppliedFrame != Time.frameCount || generatedAppliedSerial != MotionPlaybackSerial) return false;
            var root = target.transform;
            var inverse = Quaternion.Inverse(root.rotation);
            sample = new ArdyMotionPoseSample { frame = Time.frameCount, playbackSerial = MotionPlaybackSerial,
                timeSeconds = Time.timeAsDouble, rootWorldPosition = root.position,
                rootWorldRotation = root.rotation, rootScale = root.lossyScale };
            sample.available = Finite(root.position) && Finite(root.lossyScale)
                && root.lossyScale.x > 0 && root.lossyScale.y > 0 && root.lossyScale.z > 0;
            sample.reason = sample.available ? "actual-humanoid-post-VRM" : "invalid-or-reflected-avatar-coordinate-frame";
            var head = target.Humanoid.GetBoneTransform(HumanBodyBones.Head);
            var chest = target.Humanoid.GetBoneTransform(HumanBodyBones.UpperChest)
                ?? target.Humanoid.GetBoneTransform(HumanBodyBones.Chest)
                ?? target.Humanoid.GetBoneTransform(HumanBodyBones.Spine);
            var hips = target.Humanoid.GetBoneTransform(HumanBodyBones.Hips);
            sample.headAvailable = Valid(head); sample.chestAvailable = Valid(chest); sample.hipsAvailable = Valid(hips);
            if (sample.headAvailable) { sample.headCharacter = Point(head); sample.headRotationCharacter = inverse * head.rotation; }
            if (sample.chestAvailable) sample.chestCharacter = Point(chest);
            if (sample.hipsAvailable) sample.hipsCharacter = Point(hips);
            var captured = sample;
            sample.left = Arm(true); sample.right = Arm(false);
            return true;

            Vector3 Point(Transform value) => inverse * (value.position - root.position);
            bool Valid(Transform value) => value != null && Finite(value.position);
            ArdyObservedArm Arm(bool left)
            {
                var upper = target.Humanoid.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
                var lower = target.Humanoid.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
                var hand = target.Humanoid.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                var arm = new ArdyObservedArm { available = Valid(upper) && Valid(lower) && Valid(hand) };
                if (!arm.available) { arm.reason = "required-actual-arm-bones-unavailable"; return arm; }
                arm.shoulderCharacter = Point(upper); arm.elbowCharacter = Point(lower); arm.wristCharacter = Point(hand);
                arm.armLengthMeters = Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, hand.position);
                if (arm.armLengthMeters < 0.0001f) { arm.available = false; arm.reason = "degenerate-actual-arm-length"; return arm; }
                arm.wristFromShoulderNormalized = (arm.wristCharacter - arm.shoulderCharacter) / arm.armLengthMeters;
                arm.extensionRatio = arm.wristFromShoulderNormalized.magnitude;
                if (captured.headAvailable) arm.wristFromHeadNormalized = (arm.wristCharacter - captured.headCharacter) / arm.armLengthMeters;
                if (captured.chestAvailable) arm.wristFromChestNormalized = (arm.wristCharacter - captured.chestCharacter) / arm.armLengthMeters;
                arm.wristHeadDistanceNormalized = arm.wristFromHeadNormalized.magnitude;
                return arm;
            }
        }

        private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x)
            && !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        /// <summary>Sampled at the next constrained request; an existing hold never tracks a moving viewer.</summary>
        public void SetInteractionTarget(Transform value) => interactionTarget = value;

        private void Start()
        {
            if (target != null && bones.Count == 0)
            {
                try { Bind(target); }
                catch (Exception error) { Status = error.Message; Debug.LogException(error, this); }
            }
        }

        public void Bind(Vrm10Instance avatar)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            InvalidateMotionObservation();
            RestoreAnimatedPose();
            bones.Clear();
            isBound = false;
            clip = null;
            constraintSession = null;
            renderedConstraintPose = null;
            lastConstraintObservation = null;
            constraintObservedFrame = -1;
            target = avatar;
            boundTarget = avatar;
            // Scene-prefab DefaultTransformStates can lazily capture a playing idle pose.
            // Use a baked model-asset reference or genuine runtime-import local defaults.
            boundVrmEnabled = avatar.enabled;
            var reference = ArdyAvatarRestPose.Find(avatar);
            var imported = avatar.GetComponent<UniGLTF.RuntimeGltfInstance>();
            // A disabled scene VRM has no runtime work to do; don't construct one from its idle pose.
            var rig = Application.isPlaying && avatar.isActiveAndEnabled ? avatar.Runtime.ControlRig : null;
            boundRig = rig;
            usesControlRig = rig != null;
            string conflict = RuntimeConflict();
            if (conflict != null) throw new InvalidOperationException(conflict);
            if (rig == null && reference == null && imported == null)
                throw new InvalidOperationException("缺少模型参考姿态。请先用 Bake ARDY Avatar Rest Poses 从模型资产生成，不能把当前待机姿态当作参考。");

            // Global rotations collapse all four source spine segments into the available target spine.
            // Missing Chest/UpperChest is supported because descendant orientations remain global.
            Add(HumanBodyBones.Spine, "Spine");
            Add(HumanBodyBones.Chest, "Spine2");
            Add(HumanBodyBones.UpperChest, "Spine3");
            Add(HumanBodyBones.Neck, "Neck");
            Add(HumanBodyBones.Head, "Head");
            Add(HumanBodyBones.LeftShoulder, "LeftShoulder");
            Add(HumanBodyBones.LeftUpperArm, "LeftArm");
            Add(HumanBodyBones.LeftLowerArm, "LeftForeArm");
            Add(HumanBodyBones.LeftHand, "LeftHand");
            Add(HumanBodyBones.RightShoulder, "RightShoulder");
            Add(HumanBodyBones.RightUpperArm, "RightArm");
            Add(HumanBodyBones.RightLowerArm, "RightForeArm");
            Add(HumanBodyBones.RightHand, "RightHand");
            if (bones.Find(b => b.humanoid == HumanBodyBones.LeftUpperArm) == null ||
                bones.Find(b => b.humanoid == HumanBodyBones.RightUpperArm) == null ||
                bones.Find(b => b.humanoid == HumanBodyBones.Head) == null)
            {
                bones.Clear();
                throw new InvalidOperationException("The selected avatar is missing required humanoid arm/head bones.");
            }
            bones.Sort((a, b) => Depth(a.transform).CompareTo(Depth(b.transform)));
            chestAnchor = bones.Find(b => b.humanoid == HumanBodyBones.UpperChest)
                ?? bones.Find(b => b.humanoid == HumanBodyBones.Chest)
                ?? bones.Find(b => b.humanoid == HumanBodyBones.Spine);
            isBound = true;
            Status = $"已绑定 {avatar.name} · {bones.Count} 骨骼 · {(usesControlRig ? "ControlRig" : "Humanoid")} · 待机";

            void Add(HumanBodyBones human, string source)
            {
                var original = avatar.Humanoid.GetBoneTransform(human);
                if (original == null) return;
                var controlled = rig != null ? rig.GetBoneTransform(human) : original;
                if (controlled == null) return;
                Quaternion restInRoot;
                if (rig != null)
                {
                    // Control bones start at world identity when the rig is constructed,
                    // which can be later than import, after the avatar has turned.
                    var rigRoot = rig.GetBoneTransform(HumanBodyBones.Hips).parent;
                    restInRoot = Quaternion.Inverse(avatar.transform.rotation) * rigRoot.rotation;
                }
                else
                {
                    if (reference != null) restInRoot = reference.Get(human);
                    else
                    {
                        restInRoot = Quaternion.identity;
                        // InitialTransformStates stores glTF nodes, not necessarily the wrapper root.
                        // Compose saved LOCAL rotations to remain independent of later root movement.
                        for (var node = original; node != avatar.transform; node = node.parent)
                        {
                            if (node == null || !imported.InitialTransformStates.TryGetValue(node, out var state))
                                throw new InvalidOperationException($"Imported rest pose chain missing for {human}.");
                            restInRoot = state.LocalRotation * restInRoot;
                        }
                    }
                }
                bones.Add(new Bone { humanoid = human, sourceName = source, transform = controlled,
                    restInRoot = restInRoot });
            }
        }

        public void Play(TextAsset asset, ArdyMotionMask bodyMask)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            PlayClip(ArdyMotionClip.Parse(asset.text), bodyMask, false, true);
        }

        public void PlayStream(ArdyMotionClip firstChunk, ArdyMotionMask bodyMask, bool complete)
        {
            PlayClip(firstChunk, bodyMask, true, complete);
        }

        private void PlayClip(ArdyMotionClip next, ArdyMotionMask bodyMask, bool streamed, bool complete)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            next.Validate();
            RequireActivePlayback();
            if (target == null || !isBound) throw new InvalidOperationException("Bind an avatar before playing.");
            if (target != boundTarget) throw new InvalidOperationException("The avatar changed; bind the new target before playing.");
            if (!Enum.IsDefined(typeof(ArdyMotionMask), bodyMask)) throw new ArgumentOutOfRangeException(nameof(bodyMask));
            if (next.source.rotationApplication == "additive-local" && bodyMask != ArdyMotionMask.Head)
                throw new ArgumentException("The additive conversational profile requires the Head mask.");
            int nextChestIndex = Array.IndexOf(next.jointNames, "Spine3");
            if (bodyMask != ArdyMotionMask.UpperBody && (chestAnchor == null || nextChestIndex < 0))
                throw new InvalidOperationException("A chest reference is required for a partial body action.");
            string conflict = RuntimeConflict();
            if (conflict != null) throw new InvalidOperationException(conflict);
            var indices = new int[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                indices[i] = Array.IndexOf(next.jointNames, bones[i].sourceName);
                if (indices[i] < 0) throw new ArgumentException($"Motion is missing {bones[i].sourceName}.");
            }
            for (int i = 0; i < bones.Count; i++)
            {
                var bone = bones[i];
                bool nextIncluded = Included(bone.humanoid, bodyMask);
                // Keep outgoing additive bones in offset space when the next clip owns
                // another mask. Incoming absolute bones retain the existing pose crossfade.
                bone.blendAsDelta = (nextIncluded && next.source.rotationApplication == "additive-local")
                    || (IsPlaying && !nextIncluded && bone.blendAsDelta);
                bone.previousMask = IsPlaying && (bone.previousMask ||
                    (constraintSession != null ? constraintSession.Owns(bone.humanoid) : Included(bone.humanoid, mask)));
                bone.transitionFrom = IsPlaying && bone.hasRendered ? bone.lastApplied : bone.transform.localRotation;
                bone.transitionDeltaFrom = IsPlaying && bone.hasRendered
                    ? Quaternion.Inverse(bone.baseline) * bone.lastApplied : Quaternion.identity;
                bone.sourceIndex = indices[i];
            }
            InvalidateMotionObservation();
            clip = next;
            constraintSession = null;
            lastConstraintObservation = null;
            sourceChestIndex = nextChestIndex;
            mask = bodyMask;
            time = transitionTime = 0;
            stopping = false;
            streamingSession = streamed;
            streamComplete = complete;
            streamUnderrunSeconds = 0;
            StreamFailure = null;
            Status = $"播放 {clip.id} · {bodyMask} · {(usesControlRig ? "ControlRig" : "Humanoid")}";
        }

        /// <summary>Procedural geometric constraints, with the same bone owner as clip/stream playback.</summary>
        public void PlayConstrained(ArdyControlPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            plan = plan.Copy();
            plan.Validate();
            RequireActivePlayback();
            if (target == null || !isBound || target != boundTarget || chestAnchor == null)
                throw new InvalidOperationException("Bind a stable avatar with a chest before playing constraints.");
            string conflict = RuntimeConflict();
            if (conflict != null) throw new InvalidOperationException(conflict);
            // The observer caches the previous rendered frame even while idle. Do not sample
            // Animator's restored Update pose for a same-frame hold -> current handoff.
            var rendered = renderedConstraintPose ?? CaptureConstraintPose();
            if (plan.leftPalm != "keep") RequirePalmMapping(true);
            if (plan.rightPalm != "keep") RequirePalmMapping(false);
            var bindings = new Dictionary<HumanBodyBones, ArdyConstraintBone>();
            foreach (var bone in bones)
                bindings.Add(bone.humanoid, new ArdyConstraintBone { id = bone.humanoid,
                    controlled = bone.transform, actual = target.Humanoid.GetBoneTransform(bone.humanoid),
                    restInRoot = bone.restInRoot });
            Vector3? partnerPosition = null;
            string partnerSource = "not-requested";
            if (plan.leftPalm == "partner" || plan.rightPalm == "partner")
            {
                Transform partner = interactionTarget;
                if (partner != null) partnerSource = "bound-interaction-target";
                else
                {
                    var camera = Camera.main;
                    if (camera != null) { partner = camera.transform; partnerSource = "main-camera"; }
                }
                if (partner != null) partnerPosition = partner.position;
                else partnerSource = "fallback-character-forward";
            }
            var next = new ArdyConstraintSession(plan, target.transform, chestAnchor.transform, bindings, rendered,
                partnerPosition, partnerSource);
            foreach (var bone in bones)
            {
                bone.previousMask = IsPlaying && (bone.previousMask ||
                    (constraintSession != null ? constraintSession.Owns(bone.humanoid) : Included(bone.humanoid, mask)));
                bone.transitionFrom = IsPlaying && bone.hasRendered ? bone.lastApplied : bone.transform.localRotation;
                bone.transitionDeltaFrom = Quaternion.identity;
                bone.blendAsDelta = false;
            }
            InvalidateMotionObservation();
            clip = null;
            constraintSession = next;
            if (next.IsAdaptive)
                foreach (var bone in bones)
                    if (next.Owns(bone.humanoid) && rendered.bones.TryGetValue(bone.humanoid, out var observed))
                        bone.transitionFrom = Quaternion.Inverse(observed.controlledParentRotation) * observed.controlledRotation;
            lastConstraintObservation = null;
            time = transitionTime = 0;
            stopping = false;
            streamingSession = streamComplete = false;
            streamUnderrunSeconds = 0;
            StreamFailure = null;
            Status = "程序化约束动作 · 准备并观测目标姿态";
        }

        /// <summary>
        /// Called after VRM LateUpdate by ArdyConstraintObserver (execution order 12000).
        /// It reads the actual humanoid and caches it even when idle. Duplicate calls in
        /// a frame never increase the preparation stability timer.
        /// </summary>
        public ArdyConstraintObservation ObserveConstraintPose(float deltaTime)
        {
            if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime < 0)
                throw new ArgumentOutOfRangeException(nameof(deltaTime));
            if (!isBound || target == null || target != boundTarget || chestAnchor == null) return ConstraintObservation;
            if (constraintObservedFrame == Time.frameCount) return ConstraintObservation;
            constraintObservedFrame = Time.frameCount;
            renderedConstraintPose = CaptureConstraintPose();
            if (constraintSession != null)
                lastConstraintObservation = constraintSession.Observe(renderedConstraintPose, deltaTime);
            return ConstraintObservation;
        }

        private ArdyConstraintPose CaptureConstraintPose()
        {
            var chest = target.Humanoid.GetBoneTransform(chestAnchor.humanoid);
            var snapshot = new ArdyConstraintPose { frame = Time.frameCount,
                chestRotation = chest.rotation, controlledChestRotation = chestAnchor.transform.rotation,
                rootRotation = target.transform.rotation };
            foreach (var bone in bones)
            {
                var actual = target.Humanoid.GetBoneTransform(bone.humanoid);
                if (actual == null || bone.transform == null) continue;
                snapshot.bones.Add(bone.humanoid, new ArdyConstraintBonePose {
                    controlledRotation = bone.transform.rotation, controlledPosition = bone.transform.position,
                    actualRotation = actual.rotation, actualPosition = actual.position,
                    controlledParentRotation = bone.transform.parent == null ? Quaternion.identity : bone.transform.parent.rotation,
                    actualParentRotation = actual.parent == null ? Quaternion.identity : actual.parent.rotation });
            }
            // These are observed geometry only, never additional Player-owned bones.
            foreach (var id in new[] { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal,
                HumanBodyBones.LeftLittleProximal, HumanBodyBones.RightIndexProximal,
                HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightLittleProximal })
            {
                var actual = target.Humanoid.GetBoneTransform(id);
                if (actual != null) snapshot.bones[id] = new ArdyConstraintBonePose {
                    actualRotation = actual.rotation, actualPosition = actual.position };
            }
            return snapshot;
        }

        private void RequirePalmMapping(bool left)
        {
            foreach (var id in left
                ? new[] { HumanBodyBones.LeftIndexProximal, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftLittleProximal }
                : new[] { HumanBodyBones.RightIndexProximal, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightLittleProximal })
                if (target.Humanoid.GetBoneTransform(id) == null)
                    throw new InvalidOperationException("palm-basis-unsupported: missing actual humanoid " + id);
        }

        /// <summary>Extend one timeline without restarting its pose blend or replaying history.</summary>
        public void AppendStreamChunk(ArdyMotionClip chunk, int startFrame, bool complete)
        {
            if (!CanAppendStream) throw new InvalidOperationException("The motion stream is no longer accepting frames.");
            if (chunk == null) throw new ArgumentNullException(nameof(chunk));
            chunk.Validate();
            if (startFrame != clip.frames.Length || chunk.frames.Length != ArdyLiveProtocol.FramesPerChunk ||
                startFrame + chunk.frames.Length > ArdyLiveProtocol.FramesPerChunk * ArdyLiveProtocol.MaxChunks ||
                chunk.fps != clip.fps || chunk.source.rotationApplication != clip.source.rotationApplication)
                throw new ArgumentException("Out-of-order, oversized or incompatible motion chunk.");
            for (int i = 0; i < clip.jointNames.Length; i++)
                if (chunk.jointNames[i] != clip.jointNames[i] || chunk.jointParents[i] != clip.jointParents[i] ||
                    Quaternion.Angle(chunk.restGlobalRotations[i], clip.restGlobalRotations[i]) > 0.1f)
                    throw new ArgumentException("Motion chunk skeleton changed.");
            var frames = new ArdyMotionFrame[startFrame + chunk.frames.Length];
            Array.Copy(clip.frames, frames, startFrame);
            Array.Copy(chunk.frames, 0, frames, startFrame, chunk.frames.Length);
            clip.frames = frames;
            streamComplete = complete;
            streamUnderrunSeconds = 0;
        }

        /// <summary>
        /// Project the currently rendered upper body into canonical Core27 rotations.
        /// Root/legs are neutral, hand endpoints follow the hand, and Spine1 is interpolated.
        /// This intentionally does not claim to capture the avatar's full-body contacts or translation.
        /// </summary>
        public ArdyMotionFrame CaptureUpperBodyHistoryFrame()
        {
            if (!isBound || target == null || target != boundTarget)
                throw new InvalidOperationException("Bind a stable avatar before capturing motion history.");
            string conflict = RuntimeConflict();
            if (conflict != null) throw new InvalidOperationException(conflict);
            var result = new Quaternion[ArdyLiveProtocol.JointNames.Length];
            for (int i = 0; i < result.Length; i++) result[i] = Quaternion.identity;
            var inverseRoot = Quaternion.Inverse(target.transform.rotation);
            foreach (var bone in bones)
            {
                int index = Array.IndexOf(ArdyLiveProtocol.JointNames, bone.sourceName);
                if (index >= 0 && bone.transform != null)
                    result[index] = (inverseRoot * bone.transform.rotation * Quaternion.Inverse(bone.restInRoot)).normalized;
            }
            // Optional humanoid chest links collapse to the closest available parent.
            if (bones.Find(b => b.sourceName == "Spine2") == null) result[3] = result[1];
            if (bones.Find(b => b.sourceName == "Spine3") == null) result[4] = result[3];
            result[2] = Quaternion.Slerp(result[1], result[3], 0.5f);
            result[11] = result[12] = result[10];
            result[17] = result[18] = result[16];
            return new ArdyMotionFrame { globalRotations = result };
        }

        public void Stop() => BeginStop(true);

        private void BeginStop(bool interrupted)
        {
            if (!IsPlaying || stopping) return;
            if (interrupted && clip != null) MotionPlaybackWasInterrupted = true;
            foreach (var bone in bones)
            {
                bone.previousMask |= constraintSession != null
                    ? constraintSession.Owns(bone.humanoid) : Included(bone.humanoid, mask);
                bone.transitionFrom = bone.hasRendered ? bone.lastApplied : bone.transform.localRotation;
                bone.transitionDeltaFrom = bone.hasRendered
                    ? Quaternion.Inverse(bone.baseline) * bone.lastApplied : Quaternion.identity;
            }
            stopping = true;
            constraintSession?.BeginReturn("stopped");
            transitionTime = 0;
            Status = "平滑回到当前待机动画";
        }

        private void Update() => RestoreAnimatedPose();
        private void LateUpdate() => Tick(Time.deltaTime);

        /// <summary>Called before Animator evaluation. Also available to deterministic preview tests.</summary>
        public void RestoreAnimatedPose()
        {
            foreach (var bone in bones)
            {
                if (!bone.hasApplied || bone.transform == null) continue;
                bone.transform.localRotation = bone.baseline;
                bone.hasApplied = false;
            }
        }

        /// <summary>Called after Animator evaluation and before VRM LateUpdate.</summary>
        public void Tick(float deltaTime)
        {
            generatedAppliedFrame = generatedAppliedSerial = -1;
            if (!IsPlaying) return;
            if (target == null) { MotionPlaybackWasInterrupted = true; clip = null; constraintSession = null; return; }
            if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime < 0)
                throw new ArgumentOutOfRangeException(nameof(deltaTime));
            // A manual preview, switch or stop can evaluate twice in one frame.
            // Never capture our earlier overlay as the Animator baseline.
            RestoreAnimatedPose();
            if (target != boundTarget)
            {
                MotionPlaybackWasInterrupted = true;
                clip = null;
                constraintSession = null;
                Status = "角色已变更，请重新绑定";
                return;
            }
            // Re-check ownership and update order if the avatar configuration changes during playback.
            string conflict = RuntimeConflict();
            if (conflict != null)
            {
                MotionPlaybackWasInterrupted = true;
                RestoreAnimatedPose();
                clip = null;
                if (constraintSession != null)
                {
                    constraintSession.Fail(conflict);
                    lastConstraintObservation = constraintSession.Observation.Copy();
                    constraintSession = null;
                }
                Status = "播放结束：" + conflict;
                return;
            }
            if (constraintSession != null)
            {
                TickConstrained(deltaTime);
                return;
            }
            transitionTime += deltaTime;
            float duration = Mathf.Max(0.05f, stopping ? blendOutSeconds : blendInSeconds);
            float blend = Mathf.SmoothStep(0, 1, Mathf.Clamp01(transitionTime / duration));
            // Capture the animated chest before writing any current or outgoing mask.
            // The generated clip's body turn must not survive when only an arm/head is played.
            Quaternion localActionFrame = target.transform.rotation;
            if (!stopping && mask != ArdyMotionMask.UpperBody)
                localActionFrame = chestAnchor.transform.rotation * Quaternion.Inverse(chestAnchor.restInRoot)
                    * Quaternion.Inverse(clip.SampleDelta(sourceChestIndex, time));
            foreach (var bone in bones)
            {
                bool include = Included(bone.humanoid, mask);
                if ((!include && !bone.previousMask) || bone.transform == null)
                {
                    bone.hasRendered = false;
                    continue;
                }
                // The caller restores the previous override before sampling the current animated baseline.
                bone.baseline = bone.transform.localRotation;
                if (bone.blendAsDelta)
                {
                    Quaternion desiredDelta = Quaternion.identity;
                    if (!stopping && include)
                        desiredDelta = Quaternion.Inverse(bone.restInRoot)
                            * clip.SampleLocalDelta(bone.sourceIndex, time) * bone.restInRoot;
                    // Blend offsets over this frame's Animator pose, including fade-out
                    // and outgoing Head masks. Identity never delays the underlying animation.
                    bone.lastApplied = bone.baseline
                        * Quaternion.Slerp(bone.transitionDeltaFrom, desiredDelta, blend);
                }
                else
                {
                    Quaternion desired = bone.baseline;
                    if (!stopping && include)
                    {
                        Quaternion world = localActionFrame * clip.SampleDelta(bone.sourceIndex, time) * bone.restInRoot;
                        desired = bone.transform.parent == null ? world : Quaternion.Inverse(bone.transform.parent.rotation) * world;
                    }
                    bone.lastApplied = Quaternion.Slerp(bone.transitionFrom, desired, blend);
                }
                bone.transform.localRotation = bone.lastApplied;
                bone.hasApplied = true;
                bone.hasRendered = true;
            }
            if (stopping && blend >= 1)
            {
                RestoreAnimatedPose();
                clip = null;
                Status = "待机 · 身体控制已交回原动画";
                foreach (var bone in bones) { bone.previousMask = false; bone.hasRendered = false; }
                return;
            }
            if (!stopping)
            {
                generatedAppliedFrame = Time.frameCount;
                generatedAppliedSerial = MotionPlaybackSerial;
                time += deltaTime;
                if (blend >= 1) foreach (var bone in bones) bone.previousMask = false;
                if (time >= clip.Duration)
                {
                    if (streamingSession && !streamComplete)
                    {
                        // Hold only briefly; do not skip future samples while waiting for a window.
                        time = clip.Duration;
                        streamUnderrunSeconds += deltaTime;
                        if (streamUnderrunSeconds > 0.15f)
                        {
                            StreamFailure = "实时动作缓冲欠载，已停止并回到待机";
                            Stop();
                        }
                    }
                    else
                    {
                        MotionPlaybackCompletedNaturally = true;
                        BeginStop(false);
                    }
                }
            }
        }

        private void TickConstrained(float deltaTime)
        {
            if (!stopping) constraintSession.Advance(deltaTime);
            if (constraintSession.WantsStop && !stopping) Stop();
            transitionTime += deltaTime;
            float fade = Mathf.SmoothStep(0, 1, Mathf.Clamp01(transitionTime /
                Mathf.Max(0.05f, stopping ? blendOutSeconds : blendInSeconds)));
            float preparation = constraintSession.PreparationBlend;
            foreach (var bone in bones)
            {
                bool owns = constraintSession.TryGetTarget(bone.humanoid, out var world);
                if ((!owns && !bone.previousMask) || bone.transform == null)
                {
                    bone.hasRendered = false;
                    continue;
                }
                bone.baseline = bone.transform.localRotation;
                Quaternion desired = bone.baseline;
                if (owns && !stopping)
                    desired = constraintSession.TryGetPreparationLocalTarget(bone.humanoid, out var preparedLocal)
                        ? preparedLocal : bone.transform.parent == null ? world
                            : Quaternion.Inverse(bone.transform.parent.rotation) * world;
                bone.lastApplied = Quaternion.Slerp(bone.transitionFrom, desired,
                    stopping || !owns ? fade : preparation);
                bone.transform.localRotation = bone.lastApplied;
                bone.hasApplied = bone.hasRendered = true;
            }
            if (stopping && fade >= 1)
            {
                RestoreAnimatedPose();
                constraintSession.FinishReturn();
                lastConstraintObservation = constraintSession.Observation.Copy();
                constraintSession = null;
                foreach (var bone in bones) { bone.previousMask = false; bone.hasRendered = false; }
                Status = "待机 · 程序化约束控制已交回原动画";
                return;
            }
            if (!stopping && fade >= 1)
                foreach (var bone in bones) bone.previousMask = false;
            Status = "程序化约束动作 · " + constraintSession.Phase;
        }

        private void OnDisable()
        {
            InvalidateMotionObservation();
            MotionPlaybackWasInterrupted = true;
            RestoreAnimatedPose();
            clip = null;
            constraintSession = null;
            renderedConstraintPose = null;
            lastConstraintObservation = null;
            constraintObservedFrame = -1;
            foreach (var bone in bones) bone.hasRendered = false;
            Status = "播放器停用，已还原身体姿态";
        }

        private static int Depth(Transform bone)
        {
            int depth = 0;
            for (var parent = bone.parent; parent != null; parent = parent.parent) depth++;
            return depth;
        }

        private void RequireActivePlayback()
        {
            if (Application.isPlaying && !isActiveAndEnabled)
                throw new InvalidOperationException("The motion player must be active and enabled before playback; disabled players cannot queue an action.");
        }

        private string RuntimeConflict()
        {
            if (!Application.isPlaying || target == null) return null;
            if (target.enabled != boundVrmEnabled)
                return "VRM 的启用状态已变更，请重新绑定角色。";
            if (!target.enabled) return null;
            if (target.Runtime.ControlRig != boundRig)
                return "The avatar ControlRig changed; bind it again before playing.";
            if (usesControlRig && !target.isActiveAndEnabled)
                return "ControlRig requires its VRM instance to be active; the existing avatar configuration was left unchanged.";
            if (target.enabled && target.UpdateType != Vrm10Instance.UpdateTypes.LateUpdate)
                return "ARDY preview requires VRM LateUpdate; the existing avatar configuration was left unchanged.";
            if (target.enabled && target.Runtime.VrmAnimation != null)
                return "VRM Animation currently owns this avatar.";
            return null;
        }

        private static bool Included(HumanBodyBones bone, ArdyMotionMask bodyMask)
        {
            if (bodyMask == ArdyMotionMask.UpperBody) return true;
            if (bodyMask == ArdyMotionMask.Head) return bone == HumanBodyBones.Neck || bone == HumanBodyBones.Head;
            if (bodyMask == ArdyMotionMask.LeftArm)
                return bone == HumanBodyBones.LeftShoulder || bone == HumanBodyBones.LeftUpperArm ||
                       bone == HumanBodyBones.LeftLowerArm || bone == HumanBodyBones.LeftHand;
            return bone == HumanBodyBones.RightShoulder || bone == HumanBodyBones.RightUpperArm ||
                   bone == HumanBodyBones.RightLowerArm || bone == HumanBodyBones.RightHand;
        }
    }
}
