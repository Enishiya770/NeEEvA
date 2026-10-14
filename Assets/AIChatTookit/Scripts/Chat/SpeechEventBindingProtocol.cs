using System;
using System.Linq;
using Newtonsoft.Json.Linq;

// Event claims are extracted from words alone. The factual reviewer may select
// an engine receipt, but cannot rewrite the extracted actor/operation/outcome.
public static class SpeechEventBindingProtocol
{
    public const string ExtractionContract =
        "Also output eventClaims ONLY for concrete ENDED actions: COMPLETED/FAILED/CANCELLED, asserted anywhere including relative clauses. Ongoing/progressive actions ('I am following you', 'あなたについて移動している'), preparation, waiting and recovery are CURRENT states, never completed events: leave them to resultReport/otherClaims and output NO eventClaims for those clauses. Promises, questions, pure courtesy, hypothetical events and negated non-events ('no retry was submitted') are not ended actions. " +
        "Perceptual descriptions remain in otherClaims for visual review; they are not tool-execution events. A standalone acknowledgement such as 'confirmed/understood', '確認しました', '明白了' has NO concrete operated object and adds NO eventClaims. Never turn that opener into operation=inspect. Actual inspection must assert examining a concrete target. Each item: quote (exact intact clause including its actor and object markers), actor (CHARACTER for the speaker/I; USER for the listener/you; OTHER; UNSPECIFIED only if grammar and context within this sentence do not identify one), operation (neutral English action key), state (operation's requested state if explicit, otherwise empty), outcome (completed/failed/cancelled), time (historical-event/latest-event). " +
        "Normalize device state changes to operation=set-state and state=on/off; arrival to approach, following to follow, stopping movement to stop-moving, playback to play-audio, examination to inspect. Other actions use their neutral English verb; never force them into a device or movement category. " +
        "Preserve agent roles in passive, causative, omitted-subject and honorific constructions. Japanese respectful verbs describe the respected OTHER participant (USER in a direct reply), not the speaker's own action; humble verbs may describe CHARACTER. Do not discard actor or time markers when quoting. Distinguish 'the file you saved' from 'I saved the file'. A command/request is not proof that its listener performed it. " +
        "time=historical-event for ordinary past actions, including just/recently/さっき/先ほど; latest-event ONLY when the words explicitly assert the newest/last/final event. Recency is not uniqueness. " +
        "Speech acts ('acknowledge', 'understand', 'confirm' without a concrete examined target) belong to courtesy, NOT this engine-event index. For '確認しました。ドアは閉まっています。', eventClaims=[]: courtesy plus a current state, no asserted closing action. No event assertions means []. " +
        "The eventClaims restriction NEVER changes resultReport: in reportDomain=movement, current walking/following MUST still be moving with evidence, even though eventClaims=[]. Do not replace an explicit current activity with omitted. " +
        "For 'Export failed. No upload was attempted.', extract ONLY the attempted export as outcome=failed; NEVER create an upload event with outcome=failed. The negated non-attempt stays in otherClaims and is checked against the receipt's reason/attempt status. Failed attempt and absent attempt are different.\n";
    public const string ReviewContract =
        "eventClaims are independent words-only action assertions. For EACH in order, eventBindings must select an eventId from eventEvidence or empty when unsupported, and classify targetMatch as matched/uncertain/contradicted. " +
        "Match the entire quoted event: actor, operation, requested state, outcome, entity and time. A receipt for reopening an object cannot support earlier closing; a character receipt cannot support a user action. Same object or phase=performed alone is insufficient. " +
        "A latest-event claim must match that target's latest receipt for that operation; explicit earlier narration may reference the corresponding older receipt. Current state is not proof of a past operation. " +
        "Never alter eventClaims to fit receipts. Match explicit object nouns and attributes against targetDescription/entityName/entityKind; sharing an operation does not make different objects identical. Missing target semantics is uncertain, not a guessed match. Empty/mismatched evidence means inconsistent (or uncertain), with the actual quote. Generic inspect/play/save actions use their own receipts. Event evidence is historical and authorizes no new action.\n";

