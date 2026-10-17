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
    private JObject ActivitySpeechWindow(ActivityMonitorProtocol.SpeechUnit unit)
    {
        var value=unit.Snapshot();int index=m_ActivitySpeech.IndexOf(unit);
        if(index<=0||!unit.Exposed||unit.IsCorrection)return value;
        string text=unit.Text;var ids=new JArray();
        for(int i=index-1;i>=0&&ids.Count<4;i--) {
            var prior=m_ActivitySpeech[i];
            if(prior.State!="completed"||prior.IsCorrection||prior.CorrectedAfterExposure||prior.Language!=unit.Language||
                string.IsNullOrWhiteSpace(prior.Text)||"。！？.!?;；…".IndexOf(prior.Text.TrimEnd().Last())>=0||
                !ActivityEvidencePolicy.Equal(prior.ObservedAtSpeechStart,unit.ObservedAtSpeechStart))break;
            bool wordBoundary=text.Length>0&&prior.Text.Last()<128&&text[0]<128&&
                char.IsLetterOrDigit(prior.Text.Last())&&char.IsLetterOrDigit(text[0]);
            string combined=prior.Text+(wordBoundary?" ":"")+text;
            if(combined.Length>480)break;
            text=combined;ids.Insert(0,new JValue(prior.Id));
        }
        if(ids.Count==0)return value;
        value["text"]=text;
        value["playbackText"]=unit.Text;value["completedPrefixIds"]=ids;
        value["scope"]="Adjacent audio chunks of one unfinished clause; prefix actually completed. This window can only append a correction, never rewrite prior audio.";
        return value;
    }
    private void TryActivityVisualRecheck(JObject frame,string output)
    {
        JObject verdict;try{verdict=ActivityEvidencePolicy.Parse(output??"{}");}catch(Exception){return;}
        string domain=(string)verdict["claimDomain"]??(string)verdict["grounding"]?["claimDomain"];
        string act=(string)verdict["speechAct"]??(string)verdict["grounding"]?["speechAct"],quote=(string)verdict["claimQuote"];
        var unit=(frame?["speech"] as JArray)?.OfType<JObject>().FirstOrDefault();
        string id=(string)unit?["id"];
        bool sameSource=(string)verdict["perceptionSource"]=="current-writer-image";
        if((domain!="visual" && !sameSource) || act!="factual-claim" || (!sameSource && (string)verdict["claimTime"]=="past-event") || string.IsNullOrWhiteSpace(quote) || quote.Length>360 ||
            ((string)unit?["text"]??"").IndexOf(quote,StringComparison.Ordinal)<0 ||
            id==null || m_ActivityVisualAttempts.Count>=2 || m_ActivityVisualEvidence.ContainsKey(id) ||
            m_ActivityRequestTimes.Count>=12 ||
            m_ActivityReplyObservationGeneration!=m_FormalResponseGeneration || string.IsNullOrEmpty(m_ActivityReplyImage))return;
        var source=ActivityFormalObservation();
        if((bool?)source["formalPixelsProvided"]!=true)return;
        var spokenSource=(unit["observedAtSpeechStart"] as JObject??unit["observedAtCreation"] as JObject)?["formalObservation"];
        if(string.IsNullOrEmpty((string)source["captureId"]) ||
            (string)spokenSource?["captureId"]!=(string)source["captureId"])return;
        // Reinspect exactly what the writer was offered, never a later camera
        // frame used to contradict a previously true claim.
        var claims=new JArray(new JObject{["id"]="c0",["speechId"]=id,["claimQuote"]=quote});
        string context=(string)unit["text"];
        // Coalesce adjacent pending propositions from the SAME frozen image.
        // The two-call budget buys evidence groups, not two TTS fragments.
        int index=m_ActivitySpeech.FindIndex(s=>s.Id==id);
        if(index<0)return;
        foreach(var next in m_ActivitySpeech.Skip(index+1)) {
            var observed=next.ObservedAtSpeechStart??next.ObservedAtCreation;
            if((string)observed?.SelectToken("formalObservation.captureId")!=(string)source["captureId"]||
                next.State=="superseded"||next.IsCorrection)break;
            if(claims.Count>=4||context.Length+next.Text.Length>480)break;
            if(string.IsNullOrWhiteSpace(next.Text)||next.Text.Length>360||m_ActivityVisualEvidence.ContainsKey(next.Id))continue;
            claims.Add(new JObject{["id"]="c"+claims.Count,["speechId"]=next.Id,["claimQuote"]=next.Text});context+=next.Text;
        }
        string groupKey=(string)source["captureId"]+":"+ActivityEvidencePolicy.Stamp(claims);
        if(!m_ActivityVisualAttempts.Add(groupKey))return;
        var input=new JObject{["purpose"]="visual-evidence",["monitorRequestId"]=Guid.NewGuid().ToString("N"),
            ["speechId"]=id,["speech"]=context,["claimQuote"]=quote,["claims"]=claims,["captureId"]=source["captureId"]?.DeepClone(),
            ["observedAt"]=source["observedAt"]?.DeepClone()};
        input["perceptionSource"]=verdict["perceptionSource"]?.DeepClone();
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
            if(accepted&&evidence!=null)foreach(var row in evidence.OfType<JObject>())m_ActivityVisualEvidence[(string)row["speechId"]]=row;
            Debug.Log("[Activity/VisualEvidence] "+new JObject{["requestId"]=input["monitorRequestId"],["speechId"]=id,
                ["accepted"]=accepted,["status"]=status,["evidence"]=evidence,["rawVerdict"]=result,
                ["actionSubmitted"]=false}.ToString(Formatting.None),this);
        });
    }
}
