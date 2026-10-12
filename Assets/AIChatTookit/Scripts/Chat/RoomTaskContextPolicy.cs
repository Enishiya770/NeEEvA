using System;
using System.Linq;
using Newtonsoft.Json.Linq;

/// <summary>Consumer views of engine records. Never changes task ownership or completion.</summary>
public static class RoomTaskContextPolicy
{
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
