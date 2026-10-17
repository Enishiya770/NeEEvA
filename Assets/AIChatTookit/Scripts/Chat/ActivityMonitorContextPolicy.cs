using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>A bounded view of evidence, never a change to memory or execution authority.</summary>
public static class ActivityMonitorContextPolicy
{
    public static JObject Scope(string domain, params string[] issues) => new JObject {
        ["domain"] = domain, ["issues"] = new JArray(issues)
    };

    public static JObject Sizes(JObject frame) => new JObject {
        ["fields"] = new JObject(frame.Properties().Select(p => new JProperty(p.Name, Size(p.Value)))),
        ["facts"] = new JObject((frame["facts"] as JObject ?? new JObject()).Properties()
            .Select(p => new JProperty(p.Name, Size(p.Value))))
    };
    private static int Size(JToken value) => value?.ToString(Formatting.None).Length ?? 0;

    // Whole optional records are omitted, not substring-truncated. Omission has
    // no negative meaning and removes that record's intervention authority.
    public static JObject Project(JObject source, int pressure)
    {
        var frame = (JObject)source.DeepClone();
        if (!(frame["facts"] is JObject)) frame["facts"] = new JObject();
        if (!(frame["evidenceScopes"] is JObject)) frame["evidenceScopes"] = new JObject();
        var facts = (JObject)frame["facts"];
        var scopes = (JObject)frame["evidenceScopes"];
        var omitted = new JArray();
        bool bound = (int?)frame["evidenceVersion"] >= 2;
        if(bound && frame["previousAttempt"] is JObject feedback) {
            // Keep the specific rejection and repair constraint, not the full
            // bad draft that otherwise anchors the next bounded attempt.
            feedback.Remove("rejectedVerdict");feedback.Remove("rawVerdict");
            omitted.Add("previousAttempt.raw-verdict; retained-in-diagnostics");
            string sourceId=(string)feedback["unknownOnlySource"];
            if(sourceId!=null && scopes[sourceId]!=null)feedback["citedSourceCapability"]=scopes[sourceId].DeepClone();
        }
        void Drop(string key) { if (facts.Property(key) != null) { facts.Remove(key); scopes.Remove(key); omitted.Add(key); } }
        // A delivery turn already has an exact versioned receipt. Planning and
        // old whole-scene snapshots add no authority to that speech-only step.
        bool delivery = frame["delivery"] is JObject;
        if(delivery)ResultPlaybackSelection.Annotate((JObject)frame["delivery"]);
        if (delivery && (frame["actions"] as JArray)?.Count == 0)
            foreach (string key in new[] { "request", "execution", "planningFailure", "observation", "historyAnswers" }) Drop(key);
        foreach (string key in new[] { "dialogue", "intention" })
            if ((!bound && pressure > 0) || delivery || Size(facts[key]) > 2000) Drop(key);
        if (facts["execution"] is JObject execution)
        {
            if(bound)execution.Remove("scope");
            foreach (string key in new[] { "priorResults", "inspection" })
                if (pressure > 0 || delivery || Size(execution[key]) > 1200) {
                    execution.Remove(key); omitted.Add("execution." + key);
                }
        }
        if (pressure > 0 && facts["observation"] is JObject observation) {
            observation.Remove("observedObjects");
            if(bound && observation["formalSource"] is JObject formalSource)formalSource.Remove("scope");
            observation["descriptionCoverage"] = "omitted-for-budget; not a negative observation";
            omitted.Add("observation.observedObjects");
        }
        // Retain the nearest phrase; later phrases enter attention on the next
        // poll. Do not clear runtime speech, spatial memory or result ledgers.
        if (pressure > 0 && frame["speech"] is JArray speech && speech.Count > 1) {
            frame["speech"] = new JArray(speech.Take(1).Select(s => s.DeepClone()));
            omitted.Add("additional-speech-units");
        }
        foreach (var unit in (frame["speech"] as JArray ?? new JArray()).OfType<JObject>()) {
            if (unit["observedAtSpeechStart"] is JObject) unit.Remove("observedAtCreation");
            foreach (string snapshot in new[] { "observedAtCreation", "observedAtSpeechStart" })
                if (unit[snapshot] is JObject frozen) {
                    frozen.Remove("scope"); frozen.Remove("inspection");
                    if(bound && pressure>0 && frozen["formalObservation"] is JObject formal)formal.Remove("scope");
                    if (Size(frozen["priorResults"]) > 800 || pressure > 0) frozen.Remove("priorResults");
                    // Lift immutable receipts once. Frozen IDs still say which
                    // outcomes existed when these particular words started.
                    foreach(var result in frozen.Properties().Where(p=>p.Name.StartsWith("result:")).ToList()) {
                        if(facts[result.Name]==null) {
                            facts[result.Name]=result.Value.DeepClone();
                            scopes[result.Name]=new JObject{["domain"]="execution",["issues"]=(bool?)result.Value["detailsAvailable"]==true?new JArray("mistake","uncertainty"):new JArray(),
                                ["binding"]=result.Value["binding"]?.DeepClone()};
                        }
                        result.Remove();
                    }
                }
        }
        if(bound && (frame["speech"] as JArray)?.Count == 1) {
            var unit=(JObject)frame["speech"][0];
            var frozen=ActivityEvidencePolicy.FrozenSpeech(unit,frame);
            if(frozen!=null) {
                // A later execution sample cannot prove what was true when the
                // words began. Give the frozen state its own citable capability.
                var start=(JObject)frozen.DeepClone();
                start.Remove("formalObservation"); start.Remove("completedResultIds");
                start.Remove("scope");
                if(start.Count>0) {
                    start["speechId"]=unit["id"].DeepClone();
                    start["sampleTime"]="speech-start";
                    facts["speech-start"]=start;
                    scopes["speech-start"]=new JObject{["domain"]="execution",["issues"]=new JArray("mistake","uncertainty"),
                        ["binding"]=ActivityEvidencePolicy.Binding(start,(string)start["intent"]??"execution",(string)start["actionId"])};
                    if(pressure>0)unit["observedAtSpeechStart"]=new JObject{
                        ["evidenceId"]="speech-start",["completedResultIds"]=frozen["completedResultIds"]?.DeepClone(),
                        ["formalObservation"]=frozen["formalObservation"]?.DeepClone()};
                }
            }
        }
        if (bound && (string)frame["purpose"] == "speech")
            foreach (var capability in scopes.Properties().Where(p => (string)p.Value["domain"] == "execution"))
                if (new[] { "execution", "", "none" }.Contains((string)(capability.Value["binding"] as JObject)?["operation"]))
                    // Preserve untyped samples as context, not as proof that
                    // this sentence concerns their old action or target.
                    capability.Value["issues"] = new JArray();
        // These identifiers are already carried by the fact key and its typed
        // scope. Deduplicate only exact copies, after lifting frozen receipts.
        if(bound)foreach(var record in facts.Properties().Where(p=>p.Name.StartsWith("result:")).ToArray())
            if(record.Value is JObject receipt) {
                if((string)receipt["id"]==record.Name.Substring(7))receipt.Remove("id");
                if((string)frame["purpose"]!="action" && scopes[record.Name]?["binding"]!=null && ActivityEvidencePolicy.Equal(receipt["binding"],scopes[record.Name]["binding"]))receipt.Remove("binding");
            }
        if(frame["reviewOperation"] is JObject focus && focus["binding"] is JObject binding) {
            foreach(var capability in scopes.Properties().Where(p=>(string)p.Value["domain"]=="execution").ToArray())
                if(!ActivityEvidencePolicy.Equal(capability.Value["binding"],binding)) {
                    if(capability.Name.StartsWith("result:") && facts[capability.Name] is JObject result && result["binding"]==null)
                        result["binding"]=capability.Value["binding"]?.DeepClone();
                    scopes.Remove(capability.Name);
                }
            if(pressure>0)foreach(var record in facts.Properties().Where(p=>p.Name.StartsWith("result:")).ToArray())
                if(record.Value is JObject other && !ActivityEvidencePolicy.Equal(other["binding"]??scopes[record.Name]?["binding"],binding)) {
                    // Keep this event's actual status and identity as context;
                    // its full proof is checked in its OWN operation frame.
                    other["facts"]=null;other["detailsAvailable"]=false;
                    other["detailSource"]="own-operation-frame:"+record.Name;
                }
        }
        // Do not drop a constraint, original request, result, or frozen answer
        // evidence to force admission. A still oversized request stays unchecked.
        if (pressure > 1) {
            if (Size(facts["planningFailure"]) > 1800) Drop("planningFailure");
            if (!delivery && Size(facts["historyAnswers"]) > 2400) Drop("historyAnswers");
        }
        // Action entries refer to their canonical fact instead of copying it.
        foreach (var action in (frame["actions"] as JArray ?? new JArray()).OfType<JObject>()) {
            string key = "action:" + (string)action["id"];
            if (facts[key] != null) {
                foreach (var field in action.Properties().Where(p => !new[] { "id", "version", "adapter", "kind", "cancellable" }.Contains(p.Name)).ToList()) field.Remove();
                action["evidenceId"] = key;
            }
        }
        if (bound && pressure > 0) {
            // Adapter scope prose is already stated in the typed monitor
            // contract. Preserve the capture, proposition and played-prefix IDs.
            foreach(var unit in (frame["speech"] as JArray??new JArray()).OfType<JObject>())unit.Remove("scope");
            foreach(var record in facts.Properties().Where(p=>p.Name.StartsWith("visual:",StringComparison.Ordinal)))
                if(record.Value is JObject visual)visual.Remove("scope");
            foreach(var record in facts.Properties().Where(p=>p.Name.StartsWith("recalled:",StringComparison.Ordinal)))
                if(record.Value is JObject recall) {
                    if((string)recall["sourceCaptureId"]==record.Name.Substring(9))recall.Remove("sourceCaptureId");
                    if(JToken.DeepEquals(recall["question"],facts["request"]?["text"]))recall.Remove("question");
                }
            // Empty dialogue and a null failure carry no evidence authority.
            // Keep explicit unknown-history records: those do convey uncertainty.
            if (facts["dialogue"] is JArray dialogue && dialogue.Count == 0) Drop("dialogue");
            if (facts["planningFailure"]?.Type == JTokenType.Null) Drop("planningFailure");
            // Adapter prose is already part of the monitor contract; actual
            // records and the unknown-only evidence capability stay intact.
            if (facts["historyAnswers"] is JObject history) history.Remove("scope");
            foreach (var record in facts.Properties().Where(p => p.Name.StartsWith("action:")))
                if (record.Value is JObject action && action["task"] is JObject task) {
                    task.Remove("scope");
                    if ((string)task["targetDescription"] == (string)action["target"]) task.Remove("targetDescription");
                    if ((string)task["targetPurpose"] == (string)action["purpose"]) task.Remove("targetPurpose");
                    if ((string)action["target"] == (string)facts["request"]?["text"]) action.Remove("target");
                }
            // These are repeated prose annotations emitted by our adapters,
            // not user constraints or execution data. Typed scopes/IDs remain.
            foreach (var record in facts.Properties().Where(p => p.Name.StartsWith("result:") || p.Name.StartsWith("action:")))
                if (record.Value is JObject detail) { detail.Remove("scope"); detail.Remove("timeScope"); }
            foreach (var capability in scopes.Properties())
                if (capability.Value is JObject detail) detail.Remove("scope");
            // The speech consumer cannot stop an action in a separate action
            // frame. Its task and execution data still remain canonical facts.
            foreach (var record in facts.Properties().Where(p => p.Name.StartsWith("result:")))
                if (record.Value is JObject detail && detail["facts"] is JObject outcome) {
                    outcome.Remove("scope");
                    // A step summary and its immutable outcome can repeat the
                    // same identity. Keep the value at the receipt path; never
                    // merge different operations or remove unknown constraints.
                    if (outcome["outcome"] is JObject receipt)
                        foreach (string key in new[] { "actionId", "goalId", "entityId", "desiredState" })
                            if (outcome[key] is JValue value && value.Type != JTokenType.Null &&
                                JToken.DeepEquals(value, receipt[key])) outcome.Remove(key);
                }
        }
        foreach(var capability in scopes.Properties().Where(p=>facts.Property(p.Name)==null).ToArray())capability.Remove();
        if(bound && pressure>0)
            // An absent capability and an explicitly empty issue list both grant
            // no correction authority. Keep the actual source/unknown facts; only
            // their redundant zero-authority annotation is omitted on the wire.
            foreach(var capability in scopes.Properties().Where(p=>p.Value["issues"] is JArray issues && issues.Count==0).ToArray())
                capability.Remove();
        frame["projection"] = new JObject { ["pressure"] = pressure, ["omitted"] = omitted,
            ["scope"] = bound?"Omissions are unknown; full constraints retained.":delivery ? "Speech-only delivery of this exact version. Its question/outcome and full user constraints remain; the complete multi-step request stays in runtime, not replanned here. Omission never proves absence."
                : "Partial attention only. Omitted, empty or unavailable evidence never proves absence. Request and constraints remain complete." };
        return frame;
    }

