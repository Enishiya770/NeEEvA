using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private string m_ActivityReplyImage;
    private readonly HashSet<string> m_ActivityVisualAttempts=new HashSet<string>();
    private readonly Dictionary<string,JObject> m_ActivityVisualEvidence=new Dictionary<string,JObject>();
    private bool HasActivityVisualEvidence(string id,string quote=null,string capture=null)=>m_ActivityVisualEvidence.Values.Any(row=>
        (string)row["speechId"]==id && (quote==null||(string)row["claimQuote"]==quote) && (capture==null||(string)row["captureId"]==capture));
    private void RememberActivityVisualEvidence(JArray evidence)
    {
        foreach(var row in evidence.OfType<JObject>()) {
            string key=(string)row["speechId"]+":"+ActivityEvidencePolicy.Stamp(new JArray(row["claimQuote"],row["captureId"]));
            m_ActivityVisualEvidence[key]=(JObject)row.DeepClone();
        }
    }
    private JObject ActivitySpeechWindow(ActivityMonitorProtocol.SpeechUnit unit)
    {
        var value=unit.Snapshot();int index=m_ActivitySpeech.IndexOf(unit);
        if(index<=0||!unit.Exposed||unit.IsCorrection)return value;
        string text=unit.Text;var ids=new JArray();
        for(int i=index-1;i>=0&&ids.Count<4;i--) {
            var prior=m_ActivitySpeech[i];
            if(prior.State!="completed"||prior.IsCorrection||prior.CorrectedAfterExposure||prior.Language!=unit.Language||
                string.IsNullOrWhiteSpace(prior.Text)||ActivitySpeechWindowPolicy.EndsClause(prior.Text)||
                !ActivitySpeechWindowPolicy.Compatible(prior.ObservedAtSpeechStart,unit.ObservedAtSpeechStart))break;
            string combined=ActivitySpeechWindowPolicy.Join(prior.Text,text);
            if(combined.Length>480)break;
            text=combined;ids.Insert(0,new JValue(prior.Id));
        }
        if(ids.Count==0)return value;
        value["text"]=text;
        value["playbackText"]=unit.Text;value["completedPrefixIds"]=ids;
        value["scope"]="Adjacent audio chunks of one unfinished clause; prefix actually completed. This window can only append a correction, never rewrite prior audio.";
        return value;
    }

    private bool ActivitySpeechReadyForSemanticCheck(ActivityMonitorProtocol.SpeechUnit unit)
    {
        int index=m_ActivitySpeech.IndexOf(unit);
        if(index<0||unit.IsCorrection||unit.State=="interrupted")return true;
        // Check the end of a known clause once its preceding audio has played.
        // An isolated/incomplete final phrase is still checked; no indefinite wait.
        if(!unit.Exposed && index>0) {
            var prior=m_ActivitySpeech[index-1];
            if(prior.State!="superseded"&&prior.State!="interrupted"&&!prior.IsCorrection&&
                prior.Language==unit.Language&&!ActivitySpeechWindowPolicy.EndsClause(prior.Text)&&
                ActivitySpeechWindowPolicy.Compatible(prior.ObservedAtSpeechStart??prior.ObservedAtCreation,
                    unit.ObservedAtSpeechStart??unit.ObservedAtCreation))return false;
        }
        if(!ActivitySpeechWindowPolicy.EndsClause(unit.Text) && index+1<m_ActivitySpeech.Count) {
            int count=1,characters=unit.Text.Length;
            var observation=unit.ObservedAtSpeechStart??unit.ObservedAtCreation;
            // Do not hide a prefix which cannot fit in the bounded final window.
            for(int i=index-1;i>=0;i--) {
                var prior=m_ActivitySpeech[i];
                if(prior.State!="completed"||prior.IsCorrection||prior.CorrectedAfterExposure||
                    prior.Language!=unit.Language||ActivitySpeechWindowPolicy.EndsClause(prior.Text)||
                    !ActivitySpeechWindowPolicy.Compatible(prior.ObservedAtSpeechStart,observation))break;
                count++;characters+=prior.Text.Length+1;
            }
            for(int i=index+1;i<m_ActivitySpeech.Count;i++) {
                var next=m_ActivitySpeech[i];
                if(next.State=="superseded"||next.State=="interrupted"||next.IsCorrection||next.Language!=unit.Language||
                    string.IsNullOrWhiteSpace(next.Text)||
                    !ActivitySpeechWindowPolicy.Compatible(observation,next.ObservedAtSpeechStart??next.ObservedAtCreation))break;
                if(++count>5 || (characters+=next.Text.Length+1)>480)return true;
                if(ActivitySpeechWindowPolicy.EndsClause(next.Text))return false;
            }
        }
        return true;
    }
    private void TryActivityVisualRecheck(JObject frame,string output)
    {
        JObject verdict;try{verdict=ActivityEvidencePolicy.Parse(output??"{}");}catch(Exception){return;}
        if(!ActivityMonitorProtocol.ExpandPredicateTime(verdict))return;
        string domain=(string)verdict["claimDomain"]??(string)verdict["grounding"]?["claimDomain"];
        string act=(string)verdict["speechAct"]??(string)verdict["grounding"]?["speechAct"],quote=(string)verdict["claimQuote"];
        var unit=(frame?["speech"] as JArray)?.OfType<JObject>().FirstOrDefault();
        string id=(string)unit?["id"];
        bool sameSource=(string)verdict["perceptionSource"]=="current-writer-image";
        if(!ActivityClaimSourcePolicy.CurrentImageAllowed(frame,verdict) || (domain!="visual" && !sameSource) || act!="factual-claim" || (!sameSource && (string)verdict["claimTime"]=="past-event") || string.IsNullOrWhiteSpace(quote) || quote.Length>360 ||
            ((string)unit?["text"]??"").IndexOf(quote,StringComparison.Ordinal)<0 ||
            id==null || m_ActivityVisualAttempts.Count>=2 ||
            ActivityMonitorSchedulePolicy.RetryAt(m_ActivityRequestTimes,Time.realtimeSinceStartup,10)>Time.realtimeSinceStartup ||
            m_ActivityReplyObservationGeneration!=m_FormalResponseGeneration || string.IsNullOrEmpty(m_ActivityReplyImage))return;
        var source=ActivityFormalObservation();
        if((bool?)source["formalPixelsProvided"]!=true || HasActivityVisualEvidence(id,quote,(string)source["captureId"]))return;
        var spokenSource=(unit["observedAtSpeechStart"] as JObject??unit["observedAtCreation"] as JObject)?["formalObservation"];
        if(string.IsNullOrEmpty((string)source["captureId"]) ||
            (string)spokenSource?["captureId"]!=(string)source["captureId"])return;
        // Reinspect exactly what the writer was offered, never a later camera
        // frame used to contradict a previously true claim.
        var claims=new JArray(new JObject{["id"]="c0",["speechId"]=id,["claimQuote"]=quote});
        string context=(string)unit["text"];
        foreach(var claim in (frame["visualClaimGroup"] as JArray??new JArray()).OfType<JObject>()) {
            string other=(string)claim["quote"];
            if(claims.Count>=2)break;
            if(string.IsNullOrEmpty(other)||other==quote||other.Length>360||HasActivityVisualEvidence(id,other,(string)source["captureId"]))continue;
            claims.Add(new JObject{["id"]="c"+claims.Count,["speechId"]=id,["claimQuote"]=other});
        }
        // Coalesce adjacent pending propositions from the SAME frozen image.
        // The two-call budget buys evidence groups, not two TTS fragments.
        int index=m_ActivitySpeech.FindIndex(s=>s.Id==id);
        if(index<0)return;
        var adjacent=new JArray();int contextCharacters=0;
        foreach(var prior in m_ActivitySpeech.Take(index).Reverse()) {
            var observed=prior.ObservedAtSpeechStart??prior.ObservedAtCreation;
            if((string)observed?.SelectToken("formalObservation.captureId")!=(string)source["captureId"]||
                prior.State!="completed"||prior.IsCorrection||prior.CorrectedAfterExposure||prior.Language!=m_ActivitySpeech[index].Language)break;
            if(adjacent.Count>=3 || contextCharacters+prior.Text.Length>480)break;
            adjacent.Insert(0,new JObject{["speechId"]=prior.Id,["text"]=prior.Text,["state"]="completed"});contextCharacters+=prior.Text.Length;
        }
        var following=m_ActivitySpeech.Skip(index+1).ToList();
        for(int n=0;n<following.Count;n++) {
            var next=following[n];
            var observed=next.ObservedAtSpeechStart??next.ObservedAtCreation;
            if((string)observed?.SelectToken("formalObservation.captureId")!=(string)source["captureId"]||
                next.State=="superseded"||next.IsCorrection)break;
            if(claims.Count>=2||context.Length+next.Text.Length>480)break;
            if(string.IsNullOrWhiteSpace(next.Text)||next.Text.Length>360||HasActivityVisualEvidence(next.Id,next.Text,(string)source["captureId"]))continue;
            if(m_ActivityCompletedChecks.ContainsNonfactual(frame,ActivitySpeechWindow(next)))continue;
            string proposition=next.Text;var end=next;
            while(!ActivitySpeechWindowPolicy.EndsClause(proposition)&&n+1<following.Count) {
                var suffix=following[n+1];
                if(suffix.State=="superseded"||suffix.IsCorrection||suffix.Language!=next.Language||
                    !ActivitySpeechWindowPolicy.Compatible(observed,suffix.ObservedAtSpeechStart??suffix.ObservedAtCreation))break;
                string joined=ActivitySpeechWindowPolicy.Join(proposition,suffix.Text);
                if(joined.Length>360||context.Length+joined.Length>480)break;
                proposition=joined;end=suffix;n++;
            }
            // An unfinished queue tail is not a separate visual proposition.
            // It will be reconsidered as playback/the queue advances.
            if(!ActivitySpeechWindowPolicy.EndsClause(proposition)&&end.State=="queued")break;
            claims.Add(new JObject{["id"]="c"+claims.Count,["speechId"]=end.Id,["claimQuote"]=proposition});
            context=ActivitySpeechWindowPolicy.Join(context,proposition);
        }
        string groupKey=(string)source["captureId"]+":"+ActivityEvidencePolicy.Stamp(claims);
        if(!m_ActivityVisualAttempts.Add(groupKey))return;
        var input=new JObject{["purpose"]="visual-evidence",["monitorRequestId"]=Guid.NewGuid().ToString("N"),
            ["speechId"]=id,["speech"]=context,["adjacentContext"]=adjacent,["claimQuote"]=quote,["claims"]=claims,["captureId"]=source["captureId"]?.DeepClone(),
            ["observedAt"]=source["observedAt"]?.DeepClone()};
        input["perceptionSource"]=verdict["perceptionSource"]?.DeepClone();
        input["claimTime"]=verdict["claimTime"]?.DeepClone();
        input["claimDomain"]=domain;
        int request=++m_ActivityRequestSerial,epoch=m_WorkEpoch,generation=m_FormalResponseGeneration;
        string binding=ActivityBinding();
        var model=m_ChatSettings.m_ChatModel;
        m_ActivityRequestTimes.Enqueue(Time.realtimeSinceStartup);
        m_ActivityRequestPending=true;m_ActivityModel=model;m_ActivityRequestDeadline=Time.realtimeSinceStartup+9f;
        m_ActivityPendingKey="visual:"+id;m_ActivityPendingFrame=input;
        model.PostActivityVisualEvidence(input.ToString(Formatting.None),m_ActivityReplyImage,(ok,result,status)=>{
            if(request!=m_ActivityRequestSerial)return;
            m_ActivityRequestPending=false;
            if(epoch!=m_WorkEpoch||generation!=m_FormalResponseGeneration||binding!=ActivityBinding()||m_UserSpeechActiveForAutonomy||m_SceneTimePaused||
                !ReferenceEquals(model,m_ChatSettings?.m_ChatModel)||!isActiveAndEnabled)return;
            JArray evidence=null;
            bool accepted=ok&&ActivityVisualEvidenceProtocol.TryReadGroup(result,input,out evidence);
            if(accepted&&evidence!=null)RememberActivityVisualEvidence(evidence);
            Debug.Log("[Activity/VisualEvidence] "+new JObject{["requestId"]=input["monitorRequestId"],["speechId"]=id,
                ["accepted"]=accepted,["status"]=status,["evidence"]=evidence,["rawVerdict"]=result,
                ["actionSubmitted"]=false}.ToString(Formatting.None),this);
        });
    }
}
