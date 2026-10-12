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
            yield return Begin(chat, bridge, model, lamp, "follow-direct-device", OffRequest, "off", "inspect-object", v => run = v, holdApproval: true);
            StageOwnedFollow(bridge, "handoff-direct-follow");
            int reviews = model.ControlReviews.Count;
            Approve(run.review, null);
            Require(model.ControlReviews.Count == reviews + 1 && Get(chat, "m_SpatialGoalRunner") == null && lamp.ReadState().on,
                "Direct device replacement awaits one independent body handoff approval, with no premature device write.");
            var control = model.ControlReviews.Last(); Export("follow-direct-control", control);
            Require((string)JObject.Parse(control.Input)["currentUserText"] == OffRequest, "Handoff reads the exact latest request.");
            control.Complete(true, new JObject { ["intent"] = "stop-moving", ["origin"] = "user", ["evidence"] = OffRequest }.ToString(), "controlled-replacement-approved");
            Require(!bridge.IsFollowing && Get(chat, "m_SpatialGoalRunner") != null,
                "Actual stop receipt releases follow ownership before device runner starts.");
            string stopId = bridge.ActionId;
            control.Complete(true, new JObject { ["intent"] = "stop-moving", ["origin"] = "user", ["evidence"] = OffRequest }.ToString(), "controlled-duplicate");
            Require(bridge.ActionId == stopId && model.ControlReviews.Count == reviews + 1, "Duplicate review cannot repeat handoff.");
            yield return Drive(chat, bridge, model, lamp, run);
            yield return Audit(chat, model, lamp, run);
            AddCase(run, "follow-stop-receipt-device-actual-readback", chat);

            yield return Begin(chat, bridge, model, lamp, "follow-denied-then-latest-update", OnRequest, "on", "inspect-object", v => run = v, holdApproval: true);
            StageOwnedFollow(bridge, "handoff-denied-follow");
            Approve(run.review, null);
            control = model.ControlReviews.Last();
            control.Complete(true, "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", "controlled-no-handoff-authority");
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