    private static JObject Text(int max = 160) => new JObject { ["type"]="string", ["maxLength"]=max };
    private static string Operation(string op)
    {
        switch(op) {
            case "set-object-state": return "set-state";
            case "inspect-object": case "inspection": case "check": return "inspect";
            case "approach-object": return "approach";
            case "look-at": return "look";
            case "playback": return "play-audio";
            case "turn-in-place": return "turn";
            case "reposition-view": return "reposition";
            default: return op;
        }
    }
    private static JObject Enum(params string[] values) => new JObject { ["type"]="string", ["enum"]=new JArray(values) };
    public static void AddExtractionSchema(JObject schema)
    {
        schema["properties"]["eventClaims"] = new JObject { ["type"]="array", ["maxItems"]=4,
            ["items"]=new JObject { ["type"]="object", ["additionalProperties"]=false,
                ["required"]=new JArray("quote","actor","operation","state","outcome","time"),
                ["properties"]=new JObject { ["quote"]=Text(), ["actor"]=Enum("CHARACTER","USER","OTHER","UNSPECIFIED"),
                    ["operation"]=Text(40), ["state"]=Text(40), ["outcome"]=Enum("completed","failed","cancelled"),
                    ["time"]=Enum("historical-event","latest-event") } } };
        ((JArray)schema["required"]).Add("eventClaims");
    }
    public static bool ValidateClaims(JToken value, string speech)
    {
        if (!(value is JArray rows) || rows.Count>4) return false;
        return rows.All(v => v is JObject d && d.Count==6 &&
            new[]{"quote","actor","operation","state","outcome","time"}.All(k=>d[k]?.Type==JTokenType.String) &&
            !string.IsNullOrWhiteSpace((string)d["quote"]) && ((string)d["quote"]).Length<=160 &&
            speech.IndexOf((string)d["quote"],StringComparison.Ordinal)>=0 &&
            new[]{"CHARACTER","USER","OTHER","UNSPECIFIED"}.Contains((string)d["actor"]) &&
            !string.IsNullOrWhiteSpace((string)d["operation"]) && ((string)d["operation"]).Length<=40 && ((string)d["state"]).Length<=40 &&
            new[]{"completed","failed","cancelled"}.Contains((string)d["outcome"]) &&
            new[]{"historical-event","latest-event"}.Contains((string)d["time"]));
    }
    public static JArray Claims(JObject input) => input.SelectToken("independentSpeechClaim.eventClaims") as JArray;
    public static void AddReviewSchema(JObject schema, JObject input)
    {
        var claims=Claims(input); if(claims==null || claims.Count==0)return;
        var ids=Evidence(input).OfType<JObject>().Select(e=>(string)e["eventId"]).Concat(new[]{""});
        schema["properties"]["eventBindings"]=new JObject { ["type"]="array",["minItems"]=claims.Count,["maxItems"]=claims.Count,
            ["items"]=new JObject { ["type"]="object",["additionalProperties"]=false,
                ["required"]=new JArray("eventId","targetMatch"),["properties"]=new JObject {
                    ["eventId"]=new JObject { ["type"]="string",["enum"]=new JArray(ids) },
                    ["targetMatch"]=Enum("matched","uncertain","contradicted") } } };
        ((JArray)schema["required"]).Add("eventBindings");
        var original=(JObject)schema["properties"];
        schema["properties"]=new JObject(new JProperty("eventBindings",original["eventBindings"].DeepClone()),
            original.Properties().Where(p=>p.Name!="eventBindings").Select(p=>new JProperty(p.Name,p.Value.DeepClone())));
    }

