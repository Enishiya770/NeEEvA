using System;
using UnityEngine;

namespace NeEEvA.Motion
{
    /// <summary>
    /// Palm geometry uses actual humanoid finger-joint positions. Bone Euler conventions
    /// and a model's Hand.up are deliberately not assumed. No transforms are written here.
    /// </summary>
    internal static class ArdyPalmGeometry
    {
        public const float MaximumWristSwing = 80f, MaximumForearmRoll = 180f;

        internal struct Basis
        {
            public Vector3 normalInHand, longitudinalInHand;
        }

        internal struct Solution
        {
            public Quaternion forearm, hand, neutralHandActual;
            public float roll, swing, maximumCurveSwing;
        }

        public static void ValidateScale(Transform root)
        {
            Vector3 scale = root.lossyScale;
            float maximum = Mathf.Max(scale.x, scale.y, scale.z);
            if (!Finite(scale) || Mathf.Min(scale.x, scale.y, scale.z) <= 0 || root.localToWorldMatrix.determinant <= 0
                || maximum - Mathf.Min(scale.x, scale.y, scale.z) > maximum * 0.001f)
                throw new InvalidOperationException("palm-basis-unsupported: palm constraints require a positive uniform avatar scale");
        }

        public static Basis CreateBasis(ArdyConstraintPose pose, bool left)
        {
            HumanBodyBones handId = left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand;
            Measure(pose, left, out var normal, out var longitudinal);
            Quaternion inverse = Quaternion.Inverse(pose.bones[handId].actualRotation);
            return new Basis { normalInHand = inverse * normal, longitudinalInHand = inverse * longitudinal };
        }

