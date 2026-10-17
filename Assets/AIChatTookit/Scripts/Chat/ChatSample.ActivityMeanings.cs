using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private readonly Dictionary<string,JObject> m_ActivityMeanings=new Dictionary<string,JObject>();
    private static bool NeedsActivityMeaning(JObject frame)=>(string)frame["purpose"]=="speech"&&frame["speechMeaning"]==null;
    private static string ActivityCheckKey(JObject frame)=>NeedsActivityMeaning(frame)?"meaning:"+ActivitySpeechMeaningPolicy.Key(frame):
        ActivityEvidencePolicy.SemanticKey(frame["speechMeaning"]!=null?ActivityMonitorContextPolicy.Project(frame,0):frame);
    private IEnumerable<JObject> ActivityMeaningCandidates(JObject source)
    {
        var seen=new HashSet<string>();
        foreach(var frame in ActivityEvidencePolicy.AttentionFrames(source)) {
            if((string)frame["purpose"]!="speech") {yield return frame;continue;}
            string key=ActivitySpeechMeaningPolicy.Key(frame);
            if(!seen.Add(key))continue;
            if(m_ActivityMeanings.TryGetValue(key,out var meaning)) {
                foreach(var claim in ActivitySpeechMeaningPolicy.Frames(frame,meaning))yield return claim;
            } else yield return frame;
        }
    }
}
