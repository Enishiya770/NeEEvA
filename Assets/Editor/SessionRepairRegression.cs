using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class SessionRepairRegression
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    public static int RunChecks()
    {
        int count=0;
        void Check(bool ok,string reason){count++;if(!ok)throw new InvalidOperationException("Session repair: "+reason);}
        var claim=JObject.Parse(@"{'quote':'I saved it.','actor':'CHARACTER','operation':'save','state':'','outcome':'completed','time':'latest-event'}");
        var input=new JObject { ["independentSpeechClaim"]=new JObject {["eventClaims"]=new JArray(claim)},
            ["eventContext"]=new JObject {["engineEvents"]=new JArray(JObject.Parse(@"{'actionId':'save-1','intent':'save','entityId':'report','phase':'completed','observedUtc':'2026-10-13T12:00:00Z'}"))} };
        var review=JObject.Parse(@"{'eventBindings':[{'eventId':'save-1','targetMatch':'matched'}]}");
        Check(SpeechEventBindingProtocol.Reject(input,review,out _)==null,"generic non-device receipt supports exact action");
        claim["actor"]="USER";Check(SpeechEventBindingProtocol.Reject(input,review,out _)=="event-actor-mismatch","same object is not same actor");
        claim["actor"]="CHARACTER";claim["operation"]="delete";
        Check(SpeechEventBindingProtocol.Reject(input,review,out _)=="event-operation-mismatch","save is not delete");
        claim["operation"]="save";claim["outcome"]="failed";
        Check(SpeechEventBindingProtocol.Reject(input,review,out _)=="event-operation-mismatch","success is not failure");
        claim["outcome"]="completed";
        ((JArray)input.SelectToken("eventContext.engineEvents")).Add(JObject.Parse(@"{'actionId':'save-2','intent':'save','entityId':'report','phase':'failed','observedUtc':'2026-10-13T12:10:00Z'}"));
        Check(SpeechEventBindingProtocol.Reject(input,review,out _)=="event-order-mismatch","old success cannot replace new failure");
        claim["time"]="historical-event";Check(SpeechEventBindingProtocol.Reject(input,review,out _)==null,"new failure cannot erase earlier success");
        review.Remove("eventBindings");Check(SpeechEventBindingProtocol.Reject(input,review,out _)=="event-binding-missing","missing binding fails closed");
        review["eventBindings"]=new JArray(JObject.Parse(@"{'eventId':'save-1','targetMatch':'matched'}"));
        var eventRows=(JArray)input.SelectToken("eventContext.engineEvents");eventRows.RemoveAt(1);
        eventRows[0]["actor"]="USER";claim["actor"]="USER";
        Check(SpeechEventBindingProtocol.Reject(input,review,out _)==null,"explicit engine-observed user event retains its actor");
        claim["actor"]="CHARACTER";
        Check(SpeechEventBindingProtocol.Reject(input,review,out _)=="event-actor-mismatch","user event cannot be claimed by the character");
        claim["actor"]="USER";claim["time"]="latest-event";((JObject)eventRows[0]).Remove("observedUtc");
        Check(SpeechEventBindingProtocol.Reject(input,review,out _)=="event-order-unconfirmed","missing time cannot prove newest event");
        var device=JObject.Parse(@"{'sceneActionFacts':{'lastAction':{'actionId':'write-1','intent':'set-object-state','entityId':'lamp','phase':'performed','requestedState':'on'},'goal':{'completionEvidenceValid':true,'completionEvidence':{'actionId':'write-1','entityId':'lamp','actual':{'on':true},'readAtUtc':'2026-10-13T12:00:00Z'}}}}");
        Check(SpeechEventBindingProtocol.Evidence(device).Count==2,"validated state readback has a separate inspection receipt");
        device.SelectToken("sceneActionFacts.goal")["completionEvidenceValid"]=false;
        Check(SpeechEventBindingProtocol.Evidence(device).Count==1,"a state write alone does not invent inspection");
        device.SelectToken("sceneActionFacts.goal")["completionEvidenceValid"]=true;
        device.SelectToken("sceneActionFacts.goal.completionEvidence")["actionId"]="old-action";
        Check(SpeechEventBindingProtocol.Evidence(device).Count==1,"old readback cannot support the new action");
        device.SelectToken("sceneActionFacts.goal.completionEvidence")["actionId"]="write-1";
        device.SelectToken("sceneActionFacts.goal.completionEvidence")["entityId"]="other-object";
        Check(SpeechEventBindingProtocol.Evidence(device).Count==1,"another object's readback cannot prove this inspection");
        var historical=JObject.Parse(@"{'sceneActionFacts':null,'historicalDeviceEvents':[{'resultReportRequirement':{'required':true,'expectedReport':'on'},'lastAction':{'actionId':'write-1','intent':'set-object-state','entityId':'lamp','phase':'performed','requestedState':'on'}}]}");
        var historicalView=JObject.Parse(RoomTaskProtocol.ProjectSpeechReviewInput(historical.ToString()));
        Check(historicalView.SelectToken("historicalDeviceEvents[0].resultReportRequirement")==null &&
            JToken.DeepEquals(historical.SelectToken("historicalDeviceEvents[0].lastAction"),historicalView.SelectToken("historicalDeviceEvents[0].lastAction")) &&
            historical.SelectToken("historicalDeviceEvents[0].resultReportRequirement.required").Value<bool>(),
            "historical receipt keeps event proof without reactivating its former reporting obligation");
        var stopped=JObject.Parse(@"{'currentRoomFacts':{'execution':{'actionId':'stop-1','intent':'stop-moving','targetKind':'user','phase':'stopped'}}}");
        Check((string)SpeechEventBindingProtocol.Evidence(stopped)[0]["operation"]=="stop-moving","completed stop retains stop identity, not cancelled follow");
        stopped.SelectToken("currentRoomFacts.execution")["intent"]="follow";
        stopped.SelectToken("currentRoomFacts.execution")["phase"]="failed";
        Check((string)SpeechEventBindingProtocol.Evidence(stopped)[0]["operation"]=="follow" &&
            (string)SpeechEventBindingProtocol.Evidence(stopped)[0]["outcome"]=="failed","terminal follow retains its actual operation");
        stopped.SelectToken("currentRoomFacts.execution")["phase"]="moving";
        Check(SpeechEventBindingProtocol.Evidence(stopped).Count==0,"ongoing movement never fabricates a completed event");
        var host=new GameObject("failure ownership regression");host.SetActive(false);
        try {
            var c=host.AddComponent<NeEEvA.Motion.ArdyRoomLocomotionController>();
            typeof(NeEEvA.Motion.ArdyRoomLocomotionController).GetProperty("OperationRevision").SetValue(c,11);
            typeof(NeEEvA.Motion.ArdyRoomLocomotionController).GetField("requestId",Flags).SetValue(c,"old-follow-request");
            typeof(NeEEvA.Motion.ArdyRoomLocomotionController).GetMethod("RecordStreamEvent",Flags).Invoke(c,new object[]{"recovering","injected shortage","motion-supply"});
            Check(c.StreamTelemetry.failureAppliesToCurrentOperation,"current recovery retains own diagnostic");
            typeof(NeEEvA.Motion.ArdyRoomLocomotionController).GetProperty("OperationRevision").SetValue(c,18);
            typeof(NeEEvA.Motion.ArdyRoomLocomotionController).GetField("requestId",Flags).SetValue(c,"new-return-request");
            Check(!c.StreamTelemetry.failureAppliesToCurrentOperation&&c.StreamTelemetry.failureOperationRevision==11&&
                c.StreamTelemetry.failureRequestId=="old-follow-request","new action does not inherit old fault");
        } finally {UnityEngine.Object.DestroyImmediate(host);}
        bool Late(float buffer,float cost,float frame) => (bool)typeof(NeEEvA.Motion.ArdyRoomLocomotionController)
            .GetMethod("ArrivalRetargetBudgetInsufficient",Flags).Invoke(null,new object[]{buffer,cost,frame});
        Check(Late(.22069022f,.247238f,.03624f),"recorded near-arrival exit cannot dispatch inside an inadequate supply horizon");
        Check(!Late(.88585f,.247238f,.03624f),"healthy accepted buffer keeps ordinary dispatch");
        var body=JObject.Parse(@"{'autonomous':false,'currentUserText':'Follow me.','currentRequestSemantics':{'source':'independent-first-reply-current-request-review','actionAuthorization':false,'requestKind':'action','planningFocus':{'kind':'current-user-body','currentUserText':'Follow me.'}},'currentFacts':{'bodyReserved':false,'userSupportVerified':true},'visibleObjectCatalog':{'current':{'candidates':[{'entityId':'chair','sourceCaptureId':'c1','authoredHint':{'actionAuthorization':false,'displayName':'chair'},'customSafetyRule':{'noCrossing':true}}]}},'sceneActionPrerequisites':{'customConstraint':'keep clear'},'taskSequenceRequired':true}");
        bool Focus(JObject src,out JObject projected){object[] args={src.ToString(),null};bool ok=(bool)typeof(ChatQW).GetMethod("TryFocusCurrentUserBodyPlanning",Flags).Invoke(null,args);projected=JObject.Parse((string)args[1]);return ok;}
        string original=body.ToString();
        Check(Focus(body,out var projection),"complete current-body request gets purpose view");
        Check(projection.SelectToken("visibleObjectCatalog.current.candidates[0].customSafetyRule.noCrossing")?.Value<bool>()==true&&
            (string)projection.SelectToken("sceneActionPrerequisites.customConstraint")=="keep clear","unknown constraints survive attention projection");
        Check(body.ToString()==original&&JToken.DeepEquals(body["currentFacts"],projection["currentFacts"]),"resident memory and actual body facts unchanged");
        body["sceneActionPrerequisites"]["knownObjects"] = new JArray(new JObject {
            ["entityId"]="known-device",["captureId"]="fresh-1",["currentVisibility"]="confirmed-in-current-capture",
            ["registeredPowerCapability"]=true,["requiresFreshSemanticMatch"]=true,["actionAuthorization"]=false,
            ["measuredDistanceMeters"]=3.0,["deviceDistanceLimitMeters"]=2.5,["withinOperationDistance"]=false
        });
        Check(Focus(body,out projection) && projection.SelectToken("sceneActionPrerequisites.knownObjects") == null &&
            (string)projection.SelectToken("sceneActionPrerequisites.customConstraint")=="keep clear",
            "body planner omits device-only distance/capability rows without dropping unknown constraints");
        body["sceneActionPrerequisites"]["knownObjects"][0]["customSafetyRule"] = "do not enter this area";
        Check(Focus(body,out projection) && (string)projection.SelectToken("sceneActionPrerequisites.knownObjects[0].customSafetyRule")=="do not enter this area",
            "future/custom per-object restrictions survive projection");
        body["currentUserText"]="Follow me and describe that screen.";
        Check(!Focus(body,out _),"old attention cannot scope a changed complete request");
        body["currentUserText"]="Follow me.";body["taskSequence"]=new JObject();
        Check(!Focus(body,out _),"compound approved work retains predecessor receipts");
        var progressClaim=JObject.Parse(@"{'ongoingActivityQuote':'I am still recovering','attemptResult':'incomplete','attemptResultQuote':'not finished yet','actualLanguage':'en','pendingSameResultQuote':'','resultReport':'recovering','evidence':'I am still recovering','targetQuote':'','otherClaims':[],'eventClaims':[]}");
        string progressSpeech="I am still recovering, not finished yet.";
        Check(RoomSpeechClaimProtocol.TryRead(progressClaim.ToString(),progressSpeech,out var progressRead) &&
            (string)progressRead["resultReport"]=="recovering" && progressRead["otherClaims"].Values<string>().Contains("not finished yet"),
            "ongoing recovery and unfinished status coexist without a terminal failure");
        progressClaim["resultReport"]="incomplete";
        Check(!RoomSpeechClaimProtocol.TryRead(progressClaim.ToString(),progressSpeech,out _),"ongoing quote cannot silently become a terminal result");
        progressClaim["resultReport"]="recovering";progressClaim["ongoingActivityQuote"]="not finished yet";
        Check(!RoomSpeechClaimProtocol.TryRead(progressClaim.ToString(),progressSpeech,out _),"same negative quote cannot prove an affirmative activity");
        progressClaim["ongoingActivityQuote"]="";progressClaim["resultReport"]="paused";progressClaim["evidence"]="I stopped";
        Check(RoomSpeechClaimProtocol.TryRead(progressClaim.ToString(),"I stopped, not finished yet.",out progressRead) &&
            (string)progressRead["resultReport"]=="incomplete","stopping does not hide the explicit unfinished result");
        return count;
    }
}
