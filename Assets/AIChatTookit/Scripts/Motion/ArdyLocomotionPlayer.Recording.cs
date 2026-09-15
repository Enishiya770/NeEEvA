using System;
using UnityEngine;

namespace NeEEvA.Motion
{
    public sealed partial class ArdyLocomotionPlayer
    {
        [Serializable] public sealed class PoseRecordingBinding
        {
            public HumanBodyBones human;
            public string sourceJoint, drivenTransform;
            public int sourceJointIndex;
            public Quaternion applicationRestRotation;
            public bool drivesControlRig;
        }
        [Serializable] public sealed class PoseRecordingIdentity
        {
            public int operationRevision, wireRevision, globalBaseFrame;
            public string sessionId;
        }

        private ArdyLocomotionClip lastAppliedSourceClip;
        private float lastAppliedSourceTimeSeconds = -1;
        private int lastAppliedSourceUnityFrame = -1;
        private ArdyLocomotionClip identifiedRecordingClip;
        private PoseRecordingIdentity recordingIdentity;
        /// <summary>The successfully applied source time, not a clock advanced before a rejected root step.</summary>
        public float LastAppliedSourceTimeSeconds => lastAppliedSourceTimeSeconds;
        public bool HasAppliedCurrentSource => clip != null && ReferenceEquals(clip, lastAppliedSourceClip);
        public int LastAppliedSourceUnityFrame => lastAppliedSourceUnityFrame;

        /// <summary>Called when a controller adopts an audited clip, before a later cancel can change its active operation.</summary>
        public void SetPoseRecordingIdentity(int operationRevision, int wireRevision, string sessionId, int baseFrame)
        {
            identifiedRecordingClip = clip;
            recordingIdentity = new PoseRecordingIdentity { operationRevision = operationRevision, wireRevision = wireRevision,
                sessionId = sessionId, globalBaseFrame = baseFrame };
        }
        public PoseRecordingIdentity GetPoseRecordingIdentity()
        {
            if (recordingIdentity == null || !ReferenceEquals(clip, identifiedRecordingClip)) return null;
            return new PoseRecordingIdentity { operationRevision = recordingIdentity.operationRevision,
                wireRevision = recordingIdentity.wireRevision, sessionId = recordingIdentity.sessionId,
                globalBaseFrame = recordingIdentity.globalBaseFrame };
        }

        // Called only after ApplySample has successfully written the root and every mapped bone.
        private void RecordAppliedSourcePose(float seconds)
        { lastAppliedSourceClip = clip; lastAppliedSourceTimeSeconds = seconds; lastAppliedSourceUnityFrame = Time.frameCount; }

        // Tail replacement preserves the already rendered pose while changing the local source timeline.
        private void RebaseRecordedSourcePose(float removedSeconds)
        {
            if (lastAppliedSourceClip == null) return;
            lastAppliedSourceClip = clip;
            lastAppliedSourceTimeSeconds = Mathf.Max(0, lastAppliedSourceTimeSeconds - removedSeconds);
        }

        /// <summary>Copies the actual calibrated application bases; never changes a bone or body owner.</summary>
        public PoseRecordingBinding[] GetPoseRecordingBindings()
        {
            var result = new PoseRecordingBinding[bones.Count];
            for (int i = 0; i < bones.Count; i++)
            {
                var bone = bones[i];
                result[i] = new PoseRecordingBinding { human = bone.human, sourceJoint = bone.source,
                    sourceJointIndex = bone.index, applicationRestRotation = bone.rest,
                    drivesControlRig = rig != null, drivenTransform = bone.transform == null ? null : bone.transform.name };
            }
            return result;
        }
    }
}
