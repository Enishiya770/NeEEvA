using System;
using System.Linq;
using Newtonsoft.Json.Linq;

/// <summary>Consumer views of engine records. Never changes task ownership or completion.</summary>
public static class RoomTaskContextPolicy
{
    // The target anchor of a NEW proposal belongs to the complete CURRENT
    // request. Historical entity selection is a separate independent review.
    // This prevents copying an old imperative into a new operation. Long or
    // unknown requests remain unconstrained: never truncate human constraints.
    public static string AnchorNewProposalSchema(string schema, JObject input)
    {
        string current = (string)input?["currentUserText"];
        if ((bool?)input?["autonomous"] != false || string.IsNullOrWhiteSpace(current) || current.Length > 240 ||
            (bool?)input?["currentRequestOnly"] == true) return schema;
        var result = JObject.Parse(schema);
        foreach (var property in result.Descendants().OfType<JProperty>().Where(p => p.Name == "targetDescription" || p.Name == "requestQuote" && current.Length <= 160))
            if (property.Value is JObject definition && (string)definition["type"] == "string")
                definition["enum"] = new JArray(current);
        return result.ToString(Newtonsoft.Json.Formatting.None);
    }

    public static bool PlanningFailureReview(JObject input) => input?["decisionFailure"] is JObject failure &&
        (bool?)failure["newActionSubmittedByThisDecision"] == false &&
        new[] { "input-budget-exceeded", "service-request-failed", "decision-not-executable" }.Contains((string)failure["kind"]) &&
        new[] { "action", "control" }.Contains((string)input.SelectToken("currentRequestSemantics.requestKind")) &&
        string.IsNullOrWhiteSpace((string)input.SelectToken("currentRequestSemantics.currentTurnEvidence.answerQuote"));

    public static JObject ProjectPlanningFailureReview(JObject source)
    {
        var result = (JObject)source.DeepClone();
        if (!PlanningFailureReview(result)) return result;
        // This reply only reports the current planning failure. Keep all human
        // evidence, owned receipts, previous reply evidence and unknown fields.
        // No omitted scene data may justify an extra world-state claim.
        foreach (string field in new[] { "attentionState", "visualObservation", "visualEvidence", "sceneActionFacts",
            "spatialReplyFacts", "sceneSearchReportRequirement", "deviceGoalFacts", "turnSweep", "historicalDeviceEvents" }) result.Remove(field);
        result["reviewAttention"] = "current-planning-failure-receipt";
        result["scope"] = "Only current decisionFailure and no-new-action status are reportable here. Omitted scene facts supply no support for additional world claims. Human questions/conditions and unknown fields remain; no action or retry is authorized.";
        return result;
    }

    public static JObject PlanningObservationView(JObject source, Func<JObject, bool> protectObjectRow = null)
    {
        var result = DeliveryReceipt(source);
        if (!(result["visibleObjectCatalog"] is JObject catalog)) return result;
        var envelope = new JObject { ["visibleCatalog"] = catalog.DeepClone() };
        // Exact same-capture pose metadata is repeated on every candidate/view.
        // Share only equal values; historical deltas stay with their own row.
        JObject shared=null;
        if(result["planningCatalogPose"]==null && !catalog.Descendants().OfType<JProperty>().Any(p=>p.Name=="currentPoseRef")) {
            var pose=catalog.Descendants().OfType<JProperty>().FirstOrDefault(p=>p.Name=="selfPose" && p.Value is JObject o &&
                o["captureId"]?.Type==JTokenType.String && o["capturedAtUtc"]?.Type==JTokenType.String)?.Value as JObject;
            if(pose!=null) { shared=Fields(pose,"captureId","capturedAtUtc","hasBodyPose","cameraYawRelativeToBodyDegrees","cameraPitchRelativeToBodyDegreesUp");envelope["selfPose"]=shared.DeepClone(); }
        }
        var projected = SceneSearchContextPolicy.Project(envelope, out _);
        if(shared!=null) {
            foreach(var reference in ((JObject)projected["visibleCatalog"]).Descendants().OfType<JProperty>().Where(p=>p.Name=="currentPoseRef" && (string)p.Value=="selfPose"))
                reference.Value="planningCatalogPose";
            result["planningCatalogPose"]=shared;
        }
        // Unknown authored constraints can depend on sibling geometry/diagnostics.
        // Preserve the entire protected object record before any budget projection.
        if(protectObjectRow!=null)
            foreach(string path in new[]{"current.candidates","memory"})
                if(catalog.SelectToken(path) is JArray originalRows && projected["visibleCatalog"].SelectToken(path) is JArray projectedRows)
                    for(int i=0;i<originalRows.Count;i++)
                        if(originalRows[i] is JObject row && protectObjectRow(row))projectedRows[i]=row.DeepClone();
        result["visibleObjectCatalog"] = projected["visibleCatalog"].DeepClone();
        SharePlanningObservations(result,protectObjectRow);
        result["planningObservationScope"] = result["planningObservationSharing"]==null ? "All catalog entities, captures, names, constraints, frozen world points, current pixels and derived bearings remain. currentPoseRef=planningCatalogPose supplies exactly equal current capture/time/relative-angle metadata; each historical delta remains inline. Redundant raw renderer/pose diagnostics stay in Unity. Historical pixels are not current pixels. The executor independently checks geometry, route, identity and permission; engine memory is unchanged." : "All catalog entities, captures, names, constraints, frozen world points, current pixels and derived bearings remain. currentPoseRef=planningCatalogPose supplies exactly equal current capture/time/relative-angle metadata; each historical delta remains inline. observationFieldsFrom copies ONLY fields named in inheritedObservationFields from that earlier input path; all other fields remain inline. This is exact sharing of the SAME entity AND capture, never a new observation or authority. Redundant raw renderer/pose diagnostics stay in Unity. Historical pixels are not current pixels. The executor independently checks geometry, route, identity and permission; engine memory is unchanged.";
        return result;
    }

