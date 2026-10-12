using System.Linq;
using Newtonsoft.Json.Linq;

public partial class ChatQW
{
    private static void FocusRoomStepContext(JObject input)
    {
        FocusRoomPlanningEvidence(input);
        if (input["taskSequence"] is JObject sequence) input["taskSequence"] = RoomTaskContextPolicy.Sequence(sequence);
        var intents = RoomTaskContextPolicy.ReadyUserIntents(input);
        bool initialBody = RoomTaskContextPolicy.InitialUserBodyAttention(input);
        if (intents == null && !initialBody) return;
        var omitted = new JObject();
        foreach (string path in new[] { "visibleObjectCatalog.current.candidates", "visibleObjectCatalog.memory", "sceneSpatialMemory.objects",
            "sceneCognitionSummary.currentObjects", "sceneCognitionSummary.historicalObjects",
            "sceneActionFacts.objectContext.recentObservations", "sceneActionFacts.objectContext.knownObjectHistory" })
        {
            if (!(input.SelectToken(path) is JArray rows)) continue;
            // Unknown/custom restrictions protect the entire row. Initial
            // attention leaves the menu intact; only approved continuations narrow it.
            var keep = rows.Where(r => !(r is JObject obj) || !SceneAttentionOptional(obj)).Select(r => r.DeepClone()).ToArray();
            if (keep.Length == rows.Count) continue;
            omitted[path] = rows.Count - keep.Length;
            rows.Replace(new JArray(keep));
        }
        // A new, independently classified body-only request has no object
        // referent. Compound continuations still need predecessor evidence.
        if (initialBody && !(input["taskSequence"] is JObject)) FocusInitialBodyHistory(input, omitted);
        input["stepAttention"] = new JObject { ["intents"] = intents == null ? null : new JArray(intents),
            ["source"] = initialBody ? "current-turn-semantic-attention" : "approved-ready-step", ["omittedObservedRows"] = omitted,
            ["engineMemoryMutated"] = false,
            ["scope"] = "Current-user movement attention. Original request, dependencies, user pose/route/support, failures and unknown constraints remain. Object records stay in runtime for a fresh search/match when needed. An initial semantic focus does NOT narrow the action menu or authorize a move. Omission proves neither absence nor permission." };
    }

    private static void FocusInitialBodyHistory(JObject input, JObject omitted)
    {
        void Filter(string path, System.Func<JObject, bool> optional)
        {
            if (!(input.SelectToken(path) is JArray rows)) return;
            var keep = rows.Where(r => !(r is JObject obj) || !optional(obj)).Select(r => r.DeepClone()).ToArray();
            if (keep.Length == rows.Count) return;
            omitted[path] = rows.Count - keep.Length;
            rows.Replace(new JArray(keep));
        }
        bool KnownHistory(JObject row, string[] extra) => !row.Descendants().OfType<JProperty>().Any(p =>
            !RoomDecisionKnownObservationFields.Contains(p.Name) && !RoomDecisionKnownObservationChildFields.Contains(p.Name) && !extra.Contains(p.Name)) &&
            !row.Descendants().OfType<JProperty>().Any(p => p.Name == "actionAuthorization" && p.Value.Type == JTokenType.Boolean && (bool)p.Value);
        Filter("visibleObjectCatalog.historicalViews", row =>
            (string)row["imageRole"] == "historical-view-search-hint" && (bool?)row["actionAuthorization"] == false &&
            KnownHistory(row, new[] { "imageRole", "imageAttachedToCurrentLocalization", "returnDirection" }));
        Filter("workingMemory.recallIndex", row => KnownHistory(row, new[] { "authoredNames", "currentBearing", "recallStrength", "recallDetail",
            "coarseBodyDirection", "cameraYawDegreesRight", "cameraElevationDegreesUp", "bodyYawDegreesRight" }));
        if (input.SelectToken("visibleObjectCatalog.historicalViews") is JArray views && views.Count == 0 &&
            input["planningHistoryImageCaptureIds"] is JArray images && images.All(v => v.Type == JTokenType.String))
            input["planningHistoryImageCaptureIds"] = new JArray();

        // Keep outcome/identity/validity, literal user updates and unknown
        // extensions. Only known observation/trace detail leaves this request.
        if (input.SelectToken("workingMemory.completedTasks") is JObject completed)
            foreach (var entry in completed.Properties())
                if (entry.Value is JObject goal && new[] { "satisfied", "cancelled" }.Contains((string)goal["status"]))
                {
                    if (goal["boundEntityMemory"] is JObject memory && SceneAttentionOptional(memory))
                    { goal.Remove("boundEntityMemory"); omitted["workingMemory.completedTasks." + entry.Name + ".boundEntityMemory"] = 1; }
                    foreach (string name in new[] { "referenceContext", "searchProgress", "failedCandidates" })
                        if (goal[name] != null && RoomWorkingMemoryKnownArchiveField(name, goal[name])) goal.Remove(name);
                }

        var scene = input["sceneActionFacts"] as JObject;
        var last = scene?["lastAction"] as JObject;
        bool unfinished = new[] { "spatialGoal", "standingGoal", "pendingDeviceRequest", "pendingObservationGoal" }
            .Any(path => input[path] is JObject task && !new[] { "satisfied", "cancelled" }.Contains((string)task["status"]));
        if (unfinished || (bool?)scene?["active"] != false || (bool?)last?["active"] != false ||
            (string)last?["intent"] != "set-object-state" || (string)last?["phase"] != "performed") return;
        // Geometry and renderer diagnostics of a completed OLD device action
        // are not evidence for the new route to the live user. Its receipt and
        // result validity stay available; current movement facts are untouched.
        int fieldCount = last.Count;
        foreach (string name in new[] { "navigationFocusImagePoint", "actualViewOffset", "intentFocusWorldPoint", "trackingFocusWorldPoint",
            "measuredSurface", "actualRoot", "requestedImagePoint" })
            if (last[name] is JObject point && point.Properties().All(p => new[] { "x", "y", "z", "w" }.Contains(p.Name) && p.Value is JValue)) last.Remove(name);
        foreach (string name in new[] { "surfaceId", "focusKind", "targetResolutionSource", "targetPurpose", "targetMatchReason", "matchCaptureId",
            "needsCloserObservation", "observationStandOff", "predictedObservationPitch", "observationGoalId", "viewDirection", "viewPurpose", "viewPurposeStatus",
            "requestedViewDistance", "hasIntentFocus", "navigationFocusAdjusted", "trackingVisibility", "trackingCaptureId", "trackingFocusStatus",
            "trackingConfirmedImages", "trackingMovingConfirmedImages", "trackingLastSeenRealtime", "trackingFocusLastAppliedFrame", "trackingFocusAppliedDegrees",
            "requestedYaw", "requestedPitch", "actualViewChangeDegrees" })
            if (last[name] is JValue) last.Remove(name);
        foreach (string name in new[] { "viewBefore", "viewAfter" })
            if (last[name] is JObject extent && extent.Descendants().OfType<JProperty>().All(p =>
                new[] { "captureId", "entityId", "capturedAtUtc", "frame", "width", "height", "visibleOpaquePixels", "normalizedVisibleBounds",
                    "visiblePixelFraction", "cameraDistanceToObservedPoint", "touchesImageBoundary", "scope", "x", "y", "z", "w" }.Contains(p.Name))) last.Remove(name);
        if (last.Count < fieldCount) omitted["sceneActionFacts.lastAction.completedDeviceGeometry"] = fieldCount - last.Count;
    }

