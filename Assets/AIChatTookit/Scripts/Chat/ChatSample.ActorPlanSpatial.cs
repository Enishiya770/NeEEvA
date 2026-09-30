using System;

public partial class ChatSample
{
    private double? VerifiedActorPlanBodyYaw(RoomTaskProtocol.Decision decision, string actionId, string phase)
    {
        var fact = m_SceneActionFact;
        // The transport watch is disposed before a new view is captured. Only
        // that completed action's retained measurement may close its goal.
        var result = fact?.bodyTurnResult;
        if (decision?.intent != "turn-in-place" || decision.viewChange == null || phase != "observed" ||
            result == null || result.actionId != actionId || !result.terminal || !result.oriented ||
            fact == null || fact.actionId != actionId || fact.intent != "turn-in-place" || fact.phase != "observed" ||
            m_SceneActionActive || string.IsNullOrEmpty(fact.resultCaptureId) ||
            result.captureId != decision.viewChange.captureId ||
            float.IsNaN(result.requestedYaw) || float.IsInfinity(result.requestedYaw) ||
            Math.Abs(result.requestedYaw - decision.viewChange.yaw) > .01 ||
            float.IsNaN(result.actualYaw) || float.IsInfinity(result.actualYaw) ||
            float.IsNaN(result.actualFacingErrorDegrees) || float.IsInfinity(result.actualFacingErrorDegrees) ||
            float.IsNaN(result.actualRootDisplacement) || float.IsInfinity(result.actualRootDisplacement) ||
            result.actualFacingErrorDegrees < 0 || result.actualFacingErrorDegrees > 5 ||
            result.actualRootDisplacement < 0 || result.actualRootDisplacement > .15f) return null;
        return result.actualYaw;
    }
}
