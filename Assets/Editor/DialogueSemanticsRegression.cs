using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class DialogueSemanticsRegression
{
    public static int RunChecks()
    {
        int count=0;
        void Check(bool b,string why){count++;if(!b)throw new InvalidOperationException("Dialogue semantics: "+why);}
        var movementSchema=JObject.Parse(RoomSpeechClaimProtocol.SchemaForDomain("movement"));
        var movementProperties=(JObject)movementSchema["properties"];
        Check(movementProperties.Properties().First().Name=="movementReading" && movementProperties["speechReading"]==null,
            "Resolve the whole movement utterance before an isolated tense can anchor a second reading.");
        Check(movementProperties["speechActs"]!=null && movementProperties["eventClaims"]!=null && movementProperties["completedMovementQuote"]!=null,
            "Using the movement scaffold retains modality and completed-event evidence.");
        JObject Event(string quote,string operation)=>new JObject { ["quote"]=quote,["actor"]="CHARACTER",["operation"]=operation,["state"]="",["outcome"]="completed",["time"]="historical-event" };
        JObject Claim(string speech)=>new JObject { ["actualLanguage"]="ja",["pendingSameResultQuote"]="",["resultReport"]="omitted",["evidence"]="",["targetQuote"]="",["otherClaims"]=new JArray(),["eventClaims"]=new JArray(Event(speech,"acknowledge")) };
        foreach(string speech in new[]{"確認しました。","明白了。","Understood."}) {
            var courtesy=Claim(speech);courtesy["speechActs"]=new JArray(new JObject { ["quote"]=speech,["kind"]="courtesy" });
            Check(!RoomSpeechClaimProtocol.TryReadDetailed(courtesy.ToString(),speech,out _,out var failure)&&failure=="speechActs:non-event-classified-as-ended-action","Courtesy cannot become a tool event: "+speech);
        }
        var claim=Claim("I inspected the file.");claim["eventClaims"]=new JArray(Event("I inspected the file.","inspect"));
        Check(RoomSpeechClaimProtocol.TryRead(claim.ToString(),"I inspected the file.",out _),"Real inspection stays factual.");
        claim["speechActs"]=new JArray(new JObject { ["quote"]="I inspected the file.",["kind"]="courtesy" });
        Check(!RoomSpeechClaimProtocol.TryReadDetailed(claim.ToString(),"I inspected the file.",out _,out _),"Contradictory classifications must be repaired, not silently erased.");
        claim["speechActs"]=new JArray(new JObject { ["quote"]="We could organize books here someday.",["kind"]="possibility" });
        Check(RoomSpeechClaimProtocol.TryRead(claim.ToString(),"I inspected the file. We could organize books here someday.",out _),"A real event and separate possibility coexist.");
        string request="刚才保存成功了吗？现在播放这段录音。";
        var semantics=new JObject { ["requestKind"]="mixed",["currentTurnEvidence"]=new JObject { ["answerQuote"]="刚才保存成功了吗？" } };
        var obligations=ReplyAnswerObligationProtocol.FromSemantics(request,semantics);
        Check(obligations.Count==1,"Question survives independently of planner context label.");
        Check(ReplyAnswerObligationProtocol.FromSemantics("现在播放这段录音。",semantics).Count==0,"Stale quote cannot enter a new turn.");
        var input=new JObject { ["answerObligations"]=obligations,["publicSpeech"]="播放完成了。" };
        Check(ReplyAnswerObligationProtocol.Reject(input,new JObject())=="answer-coverage-missing","Missing answer cannot be accepted as whole task delivery.");
        var verdict=new JObject { ["answerCoverage"]=new JArray(new JObject { ["questionQuote"]="刚才保存成功了吗？",["status"]="omitted",["answerQuote"]="" }) };
        Check(ReplyAnswerObligationProtocol.Reject(input,verdict)=="independent-question-unanswered","New playback result cannot silently settle prior save question.");
        input["publicSpeech"]="没有刚才保存的回执，无法确认。播放完成了。";
        verdict["answerCoverage"][0]["status"]="explicit-unknown";verdict["answerCoverage"][0]["answerQuote"]="没有刚才保存的回执，无法确认。";
        Check(ReplyAnswerObligationProtocol.Reject(input,verdict)=="answer-uncertainty-not-in-spoken-words","Reviewer cannot invent an unknown answer from a result-only utterance.");
        input["independentSpeechClaim"]=new JObject { ["speechActs"]=new JArray(new JObject { ["quote"]="无法确认",["kind"]="uncertainty" }) };
        Check(ReplyAnswerObligationProtocol.Reject(input,verdict)==null,"Explicit uncertainty is a valid answer obligation result.");
        verdict["answerCoverage"][0]["answerQuote"]="已保存。";
        Check(ReplyAnswerObligationProtocol.Reject(input,verdict)!=null,"Unspoken answer cannot settle obligation.");
        var facts=new JObject { ["goal"]=new JObject { ["status"]="blocked" },["answerObligations"]=obligations,["answerEvidence"]=new JObject { ["unknownConstraint"]="Keep original copy." } };
        var projected=SpatialGoalResultContextPolicy.ProjectResultFacts(facts);
        Check(JToken.DeepEquals(projected["answerObligations"],obligations)&&JToken.DeepEquals(projected["answerEvidence"],facts["answerEvidence"]),"Terminal writing preserves question and its constraints.");
        string context=SpatialGoalResultContextPolicy.BuildContext(request,facts.ToString());
        Check(typeof(ChatSample).GetMethod("CurrentReplyRepairBaseline",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{context,"zh"})==null,"Mixed answer repair cannot collapse into device-only baseline.");
        var schema=JObject.Parse(RoomTaskProtocol.BuildSpeechReviewSchemaForInput(input.ToString()));
        Check(schema["properties"]["answerCoverage"]!=null,"Production review requires independently retained answer coverage.");
        var compact=JObject.Parse(RoomTaskProtocol.CompactDecisionFieldGuide(schema));
        Check(JToken.DeepEquals(SceneObjectProtocolRegression.ExpandNativeSchema(compact),schema),"Textual review sharing preserves every schema constraint.");
        var pendingAnswer=ReplyAnswerObligationProtocol.DeliveryResult((JObject)obligations[0]);
        Check(!WorkResultDeliveryProtocol.CanClose(new JArray(pendingAnswer),null,""),"A missing planner answer span cannot close an independent question.");
        pendingAnswer["hasCompletedPlaybackAfterResult"]=false;
        var answerReport=new WorkResultDeliveryProtocol.Report { id=(string)pendingAnswer["id"],coverage="reported",quote="无法确认。" };
        Check(!WorkResultDeliveryProtocol.Covers(pendingAnswer,answerReport,"无法确认。"),"Generated words cannot settle a question before completed audio.");
        pendingAnswer["hasCompletedPlaybackAfterResult"]=true;
        Check(WorkResultDeliveryProtocol.Covers(pendingAnswer,answerReport,"无法确认。"),"Actual completed uncertain answer can settle delivery.");
        pendingAnswer["delivered"]=true;
        Check(WorkResultDeliveryProtocol.CanClose(new JArray(pendingAnswer),null,""),"Answered question need not reopen a completed task.");
        var resultA=WorkResultDeliveryProtocol.Result("step:a","completed",new JObject{["operation"]="save"});
        var resultB=WorkResultDeliveryProtocol.Result("step:b","failed",new JObject{["operation"]="upload"});
        var results=new JArray(resultA,resultB);
        JObject Row(JObject result,string quote)=>new JObject{["id"]=result["id"].DeepClone(),["comparison"]="Compare this result with completed audio.",["coverage"]=string.IsNullOrEmpty(quote)?"unreported":"reported",["quote"]=quote};
        var decision=new JObject{["delivery_reports"]=new JArray(Row(resultA,"Saved."),Row(resultB,""))};
        Check(WorkResultDeliveryProtocol.ValidateCoverage(results,decision,out _),"Explicit unreported row preserves a pending outcome.");
        Check(!WorkResultDeliveryProtocol.Covers(resultB,new WorkResultDeliveryProtocol.Report { id=(string)resultB["id"],
            comparison="Only the start was spoken, not the failed result.",coverage="unreported",quote="Starting now." },"Starting now."),
            "An exactly played acknowledgement cannot override an explicitly unreported terminal result.");
        Check(!WorkResultDeliveryProtocol.Covers(resultB,new WorkResultDeliveryProtocol.Report { id=(string)resultB["id"],quote="Failed." },"Failed."),
            "Legacy unclassified reports cannot certify current delivery.");
        decision["delivery_reports"]=new JArray(Row(resultA,"Saved."));
        Check(!WorkResultDeliveryProtocol.ValidateCoverage(results,decision,out _),"A missing row cannot masquerade as complete coverage.");
        decision["delivery_reports"]=new JArray(Row(resultA,"Saved."),Row(resultA,"Saved."));
        Check(!WorkResultDeliveryProtocol.ValidateCoverage(results,decision,out _),"Duplicate rows cannot replace another result.");
        var bound=JObject.Parse(WorkNextIntentProtocol.ExtendReviewSchema("{\"type\":\"object\",\"properties\":{},\"required\":[]}"));
        WorkResultDeliveryProtocol.BindCoverageSchema(bound,results);
        Check((int)bound.SelectToken("properties.delivery_reports.minItems")==2 &&
            ((JArray)bound.SelectToken("properties.delivery_reports.items.properties.id.enum")).Count==2,"Native coverage is bound to exact pending versions.");
        resultA["delivered"]=true;
        Check(WorkResultDeliveryProtocol.ReviewIds(results).SequenceEqual(new[]{(string)resultB["id"]}),"Settled results do not require repeated model reporting.");
        var many=new JArray(Enumerable.Range(0,14).Select(i=>WorkResultDeliveryProtocol.Result("step:"+i,"failed",new JObject())));
        Check(WorkResultDeliveryProtocol.ReviewIds(many).Length==12&&!WorkResultDeliveryProtocol.CanClose(many,null,""),"Bounded coverage leaves unreviewed results pending.");
        var nested=new JObject{["id"]="s1",["actionId"]="",["receipt"]=new JObject{["goal"]=new JObject{
            ["status"]="satisfied",["completionEvidenceValid"]=true,["completionEvidence"]=new JObject{["actionId"]="new-operation",["entityId"]="different-object",["actual"]=new JObject{["on"]=true}}}}};
        var summary=WorkResultDeliveryProtocol.StepSummary(nested);
        Check((string)summary["actionId"]=="new-operation"&&(bool)summary.SelectToken("outcome.completionEvidence.actual.on"),"Nested proof reaches delivery summary without inventing execution.");
        Check((string)nested["actionId"]=="","Summary does not mutate canonical version facts.");
        var custom=new JObject{["receipt"]=new JObject{["phase"]="failed",["customCause"]="Keep original file untouched"}};
        Check((string)WorkResultDeliveryProtocol.StepSummary(custom).SelectToken("outcome.customCause")=="Keep original file untouched","Non-device results retain unknown facts and constraints.");
        var courtesyActs=new JArray(new JObject{["quote"]="Understood.",["kind"]="courtesy"});
        Check(!SpeechActScopeProtocol.Validate(courtesyActs,new JArray(),new JArray("Understood"),out _),"Courtesy cannot leak back as the same factual claim.");
        Check(SpeechActScopeProtocol.Validate(courtesyActs,new JArray(),new JArray("The file is saved."),out _),"Distinct factual premise remains reviewable.");
        var body=new JObject{["intent"]="approach",["actionId"]="return-2",["currentRoomFacts"]=new JObject{
            ["execution"]=new JObject{["actionId"]="return-2",["targetKind"]="user",["phase"]="arrived"},
            ["currentFacts"]=new JObject{["currentlyNearAndFacingUser"]=true}}};
        var reportMethod=typeof(ChatSample).GetMethod("WorkResultMovementReport",BindingFlags.Static|BindingFlags.NonPublic);
        foreach(string language in new[]{"zh","ja","en"})
            Check(!string.IsNullOrEmpty((string)reportMethod.Invoke(null,new object[]{body,language})),"Arrival fallback shares the owned report in "+language);
        body["currentRoomFacts"]["currentFacts"]=new JObject{["actualUserDistance"]=3.0,["desiredUserDistance"]=1.0};
        Check(((string)reportMethod.Invoke(null,new object[]{body,"en"})).Contains("has changed"),"User movement preserves arrival event but changes current proximity wording.");
        body["actionId"]="stale-action";
        Check(reportMethod.Invoke(null,new object[]{body,"en"})==null,"A stale owner cannot receive an arrival fallback.");
        return count;
    }
}