    public static string SpeechEvidenceCapabilityRejection(JObject result,JObject frame)
    {
        var row = result["speech"] as JObject;
        if ((string)row?["issue"] == "none") return null;
        string domain = (string)result["claimDomain"], issue = (string)row?["issue"];
        if (!new[] { "visual", "history", "execution", "request", "other" }.Contains(domain)) return "speech-domain-missing";
        var scopes = frame["evidenceScopes"] as JObject;
        // Typed evidence is supplied by trusted adapters, never by the verdict.
        if (scopes == null) return "speech-evidence-scope-missing";
        bool supports = (row["evidenceIds"] as JArray ?? new JArray()).Values<string>().Any(id =>
            (string)scopes[id]?["domain"] == domain &&
            (scopes[id]?["issues"] as JArray ?? new JArray()).Values<string>().Contains(issue));
        return supports?null:"speech-evidence-domain-or-coverage-mismatch";
    }

    public static string SpeechEvidenceRejection(JObject result, JObject frame)
    {
        string capability=SpeechEvidenceCapabilityRejection(result,frame);
        if(capability!=null)return capability;
        var row=result["speech"] as JObject;
        if((string)row?["issue"]=="none")return null;
        string domain=(string)result["claimDomain"],issue=(string)row?["issue"];
        var scopes=frame["evidenceScopes"] as JObject;
        if((int?)frame["evidenceVersion"]>=2) {
            var proof=row["proof"] as JObject;
            string id=(string)proof?["evidenceId"];
            if(domain=="execution" && issue=="mistake" && id=="execution") {
                var unit=(frame["speech"] as JArray??new JArray()).OfType<JObject>().FirstOrDefault(u=>(string)u["id"]==(string)row["id"]);
                if(ActivityEvidencePolicy.FrozenSpeech(unit,frame) is JObject frozen &&
                    !ActivityEvidencePolicy.Equal(frame["facts"]?[id]?["body"],frozen["body"]))
                    return "speech-proof-current-state-not-speech-start";
            }
            JToken scalar=null;
            try{scalar=frame["facts"]?[id??""]?.SelectToken((string)proof?["evidencePath"]??"");}catch(Exception){}
            var proofIssues=(scopes?[id??""]?["issues"] as JArray??new JArray()).Values<string>().ToArray();
            bool typedProof=(string)scopes?[id??""]?["domain"]==domain && proofIssues.Contains(issue);
            // An explicitly unknown-only source may quote its empty record set
            // to establish uncertainty. It still cannot establish nonoccurrence,
            // a mistake, or an observation absent from a different source.
            bool emptyUnknown=typedProof && issue=="uncertainty" && proofIssues.All(x=>x=="uncertainty") &&
                scalar is JContainer records && !records.HasValues &&
                scalar.ToString(Formatting.None)==(string)proof?["evidenceQuote"];
            if(id==null || !(row["evidenceIds"] as JArray??new JArray()).Values<string>().Contains(id) ||
                (string)proof["scope"]!="same-explicit-proposition" ||
                !typedProof || !emptyUnknown && (!(scalar is JValue) || !ActivityMonitorProtocol.ContainsQuote(scalar,(string)proof["evidenceQuote"])))
                return "speech-correction-proposition-proof-missing";
            if(domain=="execution" && !ActivityEvidencePolicy.Equal(scopes[id]?["binding"],result["claimBinding"]))
                return "speech-correction-operation-entity-mismatch";
            if(domain=="visual" && id.StartsWith("visual:")) {
                var visual=frame["facts"][id];
                if((string)visual["speechId"]!=(string)row["id"] || (string)visual["claimQuote"]!=(string)result["claimQuote"] ||
                    (string)proof["evidencePath"]!="finding" ||
                    (string)visual["captureId"]!=(string)frame["facts"]?["observation"]?["formalSource"]?["captureId"])
                    return "speech-visual-proposition-source-mismatch";
            }
            if(domain=="execution" && (string)result["claimKind"]=="entity-state" &&
                (scalar?.Parent as JProperty)?.Name=="phase")return "speech-phase-not-entity-state-evidence";
            if(domain=="execution" && issue=="uncertainty" && scalar is JValue state &&
                new[]{"status","phase"}.Contains((state.Parent as JProperty)?.Name) &&
                new[]{"completed","succeeded","satisfied","arrived","running","moving"}.Contains((string)state))
                return "speech-uncertainty-with-definite-outcome";
            if(issue=="mistake" && domain=="execution") {
                var unit=(frame["speech"] as JArray??new JArray()).OfType<JObject>().FirstOrDefault(u=>(string)u["id"]==(string)row["id"]);
                var start=ActivityEvidencePolicy.FrozenSpeech(unit,frame,true);
                if(id.StartsWith("result:") && !(start?["completedResultIds"] as JArray??new JArray()).Values<string>().Contains(id.Substring(7)))
                    return "speech-proof-result-postdates-utterance";
                if(id=="execution" && start!=null && !ActivityEvidencePolicy.Equal(frame["facts"]?[id]?["body"],start["body"]))
                    return "speech-proof-current-state-not-speech-start";
                if(id=="speech-start" && (start==null ||
                    (string)frame["facts"]?[id]?["speechId"]!=(string)unit?["id"] ||
                    !ActivityEvidencePolicy.Equal(frame["facts"]?[id]?["body"],start["body"])))
                    return "speech-proof-frozen-owner-mismatch";
            }
        }
        return null;
    }

