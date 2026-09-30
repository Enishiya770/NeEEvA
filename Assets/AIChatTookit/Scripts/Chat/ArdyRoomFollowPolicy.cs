using UnityEngine;

/// <summary>Bounded follow scheduling; all actual targets still pass room support, route and body guards.</summary>
public static class ArdyRoomFollowPolicy
{
    public const float ResumeDistanceMargin = .45f;
    public const float TargetMovementThreshold = .35f;
    public const float MinimumDispatchInterval = 1.25f;
    public const float MovingTargetCheckInterval = .35f;
    public const float SettledTargetQuietPeriod = .30f;
    public const float SettledTargetDistance = .06f;
    // Recheck a temporarily unverified user support without planning or requesting motion.
    // A stationary invalid target cannot create an unbounded polling/restart loop.
    public const float SupportCheckInterval = .5f;
    public const int MaximumSupportChecks = 6;

    public static bool NeedsLeg(float distance, float desiredDistance) =>
        Finite(distance) && Finite(desiredDistance) && distance > desiredDistance + ResumeDistanceMargin + .0001f;

    public static bool TargetMoved(Vector3 sampledHead, Vector3 currentHead)
    {
        float dx = sampledHead.x - currentHead.x, dz = sampledHead.z - currentHead.z;
        return Finite(dx) && Finite(dz) && dx * dx + dz * dz > TargetMovementThreshold * TargetMovementThreshold;
    }

    public static bool TargetNeedsUpdate(Vector3 sampledHead, Vector3 currentHead, float now, float lastMovedAt)
    {
        if (TargetMoved(sampledHead, currentHead)) return true;
        float dx = sampledHead.x - currentHead.x, dz = sampledHead.z - currentHead.z;
        return Finite(now) && Finite(lastMovedAt) && now - lastMovedAt >= SettledTargetQuietPeriod &&
            Finite(dx) && Finite(dz) && dx * dx + dz * dz > SettledTargetDistance * SettledTargetDistance;
    }

    public static bool MayDispatch(float now, float lastDispatchAt, bool blockedUntilMoved, float displacement)
    {
        if (!Finite(now) || float.IsNaN(lastDispatchAt) || float.IsPositiveInfinity(lastDispatchAt)) return false;
        return now - lastDispatchAt >= MinimumDispatchInterval &&
            (!blockedUntilMoved || Finite(displacement) && displacement > TargetMovementThreshold);
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
