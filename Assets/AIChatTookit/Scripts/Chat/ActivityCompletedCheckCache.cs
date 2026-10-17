using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// Reuse supported completed events, and the semantic classification of the
// exact same nonfactual sentence across playback states. Never cache unknown
// factual claims or live assertions as supported facts.
public sealed class ActivityCompletedCheckCache
{
    sealed class Entry { public JObject Decision; public string Fingerprint; }
    readonly Dictionary<string,Entry> entries=new Dictionary<string,Entry>();
    public void Clear()=>entries.Clear();
    static string Identity(JObject f)=>ActivityEvidencePolicy.Stamp(new JObject{["owner"]=f["owner"]?.DeepClone(),
        ["speech"]=new JArray((f["speech"] as JArray??new JArray()).Select(u=>new JObject{["id"]=u["id"],["version"]=u["version"],["text"]=u["text"],["language"]=u["language"]})),
        ["operation"]=f["reviewOperation"]?.DeepClone(),["meaning"]=f["speechMeaning"]?.DeepClone()});
    static bool Completed(JObject f)=>(string)f["purpose"]=="speech" && f["speech"] is JArray rows && rows.Count==1 && (string)rows[0]["state"]=="completed";
    static bool Speech(JObject f)=>(string)f["purpose"]=="speech" && f["speech"] is JArray rows && rows.Count==1 &&
        new[]{"queued","starting","playing","completed"}.Contains((string)rows[0]["state"]);
    static bool Nonfactual(JObject d)=>new[]{"personal-expression","invitation-or-question","qualified-guess"}.Contains((string)d["speechAct"]);
    static bool SameCaptureVisual(JObject f,JObject d)
    {
        var meaning=f["speechMeaning"];
        if((string)meaning?["domain"]!="visual" || (string)meaning["source"]!="current-writer-image" ||
            (string)d["truthWhenSaid"]!="supported")return false;
        var unit=(f["speech"] as JArray)?.FirstOrDefault() as JObject;
        string capture=(string)ActivityEvidencePolicy.FrozenSpeech(unit,f,true)?["formalObservation"]?["captureId"];
        var ids=(d["truthEvidenceIds"] as JArray??new JArray()).Values<string>().ToArray();
        return !string.IsNullOrEmpty(capture) && ids.Length>0 && ids.All(id=>
            (string)f["facts"]?[id]?["source"]=="same-capture-visual-check" &&
            (string)f["facts"][id]["assessment"]=="supported" && (string)f["facts"][id]["captureId"]==capture &&
            (string)f["facts"][id]["claimQuote"]==(string)meaning["quote"]);
    }
    static string Fingerprint(JObject f,JObject d)
    {
        // Runtime keeps the full frozen sample; wire packets may alias it as
        // speech-start. Compare their canonical evidence, not JSON packaging.
        if(f["speechMeaning"]!=null)f=ActivityMonitorContextPolicy.Project(f,2);
        var facts=f["facts"] as JObject??new JObject();var selected=new JObject{["constraints"]=facts["constraints"]?.DeepClone()};
        if(Nonfactual(d)) {
            // A new request/restriction can change the meaning of an invitation;
            // unrelated pixels, clocks and movement cannot change its words.
            selected["request"]=facts["request"]?.DeepClone();
            return ActivityEvidencePolicy.Stamp(selected);
        }
        foreach(string id in (d["truthEvidenceIds"] as JArray??new JArray()).Values<string>()) {
            if(facts[id]==null)return null;
            selected[id]=facts[id].DeepClone();selected["scope:"+id]=f["evidenceScopes"]?[id]?.DeepClone();
        }
        selected["frozen"]=ActivityEvidencePolicy.FrozenSpeech((JObject)((JArray)f["speech"])[0],f);
        return ActivityEvidencePolicy.Stamp(selected);
    }
    public bool Contains(JObject frame)=>Speech(frame)&&entries.TryGetValue(Identity(frame),out var e)&&
        (Nonfactual(e.Decision)||Completed(frame)||SameCaptureVisual(frame,e.Decision))&&
        e.Fingerprint==Fingerprint(frame,e.Decision);
    public bool ContainsNonfactual(JObject context,JObject unit)
    {
        var frame=(JObject)context.DeepClone();frame["purpose"]="speech";frame["speech"]=new JArray(unit.DeepClone());
        return Speech(frame)&&entries.TryGetValue(Identity(frame),out var e)&&Nonfactual(e.Decision)&&
            e.Fingerprint==Fingerprint(frame,e.Decision);
    }
    public void Remember(JObject frame,JObject decision)
    {
        if(!Speech(frame)||(string)decision["speech"]?["issue"]!="none")return;
        if(!Nonfactual(decision) && !SameCaptureVisual(frame,decision) && (!Completed(frame)||((string)decision["claimTime"]!="past-event"&&ActivitySpeechMeaningPolicy.KnownOngoingTruth(frame)!="supported")||
            (string)decision["truthWhenSaid"]!="supported"||((decision["truthEvidenceIds"] as JArray)?.Count??0)<=0))return;
        string stamp=Fingerprint(frame,decision);if(stamp==null)return;
        if(entries.Count>=48)entries.Remove(entries.Keys.First());
        entries[Identity(frame)]=new Entry{Decision=(JObject)decision.DeepClone(),Fingerprint=stamp};
    }
}
