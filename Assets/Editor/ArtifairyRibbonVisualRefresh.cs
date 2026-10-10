#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Refresh existing, owned avatar renderer assets after publishing the loop-only
/// material VRMs. Does not create/replace scene objects or save any scene.
/// Run ArtifairyCompatibilitySetup.Run/RunRenderVariant afterwards to rebake ARDY
/// source hashes and perform the full saved-scene compatibility checks.
/// </summary>
public static class ArtifairyRibbonVisualRefresh
{
    private const string LoopMaterialName = "Artifairy_RibbonLoops_Opaque";
    private const string AuxiliaryRendererName = "袖口饰品_VRM";
    private const string OriginalScene = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    private const string ReportPath = "E:/Artifairy/.vrm_work/ribbon_fix/unity_ribbon_visual_refresh.json";
    private static readonly Profile[] Profiles =
    {
        new Profile("Artifairy_Compatible", "Artifairy_Chat"),
        new Profile("Artifairy_RenderCompatible", "Artifairy_RenderChat")
    };

    private sealed class Profile
    {
        public readonly string Model, Prefab, Scene;
        public Profile(string model, string scene)
        {
            Model = "Assets/Model/Artifairy/" + model + ".vrm";
            Prefab = "Assets/Model/Artifairy/" + model + ".prefab";
            Scene = "Assets/Private/Artifairy/Scenes/" + scene + ".unity";
        }
    }

    private sealed class Pose
    {
        public Transform Node, Parent;
        public int Sibling;
        public Vector3 Position, Scale;
        public Quaternion Rotation;
    }

    private sealed class PreservedState
    {
        public Pose[] Poses;
        public Dictionary<Component, string> Components;
        public Animator Animator;
        public Avatar Avatar;
        public RuntimeAnimatorController Controller;
        public Dictionary<SkinnedMeshRenderer, string[]> ShapeNames;
        public Dictionary<SkinnedMeshRenderer, float[]> ShapeWeights;
    }

