using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class ResultAutonomyRegression
{
    const BindingFlags F=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static JObject fixture;static string output;static readonly JArray cases=new JArray();
    static object Call(object o,string m,params object[] a)=>o.GetType().GetMethod(m,F).Invoke(o,a);
    static object Get(object o,string f)=>o.GetType().GetField(f,F).GetValue(o);
    static void Set(object o,string f,object v)=>o.GetType().GetField(f,F).SetValue(o,v);
    static IEnumerable<JObject> Events(string tag)=>((JArray)fixture["events"]).OfType<JObject>().Where(e=>(string)e["tag"]==tag);
    static JObject At(int line)=>(JObject)((JArray)fixture["events"]).OfType<JObject>().Single(e=>(int)e["line"]==line)["data"].DeepClone();
    static void Save(bool done=false)=>File.WriteAllText(Path.Combine(output,"result-autonomy.json"),new JObject{
        ["completed"]=done,["passed"]=done&&cases.All(c=>(bool)c["passed"]),["source"]=fixture["source"],
        ["history"]=fixture["history"],["limitations"]=fixture["limitations"],["cases"]=cases}.ToString());
    static void Check(string name,bool ok,JToken evidence=null){cases.Add(new JObject{["name"]=name,["passed"]=ok,["evidence"]=evidence?.DeepClone()});Save();}
    static IEnumerator Until(Func<bool> done,float seconds=30){float t=Time.realtimeSinceStartup+seconds;while(!done()&&Time.realtimeSinceStartup<t)yield return null;}
    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string directory)
    {
        output=directory;cases.Clear();fixture=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/ResultAutonomy.20261017-1137.json"));
        var host=new GameObject("Result settlement and autonomy replay");host.SetActive(false);
        var model=host.AddComponent<ChatQW>();model.enabled=false;model.m_PrewarmPrefixOnStart=false;
        model.m_Backend=ChatQW.BackendType.Local;model.m_LocalUrl="http://127.0.0.1:8080/v1/chat/completions";Set(model,"url",model.m_LocalUrl);host.SetActive(true);
        Set(chat,"m_AgentRunning",false);Set(chat,"m_EnableActivityMonitor",false);
        try {
            var h=(JArray)fixture["history"];
            Check("complete-six-turns-and-actual-versions",h.Count==32&&h.Count(r=>(string)r["role"]=="user")==6&&h.Count(r=>(string)r["role"]=="assistant")==22&&h.Count(r=>(string)r["role"]=="assistant-opening")==4);
            Protocol();Policies();Runtime(chat,bridge,model);Budgets(model);
            foreach(int line in new[]{34796,40294,41893,50893,51194})yield return Monitor(model,At(line),"recorded-"+line,true);
            foreach(var row in new[]{new[]{"edit","编辑已经完成，文件也保存好了。","zh"},new[]{"generate","画像を生成できたわ。これから一緒に眺めたいな。","ja"},new[]{"inspect","I inspected the document. I would like to stay with you.","en"},new[]{"sing","歌い終わったわ。楽しかったね。","ja"}}) {
                var frame=General(row[0],row[1],row[2]);yield return Monitor(model,frame,"general-result-"+row[0],true);
            }
            yield return Monitor(model,General("save","Okay, I'll save it now.","en"),"acknowledgment-is-not-completion",false);
            yield return Monitor(model,General("inspect","検査できなかったわ。","ja","failed"),"failure-can-be-delivered",true);
            yield return WorkReview(model);
            Save(true);if(cases.Any(c=>!(bool)c["passed"]))throw new InvalidOperationException("Result autonomy regression failed; inspect result-autonomy.json.");
        }finally{model.CancelActiveResponse();model.CancelActivityMonitor();UnityEngine.Object.Destroy(host);}
    }
    static void Protocol()
    {
        var result=(JObject)At(51194)["delivery"];var results=new JArray(result.DeepClone());
        foreach(int line in new[]{51164,51235}) {
            var rejected=JObject.Parse((string)At(line)["rawDecision"]);
            Check("original-two-quotes-still-rejected-"+line,!WorkResultDeliveryProtocol.ValidateCoverage(results,rejected,out var reason)&&
                new[]{"delivery-result-scope-mismatch","playback-latest-relevant-quote-required"}.Contains(reason),new JObject{["reason"]=reason,["raw"]=rejected});
            // Preserve the original rejection above, then migrate only the
            // schema's identity label to prove the quote conflict is still caught.
            var migrated=(JObject)rejected.DeepClone();migrated["delivery_reports"][0]["resultScope"]=WorkResultDeliveryProtocol.ResultScope(result);
            Check("current-scope-still-rejects-original-two-quotes-"+line,!WorkResultDeliveryProtocol.ValidateCoverage(results,migrated,out var quoteReason)&&
                quoteReason=="playback-latest-relevant-quote-required",new JObject{["reason"]=quoteReason,["rawWithCurrentScope"]=migrated});
            string fix=WorkResultDeliveryProtocol.RepairDetails(results,rejected,reason);
            Check("concrete-field-repair-"+line,fix.Contains("確認しました。")&&fix.Contains("あなたのそばに着いたわ。"));
        }
        var wire=Report(result,"あなたのそばに着いたわ。");
        Check("single-selected-quote-expands-once",WorkResultDeliveryProtocol.ValidateCoverage(results,wire,out var why)&&
            (string)wire.SelectToken("delivery_reports[0].quote")=="あなたのそばに着いたわ。",new JObject{["why"]=why,["wire"]=wire});
        var bad=(JObject)wire.DeepClone();bad["delivery_reports"][0]["quote"]="確認しました。";
        Check("explicit-conflicting-alias-never-overwritten",!WorkResultDeliveryProtocol.ValidateCoverage(results,bad,out why)&&why=="playback-selection-alias-conflict");
        bad=(JObject)wire.DeepClone();bad["delivery_reports"][0]["playedRevision"]="old-version";
        Check("old-audio-version-rejected",!WorkResultDeliveryProtocol.ValidateCoverage(results,bad,out why));
        bad=(JObject)wire.DeepClone();bad["delivery_reports"][0]["reportAct"]="acknowledgment";
        Check("legacy-contradictory-label-rejected",!WorkResultDeliveryProtocol.ValidateCoverage(results,bad,out why));
        var evidence=ResultPlaybackSelection.Evidence(result,"あなたのそばに着いたわ。");
        Check("quote-binds-actual-audio-version",((JArray)evidence["completedAudio"]).Any(a=>(string)a["audioId"]=="speech:14:23"&&(int?)a["speechVersion"]==2),evidence);
        var duplicateAudio=new JObject{["completedPlaybackText"]="Done.Done.",["completedAudio"]=new JArray(
            new JObject{["audioId"]="first",["speechVersion"]=1,["textStart"]=0,["textLength"]=5,["wholeTextPlayed"]=true},
            new JObject{["audioId"]="later",["speechVersion"]=2,["textStart"]=5,["textLength"]=5,["wholeTextPlayed"]=true})};
        var selectedEvidence=ResultPlaybackSelection.Evidence(duplicateAudio,"Done.","first@1:0");
        Check("identical-words-keep-selected-audio-owner",(int)selectedEvidence["textStart"]==0&&(string)selectedEvidence.SelectToken("completedAudio[0].audioId")=="first");
        var schema=JObject.Parse(WorkNextIntentProtocol.ExtendReviewSchema((string)typeof(ChatQW).GetField("k_WorkReviewSchema",F).GetValue(null)));WorkResultDeliveryProtocol.BindCoverageSchema(schema,results);
        var rows=schema.SelectTokens("properties.delivery_reports.items.anyOf[*]").OfType<JObject>().ToArray();
        Check("native-one-quote-interpreted-before-coverage",rows.Length==3&&rows.All(r=>r.SelectToken("properties.quote")==null)&&
            rows.Where(r=>r.SelectToken("properties.reportedStage.enum").Values<string>().Contains("ongoing")).All(r=>
                JToken.DeepEquals(r.SelectToken("properties.coverage.enum"),new JArray("unreported"))),schema["properties"]["delivery_reports"]);
        Check("native-audio-unit-preserves-ack-plus-outcome",rows.All(r=>r.SelectToken("properties.lastRelevantQuote")==null&&r.SelectToken("properties.selectedPlaybackId")!=null));
        var monitorSchema=ActivityDeliveryProtocol.Schema(result);
        Check("native-consistent-cannot-select-supplement",monitorSchema["anyOf"].OfType<JObject>().All(r=>
            r.SelectToken("properties.playbackProof.properties.finalDisposition.enum").Values<string>().Contains("consistent")==
            ((string)r.SelectToken("properties.operation.enum[0]")=="covered")));
        var prior=(JObject)At(41893)["delivery"];
        var duplicate=new JObject{["id"]=prior["id"],["operation"]="supplement",["quote"]="",["comparison"]="Needs arrival report.",
            ["reportAct"]="progress-report",["text"]="無事についているわね。",["playbackProof"]=new JObject{
                ["revision"]=prior["playbackRevision"],["lastRelevantQuote"]="今、あなたのところへ向かっているわ。",["finalDisposition"]="unreported"}};
        Check("duplicate-supplement-is-rechecked-not-auto-covered",ActivityDeliveryProtocol.Reject(duplicate,prior)=="delivery-supplement-already-played");
    }
    static JObject Report(JObject r,string quote)=>new JObject{["delivery_reports"]=new JArray(new JObject{
        ["id"]=r["id"],["resultScope"]=WorkResultDeliveryProtocol.ResultScope(r),["playbackContract"]=ResultPlaybackSelection.Version,
        ["selectedPlaybackId"]=ResultPlaybackSelection.Units(r).OfType<JObject>().Single(u=>(string)u["text"]==quote)["id"],
        ["lastRelevantQuote"]=quote,["playbackMeaning"]="This phrase reports the corresponding completed outcome.",
        ["comparison"]="Actual completed words match this result.",["coverage"]="reported",["reportAct"]="outcome-report",
        ["playedRevision"]=r["playbackRevision"],["finalDisposition"]="consistent"}),["remaining_request_quote"]=""};
    static void Policies()
    {
        Check("new-operation-on-same-target-allowed",!AutonomousAttemptPolicy.Suppress(true,"approach","follow","arrived","old",null));
        Check("repeated-failed-same-operation-still-blocked",AutonomousAttemptPolicy.Suppress(true,"follow","follow","failed","old",null));
        Check("legitimate-old-task-replacement-allows-new-intention",!AutonomousAttemptPolicy.Suppress(true,"follow","follow","follow-waiting","old","old"));
        Check("unrelated-termination-cannot-release-old-attempt",AutonomousAttemptPolicy.Suppress(true,"follow","follow","follow-waiting","old","other"));
        Check("explicit-stop-does-not-grant-retry",AutonomousAttemptPolicy.Suppress(true,"follow","follow","stopped","old",null));
        Check("failed-executor-not-released-by-termination",AutonomousAttemptPolicy.Suppress(true,"follow","follow","failed","old","old"));
        Check("different-intention-cannot-bypass-shared-unreachable-route",AutonomousAttemptPolicy.Suppress(true,"approach","follow","unreachable","old",null,true));
        Check("independent-operation-is-not-the-same-failed-prerequisite",!AutonomousAttemptPolicy.Suppress(true,"save","inspect","failed","old",null,false));
        Check("confirmed-cancellation-by-replacement-is-not-an-executor-fault",!AutonomousAttemptPolicy.Suppress(true,"follow","follow","cancelled","old","old",false));
        foreach(string operation in new[]{"inspect","save","generate","sing"}) {
            Check("new-intention-general-"+operation,!AutonomousAttemptPolicy.Suppress(true,"retrieve",operation,"completed","old",null));
            Check("bounded-failure-general-"+operation,AutonomousAttemptPolicy.Suppress(true,operation,operation,"failed","old",null));
        }
        var sem=new JObject{["requestKind"]="action",["questionUnderstanding"]=new JObject{["needsEvidence"]="memory-or-direction"}};
        var receipts=new JArray(new JObject{["actionId"]="observed-operation",["observedUtc"]="2026-10-17T02:34:00Z"});
        Check("history-action-reviewed-before-rgb",ResultDeliveryContextPolicy.PreferReceiptReview(sem,receipts,false));
        Check("unseen-object-still-needs-visual-recall",!ResultDeliveryContextPolicy.PreferReceiptReview(sem,new JArray(),false));
        Check("independent-answer-keeps-recall",!ResultDeliveryContextPolicy.PreferReceiptReview(sem,receipts,true));
        sem["requestKind"]="question";Check("ordinary-question-keeps-recall",!ResultDeliveryContextPolicy.PreferReceiptReview(sem,receipts,false));
        var source=new JObject{["intent"]="follow",["actionId"]="a",["currentRoomFacts"]=new JObject{
            ["currentFacts"]=new JObject{["actualRoot"]=new JObject{["x"]=1,["y"]=2,["z"]=3},["actualUserDistance"]=1.1,["customConstraint"]="Never cross the boundary"},
            ["execution"]=new JObject{["actionId"]="a",["phase"]="failed",["reason"]="quality-rejected"}},["customConstraint"]=new JObject{["position"]=new JObject{["x"]=8}}};
        var before=source.ToString();var projected=ResultDeliveryContextPolicy.Execution(source);
        Check("projection-retains-failure-constraints-distance",source.ToString()==before&&projected.SelectToken("currentRoomFacts.currentFacts.actualRoot")==null&&
            (string)projected.SelectToken("currentRoomFacts.execution.reason")=="quality-rejected"&&JToken.DeepEquals(source["customConstraint"],projected["customConstraint"]),projected);
    }
    static void Runtime(ChatSample chat,ArdyRoomDialogueBridge bridge,ChatQW model)
    {
        var settings=(ChatSetting)Get(chat,"m_ChatSettings");settings.m_ChatModel=model;
        var constraints=Get(chat,"m_ActionConstraints");Call(constraints,"RecordUserTurn","Inspect then save the artifact.");
        int turn=(int)constraints.GetType().GetProperty("CurrentUserTurn").GetValue(constraints),epoch=(int)Get(chat,"m_WorkEpoch");
        var sequence=new JObject{["request"]="Inspect then save the artifact.",["userTurn"]=turn,["workEpoch"]=epoch,
            ["steps"]=new JArray(new JObject{["id"]="inspect",["kind"]="inspect",["actionId"]="inspection",["status"]="completed",["after"]=new JArray()},
                new JObject{["id"]="save",["kind"]="save",["actionId"]="save",["status"]="completed",["after"]=new JArray("inspect")}),["answerObligations"]=new JArray()};
        Set(chat,"m_RoomTaskSequence",sequence);Set(chat,"m_RoomTaskSequenceModel",model);Set(chat,"m_RoomTaskSequenceBridge",bridge);
        Set(chat,"m_RoomTaskSequenceAvatar",bridge.avatar);Set(chat,"m_RoomTaskSequenceEnvironment",bridge.environmentRoot);
        Set(chat,"m_WorkReviewOpen",true);Set(chat,"m_WorkStatus","continue");
        Set(chat,"m_UserSpeechActiveForAutonomy",false);Set(chat,"m_AgentGracefulShutdownPending",false);
        Call(chat,"EnsureWorkDeliveryOwner");var ledger=(Dictionary<string,string>)Get(chat,"m_WorkDeliveredResults");
        var audits=(Dictionary<string,string>)Get(chat,"m_WorkDeliveredPlayback");
        Call(chat,"RecordWorkCompletedSpeech","The inspection and save are complete.");
        var results=(JArray)((JObject)Call(chat,"ReadWorkDelivery"))["results"];
        Check("runtime-unreported-completion-keeps-obligation",!(bool)Call(chat,"TrySettleDeliveredWork"));
        foreach(JObject r in results){ledger[(string)r["id"]]="The inspection and save are complete.";audits[(string)r["id"]]=(string)r["playbackRevision"];}
        sequence["steps"][1]["status"]="pending";
        Check("runtime-pending-step-never-closed",!(bool)Call(chat,"TrySettleDeliveredWork"));sequence["steps"][1]["status"]="completed";
        Set(chat,"m_UserSpeechActiveForAutonomy",true);Check("runtime-user-interruption-prevents-close",!(bool)Call(chat,"TrySettleDeliveredWork"));Set(chat,"m_UserSpeechActiveForAutonomy",false);
        Call(chat,"RecordWorkCompletedSpeech","The inspection and save are complete.");
        Check("runtime-new-unchecked-audio-prevents-close",!(bool)Call(chat,"TrySettleDeliveredWork"));
        // Simulates the matching completed-audio semantic audit, not playback.
        results=(JArray)((JObject)Call(chat,"ReadWorkDelivery"))["results"];
        foreach(JObject r in results)audits[(string)r["id"]]=(string)r["playbackRevision"];
        Set(chat,"m_PrivateDecisionFailure",new PrivateDecisionResult("","invalid-delivery-label",false));
        Set(chat,"m_PrivateDecisionFailureEpoch",epoch);Set(chat,"m_PrivateDecisionFailureTurn",turn);
        Check("runtime-settled-ledger-releases-failed-review",(bool)Call(chat,"TrySettleDeliveredWork")&&Get(chat,"m_PrivateDecisionFailure")==null&&!(bool)Get(chat,"m_WorkReviewOpen"));
        Check("runtime-settlement-does-not-submit-action",(string)sequence.SelectToken("steps[0].actionId")=="inspection"&&(string)sequence.SelectToken("steps[1].actionId")=="save");
        Set(chat,"m_WorkReviewOpen",true);sequence["userTurn"]=turn-1;
        Check("runtime-old-owner-cannot-close-new-turn",!(bool)Call(chat,"TrySettleDeliveredWork"));
        Set(chat,"m_RoomTaskSequence",null);Set(chat,"m_WorkReviewOpen",false);ledger.Clear();audits.Clear();
    }
    static void Budgets(ChatQW model)
    {
        foreach(var e in Events("Activity/MonitorFrame")) {
            var original=(JObject)e["data"].DeepClone();int i=0;
            foreach(var frame in (string)original["purpose"]=="speech"?ActivityEvidencePolicy.SpeechOperations(original):new[]{original}) {
                string prepared=model.PrepareActivityMonitorFrame(ActivityEvidencePolicy.Focus(frame).ToString(Formatting.None),out var d);
                Check("recorded-full-wire-budget-"+e["line"]+"-"+i++,prepared!=null,JObject.Parse(d));
            }
            Check("resident-memory-unmodified-"+e["line"],JToken.DeepEquals(original,e["data"]));
        }
    }
    static JObject General(string operation,string text,string language,string status="completed")
    {
        var frame=At(51194);var d=(JObject)frame["delivery"];
        d["facts"]=new JObject{["operation"]=operation,["entityId"]="artifact-a",["status"]=status};d["status"]=status;
        d["taskContext"]=new JObject{["parentRequest"]=operation+" artifact-a",["currentOperation"]=operation};d["language"]=language;
        d["completedPlaybackText"]=text;d["playbackRevision"]=ActivityEvidencePolicy.Stamp(new JValue(text));
        d["completedAudio"]=new JArray(new JObject{["audioId"]="variant-"+operation,["speechVersion"]=2,["textStart"]=0,["textLength"]=text.Length,["wholeTextPlayed"]=true});
        d["rejectedCandidates"]=new JArray();d["supplementPlayback"]=new JArray();d["latestCorrectionStart"]=0;d["otherResults"]=new JArray();return frame;
    }
    static IEnumerator Monitor(ChatQW model,JObject original,string name,bool covered)
    {
        var attempts=new JArray();bool valid=false;JObject verdict=null;string reason=null;
        for(int retry=0;retry<2&&!valid;retry++) {
            string input=model.PrepareActivityMonitorFrame(ActivityEvidencePolicy.Focus(original).ToString(Formatting.None),out var diagnostic);
            if(input==null){attempts.Add(new JObject{["budget"]=JObject.Parse(diagnostic)});break;}
            bool done=false,ok=false;string raw=null,status=null;
            model.PostActivityMonitor(input,(a,b,c)=>{done=true;ok=a;raw=b;status=c;});yield return Until(()=>done);
            valid=ok&&ActivityMonitorProtocol.TryRead(raw,JObject.Parse(input),out verdict,out reason);
            if(valid)valid=((string)verdict.SelectToken("delivery.operation")=="covered")==covered;
            attempts.Add(new JObject{["input"]=JObject.Parse(input),["budget"]=JObject.Parse(diagnostic),["raw"]=raw,["status"]=status,["reason"]=reason,["accepted"]=valid});
            if(!valid) {
                var feedback=ActivityDeliveryProtocol.RepairFeedback(new JObject{["rejection"]=reason??"semantic-coverage-mismatch",["rawVerdict"]=raw});
                JObject rejected=null;try{rejected=JObject.Parse(raw??"{}");}catch(JsonException){}
                feedback["fieldRepair"]=ResultPlaybackSelection.Repair((JObject)original["delivery"],rejected?["delivery"] as JObject,reason,true);
                original["previousAttempt"]=feedback;
            }
        }
        Check("native-"+name,valid,attempts);
        if(valid&&covered)Check("native-selected-quote-matches-alias-"+name,(string)verdict.SelectToken("delivery.quote")==
            (string)verdict.SelectToken("delivery.playbackProof.lastRelevantQuote"),verdict);
        if(valid&&name=="recorded-50893")Check("native-lamp-proof-is-not-bare-acknowledgment",(string)verdict.SelectToken("delivery.quote")!="確認しました。",verdict);
    }
    static IEnumerator WorkReview(ChatQW model)
    {
        var results=new JArray(At(50893)["delivery"].DeepClone(),At(51194)["delivery"].DeepClone());
        var history=new JObject{["request"]="跟着我走。",["termination"]=new JObject{["committed"]=true,["reason"]="replaced-by-current-task"},
            ["taskSequence"]=new JObject{["steps"]=new JArray(new JObject{["kind"]="follow",["status"]="running"})}};
        string prompt="[Runtime work review domain: general]\n[Complete recorded dialogue and actual playback]\n"+fixture["history"].ToString(Formatting.None)+
            "\n[Task progress runtime]\n"+new JObject{["currentUserText"]="去把刚才你关掉的灯重新打开，然后回到我身边。",["previousUnfinishedWork"]=history,["delivery"]=new JObject{["results"]=results}}.ToString(Formatting.None)+
            "\n[Review instruction]\n"+WorkNextIntentProtocol.Contract;
        string raw=null;bool done=false;model.PostWorkReviewMsg(prompt,v=>{raw=v;done=true;});yield return Until(()=>done,40);
        JObject verdict=null;try{verdict=JObject.Parse(raw??"");}catch(JsonException){}
        string reason=null;Check("native-full-real-history-settlement",verdict!=null&&WorkResultDeliveryProtocol.ValidateCoverage(results,verdict,out reason)&&
            (string)verdict["previous_work_status"]=="terminated"&&((JArray)verdict["delivery_reports"]).All(r=>(string)r["coverage"]=="reported"),
            new JObject{["raw"]=raw,["reason"]=reason,["prompt"]=prompt});
        Check("native-work-review-also-keeps-complete-audio-proposition",verdict!=null&&((JArray)verdict["delivery_reports"]).All(r=>(string)r["quote"]!="確認しました。"));
        var original=JObject.Parse((string)At(51235)["rawDecision"]);
        string repair=prompt+"\n[One bounded correction of the actual rejected candidate]\n"+
            WorkResultDeliveryProtocol.RepairDetails(results,original,"playback-latest-relevant-quote-required")+"\n"+original.ToString(Formatting.None);
        raw=null;done=false;model.PostWorkReviewMsg(repair,v=>{raw=v;done=true;});yield return Until(()=>done,40);
        verdict=null;try{verdict=JObject.Parse(raw??"");}catch(JsonException){}
        Check("native-actual-rejected-candidate-corrected",verdict!=null&&WorkResultDeliveryProtocol.ValidateCoverage(results,verdict,out reason)&&
            ((JArray)verdict["delivery_reports"]).All(r=>(string)r["coverage"]=="reported"),new JObject{["raw"]=raw,["reason"]=reason});
    }
}