    public static JArray Evidence(JObject input)
    {
        // Only explicit runtime receipt fields are read. Never recursively mine
        // user text, model prose, a desired plan or previous assistant promises.
        var result=new JArray();
        void Add(JObject d, string path, string operation=null, string state=null, string description=null)
        {
            if(d==null)return;
            string phase=(string)d["phase"], action=(string)d["actionId"];
            string outcome=new[]{"performed","arrived","completed","stopped","turned"}.Contains(phase) ? "completed" :
                new[]{"failed","rejected","blocked","unavailable","unreachable","goal-not-reached","orientation-not-reached"}.Contains(phase) ? "failed" :
                phase=="cancelled"||phase=="replaced" ? "cancelled" : null;
            string op=Operation((string)d["intent"]??operation??(string)d["kind"]);
            if(op==null && (string)d["targetKind"]=="user" && phase=="arrived")op="approach";
            if(op==null && (string)d["targetKind"]=="user" && phase=="stopped")op="stop-moving";
            if(outcome==null || string.IsNullOrEmpty(action)||string.IsNullOrEmpty(op))return;
            string entity=(string)d["entityId"]??(string)d["sceneTargetId"];
            if(string.IsNullOrEmpty(entity))entity=(string)d["targetKind"]=="user"?"USER":null;
            if(string.IsNullOrEmpty(entity) && (string)d["targetKind"]=="scene-view" && op=="turn")entity="CHARACTER-VIEW";
            if(string.IsNullOrEmpty(entity))return;
            if(result.OfType<JObject>().Any(e=>(string)e["eventId"]==action))return;
            string actor=(string)d["actor"]??"CHARACTER";
            if(!new[]{"CHARACTER","USER","OTHER"}.Contains(actor))return;
            result.Add(new JObject { ["eventId"]=action,["actor"]=actor,["operation"]=op,["entityId"]=entity,
                ["state"]=(string)d["requestedState"]??state??"",["outcome"]=outcome,
                ["targetDescription"]=(string)d["targetDescription"]??description,
                ["entityName"]=d["entityName"]?.DeepClone(),["entityKind"]=d["entityKind"]?.DeepClone(),
                ["evidenceScope"]=d["evidenceScope"]?.DeepClone(),
                ["observedUtc"]=d["observedUtc"]?.DeepClone(),["sourcePath"]=path });
        }
        void Device(JObject packet,string path)
        {
            Add(packet?["lastAction"] as JObject,path+".lastAction",null,(string)packet?.SelectToken("goal.desiredState"),
                (string)packet?.SelectToken("goal.targetDescription"));
            // A successful state write and its subsequent readback are distinct
            // events. The explicit validated proof supports "checked its state";
            // it does not invent an inspection from any generic completed action.
            var proof=packet?.SelectToken("goal.completionEvidence") as JObject;
            if((bool?)packet?.SelectToken("goal.completionEvidenceValid")==true &&
                proof?.SelectToken("actual.on")?.Type==JTokenType.Boolean &&
                !string.IsNullOrEmpty((string)proof["actionId"]) &&
                !string.IsNullOrEmpty((string)proof["entityId"]) &&
                (string)proof["actionId"]==(string)packet.SelectToken("lastAction.actionId") &&
                (string)proof["entityId"]==(string)packet.SelectToken("lastAction.entityId"))
                Add(new JObject { ["actionId"]=(string)proof["actionId"]+":readback",["intent"]="inspect",
                    ["entityId"]=proof["entityId"].DeepClone(),["phase"]="completed",
                    ["requestedState"]=(bool)proof.SelectToken("actual.on")?"on":"off",
                    ["observedUtc"]=(proof["readAtUtc"]??proof["observedUtc"])?.DeepClone(),
                    ["evidenceScope"]="Validated device state readback only; not physical touching, disassembly or a broader inspection."
                },path+".goal.completionEvidence",description:(string)packet.SelectToken("goal.targetDescription"));
        }
        if(input["historicalDeviceEvents"] is JArray history)
            for(int i=0;i<history.Count;i++)Device(history[i] as JObject,"historicalDeviceEvents["+i+"]");
        Device(input["sceneActionFacts"] as JObject,"sceneActionFacts");
        Add(input.SelectToken("currentRoomFacts.execution") as JObject,"currentRoomFacts.execution");
        Add(input.SelectToken("eventContext.latestExecutionEvent") as JObject,"eventContext.latestExecutionEvent");
        // Every completed sequence step retains its executor's own receipt.
        // Step kind/status alone is NOT execution proof.
        if(input.SelectToken("taskSequence.steps") is JArray steps)
            foreach(JObject step in steps.OfType<JObject>()) {
                var receipt=step["receipt"] as JObject;
                Add(receipt,"taskSequence.steps.receipt");
                Add(receipt?["execution"] as JObject,"taskSequence.steps.receipt.execution");
                Device(receipt,"taskSequence.steps.receipt");
            }
        // Generic result receipts use the same explicit event tuple. Callers may
        // supply completed playback/inspection/tool events without a scene map.
        if(input.SelectToken("eventContext.engineEvents") is JArray engine)
            foreach(JObject e in engine.OfType<JObject>())Add(e,"eventContext.engineEvents");
        return result;
    }

    public static string Reject(JObject input,JObject review,out string quote)
    {
        quote="";var claims=Claims(input);if(claims==null||claims.Count==0)return null;
        var bindings=review["eventBindings"] as JArray;var evidence=Evidence(input);
        for(int i=0;i<claims.Count;i++)
        {
            var c=(JObject)claims[i];quote=(string)c["quote"];
            if(bindings==null||bindings.Count!=claims.Count||!(bindings[i] is JObject b))return "event-binding-missing";
            var e=evidence.OfType<JObject>().FirstOrDefault(v=>(string)v["eventId"]==(string)b["eventId"]);
            if(e==null||(string)b["targetMatch"]!="matched")return "event-evidence-unconfirmed";
            if((string)c["actor"]!="UNSPECIFIED"&&(string)c["actor"]!=(string)e["actor"])return "event-actor-mismatch";
            string claimOperation=Operation((string)c["operation"]);
            if(claimOperation!=(string)e["operation"]||(string)c["outcome"]!=(string)e["outcome"]||
                !string.IsNullOrEmpty((string)c["state"])&&(string)c["state"]!=(string)e["state"])return "event-operation-mismatch";
            if((string)c["time"]=="latest-event") {
                if(!DateTimeOffset.TryParse((string)e["observedUtc"],out var when))return "event-order-unconfirmed";
                foreach(var related in evidence.OfType<JObject>().Where(v=>(string)v["entityId"]==(string)e["entityId"] &&
                    (string)v["operation"]==(string)e["operation"])) {
                    if(!DateTimeOffset.TryParse((string)related["observedUtc"],out var later))return "event-order-unconfirmed";
                    if(later>when)return "event-order-mismatch";
                }
            }
        }
        quote="";return null;
    }
}