    public static void RefreshBoth()
    {
        var report = new Dictionary<string, object>();
        var protectedPaths = new[]
        {
            OriginalScene, "Assets/Model/NeEEvA.vrm", "Assets/AIChatTookit/Animation/Animator Controller.controller"
        }.Concat(Profiles.Select(p => p.Scene)).ToArray();
        var protectedHashes = protectedPaths.ToDictionary(p => p, FileHash);
        var protectedGuids = protectedPaths.ToDictionary(p => p, AssetDatabase.AssetPathToGUID);
        bool pass = false;
        try
        {
            Require(!EditorApplication.isPlaying, "Renderer refresh requires Edit Mode.");
            Require(Enumerable.Range(0, SceneManager.sceneCount).All(i => !SceneManager.GetSceneAt(i).isDirty),
                "Refusing to close a scene with unsaved changes.");
            foreach (var p in Profiles) Import(p);

            // Preflight BOTH profiles before saving either prefab. Opening scene boundaries
            // can invalidate imported native wrappers, so every check loads fresh handles.
            foreach (var p in Profiles) CheckScene(p, false);
            foreach (var p in Profiles) CheckPrefab(p, false);

            var refreshed = new List<object>();
            foreach (var p in Profiles) refreshed.Add(CheckPrefab(p, true));
            foreach (var p in Profiles) CheckScene(p, true);
            report["profiles"] = refreshed;
            report["ownedScenesSaved"] = false;
            report["followUpRequired"] = "Run ArtifairyCompatibilitySetup.Run and RunRenderVariant to refresh ARDY source hashes and full compatibility reports.";
            pass = true;
        }
        catch (Exception error)
        {
            // Only validation messages and method stacks; never serialize scene configuration.
            report["error"] = error.GetBaseException().Message;
            Debug.LogException(error);
        }
        finally
        {
            var changed = protectedHashes.Keys.Where(p => FileHash(p) != protectedHashes[p] ||
                AssetDatabase.AssetPathToGUID(p) != protectedGuids[p]).ToArray();
            report["protectedOriginalAndOwnedSceneFilesUnchanged"] = changed.Length == 0;
            report["changedProtectedPaths"] = changed;
            report["pass"] = pass && changed.Length == 0;
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, JsonConvert.SerializeObject(report, Formatting.Indented));
            if (Application.isBatchMode) EditorApplication.Exit(pass && changed.Length == 0 ? 0 : 1);
        }
    }

    private static void Import(Profile p)
    {
        Require(File.Exists(p.Model) && File.Exists(p.Prefab) && File.Exists(p.Scene), "Missing owned model, prefab or scene.");
        Require(!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(p.Model)), "Missing model source GUID.");
        var importer = AssetImporter.GetAtPath(p.Model);
        Require(importer != null && importer.GetType().FullName == "UniVRM10.VrmScriptedImporter", "Unexpected VRM importer.");
        var migration = new SerializedObject(importer).FindProperty("MigrateToVrm1");
        Require(migration != null && migration.boolValue, "Existing VRM migration setting must already be enabled.");
        AssetDatabase.ImportAsset(p.Model, ImportAssetOptions.ForceSynchronousImport);
        ValidateImported(p, LoadImported(p));
    }

    private static Vrm10Instance LoadImported(Profile p)
    {
        var root = AssetDatabase.LoadAssetAtPath<GameObject>(p.Model);
        var vrm = root != null ? root.GetComponent<Vrm10Instance>() : null;
        Require(vrm != null && vrm.Vrm != null && !root.scene.IsValid(), "Fresh imported model is unavailable.");
        Require(AssetDatabase.GetAssetPath(vrm.Vrm) == p.Model &&
            AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(vrm.Vrm)) == AssetDatabase.AssetPathToGUID(p.Model),
            "Imported VRM object does not belong to the selected model GUID.");
        return vrm;
    }

    private static void ValidateImported(Profile p, Vrm10Instance vrm)
    {
        var renderers = Renderers(vrm.gameObject);
        Require(renderers.Count == 5, "The material-only repair must preserve five renderers.");
        var auxiliary = renderers.Values.SingleOrDefault(r => r.name == AuxiliaryRendererName);
        Require(auxiliary != null, "Missing auxiliary garment renderer.");
        foreach (var r in renderers.Values)
        {
            Require(r.sharedMesh != null && r.sharedMaterials.Length == r.sharedMesh.subMeshCount &&
                r.sharedMaterials.All(m => m != null), "Imported material slots must match the actual submesh order and count.");
            Require(r.bones.Length == r.sharedMesh.bindposes.Length && r.bones.All(b => b != null), "Imported skin joint/bindpose counts differ.");
        }
        var hits = renderers.Values.SelectMany(r => r.sharedMaterials.Select((m, i) => new { r, m, i }))
            .Where(x => x.m.name == LoopMaterialName).ToArray();
        Require(hits.Length == 1 && hits[0].r == auxiliary, "The independent opaque material must occur only on the auxiliary renderer.");
        var loop = hits[0].m;
        // Installed MToon10Properties maps UnityZWrite to _M_ZWrite; _ZWrite is
        // the legacy MToon0 name. Read public Material properties without referencing
        // the non-auto-referenced MToon10 assembly or invoking its validator.
        string depthProperty = loop.HasProperty("_M_ZWrite") ? "_M_ZWrite" : "_ZWrite";
        Require(loop.renderQueue >= 0 && loop.renderQueue < 2450 && loop.HasProperty(depthProperty) && loop.GetFloat(depthProperty) > 0.5f,
            "The independent loop material must use the opaque queue and write depth.");
        Require((loop.HasProperty("_AlphaMode") && Mathf.Abs(loop.GetFloat("_AlphaMode")) < 0.01f) ||
                (loop.HasProperty("_BlendMode") && Mathf.Abs(loop.GetFloat("_BlendMode")) < 0.01f), "Loop material is not opaque.");
        Require(!loop.IsKeywordEnabled("_ALPHATEST_ON") && !loop.IsKeywordEnabled("_ALPHABLEND_ON") &&
            !loop.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"), "Opaque loop material still has alpha keywords.");
        Require(ConnectedLoopComponents(auxiliary.sharedMesh, hits[0].i) == 2, "Opaque submesh must contain exactly the two loop components.");
        var lace = auxiliary.sharedMaterials.SingleOrDefault(m => m.name == "饰品袖子");
        Require(lace != null && lace.renderQueue >= 2500 && lace.HasProperty("_AlphaMode") &&
            Mathf.Abs(lace.GetFloat("_AlphaMode") - 2f) < 0.01f && lace.HasProperty("_M_ZWrite") &&
            lace.GetFloat("_M_ZWrite") < 0.5f, "Original cuff/lace transparency must remain unchanged.");
    }

    private static object CheckPrefab(Profile p, bool save)
    {
        var content = PrefabUtility.LoadPrefabContents(p.Prefab);
        try
        {
            // LoadPrefabContents is another scene boundary. Acquire the import afterwards.
            var imported = LoadImported(p);
            ValidateImported(p, imported);
            var owned = content.GetComponent<Vrm10Instance>();
            AssertOwned(owned, imported, p);
            var before = Snapshot(content);
            var serializedIds = SerializedIdentity(p.Prefab);
            var serializedGenericState = SerializedNonRendererState(p.Prefab);
            var targetRenderers = Renderers(content);
            var importedRenderers = Renderers(imported.gameObject);
            Require(targetRenderers.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(importedRenderers.Keys.OrderBy(k => k, StringComparer.Ordinal)),
                "Renderer hierarchy changed; refusing a material-only refresh.");
            var transforms = TransformPaths(content);
            int changed = 0, remapped = 0;
            foreach (var pair in importedRenderers)
            {
                var source = pair.Value;
                var target = targetRenderers[pair.Key];
                Require(ShapeNames(target.sharedMesh).SequenceEqual(ShapeNames(source.sharedMesh)), "Face/shape names or order changed.");
                var mappedBones = source.bones.Select(b => ExistingTransform(imported.gameObject, b, transforms)).ToArray();
                var mappedRoot = source.rootBone == null ? null : ExistingTransform(imported.gameObject, source.rootBone, transforms);
                Require(mappedBones.Length == source.sharedMesh.bindposes.Length, "Mapped joints do not match fresh bindposes.");
                bool sameOrder = target.bones.Length == source.bones.Length &&
                    target.bones.Select(b => b != null ? b.name : null).SequenceEqual(source.bones.Select(b => b.name));
                if (sameOrder) Require(target.bones.SequenceEqual(mappedBones), "Same-named joint order has a different hierarchy.");
                if (save)
                {
                    if (!target.bones.SequenceEqual(mappedBones)) { target.bones = mappedBones; remapped++; }
                    if (target.rootBone != mappedRoot) { target.rootBone = mappedRoot; remapped++; }
                    if (target.sharedMesh != source.sharedMesh || !target.sharedMaterials.SequenceEqual(source.sharedMaterials)) changed++;
                    target.sharedMesh = source.sharedMesh;
                    target.sharedMaterials = source.sharedMaterials;
                    EditorUtility.SetDirty(target);
                }
            }
            AssertPreserved(before, content);
            if (save)
            {
                Require(PrefabUtility.SaveAsPrefabAsset(content, p.Prefab) != null, "Could not save the owned prefab in place.");
                Require(serializedIds == SerializedIdentity(p.Prefab), "Saving changed serialized object/component identities or transform hierarchy.");
                Require(serializedGenericState == SerializedNonRendererState(p.Prefab), "Saving changed a non-renderer component or application configuration.");
            }
            return new { prefab = p.Prefab, model = p.Model, modelGuid = AssetDatabase.AssetPathToGUID(p.Model),
                rendererCount = targetRenderers.Count, updatedRenderers = changed, remappedJointArraysOrRootBones = remapped,
                loopMaterial = LoopMaterialName, loopComponents = 2, genericComponentsAndPosesPreserved = true,
                serializedObjectIdsAndHierarchyPreserved = true };
        }
        finally { PrefabUtility.UnloadPrefabContents(content); }
    }

    private static void CheckScene(Profile p, bool refreshed)
    {
        var scene = EditorSceneManager.OpenScene(p.Scene, OpenSceneMode.Single);
        var imported = LoadImported(p);
        var avatars = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Vrm10Instance>(true)).ToArray();
        Require(avatars.Length == 1, "Owned scene must contain exactly one avatar.");
        var avatar = avatars[0];
        AssertOwned(avatar, imported, p);
        Require(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(avatar.gameObject) == p.Prefab &&
            AssetDatabase.AssetPathToGUID(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(avatar.gameObject)) == AssetDatabase.AssetPathToGUID(p.Prefab),
            "Scene avatar is not an instance of the selected owned prefab GUID.");
        var target = Renderers(avatar.gameObject);
        var fresh = Renderers(imported.gameObject);
        Require(target.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(fresh.Keys.OrderBy(k => k, StringComparer.Ordinal)), "Scene renderer paths differ from the fresh model.");
        foreach (var renderer in target.Values)
        {
            var serialized = new SerializedObject(renderer);
            var mesh = serialized.FindProperty("m_Mesh");
            var materials = serialized.FindProperty("m_Materials");
            Require(mesh != null && materials != null && !mesh.prefabOverride, "Existing scene has a mesh override; refusing to clear it.");
            Require(!HasOverride(materials), "Existing scene has material overrides; refusing to clear them.");
        }
        // Before the refresh, old material arrays are expected; after the save, inherited
        // renderer fields must equal fresh importer fields. No scene is saved in either case.
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(p.Prefab);
        var expected = Renderers(asset);
        foreach (var pair in target)
        {
            var inherited = expected[pair.Key];
            Require(pair.Value.sharedMesh == inherited.sharedMesh && pair.Value.sharedMaterials.SequenceEqual(inherited.sharedMaterials),
                "Scene did not inherit renderer fields from the owned prefab.");
            Require(pair.Value.bones.Length == pair.Value.sharedMesh.bindposes.Length && pair.Value.bones.All(b => b != null), "Scene skin joint/bindpose mismatch.");
            if (refreshed)
            {
                var source = fresh[pair.Key];
                Require(pair.Value.sharedMesh == source.sharedMesh && pair.Value.sharedMaterials.SequenceEqual(source.sharedMaterials) &&
                    pair.Value.sharedMaterials.Length == source.sharedMesh.subMeshCount, "Saved prefab/scene did not retain the fresh mesh and complete material array.");
            }
        }
        Require(!scene.isDirty, "Read-only scene validation unexpectedly made the scene dirty.");
    }

    private static bool HasOverride(SerializedProperty property)
    {
        if (property.prefabOverride) return true;
        var cursor = property.Copy(); var end = property.GetEndProperty();
        while (cursor.Next(true) && !SerializedProperty.EqualContents(cursor, end))
            if (cursor.prefabOverride) return true;
        return false;
    }

    private static void AssertOwned(Vrm10Instance owned, Vrm10Instance imported, Profile p)
    {
        Require(owned != null && owned.Vrm == imported.Vrm && AssetDatabase.GetAssetPath(owned.Vrm) == p.Model,
            "Existing avatar belongs to another model source GUID.");
        Require(!owned.enabled && owned.UpdateType == Vrm10Instance.UpdateTypes.None &&
            owned.GetComponent<ArtifairySpringBoneDriver>()?.Target == owned, "Existing avatar lacks our disabled-VRM/spring-only ownership.");
        var animator = owned.GetComponent<Animator>();
        Require(animator != null && animator.avatar != null && animator.avatar.isHuman && animator.avatar.isValid && animator.runtimeAnimatorController != null,
            "Existing avatar/controller configuration is invalid.");
        Require(animator.avatar == imported.GetComponent<Animator>().avatar, "Existing Animator must retain this model's own imported Avatar.");
    }

    private static Dictionary<string, SkinnedMeshRenderer> Renderers(GameObject root)
    {
        Require(root != null, "Missing renderer root.");
        var all = root.GetComponentsInChildren<Renderer>(true);
        Require(all.All(r => r is SkinnedMeshRenderer), "Unexpected renderer type in the owned model.");
        return all.Cast<SkinnedMeshRenderer>().ToDictionary(r => RelativePath(root.transform, r.transform), r => r, StringComparer.Ordinal);
    }

    private static Dictionary<string, Transform> TransformPaths(GameObject root) =>
        root.GetComponentsInChildren<Transform>(true).ToDictionary(t => RelativePath(root.transform, t), t => t, StringComparer.Ordinal);

    private static Transform ExistingTransform(GameObject importedRoot, Transform source, Dictionary<string, Transform> existing)
    {
        Require(source != null, "Missing imported joint.");
        string path = RelativePath(importedRoot.transform, source);
        Require(existing.TryGetValue(path, out Transform mapped), "Fresh joint does not exist in the owned prefab hierarchy: " + path);
        return mapped;
    }

    private static string RelativePath(Transform root, Transform node)
    {
        var parts = new Stack<string>(); var cursor = node;
        while (cursor != null && cursor != root) { parts.Push(cursor.name); cursor = cursor.parent; }
        Require(cursor == root, "Reference escapes the owned hierarchy.");
        return string.Join("/", parts);
    }

    private static PreservedState Snapshot(GameObject root)
    {
        var animator = root.GetComponent<Animator>();
        var renderers = Renderers(root).Values.ToArray();
        return new PreservedState
        {
            Poses = root.GetComponentsInChildren<Transform>(true).Select(t => new Pose { Node = t, Parent = t.parent,
                Sibling = t.GetSiblingIndex(), Position = t.localPosition, Rotation = t.localRotation, Scale = t.localScale }).ToArray(),
            Components = root.GetComponentsInChildren<Component>(true).Where(c => c != null && !(c is Renderer)).ToDictionary(c => c, c => TextHash(EditorJsonUtility.ToJson(c))),
            Animator = animator, Avatar = animator.avatar, Controller = animator.runtimeAnimatorController,
            ShapeNames = renderers.ToDictionary(r => r, r => ShapeNames(r.sharedMesh)),
            ShapeWeights = renderers.ToDictionary(r => r, r => Enumerable.Range(0, r.sharedMesh.blendShapeCount).Select(r.GetBlendShapeWeight).ToArray())
        };
    }

    private static void AssertPreserved(PreservedState before, GameObject root)
    {
        var after = root.GetComponentsInChildren<Component>(true).Where(c => c != null && !(c is Renderer)).ToArray();
        Require(after.Length == before.Components.Count && after.All(c => before.Components.TryGetValue(c, out string hash) && hash == TextHash(EditorJsonUtility.ToJson(c))),
            "Renderer-only refresh changed an application/Animator/rig component.");
        foreach (var p in before.Poses)
            Require(p.Node != null && p.Node.parent == p.Parent && p.Node.GetSiblingIndex() == p.Sibling &&
                p.Node.localPosition == p.Position && p.Node.localRotation == p.Rotation && p.Node.localScale == p.Scale,
                "Renderer-only refresh changed a bone pose or hierarchy.");
        Require(before.Animator == root.GetComponent<Animator>() && before.Animator.avatar == before.Avatar &&
            before.Animator.runtimeAnimatorController == before.Controller, "Animator/own Avatar/controller changed.");
        foreach (var pair in before.ShapeNames)
        {
            Require(pair.Value.SequenceEqual(ShapeNames(pair.Key.sharedMesh)), "Face shape indices changed.");
            Require(before.ShapeWeights[pair.Key].SequenceEqual(Enumerable.Range(0, pair.Key.sharedMesh.blendShapeCount).Select(pair.Key.GetBlendShapeWeight)),
                "Face shape weights changed.");
        }
    }

    private static string[] ShapeNames(Mesh mesh)
    {
        Require(mesh != null, "Missing skinned mesh.");
        return Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();
    }

    private static string SerializedIdentity(string path)
    {
        // Model prefabs contain no service/account configuration. Keep only object IDs,
        // local TRS and structural links in the hash; renderer fields may legitimately grow.
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var parts = new List<string>();
        foreach (Match block in Regex.Matches(text, @"^--- !u!(\d+) &(-?\d+)[^\n]*\n(.*?)(?=^--- !u!|\z)", RegexOptions.Multiline | RegexOptions.Singleline))
        {
            parts.Add(block.Groups[1].Value + "/" + block.Groups[2].Value);
            if (block.Groups[1].Value != "4" && block.Groups[1].Value != "224") continue;
            foreach (Match line in Regex.Matches(block.Groups[3].Value,
                @"^  m_(?:LocalPosition|LocalRotation|LocalScale|Father|Children|GameObject):.*(?:\n  - \{fileID: [^\n]*\})*", RegexOptions.Multiline)) parts.Add(line.Value);
        }
        return TextHash(string.Join("\n", parts));
    }

    private static string SerializedNonRendererState(string path)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var parts = new List<string>();
        foreach (Match block in Regex.Matches(text, @"^--- !u!(\d+) &(-?\d+)[^\n]*\n(.*?)(?=^--- !u!|\z)", RegexOptions.Multiline | RegexOptions.Singleline))
        {
            if (block.Groups[1].Value == "137") continue;
            parts.Add(block.Value);
        }
        // These are model prefab components, never room/account fields. Only a hash
        // is kept or reported; no component configuration is written to the report.
        return TextHash(string.Join("\n", parts));
    }

    private static int ConnectedLoopComponents(Mesh mesh, int submesh)
    {
        Require(mesh.GetTopology(submesh) == MeshTopology.Triangles, "Loop primitive must use triangles.");
        var positions = mesh.vertices; var triangles = mesh.GetIndices(submesh);
        var weld = new Dictionary<string, int>(); var parent = new List<int>();
        int Vertex(int index)
        {
            Vector3 v = positions[index];
            string key = Math.Round(v.x * 1000000) + "/" + Math.Round(v.y * 1000000) + "/" + Math.Round(v.z * 1000000);
            if (!weld.TryGetValue(key, out int id)) { id = parent.Count; weld[key] = id; parent.Add(id); }
            return id;
        }
        int Find(int id) { while (parent[id] != id) { parent[id] = parent[parent[id]]; id = parent[id]; } return id; }
        void Join(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }
        Require(triangles.Length > 0 && triangles.Length % 3 == 0, "Loop submesh is empty or incomplete.");
        for (int i = 0; i < triangles.Length; i += 3)
        {
            int a = Vertex(triangles[i]), b = Vertex(triangles[i + 1]), c = Vertex(triangles[i + 2]);
            Join(a, b); Join(b, c);
        }
        return Enumerable.Range(0, parent.Count).Select(Find).Distinct().Count();
    }

    private static string FileHash(string path) => File.Exists(path) ? TextOrBytesHash(File.ReadAllBytes(path)) : "MISSING";
    private static string TextHash(string text) => TextOrBytesHash(Encoding.UTF8.GetBytes(text));
    private static string TextOrBytesHash(byte[] bytes)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
#endif