        public static void Measure(ArdyConstraintPose pose, bool left, out Vector3 normal, out Vector3 longitudinal)
        {
            var hand = Position(pose, left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            var index = Position(pose, left ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
            var middle = Position(pose, left ? HumanBodyBones.LeftMiddleProximal : HumanBodyBones.RightMiddleProximal);
            var little = Position(pose, left ? HumanBodyBones.LeftLittleProximal : HumanBodyBones.RightLittleProximal);
            longitudinal = middle - hand;
            Vector3 radial = index - little;
            if (longitudinal.sqrMagnitude < 1e-8f || radial.sqrMagnitude < 1e-8f)
                throw new InvalidOperationException("palm-basis-unsupported: degenerate wrist/finger proximal geometry");
            longitudinal.Normalize();
            radial = Vector3.ProjectOnPlane(radial, longitudinal);
            if (radial.sqrMagnitude < 1e-8f)
                throw new InvalidOperationException("palm-basis-unsupported: collinear finger proximal geometry");
            radial.Normalize();
            // Index is the radial/thumb side on both hands. Handedness selects the volar face.
            normal = Vector3.Cross(longitudinal, radial) * (left ? -1 : 1);
            normal.Normalize();
        }

        public static Solution Solve(Quaternion forearm, Quaternion neutralHandControl,
            Quaternion handActualToControl, Vector3 forearmAxis, Basis basis, Vector3 targetNormal,
            Vector3 curveAxis, float curveAmplitude)
        {
            forearmAxis.Normalize();
            targetNormal.Normalize();
            var originalHand = neutralHandControl * Quaternion.Inverse(handActualToControl);
            Vector3 initialNormal = originalHand * basis.normalInHand;
            Vector3 from = Vector3.ProjectOnPlane(initialNormal, forearmAxis);
            Vector3 to = Vector3.ProjectOnPlane(targetNormal, forearmAxis);
            // The shortest axial turn minimizes the remaining wrist swing. A target almost
            // parallel to the forearm has no preferred roll and is tested without extra twist.
            float roll = from.sqrMagnitude > 1e-8f && to.sqrMagnitude > 1e-8f
                ? Vector3.SignedAngle(from, to, forearmAxis) : 0;
            Quaternion rolling = Quaternion.AngleAxis(roll, forearmAxis);
            Quaternion neutral = rolling * originalHand;
            Quaternion swing = FromTo(neutral * basis.normalInHand, targetNormal,
                neutral * basis.longitudinalInHand);
            Quaternion actualHand = swing * neutral;
            float baseSwing = SwingDegrees(actualHand * Quaternion.Inverse(neutral), forearmAxis);
            float curveMaximum = baseSwing;
            if (curveAmplitude > 0)
            {
                curveMaximum = MaximumCurveSwing(actualHand * Quaternion.Inverse(neutral),
                    forearmAxis, curveAxis, curveAmplitude);
            }
            if (Mathf.Abs(roll) > MaximumForearmRoll + 0.001f || curveMaximum > MaximumWristSwing + 0.001f)
                throw new InvalidOperationException(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "palm-unreachable: wrist swing including the local curve is {0:0.##} degrees (engineering limit 80); forearm roll is {1:0.##} (limit 180). Change the requested arm/elbow geometry explicitly.",
                    curveMaximum, roll));
            return new Solution { forearm = rolling * forearm, hand = actualHand * handActualToControl,
                neutralHandActual = neutral, roll = roll, swing = baseSwing, maximumCurveSwing = curveMaximum };
        }

        private static float MaximumCurveSwing(Quaternion baselineDelta, Vector3 armAxis, Vector3 curveAxis, float amplitude)
        {
            // Swing is the angle between the forearm axis and its rotated direction.
            // Rodrigues' formula makes their dot product A*cos(t)+B*sin(t)+C,
            // so both endpoints and every interior extremum can be checked exactly.
            armAxis.Normalize(); curveAxis.Normalize();
            Vector3 rotated = baselineDelta * armAxis;
            float c = Vector3.Dot(armAxis, curveAxis) * Vector3.Dot(curveAxis, rotated);
            float a = Vector3.Dot(armAxis, rotated) - c;
            float b = Vector3.Dot(armAxis, Vector3.Cross(curveAxis, rotated));
            float minimum = Mathf.Min(DotAt(-amplitude), DotAt(amplitude));
            float stationary = Mathf.Atan2(b, a) * Mathf.Rad2Deg + 180;
            for (int turn = -2; turn <= 2; turn++)
            {
                float angle = stationary + turn * 360;
                if (angle >= -amplitude && angle <= amplitude) minimum = Mathf.Min(minimum, DotAt(angle));
            }
            return Mathf.Acos(Mathf.Clamp(minimum, -1, 1)) * Mathf.Rad2Deg;

            float DotAt(float angle) => a * Mathf.Cos(angle * Mathf.Deg2Rad) + b * Mathf.Sin(angle * Mathf.Deg2Rad) + c;
        }

        public static float SwingDegrees(Quaternion delta, Vector3 longitudinalAxis)
        {
            delta = delta.normalized;
            longitudinalAxis.Normalize();
            Vector3 vector = new Vector3(delta.x, delta.y, delta.z);
            Vector3 projected = longitudinalAxis * Vector3.Dot(vector, longitudinalAxis);
            Quaternion twist = new Quaternion(projected.x, projected.y, projected.z, delta.w);
            float length = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            if (length < 1e-6f) return 180;
            twist = new Quaternion(twist.x / length, twist.y / length, twist.z / length, twist.w / length);
            return Quaternion.Angle(Quaternion.identity, delta * Quaternion.Inverse(twist));
        }

        private static Quaternion FromTo(Vector3 from, Vector3 to, Vector3 preferredAxis)
        {
            from.Normalize(); to.Normalize();
            if (Vector3.Dot(from, to) < -0.99999f)
            {
                Vector3 axis = Vector3.ProjectOnPlane(preferredAxis, from);
                if (axis.sqrMagnitude < 1e-8f)
                    throw new InvalidOperationException("palm-basis-unsupported: no stable axis for opposing palm normals");
                return Quaternion.AngleAxis(180, axis.normalized);
            }
            return Quaternion.FromToRotation(from, to);
        }

        private static Vector3 Position(ArdyConstraintPose pose, HumanBodyBones id)
        {
            if (!pose.bones.TryGetValue(id, out var value) || !Finite(value.actualPosition))
                throw new InvalidOperationException("palm-basis-unsupported: missing finite actual humanoid " + id);
            return value.actualPosition;
        }

        private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z)
            && !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);
    }
}
