using System;
using System.Collections;
using System.Linq;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;

public static partial class SpatialGoalRunnerRegression
{
    private static void StageOwnedFollow(ArdyRoomDialogueBridge bridge, string identity)
    {
        // Controlled ongoing follow ownership, not a simulated native locomotion result.
        Set(bridge, "activeAction", false); Set(bridge, "followActive", true);
        Set(bridge, "actionId", identity); Set(bridge, "phase", "follow-waiting");
        Set(bridge, "operationRevision", bridge.Controller.OperationRevision);
    }

    private static IEnumerator BodyHandoff(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, SceneObjectLightSwitch lamp)
    {
        bool oldControl = model.ControlReviewsEnabled, oldSequences = model.TaskSequencesEnabled;
        model.ControlReviewsEnabled = true;
        try
        {
            Run run = null;
            model.TaskSequencesEnabled = true;
            yield return Begin(chat, bridge, model, lamp, "failed-parent-continued-to-return", OffRequest + "然后回到我身边。", "off", "inspect-object", v => run = v);
            string resumedGoal = (string)Goal(chat)["goalId"];
            Call(chat, "FinishSpatialGoalRunner", Get(chat, "m_SpatialGoalRunner"), "controlled-search-budget-failure", false);
            Require(Get(chat, "m_SpatialGoalRunner") == null && (string)Goal(chat)["status"] == "blocked" && lamp.ReadState().on,
                "Actual failed runner settles and releases ownership without operating the device.");
            const string resumeText = "你怎么不动了，继续呀。";
            Call(chat, "SetCurrentUserInput", resumeText, resumeText);
            Call(chat, "RecordAcceptedMotionUserTurn", resumeText);
            SceneVisionObservation resumedView = null;
            Call(chat, "RequestSceneObservation", "resumed-parent-source", new Action<SceneVisionObservation>(v => resumedView = v));
            yield return Wait(() => resumedView?.HasImage == true, 8, "fresh continued-task view");
            int resumeReviews = model.DeviceGoalReviews.Count;
            run.searches = model.SceneSearchRequests.Count; run.matches = model.Matches.Count; run.speech = model.Speech.Count;
            Call(chat, "StartStreaming", resumeText, false, null, false, false, false, null, false, resumedView, true);
            yield return Wait(() => model.DeviceGoalReviews.Count == resumeReviews + 1, 8, "independent whole-parent continuation review");
            var resumeReview = model.DeviceGoalReviews.Last(); Export("failed-parent-continue-review", resumeReview);
            Require(JObject.Parse(resumeReview.Input)["parentTask"] is JObject,
                "Continuation semantics receives the actual failed parent ledger.");
            resumeReview.Complete(true, new JObject { ["verdict"] = "approved", ["relation"] = "continue", ["responseMode"] = "act",
                ["referentGoalId"] = resumedGoal, ["referentEntityId"] = "", ["resumeParentTask"] = true,
                ["reason"] = "Current user resumes the whole unfinished parent without changing its constraints." }.ToString(), "controlled-parent-resume");
            run.generation = (int)Get(chat, "m_FormalResponseGeneration");
            yield return Wait(() => Get(chat, "m_SpatialGoalRunner") != null, 8, "reviewed parent resumes without re-planning old quotes");
            var resumedSequence = (JObject)Call(chat, "ReadRoomTaskSequence");
            Require(RoomPlans(model) == run.rooms + 1 && (string)resumedSequence["originalRequest"] == run.text &&
                (string)resumedSequence["request"] == resumeText && (string)resumedSequence.SelectToken("steps[1].status") == "pending",
                "Current ownership, original parent constraints and dependent return survive the real new-turn entry.");
            yield return Drive(chat, bridge, model, lamp, run);
            Require(run.concurrentReturn && !lamp.ReadState().on && (string)Goal(chat)["status"] == "satisfied",
                "Fresh execution and actual device readback unlock the original return after a short continue utterance.");
            AddCase(run, "failed-parent-continued-fresh-execution-readback-and-return", chat);
            bridge.StopMovement("resumed-parent-test-end", "End checked return submission");
            model.TaskSequencesEnabled = false;
            yield return Begin(chat, bridge, model, lamp, "quick-stop-approved-next-task", OffRequest, "off", "inspect-object", v => run = v, holdApproval: true);
            StageOwnedFollow(bridge, "quick-stop-old-follow");
            Call(chat, "BeginSceneVisionStopPreparation", (int)Get(chat, "m_FormalResponseGeneration"));
            var quickControl = model.ControlReviews.Last();
            quickControl.Complete(true, "{\"explicitStopNow\":true,\"bodyDirective\":\"other-task\",\"force\":\"command-now\",\"reason\":\"Current task replaces following.\"}", "controlled-quick-stop");
            string quickStop = bridge.ActionId;
            Require(!bridge.IsFollowing && bridge.Phase == "stopped", "Preparation really stops its owned old follow before new-goal approval.");
            Approve(run.review, null);
            Require(Get(chat, "m_SpatialGoalRunner") != null, "Already committed quick stop must hand off the independently approved next goal.");
            yield return Drive(chat, bridge, model, lamp, run);
            Require((string)Goal(chat)["status"] == "satisfied" && !lamp.ReadState().on,
                "Quick-stop successor reaches actual same-entity readback, not merely a promise or stop receipt.");
            yield return Audit(chat, model, lamp, run);
            AddCase(run, "quick-stop-then-approved-target-actual-readback", chat);

            yield return Begin(chat, bridge, model, lamp, "follow-direct-device", OffRequest, "off", "inspect-object", v => run = v, holdApproval: true);
            StageOwnedFollow(bridge, "handoff-direct-follow");
            int reviews = model.ControlReviews.Count;
            Approve(run.review, null);
            Require(model.ControlReviews.Count == reviews + 1 && Get(chat, "m_SpatialGoalRunner") == null && lamp.ReadState().on,
                "Direct device replacement awaits one independent body handoff approval, with no premature device write.");
            var control = model.ControlReviews.Last(); Export("follow-direct-control", control);
            Require((string)JObject.Parse(control.Input)["currentUserText"] == OffRequest, "Handoff reads the exact latest request.");
            control.Complete(true, new JObject { ["explicitStopNow"] = false, ["bodyDirective"] = "other-task", ["force"] = "command-now", ["reason"] = "Immediate approved device task replaces following." }.ToString(), "controlled-replacement-approved");
            Require(!bridge.IsFollowing && Get(chat, "m_SpatialGoalRunner") != null,
                "Actual stop receipt releases follow ownership before device runner starts.");
            string stopId = bridge.ActionId;
            control.Complete(true, new JObject { ["explicitStopNow"] = false, ["bodyDirective"] = "other-task", ["force"] = "command-now", ["reason"] = "Immediate approved device task replaces following." }.ToString(), "controlled-duplicate");
            Require(bridge.ActionId == stopId && model.ControlReviews.Count == reviews + 1, "Duplicate review cannot repeat handoff.");
            yield return Drive(chat, bridge, model, lamp, run);
            yield return Audit(chat, model, lamp, run);
            AddCase(run, "follow-stop-receipt-device-actual-readback", chat);

            yield return Begin(chat, bridge, model, lamp, "follow-denied-then-latest-update", OnRequest, "on", "inspect-object", v => run = v, holdApproval: true);
            StageOwnedFollow(bridge, "handoff-denied-follow");
            Approve(run.review, null);
            control = model.ControlReviews.Last();
            control.Complete(true, "{\"explicitStopNow\":false,\"bodyDirective\":\"none\",\"force\":\"unclear\",\"reason\":\"No permission.\"}", "controlled-no-handoff-authority");
            Require(bridge.IsFollowing && Get(chat, "m_SpatialGoalRunner") == null && !lamp.ReadState().on &&
                (string)Goal(chat)["status"] == "blocked" && (string)Goal(chat)["reason"] == "device-task-body-owned-by-follow",
                "Denied semantics preserves old follow, blocks device writes, and names the owner instead of generic startup failure.");
            string goalId = (string)Goal(chat)["goalId"];
            const string latest = "你先停下，停下跟随我的脚步，然后把刚才关掉的小灯打开，然后回到我身边。";
            Call(chat, "RecordAcceptedMotionUserTurn", latest); Set(chat, "m_LastUserMsg", latest); Set(chat, "m_LastUserEvidence", latest);
            SceneVisionObservation current = null;
            Call(chat, "RequestSceneObservation", "handoff-latest-user", new Action<SceneVisionObservation>(v => current = v));
            yield return Wait(() => current?.HasImage == true, 8, "fresh latest handoff frame");
            model.TaskSequencesEnabled = true;
            int updates = model.DeviceGoalReviews.Count, plans = RoomPlans(model);
            Call(chat, "StartStreaming", latest, false, null, false, false, false, null, false, current, true);
            yield return Wait(() => model.DeviceGoalReviews.Count == updates + 1, 8, "latest update review of blocked device goal");
            model.DeviceGoalReviews.Last().Complete(true, new JObject { ["verdict"] = "approved", ["relation"] = "continue",
                ["responseMode"] = "act", ["referentGoalId"] = goalId, ["referentEntityId"] = "",
                ["reason"] = "Latest user still wants this lamp on, now explicitly adding stop before device and return after." }.ToString(), "controlled-latest-continue");
            yield return Wait(() => RoomPlans(model) == plans + 1, 8, "latest complete sequence planning");
            var decision = model.Decisions.Last(r => r.Kind == "room"); Export("blocked-goal-latest-whole-turn", decision);
            var input = JObject.Parse(decision.Input);
            Require((string)input["currentUserText"] == latest && (bool?)input["taskSequenceRequired"] == true,
                "Continue relation does not replace latest words with the old goal request or disable complete sequence validation.");
            var proposal = new JObject { ["intent"] = "stop-moving", ["origin"] = "user", ["evidence"] = "你先停下", ["taskStep"] = "s1",
                ["taskPlan"] = new JArray(
                    new JObject { ["id"] = "s1", ["quote"] = "你先停下", ["kind"] = "stop-moving", ["desiredState"] = null, ["after"] = new JArray() },
                    new JObject { ["id"] = "s2", ["quote"] = "把刚才关掉的小灯打开", ["kind"] = "device-state", ["desiredState"] = "on", ["after"] = new JArray("s1") },
                    new JObject { ["id"] = "s3", ["quote"] = "回到我身边", ["kind"] = "approach", ["desiredState"] = null, ["after"] = new JArray("s2") }),
                ["taskCoverage"] = new JArray(new JObject { ["quote"] = latest, ["kind"] = "action", ["stepIds"] = new JArray("s1", "s2", "s3") }) };
            StageOwnedFollow(bridge, "handoff-latest-follow");
            decision.Complete(true, proposal.ToString(), "controlled-latest-stop-device-return");
            Require(!bridge.IsFollowing && bridge.Phase == "stopped" && Get(chat, "m_SpatialGoalRunner") == null && !lamp.ReadState().on,
                "Existing device goal cannot intercept latest explicit stop or run before its prerequisite receipt.");
            var sequence = (JObject)Call(chat, "ReadRoomTaskSequence");
            Require((string)sequence.SelectToken("steps[0].status") == "completed" &&
                ((JArray)sequence["readyStepIds"]).Values<string>().SequenceEqual(new[] { "s2" }) &&
                (string)Goal(chat)["goalId"] == goalId,
                "Completed stop preserves the reviewed goal and unlocks only device, keeping return dependent.");
            LastReport["followLatestHandoff"] = new JObject { ["sequence"] = sequence, ["goal"] = Goal(chat),
                ["scope"] = "Controlled follow ownership and semantic replies; actual stop, production latest-continue routing and prerequisite ledger. Direct replacement above runs real search and device readback." };
            Call(chat, "CancelSpatialGoal", "handoff-regression-complete");
            Call(chat, "InvalidateFormalResponse", "handoff-regression-complete");
        }
        finally { model.ControlReviewsEnabled = oldControl; model.TaskSequencesEnabled = oldSequences; bridge.StopMovement("handoff-fixture-end", "End controlled ownership"); }
    }
}
