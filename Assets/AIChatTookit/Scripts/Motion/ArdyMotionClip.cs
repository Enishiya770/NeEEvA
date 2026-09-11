using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeEEvA.Motion
{
    [Serializable]
    public sealed class ArdyMotionFrame
    {
        public Quaternion[] globalRotations;
    }

    [Serializable]
    public sealed class ArdyMotionSource
    {
        public string coordinateSystem;
        public bool rootTranslationApplied;
        public string rotationApplication;
    }

    /// <summary>Core27 rotations in Unity coordinates, relative to the source neutral T-pose.</summary>
    [Serializable]
    public sealed class ArdyMotionClip
    {
        public int schema;
        public string id;
        public string text;
        public float fps;
        public string[] jointNames;
        public int[] jointParents;
        public Quaternion[] restGlobalRotations;
        public ArdyMotionFrame[] frames;
        public ArdyMotionSource source;

        public float Duration => frames.Length / fps;

        public static ArdyMotionClip Parse(string json)
        {
            var clip = JsonUtility.FromJson<ArdyMotionClip>(json);
            if (clip == null) throw new ArgumentException("Empty ARDY motion clip.");
            clip.Validate();
            return clip;
        }

        public void Validate()
        {
            if (schema != 1 || source == null || source.coordinateSystem != "unity-lh-y-up-z-forward" || source.rootTranslationApplied)
                throw new ArgumentException("Expected schema 1, Unity coordinates and rotation-only ARDY motion.");
            if (!string.IsNullOrEmpty(source.rotationApplication) && source.rotationApplication != "additive-local")
                throw new ArgumentException("Unsupported motion rotation application.");
            if (!Finite(fps) || fps <= 0 || fps > 240 || frames == null || frames.Length < 2 || frames.Length > 14400)
                throw new ArgumentException("Invalid motion frame rate or length.");
            if (jointNames == null || jointNames.Length != 27 || jointParents == null || jointParents.Length != 27 ||
                restGlobalRotations == null || restGlobalRotations.Length != 27)
                throw new ArgumentException("Expected a Core27 skeleton.");
            var names = new HashSet<string>();
            for (int j = 0; j < 27; j++)
            {
                if (string.IsNullOrWhiteSpace(jointNames[j]) || !names.Add(jointNames[j]) ||
                    (j == 0 ? jointParents[j] != -1 : jointParents[j] < 0 || jointParents[j] >= j))
                    throw new ArgumentException("Invalid skeleton names or hierarchy.");
                CheckRotation(restGlobalRotations[j]);
            }
            foreach (var frame in frames)
            {
                if (frame?.globalRotations == null || frame.globalRotations.Length != 27)
                    throw new ArgumentException("Inconsistent motion frame.");
                foreach (var rotation in frame.globalRotations) CheckRotation(rotation);
            }
        }

        public Quaternion SampleDelta(int joint, float seconds)
        {
            float position = Mathf.Clamp(seconds * fps, 0, frames.Length - 1);
            int a = Mathf.FloorToInt(position), b = Mathf.Min(a + 1, frames.Length - 1);
            var rotation = Quaternion.Slerp(frames[a].globalRotations[joint], frames[b].globalRotations[joint], position - a);
            return rotation * Quaternion.Inverse(restGlobalRotations[joint]);
        }

        public Quaternion SampleLocalDelta(int joint, float seconds)
        {
            var delta = SampleDelta(joint, seconds);
            int parent = jointParents[joint];
            return parent < 0 ? delta : Quaternion.Inverse(SampleDelta(parent, seconds)) * delta;
        }

        private static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);

        private static void CheckRotation(Quaternion q)
        {
            float norm = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            if (!Finite(norm) || Mathf.Abs(norm - 1) > 0.002f)
                throw new ArgumentException("Motion contains a non-unit or non-finite quaternion.");
        }
    }
}
