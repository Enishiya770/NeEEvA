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
        string original = fixture.ToString(); var reports = new JArray();
        string[] fields = { "m_AutonomousReplyOwner", "m_SceneAutonomyAttention", "m_SceneAutonomyIntention", "m_SpatialDeviceGoal",
            "m_RoomTaskSequence", "m_SceneActionFact", "m_WorkReplyOnly", "m_PendingSelfInspection", "m_SongCatalogResultPending",
            "m_LastSongCatalogResult", "m_StickyToolFailure", "m_AgentRunning", "m_WorkReviewOpen", "m_WorkStatus", "m_WorkNoProgress",
            "m_AgentCurrentRoundIsTick", "m_LastFormalReplyReceipt", "m_LastFormalReplyBridge", "m_LastFormalReplyAvatar",
            "m_LastFormalReplyEnvironment", "m_LastFormalReplySession", "m_DeviceEventsBridge", "m_DeviceEventsModel", "m_DeviceEventsAvatar",
            "m_DeviceEventsEnvironment", "m_DeviceEventsSession" };
        var saved = fields.ToDictionary(n => n, n => Get(chat,n)); var history = serializer.m_DataList.ToList();
        var recent=(Queue<JObject>)Get(chat,"m_RecentRoomReplies");var savedRecent=recent.ToArray();
        var devices=(List<JObject>)Get(chat,"m_DeviceEvents");var savedDevices=devices.ToArray();
        var body = new[] { "actionId", "phase", "reason", "latestDiagnostic", "executionTargetKind" }.ToDictionary(n => n,n => Get(bridge,n));
        bool routing = model.EnableFirstReply;
        try
        {
            model.EnableFirstReply = false;
            foreach (JObject row in fixture["cases"])
            {
                bool movement = (string)row["name"] == "gen12-approach";
                yield return PrepareRoomPlanning(chat, bridge, (string)row["originalRequest"], beforePlanner: () => {
                    Set(chat,"m_AgentCurrentRoundIsTick",true); Set(chat,"m_WorkReplyOnly",false);
                    Set(chat,"m_SpatialDeviceGoal",null); Set(chat,"m_RoomTaskSequence",null);
                    Set(chat,"m_SceneAutonomyIntention",(string)row["intention"]);
                    var decisionType=typeof(ChatSample).GetNestedType("AutonomyIntentDecision",BindingFlags.NonPublic);
                    var decision=Activator.CreateInstance(decisionType,true);
                    Set(decision,"intent",(string)row["intention"]); Set(decision,"planning_focus","current-user-body");
                    Call(chat,"BindAutonomyAttention",decision);
                    var factType=typeof(ChatSample).GetNestedType("SceneActionFact",BindingFlags.NonPublic);
                    Set(chat,"m_SceneActionFact",UnityEngine.JsonUtility.FromJson(row["scene"]["lastAction"].ToString(),factType));
                });
                int generation=(int)Get(chat,"m_FormalResponseGeneration");
                recent.Clear();devices.Clear();
                foreach(JObject reply in row["history"].Where(x=>(string)x["role"]=="assistant").TakeLast(4))
                    recent.Enqueue(new JObject{["publicText"]=reply["content"]});
                Set(chat,"m_LastFormalReplyReceipt",row["lastReplyReceipt"].DeepClone());
                Set(chat,"m_LastFormalReplyBridge",bridge);Set(chat,"m_LastFormalReplyAvatar",bridge.avatar);
                Set(chat,"m_LastFormalReplyEnvironment",bridge.environmentRoot);Set(chat,"m_LastFormalReplySession",Get(chat,"m_AgentSessionStartTime"));
                Call(chat,"ArchiveDeviceEvent",new JObject{["lastAction"]=row["scene"]["lastAction"].DeepClone(),["recordedGeneration"]=0});
                Call(chat,"StageFormalObservationFrame",(string)row["frame"],generation);
                model.Decisions.Last().Complete(true,"{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}","accepted-autonomous-conversation");
                Require((bool)Call(chat,"UseBodyReplyPacket",generation) && Call(chat,"ReadMovementReplySemantics",generation)==null,
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
                Set(chat,"m_PendingSelfInspection","Inspection result: microphone connected; no recording operation started.");
                Set(chat,"m_SongCatalogResultPending",true);Set(chat,"m_LastSongCatalogResult","Catalog result: two recordings listed; neither was played.");
                Set(chat,"m_StickyToolFailure","A separate playback request failed; no retry was submitted.");
                int operations=bridge.Controller.OperationRevision;
                serializer.m_DataList=history.ToList();
                foreach(JObject message in row["history"]) serializer.m_DataList.Add(new LLM.SendData((string)message["role"],(string)message["content"]));
                Call(chat,"StageFormalObservationFrame",(string)row["frame"],generation);
                Call(chat,"DispatchFormalStream","[程序感知 tick；不是新的用户发言。]",null,generation,true,(string)row["frame"],true);
                var writer=model.Speech.Last();
                Require(writer.Image==null && writer.Context.Contains((string)row["intention"]) && writer.Context.Contains((string)row["originalRequest"]),
                    "Current intention and historical constraints survive without an irrelevant screenshot.");
                foreach(string marker in new[]{"Inspection result:","Catalog result:","A separate playback request failed"})
                    Require(writer.Context.Contains(marker),"Pending independent outcome is retained: "+marker);
                Require(!writer.Context.Contains("knownObjectHistory") && !writer.Context.Contains("[感知帧") &&
                    Get(chat,"m_StagedFormalObservation")==null,"Actual dispatch does not reconstruct the full historical scene.");
                string directory=Path.Combine(output,(string)row["name"]);Directory.CreateDirectory(directory);
                JObject Export(NevaSceneVisionIntegrationModel.Request sent,string name)
                {
                    var messages=typeof(ChatQW).GetMethod("CreateObservationRequestHistory",Flags|BindingFlags.Static).Invoke(serializer,new object[]{serializer.m_DataList,sent.Image});
                    string wire=(string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages",Flags).Invoke(serializer,new object[]{messages,true,sent.Context,sent.Input,sent.Stage});
                    object[] admission={wire,null};
                    Require((bool)typeof(ChatQW).GetMethod("AdmitSceneSpeechWire",Flags|BindingFlags.Static).Invoke(null,admission),"Whole recorded dialogue and pending cross-domain outcomes fit unchanged ceilings: "+name);
                    File.WriteAllText(Path.Combine(directory,name+"-request.json"),wire);
                    File.WriteAllText(Path.Combine(directory,name+"-context.txt"),sent.Context);
                    return (JObject)admission[1];
                }
                var report=new JObject{["name"]=row["name"],["sourceFrameCharacters"]=((string)row["frame"]).Length,
                    ["historyMessages"]=((JArray)row["history"]).Count,["contextCharacters"]=writer.Context.Length,["writerBudget"]=Export(writer,"writer")};
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
                    foreach(var change in new[]{("m_WorkReviewOpen",(object)true),("m_AgentCurrentRoundIsTick",(object)false),
                        ("m_UserSpeechActiveForAutonomy",(object)true),("m_SceneAutonomyIntention",(object)"new unrelated intention")})
                    {
                        object old=Get(chat,change.Item1);Set(chat,change.Item1,change.Item2);
                        Require(!(bool)Call(chat,"UseBodyReplyPacket",generation),"Stale/changed autonomous owner cannot select compact reporting: "+change.Item1);
                        Set(chat,change.Item1,old);
                    }
                    var attention=(JObject)Get(chat,"m_SceneAutonomyAttention");attention["kind"]="scene";
                    Require(!(bool)Call(chat,"UseBodyReplyPacket",generation),"Scene attention retains the visual answer path.");
                    attention["kind"]="current-user-body";
                    Call(chat,"RememberFirstReplyRequestSemantics",new FirstReplyCompletion{Disposition=FirstReplyDisposition.Tools,RequestKind="mixed"},generation,model);
                    Require(!(bool)Call(chat,"UseBodyReplyPacket",generation),"An independent current question cannot be hidden by autonomous attention.");
                    Call(chat,"CancelRoomSpeechReview","autonomous-conversation-check-finished");
                }
                reports.Add(report);chat.StopAllCoroutines();
            }
            Require(fixture.ToString()==original,"Recorded observed memory and conversation are never mutated by projection.");
        }
        finally { foreach(var item in saved)Set(chat,item.Key,item.Value);foreach(var item in body)Set(bridge,item.Key,item.Value);serializer.m_DataList=history;model.EnableFirstReply=routing;
            recent.Clear();foreach(var item in savedRecent)recent.Enqueue(item);devices.Clear();devices.AddRange(savedDevices); }
        LastDecisionFailureReport["autonomousReplyBudgets"]=reports;
    }
}