    private static void SharePlanningObservations(JObject input,Func<JObject,bool> protectedRow)
    {
        int originalCharacters=input.ToString(Newtonsoft.Json.Formatting.None).Length;
        var changed=new System.Collections.Generic.List<(JArray rows,int index,JObject original)>();
        var canonical=new System.Collections.Generic.Dictionary<string,(string path,JObject value)>();
        var diagnostics=new JArray();
        foreach(string path in new[]{"visibleObjectCatalog.current.candidates","visibleObjectCatalog.memory","sceneSpatialMemory.objects",
            "sceneActionFacts.objectContext.recentObservations","sceneActionFacts.objectContext.knownObjectHistory"}) {
            if(!(input.SelectToken(path) is JArray rows))continue;
            for(int i=0;i<rows.Count;i++) {
                if(!(rows[i] is JObject row)||row["observationFieldsFrom"]!=null||protectedRow?.Invoke(row)==true)continue;
                string entity=(string)row["entityId"],capture=(string)row["sourceCaptureId"];
                if(string.IsNullOrEmpty(entity)||string.IsNullOrEmpty(capture))continue;
                string key=entity+"\n"+capture,sourcePath=path+"["+i+"]";
                if(!canonical.TryGetValue(key,out var first)){canonical[key]=(sourcePath,(JObject)row.DeepClone());continue;}
                var shared=row.Properties().Where(p=>!new[]{"entityId","sourceCaptureId","referenceId","candidateId","selectionId"}.Contains(p.Name)&&
                    first.value[p.Name]!=null&&JToken.DeepEquals(first.value[p.Name],p.Value)).Select(p=>p.Name).ToArray();
                if(shared.Length==0)continue;
                var projected=(JObject)row.DeepClone();foreach(string field in shared)projected.Remove(field);
                projected["observationFieldsFrom"]=first.path;projected["inheritedObservationFields"]=new JArray(shared);
                var restored=(JObject)projected.DeepClone();restored.Remove("observationFieldsFrom");restored.Remove("inheritedObservationFields");
                foreach(string field in shared)restored[field]=first.value[field].DeepClone();
                if(!JToken.DeepEquals(ActivityEvidencePolicy.Canonical(row),ActivityEvidencePolicy.Canonical(restored)))continue;
                int saved=row.ToString(Newtonsoft.Json.Formatting.None).Length-projected.ToString(Newtonsoft.Json.Formatting.None).Length;
                if(saved<128)continue;
                changed.Add((rows,i,(JObject)row.DeepClone()));rows[i]=projected;diagnostics.Add(new JObject{["path"]=sourcePath,["source"]=first.path,["savedCharacters"]=saved,["roundTripVerified"]=true});
            }
        }
        if(diagnostics.Count>0) {
            input["planningObservationSharing"]=diagnostics;
            // Include diagnostic/reference explanation overhead. Tiny repeated
            // rows must never make the final planning packet larger.
            if(input.ToString(Newtonsoft.Json.Formatting.None).Length+500>=originalCharacters) {
                foreach(var edit in changed)edit.rows[edit.index]=edit.original;
                input.Remove("planningObservationSharing");
            }
        }
    }

