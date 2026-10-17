using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Reporting/routing projections only. The canonical task and spatial memory
// remain in their owning runtime; no projected record authorizes an action.
public static class TaskContinuationContextPolicy
{
    // Caller must independently review the latest human instruction against
    // the WHOLE parent plan. This function only transfers a task ledger, never
    // an executor lease, route, or object confirmation.
    public static JObject Resume(JObject previous, string stepId, string request, int epoch, int turn)
    {
        if (!(previous?["steps"] is JArray steps) || string.IsNullOrWhiteSpace(request) ||
            string.IsNullOrWhiteSpace((string)previous["request"]) ||
            !steps.OfType<JObject>().Any(s => (string)s["id"] == stepId && (string)s["status"] != "completed")) return null;
        var result = (JObject)previous.DeepClone();
        result["originalRequest"] = previous["originalRequest"]?.DeepClone() ?? previous["request"]?.DeepClone();
        result["resumedFrom"] = new JObject { ["request"] = previous["request"]?.DeepClone(),
            ["workEpoch"] = previous["workEpoch"]?.DeepClone(), ["userTurn"] = previous["userTurn"]?.DeepClone(),
            ["stamp"] = ActivityEvidencePolicy.Stamp(previous), ["stepId"] = stepId };
        result["request"] = request; result["workEpoch"] = epoch; result["userTurn"] = turn;
        var resumable=new System.Collections.Generic.HashSet<string>{stepId};
        for(int pass=0;pass<steps.Count;pass++)
            foreach(var child in steps.OfType<JObject>())
                if((child["after"] as JArray??new JArray()).Values<string>().Any(resumable.Contains))resumable.Add((string)child["id"]);
        foreach (var step in ((JArray)result["steps"]).OfType<JObject>()) {
            if ((string)step["status"] == "completed") continue;
            if ((string)step["id"] == stepId || (string)step["status"] == "running" ||
                ((string)step["status"]=="blocked" && resumable.Contains((string)step["id"]))) {
                var attempt = (JObject)step.DeepClone(); attempt.Remove("previousAttempt");
                step["previousAttempt"] = RoomTaskContextPolicy.DeliveryReceipt(attempt);
                step["status"] = "pending";
                foreach (string field in new[] { "actionId", "goalId", "generation", "receipt" }) step.Remove(field);
            }
        }
        result["readyStepIds"] = RoomTaskSequenceProtocol.Ready((JArray)result["steps"]);
        return result;
    }
    public static JObject History(JObject source)
    {
        if (source == null) return null;
        var result = (JObject)source.DeepClone();
        if (source["taskSequence"] is JObject sequence) result["taskSequence"] = RoomTaskContextPolicy.Sequence(sequence);
        foreach (string name in new[] { "executionAtInterruption", "resultAtInterruption" })
            if (result[name] is JObject facts) result[name] = Outcome(facts);
        if (result["earlierRequests"] is JArray earlier)
            result["earlierRequests"] = new JArray(earlier.OfType<JObject>().Select(History));
        return result;
    }

    public static JObject Outcome(JObject source)
    {
        if (source == null) return null;
        var result = RoomTaskContextPolicy.DeliveryReceipt(source);
        // Known route diagnostics are useful to the executor, not to resolving
        // which task a follow-up refers to. Outcome/reason/identity stay intact.
        foreach (string name in new[] { "navigation", "navigationDiagnostics", "candidateStations", "streamTelemetry",
            "targetCandidates", "routePoints", "pathCorners", "approachStationDiagnostics" })
            if (RoomTaskContextPolicy.NumericDiagnostic(result[name])) result.Remove(name);
        return result;
    }

    public static JObject FirstReply(JObject source)
    {
        var result = (JObject)source.DeepClone();
        if (source["previousUnfinishedWork"] is JObject history) result["previousUnfinishedWork"] = RoutingHistory(history);
        if (source["lastActionResults"] is JObject outcome) result["lastActionResults"] = Outcome(outcome);
        // Full human evidence and unknown constraints are never substring-cut.
        return result;
    }

