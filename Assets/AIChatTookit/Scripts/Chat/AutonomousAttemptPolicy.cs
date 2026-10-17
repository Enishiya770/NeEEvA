using System;

public static class AutonomousAttemptPolicy
{
    // Geometry is only one component of an attempt. A different operation is
    // not a retry, but repeated failed work still needs executor recovery.
    public static bool Suppress(bool sameTarget, string previousOperation, string proposedOperation,
        string previousPhase, string previousAction, string replacedAction, bool failedSharedPrerequisite=false)
    {
        if (!sameTarget) return false;
        // The executor, not an intention's wording, identifies a shared failed
        // prerequisite (e.g. the unchanged route for two different body tasks).
        if (failedSharedPrerequisite) return true;
        if (!string.IsNullOrEmpty(previousOperation) && previousOperation != proposedOperation) return false;
        if (!string.IsNullOrEmpty(replacedAction) && previousAction == replacedAction &&
            (previousPhase=="cancelled" || !RoomMovementResultPolicy.IsTerminalFailure(previousPhase))) return false;
        return true;
    }
}