    // Keep the exact old human constraints, but do not present a past command as
    // the target description/state of the next proposed operation.
    public static JObject HistoricalReferent(JObject source)
    {
        if (source == null) return null;
        var result = (JObject)source.DeepClone();
        foreach (var pair in new[] { new[] { "originalRequest", "historicalUserRequest" },
            new[] { "targetDescription", "historicalTargetConstraint" }, new[] { "desiredState", "historicalRequestedState" } })
        {
            if (result[pair[0]] == null || result[pair[1]] != null) continue;
            result[pair[1]] = result[pair[0]].DeepClone(); result.Remove(pair[0]);
        }
        result["historicalOnly"] = true;
        result["historicalReferenceScope"] = "Historical identity/event evidence only. historicalUserRequest and historicalTargetConstraint may contain OLD actions, negation and subsequent steps; preserve them to resolve the old entity, never copy them as the new operation. historicalRequestedState is that earlier request, not this turn's desired state. A new operation requires the complete current human request and independent review; fresh perception and execution checks remain required.";
        return result;
    }

    public static JObject DeviceProposalReview(JObject source)
    {
        var result = (JObject)source.DeepClone();
        if ((string)result["mode"] != "new-proposal") return result;
        foreach (string field in new[] { "verifiedDeviceHistory", "verifiedObservedDeviceHistory", "referentSources" })
            if (result[field] is JArray rows)
                result[field] = new JArray(rows.Select(row => row is JObject obj ? HistoricalReferent(obj) : row.DeepClone()));
        if (result["existingGoal"] is JObject goal && new[] { "satisfied", "cancelled" }.Contains((string)goal["status"]) &&
            result["historicalExistingGoal"] == null)
        {
            result["historicalExistingGoal"] = HistoricalReferent(goal);
            result.Remove("existingGoal");
        }
        return result;
    }

    public static bool AutonomousBodyAttention(JObject input)
    {
        var attention = input?["autonomousAttention"] as JObject;
        return (bool?)input?["autonomous"] == true &&
            (string)attention?["source"] == "current-private-intent-review" &&
            (bool?)attention?["actionAuthorization"] == false &&
            (string)attention?["kind"] == "current-user-body" &&
            attention?["workEpoch"]?.Type == JTokenType.Integer && attention?["userTurn"]?.Type == JTokenType.Integer &&
            !string.IsNullOrWhiteSpace((string)input["autonomousIntention"]) &&
            (string)attention["intention"] == (string)input["autonomousIntention"];
    }

    public static bool InitialUserBodyAttention(JObject input)
    {
        var semantics = input?["currentRequestSemantics"] as JObject;
        return (bool?)input?["autonomous"] == false &&
            (string)semantics?["source"] == "independent-first-reply-current-request-review" &&
            (bool?)semantics["actionAuthorization"] == false &&
            new[] { "action", "control" }.Contains((string)semantics["requestKind"]) &&
            (string)semantics.SelectToken("planningFocus.kind") == "current-user-body" &&
            !string.IsNullOrWhiteSpace((string)input["currentUserText"]) &&
            (string)semantics.SelectToken("planningFocus.currentUserText") == (string)input["currentUserText"];
    }

    public static bool ContinuedBodyAttention(JObject input)
    {
        var attention = input?["autonomousAttention"] as JObject;
        var work = input?["workContinuation"] as JObject;
        return (bool?)input?["autonomous"] == false &&
            (string)work?["source"] == "approved-same-user-work-next-step" &&
            (string)attention?["source"] == "current-private-intent-review" &&
            (bool?)attention?["actionAuthorization"] == false &&
            (string)attention?["kind"] == "current-user-body" &&
            (int?)attention?["workEpoch"] == (int?)work?["workEpoch"] &&
            (int?)attention?["userTurn"] == (int?)work?["userTurn"] &&
            !string.IsNullOrWhiteSpace((string)work?["nextStepIntention"]) &&
            (string)attention?["intention"] == (string)work?["nextStepIntention"] &&
            (string)work?["originalRequest"] == (string)input?["currentUserText"];
    }

