using System;
using System.Collections;
using System.IO;
using System.Linq;
using AIChat.Agent;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class AgentWorkLoopRegression
{
    public static IEnumerator RunSingingQueueReadinessChecks()
    {
        int before = checks;
        Check(DeliveryDispositionRules.AllowsPendingGoalReview(true, "singing-goal-review-retry"),
            "A pending review retry is incorrectly blocked by its own undelivered-reply fence.");
        Check(!DeliveryDispositionRules.AllowsPendingGoalReview(false, "singing-goal-review-retry") &&
            !DeliveryDispositionRules.AllowsPendingGoalReview(true, "default"),
            "An unrelated wake acquired the singing review exception.");
        foreach (string gate in new[] { "none", "body", "retry", "input", "formal", "output", "review", "plan", "pause", "budget", "exhausted", "reentrant-input", "reentrant-goal" })
        using (var f = new Fixture("pending_singing_queue_" + gate))
        {
            yield return null;
            f.BeginUser("只把第一段录音里的旋律唱给我听。"); f.Confirm(f.first);
            int revision = f.sense.DescribePracticePhrases().Single(p => p.ClipRef == f.first).Revision;
            object[] args = { $"<sing_goal origin=\"user_request\" refs=\"{f.first}\" range=\"current\" revisions=\"{revision}\" expected=\"只回唱第一段旋律\"/>", true };
            Call(f.chat, "ExtractSingingGoalTags", args);
            Check((bool)Call(f.chat, "HasPendingSingingGoalReview"), "Fixture failed to register its real pending proposal.");
            Set(f.chat, "m_UserTurnAwaitingReplySince", Time.realtimeSinceStartup + 1f);
            Set(f.chat, "m_StreamComplete", true); Set(f.chat, "m_TTSSenderDone", true);
            Set(f.chat, "m_AgentRoundInFlight", false); Set(f.chat, "m_FormalResponseInFlight", false);
            Call(f.chat, "BeginSceneTimeSession"); Set(f.chat, "m_SceneTimePaused", gate == "pause");
            double now = (double)Call(f.chat, "SceneMonotonicNow");
            var clock = (SceneReviewClock)Get(f.chat, "m_SceneReviewClock");
            string reason = gate == "retry" ? "singing-goal-review-retry" : "singing-goal-review";
            clock.Schedule(now - 1, .1, reason, true);
            if (gate == "body")
            {
                Set(f.chat, "m_SceneActionActive", true);
                var bridge = f.host.AddComponent<ArdyRoomDialogueBridge>(); bridge.enabled = false;
                typeof(ArdyRoomDialogueBridge).GetField("followActive", Flags).SetValue(bridge, true);
            }
            if (gate == "input") Set(f.chat, "m_SceneExplorationManualInputActive", true);
            if (gate == "formal") Set(f.chat, "m_FormalResponseInFlight", true);
            if (gate == "output") Set(f.chat, "m_TTSSenderDone", false);
            if (gate == "review") Set(f.chat, "m_AutonomyProbeInFlight", true);
            if (gate == "plan") Set(f.chat, "m_ActorPlan", new ActorPlanRuntime("已有受理规划"));
            if (gate == "budget") for (int i = 0; i < 6; i++) clock.RecordRequest(now);
            if (gate == "exhausted")
            {
                var goal = Get(f.chat, "m_SingingGoal");
                Set(f.chat, "m_SingingUnavailableGoal", goal); Set(f.chat, "m_SingingUnavailableEpoch", Get(f.chat, "m_WorkEpoch"));
                Set(f.chat, "m_SingingUnavailableVersion", goal.GetType().GetField("version", Flags).GetValue(goal));
                Set(f.chat, "m_SingingUnavailableAttempts", 2);
            }
            bool reentered = false;
            if (gate.StartsWith("reentrant-", StringComparison.Ordinal))
                Set(f.chat, "m_ResponseTrace", new ConversationTrace("review-queue-reentry", raw => {
                    if ((string)JObject.Parse(raw)["event"] != "goal.review_queue_dispatch") return;
                    reentered = true;
                    if (gate == "reentrant-input") Set(f.chat, "m_UserSpeechActiveForAutonomy", true);
                    else
                    {
                        f.BeginUser("先暂停刚才的目标，等我说明新的片段。");
                        Set(f.chat, "m_FormalResponseGeneration", (int)Get(f.chat, "m_FormalResponseGeneration") + 1);
                    }
                    clock.Schedule((double)Call(f.chat, "SceneMonotonicNow"), 10, "successor-owned-event", true);
                }));
            Call(f.chat, "StepUrge");
            bool ready = gate == "none" || gate == "body" || gate == "retry";
            Check(f.model.reviewRequests == (ready ? 1 : 0), "Real StepUrge dispatched incorrectly for gate=" + gate);
            if (ready)
            {
                Check(!clock.Pending && (bool)Get(f.chat, "m_AutonomyProbeInFlight"), "Review queue did not consume exactly one owned wake.");
                Call(f.chat, "StepUrge");
                Check(f.model.reviewRequests == 1 && f.model.Requests.Count == 0 &&
                    !(bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn"), "Review duplicated or authorized output without a verdict.");
                if (gate == "body") Check((bool)Get(f.chat, "m_SceneActionActive") &&
                    f.host.GetComponent<ArdyRoomDialogueBridge>().IsFollowing, "Recording review changed the existing body task.");
            }
            else if (gate != "exhausted") Check(clock.Pending, "A blocked review lost its pending wake: " + gate);
            if (gate.StartsWith("reentrant-", StringComparison.Ordinal)) Check(reentered && clock.Reason == "successor-owned-event",
                "Old review dispatch changed the successor's queued event: " + gate);
            Set(f.chat, "m_SceneTimePaused", true);
        }
        File.WriteAllText("singing-queue-readiness-regression.json", new JObject {
            ["passed"] = true, ["checks"] = checks - before,
            ["scope"] = "Production StepUrge and pending goal/request orchestration with controlled LLM callbacks; no remote model, real microphone or playback." }.ToString());
    }
}
