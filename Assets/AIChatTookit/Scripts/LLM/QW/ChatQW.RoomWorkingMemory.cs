using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public partial class ChatQW
{
    private const string RoomWorkingMemoryVersion = "working-memory-v1";

    // A projection of the authoritative controller state, never another task
    // owner/cache. Rebuilding it each decision observes cancellations, new poses,
    // invalidated object positions and current user constraints immediately.
    private static string PrepareRoomPlanningWorkingMemory(string input)
    {
        try
        {
            var source = ReadRoomTaskRecoveryJson(input ?? "{}");
            if ((string)source["planningContextVersion"] != RoomWorkingMemoryVersion ||
                source["workingMemory"] != null) return input;
            return ProjectRoomWorkingMemory(source).ToString(Formatting.None);
        }
        catch (JsonException) { return input; }
    }

    private static JObject ProjectRoomWorkingMemory(JObject source)
    {
        // Existing projection already proves duplicate cognition and retains
        // unknown constraints. Runtime keeps the observations; only a bounded,
        // age/attention-ranked working set enters this planning request.
        var result = ProjectRoomDecisionInput(source, int.MaxValue);
        var changes = new JArray();
        var recallIndex = new JArray();
        var completedTasks = new JObject();
        var activeEntities = new HashSet<string>(StringComparer.Ordinal);
        foreach (string field in new[] { "spatialGoal", "standingGoal" })
        {
            if (!(result[field] is JObject goal)) continue;
            string status = (string)goal["status"];
            bool terminal = status == "satisfied" || status == "cancelled";
            if (!terminal)
            {
                CollectRoomWorkingMemoryActiveBinding(goal, activeEntities);
                continue; // Suspended/failed/unresolved tasks keep their recovery state.
            }
            var archive = new JObject {
                ["goalId"] = goal["goalId"]?.DeepClone(), ["status"] = status,
                ["source"] = "runtime spatial task record; details retained by controller",
                ["newActionAuthorization"] = false
            };
            // Only these known diagnostic/history fields leave terminal task
            // working memory. Original request, result validity, proof, identity,
            // ownership and every unknown field remain literal.
            foreach (string fieldName in new[] { "referenceContext", "searchProgress", "failedCandidates", "userUpdates" })
            {
                var value = goal[fieldName];
                if (value == null) continue;
                if (!RoomWorkingMemoryKnownArchiveField(fieldName, value)) continue;
                archive[fieldName + "Archived"] = true;
                if (value is JArray rows) archive[fieldName + "Count"] = rows.Count;
                goal.Remove(fieldName);
            }
            goal["archivedDetail"] = archive;
            // A completed task is an episode for reference, not the planner's
            // current goal. Keep its literal outcome/identity/unknown fields in
            // a separately labelled archive instead of an active-shaped record.
            completedTasks[field] = goal.DeepClone();
            result[field] = new JObject {
                ["status"] = status, ["goalId"] = goal["goalId"]?.DeepClone(),
                ["archivedContextPath"] = "workingMemory.completedTasks." + field,
                ["newActionAuthorization"] = false,
                ["scope"] = "Terminal historical task only. Full retained outcome is at archivedContextPath. This is not the current request and cannot supply an operation, target or desired state to a new action."
            };
            changes.Add(new JObject { ["path"] = field, ["operation"] = "terminal-task-summary", ["goalId"] = goal["goalId"]?.DeepClone() });
        }
        foreach (string field in new[] { "pendingDeviceRequest", "pendingObservationGoal" })
            CollectRoomWorkingMemoryActiveBinding(result[field] as JObject, activeEntities);
        var attended = SelectRoomWorkingMemoryObjects(result, activeEntities);

        // Each entry keeps its frozen point, capture/source, identity uncertainty,
        // validity and CURRENT body/camera bearing. Observation-specific pose
        // histories stay in runtime, rather than repeated inside every bearing.
        foreach (string path in new[] { "visibleObjectCatalog.current.candidates" })
            if (result.SelectToken(path) is JArray rows)
                foreach (var row in rows.OfType<JObject>()) CompactRoomWorkingMemoryObservation(row);
        foreach (string path in new[] { "visibleObjectCatalog.memory", "sceneSpatialMemory.objects" })
            if (result.SelectToken(path) is JArray memoryRows)
            {
                var detailed = new JArray();
                int optional = 0;
                foreach (var token in memoryRows)
                {
                    if (!(token is JObject row) || RoomDecisionUnknownObservationFields(row) ||
                        row.Descendants().OfType<JProperty>().Any(p => p.Name == "actionAuthorization" && p.Value.Type == JTokenType.Boolean && (bool)p.Value))
                    { detailed.Add(token.DeepClone()); continue; }
                    bool bound = activeEntities.Contains((string)row["entityId"] ?? "");
                    if (!bound && !attended.Contains(RoomWorkingMemoryObjectKey(row))) continue;
                    recallIndex.Add(RoomWorkingMemoryRecallIndex(row));
                    if (bound || RoomWorkingMemoryRecallStrength(row) >= .65 && optional++ < 2)
                    {
                        var detail = (JObject)row.DeepClone();
                        CompactRoomWorkingMemoryObservation(detail);
                        detailed.Add(detail);
                    }
                }
                changes.Add(new JObject { ["path"] = path, ["sourceCount"] = memoryRows.Count, ["retainedDetailCount"] = detailed.Count,
                    ["operation"] = "age/attention-ranked recall, at most 12 ordinary entries across collections; active bindings protected; older records remain in runtime" });
                memoryRows.Replace(detailed);
            }
        if (result.SelectToken("visibleObjectCatalog.historicalViews") is JArray historicalViews)
            foreach (var view in historicalViews.OfType<JObject>())
                if (view["returnDirection"] is JObject direction) CompactRoomWorkingMemoryBearing(direction);

        // The latest sample per entity is sufficient in the planner's recent
        // observation list. Task-specific progress above still records failures
        // and coverage for unfinished tasks. Unknown record shapes are protected.
        CompactRoomWorkingMemoryObservations(result, "sceneActionFacts.objectContext.recentObservations", changes);
        CompactRoomWorkingMemoryViewSamples(result, changes);
        SelectRoomWorkingMemoryViews(result, activeEntities, changes);
        // ProjectRoomDecisionInput's diagnostic is not an ever-growing memory of
        // earlier projections. Keep the latest counts once, outside prior chains.
        result.Remove(RoomDecisionProjectionKey);
        result["workingMemory"] = new JObject {
            ["version"] = RoomWorkingMemoryVersion, ["engineMemoryMutated"] = false,
            ["sourceTextUnits"] = EstimateRoomDecisionTextUnits(source.ToString(Formatting.None)),
            ["changes"] = changes,
            ["completedTasks"] = completedTasks,
            ["recallIndex"] = recallIndex,
            ["ordinaryRecallCapacity"] = 12,
            ["scope"] = "Finite current working state plus age/attention-ranked recall. Source records originate in observed pixels, never a hidden room inventory. Terminal trace is archived; unfinished tasks retain requirements/recovery. Ordinary recall has 12 slots, active bindings are protected. Every index entry is historical, requires reobservation and grants NO action authority. referenceId points to runtime search memory. recallStrength is an attention/retention weight, NOT probability of truth or visibility. Fading records expose only coarse directions; weak records names/time only. Frozen points have explicit validity; currentBearing is measured at visualObservation.captureId (positive yaw right, elevation up; body/camera distinct). Authored names are clues, not recognition or support. Use search-scene / reviewed device-task search to recall/reobserve. Omission never proves absence. Never use old pixels as current or restart terminal tasks."
        };
        result["workingMemory"]["projectedTextUnits"] = EstimateRoomDecisionTextUnits(result.ToString(Formatting.None));
        return result;
    }

    private static bool RoomWorkingMemoryKnownArchiveField(string name, JToken value)
    {
        // Only producer-owned trace schemas may be archived. A future constraint
        // must not vanish just because somebody placed it inside history.
        string[] fields = name == "referenceContext" ? new[] { "quotedDialogue", "scope" } :
            name == "searchProgress" ? new[] { "completedSteps", "stepCount", "observedViews", "scope" } : null;
        if (fields != null && value is JObject obj)
            return obj.Properties().All(p => fields.Contains(p.Name)) &&
                (name != "referenceContext" || obj.Properties().All(p =>
                    p.Name == "scope" && p.Value.Type == JTokenType.String ||
                    p.Name == "quotedDialogue" && p.Value is JArray dialogue && dialogue.All(row => row is JObject quote &&
                        quote.Properties().All(q => new[] { "role", "quotedText", "textTruncated" }.Contains(q.Name) && q.Value is JValue)))) &&
                (name != "searchProgress" || obj.Properties().All(p => p.Name == "scope" || p.Name == "stepCount" ||
                    KnownRoomWorkingMemoryProgress(p.Value, "searchProgress." + p.Name)));
        if (name == "failedCandidates" && value is JArray failures)
            return KnownRoomWorkingMemoryProgress(failures, "failedCandidates");
        // Even terminal user updates may hold constraints for later requests.
        return name == "userUpdates" && value is JArray updates && updates.Count == 0;
    }

    private static bool KnownRoomWorkingMemoryProgress(JToken token, string path)
    {
        JArray rows = token as JArray;
        if (token is JObject table && table["columns"] is JArray columns && table["rows"] is JArray values &&
            table.Properties().All(p => p.Name == "columns" || p.Name == "rows" || p.Name == "encoding") &&
            ((string)table["encoding"] ?? "").StartsWith("objects-by-columns-v1:", StringComparison.Ordinal) &&
            columns.All(c => c.Type == JTokenType.String) && values.All(v => v is JArray a && a.Count == columns.Count))
        {
            rows = new JArray(values.OfType<JArray>().Select(v => new JObject(columns.Select((c, i) => new JProperty((string)c, v[i].DeepClone())))));
        }
        if (rows == null) return false;
        if (rows.Count == 0) return true;
        // The existing schema proof requires at least three homogeneous rows.
        // Check each row against that schema without changing the real history.
        return rows.All(row => row is JObject value &&
            value.Properties().All(p => !(p.Value is JContainer) || p.Value is JArray array && array.All(v => v is JValue) ||
                p.Value is JObject point && point.Properties().All(c => new[] { "x", "y", "z", "w" }.Contains(c.Name) && c.Value is JValue)) &&
            RoomDecisionProgressTable(new JArray(row.DeepClone(), row.DeepClone(), row.DeepClone()), path) != null);
    }

    private static void CollectRoomWorkingMemoryActiveBinding(JObject record, HashSet<string> entities)
    {
        if (record == null) return;
        foreach (string name in new[] { "boundEntityId", "entityId", "requestedEntityId", "selectedEntityId" })
            if (record[name]?.Type == JTokenType.String && !string.IsNullOrEmpty((string)record[name])) entities.Add((string)record[name]);
        if (record["confirmedTarget"] is JObject target) CollectRoomWorkingMemoryActiveBinding(target, entities);
    }

    private static void CompactRoomWorkingMemoryViewSamples(JObject input, JArray changes)
    {
        if (!(input.SelectToken("sceneSpatialMemory.viewSamples") is JArray rows) || rows.Count <= 2) return;
        string[] fields = { "captureId", "capturedAtUtc", "capturedAtRealtime", "purpose", "cameraPosition", "cameraRotation",
            "bodyPosition", "bodyRotation", "viewDirectionWorld", "confirmedKnownEntityIds", "notVisibleKnownEntityIds", "historical", "scope" };
        if (rows.Any(row => !(row is JObject value) || value.Properties().Any(p => !fields.Contains(p.Name)) ||
            value.Descendants().OfType<JProperty>().Any(p => !fields.Contains(p.Name) &&
                !new[] { "x", "y", "z", "w" }.Contains(p.Name)))) return;
        var recent = rows.OfType<JObject>().OrderByDescending(row => (double?)row["capturedAtRealtime"] ?? double.MinValue).Take(2).Reverse().ToArray();
        changes.Add(new JObject { ["path"] = "sceneSpatialMemory.viewSamples", ["sourceCount"] = rows.Count, ["retainedCount"] = recent.Length,
            ["operation"] = "two latest self-motion samples; indexed old viewpoints and unfinished task coverage remain available" });
        rows.Replace(new JArray(recent.Select(row => row.DeepClone())));
    }

    private static void CompactRoomWorkingMemoryObservation(JObject row)
    {
        if (RoomDecisionUnknownObservationFields(row)) return;
        foreach (string name in new[] { "directionFromCurrentPose", "relativeToCurrentView" })
        {
            if (row[name] is JObject bearing) CompactRoomWorkingMemoryBearing(bearing);
        }
    }

    private static JToken RoomWorkingMemoryRecallIndex(JToken token)
    {
        var row = (JObject)token;
        var index = new JObject();
        foreach (string name in new[] { "referenceId", "entityId", "sourceCaptureId", "captureId", "capturedAtUtc", "observedUtc",
            "modelDescription", "matchedOriginalTarget", "semanticStatus", "identitySource", "pointAtCapture", "positionMemoryValid", "positionInvalidReason" })
            if (row[name] != null && row[name].Type != JTokenType.Null) index[name] = row[name].DeepClone();
        var names = new[] { (string)row["sceneLabel"], (string)row["authoredHint"]?["sceneLabel"],
            (string)row["authoredHint"]?["rendererName"], (string)row["authoredHint"]?["entityRootName"] }
            .Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparer.Ordinal);
        index["authoredNames"] = new JArray(names);
        var bearing = row["relativeToCurrentView"] as JObject;
        var camera = bearing?["relativeToCurrentView"] as JObject ?? bearing;
        var body = bearing?["relativeToCurrentBody"] as JObject ?? row["relativeToCurrentBody"] as JObject;
        index["currentBearing"] = new JObject {
            ["viewCaptureId"] = camera?["viewCaptureId"]?.DeepClone(),
            ["cameraYawDegreesRight"] = camera?["yawDegreesRight"]?.DeepClone(),
            ["cameraElevationDegreesUp"] = camera?["elevationDegreesUp"]?.DeepClone(),
            ["bodyYawDegreesRight"] = body?["yawDegrees"]?.DeepClone()
        };
        double strength = RoomWorkingMemoryRecallStrength(row);
        index["recallStrength"] = Math.Round(strength, 3);
        index["recallDetail"] = strength >= .65 ? "clear-historical" : strength >= .2 ? "fading" : "weak";
        if (strength < .65)
        {
            index.Remove("pointAtCapture"); index.Remove("currentBearing");
            if (strength >= .2 && (bool?)row["positionMemoryValid"] == true && body?["yawDegrees"] != null)
            {
                double yaw = (double)body["yawDegrees"];
                if (!double.IsNaN(yaw) && !double.IsInfinity(yaw))
                    index["coarseBodyDirection"] = new[] { "front", "front-right", "right", "back-right", "back", "back-left", "left", "front-left" }
                        [((int)Math.Floor(((yaw % 360 + 360) % 360 + 22.5) / 45)) % 8];
            }
        }
        return index;
    }

    private static string RoomWorkingMemoryObjectKey(JObject row)
        => (string)row["entityId"] + "|" + ((string)row["sourceCaptureId"] ?? (string)row["captureId"]);

    private static double RoomWorkingMemoryRecallStrength(JObject row)
    {
        double age = (double?)row["ageSeconds"] ?? (double?)row["sampleAgeSeconds"] ?? 0;
        if (double.IsNaN(age) || double.IsInfinity(age) || age < 0) return 0;
        double observations = Math.Max(1, Math.Min(5, (int?)row["observationCount"] ?? 1));
        double attention = !string.IsNullOrEmpty((string)row["matchedOriginalTarget"]) ? 1.5 :
            !string.IsNullOrEmpty((string)row["modelDescription"]) ? 1.2 : 1;
        return Math.Min(1, Math.Pow(.5, age / (90 * (1 + .5 * (observations - 1)))) * attention);
    }

    private static HashSet<string> SelectRoomWorkingMemoryObjects(JObject input, HashSet<string> activeEntities)
    {
        var rows = new List<JObject>();
        foreach (string path in new[] { "visibleObjectCatalog.memory", "sceneSpatialMemory.objects" })
            if (input.SelectToken(path) is JArray values) rows.AddRange(values.OfType<JObject>());
        return new HashSet<string>(rows.Where(row => !activeEntities.Contains((string)row["entityId"] ?? "") &&
                !RoomDecisionUnknownObservationFields(row) && RoomWorkingMemoryRecallStrength(row) >= .06)
            .OrderByDescending(RoomWorkingMemoryRecallStrength).ThenBy(RoomWorkingMemoryObjectKey, StringComparer.Ordinal)
            .Select(RoomWorkingMemoryObjectKey).Distinct(StringComparer.Ordinal).Take(12), StringComparer.Ordinal);
    }

    private static void CompactRoomWorkingMemoryBearing(JObject bearing)
    {
        // The scalars and their capture namespaces are unchanged. Drop only
        // expanded poses already represented by current selfPose and the frozen
        // point. Unknown extensions keep the full bearing intact.
        if (bearing.Descendants().OfType<JProperty>().Any(p => !RoomDecisionKnownObservationFields.Contains(p.Name) &&
            !RoomDecisionKnownObservationChildFields.Contains(p.Name))) return;
        bearing.Remove("selfPose");
        bearing.Remove("scope");
        bearing.Remove("bodyPosition");
        bearing.Remove("bodyForward");
        foreach (string name in new[] { "relativeToCurrentView", "relativeToCurrentBody" })
            if (bearing[name] is JObject inner) CompactRoomWorkingMemoryBearing(inner);
    }

    private static void CompactRoomWorkingMemoryObservations(JObject input, string path, JArray changes)
    {
        if (!(input.SelectToken(path) is JArray rows)) return;
        var latest = new Dictionary<string, JObject>(StringComparer.Ordinal);
        var protectedRows = new List<JToken>();
        foreach (var row in rows)
        {
            if (!(row is JObject value) || RoomDecisionUnknownObservationFields(value) ||
                string.IsNullOrEmpty((string)value["entityId"]) || string.IsNullOrEmpty((string)value["observedUtc"]))
            { protectedRows.Add(row); continue; }
            string entity = (string)value["entityId"];
            if (!latest.TryGetValue(entity, out var previous) ||
                string.CompareOrdinal((string)value["observedUtc"], (string)previous["observedUtc"]) > 0) latest[entity] = value;
        }
        var kept = new HashSet<JToken>(protectedRows.Concat(latest.Values), ReferenceEqualityComparerForRoomMemory.Instance);
        var replacement = new JArray(rows.Where(kept.Contains).Select(row => row.DeepClone()));
        changes.Add(new JObject { ["path"] = path, ["sourceCount"] = rows.Count, ["retainedCount"] = replacement.Count,
            ["operation"] = "latest-observation-per-entity; older frames retained in runtime" });
        rows.Replace(replacement);
    }

    private sealed class ReferenceEqualityComparerForRoomMemory : IEqualityComparer<JToken>
    {
        public static readonly ReferenceEqualityComparerForRoomMemory Instance = new ReferenceEqualityComparerForRoomMemory();
        public bool Equals(JToken a, JToken b) => ReferenceEquals(a, b);
        public int GetHashCode(JToken value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }

    private static void SelectRoomWorkingMemoryViews(JObject input, HashSet<string> activeEntities, JArray changes)
    {
        // Generic descriptive goals are delegated to the existing search runner,
        // which can retrieve historical RGB by catalog reference. Do not eagerly
        // attach those images to every subsequent navigation decision.
        var captures = new HashSet<string>(StringComparer.Ordinal);
        if (input.SelectToken("visibleObjectCatalog.memory") is JArray memory)
            foreach (var row in memory.OfType<JObject>())
                if (activeEntities.Contains((string)row["entityId"] ?? "") && captures.Count < 1 &&
                    !string.IsNullOrEmpty((string)row["sourceCaptureId"])) captures.Add((string)row["sourceCaptureId"]);
        if ((bool?)input.SelectToken("turnSweep.available") == true && input.SelectToken("turnSweep.frames") is JArray frames)
            foreach (var row in frames.OfType<JObject>().Reverse())
                if (captures.Count < 1 && !string.IsNullOrEmpty((string)row["captureId"])) captures.Add((string)row["captureId"]);
        input["planningHistoryImageCaptureIds"] = new JArray(captures.OrderBy(id => id, StringComparer.Ordinal));
        changes.Add(new JObject { ["path"] = "history-image-attachments", ["selectedCount"] = captures.Count,
            ["operation"] = "at-most-one-active-reference; all historical view indexes remain retrievable by search" });
    }

    private static bool RoomWorkingMemoryAllowsImage(JObject input, string captureId)
        => !(input?["planningHistoryImageCaptureIds"] is JArray selected) ||
           selected.Any(value => (string)value == captureId);

    private static bool CompactRoomWorkingMemoryIndex(JObject input)
    {
        if (!(input.SelectToken("workingMemory.recallIndex") is JArray rows) || rows.Count < 3 || rows.Any(row => !(row is JObject))) return false;
        var columns = rows.OfType<JObject>().SelectMany(row => row.Properties().Select(p => p.Name)).Distinct(StringComparer.Ordinal).ToArray();
        rows.Replace(new JObject {
            ["encoding"] = "recall-index-columns-v1: pair each row with columns; null means no recorded value; all entries are historical hints, not permissions. Resolve requestEvidenceRef values using requestEvidenceTable.",
            ["columns"] = new JArray(columns),
            ["rows"] = new JArray(rows.OfType<JObject>().Select(row => new JArray(columns.Select(name => row[name]?.DeepClone() ?? JValue.CreateNull()))))
        });
        return true;
    }
}
