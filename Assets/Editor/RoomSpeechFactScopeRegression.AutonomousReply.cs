using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

public static partial class RoomSpeechFactScopeRegression
{
    private static IEnumerator CheckAutonomousReply(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, ChatQW serializer, string output)
    {
        var fixture = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/AutonomousReply.latest-20261013.json"));
        LastDecisionFailureReport["persistentAttention"] = SceneAttentionStateRegression.Run();
        var latest = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/Attention.latest-20261013.json"));
        foreach (var row in latest["cases"]) ((JArray)fixture["cases"]).Add(row.DeepClone());
        foreach (var row in JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/AttentionLifecycle.latest-20261013.json"))["cases"])
            ((JArray)fixture["cases"]).Add(row.DeepClone());
        foreach (var row in JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/QualityRecovery.latest-20261013.json"))["cases"])
            ((JArray)fixture["cases"]).Add(row.DeepClone());
        string original = fixture.ToString(); var reports = new JArray();
        string[] fields = { "m_AutonomousReplyOwner", "m_SceneAutonomyAttention", "m_SceneAutonomyIntention", "m_SpatialDeviceGoal",
            "m_RoomTaskSequence", "m_RoomTaskSequenceModel", "m_RoomTaskSequenceBridge", "m_RoomTaskSequenceAvatar", "m_RoomTaskSequenceEnvironment",
            "m_SceneActionFact", "m_WorkReplyOnly", "m_PendingSelfInspection", "m_SongCatalogResultPending",
            "m_LastSongCatalogResult", "m_StickyToolFailure", "m_AgentRunning", "m_WorkReviewOpen", "m_WorkStatus", "m_WorkNoProgress",
            "m_AgentCurrentRoundIsTick", "m_LastFormalReplyReceipt", "m_LastFormalReplyBridge", "m_LastFormalReplyAvatar",
            "m_LastFormalReplyEnvironment", "m_LastFormalReplySession", "m_DeviceEventsBridge", "m_DeviceEventsModel", "m_DeviceEventsAvatar",
            "m_DeviceEventsEnvironment", "m_DeviceEventsSession", "m_WorkDeviceResult", "m_WorkDeviceResultGoal", "m_WorkDeviceResultEpoch", "m_WorkDeviceResultTurn" };
        var saved = fields.ToDictionary(n => n, n => Get(chat,n)); var history = serializer.m_DataList.ToList();
        var recent=(Queue<JObject>)Get(chat,"m_RecentRoomReplies");var savedRecent=recent.ToArray();
        var devices=(List<JObject>)Get(chat,"m_DeviceEvents");var savedDevices=devices.ToArray();
        var body = new[] { "actionId", "phase", "reason", "latestDiagnostic", "executionTargetKind" }.ToDictionary(n => n,n => Get(bridge,n));
        bool routing = model.EnableFirstReply;
        var runtimeHistory=model.m_DataList.ToList();
        try
        {
            model.EnableFirstReply = false;
            foreach (JObject row in fixture["cases"])
            {
                bool movement = (string)row["name"] == "gen12-approach";
                bool crossDomain=(bool?)row["crossDomainResults"]!=false;
                object retainedGoal=null; JObject retainedResult=null;
                yield return PrepareRoomPlanning(chat, bridge, (string)row["originalRequest"], beforePlanner: () => {
                    Set(chat,"m_AgentCurrentRoundIsTick",true); Set(chat,"m_WorkReplyOnly",false);
                    Set(chat,"m_SpatialDeviceGoal",null); Set(chat,"m_RoomTaskSequence",null);
                    Set(chat,"m_SceneAutonomyIntention",(string)row["intention"]);
                    var decisionType=typeof(ChatSample).GetNestedType("AutonomyIntentDecision",BindingFlags.NonPublic);
                    var decision=Activator.CreateInstance(decisionType,true);
                    Set(decision,"intent",(string)row["intention"]); Set(decision,"planning_focus",(string)row["planningFocus"] ?? "current-user-body");
                    Call(chat,"BindAutonomyAttention",decision);
                    var factType=typeof(ChatSample).GetNestedType("SceneActionFact",BindingFlags.NonPublic);
                    Set(chat,"m_SceneActionFact",UnityEngine.JsonUtility.FromJson(row["scene"]["lastAction"].ToString(),factType));
                    // Preserve the live bug's completed goal and cached mandatory
                    // result instead of only inserting a detached historical event.
                    retainedGoal=Activator.CreateInstance(typeof(ChatSample).GetField("m_SpatialDeviceGoal",Flags).FieldType,true);
                    Set(retainedGoal,"id","completed-retained-goal"); Set(retainedGoal,"status","satisfied");
                    Set(retainedGoal,"environment",bridge.environmentRoot);Set(retainedGoal,"avatar",bridge.avatar);
                    Set(retainedGoal,"source",Get(chat,"m_SceneVisionCapture"));Set(retainedGoal,"model",model);
                    Set(retainedGoal,"latestUserText",(string)row["originalRequest"]);
                    Require(NeEEvA.Perception.SceneObjectLampCatalog.EnsureRegistered(bridge.environmentRoot,out var retainedDevice,out string bindFailure),
                        "Retained completion binds the actual isolated scene device: "+bindFailure);
                    Set(retainedGoal,"entityId",retainedDevice.EntityId);Set(retainedGoal,"entityRoot",retainedDevice.Root);
                    Set(retainedGoal,"desiredState",retainedDevice.ReadState().on?"on":"off");
                    Set(retainedGoal,"completionEvidence",new JObject { ["entityId"]=retainedDevice.EntityId,
                        ["actual"]=JObject.Parse(UnityEngine.JsonUtility.ToJson(retainedDevice.ReadState())) });
                    Set(chat,"m_SpatialDeviceGoal",retainedGoal);
                    Set(chat,"m_WorkDeviceResultGoal",retainedGoal);Set(chat,"m_WorkDeviceResultEpoch",Get(chat,"m_WorkEpoch"));
                    var constraints=Get(chat,"m_ActionConstraints");
                    int currentTurn=(int)constraints.GetType().GetProperty("CurrentUserTurn").GetValue(constraints);
                    Set(chat,"m_WorkDeviceResultTurn",currentTurn);
                    var completedSequence=new JObject { ["workEpoch"]=(int)Get(chat,"m_WorkEpoch"),["userTurn"]=currentTurn,
                        ["request"]=(string)row["originalRequest"],["steps"]=new JArray(new JObject {
                            ["id"]="done",["kind"]="observation",["status"]="completed",["after"]=new JArray(),
                            ["receipt"]=new JObject { ["phase"]="observed",["actionId"]="completed-inspection" } }) };
                    Set(chat,"m_RoomTaskSequence",completedSequence);Set(chat,"m_RoomTaskSequenceModel",model);
                    Set(chat,"m_RoomTaskSequenceBridge",bridge);Set(chat,"m_RoomTaskSequenceAvatar",bridge.avatar);
                    Set(chat,"m_RoomTaskSequenceEnvironment",bridge.environmentRoot);
                    Require(Call(chat,"ReadRoomTaskSequence") is JObject,"Completed task ledger is still current and present during new autonomy.");
                    retainedResult=new JObject { ["resultReportRequirement"]=new JObject { ["required"]=true,["expectedReport"]="on" },
                        ["goal"]=new JObject { ["goalId"]="completed-retained-goal",["status"]="satisfied" } };
                    Set(chat,"m_WorkDeviceResult",retainedResult);
                    if(row["bodyCompletion"] is JObject bodyCompletion) {
                        Set(bridge,"actionId",(string)bodyCompletion["actionId"]);Set(bridge,"executionTargetKind","user");
                        Call(bridge,"SetFact","arrived",(string)bodyCompletion["reason"]);
                        Set(bridge,"latestDiagnostic",UnityEngine.JsonUtility.FromJson(bodyCompletion.ToString(),typeof(ArdyRoomDialogueBridge).GetField("latestDiagnostic",Flags).FieldType));
                        model.m_DataList=history.Concat(row["history"].OfType<JObject>().Select(m=>new LLM.SendData((string)m["role"],(string)m["content"]))).ToList();
                    }
                });
                int generation=(int)Get(chat,"m_FormalResponseGeneration");
                recent.Clear();devices.Clear();
                foreach(JObject reply in row["history"].Where(x=>(string)x["role"]=="assistant").TakeLast(4))
                    recent.Enqueue(new JObject{["publicText"]=reply["content"]});
                var replayReceipt = (JObject)row["lastReplyReceipt"].DeepClone();
                replayReceipt["recordedAtUtc"] = DateTime.UtcNow.AddSeconds(-2).ToString("o");
                Set(chat,"m_LastFormalReplyReceipt",replayReceipt);
                Set(chat,"m_LastFormalReplyBridge",bridge);Set(chat,"m_LastFormalReplyAvatar",bridge.avatar);
                Set(chat,"m_LastFormalReplyEnvironment",bridge.environmentRoot);Set(chat,"m_LastFormalReplySession",Get(chat,"m_AgentSessionStartTime"));
                Call(chat,"ArchiveDeviceEvent",new JObject{["lastAction"]=row["scene"]["lastAction"].DeepClone(),["recordedGeneration"]=0});
                if(row["deviceEvents"] is JArray recordedEvents) {
                    devices.Clear();foreach(JObject recorded in recordedEvents)Call(chat,"ArchiveDeviceEvent",recorded);
                }
                Call(chat,"StageFormalObservationFrame",(string)row["frame"],generation);
                var planningRequest=model.Decisions.Last();
                model.Decisions.Last().Complete(true,"{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}","accepted-autonomous-conversation");
                Require(Call(chat,"BuildMovementPriorResults")==null && ReferenceEquals(Get(chat,"m_SpatialDeviceGoal"),retainedGoal) &&
                    (bool)retainedResult["resultReportRequirement"]["required"],
                    "New autonomous purpose excludes old mandatory reporting without clearing completed memory or its original obligation.");
                Require((bool)Call(chat,"UseFocusedReplyPacket",generation) && Call(chat,"ReadMovementReplySemantics",generation)==null,
                    "Real autonomous planner callback binds a reporting purpose without manufacturing user action semantics.");
                Require(model.Speech.Last().Image==null && model.Speech.Last().Stage==FormalResponseStage.CommittedSpatialReply,
                    "Autonomous no-action conversation uses the guarded compact producer.");
                // Controlled execution receipt; this is not a physical route replay.
                if(movement)
                {
                    Set(bridge,"actionId","autonomous-owned-arrival");Set(bridge,"executionTargetKind","user");
                    Call(bridge,"SetFact","arrived","Controlled replay of the recorded successful autonomous approach.");
                    Set(chat,"m_RoomTaskIntent","approach");Set(chat,"m_RoomTaskActionId","autonomous-owned-arrival");
                    Set(chat,"m_RoomTaskDispatch","attempt-submitted");
                    Call(chat,"BindAutonomousReplyOwner",generation,true,true,bridge);
                }
                Set(chat,"m_PendingSelfInspection",crossDomain?"Inspection result: microphone connected; no recording operation started.":null);
                Set(chat,"m_SongCatalogResultPending",crossDomain);Set(chat,"m_LastSongCatalogResult",crossDomain?"Catalog result: two recordings listed; neither was played.":null);
                Set(chat,"m_StickyToolFailure",crossDomain?"A separate playback request failed; no retry was submitted.":null);
                int operations=bridge.Controller.OperationRevision;
                serializer.m_DataList=((string)row["name"]=="latest-quality-completion" ? history.Where(x=>x.role=="system") : history).ToList();
                foreach(JObject message in row["history"]) serializer.m_DataList.Add(new LLM.SendData((string)message["role"],(string)message["content"]));
                model.m_DataList=serializer.m_DataList.ToList();
                Call(chat,"StageFormalObservationFrame",(string)row["frame"],generation);
                Require((bool)Call(chat,"UseFocusedReplyPacket",generation), "Autonomous owner survives retained ledger before dispatch: " + row["name"] +
                    " owner=" + (Get(chat,"m_AutonomousReplyOwner")!=null) + " attention=" + Call(chat,"ReadAutonomyAttention") +
                    " review=" + Get(chat,"m_WorkReviewOpen") + " goalAnswer=" + Call(chat,"CurrentGenerationRequiresReviewedGoalAnswer",generation) +
                    " tool=" + Call(chat,"HasWorkToolInFlight") + " sceneBusy=" + typeof(ChatSample).GetProperty("SceneActionIsBusy",Flags).GetValue(chat));
                Call(chat,"DispatchFormalStream",(string)row["frame"],null,generation,true,(string)row["frame"],true);
                var writer=model.Speech.Last();
                Require(writer.Image==null && writer.Context.Contains((string)row["intention"]) && writer.Context.Contains((string)row["originalRequest"]),
                    "Current intention and historical constraints survive without an irrelevant screenshot.");
                foreach(string marker in crossDomain?new[]{"Inspection result:","Catalog result:","A separate playback request failed"}:new string[0])
                    Require(writer.Context.Contains(marker),"Pending independent outcome is retained: "+marker);
                Require(!writer.Context.Contains("knownObjectHistory") && !writer.Context.Contains("[感知帧") &&
                    Get(chat,"m_StagedFormalObservation")==null,"Actual dispatch does not reconstruct the full historical scene.");
                string directory=Path.Combine(output,(string)row["name"]);Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory,"planner-input.json"),planningRequest.Input);
                JObject Export(NevaSceneVisionIntegrationModel.Request sent,string name)
                {
                    var messages=typeof(ChatQW).GetMethod("CreateObservationRequestHistory",Flags|BindingFlags.Static).Invoke(serializer,new object[]{serializer.m_DataList,sent.Image});
                    // A formal prompt is not execution feedback. The production
                    // writer selects its final instruction from bound facts.
                    string feedback = sent.Kind == "work-feedback" ? sent.Input : null;
                    string wire=(string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages",Flags).Invoke(serializer,new object[]{messages,true,sent.Context,feedback,sent.Stage});
                    object[] admission={wire,null};
                    Require((bool)typeof(ChatQW).GetMethod("AdmitSceneSpeechWire",Flags|BindingFlags.Static).Invoke(null,admission),"Whole recorded dialogue and pending cross-domain outcomes fit unchanged ceilings: "+name);
                    File.WriteAllText(Path.Combine(directory,name+"-request.json"),wire);
                    File.WriteAllText(Path.Combine(directory,name+"-context.txt"),sent.Context);
                    File.WriteAllText(Path.Combine(directory,name+"-budget.json"),((JObject)admission[1]).ToString());
                    bool currentPurpose = wire.Contains("Write for the current autonomous intention now");
                    if (name == "repair") {
                        var bound = CommittedSpatialReplyProtocol.ReadReplyFacts(sent.Context);
                        currentPurpose = (string)bound?.SelectToken("replyPurpose.source") == "accepted-current-autonomous-decision" &&
                            wire.Contains("Correct the unreleased result using only this bound current purpose and its receipts.") &&
                            wire.Contains((string)bound.SelectToken("replyPurpose.actionId")) &&
                            wire.Contains("Do not resume historical commands");
                    }
                    Require(currentPurpose && !wire.Contains("[Write the answer to the current human request now]"),
                        "The final writer instruction cannot override the current autonomous purpose with an old human command.");
                    if ((string)row["name"] == "gen17-other" || (string)row["name"] == "latest-retained-completion")
                        Require((name != "writer" || (long)((JObject)admission[1])["estimatedTextUnits"] < 14000) && !wire.Contains("knownObjectHistory"),
                            "Latest seven input captures and ordinary autonomous purpose stay compact at the complete serializer boundary.");
                    return (JObject)admission[1];
                }
                var report=new JObject{["name"]=row["name"],["sourceFrameCharacters"]=((string)row["frame"]).Length,
                    ["historyMessages"]=((JArray)row["history"]).Count,["contextCharacters"]=writer.Context.Length,["writerBudget"]=Export(writer,"writer")};
                if((string)row["name"]=="latest-retained-completion") {
                    string workPrompt=(string)Call(chat,"BuildDedicatedWorkReviewPrompt",(string)row["frame"]);
                    string workWire=(string)typeof(ChatQW).GetMethod("BuildWorkReviewRequestJson",Flags).Invoke(serializer,new object[]{workPrompt});
                    object[] budget={workWire,null};
                    Require((bool)typeof(ChatQW).GetMethod("AdmitWorkReviewWire",Flags).Invoke(serializer,budget),"Full latest work-review production load still fits unchanged ceilings.");
                    report["workReviewBudget"]=(JObject)budget[1];
                    File.WriteAllText(Path.Combine(directory,"work-review-budget.json"),((JObject)budget[1]).ToString());
                    Require((long)((JObject)budget[1])["remainingTextUnits"] > 1000,
                        "General progress review leaves over 1000 units of headroom under the unchanged 24000-unit hard limit.");
                    Require(JObject.Parse(workWire)["messages"].Count(message =>
                        ((string)message["content"] ?? "").Contains(WorkNextIntentProtocol.Contract)) == 1,
                        "The complete next-intent contract remains present exactly once in the production work-review wire.");
                    File.WriteAllText(Path.Combine(directory,"work-review-request.json"),workWire);
                    string fullDomainWire=(string)typeof(ChatQW).GetMethod("BuildWorkReviewRequestJson",Flags).Invoke(serializer,new object[]{workPrompt.Replace("[Runtime work review domain: general]\n","")});
                    budget=new object[]{fullDomainWire,null};typeof(ChatQW).GetMethod("AdmitWorkReviewWire",Flags).Invoke(serializer,budget);
                    report["fullDomainSystemControlBudget"]=(JObject)budget[1];
                    Require(workWire.Length<fullDomainWire.Length && workWire.Contains((string)row["originalRequest"]),
                        "Purpose budgeting removes unrelated system detail while retaining the complete actual user request.");
                }
                if(movement)
                {
                    int count=model.Speech.Count, priorHistory=model.m_DataList.Count;
                    const string rejected="今、もうすぐ着くところよ。大丈夫、ちゃんと隣にいるから。";
                    writer.SpeechCallback(new SpeechText(rejected,"ja",actionDependency:SpeechActionDependency.Independent));
                    writer.SpeechCompleteCallback(rejected);
                    var review=model.Decisions.Last();
                    var facts=JObject.Parse(review.Input);
                    Require(review.Kind=="room-review" && review.Image==null && facts["visualEvidence"].Type==JTokenType.Null &&
                        (string)facts.SelectToken("movementReportRequirement.expectedReport")=="arrived" && facts["replyPurpose"] is JObject,
                        "Reviewer uses current owned arrival and purpose without fresh image or full scene facts.");
                    File.WriteAllText(Path.Combine(directory,"review-input.json"),review.Input);
                    if(review.Wire!=null)File.WriteAllText(Path.Combine(directory,"review-request.json"),review.Wire);
                    string verdict=new JObject{["verdict"]="inconsistent",["factScope"]="user-target",["actualLanguage"]="ja",
                        ["resultReport"]="arrived",["reason"]="The candidate says both not yet arrived and already beside the user."}.ToString();
                    review.Complete(true,verdict,"controlled-temporal-conflict");
                    yield return Wait(()=>model.Speech.Count==count+1,"One compact autonomous speech correction");
                    var repair=model.Speech.Last();
                    Require(repair.Image==null && repair.Context.Contains("autonomous-owned-arrival") &&
                        !repair.Context.Contains("knownObjectHistory") && !repair.Input.Contains("autonomous-owned-arrival"),
                        "Bounded repair rebuilds one owned packet, not the old full scene or a duplicate facts document.");
                    report["repairBudget"]=Export(repair,"repair");
                    review.Complete(true,verdict,"duplicate-review");
                    Require(model.Speech.Count==count+1 && model.m_DataList.Count==priorHistory && bridge.Controller.OperationRevision==operations,
                        "Rejected candidate stays private and duplicate rejection cannot repeat action or repair.");
                    Call(chat,"InvalidateFormalResponse","autonomous-repair-interrupted");
                    repair.SpeechCallback(new SpeechText("あなたのそばに着いたわ。","ja",actionDependency:SpeechActionDependency.Independent));
                    repair.SpeechCompleteCallback("あなたのそばに着いたわ。");
                    Require(model.m_DataList.Count==priorHistory && (bool)Get(chat,"m_SongCatalogResultPending"),
                        "Late repair callback cannot publish or acknowledge undelivered outcomes after interruption.");
                }
                else
                {
                    string candidate = (string)row["candidate"] ?? "ずっと会話が途切れていたのね。";
                    writer.SpeechCallback(new SpeechText(candidate, "ja", actionDependency:SpeechActionDependency.Independent));
                    writer.SpeechCompleteCallback(candidate);
                    var review = model.Decisions.Last();
                    Require(review.Kind == "room-review" && review.Image == null && JObject.Parse(review.Input)["replyPurpose"] is JObject,
                        "Ordinary autonomous conversation review retains its own purpose without rebuilding scene evidence.");
                    var autonomousReview=JObject.Parse(review.Input);
                    if((string)row["name"]=="latest-quality-completion") {
                        Require((string)autonomousReview.SelectToken("eventContext.latestExecutionEvent.phase")=="arrived" &&
                            (bool?)autonomousReview.SelectToken("eventContext.executionBelongsToThisReply")==false &&
                            (string)autonomousReview.SelectToken("eventContext.latestDeviceEventByEntity[0].desiredState")=="on",
                            "Recorded completed return stays true without a new action; newest same-entity event is on, not older off.");
                        Require(writer.Context.Contains("completedPlaybackText") && !writer.Context.Contains("A separate playback request failed"),
                            "Actual latest replay preserves delivered speech and does not invent cross-domain failures.");
                    }
                    Require(RoomSpeechClaimProtocol.Requirement(autonomousReview,out string claimDomain)==null && claimDomain=="conversation",
                        "Production review input with a retained completed goal reaches conversation classification, not an old on/off requirement.");
                    File.WriteAllText(Path.Combine(directory, "review-input.json"), review.Input);
                    if (review.Wire != null) File.WriteAllText(Path.Combine(directory, "review-request.json"), review.Wire);
                    if ((string)row["name"] == "gen17-other" || (string)row["name"] == "latest-retained-completion")
                    {
                        int before = model.Speech.Count;
                        review.Complete(true, Verdict("inconsistent", "none"), "controlled-unsupported-silence");
                        yield return Wait(() => model.Speech.Count == before + 1, "One bounded general-purpose speech correction");
                        var repair = model.Speech.Last();
                        report["repairBudget"] = Export(repair, "repair");
                        Require((long)report["repairBudget"]["estimatedTextUnits"] < (long)report["writerBudget"]["estimatedTextUnits"] + 2000,
                            "A bounded speech repair adds only its rejection/current facts, not another full context packet.");
                        Require(repair.Image == null && repair.Context.Contains((string)row["intention"]) && !repair.Context.Contains("knownObjectHistory"),
                            "General conversation repair keeps the same focused facts and intended topic.");
                        review.Complete(true, Verdict("inconsistent", "none"), "duplicate-general-rejection");
                        Require(model.Speech.Count == before + 1 && bridge.Controller.OperationRevision == operations,
                            "Repeated review cannot create another repair or action.");
                    }
                    foreach(var change in new[]{("m_WorkReviewOpen",(object)true),("m_AgentCurrentRoundIsTick",(object)false),
                        ("m_UserSpeechActiveForAutonomy",(object)true),("m_SceneAutonomyIntention",(object)"new unrelated intention")})
                    {
                        object old=Get(chat,change.Item1);Set(chat,change.Item1,change.Item2);
                        Require(!(bool)Call(chat,"UseFocusedReplyPacket",generation),"Stale/changed autonomous owner cannot select compact reporting: "+change.Item1);
                        Set(chat,change.Item1,old);
                    }
                    var attention=(JObject)Get(chat,"m_SceneAutonomyAttention");attention["kind"]="scene";
                    Require(!(bool)Call(chat,"UseFocusedReplyPacket",generation),"Scene attention retains the visual answer path.");
                    attention["kind"]=(string)row["planningFocus"] ?? "current-user-body";
                    Call(chat,"RememberFirstReplyRequestSemantics",new FirstReplyCompletion{Disposition=FirstReplyDisposition.Tools,RequestKind="mixed"},generation,model);
                    Require(!(bool)Call(chat,"UseFocusedReplyPacket",generation),"An independent current question cannot be hidden by autonomous attention.");
                    Call(chat,"CancelRoomSpeechReview","autonomous-conversation-check-finished");
                }
                reports.Add(report);chat.StopAllCoroutines();
            }
            Require(fixture.ToString()==original,"Recorded observed memory and conversation are never mutated by projection.");
        }
        finally { foreach(var item in saved)Set(chat,item.Key,item.Value);foreach(var item in body)Set(bridge,item.Key,item.Value);serializer.m_DataList=history;model.m_DataList=runtimeHistory;model.EnableFirstReply=routing;
            recent.Clear();foreach(var item in savedRecent)recent.Enqueue(item);devices.Clear();devices.AddRange(savedDevices); }
        LastDecisionFailureReport["autonomousReplyBudgets"]=reports;
    }
}
