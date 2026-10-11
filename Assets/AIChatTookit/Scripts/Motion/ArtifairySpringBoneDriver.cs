// Spring-only adapter for the existing scene's disabled Vrm10Instance setup.
// Copy to Assets/AIChatTookit/Scripts/Motion/ArtifairySpringBoneDriver.cs.
// It never reads Vrm10Instance.Runtime, enables VRM, or writes blendshape weights.
using System;
using System.Collections.Generic;
using System.Linq;
using NeEEvA.Motion;
using UniGLTF.SpringBoneJobs;
using UniGLTF.SpringBoneJobs.Blittables;
using UniGLTF.SpringBoneJobs.InputPorts;
using UniVRM10;
using UnityEngine;

[DisallowMultipleComponent, DefaultExecutionOrder(12010)]
public sealed class ArtifairySpringBoneDriver : MonoBehaviour
{
    [SerializeField] private Vrm10Instance target;
    [Tooltip("Only imported hair/skirt chains are processed, so NEVA's FoxTail component keeps ownership of its tail.")]
    [SerializeField] private bool hairAndSkirtOnly = true;
    [Tooltip("Use a firm attachment and progressively softer cloth tips. Opt in per avatar.")]
    [SerializeField] private bool naturalClothDynamics;
    [Tooltip("Gravity-draped cloth: resolve floor contact at bone length and apply contact friction.")]
    [SerializeField] private bool gravityDrape;
    [Tooltip("Softer tips for the ponytail and side locks; the hair cap stays attached to the head.")]
    [SerializeField] private bool naturalHairDynamics;
    [Tooltip("A light, slowly varying breeze applied as forces inside the spring solver.")]
    [SerializeField] private bool ambientBreeze;
    [SerializeField, Range(0f, .05f)] private float breezeStrength = .022f;
    [SerializeField] private bool bodyClearance;
    private float breezeTime;
    private BreezeJoint[] breezeJoints;
    private struct BreezeJoint
    {
        public BlittableJointMutable rest;
        public float exposure, phase;
        public bool cloth;
        public Transform head, tail;
    }
    [Tooltip("Cloth support plane above this avatar's ground origin, in local meters. Used only by natural cloth dynamics.")]
    [SerializeField, Min(0)] private float clothFloorHeight = .035f;
    private Vector3 simulatedRootPosition;
    private Quaternion simulatedRootRotation;
    private FastSpringBoneBuffer buffer;
    private FastSpringBoneBufferCombiner combiner;
    private FastSpringBoneScheduler scheduler;
    private readonly Dictionary<Transform, Quaternion> restRotations = new Dictionary<Transform, Quaternion>();
    private Vector3 previousRootPosition, previousRootScale;
    private Quaternion previousRootRotation;
    private float accumulator;
    private bool warnedOwner;
    private SkinnedMeshRenderer skirtRenderer;
    private Mesh skirtSourceMesh, skirtRuntimeMesh;
    private Vector3[] skirtRestVertices, skirtCorrectedVertices;
    private int[] skirtSurfaceVertices;
    private Matrix4x4[] skirtSkinMatrices;
    private Matrix4x4[] skirtBindposes;
    private BoneWeight[] skirtBoneWeights;
    private Transform[] skirtSkinBones;
    private FastSpringBoneCollider[] skirtSurfaceColliders;
    private SurfaceCapsule[] skirtWorldCapsules;
    private struct SurfaceCapsule { public Vector3 start, axis; public float inverseLengthSquared, radius; }
    private SurfaceLink[] skirtSurfaceLinks;
    private struct SurfaceLink { public Transform a, b; public float radius, upOffset; }
    private const float Step = 1f / 60f;
    public Vrm10Instance Target => target;
    public bool IsInitialized => scheduler != null;

