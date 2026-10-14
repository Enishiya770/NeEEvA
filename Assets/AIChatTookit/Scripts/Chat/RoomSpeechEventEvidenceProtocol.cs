using System;
using System.Linq;
using Newtonsoft.Json.Linq;

// Words-only temporal classification selects the review's evidence view. This
// never changes memory, actor permissions, source receipts or the public words.
public static class RoomSpeechEventEvidenceProtocol
{
    public static readonly string[] Times = { "historical-event", "latest-event", "current-state", "future-action", "mixed", "timeless" };

    public static bool AutonomousConversation(JObject input) =>
        (string)input?.SelectToken("eventContext.source")=="owner-validated-engine-event-index" &&
        (string)input.SelectToken("replyPurpose.source")=="accepted-current-autonomous-decision" &&
        (bool?)input.SelectToken("replyPurpose.actionAuthorization")==false &&
        string.IsNullOrEmpty((string)input.SelectToken("replyPurpose.actionId")) &&
        !(input["currentRequestSemantics"] is JObject) && RoomSpeechClaimProtocol.Requirement(input,out _)==null;

    public const string EventOrderContract =
        "When eventOrderComparison/eventOrderConsistency are requested, compare the words' temporal cue with the event time/order for THAT CLAIM'S entity and operation. Write one short comparison of at most 100 characters. A latest-result recap must match that entity's newest recorded outcome; an older opposite outcome cannot establish the latest result. An explicitly earlier event/sequence may use the corresponding older receipts. Arrival and device-operation events concern different operations: a device event cannot invalidate an arrival. No event assertion means not-applicable; missing necessary proof means uncertain. A user request is never completion proof. Read the actual publicSpeech, never invent or substitute its predicate.\n";

    public const string ReviewContract = EventOrderContract +
        "Review the public speech for a NEW autonomous conversation. Historical user commands are context, not new work or a requirement to repeat completed actions. Return only the supplied review schema; never act or rewrite the sentence. " +
        "Read the full speech. independentSpeechClaim contains words-only quotes and timing hints, not factual proof. Questions, invitations, hypothetical/quoted conditions and pure courtesy/feelings do not assert their answers or commit the speaker to action. Check every actual factual assertion and speaker commitment; preserve uncertainty without inventing evidence. " +
        "For each assertion distinguish EVENT, CURRENT STATE and FUTURE COMMITMENT. An execution receipt with phase=arrived proves that the character arrived at that event time. A later distance of 6 meters cannot disprove 'I returned earlier'. No new action ID, matchesRequestedAction=false, or intent=none means no new authority; none invalidates an actual past event. Conversely, past arrival never proves 'I am still beside you'. Use currentUserRelation/currentFacts measurements for present physical position; distance and facing are different. " +
        "For an object operation, latestDeviceEventByEntity identifies the newest receipt for each object. Explicit historical narration retains the older event receipts too. Historical state does not prove an unobserved present state. " +
        "Target consistency compares identity only. The same user/object remains the target after its position or state changes. Do not use current distance or absent action permission to mark a historical target contradicted. A failure explanation is a result; never demand successful output from a failed operation. " +
        "Speaker promises need a current approved plan/execution receipt; an autonomous conversation grants none. Do not invent retries, adjustments, causes, methods, locations or user events. Claims that the user said/did something require a matching role and quote in recentDialogue; intention/persona/previous character speech cannot prove them. Missing visual evidence cannot support new visual claims; reasonable uncertain inference still needs actual visible clues. " +
        "taskRelation checks ONLY actual actor/recipient reversals in speech, not completion or current authorization; ordinary conversation is not-applicable. Compare time and proof separately for each mixed clause. Keep comparisons short (one compact clause per claim); cite receipt index/phase or current measured value rather than copying IDs. Preserve independent resultReport/actualLanguage. factScope=scene-action for historical operations, user-target for current user position, combined for additional visual dependence, none for pure conversation. " +
        "Any unsupported fact/commitment or concrete contradiction requires inconsistent and an exact conflictQuote from publicSpeech. Pure questions/courtesy do not. Supported assertions may be consistent even with no current action. Never equate 'the user originally requested success' with 'truthful failure is inconsistent'.";

    public static string TemporalScope(JObject claim)
    {
        if (!(claim?["otherClaimTimes"] is JArray times) || !(claim["otherClaims"] is JArray quotes) || times.Count != quotes.Count ||
            (string)claim["resultReport"] != "omitted" || times.Any(t=>t.Type!=JTokenType.String || !Times.Contains((string)t))) return null;
        if (times.Count == 0) return "none";
        var scopes=times.Values<string>().Select(t=> t=="historical-event" || t=="latest-event" ? "past-event" : t=="timeless" ? "none" : t).Distinct().ToArray();
        return scopes.Length==1 ? scopes[0] : "mixed";
    }

    public static JObject Project(JObject input)
    {
        var result=(JObject)input.DeepClone();
        var claim=input["independentSpeechClaim"] as JObject;
        if (!AutonomousConversation(input) || TemporalScope(claim)!="past-event") return result;
        // Every independently extracted assertion is about a completed event.
        // Live distance cannot disprove it. Current questions/mixed/future
        // clauses and missing/legacy classifications keep the full evidence.
        if (result["currentRoomFacts"] is JObject room && room["execution"] is JObject execution)
        {
            var receipt=(JObject)execution.DeepClone();receipt.Remove("matchesRequestedAction");
            result["currentRoomFacts"]=new JObject { ["execution"]=receipt,
                ["scope"]="Historical execution receipt. Current pose is irrelevant to these event-only assertions; no new action is authorized." };
        }
        ((JObject)result["eventContext"]).Remove("currentUserRelation");
        // A grammatical classifier can mistake a multi-event clause for a
        // latest-event label. Retain every outcome; the engine index identifies
        // the newest per entity without deleting an earlier true event.
        result["speechEventEvidenceView"]=new JObject { ["source"]="independent-words-only-time-scope",["temporalScope"]="past-event",
            ["omittedCurrentPose"]=true,["historicalEventsRetained"]=true,["engineMemoryMutated"]=false,
            ["scope"]="Judge the asserted completed events against their receipts. Latest-event refers to the latest outcome for its target, not any older opposite result. Explicit earlier-event narration retains chronological history. No event requires a new action submission." };
        return result;
    }
}
