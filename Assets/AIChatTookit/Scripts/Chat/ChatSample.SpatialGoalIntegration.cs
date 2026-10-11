using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NeEEvA.Perception;
using System.Linq;
using UnityEngine;

public partial class ChatSample
{
    private int m_SpatialGoalTerminalReplyGeneration = -1;
    private JObject m_SpatialGoalTerminalProof;

    private string SpatialGoalReportLanguage(string candidateLanguage) =>
        SpatialGoalResultContextPolicy.NormalizeReportLanguage(candidateLanguage) ??
        SpatialGoalResultContextPolicy.NormalizeReportLanguage(m_ChatSettings?.m_ChatModel?.DialogueLanguage) ?? "zh";

    private string BuildSpatialGoalReviewFallback(string language = null) =>
        SpatialGoalResultContextPolicy.BuildTrustedReport(BuildSpatialGoalExecutionFacts(), SpatialGoalReportLanguage(language));

    private bool ValidateSpatialGoalTerminalReview(string output) =>
        SpatialGoalResultContextPolicy.CanReleaseGeneratedTerminalReport(output, BuildSpatialGoalExecutionFacts());

    private bool SpatialGoalReportOwnsAction(string actionId, JObject runner)
    {
        var goal = m_SpatialDeviceGoal;
        if (string.IsNullOrEmpty(actionId) || goal == null || !SpatialGoalOwnerCurrent(goal) ||
            goal.formalGeneration != m_FormalResponseGeneration || goal.workEpoch != m_WorkEpoch ||
            goal.userTurn != m_ActionConstraints.CurrentUserTurn ||
            !m_SpatialGoalActions.TryGetValue(actionId, out var owner) || owner.goal != goal) return false;
        if (owner.executionRevision == goal.executionRevision) return true;
        // Entering clarification revokes the execution lease. Its own terminal
        // receipts still explain this result, but a replacement/resumed runner
        // cannot borrow a prior action merely because it used the same device.
        return (string)runner?["goalId"] == goal.id && (int?)runner?["generation"] == m_FormalResponseGeneration &&
            (int?)runner?["workEpoch"] == m_WorkEpoch && (int?)runner?["userTurn"] == m_ActionConstraints.CurrentUserTurn &&
            (int?)runner?["goalExecutionRevision"] == owner.executionRevision &&
            (runner?["receipts"] as JArray)?.OfType<JObject>().Any(receipt => (string)receipt["actionId"] == actionId) == true;
    }

    private JObject SpatialGoalOwnedSearchReport(JObject runner)
    {
        var goal = m_SpatialDeviceGoal;
        var search = BuildSceneSearchContext();
        if (goal == null || !SpatialGoalOwnerCurrent(goal)) return null;
        // A terminal transition revokes execution, but its exact child receipt
        // remains reportable. Prior turns or replacement leases cannot lend one.
        return SpatialGoalResultContextPolicy.SearchReceiptOwnedByGoal(BuildSpatialGoalContext(), search, runner,
            m_FormalResponseGeneration, m_WorkEpoch, m_ActionConstraints.CurrentUserTurn) ? search : null;
    }

    private bool ValidateSpatialGoalTerminalProof(int generation)
    {
        if (generation != m_FormalResponseGeneration) return false;
        if (generation != m_SpatialGoalTerminalReplyGeneration) return true;
        var current = SpatialGoalCompletionSpeechStamp();
        if (JToken.DeepEquals(m_SpatialGoalTerminalProof, current)) return true;
        if (m_SpatialGoalTerminalProof != null && (bool?)current["ownerCurrent"] == true &&
            (string)current["goalId"] == (string)m_SpatialGoalTerminalProof["goalId"])
        {
            // The runner already ended; keep invalidated completion as historical
            // evidence and expose a blocked outcome instead of an orphaned active
            // goal beneath a globally closed work item. Never mutate a replacement.
            BlockSpatialGoal("goal-result-invalidated-during-report");
            m_WorkStatus = "blocked"; m_WorkReviewOpen = false;
        }
        return false;
    }

