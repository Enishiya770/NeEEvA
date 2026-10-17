using System;
using System.Linq;
using Newtonsoft.Json.Linq;

// Source ownership is independent of the camera offered to the answer writer.
// Retained observations are not silently promoted to current visibility.
public static class ActivityClaimSourcePolicy
{
    public static JObject QuestionContext(JObject frame)
    {
        var request=frame["facts"]?["request"];
        string question=(string)(request is JObject r?r["text"]:request?.Type==JTokenType.String?request:null);
        if(string.IsNullOrEmpty(question))return null;
        string stamp=ActivityEvidencePolicy.Stamp(new JValue(question));
        if(frame["claimSourceContext"] is JObject retained && (string)retained["requestStamp"]==stamp)return retained;
        var sources=new JArray((frame["facts"] as JObject??new JObject()).Properties().Where(p=>
            p.Name.StartsWith("recalled:",StringComparison.Ordinal) &&
            ((string)p.Value["question"]==question || (string)p.Value["questionSource"]=="request") &&
            !string.IsNullOrEmpty((string)p.Value["sourceCaptureId"])).Select(p=>new JObject{
                ["id"]=p.Name,["captureId"]=p.Value["sourceCaptureId"]}));
        return sources.Count==0?null:new JObject{["requestStamp"]=stamp,["retainedSources"]=sources};
    }
    public static bool CurrentImageAllowed(JObject frame,JObject verdict)
    {
        string domain=(string)verdict["claimDomain"]??(string)verdict.SelectToken("grounding.claimDomain");
        if(domain=="history" || (string)verdict["perceptionSource"]=="earlier-observation")return false;
        if((string)verdict["claimTime"]!="past-event")return true;
        // A source actually recalled for this question is a stronger temporal
        // binding than the incidental current image attached to the answer.
        // This does not ban past-tense descriptions of a newly inspected image.
        return QuestionContext(frame)==null;
    }
    public const string Repair = "This past assertion answers a question with retained earlier observation sources. The answer writer's incidental current image is not evidence against that earlier event. Bind the claim to its earlier source and time; preserve uncertainty if those sources cannot establish it. Do not deny the event or invent a new observation.";
}