    // Configure the imported avatar's prefab with Vrm10Instance disabled and
    // UpdateType=None before enabling this driver. Existing BlinkController,
    // Audio2LipScript and eye tracking retain their existing ownership.
    public void Configure(Vrm10Instance avatar)
    {
        StopSolver(true);
        target = avatar;
        if (Application.isPlaying && isActiveAndEnabled && !HasOtherOwner()) StartSolver();
    }
    private void Reset() { target = GetComponent<Vrm10Instance>(); }
    private void OnEnable()
    {
        if (!Application.isPlaying) return;
        if (target == null) target = GetComponent<Vrm10Instance>();
        if (target != null && !HasOtherOwner()) StartSolver();
    }
    private IEnumerable<Vrm10InstanceSpringBone.Spring> SelectedSprings()
    {
        if (target == null) return Enumerable.Empty<Vrm10InstanceSpringBone.Spring>();
        return target.SpringBone.Springs.Where(s => !hairAndSkirtOnly ||
            (s.Name ?? "").IndexOf("hair", StringComparison.OrdinalIgnoreCase) >= 0 ||
            (s.Name ?? "").IndexOf("skirt", StringComparison.OrdinalIgnoreCase) >= 0 ||
            (s.Name ?? "").Contains("发") || (s.Name ?? "").Contains("裙"));
    }
    private bool HasOtherOwner()
    {
        bool conflict = target != null && target.isActiveAndEnabled && target.UpdateType != Vrm10Instance.UpdateTypes.None;
        if (!conflict && target != null)
        {
            var ownedJoints = new HashSet<Transform>(SelectedSprings().SelectMany(s => s.Joints).Where(j => j != null).Select(j => j.transform));
            conflict = target.GetComponentsInChildren<NevaTailSpring>(true)
                .Any(t => t.isActiveAndEnabled && t.Target == target && t.Joints.Any(ownedJoints.Contains));
        }
        if (conflict && !warnedOwner)
            Debug.LogWarning("[Artifairy spring] Another VRM/tail updater owns these joints. Spring-only processing is paused.", this);
        warnedOwner = conflict;
        return conflict;
    }
    private void StartSolver()
    {
        if (target == null || IsInitialized) return;
        var springs = SelectedSprings().ToArray();
        if (springs.Length == 0) { Debug.LogWarning("[Artifairy spring] No imported hair/skirt spring chains.", this); return; }
        try
        {
            var input = springs.Select(s => new FastSpringBoneSpring
            {
                center = s.Center,
                joints = s.Joints.Select((j, index) =>
                {
                    if (j == null) throw new InvalidOperationException("Null spring joint");
                    if (!target.DefaultTransformStates.TryGetValue(j.transform, out var state))
                        throw new InvalidOperationException("No rest state for " + j.name);
                    restRotations[j.transform] = state.LocalRotation;
                    bool cloth = naturalClothDynamics && (s.Name ?? "").StartsWith("Skirt", StringComparison.OrdinalIgnoreCase);
                    bool hair = naturalHairDynamics && (s.Name ?? "").StartsWith("Hair", StringComparison.OrdinalIgnoreCase);
                    float along = Mathf.Clamp01(index / (float)Mathf.Max(1, s.Joints.Count - 2));
                    return new FastSpringBoneJoint
                    {
                        Transform = j.transform,
                        DefaultLocalRotation = state.LocalRotation,
                        Joint = new BlittableJointMutable
                        {
                            stiffnessForce = j.m_stiffnessForce * (cloth ? Mathf.Lerp(1.5f, .55f, along) : hair ? Mathf.Lerp(1.35f, .50f, along) : 1f), gravityDir = j.m_gravityDir,
                            gravityPower = j.m_gravityPower * (cloth ? Mathf.Lerp(.5f, 1.1f, along) : hair ? Mathf.Lerp(.5f, 1f, along) : 1f),
                            dragForce = Mathf.Clamp01(j.m_dragForce * (cloth ? Mathf.Lerp(1.2f, .8f, along) : hair ? Mathf.Lerp(1.2f, .85f, along) : 1f)), radius = j.m_jointRadius
                        }
                    };
                }).ToArray(),
                colliders = BuildColliders(s)
            }).ToArray();
            foreach (var spring in input)
                if (spring.joints.Length < 2) throw new InvalidOperationException("A spring needs a head and tail");
            buffer = new FastSpringBoneBuffer(target.transform, input);
            combiner = new FastSpringBoneBufferCombiner();
            combiner.Register(buffer, null);
            combiner.ReconstructIfDirty(default).Complete();
            combiner.Combined.SetModelLevel(target.transform, new BlittableModelLevel { SupportsScalingAtRuntime = true });
            var wind = new List<BreezeJoint>();
            for (int s = 0; s < springs.Length; s++)
            {
                string name = springs[s].Name ?? "";
                float exposure = name.Contains("Ponytail") ? .55f : name.Contains("Bang") ? .08f :
                    name.Contains("Side") ? .30f : name.Contains("RibbonSpread") ? .9f : name.Contains("Train") ? .55f : .22f;
                for (int j = 0; j < input[s].joints.Length - 1; j++)
                {
                    float along = j / (float)Mathf.Max(1, input[s].joints.Length - 2);
                    var p = target.transform.InverseTransformPoint(input[s].joints[j].Transform.position);
                    wind.Add(new BreezeJoint { rest = input[s].joints[j].Joint,
                        cloth = name.StartsWith("Skirt", StringComparison.OrdinalIgnoreCase),
                        head = input[s].joints[j].Transform, tail = input[s].joints[j + 1].Transform,
                        exposure = exposure * Mathf.Lerp(.12f, 1f, along), phase = p.x * 5f + p.z * 2f - along * 1.4f });
                }
            }
            breezeJoints = wind.ToArray();
            breezeTime = 0;
            scheduler = new FastSpringBoneScheduler(combiner);
            accumulator = 0;
            simulatedRootPosition = target.transform.position;
            simulatedRootRotation = target.transform.rotation;
            RememberRoot();
        }
        catch (Exception e) { StopSolver(false); Debug.LogException(e, this); enabled = false; }
    }
    private void LateUpdate()
    {
        if (Application.isPlaying) Simulate(Time.deltaTime);
    }
    /// <summary>Run the production solver explicitly; also supports deterministic editor validation.</summary>
    public void Simulate(float deltaTime)
    {
        if (!isActiveAndEnabled) { StopSolver(!warnedOwner); return; }
        if (target == null) { StopSolver(false); return; }
        if (HasOtherOwner()) { StopSolver(false); return; }
        if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime <= 0) return;
        if (!IsInitialized) StartSolver();
        if (!IsInitialized) return;
        var root = target.transform;
        float scale = Mathf.Max(0.01f, root.lossyScale.magnitude / Mathf.Sqrt(3f));
        if ((root.position - previousRootPosition).sqrMagnitude > 0.75f * scale * 0.75f * scale ||
            Quaternion.Angle(root.rotation, previousRootRotation) > 90f ||
            (root.lossyScale - previousRootScale).sqrMagnitude > 0.000001f || deltaTime > 0.25f)
        {
            StopSolver(true); StartSolver();
            if (!IsInitialized) return;
        }
        RememberRoot();
        accumulator += Mathf.Min(deltaTime, 4f * Step);
        int count = Mathf.Min(4, Mathf.FloorToInt((accumulator + 0.000001f) / Step));
        var finalPosition = root.position;
        var finalRotation = root.rotation;
        // World-space springs need the root pose at each fixed substep; a 30 Hz
        // frame must not inject its whole displacement into the first 60 Hz step.
        try
        {
            for (int step = 1; step <= count; step++)
            {
                if (naturalClothDynamics)
                {
                    float t = step / (float)count;
                    root.SetPositionAndRotation(Vector3.Lerp(simulatedRootPosition, finalPosition, t),
                        Quaternion.Slerp(simulatedRootRotation, finalRotation, t));
                }
                UpdateBreeze();
                scheduler.Schedule(Step).Complete();
                if (gravityDrape) ResolveClothSupport();
                accumulator = Mathf.Max(0, accumulator - Step);
            }
        }
        finally { root.SetPositionAndRotation(finalPosition, finalRotation); }
        if (count > 0) { simulatedRootPosition = finalPosition; simulatedRootRotation = finalRotation; }
        if(bodyClearance)ResolveSkirtSurface();
    }
    private void RememberRoot()
    {
        previousRootPosition = target.transform.position;
        previousRootRotation = target.transform.rotation;
        previousRootScale = target.transform.lossyScale;
    }
    private void UpdateBreeze()
    {
        breezeTime += Step;
        if (breezeJoints == null) return;
        var joints = combiner.Combined.Joints;
        float strength = ambientBreeze ? Mathf.Clamp(breezeStrength, 0, .05f) : 0;
        // Several smooth frequencies keep the breeze from looking like a rigid
        // synchronized oscillation. Rest gravity is rebuilt each step, not accumulated.
        for (int i = 0; i < breezeJoints.Length; i++)
        {
            var settings = breezeJoints[i];var joint = settings.rest;
            float t = breezeTime, phase = settings.phase;
            var flow = new Vector3(.65f * Mathf.Sin(t * .83f + phase) + .25f * Mathf.Sin(t * 1.47f + phase * .4f),
                .10f * Mathf.Sin(t * 1.13f + phase), .45f * Mathf.Sin(t * .63f + .8f + phase * .7f));
            float fade = Mathf.SmoothStep(0, 1, Mathf.Clamp01(t / 2f));
            var force = joint.gravityDir * joint.gravityPower + flow * (strength * settings.exposure * fade);
            if (gravityDrape && settings.cloth)
            {
                float height = target.transform.InverseTransformPoint(settings.tail.position).y;
                float contact = 1f - Mathf.SmoothStep(0, 1, Mathf.Clamp01((height - clothFloorHeight - joint.radius - .002f) / .02f));
                // Grounded cloth loses its rest-shape torque. Static friction
                // absorbs weak lateral forces; airborne segments retain the breeze.
                joint.stiffnessForce *= 1f - contact;
                joint.dragForce = Mathf.Lerp(joint.dragForce, .85f, contact);
                var normal = target.transform.up;
                var tangent = Vector3.ProjectOnPlane(force, normal);
                float friction = settings.rest.gravityPower * .25f;
                force -= Vector3.ClampMagnitude(tangent, friction) * contact;
                // The support surface cancels downward load. Leaving it in the
                // Verlet step shortens a slanted segment before plane projection
                // and can make a long chain crawl sideways even without wind.
                float load = Vector3.Dot(force, normal);
                if (load < 0) force -= normal * load * contact;
            }
            joint.gravityPower = force.magnitude;
            joint.gravityDir = force.sqrMagnitude > 1e-12f ? force.normalized : Vector3.down;
            joints[i] = joint;
        }
    }
    private void ResolveClothSupport()
    {
        var root = target.transform;
        var normal = root.up;
        float scale = Mathf.Max(.01f, Mathf.Abs(root.lossyScale.y));
        var floor = root.TransformPoint(Vector3.up * clothFloorHeight);
        var next = combiner.Combined.NextTails;
        var current = combiner.Combined.CurrentTails;
        for (int i = 0; i < breezeJoints.Length; i++)
        {
            var joint = breezeJoints[i];
            if (!joint.cloth) continue;
            var head = joint.head.position;
            var tail = joint.tail.position;
            float radius = joint.rest.radius * scale;
            float height = Vector3.Dot(tail - floor, normal) - radius;
            if (height < 0)
            {
                var delta = tail - head;
                float length = delta.magnitude;
                float vertical = Vector3.Dot(floor - head, normal) + radius;
                if (Mathf.Abs(vertical) < length)
                {
                    var horizontal = Vector3.ProjectOnPlane(delta, normal);
                    if (horizontal.sqrMagnitude > 1e-12f)
                    {
                        var corrected = horizontal.normalized * Mathf.Sqrt(Mathf.Max(0, length * length - vertical * vertical)) + normal * vertical;
                        joint.head.rotation = Quaternion.FromToRotation(delta, corrected) * joint.head.rotation;
                        tail = joint.tail.position;
                    }
                }
            }
            // The stock plane projection changes tail length before writing a
            // rotation. Synchronize its history with the actual fixed-length bone.
            next[i] = tail;
            if (Vector3.Dot(tail - floor, normal) <= radius + .002f)
                current[i] = Vector3.Lerp(tail, current[i], .15f);
        }
    }
    private FastSpringBoneCollider[] BuildColliders(Vrm10InstanceSpringBone.Spring spring)
    {
        var colliders = spring.ColliderGroups.SelectMany(g => g.Colliders).Select(c => new FastSpringBoneCollider
        {
            Transform = c.transform,
            Collider = new BlittableCollider { colliderType = ConvertColliderType(c.ColliderType),
                offset = c.Offset, radius = c.Radius, tailOrNormal = c.TailOrNormal }
        }).ToList();
        if (!naturalClothDynamics || !(spring.Name ?? "").StartsWith("Skirt", StringComparison.OrdinalIgnoreCase))
            return colliders.ToArray();
        // Fill the gaps between the VRM 0 sphere samples along each shin/thigh.
        // Preserve the source spheres and only bridge colliders on the same bone.
        foreach (var group in spring.ColliderGroups)
        {
            var spheres = group.Colliders.Where(c => c.ColliderType == VRM10SpringBoneColliderTypes.Sphere &&
                (c.transform.name.Contains("UpperLeg") || c.transform.name.Contains("LowerLeg"))).ToArray();
            for (int i = 1; i < spheres.Length; i++)
            {
                var a = spheres[i - 1]; var b = spheres[i];
                if (a.transform != b.transform) continue;
                // The train is a wide surface skinned between two guide chains.
                // Reserve room for that surface, not just its bone centerline.
                float clothThickness = gravityDrape && (spring.Name ?? "").Contains("Train") ? (bodyClearance ? .10f : .035f) : (bodyClearance ? .022f : 0f);
                colliders.Add(new FastSpringBoneCollider { Transform = a.transform, Collider = new BlittableCollider
                { colliderType = BlittableColliderType.Capsule, offset = a.Offset, tailOrNormal = b.Offset, radius = Mathf.Max(a.Radius, b.Radius) + clothThickness } });
            }
        }
        if(bodyClearance && (spring.Name ?? "").Contains("Train"))
        {
            // The pelvis is wider than its old single sphere. A finite capsule
            // follows the hip joints while leaving the free train unconstrained.
            var hips=target.Humanoid.GetBoneTransform(HumanBodyBones.Hips);
            var left=target.Humanoid.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            var right=target.Humanoid.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            if(hips!=null && left!=null && right!=null)
                colliders.Add(new FastSpringBoneCollider {Transform=hips,Collider=new BlittableCollider {
                    colliderType=BlittableColliderType.Capsule,
                    offset=hips.InverseTransformPoint(left.position)+Vector3.up*.015f,
                    tailOrNormal=hips.InverseTransformPoint(right.position)+Vector3.up*.015f,radius=.088f}});
        }
        // NeEEvA locomotion places the avatar origin on its support surface.
        // This follows that support height; it is not arbitrary scene-mesh collision.
        colliders.Add(new FastSpringBoneCollider { Transform = target.transform, Collider = new BlittableCollider
        { colliderType = BlittableColliderType.Plane, offset = Vector3.up * clothFloorHeight, tailOrNormal = Vector3.up } });
        return colliders.ToArray();
    }
    private void ResolveSkirtSurface()
    {
        if (skirtRuntimeMesh == null)
        {
            skirtRenderer = target.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r.name.Contains("衣服"));
            var spring = target.SpringBone.Springs.FirstOrDefault(s => s.Name.StartsWith("Skirt_Front"));
            if (skirtRenderer == null || spring == null) return;
            skirtSourceMesh = skirtRenderer.sharedMesh;
            skirtRuntimeMesh = Instantiate(skirtSourceMesh);
            skirtRuntimeMesh.name = skirtSourceMesh.name + " (cloth clearance instance)";
            skirtRuntimeMesh.MarkDynamic();
            var bounds = skirtRuntimeMesh.bounds;bounds.Expand(.15f);skirtRuntimeMesh.bounds = bounds;
            skirtRestVertices = skirtSourceMesh.vertices;
            skirtCorrectedVertices = (Vector3[])skirtRestVertices.Clone();
            var weights = skirtSourceMesh.boneWeights;
            var bones = skirtRenderer.bones;
            skirtBoneWeights = weights;skirtSkinBones = bones;skirtBindposes = skirtSourceMesh.bindposes;
            skirtSurfaceVertices = Enumerable.Range(0, skirtRestVertices.Length).Where(i =>
            {
                var w = weights[i];
                float skirt = (IsShortSkirt(bones[w.boneIndex0]) ? w.weight0 : 0) +
                    (IsShortSkirt(bones[w.boneIndex1]) ? w.weight1 : 0) +
                    (IsShortSkirt(bones[w.boneIndex2]) ? w.weight2 : 0) +
                    (IsShortSkirt(bones[w.boneIndex3]) ? w.weight3 : 0);
                var p = target.transform.InverseTransformPoint(skirtRenderer.transform.TransformPoint(skirtRestVertices[i]));
                return skirt > .01f && p.y < .90f;
            }).ToArray();
            skirtSkinMatrices = new Matrix4x4[bones.Length];
            skirtSurfaceColliders = BuildColliders(spring).Where(c => c.Collider.colliderType == BlittableColliderType.Capsule && (c.Transform.name.Contains("UpperLeg") || c.Transform.name.Contains("LowerLeg"))).ToArray();
            var links = new List<SurfaceLink>();
            void Link(HumanBodyBones a, HumanBodyBones b, float radius, float upOffset = 0)
            {
                var start = target.Humanoid.GetBoneTransform(a);var end = target.Humanoid.GetBoneTransform(b);
                if (start != null && end != null) links.Add(new SurfaceLink { a = start, b = end, radius = radius, upOffset = upOffset });
            }
            Link(HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, .096f, .035f);
            Link(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, .043f);
            Link(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, .043f);
            Link(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, .065f);
            Link(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, .065f);
            Link(HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, .055f);
            Link(HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, .055f);
            Link(HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleDistal, .037f);
            Link(HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleDistal, .037f);
            skirtSurfaceLinks = links.ToArray();
            skirtWorldCapsules = new SurfaceCapsule[skirtSurfaceColliders.Length + skirtSurfaceLinks.Length];
            skirtRenderer.sharedMesh = skirtRuntimeMesh;
        }
        for (int i = 0; i < skirtSkinBones.Length; i++) skirtSkinMatrices[i] = skirtSkinBones[i].localToWorldMatrix * skirtBindposes[i];
        for (int i = 0; i < skirtSurfaceColliders.Length; i++)
        {
            var collider = skirtSurfaceColliders[i];
            var start = collider.Transform.TransformPoint(collider.Collider.offset);
            var axis = collider.Transform.TransformPoint(collider.Collider.tailOrNormal) - start;
            float scale = Mathf.Max(.01f, collider.Transform.lossyScale.magnitude / Mathf.Sqrt(3f));
            skirtWorldCapsules[i] = new SurfaceCapsule { start = start, axis = axis,
                inverseLengthSquared = axis.sqrMagnitude > 1e-10f ? 1f / axis.sqrMagnitude : 0,
                radius = (collider.Collider.radius + .013f) * scale };
        }
        float modelScale = Mathf.Max(.01f, target.transform.lossyScale.magnitude / Mathf.Sqrt(3f));
        for (int i = 0; i < skirtSurfaceLinks.Length; i++)
        {
            var link = skirtSurfaceLinks[i];var axis = link.b.position - link.a.position;
            skirtWorldCapsules[skirtSurfaceColliders.Length + i] = new SurfaceCapsule {
                start = link.a.position + target.transform.up * (link.upOffset * modelScale), axis = axis,
                inverseLengthSquared = axis.sqrMagnitude > 1e-10f ? 1f / axis.sqrMagnitude : 0, radius = link.radius * modelScale };
        }
        foreach (int i in skirtSurfaceVertices)
        {
            var w = skirtBoneWeights[i];
            var a = skirtSkinMatrices[w.boneIndex0];var b = skirtSkinMatrices[w.boneIndex1];
            var c = skirtSkinMatrices[w.boneIndex2];var d = skirtSkinMatrices[w.boneIndex3];
            var matrix = new Matrix4x4(
                a.GetColumn(0)*w.weight0+b.GetColumn(0)*w.weight1+c.GetColumn(0)*w.weight2+d.GetColumn(0)*w.weight3,
                a.GetColumn(1)*w.weight0+b.GetColumn(1)*w.weight1+c.GetColumn(1)*w.weight2+d.GetColumn(1)*w.weight3,
                a.GetColumn(2)*w.weight0+b.GetColumn(2)*w.weight1+c.GetColumn(2)*w.weight2+d.GetColumn(2)*w.weight3,
                a.GetColumn(3)*w.weight0+b.GetColumn(3)*w.weight1+c.GetColumn(3)*w.weight2+d.GetColumn(3)*w.weight3);
            var original = matrix.MultiplyPoint3x4(skirtRestVertices[i]);
            var point = original;
            for (int pass = 0; pass < 3; pass++) foreach (var capsule in skirtWorldCapsules)
            {
                float t = Mathf.Clamp01(Vector3.Dot(point - capsule.start, capsule.axis) * capsule.inverseLengthSquared);
                var center = capsule.start + capsule.axis * t;
                float radius = capsule.radius;
                var offset = point - center;
                if (offset.sqrMagnitude < radius * radius)
                    point = center + (offset.sqrMagnitude > 1e-12f ? offset.normalized : target.transform.forward) * radius;
            }
            // Inverse skinning changes only this instance's cloth surface. The
            // rest mesh, skin weights, UVs and authored toon normals stay intact.
            var correction = point - original;
            skirtCorrectedVertices[i] = correction.sqrMagnitude > 1e-12f ?
                skirtRestVertices[i] + matrix.inverse.MultiplyVector(correction) : skirtRestVertices[i];
        }
        skirtRuntimeMesh.SetVertices(skirtCorrectedVertices, 0, skirtCorrectedVertices.Length, UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds);
    }
    private static bool IsShortSkirt(Transform bone) => bone.name.StartsWith("J_Sec_Skirt_Front") || bone.name.StartsWith("J_Sec_Skirt_InnerBack");
    private void ReleaseSkirtSurface()
    {
        if (skirtRenderer != null && skirtRenderer.sharedMesh == skirtRuntimeMesh) skirtRenderer.sharedMesh = skirtSourceMesh;
        if (skirtRuntimeMesh != null)
        {
            if (Application.isPlaying) Destroy(skirtRuntimeMesh);else DestroyImmediate(skirtRuntimeMesh);
        }
        skirtRuntimeMesh = null;skirtSourceMesh = null;skirtRenderer = null;
        skirtRestVertices = null;skirtCorrectedVertices = null;skirtSurfaceVertices = null;skirtSkinMatrices = null;skirtSurfaceColliders = null;
        skirtBindposes = null;skirtBoneWeights = null;skirtSkinBones = null;skirtWorldCapsules = null;skirtSurfaceLinks = null;
    }
    private static BlittableColliderType ConvertColliderType(VRM10SpringBoneColliderTypes kind)
    {
        switch (kind)
        {
            case VRM10SpringBoneColliderTypes.Sphere: return BlittableColliderType.Sphere;
            case VRM10SpringBoneColliderTypes.Capsule: return BlittableColliderType.Capsule;
            case VRM10SpringBoneColliderTypes.Plane: return BlittableColliderType.Plane;
            case VRM10SpringBoneColliderTypes.SphereInside: return BlittableColliderType.SphereInside;
            case VRM10SpringBoneColliderTypes.CapsuleInside: return BlittableColliderType.CapsuleInside;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
    private void StopSolver(bool restoreRest)
    {
        ReleaseSkirtSurface();
        if (scheduler != null) scheduler.Dispose(); // owns combined buffers
        else combiner?.Dispose();
        scheduler = null; combiner = null;
        buffer?.Dispose(); buffer = null;
        breezeJoints = null;
        accumulator = 0;
        if (restoreRest) foreach (var item in restRotations) if (item.Key != null) item.Key.localRotation = item.Value;
        restRotations.Clear();
    }
    private void OnDisable() { StopSolver(!warnedOwner); }
    private void OnDestroy() { StopSolver(false); }
    private void OnDrawGizmosSelected() { if (IsInitialized) combiner.DrawGizmos(); }
}