    public static bool InitialUserViewAttention(JObject input)
    {
        var semantics=input?["currentRequestSemantics"] as JObject;
        return (bool?)input?["autonomous"]==false &&
            (string)semantics?["source"]=="independent-first-reply-current-request-review" &&
            (bool?)semantics["actionAuthorization"]==false &&
            new[]{"action","control"}.Contains((string)semantics["requestKind"]) &&
            (string)semantics.SelectToken("planningFocus.kind")=="current-view" &&
            !string.IsNullOrWhiteSpace((string)input["currentUserText"]) &&
            (string)semantics.SelectToken("planningFocus.currentUserText")== (string)input["currentUserText"];
    }

    public static JObject DeviceEvent(JObject receipt)
    {
        if (receipt == null) return null;
        var result = Fields(receipt, "recordedGeneration", "recordedAtUtc", "sourceUserTurn", "sourceWorkEpoch");
        if (receipt["goal"] is JObject sourceGoal)
        {
            var goal = Fields(sourceGoal, "goalId", "status", "reason", "originalRequest", "targetDescription", "desiredState", "boundEntityId", "completionEvidenceValid");
            goal["operationSummary"] = Fields(sourceGoal["operationSummary"] as JObject, "operationAttempted", "stateWriteObserved", "lastOperationPhase");
            if (sourceGoal["completionEvidence"] is JObject sourceProof)
            {
                var proof = Fields(sourceProof, "actionId", "entityId", "desiredState", "observedUtc", "resultCaptureId");
                proof["actual"] = sourceProof["actual"]?.DeepClone();
                proof["historicalOnly"] = true;
                goal["completionEvidence"] = proof;
            }
            result["goal"] = goal;
        }
        result["lastAction"] = Fields(receipt["lastAction"] as JObject, "actionId", "intent", "entityId", "requestedState", "phase", "stateChanged", "observedUtc", "reason");
        result["historicalResult"] = true;
        result["scope"] = "Past engine event only, not current device state/visibility or action permission. Preserve entity, original request, time, outcome and operationAttempted; null means unknown. A later event for the same entity can supersede its state, not erase that it happened.";
        return result;
    }

    public static JObject DeliveryReceipt(JObject source)
    {
        if (source == null) return null;
        var result = (JObject)source.DeepClone();
        // Delivery decides whether an observed result was actually explained.
        // Pose/render diagnostics cannot establish that. Keep state, causes,
        // entity/action/step/time, distances, readback and all unknown constraints.
        foreach (var property in result.Descendants().OfType<JProperty>().ToArray())
        {
            if (!(property.Value is JObject node)) continue;
            if (property.Name == "lastAction")
                foreach (string key in new[] { "viewBefore", "viewAfter", "requestedImagePoint", "navigationFocusImagePoint",
                    "actualViewOffset", "intentFocusWorldPoint", "trackingFocusWorldPoint", "measuredSurface", "actualRoot" })
                    if (NumericDiagnostic(node[key])) node.Remove(key);
            if (property.Name == "sceneSearch" && node["receipts"] is JArray receipts)
            {
                // Keep every receipt's semantics and failure, just omit raw pose.
                foreach (var row in receipts.OfType<JObject>())
                    foreach (string key in new[] { "viewBefore", "viewAfter", "actualRoot", "trackingFocusWorldPoint" })
                        if (NumericDiagnostic(row[key])) row.Remove(key);
            }
        }
        return result;
    }

    // Unknown annotations/constraints inside diagnostic containers must survive.
    // Only omit the known numeric vector/pose shape, never arbitrary nested data.
    public static bool NumericDiagnostic(JToken value)
    {
        if (value == null || value.Type == JTokenType.Null) return true;
        if (value is JObject obj) return obj.Properties().All(p =>
            new[] { "x", "y", "z", "w", "position", "rotation", "eulerAngles", "forward", "right", "up", "valid",
                "cameraPosition", "cameraRotation", "cameraForward", "bodyPosition", "bodyRotation", "bodyForward", "width", "height" }.Contains(p.Name) && NumericDiagnostic(p.Value));
        if (value is JArray array) return array.All(NumericDiagnostic);
        return value.Type == JTokenType.Integer || value.Type == JTokenType.Float || value.Type == JTokenType.Boolean;
    }

