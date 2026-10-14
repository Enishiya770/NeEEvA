using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class RoomSpeechFactScopeRegression
{
    private static IEnumerator CheckQualityFailureReply(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, ChatQW serializer, string output)
    {
        var fixture = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/QualityRecovery.latest-20261013.json"));
        var row = (JObject)fixture["failedFollow"];
        var saved = new[] { "m_SpatialDeviceGoal", "m_SceneActionFact", "m_RoomTaskSequence", "m_RoomTaskSequenceModel",
            "m_RoomTaskSequenceBridge", "m_RoomTaskSequenceAvatar", "m_RoomTaskSequenceEnvironment", "m_WorkReplyOnly",
            "m_WorkIntent", "m_WorkNextStepId", "m_WorkReviewOpen", "m_WorkStatus", "m_WorkNoProgress",
            "m_AgentCurrentRoundIsTick", "m_AgentRunning", "m_PendingSelfInspection", "m_SongCatalogResultPending",
            "m_LastSongCatalogResult", "m_StickyToolFailure", "m_LastFormalReplyReceipt", "m_LastFormalReplyBridge",
            "m_LastFormalReplyAvatar", "m_LastFormalReplyEnvironment", "m_LastFormalReplySession",
            "m_DeviceEventsBridge", "m_DeviceEventsModel", "m_DeviceEventsAvatar", "m_DeviceEventsEnvironment", "m_DeviceEventsSession" }.ToDictionary(n => n, n => Get(chat, n));
        var body = new[] { "actionId", "phase", "reason", "latestDiagnostic", "executionTargetKind" }.ToDictionary(n => n, n => Get(bridge, n));
        var history = serializer.m_DataList.ToList(); var runtimeHistory = model.m_DataList.ToList();
        var recent=(Queue<JObject>)Get(chat,"m_RecentRoomReplies"); var savedRecent=recent.ToArray();
        var devices=(List<JObject>)Get(chat,"m_DeviceEvents"); var savedDevices=devices.ToArray();
        try
        {
            yield return PrepareRoomPlanning(chat, bridge, (string)row["originalRequest"]);
            model.Decisions.Last().Complete(true, "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", "quality-source-fixture");
            int generation = (int)Get(chat, "m_FormalResponseGeneration");
            Call(chat, "RememberFirstReplyRequestSemantics", new FirstReplyCompletion { Disposition = FirstReplyDisposition.Tools, RequestKind = "action" }, generation, model);
            var observation = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", generation);
            Set(chat, "m_FormalResponseGeneration", ++generation); Call(chat, "BindFormalSceneObservation", observation, generation);
            Set(chat, "m_SpatialDeviceGoal", null); Set(chat, "m_SceneActionFact", null);
            Set(chat, "m_WorkReviewOpen", true); Set(chat, "m_WorkStatus", "continue"); Set(chat, "m_WorkNoProgress", false);
            Set(chat, "m_AgentCurrentRoundIsTick", true); Set(chat, "m_AgentRunning", true);
            Set(chat, "m_PendingSelfInspection", null); Set(chat, "m_SongCatalogResultPending", false); Set(chat, "m_StickyToolFailure", null);
            recent.Clear(); devices.Clear();
            foreach(JObject reply in row["history"].Where(x=>(string)x["role"]=="assistant").TakeLast(4))
                recent.Enqueue(new JObject{["publicText"]=reply["content"]});
            var prior=(JObject)row["lastReplyReceipt"].DeepClone(); prior["recordedAtUtc"]=System.DateTime.UtcNow.AddSeconds(-2).ToString("o");
            Set(chat,"m_LastFormalReplyReceipt",prior); Set(chat,"m_LastFormalReplyBridge",bridge); Set(chat,"m_LastFormalReplyAvatar",bridge.avatar);
            Set(chat,"m_LastFormalReplyEnvironment",bridge.environmentRoot); Set(chat,"m_LastFormalReplySession",Get(chat,"m_AgentSessionStartTime"));
            Call(chat,"ArchiveDeviceEvent",new JObject{["lastAction"]=row["scene"]["lastAction"].DeepClone(),["recordedGeneration"]=6});
            string action = (string)row["bodyReceipt"]["actionId"];
            Set(bridge, "actionId", action); Set(bridge, "executionTargetKind", "user");
            Call(bridge, "SetFact", "failed", (string)row["bodyReceipt"]["reason"]);
            var replayReceipt = (JObject)row["bodyReceipt"].DeepClone();
            // This recorded receipt predates explicit failure ownership. Bind its
            // already-recorded failure to its own operation, never the live fixture's.
            var replayTelemetry = (JObject)replayReceipt["streamTelemetry"];
            Require((int?)replayTelemetry["operationRevision"] == (int?)replayReceipt["operationRevision"],
                "Legacy failure telemetry and terminal receipt identify the same operation.");
            replayTelemetry["failureOperationRevision"] = replayReceipt["operationRevision"];
            replayTelemetry["failureAppliesToCurrentOperation"] = true;
            replayTelemetry["failureObservedUtc"] = replayReceipt["observedUtc"];
            Set(bridge, "latestDiagnostic", JsonUtility.FromJson(replayReceipt.ToString(), typeof(ArdyRoomDialogueBridge).GetField("latestDiagnostic", Flags).FieldType));
            Set(chat, "m_RoomTaskIntent", "follow"); Set(chat, "m_RoomTaskActionId", action); Set(chat, "m_RoomTaskDispatch", "attempt-submitted");
            Call(chat, "RecordWorkRoomTaskContext");
            var sequence = new JObject { ["workEpoch"] = (int)Get(chat, "m_WorkEpoch"),
                ["userTurn"] = ((JObject)Call(chat, "ReadMovementReplySemantics", generation))["userTurn"],
                ["request"] = row["originalRequest"], ["steps"] = new JArray(new JObject {
                    ["id"] = "s1", ["kind"] = "follow", ["status"] = "running", ["after"] = new JArray(),
                    ["actionId"] = action }) };
            Set(chat, "m_RoomTaskSequence", sequence); Set(chat, "m_RoomTaskSequenceModel", model); Set(chat, "m_RoomTaskSequenceBridge", bridge);
            Set(chat, "m_RoomTaskSequenceAvatar", bridge.avatar); Set(chat, "m_RoomTaskSequenceEnvironment", bridge.environmentRoot);
            Call(chat, "ReadRoomTaskSequence"); // Let the production ledger consume the actual recorded terminal receipt.
            Require((string)sequence["steps"][0]["status"] == "failed", "Recorded terminal execution closes the attempted step as failed.");
            Set(chat,"m_RoomTaskSequence",null);
            object SingleProposal() => JsonUtility.FromJson("{\"intent\":\"向用户解释失败，并尝试重新跟随\",\"next_step_id\":\"\",\"next_intent_source\":\"current-work\",\"planning_focus\":\"current-user-body\"}",
                typeof(ChatSample).GetNestedType("AutonomyIntentDecision",Flags));
            var single=SingleProposal();
            Call(chat,"SelectWorkContinuationScope",single,null);
            Require((bool)Get(chat,"m_WorkReplyOnly") && (string)Get(single,"planning_focus")=="other" &&
                Call(chat,"CreateWorkRoomStepRequest")==null && !((string)Get(single,"intent")).Contains("尝试重新跟随"),
                "A terminal single action without a step table must deliver its failure rather than reopen movement.");
            int ownerTurn=(int)Get(chat,"m_WorkRoomTaskUserTurn");string ownerAction=(string)Get(chat,"m_WorkRoomTaskActionId");
            try {
                Set(chat,"m_WorkRoomTaskUserTurn",ownerTurn-1);
                Call(chat,"SelectWorkContinuationScope",SingleProposal(),null);
                Require(!(bool)Get(chat,"m_WorkReplyOnly"),"An older user turn's failure cannot veto a new current task.");
                Set(chat,"m_WorkRoomTaskUserTurn",ownerTurn);Set(chat,"m_WorkRoomTaskActionId","different-current-action");
                Call(chat,"SelectWorkContinuationScope",SingleProposal(),null);
                Require(!(bool)Get(chat,"m_WorkReplyOnly"),"Another action's old failure cannot supply the current outcome.");
            } finally {Set(chat,"m_WorkRoomTaskUserTurn",ownerTurn);Set(chat,"m_WorkRoomTaskActionId",ownerAction);}
            LastDecisionFailureReport["singleActionFailureDelivery"]=new JObject{["passed"]=true,["noLedger"]=true,["newTurnProtected"]=true,["differentActionProtected"]=true};
            Set(chat,"m_RoomTaskSequence",sequence);
            var proposal = JsonUtility.FromJson(new JObject { ["intent"] = row["proposedDecision"]["intent"],
                ["next_step_id"] = "s1", ["next_intent_source"] = "current-work", ["planning_focus"] = "current-user-body" }.ToString(),
                typeof(ChatSample).GetNestedType("AutonomyIntentDecision", Flags));
            Call(chat, "SelectWorkContinuationScope", proposal, sequence); Set(chat, "m_WorkIntent", Get(proposal, "intent"));
            Require((bool)Get(chat, "m_WorkReplyOnly") && (string)Get(proposal, "next_step_id") == "" &&
                !((string)Get(proposal, "intent")).Contains((string)row["proposedDecision"]["intent"]),
                "Actual rejected retry cannot remain the writer's effective intention.");
            serializer.m_DataList = history.Where(x => x.role == "system").Concat(row["history"].Select(x => new LLM.SendData((string)x["role"], (string)x["content"]))).ToList();
            model.m_DataList = serializer.m_DataList.ToList();
            Require((bool)Call(chat, "UseWorkResultReplyPacket", generation), "Recorded failed follow selects the actual result producer.");
            int operations = bridge.Controller.OperationRevision, speech = model.Speech.Count;
            Call(chat, "StageFormalObservationFrame", (string)row["frame"], generation);
            Call(chat, "DispatchWorkFeedback", (string)row["frame"], observation?.ImageDataUrl, generation);
            var writer = model.Speech.Last();
            Require(model.Speech.Count == speech + 1 && writer.Image == null && writer.Input == "" &&
                writer.Context.Contains("GenerationFailed") && writer.Context.Contains(action) &&
                !writer.Context.Contains((string)row["proposedDecision"]["intent"]) && bridge.Controller.OperationRevision == operations,
                "Failure delivery keeps execution identity without a retry, stale intent, or duplicate perception frame.");
            var messages = typeof(ChatQW).GetMethod("CreateObservationRequestHistory", Flags | System.Reflection.BindingFlags.Static).Invoke(serializer, new object[] { serializer.m_DataList, null });
            string wire = (string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", Flags).Invoke(serializer,
                new object[] { messages, true, writer.Context, writer.Input, writer.Stage });
            object[] budget = { wire, null };
            Require((bool)typeof(ChatQW).GetMethod("AdmitSceneSpeechWire", Flags | System.Reflection.BindingFlags.Static).Invoke(null, budget), "Full latest failed-follow dialogue fits unchanged budget.");
            Require(wire.Contains("[Deliver the pending execution outcome now]") && !wire.Contains("[Write the answer to the current human request now]"),
                "The final writer instruction cannot reopen the original command after execution failed.");
            string directory = Path.Combine(output, "latest-quality-follow-failure"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "writer-request.json"), wire);
            File.WriteAllText(Path.Combine(directory, "writer-context.txt"), writer.Context);
            File.WriteAllText(Path.Combine(directory, "writer-budget.json"), ((JObject)budget[1]).ToString());
            string candidate = (string)row["candidate"];
            Call(chat, "CompleteRoomSpeech", Buffer(candidate), candidate, generation, true, Call(chat, "CreateObservationReceipt"));
            var review = model.Decisions.Last(); var input = JObject.Parse(review.Input);
            Require((string)input.SelectToken("eventContext.latestExecutionEvent.phase") == "failed" &&
                (string)input.SelectToken("movementReportRequirement.expectedReport") == "incomplete",
                "Failed execution remains an established fact for independent speech review.");
            Require((string)input.SelectToken("currentRoomFacts.execution.boundedRecovery.lastFailureCategory")=="motion-quality" &&
                (bool?)input.SelectToken("currentRoomFacts.execution.boundedRecovery.recovering")==false,
                "Recorded quality failure and inactive recovery survive without inventing an attempted recovery.");
            File.WriteAllText(Path.Combine(directory, "review-input.json"), review.Input);
            File.WriteAllText(Path.Combine(directory, "review-request.json"), review.Wire);
            LastDecisionFailureReport["latestQualityFailureBudget"] = new JObject { ["historyMessages"] = row["history"].Count(),
                ["sourceSha256"] = fixture["sourceSha256"], ["writerBudget"] = (JObject)budget[1] };
            Call(chat, "CancelRoomSpeechReview", "quality-replay-finished"); chat.StopAllCoroutines();
        }
        finally { foreach (var item in saved) Set(chat, item.Key, item.Value); foreach (var item in body) Set(bridge, item.Key, item.Value);
            serializer.m_DataList = history; model.m_DataList = runtimeHistory;
            recent.Clear(); foreach(var reply in savedRecent)recent.Enqueue(reply);
            devices.Clear(); devices.AddRange(savedDevices); }
    }
}
