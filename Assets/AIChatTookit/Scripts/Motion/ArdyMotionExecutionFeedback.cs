using System;

namespace NeEEvA.Motion
{
    /// <summary>Execution facts for one controller-owned action, never a claim inferred from its text.</summary>
    [Serializable]
    public sealed class ArdyMotionExecutionFeedback
    {
        public string actionId, parentActionId, name, description, status, reason, replayOf;
        public int responseGeneration, repairAttempt;
        public bool canRepair;
        public ArdyMotionGoal goal;
        public ArdyMotionObservation observation;

        public ArdyMotionExecutionFeedback Copy() => new ArdyMotionExecutionFeedback {
            actionId = actionId, parentActionId = parentActionId, name = name, description = description,
            status = status, reason = reason, replayOf = replayOf, responseGeneration = responseGeneration,
            repairAttempt = repairAttempt, canRepair = canRepair, goal = goal?.Copy(), observation = observation?.Copy() };
    }
}
