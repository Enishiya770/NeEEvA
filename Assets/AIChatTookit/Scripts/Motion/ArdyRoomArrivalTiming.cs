using System;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>
    /// Room walking playback policy, applied only after the full generated path is audited.
    /// Native window padding remains in the source clip. Other actions may intentionally hold
    /// a pose, so the general locomotion player never applies this policy automatically.
    /// </summary>
    public sealed class ArdyRoomArrivalTiming
    {
        public int TerminalHoldStartFrame { get; }
        public int PlaybackEndFrame { get; }
        public float PlannedArrivalSeconds { get; }
        public float PlaybackEndSeconds { get; }
        public float SkippedTailSeconds { get; }

        private ArdyRoomArrivalTiming(int terminal, int end, int last, float fps)
        {
            TerminalHoldStartFrame = terminal; PlaybackEndFrame = end;
            PlannedArrivalSeconds = terminal / fps; PlaybackEndSeconds = end / fps;
            SkippedTailSeconds = (last - end) / fps;
        }

        public static ArdyRoomArrivalTiming Select(ArdyLocomotionClip clip, Vector3[] worldRoots,
            Vector3 worldTarget, float arrivalTolerance)
        {
            if (clip == null) throw new ArgumentNullException(nameof(clip));
            clip.Validate();
            if (worldRoots == null || worldRoots.Length != clip.frames.Length || worldRoots.Length < 2 ||
                !ArdyLocomotionClip.Finite(worldTarget) || !ArdyLocomotionClip.Finite(arrivalTolerance) || arrivalTolerance <= 0)
                throw new ArgumentException("Arrival timing needs an audited world trajectory and a positive arrival tolerance.");
            foreach (var root in worldRoots)
                if (!ArdyLocomotionClip.Finite(root)) throw new ArgumentException("The audited trajectory contains a nonfinite root.");

            float fps = clip.rotationClip.fps;
            int last = worldRoots.Length - 1;
            int terminal = last;
            while (terminal > 0 && SameTarget(clip.targets[terminal - 1], clip.targets[last])) terminal--;
            var full = new ArdyRoomArrivalTiming(terminal, last, last, fps);
            // Allow the return as soon as the final planned deceleration/turn finishes,
            // with no extra hold. Stability is checked in already-generated frames below.
            // A mid-route pause or pass near the destination can never start the return.
            int stabilityFrames = Mathf.Max(1, Mathf.CeilToInt(.15f * fps));
            int minimumSaving = Mathf.CeilToInt(.35f * fps);
            var headings = new float[worldRoots.Length];
            for (int i = 0; i <= last; i++)
            {
                Vector3 forward = clip.rotationClip.SampleDelta(0, i / fps) * Vector3.forward;
                forward.y = 0;
                if (forward.sqrMagnitude < .0001f) return full;
                headings[i] = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            }
            float radius = Mathf.Min(arrivalTolerance, .05f);
            for (int end = Mathf.Max(stabilityFrames, terminal); end <= last - minimumSaving; end++)
            {
                bool stable = true;
                for (int i = end - stabilityFrames + 1; i <= end; i++)
                    if (HorizontalDistance(worldRoots[i], worldRoots[i - 1]) * fps > .12f ||
                        Mathf.Abs(Mathf.DeltaAngle(headings[i - 1], headings[i])) * fps > 20f)
                    { stable = false; break; }
                if (!stable) continue;
                // Check the remaining generated tail as well: do not hide a late overshoot,
                // additional movement or unfinished turn simply because this one frame is close.
                for (int i = end; i <= last; i++)
                    if (HorizontalDistance(worldRoots[i], worldTarget) > radius ||
                        HorizontalDistance(worldRoots[i], worldRoots[end]) > .04f ||
                        Mathf.Abs(Mathf.DeltaAngle(headings[i], clip.targets[last].headingDegrees)) > 6f ||
                        Mathf.Abs(Mathf.DeltaAngle(headings[i], headings[end])) > 5f)
                    { stable = false; break; }
                if (stable) return new ArdyRoomArrivalTiming(terminal, end, last, fps);
            }
            return full;
        }

        private static bool SameTarget(ArdyLocomotionTarget a, ArdyLocomotionTarget b) =>
            // These are explicit repeated commands, not noisy measured positions. Even the
            // final submillimetre deceleration belongs to the route, before settling begins.
            a.rootPosition.x == b.rootPosition.x && a.rootPosition.z == b.rootPosition.z &&
            Mathf.DeltaAngle(a.headingDegrees, b.headingDegrees) == 0f;

        private static float HorizontalDistance(Vector3 a, Vector3 b) =>
            Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
    }
}
