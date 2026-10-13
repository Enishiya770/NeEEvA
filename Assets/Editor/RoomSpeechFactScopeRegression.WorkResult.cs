using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;

public static partial class RoomSpeechFactScopeRegression
{
    private static void CheckIndependentResultDependencies(ChatSample chat, ArdyRoomDialogueBridge bridge)
    {
        int operation = bridge.Controller.OperationRevision, epoch = (int)Get(chat, "m_SceneActionEpoch");
        object spatialAnswer = Get(chat, "m_RoomTaskSpatialAnswer");
        object replyOnly = Get(chat, "m_WorkReplyOnly");
        try
        {
            Set(chat, "m_RoomTaskSpatialAnswer", false);
            Set(chat, "m_WorkReplyOnly", false);
            foreach (string intent in new[] { "set-object-state", "look-at" })
            {
                var snapshot = Call(chat, "CaptureRoomSpeechSnapshot", bridge, null);
                var sceneScope = Call(chat, "ResolveRoomSpeechDependencies", intent, "scene-action");
                Set(bridge.Controller, "<OperationRevision>k__BackingField", operation + 1);
                Require(((List<string>)Call(chat, "ChangedRoomSpeechFacts", snapshot, sceneScope)).Count == 0,
                    "An unrelated body operation does not revoke a stable result: " + intent);
                var movementScope = Call(chat, "ResolveRoomSpeechDependencies", "approach", "user-target");
                Require(((List<string>)Call(chat, "ChangedRoomSpeechFacts", snapshot, movementScope)).Contains("userTaskOperation"),
                    "Current movement still depends on its actual body operation.");
                Set(chat, "m_SceneActionEpoch", epoch + 1);
                Require(((List<string>)Call(chat, "ChangedRoomSpeechFacts", snapshot, sceneScope)).Contains("sceneActionEpoch"),
                    "Replacing the scene action still invalidates its result.");
                Set(chat, "m_SceneActionEpoch", epoch);
                Set(bridge.Controller, "<OperationRevision>k__BackingField", operation);
            }
        }
        finally { Set(chat, "m_SceneActionEpoch", epoch); Set(chat, "m_RoomTaskSpatialAnswer", spatialAnswer); Set(chat, "m_WorkReplyOnly", replyOnly); Set(bridge.Controller, "<OperationRevision>k__BackingField", operation); }
    }

