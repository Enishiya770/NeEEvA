using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

// The fixture retains the full accepted dialogue, uncertain recognition and real
// audio ordering. Native requests are purpose projections of those real frames.
public static class ClarificationDeliveryRegression
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static JObject fixture;static string output;static readonly JArray cases=new JArray();
    static object Call(object o,string m,params object[] a)=>o.GetType().GetMethod(m,F).Invoke(o,a);
    static object Get(object o,string f)=>o.GetType().GetField(f,F).GetValue(o);
    static void Set(object o,string f,object v)=>o.GetType().GetField(f,F).SetValue(o,v);
    static IEnumerable<JObject> Events(string tag)=>((JArray)fixture["events"]).OfType<JObject>().Where(e=>(string)e["tag"]==tag);
    static JObject At(int line)=>(JObject)((JArray)fixture["events"]).OfType<JObject>().Single(e=>(int)e["line"]==line)["data"].DeepClone();
    static void Save(bool done=false)=>File.WriteAllText(Path.Combine(output,"clarification-delivery.json"),new JObject{
        ["completed"]=done,["passed"]=done&&cases.All(c=>(bool)c["passed"]),["source"]=fixture["source"],
        ["history"]=fixture["history"],["limitations"]=fixture["limitations"],["cases"]=cases}.ToString());
    static void Check(string name,bool ok,JToken data=null){cases.Add(new JObject{["name"]=name,["passed"]=ok,["evidence"]=data?.DeepClone()});Save();}
    static IEnumerator Until(Func<bool> done,float seconds=20){float t=Time.realtimeSinceStartup+seconds;while(!done()&&Time.realtimeSinceStartup<t)yield return null;}
    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string directory)
    {
        output=directory;cases.Clear();fixture=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/ClarificationDelivery.20261017-1035.json"));
        var host=new GameObject("Clarification delivery replay");host.SetActive(false);
        var model=host.AddComponent<ChatQW>();model.enabled=false;model.m_PrewarmPrefixOnStart=false;
        model.m_Backend=ChatQW.BackendType.Local;model.m_LocalUrl="http://127.0.0.1:8080/v1/chat/completions";Set(model,"url",model.m_LocalUrl);host.SetActive(true);
        Set(chat,"m_AgentRunning",false);Set(chat,"m_EnableActivityMonitor",true);Set(chat,"m_ActivityNextPoll",Time.realtimeSinceStartup+600);
        try {
            var history=(JArray)fixture["history"];
            Check("complete-actual-dialogue",history.Count==25&&history.Count(r=>(string)r["role"]=="user")==5&&history.Count(r=>(string)r["role"]=="assistant")==19);
            Check("uncertain-audio-not-authorized-user",history.Count(r=>(string)r["role"]=="unresolved-audio")==1);
            Budgets(model);HistoricalState(chat);OriginalDelivery(chat);
            yield return Recognition(model);
            foreach(int line in new[]{21602,23500})yield return Monitor(model,At(line),"recorded-delivery-"+line,true);
            foreach(int line in new[]{22447,22827,23119}) {
                var original=At(line);
                foreach(var frame in ActivityEvidencePolicy.SpeechOperations(original))yield return Monitor(model,frame,"current-clause-"+line,false);
            }
            foreach(var row in new[]{new[]{"save","文件已保存。","zh"},new[]{"generate","画像は生成できたわ。","ja"},new[]{"inspect","I inspected the document.","en"}}) {
                var frame=At(23500);var d=(JObject)frame["delivery"];
                d["facts"]=new JObject{["operation"]=row[0],["entityId"]="artifact-a",["status"]="completed"};
                d["taskContext"]=new JObject{["request"]=row[0]+" artifact-a",["currentOperation"]=row[0]};d["language"]=row[2];
                d["completedPlaybackText"]=row[1];d["playbackRevision"]=ActivityEvidencePolicy.Stamp(new JValue(row[1]));
                d["completedAudio"]=new JArray(new JObject{["audioId"]="general-"+row[0],["speechVersion"]=1,["textStart"]=0,["textLength"]=row[1].Length,["wholeTextPlayed"]=true});
                d["rejectedCandidates"]=new JArray();d["supplementPlayback"]=new JArray();d["latestCorrectionStart"]=0;
                yield return Monitor(model,frame,"general-delivery-"+row[2],true);
            }
            yield return WriterAndWork(chat,model);
            yield return ClarificationPlayback(chat,host);
            Save(true);if(cases.Any(c=>!(bool)c["passed"]))throw new InvalidOperationException("Clarification delivery replay failed; inspect clarification-delivery.json.");
        }finally{model.CancelActiveResponse();model.CancelActivityMonitor();UnityEngine.Object.Destroy(host);}
    }
    static void Budgets(ChatQW model)
    {
        foreach(var e in Events("Activity/MonitorFrame")) {
            var original=(JObject)e["data"].DeepClone();int index=0;
            foreach(var frame in (string)original["purpose"]=="speech"?ActivityEvidencePolicy.SpeechOperations(original):new[]{original}) {
                string input=model.PrepareActivityMonitorFrame(ActivityEvidencePolicy.Focus(frame).ToString(Formatting.None),out var diagnostic);
                Check("full-wire-budget-"+e["line"]+"-"+index++,input!=null,JObject.Parse(diagnostic));
            }
            Check("resident-frame-preserved-"+e["line"],JToken.DeepEquals(original,e["data"]));
        }
    }
    static IEnumerator Monitor(ChatQW model,JObject original,string name,bool covered)
    {
        var attempts=new JArray();bool valid=false;JObject verdict=null;string reason=null;
        for(int retry=0;retry<3&&!valid;retry++) {
            var focused=ActivityEvidencePolicy.Focus(original);
            string input=model.PrepareActivityMonitorFrame(focused.ToString(Formatting.None),out var diagnostic);
            if(input==null){attempts.Add(new JObject{["budget"]=JObject.Parse(diagnostic)});break;}
            bool done=false,ok=false;string raw=null,status=null;
            model.PostActivityMonitor(input,(a,b,c)=>{done=true;ok=a;raw=b;status=c;});yield return Until(()=>done);
            valid=ok&&ActivityMonitorProtocol.TryRead(raw,JObject.Parse(input),out verdict,out reason);
            if(valid&&covered)valid=(string)verdict.SelectToken("delivery.operation")=="covered";
            if(valid&&!covered)valid=(string)verdict.SelectToken("speech.issue")=="none";
            attempts.Add(new JObject{["input"]=JObject.Parse(input),["budget"]=JObject.Parse(diagnostic),["raw"]=raw,["status"]=status,["reason"]=reason,["accepted"]=valid});
            if(!valid)original["previousAttempt"]=ActivityDeliveryProtocol.RepairFeedback(new JObject{["rejection"]=reason??"semantic-coverage-mismatch",["rawVerdict"]=raw});
        }
        Check("native-"+name,valid,attempts);
    }
    static IEnumerator Recognition(ChatQW model)
    {
        var packet=At(7104);
        packet["full"]["source"]="segmented-primary-via-full-preview";
        packet["provenance"]=fixture["recognitionProvenance"].DeepClone();
        ((JObject)packet["provenance"]).Remove("originalLog");
        bool got=false;yield return model.ReviewTranscriptComparison(packet,()=>true,v=>got=v);
        Check("native-original-uncertain-tail-equivalent",got,packet);
        foreach(var row in new[]{
            new[]{"保存文件，然后检查。","保存文件，然后检查。嗯。","equivalent"},
            new[]{"ファイルを保存して。","ファイルを保存して。ええ。","equivalent"},
            new[]{"Inspect the document.","Inspect the document. Yeah.","equivalent"},
            new[]{"保存文件。","保存文件，不，别保存。","different"},
            new[]{"Save then upload.","Upload then save.","different"},
            new[]{"歌って。","歌わないで。","different"}}) {
            var p=At(7104);p["stream"]["text"]=row[0];p["full"]["text"]=row[1];bool equivalent=false;
            yield return model.ReviewTranscriptComparison(p,()=>true,v=>equivalent=v);
            Check("native-asr-"+row[0],equivalent==(row[2]=="equivalent"),p);
        }
    }
    static void HistoricalState(ChatSample chat)
    {
        var old=(JObject)Get(chat,"m_PreviousUnfinishedWork");
        var snapshot=new JObject{["request"]="Continue observing",["taskSequence"]=new JObject{["steps"]=new JArray(new JObject{["id"]="observe",["actionId"]="old-observation",["status"]="running"})}};
        Set(chat,"m_PreviousUnfinishedWork",snapshot);
        Call(chat,"RecordHistoricalWorkTermination","other",new JObject{["phase"]="stopped"},"replacement");
        Check("other-owner-cannot-terminate-history",WorkNextIntentProtocol.HistoricalStatus(snapshot)=="unfinished");
        Call(chat,"RecordHistoricalWorkTermination","old-observation",new JObject{["phase"]="failed"},"replacement");
        Check("failed-stop-is-not-termination",snapshot["termination"]==null);
        Call(chat,"RecordHistoricalWorkTermination","old-observation",new JObject{["phase"]="stopped"},"replacement");
        Check("continuous-observation-terminated-not-completed",WorkNextIntentProtocol.HistoricalStatus(snapshot)=="terminated"&&(string)snapshot.SelectToken("taskSequence.steps[0].status")=="running",snapshot);
        var type=typeof(ChatSample).GetNestedType("AutonomyIntentDecision",F);
        var decision=JsonConvert.DeserializeObject("{\"previous_work_status\":\"completed\",\"proceed\":false,\"work_status\":\"closed\"}",type);
        Check("wrong-history-field-does-not-discard-current-delivery",(bool)Call(chat,"ValidateHistoricalWorkAssessment",decision)&&
            (string)Get(decision,"previous_work_status")=="terminated"&&!(bool)Get(decision,"proceed"));
        Set(chat,"m_PreviousUnfinishedWork",old);
        Set(chat,"m_PrivateDecisionFailure",new PrivateDecisionResult("","context-budget",false));
        Set(chat,"m_PrivateDecisionFailureEpoch",(int)Get(chat,"m_WorkEpoch"));
        var constraints=Get(chat,"m_ActionConstraints");
        Set(chat,"m_PrivateDecisionFailureTurn",(int)constraints.GetType().GetProperty("CurrentUserTurn",F).GetValue(constraints));
        Set(chat,"m_WorkReviewOpen",true);
        Check("unreviewed-current-work-is-still-held",(bool)Call(chat,"PrivateDecisionBlocksCurrentWork"));
        Set(chat,"m_WorkReviewOpen",false);
        Check("settled-work-does-not-disable-independent-autonomy",!(bool)Call(chat,"PrivateDecisionBlocksCurrentWork"));
        Set(chat,"m_PrivateDecisionFailure",null);
    }
    static void OriginalDelivery(ChatSample chat)
    {
        var lamp=(JObject)At(21602)["delivery"];var arrival=(JObject)At(23500)["delivery"];
        var results=new JArray(lamp,arrival);
        var verdict=new JObject{["delivery_reports"]=At(23816)["rawReports"].DeepClone()};
        Check("later-review-cannot-use-earlier-playback-version",!WorkResultDeliveryProtocol.ValidateCoverage(results,verdict,out var why)&&why=="playback-revision-not-audited",new JObject{["reason"]=why,["reports"]=verdict});
        foreach(JObject report in (JArray)verdict["delivery_reports"])report["playedRevision"]=results.OfType<JObject>().Single(r=>(string)r["id"]==(string)report["id"])["playbackRevision"].DeepClone();
        Check("recorded-quotes-cover-matching-snapshot",WorkResultDeliveryProtocol.ValidateCoverage(results,verdict,out why),new JObject{["reason"]=why,["scope"]="Same actual quotes revalidated against the recorded earlier snapshot, not an invented archived work-review wire."});
        foreach(var e in Events("Activity/MonitorUnchecked").Where(e=>(int)e["line"]==21668||(int)e["line"]==23520))
            Check("original-rejection-preserved-"+e["line"],e["data"]!=null,e["data"]);
        // The production player records completion by result, not queued output.
        Call(chat,"EnsureWorkDeliveryOwner");var ledger=(Dictionary<string,JObject>)Get(chat,"m_WorkToolResultReceipts");ledger.Clear();
        foreach(string field in new[]{"m_WorkResultPlaybackText","m_WorkDeliveredResults","m_WorkDeliveredPlayback"})Call(Get(chat,field),"Clear");
        foreach(JObject result in results) {
            ledger[(string)result["id"]]=(JObject)result.DeepClone();
            Call(chat,"RecordWorkCompletedSpeechForResult",(string)result["completedPlaybackText"],(string)result["id"]);
        }
        var actual=(JArray)((JObject)Call(chat,"ReadWorkDelivery"))["results"];
        foreach(JObject report in (JArray)verdict["delivery_reports"]) {
            var result=actual.OfType<JObject>().Single(r=>(string)r["id"]==(string)report["id"]);report["playedRevision"]=result["playbackRevision"].DeepClone();
        }
        var decision=verdict.ToObject(typeof(ChatSample).GetNestedType("AutonomyIntentDecision",F));
        Call(chat,"ApplyWorkDelivery",decision,null);
        Check("production-both-real-reports-settle",((JArray)((JObject)Call(chat,"ReadWorkDelivery"))["results"]).All(r=>(bool?)r["delivered"]==true));
        ledger.Clear();
    }
    static IEnumerator WriterAndWork(ChatSample chat,ChatQW model)
    {
        var settings=(ChatSetting)Get(chat,"m_ChatSettings");var savedModel=settings.m_ChatModel;settings.m_ChatModel=model;
        Call(Get(chat,"m_ActionConstraints"),"RecordUserTurn","去把刚才你关掉的灯重新打开，然后回到我身边。");Call(chat,"EnsureWorkDeliveryOwner");
        var ledger=(Dictionary<string,JObject>)Get(chat,"m_WorkToolResultReceipts");ledger.Clear();
        foreach(string f in new[]{"m_WorkResultPlaybackText","m_WorkDeliveredResults","m_WorkDeliveredPlayback"})Call(Get(chat,f),"Clear");
        var arrival=(JObject)At(23500)["delivery"].DeepClone();
        foreach(string f in new[]{"completedPlaybackText","playbackRevision","completedAudio","rejectedCandidates","latestCorrectionStart"})arrival.Remove(f);
        arrival["hasCompletedPlaybackAfterResult"]=false;arrival["delivered"]=false;
        arrival["facts"]["receipt"]=arrival["facts"]["outcome"].DeepClone();
        ledger[(string)arrival["id"]]=arrival;
        var lamp=(JObject)At(21602)["delivery"].DeepClone();lamp["facts"]["receipt"]=new JObject{["goal"]=lamp["facts"]["outcome"].DeepClone()};
        ledger[(string)lamp["id"]]=lamp;
        Call(chat,"RecordWorkCompletedSpeechForResult","灯は正常に点灯しています。",(string)lamp["id"]);
        ((Dictionary<string,string>)Get(chat,"m_WorkDeliveredResults"))[(string)lamp["id"]]="灯は正常に点灯しています。";
        ((Dictionary<string,string>)Get(chat,"m_WorkDeliveredPlayback"))[(string)lamp["id"]]=ActivityEvidencePolicy.Stamp(new JValue("灯は正常に点灯しています。"));
        var facts=(JObject)Call(chat,"BuildWorkResultReplyFacts",(int)Get(chat,"m_FormalResponseGeneration"));
        var task=At(17758);
        foreach(JObject step in (JArray)task["steps"]) {var r=(string)step["id"]=="s1"?lamp:arrival;step["status"]="completed";step["receipt"]=r["facts"]["receipt"].DeepClone();step["actionId"]=r["facts"]["actionId"].DeepClone();}
        facts["taskSequence"]=task;
        facts["eventContext"]=At(22447)["facts"]["execution"].DeepClone();
        var chronology=new JArray(((JArray)fixture["history"]).Where(r=>(int)r["line"]<21728).Select(r=>r.DeepClone()));
        model.m_DataList.Clear();foreach(JObject row in chronology)if((string)row["role"]!="unresolved-audio")model.m_DataList.Add(new LLM.SendData((string)row["role"],(string)row["text"]));
        string context=CommittedSpatialReplyProtocol.AppendReplyFacts("[Current work result]\n",facts)+"\nDialogue language: Japanese. Report the owed result naturally.";
        var projectionArgs=new object[]{context,0,null};
        string projected=(string)typeof(ChatQW).GetMethod("ProjectReplyFacts",F).Invoke(null,projectionArgs);
        var projection=(JObject)projectionArgs[2];
        Check("result-writer-exact-sharing-reduces-actual-facts",(projection["resultOnlySharing"] as JArray)?.OfType<JObject>().Any(r=>(bool?)r["applied"]==true&&(bool?)r["roundTripVerified"]==true)==true&&projected.Length<context.Length,projection);
        var parts=new SpeechTextBuffer();bool done=false;string raw=null;
        model.PostSpeechFeedbackStreamForStage(context,null,p=>parts.Append(p),v=>{raw=v;done=true;},null,false,FormalResponseStage.CommittedSpatialReply);
        yield return Until(()=>done,25);
        string text=string.Concat(parts.Snapshot().Select(p=>p.Text));
        Check("native-report-only-writer-produced-public-output",done&&!string.IsNullOrWhiteSpace(text),new JObject{["facts"]=facts,["raw"]=raw,["speech"]=text,["history"]=chronology});
        var frame=At(23500);var d=(JObject)frame["delivery"];d["completedPlaybackText"]=text;d["playbackRevision"]=ActivityEvidencePolicy.Stamp(new JValue(text));
        d["completedAudio"]=new JArray();d["rejectedCandidates"]=new JArray();d["latestCorrectionStart"]=0;
        // This checks the writer's meaning only. Actual audio is tested below;
        // this synthetic review is never applied to the live delivery ledger.
        yield return Monitor(model,frame,"writer-delivers-current-outcome",true);
        var results=new JArray(At(21602)["delivery"].DeepClone(),At(23500)["delivery"].DeepClone());
        var history=new JObject{["request"]="跟着我走。",["termination"]=new JObject{["committed"]=true,["reason"]="replaced-by-current-task"},
            ["taskSequence"]=new JObject{["steps"]=new JArray(new JObject{["kind"]="follow",["status"]="running"})}};
        string prompt="[Runtime work review domain: general]\n[Complete recorded dialogue and actual playback]\n"+fixture["history"].ToString(Formatting.None)+
            "\n[Task progress runtime]\n"+new JObject{["currentUserText"]="去把刚才你关掉的灯重新打开，然后回到我身边。",["previousUnfinishedWork"]=history,["delivery"]=new JObject{["results"]=results}}.ToString(Formatting.None)+
            "\n[Review instruction]\n"+WorkNextIntentProtocol.Contract;
        raw=null;done=false;model.PostWorkReviewMsg(prompt,v=>{raw=v;done=true;});yield return Until(()=>done,25);
        JObject verdict=null;try{verdict=JObject.Parse(raw??"");}catch(Exception){}
        string reason=null;Check("native-full-history-current-results-and-terminated-prior-task",verdict!=null&&
            WorkResultDeliveryProtocol.ValidateCoverage(results,verdict,out reason)&&(string)verdict["previous_work_status"]=="terminated"&&
            ((JArray)verdict["delivery_reports"]).All(r=>(string)r["coverage"]=="reported"),new JObject{["raw"]=raw,["reason"]=reason,["prompt"]=prompt});
        ledger.Clear();settings.m_ChatModel=savedModel;
    }
    static IEnumerator ClarificationPlayback(ChatSample chat,GameObject host)
    {
        var model=host.AddComponent<ActivityMonitorProbeModel>();var tts=host.AddComponent<ActivityMonitorProbeTTS>();
        var settings=(ChatSetting)Get(chat,"m_ChatSettings");settings.m_ChatModel=model;settings.m_TextToSpeech=tts;
        var audio=(AudioSource)Get(chat,"m_AudioSource");audio.enabled=true;audio.playOnAwake=false;audio.loop=false;
        Set(chat,"m_EnableLatencyFiller",false);Set(chat,"m_EnableAutonomyIntentProbe",false);Set(chat,"m_IsVoiceMode",true);
        foreach(bool streaming in new[]{true,false})foreach(string lang in new[]{"zh","ja","en"}) {
            chat.StopAllCoroutines();tts.CancelStreaming();audio.Stop();audio.clip=null;model.ResetProbe();tts.Played.Clear();tts.Requests.Clear();
            Call(chat,"BeginFormalResponseGeneration");
            foreach(string f in new[]{"m_SceneTimePaused","m_UserSpeechActiveForAutonomy","m_StreamComplete","m_TTSSenderDone","m_RoundIsInner","m_RoundSilent","m_FirstChunkFlushed","m_RoomTaskFormatError","m_HoldSpeechForSongMemoryResult","m_HoldSpeechForHumBackResult","m_LatencyFillerPlayed","m_WorkReviewOpen","m_RoundContinue","m_RoundSilencedForRepeat","m_RoundRepeatCheckDone","m_RoundRepeatHold","m_RoundInnerCheckDone"})Set(chat,f,false);
            Set(chat,"<IsAISpeaking>k__BackingField",false);Set(chat,"m_CurrentlyPlayingText","");
            Set(chat,"m_ChatHistory",new List<string>());model.m_DataList.Clear();
            Set(chat,"m_SentenceBuffer",new SpeechTextBuffer());Set(chat,"m_AssistantHeardText",new StringBuilder());
            foreach(string f in new[]{"m_PendingChunks","m_PendingClips","m_ActivityDialogue"})Call(Get(chat,f),"Clear");
            Set(chat,"m_CurrentSpeechRoundId",(int)Get(chat,"m_CurrentSpeechRoundId")+1);
            Set(chat,"m_RoomTaskIntent","none");Set(chat,"m_RoomTaskActionId","");Set(chat,"m_RoomTaskDispatch","not-submitted");
            Set(chat,"m_ActivityNextPoll",Time.realtimeSinceStartup+600);tts.Streaming=streaming;tts.Seconds=.2f;
            string input="Unresolved capture "+lang+streaming;string text=TranscriptDisagreementPolicy.Clarification(lang);
            Call(Get(chat,"m_ActionConstraints"),"RecordUserTurn",input);
            Call(chat,"MarkUserTurnAwaitingReply");
            Set(chat,"m_TranscriptClarificationInput",input);Set(chat,"m_TranscriptClarificationOutput","<lang code=\""+lang+"\"/><speech mode=\"independent\"/><say>"+text+"</say>");
            int gen=(int)Get(chat,"m_FormalResponseGeneration");
            if(streaming)chat.StartCoroutine((IEnumerator)Call(chat,"StreamDirectTTSPlayer",gen));
            else {chat.StartCoroutine((IEnumerator)Call(chat,"StreamTTSSender",gen));chat.StartCoroutine((IEnumerator)Call(chat,"StreamAudioPlayer",gen));}
            bool published=(bool)Call(chat,"TryPublishTranscriptClarification",input,gen);
            Check("queued-clarification-is-not-delivery-"+lang+streaming,(float)Get(chat,"m_UserTurnAwaitingReplySince")>0);
            bool audioStarted=false;float until=Time.realtimeSinceStartup+8;
            while((int)Get(chat,"m_TextOutputDrainedGeneration")!=gen&&Time.realtimeSinceStartup<until){audioStarted|=audio.isPlaying;yield return null;}
            string completed=string.Concat(((List<JObject>)Get(chat,"m_ActivityDialogue")).Select(r=>(string)r["text"]));
            string Compact(string s)=>new string(s.Where(c=>!char.IsWhiteSpace(c)).ToArray());
            var receipt=(JObject)Get(chat,"m_LastFormalReplyReceipt");
            Check("actual-clarification-playback-"+lang+"-"+streaming,published&&audioStarted&&Compact(completed)==Compact(text)&&
                tts.Requests.All(s=>s.LanguageCode==lang)&&!chat.IsAISpeaking&&(float)Get(chat,"m_UserTurnAwaitingReplySince")<0&&(string)Get(chat,"m_RoomTaskDispatch")=="not-submitted",
                new JObject{["played"]=completed,["audioStarted"]=audioStarted,["requests"]=JArray.FromObject(tts.Requests),["receipt"]=receipt?.DeepClone(),["scope"]="Real production player with silent synthetic AudioClip; not human audibility or TTS pronunciation verification."});
            if(lang=="ja")foreach(bool during in new[]{false,true}) {
                Call(chat,"BeginFormalResponseGeneration");gen=(int)Get(chat,"m_FormalResponseGeneration");
                Set(chat,"m_StreamComplete",false);Set(chat,"m_TTSSenderDone",false);Set(chat,"m_FirstChunkFlushed",false);
                Set(chat,"m_SentenceBuffer",new SpeechTextBuffer());Set(chat,"m_AssistantHeardText",new StringBuilder());
                Call(chat,"MarkUserTurnAwaitingReply");tts.Seconds=.8f;tts.PrepareDelay=during?0:1;
                input+="-cancel";Set(chat,"m_TranscriptClarificationInput",input);
                Set(chat,"m_TranscriptClarificationOutput","<lang code=\"ja\"/><speech mode=\"independent\"/><say>"+text+"</say>");
                if(streaming)chat.StartCoroutine((IEnumerator)Call(chat,"StreamDirectTTSPlayer",gen));
                else {chat.StartCoroutine((IEnumerator)Call(chat,"StreamTTSSender",gen));chat.StartCoroutine((IEnumerator)Call(chat,"StreamAudioPlayer",gen));}
                Call(chat,"TryPublishTranscriptClarification",input,gen);
                if(during)yield return Until(()=>audio.isPlaying,5);
                Call(chat,"CancelOwnedSpeechOutput",gen,"user-interrupt-probe");
                Check("cancel-clarification-"+streaming+"-"+during,!audio.isPlaying&&!chat.IsAISpeaking&&(float)Get(chat,"m_UserTurnAwaitingReplySince")>0);
                Call(chat,"BeginFormalResponseGeneration");Set(chat,"<IsAISpeaking>k__BackingField",true);
                Call(chat,"ReleaseSpeechPresentation",gen,"old-callback-probe");
                Check("old-owner-cannot-release-new-speech-"+streaming+"-"+during,chat.IsAISpeaking);
                Set(chat,"<IsAISpeaking>k__BackingField",false);tts.PrepareDelay=0;chat.StopAllCoroutines();tts.CancelStreaming();
            }
        }
        Call(chat,"BeginFormalResponseGeneration");Set(chat,"m_AssistantHeardText",new StringBuilder());Call(chat,"MarkUserTurnAwaitingReply");
        Call(chat,"FinishSpeakingNaturally");Check("empty-audio-drain-does-not-settle-reply",(float)Get(chat,"m_UserTurnAwaitingReplySince")>0);
        Set(chat,"m_IsVoiceMode",false);Set(chat,"m_WordWaitTime",.001f);
        string plain=TranscriptDisagreementPolicy.Clarification("zh");
        Call(chat,"CallBack","<lang code=\"zh\"/><speech mode=\"independent\"/><say>"+plain+"</say>");
        Check("typing-is-not-yet-delivered",(float)Get(chat,"m_UserTurnAwaitingReplySince")>0);
        yield return Until(()=>(float)Get(chat,"m_UserTurnAwaitingReplySince")<0,8);
        Check("explicit-text-delivery-settles-after-display",(float)Get(chat,"m_UserTurnAwaitingReplySince")<0);
    }
}