    private static JObject Fields(JObject source, params string[] names)
    {
        if (source == null) return null;
        var result = new JObject();
        foreach (string name in names) if (source[name] != null) result[name] = source[name].DeepClone();
        return result;
    }

    public static JObject Sequence(JObject sequence)
    {
        if (sequence == null) return null;
        var result = (JObject)sequence.DeepClone();
        if (result["steps"] is JArray steps)
            foreach (var step in steps.OfType<JObject>())
                if (step["receipt"] is JObject receipt && receipt["goal"] is JObject)
                {
                    var detail = DeviceEvent(receipt);
                    var goal = (JObject)detail["goal"];
                    // Sequence already carries the literal request/step quote.
                    goal.Remove("originalRequest"); goal.Remove("targetDescription");
                    step["receipt"] = detail;
                }
        return result;
    }

    public static string[] ReadyUserIntents(JObject input)
    {
        // Only an engine-owned continuation may narrow its action menu. New
        // utterances and initial model decomposition still see all room actions.
        if ((bool?)input?["taskSequenceRequired"] != true || (bool?)input["autonomous"] == true ||
            !(input["workContinuation"] is JObject work) ||
            (string)work["source"] != "approved-same-user-work-next-step" ||
            !(input["taskSequence"] is JObject sequence) || !(sequence["steps"] is JArray steps) ||
            sequence["workEpoch"]?.Type != JTokenType.Integer || sequence["userTurn"]?.Type != JTokenType.Integer ||
            string.IsNullOrWhiteSpace((string)sequence["request"]) ||
            (string)sequence["request"] != (string)input["currentUserText"] ||
            (int?)sequence["workEpoch"] != (int?)work["workEpoch"] ||
            (int?)sequence["userTurn"] != (int?)work["userTurn"]) return null;
        var ready = RoomTaskSequenceProtocol.Ready(steps).Values<string>().ToArray();
        if (ready.Length == 0) return null;
        var kinds = steps.OfType<JObject>().Where(s => ready.Contains((string)s["id"]))
            .Select(s => (string)s["kind"]).Distinct().ToArray();
        return kinds.All(k => new[] { "approach", "follow", "stop-moving" }.Contains(k)) ? kinds : null;
    }

    public static string FocusSchema(string schema, JObject input)
    {
        var kinds = ReadyUserIntents(input);
        if (kinds == null) return schema;
        var result = JObject.Parse(schema);
        var allowed = kinds.Concat(new[] { "none" }).ToArray();
        if (!(result["oneOf"] is JArray branches)) return schema;
        foreach (var branch in branches.OfType<JObject>().ToArray())
        {
            var props = (JObject)branch["properties"];
            var intents = ((JArray)props["intent"]["enum"]).Values<string>().Where(allowed.Contains).ToArray();
            if (intents.Length == 0 || ((JArray)branch["required"]).Values<string>().Contains("deviceGoal")) { branch.Remove(); continue; }
            props["intent"]["enum"] = new JArray(intents); props.Remove("deviceGoal");
        }
        var fields = branches.OfType<JObject>().SelectMany(b => ((JObject)b["properties"]).Properties().Select(p => p.Name)).Distinct().ToArray();
        foreach (var p in ((JObject)result["properties"]).Properties().ToArray()) if (!fields.Contains(p.Name)) p.Remove();
        result["properties"]["intent"]["enum"] = new JArray(allowed);
        return result.ToString(Newtonsoft.Json.Formatting.None);
    }

    public static string AvailableDeviceGoalSchema(string schema, JObject input)
    {
        // Engine-owned availability only. Missing information leaves the menu
        // unchanged; old completed/cancelled goals cannot be cancelled again.
        if ((bool?)input?["deviceGoalCancellationAvailable"] != false) return schema;
        var result = JObject.Parse(schema);
        if (!(result["oneOf"] is JArray branches)) return schema;
        foreach (var branch in branches.OfType<JObject>().ToArray())
        {
            var goal = branch["properties"]?["deviceGoal"] as JObject;
            if (goal == null) continue;
            if ((string)goal.SelectToken("properties.operation.enum[0]") == "cancel") { branch.Remove(); continue; }
            if (goal["oneOf"] is JArray operations)
                foreach (var operation in operations.OfType<JObject>().ToArray())
                    if ((string)operation.SelectToken("properties.operation.enum[0]") == "cancel") operation.Remove();
        }
        return result.ToString(Newtonsoft.Json.Formatting.None);
    }
}
