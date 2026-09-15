using System;
using UnityEngine;

/// <summary>Pure observed-motion, pursuit-speed and routed-distance cases; no service or fake movement completion.</summary>
public static class ArdyRoomFollowSpeedRegression
{
    private static int checks;
    public static int RunChecks()
    {
        checks = 0;
        var velocity = new ArdyRoomUserVelocityEstimator();
        velocity.Observe(Vector3.zero, 0);
        Check(!velocity.IsMoving && velocity.Speed == 0, "A first observation invented user movement.");
        for (int i = 1; i <= 40; i++) velocity.Observe(Vector3.forward * (.65f * i / 20f), i / 20f);
        Check(velocity.IsMoving && Mathf.Abs(velocity.Speed - .65f) < .02f && velocity.Velocity.z > 0,
            "Ordinary walking speed/direction did not converge.");
        for (int i = 41; i <= 50; i++) velocity.Observe(Vector3.forward * 1.3f, i / 20f);
        Check(!velocity.IsMoving && velocity.Speed == 0, "A stopped user left a lingering pursuit velocity.");
        velocity.Reset(); velocity.Observe(Vector3.zero, 0);
        for (int i = 1; i <= 40; i++)
            velocity.Observe(new Vector3(Mathf.Sin(i) * .003f, Mathf.Sin(i) * .2f, 0), i / 20f);
        Check(!velocity.IsMoving && velocity.Speed == 0, "Head bob and bounded horizontal jitter became locomotion.");
        velocity.Reset(); velocity.Observe(Vector3.zero, 0);
        for (int i = 1; i <= 20; i++) velocity.Observe(Vector3.forward * (6f * i / 20f), i / 20f);
        Check(velocity.Speed > 5.8f && !velocity.Discontinuity, "The normal desktop sprint was mistaken for a tracking teleport.");
        velocity.Observe(Vector3.forward * 20, 1.05f);
        Check(velocity.Discontinuity && !velocity.IsMoving && velocity.Speed == 0, "A tracking jump created a huge chase impulse.");
        velocity.Observe(Vector3.forward * 20, 2f);
        Check(velocity.Discontinuity && velocity.Speed == 0, "A stale observation gap reused old velocity.");
        Check(!velocity.Observe(new Vector3(float.NaN, 0, 0), 2.1f) && !velocity.HasSample && velocity.Speed == 0,
            "Invalid tracking data survived estimator reset.");
        var fast = Sample(.05f, .65f); var slower = Sample(.10f, .65f);
        Check((fast.Velocity - slower.Velocity).magnitude < .02f, "Velocity smoothing changed with ordinary sample frequency.");
        for (int i = 1; i <= 20; i++) fast.Observe(new Vector3(0, 0, 1.3f - .65f * i / 20f), 2f + i / 20f);
        Check(fast.Velocity.z < -.6f, "Turning back retained an obsolete forward velocity.");

        bool limited;
        float command = ArdyRoomFollowMotionPolicy.DesiredSpeed(.06f, Vector3.forward, Vector3.forward * .65f,
            1.2f, 1f, true, out limited);
        Check(Mathf.Abs(command - .65f) < .001f && !limited, "An already moving partner did not produce matching feed-forward speed.");
        float catchUp = ArdyRoomFollowMotionPolicy.DesiredSpeed(2f, Vector3.forward, Vector3.forward * .65f,
            3f, 1f, true, out limited);
        Check(catchUp == 1.2f && limited, "A distant target lacked a bounded catch-up command.");
        command = ArdyRoomFollowMotionPolicy.DesiredSpeed(.2f, Vector3.forward, Vector3.forward * 2f,
            1.3f, 1f, true, out limited);
        Check(command == 1.2f && limited, "A faster camera exceeded the validated speed ceiling or hid its limit.");
        Check(ArdyRoomFollowMotionPolicy.DesiredSpeed(0, Vector3.forward, Vector3.zero, 1f, 1f, false, out _) == 0,
            "A quiet nearby partner triggered perpetual micro-walking.");
        Check(ArdyRoomFollowMotionPolicy.DesiredSpeed(.2f, Vector3.forward, Vector3.back * .8f, 1.3f, 1f, true, out _) == 0,
            "An approaching partner caused acceleration toward their body.");
        Check(ArdyRoomFollowMotionPolicy.DesiredSpeed(2f, Vector3.forward, Vector3.forward, .7f, 1f, true, out _) == 0,
            "Path error overrode the current close-user stop band.");
        Check(ArdyRoomFollowMotionPolicy.DesiredSpeed(float.NaN, Vector3.forward, Vector3.forward, 3f, 1f, true, out _) == 0,
            "An invalid path distance created a command.");
        command = ArdyRoomFollowMotionPolicy.SmoothSpeed(0, 1.2f, .1f);
        Check(Mathf.Abs(command - .1f) < .001f, "Speed command ignored its acceleration bound.");
        command = ArdyRoomFollowMotionPolicy.SmoothSpeed(1.2f, 0, .1f);
        Check(Mathf.Abs(command - 1.02f) < .001f, "Speed command ignored its deceleration bound.");
        Check(ArdyRoomFollowMotionPolicy.SmoothSpeed(0, 1.2f, 10f) <= .25f,
            "A delayed tick caused an instant full-speed command.");

        var route = new[] { Vector3.zero, new Vector3(0, 0, 2), new Vector3(3, 0, 2) };
        float remaining = ArdyRoomFollowMotionPolicy.RemainingPath(Vector3.zero, route, out Vector3 direction);
        Check(Mathf.Abs(remaining - 5f) < .001f && direction.z > 0 && direction.x == 0,
            "Routed following cut across the inside of a navigation corner.");
        remaining = ArdyRoomFollowMotionPolicy.RemainingPath(new Vector3(0, 0, 1), route, out direction);
        Check(Mathf.Abs(remaining - 4f) < .001f, "Path progress did not reduce remaining routed distance.");
        remaining = ArdyRoomFollowMotionPolicy.RemainingPath(new Vector3(1, 0, 2), route, out direction);
        Check(Mathf.Abs(remaining - 2f) < .001f && direction.x > 0, "Passing a corner retained the old steering direction.");
        Check(ArdyRoomFollowMotionPolicy.ShouldBegin(1.1f, 1f, Vector3.forward, Vector3.forward * .4f, true),
            "A moving user still had to cross the old stationary .45 m resume band.");
        Check(!ArdyRoomFollowMotionPolicy.ShouldBegin(1.1f, 1f, Vector3.forward, Vector3.zero, false) &&
            !ArdyRoomFollowMotionPolicy.ShouldBegin(1.1f, 1f, Vector3.forward, Vector3.back, true),
            "A nearby stationary/approaching user triggered pursuit.");
        Check(ArdyRoomFollowMotionPolicy.ShouldBegin(2f, 1f, Vector3.forward, Vector3.zero, false),
            "An initially stationary distant partner could not start follow.");
        return checks;
    }

    private static ArdyRoomUserVelocityEstimator Sample(float dt, float speed)
    {
        var value = new ArdyRoomUserVelocityEstimator(); value.Observe(Vector3.zero, 0);
        for (int i = 1; i <= Mathf.RoundToInt(2f / dt); i++) value.Observe(Vector3.forward * (speed * i * dt), i * dt);
        return value;
    }
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new InvalidOperationException(message); }
}
