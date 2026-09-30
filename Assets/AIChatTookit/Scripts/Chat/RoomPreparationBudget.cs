using System;

/// <summary>One budget for speculative room semantics; joining never restarts its clock.</summary>
public static class RoomPreparationBudget
{
    public const int BackgroundTimeoutSeconds = 12;
    public const float FormalJoinSeconds = .5f;

    public static float JoinDeadline(float backgroundDeadline, float now)
        => Math.Min(backgroundDeadline, now + FormalJoinSeconds);
}
