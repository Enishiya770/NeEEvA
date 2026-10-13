using System;
using System.Collections;
using System.IO;
using System.Linq;
using NeEEvA.Perception;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class RoomSpeechFactScopeRegression
{
    private static IEnumerator CheckContinuationReplyFeedback(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, ChatQW serializer, string output)
    {
        var fixture=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/ReplyContinuation.latest-1843-20261011.json"));
        string original=fixture.ToString(); var reports=new JArray();
        var savedNames=new[]{"m_SceneActionFact","m_SpatialDeviceGoal","m_PendingSelfInspection"};
        var saved=savedNames.ToDictionary(n=>n,n=>Get(chat,n));
        var oldHistory=serializer.m_DataList.ToList();
        try {
            foreach(JObject row in fixture["cases"])
            {
                string request=(string)row["originalRequest"];
                yield return PrepareRoomPlanning(chat,bridge,request);
                model.Decisions.Last().Complete(true,"{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}","continuation-source-fixture");
                int generation=(int)Get(chat,"m_FormalResponseGeneration");
                Call(chat,"RememberFirstReplyRequestSemantics",new FirstReplyCompletion {Disposition=FirstReplyDisposition.Tools,RequestKind="action"},generation,model);
                var observed=(SceneVisionObservation)Call(chat,"GetFormalSceneObservation",generation);
                var factType=typeof(ChatSample).GetNestedType("SceneActionFact",System.Reflection.BindingFlags.NonPublic);
                Set(chat,"m_SceneActionFact",UnityEngine.JsonUtility.FromJson(row["scene"]["lastAction"].ToString(),factType));
                Set(chat,"m_SpatialDeviceGoal",null);
                Set(chat,"m_WorkReviewOpen",true);Set(chat,"m_WorkStatus","continue");
                Set(chat,"m_WorkNoProgress",false);Set(chat,"m_WorkIntent","执行剩余步骤，回到用户身边。");
                Set(chat,"m_WorkContinuations",1);Set(chat,"m_AgentCurrentRoundIsTick",true);
                Set(chat,"m_FormalResponseGeneration",++generation);
                Call(chat,"BindFormalSceneObservation",observed,generation);
                object oldId=Get(bridge,"actionId"),oldPhase=Get(bridge,"phase"),oldReason=Get(bridge,"reason"),oldDiagnostic=Get(bridge,"latestDiagnostic");
                try {
                    string action="continuation-return-"+row["name"];
                    Set(bridge,"actionId",action);Call(bridge,"SetFact","moving","controlled owner-bound return in progress");
                    Set(chat,"m_RoomTaskIntent","approach");Set(chat,"m_RoomTaskActionId",action);
                    Set(chat,"m_RoomTaskDispatch","attempt-submitted");
                    Set(chat,"m_PendingSelfInspection","Unconsumed inspection: the secondary tool failed; do not repeat it.");
                    Call(chat,"RecordWorkRoomTaskContext");
                    serializer.m_DataList.Add(new LLM.SendData("user",request));
                    foreach(bool stress in new[]{false,true})
                    {
                        var scene=(JObject)row["scene"].DeepClone();
                        if(stress)
                        {
                            // Grow only real observed-memory detail; never pad
                            // or truncate mandatory current outcomes/user input.
                            var history=(JArray)scene["objectContext"]["knownObjectHistory"];
                            if(history==null) scene["objectContext"]["knownObjectHistory"]=history=new JArray();
                            var sample=history.OfType<JObject>().FirstOrDefault() ?? new JObject { ["entityId"]="historical-observed" };
                            for(int i=0;i<80;i++) {var item=(JObject)sample.DeepClone();item["entityId"]="observed-growth-"+i;item["modelDescription"]=new string('旧',500);history.Add(item);}
                        }
                        string feedback=(string)Call(chat,"BuildWorkContinuationContext")+"\n"+(string)Call(chat,"BuildRoomTaskReplyFacts")+
                            "\n[视觉关注与行动事实]\n"+scene.ToString(Formatting.None)+
                            "\nUnknown custom constraint: keep all unrelated devices unchanged.";
                        Call(chat,"StageFormalObservationFrame",(string)row["frame"],generation);
                        int speech=model.Speech.Count, operations=bridge.Controller.OperationRevision;
                        Call(chat,"DispatchPlannedWorkRoomReply",feedback,observed?.ImageDataUrl,generation);
                        Require(model.Speech.Count==speech+1,"Actual planned-work reply producer dispatches exactly once.");
                        var sent=model.Speech.Last();
                        Require(sent.Stage==FormalResponseStage.CommittedSpatialReply && sent.Input.Contains("[视觉关注与行动事实]") &&
                            sent.Input.Contains((string)row["scene"]["lastAction"]["actionId"]) && sent.Context.Contains(action),
                            "Regression exercises BOTH compact movement context and full continuation execution feedback, including recorded device outcome.");
                        var historyWire=typeof(ChatQW).GetMethod("CreateObservationRequestHistory",Flags | System.Reflection.BindingFlags.Static).Invoke(serializer,new object[]{serializer.m_DataList,sent.Image});
                        string wire=(string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages",Flags).Invoke(serializer,
                            new object[]{historyWire,true,sent.Context,sent.Input,sent.Stage});
                        object[] admission={wire,null};
                        Require((bool)typeof(ChatQW).GetMethod("AdmitSceneSpeechWire",Flags | System.Reflection.BindingFlags.Static).Invoke(null,admission),
                            "Complete actual continuation serializer admits latest recorded feedback load and bounded memory growth: "+admission[1]);
                        Require(wire.Contains(request) && wire.Contains(action) && wire.Contains("secondary tool failed") &&
                            wire.Contains("keep all unrelated devices unchanged") && bridge.Controller.OperationRevision==operations,
                            "Projection preserves full request, current action, pending non-spatial result and unknown constraints without submitting motion.");
                        object[] projection={sent.Context,sent.Input,0,null};
                        string projected=(string)typeof(ChatQW).GetMethod("ProjectReplyInputs",Flags | System.Reflection.BindingFlags.Static).Invoke(null,projection);
                        Require((bool?)((JObject)projection[3])["feedbackIncludedInProjection"]==true && projected.Length<sent.Context.Length+sent.Input.Length,
                            "Feedback participates in field-level projection instead of bypassing it as a raw system block.");
                        string directory=Path.Combine(output,"continuation-"+row["name"]+(stress?"-growth":""));Directory.CreateDirectory(directory);
                        File.WriteAllText(Path.Combine(directory,"context.txt"),sent.Context);File.WriteAllText(Path.Combine(directory,"feedback.txt"),sent.Input);
                        File.WriteAllText(Path.Combine(directory,"request.json"),wire);
                        reports.Add(new JObject { ["name"]=row["name"],["stressObservedMemory"]=stress,["budget"]=(JObject)admission[1],["projection"]=(JObject)projection[3] });
                        // Unknown protected feedback cannot disappear in order
                        // to force admission, including on a preprojected input.
                        string protectedWire=(string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages",Flags).Invoke(serializer,
                            new object[]{historyWire,true,projected,"Protected unknown outcome: "+new string('密',65000),sent.Stage});
                        object[] rejected={protectedWire,null};
                        Require(!(bool)typeof(ChatQW).GetMethod("AdmitSceneSpeechWire",Flags | System.Reflection.BindingFlags.Static).Invoke(null,rejected) && protectedWire.Contains(new string('密',65000)),
                            "New protected feedback cannot hide behind an already-projected marker or be silently dropped.");
                        Call(chat,"CancelRoomSpeechReview","continuation-budget-fixture-finished");
                    }
                } finally {Set(bridge,"actionId",oldId);Set(bridge,"phase",oldPhase);Set(bridge,"reason",oldReason);Set(bridge,"latestDiagnostic",oldDiagnostic);}
                serializer.m_DataList.Add(new LLM.SendData("assistant",(string)row["priorReceipt"]["publicText"]));
            }
            Require(fixture.ToString()==original,"Request projection does not alter recorded memory or outcomes.");
        } finally {foreach(var item in saved)Set(chat,item.Key,item.Value);serializer.m_DataList=oldHistory;}
        LastDecisionFailureReport["latestContinuationFeedbackBudgets"]=reports;
        yield return CheckNarrowMovementContinuation(chat, bridge, model, serializer, output);
        yield return CheckWorkResultContinuation(chat, bridge, model, serializer, output);
        yield return CheckAutonomousReply(chat, bridge, model, serializer, output);
    }

    private static IEnumerator CheckNarrowMovementContinuation(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, ChatQW serializer, string output)
    {
        var fixture = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/ReplyContinuation.latest-20261012.json"));
        var reports = new JArray(); var history = serializer.m_DataList.ToList();
        string[] names = { "m_SpatialDeviceGoal", "m_RoomTaskSequence", "m_RoomTaskSequenceModel", "m_RoomTaskSequenceBridge",
            "m_RoomTaskSequenceAvatar", "m_RoomTaskSequenceEnvironment", "m_PendingSelfInspection" };
        var saved = names.ToDictionary(n => n, n => Get(chat, n));
        try
        {
            foreach (JObject row in fixture["cases"])
            {
                string request = (string)row["originalRequest"];
                yield return PrepareRoomPlanning(chat, bridge, request);
                model.Decisions.Last().Complete(true, "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", "narrow-movement-fixture");
                int generation = (int)Get(chat, "m_FormalResponseGeneration");
                Call(chat, "RememberFirstReplyRequestSemantics", new FirstReplyCompletion { Disposition = FirstReplyDisposition.Tools, RequestKind = "action" }, generation, model);
                var observed = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", generation);
                Set(chat, "m_SpatialDeviceGoal", null); Set(chat, "m_WorkReviewOpen", true); Set(chat, "m_WorkNoProgress", false);
                Set(chat, "m_AgentCurrentRoundIsTick", true); Set(chat, "m_FormalResponseGeneration", ++generation);
                Call(chat, "BindFormalSceneObservation", observed, generation);
                string action = "latest-return-" + row["name"];
                object oldId = Get(bridge, "actionId"), oldPhase = Get(bridge, "phase"), oldReason = Get(bridge, "reason"), oldDiagnostic = Get(bridge, "latestDiagnostic");
                try
                {
                    Set(bridge, "actionId", action); Call(bridge, "SetFact", "moving", "owner-bound return in progress");
                    Set(chat, "m_RoomTaskIntent", "approach"); Set(chat, "m_RoomTaskActionId", action); Set(chat, "m_RoomTaskDispatch", "attempt-submitted");
                    Call(chat, "RecordWorkRoomTaskContext");
                    var sequence = (JObject)row["taskSequence"].DeepClone();
                    sequence["workEpoch"] = (int)Get(chat, "m_WorkEpoch"); sequence["userTurn"] = ((JObject)Call(chat, "ReadMovementReplySemantics", generation))["userTurn"];
                    sequence["request"] = request;
                    sequence["steps"][0]["status"] = "completed"; sequence["steps"][0]["receipt"] = row["resultFacts"].DeepClone();
                    sequence["steps"][1]["status"] = "running"; sequence["steps"][1]["actionId"] = action;
                    Set(chat, "m_RoomTaskSequence", sequence); Set(chat, "m_RoomTaskSequenceModel", model); Set(chat, "m_RoomTaskSequenceBridge", bridge);
                    Set(chat, "m_RoomTaskSequenceAvatar", bridge.avatar); Set(chat, "m_RoomTaskSequenceEnvironment", bridge.environmentRoot);
                    Call(chat, "ArchiveDeviceEvent", (JObject)row["resultFacts"]);
                    serializer.m_DataList = history.ToList();
                    foreach (JObject message in row["history"]) serializer.m_DataList.Add(new LLM.SendData((string)message["role"], (string)message["content"]));
                    Set(chat, "m_PendingSelfInspection", "Pending non-movement result must remain unread.");
                    Call(chat, "StageFormalObservationFrame", (string)row["frame"], generation);
                    int before = model.Speech.Count, operations = bridge.Controller.OperationRevision;
                    Call(chat, "DispatchPlannedMovementRoomReply", observed?.ImageDataUrl, generation);
                    var sent = model.Speech.Last();
                    Require(model.Speech.Count == before + 1 && sent.Stage == FormalResponseStage.CommittedSpatialReply && sent.Input == "",
                        "Typed continuation producer adds no duplicate perception/feedback document.");
                    Require(sent.Context.Contains(action) && sent.Context.Contains(request) && sent.Context.Contains((string)row.SelectToken("resultFacts.lastAction.actionId")),
                        "Latest whole request, same-action live receipt and earlier actual device result survive.");
                    Require(!sent.Context.Contains("knownObjectHistory") && !sent.Context.Contains("sceneSearch") && Get(chat, "m_StagedFormalObservation") == null,
                        "Observed object/search details are not rebuilt as a movement report and staged frame is consumed without acknowledgement.");
                    var wireHistory = typeof(ChatQW).GetMethod("CreateObservationRequestHistory", Flags | System.Reflection.BindingFlags.Static).Invoke(serializer, new object[] { serializer.m_DataList, sent.Image });
                    string wire = (string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", Flags).Invoke(serializer,
                        new object[] { wireHistory, true, sent.Context, sent.Input, sent.Stage });
                    object[] admission = { wire, null };
                    Require((bool)typeof(ChatQW).GetMethod("AdmitSceneSpeechWire", Flags | System.Reflection.BindingFlags.Static).Invoke(null, admission), "Latest complete movement wire fits unchanged admission ceilings.");
                    var messages = (JArray)JObject.Parse(wire)["messages"];
                    Require(!((string)messages.Last["content"]).Contains("\"replyFacts\"") && bridge.Controller.OperationRevision == operations,
                        "Final writing instruction does not duplicate bound facts or execute movement.");
                    Require((string)Get(chat, "m_PendingSelfInspection") == "Pending non-movement result must remain unread.", "Narrow report does not acknowledge unsent unrelated observations.");
                    string directory = Path.Combine(output, "latest-movement-" + row["name"]); Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "request.json"), wire); File.WriteAllText(Path.Combine(directory, "context.txt"), sent.Context);
                    reports.Add(new JObject { ["name"] = row["name"], ["sourceLine"] = row["sourceLine"], ["budget"] = (JObject)admission[1],
                        ["sourceFrameCharacters"] = ((string)row["frame"]).Length, ["recordedHistoryMessages"] = ((JArray)row["history"]).Count,
                        ["recordedHistoryCharacters"] = row["history"].Sum(m => ((string)m["content"]).Length),
                        ["contextCharacters"] = sent.Context.Length, ["feedbackCharacters"] = sent.Input.Length });
                    Call(chat, "CancelRoomSpeechReview", "latest-narrow-regression-complete");
                }
                finally { Set(bridge, "actionId", oldId); Set(bridge, "phase", oldPhase); Set(bridge, "reason", oldReason); Set(bridge, "latestDiagnostic", oldDiagnostic); }
            }
        }
        finally { foreach (var item in saved) Set(chat, item.Key, item.Value); serializer.m_DataList = history; }
        LastDecisionFailureReport["latestMovementProducerBudgets"] = reports;
    }
}
