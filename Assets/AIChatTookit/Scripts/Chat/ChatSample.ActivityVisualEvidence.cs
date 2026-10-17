using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private string m_ActivityReplyImage;
    private readonly ActivitySourceCheckQueue m_ActivitySourceQueue=new ActivitySourceCheckQueue();
    private string ActivitySourceOwner()=>m_FormalResponseGeneration+":"+m_WorkEpoch+":"+m_ActionConstraints.CurrentUserTurn+":"+ActivityBinding();
    private void QueueInterpretedVisualSource(JObject frame)
    {
        var meaning=frame["speechMeaning"] as JObject;
        if((string)meaning?["act"]!="factual-claim" || (string)meaning["domain"]!="visual" ||
            (string)meaning["source"]!="current-writer-image")return;
        // Literal parsing identifies what to inspect, not whether it is true.
        // Avoid a text-only fact call merely to rediscover that it has no pixels.
        TryActivityVisualRecheck(frame,new JObject{
            ["claimQuote"]=meaning["quote"],["claimDomain"]=meaning["domain"],["speechAct"]=meaning["act"],
            ["predicateTense"]=meaning["tense"],["perceptionSource"]=meaning["source"]}.ToString(Formatting.None));
    }
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
            id==null ||
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
        // Only parsed visual propositions share an image request. Unparsed
        // neighboring feelings or invitations remain free expression, not pixels.
        string groupKey=ActivitySourceCheckQueue.Key((string)source["captureId"],claims,(int?)unit["version"]??0);
        var input=new JObject{["purpose"]="visual-evidence",["monitorRequestId"]=Guid.NewGuid().ToString("N"),
            ["speechId"]=id,["speech"]=context,["adjacentContext"]=adjacent,["claimQuote"]=quote,["claims"]=claims,["captureId"]=source["captureId"]?.DeepClone(),
            ["observedAt"]=source["observedAt"]?.DeepClone()};
        input["perceptionSource"]=verdict["perceptionSource"]?.DeepClone();
        input["claimTime"]=verdict["claimTime"]?.DeepClone();
        input["claimDomain"]=domain;
        input["binding"]=frame["speechMeaning"]?.DeepClone();
        input["speechVersion"]=unit["version"]?.DeepClone();
        m_ActivitySourceQueue.Changed=receipt=>Debug.Log("[Activity/SourceQueue] "+receipt.ToString(Formatting.None),this);
        m_ActivitySourceQueue.Enqueue(groupKey,ActivitySourceOwner(),input,m_ActivityReplyImage,Time.realtimeSinceStartup);
    }

    private bool DispatchActivitySourceCheck(float now)
    {
        m_ActivitySourceQueue.Maintain(ActivitySourceOwner(),now);
        if(m_ActivitySourceQueue.Count==0)return false;
        if(m_ActivityVisualAttempts.Count>=2) {
            m_ActivitySourceQueue.Cancel("source-budget-exhausted-unchecked",now);return false;
        }
        var job=m_ActivitySourceQueue.Take(ActivitySourceOwner(),now,
            ActivityMonitorSchedulePolicy.RetryAt(m_ActivityRequestTimes,now,10));
        if(job==null)return false;
        var input=job.Input;string id=(string)input["speechId"];
        var unit=m_ActivitySpeech.FirstOrDefault(s=>s.Id==id);
        if(unit==null || unit.State=="superseded" || unit.CorrectedAfterExposure ||
            (int?)input["speechVersion"]!=unit.Version) {
            m_ActivitySourceQueue.Close(job,"speech-replaced-unchecked",now);return false;
        }
        if(HasActivityVisualEvidence(id,(string)input["claimQuote"],(string)input["captureId"])) {
            m_ActivitySourceQueue.Close(job,"shared-evidence-available",now);return false;
        }
        if(!m_ActivityVisualAttempts.Add(job.Key)) {
            m_ActivitySourceQueue.Close(job,"attempt-already-consumed-unchecked",now);return false;
        }
        input["monitorRequestId"]=Guid.NewGuid().ToString("N");
        int request=++m_ActivityRequestSerial,epoch=m_WorkEpoch,generation=m_FormalResponseGeneration;
        string binding=ActivityBinding();
        var model=m_ChatSettings.m_ChatModel;
        m_ActivityRequestTimes.Enqueue(now);m_ActivityLastPurpose="visual-evidence";
        m_ActivityRequestPending=true;m_ActivityModel=model;m_ActivityRequestDeadline=now+9f;
        m_ActivityPendingKey="visual:"+id;m_ActivityPendingFrame=input;
        try { model.PostActivityVisualEvidence(input.ToString(Formatting.None),job.Image,(ok,result,status)=>{
            if(request!=m_ActivityRequestSerial)return;
            m_ActivityRequestPending=false;
            var currentUnit=m_ActivitySpeech.FirstOrDefault(s=>s.Id==id);
            if(job.State!="requesting"||epoch!=m_WorkEpoch||generation!=m_FormalResponseGeneration||binding!=ActivityBinding()||m_UserSpeechActiveForAutonomy||m_SceneTimePaused||
                currentUnit==null||currentUnit.State=="superseded"||currentUnit.CorrectedAfterExposure||currentUnit.Version!=(int?)input["speechVersion"]||
                !ReferenceEquals(model,m_ChatSettings?.m_ChatModel)||!isActiveAndEnabled) {
                m_ActivitySourceQueue.Close(job,"owner-revoked-unchecked",Time.realtimeSinceStartup);return;
            }
            JArray evidence=null;
            bool accepted=ok&&ActivityVisualEvidenceProtocol.TryReadGroup(result,input,out evidence);
            if(accepted&&evidence!=null)RememberActivityVisualEvidence(evidence);
            m_ActivitySourceQueue.Close(job,accepted?"evidence-received":"provider-"+status+"-unchecked",Time.realtimeSinceStartup);
            m_ActivityNextPoll=Time.realtimeSinceStartup;
            Debug.Log("[Activity/VisualEvidence] "+new JObject{["requestId"]=input["monitorRequestId"],["speechId"]=id,
                ["accepted"]=accepted,["status"]=status,["evidence"]=evidence,["rawVerdict"]=result,
                ["actionSubmitted"]=false}.ToString(Formatting.None),this);
        }); } catch(Exception error) {
            m_ActivitySourceQueue.Close(job,"provider-exception-"+error.GetType().Name+"-unchecked",Time.realtimeSinceStartup);
            CancelActivityMonitoringForReason(false,"visual-provider-exception");
        }
        return true;
    }
}
