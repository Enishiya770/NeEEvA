using System;
using System.Linq;
using NeEEvA.Motion;
using UnityEngine;

/// <summary>Pure native-constraint checks. These do not claim model adherence or rendered motion quality.</summary>
public static class ArdyRoomTargetBuilderRegression
{
    /// <summary>Call from the existing isolated locomotion harness and add the returned assertion count.</summary>
    public static int RunChecks()
    {
        int checks = 0;
        Action<bool, string> check = (value, message) => { checks++; if (!value) throw new InvalidOperationException(message); };
        var straight = new[] { Vector3.zero, Vector3.forward };
        var walking = ArdyRoomTargetBuilder.Build(straight, 1, Quaternion.identity, Vector3.zero, 1, .65f, 200, 0);
        check(walking.Length == 80, "Accepted one-metre walk changed its native window length.");
        check(Mathf.Abs(walking[13].rootPosition.z - .2275f) < .000001f,
            "Accepted 0.7-second acceleration distance changed.");
        check(walking.All(t => t.rootPosition.x == 0 && t.headingDegrees == 0), "Point-only walking gained a facing turn.");
        check(walking[walking.Length - 1].rootPosition == Vector3.forward, "Point-only walking misses its terminal constraint.");

        var facing = ArdyRoomTargetBuilder.Build(straight, 1, Quaternion.identity, Vector3.zero, 1, .65f, 200, 0, 90);
        int firstTurn = Array.FindIndex(facing, t => Mathf.Abs(t.headingDegrees) > .001f);
        check(firstTurn > 0 && facing[firstTurn].rootPosition == Vector3.forward, "Final facing begins before forward deceleration finishes.");
        for (int i = 0; i < firstTurn; i++)
            check(facing[i].rootPosition == walking[i].rootPosition && facing[i].headingDegrees == walking[i].headingDegrees,
                "Adding a final facing point changed the approach path.");
        CheckTurn(facing, 0, 90, check);
        for (int i = firstTurn; i < facing.Length; i++)
            check(facing[i].rootPosition == Vector3.forward, "Final facing turn moves the planned arrival position.");

        var still = ArdyRoomTargetBuilder.Build(new[] { new Vector3(2, -.7f, 3) }, 0,
            Quaternion.Euler(0, 90, 0), new Vector3(2, -.7f, 3), .9f, .65f, 200, 0, 180);
        check(still.All(t => Finite(t.rootPosition) && t.rootPosition == Vector3.zero),
            "Same-position turn produced division by zero or translation.");
        CheckTurn(still, 0, 180, check);
        var noTurn = ArdyRoomTargetBuilder.Build(new[] { Vector3.zero }, 0,
            Quaternion.identity, Vector3.zero, 1, .65f, 200, 30, 30);
        check(noTurn.All(t => t.rootPosition == Vector3.zero && t.headingDegrees == 30), "Already-facing zero route changed pose constraints.");

        var crossing = ArdyRoomTargetBuilder.Build(new[] { Vector3.zero }, 0,
            Quaternion.identity, Vector3.zero, 1, .65f, 200, 170, -170);
        CheckTurn(crossing, 170, -170, check);
        float totalTurn = 0, previous = 170;
        foreach (var target in crossing) { totalTurn += Mathf.Abs(Mathf.DeltaAngle(previous, target.headingDegrees)); previous = target.headingDegrees; }
        check(Mathf.Abs(totalTurn - 20) < .001f, "Facing across +/-180 takes the long way around.");

        bool rejected = false;
        try { ArdyRoomTargetBuilder.Build(new[] { Vector3.zero }, 0, Quaternion.identity, Vector3.zero, 1, .65f, 40, 0, 180); }
        catch (InvalidOperationException) { rejected = true; }
        check(rejected, "A facing turn beyond the native frame budget was silently truncated.");
        return checks;
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    private static void CheckTurn(ArdyLocomotionTarget[] targets, float initial, float desired, Action<bool, string> check)
    {
        float previous = initial;
        foreach (var target in targets)
        {
            check(Finite(target.headingDegrees), "Facing constraint contains a nonfinite heading.");
            check(Mathf.Abs(Mathf.DeltaAngle(previous, target.headingDegrees)) <= 4.5001f, "Facing constraint exceeds 90 degrees per second.");
            previous = target.headingDegrees;
        }
        check(Mathf.Abs(Mathf.DeltaAngle(previous, desired)) < .001f, "Facing constraints do not reach the requested final heading.");
    }
}