    public static bool NeedsNewSource(string reason,string output,JObject frame)
    {
        if(reason=="speech-perception-needs-source")return true;
        if(reason!="speech-evidence-domain-or-coverage-mismatch"&&reason!="speech-truth-without-qualified-evidence")return false;
        try {
            var verdict=JObject.Parse(output);
            string domain=(string)verdict.SelectToken("grounding.claimDomain")??(string)verdict["claimDomain"];
            string proof=(string)verdict.SelectToken("speech.proof.evidenceId");
            if((string)verdict["claimTime"]=="past-event" && (string)verdict["perceptionSource"]!="current-writer-image" &&
                domain=="visual" && (frame["evidenceScopes"] as JObject??new JObject()).Properties().Any(p=>
                    (string)p.Value["domain"]=="history" && (p.Value["issues"] as JArray)?.Count>0))return false;
            var capability=frame["evidenceScopes"]?[proof??""];
            if(capability!=null && (capability["issues"] as JArray)?.Count>0 &&
                (domain=="other" && (string)capability["domain"]!="other" ||
                 (string)verdict["claimTime"]=="past-event" && (string)capability["domain"]=="history"))
                return false; // Repair conflicting classification against an available source first.
            return domain!=null && !(frame["evidenceScopes"] as JObject??new JObject()).Properties().Any(p=>
                (string)p.Value["domain"]==domain && (p.Value["issues"] as JArray)?.Count>0);
        } catch(Exception) {return false;}
    }
}

