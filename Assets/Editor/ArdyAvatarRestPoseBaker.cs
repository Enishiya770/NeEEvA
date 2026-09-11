using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using NeEEvA.Motion;
using UniVRM10;
using UnityEditor;
using UnityEngine;

/// <summary>Read imported prefab assets and bake rest references without creating scene instances.</summary>
public static class ArdyAvatarRestPoseBaker
{
    private const string Destination = "Assets/AIChatTookit/Resources/ARDY/RestPoses";
    private static readonly string[] Sources = { "Assets/Model/NEVA.vrm", "Assets/Model/NeEEvA.vrm" };
    private static readonly HumanBodyBones[] Bones =
    {
        HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest,
        HumanBodyBones.Neck, HumanBodyBones.Head,
        HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm,
        HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
        HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm,
        HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
    };

    [MenuItem("Tools/NeEEvA/Bake ARDY Avatar Rest Poses")]
    public static void Bake()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Bake imported rest references outside Play mode.");
        // Read and validate both sources before changing any destination asset.
        var prepared = new List<ArdyAvatarRestPose>();
        try
        {
            foreach (string source in Sources) prepared.Add(ReadImportedPrefab(source));
            EnsureFolder(Destination);
            foreach (var reference in prepared)
            {
                string path = Destination + "/" + Path.GetFileNameWithoutExtension(reference.sourcePath) + "-rest.asset";
                var existing = AssetDatabase.LoadAssetAtPath<ArdyAvatarRestPose>(path);
                if (existing == null)
                {
                    if (AssetDatabase.LoadMainAssetAtPath(path) != null || File.Exists(Path.GetFullPath(path)))
                        throw new InvalidOperationException("The rest-reference path contains another asset: " + path);
                    AssetDatabase.CreateAsset(reference, path);
                }
                else
                {
                    // Preserve the asset and .meta GUID so existing references stay valid.
                    existing.schema = reference.schema;
                    existing.avatar = reference.avatar;
                    existing.sourcePath = reference.sourcePath;
                    existing.sourceSha256 = reference.sourceSha256;
                    existing.sourceDependencyHash = reference.sourceDependencyHash;
                    existing.entries = reference.entries;
                    EditorUtility.SetDirty(existing);
                }
                Debug.Log($"[ArdyAvatarRestPoseBaker] {reference.entries.Length} imported bones: {reference.sourcePath} -> {path}");
            }
            AssetDatabase.SaveAssets();
        }
        finally
        {
            foreach (var reference in prepared)
                if (reference != null && !AssetDatabase.Contains(reference)) UnityEngine.Object.DestroyImmediate(reference);
        }
    }

    private static ArdyAvatarRestPose ReadImportedPrefab(string path)
    {
        var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (root == null) throw new InvalidOperationException("Imported VRM prefab is missing: " + path);
        var vrm = root.GetComponentInChildren<Vrm10Instance>(true);
        if (vrm == null || vrm.Vrm == null || vrm.Humanoid == null)
            throw new InvalidOperationException("Imported asset is missing VRM humanoid metadata: " + path);
        var entries = new List<ArdyAvatarRestPose.Entry>();
        var rootInverse = Quaternion.Inverse(vrm.transform.rotation);
        foreach (var bone in Bones)
        {
            var transform = vrm.Humanoid.GetBoneTransform(bone);
            // Optional Chest/UpperChest/Shoulder bones match the runtime mapping.
            if (transform == null) continue;
            entries.Add(new ArdyAvatarRestPose.Entry
            {
                bone = bone,
                restInRoot = (rootInverse * transform.rotation).normalized,
            });
        }
        foreach (var required in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.Head })
            if (!entries.Exists(entry => entry.bone == required))
                throw new InvalidOperationException($"Imported asset {path} has no required {required} bone.");

        var result = ScriptableObject.CreateInstance<ArdyAvatarRestPose>();
        try
        {
            result.name = Path.GetFileNameWithoutExtension(path) + " ARDY Imported Rest";
            result.schema = 1;
            result.avatar = vrm.Vrm;
            result.sourcePath = path;
            using (var algorithm = SHA256.Create())
            using (var stream = File.OpenRead(Path.GetFullPath(path)))
                result.sourceSha256 = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            result.sourceDependencyHash = AssetDatabase.GetAssetDependencyHash(path).ToString();
            result.entries = entries.ToArray();
            result.Validate();
            return result;
        }
        catch
        {
            UnityEngine.Object.DestroyImmediate(result);
            throw;
        }
    }

    private static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    public static void BakeBatch()
    {
        try { Bake(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
}
