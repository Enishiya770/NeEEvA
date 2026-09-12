using System;
using System.Collections.Generic;
using UniVRM10;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>Full-body imported rest reference. Never infer it from a scene's animated pose.</summary>
    [Serializable] public sealed class ArdyLocomotionAvatarReference
    {
        [Serializable] public sealed class Entry
        {
            public HumanBodyBones bone;
            public Vector3 positionInRoot;
            public Quaternion rotationInRoot;
        }

        public int schema = 1;
        public string sourcePath;
        public VRM10Object avatar;
        public float groundY;
        public Entry[] entries;

        public Entry Get(HumanBodyBones bone)
        {
            if (entries != null) foreach (var entry in entries) if (entry != null && entry.bone == bone) return entry;
            throw new InvalidOperationException("Imported locomotion rest reference has no " + bone);
        }

        public void Validate()
        {
            if (schema != 1 || string.IsNullOrWhiteSpace(sourcePath) || entries == null || !ArdyLocomotionClip.Finite(groundY))
                throw new ArgumentException("A full-body reference from the imported model is required.");
            var seen = new HashSet<HumanBodyBones>();
            foreach (var entry in entries)
            {
                if (entry == null || (int)entry.bone < 0 || entry.bone >= HumanBodyBones.LastBone || !seen.Add(entry.bone) ||
                    !ArdyLocomotionClip.Finite(entry.positionInRoot)) throw new ArgumentException("Invalid imported locomotion bone reference.");
                var q = entry.rotationInRoot;
                float norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
                if (!ArdyLocomotionClip.Finite(norm) || Mathf.Abs(norm - 1) > .002f)
                    throw new ArgumentException("Invalid imported locomotion reference rotation.");
            }
            foreach (var required in RequiredBones) Get(required);
            if (Get(HumanBodyBones.Hips).positionInRoot.y - groundY < .1f)
                throw new ArgumentException("Neutral pelvis must be above the imported foot/toe support plane.");
        }

        public static readonly HumanBodyBones[] RequiredBones =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Head,
            HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
        };

        /// <summary>
        /// Caller must supply the uninstantiated imported asset, not a currently animated instance.
        /// Editor's prefab check is deliberate: runtime callers must use saved glTF import states.
        /// </summary>
        public static ArdyLocomotionAvatarReference FromImportedPrefab(Vrm10Instance importedPrefab, string sourcePath)
        {
            if (importedPrefab == null || importedPrefab.Humanoid == null || importedPrefab.gameObject.scene.IsValid())
                throw new ArgumentException("Read the imported prefab asset, never an animated scene instance.");
            var result = new ArdyLocomotionAvatarReference { sourcePath = sourcePath, avatar = importedPrefab.Vrm };
            var entries = new List<Entry>();
            var root = importedPrefab.transform;
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var bone = (HumanBodyBones)i; var tf = importedPrefab.Humanoid.GetBoneTransform(bone);
                if (tf == null) continue;
                entries.Add(new Entry { bone = bone, positionInRoot = root.InverseTransformPoint(tf.position),
                    rotationInRoot = (Quaternion.Inverse(root.rotation) * tf.rotation).normalized });
            }
            result.entries = entries.ToArray();
            result.groundY = float.PositiveInfinity;
            foreach (var entry in result.entries)
                if (entry.bone == HumanBodyBones.LeftFoot || entry.bone == HumanBodyBones.RightFoot ||
                    entry.bone == HumanBodyBones.LeftToes || entry.bone == HumanBodyBones.RightToes)
                    result.groundY = Mathf.Min(result.groundY, entry.positionInRoot.y);
            result.Validate();
            return result;
        }
    }
}
