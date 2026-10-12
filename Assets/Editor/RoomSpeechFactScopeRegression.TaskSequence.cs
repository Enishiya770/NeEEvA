using System;
using System.Collections;
using System.IO;
using System.Linq;
using AIChat.Agent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class RoomSpeechFactScopeRegression
{
    private static IEnumerator CheckTaskSequenceDispatch(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, ChatQW serializer, string output)
    {
        var recorded = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/TaskSequence.latest-2003-20261011.json"));
        string request = (string)recorded["request"];
        bool old = model.TaskSequencesEnabled;
        model.TaskSequencesEnabled = true;
        try
        {
            yield return PrepareRoomPlanning(chat, bridge, request);
            var initial = model.Decisions.Last(); int decisions = model.Decisions.Count, revision = bridge.Controller.OperationRevision;
            var wrong = new JObject { ["intent"] = "approach", ["origin"] = "user", ["evidence"] = "走到我身边来", ["taskStep"] = "s2",
                ["taskPlan"] = new JArray(
                    new JObject { ["id"] = "s1", ["quote"] = "把你刚才关掉的那盏小灯打开", ["kind"] = "device-state", ["desiredState"] = "on", ["after"] = new JArray() },
                    new JObject { ["id"] = "s2", ["quote"] = "走到我身边来", ["kind"] = "approach", ["desiredState"] = null, ["after"] = new JArray("s1") }) };
            wrong["taskCoverage"] = new JArray(new JObject { ["quote"] = request, ["kind"] = "action", ["stepIds"] = new JArray("s1", "s2") });
            initial.Complete(true, wrong.ToString(Formatting.None), "controlled-latest-wrong-order");
            yield return Wait(() => model.Decisions.Count > decisions, "One bounded plan correction for an unmet predecessor");
            var correction = model.Decisions.Last(); var input = JObject.Parse(correction.Input);
            Require(bridge.Controller.OperationRevision == revision &&
                (string)input.SelectToken("taskSequence.steps[0].status") == "pending" &&
                (string)input.SelectToken("decisionRepair.rejectionReason") == "invalid-task-sequence-prerequisite",
                "Production callback rejects the latest Play's wrong-order approach before submitting movement; repair retains the first step.");
            File.WriteAllText(Path.Combine(output, "sequence-wrong-order-repair.request.json"), correction.Wire);
            // The rejected planner cannot erase its first step in its retry.
            wrong.Remove("taskPlan"); correction.Complete(true, wrong.ToString(Formatting.None), "controlled-repeat-wrong-order");
            Require(bridge.Controller.OperationRevision == revision && model.Decisions.Count == decisions + 1 &&
                (string)((JObject)Call(chat, "ReadRoomTaskDecisionFailure"))?["validationReason"] == "invalid-task-sequence-prerequisite",
                "A second wrong order settles as an explicit planning failure with no action or unbounded retry.");
            Call(chat, "InvalidateFormalResponse", "sequence-regression-complete");
        }
        finally { model.TaskSequencesEnabled = old; }

        // Exercise the real serializer using an actual recorded production frame,
        // authored persona and multi-turn accepted text, rather than a tiny scene.
        var history = serializer.m_DataList.ToList(); string trailing = serializer.TrailingContext;
        try
        {
            foreach (JObject reply in recorded["dialogue"]) serializer.m_DataList.Add(new LLM.SendData("assistant", (string)reply["publicText"]));
            string prompt = (string)Call(chat, "BuildOrdinaryAutonomyIntentProbePrompt", "latest-full-frame", (string)recorded["frame"]);
            string Wire() => (string)typeof(ChatQW).GetMethod("BuildAutonomyIntentRequestJsonWithImage", Flags)
                .Invoke(serializer, new object[] { prompt, "data:image/png;base64,AA==" });
            string wire = Wire(); object[] args = { wire, null };
            Require((bool)typeof(ChatQW).GetMethod("AdmitAutonomyIntentWire", Flags).Invoke(serializer, args),
                "Latest production frame with the real complete message envelope fits the unchanged autonomy budget: " + args[1]);
            File.WriteAllText(Path.Combine(output, "latest-autonomy-full-envelope.request.json"), wire);
            LastDecisionFailureReport["latestAutonomyFullEnvelopeBudget"] = (JObject)args[1];
            serializer.TrailingContext = "Unknown authored restriction that must remain literal: " + new string('约', 30000);
            wire = Wire(); args = new object[] { wire, null };
            Require(!(bool)typeof(ChatQW).GetMethod("AdmitAutonomyIntentWire", Flags).Invoke(serializer, args) && wire.Contains(new string('约', 30000)),
                "Oversized mandatory constraints still reject rather than silently disappearing in the autonomy projection.");
        }
        finally { serializer.m_DataList = history; serializer.TrailingContext = trailing; }
        model.TaskSequencesEnabled = true;
        try {
            yield return CheckStopHandoffAfterRejectedProposal(chat, bridge, model);
            yield return CheckRepeatedStopDispatch(chat, bridge, model);
            yield return CheckConcurrentBodyFailure(chat, bridge, model);
            yield return CheckUserTargetRefreshWithoutReplanning(chat, bridge, model);
            yield return CheckStopAndPlanningFailure(chat, bridge, model);
        }
        finally { model.TaskSequencesEnabled = old; }
    }

    private static IEnumerator CheckStopAndPlanningFailure(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model)
    {
        bool previous = model.ControlReviewsEnabled; model.ControlReviewsEnabled = true;
        try {
          foreach (string requestKind in new[] { "action", "mixed" }) {
            string words = "先停止跟随，再打开灯，然后回到我身边。" + (requestKind == "mixed" ? "顺便告诉我二加二是多少。" : "");
            yield return PrepareRoomPlanning(chat, bridge, words);
            int generation = (int)Get(chat, "m_FormalResponseGeneration");
            Call(chat, "RememberFirstReplyRequestSemantics", new FirstReplyCompletion {
                Disposition = FirstReplyDisposition.Tools, RequestKind = requestKind }, generation, model);
            Set(bridge, "followActive", true); Set(bridge, "actionId", "old-follow-before-budget-failure");
            Set(bridge, "operationRevision", bridge.Controller.OperationRevision);
            Call(chat, "BeginSceneVisionStopPreparation", generation);
            model.ControlReviews.Last().Complete(true, new JObject { ["explicitStopNow"] = true, ["bodyDirective"] = "stop-current", ["force"] = "command-now", ["reason"] = "Explicit present stop before the next task." }.ToString(), "controlled-stop");
            string stopId = bridge.ActionId;
            Require(!bridge.IsFollowing && bridge.Phase == "stopped", "Actual quick stop completed independently.");
            model.Decisions.Last(r => r.Kind == "room").Complete(false, "", "room-task-http-context-budget;attempts=0;recovery=preflight-budget");
            var failure = (JObject)Call(chat, "ReadRoomTaskDecisionFailure");
            Require((bool?)failure?["remainingAfterCommittedStop"] == true && (string)failure["completedStopActionId"] == stopId &&
                (string)failure["kind"] == "input-budget-exceeded", "Successful stop cannot hide rejected remainder planning.");
            Require((string)((JObject)Call(chat, "ReadPreviousRoomPlanningFailure")).SelectToken("receipt.completedStopActionId") == stopId,
                "Later diagnostic questions retain the independent completed stop, too.");
            string partial = requestKind == "action" ? "今は止まって待っているわ。" :
                "移動は止めたわ。ただ、続きの動作は情報量が計画の処理枠を超えて、まだ始められていないの。二足す二は四よ。";
            Call(chat, "CompleteRoomSpeech", Buffer(partial), partial, generation, true, null);
            if (requestKind == "mixed") {
                var review = JObject.Parse(Verdict("consistent", "scene-action", "input-budget-exceeded"));
                review["requestDisposition"] = "action-request";
                model.Decisions.Last().Complete(true, review.ToString(), "controlled-complete-mixed-reply");
            }
            var receipt = (JObject)Get(chat, "m_LastFormalReplyReceipt");
            Require((string)receipt?["actionId"] == stopId && (bool?)receipt.SelectToken("planningFailure.remainingAfterCommittedStop") == true &&
                model.m_DataList.Last().content.Contains("計画の処理枠"), "Final receipt and public fallback report stop success AND planning budget failure.");
            if (requestKind == "mixed") Require(model.m_DataList.Last().content.Contains("二足す二は四よ。"),
                "A reviewed complete mixed reply keeps its independent answer alongside the stop and planning diagnosis.");
            Call(chat, "InvalidateFormalResponse", "stop-plus-failure-complete");
            Require(Call(chat, "ReadRoomTaskDecisionFailure") == null, "Combined failure cannot leak into a replacement generation.");
          }
        }
        finally { model.ControlReviewsEnabled = previous; bridge.StopMovement("stop-budget-fixture-end", "End receipt check"); }
    }

    private static IEnumerator CheckConcurrentBodyFailure(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model)
    {
        bool sequences = model.TaskSequencesEnabled; model.TaskSequencesEnabled = false;
        try
        {
            yield return PrepareRoomPlanning(chat, bridge, "先停下来，不要跟着我。", beforePlanner: () => {
                Set(bridge, "followActive", true); Set(bridge, "actionId", "prior-follow-failed-during-plan");
                Set(bridge, "phase", "follow-waiting");
            });
            int generation = (int)Get(chat, "m_FormalResponseGeneration");
            Call(chat, "RememberFirstReplyRequestSemantics", new FirstReplyCompletion {
                Disposition = FirstReplyDisposition.Tools, RequestKind = "control" }, generation, model);
            Set(bridge, "followActive", false); Set(bridge, "activeAction", false);
            Call(bridge, "SetFact", "failed", "Controlled old movement service timeout");
            model.Decisions.Last(r => r.Kind == "room").Complete(false, "", "room-task-http-invalid-request;attempts=1;recovery=not-eligible");
            Require(Call(chat, "ReadConcurrentBodyFailure", generation) != null && model.Speech.Last().Context.Contains("concurrentBodyFailure"),
                "Reply facts retain the independent old follow failure alongside new planning failure.");
            string partial = "動作の計画が失敗したので、新しい動作は始めていないわ。";
            Call(chat, "CompleteRoomSpeech", Buffer(partial), partial, generation, true, null);
            model.Decisions.Last().Complete(true, Verdict("consistent", "scene-action", "service-request-failed"), "controlled-partial-cause-approved");
            var receipt = (JObject)Get(chat, "m_LastFormalReplyReceipt");
            Require(model.m_DataList.Last().content.Contains("それとは別に") &&
                (string)receipt.SelectToken("concurrentBodyFailure.execution.phase") == "failed" &&
                (string)receipt.SelectToken("planningFailure.kind") == "service-request-failed" &&
                string.IsNullOrEmpty((string)receipt["actionId"]),
                "Final report explains both failures and never credits the failed stop command with ending the old movement.");
            Set(bridge, "actionId", "newer-unrelated-action");
            Require(Call(chat, "ReadConcurrentBodyFailure", generation) == null, "Failure cannot migrate to a replacement body identity.");
            Call(chat, "InvalidateFormalResponse", "concurrent-failure-fixture-end");
        }
        finally { model.TaskSequencesEnabled = sequences; bridge.StopMovement("combined-failure-fixture-end", "End controlled receipt"); }
    }

    private static IEnumerator CheckRepeatedStopDispatch(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model)
    {
        const string words = "那你先停下来，你先停下来，不要跟着我不要跟着我。";
        bool previous = model.ControlReviewsEnabled; model.ControlReviewsEnabled = true;
        try
        {
            foreach (string outcome in new[] { "approved", "already-stopped", "denied", "stale" })
            {
                yield return PrepareRoomPlanning(chat, bridge, words);
                Set(bridge, "followActive", true); Set(bridge, "actionId", "repeated-stop-" + outcome);
                string preparedStop = null;
                if (outcome == "already-stopped")
                {
                    Set(bridge, "operationRevision", bridge.Controller.OperationRevision);
                    Call(chat, "BeginSceneVisionStopPreparation", (int)Get(chat, "m_FormalResponseGeneration"));
                    model.ControlReviews.Last().Complete(true, new JObject { ["explicitStopNow"] = true, ["bodyDirective"] = "stop-current", ["force"] = "command-now", ["reason"] = "Explicit stop now." }.ToString(), "controlled-early-stop");
                    Require(!bridge.IsFollowing && bridge.Phase == "stopped", "Independent explicit quick stop commits before the full planner replies.");
                    preparedStop = bridge.ActionId;
                }
                var candidate = RoomControlReviewRegression.Plan(words, "stop-moving", null,
                    "那你先停下来，你先停下来，", "不要跟着我不要跟着我。");
                int reviews = model.ControlReviews.Count, operations = bridge.Controller.OperationRevision;
                model.Decisions.Last(r => r.Kind == "room").Complete(true, candidate.ToString(), "controlled-recorded-repeated-stop");
                Require(model.ControlReviews.Count == reviews + 1 && bridge.IsFollowing == (outcome != "already-stopped") &&
                    bridge.Controller.OperationRevision == operations && Call(chat, "ReadRoomTaskSequence") == null,
                    "Shared coverage waits for independent equivalence review before adopting or stopping.");
                if (outcome == "stale")
                {
                    Call(chat, "InvalidateFormalResponse", "new-user-before-equivalence");
                    // The new user lifecycle cancels old movement itself. Stage its
                    // replacement and verify that the obsolete callback cannot stop it.
                    Set(bridge, "followActive", true); Set(bridge, "actionId", "new-owner-after-stale-review");
                    operations = bridge.Controller.OperationRevision;
                }
                model.ControlReviews.Last().Complete(true, new JObject { ["verdict"] = outcome == "denied" ? "not-equivalent" : "equivalent",
                    ["reason"] = outcome == "denied" ? "Controlled semantic denial." : "The complete words repeatedly request one stop." }.ToString(), "controlled-equivalence");
                if (outcome == "approved" || outcome == "already-stopped")
                {
                    Require(!bridge.IsFollowing && bridge.Phase == "stopped" &&
                        (string)((JObject)Call(chat, "ReadRoomTaskSequence")).SelectToken("steps[0].status") == "completed",
                        "Actual repeated natural utterance commits one stop and completes its single shared step.");
                    if (preparedStop != null) Require(bridge.ActionId == preparedStop && bridge.Controller.OperationRevision == operations,
                        "An already committed quick-stop receipt survives asynchronous equivalence review without replaying the stop.");
                }
                else Require(bridge.IsFollowing && bridge.Controller.OperationRevision == operations && Call(chat, "ReadRoomTaskSequence") == null,
                    "Semantic rejection or stale approval cannot execute a merged step: " + outcome);
                bridge.StopMovement("repeated-fixture-end", "End controlled ownership");
                Call(chat, "InvalidateFormalResponse", "repeated-stop-case-end");
            }
        }
        finally { model.ControlReviewsEnabled = previous; }
    }

    private static IEnumerator CheckUserTargetRefreshWithoutReplanning(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model)
    {
        bool sequences = model.TaskSequencesEnabled; model.TaskSequencesEnabled = false;
        object age = Get(chat, "m_SceneObservationMaxAgeSeconds");
        try
        {
            yield return PrepareRoomPlanning(chat, bridge, "请走到我身边。");
            var pending = model.Decisions.Last(r => r.Kind == "room"); int count = model.Decisions.Count, speech = model.Speech.Count;
            int generation = (int)Get(chat, "m_FormalResponseGeneration");
            var old = (NeEEvA.Perception.SceneVisionObservation)Call(chat, "GetFormalSceneObservation", generation);
            Set(chat, "m_SceneObservationMaxAgeSeconds", .001f); yield return null;
            pending.Complete(true, "{\"intent\":\"approach\",\"origin\":\"user\",\"evidence\":\"请走到我身边。\"}", "controlled-user-target-expired-view");
            Set(chat, "m_SceneObservationMaxAgeSeconds", 12f);
            yield return Wait(() => model.Speech.Count > speech, "Existing user-target intent revalidated after fresh capture");
            var current = (NeEEvA.Perception.SceneVisionObservation)Call(chat, "GetFormalSceneObservation", generation);
            Require(model.Decisions.Count == count && current?.HasImage == true && current.CaptureId != old.CaptureId &&
                (bool)Call(chat, "IsFreshSceneObservation", current),
                "A tracked-user target refreshes the real camera and rechecks execution without a duplicate full semantic planner call.");
            Call(chat, "InvalidateFormalResponse", "user-refresh-regression-end");
        }
        finally { model.TaskSequencesEnabled = sequences; Set(chat, "m_SceneObservationMaxAgeSeconds", age); bridge.StopMovement("refresh-fixture-end", "End refresh check"); }
    }

    private static IEnumerator CheckStopHandoffAfterRejectedProposal(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model)
    {
        const string request = "先停止跟随，再打开那盏灯，然后回到我身边。";
        yield return PrepareRoomPlanning(chat, bridge, request);
        int generation = (int)Get(chat, "m_FormalResponseGeneration");
        Call(chat, "RememberFirstReplyRequestSemantics", new FirstReplyCompletion { Disposition = FirstReplyDisposition.Tools, RequestKind = "action" }, generation, model);
        Set(chat, "m_WorkReviewOpen", true); Set(chat, "m_WorkNoProgress", false);
        Set(bridge, "followActive", true);
        var incomplete = new JObject { ["intent"] = "none", ["origin"] = "user", ["evidence"] = "打开那盏灯", ["taskStep"] = "s1",
            ["deviceGoal"] = new JObject { ["operation"] = "set", ["requestQuote"] = "打开那盏灯", ["targetDescription"] = "那盏灯", ["desiredState"] = "on" },
            ["taskPlan"] = new JArray(new JObject { ["id"] = "s1", ["quote"] = "打开那盏灯", ["kind"] = "device-state", ["desiredState"] = "on", ["after"] = new JArray() }),
            ["taskCoverage"] = new JArray(new JObject { ["quote"] = "再打开那盏灯", ["kind"] = "action", ["stepIds"] = new JArray("s1") }) };
        int initialDecisions = model.Decisions.Count, initialReviews = model.DeviceGoalReviews.Count;
        int initialOperations = bridge.Controller.OperationRevision;
        model.Decisions.Last().Complete(true, incomplete.ToString(Formatting.None), "controlled-missing-stop-and-return");
        yield return Wait(() => model.Decisions.Count > initialDecisions, "One bounded repair for omitted request clauses");
        Require(model.DeviceGoalReviews.Count == initialReviews && bridge.Controller.OperationRevision == initialOperations && Call(chat, "ReadRoomTaskSequence") == null &&
            (string)JObject.Parse(model.Decisions.Last().Input).SelectToken("decisionRepair.rejectionReason") == "invalid-task-sequence-coverage",
            "Incomplete request coverage is rejected before goal registration or physical takeover.");
        var cancel = new JObject { ["intent"] = "none", ["origin"] = "user", ["evidence"] = "停止跟随",
            ["deviceGoal"] = new JObject { ["operation"] = "cancel", ["requestQuote"] = "停止跟随" } };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int reviews = model.DeviceGoalReviews.Count, decisions = model.Decisions.Count;
            model.Decisions.Last().Complete(true, cancel.ToString(Formatting.None), "controlled-wrong-cancel-proposal");
            yield return Wait(() => model.DeviceGoalReviews.Count > reviews, "Independent rejection of mistaken device cancellation");
            model.DeviceGoalReviews.Last().Complete(true,
                "{\"verdict\":\"rejected\",\"reason\":\"The complete request stops following then requests a new device operation, not cancellation of that operation.\",\"relation\":\"new\",\"referentKind\":\"unbound\",\"referentSourceId\":\"none\",\"requestAssessment\":\"requested\"}", "controlled-full-request-review");
            yield return Wait(() => model.Decisions.Count > decisions, "Bounded current-request reinterpretation");
        }
        Require(!(bool)Call(chat, "CurrentGenerationRequiresReviewedGoalAnswer", generation), "Rejected action interpretation cannot invent a spatial question obligation.");
        var plan = new JArray(
            new JObject { ["id"] = "s1", ["quote"] = "停止跟随", ["kind"] = "stop-moving", ["desiredState"] = null, ["after"] = new JArray() },
            new JObject { ["id"] = "s2", ["quote"] = "打开那盏灯", ["kind"] = "device-state", ["desiredState"] = "on", ["after"] = new JArray("s1") },
            new JObject { ["id"] = "s3", ["quote"] = "回到我身边", ["kind"] = "approach", ["desiredState"] = null, ["after"] = new JArray("s2") });
        var stop = new JObject { ["intent"] = "stop-moving", ["origin"] = "user", ["evidence"] = "停止跟随", ["taskPlan"] = plan, ["taskStep"] = "s1",
            ["taskCoverage"] = new JArray(new JObject { ["quote"] = request, ["kind"] = "action", ["stepIds"] = new JArray("s1", "s2", "s3") }) };
        // Controlled follow state belongs at the stop callback boundary. It is
        // not a simulated live controller leg across asynchronous fixture waits.
        Set(bridge, "followActive", true);
        model.Decisions.Last().Complete(true, stop.ToString(Formatting.None), "controlled-complete-stop-device-return");
        yield return Wait(() => (string)Get(chat, "m_RoomTaskDispatch") == "stop-submitted", "Actual stop committed");
        Require(!bridge.IsFollowing && !bridge.ReservesBody && bridge.Phase == "stopped", "A real StopForTask releases the previous follow before device takeover.");
        var sequence = (JObject)Call(chat, "ReadRoomTaskSequence");
        Require((string)sequence.SelectToken("steps[0].status") == "completed" && ((JArray)sequence["readyStepIds"]).Values<string>().SequenceEqual(new[] { "s2" }),
            "The real stop receipt unlocks only the device step; return stays dependent on that device.");
        Require((bool)Get(chat, "m_WorkSubstepReviewPending") && (string)Get(chat, "m_WorkStatus") == "substep-result-pending",
            "Synchronous stop queues remaining-work review without waiting thirty seconds or another user prompt.");
        string fallback = (string)Call(chat, "RoomTaskFallback", bridge);
        Require(fallback == "我已经停止移动了。", "Successful stop fallback uses its actual result after rejected device proposals.");
        var clock = (SceneReviewClock)Get(chat, "m_SceneReviewClock"); double due = clock.Deadline;
        int revision = (int)Get(chat, "m_SceneReviewEventRevision");
        Call(chat, "ObserveRoomTaskSequenceProgress", true);
        Require(clock.Deadline == due && (int)Get(chat, "m_SceneReviewEventRevision") == revision, "Repeated reads do not continuously reschedule a settled step.");
        int operations = bridge.Controller.OperationRevision;
        Set(chat, "m_RoomTaskSequence", null); Set(chat, "m_WorkSubstepReviewPending", false);
        Call(chat, "ClearScheduledAgentWake");
        // Exercise adoption of an already committed early stop even when the
        // later draft selected its successor; adoption must not execute either.
        stop["taskStep"] = "s2";
        Call(chat, "AdoptPreparedStopSequence", stop.ToString(Formatting.None));
        Call(chat, "BindRoomTaskSequenceStep", new object[] { null });
        sequence = (JObject)Call(chat, "ReadRoomTaskSequence");
        Require((string)sequence.SelectToken("steps[0].status") == "completed" && (bool)Get(chat, "m_WorkSubstepReviewPending") &&
            bridge.Controller.OperationRevision == operations, "Already executed early stop attaches to the full plan without replaying cancellation or skipping the device.");
        int epoch = (int)Get(chat, "m_WorkEpoch"); Set(chat, "m_WorkEpoch", epoch + 1);
        Call(chat, "ClearScheduledAgentWake"); Set(chat, "m_WorkSubstepReviewPending", false);
        Call(chat, "ObserveRoomTaskSequenceProgress", true);
        Require(!clock.Pending && !(bool)Get(chat, "m_WorkSubstepReviewPending"), "Old work completion cannot queue a sequel after a user-work epoch change.");
        Set(chat, "m_WorkEpoch", epoch); Set(chat, "m_WorkReviewOpen", false);
        Call(chat, "InvalidateFormalResponse", "stop-handoff-regression-complete");
    }
}
