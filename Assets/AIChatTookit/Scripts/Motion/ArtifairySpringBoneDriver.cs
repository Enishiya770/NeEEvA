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
                    float along = Mathf.Clamp01(index / (float)Mathf.Max(1, s.Joints.Count - 2));
                    return new FastSpringBoneJoint
                    {
                        Transform = j.transform,
                        DefaultLocalRotation = state.LocalRotation,
                        Joint = new BlittableJointMutable
                        {
                            stiffnessForce = j.m_stiffnessForce * (cloth ? Mathf.Lerp(1.5f, .55f, along) : 1f), gravityDir = j.m_gravityDir,
                            gravityPower = j.m_gravityPower * (cloth ? Mathf.Lerp(.5f, 1.1f, along) : 1f),
                            dragForce = Mathf.Clamp01(j.m_dragForce * (cloth ? Mathf.Lerp(1.2f, .8f, along) : 1f)), radius = j.m_jointRadius
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
                scheduler.Schedule(Step).Complete();
                accumulator = Mathf.Max(0, accumulator - Step);
            }
        }
        finally { root.SetPositionAndRotation(finalPosition, finalRotation); }
        if (count > 0) { simulatedRootPosition = finalPosition; simulatedRootRotation = finalRotation; }
    }
    private void RememberRoot()
    {
        previousRootPosition = target.transform.position;
        previousRootRotation = target.transform.rotation;
        previousRootScale = target.transform.lossyScale;
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
                colliders.Add(new FastSpringBoneCollider { Transform = a.transform, Collider = new BlittableCollider
                { colliderType = BlittableColliderType.Capsule, offset = a.Offset, tailOrNormal = b.Offset, radius = Mathf.Max(a.Radius, b.Radius) } });
            }
        }
        // NeEEvA locomotion places the avatar origin on its support surface.
        // This follows that support height; it is not arbitrary scene-mesh collision.
        colliders.Add(new FastSpringBoneCollider { Transform = target.transform, Collider = new BlittableCollider
        { colliderType = BlittableColliderType.Plane, offset = Vector3.up * clothFloorHeight, tailOrNormal = Vector3.up } });
        return colliders.ToArray();
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
        if (scheduler != null) scheduler.Dispose(); // owns combined buffers
        else combiner?.Dispose();
        scheduler = null; combiner = null;
        buffer?.Dispose(); buffer = null;
        accumulator = 0;
        if (restoreRest) foreach (var item in restRotations) if (item.Key != null) item.Key.localRotation = item.Value;
        restRotations.Clear();
    }
    private void OnDisable() { StopSolver(!warnedOwner); }
    private void OnDestroy() { StopSolver(false); }
    private void OnDrawGizmosSelected() { if (IsInitialized) combiner.DrawGizmos(); }
}
