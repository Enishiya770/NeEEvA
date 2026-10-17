using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// Small attention queue. A terminal unchecked item is never a verified fact.
// Source pixels and semantic bindings are frozen at enqueue, not read at dispatch.
public sealed class ActivitySourceCheckQueue
{
    public sealed class Job
    {
        public string Key,Owner,Image,State="waiting";
        public JObject Input;
        public float CreatedAt,RetryAt;
        public int Attempts;
    }
    readonly List<Job> jobs=new List<Job>();
    readonly Queue<string> closed=new Queue<string>();
    public Action<JObject> Changed;
    public int Count=>jobs.Count;
    public const float Lifetime=75f;
    public static string Key(string capture,JArray claims,int version)=>capture+":"+ActivityEvidencePolicy.Stamp(
        new JObject{["version"]=version,["claims"]=new JArray(claims.OfType<JObject>()
            .OrderBy(c=>(string)c["speechId"],StringComparer.Ordinal).ThenBy(c=>(string)c["claimQuote"],StringComparer.Ordinal)
            .Select(c=>new JObject{["speechId"]=c["speechId"],["claimQuote"]=c["claimQuote"]}))});
    public bool Enqueue(string key,string owner,JObject input,string image,float now)
    {
        if(closed.Contains(owner+"|"+key)||jobs.Any(j=>j.Key==key&&j.Owner==owner))return false;
        var job=new Job{Key=key,Owner=owner,Input=(JObject)input.DeepClone(),Image=image,CreatedAt=now};
        if(jobs.Count>=4){Emit(job,"capacity-unchecked",now);return false;}
        jobs.Add(job);Emit(job,"queued",now);return true;
    }
    public void Maintain(string owner,float now)
    {
        foreach(var job in jobs.ToArray())
            if(job.Owner!=owner)Close(job,"owner-replaced",now);
            else if(now-job.CreatedAt>=Lifetime)Close(job,"expired-unchecked",now);
    }
    public Job Take(string owner,float now,float retryAt)
    {
        Maintain(owner,now);
        var job=jobs.FirstOrDefault(j=>j.State=="waiting");
        if(job==null)return null;
        if(retryAt>now) {
            if(Math.Abs(job.RetryAt-retryAt)>.01f){job.RetryAt=retryAt;Emit(job,"rate-limited",now);}
            return null;
        }
        job.State="requesting";job.Attempts++;Emit(job,"requesting",now);return job;
    }
    public void Close(Job job,string reason,float now)
    { if(!jobs.Remove(job))return;job.State=reason;Emit(job,reason,now);job.Image=null;
      closed.Enqueue(job.Owner+"|"+job.Key);while(closed.Count>32)closed.Dequeue(); }
    public void Cancel(string reason,float now)
    { foreach(var job in jobs.ToArray())Close(job,reason,now); }
    void Emit(Job job,string status,float now)=>Changed?.Invoke(new JObject{
        ["key"]=job.Key,["owner"]=job.Owner,["state"]=status,["ageSeconds"]=Math.Max(0,now-job.CreatedAt),
        ["retryAt"]=job.RetryAt,["attempts"]=job.Attempts,["captureId"]=job.Input["captureId"]?.DeepClone(),
        ["claims"]=job.Input["claims"]?.DeepClone(),["evidenceReceived"]=status=="evidence-received",
        ["reviewed"]=false,
        ["scope"]="Source acquisition only; unresolved evidence and literal interpretation do not certify truth or authorize actions."});
}
