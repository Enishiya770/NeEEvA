using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private readonly ReviewedDeliveryEvidence m_ReviewedDeliveryEvidence = new ReviewedDeliveryEvidence();

    private void ApplySharedWorkDeliveryEvidence()
    {
        if(m_UserSpeechActiveForAutonomy || m_SceneTimePaused)return;
        foreach(var result in ((JArray)ReadWorkDelivery()["results"]).OfType<JObject>()) {
            var report=m_ReviewedDeliveryEvidence.Take(m_WorkEpoch,m_FormalResponseGeneration,result);
            if(report==null || (bool?)result["delivered"]==true && (bool?)result["playbackReviewPending"]!=true)continue;
            m_WorkDeliveredResults[report.id]=report.quote;
            m_WorkDeliveredPlayback[report.id]=(string)result["playbackRevision"];
            m_WorkDeliveredPlaybackLengths[report.id]=((string)result["completedPlaybackText"]??"").Length;
            AcknowledgeDeliveredToolResult((string)result["source"]);
            Debug.Log("[Work/SharedDeliveryEvidence] "+new JObject{["workEpoch"]=m_WorkEpoch,["generation"]=m_FormalResponseGeneration,
                ["id"]=report.id,["playbackRevision"]=result["playbackRevision"],["quote"]=report.quote,
                ["source"]="validated-work-review",["newActionSubmitted"]=false}.ToString(Formatting.None),this);
        }
        // Transfer exact completed-result proofs while other speech progresses.
        // Later audio has a new revision and reopens its own audit. Closing the
        // whole work still waits for output to drain; no new action is launched.
        if(!m_FormalResponseInFlight && m_StreamComplete && m_TTSSenderDone && !IsAISpeaking &&
            m_PendingChunks.Count==0 && m_PendingClips.Count==0)TrySettleDeliveredWork();
    }
}
