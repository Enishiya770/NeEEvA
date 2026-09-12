using System;
using UnityEngine;

namespace NeEEvA.Motion
{
    [Serializable] public sealed class ArdyLocomotionFrame
    {
        public Vector3 rootPosition;
        public float leftFootContact, rightFootContact;
    }

    [Serializable] public sealed class ArdyLocomotionTarget
    {
        public Vector3 rootPosition;
        public float headingDegrees;
    }

    [Serializable] public sealed class ArdyLocomotionSource
    {
        public string coordinateSystem;
        public string generation;
        public string rootPositionMeaning;
    }

    /// <summary>
    /// An explicit full-body validation envelope. Inner rotationClip stays compatible with
    /// the existing rotation-only wire; root translation is applied only by this player.
    /// Source Y=0 is the neutral source foot/toe support plane, not the pelvis origin.
    /// </summary>
    [Serializable] public sealed class ArdyLocomotionClip
    {
        public int schema;
        public string id;
        public ArdyMotionClip rotationClip;
        public float sourceRootHeight;
        public Vector3 sourceOrigin;
        public float sourceHeadingDegrees;
        public ArdyLocomotionFrame[] frames;
        public ArdyLocomotionTarget[] targets;
        public ArdyLocomotionSource source;
        public float Duration => rotationClip.Duration;
        public float LastSampleSeconds => (frames.Length - 1) / rotationClip.fps;

        public static ArdyLocomotionClip Parse(string json)
        {
            var clip = JsonUtility.FromJson<ArdyLocomotionClip>(json);
            if (clip == null) throw new ArgumentException("Empty ARDY locomotion clip.");
            clip.Validate();
            return clip;
        }

        public void Validate()
        {
            if (schema != 1 || rotationClip == null) throw new ArgumentException("Expected locomotion schema 1 and rotationClip.");
            rotationClip.Validate();
            if (!string.IsNullOrEmpty(rotationClip.source.rotationApplication))
                throw new ArgumentException("Locomotion requires absolute source global rotations, not an additive profile.");
            if (!Finite(sourceRootHeight) || sourceRootHeight < .1f || sourceRootHeight > 3f ||
                !Finite(sourceOrigin) || Mathf.Abs(sourceOrigin.y) > .0001f || !Finite(sourceHeadingDegrees))
                throw new ArgumentException("Invalid source support plane, neutral pelvis height or heading.");
            if (source != null && !string.IsNullOrEmpty(source.coordinateSystem) && source.coordinateSystem != "unity-lh-y-up-z-forward")
                throw new ArgumentException("Locomotion root positions must already be in canonical Unity coordinates.");
            if (frames == null || frames.Length != rotationClip.frames.Length || targets == null || targets.Length != frames.Length)
                throw new ArgumentException("Locomotion roots, targets and rotation frames must have identical lengths.");
            if (rotationClip.jointNames[0] != "Hips") throw new ArgumentException("Core27 root must be Hips.");
            for (int i = 0; i < frames.Length; i++)
            {
                var f = frames[i]; var t = targets[i];
                if (f == null || !Finite(f.rootPosition) || !Contact(f.leftFootContact) || !Contact(f.rightFootContact) ||
                    t == null || !Finite(t.rootPosition) || !Finite(t.headingDegrees))
                    throw new ArgumentException("Locomotion contains invalid roots, targets or foot contacts at frame " + i);
            }
        }

        public Vector3 SampleRoot(float seconds)
        {
            Indices(seconds, out int a, out int b, out float blend);
            return Vector3.Lerp(frames[a].rootPosition, frames[b].rootPosition, blend);
        }

        public float SampleContact(bool left, float seconds)
        {
            Indices(seconds, out int a, out int b, out float blend);
            return Mathf.Lerp(left ? frames[a].leftFootContact : frames[a].rightFootContact,
                left ? frames[b].leftFootContact : frames[b].rightFootContact, blend);
        }

        private void Indices(float seconds, out int a, out int b, out float blend)
        {
            if (!Finite(seconds)) throw new ArgumentException("Sample time must be finite.");
            float index = Mathf.Clamp(seconds * rotationClip.fps, 0, frames.Length - 1);
            a = Mathf.FloorToInt(index); b = Mathf.Min(a + 1, frames.Length - 1); blend = index - a;
        }

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool Contact(float value) => Finite(value) && value >= 0 && value <= 1;
    }
}
