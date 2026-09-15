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
            float walkingSpeed, int maxFrames, float initialHeading, float? finalHeading = null,
            float? signedInPlaceYaw = null, Vector3? continuousWorldFacePoint = null)
        {
            if (corners == null || corners.Length == 0 || !ArdyLocomotionClip.Finite(length) || length < 0 ||
                !ArdyLocomotionClip.Finite(anchorGround) || !ArdyLocomotionClip.Finite(motionScale) || motionScale <= 0 ||
                !ArdyLocomotionClip.Finite(initialHeading) ||
                (finalHeading.HasValue && !ArdyLocomotionClip.Finite(finalHeading.Value)))
                throw new ArgumentException("Invalid room target construction parameters.");
            foreach (var point in corners)
                if (!ArdyLocomotionClip.Finite(point)) throw new ArgumentException("Route contains a nonfinite point.");
            if (continuousWorldFacePoint.HasValue)
            {
                if (signedInPlaceYaw.HasValue || !ArdyLocomotionClip.Finite(continuousWorldFacePoint.Value) || length < .1f || length > 1.4f)
                    throw new ArgumentException("Continuous target-facing requires one bounded translation, not a signed in-place turn.");
                return BuildTargetFacing(corners, length, anchorRotation, anchorGround, motionScale, walkingSpeed,
                    maxFrames, initialHeading, continuousWorldFacePoint.Value);
            }
            if (signedInPlaceYaw.HasValue && (!ArdyLocomotionClip.Finite(signedInPlaceYaw.Value) ||
                Mathf.Abs(signedInPlaceYaw.Value) < 5f || Mathf.Abs(signedInPlaceYaw.Value) > 180f ||
                length > .00001f || !finalHeading.HasValue))
                throw new ArgumentException("Signed body turns require a zero-length route, a final heading and 5..180 degrees.");
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
            float finalTurn = Mathf.DeltaAngle(arrivalHeading, finalHeading.Value);
            float signedTarget = finalHeading.Value;
            if (signedInPlaceYaw.HasValue)
            {
                // A face point alone loses left/right at exactly half a revolution.
                // Select the equivalent unwrapped heading nearest the requested
                // signed turn, while still ending at the measured world face point.
                signedTarget += 360f * Mathf.Round((arrivalHeading + signedInPlaceYaw.Value - signedTarget) / 360f);
                finalTurn = signedTarget - arrivalHeading;
                if (finalTurn * signedInPlaceYaw.Value <= 0f)
                    throw new InvalidOperationException("Observed pelvis heading conflicts with the requested body-turn direction.");
            }
            float finalTurnSeconds = Mathf.Abs(finalTurn) / TurnDegreesPerSecond;
            count = RoundedCount((arrivalFrame + 1) / FramesPerSecond + finalTurnSeconds + settle, maxFrames);
            var result = new ArdyLocomotionTarget[count];
            heading = arrivalHeading;
            for (int i = 0; i < count; i++)
            {
                if (i <= arrivalFrame) { result[i] = walking[i]; continue; }
                heading = signedInPlaceYaw.HasValue
                    ? Mathf.MoveTowards(heading, signedTarget, TurnDegreesPerSecond / FramesPerSecond)
                    : Mathf.MoveTowardsAngle(heading, finalHeading.Value, TurnDegreesPerSecond / FramesPerSecond);
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

        private static ArdyLocomotionTarget[] BuildTargetFacing(Vector3[] corners, float length,
            Quaternion anchorRotation, Vector3 anchorGround, float scale, float walkingSpeed,
            int maxFrames, float initialHeading, Vector3 facePoint)
        {
            Quaternion inverse = Quaternion.Inverse(anchorRotation);
            float Heading(Vector3 point)
            {
                Vector3 direction = inverse * (facePoint - point); direction.y = 0;
                if (direction.sqrMagnitude < .0225f) throw new InvalidOperationException("观察目标过近，不能稳定约束身体朝向。");
                return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            }
            float firstHeading = Heading(corners[0]);
            int turnFrames = Mathf.CeilToInt(Mathf.Abs(Mathf.DeltaAngle(initialHeading, firstHeading)) /
                (TurnDegreesPerSecond / FramesPerSecond));
            float minimumRange = float.PositiveInfinity;
            for (float d = 0; d <= length + .001f; d += .025f)
            {
                Vector3 delta = facePoint - AtDistance(corners, Mathf.Min(d, length)); delta.y = 0;
                minimumRange = Mathf.Min(minimumRange, delta.magnitude);
            }
            // Small sidesteps close to a target may require slower translation to
            // preserve the same existing 90 degree/second heading bound.
            float speed = Mathf.Min(Mathf.Clamp(walkingSpeed, .15f, 1.2f), minimumRange * Mathf.Deg2Rad * TurnDegreesPerSecond * .8f);
            if (speed < .15f) throw new InvalidOperationException("维持目标朝向所需速度低于已验证步速，未提交动作。");
            float acceleration = speed / .7f;
            speed = Mathf.Min(speed, Mathf.Sqrt(length * acceleration));
            float ramp = speed / acceleration, movingSeconds = length / speed + ramp;
            int count = RoundedCount(turnFrames / FramesPerSecond + movingSeconds + 1f, maxFrames);
            var result = new ArdyLocomotionTarget[count];
            float heading = initialHeading;
            for (int i = 0; i < count; i++)
            {
                float t = Mathf.Max(0, (i - turnFrames) / FramesPerSecond);
                float distance;
                if (t < ramp) distance = speed * t * t / (2 * ramp);
                else if (t <= movingSeconds - ramp) distance = speed * (t - ramp / 2);
                else if (t < movingSeconds) { float remaining = movingSeconds - t; distance = length - speed * remaining * remaining / (2 * ramp); }
                else distance = length;
                Vector3 world = AtDistance(corners, Mathf.Clamp(distance, 0, length));
                float desired = Heading(world);
                if (i == 0) heading = initialHeading; // exact measured history continuity
                else if (i <= turnFrames) heading = Mathf.MoveTowardsAngle(heading, firstHeading, TurnDegreesPerSecond / FramesPerSecond);
                else
                {
                    if (Mathf.Abs(Mathf.DeltaAngle(heading, desired)) > TurnDegreesPerSecond / FramesPerSecond + .001f)
                        throw new InvalidOperationException("沿路关注目标需要超过已验证的转向速度，未提交动作。");
                    heading += Mathf.DeltaAngle(heading, desired);
                }
                Vector3 local = inverse * (world - anchorGround) / scale; local.y = 0;
                result[i] = new ArdyLocomotionTarget { rootPosition = local, headingDegrees = heading };
            }
            return result;
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
