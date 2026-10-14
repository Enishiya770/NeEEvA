using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private const int SceneSearchStepLimit = 12;
    private const int SceneSearchDecisionLimit = 36;
    private const int SceneSearchNoProgressLimit = 3;
    private const int SceneSearchRecoveryLimit = 1;
    private const float SceneSearchDuration = 180f;
    private const float SceneSearchPlanDeadline = 30f;
    private const int SceneSearchHistoryReviewLimit = 2;
    private SceneSearchLease m_SceneSearch;
    private JObject m_SceneSearchLastReport;

    // A planner quotes one offered observation, not whichever observation of the
    // same entity happens to occupy the memory cache when its reply arrives.
    private sealed class SceneSearchPointOffer
    {
        public string referenceId, entityId, sourceCaptureId;
        public Vector3 point;
        public SceneObservedObject observed;
        public SceneVisibleMemory memory;
        public KnownSceneObject known;
    }

    private sealed class SceneSearchLease
    {
        public string id, mode, originalRequest, latestUserText, origin, autonomousIntention;
        public string phase = "starting", status = "active", reason, pendingKind, actionId;
        public string deviceGoalId, boundEntityId, foundEntityId, foundCaptureId;
        public int generation, workEpoch, userTurn, visionEpoch, sceneEpoch, controllerOperation;
        public int deviceRevision, steps, decisions, noProgress, totalNoProgress, recoveryAttempts, requestRevision, preferredPage;
        public int viewActionControllerOperation = -1;
        public float startedAt, phaseStartedAt;
        public Vector3 startForward, rootPosition, viewActionRootPosition;
        public bool ending, boundMemoryRevisited;
        public Action completed;
        public ArdyRoomDialogueBridge bridge;
        public ArdyRoomLocomotionController controller;
        public UniVRM10.Vrm10Instance avatar;
        public Transform environment, user;
        public NevaSceneVisionCapture source;
        public SceneVisionObservation bindingObservation, observation;
        public LLM model;
        public JObject lastStep;
        public JObject referenceContext;
        public readonly HashSet<int> coveredRegions = new HashSet<int>();
        public readonly HashSet<string> reviewedPages = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> reviewedHistory = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> revisitedMemoryEntities = new HashSet<string>(StringComparer.Ordinal);
        public readonly Dictionary<string, SceneObservedObject> reviewedCandidates = new Dictionary<string, SceneObservedObject>(StringComparer.Ordinal);
        public readonly Dictionary<string, SceneObservedObject> unsupportedDeviceCandidates = new Dictionary<string, SceneObservedObject>(StringComparer.Ordinal);
        public readonly Dictionary<string, SceneObservedObject> reviewedPageEntities = new Dictionary<string, SceneObservedObject>(StringComparer.Ordinal);
        public readonly JArray pageCoverage = new JArray();
        public readonly JArray receipts = new JArray();
    }

    private bool SceneSearchBindingCurrent(SceneSearchLease lease) => lease != null &&
        ReferenceEquals(m_SceneSearch, lease) && !lease.ending && isActiveAndEnabled &&
        m_EnableSceneActions && RoomMotionOutputEnabled && SceneEyesOpen && !m_UserSpeechActiveForAutonomy &&
        !m_SceneTimePaused && Time.timeScale > 0 && lease.generation == m_FormalResponseGeneration &&
        lease.workEpoch == m_WorkEpoch && lease.userTurn == m_ActionConstraints.CurrentUserTurn &&
        lease.latestUserText == m_ActionConstraints.CurrentUserText && lease.visionEpoch == m_SceneVisionEpoch &&
        (lease.origin != "autonomous" || lease.autonomousIntention == m_SceneAutonomyIntention) &&
        lease.model != null && lease.model == m_ChatSettings?.m_ChatModel &&
        lease.bridge != null && lease.bridge == GetComponent<ArdyRoomDialogueBridge>() && lease.bridge.IsConnected &&
        lease.avatar != null && lease.avatar.gameObject.activeInHierarchy && lease.bridge.avatar == lease.avatar &&
        lease.environment != null && lease.bridge.environmentRoot == lease.environment &&
        lease.bridge.userHead == lease.user && lease.controller != null && lease.bridge.Controller == lease.controller &&
        lease.source != null && lease.source == m_SceneVisionCapture && lease.source.isActiveAndEnabled &&
        lease.source.Target == lease.avatar && lease.source.IsObservationBindingCurrent(lease.bindingObservation) &&
        lease.sceneEpoch == m_SceneActionEpoch && SceneSearchRootPositionCurrent(lease) &&
        (lease.controller.OperationRevision == lease.controllerOperation ||
            SceneSearchOwnsBodyTurnDisplacement(lease)) &&
        (string.IsNullOrEmpty(lease.deviceGoalId) ||
            SpatialGoalExecutionCurrent(m_SpatialDeviceGoal) && m_SpatialDeviceGoal.id == lease.deviceGoalId &&
            m_SpatialDeviceGoal.executionRevision == lease.deviceRevision &&
            m_SpatialDeviceGoal.originalRequest == lease.originalRequest);

    private static bool SceneSearchBodyTurnOwnsDisplacement(string phase, string actionId, string factActionId,
        string factIntent, string bridgeActionId, int expectedOperation, int actualOperation, bool nativeTurnOwned) =>
        phase == "moving" && !string.IsNullOrEmpty(actionId) && factActionId == actionId &&
        factIntent == "turn-in-place" && bridgeActionId == actionId && expectedOperation == actualOperation && nativeTurnOwned;

    private static bool SceneSearchRootDisplacementAllowed(float stationaryDistance, float actionDistance, bool ownsBodyTurn) =>
        !float.IsNaN(stationaryDistance) && !float.IsInfinity(stationaryDistance) && stationaryDistance >= 0f &&
        !float.IsNaN(actionDistance) && !float.IsInfinity(actionDistance) && actionDistance >= 0f &&
        (stationaryDistance < .04f || ownsBodyTurn && actionDistance <= .16f);

    private bool SceneSearchOwnsBodyTurnDisplacement(SceneSearchLease lease)
    {
        var terminal = lease.bridge.LastSceneOrientResult;
        bool nativeTurnOwned = lease.bridge.IsOrientingScene || terminal != null && terminal.terminal && terminal.oriented &&
            terminal.actionId == lease.actionId && terminal.captureId == lease.observation.CaptureId &&
            terminal.controllerOperationRevision == lease.controller.OperationRevision;
        // Head release can defer native submission beyond BeginSceneViewChange.
        // Adopt only the bridge's exact submitted operation, once per action;
        // a subsequent controller operation can never inherit this allowance.
        if (lease.viewActionControllerOperation < 0 &&
            lease.bridge.TryGetSubmittedMotionOperation(lease.actionId, out var submittedController, out int submittedOperation) &&
            submittedController == lease.controller && SceneSearchBodyTurnOwnsDisplacement(lease.phase, lease.actionId,
                m_SceneActionFact?.actionId, m_SceneActionFact?.intent, lease.bridge.ActionId,
                submittedOperation, lease.controller.OperationRevision, nativeTurnOwned))
            lease.viewActionControllerOperation = submittedOperation;
        return SceneSearchBodyTurnOwnsDisplacement(lease.phase, lease.actionId, m_SceneActionFact?.actionId,
            m_SceneActionFact?.intent, lease.bridge.ActionId, lease.viewActionControllerOperation, lease.controller.OperationRevision, nativeTurnOwned);
    }

    private bool SceneSearchRootPositionCurrent(SceneSearchLease lease) => SceneSearchRootDisplacementAllowed(
        Vector3.Distance(lease.avatar.transform.position, lease.rootPosition),
        Vector3.Distance(lease.avatar.transform.position, lease.viewActionRootPosition), SceneSearchOwnsBodyTurnDisplacement(lease));

    private void BeginSceneSearchTask(RoomTaskProtocol.Decision decision, SceneVisionObservation observation,
        int generation, Action completed)
    {
        CancelSceneSearchTask("search-replaced");
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var model = m_ChatSettings?.m_ChatModel;
        string mode = decision?.searchTask?.mode;
        string failure = mode != "survey" && mode != "find" ? "invalid-search-mode" :
            model?.SupportsSceneSearchMessages != true ? "search-planner-unavailable" :
            generation != m_FormalResponseGeneration || !isActiveAndEnabled || !SceneEyesOpen ||
            m_SceneTimePaused || m_UserSpeechActiveForAutonomy || !RoomMotionOutputEnabled || !m_EnableSceneActions ? "search-owner-unavailable" :
            SpatialGoalRunnerOwnsGeneration(generation) && !SpatialGoalRunnerDelegatesSearch(generation) ? "device-runner-already-owns-actions" :
            m_SceneActionActive || bridge == null || !bridge.IsConnected || bridge.avatar == null ||
            bridge.environmentRoot == null || bridge.Controller == null || bridge.ReservesBody || bridge.IsFollowing ||
            bridge.Controller.IsBusy || bridge.Controller.IsMoving ? "search-body-owner-unavailable" :
            m_SceneVisionCapture == null || !IsFreshSceneObservation(observation) ? "search-current-image-unavailable" : null;
        if (failure != null)
        {
            m_SceneSearchLastReport = new JObject { ["active"] = false, ["status"] = "incomplete",
                ["generation"] = generation, ["reason"] = failure, ["searchAttempted"] = false,
                ["mode"] = mode, ["deviceWriteAuthorized"] = false, ["steps"] = 0 };
            m_RoomTaskDispatch = failure;
            completed?.Invoke();
            return;
        }
        bool device = SpatialGoalExecutionCurrent(m_SpatialDeviceGoal) && SpatialGoalUnfinished(m_SpatialDeviceGoal);
        string origin = decision.origin == "autonomous" && !device ? "autonomous" : "user";
        var lease = new SceneSearchLease {
            id = "scene-search-" + Guid.NewGuid().ToString("N"), mode = mode,
            originalRequest = device ? m_SpatialDeviceGoal.originalRequest : origin == "autonomous" ?
                m_SceneAutonomyIntention : m_ActionConstraints.CurrentUserText,
            latestUserText = m_ActionConstraints.CurrentUserText, origin = origin,
            autonomousIntention = origin == "autonomous" ? m_SceneAutonomyIntention : null,
            generation = generation, workEpoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn,
            visionEpoch = m_SceneVisionEpoch, sceneEpoch = m_SceneActionEpoch, model = model,
            bridge = bridge, controller = bridge.Controller, controllerOperation = bridge.Controller.OperationRevision,
            avatar = bridge.avatar, environment = bridge.environmentRoot, user = bridge.userHead,
            source = m_SceneVisionCapture, bindingObservation = observation, observation = observation,
            rootPosition = bridge.avatar.transform.position, viewActionRootPosition = bridge.avatar.transform.position,
            startForward = Vector3.ProjectOnPlane(observation.CameraRotation * Vector3.forward, Vector3.up).normalized,
            deviceGoalId = device ? m_SpatialDeviceGoal.id : null,
            referenceContext = device ? m_SpatialDeviceGoal.referenceContext?.DeepClone() as JObject :
                RoomTaskProtocol.BuildDeviceReferenceContext(JArray.Parse(RecentRoomDialogue())),
            deviceRevision = device ? m_SpatialDeviceGoal.executionRevision : -1,
            boundEntityId = device ? (string)BuildSpatialGoalContext()["boundEntityId"] : null,
            startedAt = Time.realtimeSinceStartup, phaseStartedAt = Time.realtimeSinceStartup, completed = completed
        };
        m_SceneSearch = lease; m_SceneSearchLastReport = null;
        AcceptSceneSearchObservation(lease, observation);
        Debug.Log("[SceneSearch/Started] " + BuildSceneSearchContext().ToString(Formatting.None), this);
        if (origin == "user") AcknowledgeAcceptedTask(generation, lease.id);
        StartCoroutine(WatchSceneSearchTask(lease));
        StartCoroutine(RunSceneSearchSafely(lease, RunSceneSearchTask(lease)));
    }

    // Flatten nested iterators so an exception in capture/matching also releases this lease.
    private IEnumerator RunSceneSearchSafely(SceneSearchLease lease, IEnumerator root)
    {
        var stack = new Stack<IEnumerator>(); stack.Push(root);
        try
        {
            while (stack.Count > 0 && SceneSearchBindingCurrent(lease))
            {
                bool next = false; object value = null; Exception failure = null;
                try { next = stack.Peek().MoveNext(); if (next) value = stack.Peek().Current; }
                catch (Exception error) { failure = error; }
                if (failure != null)
                { FinishSceneSearchTask(lease, "incomplete", "search-exception:" + failure.GetType().Name); yield break; }
                if (!next) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                if (value is IEnumerator nested) { stack.Push(nested); continue; }
                yield return value;
            }
        }
        finally { while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose(); }
    }

    private IEnumerator WatchSceneSearchTask(SceneSearchLease lease)
    {
        while (ReferenceEquals(m_SceneSearch, lease) && !lease.ending)
        {
            if (!SceneSearchBindingCurrent(lease))
            {
                CancelSceneSearchTask("search-owner-invalidated");
                if (lease.generation == m_FormalResponseGeneration) CancelRoomSpeechReview("search-owner-invalidated");
                yield break;
            }
            if (Time.realtimeSinceStartup - lease.startedAt >= SceneSearchDuration)
            { FinishSceneSearchTask(lease, "incomplete", "search-time-budget"); yield break; }
            yield return null;
        }
    }

    private bool AcceptSceneSearchObservation(SceneSearchLease lease, SceneVisionObservation observation)
    {
        if (!SceneSearchBindingCurrent(lease) || !IsFreshSceneObservation(observation)) return false;
        if (lease.observation != null && !SceneSearchSameInspectionView(lease.observation, observation)) lease.preferredPage = 0;
        lease.observation = observation;
        // A camera roll or idle head tremor does not create new target evidence.
        // Each exclusion keeps its own inspected pose, so small increments cannot
        // preserve a stale exclusion forever or erase every entity at once.
        foreach (var item in lease.reviewedCandidates.ToArray())
            if (!SceneSearchReviewStillUseful(lease, item.Value)) lease.reviewedCandidates.Remove(item.Key);
        foreach (var item in lease.reviewedPageEntities.ToArray())
            if (!SceneSearchReviewStillUseful(lease, item.Value)) lease.reviewedPageEntities.Remove(item.Key);
        BindFormalSceneObservation(observation, lease.generation);
        ObserveKnownSceneObjects(observation, "scene-search-step");
        ObserveSceneVisibleCatalog(observation, "scene-search-step");
        int before = lease.coveredRegions.Count;
        float halfWidth = Mathf.Atan(Mathf.Tan(observation.VerticalFov * Mathf.Deg2Rad * .5f) *
            observation.Width / Mathf.Max(1f, observation.Height)) * Mathf.Rad2Deg;
        // Only central horizontal directions of actual settled RGB count. This does
        // not assert that occluded objects, ceilings or the entire room were inspected.
        float usableHalfWidth = Mathf.Clamp(halfWidth - 12f, 5f, 55f);
        Vector3 forward = Vector3.ProjectOnPlane(observation.CameraRotation * Vector3.forward, Vector3.up).normalized;
        for (int i = 0; i < 12; i++)
            if (Vector3.Angle(forward, SceneSearchRegionDirection(lease, i)) <= usableHalfWidth) lease.coveredRegions.Add(i);
        return lease.coveredRegions.Count > before;
    }

    private IEnumerator CaptureSceneSearchObservation(SceneSearchLease lease)
    {
        lease.phase = "capturing"; lease.phaseStartedAt = Time.realtimeSinceStartup;
        bool received = false; SceneVisionObservation next = null;
        float requestedAt = Time.realtimeSinceStartup;
        string previous = lease.observation?.CaptureId;
        int revision = ++lease.requestRevision;
        RequestSceneObservation("scene-search-next-step", value => {
            if (!SceneSearchBindingCurrent(lease) || revision != lease.requestRevision || lease.phase != "capturing") return;
            received = true; next = value;
        });
        float until = requestedAt + 4.5f;
        while (SceneSearchBindingCurrent(lease) && !received && Time.realtimeSinceStartup < until) yield return null;
        if (!SceneSearchBindingCurrent(lease)) yield break;
        if (!received || next?.HasImage != true || next.CaptureId == previous || next.CapturedAtRealtime < requestedAt ||
            !IsFreshSceneObservation(next))
        { FinishSceneSearchTask(lease, "incomplete", "search-fresh-image-unavailable"); yield break; }
        AcceptSceneSearchObservation(lease, next);
    }

    private JObject SceneSearchPlannerInput(SceneSearchLease lease, JObject catalog)
    {
        catalog = (JObject)catalog.DeepClone();
        var current = catalog["current"] as JObject;
        if (current?["availablePages"] is JArray pages)
            foreach (var page in pages.OfType<JObject>())
            {
                int index = (int)page["pageIndex"];
                bool reviewed = SceneSearchPageReviewed(lease, index);
                page["reviewed"] = reviewed;
                page["selectionAllowed"] = !reviewed && index != (int?)current["pageIndex"];
            }
        if (current?["candidates"] is JArray candidates)
            foreach (var candidate in candidates.OfType<JObject>())
            {
                string entity = (string)candidate["entityId"];
                bool rejected = SceneSearchCandidateReviewed(lease, entity);
                bool unsupported = SceneSearchUnsupportedDeviceCandidate(lease, entity);
                bool matchesBinding = string.IsNullOrEmpty(lease.boundEntityId) || entity == lease.boundEntityId;
                candidate["reviewed"] = lease.reviewedPageEntities.TryGetValue(entity, out var old) && SceneSearchFrozenEvidenceValid(lease, old);
                candidate["independentlyReviewedAtThisView"] = rejected;
                candidate["selectionAllowed"] = lease.mode == "find" && matchesBinding && !rejected && !unsupported;
                candidate["selectionConstraint"] = unsupported ? "same-search-device-control-unavailable" : !matchesBinding ? "different-from-bound-target" : rejected ?
                    "already-independently-reviewed-at-this-view" : lease.mode != "find" ? "survey-observation-only" : "requires-independent-complete-target-match";
            }
        if (catalog["memory"] is JArray memory)
            foreach (var point in memory.OfType<JObject>())
            {
                string entity = (string)point["entityId"];
                bool revisited = lease.revisitedMemoryEntities.Contains(entity ?? "") || lease.boundMemoryRevisited && entity == lease.boundEntityId;
                point["revisitedByTask"] = revisited;
                point["selectionAllowed"] = lease.mode == "find" && (bool?)point["positionMemoryValid"] == true &&
                    !revisited && !SceneSearchUnsupportedDeviceCandidate(lease, entity) &&
                    (string.IsNullOrEmpty(lease.boundEntityId) || entity == lease.boundEntityId);
            }
        if (catalog["historicalViews"] is JArray historicalViews)
            foreach (var historical in historicalViews.OfType<JObject>())
            {
                bool reviewed = lease.reviewedHistory.Contains((string)historical["sourceCaptureId"]);
                historical["reviewedByTask"] = reviewed;
                historical["selectionAllowed"] = lease.mode == "find" && !reviewed;
            }
        var regions = new JArray();
        for (int i = 0; i < 12; i++)
        {
            Vector3 direction = SceneSearchRegionDirection(lease, i);
            float cameraYaw = SceneSearchShortestYaw(lease.observation.CameraRotation * Vector3.forward, direction);
            float bodyYaw = SceneSearchShortestYaw(lease.avatar.transform.forward, direction);
            regions.Add(new JObject { ["referenceId"] = "region-" + i,
                ["yawFromStartDegrees"] = i * 30, ["yawRelativeToCurrentCameraDegrees"] = cameraYaw,
                ["yawRelativeToCurrentBodyDegrees"] = bodyYaw, ["observed"] = lease.coveredRegions.Contains(i),
                ["selectionAllowed"] = Mathf.Abs(cameraYaw) >= 3f,
                ["directionConvention"] = "negative yaw is left; positive yaw is right; shortest horizontal angle" });
        }
        return new JObject {
            ["task"] = SceneSearchReport(lease), ["imageRole"] = "current", ["currentCaptureId"] = lease.observation.CaptureId,
            ["attentionState"] = ReadAttentionState("search"),
            ["visualObservation"] = JObject.Parse(lease.observation.MetadataJson), ["visibleCatalog"] = catalog,
            ["selfPose"] = BuildSceneSelfPoseContext(lease.observation),
            ["explorationRegions"] = regions, ["lastStep"] = lease.lastStep?.DeepClone(),
            ["reviewedSameViewEntityIds"] = new JArray(lease.reviewedPageEntities.Where(pair => SceneSearchFrozenEvidenceValid(lease, pair.Value)).Select(pair => pair.Key)),
            ["scope"] = "Choose only a listed reference. The complete originalUserRequest remains the target; a scene description cannot replace it. " +
                "Current candidates are actual opaque pixels, not semantically confirmed targets. Historical pictures/positions are search hints only. " +
                "For find, preserve all target and landmark relations. Prefer observed historical points or actual earlier pictures before unknown regions; never infer furniture directions from typical room layouts. " +
                "Authored Unity names are provenance-labelled hints, not visual confirmation. Select a plausible current-candidate for independent matching; done means stop without confirming the target, never prove absence. " +
                "For survey, inspect offered candidate pages and select useful unobserved regions. Unity computes natural head motion first, then bounded body turns. " +
                "No device operation, approach, navigation or inferred coordinate is authorized by this task. Observations describe only listed current candidates in this image. " +
                "reviewedSameViewEntityIds are page-coverage hints from actual earlier images at this pose whose geometry remains unchanged, not current recognition or action references."
        };
    }

    private IEnumerator RequestSceneSearchPlan(SceneSearchLease lease, JObject input, string image,
        Action<SceneSearchProtocol.Decision, string> completed)
    {
        lease.phase = "planning"; lease.phaseStartedAt = Time.realtimeSinceStartup;
        lease.pendingKind = "search"; int revision = ++lease.requestRevision;
        input = SceneSearchProtocol.CreateScopedInput(input, "g" + lease.generation + "q" + revision, out var references);
        Debug.Log("[SceneSearch/Input] " + new JObject { ["taskId"] = lease.id, ["generation"] = lease.generation,
            ["requestId"] = input["requestId"], ["imageRole"] = input["imageRole"], ["captureId"] = input["currentCaptureId"] ?? input["historicalCaptureId"],
            ["mode"] = lease.mode, ["boundEntityId"] = lease.boundEntityId, ["originalUserRequest"] = lease.originalRequest,
            ["page"] = input["visibleCatalog"]?["current"]?["pageIndex"], ["pages"] = input["visibleCatalog"]?["current"]?["availablePages"],
            ["candidates"] = input["visibleCatalog"]?["current"]?["candidates"], ["memory"] = input["visibleCatalog"]?["memory"],
            ["historicalViews"] = input["visibleCatalog"]?["historicalViews"], ["historicalCandidates"] = input["historicalCandidates"],
            ["regions"] = input["explorationRegions"], ["requestChoices"] = input["requestChoices"],
            ["targetRetrieval"] = input["targetRetrieval"], ["recovery"] = input["recovery"],
            ["scope"] = "Bounded structured search evidence; no image bytes. Labels/descriptions are not independent target confirmation." }.ToString(Formatting.None), this);
        bool received = false, accepted = false; string raw = "", status = "search-planner-timeout";
        try
        {
            lease.model.PostSceneSearchDecisionMessage(input.ToString(Formatting.None), image, (ok, value, why) => {
                if (!SceneSearchBindingCurrent(lease) || lease.requestRevision != revision || lease.pendingKind != "search") return;
                received = true; accepted = ok; raw = value; status = why;
            });
        }
        catch (Exception error) { received = true; status = "search-planner-request:" + error.GetType().Name; }
        float until = Time.realtimeSinceStartup + SceneSearchPlanDeadline;
        while (SceneSearchBindingCurrent(lease) && !received && Time.realtimeSinceStartup < until) yield return null;
        if (!SceneSearchBindingCurrent(lease) || revision != lease.requestRevision) yield break;
        lease.pendingKind = null; lease.model.CancelSceneSearchDecisionMessage();
        SceneSearchProtocol.Decision decision = null;
        string failure = status;
        bool parsed = received && accepted && SceneSearchProtocol.TryResolveDecision(raw, references, out decision, out failure);
        lease.decisions++;
        Debug.Log("[SceneSearch/Decision] " + new JObject { ["taskId"] = lease.id, ["generation"] = lease.generation,
            ["imageRole"] = input["imageRole"], ["captureId"] = input["currentCaptureId"] ?? input["historicalCaptureId"],
            ["requestId"] = input["requestId"], ["decision"] = parsed ? JObject.Parse(raw) : null,
            ["rejectedOutput"] = !parsed && raw != null ? raw.Substring(0, Math.Min(raw.Length, 2400)) : null,
            ["resolvedReferenceId"] = parsed ? decision.referenceId : null, ["accepted"] = parsed, ["reason"] = failure,
            ["wireStatus"] = status }.ToString(Formatting.None), this);
        if (parsed && (string)input["imageRole"] == "current" && IsFreshSceneObservation(lease.observation))
        {
            foreach (var note in decision.observations)
                RememberSceneVisibleDescription(lease.observation, note.referenceId, note.description);
        }
        completed(parsed ? decision : null, parsed ? null : failure);
    }

    private IEnumerator RunSceneSearchTask(SceneSearchLease lease)
    {
        while (SceneSearchBindingCurrent(lease))
        {
            if (lease.steps >= SceneSearchStepLimit || lease.decisions >= SceneSearchDecisionLimit)
            { FinishSceneSearchTask(lease, "incomplete", "search-step-budget"); yield break; }
            if (!IsFreshSceneObservation(lease.observation)) yield return CaptureSceneSearchObservation(lease);
            if (!SceneSearchBindingCurrent(lease)) yield break;
            if (lease.noProgress >= SceneSearchNoProgressLimit)
            {
                bool progressed = false;
                yield return RecoverSceneSearchProgress(lease, value => progressed = value);
                if (!SceneSearchBindingCurrent(lease)) yield break;
                if (!progressed)
                { FinishSceneSearchTask(lease, "incomplete", "search-no-progress-after-bounded-recovery"); yield break; }
                continue;
            }
            // A reviewed historical entity is an identity constraint, not a new
            // visual guessing problem. Revisit its frozen point, then use NEW RGB
            // and the same independent semantic matcher as any other candidate.
            if (lease.mode == "find" && !string.IsNullOrEmpty(lease.boundEntityId))
            {
                var full = GetCurrentSceneVisibleCatalog(lease.observation)?.catalog;
                var bound = full?.Candidates.FirstOrDefault(candidate => candidate.entityId == lease.boundEntityId);
                if (bound != null && !SceneSearchCandidateReviewed(lease, bound.entityId) &&
                    TrySelectSceneVisibleCatalogPage(lease.observation, full.CatalogId + ":p" + bound.pageIndex, out var boundPage, out _))
                {
                    lease.decisions++;
                    lease.receipts.Add(new JObject { ["choice"] = "bound-current-candidate", ["entityId"] = bound.entityId,
                        ["sourceCaptureId"] = lease.observation.CaptureId, ["reason"] = "Existing bound identity is in current opaque pixels; independent complete-target match follows.",
                        ["deviceWriteAuthorized"] = false });
                    yield return ConfirmSceneSearchCandidate(lease, bound.referenceId, boundPage, BuildSceneVisibleCatalogContext(lease.observation));
                    continue;
                }
                if (bound == null && !lease.boundMemoryRevisited &&
                    TryGetSceneSearchBoundPoint(lease.observation, lease.boundEntityId, out var frozenPoint, out string pointReference))
                {
                    lease.boundMemoryRevisited = true; lease.decisions++;
                    lease.receipts.Add(new JObject { ["choice"] = "bound-historical-object", ["referenceId"] = pointReference,
                        ["entityId"] = lease.boundEntityId, ["reason"] = "Revisit the already observed frozen point; memory is direction only, not visibility or permission.",
                        ["deviceWriteAuthorized"] = false });
                    yield return MoveSceneSearchView(lease, frozenPoint, "historical-object", pointReference);
                    continue;
                }
            }
            JObject catalog = BuildSceneVisibleCatalogContext(lease.observation);
            if (!(catalog["current"] is JObject))
            { FinishSceneSearchTask(lease, "incomplete", "search-current-catalog-unavailable"); yield break; }
            if (lease.mode == "survey" && SceneSearchCurrentPagesReviewed(lease, catalog))
            {
                if (lease.coveredRegions.Count == 12)
                { FinishSceneSearchTask(lease, "completed", "survey-observed-directions-complete"); yield break; }
                if (TryGetSceneSearchSurveyDirection(lease, out var nextDirection, out string regionReference))
                {
                    lease.decisions++;
                    yield return MoveSceneSearchView(lease, nextDirection, "survey-unobserved-direction", regionReference);
                    continue;
                }
            }
            var offeredPages = catalog["current"]?["availablePages"] as JArray;
            var desiredPage = offeredPages?.OfType<JObject>().FirstOrDefault(value => (int?)value["pageIndex"] == lease.preferredPage);
            if (desiredPage != null && (int?)catalog["current"]?["pageIndex"] != lease.preferredPage &&
                TrySelectSceneVisibleCatalogPage(lease.observation, (string)desiredPage["referenceId"], out _, out _))
                catalog = BuildSceneVisibleCatalogContext(lease.observation);
            int page = (int?)catalog["current"]?["pageIndex"] ?? 0;
            SceneObjectCandidateView view;
            string pageFailure;
            bool hasPage = TryGetSceneVisibleCatalogPage(lease.observation, page, out view, out pageFailure);
            var presented = new List<SceneObservedObject>();
            if (hasPage) foreach (var candidate in view.Candidates)
                if (view.TryGetCandidate(candidate.candidateId, out var observedCandidate, out _)) presented.Add(observedCandidate);
            string image = hasPage ? view.ImageDataUrl : lease.observation.ImageDataUrl;
            if (hasPage) SetSceneSearchCandidatePreview(view, "search-current-page");
            string pageKey = lease.observation.CaptureId + ":" + page;
            SceneSearchProtocol.Decision choice = null; string failure = null;
            var plannerInput = SceneSearchPlannerInput(lease, catalog);
            var memoryOffers = FreezeSceneSearchPointOffers(lease, plannerInput);
            yield return RequestSceneSearchPlan(lease, plannerInput, image,
                (value, reason) => { choice = value; failure = reason; });
            if (!SceneSearchBindingCurrent(lease)) yield break;
            if (choice == null) { FinishSceneSearchTask(lease, "incomplete", failure ?? "search-invalid-plan"); yield break; }
            bool newPage = false;
            foreach (var entity in presented)
                if (SceneSearchFrozenEvidenceValid(lease, entity))
                {
                    if (!lease.reviewedPageEntities.ContainsKey(entity.EntityId)) newPage = true;
                    lease.reviewedPageEntities[entity.EntityId] = entity;
                }
            lease.reviewedPages.Add(pageKey);
            // Preserve a selected page through a freshness-only recapture. Its old
            // candidate IDs never authorize matching on the replacement image.
            if (choice.choice == "next-page")
            {
                var offered = (catalog["current"]?["availablePages"] as JArray)?.OfType<JObject>()
                    .FirstOrDefault(value => (string)value["referenceId"] == choice.referenceId);
                if (offered != null) lease.preferredPage = (int)offered["pageIndex"];
            }
            if (!IsFreshSceneObservation(lease.observation))
            {
                lease.lastStep = new JObject { ["reason"] = "search-plan-current-image-expired", ["countsAsNoProgress"] = false,
                    ["scope"] = "Infrastructure refresh only; old pixels do not authorize an action. Total decision/time budgets still apply." };
                lease.receipts.Add(lease.lastStep);
                yield return CaptureSceneSearchObservation(lease);
                if (!SceneSearchBindingCurrent(lease)) yield break;
                // A historical point keeps its quoted source across a freshness
                // refresh. Pixel/page choices still need a new planner request.
                if (choice.choice != "historical-object") continue;
            }
            if (choice.choice == "current-candidate")
            {
                if (lease.mode != "find") { SceneSearchNoProgress(lease, "survey-candidate-is-not-a-find-target"); continue; }
                if (!hasPage) { SceneSearchNoProgress(lease, pageFailure ?? "search-candidate-page-unavailable"); continue; }
                yield return ConfirmSceneSearchCandidate(lease, choice.referenceId, view, catalog);
                continue;
            }
            // A new utterance must not turn a remembered room into an unexplored
            // room. Before blind motion (or giving up), review a bounded set of
            // actual pre-task frames. Current pixels and their match stay first.
            if (lease.mode == "find" && (choice.choice == "explore-region" || choice.choice == "done"))
            {
                bool revisited = false;
                var history = GetSceneVisibleHistoricalViews(lease.observation)
                    .Where(old => old.CapturedAtRealtime < lease.startedAt && !lease.reviewedHistory.Contains(old.CaptureId))
                    .Take(Math.Max(0, SceneSearchHistoryReviewLimit - lease.reviewedHistory.Count)).ToArray();
                foreach (var old in history)
                {
                    yield return ReviewSceneSearchHistoricalHint(lease, old, value => revisited = value);
                    if (!SceneSearchBindingCurrent(lease)) yield break;
                    if (revisited) break;
                }
                if (revisited) continue;
                if (!IsFreshSceneObservation(lease.observation))
                {
                    yield return CaptureSceneSearchObservation(lease);
                    if (!SceneSearchBindingCurrent(lease)) yield break;
                }
            }
            if (choice.choice == "done")
            {
                if (lease.mode == "find")
                {
                    bool progressed = false;
                    yield return RecoverSceneSearchProgress(lease, value => progressed = value, "premature-done");
                    if (!SceneSearchBindingCurrent(lease)) yield break;
                    if (progressed) continue;
                }
                bool covered = lease.mode == "survey" && lease.coveredRegions.Count == 12 && SceneSearchCurrentPagesReviewed(lease, catalog);
                if (lease.mode == "survey" && !covered && SceneSearchCurrentPagesReviewed(lease, catalog) &&
                    TryGetSceneSearchSurveyDirection(lease, out var nextDirection, out string regionReference))
                {
                    yield return MoveSceneSearchView(lease, nextDirection, "survey-unobserved-direction", regionReference);
                    continue;
                }
                FinishSceneSearchTask(lease, covered ? "completed" : "incomplete",
                    covered ? "survey-observed-directions-complete" : "search-stopped-without-target-or-complete-survey");
                yield break;
            }
            if (choice.choice == "next-page")
            {
                SceneObjectCandidateView next;
                if (!TrySelectSceneVisibleCatalogPage(lease.observation, choice.referenceId, out next, out failure))
                    SceneSearchNoProgress(lease, failure ?? "search-page-reference-invalid");
                else if (!newPage) SceneSearchNoProgress(lease, "search-repeated-candidate-page");
                else { lease.noProgress = 0; lease.lastStep = new JObject { ["choice"] = "next-page", ["referenceId"] = choice.referenceId }; }
                continue;
            }
            Vector3 worldPoint;
            if (choice.choice == "historical-view")
            {
                SceneVisionObservation historical;
                if (!TryGetSceneSearchHistoricalView(choice.referenceId, lease.observation, out historical, out failure))
                { SceneSearchNoProgress(lease, failure); continue; }
                if (lease.reviewedHistory.Contains(historical.CaptureId))
                { SceneSearchNoProgress(lease, "historical-hint-already-reviewed"); continue; }
                bool revisited = false;
                yield return ReviewSceneSearchHistoricalHint(lease, historical, value => revisited = value);
                continue;
            }
            if (choice.choice == "explore-region")
            {
                int region;
                if (!TrySceneSearchRegion(choice.referenceId, out region))
                { SceneSearchNoProgress(lease, "search-region-reference-invalid"); continue; }
                worldPoint = lease.observation.CameraPosition + SceneSearchRegionDirection(lease, region) * 3f;
            }
            else if (choice.choice != "historical-object" ||
                !TryResolveSceneSearchPointOffer(lease, memoryOffers, choice.referenceId, out worldPoint, out failure))
            { SceneSearchNoProgress(lease, failure ?? "search-selection-reference-invalid"); continue; }
            if (choice.choice == "historical-object")
            {
                RememberSceneSearchMemoryVisit(lease, catalog, choice.referenceId, choice.selectionRole);
                if (TryGetSceneSearchReobservedPoint(lease, memoryOffers[choice.referenceId], out var currentView, out var currentReference, out var currentCatalog))
                {
                    yield return ConfirmSceneSearchCandidate(lease, currentReference, currentView, currentCatalog);
                    continue;
                }
            }
            RecordSceneSearchPageCoverage(lease, catalog);
            yield return MoveSceneSearchView(lease, worldPoint, choice.choice, choice.referenceId);
        }
    }

    private IEnumerator ReviewSceneSearchHistoricalHint(SceneSearchLease lease, SceneVisionObservation historical, Action<bool> completed)
    {
        completed(false);
        if (lease.decisions >= SceneSearchDecisionLimit || lease.reviewedHistory.Contains(historical.CaptureId)) yield break;
        if (!IsFreshSceneObservation(lease.observation)) yield return CaptureSceneSearchObservation(lease);
        if (!SceneSearchBindingCurrent(lease)) yield break;
        string image = historical.ImageDataUrl;
        JArray candidates;
        if (!TryGetSceneSearchHistoricalPage(historical, lease.observation, 0, out var annotated, out candidates, out _))
            candidates = new JArray();
        else image = annotated;
        var input = new JObject { ["task"] = SceneSearchReport(lease), ["imageRole"] = "historical",
            ["historicalCaptureId"] = historical.CaptureId, ["historicalReferenceId"] = historical.CaptureId,
            ["historicalCandidates"] = candidates, ["visualObservation"] = JObject.Parse(historical.MetadataJson),
            ["currentSelfPose"] = BuildSceneSelfPoseContext(lease.observation, historical),
            ["scope"] = "This is actual earlier RGB, possibly annotated from its own frozen opaque IDs, never current sight. " +
                "Review the COMPLETE originalUserRequest including landmark relations. Prefer historical-object for an observed object/landmark: " +
                "Unity recomputes its frozen world point from the current position, including translation. historical-view only revisits the old camera direction, " +
                "not an object position. Choose done when this picture is not a useful hint. Authored names are hints only. " +
                "Descriptions apply only to this historical capture. No current visibility, target confirmation or operation authority follows." };
        SceneSearchProtocol.Decision recall = null; string failure = null;
        yield return RequestSceneSearchPlan(lease, input, image, (value, reason) => { recall = value; failure = reason; });
        if (!SceneSearchBindingCurrent(lease)) yield break;
        if (recall != null)
        {
            lease.reviewedHistory.Add(historical.CaptureId);
            foreach (var note in recall.observations) RememberSceneHistoricalDescription(historical, note.referenceId, note.description);
        }
        lease.receipts.Add(new JObject { ["choice"] = "review-historical-image", ["sourceCaptureId"] = historical.CaptureId,
            ["selectedChoice"] = recall?.choice, ["referenceId"] = recall?.referenceId,
            ["reason"] = recall?.reason ?? failure, ["actionAuthorization"] = false });
        if (recall == null)
        { FinishSceneSearchTask(lease, "incomplete", failure ?? "historical-review-unavailable"); yield break; }
        if (recall.choice == "done") yield break;
        // Obtain actual current pose after model latency; never execute using the
        // historical image or substitute a hidden live transform for its point.
        yield return CaptureSceneSearchObservation(lease);
        if (!SceneSearchBindingCurrent(lease)) yield break;
        Vector3 point;
        if (recall.choice == "historical-object")
        {
            if (!TryResolveSceneSearchHistoricalPoint(historical, recall.referenceId, out point, out failure)) yield break;
            var selected = candidates.OfType<JObject>().FirstOrDefault(row => (string)row["referenceId"] == recall.referenceId);
            if (!string.IsNullOrEmpty((string)selected?["entityId"])) lease.revisitedMemoryEntities.Add((string)selected["entityId"]);
        }
        else if (recall.choice == "historical-view" && recall.referenceId == historical.CaptureId &&
            lease.source.IsObservationHistoryBindingCurrent(historical))
            point = lease.observation.CameraPosition + historical.CameraRotation * Vector3.forward * 3f;
        else yield break;
        completed(true);
        yield return MoveSceneSearchView(lease, point, recall.choice, recall.referenceId);
    }

    private void RememberSceneSearchMemoryVisit(SceneSearchLease lease, JObject catalog, string referenceId, string role)
    {
        var point = (catalog["memory"] as JArray)?.OfType<JObject>().FirstOrDefault(row => (string)row["referenceId"] == referenceId);
        string entity = (string)point?["entityId"];
        if (!string.IsNullOrEmpty(entity)) lease.revisitedMemoryEntities.Add(entity);
        lease.receipts.Add(new JObject { ["stage"] = "memory-retrieval", ["referenceId"] = referenceId,
            ["entityId"] = entity, ["selectionRole"] = role, ["sourceCaptureId"] = point?["sourceCaptureId"]?.DeepClone(),
            ["scope"] = "This frozen point was selected for reobservation only; neither target identity nor device operation is confirmed." });
    }

    private Dictionary<string, SceneSearchPointOffer> FreezeSceneSearchPointOffers(SceneSearchLease lease, JObject input)
    {
        var offers = new Dictionary<string, SceneSearchPointOffer>(StringComparer.Ordinal);
        if (!(input["visibleCatalog"]?["memory"] is JArray memory)) return offers;
        foreach (var row in memory.OfType<JObject>())
        {
            string reference = (string)row["referenceId"], entity = (string)row["entityId"], capture = (string)row["sourceCaptureId"];
            if ((bool?)row["selectionAllowed"] != true || string.IsNullOrEmpty(reference)) continue;
            SceneSearchPointOffer offer = null;
            var sample = m_SceneVisibleMemory.FirstOrDefault(item => item.entry.entityId == entity && item.entry.sourceCaptureId == capture &&
                reference == capture + ":memory:" + entity);
            if (sample != null)
                offer = new SceneSearchPointOffer { referenceId = reference, entityId = entity, sourceCaptureId = capture,
                    point = sample.entry.pointAtCapture, observed = sample.entry.observed, memory = sample };
            else if (reference == "known-object-point:" + capture + ":" + entity && KnownSceneObjectBinding(entity, out var known) &&
                known.observed?.CaptureId == capture)
                offer = new SceneSearchPointOffer { referenceId = reference, entityId = entity, sourceCaptureId = capture,
                    point = known.observed.WorldPoint, observed = known.observed, known = known };
            if (offer != null && SceneSearchPointOfferValid(lease, offer)) offers[reference] = offer;
        }
        return offers;
    }

    private bool SceneSearchPointOfferValid(SceneSearchLease lease, SceneSearchPointOffer offer) =>
        SceneSearchBindingCurrent(lease) && offer != null && offer.observed?.EntityId == offer.entityId &&
        offer.observed.CaptureId == offer.sourceCaptureId && SceneSearchFrozenEvidenceValid(lease, offer.observed) &&
        (offer.memory != null ? SceneVisibleCatalogHistoryBinding(offer.memory.view) && SceneVisibleMemoryPositionValid(offer.memory) :
            offer.known != null && KnownSceneSpatialPositionValid(offer.known, out _));

    private bool TryResolveSceneSearchPointOffer(SceneSearchLease lease, Dictionary<string, SceneSearchPointOffer> offers,
        string reference, out Vector3 point, out string reason)
    {
        point = default;
        if (reference == null || !offers.TryGetValue(reference, out var offer) || !SceneSearchPointOfferValid(lease, offer))
        { reason = "search-quoted-historical-point-owner-or-geometry-invalid"; return false; }
        point = offer.point; reason = null; return true;
    }

    private bool TryGetSceneSearchReobservedPoint(SceneSearchLease lease, SceneSearchPointOffer offer,
        out SceneObjectCandidateView page, out string reference, out JObject catalog)
    {
        page = null; reference = null; catalog = null;
        if (!SceneSearchPointOfferValid(lease, offer) || SceneSearchCandidateReviewed(lease, offer.entityId) ||
            SceneSearchUnsupportedDeviceCandidate(lease, offer.entityId)) return false;
        var view = GetCurrentSceneVisibleCatalog(lease.observation);
        var current = view?.catalog.Candidates.FirstOrDefault(entry => entry.entityId == offer.entityId &&
            entry.observed.Root == offer.observed.Root && entry.observed.VisibleConfirmed && entry.observed.GeometryIsCurrent);
        if (current == null || !TrySelectSceneVisibleCatalogPage(lease.observation, view.catalog.CatalogId + ":p" + current.pageIndex, out page, out _)) return false;
        reference = current.referenceId; catalog = BuildSceneVisibleCatalogContext(lease.observation);
        lease.receipts.Add(new JObject { ["stage"] = "quoted-memory-reobserved", ["referenceId"] = offer.referenceId,
            ["entityId"] = offer.entityId, ["sourceCaptureId"] = offer.sourceCaptureId,
            ["quotedPointAtCapture"] = SceneSpatialVector(offer.point), ["resultCaptureId"] = lease.observation.CaptureId,
            ["semanticIdentityConfirmed"] = false, ["deviceWriteAuthorized"] = false,
            ["scope"] = "The quoted historical point retained its original source. New opaque pixels show the same entity; independent full-target matching still follows." });
        return true;
    }

    // Recover inside the SAME search lease and cumulative budgets. Failed target
    // checks are not a request for the human to restate an already clear goal.
    private IEnumerator RecoverSceneSearchProgress(SceneSearchLease lease, Action<bool> completed, string trigger = "candidate-failures")
    {
        completed(false);
        if (lease.recoveryAttempts >= SceneSearchRecoveryLimit || lease.steps >= SceneSearchStepLimit ||
            lease.decisions >= SceneSearchDecisionLimit || Time.realtimeSinceStartup - lease.startedAt >= SceneSearchDuration) yield break;
        lease.recoveryAttempts++;
        var catalog = BuildSceneVisibleCatalogContext(lease.observation);
        var input = SceneSearchPlannerInput(lease, catalog);
        var offered = (JObject)input["visibleCatalog"];
        foreach (var rows in new[] { offered["current"]?["candidates"], offered["current"]?["availablePages"], offered["historicalViews"] })
            if (rows is JArray entries) foreach (var row in entries.OfType<JObject>()) row["selectionAllowed"] = false;
        foreach (var row in ((JArray)input["explorationRegions"]).OfType<JObject>())
        {
            int offeredRegion;
            bool useful = TrySceneSearchRegion((string)row["referenceId"], out offeredRegion) && !lease.coveredRegions.Contains(offeredRegion);
            row["selectionAllowed"] = useful && TryBuildSceneSearchViewDecision(lease,
                lease.observation.CameraPosition + SceneSearchRegionDirection(lease, offeredRegion) * 3f, out _, out _);
        }
        input["recovery"] = new JObject { ["onlyNewView"] = true, ["attempt"] = lease.recoveryAttempts,
            ["trigger"] = trigger,
            ["limit"] = SceneSearchRecoveryLimit, ["cumulativeDecisions"] = lease.decisions, ["cumulativeMotionSteps"] = lease.steps,
            ["scope"] = "The target remains unresolved and useful views remain. Compare all unvisited historical object clues with the complete requested object, distinct from its landmark. Revisit a useful target clue first, otherwise survey an offered uncovered direction. Existing time/decision/motion limits remain unchanged. No target match or device authority is granted." };
        bool hasMemory = (offered["memory"] as JArray)?.OfType<JObject>().Any(row => (bool?)row["selectionAllowed"] == true) == true;
        bool hasRegion = ((JArray)input["explorationRegions"]).OfType<JObject>().Any(row => (bool?)row["selectionAllowed"] == true);
        if (!hasMemory && !hasRegion) yield break;
        var memoryOffers = FreezeSceneSearchPointOffers(lease, input);
        SceneSearchProtocol.Decision choice = null; string failure = null;
        yield return RequestSceneSearchPlan(lease, input, lease.observation.ImageDataUrl,
            (value, reason) => { choice = value; failure = reason; });
        if (!SceneSearchBindingCurrent(lease)) yield break;
        if (choice == null)
        {
            lease.receipts.Add(new JObject { ["stage"] = "recovery", ["accepted"] = false, ["reason"] = failure,
                ["attempt"] = lease.recoveryAttempts });
            yield break;
        }
        // Planning latency can age the image, but not its frozen point. Refresh
        // and resolve the exact offered point again; never substitute a transform.
        if (!IsFreshSceneObservation(lease.observation)) yield return CaptureSceneSearchObservation(lease);
        if (!SceneSearchBindingCurrent(lease)) yield break;
        Vector3 point; string reference = choice.referenceId, moveChoice = choice.choice;
        if (choice.choice == "historical-object")
        {
            if (!TryResolveSceneSearchPointOffer(lease, memoryOffers, reference, out point, out failure)) yield break;
            RememberSceneSearchMemoryVisit(lease, catalog, reference, choice.selectionRole);
            if (TryGetSceneSearchReobservedPoint(lease, memoryOffers[reference], out var currentView, out var currentReference, out var currentCatalog))
            {
                // A new same-entity observation is useful progress without an
                // unnecessary turn. Matching can still reject the actual target.
                lease.noProgress = 0;
                var reobserved = new JObject { ["stage"] = "recovery", ["attempt"] = lease.recoveryAttempts,
                    ["trigger"] = trigger, ["choice"] = "reobserved-historical-object", ["referenceId"] = reference,
                    ["selectionRole"] = choice.selectionRole, ["progressed"] = true,
                    ["decisions"] = lease.decisions, ["motionSteps"] = lease.steps, ["totalNoProgress"] = lease.totalNoProgress,
                    ["scope"] = "Fresh opaque pixels recovered the offered entity without motion. Complete-target matching is still pending; no identity or operation is authorized." };
                lease.receipts.Add(reobserved);
                Debug.Log("[SceneSearch/Recovery] " + reobserved.ToString(Formatting.None), this);
                yield return ConfirmSceneSearchCandidate(lease, currentReference, currentView, currentCatalog);
                if (SceneSearchBindingCurrent(lease)) completed(true);
                yield break;
            }
        }
        else if (choice.choice == "explore-region" && TrySceneSearchRegion(reference, out int region) && !lease.coveredRegions.Contains(region))
            point = lease.observation.CameraPosition + SceneSearchRegionDirection(lease, region) * 3f;
        else if (choice.choice == "done" && TryGetSceneSearchSurveyDirection(lease, out point, out reference))
            moveChoice = "survey-unobserved-direction";
        else yield break;
        int beforeDecisions = lease.decisions, beforeSteps = lease.steps;
        yield return MoveSceneSearchView(lease, point, moveChoice, reference);
        if (!SceneSearchBindingCurrent(lease)) yield break;
        bool changed = lease.steps > beforeSteps && (bool?)lease.lastStep?["observed"] == true;
        if (changed) lease.noProgress = 0;
        var receipt = new JObject { ["stage"] = "recovery", ["attempt"] = lease.recoveryAttempts,
            ["trigger"] = trigger,
            ["choice"] = moveChoice, ["referenceId"] = reference, ["selectionRole"] = choice.selectionRole,
            ["progressed"] = changed, ["decisions"] = beforeDecisions, ["motionSteps"] = lease.steps,
            ["totalNoProgress"] = lease.totalNoProgress,
            ["scope"] = "Progress means a new actual view after a previously unvisited clue/uncovered direction, not target recognition, completion, or operation." };
        lease.receipts.Add(receipt);
        Debug.Log("[SceneSearch/Recovery] " + receipt.ToString(Formatting.None), this);
        completed(changed);
    }

    private IEnumerator ConfirmSceneSearchCandidate(SceneSearchLease lease, string referenceId,
        SceneObjectCandidateView view, JObject catalog)
    {
        JObject item = (catalog["current"]?["candidates"] as JArray)?.OfType<JObject>().FirstOrDefault(value =>
            (string)value["referenceId"] == referenceId || (string)value["selectionId"] == referenceId);
        SceneObservedObject initial;
        string failure;
        if (item == null || !view.TryGetCandidate((string)item["candidateId"], out initial, out failure) ||
            !string.IsNullOrEmpty(lease.boundEntityId) && initial.EntityId != lease.boundEntityId)
        { SceneSearchNoProgress(lease, "search-candidate-not-current-or-bound-identity-conflict"); yield break; }
        string candidateKey = initial.EntityId;
        if (SceneSearchUnsupportedDeviceCandidate(lease, candidateKey))
        { SceneSearchNoProgress(lease, "search-device-control-unavailable-already-reviewed"); yield break; }
        if (lease.reviewedCandidates.TryGetValue(candidateKey, out var earlierCandidate) &&
            earlierCandidate.Root == initial.Root && SceneSearchReviewStillUseful(lease, earlierCandidate))
        { SceneSearchNoProgress(lease, "search-repeated-candidate-without-new-view"); yield break; }
        if (!lease.model.SupportsSceneObjectMatchMessages)
        { FinishSceneSearchTask(lease, "incomplete", "search-target-matcher-unavailable"); yield break; }
        // Search selection and independent semantic review are separate model
        // calls. Give the review its own fresh RGB lifetime; the former selection
        // is only a same-identity hint, never an executable current-image claim.
        string selectionCaptureId = view.CaptureId;
        SceneVisionObservation selectionObservation = lease.observation;
        yield return CaptureSceneSearchObservation(lease);
        if (!SceneSearchBindingCurrent(lease)) yield break;
        SceneObservedObject matchInitial = null;
        bool oldGeometryCurrent = initial.GeometryIsCurrent;
        string refreshFailure = null;
        if (!oldGeometryCurrent) refreshFailure = "selection-geometry-changed";
        else if (!lease.source.TryFindObservedObject(lease.observation, initial.EntityId, out matchInitial, out failure))
            refreshFailure = "same-entity-not-in-fresh-pixels:" + failure;
        else if (!matchInitial.VisibleConfirmed) refreshFailure = "fresh-entity-not-opaque-confirmed";
        else if (matchInitial.Root != initial.Root) refreshFailure = "fresh-entity-root-changed";
        else if (!matchInitial.GeometryIsCurrent) refreshFailure = "fresh-entity-geometry-changed";
        else if (!matchInitial.TryValidate(out failure)) refreshFailure = "fresh-entity-validation:" + failure;
        else if (!lease.source.TryBuildObjectCandidateView(lease.observation, matchInitial.ImagePoint, matchInitial.EntityId, out view, out failure))
            refreshFailure = "fresh-candidate-view-unavailable:" + failure;
        // Idle head return may change the camera while the entity remains in view.
        // Old pixels are only an identity hint: current opaque identity and the NEW
        // composite below supply the independent match, so old camera compatibility
        // is diagnostic information, never a prerequisite for that fresh review.
        var refreshReceipt = new JObject { ["taskId"] = lease.id, ["entityId"] = initial.EntityId,
            ["selectionCaptureId"] = selectionCaptureId, ["freshCaptureId"] = lease.observation.CaptureId,
            ["cameraTranslationMeters"] = Vector3.Distance(selectionObservation.CameraPosition, lease.observation.CameraPosition),
            ["cameraRotationDegrees"] = Quaternion.Angle(selectionObservation.CameraRotation, lease.observation.CameraRotation),
            ["selectionGeometryCurrent"] = oldGeometryCurrent, ["freshOpaqueConfirmed"] = matchInitial?.VisibleConfirmed == true,
            ["freshSameRoot"] = matchInitial != null && matchInitial.Root == initial.Root,
            ["freshGeometryCurrent"] = matchInitial?.GeometryIsCurrent == true,
            ["acceptedForIndependentReview"] = refreshFailure == null, ["reason"] = refreshFailure };
        Debug.Log("[SceneSearch/CandidateRefresh] " + refreshReceipt.ToString(Formatting.None), this);
        lease.receipts.Add(refreshReceipt);
        if (refreshFailure != null)
        { SceneSearchNoProgress(lease, "search-selected-entity-not-current-for-independent-review:" + refreshFailure); yield break; }
        initial = matchInitial;
        SetSceneSearchCandidatePreview(view, "search-independent-target-match");
        // The fresh capture can legitimately replace the old view's review cache
        // (for example while idle animation returns the head). Deduplicate the
        // image actually supplied to the independent reviewer, not the earlier
        // selection image that CaptureSceneSearchObservation may have invalidated.
        lease.reviewedCandidates[candidateKey] = initial;
        var input = new JObject {
            ["intent"] = "inspect-object", ["targetPurpose"] = "direct-target",
            ["originalGoal"] = lease.originalRequest, ["currentRequest"] = lease.originalRequest,
            ["latestUserRequest"] = lease.latestUserText, ["requestedDescription"] = lease.originalRequest,
            ["requestedEntityId"] = initial.EntityId, ["initialGeometryEntityId"] = initial.EntityId,
            ["referenceContext"] = lease.referenceContext?.DeepClone(),
            ["requiredBoundEntityId"] = lease.boundEntityId, ["candidates"] = JObject.Parse(view.MetadataJson),
            ["referentSources"] = BuildSpatialGoalReferentSources(),
            ["instructionScope"] = "Independently determine whether the selected current opaque candidate is the target of the COMPLETE originalGoal. " +
                "This is only a search confirmation, never device authorization. Historical referents constrain identity only. " +
                "Decline if uncertain. A bound entity must not be replaced with another lamp/object. Only the selected entity can complete this search step."
        };
        lease.phase = "matching"; lease.pendingKind = "match"; lease.phaseStartedAt = Time.realtimeSinceStartup;
        int revision = ++lease.requestRevision;
        bool received = false, accepted = false; string raw = "", status = "search-match-timeout";
        try { lease.model.PostSceneObjectMatchMessage(input.ToString(Formatting.None), view.ImageDataUrl, (ok, value, why) => {
            if (!SceneSearchBindingCurrent(lease) || revision != lease.requestRevision || lease.pendingKind != "match") return;
            received = true; accepted = ok; raw = value; status = why;
        }); }
        catch (Exception error) { received = true; status = "search-match-request:" + error.GetType().Name; }
        float until = Time.realtimeSinceStartup + 12.5f;
        while (SceneSearchBindingCurrent(lease) && !received && Time.realtimeSinceStartup < until) yield return null;
        if (!SceneSearchBindingCurrent(lease)) yield break;
        lease.pendingKind = null; lease.model.CancelSceneObjectMatchMessage();
        RoomTaskProtocol.ObjectMatch match = null;
        SceneObservedObject matched = null;
        bool parsed = received && accepted && RoomTaskProtocol.TryObjectMatch(raw, view.Candidates.Select(value => value.candidateId), out match);
        bool confirmed = parsed && match.IsMatched && view.TryGetCandidate(match.candidateId, out matched, out failure) &&
            matched.EntityId == initial.EntityId && matched.Root == initial.Root && matched.VisibleConfirmed;
        var receipt = new JObject { ["choice"] = "current-candidate", ["referenceId"] = referenceId,
            ["selectionCaptureId"] = selectionCaptureId,
            ["matchCaptureId"] = view.CaptureId, ["entityId"] = initial.EntityId, ["confirmed"] = confirmed,
            ["returnedCandidateId"] = parsed ? match.candidateId : null, ["returnedEntityId"] = matched?.EntityId,
            ["freshCandidateMapping"] = new JArray(view.Candidates.Select(candidate => new JObject {
                ["candidateId"] = candidate.candidateId, ["entityId"] = candidate.entityId, ["sceneLabel"] = candidate.sceneLabel })),
            ["verdict"] = parsed ? match.verdict : "unavailable", ["candidateRole"] = parsed ? match.candidateRole : null,
            ["reason"] = parsed ? match.reason : status };
        lease.lastStep = receipt; lease.receipts.Add(receipt);
        Debug.Log("[SceneSearch/Match] " + receipt.ToString(Formatting.None), this);
        if (!confirmed) { SceneSearchNoProgress(lease, "search-target-not-confirmed"); yield break; }
        yield return CaptureSceneSearchObservation(lease);
        if (!SceneSearchBindingCurrent(lease)) yield break;
        SceneObservedObject refreshed;
        if (!initial.GeometryIsCurrent || !lease.source.TryFindObservedObject(lease.observation, initial.EntityId, out refreshed, out failure) ||
            !refreshed.VisibleConfirmed || refreshed.Root != initial.Root || !refreshed.GeometryIsCurrent || !refreshed.TryValidate(out failure))
        { SceneSearchNoProgress(lease, "search-matched-entity-not-reobserved"); yield break; }
        var device = refreshed.Root.GetComponent<SceneObjectLightSwitch>();
        if (device != null && (!device.IsBound || device.EntityId != refreshed.EntityId || !device.TryValidate(out _))) device = null;
        var fact = new SceneActionFact { actionId = lease.id + ":confirmed", intent = "inspect-object", origin = lease.origin,
            captureId = view.CaptureId, entityId = refreshed.EntityId, targetDescription = BoundObjectGoal(lease.originalRequest),
            targetPurpose = "direct-target", semanticTargetConfirmed = true, objectConfirmed = true,
            targetMatchVerdict = match.verdict, targetMatchReason = match.reason, matchCaptureId = view.CaptureId,
            confirmedCaptureId = lease.observation.CaptureId, resultCaptureId = lease.observation.CaptureId,
            scope = "Search target independently matched in actual current RGB, then the same entity reobserved in a new image. No device write or navigation was performed." };
        if (!string.IsNullOrEmpty(lease.deviceGoalId))
        {
            // Bind only the same existing, independently authorized device goal.
            // Returning found with an unbound goal would let the next planner
            // silently replace this confirmed entity with a different device.
            if (!TryBindSpatialGoalSearchTarget(fact, refreshed, out failure))
            {
                if (failure == "search-selected-object-has-no-registered-control") lease.unsupportedDeviceCandidates[refreshed.EntityId] = refreshed;
                SceneSearchNoProgress(lease, string.IsNullOrEmpty(failure) ? "search-confirmed-object-cannot-bind-device-goal" : failure); yield break;
            }
            lease.boundEntityId = refreshed.EntityId;
        }
        RememberSceneVisibleSemanticMatch(matched, lease.originalRequest, view.CaptureId);
        RememberSceneObject(refreshed, device);
        RecordSceneAction(fact, "observed", "search target matched and same entity reobserved in a new image");
        RememberVerifiedObservedDeviceReferent(fact);
        lease.foundEntityId = refreshed.EntityId; lease.foundCaptureId = lease.observation.CaptureId;
        receipt["freshSameEntityCaptureId"] = lease.foundCaptureId;
        FinishSceneSearchTask(lease, "found", "search-current-target-confirmed");
    }

    private IEnumerator MoveSceneSearchView(SceneSearchLease lease, Vector3 worldPoint, string choice, string referenceId)
    {
        RoomTaskProtocol.Decision motion;
        string failure;
        if (!TryBuildSceneSearchViewDecision(lease, worldPoint, out motion, out failure))
        { SceneSearchNoProgress(lease, failure); yield break; }
        int before = lease.coveredRegions.Count;
        string previous = lease.observation.CaptureId;
        bool completed = false;
        lease.phase = "moving"; lease.phaseStartedAt = Time.realtimeSinceStartup;
        lease.viewActionRootPosition = lease.avatar.transform.position;
        lease.viewActionControllerOperation = -1;
        BeginSceneViewChange(motion, lease.observation, lease.generation, () => { completed = true; });
        lease.sceneEpoch = m_SceneActionEpoch; lease.actionId = m_SceneActionFact?.actionId;
        lease.controllerOperation = lease.controller.OperationRevision;
        float until = Time.realtimeSinceStartup + 110f;
        while (SceneSearchBindingCurrent(lease) && !completed && Time.realtimeSinceStartup < until) yield return null;
        if (!SceneSearchBindingCurrent(lease)) yield break;
        lease.steps++; lease.controllerOperation = lease.controller.OperationRevision;
        var fact = m_SceneActionFact;
        var next = GetFormalSceneObservation(lease.generation);
        bool observed = completed && fact != null && fact.actionId == lease.actionId && fact.intent == motion.intent &&
            fact.phase == "observed" && fact.actualViewChangeDegrees >= 1f && next?.CaptureId == fact.resultCaptureId &&
            next.CaptureId != previous && IsFreshSceneObservation(next);
        lease.lastStep = new JObject { ["choice"] = choice, ["referenceId"] = referenceId, ["intent"] = motion.intent,
            ["requestedYaw"] = motion.viewChange.yaw, ["requestedPitch"] = motion.viewChange.pitch,
            ["sourceCaptureId"] = previous, ["resultCaptureId"] = observed ? next.CaptureId : null,
            ["actionId"] = lease.actionId, ["observed"] = observed,
            ["controllerOperationRevision"] = lease.viewActionControllerOperation,
            ["rootDisplacementFromStationaryBaseline"] = Vector3.Distance(lease.avatar.transform.position, lease.rootPosition),
            ["rootDisplacementFromActionStart"] = Vector3.Distance(lease.avatar.transform.position, lease.viewActionRootPosition),
            ["stationaryRootRebased"] = false,
            ["actualViewChangeDegrees"] = fact?.actualViewChangeDegrees ?? 0, ["reason"] = fact?.reason };
        lease.receipts.Add(lease.lastStep);
        if (!observed)
        { FinishSceneSearchTask(lease, "incomplete", fact?.reason ?? "search-view-action-did-not-complete"); yield break; }
        // Only this exact native turn's successful receipt plus its NEW RGB can
        // establish a new stationary baseline. Unowned/root-only motion retains
        // the original 4 cm cancellation boundary throughout the search.
        if (motion.intent == "turn-in-place" && SceneSearchOwnsBodyTurnDisplacement(lease))
        {
            lease.rootPosition = lease.avatar.transform.position;
            lease.lastStep["stationaryRootRebased"] = true;
        }
        AcceptSceneSearchObservation(lease, next);
        lease.noProgress = lease.coveredRegions.Count > before ? 0 : lease.noProgress + 1;
        lease.phase = "observing";
    }

    private bool TryBuildSceneSearchViewDecision(SceneSearchLease lease, Vector3 worldPoint,
        out RoomTaskProtocol.Decision decision, out string reason)
    {
        decision = null; reason = null;
        var eyes = lease.avatar.GetComponent<NevaEyeTracking>();
        if (eyes == null || !eyes.IsInitialized || !eyes.isActiveAndEnabled || !IsFreshSceneObservation(lease.observation))
        { reason = "search-attention-rig-or-image-unavailable"; return false; }
        Vector3 direction = worldPoint - eyes.EyeOrigin;
        if (!SceneSearchFinite(direction) || direction.sqrMagnitude < .01f)
        { reason = "search-selected-direction-unavailable"; return false; }
        Quaternion frame = eyes.HeadFrameRotation;
        Vector2 current = m_SceneAttention != null && m_SceneAttention.Target == eyes.Target ? m_SceneAttention.CurrentHeadAngles : Vector2.zero;
        Quaternion neutral = frame * Quaternion.Inverse(Quaternion.Euler(-current.y, current.x, 0f));
        Vector2 relative = SceneSearchDirectionAngles(Quaternion.Inverse(frame) * direction);
        Vector2 total = SceneSearchDirectionAngles(Quaternion.Inverse(neutral) * direction);
        float maxYaw = m_SceneAttention != null ? Mathf.Clamp(m_SceneAttention.maximumYawDegrees, 10f, 55f) : 50f;
        float maxUp = m_SceneAttention != null ? Mathf.Clamp(m_SceneAttention.maximumUpDegrees, 5f, 30f) : 25f;
        float maxDown = m_SceneAttention != null ? Mathf.Clamp(m_SceneAttention.maximumDownDegrees, 5f, 35f) : 30f;
        bool head = Mathf.Abs(total.x) <= maxYaw - 1f && total.y <= maxUp - 1f && total.y >= -maxDown + 1f &&
            Mathf.Abs(relative.x) <= 50f && Mathf.Abs(relative.y) <= 25f;
        float yaw, pitch;
        if (head)
        {
            yaw = relative.x; pitch = relative.y;
            if (Mathf.Max(Mathf.Abs(yaw), Mathf.Abs(pitch)) < 3f)
            { reason = "search-selected-view-already-current"; return false; }
        }
        else
        {
            float remaining = SceneSearchShortestYaw(lease.avatar.transform.forward, direction);
            yaw = Mathf.Clamp(remaining, -180f, 180f); pitch = 0;
            if (Mathf.Abs(yaw) < 5f)
            { reason = "search-selected-direction-outside-natural-head-range"; return false; }
        }
        decision = new RoomTaskProtocol.Decision { intent = head ? "orient-view" : "turn-in-place",
            origin = lease.origin, evidence = lease.originalRequest,
            viewChange = new RoomTaskProtocol.ViewChange { captureId = lease.observation.CaptureId, yaw = yaw, pitch = pitch } };
        return true;
    }

    // Once actual candidate pages at this pose have been reviewed, survey must
    // acquire a different real view. Prefer a comfortable head change exposing a
    // new sector; use a shortest body turn only when the head cannot do that.
    private bool TryGetSceneSearchSurveyDirection(SceneSearchLease lease, out Vector3 point, out string reference)
    {
        point = default; reference = null; float best = float.PositiveInfinity;
        float halfWidth = Mathf.Atan(Mathf.Tan(lease.observation.VerticalFov * Mathf.Deg2Rad * .5f) *
            lease.observation.Width / Mathf.Max(1f, lease.observation.Height)) * Mathf.Rad2Deg;
        float usable = Mathf.Clamp(halfWidth - 12f, 5f, 55f);
        for (int i = 0; i < 12; i++)
        {
            Vector3 direction = SceneSearchRegionDirection(lease, i);
            bool additional = Enumerable.Range(0, 12).Any(index => !lease.coveredRegions.Contains(index) &&
                Vector3.Angle(direction, SceneSearchRegionDirection(lease, index)) <= usable);
            Vector3 candidate = lease.observation.CameraPosition + direction * 3f;
            if (!additional || !TryBuildSceneSearchViewDecision(lease, candidate, out var motion, out _)) continue;
            float cost = Mathf.Abs(motion.viewChange.yaw) + (motion.intent == "turn-in-place" ? 360f : 0f);
            if (cost >= best) continue;
            best = cost; point = candidate; reference = "region-" + i;
        }
        return reference != null;
    }

    private static Vector2 SceneSearchDirectionAngles(Vector3 direction) => new Vector2(
        Mathf.Atan2(direction.x, direction.z), Mathf.Atan2(direction.y, new Vector2(direction.x, direction.z).magnitude)) * Mathf.Rad2Deg;
    private static float SceneSearchShortestYaw(Vector3 from, Vector3 to) =>
        Vector3.SignedAngle(Vector3.ProjectOnPlane(from, Vector3.up), Vector3.ProjectOnPlane(to, Vector3.up), Vector3.up);
    private static bool SceneSearchFinite(Vector3 value) => !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z) &&
        !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);
    private static Vector3 SceneSearchRegionDirection(SceneSearchLease lease, int region) => Quaternion.AngleAxis(region * 30f, Vector3.up) * lease.startForward;
    private static bool TrySceneSearchRegion(string referenceId, out int region)
    {
        region = -1;
        return referenceId != null && referenceId.StartsWith("region-", StringComparison.Ordinal) &&
            int.TryParse(referenceId.Substring(7), out region) && region >= 0 && region < 12 && referenceId == "region-" + region;
    }
    private bool SceneSearchCurrentPagesReviewed(SceneSearchLease lease, JObject catalog)
    {
        int count = (int?)catalog["current"]?["pageCount"] ?? 0;
        if (count < 1) return (int?)catalog["current"]?["totalCandidates"] == 0;
        if ((bool?)catalog["current"]?["truncated"] == true) return false;
        for (int i = 0; i < count; i++)
        {
            if (!TryGetSceneVisibleCatalogPage(lease.observation, i, out var page, out _)) return false;
            foreach (var candidate in page.Candidates)
                if (!lease.reviewedPageEntities.TryGetValue(candidate.entityId, out var earlier) || !SceneSearchFrozenEvidenceValid(lease, earlier) ||
                    !page.TryGetCandidate(candidate.candidateId, out var current, out _) || current.Root != earlier.Root) return false;
        }
        return true;
    }
    private bool SceneSearchFrozenEvidenceValid(SceneSearchLease lease, SceneObservedObject observed) =>
        observed != null && observed.source == lease.source && observed.Root != null &&
        (observed.Root == lease.environment || observed.Root.IsChildOf(lease.environment)) &&
        SceneSearchMemoryAgeValid(observed.observation.CapturedAtRealtime, Time.realtimeSinceStartup) &&
        observed.TryValidateFrozenGeometry(out _);

    private static bool SceneSearchSameInspectionPose(Vector3 oldPosition, Vector3 oldForward, Vector3 newPosition, Vector3 newForward) =>
        Vector3.Distance(oldPosition, newPosition) <= .12f && Vector3.Angle(oldForward, newForward) <= 12f;

    private static bool SceneSearchSameInspectionView(SceneVisionObservation previous, SceneVisionObservation current) =>
        previous != null && current != null && SceneSearchSameInspectionPose(previous.CameraPosition,
            previous.CameraRotation * Vector3.forward, current.CameraPosition, current.CameraRotation * Vector3.forward);

    private bool SceneSearchReviewStillUseful(SceneSearchLease lease, SceneObservedObject observed) =>
        SceneSearchFrozenEvidenceValid(lease, observed) && SceneSearchSameInspectionView(observed.observation, lease.observation);

    private bool SceneSearchUnsupportedDeviceCandidate(SceneSearchLease lease, string entity)
    {
        if (string.IsNullOrEmpty(lease.deviceGoalId) || string.IsNullOrEmpty(entity) ||
            !lease.unsupportedDeviceCandidates.TryGetValue(entity, out var old) || !SceneSearchFrozenEvidenceValid(lease, old)) return false;
        var device = old.Root.GetComponent<SceneObjectLightSwitch>();
        return device == null || !device.IsBound || device.EntityId != entity || !device.TryValidate(out _);
    }

    private bool SceneSearchCandidateReviewed(SceneSearchLease lease, string entity) =>
        entity != null && lease.reviewedCandidates.TryGetValue(entity, out var earlier) && SceneSearchReviewStillUseful(lease, earlier);

    private bool SceneSearchPageReviewed(SceneSearchLease lease, int index)
    {
        if (!TryGetSceneVisibleCatalogPage(lease.observation, index, out var page, out _)) return false;
        foreach (var candidate in page.Candidates)
            if (!lease.reviewedPageEntities.TryGetValue(candidate.entityId, out var old) || !SceneSearchFrozenEvidenceValid(lease, old) ||
                !page.TryGetCandidate(candidate.candidateId, out var current, out _) || old.Root != current.Root) return false;
        return true;
    }
    private void SceneSearchNoProgress(SceneSearchLease lease, string reason)
    {
        lease.noProgress++; lease.totalNoProgress++;
        lease.lastStep = new JObject { ["accepted"] = false, ["reason"] = reason ?? "search-no-progress" };
        lease.receipts.Add(lease.lastStep);
    }

    private void RecordSceneSearchPageCoverage(SceneSearchLease lease, JObject catalog)
    {
        int pages = (int?)catalog["current"]?["pageCount"] ?? 0;
        int checkedPages = 0;
        for (int i = 0; i < pages; i++) if (lease.reviewedPages.Contains(lease.observation.CaptureId + ":" + i)) checkedPages++;
        lease.pageCoverage.Add(new JObject { ["captureId"] = lease.observation.CaptureId,
            ["offeredPages"] = pages, ["reviewedPagesInExactCapture"] = checkedPages,
            ["allCurrentCandidatesReviewedAtThisPose"] = SceneSearchCurrentPagesReviewed(lease, catalog),
            ["truncated"] = (bool?)catalog["current"]?["truncated"] ?? false });
    }

    private JObject SceneSearchReport(SceneSearchLease lease) => new JObject {
        ["taskId"] = lease.id, ["generation"] = lease.generation, ["workEpoch"] = lease.workEpoch, ["userTurn"] = lease.userTurn,
        ["active"] = !lease.ending, ["ownerCurrent"] = SceneSearchBindingCurrent(lease), ["mode"] = lease.mode,
        ["originalUserRequest"] = lease.originalRequest, ["latestUserText"] = lease.latestUserText,
        ["referenceContext"] = lease.referenceContext?.DeepClone(),
        ["phase"] = lease.phase, ["status"] = lease.status, ["reason"] = lease.reason,
        ["steps"] = lease.steps, ["stepLimit"] = SceneSearchStepLimit, ["decisions"] = lease.decisions,
        ["decisionLimit"] = SceneSearchDecisionLimit, ["noProgressSteps"] = lease.noProgress,
        ["totalNoProgress"] = lease.totalNoProgress, ["recoveryAttempts"] = lease.recoveryAttempts, ["recoveryLimit"] = SceneSearchRecoveryLimit,
        ["revisitedMemoryEntityIds"] = new JArray(lease.revisitedMemoryEntities),
        ["unsupportedDeviceEntityIds"] = new JArray(lease.unsupportedDeviceCandidates.Keys),
        ["durationSeconds"] = Mathf.Max(0, Time.realtimeSinceStartup - lease.startedAt), ["durationLimitSeconds"] = SceneSearchDuration,
        ["coveredHorizontalRegions"] = lease.coveredRegions.Count, ["horizontalRegionCount"] = 12,
        ["candidatePageCoverage"] = lease.pageCoverage.DeepClone(),
        ["allVisitedCandidatePagesReviewed"] = lease.pageCoverage.Count > 0 && lease.pageCoverage.OfType<JObject>()
            .All(value => (bool?)value["allCurrentCandidatesReviewedAtThisPose"] == true && (bool?)value["truncated"] != true),
        ["foundEntityId"] = lease.foundEntityId, ["foundCaptureId"] = lease.foundCaptureId,
        ["boundEntityId"] = lease.boundEntityId, ["deviceGoalId"] = lease.deviceGoalId,
        ["goalExecutionRevision"] = lease.deviceRevision,
        ["searchAttempted"] = lease.decisions > 0 || lease.steps > 0, ["deviceWriteAuthorized"] = false,
        ["receipts"] = lease.receipts.DeepClone(), ["reviewedHistoricalCaptureIds"] = new JArray(lease.reviewedHistory),
        ["scope"] = "Bounded observation task only. found requires an independent semantic match and same-entity opaque confirmation in a NEW current frame. " +
            "completed surveys concern sampled horizontal view directions, not every room object or occluded space. " +
            "Candidate identification is limited to pages actually reviewed; candidatePageCoverage separately records skipped or truncated pages in visited views. " +
            "Never claim all offered objects were inspected unless allVisitedCandidatePagesReviewed is true; even then unsupported/occluded objects remain unassessed. " +
            "incomplete/cancelled do not prove the target absent. Historical positions/descriptions are hints, not current visibility, navigation or device authority. " +
            "No device state was changed by this search. The most recent motion receipt describes only one substep, not the overall result."
    };

    private JObject BuildSceneSearchContext()
    {
        if (m_SceneSearch != null) return SceneSearchReport(m_SceneSearch);
        if ((int?)m_SceneSearchLastReport?["generation"] == m_FormalResponseGeneration)
            return (JObject)m_SceneSearchLastReport.DeepClone();
        return new JObject { ["active"] = false, ["ownerCurrent"] = false };
    }

    private void FinishSceneSearchTask(SceneSearchLease lease, string status, string reason)
    {
        if (!SceneSearchBindingCurrent(lease)) return;
        lease.status = status; lease.reason = reason; lease.phase = "terminal";
        if (IsFreshSceneObservation(lease.observation)) RecordSceneSearchPageCoverage(lease, BuildSceneVisibleCatalogContext(lease.observation));
        m_SceneSearchLastReport = SceneSearchReport(lease);
        m_SceneSearchLastReport["active"] = false;
        lease.ending = true; m_SceneSearch = null;
        ReleaseSceneSearchOwnedWork(lease, reason);
        m_RoomTaskDispatch = "search-" + status + ": " + reason;
        Debug.Log("[SceneSearch/Finished] " + m_SceneSearchLastReport.ToString(Formatting.None), this);
        lease.completed?.Invoke();
    }

    private void CancelSceneSearchTask(string reason)
    {
        var lease = m_SceneSearch;
        if (lease == null) return;
        lease.status = "cancelled"; lease.reason = reason; lease.phase = "terminal";
        m_SceneSearchLastReport = SceneSearchReport(lease);
        m_SceneSearchLastReport["active"] = false; m_SceneSearchLastReport["ownerCurrent"] = false;
        lease.ending = true; m_SceneSearch = null;
        ReleaseSceneSearchOwnedWork(lease, reason);
        Debug.Log("[SceneSearch/Cancelled] " + m_SceneSearchLastReport.ToString(Formatting.None), this);
        // Cancellation never emits a stale completion callback or public speech.
    }

    private void ReleaseSceneSearchOwnedWork(SceneSearchLease lease, string reason)
    {
        ++lease.requestRevision;
        if (lease.pendingKind == "search") lease.model?.CancelSceneSearchDecisionMessage();
        if (lease.pendingKind == "match") lease.model?.CancelSceneObjectMatchMessage();
        lease.pendingKind = null;
        if (!string.IsNullOrEmpty(lease.actionId) && m_SceneActionFact?.actionId == lease.actionId &&
            lease.sceneEpoch == m_SceneActionEpoch && m_SceneActionActive) CancelSceneAction("scene-search: " + reason);
        // Preserve the last settled camera through a legitimate terminal reply;
        // explicit cancellation must also release a head held between planner calls.
        if (lease.status == "cancelled" && !string.IsNullOrEmpty(lease.actionId))
            m_SceneAttention?.Release(lease.actionId, "scene-search: " + reason);
    }
}