    // A routing consumer needs referents and step states, not the executor's
    // repeated readback/pose archives. Keep the canonical record in the owner.
    public static JObject RoutingHistory(JObject source, string path="previousUnfinishedWork")
    {
        if(source==null)return null;
        if((int?)(source["historySource"] as JObject)?["routingVersion"]==3)return (JObject)source.DeepClone();
        var result=History(source);
        if(source["earlierRequests"] is JArray earlier)
            result["earlierRequests"]=new JArray(earlier.OfType<JObject>().Select((row,i)=>RoutingHistory(row,path+".earlierRequests["+i+"]")));
        foreach(string name in new[]{"executionAtInterruption","resultAtInterruption"})
            if(result[name] is JObject outcome)result[name]=IndexOutcome(outcome);
        if(result["bodyAtInterruption"] is JObject body)
            foreach(string name in new[]{"actualUserDistance","actualFacingErrorDegrees","observedAtRealtime"})body.Remove(name);
        if(result["taskSequence"] is JObject sequence && sequence["steps"] is JArray steps)
            foreach(var step in steps.OfType<JObject>())
                if(step["receipt"] is JObject receipt)step["receipt"]=IndexOutcome(receipt);
        // These are trusted producer annotations. Literal human strings and all
        // unknown/custom fields, including constraints inside outcomes, survive.
        foreach(var node in KnownRecords(result))
            if(node["scope"]?.Type==JTokenType.String && KnownScope((string)node["scope"]))node.Remove("scope");
        // Runtime owns the full records. Routing uses a normalized ledger, not
        // three copies of each body's last event. Unknown/custom fields survive.
        if(result["executionAtInterruption"] is JObject execution) {
            if(result["resultAtInterruption"] is JObject outcome &&
                outcome.Properties().All(p=>p.Name=="appliesToCurrentTarget"||JToken.DeepEquals(execution[p.Name],p.Value)))
                result["resultAtInterruption"]=new JObject{["sameOutcomeAs"]="executionAtInterruption",
                    ["appliesToCurrentTarget"]=outcome["appliesToCurrentTarget"]?.DeepClone()};
            if((result["taskSequence"] as JObject)?["steps"] is JArray historySteps)
                foreach(var step in historySteps.OfType<JObject>())
                    if(JToken.DeepEquals(step["receipt"],execution))step["receipt"]=new JObject{["sameOutcomeAs"]="executionAtInterruption"};
        }
        if(result["taskSequence"] is JObject ledger) {
            // Coverage duplicates full request, step quotes and answer duties.
            // Only this known producer shape may be replaced by the source index.
            if(ledger["coverage"] is JArray coverage && coverage.OfType<JObject>().Count()==coverage.Count &&
                coverage.OfType<JObject>().All(c=>c.Properties().All(p=>new[]{"quote","kind","stepIds"}.Contains(p.Name))))ledger.Remove("coverage");
            if(JToken.DeepEquals(ledger["request"],result["request"]))ledger.Remove("request");
            foreach(string field in new[]{"workEpoch","userTurn"})
                if(JToken.DeepEquals(ledger[field],result[field]))ledger.Remove(field);
            if((string)result["taskReceiptSource"]=="owned-task-sequence")result.Remove("taskReceiptSource");
            if(ledger["steps"] is JArray ownedSteps)foreach(var step in ownedSteps.OfType<JObject>()) {
                if(JToken.DeepEquals(step["intent"],step["kind"]))step.Remove("intent");
                if(step["receipt"] is JObject receipt && receipt["goal"] is JObject goal) {
                    // A classifier needs the historical entity/state and proof
                    // validity, not render-channel diagnostics or capture bytes.
                    // The enclosing source index recalls the full receipt later.
                    if(goal["completionEvidence"] is JObject proof) {
                        proof.Remove("resultCaptureId");
                        if((string)proof["entityId"]==(string)goal["boundEntityId"])proof.Remove("entityId");
                        if(JToken.DeepEquals(proof["desiredState"],goal["desiredState"]))proof.Remove("desiredState");
                        if(proof["actual"] is JObject actual) {
                            if((string)actual["entityId"]==(string)goal["boundEntityId"])actual.Remove("entityId");
                            foreach(string field in new[]{"lightEnabled","emissionEnabled","lightState","emissionState","bakedLightingResidualUncontrolled"})actual.Remove(field);
                        }
                    }
                }
            }
            foreach(var duty in (ledger["answerObligations"] as JArray??new JArray()).OfType<JObject>())
                if(((string)duty["scope"]??"").StartsWith("Same-turn question;",StringComparison.Ordinal))duty.Remove("scope");
            if(ledger["answerEvidence"] is JObject answer && ((string)answer["scope"]??"").StartsWith("Frozen before dispatch of this task plan.",StringComparison.Ordinal))answer.Remove("scope");
        }
        if(result["bodyAtInterruption"] is JObject historicalBody && result["executionAtInterruption"] is JObject historicalExecution) {
            if((bool?)historicalBody["connected"]==true)historicalBody.Remove("connected");
            if(JToken.DeepEquals(historicalBody["executionTargetKind"],historicalExecution["targetKind"]))historicalBody.Remove("executionTargetKind");
            string active=(string)historicalBody["activeActionId"];
            if(string.IsNullOrEmpty(active)||active==(string)historicalExecution["actionId"])historicalBody.Remove("activeActionId");
        }
        if((result["taskSequence"] as JObject)?["steps"] is JArray indexedSteps && result["executionProgress"] is JObject progress &&
            progress.Properties().All(p=>new[]{"allStepsCompleted","readyStepIds","failedStepIds"}.Contains(p.Name)) &&
            (bool?)progress["allStepsCompleted"]==indexedSteps.OfType<JObject>().All(s=>(string)s["status"]=="completed") &&
            JToken.DeepEquals(progress["readyStepIds"],result["taskSequence"]["readyStepIds"]) &&
            JToken.DeepEquals(progress["failedStepIds"],new JArray(indexedSteps.OfType<JObject>().Where(s=>(string)s["status"]=="failed").Select(s=>s["id"]))))
            result.Remove("executionProgress");
        foreach(var node in KnownRecords(result)) {
            foreach(string name in new[]{"privateFailure","planningFailure","taskSequence","executionProgress","goalId","desiredState"})
                if(node[name]?.Type==JTokenType.Null)node.Remove(name);
            foreach(string name in new[]{"generation","recordedGeneration","observedAtRealtime","stage","executionReason","matchesRequestedAction"})
                if(node[name] is JValue)node.Remove(name);
            foreach(string name in new[]{"captureId","sceneTargetId","actionId","intent"})
                if(node[name]?.Type==JTokenType.String&&((string)node[name]==""||(name=="intent"&&(string)node[name]=="none")))node.Remove(name);
        }
        var originalSource=source["historySource"] as JObject;
        result["historySource"]=new JObject{["path"]=originalSource?["path"]??new JValue(path),
            ["stamp"]=originalSource?["stamp"]??new JValue(ActivityEvidencePolicy.Stamp(source)),["routingVersion"]=3,
            ["detailAvailable"]=true,["authority"]="historical-only"};
        return result;
    }

