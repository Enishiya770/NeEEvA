using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// An opportunity decision needs activity, outcomes, prohibitions and recallable
// sources. It does not choose executor parameters or settle audio delivery.
public static class PrivateInterestContextPolicy
{
    public static JObject Project(JObject source)
    {
        var result=(JObject)source.DeepClone();var removed=new JArray();
        void Drop(JObject node,string field) {
            if(node?[field]==null)return;
            removed.Add(node[field].Path);node.Remove(field);
        }
        void Scalars(JObject node,params string[] fields) {
            foreach(string field in fields)if(node?[field] is JValue)Drop(node,field);
        }
        // Only fully recognized producer records can be deferred. Unknown
        // extensions keep the whole object, including their sibling context.
        void Defer(JObject parent,string field,string[] known,string[] keep) {
            if(!(parent?[field] is JObject value)||value.Properties().Any(p=>!known.Contains(p.Name)))return;
            var index=new JObject(value.Properties().Where(p=>keep.Contains(p.Name)||p.Name=="scope")
                .Select(p=>new JProperty(p.Name,p.Value.DeepClone())));
            index["detailSource"]=new JObject{["path"]=value.Path,["stamp"]=ActivityEvidencePolicy.Stamp(value),["detailAvailable"]=true};
            if(index.ToString(Formatting.None).Length+64<value.ToString(Formatting.None).Length){removed.Add(value.Path);parent[field]=index;}
        }
        // Keep original human input, all ledger steps and outcome identities.
        // Only these producer-owned diagnostic paths are projected; custom
        // objects/constraints are never traversed by a global name blacklist.
        void Outcome(JObject node) {
            if(node==null)return;
            Scalars(node,"generation","recordedGeneration","observedAtRealtime","executionRevision",
                "approachRetargetCount","approachRetargetTravel","approachRetargetStatus","matchesRequestedAction");
            foreach(string field in new[]{"lastAction","execution","goal","completionEvidence","actual","receipt",
                "executionAtInterruption","resultAtInterruption","taskSequence","bodyAtInterruption"})Outcome(node[field] as JObject);
            foreach(string field in new[]{"steps","earlierRequests"})
                foreach(var row in (node[field] as JArray??new JArray()).OfType<JObject>())Outcome(row);
            if(node["boundedRecovery"] is JObject recovery) {
                // Counts/limits/failure/owner remain; old transport prebuffer
                // telemetry does not decide whether a new interest is allowed.
                if(recovery["lastHandoff"] is JObject handoff)Scalars(handoff,"wireRevision","controlRevision","playerRevision","bufferSeconds");
            }
        }
        foreach(string path in new[]{"work.previousUnfinishedWork","work.taskSequence","spatialGoal","priorTaskResults","currentRoomTask"})
            Outcome(result.SelectToken(path) as JObject);
        var current=result.SelectToken("currentRoomTask.currentRoomFacts.currentFacts") as JObject;
        Scalars(current,"followLegCount","followTargetUpdatedAtRealtime","followNextPlanAtRealtime","segmentIndex","segmentCount",
            "preparedSegmentCount","plannedDurationSeconds","playedDurationSeconds","waitingForSegment","poseReturning",
            "movingTargetRevision","committedTargetRevision","movingTargetNeedsRestart","movingTargetStatus","observedAtRealtime");
        // The geometry assessment is still only a proposal capability, never an
        // execution guarantee. Keep state/reason/validity and unknown additions.
        var assessment=current?["assessment"] as JObject;
        if(assessment?["observation"] is JObject observation)
            Scalars(observation,"candidateCount","acceptedCount","startRejected","targetRejected","pathRejected","userClearanceRejected","otherRejected");
        if(result["time"] is JObject time) {
            foreach(var row in (time["samples"] as JArray??new JArray()).OfType<JObject>())
                if(RoomTaskContextPolicy.NumericDiagnostic(row["viewDirectionWorld"]))Drop(row,"viewDirectionWorld");
            Scalars(time,"reviewsInLastMinute","coalescedWakeEvents","sinceLastSampleSeconds");
            if(time["samples"] is JArray samples && samples.Count>1 && samples.OfType<JObject>().Count()==samples.Count &&
                samples.OfType<JObject>().All(s=>s.Properties().All(p=>new[]{"captureId","purpose","activeSeconds","capturedAtUtc","gapSincePreviousSampleSeconds","scope"}.Contains(p.Name)))) {
                time["sampleHistorySource"]=new JObject{["stamp"]=ActivityEvidencePolicy.Stamp(samples),["count"]=samples.Count,["detailAvailable"]=true,
                    ["scope"]="Earlier sampled-view timing stays in runtime; omitted intervals are unknown. observedMemory remains available."};
                time["samples"]=new JArray(samples.Last.DeepClone());removed.Add("time.samples.earlier");
            }
        }
        // A new-interest judgment does not re-audit an old plan or movement
        // trace. Outcomes and their original requests remain in the task ledger.
        var prior=result["priorTaskResults"] as JObject;
        if(prior?["goal"] is JObject priorGoal) {
            Defer(priorGoal,"searchSummary",new[]{"searchAttempted","completedStepCount","completedStepCountScope","observedViewCount","failedCandidateCount","latestSearchTaskId","latestSearchStatus","latestSearchReason","latestSearchDecisions","latestSearchMotionSteps","latestSearchFailureReason","allVisitedCandidatePagesReviewed"},
                new[]{"searchAttempted","latestSearchTaskId","latestSearchStatus","latestSearchFailureReason","allVisitedCandidatePagesReviewed"});
            if((string)priorGoal["status"]=="satisfied" && (bool?)priorGoal["completionEvidenceValid"]==true) {
                // The event/readback remain. Old interface and planner-output
                // diagnostics do not select a new executable target or route.
                if(prior["objectContext"] is JObject objects && objects.Count==1 && objects["runtimeDeviceControl"] is JObject control &&
                    control.Properties().All(p=>new[]{"implementationConnected","featureEnabled","supportedOperation","physicalHandRequired","requiresCurrentTargetConfirmation","requiresIndependentUserAuthorization"}.Contains(p.Name)))
                    Drop(prior,"objectContext");
                if(prior["roomDecision"] is JObject dispatch && dispatch.Properties().All(p=>new[]{"intent","dispatch"}.Contains(p.Name)))Drop(prior,"roomDecision");
            }
        }
        if(result["spatialGoal"] is JObject finished && (string)finished["status"]=="satisfied" && (bool?)finished["completionEvidenceValid"]==true &&
            finished["completionPredicate"] is JObject predicate &&
            predicate.Properties().All(p=>new[]{"kind","entityId","desiredState","requiresCurrentOpaqueReobservation","requiresFreshReadback","substepCompletionDoesNotSatisfyGoal"}.Contains(p.Name)) &&
            (string)predicate["entityId"]==(string)finished["boundEntityId"] && (string)predicate["desiredState"]==(string)finished["desiredState"])
            Drop(finished,"completionPredicate");
        // Keep any nonstandard proposal verbatim: the known approval envelope
        // can contain an unknown limitation in its extension fields.
        if(prior?["latestGoalReview"] is JObject review && review["proposal"] is JObject proposal &&
            proposal.Properties().All(p=>new[]{"operation","requestQuote","targetDescription","desiredState"}.Contains(p.Name)) &&
            new[]{"requestQuote","targetDescription"}.All(k=>(string)proposal[k]==(string)prior?["goal"]?["originalRequest"]))
            Defer(prior,"latestGoalReview",new[]{"verdict","reason","validationFailure","registered","pendingRetained","requestAssessment","proposalDiscarded","proposal"},
                new[]{"verdict","validationFailure","registered","pendingRetained","proposalDiscarded"});
        foreach(string path in new[]{"work.previousUnfinishedWork","work.previousUnfinishedWork.earlierRequests"}) {
            var selected=result.SelectToken(path);
            var histories=selected is JArray array?array.OfType<JObject>():selected is JObject one?new[]{one}:Enumerable.Empty<JObject>();
            foreach(var history in histories) {
                // Historical running/failed records keep recovery conditions.
                // Settled historical receipts retain operation and result; the
                // detailed old readback is recallable, never current authority.
                foreach(var step in (history.SelectToken("taskSequence.steps") as JArray??new JArray()).OfType<JObject>())
                    if((string)step["status"]=="completed" && step["receipt"] is JObject receipt && receipt["goal"] is JObject goal &&
                        (bool?)goal["completionEvidenceValid"]==true && goal["completionEvidence"] is JObject proof &&
                        proof["actual"] is JObject actual && actual.Properties().All(p=>new[]{"sceneLabel","registered","on","conflict","entityId","lightEnabled","emissionEnabled","lightState","emissionState","bakedLightingResidualUncontrolled"}.Contains(p.Name)))
                        Defer(goal,"completionEvidence",new[]{"actionId","entityId","desiredState","observedUtc","readAtUtc","resultCaptureId","actual","historicalOnly","scope"},
                            new[]{"actionId","entityId","desiredState","observedUtc","historicalOnly"});
            }
        }
        // Renderer details are not needed for an interest; preserve authoritative
        // on/conflict/registered state and the explicit baked-light limitation.
        foreach(string path in new[]{"spatialGoal.completionEvidence.actual","priorTaskResults.goal.completionEvidence.actual"})
            if(result.SelectToken(path) is JObject actual) {
                if(RoomTaskContextPolicy.NumericDiagnostic(actual["emissionColor"]))Drop(actual,"emissionColor");
                Scalars(actual,"lightIntensity");
            }
        // Reuse the generic receipt index for opportunity decisions. Execution
        // details remain recallable; failure, identity and unknown limits stay.
        foreach(string path in new[]{"priorTaskResults","currentRoomTask","spatialGoal","work.taskSequence"})
            if(result.SelectToken(path) is JObject outcome) {
                var indexed=TaskContinuationContextPolicy.IndexOutcome(outcome);
                if(indexed.ToString(Formatting.None).Length+96<outcome.ToString(Formatting.None).Length) {
                    indexed["detailSource"]=new JObject{["path"]=path,["stamp"]=ActivityEvidencePolicy.Stamp(outcome),["detailAvailable"]=true};
                    outcome.Replace(indexed);removed.Add(path);
                }
            }
        if(removed.Count>0)result["interestProjection"]=new JObject{["version"]=1,["omittedDiagnosticFields"]=removed.Count,
            ["sourceStamp"]=ActivityEvidencePolicy.Stamp(source),["detailAvailable"]=true,
            ["scope"]="Interest only. Executor telemetry remains in runtime. Task limits, original inputs, unknown constraints, results and memory remain. No action or delivery is authorized."};
        return result;
    }
}