    private static IEnumerator CheckWorkResultContinuation(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, ChatQW serializer, string output)
    {
        var fixture = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/WorkResult.latest-20261013.json"));
        string original = fixture.ToString(), request = (string)fixture["originalRequest"];
        string[] names = { "m_SceneActionFact", "m_SpatialDeviceGoal", "m_RoomTaskSequence", "m_RoomTaskSequenceModel",
            "m_RoomTaskSequenceBridge", "m_RoomTaskSequenceAvatar", "m_RoomTaskSequenceEnvironment", "m_WorkReplyOnly",
            "m_PendingSelfInspection", "m_SongCatalogResultPending", "m_LastSongCatalogResult", "m_StickyToolFailure", "m_AgentRunning" };
        var saved = names.ToDictionary(n => n, n => Get(chat, n)); var oldHistory = serializer.m_DataList.ToList();
        var oldBody = new[] { "actionId", "phase", "reason", "latestDiagnostic", "executionTargetKind" }.ToDictionary(n => n, n => Get(bridge, n));
        try
        {
            yield return PrepareRoomPlanning(chat, bridge, request);
            model.Decisions.Last().Complete(true, "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", "recorded-work-result-fixture");
            int generation = (int)Get(chat, "m_FormalResponseGeneration");
            Call(chat, "RememberFirstReplyRequestSemantics", new FirstReplyCompletion { Disposition = FirstReplyDisposition.Tools, RequestKind = "action" }, generation, model);
            var observed = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", generation);
            Set(chat, "m_FormalResponseGeneration", ++generation); Call(chat, "BindFormalSceneObservation", observed, generation);
            Set(chat, "m_WorkReviewOpen", true); Set(chat, "m_WorkStatus", "continue"); Set(chat, "m_WorkReplyOnly", true);
            Set(chat, "m_WorkNoProgress", false); Set(chat, "m_AgentCurrentRoundIsTick", true); Set(chat, "m_AgentRunning", true);
            Set(chat, "m_SpatialDeviceGoal", null);
            var factType = typeof(ChatSample).GetNestedType("SceneActionFact", System.Reflection.BindingFlags.NonPublic);
            Set(chat, "m_SceneActionFact", UnityEngine.JsonUtility.FromJson(fixture["scene"]["lastAction"].ToString(), factType));
            string primary = (string)fixture["scene"]["lastAction"]["actionId"], action = (string)fixture["bodyReceipt"]["actionId"];
            Set(chat, "m_RoomTaskIntent", "set-object-state"); Set(chat, "m_RoomTaskActionId", primary);
            Set(chat, "m_RoomTaskDispatch", "performed"); Call(chat, "RecordWorkRoomTaskContext");
            Set(bridge, "actionId", action); Set(bridge, "executionTargetKind", "user");
            Call(bridge, "SetFact", "goal-not-reached", "Recorded return failed required distance after two bounded route updates.");
            var sequence = (JObject)fixture["taskSequence"].DeepClone();
            sequence["workEpoch"] = (int)Get(chat, "m_WorkEpoch");
            sequence["userTurn"] = ((JObject)Call(chat, "ReadMovementReplySemantics", generation))["userTurn"];
            Set(chat, "m_RoomTaskSequence", sequence); Set(chat, "m_RoomTaskSequenceModel", model); Set(chat, "m_RoomTaskSequenceBridge", bridge);
            Set(chat, "m_RoomTaskSequenceAvatar", bridge.avatar); Set(chat, "m_RoomTaskSequenceEnvironment", bridge.environmentRoot);
            Set(chat, "m_PendingSelfInspection", "Inspection result: playback endpoint is connected, no playback submitted.");
            Set(chat, "m_SongCatalogResultPending", true); Set(chat, "m_LastSongCatalogResult", "Catalog result: two matching recordings, neither played.");
            Set(chat, "m_StickyToolFailure", "A separate playback request failed; do not claim success or retry.");
            serializer.m_DataList = oldHistory.ToList();
            foreach (JObject message in fixture["history"]) serializer.m_DataList.Add(new LLM.SendData((string)message["role"], (string)message["content"]));
            CheckIndependentResultDependencies(chat, bridge);
            Require((bool)Call(chat, "UseWorkResultReplyPacket", generation), "Typed pending diagnosis selects the work-result producer.");
            var requirement = (JObject)Call(chat, "CurrentSpeechMovementRequirement", generation);
            Require((string)requirement["expectedReport"] == "incomplete" && requirement.ToString().Contains(action),
                "A prior device primary cannot hide the current failed body step from speech review.");
            Call(chat, "StageFormalObservationFrame", (string)fixture["frame"], generation);
            int speech = model.Speech.Count, operations = bridge.Controller.OperationRevision;
            Call(chat, "DispatchWorkFeedback", (string)fixture["frame"], observed?.ImageDataUrl, generation);
            var sent = model.Speech.Last();
            Require(model.Speech.Count == speech + 1 && sent.Stage == FormalResponseStage.CommittedSpatialReply && sent.Input == "" && sent.Image == null,
                "Actual diagnostic dispatch sends one fact packet without a duplicate frame or irrelevant image.");
            Require(sent.Context.Contains(request) && sent.Context.Contains(action) && sent.Context.Contains(primary) &&
                sent.Context.Contains("goal-not-reached") && sent.Context.Contains("1.443114") && sent.Context.Contains("approachRetargetCount"),
                "Whole original request, completed device, measured failed step and bounded recovery survive.");
            Require(!sent.Context.Contains("knownObjectHistory") && !sent.Context.Contains("[感知帧") && Get(chat, "m_StagedFormalObservation") == null,
                "Historical object payload is not recopied into an already-settled result.");
            foreach (string marker in new[] { "Inspection result:", "Catalog result:", "A separate playback request failed" })
                Require(sent.Context.Contains(marker), "Pending cross-domain result remains available: " + marker);
            var wireHistory = typeof(ChatQW).GetMethod("CreateObservationRequestHistory", Flags | System.Reflection.BindingFlags.Static).Invoke(serializer, new object[] { serializer.m_DataList, sent.Image });
            string wire = (string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", Flags).Invoke(serializer,
                new object[] { wireHistory, true, sent.Context, sent.Input, sent.Stage });
            object[] admission = { wire, null };
            Require((bool)typeof(ChatQW).GetMethod("AdmitSceneSpeechWire", Flags | System.Reflection.BindingFlags.Static).Invoke(null, admission),
                "Complete five-turn production load plus cross-domain pending results fits unchanged ceilings.");
            Require(bridge.Controller.OperationRevision == operations && (bool)Get(chat, "m_SongCatalogResultPending"),
                "Diagnostic construction does not execute actions or acknowledge undelivered results.");
            string directory = Path.Combine(output, "latest-work-result"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "request.json"), wire); File.WriteAllText(Path.Combine(directory, "context.txt"), sent.Context);
            LastDecisionFailureReport["latestWorkResultBudget"] = new JObject { ["budget"] = (JObject)admission[1],
                ["sourceFrameCharacters"] = ((string)fixture["frame"]).Length, ["recordedHistoryMessages"] = ((JArray)fixture["history"]).Count,
                ["contextCharacters"] = sent.Context.Length, ["sourceSha256"] = fixture["sourceSha256"] };
            var receipt = Call(chat, "CreateObservationReceipt");
            Call(chat, "CompleteRoomSpeech", Buffer("戻る動作は完了していないわ。"), "戻る動作は完了していないわ。", generation, true, receipt);
            var review = model.Decisions.Last();
            Require(review.Image == null && (string)JObject.Parse(review.Input).SelectToken("movementReportRequirement.expectedReport") == "incomplete",
                "Actual diagnostic review uses current failed-step evidence without waiting for a new camera frame.");
            File.WriteAllText(Path.Combine(directory, "review-input.json"), review.Input);
            review.Complete(false, "", "controlled-review-service-failure");
            Require(model.m_DataList.Last().content.Contains("今回の移動は未完了") &&
                (string)Get(chat, "m_PendingSelfInspection") != "" && (bool)Get(chat, "m_SongCatalogResultPending"),
                "Failed review reports the owned return failure while preserving undelivered cross-domain results.");
            Set(chat, "m_LastSongCatalogResult", "Newer catalog result, not included in the old packet.");
            Call(chat, "AcknowledgeObservations", receipt, generation, "Delivered original result packet.");
            Require((bool)Get(chat, "m_SongCatalogResultPending") && (string)Get(chat, "m_PendingSelfInspection") == "",
                "Acknowledgement clears only included unchanged results; a newer cross-domain result survives.");
            // A separate user question still uses the full answer/evidence path.
            Set(chat, "m_WorkStatus", "continue");
            Call(chat, "RememberFirstReplyRequestSemantics", new FirstReplyCompletion { Disposition = FirstReplyDisposition.Tools, RequestKind = "mixed" }, generation, model);
            Require(!(bool)Call(chat, "UseWorkResultReplyPacket", generation), "Mixed answer obligations cannot be dropped by result-only projection.");
            Require(fixture.ToString() == original, "Projection never mutates recorded observations or memory.");
            Call(chat, "CancelRoomSpeechReview", "recorded-work-result-fixture-finished");
        }
        finally { foreach (var item in saved) Set(chat, item.Key, item.Value); foreach (var item in oldBody) Set(bridge, item.Key, item.Value); serializer.m_DataList = oldHistory; }
    }
}
