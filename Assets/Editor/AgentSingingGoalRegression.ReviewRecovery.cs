using System;
using System.Reflection;
using Newtonsoft.Json.Linq;

public static partial class AgentSingingGoalRegression
{
    // Real goal registration, system notice and scheduling/entry guards. Only
    // provider failure completion is injected; no service or audio is requested.
    public static JObject RunReviewRecoveryChecks()
    {
        int before = checks;
        using (var f = new Fixture())
        {
            f.Begin("请把我刚才唱的这一段回唱给我听。"); f.Propose(f.first, "clean");
            object goal = Get(f.chat, "m_SingingGoal");
            int version = (int)goal.GetType().GetField("version").GetValue(goal);
            int epoch = (int)Get(f.chat, "m_WorkEpoch");
            Set(f.chat, "m_AutonomyProbeGeneration", 101);
            bool Fail(object source, int v, int e, int g) => (bool)Call(f.chat,
                "TryHandleSingingReviewUnavailable", source, v, e, g, null);
            bool Exhausted() => (bool)typeof(ChatSample).GetProperty("SingingReviewRetryExhausted", Flags).GetValue(f.chat);

            Check(Fail(goal, version, epoch, 101), "First owned review failure was not handled");
            Check(!Exhausted() && (int)Get(f.chat, "m_SingingUnavailableAttempts") == 1,
                "First unavailable review did not preserve exactly one retry");
            var overlay = (SubtitleOverlay)Get(f.chat, "m_SubtitleOverlay");
            Check(overlay != null && overlay.NoticeHistory.Count == 1 &&
                overlay.NoticeHistory[0].Code == "singing_review_retrying" &&
                overlay.NoticeHistory[0].Severity == SystemNoticeSeverity.Error,
                "First failure did not publish a visible program-stage notice");
            Check(overlay.NoticeHistory[0].Message.Contains("尚未提交转换或播放"),
                "Retry notice invented playback or requested different user wording");
            Check((bool)Call(f.chat, "ShouldReviewUserWork"), "First failure prevented its permitted retry");
            Check(Fail(goal, version, epoch, 101) && (int)Get(f.chat, "m_SingingUnavailableAttempts") == 1 &&
                overlay.NoticeHistory.Count == 1, "Duplicate failure consumed another attempt or repeated the notice");
            Check(!Fail(goal, version, epoch - 1, 101) && !Fail(goal, version + 1, epoch, 101) &&
                !Fail(goal, version, epoch, 100), "Stale review owner consumed retry budget");

            Set(f.chat, "m_AutonomyProbeGeneration", 102);
            Check(Fail(goal, version, epoch, 102) && Exhausted(), "Second failure did not exhaust the same-goal retry budget");
            Check(overlay.NoticeHistory.Count == 2 && overlay.NoticeHistory[1].Code == "singing_review_unavailable" &&
                overlay.NoticeHistory[1].Message.Contains("自动重试已停止"), "Terminal review failure was not visible");
            var failureFact = (JObject)Call(f.chat, "BuildSingingReviewFailureFact");
            Check((string)failureFact["stage"] == "goal_review" &&
                (string)failureFact["status"] == "automatic_retry_stopped" &&
                (int)failureFact["goalVersion"] == version && (bool)failureFact["appliesToCurrentProposal"] &&
                !(bool)failureFact["audioSubmittedAtFailure"], "Failure fact lost the actual stage or invented audio submission");
            failureFact["status"] = "caller-mutated";
            Check((string)((JObject)Call(f.chat, "BuildSingingReviewFailureFact"))["status"] == "automatic_retry_stopped",
                "Readonly failure projection exposed mutable runtime state");
            Check(ReferenceEquals(goal, Get(f.chat, "m_SingingGoal")) &&
                (string)goal.GetType().GetField("review").GetValue(goal) == "pending" &&
                f.sense.DescribePracticePhrases().Count == 2, "Failure erased the objective or retained material");
            Check(!(bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn") &&
                !(bool)Call(f.chat, "ShouldReviewUserWork"), "Exhausted pending goal gained execution or default work review authority");
            int generation = (int)Get(f.chat, "m_AutonomyProbeGeneration");
            Call(f.chat, "BeginAutonomyIntentProbeCore", "default", false);
            Call(f.chat, "FireTick", "default");
            Check((int)Get(f.chat, "m_AutonomyProbeGeneration") == generation &&
                !(bool)Get(f.chat, "m_AutonomyProbeInFlight") && overlay.NoticeHistory.Count == 2,
                "Default probe/tick bypassed the exhausted singing review latch");
            Check(Fail(goal, version, epoch, 103) == false && overlay.NoticeHistory.Count == 2,
                "An unsolicited later callback republished the terminal notice");

            f.Begin("再试一次同一段。"); f.Propose(f.first, "clean");
            object replacement = Get(f.chat, "m_SingingGoal");
            int nextVersion = (int)replacement.GetType().GetField("version").GetValue(replacement);
            int nextEpoch = (int)Get(f.chat, "m_WorkEpoch");
            Check(!Exhausted() && (bool)Call(f.chat, "ShouldReviewUserWork"), "New accepted input inherited the old failure latch");
            var retainedFailure = (JObject)Call(f.chat, "BuildSingingReviewFailureFact");
            Check((int)retainedFailure["workEpoch"] == epoch && (int)retainedFailure["goalVersion"] == version &&
                !(bool)retainedFailure["appliesToCurrentProposal"] &&
                ((string)retainedFailure["scope"]).Contains("not waiting for user permission"),
                "New user explanation lost the prior failure or treated it as current authority");
            Check(!Fail(goal, version, epoch, 102), "Late old goal failure affected the new goal");
            Set(f.chat, "m_AutonomyProbeGeneration", 104);
            Check(Fail(replacement, nextVersion, nextEpoch, 104) &&
                (int)Get(f.chat, "m_SingingUnavailableAttempts") == 1, "New goal did not get an independent retry budget");
            Call(f.chat, "ClearSingingReviewFailureBudget", replacement, nextEpoch);
            Check((int)Get(f.chat, "m_SingingUnavailableAttempts") == 0 && !Exhausted(),
                "A valid review could not clear its own unavailable-attempt budget");
            Check((string)((JObject)Call(f.chat, "BuildSingingReviewFailureFact"))["status"] == "recovered_by_valid_review",
                "Successful review left a stale retry-in-progress fact");
        }
        return new JObject { ["passed"] = true, ["checks"] = checks - before,
            ["scope"] = "Production goal, recovery, system notice and work/probe/tick guards; synthetic callback failures, no network or role speech/audio." };
    }
}
