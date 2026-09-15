using System;
using UnityEngine;

namespace NeEEvA.Motion
{
    public sealed partial class ArdyLocomotionPlayer
    {
        public bool AppliesSourceJoint(int index)
        {
            if (index < 0 || index >= ArdyLiveProtocol.JointNames.Length) return false;
            string source = ArdyLiveProtocol.JointNames[index];
            foreach (var bone in bones) if (bone.source == source) return true;
            return false;
        }
        /// <summary>Replace only future samples and discard already displayed history without
        /// changing the world anchor, body ownership or animation phase.</summary>
        public void ReplaceStreamingTail(ArdyLocomotionClip replacement, int unchangedFrames, int droppedFrames, int committedTailFrames = 8)
        {
            if (!HasPoseOwnership || !IsPlaying || IsReturningToAnimation || clip == null)
                throw new InvalidOperationException("Streaming requires the same active body owner.");
            replacement.Validate(); ValidateReturnState();
            if (unchangedFrames < 1 || unchangedFrames > clip.frames.Length || droppedFrames < 0 ||
                droppedFrames > Mathf.FloorToInt(elapsed * clip.rotationClip.fps) ||
                unchangedFrames <= droppedFrames || elapsed >= (unchangedFrames - 1) / clip.rotationClip.fps ||
                (committedTailFrames != 8 && committedTailFrames != 16) ||
                replacement.frames.Length < unchangedFrames - droppedFrames + committedTailFrames ||
                replacement.rotationClip.fps != clip.rotationClip.fps ||
                replacement.sourceOrigin != clip.sourceOrigin || replacement.sourceHeadingDegrees != clip.sourceHeadingDegrees ||
                Mathf.Abs(replacement.sourceRootHeight - clip.sourceRootHeight) > .0001f)
                throw new InvalidOperationException("Streaming response missed its unplayed seam or changed the source anchor.");
            for (int j = 0; j < 27; j++)
                if (replacement.rotationClip.jointNames[j] != clip.rotationClip.jointNames[j] ||
                    replacement.rotationClip.jointParents[j] != clip.rotationClip.jointParents[j] ||
                    Quaternion.Angle(replacement.rotationClip.restGlobalRotations[j], clip.rotationClip.restGlobalRotations[j]) > .001f)
                    throw new InvalidOperationException("Streaming changed the skeleton basis.");
            for (int i = droppedFrames; i < unchangedFrames; i++)
                if (replacement.frames[i - droppedFrames] != clip.frames[i] ||
                    replacement.rotationClip.frames[i - droppedFrames] != clip.rotationClip.frames[i])
                    throw new InvalidOperationException("Streaming modified a committed sample.");
            elapsed -= droppedFrames / clip.rotationClip.fps;
            clip = replacement; PlaybackEndSeconds = (unchangedFrames - droppedFrames + committedTailFrames - 1) / 20f;
            PlaybackRevision++; CompletedNaturally = false;
            RebaseRecordedSourcePose(droppedFrames / clip.rotationClip.fps);
            Status = "连续播放 · 短段步态更新";
        }
    }
}
