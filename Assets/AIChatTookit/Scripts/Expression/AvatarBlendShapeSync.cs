using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>Share authored blink, vowel and emotion weights across face/iris renderers.</summary>
[DisallowMultipleComponent, DefaultExecutionOrder(12900)]
public sealed class AvatarBlendShapeSync : MonoBehaviour
{
    [SerializeField] private SkinnedMeshRenderer source;
    [SerializeField] private SkinnedMeshRenderer[] targets = Array.Empty<SkinnedMeshRenderer>();
    private sealed class Binding { public SkinnedMeshRenderer renderer; public int sourceIndex, targetIndex; }
    private readonly List<Binding> bindings = new List<Binding>();
    public SkinnedMeshRenderer Source => source;
    public int BindingCount => bindings.Count;
    private static bool SharedExpression(string name) => name == "Fcl_EYE_Close" || name == "Fcl_EYE_Close_L" ||
        name == "Fcl_EYE_Close_R" || name == "Fcl_MTH_A" || name == "Fcl_MTH_I" || name == "Fcl_MTH_U" ||
        name == "Fcl_MTH_E" || name == "Fcl_MTH_O" || name.StartsWith("Fcl_ALL_", StringComparison.Ordinal);
    public void Configure(SkinnedMeshRenderer primary, IEnumerable<SkinnedMeshRenderer> additional)
    {
        source = primary;
        targets = additional.Where(r => r != null && r != source).Distinct().ToArray();
        Rebuild();
    }
    private void OnEnable() { Rebuild(); }
    private void Rebuild()
    {
        bindings.Clear();
        if (source == null || source.sharedMesh == null) return;
        for (int i = 0; i < source.sharedMesh.blendShapeCount; i++)
        {
            string name = source.sharedMesh.GetBlendShapeName(i);
            if (!SharedExpression(name)) continue;
            foreach (var target in targets)
            {
                if (target == null || target.sharedMesh == null) continue;
                int index = target.sharedMesh.GetBlendShapeIndex(name);
                if (index >= 0) bindings.Add(new Binding { renderer = target, sourceIndex = i, targetIndex = index });
            }
        }
    }
    public void SynchronizeWeights()
    {
        if (source == null) return;
        foreach (var binding in bindings)
            if (binding.renderer != null) binding.renderer.SetBlendShapeWeight(binding.targetIndex, source.GetBlendShapeWeight(binding.sourceIndex));
    }
    private void LateUpdate() { SynchronizeWeights(); }
}
