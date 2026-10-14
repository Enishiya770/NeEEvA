using Newtonsoft.Json.Linq;

public static partial class RoomSpeechClaimRegression
{
    private static void CheckEventEvidence()
    {
        var claim=Claim("omitted", "");claim["otherClaims"]=new JArray("I came back earlier.");
        claim["otherClaimTimes"]=new JArray("latest-event");
        Check(RoomSpeechClaimProtocol.TryRead(claim.ToString(),"I came back earlier.",out _),"Exact quote and corresponding temporal label are retained.");
        var invalid=(JObject)claim.DeepClone();invalid["otherClaimTimes"]=new JArray();
        Check(!RoomSpeechClaimProtocol.TryRead(invalid.ToString(),"I came back earlier.",out _),"Missing quote-time correspondence cannot select reduced evidence.");
        var source=new JObject {
            ["publicSpeech"]="I came back earlier.",["independentSpeechClaim"]=claim,
            ["replyPurpose"]=new JObject{["source"]="accepted-current-autonomous-decision",["actionAuthorization"]=false},
            ["currentRoomFacts"]=new JObject{["execution"]=new JObject{["actionId"]="past-arrival",["phase"]="arrived",["matchesRequestedAction"]=false},
                ["currentFacts"]=new JObject{["actualUserDistance"]=6}},
            ["eventContext"]=new JObject{["source"]="owner-validated-engine-event-index",["currentUserRelation"]=new JObject{["actualUserDistance"]=6},
                ["latestDeviceEventByEntity"]=new JArray(new JObject{["entityId"]="A",["historicalDeviceEventIndex"]=1},new JObject{["entityId"]="B",["historicalDeviceEventIndex"]=2})},
            ["historicalDeviceEvents"]=new JArray(
                new JObject{["lastAction"]=new JObject{["entityId"]="A",["requestedState"]="off"}},
                new JObject{["lastAction"]=new JObject{["entityId"]="A",["requestedState"]="on"}},
                new JObject{["lastAction"]=new JObject{["entityId"]="B",["requestedState"]="off"}}) };
        string before=source.ToString();var projected=RoomSpeechEventEvidenceProtocol.Project(source);
        Check(source.ToString()==before && projected.SelectToken("currentRoomFacts.currentFacts")==null &&
            (string)projected.SelectToken("currentRoomFacts.execution.phase")=="arrived" &&
            projected.SelectToken("eventContext.currentUserRelation")==null,"Past-event projection preserves arrival and original memory while excluding irrelevant live pose.");
        Check(JToken.DeepEquals(projected["historicalDeviceEvents"],source["historicalDeviceEvents"]) &&
            JToken.DeepEquals(projected.SelectToken("eventContext.latestDeviceEventByEntity"),source.SelectToken("eventContext.latestDeviceEventByEntity")),
            "All chronological events and the latest index per entity survive even a mistaken grammatical timing label.");
        Check(RoomSpeechEventEvidenceProtocol.AutonomousConversation(source),"Owned autonomous conversation can use the focused factual contract.");
        var schema=JObject.Parse(RoomTaskProtocol.BuildSpeechReviewSchemaForInput(source.ToString()));
        Check(schema.SelectToken("properties.eventOrderComparison")!=null && schema.SelectToken("properties.eventOrderConsistency")!=null,
            "Event order is compared explicitly before an autonomous reply verdict.");
        var bad=new JObject{["verdict"]="consistent",["reason"]="old outcome",["eventOrderComparison"]="latest receipt contradicts",["eventOrderConsistency"]="contradicted"};
        Check(!RoomTaskProtocol.ReviewAllows(bad.ToString()),"A matching old event cannot override a contradictory latest outcome.");
        bad["verdict"]="inconsistent";
        Check(RoomTaskProtocol.TryReviewScope(bad.ToString(),out _),"Event order contradiction remains a valid bounded-correction rejection.");
        bad["verdict"]="consistent";bad["eventOrderConsistency"]="uncertain";
        Check(!RoomTaskProtocol.ReviewAllows(bad.ToString()),"Unresolved event order never silently approves a reply.");
        foreach(string time in new[]{"current-state","future-action","mixed","timeless"}) {
            var copy=(JObject)source.DeepClone();copy["independentSpeechClaim"]["otherClaimTimes"]=new JArray(time);
            Check(JToken.DeepEquals(copy,RoomSpeechEventEvidenceProtocol.Project(copy)),"Other claim scopes retain all current and historical evidence: "+time);
        }
        var earlier=(JObject)source.DeepClone();earlier["independentSpeechClaim"]["otherClaimTimes"]=new JArray("historical-event");
        Check(((JArray)RoomSpeechEventEvidenceProtocol.Project(earlier)["historicalDeviceEvents"]).Count==3,"Explicit earlier narration retains the complete chronological outcomes.");
        var question=(JObject)source.DeepClone();question.Remove("replyPurpose");
        Check(JToken.DeepEquals(question,RoomSpeechEventEvidenceProtocol.Project(question)),"A real current question cannot hide live evidence by mentioning a past event.");
        Check(!RoomSpeechEventEvidenceProtocol.AutonomousConversation(question),"A real current question retains the complete general review contract.");
        var broken=(JObject)source.DeepClone();broken["eventContext"]["latestDeviceEventByEntity"][0]["historicalDeviceEventIndex"]=2;
        Check(((JArray)RoomSpeechEventEvidenceProtocol.Project(broken)["historicalDeviceEvents"]).Count==3,"Stale or mismatched identity index cannot remove old event evidence.");
        var legacy=(JObject)source.DeepClone();((JObject)legacy["independentSpeechClaim"]).Remove("otherClaimTimes");
        Check(JToken.DeepEquals(legacy,RoomSpeechEventEvidenceProtocol.Project(legacy)),"Legacy missing time classification retains full evidence.");
    }
}
