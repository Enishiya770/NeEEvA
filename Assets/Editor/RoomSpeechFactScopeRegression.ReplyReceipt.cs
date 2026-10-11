using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class RoomSpeechFactScopeRegression
{
    private static IEnumerator RunReplyReceiptChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        var results = new JArray();
        var localize = typeof(ChatSample).GetMethod("LocalizeRoomFallback", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (string intent in new[] { "approach", "follow" })
            foreach (string phase in new[] { "preparing", "generating", "moving", "follow-waiting", "follow-paused",
                "follow-recovering", "follow-replanning", "nav-recovering", "returning", "failed", "blocked", "cancelled" })
            {
                var facts = new JObject { ["execution"] = new JObject { ["actionId"] = "owned", ["targetKind"] = "user", ["phase"] = phase } };
                string text = RoomMovementResultPolicy.Fallback(facts, intent, "owned");
                Require(text != null && RoomMovementResultPolicy.Fallback(facts, intent, "replacement") == null,
                    "Movement receipt requires its actual action ID: " + phase);
                var requirement = RoomMovementResultPolicy.Requirement(facts, intent, "owned");
                string expectedReport = (string)requirement["expectedReport"];
                Require((bool)requirement["required"] && !RoomMovementResultPolicy.ReviewReports("{\"resultReport\":\"omitted\"}", requirement) &&
                    RoomMovementResultPolicy.ReviewReports(new JObject { ["resultReport"] = expectedReport }.ToString(), requirement),
                    "Every recorded movement phase requires its actual spoken state, not generic reassurance.");
                Require(RoomTaskProtocol.ReviewAllows(new JObject { ["verdict"] = "consistent", ["reason"] = "controlled", ["resultReport"] = expectedReport }.ToString()),
                    "The production review parser accepts the complete movement-state vocabulary.");
                foreach (string language in new[] { "zh", "ja", "en" })
                {
                    string localized = (string)localize.Invoke(null, new object[] { text, language });
                    Require(!string.IsNullOrWhiteSpace(localized) && (language == "zh" || localized != text) &&
                        localized != "今の状態をまだ確認できていないわ。できたとは言えないの。" &&
                        localized != "I haven't confirmed the current result yet, so I can't say it's completed.",
                        "Actual movement phase has a specific localized receipt: " + phase + "/" + language);
                }
            }
        string oldLanguage = model.DialogueLanguage;
        var serializerHost = new GameObject("Inactive production reply load serializer"); serializerHost.SetActive(false);
        var serializer = serializerHost.AddComponent<ChatQW>();
        NevaSceneVisionIntegrationRegression.ConfigureActualRoleSerializer(serializer);
        var system = serializer.m_DataList.Where(m => m.role == "system").ToArray();
        Require(serializer.HasPromptFiles && system.Sum(m => m.content.Length) > 10000,
            "Multi-turn load requires the complete actual scene persona, language and behavior assets.");
        var fixture = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/RoomDecisionBudget.real-producer.json"));
        var historicalRows = (JArray)fixture["sceneSpatialMemory"]["objects"];
        string[] phases = { "preparing", "moving", "follow-paused", "follow-recovering", "follow-replanning", "nav-recovering", "follow-waiting" };
        try
        {
            for (int turn = 0; turn < phases.Length; turn++)
            {
                string request = "请继续跟着我，并说明现在的状态。";
                yield return PrepareRoomPlanning(chat, bridge, request);
                int generation = (int)Get(chat, "m_FormalResponseGeneration");
                model.Decisions.Last().Complete(true, "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", "controlled-reply-facts-fixture");
                Call(chat, "RememberFirstReplyRequestSemantics", new FirstReplyCompletion {
                    Disposition = FirstReplyDisposition.Tools, RequestKind = "mixed" }, generation, model);
                string language = new[] { "ja", "en", "zh" }[turn % 3];
                typeof(LLM).GetField("lan", Flags).SetValue(model, language);
                string action = "receipt-fixture-" + turn;
                // Controlled engine state, no movement command: record through the
                // real diagnostic producer, then release the actual speech path.
                object oldId = Get(bridge, "actionId"), oldPhase = Get(bridge, "phase"), oldReason = Get(bridge, "reason"),
                    oldDiagnostic = Get(bridge, "latestDiagnostic"), oldFollow = Get(bridge, "followActive");
                int operations = bridge.Controller.OperationRevision;
                try
                {
                    Set(bridge, "actionId", action); Set(bridge, "followActive", true);
                    Call(bridge, "SetFact", phases[turn], "controlled receipt phase");
                    Set(chat, "m_RoomTaskIntent", "follow"); Set(chat, "m_RoomTaskActionId", action);
                    Set(chat, "m_RoomTaskDispatch", "follow-submitted"); Set(chat, "m_RoomTaskGeneration", generation);
                    Set(chat, "m_RoomSpeechGuardGeneration", generation);
                    // The real user path stages a user-spoke frame. Its marker
                    // precedes newly captured scene cognition and Bound facts.
                    string frame = (string)Call(chat, "BuildPerceptionFrame", "user-spoke");
                    Call(chat, "StageFormalObservationFrame", frame, generation);
                    string context = (string)Call(chat, "BuildFormalObservationContext", (string)Call(chat, "BuildRoomTaskReplyFacts"));
                    context = (string)Call(chat, "BuildCommittedSpatialReplyContext", context, generation);
                    // Add growing observations captured by the real producer in a
                    // prior Play. This is a load reconstruction, not its lost wire.
                    var memory = (JObject)fixture["sceneSpatialMemory"].DeepClone();
                    var growth = new JArray();
                    for (int index = 0; index < 24 + turn * 12; index++)
                    {
                        var row = (JObject)historicalRows[index % historicalRows.Count].DeepClone();
                        row["entityId"] = "controlled-memory-" + index; growth.Add(row);
                    }
                    memory["objects"] = growth;
                    string growthBlock = "[只读场景问答]\n" + new JObject { ["spatialReplyFacts"] = new JObject { ["sceneSpatialMemory"] = memory },
                        ["spatialGoal"] = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/RoomWorkingMemory.completed-task.json")) }.ToString(Formatting.None) + "\n";
                    int cognitionAt = context.IndexOf("[物件与自身方位的观察记录", StringComparison.Ordinal);
                    Require(context.Contains("(用户刚开口讲了下面这段话，请回应)") && cognitionAt > context.IndexOf("(用户刚开口讲了下面这段话，请回应)", StringComparison.Ordinal),
                        "Production staged user frame precedes the current scene cognition.");
                    context = context.Insert(cognitionAt, growthBlock);
                    serializer.m_DataList = new List<LLM.SendData>(system.Concat(model.m_DataList.Where(m => m.role != "system")));
                    serializer.m_DataList.Add(new LLM.SendData("user", request));
                    var observation = (NeEEvA.Perception.SceneVisionObservation)Call(chat, "GetFormalSceneObservation", generation);
                    var audit = RoomDecisionBudgetRegression.AuditFormalReply(serializer, serializer.m_DataList, context,
                        observation?.ImageDataUrl, Path.Combine(output, "reply-load-turn-" + turn));
                    Require((bool)audit["admitted"], "Multi-turn production reply plus growing retained scene history fits: " + audit);
                    Require((bool?)audit["projection"]["currentRequestBound"] == true &&
                        audit["projection"]["recognizedBlocks"].Values<string>().Contains("[物件与自身方位的观察记录；回答当前对话可直接参考，无需先发起寻物]"),
                        "Actual user-spoke suffix cognition and current request participate in the shared budget.");
                    string failure = turn % 2 == 0 ? "context-budget" : "no-public-output-deadline";
                    Call(chat, "HandleResponseGenerationFailure", failure);
                    Call(chat, "CompleteRoomSpeech", new SpeechTextBuffer(), "", generation, true, null);
                    var receipt = (JObject)Get(chat, "m_LastFormalReplyReceipt");
                    Require((string)receipt["replyFailure"]["kind"] == (turn % 2 == 0 ? "reply-budget-exceeded" : "reply-timeout") &&
                        (bool)receipt["replyFailure"]["httpRequestSent"] == (turn % 2 != 0) && receipt["planningFailure"].Type == JTokenType.Null &&
                        (string)receipt["execution"]["actionId"] == action && (string)receipt["execution"]["phase"] == phases[turn],
                        "Final receipt preserves independent reply failure, no planning failure, and actual owned execution phase.");
                    string expectedState = RoomMovementResultPolicy.Fallback(new JObject { ["execution"] = receipt["execution"].DeepClone() }, "follow", action);
                    string localized = (string)localize.Invoke(null, new object[] { expectedState, language });
                    Require(model.m_DataList.Last().content.StartsWith(localized, StringComparison.Ordinal) &&
                        bridge.Controller.OperationRevision == operations, "Final speech reports the localized phase without a new action or retry.");
                    results.Add(new JObject { ["turn"] = turn, ["audit"] = audit, ["receipt"] = receipt.DeepClone() });
                }
                finally
                {
                    Set(bridge, "actionId", oldId); Set(bridge, "phase", oldPhase); Set(bridge, "reason", oldReason);
                    Set(bridge, "latestDiagnostic", oldDiagnostic); Set(bridge, "followActive", oldFollow);
                }
            }
            Require(Call(chat, "CurrentReplyFailure", (int)Get(chat, "m_FormalResponseGeneration") + 1) == null,
                "Reply failure cannot leak to a later generation.");
        }
        finally { UnityEngine.Object.DestroyImmediate(serializerHost); typeof(LLM).GetField("lan", Flags).SetValue(model, oldLanguage); }
        yield return RunMovementStateReviewChecks(chat, bridge, model, output);
        yield return RunLatestMovementPacketChecks(chat, bridge, model, output);
        yield return RunMovementPlaybackChecks(chat, bridge, model);
        yield return RunLanguageReplyChecks(chat, bridge, model, output);
        LastDecisionFailureReport["replyReceiptsAndLoad"] = new JObject { ["status"] = "passed", ["turns"] = results,
            ["scope"] = "Real ChatSample producer/serializer/persona and accumulated dialogue; controlled phase and failure callbacks with retained production-record load growth. No live model, movement or audible-TTS claim." };
    }

    private static IEnumerator RunLanguageReplyChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        var results = new JArray();
        foreach (string mode in new[] { "corrected", "still-wrong", "cancelled", "unreviewed" })
        {
            yield return Prepare(chat, bridge, model, "none");
            int generation = (int)Get(chat, "m_FormalResponseGeneration"), history = model.m_DataList.Count;
            int operations = bridge.Controller.OperationRevision, speech = model.Speech.Count;
            const string bad = "刚才关掉的那盏小灯，已经重新打开了哦。";
            Call(chat, "CompleteRoomSpeech", Buffer(bad), bad, generation, true, null);
            var first = model.Decisions.Last();
            File.WriteAllText(Path.Combine(output, "language-" + mode + ".request.json"), first.Wire);
            var verdict = new JObject { ["verdict"] = "consistent", ["reason"] = "controlled fact approval must not override language", ["factScope"] = "none" };
            if (mode != "unreviewed") verdict["actualLanguage"] = "zh";
            first.Complete(true, verdict.ToString(), "language-test:first");
            yield return Wait(() => model.Speech.Count == speech + 1, "One private language rewrite");
            var repair = model.Speech.Last();
            Require(repair.Stage == FormalResponseStage.CommittedSpatialReply && !repair.RecordAssistantHistory &&
                model.m_DataList.Count == history && bridge.Controller.OperationRevision == operations,
                "Language rejection remains private and rewrites only speech, without replanning or repeating an action.");
            first.Complete(true, verdict.ToString(), "language-test:duplicate");
            if (mode == "cancelled") Call(chat, "InvalidateFormalResponse", "language-rewrite-owner-cancelled");
            string corrected = mode == "still-wrong" ? bad : "今の景色を確認しているわ。";
            repair.SpeechCallback(new SpeechText(corrected, "ja", actionDependency: SpeechActionDependency.Independent));
            repair.SpeechCompleteCallback(corrected);
            if (mode == "cancelled")
                Require(model.m_DataList.Count == history, "Cancelled language rewrite cannot publish through late callbacks.");
            else if (mode == "still-wrong")
                Require(model.Decisions.Last() == first && model.m_DataList.Count == history + 1 &&
                    model.m_DataList.Last().content != bad,
                    "An identical rejected rewrite immediately uses the existing fallback without another factual-review request.");
            else
            {
                yield return Wait(() => model.Decisions.Last() != first, "Rewritten language and facts reviewed again");
                var second = model.Decisions.Last();
                verdict["actualLanguage"] = mode == "still-wrong" ? "zh" : "ja";
                second.Complete(true, verdict.ToString(), "language-test:second");
                Require(model.m_DataList.Count == history + 1 && model.Speech.Count == speech + 1 &&
                    (mode == "still-wrong" ? model.m_DataList.Last().content != bad : model.m_DataList.Last().content == corrected),
                    "The single rewrite is released only on matching language; repeated mismatch uses a localized fallback.");
            }
            Require(bridge.Controller.OperationRevision == operations, "Language correction cannot execute device or movement operations.");
            results.Add(mode);
        }
        LastDecisionFailureReport["languageRecovery"] = new JObject { ["status"] = "passed", ["cases"] = results };
    }

    private static IEnumerator RunMovementStateReviewChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        string[] phases = { "preparing", "follow-recovering", "follow-waiting", "moving" };
        string[] badSpeech = { "好，我准备好了。跟紧你了哦。", "我一直跟着你移动。", "我马上开始跟着你走。", "我正在跟着你移动。" };
        for (int i = 0; i < phases.Length; i++)
        {
            yield return PrepareRoomPlanning(chat, bridge, i == 0 ? "来我身边。" : "请继续跟着我，并说明现在的状态。");
            int generation = (int)Get(chat, "m_FormalResponseGeneration");
            model.Decisions.Last().Complete(true, "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", "controlled-state-review-source");
            Call(chat, "RememberFirstReplyRequestSemantics", new FirstReplyCompletion {
                Disposition = FirstReplyDisposition.Tools, RequestKind = i == 0 ? "action" : "mixed" }, generation, model);
            object oldId = Get(bridge, "actionId"), oldPhase = Get(bridge, "phase"), oldReason = Get(bridge, "reason"),
                oldDiagnostic = Get(bridge, "latestDiagnostic"), oldFollow = Get(bridge, "followActive"), oldActive = Get(bridge, "activeAction");
            string oldLanguage = model.DialogueLanguage;
            string intent = i == 0 ? "approach" : "follow";
            try
            {
                typeof(LLM).GetField("lan", Flags).SetValue(model, "中文");
                string action = "state-review-" + i;
                // Hold a controlled pending approach receipt without starting
                // live follow updates between the real HTTP callbacks.
                if (i == 0) Set(bridge, "activeAction", false);
                Set(bridge, "actionId", action); Set(bridge, "followActive", i != 0); Call(bridge, "SetFact", phases[i], "controlled-state-review");
                Set(chat, "m_RoomTaskIntent", intent); Set(chat, "m_RoomTaskActionId", action);
                Set(chat, "m_RoomTaskDispatch", intent + "-submitted");
                Set(chat, "m_RoomTaskTargetRevision", bridge.ReadCurrentTargetEvidence().currentTargetRevision);
                int count = model.Decisions.Count, operations = bridge.Controller.OperationRevision;
                var speech = new SpeechTextBuffer(); speech.Append(new SpeechText(badSpeech[i], "zh", actionDependency: SpeechActionDependency.Independent));
                Call(chat, "CompleteRoomSpeech", speech, badSpeech[i], generation, true, null);
                yield return Wait(() => model.Decisions.Count > count, "Movement state review with fresh RGB");
                var review = model.Decisions.Last(); var input = JObject.Parse(review.Input);
                var requirement = (JObject)input["movementReportRequirement"];
                Require(review.Kind == "room-review" && (bool)requirement["required"], "Actual speech audit receives the owner-bound phase requirement.");
                if (i == 0) Require(input["sceneActionFacts"]?.Type == JTokenType.Null && input["sceneSearchReportRequirement"]?.Type == JTokenType.Null,
                    "Actual movement producer explicitly emits null unrelated fact branches through the HTTP regression.");
                string directory = Path.Combine(output, "movement-state-review-" + i); Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "bad.request.json"), review.Wire);
                var room = (JObject)Call(chat, "ReadCompactRoomTaskFacts", bridge);
                string good = RoomMovementResultPolicy.Fallback(room, intent, action);
                input["publicSpeech"] = good;
                var writer = (ChatQW)Get(model, "serializer");
                string goodWire = (string)Call(writer, "BuildRoomTaskRequestJsonWithImage", input.ToString(Formatting.None), review.Image, true);
                File.WriteAllText(Path.Combine(directory, "good.request.json"), goodWire);
                string wrongState = (string)requirement["expectedReport"] == "moving" ? "waiting" : "moving";
                int beforeRepair = model.Speech.Count, history = model.m_DataList.Count;
                if (i == 3) { Call(bridge, "SetFact", "follow-waiting", "phase changed while review pending");
                    room = (JObject)Call(chat, "ReadCompactRoomTaskFacts", bridge); good = RoomMovementResultPolicy.Fallback(room, intent, action); }
                var rejected = new JObject { ["verdict"] = "consistent", ["reason"] = "controlled wrong spoken state",
                    ["actualLanguage"] = "zh", ["factScope"] = "user-target", ["resultReport"] = i == 3 ? "moving" : wrongState };
                if (i == 0)
                    yield return RoomSpeechClaimRegression.ReviewProductionInput(writer, review.Input, review.Image,
                        wrongState, badSpeech[i], "zh", null, (ok, result, status) => review.Complete(ok, result, status));
                else review.Complete(true, rejected.ToString(), "controlled-conflicting-phase");
                yield return Wait(() => model.Speech.Count == beforeRepair + 1, "Single movement wording repair");
                Require(model.m_DataList.Count == history && bridge.Controller.OperationRevision == operations,
                    "Incorrect or stale movement words stay private; repair does not submit any action.");
                review.Complete(true, rejected.ToString(), "duplicate phase verdict");
                var repair = model.Speech.Last();
                if (i == 2) Call(chat, "InvalidateFormalResponse", "movement-rewrite-cancelled");
                repair.SpeechCallback(new SpeechText(good, "zh", actionDependency: SpeechActionDependency.Independent));
                repair.SpeechCompleteCallback(good);
                if (i == 2) { Require(model.m_DataList.Count == history, "Cancelled movement repair has no history or late publication."); continue; }
                yield return Wait(() => model.Decisions.Last() != review, "Rewritten movement is reviewed again");
                string expectedState = (string)RoomMovementResultPolicy.Requirement(room, intent, action)["expectedReport"];
                var approved = new JObject { ["verdict"] = "consistent", ["reason"] = "controlled corrected phase",
                    ["actualLanguage"] = "zh", ["factScope"] = "user-target", ["resultReport"] = i == 1 ? wrongState : expectedState,
                    ["targetComparison"] = "The current user remains the destination.", ["targetConsistency"] = "matched",
                    ["otherClaimsComparison"] = "No additional clauses.", ["otherClaimsSupport"] = "none", ["conflict"] = "none", ["conflictQuote"] = "" };
                var repairedReview = model.Decisions.Last();
                if (i == 0)
                    yield return RoomSpeechClaimRegression.ReviewProductionInput(writer, repairedReview.Input, repairedReview.Image,
                        expectedState, good, "zh", approved.ToString(), (ok, result, status) => repairedReview.Complete(ok, result, status));
                else repairedReview.Complete(true, approved.ToString(), "controlled-repair-reviewed");
                string expected = (string)typeof(ChatSample).GetMethod("LocalizeRoomFallback", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { good, "zh" });
                Require(model.m_DataList.Last().content == expected && model.Speech.Count == beforeRepair + 1 && bridge.Controller.OperationRevision == operations,
                    "Corrected phase is released; a second mismatch falls back once. State change and repair never repeat the movement.");
            }
            finally
            {
                Set(bridge, "actionId", oldId); Set(bridge, "phase", oldPhase); Set(bridge, "reason", oldReason);
                Set(bridge, "latestDiagnostic", oldDiagnostic); Set(bridge, "followActive", oldFollow);
                typeof(LLM).GetField("lan", Flags).SetValue(model, oldLanguage);
                Set(bridge, "activeAction", oldActive);
            }
        }
    }
}
