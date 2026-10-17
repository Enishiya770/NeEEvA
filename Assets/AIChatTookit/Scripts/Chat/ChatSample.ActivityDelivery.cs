using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private string m_ActivityDeliveryLanguage;

    private bool ConfirmActivityDeliveryLanguage(JObject frame,JObject verdict,Action complete,Action<string> failed)
    {
        string expected=(string)frame.SelectToken("delivery.language"),text=(string)verdict?["text"];
        if((string)verdict?["operation"]!="supplement"||!SpeechLanguagePolicy.NeedsCheck(text,expected))return false;
        var model=m_ChatSettings?.m_ChatModel;
        if(model?.SupportsSpeechLanguageCheck!=true){failed("delivery-language-unchecked");return true;}
        int serial=m_ActivityRequestSerial,generation=m_FormalResponseGeneration,epoch=m_WorkEpoch;
        bool ended=false;m_ActivityRequestPending=true;m_ActivityRequestDeadline=Time.realtimeSinceStartup+4f;
        void Finish(bool ok,string actual,string status) {
            if(ended)return;ended=true;
            if(serial!=m_ActivityRequestSerial||generation!=m_FormalResponseGeneration||epoch!=m_WorkEpoch)return;
            m_ActivityRequestPending=false;
            if(m_UserSpeechActiveForAutonomy||m_SceneTimePaused||model!=m_ChatSettings?.m_ChatModel||!isActiveAndEnabled)return;
            bool accepted=ok && (actual==expected || actual=="mixed");
            Debug.Log("[Activity/DeliveryLanguage] "+new JObject{["id"]=verdict["id"],["expected"]=expected,
                ["actual"]=actual,["accepted"]=accepted,["status"]=status,["queued"]=false}.ToString(Formatting.None),this);
            if(accepted)complete();else failed("delivery-language-mismatch:expected="+expected+";actual="+(actual??"unknown"));
        }
        try{model.PostSpeechLanguage(text,Finish);}catch(Exception error){Finish(false,null,error.GetType().Name);}
        StartCoroutine(Deadline());
        System.Collections.IEnumerator Deadline(){float until=Time.realtimeSinceStartup+3.25f;
            while(!ended&&serial==m_ActivityRequestSerial&&Time.realtimeSinceStartup<until)yield return null;
            if(!ended)Finish(false,null,"language-check-timeout");}
        return true;
    }
    private void RecordActivityRepeatedCandidate(string text)
    {
        if(!ActivityMonitoringAvailable||string.IsNullOrWhiteSpace(text))return;
        if(!m_ActivityRejectedCandidates.Contains(text))m_ActivityRejectedCandidates.Add(text);
        if(m_ActivityRejectedCandidates.Count>8)m_ActivityRejectedCandidates.RemoveAt(0);
        // Do not close or clear the independent question/result ledger. This
        // path authorizes speech only, never a new step or action retry.
        m_ActivityNextPoll=0;
    }

    private JObject ReadActivityDelivery()
    {
        if(m_ActivityPublishedGeneration!=m_FormalResponseGeneration||
            m_FormalResponseInFlight||!m_StreamComplete||!m_TTSSenderDone||IsAISpeaking||
            m_PendingChunks.Count>0||m_PendingClips.Count>0)return null;
        var ledger=ReadWorkDelivery();
        // A closed execution can still have a later spoken retraction awaiting
        // audit. This permits reporting review only, never restarts its action.
        if(!m_WorkReviewOpen&&!((JArray)ledger["results"]).OfType<JObject>().Any(r=>
            (bool?)r["playbackReviewPending"]==true || (bool?)r["delivered"]!=true &&
            m_ActivityDeliverySends.ContainsKey((string)r["id"])))return null;
        var owed=((JArray)ledger["results"]).OfType<JObject>().Where(r=>(bool?)r["delivered"]!=true || (bool?)r["playbackReviewPending"]==true)
            .OrderBy(r=>(bool?)r["delivered"]==true)
            .ThenByDescending(r=>(string)r["source"]=="independent-answer").FirstOrDefault();
        if(owed==null)return null;
        var result=(JObject)owed.DeepClone();
        result["taskContext"]=WorkResultDeliveryProtocol.TaskContext(ReadRoomTaskSequence(),m_ActionConstraints.CurrentUserText,result);
        m_ActivityDeliverySends.TryGetValue((string)result["id"],out int sends);
        result["supplementAllowed"]=sends<2;
        result["attempts"]=sends;
        result["language"]=m_ActivityDeliveryLanguage??m_ActivitySpeech.LastOrDefault()?.Language??"auto";
        result["rejectedCandidates"]=new JArray(m_ActivityRejectedCandidates);
        result["supplementPlayback"]=new JArray(m_ActivitySpeech.Where(u=>u.IsCorrection &&
            u.CorrectedResultId==(string)result["id"]).Select(u=>new JObject{
                ["audioId"]=u.Id,["version"]=u.Version,["state"]=u.State,["text"]=u.Text}));
        result["answerEvidence"]=ReadRoomTaskSequence()?["answerEvidence"]?.DeepClone();
        if((string)result["source"]=="independent-answer")result["answerEvidence"]=new JObject{
            ["taskEvidence"]=result["answerEvidence"],["visualRecall"]=ReadSceneRecallFacts()};
        result["otherResults"]=new JArray(((JArray)ledger["results"]).OfType<JObject>()
            .Where(r=>(string)r["id"]!=(string)result["id"]).Select(r=>PickRoomFields(r,"id","source","status","delivered")));
        result["scope"]="This exact outstanding result/question only. Generated or suppressed candidates were not heard. No new action authority.";
        return result;
    }

    private string ApplyActivityDelivery(JObject frame,JObject verdict)
    {
        if(verdict==null)return null;
        var captured=frame["delivery"] as JObject;
        var live=ReadActivityDelivery();
        string rejection=ActivityDeliveryProtocol.Reject(verdict,live);
        var differences=new JArray();
        foreach(string field in new[]{"id","facts","answerEvidence","playbackRevision"})
            foreach(var difference in ActivityEvidencePolicy.Differences(captured?[field],live?[field],field))differences.Add(difference);
        if(rejection==null && differences.Count>0)rejection="delivery-premise-changed";
        string operation=(string)verdict["operation"],id=(string)verdict["id"];
        if(rejection==null && operation=="covered") {
            m_WorkDeliveredResults[id]=(string)verdict["quote"];
            m_WorkDeliveredPlayback[id]=(string)live["playbackRevision"];
            m_WorkDeliveredPlaybackLengths[id]=((string)live["completedPlaybackText"]??"").Length;
            AcknowledgeDeliveredToolResult((string)live["source"]);
            TrySettleDeliveredWork();
        }
        if(rejection==null && operation=="supplement") {
            m_ActivityDeliverySends.TryGetValue(id,out int sends);
            if(sends>=2)rejection="delivery-supplement-limit";
            else {
                m_WorkDeliveredResults.Remove(id); m_WorkDeliveredPlayback.Remove(id);
                string text=(string)verdict["text"];
                m_ActivityDeliverySends[id]=sends+1;
                QueueActivityCorrection(text,(string)live["language"]??"auto",id);
                // Coverage is checked only after real audio completion. Merely
                // queuing text must not mark this or another result delivered.
            }
        }
        Debug.Log("[Activity/Delivery] "+new JObject{["owner"]=frame["owner"]?.DeepClone(),["id"]=id,
            ["operation"]=operation,["verdict"]=verdict.DeepClone(),["rejection"]=rejection,
            ["premiseDifferences"]=differences,["requestId"]=frame["monitorRequestId"],
            ["playbackEvidence"]=ResultPlaybackSelection.Evidence(live,(string)verdict.SelectToken("playbackProof.lastRelevantQuote"),(string)verdict.SelectToken("playbackProof.selectedPlaybackId")),
            ["settled"]=rejection==null&&operation=="covered",["newActionSubmitted"]=false}.ToString(Formatting.None),this);
        return rejection;
    }

    private bool TrySettleDeliveredWork()
    {
        if(!m_WorkReviewOpen || m_UserSpeechActiveForAutonomy || m_AgentGracefulShutdownPending ||
            HasWorkToolInFlight() || NeedsSingingReviewDetails() || SpatialGoalPreventsWorkClosure())return false;
        var sequence=ReadRoomTaskSequence();
        // Only a canonical request/plan can establish the complete obligations.
        if(sequence==null && (m_WorkRequestSemantics==null ||
            (int?)m_WorkRequestSemantics["workEpoch"]!=m_WorkEpoch ||
            (int?)m_WorkRequestSemantics["userTurn"]!=m_ActionConstraints.CurrentUserTurn ||
            (string)m_WorkRequestSemantics["currentUserText"]!=m_ActionConstraints.CurrentUserText ||
            m_WorkRequestSemanticsModel!=m_ChatSettings?.m_ChatModel ||
            (bool?)m_WorkRequestSemantics["requiresActionOrControl"]!=false))return false;
        var delivery=ReadWorkDelivery();var results=(JArray)delivery["results"];
        if(sequence==null && !results.OfType<JObject>().Any(r=>(string)r["source"]=="independent-answer"))return false;
        if(!WorkResultDeliveryProtocol.CanClose(results,sequence,"") ||
            results.OfType<JObject>().Any(r=>(bool?)r["playbackReviewPending"]==true))return false;
        CancelAutonomyIntentProbeForReason("delivery-settled");
        m_WorkStatus="closed";m_WorkReviewOpen=false;m_WorkSubstepReviewPending=false;
        m_PrivateDecisionFailure=null;m_PrivateDecisionRetries=0;
        Debug.Log("[Work/DeliverySettled] "+new JObject{["workEpoch"]=m_WorkEpoch,
            ["userTurn"]=m_ActionConstraints.CurrentUserTurn,["resultIds"]=new JArray(results.Select(r=>r["id"].DeepClone())),
            ["source"]="validated-current-playback-ledger",["newActionSubmitted"]=false,
            ["scope"]="Execution and spoken obligations are settled separately. Subsequent autonomous intention remains optional and needs its own current decision."}.ToString(Formatting.None),this);
        ScheduleNextTick(5f,"delivery-settled-independent-attention");
        return true;
    }
}
