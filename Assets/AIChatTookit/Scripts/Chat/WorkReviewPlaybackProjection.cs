using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// A review of delivered results consumes audio receipts, not abandoned drafts.
// Resident text, user instructions and future intentions are never mutated.
public static class WorkReviewPlaybackProjection
{
    public static void Apply(IEnumerable<JObject> documents)
    {
        var roots=documents.ToArray();var drafts=new HashSet<string>();
        foreach(var root in roots) {
            if(!(root["speech"] is JObject speech) || !(root["delivery"]?["results"] is JArray))continue;
            var playback=speech["lastPublicPlayback"] as JObject;
            if(playback?["completedPlaybackText"]?.Type!=JTokenType.String)continue;
            foreach(var field in new[]{speech.Property("lastGeneratedText"),playback.Property("publicText")})
                if(field?.Value.Type==JTokenType.String) {
                    drafts.Add((string)field.Value);
                    var path=field.Path;var text=(string)field.Value;
                    field.Replace(new JProperty(field.Name+"Source",new JObject{
                        ["runtimePath"]=path,["stamp"]=ActivityEvidencePolicy.Stamp(new JValue(text))}));
                }
            if(playback["latestPlaybackRevision"] is JObject revision && revision.Properties().All(p=>new[]{
                "generation","userTurn","actionId","revision","observedUtc","before","after","publicText","currentRelativePose",
                "arrivalObservedUtc","phaseNeutral","disposition","scope"}.Contains(p.Name))) {
                if(revision["publicText"]?.Type==JTokenType.String)revision.Remove("publicText");
                foreach(var key in new[]{"before","after"})
                    if(revision[key] is JObject stage && stage.Properties().All(p=>new[]{"actionId","phase","operationRevision","reportText","arrivalRelation","expectedReport"}.Contains(p.Name)))stage.Remove("reportText");
            }
            speech["deliveryEvidenceScope"]="Only each result's completed audio/version proves delivery. *Source points to generated drafts retained in runtime, never played proof. Playing/queued text is incomplete. Later completion cannot contradict an earlier progress report. New intentions are separate.";
        }
        foreach(var root in roots)
            if(root["responses"] is JArray responses)
                for(int i=0;i<responses.Count;i++)
                    if(responses[i].Type==JTokenType.String && drafts.Contains((string)responses[i])) {
                        string text=(string)responses[i];responses[i]=new JObject{["kind"]="generated-only",
                            ["runtimePath"]="responses["+i+"]",["stamp"]=ActivityEvidencePolicy.Stamp(new JValue(text))};
                    }
    }
}