/// <summary>Failures are not successes; same evidence gets at most two retries.</summary>
public sealed class ActivityMonitorAttempts
{
    private sealed class Entry { public int Failures; public float Next; public JObject Feedback; }
    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
    private readonly Queue<string> order = new Queue<string>();
    public bool CanAttempt(string key, float now) => !entries.TryGetValue(key, out var e) || e.Failures < 3 && now >= e.Next;
    public JObject Feedback(string key) => entries.TryGetValue(key,out var entry)?(JObject)entry.Feedback?.DeepClone():null;
    public int Failed(string key, float now, string reason=null, string output=null, bool needsNewSource=false) {
        if (!entries.TryGetValue(key, out var e)) {
            while (order.Count >= 32) entries.Remove(order.Dequeue());
            entries[key] = e = new Entry(); order.Enqueue(key);
        }
        var priorFeedback=e.Feedback;
        e.Feedback=new JObject{["rejection"]=reason,["rejectedVerdict"]=output!=null&&output.Length<=2600?output:null,
            ["scope"]=reason=="speech-unknown-assertion-left-unresolved"
                ?"The provided historical evidence explicitly leaves this event unknown. A definite denial is not justified by absence of disproof. Express narrow uncertainty about the same subject/entity/time; never flip it to a positive claim."
                :reason=="speech-act-not-factual"?"Reconcile speechAct and issue. An actual unsure guess needs no correction; a definite assertion restricted only to a time/place/entity is factual-claim, not qualified-guess. Re-read the literal claim."
                :reason=="speech-qualification-not-quoted"?"qualificationQuote was not an exact uncertainty phrase in the original speech. Do not insert ellipses or paraphrase. Time/place/entity qualifiers do not express uncertainty. If the original makes a definite positive or negative assertion, choose factual-claim and correct only its narrow unsupported claim."
                :reason=="speech-qualification-fragment"?"Quote the complete uncertainty expression with enough literal context to establish its meaning. A single character or detached sentence ending is insufficient evidence; this does not prohibit qualified guesses. Re-read the utterance, preserving actual feelings, questions and uncertainty."
                :reason=="speech-qualification-not-epistemic"?"The quote only limits time, asserts categorical negation, or is punctuation; it does not say the speaker is unsure. The definite positive/negative claim remains factual-claim. Unknown history needs narrow uncertainty about the same actor/event/time, not a categorical denial or a new action."
                :reason=="speech-update-repeats-rejected-claim"?"groundedUpdate repeated the rejected assertion. The replacement itself must express the corrected fact or narrow uncertainty. correctionLeadIn is only a short transition, not the factual correction."
                :reason=="speech-leadin-repeats-rejected-claim"?"The transition repeated the rejected factual claim. Put one natural correction in groundedUpdate, including a brief transition if needed, and leave correctionLeadIn empty. Do not restate the erroneous assertion."
                :reason=="speech-temporal-comparison-conflict"?"claimTime describes the original sentence's tense, not the later monitor's timestamp. A present-tense sentence stays current-state even after its world changes. unknown permits uncertainty, not mistake; contradicted permits mistake; world-change requires a supported current-state claim that changed later. Past true events remain true."
                :reason=="action-conflict-quotation-invalid"?"Cite both the exact action key and requirementEvidenceId in evidenceIds. observedQuote must come from the actual action, not a prior result or task purpose. If a failed prerequisite establishes the conflict, additionally cite its result key. Use exact scalar quotations, never invented JSON fragments."
                :reason=="speech-correction-proposition-proof-missing"?"Path is relative to facts[evidenceId], quote its exact scalar. Running proves ongoing, not completed."
                :reason=="speech-proof-current-state-not-speech-start"||reason=="speech-truth-current-state-not-speech-start"?"Judge truth when said using speech-start, not a different later execution/result. Cite its exact scalar and describe the earlier mistake in the speech language."
                :reason=="speech-claim-not-in-current-unit"?"claimQuote came from the user request or adjacent dialogue, not the CURRENT speech.text. Recheck only the actual speech unit. Copy its own atomic assertion; do not assess a different completed step or call a completion statement a personal feeling."
                :reason=="speech-claim-outside-reviewed-operation"?"Recheck claimOperation from literal words, not proof. Only reviewOperation.claimKey may be corrected here; other operations have their own frame."
                :reason=="speech-evidence-domain-or-coverage-mismatch"||reason=="speech-perception-domain-conflict"?"Recheck the claim's actual source and time against evidenceScopes. A prior observation belongs to historical evidence, not the current camera merely because it mentions seeing. An explicitly unknown past event permits narrow uncertainty, never definite denial. No source can gain authority by changing its label."
                :reason=="speech-uncertainty-with-definite-outcome"?"The cited status states a known completed or running operation, not an unknown outcome. Check the operation the original sentence actually describes. Do not reinterpret a completed result as a claim that its following step also ended. Leave supported or ambiguous statements unchanged; uncertainty needs explicit unknown evidence."
                :reason=="speech-phase-not-entity-state-evidence"?"An execution phase describes an action's progress, not the entity's readiness or physical property. Re-read the completed results for the entity. Keep an ambiguous statement already supported by its completed result; do not invent an explicit action claim to justify intervening."
                :reason=="delivery-repeated-candidate"?"The proposed supplement was already generated or heard. Re-evaluate actual completedPlaybackText against taskContext and the result. Ellipsis can already communicate that outcome, in which case choose covered. Only a genuinely missing meaning needs a different supplement that fully states that meaning; never repeat an action or merely rephrase the same insufficient reassurance."
                :reason=="playback-quote-precedes-result-correction"?"Read the full playback, but quote only its last relevant clause after latestCorrectionStart. Do not quote the whole paragraph across that boundary. If the final clause already conveys this result in context, choose covered; otherwise describe the actual missing outcome, never replay an action."
                :"Previous attempt was NOT applied or reviewed. Recheck the current supplied sources and evidenceScopes. Unsupported intervention must become none; do not invent evidence to preserve it."};
        if(reason=="speech-unknown-assertion-left-unresolved" || (bool?)priorFeedback?["requireHistoricalUnknownConsistency"]==true)
            e.Feedback["requireHistoricalUnknownConsistency"]=true;
        if((bool?)priorFeedback?["reclassifyScopeOnlyGuess"]==true)e.Feedback["reclassifyScopeOnlyGuess"]=true;
        if((bool?)priorFeedback?["historicalObservation"]==true)e.Feedback["historicalObservation"]=true;
        if(reason=="speech-phase-not-entity-state-evidence" || (bool?)priorFeedback?["excludePhaseProof"]==true)
            e.Feedback["excludePhaseProof"]=true;
        if(priorFeedback?["proofSource"]!=null)e.Feedback["proofSource"]=priorFeedback["proofSource"].DeepClone();
        if(new[]{"speech-proof-current-state-not-speech-start","speech-truth-current-state-not-speech-start"}.Contains(reason) ||
            (bool?)priorFeedback?["bindSpeechStartProof"]==true) {
            e.Feedback["bindSpeechStartProof"]=true;e.Feedback["proofSource"]="speech-start";
        }
        if(reason=="speech-claim-outside-reviewed-operation" || (bool?)priorFeedback?["bindClaimOperationScope"]==true)
            e.Feedback["bindClaimOperationScope"]=true;
        if(new[]{"speech-claim-not-in-current-unit","speech-claim-not-verbatim"}.Contains(reason) || (bool?)priorFeedback?["bindCurrentQuote"]==true)
            e.Feedback["bindCurrentQuote"]=true;
        if(reason=="speech-leadin-repeats-rejected-claim" || (bool?)priorFeedback?["singlePhraseCorrection"]==true)e.Feedback["singlePhraseCorrection"]=true;
        if(output!=null)try {
            var rejected=JObject.Parse(output);
            if(new[]{"speech-evidence-domain-or-coverage-mismatch","speech-perception-domain-conflict"}.Contains(reason) && (string)rejected["claimTime"]=="past-event" &&
                (string)(rejected.SelectToken("grounding.claimDomain")??rejected["claimDomain"])=="visual" &&
                (string)rejected["perceptionSource"]!="current-writer-image") {
                e.Feedback["historicalObservation"]=true;
                if((string)(rejected.SelectToken("grounding.truthWhenSaid")??rejected["truthWhenSaid"])=="unknown")
                    e.Feedback["requireHistoricalUnknownConsistency"]=true;
            }
            // The failed verdict itself selected a historical fact with unknown
            // truth. Retry against history capabilities, not a request that only
            // asks about that fact. The original speech can still be reclassified
            // as a feeling/qualified guess; no correction is authorized here.
            if(reason=="speech-evidence-domain-or-coverage-mismatch" &&
                (string)(rejected.SelectToken("grounding.claimDomain")??rejected["claimDomain"])=="history" &&
                (string)(rejected.SelectToken("grounding.truthWhenSaid")??rejected["truthWhenSaid"])=="unknown") {
                e.Feedback["historicalObservation"]=true;
                e.Feedback["requireHistoricalUnknownConsistency"]=true;
            }
            if(new[]{"speech-temporal-comparison-conflict","speech-evidence-domain-or-coverage-mismatch"}.Contains(reason) &&
                (string)(rejected.SelectToken("grounding.truthWhenSaid")??rejected["truthWhenSaid"])=="unknown")
                e.Feedback["unknownOnlySource"]=rejected.SelectToken("speech.proof.evidenceId")?.DeepClone();
            if(reason=="speech-correction-proposition-proof-missing" || reason=="speech-phase-not-entity-state-evidence")
                e.Feedback["proofSource"]=rejected.SelectToken("speech.proof.evidenceId")?.DeepClone();
            if(reason=="speech-qualification-not-epistemic" && ActivityMonitorProtocol.ScopeOnlyQualifier(
                (string)(rejected.SelectToken("grounding.qualificationQuote")??rejected["qualificationQuote"])))
                e.Feedback["reclassifyScopeOnlyGuess"]=true;
        }catch(Exception){}
        e.Failures++; e.Next = needsNewSource?float.PositiveInfinity:now + (e.Failures == 1 ? 2f : 5f); return e.Failures;
    }
    public void Clear() { entries.Clear(); order.Clear(); }
}
