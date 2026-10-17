using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// All purposes still share twelve attempts/minute. Background completed audio
// and visual acquisition cannot spend the two slots reserved for live/delivery work.
public static class ActivityMonitorSchedulePolicy
{
    public static int Ceiling(JObject frame) => (string)frame?["purpose"]=="delivery"?12:
        (string)frame?["purpose"]=="action" || (frame?["speech"] as JArray??new JArray())
            .Any(s=>new[]{"starting","playing","queued"}.Contains((string)s["state"]))?11:10;
    public static float RetryAt(IEnumerable<float> times,float now,int ceiling)
    {
        var active=times.Where(t=>now-t<60f).OrderBy(t=>t).ToArray();
        return active.Length<ceiling?now:active[active.Length-ceiling]+60.001f;
    }
    public static int Priority(JObject frame,string last)
    {
        string purpose=(string)frame["purpose"];
        if(purpose=="delivery" && last!="delivery")return 0;
        if(purpose!=last && (purpose=="action" || (frame["speech"] as JArray??new JArray()).Any(s=>
            new[]{"starting","playing","queued"}.Contains((string)s["state"]))))return 1;
        return purpose==last?3:2;
    }
}
