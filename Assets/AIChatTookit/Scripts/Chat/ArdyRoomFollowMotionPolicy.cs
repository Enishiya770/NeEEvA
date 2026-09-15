using UnityEngine;

/// <summary>Horizontal motion estimated only from caller-validated user ground observations.</summary>
public sealed class ArdyRoomUserVelocityEstimator
{
    public Vector3 Velocity { get; private set; }
    public float Speed => Velocity.magnitude;
    public bool IsMoving { get; private set; }
    public bool HasSample { get; private set; }
    public bool Discontinuity { get; private set; }
    public string State { get; private set; } = "unobserved";
    public float LastMovementAt { get; private set; }
    private Vector3 previous, movementAnchor;
    private float previousAt;

    public void Reset()
    {
        Velocity = Vector3.zero; HasSample = IsMoving = Discontinuity = false;
        State = "unobserved"; previousAt = LastMovementAt = 0;
    }

    public bool Observe(Vector3 ground, float now)
    {
        if (!Finite(ground.x) || !Finite(ground.y) || !Finite(ground.z) || !Finite(now))
        { Reset(); State = "invalid-observation"; return false; }
        ground.y = 0;
        if (!HasSample) { Seed(ground, now, false); return true; }
        float dt = now - previousAt;
        if (dt < 0 || dt > .5f) { Seed(ground, now, true); return true; }
        if (dt < .025f) return false;
        Vector3 delta = ground - previous;
        // Normal desktop movement includes a 6 m/s sprint; tracking jumps must not
        // become a huge pursuit command. Geometry remains independently guarded.
        if (delta.magnitude > .35f && delta.magnitude / dt > 8f)
        { Seed(ground, now, true); return true; }
        previous = ground; previousAt = now; Discontinuity = false;
        if ((ground - movementAnchor).sqrMagnitude >= .015f * .015f)
        { movementAnchor = ground; LastMovementAt = now; }
        Vector3 observed = Vector3.ClampMagnitude(delta / dt, 8f);
        Velocity = Vector3.Lerp(Velocity, observed, 1f - Mathf.Exp(-dt / .20f));
        if (now - LastMovementAt >= .30f) Velocity = Vector3.zero;
        IsMoving = Speed >= .08f && now - LastMovementAt < .30f;
        State = IsMoving ? "moving" : "stationary";
        return true;
    }

    private void Seed(Vector3 ground, float now, bool discontinuity)
    {
        HasSample = true; previous = movementAnchor = ground; previousAt = LastMovementAt = now;
        Velocity = Vector3.zero; IsMoving = false; Discontinuity = discontinuity;
        State = discontinuity ? "observation-reset" : "warming-up";
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}

/// <summary>Bounded speed requests; the motion controller still owns acceleration, trajectory and collision checks.</summary>
public static class ArdyRoomFollowMotionPolicy
{
    public const float MaximumSpeed = 1.2f;
    public const float GeometryInterval = .20f;
    public const float CommandInterval = .10f;
    public const float CommandAcceleration = 1f;
    public const float CommandDeceleration = 1.8f;

    public static bool ShouldBegin(float distance, float desiredDistance, Vector3 toUser, Vector3 userVelocity, bool userMoving)
    {
        if (ArdyRoomFollowPolicy.NeedsLeg(distance, desiredDistance)) return true;
        toUser.y = 0; userVelocity.y = 0;
        return Finite(distance) && Finite(desiredDistance) && userMoving && distance > desiredDistance + .08f &&
            toUser.sqrMagnitude > .0001f && Vector3.Dot(userVelocity, toUser.normalized) > .08f;
    }

    public static float DesiredSpeed(float remainingPath, Vector3 direction, Vector3 userVelocity,
        float userDistance, float desiredDistance, bool userMoving, out bool speedLimited)
    {
        speedLimited = false;
        if (!Finite(remainingPath) || !Finite(userDistance) || !Finite(desiredDistance) || remainingPath < 0 ||
            !Finite(direction.x) || !Finite(direction.z) || !Finite(userVelocity.x) || !Finite(userVelocity.z)) return 0;
        if (userDistance <= desiredDistance - .12f) return 0;
        direction.y = 0; userVelocity.y = 0;
        float feedForward = userMoving && direction.sqrMagnitude > .0001f
            ? Vector3.Dot(userVelocity, direction.normalized) : 0;
        float requested = Mathf.Max(0, feedForward + Mathf.Max(0, remainingPath - .06f) * 1.2f);
        if (!userMoving)
            requested = Mathf.Min(requested, Mathf.Sqrt(2f * CommandDeceleration * Mathf.Max(0, remainingPath - .04f)));
        speedLimited = requested > MaximumSpeed + .01f;
        return Mathf.Clamp(requested, 0, MaximumSpeed);
    }

    public static float SmoothSpeed(float current, float desired, float dt)
    {
        if (!Finite(current) || !Finite(desired) || !Finite(dt) || dt < 0) return 0;
        desired = Mathf.Clamp(desired, 0, MaximumSpeed);
        return Mathf.MoveTowards(Mathf.Clamp(current, 0, MaximumSpeed), desired,
            (desired < current ? CommandDeceleration : CommandAcceleration) * Mathf.Min(dt, .25f));
    }

    // Remaining distance follows the approved route polyline, not straight through a wall.
    // This does not move or shorten the route sent to the motion controller.
    public static float RemainingPath(Vector3 root, Vector3[] corners, out Vector3 direction)
    {
        direction = Vector3.zero;
        if (corners == null || corners.Length == 0) return 0;
        if (corners.Length == 1) { direction = Horizontal(corners[0] - root); return direction.magnitude; }
        float closest = float.PositiveInfinity, remaining = 0, tail = 0;
        for (int i = corners.Length - 2; i >= 0; i--)
        {
            Vector3 a = Horizontal(corners[i]), b = Horizontal(corners[i + 1]), p = Horizontal(root), segment = b - a;
            float length = segment.magnitude;
            float fraction = length < .0001f ? 0 : Mathf.Clamp01(Vector3.Dot(p - a, segment) / (length * length));
            Vector3 nearest = a + segment * fraction; float offset = (p - nearest).sqrMagnitude;
            if (offset < closest)
            {
                closest = offset; remaining = (b - nearest).magnitude + tail;
                direction = b - p;
                if (direction.sqrMagnitude < .0001f && i + 2 < corners.Length) direction = Horizontal(corners[i + 2]) - p;
            }
            tail += length;
        }
        return remaining;
    }
    private static Vector3 Horizontal(Vector3 value) { value.y = 0; return value; }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
