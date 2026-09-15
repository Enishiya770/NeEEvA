using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>A continuous native target timeline, sent in bounded requests with an audited stopping tail.</summary>
    public sealed class ArdyRoomSegmentPlan
    {
        public sealed class Segment
        {
            public int StartFrame, ContinueAfterFrames;
            public bool Final;
            public ArdyLocomotionTarget[] Targets;
        }
        public readonly List<Segment> Segments = new List<Segment>();
        public int TotalFrames { get; private set; }

        public void OffsetFrames(int offset)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            foreach (var segment in Segments) segment.StartFrame += offset;
        }

        /// <summary>Continue the measured source speed along a newly navigated same-floor path.</summary>
        public static ArdyLocomotionTarget[] BuildMovingTimeline(Vector3[] corners, float length,
            Quaternion anchorRotation, Vector3 anchorGround, float motionScale, float walkingSpeed,
            ArdyRoomHistoryFrame[] history, float? finalHeading)
        {
            if (corners == null || corners.Length == 0 || history == null || history.Length != 16 ||
                !ArdyLocomotionClip.Finite(length) || length < 0 || motionScale <= 0)
                throw new ArgumentException("Moving route requires an explicit future seam and navigated path.");
            Vector3 displacement = history[15].rootPosition - history[12].rootPosition; displacement.y = 0;
            float velocity = displacement.magnitude * motionScale / .15f;
            float speed = Mathf.Clamp(walkingSpeed, .15f, 1.2f);
            float acceleration = Mathf.Max(.2f, speed / .7f);
            if (velocity > 1.5f || length < velocity * velocity / (2 * acceleration) + .08f)
                throw new InvalidOperationException("最新站位离未来接缝过近，无法连续减速；需要先停稳。");
            Vector3 forward = history[15].globalRotations[0] * Vector3.forward; forward.y = 0;
            float heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            Vector3 firstDirection = Quaternion.Inverse(anchorRotation) * (AtDistance(corners, Mathf.Min(.2f, length)) - corners[0]);
            float firstHeading = Mathf.Atan2(firstDirection.x, firstDirection.z) * Mathf.Rad2Deg;
            float turn = Mathf.Abs(Mathf.DeltaAngle(heading, firstHeading));
            if (velocity > .15f && turn > 75f)
                throw new InvalidOperationException("新方向需要大幅转身；先停稳再转向。");
            var values = new List<ArdyLocomotionTarget>();
            if (velocity <= .15f && turn > 5f)
            {
                // Match the ordinary room target builder's stand-turn-walk ordering. At a
                // stationary/very slow future seam, starting translation in the new direction
                // while the pelvis still turns can otherwise command a visible sideways glide.
                // Faster turnabouts are rejected above instead of cancelling their momentum.
                velocity = 0;
                Vector3 stance = history[15].rootPosition; stance.y = 0;
                while (Mathf.Abs(Mathf.DeltaAngle(heading, firstHeading)) > .001f && values.Count < 2360)
                {
                    heading = Mathf.MoveTowardsAngle(heading, firstHeading, 4.5f);
                    values.Add(new ArdyLocomotionTarget { rootPosition = stance, headingDegrees = heading });
                }
            }
            float distance = 0;
            while ((distance < length - .00001f || velocity > .005f) && values.Count < 2360)
            {
                float remaining = Mathf.Max(0, length - distance);
                float desired = Mathf.Min(speed, Mathf.Sqrt(2 * acceleration * remaining));
                float next = Mathf.MoveTowards(velocity, desired, acceleration / 20f);
                distance = Mathf.Min(length, distance + (velocity + next) / 40f); velocity = next;
                Vector3 point = AtDistance(corners, distance);
                Vector3 direction = Quaternion.Inverse(anchorRotation) * (AtDistance(corners, Mathf.Min(length, distance + .2f)) - point);
                if (direction.sqrMagnitude > .00001f)
                    heading = Mathf.MoveTowardsAngle(heading, Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg, 4.5f);
                Vector3 source = Quaternion.Inverse(anchorRotation) * (point - anchorGround) / motionScale; source.y = 0;
                values.Add(new ArdyLocomotionTarget { rootPosition = source, headingDegrees = heading });
            }
            if (values.Count == 0 || values.Count >= 2360) throw new InvalidOperationException("滚动路径超过连续任务预算。");
            Vector3 endpoint = values[values.Count - 1].rootPosition;
            if (finalHeading.HasValue)
                while (Mathf.Abs(Mathf.DeltaAngle(heading, finalHeading.Value)) > .001f && values.Count < 2360)
                {
                    heading = Mathf.MoveTowardsAngle(heading, finalHeading.Value, 4.5f);
                    values.Add(new ArdyLocomotionTarget { rootPosition = endpoint, headingDegrees = heading });
                }
            int count = Mathf.Max(40, Mathf.CeilToInt((values.Count + 20) / 40f) * 40);
            if (count > 2400) throw new InvalidOperationException("滚动路径与转向超过连续任务预算。");
            while (values.Count < count) values.Add(new ArdyLocomotionTarget { rootPosition = endpoint, headingDegrees = heading });
            return values.ToArray();
        }

        private static Vector3 AtDistance(Vector3[] corners, float distance)
        {
            for (int i = 1; i < corners.Length; i++)
            {
                float segment = Vector3.Distance(corners[i - 1], corners[i]);
                if (distance <= segment) return Vector3.Lerp(corners[i - 1], corners[i], segment < .00001f ? 1 : distance / segment);
                distance -= segment;
            }
            return corners[corners.Length - 1];
        }

        public static ArdyRoomSegmentPlan Build(ArdyLocomotionTarget[] timeline, int maxFrames)
        {
            if (timeline == null || timeline.Length < 40 || timeline.Length % 40 != 0 ||
                maxFrames < 120 || maxFrames % 40 != 0)
                throw new ArgumentException("Room segments require complete native windows and a stopping tail.");
            var plan = new ArdyRoomSegmentPlan { TotalFrames = timeline.Length };
            foreach (var target in timeline)
                if (target == null || !ArdyLocomotionClip.Finite(target.rootPosition) ||
                    Mathf.Abs(target.rootPosition.x) > 19 || Mathf.Abs(target.rootPosition.z) > 19)
                    throw new InvalidOperationException("同一连续任务超出源坐标范围；请选择更近的同层目标。");
            int prefix = maxFrames - 80;
            for (int start = 0; start < timeline.Length;)
            {
                int remaining = timeline.Length - start;
                bool final = remaining <= maxFrames;
                int count = final ? remaining : maxFrames;
                var targets = new ArdyLocomotionTarget[count];
                for (int i = 0; i < count; i++) targets[i] = Copy(timeline[start + Mathf.Min(i, remaining - 1)]);
                if (!final)
                {
                    // The first prefix is the unchanged whole-route timeline. The remaining
                    // 4 seconds are an emergency deceleration and hold, never a mid-step freeze.
                    // Stop after 0.7 seconds using the original path, so bends stay on the path.
                    const int rampFrames = 14;
                    for (int i = prefix; i < count; i++)
                    {
                        float t = Mathf.Min(i - prefix + 1, rampFrames);
                        float index = prefix - 1 + t - t * t / (2 * rampFrames);
                        int a = Mathf.FloorToInt(index), b = Mathf.Min(a + 1, count - 1);
                        float blend = index - a;
                        targets[i] = new ArdyLocomotionTarget {
                            rootPosition = Vector3.Lerp(timeline[start + a].rootPosition, timeline[start + b].rootPosition, blend),
                            headingDegrees = Mathf.LerpAngle(timeline[start + a].headingDegrees, timeline[start + b].headingDegrees, blend)
                        };
                    }
                }
                plan.Segments.Add(new Segment { StartFrame = start, ContinueAfterFrames = final ? count : prefix,
                    Final = final, Targets = targets });
                start += final ? count : prefix;
            }
            return plan;
        }

        public static ArdyRoomHistoryFrame[] GeneratedHistory(ArdyLocomotionClip clip, int beforeFrame)
        {
            if (clip == null || beforeFrame < 16 || beforeFrame > clip.frames.Length)
                throw new ArgumentException("Continuation needs sixteen preceding audited source frames.");
            var history = new ArdyRoomHistoryFrame[16];
            for (int i = 0; i < history.Length; i++)
            {
                int frame = beforeFrame - 16 + i;
                var rotations = new Quaternion[27];
                for (int j = 0; j < rotations.Length; j++) rotations[j] = clip.rotationClip.SampleDelta(j, frame / 20f);
                history[i] = new ArdyRoomHistoryFrame { rootPosition = clip.frames[frame].rootPosition, globalRotations = rotations };
            }
            return history;
        }

        public static ArdyLocomotionTarget[] ContinueHeading(ArdyLocomotionTarget[] planned, ArdyRoomHistoryFrame last)
        {
            if (planned == null || planned.Length == 0 || last?.globalRotations == null || last.globalRotations.Length != 27)
                throw new ArgumentException("Continuation targets require an explicit source seam pose.");
            Vector3 forward = last.globalRotations[0] * Vector3.forward; forward.y = 0;
            if (forward.sqrMagnitude < .0001f) throw new ArgumentException("Continuation pelvis heading is undefined.");
            float heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            var result = new ArdyLocomotionTarget[planned.Length];
            for (int i = 0; i < result.Length; i++)
            {
                heading = Mathf.MoveTowardsAngle(heading, planned[i].headingDegrees,
                    ArdyRoomTargetBuilder.TurnDegreesPerSecond / ArdyRoomTargetBuilder.FramesPerSecond);
                result[i] = new ArdyLocomotionTarget { rootPosition = planned[i].rootPosition, headingDegrees = heading };
            }
            return result;
        }

        public static ArdyLocomotionClip Append(ArdyLocomotionClip previous, int prefixFrames, ArdyLocomotionClip next,
            out float rootStep, out float maximumJointStep)
        {
            previous.Validate(); next.Validate();
            if (prefixFrames < 16 || prefixFrames > previous.frames.Length ||
                Mathf.Abs(previous.sourceRootHeight - next.sourceRootHeight) > .0001f ||
                Mathf.Abs(previous.rotationClip.fps - 20) > .001f || Mathf.Abs(next.rotationClip.fps - 20) > .001f ||
                previous.sourceOrigin != next.sourceOrigin || previous.sourceHeadingDegrees != next.sourceHeadingDegrees)
                throw new ArgumentException("Continuation must use the same fixed source anchor.");
            rootStep = Vector3.Distance(previous.frames[prefixFrames - 1].rootPosition, next.frames[0].rootPosition);
            maximumJointStep = 0;
            for (int j = 0; j < 27; j++)
            {
                if (previous.rotationClip.jointNames[j] != next.rotationClip.jointNames[j] ||
                    Quaternion.Angle(previous.rotationClip.restGlobalRotations[j], next.rotationClip.restGlobalRotations[j]) > .001f)
                    throw new ArgumentException("Continuation skeleton/rest rotations changed.");
                maximumJointStep = Mathf.Max(maximumJointStep, Quaternion.Angle(
                    previous.rotationClip.SampleDelta(j, (prefixFrames - 1) / 20f), next.rotationClip.SampleDelta(j, 0)));
            }
            if (rootStep > .10f || maximumJointStep > 35f)
                throw new InvalidOperationException($"续段接缝不连续（源位移 {rootStep:F3} 米、关节 {maximumJointStep:F1} 度）；已拒绝续播。");
            int count = prefixFrames + next.frames.Length;
            var result = new ArdyLocomotionClip { schema = previous.schema, id = previous.id,
                sourceRootHeight = previous.sourceRootHeight, sourceOrigin = previous.sourceOrigin,
                sourceHeadingDegrees = previous.sourceHeadingDegrees, source = previous.source,
                frames = new ArdyLocomotionFrame[count], targets = new ArdyLocomotionTarget[count],
                rotationClip = new ArdyMotionClip { schema = previous.rotationClip.schema, id = previous.rotationClip.id,
                    text = previous.rotationClip.text, fps = previous.rotationClip.fps, source = previous.rotationClip.source,
                    jointNames = previous.rotationClip.jointNames, jointParents = previous.rotationClip.jointParents,
                    restGlobalRotations = previous.rotationClip.restGlobalRotations, frames = new ArdyMotionFrame[count] } };
            for (int i = 0; i < count; i++)
            {
                var from = i < prefixFrames ? previous : next;
                int index = i < prefixFrames ? i : i - prefixFrames;
                result.frames[i] = from.frames[index]; result.targets[i] = from.targets[index];
                result.rotationClip.frames[i] = from.rotationClip.frames[index];
            }
            result.Validate(); return result;
        }

        private static ArdyLocomotionTarget Copy(ArdyLocomotionTarget t) =>
            new ArdyLocomotionTarget { rootPosition = t.rootPosition, headingDegrees = t.headingDegrees };
    }
}
