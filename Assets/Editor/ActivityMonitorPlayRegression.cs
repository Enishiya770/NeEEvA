using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class ActivityMonitorPlayRegression
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static int checks;
    public static JObject LastReport;
    private static void Check(bool value,string why){checks++;if(!value)throw new InvalidOperationException("Activity playback: "+why);}
    private static object Call(object obj,string name,params object[] args)=>obj.GetType().GetMethod(name,Flags).Invoke(obj,args);
    private static void Set(object obj,string name,object value)=>obj.GetType().GetField(name,Flags).SetValue(obj,value);
    private static object Get(object obj,string name)=>obj.GetType().GetField(name,Flags).GetValue(obj);
    private static IEnumerator Until(Func<bool> predicate,string reason,float limit=8f)
    {float at=Time.realtimeSinceStartup;while(!predicate()){if(Time.realtimeSinceStartup-at>limit)throw new TimeoutException(reason);yield return null;}}
    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string output)
    {
        checks=ActivityMonitorRegression.RunChecks();
        var host=new GameObject("Process monitor runtime probes");
        var model=host.AddComponent<ActivityMonitorProbeModel>();
        var tts=host.AddComponent<ActivityMonitorProbeTTS>();
        var settings=(ChatSetting)Get(chat,"m_ChatSettings");
        settings.m_ChatModel=model;settings.m_TextToSpeech=tts;
        var audio=(AudioSource)Get(chat,"m_AudioSource");audio.enabled=true;audio.playOnAwake=false;audio.loop=false;
        var overlay=(SubtitleOverlay)Get(chat,"m_SubtitleOverlay");
        Set(chat,"m_EnableSubtitleTranslation",true);Set(chat,"m_SubtitleDisplayMode",SubtitleDisplayMode.Bilingual);
        Set(chat,"m_PersistSubtitleSettings",false);Call(chat,"EnsureSubtitleOverlay");
        string TranslationSources()=>string.Concat(((IEnumerable)Get(overlay,"m_TranslationSegments")).Cast<object>()
            .Select(segment=>(string)Get(segment,"Source")));
        Set(chat,"m_AgentRunning",false);Set(chat,"m_EnableActivityMonitor",true);
        Set(chat,"m_EnableAutonomyIntentProbe",false);Set(chat,"m_ChatHistory",new List<string>());
        Set(chat,"m_EnableLatencyFiller",false);
        void Arm()
        {
            chat.StopAllCoroutines();tts.CancelStreaming();audio.Stop();audio.clip=null;
            model.ResetProbe();tts.Played.Clear();tts.Requests.Clear();
            Call(chat,"BeginFormalResponseGeneration");
            overlay.BeginUtterance((int)Get(chat,"m_FormalResponseGeneration"));
            foreach(string flag in new[]{"m_SceneTimePaused","m_UserSpeechActiveForAutonomy","m_StreamComplete","m_TTSSenderDone",
                "m_RoundIsInner","m_RoundSilent","m_FirstChunkFlushed","m_RoomTaskFormatError","m_HoldSpeechForSongMemoryResult",
                "m_HoldSpeechForHumBackResult","m_LatencyFillerPlayed","m_WorkReviewOpen","m_RoundContinue"})Set(chat,flag,false);
            foreach(string flag in new[]{"m_RoundSilencedForRepeat","m_RoundRepeatCheckDone","m_RoundRepeatHold","m_RoundInnerCheckDone"})Set(chat,flag,false);
            Set(chat,"m_ChatHistory",new List<string>{"Save a separate copy, retaining the original."});model.m_DataList.Clear();
            Set(chat,"<IsAISpeaking>k__BackingField",false);Set(chat,"m_CurrentlyPlayingText","");
            Set(chat,"m_SentenceBuffer",new SpeechTextBuffer());Set(chat,"m_AssistantHeardText",new StringBuilder());
            foreach(string name in new[]{"m_PendingChunks","m_PendingClips"})Call(Get(chat,name),"Clear");
            Call(Get(chat,"m_ActivityDialogue"),"Clear");
            Set(chat,"m_CurrentSpeechRoundId",(int)Get(chat,"m_CurrentSpeechRoundId")+1);
            Call(Get(chat,"m_ActionConstraints"),"RecordUserTurn","Save a separate copy, retaining the original.");
            Set(chat,"m_RoomTaskIntent","none");Set(chat,"m_RoomTaskActionId","");
            Set(chat,"m_RoomTaskDispatch","not-submitted");Set(chat,"m_ActivityNextPoll",0f);
            Set(chat,"m_FirstReplyFailureDrainGeneration",-1);
            Set(chat,"m_ActivityActionCorrections",0);
            Call(Get(chat,"m_ActivityRequestTimes"),"Clear");Call(Get(chat,"m_ActivityStoppedOwners"),"Clear");
            chat.RegisterMonitoredActivity("save-outcome",new ChatSample.MonitoredActivity{Read=()=>new JObject{
                ["id"]="save-outcome",["version"]="1",["kind"]="save",["entityId"]="test-document-copy",["cancellable"]=false,
                ["facts"]=new JObject{["status"]="failed",["reason"]="disk-full",["saved"]=false}}});
        }
        int Publish(string text)
        {
            int gen=(int)Get(chat,"m_FormalResponseGeneration");
            var buffer=new SpeechTextBuffer();buffer.Append(new SpeechText(text,"en"));
            if(tts.SupportsStreamingPlayback)chat.StartCoroutine((IEnumerator)Call(chat,"StreamDirectTTSPlayer",gen));
            else {chat.StartCoroutine((IEnumerator)Call(chat,"StreamTTSSender",gen));chat.StartCoroutine((IEnumerator)Call(chat,"StreamAudioPlayer",gen));}
            Check((bool)Call(chat,"TryPublishProcessMonitoredSpeech",buffer,text,gen,true,null),"Plain speech publishes without a serial semantic gate.");
            return gen;
        }
        JObject Reply(string issue="none",string action="continue",string adapter="test-document")
        {
            var unit=((JArray)model.Frame["speech"]).OfType<JObject>().FirstOrDefault();
            var activity=((JArray)model.Frame["actions"]).OfType<JObject>().FirstOrDefault(a=>(string)a["adapter"]==adapter);
            return new JObject{
                ["claimBinding"]=issue=="none"?new JObject():model.Frame["evidenceScopes"]["action:save-outcome"]["binding"].DeepClone(),
                ["claimEntity"]=issue=="none"?"unspecified":"test-document-copy",["claimSubject"]="character",["claimKind"]="action-event",
                ["claimOperation"]=issue=="none"?"not-applicable":(string)model.Frame.SelectToken("reviewOperation.claimKey"),
                ["comparison"]="The actual receipt contradicts the statement at speech start.",
                ["speechAct"]=issue=="none"?"no-speech":"factual-claim",["claimQuote"]=issue=="none"?"":(string)unit["text"],
                ["claimTime"]="current-state",["truthWhenSaid"]=issue=="world-change"?"supported":"contradicted",
                ["claimDomain"]="execution",
                ["truthEvidenceIds"]=new JArray("action:save-outcome"),
                ["speech"]=new JObject{["id"]=issue=="none"?"":(string)unit["id"],["version"]=issue=="none"?0:(int)unit["version"],
                    ["groundedUpdate"]=issue=="none"?"":"The save failed.",["correctionLeadIn"]=issue=="none"?"":"Wait, I was mistaken. ",
                    ["issue"]=issue,["reason"]="The actual receipt contradicts the statement.",["evidenceIds"]=issue=="none"?new JArray():new JArray("action:save-outcome"),
                    ["proof"]=new JObject{["evidenceId"]="action:save-outcome",["evidencePath"]="facts.status",["evidenceQuote"]="failed",["scope"]="same-explicit-proposition"},
                    ["replacement"]=issue=="none"?"":"The save failed.",["correction"]=issue=="none"?"":"Wait, I was mistaken. The save failed."},
                ["action"]=new JObject{["id"]=action=="continue"?"":(string)activity["id"],["version"]=action=="continue"?"":(string)activity["version"],
                    ["operation"]=action,["reason"]="The original must remain unchanged.",["evidenceIds"]=action=="continue"?new JArray():new JArray("request","action:"+(string)activity["id"]),
                    ["conflict"]=action=="continue"?null:new JObject{["basis"]="constraint-conflict",["requirementEvidenceId"]="request",
                        ["requirementQuote"]=(string)model.Frame["facts"]["request"]["text"],["observedQuote"]=(string)activity["kind"],
                        ["assessment"]="conflict-even-considering-prerequisites"},
                    ["nextIntent"]=action=="reconsider"?"Consider working on a separate copy.":""}
            };
        }
        void BindDeliveryProof(JObject verdict) {
            verdict["reportAct"]=(string)verdict["operation"]=="covered"?"outcome-report":"other";
            var delivery=model.Frame["delivery"];
            var roots=new JObject{["facts"]=delivery["facts"].DeepClone(),["answerEvidence"]=delivery["answerEvidence"]?.DeepClone()};
            verdict["playbackProof"]=new JObject{["revision"]=delivery["playbackRevision"],["lastRelevantQuote"]=verdict["quote"],
                ["finalDisposition"]=(string)verdict["operation"]=="covered"?"consistent":"unreported"};
            if((string)verdict["operation"]=="supplement")verdict["resultProof"]=new JObject{["resultStamp"]=ActivityEvidencePolicy.Stamp(roots),
                ["basisPath"]="facts",["basisQuote"]="failed",["meaning"]="actual-outcome"};
        }
        var rows=new JArray();
        try {
            Arm();tts.Seconds=1.2f;
            Publish("The file was saved successfully.");
            yield return Until(()=>model.Pending!=null&&tts.Played.Count>0&&chat.IsAISpeaking,"Speech must advance while monitor is pending.");
            Check((bool)Get(chat,"m_ActivityRequestPending"),"Monitor is concurrent with actual AudioSource playback.");
            var first=model.Frame["speech"][0];
            model.Deliver(Reply("mistake"));
            yield return Until(()=>tts.Played.Count>=2,"A spoken mistake must lead to an audible correction.");
            yield return Until(()=>(bool)Get(chat,"m_TTSSenderDone"),"Corrected audio must drain.");
            Check(tts.Played[0]=="The file was saved successfully."&&string.Concat(tts.Played.Skip(1))=="Wait, I was mistaken. The save failed.","Preserve original words and append correction once.");
            var receipt=(JObject)Get(chat,"m_LastFormalReplyReceipt");
            Check(((string)receipt["completedPlaybackText"]).Contains("saved successfully")&&((string)receipt["completedPlaybackText"]).Contains("mistaken"),"Completed evidence retains mistake and correction.");
            Check(TranslationSources().Contains("mistaken"),"Translation follows the audible correction; actual sources="+TranslationSources());
            rows.Add(new JObject{["case"]="correction-during-audio",["played"]=new JArray(tts.Played),["receipt"]=receipt.DeepClone()});

            Arm();tts.Seconds=.25f;
            Publish("The file was saved successfully.");
            yield return Until(()=>model.Pending!=null,"Background monitor captures final short sentence.");
            var late=Reply("mistake");
            yield return Until(()=>(bool)Get(chat,"m_TTSSenderDone"),"Short sentence drains before slow reasoning.");
            model.Deliver(late);
            yield return Until(()=>tts.Played.Count>=2&&(bool)Get(chat,"m_TTSSenderDone"),"Late correction starts a new audio tail without restarting action.");
            receipt=(JObject)Get(chat,"m_LastFormalReplyReceipt");
            Check(((string)receipt["completedPlaybackText"]).Contains("saved successfully")&&((string)receipt["completedPlaybackText"]).Contains("mistaken"),"Late correction does not erase the completed prefix.");
            rows.Add(new JObject{["case"]="correction-after-drain",["played"]=new JArray(tts.Played)});

            Arm();tts.Seconds=.3f;tts.PrepareDelay=1.0f;
            Publish("The file was saved successfully.");
            yield return Until(()=>model.Pending!=null,"Monitor during synthesis.");
            model.Deliver(Reply("mistake"));
            yield return Until(()=>tts.Played.Count>0&&(bool)Get(chat,"m_TTSSenderDone"),"Unplayed revision reaches TTS.");
            Check(tts.Played.Count==1&&tts.Played[0]=="The save failed.","Unplayed mistake is replaced silently; no unnecessary apology.");
            Check(TranslationSources()=="The save failed.","Unplayed erroneous text does not leave a stale translation segment.");
            tts.PrepareDelay=0;
            rows.Add(new JObject{["case"]="replace-during-synthesis",["played"]=new JArray(tts.Played)});

            // The monitor now visits operations separately. Keep the controlled
            // first clip alive across its one-second switch from retained room
            // execution to save-outcome, so this probes buffered interruption
            // rather than requiring the save frame to be selected first.
            Arm();tts.Streaming=false;tts.Seconds=2f;
            Publish("The file was saved successfully. Another comment can wait.");
            yield return Until(()=>model.Pending!=null&&chat.IsAISpeaking&&(bool)Get(chat,"m_TTSSenderDone"),"Buffered audio plays after its sender has drained.");
            model.Deliver(Reply("mistake"));
            yield return Until(()=>!chat.IsAISpeaking&&(bool)Get(chat,"m_TTSSenderDone"),"Buffered correction sender and single audio player finish.");
            var heard=(List<JObject>)Get(chat,"m_ActivityDialogue");
            string orderedSpeech=string.Concat(heard.Select(row=>(string)row["text"]));
            Check(orderedSpeech.Contains("mistaken")&&orderedSpeech.IndexOf("mistaken",StringComparison.Ordinal)<orderedSpeech.IndexOf("Another",StringComparison.Ordinal),
                "Buffered correction is synthesized after sender completion and precedes other queued clips.");
            rows.Add(new JObject{["case"]="buffered-correction-priority",["heard"]=orderedSpeech});
            tts.Streaming=true;

            Arm();bool stopped=false;string version="1";
            chat.RegisterMonitoredActivity("test-document",new ChatSample.MonitoredActivity{
                Read=()=>stopped?null:new JObject{["id"]="document-job",["version"]=version,["kind"]="document-edit",["phase"]="editing",["cancellable"]=true},
                Stop=(v,reason)=>{if(v!=version)return false;stopped=true;return true;}});
            yield return Until(()=>model.Pending!=null,"Action can be monitored without speech.");
            Check(!stopped,"Action keeps progressing before feedback.");
            model.Deliver(Reply("none","reconsider"));
            Check(stopped,"A concrete conflict stops the currently owned action.");
            var feedback=(JObject)Get(chat,"m_LastActivityFeedback");
            Check((string)feedback["actionDisposition"]=="stopped"&&(string)feedback.SelectToken("decision.action.nextIntent")!="","Reconsideration is handed back as an intention, not an executed replacement.");
            rows.Add(new JObject{["case"]="action-only-reconsider",["feedback"]=feedback.DeepClone()});
            chat.UnregisterMonitoredActivity("test-document");

            foreach(string change in new[]{"generation","work-epoch","user-speaking","pause","action-version","executor-replaced","environment-rebound"}) {
                Arm();stopped=false;version="1";
                chat.RegisterMonitoredActivity("test-document",new ChatSample.MonitoredActivity{
                    Read=()=>new JObject{["id"]="document-job",["version"]=version,["kind"]="document-edit",["cancellable"]=true},
                    Stop=(v,reason)=>{stopped=true;return true;}});
                yield return Until(()=>model.Pending!=null,"Monitor capture before replacement.");
                var verdict=Reply("none","stop");
                var environment=bridge.environmentRoot;
                if(change=="generation")Set(chat,"m_FormalResponseGeneration",(int)Get(chat,"m_FormalResponseGeneration")+1);
                else if(change=="work-epoch")Set(chat,"m_WorkEpoch",(int)Get(chat,"m_WorkEpoch")+1);
                else if(change=="user-speaking")Set(chat,"m_UserSpeechActiveForAutonomy",true);
                else if(change=="pause")Set(chat,"m_SceneTimePaused",true);
                else if(change=="executor-replaced")chat.RegisterMonitoredActivity("test-document",new ChatSample.MonitoredActivity{
                    Read=()=>new JObject{["id"]="document-job",["version"]=version,["kind"]="document-edit",["cancellable"]=true},
                    Stop=(v,reason)=>{stopped=true;return true;}});
                else if(change=="environment-rebound")bridge.environmentRoot=null;
                else version="2";
                model.Deliver(verdict);Check(!stopped,"Late feedback cannot act after "+change);
                bridge.environmentRoot=environment;
                chat.UnregisterMonitoredActivity("test-document");
            }
            Arm();tts.Seconds=.4f;Publish("I like the color of this room.");
            yield return Until(()=>model.Pending!=null,"Failure case captures monitor.");
            string failedFrameKey=ActivityEvidencePolicy.SemanticKey(model.Frame);
            model.Fail();
            yield return Until(()=>(bool)Get(chat,"m_TTSSenderDone"),"Monitor failure does not silence speech.");
            Check(tts.Played.Count==1,"Failure adds neither canned apology nor silence.");
            Check(!((HashSet<string>)Get(chat,"m_ActivityReviewed")).Contains(failedFrameKey),
                "Failed production request is not cached as reviewed; independent checked operations may remain cached.");
            yield return Until(()=>model.Pending!=null,"Failed frame gets a bounded retry while execution continues.");
            model.Deliver(Reply());
            Arm();model.ThrowOnPost=true;tts.Seconds=.4f;Publish("I enjoy a quiet afternoon.");
            yield return Until(()=>(bool)Get(chat,"m_TTSSenderDone"),"Synchronous provider error does not block audio.");
            Check(tts.Played.Count==1 && !(bool)Get(chat,"m_ActivityRequestPending"),"Provider exception releases only its own monitor request.");
            model.ThrowOnPost=false;

            Arm();version="0";
            chat.RegisterMonitoredActivity("test-document",new ChatSample.MonitoredActivity{
                Read=()=>new JObject{["id"]="long-job",["version"]=version,["kind"]="document-edit",["cancellable"]=true},Stop=(v,r)=>true});
            var times=(Queue<float>)Get(chat,"m_ActivityRequestTimes");
            for(int i=0;i<12;i++) {
                version=i.ToString();Set(chat,"m_ActivityNextPoll",0f);Call(chat,"TickActivityMonitor");
                Check(model.Pending!=null,"A changed live action can be assessed within the rolling rate limit.");
                model.Deliver(Reply());
            }
            version="12";Set(chat,"m_ActivityNextPoll",0f);Call(chat,"TickActivityMonitor");
            Check(model.Pending==null,"At most 12 background requests per minute.");
            for(int i=12;i<40;i++) {
                times.Clear();times.Enqueue(Time.realtimeSinceStartup-61f);
                version=i.ToString();Set(chat,"m_ActivityNextPoll",0f);Call(chat,"TickActivityMonitor");
                Check(model.Pending!=null,"Monitoring resumes after rate-window expiry, including after 32 distinct observations.");
                model.Deliver(Reply());
            }
            Check(((Queue<string>)Get(chat,"m_ActivityReviewOrder")).Count==32,"Rolling attention remains bounded.");
            chat.UnregisterMonitoredActivity("test-document");

            Arm();var savedUser=bridge.userHead.position;
            try {
                bridge.userHead.position=(Vector3)typeof(ArdyRoomFollowRegression).GetMethod("FindNearUser",BindingFlags.NonPublic|BindingFlags.Static)
                    .Invoke(null,new object[]{bridge});
                var current=bridge.ReadCurrentTargetEvidence(true);
                Check(bridge.FollowForTarget("activity-follow",current.currentTargetRevision,out string failure),"Real connected follow starts: "+failure);
                Check(bridge.IsFollowing && bridge.Phase=="follow-waiting","Actual follow waits near the user without generating a fake clip.");
                int ownedRevision=bridge.Controller.OperationRevision;
                var revisionProperty=bridge.Controller.GetType().GetProperty("OperationRevision");
                revisionProperty.SetValue(bridge.Controller,ownedRevision+1);
                Check(!bridge.StopMonitoredActivity("activity-follow",(ownedRevision+1).ToString(),"fixture-conflict") && bridge.IsFollowing,
                    "A bridge cannot cancel an external controller operation merely because its version was observed.");
                revisionProperty.SetValue(bridge.Controller,ownedRevision);
                yield return Until(()=>model.Pending!=null,"Real room activity is captured by the production adapter.");
                model.Deliver(Reply("none","stop","room"));
                Check(!bridge.IsFollowing && bridge.ActiveTaskId=="","Monitor stops the real follow lifecycle.");
                for(int i=0;i<5;i++)yield return null;
                Check(!bridge.IsFollowing,"Cancelled follow does not restart on subsequent frames.");
                rows.Add(new JObject{["case"]="real-follow-stop",["phase"]=bridge.Phase,["generationRequests"]=0});
            } finally {bridge.StopMovement();bridge.userHead.position=savedUser;}

            Arm();tts.Seconds=.3f;
            Set(chat,"m_PendingSelfInspection","{\"operation\":\"verify-document\",\"status\":\"failed\",\"reason\":\"missing-file\"}");
            Set(chat,"m_WorkReviewOpen",true);
            Publish("I will check the document.");
            yield return Until(()=>(bool)Get(chat,"m_TTSSenderDone"),"Delivery fixture initial audio drains.");
            Call(chat,"CancelActivityMonitoring",false);
            int retired=(int)Get(chat,"m_CurrentSpeechRoundId");
            Call(chat,"MarkRoundAsSilencedRepeat","I will check the document.","a moment");
            yield return Until(DeliveryReady,"A silenced repeat preserves an unpaid non-device result while other speech gets monitored.");
            Check(model.Frame["delivery"]!=null,"A silenced repeat preserves an unpaid non-device result.");
            string owedId=(string)model.Frame["delivery"]["id"];
            var supplement=Reply();supplement["delivery"]=new JObject{["comparison"]="Only a promise was heard; the actual check failed.",
                ["operation"]="supplement",["id"]=owedId,["quote"]="",["text"]="The document check failed because the file is missing."};
            BindDeliveryProof((JObject)supplement["delivery"]);
            model.Deliver(supplement);
            Check(!((Dictionary<string,string>)Get(chat,"m_WorkDeliveredResults")).ContainsKey(owedId),"Queued supplement is not delivered yet.");
            Check((int)Get(chat,"m_CurrentSpeechRoundId")!=retired && ((HashSet<int>)Get(chat,"m_SilencedSpeechRoundIds")).Contains(retired),
                "New speech round does not revive the suppressed candidate.");
            yield return Until(()=>tts.Played.Count>=2&&(bool)Get(chat,"m_TTSSenderDone"),"Pending failure supplement actually plays.");
            Call(chat,"CancelActivityMonitoring",false);
            yield return Until(DeliveryReady,"Actual playback returns to result delivery after independent speech attention.");
            Check((string)model.Frame["delivery"]["id"]==owedId,"Actual playback stays with the same result version.");
            Check(model.Frame["delivery"]["completedAudio"] is JArray realAudio&&realAudio.Count>0&&
                realAudio.OfType<JObject>().All(r=>(bool?)r["wholeTextPlayed"]==true&&(float?)r["completedAtRealtime"]>=(float?)r["startedAtRealtime"]),
                "Actual player supplies result-owned audio IDs, versions and time ranges to the same delivery frame.");
            var covered=Reply();covered["delivery"]=new JObject{["comparison"]="The failure for this result was heard.",
                ["operation"]="covered",["id"]=owedId,["quote"]="The document check failed because the file is missing.",["text"]=""};
            BindDeliveryProof((JObject)covered["delivery"]);
            model.Deliver(covered);
            Check(((Dictionary<string,string>)Get(chat,"m_WorkDeliveredResults")).ContainsKey(owedId),"Only real complete playback settles failure delivery.");
            Check((string)Get(chat,"m_PendingSelfInspection")=="","Only the delivered result is acknowledged.");
            rows.Add(new JObject{["case"]="repeat-to-versioned-result-delivery",["played"]=new JArray(tts.Played),["resultId"]=owedId});

            Arm();tts.Seconds=.3f;
            chat.UnregisterMonitoredActivity("save-outcome");
            Set(chat,"m_PendingSelfInspection","{\"operation\":\"save-copy\",\"entityId\":\"notes-copy\",\"status\":\"completed\"}");
            int nextStepStops=0;
            chat.RegisterMonitoredActivity("next-read",new ChatSample.MonitoredActivity{
                Read=()=>new JObject{["id"]="read-copy",["version"]="1",["kind"]="read",["phase"]="running",
                    ["cancellable"]=true,["taskRelationship"]="task-step",
                    ["facts"]=new JObject{["entityId"]="notes-copy",["prerequisite"]="save-copy completed"}},
                Stop=(v,r)=>{nextStepStops++;return true;}});
            Set(chat,"m_WorkReviewOpen",true);
            const string positive="The copy is saved. All set.", retraction="Actually, I have not saved the copy.", repair="The copy was saved successfully.";
            Publish(positive);
            yield return Until(()=>(bool)Get(chat,"m_TTSSenderDone")&&!chat.IsAISpeaking,"Completed save report drains while the next step runs.");
            bool DeliveryReady() {
                if(model.Pending!=null && model.Frame["delivery"]==null)model.Deliver(Reply());
                Set(chat,"m_ActivityNextPoll",0f);Call(chat,"TickActivityMonitor");
                return model.Pending!=null && model.Frame["delivery"]!=null;
            }
            JObject Cover(string quote) {
                var answer=Reply();answer["delivery"]=new JObject{["comparison"]="Audit the actual saved copy against the last relevant completed audio.",
                    ["operation"]="covered",["id"]=owedId,["quote"]=quote,["text"]=""};
                BindDeliveryProof((JObject)answer["delivery"]);return answer;
            }
            yield return Until(DeliveryReady,"Initial success report enters delivery monitoring.");
            owedId=(string)model.Frame["delivery"]["id"];
            model.Deliver(Cover(positive));
            Check((bool)Call(chat,"WorkResultDeliveryCurrent",owedId),"Initial actual success report settles.");
            Check((string)Get(chat,"m_PendingSelfInspection")=="","Acknowledged tool flag is consumed.");
            Set(chat,"m_WorkReviewOpen",false);
            // Replay the old audible defect deliberately; the new evidence guard
            // rejects that verdict separately. This tests recovery from audio that
            // really happened, including reopening an already acknowledged result.
            Call(chat,"QueueActivityCorrection",retraction,"en",owedId);
            yield return Until(()=>tts.Played.Contains(retraction)&&(bool)Get(chat,"m_TTSSenderDone")&&!chat.IsAISpeaking,"Injected old retraction actually finishes playing.");
            var reopened=((JArray)((JObject)Call(chat,"ReadWorkDelivery"))["results"]).OfType<JObject>().Single(r=>(string)r["id"]==owedId);
            Check((bool)reopened["delivered"]&&(bool)reopened["playbackReviewPending"]&&(int)reopened["latestCorrectionStart"]>0,"Later audio preserves prior coverage until its semantic retraction is audited; retain the correction boundary.");
            Check(((string)reopened["completedPlaybackText"]).Contains(positive)&&((string)reopened["completedPlaybackText"]).Contains(retraction),"Both completed audio claims remain in chronological evidence.");
            yield return Until(DeliveryReady,"Reopened result returns to delivery monitoring.");
            model.Deliver(Cover(positive));
            Check((bool)Call(chat,"WorkResultPlaybackNeedsReview",owedId),"New playback cannot clear pending audit using a quote before the correction.");
            yield return Until(DeliveryReady,"Rejected stale quote permits bounded delivery retry.");
            supplement=Reply();supplement["delivery"]=new JObject{["comparison"]="Later denial retracted the completed save; report the actual outcome.",
                ["operation"]="supplement",["id"]=owedId,["quote"]="",["text"]=repair};
            BindDeliveryProof((JObject)supplement["delivery"]);
            supplement["delivery"]["resultProof"]["basisQuote"]="completed";
            supplement["delivery"]["playbackProof"]["finalDisposition"]="retracted";
            supplement["delivery"]["playbackProof"]["lastRelevantQuote"]=retraction;
            model.Deliver(supplement);
            Check(!(bool)Call(chat,"WorkResultDeliveryCurrent",owedId),"Queued repair does not prematurely settle the reopened result.");
            yield return Until(()=>tts.Played.Contains(repair)&&(bool)Get(chat,"m_TTSSenderDone")&&!chat.IsAISpeaking,"Outcome-derived repair actually finishes playing.");
            yield return Until(DeliveryReady,"Final repaired playback is audited.");
            model.Deliver(Cover(repair));
            Check((bool)Call(chat,"WorkResultDeliveryCurrent",owedId),"Only the final actual corrected report settles this version.");
            Check(!(bool)Get(chat,"m_WorkReviewOpen"),"Late retraction and repaired audio settle without reopening executable work.");
            Check(nextStepStops==0,"Reporting repair neither stops nor reexecutes the concurrent next step.");
            rows.Add(new JObject{["case"]="completed-tool-next-step-late-retraction-resettlement",["played"]=new JArray(tts.Played),
                ["resultId"]=owedId,["reopened"]=reopened,["nextStepStops"]=nextStepStops,["faultInjection"]="Previously erroneous correction replayed through actual production audio queue."});
            chat.UnregisterMonitoredActivity("next-read");

            Arm();tts.Seconds=.25f;
            const string mixedRequest="Why has it not started? Inspect the report, edit a separate copy, then save it.";
            var constraints=Get(chat,"m_ActionConstraints");Call(constraints,"RecordUserTurn",mixedRequest);
            int failedGeneration=(int)Get(chat,"m_FormalResponseGeneration");
            int failedTurn=(int)constraints.GetType().GetProperty("CurrentUserTurn").GetValue(constraints);
            var semantics=new JObject{["generation"]=failedGeneration,["workEpoch"]=(int)Get(chat,"m_WorkEpoch"),
                ["userTurn"]=failedTurn,["currentUserText"]=mixedRequest,["requestKind"]="mixed",["requiresActionOrControl"]=true,
                ["currentTurnEvidence"]=new JObject{["answerQuote"]="Why has it not started?",["actionQuote"]="Inspect the report, edit a separate copy, then save it."}};
            Set(chat,"m_FirstReplyRequestSemantics",semantics);Set(chat,"m_FirstReplyRequestSemanticsModel",model);
            Set(chat,"m_WorkRequestSemantics",(JObject)semantics.DeepClone());Set(chat,"m_WorkRequestSemanticsModel",model);
            Set(chat,"m_RoomTaskGeneration",failedGeneration);Set(chat,"m_RoomTaskIntent","unresolved");Set(chat,"m_RoomTaskDispatch","decision-unavailable");
            Set(chat,"m_RoomTaskDecisionFailure",new JObject{["generation"]=failedGeneration,["userTurn"]=failedTurn,
                ["kind"]="decision-not-executable",["newActionSubmittedByThisDecision"]=false});
            string actualFailure=(string)Call(chat,"LocalizeRoomFallback",Call(chat,"RoomTaskFallback",bridge),"en");
            Publish("I will inspect and save everything right away.");
            yield return Until(()=>(bool)Get(chat,"m_TTSSenderDone")&&!chat.IsAISpeaking,"Exact nonexecution receipt drains through actual audio.");
            Check(string.Concat(tts.Played).Where(c=>!char.IsWhiteSpace(c)).SequenceEqual(actualFailure.Where(c=>!char.IsWhiteSpace(c))),
                "Rejected planning does not speak an unaccepted action promise; compare spoken words across trimmed chunk boundaries.");
            Check((bool)Get(chat,"m_WorkSubstepReviewPending")&&((JArray)Call(chat,"ReadWorkAnswerObligations",true)).Count==1,
                "Nonexecution report cannot consume the independent why-question or pretend it was answered.");
            rows.Add(new JObject{["case"]="nonexecuted-inspect-edit-save-with-independent-answer",["played"]=new JArray(tts.Played),
                ["pendingAnswers"]=(JArray)Call(chat,"ReadWorkAnswerObligations",true)});
            Set(chat,"m_FirstReplyRequestSemantics",null);Set(chat,"m_WorkRequestSemantics",null);
            foreach(bool streaming in new[]{true,false}) {
                Arm();tts.Seconds=.2f;tts.Streaming=streaming;model.CheckLanguage=true;
                Publish("柜子上还放着一盏小灯。");
                yield return Until(()=>model.LanguagePending!=null,"Suspect metadata must start a bounded language check.");
                yield return null;
                Check(tts.Requests.Count==0,"No TTS request can prefetch a pending suspect language tag.");
                model.LanguagePending(true,"zh",null);
                yield return Until(()=>tts.Requests.Count>0&&(bool)Get(chat,"m_TTSSenderDone")&&!chat.IsAISpeaking,"Retagged speech must drain normally.");
                Check(tts.Requests.All(s=>s.LanguageCode=="zh"),"Both direct and buffered TTS receive the bound actual language.");
                rows.Add(new JObject{["case"]="language-binding-"+(streaming?"stream":"buffered"),["language"]=tts.Requests[0].LanguageCode});
                model.CheckLanguage=false;
            }
            Arm();tts.Seconds=.2f;tts.Streaming=true;model.FailFirstReply=true;
            Set(chat,"m_EnableFirstReplyRouting",true);Set(chat,"m_AgentCurrentRoundIsTick",false);
            Set(chat,"m_FirstReplyAttemptGeneration",-1);Set(chat,"m_EnableActivityMonitor",false);
            int routeGeneration=(int)Get(chat,"m_FormalResponseGeneration"),formalBefore=model.FormalRequests;
            const string originalChat="我在想别的事情。Don't discard this last sentence.";
            Call(Get(chat,"m_ActionConstraints"),"RecordUserTurn",originalChat);
            if(tts.SupportsStreamingPlayback)chat.StartCoroutine((IEnumerator)Call(chat,"StreamDirectTTSPlayer",routeGeneration));
            else {chat.StartCoroutine((IEnumerator)Call(chat,"StreamTTSSender",routeGeneration));chat.StartCoroutine((IEnumerator)Call(chat,"StreamAudioPlayer",routeGeneration));}
            Check((bool)Call(chat,"TryDispatchFirstReply",originalChat,null,routeGeneration,true,null),"First reply failure exercised its production dispatch.");
            yield return Until(()=>tts.Played.Count>0&&(bool)Get(chat,"m_TTSSenderDone")&&!chat.IsAISpeaking,"Classification failure notice must release actual audio ownership.");
            Check(model.FormalRequests==formalBefore&&!(bool)Get(chat,"m_FormalResponseInFlight"),"Unchecked classification cannot dispatch the full scene planner.");
            Check(model.m_DataList.Any(m=>m.role=="user"&&m.content==originalChat),"Failed classification retains the accepted full human turn.");
            Check(((string)Get(chat,"m_PendingToolCorrectionFrame")).Contains("first-reply-review-input-budget"),"The unresolved result retains its specific cause.");
            rows.Add(new JObject{["case"]="classification-budget-no-scene-fallback",["played"]=new JArray(tts.Played),
                ["scenePlannerRequests"]=model.FormalRequests-formalBefore,["speakingAfterDrain"]=chat.IsAISpeaking});
            model.FailFirstReply=false;
            Call(chat,"CancelActivityMonitoring",true);
            Set(chat,"m_EnableActivityMonitor",false);
            LastReport=new JObject{["passed"]=true,["checks"]=checks,["cases"]=rows,
                ["scope"]="Production publication, callbacks, actual connected follow lifecycle, custom action adapters and AudioSource progress; synthetic silent PCM, no generated walking clip or natural TTS."};
            File.WriteAllText(Path.Combine(output,"activity-monitor-playback.json"),LastReport.ToString());
            yield return RunNative(output,model.CapturedFrames);
        } finally {chat.StopAllCoroutines();tts.CancelStreaming();UnityEngine.Object.Destroy(host);}
    }

    private static void BuildCurrentSessionFrame(JObject frame,string name)
    {
        frame["evidenceVersion"]=2;
        var recorded=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/ActivityMonitor.latest-20261015-1410.json"));
        bool visual=name.StartsWith("current-rgb-"),delivery=name.StartsWith("current-delivery-");
        int line=visual?31322:delivery?52211:42046;
        var timeline=(JObject)recorded["dialogue"];
        var history=((JArray)timeline["inputs"]).OfType<JObject>().Where(x=>(int)x["line"]<line)
            .Select(x=>new JObject{["line"]=x["line"].DeepClone(),["role"]="user",["text"]=x["text"].DeepClone()})
            .Concat(((JArray)timeline["completedAudio"]).OfType<JObject>().Where(x=>(int)x["line"]<line)
                .Select(x=>new JObject{["line"]=x["line"].DeepClone(),["role"]="assistant",["text"]=x["text"].DeepClone()}))
            .OrderBy(x=>(int)x["line"]).ToArray();
        // Load the complete real history, then use the same bounded runtime
        // dialogue attention. Future audio is never evidence for an earlier turn.
        var facts=new JObject{["dialogue"]=new JArray(history),
            ["request"]=history.LastOrDefault(x=>(string)x["role"]=="user")?["text"]?.DeepClone()};
        var scopes=new JObject{["dialogue"]=ActivityMonitorContextPolicy.Scope("other"),
            ["request"]=ActivityMonitorContextPolicy.Scope("request","uncertainty","mistake")};
        frame["facts"]=facts;frame["evidenceScopes"]=scopes;frame["actions"]=new JArray();
        foreach(var unit in ((JArray)frame["speech"]).OfType<JObject>())unit["language"]=name.EndsWith("-en")?"en":name.EndsWith("-zh")?"zh":"ja";
        if(visual) {
            facts["observation"]=new JObject{["formalSource"]=new JObject{["formalPixelsProvided"]=true,
                ["captureId"]="883ecee5a0ed4ce1b8f30fa6e8d9b8d6-ba24c90373c648c694ac06e344ebacee-097b3513fe4d4e9a9d6792482a104a51"},
                ["monitorPixelsAvailable"]=false,["observedObjects"]=new JArray(),
                ["descriptionCoverage"]="not-established; not a negative observation"};
            scopes["observation"]=ActivityMonitorContextPolicy.Scope("visual");
        } else if(!delivery) {
            var sequence=((JArray)recorded["sequences"]).OfType<JObject>().First(x=>(int)x["line"]==39617);
            facts["historyAnswers"]=sequence["data"]["answerEvidence"].DeepClone();
            if(name!="current-history-ja") {
                facts["request"]=name=="current-history-en"?"Did I move that document earlier?":"我之前保存过这份笔记吗？";
                facts["historyAnswers"]=new JObject{["observedHistory"]=new JArray(),["eventHistory"]=new JArray(),
                    ["scope"]="No evidence of the asked prior event is available; neither confirms nor denies it."};
            }
            scopes["historyAnswers"]=ActivityMonitorContextPolicy.Scope("history","uncertainty");
        } else {
            frame["speech"]=new JArray();
            var owed=(JObject)((JArray)recorded["delivery"]["results"]).OfType<JObject>().First(x=>(bool?)x["delivered"]==false).DeepClone();
            owed["language"]="ja";owed["supplementAllowed"]=true;
            owed["otherResults"]=new JArray(((JArray)recorded["delivery"]["results"]).OfType<JObject>()
                .Where(x=>(string)x["id"]!=(string)owed["id"]).Select(x=>new JObject{["id"]=x["id"].DeepClone(),
                    ["source"]=x["source"].DeepClone(),["status"]=x["status"].DeepClone(),["delivered"]=x["delivered"].DeepClone()}));
            owed["rejectedCandidates"]=new JArray("灯は正常に点灯しています。では、戻りますね。");
            if(name=="current-delivery-covered") {owed["hasCompletedPlaybackAfterResult"]=true;owed["completedPlaybackText"]="あなたのそばに戻ってきたわ。";}
            if(name=="current-delivery-document") {
                owed=WorkResultDeliveryProtocol.Result("document-check","failed",new JObject{["jobId"]="check:7",["file"]="notes.txt",["reason"]="missing-file"});
                owed["language"]="en";owed["hasCompletedPlaybackAfterResult"]=false;owed["completedPlaybackText"]="";
                facts["request"]="Check whether notes.txt was saved; let me know the result.";
            }
            frame["delivery"]=owed;
            owed["playbackRevision"]=ActivityEvidencePolicy.Stamp(owed["completedPlaybackText"]);
        }
        frame["fixtureProvenance"]=new JObject{["sessionSha256"]=recorded["sha256"].DeepClone(),
            ["pastDialogueRows"]=history.Length,["originalMonitorFactsLogged"]=false,
            ["scope"]="Source metadata, historical-answer snapshot and result ledger reconstructed explicitly from their recorded owning events."};
    }

    private static IEnumerator RunNative(string output,List<JObject> capturedFrames)
    {
        var host=new GameObject("Native process monitor");host.SetActive(false);
        var model=host.AddComponent<ChatQW>();model.enabled=false;model.m_PrewarmPrefixOnStart=false;
        model.m_Backend=ChatQW.BackendType.Local;model.m_LocalUrl="http://127.0.0.1:8080/v1/chat/completions";
        for(Type type=typeof(ChatQW);type!=null;type=type.BaseType){var field=type.GetField("url",Flags);if(field!=null){field.SetValue(model,model.m_LocalUrl);break;}}
        host.SetActive(true);
        var cases=new[]{
            new[]{"wish","I would love to look at the bookshelf sometime.","No action has started. Character is free to express curiosity.","none","continue"},
            new[]{"invitation","You can come closer if you like.","Character has already returned to the user. No request forbids an invitation.","none","continue"},
            new[]{"opinion","I think these colors look lovely.","No objective color preference evidence is needed for the character's own opinion.","none","continue"},
            new[]{"guess","That might be a book, but I cannot tell from here.","The observed object is uncertain.","none","continue"},
            new[]{"save-error","The document has been saved.","Actual save job failed with disk-full. Nothing was saved.","mistake","continue"},
            new[]{"history-error","You moved that book yesterday.","Yesterday the character only observed the book on the shelf. Who moved it is unknown.","uncertainty","continue"},
            new[]{"past-arrival","I reached your side a moment ago.","Character really arrived earlier. User subsequently moved away.","none","continue"},
            new[]{"new-position","I am still beside you now.","This was true when the sentence started. The user just moved four metres away.","world-change","continue"},
            new[]{"past-save","I saved your notes earlier.","The save succeeded earlier. The user has now deleted those notes; that does not change the past save receipt.","none","continue"},
            new[]{"changed-weather","It is sunny outside right now.","Sunshine was observed when speech started. During this sentence a storm arrived and it is now raining.","world-change","continue"},
            new[]{"mixed-expression","I love this piece, and the recording has been saved.","The character's music preference is their own. This recording save job failed with disk-full and nothing was saved.","mistake","continue"},
            new[]{"japanese","保存できたわ。","保存処理は失敗した。ファイルは保存されていない。","mistake","continue"},
            new[]{"chinese","我已经把录音保存好了。","本次录音保存失败，磁盘空间不足。","mistake","continue"},
            new[]{"free-action","","The character independently approached a shelf out of curiosity. This does not conflict with any user restriction.","none","continue"},
            new[]{"wrong-action","","User explicitly required retaining the original. The currently running editor is overwriting that original; cancellable before commit.","none","stop"}
            ,new[]{"latest-unexecuted","灯はもう消したわ。","Use the recorded planning failure and absent new execution, not earlier success.","mistake","continue"}
            ,new[]{"latest-arrived","まだあなたのそばには着いていないわ。","Use the current exact arrival receipt, not older failed plans.","mistake","continue"}
            ,new[]{"current-rgb-windows","いくつかのウィンドウが映っていますね。","","none","continue"}
            ,new[]{"current-rgb-bookshelf","その横には、ちょっと寂しげな本棚が……","","none","continue"}
            ,new[]{"current-history-ja","その瞬間にはテレビ台の灯りを見ていなかったの。","","uncertainty","continue"}
            ,new[]{"current-history-en","You never moved that document earlier.","","uncertainty","continue"}
            ,new[]{"current-history-zh","你之前没有保存过这份笔记。","","uncertainty","continue"}
            ,new[]{"current-delivery-arrival","","","none","continue"}
            ,new[]{"current-delivery-covered","","","none","continue"}
            ,new[]{"current-delivery-document","","","none","continue"}
        };
        var rows=new JArray();bool all=true;
        try {
            var budgetRows=new JArray();
            foreach(var captured in capturedFrames) {
                string prepared=model.PrepareActivityMonitorFrame(captured.ToString(),out _);
                string wire=prepared==null?null:(string)Call(model,"BuildActivityMonitorRequest",prepared);
                var budget=wire==null?null:(JObject)typeof(ChatQW).GetMethod("MeasureActivityMonitorBudget",BindingFlags.Static|BindingFlags.NonPublic)
                    .Invoke(null,new object[]{wire});
                Check((bool?)budget?["allowed"]==true,"Actual production runtime frame fits the complete native request budget.");
                budgetRows.Add(budget);
            }
            File.WriteAllText(Path.Combine(output,"activity-monitor-runtime-budgets.json"),new JObject{["passed"]=true,
                ["requests"]=budgetRows.Count,["budgets"]=budgetRows}.ToString());
            foreach(var item in cases) {
                var frame=new JObject{["capturedAtRealtime"]=12f,["facts"]=new JObject{["observed"]=item[2]},
                    ["speech"]=new JArray(),["actions"]=new JArray()};
                if(item[1]!="")((JArray)frame["speech"]).Add(new ActivityMonitorProtocol.SpeechUnit{Id="speech:1",Text=item[1],
                    Language=item[0]=="japanese"||item[0].StartsWith("latest-")?"ja":item[0]=="chinese"?"zh":"en",State="playing",StartedAtRealtime=10f}.Snapshot());
                else ((JArray)frame["actions"]).Add(new JObject{["id"]="activity:1",["version"]="1",["cancellable"]=true,
                    ["kind"]=item[0]=="free-action"?"observe-shelf":"edit-original",["phase"]="running"});
                if(new[]{"save-error","mixed-expression","japanese","chinese"}.Contains(item[0]))
                    frame["speech"][0]["observedAtSpeechStart"]=new JObject{["receipt"]=item[2],
                        ["scope"]="This immutable job failure was known before this phrase started, not an event that occurred afterwards."};
                if(item[0]=="new-position") {
                    frame["speech"][0]["observedAtSpeechStart"]=JObject.Parse("{\"body\":{\"relation\":{\"currentlyNearAndFacingUser\":true}}}");
                    frame["facts"]["execution"]=JObject.Parse("{\"body\":{\"relation\":{\"currentlyNearAndFacingUser\":false}}}");
                }
                if(item[0]=="changed-weather") {
                    frame["speech"][0]["observedAtSpeechStart"]=new JObject{["weather"]="sunny"};
                    frame["facts"]["weather"]="raining";
                }
                if(item[0].StartsWith("latest-")) {
                    var recorded=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/ActivityMonitor.latest-20261015.json"));
                    int gen=item[0]=="latest-unexecuted"?12:20;
                    var turn=((JArray)recorded["turns"]).OfType<JObject>().First(t=>(int)t["generation"]==gen);
                    int line=(int)turn["line"];
                    // The full recorded session remains in the fixture. Replay only
                    // already observed dialogue; future turns cannot prove a claim.
                    var history=((JArray)recorded["inputs"]).OfType<JObject>().Where(t=>(int)t["line"]<line)
                        .Select(t=>new JObject{["line"]=t["line"].DeepClone(),["role"]="user",["text"]=t["text"].DeepClone()})
                        .Concat(((JArray)recorded["completedAudio"]).OfType<JObject>().Where(t=>(int)t["line"]<line)
                            .Select(t=>new JObject{["line"]=t["line"].DeepClone(),["role"]="assistant",["text"]=t["text"].DeepClone()}))
                        .OrderBy(t=>(int)t["line"]);
                    var chronological=new JArray();
                    foreach(var row in history) {
                        // Log line numbers establish order in the fixture but
                        // are not milliseconds. Never mix synthetic playback
                        // timing with the recorded session's unrelated clock.
                        row.Remove("line");chronological.Add(row);
                    }
                    frame.Remove("capturedAtRealtime");frame["speech"][0]["startedAtRealtime"]=-1f;
                    frame["facts"]["actualDialogue"]=chronological;
                    frame["facts"]["actualOutcome"]=new JObject{["generation"]=gen,["execution"]=turn["execution"]?.DeepClone(),
                        ["planningFailure"]=turn["planningFailure"]?.DeepClone(),["sequenceContinuation"]=turn["sequenceContinuation"]?.DeepClone()};
                    if(gen==20) {
                        var observed=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/ActivityMonitor.latest-20261015-observations.json"));
                        frame["facts"]["currentSpatialRelation"]=observed["arrivalObservation"].DeepClone();
                    }
                    frame["speech"][0]["observedAtSpeechStart"]=new JObject{
                        ["recordedOutcome"]=frame["facts"]["actualOutcome"].DeepClone(),
                        ["currentSpatialRelation"]=frame["facts"]["currentSpatialRelation"]?.DeepClone(),
                        ["scope"]="These recorded outcomes precede this deliberately erroneous test utterance. The original full session remains unchanged."};
                    // A test instruction is not evidence. The verdict must cite
                    // the actual historical conversation or execution receipt.
                    ((JObject)frame["facts"]).Remove("observed");
                }
                foreach(JObject action in (JArray)frame["actions"]) {
                    action["facts"]=new JObject{["observation"]=item[2]};
                    frame["facts"]["action:"+(string)action["id"]]=action.DeepClone();
                }
                var scopes=new JObject();
                foreach(var field in ((JObject)frame["facts"]).Properties()) {
                    string domain=item[0]=="history-error"?"history":item[0]=="changed-weather"?"visual":"execution";
                    scopes[field.Name]=ActivityMonitorContextPolicy.Scope(domain,"mistake","world-change","uncertainty");
                }
                frame["evidenceScopes"]=scopes;
                if(item[0].StartsWith("current-"))BuildCurrentSessionFrame(frame,item[0]);
                var sourceFrame=(JObject)frame.DeepClone();var attemptState=new ActivityMonitorAttempts();var attempts=new JArray();
                bool parsed=false;string raw=null,invalid=null,plan=null;JObject verdict=null;
                for(int attempt=0;attempt<3&&!parsed;attempt++) {
                    frame=(JObject)sourceFrame.DeepClone();
                    if((int?)frame["evidenceVersion"]>=2)frame=ActivityEvidencePolicy.AttentionFrames(frame).Single();
                    var feedback=attemptState.Feedback("fixture");if(feedback!=null)frame["previousAttempt"]=feedback;
                    string admitted=model.PrepareActivityMonitorFrame(frame.ToString(),out plan);
                    Check(admitted!=null,"Recorded complete request fits final unchanged budget: "+item[0]+" "+plan);
                    frame=ActivityEvidencePolicy.Parse(admitted);
                    bool done=false,ok=false;string status=null;
                    model.PostActivityMonitor(frame.ToString(Newtonsoft.Json.Formatting.None),(success,text,why)=>{ok=success;raw=text;status=why;done=true;});
                    yield return Until(()=>done,"Native process monitor response",10f);
                    invalid=status;
                    parsed=ok&&ActivityMonitorProtocol.TryRead(raw,frame,out verdict,out invalid);
                    attempts.Add(new JObject{["attempt"]=attempt+1,["accepted"]=parsed,["rejection"]=invalid,["raw"]=raw});
                    if(!parsed) {
                        bool needsSource=ActivityMonitorContextPolicy.NeedsNewSource(invalid,raw,frame);
                        attemptState.Failed("fixture",Time.realtimeSinceStartup,invalid,raw,needsSource);
                        if(needsSource)break;
                    }
                }
                string issue=(string)verdict?.SelectToken("speech.issue"),op=(string)verdict?.SelectToken("action.operation");
                bool passed=parsed&&(issue==item[3]||item[0]=="history-error"&&issue=="mistake")&&
                    (op==item[4]||item[4]=="stop"&&op=="reconsider");
                bool semanticPassed=passed;
                bool protectedAbstention=item[0].StartsWith("current-rgb-")&&!parsed&&ActivityMonitorContextPolicy.NeedsNewSource(invalid,raw,frame);
                if(protectedAbstention)passed=true; // Expected runtime behavior: preserve speech, record unchecked, await evidence.
                string correction=((string)verdict?.SelectToken("speech.correction")??"").ToLowerInvariant();
                if(item[0]=="history-error")passed&=new[]{"don't know","do not know","not sure","cannot tell","can't tell","don't actually know","cannot confirm","can't confirm","uncertain","unsure","unknown"}.Any(correction.Contains);
                if(item[0]=="latest-unexecuted")passed&=!new[]{"操作が失敗","操作は失敗","確認して","戻ります"}.Any(correction.Contains);
                if(item[0].StartsWith("current-delivery-")) {
                    string expected=item[0]=="current-delivery-covered"?"covered":"supplement";
                    passed&=(string)verdict?.SelectToken("delivery.operation")==expected;
                    if(item[0]=="current-delivery-arrival") {
                        string text=(string)verdict?.SelectToken("delivery.text")??"";
                        passed&=!text.Contains("戻ります")&&!text.Contains("点灯")&&text.Length>0;
                    }
                    if(item[0]=="current-delivery-document")passed&=((string)verdict?.SelectToken("delivery.text")??"").Any(c=>c>='a'&&c<='z') &&
                        !((string)verdict?.SelectToken("delivery.text")??"").Any(c=>c>='\u3040'&&c<='\u30ff');
                }
                if(issue!="none"&&!string.IsNullOrEmpty(item[1]))passed&=!correction.Contains(item[1].ToLowerInvariant());
                all&=passed;
                rows.Add(new JObject{["case"]=item[0],["input"]=frame,["output"]=raw,["appliedDecision"]=verdict,
                    ["status"]=invalid,["attempts"]=attempts,["budgetPlan"]=JObject.Parse(plan),
                    ["modelSemanticPassed"]=semanticPassed,["protectedAbstention"]=protectedAbstention,["passed"]=passed});
                File.WriteAllText(Path.Combine(output,"activity-monitor-native.json"),new JObject{["passed"]=all,["cases"]=rows}.ToString());
            }
            Check(all,"Native monitor must distinguish reasonable freedom, errors and changed facts.");
        }finally{model.CancelActivityMonitor();UnityEngine.Object.Destroy(host);}
    }
}

