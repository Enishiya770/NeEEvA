using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NeEEvA.Perception;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private sealed class SceneRecallRecord
    {
        public SceneVisibleCatalogView view;
        public SceneVisibleObjectCatalog.Entry entry;
        public SceneVisibleMemory location;
        public string question;
        public JObject verdict;
    }
    private readonly List<SceneRecallRecord> m_SceneRecallRecords=new List<SceneRecallRecord>();
    private int m_SceneRecallAttemptGeneration=-1;
    private int m_SceneRecallRequestSerial;
    private string m_SceneRecallStatus="not-requested";

    private static bool NeedsSceneRecall(JObject understanding) => (string)understanding?["needsEvidence"]=="memory-or-direction";

    private bool TryBeginSceneRecall(SceneVisionObservation current,JObject understanding,int generation,Action completed)
    {
        if(!NeedsSceneRecall(understanding)||m_SceneRecallAttemptGeneration==generation)return false;
        m_SceneRecallAttemptGeneration=generation;m_SceneRecallStatus="pending";
        StartCoroutine(RecallSceneViews(current,(JObject)understanding.DeepClone(),generation,completed));return true;
    }

    private IEnumerator RecallSceneViews(SceneVisionObservation current,JObject understanding,int generation,Action completed)
    {
        var model=m_ChatSettings?.m_ChatModel;var bridge=GetComponent<ArdyRoomDialogueBridge>();
        var avatar=bridge?.avatar;var environment=bridge?.environmentRoot;
        int epoch=m_WorkEpoch,visionEpoch=m_SceneVisionEpoch,userTurn=m_ActionConstraints.CurrentUserTurn;
        string question=m_ActionConstraints.CurrentUserText;
        bool Current()=>isActiveAndEnabled&&!m_SceneTimePaused&&!m_UserSpeechActiveForAutonomy&&
            generation==m_FormalResponseGeneration&&epoch==m_WorkEpoch&&visionEpoch==m_SceneVisionEpoch&&
            userTurn==m_ActionConstraints.CurrentUserTurn&&model==m_ChatSettings?.m_ChatModel&&bridge!=null&&bridge.IsConnected&&
            bridge==GetComponent<ArdyRoomDialogueBridge>()&&avatar!=null&&avatar==bridge.avatar&&environment!=null&&environment==bridge.environmentRoot;
        // Independent channel and local offered entries: recalling cannot cancel
        // an executing search or replace its current candidate-page ownership.
        var sources=current==null?new List<SceneVisionObservation>():GetSceneVisibleHistoricalViews(current).ToList();
        m_SceneRecallRecords.RemoveAll(r=>!SceneVisibleCatalogHistoryBinding(r.view));
        // Deduplicating nearby recent camera views must not erase a retained,
        // source-bound recall anchor. Still bounded by record count and age.
        if(current!=null)foreach(var record in m_SceneRecallRecords)
            if(record.view.observation.CaptureId!=current.CaptureId&&!sources.Any(s=>s.CaptureId==record.view.observation.CaptureId))
                sources.Add(record.view.observation);
        if(model?.SupportsSceneRecallMessages!=true||sources.Count==0) {
            m_SceneRecallStatus=model?.SupportsSceneRecallMessages!=true?"provider-unavailable":"no-retained-view";
            if(Current())completed();yield break;
        }
        // At most two distinct source views. Oldest and newest preserve a time
        // contrast; selection is exposed, not advertised as exhaustive memory.
        var ordered=sources.OrderBy(s=>s.CapturedAtRealtime).ToList();
        var selected=new[]{ordered.First(),ordered.Last()}.Distinct().ToArray();
        int checkedViews=0;float deadline=Time.realtimeSinceStartup+22f;
        foreach(var source in selected) {
            if(!Current())yield break;
            var view=FindRetainedSceneVisibleCatalog(source)??m_SceneRecallRecords.FirstOrDefault(r=>ReferenceEquals(r.view.observation,source))?.view;
            if(view==null||!view.catalog.TryBuildHistoricalPage(view.activePage,out var preview,out _))continue;
            var entries=view.catalog.Candidates.Where(e=>e.pageIndex==view.activePage).Take(6).ToArray();
            var candidates=new JArray(entries.Select(e=>new JObject{["referenceId"]=e.candidateId,["candidateId"]=e.candidateId,
                ["imagePoint"]=new JObject{["x"]=e.imagePoint.x,["y"]=e.imagePoint.y},["pixelBounds"]=JObject.FromObject(e.pixelBounds)}));
            var input=new JObject{["question"]=question,["captureId"]=source.CaptureId,["capturedAtUtc"]=source.CapturedAtUtc,
                ["questionInterpretation"]=understanding,
                ["captureRole"]="historical",
                ["ageSeconds"]=Time.realtimeSinceStartup-source.CapturedAtRealtime,["candidates"]=candidates,
                ["scope"]="Retained historical RGB, not current pixels. Only one candidate page is inspected; omitted pages/objects remain unknown."};
            bool received=false,ok=false;string output="",status="recall-timeout";int request=++m_SceneRecallRequestSerial;
            model.PostSceneRecallMessage(input.ToString(Formatting.None),preview.ImageDataUrl,(accepted,json,reason)=>{
                if(!Current()||request!=m_SceneRecallRequestSerial)return;received=true;ok=accepted;output=json;status=reason;
            });
            while(Current()&&request==m_SceneRecallRequestSerial&&!received&&Time.realtimeSinceStartup<deadline)yield return null;
            if(request!=m_SceneRecallRequestSerial)yield break;
            if(!Current()){model.CancelSceneRecallMessage();yield break;}
            if(!received){model.CancelSceneRecallMessage();m_SceneRecallStatus="unchecked-timeout";break;}
            if(!ok||!SceneRecallProtocol.TryRead(output,input,out var verdict,out _)||!SceneVisibleCatalogHistoryBinding(view)) {
                m_SceneRecallStatus="unchecked:"+status;continue;
            }
            checkedViews++;
            // Page interpretations are hypotheses, not an entity dictionary.
            // Independently inspect only the proposed target's original mask,
            // within the same recall deadline, before promoting its label.
            var selectedEntry=entries.FirstOrDefault(e=>e.candidateId==(string)verdict["referenceId"]);
            JObject verified = null;
            if (selectedEntry != null && Time.realtimeSinceStartup < deadline &&
                view.catalog.TryBuildHistoricalFocus(selectedEntry.referenceId, out var focus, out _)) {
                var focused = (JObject)input.DeepClone();
                focused["candidates"] = new JArray(new JObject { ["referenceId"] = selectedEntry.candidateId,
                    ["candidateId"] = "c1", ["marker"] = "1" });
                focused["scope"] = "Independent selected-mask check in the SAME historical RGB. Only marker 1 identifies this candidate; other visible objects are context, not this entity. Describe the marked opaque surface itself. Confirm all question relations or abstain. No earlier model label is supplied.";
                bool checkedTarget = false, targetOk = false; string targetOutput = "";
                request = ++m_SceneRecallRequestSerial;
                model.PostSceneRecallMessage(focused.ToString(Formatting.None), focus.ImageDataUrl, (accepted,json,reason) => {
                    if (!Current() || request != m_SceneRecallRequestSerial) return;
                    checkedTarget = true; targetOk = accepted; targetOutput = json;
                });
                while (Current() && !checkedTarget && Time.realtimeSinceStartup < deadline) yield return null;
                if (!Current()) { model.CancelSceneRecallMessage(); yield break; }
                if (!checkedTarget) model.CancelSceneRecallMessage();
                if (checkedTarget && targetOk && SceneRecallProtocol.TryRead(targetOutput, focused, out var check, out _) &&
                    (string)check["status"] == "supported" && SceneVisibleCatalogHistoryBinding(view)) verified = check;
                Debug.Log("[SceneRecall/TargetCheck] " + new JObject { ["captureId"] = source.CaptureId,
                    ["referenceId"] = selectedEntry.referenceId, ["entityId"] = selectedEntry.entityId,
                    ["accepted"] = verified != null, ["received"] = checkedTarget, ["rawVerdict"] = targetOutput }.ToString(Formatting.None), this);
            }
            var hypotheses = verdict["observations"].DeepClone();
            if (verified != null) verdict = verified;
            else if (selectedEntry != null) {
                verdict = (JObject)verdict.DeepClone(); verdict["status"] = "unresolved"; verdict["referenceId"] = "";
                verdict["finding"] = "Page-level target association was not confirmed by the independent selected-mask check.";
                selectedEntry = null;
            }
            foreach(JObject note in verified?["observations"] as JArray ?? new JArray()) {
                var entry=entries.First(e=>e.candidateId==(string)note["referenceId"]);
                var memory=m_SceneVisibleMemory.Find(m=>m.entry.entityId==entry.entityId);
                // Never overwrite a newer geometry/description with old pixels.
                if(memory!=null&&memory.entry.sourceCaptureId!=source.CaptureId)continue;
                if(memory==null){memory=new SceneVisibleMemory{view=view,entry=entry};m_SceneVisibleMemory.Add(memory);}
                memory.description=(string)note["description"];memory.descriptionCaptureId=source.CaptureId;
            }
            m_SceneRecallRecords.RemoveAll(r=>r.view.observation.CaptureId==source.CaptureId&&r.question==question);
            verdict["pageHypotheses"] = hypotheses;
            verdict["entityLabelsConfirmed"] = verified != null;
            m_SceneRecallRecords.Add(new SceneRecallRecord{view=view,question=question,verdict=verdict,
                entry=selectedEntry,location=selectedEntry==null?null:new SceneVisibleMemory{view=view,entry=selectedEntry}});
            while(m_SceneRecallRecords.Count>4)m_SceneRecallRecords.RemoveAt(0);
            TrimSceneVisibleMemory();
            Debug.Log("[SceneRecall/Result] "+new JObject{["generation"]=generation,["userTurn"]=userTurn,
                ["question"]=question,["verdict"]=verdict,["actionAuthorization"]=false}.ToString(Formatting.None),this);
        }
        if(checkedViews>0)m_SceneRecallStatus=checkedViews==selected.Length?"checked-selected-views":"partially-checked";
        else if(m_SceneRecallStatus=="pending")m_SceneRecallStatus="retained-pixels-unavailable";
        if(Current())completed();
    }

    private JObject ReadSceneRecallFacts(SceneVisionObservation current=null)
    {
        var records=new JArray();
        foreach(var record in m_SceneRecallRecords.Where(r=>SceneVisibleCatalogHistoryBinding(r.view))
            .OrderByDescending(r=>r.question==m_ActionConstraints.CurrentUserText).ThenByDescending(r=>r.view.observation.CapturedAtRealtime)
            .GroupBy(r=>r.view.observation.CaptureId).Select(group=>group.First()).Take(2)) {
            var old=record.view.observation;var e=record.entry;
            bool valid=e!=null&&SceneVisibleMemoryPositionValid(record.location);
            records.Add(new JObject{["sourceCaptureId"]=old.CaptureId,["capturedAtUtc"]=old.CapturedAtUtc,
                ["question"]=record.question,["assessment"]=record.verdict["status"], ["finding"]=record.verdict["finding"],
                ["observations"]=record.verdict["observations"].DeepClone(),["entityLabelsConfirmed"]=record.verdict["entityLabelsConfirmed"],
                ["entityId"]=e?.entityId,
                ["referenceId"]=e==null?null:"recall:"+e.referenceId,["positionMemoryValid"]=valid,
                ["bearing"]=valid&&current!=null?BuildSceneRememberedPointBearing(e.pointAtCapture,current,old.CaptureId,old):null,
                ["ageSeconds"]=Time.realtimeSinceStartup-old.CapturedAtRealtime,["currentlyVisible"]=null,["noticedAtCapture"]=null});
        }
        return new JObject{["state"]=m_SceneRecallAttemptGeneration==m_FormalResponseGeneration?m_SceneRecallStatus:"not-selected-this-turn",
            ["records"]=records,["actionAuthorization"]=false,
            ["scope"]="Source-bound rereading of retained pixels. A supported old-image finding is not current visibility or proof of prior conscious recognition. Missing/unselected/unchecked evidence is unknown, never absence. Frozen bearings are search hints; new pixels must confirm a target after reorientation."};
    }

    private bool ResolveSceneRecallOrientation(string reference,SceneVisionObservation current,bool body,
        out float yaw,out float pitch,out JObject evidence)
    {
        yaw=pitch=0;evidence=null;
        if(!IsFreshSceneObservation(current))return false;
        var record=m_SceneRecallRecords.LastOrDefault(r=>r.entry!=null&&"recall:"+r.entry.referenceId==reference&&SceneVisibleCatalogHistoryBinding(r.view));
        if(record!=null) {
            if(!SceneVisibleMemoryPositionValid(record.location))return false;
            evidence=BuildSceneRememberedPointBearing(record.entry.pointAtCapture,current,record.view.observation.CaptureId,record.view.observation);
            if(evidence==null||(body&&evidence["relativeToCurrentBody"]?.Type==JTokenType.Null))return false;
            yaw=(float)evidence[body?"relativeToCurrentBody":"relativeToCurrentView"][body?"yawDegrees":"yawDegreesRight"];
            pitch=body?0:(float)evidence["relativeToCurrentView"]["elevationDegreesUp"];
            evidence["referenceKind"]="frozen-object-point";
        } else {
            var old=GetSceneVisibleHistoricalViews(current).FirstOrDefault(s=>s.CaptureId==reference);
            if(old==null||body&&!current.HasBodyPose)return false;
            evidence=SceneHistoricalViewBearing(old,current);evidence["sourceCaptureId"]=old.CaptureId;
            evidence["referenceKind"]="historical-camera-direction";
            yaw=(float)evidence[body?"yawRelativeToCurrentBodyDegrees":"yawRelativeToCurrentCameraDegrees"];
        }
        evidence["referenceId"]=reference;evidence["computedYaw"]=yaw;evidence["computedPitch"]=pitch;
        // Never clamp an unreachable head request and call the target reached.
        return body?Mathf.Abs(yaw)<=180:Mathf.Abs(yaw)<=50&&Mathf.Abs(pitch)<=25;
    }

    private IEnumerator VerifyReorientedScene(SceneVisionObservation frame,SceneActionFact fact,Func<bool> current)
    {
        fact.viewVerification="unchecked";
        var model=m_ChatSettings?.m_ChatModel;var view=GetCurrentSceneVisibleCatalog(frame);
        if(model?.SupportsSceneRecallMessages!=true||view==null||
            !view.catalog.TryBuildHistoricalPage(view.activePage,out var preview,out _))yield break;
        var input=new JObject{["captureRole"]="current-verification",["captureId"]=frame.CaptureId,
            ["question"]=m_ActionConstraints.CurrentUserText,
            ["questionInterpretation"]=ReadCurrentRequestSemantics(m_FormalResponseGeneration)?["questionUnderstanding"]?.DeepClone(),
            ["candidates"]=new JArray(view.catalog.Candidates.Where(e=>e.pageIndex==view.activePage).Take(6)
                .Select(e=>new JObject{["referenceId"]=e.candidateId,["candidateId"]=e.candidateId}))};
        var recalled=m_SceneRecallRecords.LastOrDefault(r=>r.entry!=null&&"recall:"+r.entry.referenceId==fact.historyReferenceId&&SceneVisibleCatalogHistoryBinding(r.view));
        if(recalled!=null)input["historicalTargetHint"]=new JObject{["sourceCaptureId"]=recalled.view.observation.CaptureId,
            ["question"]=recalled.question,["finding"]=recalled.verdict["finding"],
            ["description"]=((JArray)recalled.verdict["observations"]).FirstOrDefault(n=>(string)n["referenceId"]==recalled.entry.candidateId)?["description"]?.DeepClone(),
            ["scope"]="Past-image interpretation to resolve the current referent only; current visibility must be checked independently in the new pixels."};
        bool received=false,ok=false;string output="";int request=++m_SceneRecallRequestSerial;
        model.PostSceneRecallMessage(input.ToString(Formatting.None),preview.ImageDataUrl,(accepted,json,status)=>{
            if(!current()||request!=m_SceneRecallRequestSerial)return;received=true;ok=accepted;output=json;
        });
        float deadline=Time.realtimeSinceStartup+11f;
        while(current()&&request==m_SceneRecallRequestSerial&&!received&&Time.realtimeSinceStartup<deadline)yield return null;
        if(request!=m_SceneRecallRequestSerial)yield break;
        if(!current()||!received){model.CancelSceneRecallMessage();yield break;}
        if(ok&&SceneRecallProtocol.TryRead(output,input,out var result,out _)) {
            fact.viewVerification=(string)result["status"];fact.viewFinding=(string)result["finding"];
            // This interpretation permits a report only. Operating/approaching
            // the object still uses the independent original-target matcher.
            fact.semanticTargetConfirmed=false;
        }
        Debug.Log("[SceneRecall/ReorientationResult] "+new JObject{["actionId"]=fact.actionId,["referenceId"]=fact.historyReferenceId,
            ["source"]=fact.historyBearing,["resultCaptureId"]=frame.CaptureId,["actualViewChangeDegrees"]=fact.actualViewChangeDegrees,
            ["verification"]=fact.viewVerification,["finding"]=fact.viewFinding,["rawOutput"]=output}.ToString(Formatting.None),this);
    }
}
