using System;
using System.Collections.Generic;
using UniVRM10;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>
    /// Imported humanoid rest rotations in avatar-root coordinates. This asset is
    /// baked from the source prefab, never sampled from an animated scene instance.
    /// </summary>
    public sealed class ArdyAvatarRestPose : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public HumanBodyBones bone;
            public Quaternion restInRoot;
        }

        public int schema = 1;
        public VRM10Object avatar;
        public string sourcePath;
        public string sourceSha256;
        public string sourceDependencyHash;
        public Entry[] entries;

        /// <summary>
        /// Resolve by the shared VRM asset identity, including instances created
        /// after scene load. A missing reference returns null for the caller's
        /// explicit runtime-import fallback; it never captures the current pose.
        /// </summary>
        public static ArdyAvatarRestPose Find(Vrm10Instance instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            if (instance.Vrm == null) return null;
            ArdyAvatarRestPose match = null;
            foreach (var candidate in Resources.LoadAll<ArdyAvatarRestPose>("ARDY/RestPoses"))
            {
                if (candidate.avatar != instance.Vrm) continue;
                candidate.Validate();
                if (match != null)
                    throw new InvalidOperationException($"Multiple ARDY rest references match {instance.Vrm.name}.");
                match = candidate;
            }
            return match;
        }

        public Quaternion Get(HumanBodyBones bone)
        {
            Validate();
            foreach (var entry in entries)
                if (entry.bone == bone) return entry.restInRoot;
            throw new InvalidOperationException($"ARDY imported rest reference '{name}' has no {bone} bone.");
        }

        public void Validate()
        {
            if (schema != 1 || avatar == null || entries == null || entries.Length == 0)
                throw new InvalidOperationException($"Invalid ARDY imported rest reference '{name}'.");
            if (string.IsNullOrWhiteSpace(sourcePath) || sourceSha256 == null || sourceSha256.Length != 64)
                throw new InvalidOperationException($"ARDY rest reference '{name}' is missing source provenance.");
            foreach (char digit in sourceSha256)
                if (!((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f')))
                    throw new InvalidOperationException($"ARDY rest reference '{name}' has an invalid source hash.");
            var seen = new HashSet<HumanBodyBones>();
            foreach (var entry in entries)
            {
                if (entry == null || (int)entry.bone < 0 || entry.bone >= HumanBodyBones.LastBone || !seen.Add(entry.bone))
                    throw new InvalidOperationException($"ARDY rest reference '{name}' has invalid or duplicate bones.");
                var q = entry.restInRoot;
                float norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                if (float.IsNaN(norm) || float.IsInfinity(norm) || Mathf.Abs(norm - 1f) > 0.002f)
                    throw new InvalidOperationException($"ARDY rest reference '{name}' has an invalid {entry.bone} rotation.");
            }
        }
    }
}