    private static void FocusRoomPlanningEvidence(JObject input)
    {
        if (input["workingMemory"] == null || (bool?)input["quietSpatialGoalStep"] == true) return;
        var archived = new JArray();
        // The general planner chooses the next task. A reviewed, bound task
        // already has a source ID and literal requirements; its frozen dialogue
        // was for resolving identity, not another source of new instructions.
        // The actual search/goal runner still reads the full runtime record.
        foreach (string path in new[] { "spatialGoal", "standingGoal", "sceneSearch", "sceneActionFacts.sceneSearch" })
        {
            if (!(input.SelectToken(path) is JObject task)) continue;
            bool bound = !string.IsNullOrEmpty((string)task["boundEntityId"]);
            bool resolvedSearch = (bool?)task["active"] == false && !string.IsNullOrEmpty((string)task["foundEntityId"]);
            if ((bound || resolvedSearch) && task["referenceContext"] is JObject context &&
                RoomWorkingMemoryKnownArchiveField("referenceContext", context))
            {
                task.Remove("referenceContext");
                archived.Add(path + ".referenceContext");
            }
        }
        foreach (string path in new[] { "visibleObjectCatalog.current.candidates", "visibleObjectCatalog.memory", "sceneSpatialMemory.objects" })
            if (input.SelectToken(path) is JArray rows)
                foreach (var row in rows.OfType<JObject>())
                {
                    if (RoomDecisionUnknownObservationFields(row)) continue;
                    // Frozen point, image point, identity uncertainty and current
                    // body/camera bearings remain. Renderer bounds/neighbours
                    // are detail for independent object matching, not a route.
                    foreach (string field in new[] { "geometryNeighbor", "observedRendererBoundsCenter", "observedRendererBoundsSize",
                        "cameraPositionAtCapture", "cameraRotationAtCapture", "bodyPositionAtCapture", "bodyRotationAtCapture", "viewDirectionWorldAtCapture",
                        "localHierarchy", "markerAnchorPixel", "markerPlacement", "markerPixelBounds" })
                        row.Remove(field);
                }
        input["planningAttention"] = new JObject {
            ["phase"] = "general-task-selection", ["archivedReferences"] = archived,
            ["engineMemoryMutated"] = false,
            ["scope"] = "Literal requests, user updates, identities, outcome proof, uncertainty, image sample, visible extent, frozen points and current bearings retained. Bound identity dialogue, renderer hierarchy/geometry and annotation layout stay in runtime for dedicated matching. Unknown fields remain. Omission establishes neither absence, support, contact, reachability nor permission."
        };
    }
}
