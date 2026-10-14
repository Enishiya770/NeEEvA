using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class RoomSpeechFactScopeRegression
{
    public static IEnumerator RunPrivatePreflightChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        checks=0;LastDecisionFailureReport=new JObject{["status"]="running"};
        var host=new GameObject("Private production serializer");host.SetActive(false);
        var serializer=host.AddComponent<ChatQW>();NevaSceneVisionIntegrationRegression.ConfigureActualRoleSerializer(serializer);
        try { yield return CheckPrivatePreflight(chat,bridge,model,serializer,output);
            LastDecisionFailureReport["checks"]=checks;LastDecisionFailureReport["status"]="passed";
        } finally { File.WriteAllText(Path.Combine(output,"private-preflight-report.json"),LastDecisionFailureReport.ToString());UnityEngine.Object.DestroyImmediate(host); }
    }

    private static IEnumerator CheckPrivatePreflight(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, ChatQW serializer, string output)
    {
        var fixture = PrivatePreflightRegression.Fixture(); var reports = new JArray();
        var saved = new[] { "m_SpatialDeviceGoal", "m_AutonomousReplyOwner", "m_SpatialGoalTerminalReplyGeneration",
            "m_SceneActionFact", "m_LastFormalReplyReceipt", "m_WorkReviewOpen", "m_WorkStatus", "m_PendingSelfInspection",
            "m_SongCatalogResultPending", "m_LastSongCatalogResult", "m_UserSpeechActiveForAutonomy",
            "m_WorkRoomTaskEpoch", "m_WorkRoomTaskUserTurn", "m_WorkRoomTaskActionId", "m_WorkRoomTaskIntent",
            "m_RoomTaskActionId", "m_RoomTaskSequence", "m_RoomTaskSequenceModel", "m_RoomTaskSequenceBridge",
            "m_RoomTaskSequenceAvatar", "m_RoomTaskSequenceEnvironment", "m_LastFormalReplyBridge", "m_LastFormalReplyAvatar",
            "m_LastFormalReplyEnvironment", "m_LastFormalReplySession", "m_TextOutputDrainedGeneration", "m_LastAIMsgPlain" }.ToDictionary(n=>n,n=>Get(chat,n));
        var savedBody=new[]{"actionId","phase","reason","latestDiagnostic","executionTargetKind","lastAttemptActionId","lastAttemptPhase","lastAttemptReason","lastAttemptTargetRevision","lastAttemptTime"}.ToDictionary(n=>n,n=>Get(bridge,n));
        Vector3 oldRoot=bridge.avatar.transform.position,oldUser=bridge.userHead.position;Quaternion oldRotation=bridge.avatar.transform.rotation;
        NeEEvA.Perception.SceneObjectLampCatalog.EnsureRegistered(bridge.environmentRoot,out var device,out _);bool oldPower=device.ReadState().on;
        var history = serializer.m_DataList.ToList(); string memory=serializer.TrailingContext;
        try {
            foreach (JObject row in fixture["cases"])
            {
                yield return PrepareRoomPlanning(chat,bridge,(string)row["request"],(string)row["inputEvidence"]);
                model.Decisions.Last().Complete(true,"{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}","controlled-preflight-source");
                chat.StopAllCoroutines(); Call(chat,"ClearScheduledAgentWake");
                var goal=PrivatePreflightRegression.Goal(chat,bridge,model,(JObject)row["goal"]);
                Set(chat,"m_SpatialDeviceGoal",goal); Set(chat,"m_AutonomousReplyOwner",null);
                Set(chat,"m_SpatialGoalTerminalReplyGeneration",Get(chat,"m_FormalResponseGeneration"));
                Set(chat,"m_SceneActionFact",JsonUtility.FromJson(row["sceneAction"].ToString(),typeof(ChatSample).GetField("m_SceneActionFact",Flags).FieldType));
                string action=(string)row["bodyCompletion"]["actionId"];
                Vector3 Vector(JToken v)=>new Vector3((float)v["x"],(float)v["y"],(float)v["z"]);
                var ground=Vector(row["bodyCompletion"]["avatarGround"]);
                bridge.avatar.transform.position=new Vector3(ground.x,oldRoot.y,ground.z);
                bridge.userHead.position=Vector(row["bodyCompletion"]["userHead"]);
                var toward=bridge.userHead.position-bridge.avatar.transform.position;toward.y=0;
                bridge.avatar.transform.rotation=Quaternion.LookRotation(toward);
                Set(bridge,"actionId",action);Set(bridge,"executionTargetKind","user");
                Set(bridge,"lastAttemptActionId",action);Set(bridge,"lastAttemptPhase","arrived");
                Set(bridge,"lastAttemptReason",(string)row["bodyCompletion"]["reason"]);
                Set(bridge,"lastAttemptTargetRevision",bridge.ReadCurrentTargetEvidence().currentTargetRevision);
                Set(bridge,"lastAttemptTime",Time.realtimeSinceStartup);
                Call(bridge,"SetFact","arrived",(string)row["bodyCompletion"]["reason"]);
                Set(bridge,"latestDiagnostic",JsonUtility.FromJson(row["bodyCompletion"].ToString(),typeof(ArdyRoomDialogueBridge).GetField("latestDiagnostic",Flags).FieldType));
                int userTurn=(int)Get(chat,"m_ActionConstraints").GetType().GetProperty("CurrentUserTurn").GetValue(Get(chat,"m_ActionConstraints"));
                Set(chat,"m_WorkRoomTaskEpoch",Get(chat,"m_WorkEpoch"));Set(chat,"m_WorkRoomTaskUserTurn",userTurn);
                Set(chat,"m_WorkRoomTaskActionId",action);Set(chat,"m_WorkRoomTaskIntent","approach");Set(chat,"m_RoomTaskActionId",action);
                Set(chat,"m_RoomTaskSequence",new JObject{["workEpoch"]=(int)Get(chat,"m_WorkEpoch"),["userTurn"]=userTurn,["request"]=row["request"],
                    ["steps"]=new JArray(new JObject{["id"]="device",["kind"]="set-object-state",["status"]="completed",["after"]=new JArray(),["receipt"]=row["goal"]["completionEvidence"].DeepClone()},
                        new JObject{["id"]="return",["kind"]="approach",["status"]="completed",["after"]=new JArray("device"),["actionId"]=action,
                            ["receipt"]=new JObject{["actionId"]=action,["phase"]="arrived",["reason"]=row["bodyCompletion"]["reason"]}})});
                Set(chat,"m_RoomTaskSequenceModel",model);Set(chat,"m_RoomTaskSequenceBridge",bridge);
                Set(chat,"m_RoomTaskSequenceAvatar",bridge.avatar);Set(chat,"m_RoomTaskSequenceEnvironment",bridge.environmentRoot);
                var receipt=(JObject)row["receipt"].DeepClone();
                receipt["sourceGeneration"]=receipt["generation"].DeepClone();receipt["generation"]=(int)Get(chat,"m_FormalResponseGeneration");
                Set(chat,"m_LastFormalReplyReceipt",receipt);Set(chat,"m_LastAIMsgPlain",(string)receipt["publicText"]);
                Set(chat,"m_LastFormalReplyBridge",bridge);Set(chat,"m_LastFormalReplyAvatar",bridge.avatar);
                Set(chat,"m_LastFormalReplyEnvironment",bridge.environmentRoot);Set(chat,"m_LastFormalReplySession",Get(chat,"m_AgentSessionStartTime"));
                Set(chat,"m_TextOutputDrainedGeneration",Get(chat,"m_FormalResponseGeneration"));
                Set(chat,"m_WorkReviewOpen",true); Set(chat,"m_WorkStatus","substep-result-pending");
                Set(chat,"m_UserSpeechActiveForAutonomy",true); // Actual failed gen7 path selects ordinary autonomy while the user is speaking.
                Require(!(bool)Call(chat,"ShouldReviewUserWork"),"Speaking input selects the real ordinary private path even with an open work ledger.");
                serializer.m_DataList=history.Where(x=>x.role=="system").Concat(row["history"].Select(x=>new LLM.SendData((string)x["role"],(string)x["content"]))).ToList();
                serializer.TrailingContext=model.TrailingContext;
                NeEEvA.Perception.SceneVisionObservation refreshed=null;
                Call(chat,"RequestSceneObservation","recorded-completion-rebound-view",new Action<NeEEvA.Perception.SceneVisionObservation>(x=>refreshed=x));
                yield return Wait(()=>refreshed!=null,"Fresh isolated view after rebinding recorded completed pose and device state");
                string sourceGoal=((JObject)Call(chat,"BuildSpatialGoalContext")).ToString();
                foreach (string variant in new[] { "", "-cross-domain", "-microphone-task" })
                {
                    bool crossDomain=variant!="";
                    if(variant=="-microphone-task") {
                        const string question="检查麦克风连接情况和录音目录，告诉我结果，不要开始录音、播放或移动。";
                        Call(chat,"SetCurrentUserInput",question,question);Call(chat,"RecordAcceptedMotionUserTurn",question);
                        Set(chat,"m_SpatialDeviceGoal",PrivatePreflightRegression.Goal(chat,bridge,model,(JObject)row["goal"]));
                        Set(chat,"m_WorkReviewOpen",true);Set(chat,"m_WorkStatus","inspection-result-pending");
                        Set(chat,"m_UserSpeechActiveForAutonomy",true);
                        serializer.m_DataList.Add(new LLM.SendData("user",question));
                        sourceGoal=((JObject)Call(chat,"BuildSpatialGoalContext")).ToString();
                    }
                    Set(chat,"m_PendingSelfInspection",crossDomain?"Inspection: microphone connected, no recording started; do not delete any recording.":"");
                    Set(chat,"m_SongCatalogResultPending",crossDomain);
                    Set(chat,"m_LastSongCatalogResult",crossDomain?"Catalog: two pending recordings; no playback or edit performed.":"");
                    string frame=(string)Call(chat,"BuildAutonomyIntentFrame","scene-action-facts-changed");
                    string prompt=(string)Call(chat,"BuildOrdinaryAutonomyIntentProbePrompt","scene-action-facts-changed",frame)+
                        (string)Call(chat,"BuildWorkReviewObservationContext",chat.SceneVisionCapture.LatestObservation);
                    string wire=(string)typeof(ChatQW).GetMethod("BuildEphemeralRequestJsonCore",Flags).Invoke(serializer,
                        new object[]{prompt,chat.SceneVisionCapture.LatestObservation.ImageDataUrl,1,false,true});
                    object[] budget={wire,null};
                    Require((bool)typeof(ChatQW).GetMethod("AdmitAutonomyIntentWire",Flags).Invoke(serializer,budget),"Complete actual private wire fits unchanged 24000/32192 budgets: "+row["name"]);
                    var messages=(JArray)JObject.Parse(wire)["messages"];
                    foreach(JObject message in row["history"])
                        Require(messages.Any(x=>(string)x["role"]==(string)message["role"] && (string)x["content"]==(string)message["content"]),"All recorded dialogue is retained verbatim.");
                    Require(prompt.Contains((string)row["request"]) && prompt.Contains("completionEvidence") && prompt.Contains("satisfied") && prompt.Contains(action) && prompt.Contains("arrived"),"Complete request, retained device completion and actual return receipt survive: "+row["name"]);
                    Require(!prompt.Contains("knownObjectHistory") && !prompt.Contains("observedViews"),"Private producer does not expand geometry/history intended for the next planner.");
                    Require(!crossDomain || prompt.Contains("do not delete any recording") && prompt.Contains("no playback or edit performed"),"Cross-domain pending result and restriction survive unchanged.");
                    Require(sourceGoal==((JObject)Call(chat,"BuildSpatialGoalContext")).ToString(),"Private projection does not erase completed goal, search history or observations.");
                    string name=(string)row["name"]+variant;
                    string directory=Path.Combine(output,"private-preflight",name);Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory,"request.json"),wire);File.WriteAllText(Path.Combine(directory,"prompt.txt"),prompt);
                    File.WriteAllText(Path.Combine(directory,"budget.json"),((JObject)budget[1]).ToString());
                    reports.Add(new JObject{["name"]=name,["historyMessages"]=row["history"].Count(),["budget"]=(JObject)budget[1]});
                }
                serializer.m_DataList.Add(new LLM.SendData("system",new string('界',25000)));
                string oversized=(string)typeof(ChatQW).GetMethod("BuildEphemeralRequestJsonCore",Flags).Invoke(serializer,new object[]{
                    (string)Call(chat,"BuildOrdinaryAutonomyIntentProbePrompt","scheduled",(string)Call(chat,"BuildAutonomyIntentFrame","scheduled")),null,1,false,true});
                object[] rejected={oversized,null};
                Require(!(bool)typeof(ChatQW).GetMethod("AdmitAutonomyIntentWire",Flags).Invoke(serializer,rejected) && oversized.Contains(new string('界',25000)),
                    "Oversized authored mandatory constraints remain literal and reject locally, without a larger limit or silent truncation.");
            }
        } finally { foreach(var item in saved)Set(chat,item.Key,item.Value);foreach(var item in savedBody)Set(bridge,item.Key,item.Value);
            bridge.avatar.transform.position=oldRoot;bridge.avatar.transform.rotation=oldRotation;bridge.userHead.position=oldUser;
            device.TrySetPower("isolated-restore-"+Guid.NewGuid().ToString("N"),oldPower,out _,out _);
            serializer.m_DataList=history;serializer.TrailingContext=memory; }
        LastDecisionFailureReport["privatePreflightBudgets"]=reports;
    }
}