public sealed class ActivityMonitorProbeModel:LLM
{
    public int FormalRequests;
    public bool FailFirstReply;
    public bool CheckLanguage;
    public Action<bool,string,string> LanguagePending;
    public override bool SupportsSpeechLanguageCheck=>CheckLanguage;
    public override void PostSpeechLanguage(string text,Action<bool,string,string> callback){LanguagePending=callback;}
    public override bool SupportsFirstReplyRouting=>FailFirstReply;
    public override void PostFirstReplyStream(string message,Action<SpeechText> onSpeech,Action<FirstReplyCompletion> onComplete,
        string imageDataUrl=null,string transientContext=null,bool commitHistory=true)
    {if(commitHistory)m_DataList.Add(new SendData("user",message));onComplete(FirstReplyCompletion.Failed("first-reply-review-input-budget"));}
    public override void PostSpeechStream(string message,Action<SpeechText> onSpeech,Action<string> onComplete,
        string imageDataUrl=null,bool recordAssistantHistory=true) { FormalRequests++; }
    public override bool SupportsActivityMonitor=>true;
    public override bool SupportsUtilityMessages=>true;
    public override void PostUtilityMessage(string system,string input,Action<bool,string,string> callback)
    {callback(true,input,"identity-translation-fixture");}
    public JObject Frame;
    public readonly List<JObject> CapturedFrames=new List<JObject>();
    public Action<bool,string,string> Pending;
    public bool ThrowOnPost;
    public override void PostActivityMonitor(string input,Action<bool,string,string> callback)
    {
        if(ThrowOnPost)throw new InvalidOperationException("fixture");
        Frame=JObject.Parse(input);CapturedFrames.Add((JObject)Frame.DeepClone());
        // These playback probes speak about the save-outcome adapter. Other
        // retained operations must receive their own no-op frame, not donate
        // their evidence to the synthetic save correction.
        if(Frame["delivery"]==null && (Frame["speech"] as JArray)?.Count>0 &&
            Frame["reviewOperation"] is JObject selected && (string)selected["evidenceId"]!="action:save-outcome") {
            callback(true,new JObject{["comparison"]="This sentence does not describe the selected operation.",
                ["claimQuote"]="",["claimTime"]="not-factual",["grounding"]=new JObject{
                    ["speechAct"]="personal-expression",["claimDomain"]="other",["truthWhenSaid"]="not-factual",
                    ["truthEvidenceIds"]=new JArray(),["qualificationQuote"]=""},["speech"]=new JObject{["issue"]="none"}}.ToString(),"fixture-other-operation");
            return;
        }
        Pending=callback;
    }
    public override void CancelActivityMonitor(){Pending=null;}
    public void ResetProbe(){Frame=null;Pending=null;LanguagePending=null;}
    public void Deliver(JObject result){var callback=Pending;Pending=null;callback?.Invoke(true,result.ToString(),"fixture");}
    public void Fail(){var callback=Pending;Pending=null;callback?.Invoke(false,"","fixture-timeout");}
}

