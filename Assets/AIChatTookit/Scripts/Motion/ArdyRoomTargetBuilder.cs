using System;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>Builds native position/heading constraints; never moves a Unity transform.</summary>
    public static class ArdyRoomTargetBuilder
    {
        public const float FramesPerSecond = 20f;
        public const float TurnDegreesPerSecond = 90f;

        public static ArdyLocomotionTarget[] Build(Vector3[] corners, float length,
            Quaternion anchorRotation, Vector3 anchorGround, float motionScale,
            float walkingSpeed, int maxFrames, float initialHeading, float? finalHeading = null)
        {
            if (corners == null || corners.Length == 0 || !ArdyLocomotionClip.Finite(length) || length < 0 ||
                !ArdyLocomotionClip.Finite(anchorGround) || !ArdyLocomotionClip.Finite(motionScale) || motionScale <= 0 ||
                !ArdyLocomotionClip.Finite(initialHeading) ||
                (finalHeading.HasValue && !ArdyLocomotionClip.Finite(finalHeading.Value)))
                throw new ArgumentException("Invalid room target construction parameters.");
            foreach (var point in corners)
                if (!ArdyLocomotionClip.Finite(point)) throw new ArgumentException("Route contains a nonfinite point.");
            float speed = Mathf.Clamp(walkingSpeed, .15f, 1.2f);
            const float settle = 1f;
            float acceleration = speed / .7f;
            float ramp = 0, movingSeconds = 0, turnSeconds = 0;
            if (length > .00001f)
            {
                speed = Mathf.Min(speed, Mathf.Sqrt(length * acceleration));
                ramp = speed / acceleration;
                movingSeconds = length / speed + ramp;
                Vector3 firstDirection = Quaternion.Inverse(anchorRotation) * (AtDistance(corners, Mathf.Min(.2f, length)) - corners[0]);
                float firstHeading = Mathf.Atan2(firstDirection.x, firstDirection.z) * Mathf.Rad2Deg;
                turnSeconds = Mathf.Abs(Mathf.DeltaAngle(initialHeading, firstHeading)) / TurnDegreesPerSecond;
            }
            int count = RoundedCount(turnSeconds + movingSeconds + settle, maxFrames);
            var walking = new ArdyLocomotionTarget[count];
            float heading = initialHeading;
            for (int i = 0; i < count; i++)
            {
                float t = Mathf.Max(0, (i + 1) / FramesPerSecond - turnSeconds);
                float distance;
                if (length <= .00001f) distance = 0;
                else if (t < ramp) distance = speed * t * t / (2 * ramp);
                else if (t <= movingSeconds - ramp) distance = speed * (t - ramp / 2);
                else if (t < movingSeconds) { float remaining = movingSeconds - t; distance = length - speed * remaining * remaining / (2 * ramp); }
                else distance = length;
                distance = Mathf.Clamp(distance, 0, length);
                Vector3 world = AtDistance(corners, distance);
                Vector3 ahead = AtDistance(corners, Mathf.Min(length, distance + .2f));
                Vector3 direction = Quaternion.Inverse(anchorRotation) * (ahead - world);
                if (direction.sqrMagnitude > .00001f)
                    heading = Mathf.MoveTowardsAngle(heading, Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg, TurnDegreesPerSecond / FramesPerSecond);
                Vector3 local = Quaternion.Inverse(anchorRotation) * (world - anchorGround) / motionScale;
                local.y = 0;
                walking[i] = new ArdyLocomotionTarget { rootPosition = local, headingDegrees = heading };
            }
            // Keep the accepted point-only walking request byte-for-byte equivalent. Facing is
            // an explicit extra phase after deceleration, represented entirely by native targets.
            if (!finalHeading.HasValue) return walking;
            int arrivalFrame = Mathf.Max(0, Mathf.CeilToInt((turnSeconds + movingSeconds) * FramesPerSecond) - 1);
            float arrivalHeading = walking[arrivalFrame].headingDegrees;
            float finalTurnSeconds = Mathf.Abs(Mathf.DeltaAngle(arrivalHeading, finalHeading.Value)) / TurnDegreesPerSecond;
            count = RoundedCount((arrivalFrame + 1) / FramesPerSecond + finalTurnSeconds + settle, maxFrames);
            var result = new ArdyLocomotionTarget[count];
            heading = arrivalHeading;
            for (int i = 0; i < count; i++)
            {
                if (i <= arrivalFrame) { result[i] = walking[i]; continue; }
                heading = Mathf.MoveTowardsAngle(heading, finalHeading.Value, TurnDegreesPerSecond / FramesPerSecond);
                result[i] = new ArdyLocomotionTarget { rootPosition = walking[arrivalFrame].rootPosition, headingDegrees = heading };
            }
            return result;
        }

        private static int RoundedCount(float seconds, int maxFrames)
        {
            int count = Mathf.Max(40, Mathf.CeilToInt(seconds * FramesPerSecond / 40f) * 40);
            if (count > maxFrames) throw new InvalidOperationException($"目标路径/转向及收尾需要约 {seconds:F2} 秒，按生成窗口取整为 {count / FramesPerSecond:F1} 秒（{count} 帧）；本次上限 {maxFrames / FramesPerSecond:F1} 秒（{maxFrames} 帧）。这是单次动作时长限制，请先选择更近的位置。 ");
            return count;
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
    }
}