    private JObject BuildSpatialGoalExecutionFacts()
    {
        var scene = BuildSceneActionFacts();
        var objects = scene["objectContext"] as JObject;
        var goal = BuildSpatialGoalContext();
        var runner = BuildSpatialGoalRunnerContext(m_FormalResponseGeneration);
        var latestReview = (int?)m_SpatialGoalLatestReview?["generation"] == m_FormalResponseGeneration ?
            m_SpatialGoalLatestReview : null;
        var resultGoal = PickRoomFields(goal, "goalId", "status", "ownerCurrent", "reason", "originalRequest", "latestUserText",
            "userUpdates", "executionSuspended", "targetDescription", "desiredState",
            "boundEntityId", "completionPredicate", "completionEvidence", "completionEvidenceValid");
        bool unregisteredResult = latestReview?["proposal"] is JObject proposal && (string)proposal["operation"] == "set" &&
            (bool?)latestReview["registered"] == false;
        if (unregisteredResult)
        {
            // A rejected new proposal never registered an objective. Report this
            // transaction's review, rather than the cancelled old goal's result.
            resultGoal = SpatialGoalResultContextPolicy.BuildUnregisteredGoal(latestReview, m_ActionConstraints.CurrentUserText);
        }
        var lastAction = scene["lastAction"] as JObject;
        if (unregisteredResult || !SpatialGoalReportOwnsAction((string)lastAction?["actionId"], runner)) lastAction = null;
        var stateResult = objects?["stateResult"] as JObject;
        if (lastAction == null || (string)stateResult?["actionId"] != (string)lastAction["actionId"]) stateResult = null;
        var ownedSearch = unregisteredResult ? null : SpatialGoalOwnedSearchReport(runner);
        resultGoal["searchSummary"] = SpatialGoalResultContextPolicy.BuildSearchSummary(goal, lastAction, unregisteredResult, ownedSearch);
        resultGoal["operationSummary"] = unregisteredResult ? new JObject { ["operationAttempted"] = false, ["stateWriteObserved"] = false } :
            SpatialGoalResultContextPolicy.BuildOperationSummary(goal, lastAction, runner);
        if (!unregisteredResult && (bool?)goal["ownerCurrent"] == true)
        {
            var memory = goal["boundEntityMemory"] as JObject;
            bool identityKnown = !string.IsNullOrEmpty((string)goal["boundEntityId"]);
            resultGoal["targetKnowledge"] = new JObject {
                ["identityKnown"] = identityKnown,
                ["historicalPositionKnown"] = identityKnown && (bool?)memory?["positionMemoryValid"] == true,
                ["currentVisibility"] = "not-established",
                ["scope"] = "Known identity and a frozen historical point do not establish current visibility or state. A search failure does not erase the identity or prove the object absent; do not ask which object or room solely because reobservation failed."
            };
        }
        var result = new JObject {
            ["lastAction"] = lastAction == null ? null : PickRoomFields(lastAction,
                "actionId", "intent", "entityId", "requestedEntityId", "requestedState", "phase", "reason",
                "captureId", "confirmedCaptureId", "resultCaptureId", "objectConfirmed", "semanticTargetConfirmed",
                "groundArrived", "objectReobserved", "stateChanged", "active", "reached", "observedUtc"),
            ["objectContext"] = new JObject {
                ["stateResult"] = stateResult?.DeepClone(),
                ["runtimeDeviceControl"] = objects?["runtimeDeviceControl"]?.DeepClone(),
                ["verifiedObservedDeviceHistory"] = BuildVerifiedObservedDeviceReferentHistory()
            },
            ["goal"] = resultGoal,
            ["latestGoalReview"] = latestReview?.DeepClone(),
            ["roomDecision"] = new JObject { ["intent"] = m_RoomTaskIntent, ["dispatch"] = m_RoomTaskDispatch },
            ["scope"] = "Execution readback and owner-bound goal evidence. Substep arrival or a spoken statement does not satisfy a device goal."
        };
        result["resultReportRequirement"] = SpatialGoalResultContextPolicy.BuildReportRequirement(result);
        return result;
    }

    private void DispatchSpatialGoalTerminalReply(int generation)
    {
        if (generation != m_FormalResponseGeneration || !isActiveAndEnabled || m_UserSpeechActiveForAutonomy || m_SceneTimePaused)
            return;
        m_SpatialGoalTerminalReplyGeneration = generation;
        m_SpatialGoalTerminalProof = SpatialGoalCompletionSpeechStamp();
        var observation = GetFormalSceneObservation(generation);
        var facts = BuildSpatialGoalExecutionFacts();
        string goalStatus = (string)facts["goal"]?["status"];
        if (goalStatus == "satisfied" || goalStatus == "blocked" || goalStatus == "cancelled" || goalStatus == "waiting_clarification" || goalStatus == "rejected")
        {
            // This is one device outcome, not a verdict on the complete human
            // request. A following movement/question may still be outstanding.
            QueueWorkSubstepReview(generation, goalStatus);
        }
        facts["runner"] = BuildSpatialGoalRunnerContext(generation);
        facts["taskSequence"] = ReadRoomTaskSequence();
        facts["visualObservation"] = observation == null ? null : JObject.Parse(observation.MetadataJson);
        facts["scope"] = "One result for the original device request. State success requires actual same-device proof; blocked/cancelled/unknown remain explicit. Clarification is reserved for a genuinely unresolved request detail, not a known object's unsuccessful reobservation. Known identity and historical position must not be described as forgotten. A rejected proposal is not pending clarification. registered=false alone establishes no missing human permission. Only cancellationApplied proves an applied cancellation. searchAttempted=false forbids claiming a failed search; page review counts as a search attempt without proving movement or absence. No new action is submitted by this report.";
        var reportFacts = SpatialGoalResultContextPolicy.ProjectResultFacts(facts);
        Debug.Log("[SpatialGoal/ResultProjection] " + new JObject {
            ["generation"] = generation, ["goalId"] = facts["goal"]?["goalId"]?.DeepClone(),
            ["expectedReport"] = SpatialGoalResultContextPolicy.RequiredReport(facts),
            ["sourceTextEstimate"] = SpatialGoalResultContextPolicy.EstimateTextTokens(facts.ToString(Formatting.None)),
            ["resultTextEstimate"] = reportFacts == null ? (JToken)JValue.CreateNull() :
                SpatialGoalResultContextPolicy.EstimateTextTokens(reportFacts.ToString(Formatting.None)),
            ["valid"] = reportFacts != null
        }.ToString(Formatting.None), this);
        // A failed projection takes the existing guarded fallback path; it must
        // not change the engine's actual proof or manufacture a success result.
        DispatchPlannedWorkRoomReply((reportFacts ?? facts).ToString(Formatting.None),
            observation?.HasImage == true ? observation.ImageDataUrl : null, generation);
    }
}
