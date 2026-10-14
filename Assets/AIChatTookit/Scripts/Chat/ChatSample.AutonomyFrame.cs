using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // The private consumer chooses WHETHER another decision is worthwhile. It
    // does not resolve targets or routes. Keep its facts separate from the full
    // public/planning frame, which remains available for the ensuing decision.
    private string BuildAutonomyIntentFrame(string reason)
    {
        if (m_EnableMemoryRecall && m_MemoryHub != null && m_ChatSettings?.m_ChatModel != null)
            m_ChatSettings.m_ChatModel.TrailingContext = m_MemoryHub.BuildMemoryMap() ?? "";
        var attention = ReadAttentionState("autonomy-intent");
        var selected = new JArray();
        foreach (JObject entry in (JArray)attention["selectedEntities"])
        {
            var remembered = m_SceneVisibleMemory.LastOrDefault(x => x.entry.entityId == (string)entry["entityId"] &&
                x.view.observation.CaptureId == (string)entry["sourceCaptureId"] && SceneVisibleCatalogHistoryBinding(x.view));
            var row = (JObject)entry.DeepClone();
            row["modelDescription"] = remembered?.description;
            row["matchedOriginalTarget"] = remembered?.matchedOriginalTarget;
            row["historical"] = true;
            selected.Add(row);
        }
        var goal = BuildSpatialGoalContext();
        var search = BuildSceneSearchContext();
        var runtime = new JObject {
            ["version"] = "autonomy-runtime-v1", ["attention"] = attention, ["observedMemory"] = selected,
            ["time"] = BuildSceneTimeContext(),
            ["work"] = new JObject {
                ["open"] = m_WorkReviewOpen, ["status"] = m_WorkStatus, ["epoch"] = m_WorkEpoch,
                ["originalRequest"] = m_ActionConstraints.CurrentUserText, ["originalInputEvidence"] = m_LastUserMsg,
                ["previousUnfinishedWork"] = m_PreviousUnfinishedWork, ["progress"] = m_WorkProgressFact,
                ["responses"] = new JArray(m_WorkResponses), ["intent"] = m_WorkIntent,
                ["continuationCount"] = m_WorkContinuations, ["maximumContinuations"] = MaxWorkContinuations,
                ["lastInspection"] = m_LastSelfInspection,
                ["lastMotionExecution"] = m_LastMotionExecutionFeedback == null ? null : MotionFeedbackJson(m_LastMotionExecutionFeedback),
                ["toolInFlight"] = HasWorkToolInFlight(),
                ["taskSequence"] = ReadRoomTaskSequence(), ["pendingSubstepReview"] = m_WorkSubstepReviewPending
            },
            ["spatialGoal"] = PickRoomFields(goal, "goalId", "status", "ownerCurrent", "reason", "executionCurrent",
                "executionSuspended", "executionRevision", "latestUserText", "userUpdates", "originalRequest", "requestQuote",
                "targetDescription", "desiredState", "boundEntityId", "confirmedTarget", "referentKind", "referentSourceId",
                "completionPredicate", "completionEvidence", "completionEvidenceValid", "scope"),
            ["search"] = PickRoomFields(search, "taskId", "generation", "active", "ownerCurrent", "mode",
                "originalUserRequest", "latestUserText", "status", "reason", "steps", "stepLimit", "decisions", "decisionLimit",
                "noProgressSteps", "totalNoProgress", "recoveryAttempts", "recoveryLimit", "unsupportedDeviceEntityIds",
                "foundEntityId", "foundCaptureId", "boundEntityId", "deviceGoalId", "searchAttempted",
                "coveredHorizontalRegions", "horizontalRegionCount", "allVisitedCandidatePagesReviewed", "scope"),
            ["deviceResult"] = m_SceneObjectStateResult?.DeepClone(),
            ["currentRoomTask"] = PrivateProgressResultFacts(BuildWorkRoomCompletionObservation()),
            ["priorTaskResults"] = PrivateProgressResultFacts(BuildMovementPriorResults()),
            ["lastPublicReply"] = BuildAttentionInteractionTime(),
            ["interaction"] = new JObject {
                ["userSpeaking"] = m_UserSpeechActiveForAutonomy, ["lastFocus"] = m_LastFocus,
                ["secondsSinceUser"] = m_LastUserTurnTime > 0 ? (float?)(Time.realtimeSinceStartup - m_LastUserTurnTime) : null,
                ["consecutiveSpokenTurns"] = m_ConsecutiveAITurns, ["eyesOpen"] = SceneEyesOpen,
                ["recentNoise"] = m_LastSpikeTime > 0 && Time.realtimeSinceStartup - m_LastSpikeTime < 5f,
                ["approvedSkillReason"] = m_PendingAutonomousSkillReason
            },
            ["scope"] = "Private opportunity assessment only. Complete dialogue, current requirements, corrections and outstanding results remain protected. " +
                "Selected memory is historical observation, not current sight, semantic proof or action authority. Current image has its own capture metadata. " +
                "Detailed object catalogs, frozen coordinates, search traces and failed-candidate geometry remain in Unity; the next planner reads them afresh. Omission proves neither absence nor completion."
        };
        // Cross-domain pending outcomes and actual singing/material state use
        // the same production source as work review; never consume them here.
        return BuildWorkReviewFrame(reason) + "\n[Autonomy runtime facts]\n" + runtime.ToString(Formatting.None);
    }
}