    private static bool KnownScope(string text) => new[]{"Historical requests and actual outcomes,", "Historical result of the last actual room approach;",
        "Whole current request with engine-recorded progress.","Execution ledger only.","Past engine event only,",
        "Actual owned outcomes;"}.Any(prefix=>(text??"").StartsWith(prefix,StringComparison.Ordinal));

    private static System.Collections.Generic.IEnumerable<JObject> KnownRecords(JObject record)
    {
        yield return record;
        foreach(string field in new[]{"execution","lastAction","goal","completionEvidence","actual","body","relation","operationSummary",
            "taskSequence","receipt","executionProgress","bodyAtInterruption","executionAtInterruption","resultAtInterruption"})
            if(record[field] is JObject child)foreach(var node in KnownRecords(child))yield return node;
        foreach(string field in new[]{"steps","earlierRequests"})
            if(record[field] is JArray rows)foreach(var row in rows.OfType<JObject>())foreach(var node in KnownRecords(row))yield return node;
    }

    public static JObject IndexOutcome(JObject source)
    {
        var result=Outcome(source);
        // Only adapter-owned record paths are diagnostics. A custom nested
        // constraint can itself mention generation, stage or an empty actionId.
        foreach(var node in KnownRecords(result)) {
            // Diagnostic counters are not task state. Keep failed phase/reason,
            // operation/actor/entity, timestamps, evidence validity and readback.
            foreach(string name in new[]{"approachRetargetCount","approachRetargetTravel","approachRetargetStatus",
                "targetRevision","operationRevision","generation","recordedGeneration","observedAtRealtime","stage","executionReason","matchesRequestedAction"})
                if(node[name] is JValue)node.Remove(name);
            foreach(string name in new[]{"captureId","sceneTargetId","actionId","intent"})
                if(node[name]?.Type==JTokenType.String && (string)node[name]=="")node.Remove(name);
            if(node["scope"]?.Type==JTokenType.String && KnownScope((string)node["scope"]))node.Remove("scope");
        }
        // The event's goal readback is authoritative; the parallel lastAction
        // copy adds renderer prose but no different result. Remove it ONLY if
        // all of its identity, state and time fields match that exact outcome.
        if(result["goal"] is JObject goal && result["lastAction"] is JObject action &&
            goal["completionEvidence"] is JObject evidence && (bool?)goal["completionEvidenceValid"]==true &&
            (string)action["actionId"]==(string)evidence["actionId"] &&
            (string)action["entityId"]==(string)evidence["entityId"] &&
            (string)action["requestedState"]==(string)evidence["desiredState"] &&
            (string)action["phase"]=="performed" && action.Properties().All(p=>new[]{"actionId","intent","entityId","requestedState","phase","stateChanged","observedUtc","reason"}.Contains(p.Name))) {
            result["operationReceipt"]=new JObject{["actionId"]=action["actionId"], ["intent"]=action["intent"],
                ["phase"]=action["phase"], ["observedUtc"]=action["observedUtc"], ["stateChanged"]=action["stateChanged"]};
            result.Remove("lastAction");
        }
        return result;
    }

    public static JObject Sizes(JObject source) => new JObject(source.Properties().Select(p =>
        new JProperty(p.Name, p.Value.ToString(Formatting.None).Length)));
}
