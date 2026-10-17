using System;
using System.Linq;
using Newtonsoft.Json.Linq;

/// <summary>Audio boundaries are not semantic boundaries. This never changes playback evidence.</summary>
public static class ActivitySpeechWindowPolicy
{
    public static bool EndsClause(string text) => !string.IsNullOrWhiteSpace(text) &&
        "。！？.!?;；…".IndexOf(text.TrimEnd().Last()) >= 0;

    public static string Join(string left,string right) => left +
        (!string.IsNullOrEmpty(left)&&!string.IsNullOrEmpty(right)&&left.Last()<128&&right[0]<128&&
         char.IsLetterOrDigit(left.Last())&&char.IsLetterOrDigit(right[0])?" ":"") + right;

    public static bool Compatible(JObject left,JObject right)
    {
        if(left==null||right==null)return false;
        JObject Identity(JObject source) {
            var copy=(JObject)source.DeepClone();
            // Evidence may arrive while one sentence is playing. It is neither
            // a new capture nor a change to the frozen execution observation.
            foreach(var p in copy.Properties().Where(p=>p.Name.StartsWith("visual:",StringComparison.Ordinal)).ToArray())p.Remove();
            copy.Remove("scope");
            return copy;
        }
        return ActivityEvidencePolicy.Equal(Identity(left),Identity(right));
    }
}