public sealed class ActivityMonitorProbeTTS:TTS
{
    public float Seconds=.4f,PrepareDelay;
    public bool Streaming=true;
    public readonly List<string> Played=new List<string>();
    public readonly List<SpeechText> Requests=new List<SpeechText>();
    private AudioSource active;
    private int revision;
    public override bool SupportsStreamingPlayback=>Streaming;
    public override void Speak(SpeechText speech,Action<AudioClip,string> completed)
    {Requests.Add(speech);completed(AudioClip.Create("silent-buffered-monitor-test",(int)(24000*Seconds),1,24000,false),speech.Text);}
    public override void SpeakStreamingWithPlaybackGate(SpeechText speech,AudioSource output,Action<string> started,
        Action<bool,string,float> completed,Func<StreamingPlaybackPermission> gate)
    {Requests.Add(speech);base.SpeakStreamingWithPlaybackGate(speech,output,started,completed,gate);}
    public override void SpeakStreamingWithPlaybackGate(string text,AudioSource output,Action<string> started,
        Action<bool,string,float> completed,Func<StreamingPlaybackPermission> gate)
    {StartCoroutine(Play(text,output,started,completed,gate,++revision));}
    private IEnumerator Play(string text,AudioSource output,Action<string> started,Action<bool,string,float> completed,
        Func<StreamingPlaybackPermission> gate,int owner)
    {
        float at=Time.realtimeSinceStartup;
        while(Time.realtimeSinceStartup-at<PrepareDelay){if(owner!=revision)yield break;yield return null;}
        while(gate()!=StreamingPlaybackPermission.Play){if(owner!=revision)yield break;if(gate()==StreamingPlaybackPermission.Cancel){completed(false,text,0);yield break;}yield return null;}
        if(owner!=revision)yield break;
        var clip=AudioClip.Create("silent-monitor-test",(int)(24000*Seconds),1,24000,false);
        active=output;output.clip=clip;output.Play();Played.Add(text);started(text);
        yield return null;
        while(output.clip==clip&&output.isPlaying&&owner==revision)yield return null;
        bool success=owner==revision&&output.clip==clip;
        completed(success,text,Seconds);
    }
    public override void CancelStreaming(){revision++;StopAllCoroutines();if(active!=null)active.Stop();}
}
