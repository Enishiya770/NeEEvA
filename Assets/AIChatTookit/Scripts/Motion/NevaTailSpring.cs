using System;
using System.Collections.Generic;
using System.Linq;
using UniGLTF.SpringBoneJobs;
using UniGLTF.SpringBoneJobs.Blittables;
using UniGLTF.SpringBoneJobs.InputPorts;
using UniVRM10;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>Independent FoxTail spring; never starts the avatar's expression/look-at runtime.</summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(12000)]
    public sealed class NevaTailSpring : MonoBehaviour
    {
        [SerializeField] private Vrm10Instance target;
        [SerializeField, HideInInspector] private Transform[] joints = Array.Empty<Transform>();
        [SerializeField, HideInInspector] private Quaternion[] sourceLocalRotations = Array.Empty<Quaternion>();
        [SerializeField, HideInInspector] private Quaternion[] restLocalRotations = Array.Empty<Quaternion>();

        [Header("Rest silhouette: root to tip, degrees below horizontal")]
        public float[] downwardAngles = { 25f, 40f, 60f, 65f, 50f };
        [Header("Spring")]
        [Min(0.01f)] public float rootStiffness = 1.1f;
        [Min(0.01f)] public float tipStiffness = 0.45f;
        [Min(0f)] public float gravityPower = 0.12f;
        [Range(0f, 1f)] public float dragForce = 0.25f;
        [Header("Idle movement")]
        [Tooltip("Sideways bend of the distal tail. The first two segments stay anchored; bend increases towards the tip.")]
        [Range(0f, 30f)] public float idleSwayDegrees = 12f;
        [Tooltip("Small secondary bend of the distal tail; does not lift or rotate the attachment.")]
        [Range(0f, 12f)] public float idleLiftDegrees = 1f;
        [Min(0.5f)] public float idleSwayPeriod = 3.2f;
        [Header("Body clearance")]
        public bool bodyCollisions = true;
        [Min(0.001f)] public float jointRadius = 0.025f;

        public bool IsInitialized => scheduler != null;
        public Vrm10Instance Target => target;
        public IReadOnlyList<Transform> Joints => joints;

        private FastSpringBoneBuffer buffer;
        private FastSpringBoneBufferCombiner combiner;
        private FastSpringBoneScheduler scheduler;
        private Transform anchor;
        private Vector3 lastAnchorPosition, lastScale;
        private Quaternion lastAnchorRotation;
        private float accumulatedTime, swayTime;
        private bool warnedAboutOwner, pendingRebuild;
        private const float Step = 1f / 90f;
        private const int FirstSimulatedJoint = 2;
        private static readonly float[] BendWeights = { 0.35f, 0.65f, 1f };
        private readonly Vector3[] sideBendAxes = new Vector3[3];
        private readonly Vector3[] liftBendAxes = new Vector3[3];

        /// <summary>Author and serialize a rest curve. Safe to call repeatedly in Edit Mode.</summary>
        public void Configure(Vrm10Instance avatar)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            var transforms = avatar.GetComponentsInChildren<Transform>(true);
            var chain = new Transform[6];
            for (int i = 0; i < chain.Length; ++i)
            {
                string name = i == 5 ? "J_Opt_C_FoxTail5_end_01" : "J_Opt_C_FoxTail" + (i + 1) + "_01";
                chain[i] = transforms.FirstOrDefault(t => t.name == name);
                if (chain[i] == null || (i > 0 && chain[i].parent != chain[i - 1]))
                    throw new InvalidOperationException("Expected NEVA's continuous FoxTail chain: " + name);
            }
            bool sameChain = target == avatar && joints.Length == chain.Length &&
                joints.SequenceEqual(chain) && sourceLocalRotations.Length == chain.Length;
            StopSolver(false);
            target = avatar;
            joints = chain;
            if (!sameChain) sourceLocalRotations = chain.Select(t => t.localRotation).ToArray();
            ApplyRotations(sourceLocalRotations);

            Vector3 up = avatar.transform.up;
            Vector3 backward = Vector3.ProjectOnPlane(joints[5].position - joints[0].position, up).normalized;
            if (backward.sqrMagnitude < 0.5f) backward = -avatar.transform.forward;
            restLocalRotations = new Quaternion[chain.Length];
            for (int i = 0; i < chain.Length - 1; ++i)
            {
                float angle = downwardAngles != null && i < downwardAngles.Length ? downwardAngles[i] : 50f;
                float radians = Mathf.Clamp(angle, -10f, 85f) * Mathf.Deg2Rad;
                Vector3 direction = backward * Mathf.Cos(radians) - up * Mathf.Sin(radians);
                Quaternion aim = Quaternion.FromToRotation(chain[i + 1].position - chain[i].position, direction);
                chain[i].rotation = aim * chain[i].rotation;
                restLocalRotations[i] = chain[i].localRotation;
            }
            restLocalRotations[5] = sourceLocalRotations[5];
        }

        public void Bind(Vrm10Instance avatar)
        {
            Configure(avatar);
            if (Application.isPlaying && isActiveAndEnabled && !HasAutomaticVrmOwner()) StartSolver();
        }

        [ContextMenu("Rebuild Tail / Apply Settings")]
        public void Rebuild()
        {
            Bind(target != null ? target : GetComponent<Vrm10Instance>());
        }

        private void OnEnable()
        {
            if (!Application.isPlaying) return;
            var avatar = target != null ? target : GetComponent<Vrm10Instance>();
            if (avatar == null) return;
            // Authored rest rotations are relative to the hips, independent of the current animation.
            if (target == avatar && joints.Length == 6 && restLocalRotations.Length == 6 && joints.All(t => t != null))
            {
                ApplyRotations(restLocalRotations);
                if (!HasAutomaticVrmOwner()) StartSolver();
            }
            else Bind(avatar);
        }

        private bool HasAutomaticVrmOwner()
        {
            bool conflict = target != null && target.isActiveAndEnabled &&
                target.UpdateType != Vrm10Instance.UpdateTypes.None;
            if (conflict && !warnedAboutOwner)
                Debug.LogWarning("[NEVA Tail] Automatic VRM updates are active; the independent tail solver is paused to avoid two writers. Keep the existing disabled VRM setup when using this component.", this);
            warnedAboutOwner = conflict;
            return conflict;
        }

        private void StartSolver()
        {
            if (target == null || joints.Length != 6 || joints.Any(t => t == null)) return;
            ApplyRotations(restLocalRotations);
            // The two short attachment segments are rigidly carried by the hips. Only the
            // three long distal segments simulate and bend, so the bulky root stays quiet.
            var physicsJoints = new FastSpringBoneJoint[joints.Length - FirstSimulatedJoint];
            for (int i = 0; i < physicsJoints.Length; ++i)
            {
                int jointIndex = i + FirstSimulatedJoint;
                physicsJoints[i] = new FastSpringBoneJoint
                {
                    Transform = joints[jointIndex], DefaultLocalRotation = restLocalRotations[jointIndex],
                    Joint = new BlittableJointMutable
                    {
                        stiffnessForce = Mathf.Lerp(Mathf.Max(0.01f, rootStiffness), Mathf.Max(0.01f, tipStiffness), i / 2f),
                        gravityDir = Vector3.down, gravityPower = Mathf.Max(0f, gravityPower),
                        dragForce = Mathf.Clamp01(dragForce), radius = Mathf.Max(0.001f, jointRadius)
                    }
                };
                if (i < BendWeights.Length)
                {
                    Vector3 segment = (joints[jointIndex + 1].position - joints[jointIndex].position).normalized;
                    var parent = joints[jointIndex].parent;
                    // Bend across the segment, rather than twisting a hanging tail around
                    // the avatar's vertical axis. Cache axes in the authored parent frame.
                    sideBendAxes[i] = parent.InverseTransformDirection(Vector3.Cross(segment, target.transform.right).normalized);
                    liftBendAxes[i] = parent.InverseTransformDirection(target.transform.right);
                }
            }
            buffer = new FastSpringBoneBuffer(target.transform, new[] { new FastSpringBoneSpring
            {
                center = null, joints = physicsJoints,
                colliders = bodyCollisions ? BuildBodyColliders() : Array.Empty<FastSpringBoneCollider>()
            } });
            combiner = new FastSpringBoneBufferCombiner();
            combiner.Register(buffer, null);
            combiner.ReconstructIfDirty(default).Complete();
            combiner.Combined.SetModelLevel(target.transform, new BlittableModelLevel { SupportsScalingAtRuntime = true });
            scheduler = new FastSpringBoneScheduler(combiner);
            anchor = joints[FirstSimulatedJoint].parent;
            RememberAnchor();
            accumulatedTime = 0f;
            pendingRebuild = false;
        }

        private FastSpringBoneCollider[] BuildBodyColliders()
        {
            var colliders = new List<FastSpringBoneCollider>();
            var hips = target.Humanoid.GetBoneTransform(HumanBodyBones.Hips);
            var left = target.Humanoid.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            var right = target.Humanoid.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            if (hips != null && left != null && right != null)
            {
                Vector3 a = hips.InverseTransformPoint(left.position);
                Vector3 b = hips.InverseTransformPoint(right.position);
                // The capsule spans the pelvis; keeping it forward leaves room for the tail's attachment.
                float width = Vector3.Distance(a, b);
                Vector3 lift = hips.InverseTransformDirection(target.transform.up) * width * 0.15f;
                Vector3 forward = hips.InverseTransformDirection(target.transform.forward) * width * 0.08f;
                colliders.Add(Capsule(hips, a + lift + forward, b + lift + forward, width * 0.42f));
            }
            AddLeg(colliders, left, target.Humanoid.GetBoneTransform(HumanBodyBones.LeftLowerLeg));
            AddLeg(colliders, right, target.Humanoid.GetBoneTransform(HumanBodyBones.RightLowerLeg));
            return colliders.ToArray();
        }

        private static void AddLeg(List<FastSpringBoneCollider> colliders, Transform top, Transform bottom)
        {
            if (top == null || bottom == null) return;
            Vector3 end = top.InverseTransformPoint(bottom.position);
            colliders.Add(Capsule(top, end * 0.08f, end * 0.85f, end.magnitude * 0.18f));
        }

        private static FastSpringBoneCollider Capsule(Transform bone, Vector3 a, Vector3 b, float radius)
        {
            return new FastSpringBoneCollider
            {
                Transform = bone,
                Collider = new BlittableCollider { colliderType = BlittableColliderType.Capsule,
                    offset = a, tailOrNormal = b, radius = Mathf.Max(0.001f, radius) }
            };
        }

        private void LateUpdate()
        {
            if (target == null) { StopSolver(false); return; }
            if (HasAutomaticVrmOwner()) { StopSolver(false); return; }
            if (!IsInitialized || pendingRebuild) { StopSolver(false); StartSolver(); }
            if (!IsInitialized || Time.deltaTime <= 0f) return;
            if (joints.Any(t => t == null)) { StopSolver(false); return; }
            for (int i = 0; i < FirstSimulatedJoint; ++i)
                joints[i].localRotation = restLocalRotations[i];
            float scale = Mathf.Max(0.01f, target.transform.lossyScale.magnitude / Mathf.Sqrt(3f));
            if (Vector3.Distance(anchor.position, lastAnchorPosition) > 0.75f * scale ||
                Quaternion.Angle(anchor.rotation, lastAnchorRotation) > 65f ||
                (target.transform.lossyScale - lastScale).sqrMagnitude > 0.000001f || Time.deltaTime > 0.25f)
            {
                // Teleports and large frame stalls must not launch the tail with an artificial impulse.
                StopSolver(false);
                StartSolver();
            }
            RememberAnchor();
            accumulatedTime += Mathf.Min(Time.deltaTime, 0.05f);
            while (accumulatedTime >= Step)
            {
                swayTime += Step;
                float phase = swayTime * Mathf.PI * 2f / Mathf.Max(0.5f, idleSwayPeriod);
                var logics = combiner.Combined.Logics;
                for (int i = 0; i < BendWeights.Length; ++i)
                {
                    // Increasing curvature and a small travelling delay put the largest
                    // visible displacement at the tip while the attachment stays anchored.
                    float delayedPhase = phase - i * 0.25f;
                    float angle = Mathf.Sin(delayedPhase) * idleSwayDegrees * BendWeights[i];
                    float lift = Mathf.Sin(delayedPhase + Mathf.PI * 0.35f) * idleLiftDegrees * BendWeights[i];
                    var segment = logics[i];
                    segment.localRotation = Quaternion.AngleAxis(angle, sideBendAxes[i]) *
                        Quaternion.AngleAxis(lift, liftBendAxes[i]) * restLocalRotations[i + FirstSimulatedJoint];
                    logics[i] = segment;
                }
                scheduler.Schedule(Step).Complete();
                accumulatedTime -= Step;
            }
        }

        private void RememberAnchor()
        {
            lastAnchorPosition = anchor.position;
            lastAnchorRotation = anchor.rotation;
            lastScale = target.transform.lossyScale;
        }

        private void ApplyRotations(Quaternion[] rotations)
        {
            if (rotations == null || joints == null || rotations.Length != joints.Length) return;
            for (int i = 0; i < joints.Length; ++i)
                if (joints[i] != null) joints[i].localRotation = rotations[i];
        }

        private void StopSolver(bool restoreRest)
        {
            scheduler?.Dispose(); // Scheduler owns the combined buffers.
            scheduler = null;
            combiner?.Dispose();
            combiner = null;
            buffer?.Dispose();
            buffer = null;
            accumulatedTime = 0f;
            if (restoreRest) ApplyRotations(restLocalRotations);
        }

        private void OnValidate() { pendingRebuild = true; }
        private void OnDisable() { StopSolver(!warnedAboutOwner); }
        private void OnDestroy() { StopSolver(false); }
        private void OnDrawGizmosSelected() { if (IsInitialized) combiner.DrawGizmos(); }
    }
}
