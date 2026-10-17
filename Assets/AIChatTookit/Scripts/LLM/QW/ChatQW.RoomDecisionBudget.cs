using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public partial class ChatQW
{
    // Separate core text and complete-payload limits. Real paired RGB calibration
    // showed 47,200 backend prompt tokens vs 47,991 estimated text units; adding
    // grammar/image/framing reserves to the SAME text ceiling rejected it twice.
    // First-send total is 61,440 (4,096 below the observed 65,536 slot), recovery
    // total is 45,056. These remain estimates, never tokenizer guarantees.
    private const int RoomDecisionBudget = 49152;
    private const int RoomDecisionRecoveryBudget = 32768;
    private const int RoomDecisionImageReserve = 4096;
    private const int RoomDecisionNonTextHeadroom = 12288;
    private const int RoomDecisionMaximumWireBytes = 2 * 1024 * 1024;
    private const string RoomDecisionProjectionKey = "decisionEvidenceProjection";

    private static JObject MeasureRoomDecisionBudget(string json, int ceiling)
    {
        var wire = ReadRoomTaskRecoveryJson(json);
        long textBytes = 0, textUnits = 0;
        var messageMeasures = new JArray();
        var messages = wire["messages"] as JArray;
        if (messages != null)
            foreach (var message in messages.OfType<JObject>())
            {
                string text = RoomTaskMessageText(message);
                int count = Encoding.UTF8.GetByteCount(text); long units = EstimateRoomDecisionTextUnits(text);
                textBytes += count; textUnits += units;
                messageMeasures.Add(new JObject { ["index"] = messageMeasures.Count, ["role"] = (string)message["role"],
                    ["textUtf8Bytes"] = count, ["estimatedTextUnits"] = units });
            }
        string schemaText = wire["response_format"]?["schema"]?.ToString(Formatting.None) ?? "";
        int schemaBytes = Encoding.UTF8.GetByteCount(schemaText);
        long schemaReserve = EstimateRoomDecisionTextUnits(schemaText);
        int images = wire.SelectTokens("$..image_url.url").Count();
        int output = wire["max_tokens"]?.Type == JTokenType.Integer ? (int)wire["max_tokens"] : 640;
        long estimate = textUnits + schemaReserve + images * (long)RoomDecisionImageReserve + output + 2048;
        int totalCeiling = ceiling + RoomDecisionNonTextHeadroom;
        long nonTextReserve = schemaReserve + images * (long)RoomDecisionImageReserve + output + 2048;
        long effectiveTextBudget = Math.Min(ceiling, totalCeiling - nonTextReserve);
        int bytes = Encoding.UTF8.GetByteCount(json);
        var fields = new JObject();
        var collections = new JObject();
        if (TryFindRoomTaskInput(wire, out _, out var input))
        {
            foreach (string field in RoomDecisionMeasuredFields)
                if (input[field] != null) fields[field] = RoomDecisionValueMeasure(input[field]);
            var other = new JObject();
            foreach (var field in input.Properties())
                if (!RoomDecisionMeasuredFields.Contains(field.Name)) other[field.Name] = field.Value.DeepClone();
            if (other.Count > 0) fields["otherProtectedFields"] = RoomDecisionValueMeasure(other);
            foreach (string path in new[] { "visibleObjectCatalog.current.candidates", "visibleObjectCatalog.memory", "visibleObjectCatalog.historicalViews",
                "sceneCognitionSummary.currentObjects", "sceneCognitionSummary.historicalObjects", "sceneSpatialMemory.dialogueViews", "sceneSpatialMemory.viewSamples",
                "spatialGoal.searchProgress", "spatialGoal.failedCandidates", "spatialGoal.userUpdates", "standingGoal.searchProgress", "standingGoal.failedCandidates", "standingGoal.userUpdates" })
                if (input.SelectToken(path) is JToken value)
                {
                    var measure = RoomDecisionValueMeasure(value);
                    if (value is JArray array) measure["rowCount"] = array.Count;
                    collections[path] = measure;
                }
        }
        return new JObject {
            ["accountingVersion"] = "mixed-text-v2-separate-limits", ["messageText"] = messageMeasures, ["inputFields"] = fields, ["inputCollections"] = collections,
            ["textUtf8Bytes"] = textBytes, ["schemaUtf8Bytes"] = schemaBytes, ["imageCount"] = images,
            ["estimatedTextUnits"] = textUnits, ["schemaSafetyReserve"] = schemaReserve,
            ["previousByteHalfEstimate"] = (textBytes + 1) / 2 + (schemaBytes + 1L) / 2 + images * (long)RoomDecisionImageReserve + output + 2048,
            ["imageReservePerImage"] = RoomDecisionImageReserve, ["completionReserve"] = output,
            ["templateAndSafetyReserve"] = 2048, ["estimatedUnits"] = estimate, ["unitBudget"] = totalCeiling,
            ["textUnitBudget"] = ceiling, ["totalUnitBudget"] = totalCeiling, ["previousSingleTotalUnitBudget"] = ceiling,
            ["nonTextReserve"] = nonTextReserve, ["effectiveTextUnitBudget"] = effectiveTextBudget,
            ["remainingTextUnits"] = effectiveTextBudget - textUnits,
            ["textWithinBudget"] = textUnits <= ceiling, ["totalWithinBudget"] = estimate <= totalCeiling,
            ["wireUtf8Bytes"] = bytes, ["maximumWireUtf8Bytes"] = RoomDecisionMaximumWireBytes,
            ["allowed"] = textUnits <= ceiling && estimate <= totalCeiling && images <= 5 && bytes <= RoomDecisionMaximumWireBytes && output >= 0 && output <= 640,
            ["scope"] = "Estimated admission units, not tokenizer counts: ASCII letters/digits /3, punctuation /2, whitespace /4, CJK 1 each, other Unicode UTF-8 bytes/2; long hexadecimal or mixed alphanumeric runs /2. Native schema is preserved and separately reserved with the same estimator, not claimed to be prompt tokens. Core text ceiling is 49152 (recovery 32768); total including schema reserve, 4096/image, completion and 2048 framing is 61440 (recovery 45056). This explicitly replaces the older 49152/32768 single total ceiling; total allowance is wider, core text remains bounded. Observed slot 65536; actual backend usage remains authoritative." };
    }

    private static long EstimateRoomDecisionTextUnits(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        long twelfths = 0;
        for (int index = 0; index < text.Length; ++index)
        {
            char c = text[index];
            if (c <= 127 && char.IsLetterOrDigit(c))
            {
                int end = index; bool hex = true, letters = false, digits = false;
                while (end < text.Length && text[end] <= 127 && char.IsLetterOrDigit(text[end]))
                {
                    char part = text[end++]; digits |= part >= '0' && part <= '9'; letters |= char.IsLetter(part);
                    hex &= part >= '0' && part <= '9' || part >= 'a' && part <= 'f' || part >= 'A' && part <= 'F';
                }
                int length = end - index;
                // GUID/capture hashes and opaque mixed IDs are less compressible
                // than ordinary prose; never apply an English word ratio to them.
                twelfths += length * (long)((hex && letters && length >= 16 || letters && digits && length >= 64) ? 6 : 4);
                index = end - 1;
            }
            else if (c <= 127) twelfths += char.IsWhiteSpace(c) ? 3 : 6;
            else if (c >= 0x3040 && c <= 0x30ff || c >= 0x3400 && c <= 0x9fff || c >= 0xac00 && c <= 0xd7af) twelfths += 12;
            else
            {
                int length = char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
                twelfths += Encoding.UTF8.GetByteCount(text.Substring(index, length)) * 6L;
                index += length - 1;
            }
        }
        return (twelfths + 11) / 12;
    }

    private static JObject RoomDecisionValueMeasure(JToken value)
    {
        string text = value?.ToString(Formatting.None) ?? "";
        return new JObject { ["characters"] = text.Length, ["utf8Bytes"] = Encoding.UTF8.GetByteCount(text), ["estimatedTextUnits"] = EstimateRoomDecisionTextUnits(text) };
    }

    private static readonly string[] RoomDecisionMeasuredFields = {
        "currentUserText", "latestUserText", "autonomous", "recentDialogue", "currentFacts", "lastActionResults", "execution",
        "lastSceneTargetResult", "currentSupport", "scope", "visualObservation", "visualEvidence", "sceneActionFacts", "visibleKnownObjects",
        "sceneActionPrerequisites", "sceneSpatialMemory", "visibleObjectCatalog", "sceneSearch", "turnSweep", "spatialGoal", "verifiedDeviceHistory",
        "verifiedObservedDeviceHistory", "referentSources", "objectTargetReview", "pendingObservationGoal", "decisionContractFacts", "sceneTime",
        "autonomousIntention", "autonomousContext", "sceneCognitionSummary", "decisionEvidenceProjection", "requestEvidenceTable", "requestEvidenceEncoding",
        "standingGoal", "pendingDeviceRequest", "latestGoalUpdate", "currentRequestSemantics", "workingMemory", "planningHistoryImageCaptureIds",
        "taskSequence", "taskSequenceRequired", "workContinuation", "stepAttention" };

    private static bool TryBudgetRoomDecisionRequest(string json, bool recovery, out string admitted, out JObject diagnostic)
    {
        admitted = json;
        int ceiling = recovery ? RoomDecisionRecoveryBudget : RoomDecisionBudget;
        diagnostic = new JObject { ["phase"] = recovery ? "context-recovery" : "before-first-http", ["projection"] = "none", ["admitted"] = false };
        try
        {
            var initial = MeasureRoomDecisionBudget(json, ceiling); diagnostic["original"] = initial;
            var initialWire = ReadRoomTaskRecoveryJson(json);
            // Historical task telemetry does not need to wait for a budget
            // failure to yield. Apply the established recallable routing index
            // before the expensive main planner, preserving current execution.
            if(TryFindRoomTaskInput(initialWire,out var historyText,out var historyInput) && RestoreRoomDecisionSharedEvidence(historyInput)) {
                bool changed=false;
                foreach(string path in new[]{"previousWorkContext","previousUnfinishedWork","workingMemory.previousUnfinishedWork"})
                    if(historyInput.SelectToken(path) is JObject history) {
                        var index=TaskContinuationContextPolicy.RoutingHistory(history,path);
                        if(index.ToString(Formatting.None).Length+128<history.ToString(Formatting.None).Length) {
                            history.Replace(index);changed=true;
                        }
                    }
                if(changed) {
                    string text=(string)historyText;
                    historyText.Replace(text.Substring(0,text.IndexOf('{'))+historyInput.ToString(Formatting.None));
                    string indexed=initialWire.ToString(Formatting.None);
                    if(TryCompactRoomTaskRequest(indexed,out string compactHistory))indexed=compactHistory;
                    var measure=MeasureRoomDecisionBudget(indexed,ceiling);
                    if((long)measure["estimatedUnits"]<(long)initial["estimatedUnits"]) {
                        json=admitted=indexed;initial=measure;initialWire=ReadRoomTaskRecoveryJson(json);
                        diagnostic["historyProjection"]="recallable-task-index-before-planning";
                    }
                }
            }
            bool workingMemory = TryFindRoomTaskInput(initialWire, out _, out var initialInput) && initialInput["workingMemory"] != null;
            // Optional evidence yields before the hard limit. Mandatory facts may
            // exceed this preference and still use the unchanged admission limit.
            string mode = (string)(initialInput?["attentionState"] as JObject)?["mode"];
            double fraction = mode == "investigate" ? .9 : mode == "dialogue" || mode == "idle" ? .7 : .8;
            long preferred = workingMemory && !recovery ? (long)(ceiling * fraction) : long.MaxValue;
            diagnostic["preferredTextUnits"] = preferred == long.MaxValue ? (long?)null : preferred;
            string best = !recovery && (bool)initial["allowed"] ? json : null;
            JObject bestMeasure = best == null ? null : initial;
            string bestProjection = "none"; JObject bestSummary = null; int? bestRows = null;
            // Cheap exact sharing first; this can be enough without omitting any facts.
            if (TryCompactRoomTaskRequest(json, out string shared))
            {
                var sharedMeasure = MeasureRoomDecisionBudget(shared, ceiling);
                if ((bool)sharedMeasure["allowed"])
                {
                    if (bestMeasure == null || (long)sharedMeasure["estimatedUnits"] < (long)bestMeasure["estimatedUnits"])
                    { best = shared; bestMeasure = sharedMeasure; bestProjection = "lossless-sharing"; }
                    if ((long)sharedMeasure["estimatedTextUnits"] <= preferred)
                    { admitted = shared; diagnostic["projection"] = "lossless-sharing";
                      diagnostic["final"] = sharedMeasure; diagnostic["admitted"] = true; return true; }
                }
            }
            if (!recovery && (bool)initial["allowed"] && (long)initial["estimatedTextUnits"] <= preferred)
            { diagnostic["final"] = initial.DeepClone(); diagnostic["admitted"] = true; return true; }
            var originalWire = ReadRoomTaskRecoveryJson(json);
            if (!TryFindRoomTaskInput(originalWire, out _, out var originalInput))
            { diagnostic["reason"] = "missing-structured-decision-input"; diagnostic["final"] = initial.DeepClone(); return false; }
            if (!RestoreRoomDecisionSharedEvidence(originalInput))
            { diagnostic["reason"] = "invalid-shared-evidence"; diagnostic["final"] = initial.DeepClone(); return false; }
            if (originalInput[RoomDecisionProjectionKey] != null &&
                (string)(originalInput[RoomDecisionProjectionKey] as JObject)?["producer"] != "room-decision-request-budget-v1")
            { diagnostic["reason"] = "reserved-projection-field-collision"; diagnostic["final"] = initial.DeepClone(); return false; }

            foreach (int optionalRows in recovery ? new[] { 4, 2, 0 } : new[] { 12, 6, 2, 0 })
            {
                var wire = ReadRoomTaskRecoveryJson(json);
                if (!TryFindRoomTaskInput(wire, out var textToken, out _)) break;
                var projected = ProjectRoomDecisionInput(originalInput, optionalRows);
                if (workingMemory)
                    ApplySceneAttention(new[] { projected }, optionalRows,
                        Math.Min(SceneAttentionDetailUnits, optionalRows * 300));
                string text = (string)textToken;
                textToken.Replace(text.Substring(0, text.IndexOf('{')) + projected.ToString(Formatting.None));
                string candidate = wire.ToString(Formatting.None);
                if (TryCompactRoomTaskRequest(candidate, out shared)) candidate = shared;
                var measured = MeasureRoomDecisionBudget(candidate, ceiling);
                diagnostic["final"] = measured;
                diagnostic["projection"] = "structured-evidence";
                diagnostic["optionalHistoryRowsPerCollection"] = optionalRows;
                diagnostic["summary"] = projected[RoomDecisionProjectionKey].DeepClone();
                bool meaningfullyReduced = Encoding.UTF8.GetByteCount(json) - Encoding.UTF8.GetByteCount(candidate) >= 1024;
                if ((bool)measured["allowed"] && (!recovery || meaningfullyReduced))
                {
                    if (bestMeasure == null || (long)measured["estimatedUnits"] < (long)bestMeasure["estimatedUnits"])
                    { best = candidate; bestMeasure = measured; bestProjection = "structured-evidence";
                      bestSummary = projected[RoomDecisionProjectionKey] as JObject; bestRows = optionalRows; }
                    if ((long)measured["estimatedTextUnits"] <= preferred)
                    { admitted = candidate; diagnostic["admitted"] = true; return true; }
                }
            }
            if (best != null)
            {
                admitted = best; diagnostic["admitted"] = true; diagnostic["final"] = bestMeasure;
                diagnostic["projection"] = bestProjection; diagnostic["summary"] = bestSummary;
                diagnostic["optionalHistoryRowsPerCollection"] = bestRows;
                diagnostic["preferenceExceededByProtectedEvidence"] = true; return true;
            }
            diagnostic["reason"] = "protected-input-and-reserved-payload-exceed-budget";
            // The rejected candidate is not returned; callers cannot accidentally send it.
            return false;
        }
        catch (JsonException)
        { diagnostic["reason"] = "invalid-decision-json"; return false; }
    }

    private static bool RestoreRoomDecisionSharedEvidence(JObject input)
    {
        if (input["requestEvidenceTable"] == null)
            return !input.Descendants().OfType<JProperty>().Any(p => p.Name == "requestEvidenceRef" || p.Name == "requestEvidenceEncoding");
        if (!(input["requestEvidenceTable"] is JObject table) || input["requestEvidenceEncoding"]?.Type != JTokenType.String ||
            !((string)input["requestEvidenceEncoding"]).StartsWith("Lossless shared read-only facts:", StringComparison.Ordinal) ||
            table.Descendants().OfType<JProperty>().Any(p => p.Name == "requestEvidenceRef")) return false;
        foreach (var reference in input.Descendants().OfType<JObject>().Where(value => value["requestEvidenceRef"] != null).ToArray())
        {
            if (reference.Count != 1 || reference["requestEvidenceRef"].Type != JTokenType.String || table[(string)reference["requestEvidenceRef"]] == null) return false;
            reference.Replace(table[(string)reference["requestEvidenceRef"]].DeepClone());
        }
        input.Remove("requestEvidenceTable"); input.Remove("requestEvidenceEncoding");
        return true;
    }

    private static JObject ProjectRoomDecisionInput(JObject source, int optionalRows)
    {
        var result = (JObject)source.DeepClone();
        var changes = new JArray();
        // Old tasks resolve referents; they are not the current executor plan.
        // Preserve their requests, outcomes and recall source instead of sending
        // the complete interrupted pose/readback archive on every new decision.
        foreach(var field in result.Descendants().OfType<JProperty>().ToArray())
            if(new[]{"previousUnfinishedWork","previousWorkContext"}.Contains(field.Name) && field.Value is JObject history) {
                int before=history.ToString(Formatting.None).Length;
                field.Value=TaskContinuationContextPolicy.RoutingHistory(history,field.Path);
                changes.Add(new JObject{["path"]=field.Path,["operation"]="historical-task-source-index",
                    ["beforeCharacters"]=before,["afterCharacters"]=field.Value.ToString(Formatting.None).Length});
            }
        // Only named observation/diagnostic collections below are projected. Unknown
        // top-level fields, exact speech, authority, route/support facts, active goals
        // and schema facts stay intact; if those do not fit, admission fails locally.
        var priorityEntities = new HashSet<string>(StringComparer.Ordinal);
        foreach (string field in new[] { "spatialGoal", "standingGoal", "pendingDeviceRequest", "pendingObservationGoal", "objectTargetReview", "referentSources", "visibleKnownObjects" })
            CollectRoomDecisionEntityReferences(source[field], priorityEntities);
        CollectRoomDecisionEntityReferences(((source["sceneActionFacts"] as JObject)?["objectContext"] as JObject)?["selected"], priorityEntities);
        // Prove duplicated cognition against COMPLETE source rows, before selecting
        // optional history. Otherwise dropping a catalog row resurrects it through
        // the cognition block. Extra pages and any unproved annotations stay intact.
        ProjectRoomDecisionCognition(result, changes, priorityEntities);
        foreach (string path in new[] { "visibleObjectCatalog.current.candidates", "visibleObjectCatalog.memory", "sceneSpatialMemory.objects",
            "sceneActionFacts.objectContext.recentObservations", "sceneActionFacts.objectContext.knownObjectHistory" })
        {
            if (!(result.SelectToken(path) is JArray rows)) continue;
            int before = rows.Count;
            bool current = path == "visibleObjectCatalog.current.candidates";
            var ordered = rows.Select((row, index) => new { Row = row, Index = index, Priority = RoomDecisionEvidencePriority(row, priorityEntities) })
                .OrderByDescending(item => item.Priority).ThenBy(item => item.Index).ToArray();
            // All current candidate pixels and all referenced/bound entities are
            // mandatory. Remaining history is selected structurally, never by words.
            var selected = current ? ordered.Select(item => item.Index).ToHashSet() : ordered
                .Where(item => item.Priority >= 100).Select(item => item.Index).Concat(ordered.Where(item => item.Priority < 100)
                    .Take(optionalRows).Select(item => item.Index)).ToHashSet();
            var kept = new JArray();
            for (int index = 0; index < rows.Count; ++index)
                if (selected.Contains(index)) kept.Add(ProjectRoomDecisionObservationRow(rows[index]));
            rows.Replace(kept);
            changes.Add(new JObject { ["path"] = path, ["sourceCount"] = before, ["retainedCount"] = kept.Count,
                ["omittedCount"] = before - kept.Count, ["allCurrentCandidatesRetained"] = current,
                ["detailProjection"] = "Pixel annotation boxes, renderer bounds/hierarchy and root-transform validation copies omitted. Frozen point, current pixels, names/descriptions, identity/source, current camera/body bearings and all unknown fields retained." });
        }

        foreach (string goalName in new[] { "spatialGoal", "standingGoal" })
        {
            var goal = result[goalName] as JObject;
            if (goal == null) continue;
            // Lossless column encoding removes repeated keys, not old reasons,
            // failures, poses, authorization or user updates. Unknown row shapes
            // remain literal. Paused/unfinished goals need the same treatment as
            // terminal goals and currentRequestOnly's renamed standingGoal.
            foreach (string path in new[] { "searchProgress.completedSteps", "searchProgress.observedViews", "failedCandidates" })
            {
                if (!(goal.SelectToken(path) is JArray rows)) continue;
                var compact = RoomDecisionProgressTable(rows, path);
                if (compact == null) continue;
                int before = Encoding.UTF8.GetByteCount(rows.ToString(Formatting.None));
                int after = Encoding.UTF8.GetByteCount(compact.ToString(Formatting.None));
                if (before <= after) continue;
                rows.Replace(compact);
                changes.Add(new JObject { ["path"] = goalName + "." + path, ["sourceCount"] = rows.Count,
                    ["retainedCount"] = rows.Count, ["sourceUtf8Bytes"] = before, ["retainedUtf8Bytes"] = after,
                    ["replacement"] = "Lossless columns/rows table. Every row value, source, result, reason and constraint remains; reconstruct objects using columns before interpreting. This is historical progress, not new action authority." });
            }
        }
        var previous = source[RoomDecisionProjectionKey] as JObject;
        result[RoomDecisionProjectionKey] = new JObject {
            ["producer"] = "room-decision-request-budget-v1", ["optionalHistoryRowsPerCollection"] = optionalRows,
            ["changes"] = changes, ["priorProjection"] = previous?.DeepClone(), ["engineMemoryMutated"] = false,
            ["scope"] = "This is a request-only projection. Omitted history/diagnostic detail remains in engine memory. Omission never means an entity is absent, unseen, inaccessible or forbidden. Current candidate IDs/pages/captures, attached historical view metadata, bound/pending authority and all current user/route/support facts remain. No image is promoted from historical to current. Equivalent cognition rows follow the SAME catalog history selection; their duplicated copy cannot restore an omitted optional row. If identity cannot be established from retained evidence, preserve uncertainty and use the normal search/confirmation contract, never substitute another target or guess an action." };
        return result;
    }

    private static void ProjectRoomDecisionCognition(JObject result, JArray changes, HashSet<string> priorityEntities)
    {
        // The cognition block repeats the separately structured object catalog. Keep
        // its current/prior self-pose and every unknown summary field explicitly.
        const string marker = "\n[物件与自身方位的观察记录；回答当前对话可直接参考，无需先发起寻物]\n";
        if (result["visualEvidence"]?.Type == JTokenType.String && result["visibleObjectCatalog"] is JObject &&
            result["sceneSpatialMemory"] is JObject && result["sceneCognitionSummary"] == null)
        {
            string visual = (string)result["visualEvidence"];
            int at = visual.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                try
                {
                    var cognition = ReadRoomTaskRecoveryJson(visual.Substring(at + marker.Length));
                    if (cognition["currentObjects"] is JArray currentRows && cognition["historicalObjects"] is JArray historicalRows &&
                        (string)cognition["currentCaptureId"] == (string)(result["visualObservation"] as JObject)?["captureId"])
                    {
                        var canonicalCurrent = result.SelectToken("visibleObjectCatalog.current.candidates") as JArray;
                        var canonicalHistory = result.SelectToken("visibleObjectCatalog.memory") as JArray;
                        // The cognition view may include other catalog pages. Never
                        // mistake its 24-row view for the current six-row page.
                        cognition["currentObjects"] = new JArray(currentRows.Where(row => !RoomDecisionRowRepresented(row, canonicalCurrent, true)).Select(row => row.DeepClone()));
                        cognition["historicalObjects"] = new JArray(historicalRows.Where(row => !RoomDecisionHistoricalCognitionRepresented(row, canonicalHistory)).Select(row => row.DeepClone()));
                        // A differing semantic/permission annotation must remain
                        // alongside its canonical history, not become the only
                        // surviving interpretation after optional selection.
                        CollectRoomDecisionEntityReferences(cognition["historicalObjects"], priorityEntities);
                        cognition["objectEvidenceSource"] = "input.visibleObjectCatalog and input.sceneSpatialMemory; these remain capture-labelled and may have explicitly omitted history entries";
                        result["sceneCognitionSummary"] = cognition;
                        result["visualEvidence"] = visual.Substring(0, at);
                        changes.Add(new JObject { ["path"] = "visualEvidence.cognition-object-arrays", ["sourceCurrentCount"] = currentRows.Count,
                            ["retainedCurrentCount"] = ((JArray)cognition["currentObjects"]).Count,
                            ["sourceHistoricalCount"] = historicalRows.Count, ["retainedHistoricalCount"] = ((JArray)cognition["historicalObjects"]).Count,
                            ["replacement"] = "visibleObjectCatalog + sceneSpatialMemory; cognition selfPose and unknown summary fields retained" });
                    }
                }
                catch (JsonException) { /* An unrecognized visual note is protected, never sliced. */ }
            }
        }

    }

    private static bool RoomDecisionHistoricalCognitionRepresented(JToken row, JArray canonical)
    {
        if (RoomDecisionRowRepresented(row, canonical, false)) return true;
        if (!(row is JObject value) || canonical == null || value["ageSeconds"] == null ||
            (value["ageSeconds"].Type != JTokenType.Float && value["ageSeconds"].Type != JTokenType.Integer) ||
            string.IsNullOrEmpty((string)value["capturedAtUtc"])) return false;
        foreach (var candidate in canonical.OfType<JObject>())
        {
            if (!JToken.DeepEquals(value["capturedAtUtc"], candidate["capturedAtUtc"]) ||
                candidate["ageSecondsAtCognitionRead"] != null) continue;
            // Both producer calls sample elapsed age independently. Do not use a
            // tolerance or throw away either reading: prove all other fields,
            // then retain the cognition reading beside the catalog reading.
            var comparable = (JObject)candidate.DeepClone();
            comparable["ageSeconds"] = value["ageSeconds"].DeepClone();
            if (!RoomDecisionRowRepresented(value, new JArray(comparable), false)) continue;
            candidate["ageSecondsAtCognitionRead"] = value["ageSeconds"].DeepClone();
            return true;
        }
        return false;
    }

    private static JObject RoomDecisionProgressTable(JArray rows, string path)
    {
        if (rows.Count < 3 || rows.Any(row => !(row is JObject))) return null;
        string[] known = path == "searchProgress.completedSteps" ? new[] { "actionId", "intent", "phase", "reason", "captureId", "entityId", "targetPurpose", "observedUtc", "actualRoot", "actualViewChangeDegrees", "mainGoalSatisfied" } :
            path == "searchProgress.observedViews" ? new[] { "captureId", "capturedAtUtc", "capturedAtRealtime", "cameraPosition", "viewDirectionWorld", "purpose", "confirmedBoundEntityIds", "notVisibleBoundEntityIds", "historical", "scope" } :
            new[] { "actionId", "entityId", "captureId", "verdict", "reason", "observedUtc", "scope" };
        var columns = ((JObject)rows[0]).Properties().Select(field => field.Name).ToArray();
        // Same field presence preserves the distinction between absent and null.
        if (columns.Any(name => !known.Contains(name)) || rows.OfType<JObject>().Any(row =>
            row.Count != columns.Length || columns.Any(name => row.Property(name) == null))) return null;
        return new JObject { ["encoding"] = "objects-by-columns-v1: each rows entry reconstructs one object by pairing its values with columns; all values are literal and complete",
            ["columns"] = new JArray(columns), ["rows"] = new JArray(rows.OfType<JObject>().Select(row =>
                new JArray(columns.Select(name => row[name].DeepClone())))) };
    }

    private static void CollectRoomDecisionEntityReferences(JToken value, HashSet<string> ids)
    {
        if (!(value is JContainer container)) return;
        foreach (var property in container.Descendants().OfType<JProperty>())
            if ((property.Name == "entityId" || property.Name == "boundEntityId" || property.Name == "selectedEntityId" ||
                 property.Name == "requestedEntityId") && property.Value.Type == JTokenType.String && !string.IsNullOrEmpty((string)property.Value))
                ids.Add((string)property.Value);
    }

    private static int RoomDecisionEvidencePriority(JToken row, HashSet<string> ids)
    {
        if (!(row is JObject value)) return 100; // Unknown row forms are protected.
        if (RoomDecisionUnknownObservationFields(value)) return 100;
        string entity = (string)value["entityId"];
        if (entity != null && ids.Contains(entity)) return 100;
        if (value.Descendants().OfType<JProperty>().Any(property => property.Name == "actionAuthorization" &&
            property.Value.Type == JTokenType.Boolean && (bool)property.Value)) return 100;
        if (!string.IsNullOrEmpty((string)value["matchedOriginalTarget"])) return 30;
        if (!string.IsNullOrEmpty((string)value["modelDescription"])) return 20;
        if ((bool?)value["positionMemoryValid"] == true) return 10;
        return 0;
    }

    private static readonly HashSet<string> RoomDecisionKnownObservationFields = new HashSet<string>(StringComparer.Ordinal) {
        "referenceId", "sourceKind", "sourceCaptureId", "entityId", "capturedAtUtc", "captureId", "observedUtc", "capturedAtRealtime",
        "authoredHint", "observedRendererBoundsCenter", "observedRendererBoundsSize", "geometryNeighbor", "pointAtCapture",
        "imagePointAtCapture", "imagePoint", "pixelBounds", "markerPixelBounds", "markerAnchorPixel", "markerPlacement", "visiblePixelCount",
        "pageIndex", "imageRegion", "opaquePixelsConfirmed",
        "reviewed", "independentlyReviewedAtThisView", "selectionAllowed", "selectionConstraint", "revisitedByTask",
        "modelDescription", "descriptionCaptureId", "semanticStatus", "matchedOriginalTarget", "targetMatchCaptureId", "positionMemoryValid",
        "relativeToCurrentView", "relativeToCurrentBody", "ageSeconds", "historical", "currentVisibility", "actionAuthorization", "scope",
        "sceneLabel", "identitySource", "requiresReobservation", "semanticIdentityConfirmed", "candidateId", "marker", "selectionId",
        "localHierarchy", "directionFromCurrentPose", "cameraPositionAtCapture", "cameraRotationAtCapture", "bodyPositionAtCapture",
        "bodyRotationAtCapture", "viewDirectionWorldAtCapture", "capabilities", "samplePurpose", "rootPositionAtCapture", "rootRotationAtCapture",
        "rootScaleAtCapture", "positionInvalidReason", "sampleAgeSeconds", "ageSecondsAtCognitionRead", "selfPose", "observationCount" , "bearing", "currentOpaquePixels", "observationFieldsFrom", "inheritedObservationFields" };

    // Current producer keys inside authored hints, geometry, pose, bearing and
    // diagnostic boxes/hierarchy. New nested constraints protect their whole row
    // until their producer semantics are explicitly supported by this projection.
    private static readonly HashSet<string> RoomDecisionKnownObservationChildFields = new HashSet<string>(StringComparer.Ordinal) {
        "x", "y", "z", "w", "width", "height", "source", "rendererName", "entityRootName", "selectedPartName", "selectedRendererType",
        "partToEntityRoot", "pathTruncated", "registeredParts", "name", "componentType", "visibilityNotAsserted",
        "observedPointOffset", "observedPointDistanceMeters", "verticalBoundsGapMeters", "relativeToCurrentCaptureId",
        "yawRelativeToCurrentCameraDegrees", "yawRelativeToCurrentBodyDegrees", "cameraTranslationSinceCaptureMeters",
        "cameraPosition", "cameraRotation", "cameraForward", "hasBodyPose", "bodyPosition", "bodyRotation", "bodyForward",
        "cameraYawRelativeToBodyDegrees", "cameraPitchRelativeToBodyDegreesUp", "sinceHistoricalCapture", "cameraDisplacementWorld",
        "cameraTranslationMeters", "cameraRotationDegrees", "bodyDisplacementWorld", "bodyTranslationMeters", "bodyYawChangeDegreesRight",
        "currentPoseRef",
        "viewCaptureId", "yawDegreesRight", "elevationDegreesUp", "distanceMeters", "yawDegrees", "horizontalDistanceMeters" };

    private static bool RoomDecisionUnknownObservationFields(JObject row) => row.Properties()
        .Any(property => !RoomDecisionKnownObservationFields.Contains(property.Name)) || row.Descendants().OfType<JProperty>()
        .Any(property => !RoomDecisionKnownObservationFields.Contains(property.Name) && !RoomDecisionKnownObservationChildFields.Contains(property.Name));

    private static bool RoomDecisionRowRepresented(JToken row, JArray canonical, bool current)
    {
        if (!(row is JObject value) || canonical == null) return false;
        if (string.IsNullOrEmpty((string)value["entityId"]) || string.IsNullOrEmpty((string)value["sourceCaptureId"])) return false;
        // Unknown annotations can carry additional constraints; retain those rows.
        if (value.Properties().Any(property => !RoomDecisionKnownObservationFields.Contains(property.Name) &&
            property.Name != "bearing" && property.Name != "currentOpaquePixels")) return false;
        foreach (var candidate in canonical.OfType<JObject>())
        {
            if ((string)value["entityId"] != (string)candidate["entityId"] || (string)value["sourceCaptureId"] != (string)candidate["sourceCaptureId"]) continue;
            bool equal = true;
            foreach (var property in value.Properties())
            {
                JToken represented = candidate[property.Name];
                if (property.Name == "currentOpaquePixels" && current) represented = new JValue(true);
                if (property.Name == "actionAuthorization" && current && represented == null &&
                    (bool?)(candidate["authoredHint"] as JObject)?["actionAuthorization"] == false &&
                    (bool?)(candidate["directionFromCurrentPose"] as JObject)?["actionAuthorization"] == false &&
                    !candidate.Descendants().OfType<JProperty>().Any(p => p.Name == "actionAuthorization" && p.Value.Type == JTokenType.Boolean && (bool)p.Value))
                    represented = new JValue(false); // Explicit negative evidence in BOTH producer structures, never a default for missing authority.
                if (property.Name == "bearing")
                {
                    var direction = candidate[current ? "directionFromCurrentPose" : "relativeToCurrentView"] as JObject;
                    represented = direction == null ? null : new JObject {
                        ["cameraYawDegreesRight"] = direction["relativeToCurrentView"]?["yawDegreesRight"]?.DeepClone(),
                        ["cameraElevationDegreesUp"] = direction["relativeToCurrentView"]?["elevationDegreesUp"]?.DeepClone(),
                        ["cameraDistanceMeters"] = direction["relativeToCurrentView"]?["distanceMeters"]?.DeepClone(),
                        ["bodyYawDegreesRight"] = direction["relativeToCurrentBody"]?["yawDegrees"]?.DeepClone(),
                        ["bodyHorizontalDistanceMeters"] = direction["relativeToCurrentBody"]?["horizontalDistanceMeters"]?.DeepClone() };
                }
                // Every semantic/permission value and nested annotation must be
                // proved represented. Matching entity/capture alone is insufficient.
                if (!RoomDecisionEvidenceSubset(property.Value, represented,
                    property.Name == "imagePoint" || property.Name == "pointAtCapture" ? property.Name : "")) { equal = false; break; }
            }
            if (equal) return true;
        }
        return false;
    }

    private static bool RoomDecisionEvidenceSubset(JToken evidence, JToken represented, string coordinatePath = "")
    {
        if (evidence == null || evidence.Type == JTokenType.Null) return represented == null || represented.Type == JTokenType.Null;
        if (evidence is JObject fields && represented is JObject complete)
            return fields.Properties().All(property => RoomDecisionEvidenceSubset(property.Value, complete[property.Name],
                coordinatePath.Length > 0 ? coordinatePath + "." + property.Name : ""));
        // Unity's JsonUtility emits exact double representations of float32 while
        // JObject(float) emits its shortest roundtrip decimal. Equivalence is
        // allowed ONLY for known scalar coordinates AFTER entity/capture match,
        // and only when both JSON numbers round back to the identical finite
        // float32. This is not a distance tolerance and cannot merge different
        // permission flags, IDs, yaw, timestamps, or unknown numeric constraints.
        if ((coordinatePath == "imagePoint.x" || coordinatePath == "imagePoint.y" ||
            coordinatePath == "pointAtCapture.x" || coordinatePath == "pointAtCapture.y" || coordinatePath == "pointAtCapture.z") &&
            evidence?.Type == JTokenType.Float && represented?.Type == JTokenType.Float)
        {
            float a = (float)evidence, b = (float)represented;
            if (!float.IsNaN(a) && !float.IsInfinity(a) && !float.IsNaN(b) && !float.IsInfinity(b) && a.Equals(b)) return true;
        }
        return JToken.DeepEquals(evidence, represented);
    }

    private static JToken ProjectRoomDecisionObservationRow(JToken row)
    {
        var copy = row.DeepClone();
        if (!(copy is JObject value)) return copy;
        if (RoomDecisionUnknownObservationFields(value)) return copy;
        foreach (string name in new[] { "pixelBounds", "markerPixelBounds", "markerAnchorPixel", "markerPlacement", "visiblePixelCount",
            "observedRendererBoundsCenter", "observedRendererBoundsSize", "localHierarchy", "rootPositionAtCapture", "rootRotationAtCapture", "rootScaleAtCapture" }) value.Remove(name);
        return value;
    }
}
