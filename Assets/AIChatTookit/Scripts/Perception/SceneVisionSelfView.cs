using System;
using System.Collections.Generic;
using UniGLTF;
using UniVRM10;
using UnityEngine;
using UnityEngine.Rendering;

namespace NeEEvA.Perception
{
    /// <summary>Owns headless render copies; never changes source meshes, layers, bones or user cameras.</summary>
    internal sealed class SceneVisionSelfView : IDisposable
    {
        private sealed class Entry
        {
            internal Renderer source;
            internal SkinnedMeshRenderer proxy;
            internal Mesh ownedMesh;
            internal Mesh sourceMesh;
            internal bool before;
            internal bool changed;
        }
        private readonly List<Entry> entries = new List<Entry>();
        private bool applied;

        internal SceneVisionSelfView(Vrm10Instance avatar)
        {
            var head = avatar.Humanoid.GetBoneTransform(HumanBodyBones.Head);
            if (head == null) throw new InvalidOperationException("角色没有有效头骨，不能隔离第一人称自身遮挡。");
            try
            {
                foreach (var renderer in avatar.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer is SkinnedMeshRenderer skin && skin.sharedMesh != null)
                    {
                        bool headInfluence = false;
                        foreach (Transform bone in skin.bones)
                            if (bone != null && (bone == head || bone.IsChildOf(head))) { headInfluence = true; break; }
                        if (!headInfluence && !skin.transform.IsChildOf(head)) continue;
                        var entry = new Entry { source = renderer, sourceMesh = skin.sharedMesh }; entries.Add(entry);
                        if (headInfluence)
                        {
                            // This is one bounded bind-time preparation. The supplied caller
                            // completes synchronously: no unfinished mesh job survives disposal.
                            var task = VRM10ObjectFirstPerson.CreateErasedMeshAsync(skin, head, new ImmediateCaller());
                            if (!task.IsCompleted) throw new InvalidOperationException("第一人称网格准备未同步完成。");
                            Mesh erased = task.GetAwaiter().GetResult();
                            Mesh mesh = null;
                            if (erased != null)
                            {
                                // Keep all source vertex channels and blendshapes; the VRM
                                // eraser supplies only the head-free submesh triangles.
                                // Its standalone mesh omits blendshapes and secondary UVs.
                                try
                                {
                                    mesh = UnityEngine.Object.Instantiate(skin.sharedMesh);
                                    entry.ownedMesh = mesh;
                                    mesh.subMeshCount = erased.subMeshCount;
                                    for (int sub = 0; sub < erased.subMeshCount; sub++)
                                        mesh.SetTriangles(erased.GetTriangles(sub), sub, false);
                                }
                                finally { DestroyOwned(erased); }
                            }
                            if (mesh != null)
                            {
                                mesh.hideFlags = HideFlags.HideAndDontSave;
                                var host = new GameObject("NeEEvA scene vision headless proxy") { hideFlags = HideFlags.HideAndDontSave };
                                host.transform.SetParent(skin.transform, false);
                                host.layer = skin.gameObject.layer;
                                var proxy = host.AddComponent<SkinnedMeshRenderer>(); entry.proxy = proxy;
                                proxy.enabled = false; proxy.sharedMesh = mesh; proxy.sharedMaterials = skin.sharedMaterials;
                                proxy.bones = skin.bones; proxy.rootBone = skin.rootBone; proxy.localBounds = skin.localBounds;
                                proxy.updateWhenOffscreen = true; proxy.shadowCastingMode = skin.shadowCastingMode;
                                proxy.receiveShadows = skin.receiveShadows; proxy.quality = skin.quality;
                            }
                        }
                    }
                    else if (renderer.transform == head || renderer.transform.IsChildOf(head))
                        entries.Add(new Entry { source = renderer });
                }
            }
            catch { Dispose(); throw; }
        }

        internal void Apply()
        {
            if (applied) throw new InvalidOperationException("Scene vision rendering is already active.");
            applied = true;
            foreach (Entry entry in entries)
            {
                if (entry.source == null) throw new InvalidOperationException("角色渲染对象已变化，请重新绑定视觉。");
                entry.before = entry.source.forceRenderingOff;
                entry.changed = true;
                if (entry.proxy != null)
                {
                    var source = (SkinnedMeshRenderer)entry.source;
                    if (source.sharedMesh != entry.sourceMesh)
                        throw new InvalidOperationException("角色网格已变化，请重新绑定场景视觉。");
                    entry.proxy.sharedMaterials = source.sharedMaterials;
                    entry.proxy.localBounds = source.localBounds;
                    entry.proxy.gameObject.layer = source.gameObject.layer;
                    entry.proxy.shadowCastingMode = source.shadowCastingMode;
                    entry.proxy.receiveShadows = source.receiveShadows;
                    entry.proxy.sortingLayerID = source.sortingLayerID; entry.proxy.sortingOrder = source.sortingOrder;
                    for (int shape = 0; shape < source.sharedMesh.blendShapeCount; shape++)
                        entry.proxy.SetBlendShapeWeight(shape, source.GetBlendShapeWeight(shape));
                    var properties = new MaterialPropertyBlock(); source.GetPropertyBlock(properties);
                    entry.proxy.SetPropertyBlock(properties);
                    for (int material = 0; material < source.sharedMaterials.Length; material++)
                    {
                        source.GetPropertyBlock(properties, material);
                        entry.proxy.SetPropertyBlock(properties, material);
                    }
                    entry.proxy.enabled = source.enabled && source.gameObject.activeInHierarchy && !entry.before;
                }
                entry.source.forceRenderingOff = true;
            }
        }

        internal void Restore()
        {
            if (!applied) return;
            foreach (Entry entry in entries)
            {
                if (entry.proxy != null) entry.proxy.enabled = false;
                if (entry.changed && entry.source != null) entry.source.forceRenderingOff = entry.before;
                entry.changed = false;
            }
            applied = false;
        }

        public void Dispose()
        {
            Restore();
            foreach (Entry entry in entries)
            {
                if (entry.proxy != null)
                {
                    entry.proxy.enabled = false;
                    // Destroy is deferred in Play. Detach now so an immediate rebind
                    // cannot discover the retiring proxy as another source renderer.
                    entry.proxy.transform.SetParent(null, false);
                    DestroyOwned(entry.proxy.gameObject);
                }
                if (entry.ownedMesh != null) DestroyOwned(entry.ownedMesh);
            }
            entries.Clear();
        }

        internal static void DestroyOwned(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
