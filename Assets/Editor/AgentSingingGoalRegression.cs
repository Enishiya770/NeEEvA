using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Production parsing, source preparation, task validation and result ledger.
// Semantic review verdicts and playback-completion callbacks are controlled inputs;
// this suite does not claim a model understood the private conversation or that sound was heard.
public static class AgentSingingGoalRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static void RunBatch()
    {
        bool passed = false; string error = "";
        try { RunInteractive(); passed = true; }
        catch (Exception ex) { error = ex.ToString(); Debug.LogException(ex); }
        string path = Path.GetFullPath("Tools/MotionAdapter/reports/agent-singing-goal-regression.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, new JObject { ["passed"] = passed, ["checks"] = checks, ["error"] = error,
            ["scope"] = "Real production goal parser, sing resolver and non-destructive retained audio range preparation; semantic review and playback callbacks simulated; no actual speaker output or user-content recognition." }.ToString());
        EditorApplication.Exit(passed ? 0 : 1);
    }

    public static void RunInteractive()
    {
        ApprovalAndCorrections(); ReviewedTargetMatching(); RetryAndAcceptance(); Lifecycle(); PersistentFollowups();
        StructuredFailuresAndDeferredExecution(); StreamCompletionAndDeferredExecution();
        InitialRequestSourceRecovery(); RecoveredGoalContinuationAndPolicySpeech(); ExportPublicModelCases();
        Debug.Log("[AgentSingingGoalRegression] passed checks=" + checks);
    }

    private static void ApprovalAndCorrections()
    {
        using (var f = new Fixture())
        {
            f.Begin("请把两段连接起来唱。");
            Check(!f.Resolve(f.first + "," + f.second, "current", out _), "Singing without a current goal passed.");
            object[] defaultGoal = { $"<sing_goal refs=\"{f.first}\" revisions=\"{f.Phrase(f.first).Revision}\" expected=\"第一段当前范围\"/>", true };
            Call(f.chat, "ExtractSingingGoalTags", defaultGoal);
            string defaults = f.Context();
            Check((bool)Call(f.chat, "HasPendingSingingGoalReview") && defaults.Contains("\"repairScope\":\"content\"") &&
                defaults.Contains("\"intent\":\"perform\"") && defaults.Contains("\"feedback\":\"none\"") &&
                defaults.Contains("\"completion\":\"playback\"") && defaults.Contains("\"range\":\"current\"") && !defaults.Contains("NaN"),
                "Missing optional goal attributes did not take their documented defaults.");
            f.Propose(f.first + "," + f.second, "current");
            Check((bool)Call(f.chat, "HasPendingSingingGoalReview"), "First goal skipped independent review.");
            var before = f.sense.DescribePracticePhrases().Select(p => p.Revision).ToArray();
            Check(!f.Resolve(f.first + "," + f.second, "expanded", out _), "Unreviewed goal performed material preparation.");
            object[] revise = { $"<clip_revise ref=\"{f.first}\" range=\"expanded\"/>", true };
            Call(f.chat, "ExtractAndApplyClipReviseTags", revise);
            Check(before.SequenceEqual(f.sense.DescribePracticePhrases().Select(p => p.Revision)), "Rejected preflight mutated a clip revision.");
            f.Approve();
            f.Propose(f.first + "," + f.second, "current");
            Check(!(bool)Call(f.chat, "HasPendingSingingGoalReview"), "Identical approved goal demanded review again.");
            Check(f.Resolve(f.first + "," + f.second, "current", out _), "Approved pair did not resolve.");
            f.Completed(f.first, f.second);
            string prior = f.Context();
            Check(prior.Contains("playback_complete_content_unverified") && prior.Contains("\"userConfirmed\":false"), "Playback was elevated to heard/content satisfaction.");

            f.Begin("现在只唱第一段，并补回开头。");
            Check(f.Context().Contains("previous_goal") && f.Context().Contains(f.second), "A follow-up discarded the prior goal/history.");
            f.Propose(f.first + "," + f.second, "current");
            Call(f.chat, "ApplySingingGoalReview", "revise", "最新用户只要第一段；该proposal仍含第二段且未扩大开头范围。");
            Check(!f.Resolve(f.first + "," + f.second, "current", out _), "Model-authored matching but semantically rejected goal/action passed.");
            f.Propose(f.first + "," + f.second, "current");
            Check(((string)Get(f.chat, "m_LastPracticeEditResult")).Contains("完全相同"), "Unchanged rejected goal silently stalled without corrective feedback.");
            Check((bool)Get(f.chat, "m_WorkNoProgress"), "Identical rejected proposal received an unlimited automatic retry allowance.");
            f.Begin("现在只唱第一段，并补回开头。请用新的范围重新处理。");

            f.Propose(f.first, "expanded", completion: "user_confirmation"); f.Approve();
            int revision = f.Phrase(f.first).Revision;
            Check(!f.Resolve(f.first + "," + f.second, "expanded", out string refsFailure) && refsFailure.Contains("actual_refs"), "Actual refs ignored the corrected single-clip goal.");
            Check(!f.Resolve(f.first, "current", out string rangeFailure, "expanded已写在reason里") && rangeFailure.Contains("range=current"), "Reason text implicitly changed range.");
            Check(f.Phrase(f.first).Revision == revision, "Mismatch rejection revised audio before validating its goal.");
            Check(f.Resolve(f.first, "expanded", out _), "Explicit approved expanded selection failed.");
            Check(f.Phrase(f.first).Revision > revision && f.Phrase(f.first).ActiveCapture == "expanded", "Expanded did not actually prepare the broader revision.");
            f.Completed(f.first);
            Check((bool)Call(f.chat, "IsSingingGoalAwaitingConfirmation"), "Content repair was closed merely because Unity completed playback.");
            Check(!f.Resolve(f.first, "expanded", out string stale) && stale.Contains("版本已过期"), "A stale goal input revision was reused after preparation changed it.");
        }
    }

    private static void RetryAndAcceptance()
    {
        using (var f = new Fixture())
        {
            f.Begin("唱第一段。"); f.Propose(f.first, "current"); f.Approve();
            Check(f.Resolve(f.first, "current", out _), "Baseline plan failed."); f.Completed(f.first);
            f.Begin("第一段的开头还是没有补回来。");
            f.Propose(f.first, "current", "repair", "user_confirmation", "unsatisfied", f.user); f.Approve();
            Check(!f.Resolve(f.first, "current", out string failure) && failure.Contains("没有原样重播"), "Explicit failed-content feedback allowed an unchanged retry.");
            f.Propose(f.first, "current", "perform"); f.Approve();
            Check(!f.Resolve(f.first, "current", out _), "Changing intent in the same repair turn bypassed the rejected plan.");
            f.Propose(f.first, "current", "repeat", evidence: f.user); f.Approve();
            Check(!f.Resolve(f.first, "current", out _), "Same-turn repeat bypassed an already confirmed negative repair verdict.");

            // Revision number changes alone are not evidence that the actual content changed.
            var list = (IList)typeof(SenseVoiceSpeechToText).GetField("m_PracticePhrases", Flags).GetValue(f.sense);
            var phrase = list.Cast<object>().Single(p => (string)p.GetType().GetField("ClipRef").GetValue(p) == f.first);
            var versionField = phrase.GetType().GetField("Revision");
            versionField.SetValue(phrase, (int)versionField.GetValue(phrase) + 1);
            f.Propose(f.first, "current", "repair"); f.Approve();
            Check(!f.Resolve(f.first, "current", out _), "An unchanged source window evaded retry protection through a revision bump.");

            f.Propose(f.first, "expanded", "repair", "user_confirmation"); f.Approve();
            Check(f.Resolve(f.first, "expanded", out _), "A real changed raw window was prevented from repairing content."); f.Completed(f.first);
            f.Begin("我听到了，开头这次完整了。");
            f.Observe("satisfied", f.user); f.Approve();
            Check(f.Context().Contains("\"userConfirmed\":true") && f.Context().Contains("user_confirmed"), "A reviewed explicit user confirmation was not recorded.");

            f.Begin("请按最初那一遍再唱一次。");
            f.Propose(f.first, "clean", "repeat", evidence: f.user);
            Check(!f.Resolve(f.first, "clean", out _), "A new repeat declaration bypassed independent review.");
            f.Approve(); Check(f.Resolve(f.first, "clean", out _), "Legitimate new-user reviewed repetition was blocked.");
        }
    }

    private static void ReviewedTargetMatching()
    {
        using (var f = new Fixture())
        {
            bool Match(JObject target, out string reason)
            {
                object[] args = { target, "" };
                bool matched = (bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", args);
                reason = (string)args[1]; return matched;
            }
            JObject Target(string refs, string range, JToken start = null, JToken end = null) => new JObject {
                ["origin"] = refs.Length == 0 ? "none" : "user_request", ["request_quote"] = refs.Length == 0 ? "" : f.user,
                ["refs"] = refs, ["range"] = range, ["start_seconds"] = start ?? JValue.CreateNull(), ["end_seconds"] = end ?? JValue.CreateNull() };

            f.Begin("第一段は、保存された冒頭を含めてexpandedの範囲で歌って。");
            f.Propose(f.first, "current");
            Check(!Match(Target(f.first, "expanded"), out string rangeFailure) &&
                rangeFailure.Contains("expected_range=expanded") && rangeFailure.Contains("proposal_range=current"),
                "A review falsely calling current expanded was accepted.");
            Check(f.Context().Contains("\"range\":\"current\"") && (bool)Call(f.chat, "HasPendingSingingGoalReview"),
                "Independent target comparison rewrote the role's proposal or approved it.");
            Check(!Match(null, out _), "Missing independent target was accepted.");
            f.Propose(f.first + "," + f.second, "current");
            Check(!Match(Target(f.second + "," + f.first, "current"), out _), "Independent target order was discarded.");
            Check(Match(Target(f.first + ", " + f.second, "current"), out _), "Equivalent ordered references with separator whitespace were rejected.");
            Check(!Match(Target(f.first, "current"), out _), "Independent single-reference target matched a two-reference proposal.");

            object[] window = { $"<sing_goal refs=\"{f.first}\" revisions=\"{f.Phrase(f.first).Revision}\" range=\"window\" start_seconds=\"0.2\" end_seconds=\"2.3\" expected=\"明确原录音窗口\"/>", true };
            Call(f.chat, "ExtractSingingGoalTags", window);
            Check(Match(Target(f.first, "window", new JValue(.2), new JValue(2.3)), out _), "Matching finite window was rejected.");
            Check(!Match(Target(f.first, "window", new JValue(.4), new JValue(2.3)), out string windowFailure) && windowFailure.Contains("window="),
                "Different independently extracted window was approved.");
            Check(!Match(Target(f.first, "window", JValue.CreateNull(), new JValue(2.3)), out _), "Missing window coordinate was accepted.");
            Check(!Match(Target(f.first, "window", new JValue(double.PositiveInfinity), new JValue(2.3)), out _), "Nonfinite window was accepted.");
            Check(!Match(Target(f.first, "window", new JValue("0.2"), new JValue(2.3)), out _), "String coordinates bypassed the numeric review contract.");

            f.Begin("已经听到了。"); f.Observe("satisfied", f.user);
            Check(Match(Target("", "none"), out _), "Observation-only feedback could not match an empty action target.");
            Check(!Match(Target(f.first, "current"), out _), "Observation-only proposal matched a newly requested performance.");
            f.Approve(); Check(!Match(Target("", "none"), out _), "A stale review matched an already handled proposal.");
        }
    }

    private static void Lifecycle()
    {
        using (var f = new Fixture())
        {
            f.Begin("唱第一段。"); f.Propose(f.first, "current"); f.Approve();
            Check(f.Resolve(f.first, "current", out _), "Lifecycle plan failed.");
            Call(f.chat, "CapturePracticePlaybackFacts", f.sense, new List<int> { f.Phrase(f.first).Index });
            f.Begin("现在换成第二段。"); f.Propose(f.second, "current"); f.Approve();
            Call(f.chat, "RecordPracticePlaybackOutcome", true, true);
            Check((bool)Call(f.chat, "IsSingingGoalAwaitingExecution"), "An old playback callback completed a new user's different goal.");
            Check(f.Context().Contains("\"outcome\":\"not_submitted\""), "New goal inherited the old callback's outcome.");
            Call(f.chat, "ClearSingingGoalSession");
            string context = f.Context();
            Check(context.Contains("\"goal\":null") && context.Contains("\"previous_goal\":null") && context.Contains("\"last_playback\":null"), "New session retained old target or playback history.");
            f.Begin("只是反馈一下。"); f.Observe("satisfied", "不是用户说的话");
            Check(!(bool)Call(f.chat, "HasPendingSingingGoalReview"), "Invented user evidence registered satisfaction.");

            f.Begin("只唱第一段。"); f.Propose(f.first, "current"); f.Approve();
            Check(f.Resolve(f.first, "current", out _), "Postflight test could not validate its planned selection.");
            f.Completed(f.second); // Simulate a downstream/legacy route actually selecting the wrong clip.
            Check(f.Context().Contains("playback_goal_mismatch") && (bool)Call(f.chat, "IsSingingGoalAwaitingExecution"),
                "Actual playback of the wrong material was recorded as completing the planned target.");

            Set(f.chat, "m_WorkReviewOpen", false); // Budget exhaustion must not confer autonomous ownership.
            f.Propose(f.second, "current");
            Check(!(bool)Call(f.chat, "HasAutonomousSingingGoal"), "Changing a user goal after work closure created a fresh autonomous allowance.");
            Call(f.chat, "ClearSingingGoalSession");
            f.Propose(f.first, "current");
            Check((bool)Call(f.chat, "HasAutonomousSingingGoal"), "A genuinely new idle-session proposal did not own an autonomous goal.");
            Call(f.chat, "ClearSingingGoalSession"); Set(f.chat, "m_WorkReviewOpen", false); Set(f.chat, "m_WorkStatus", "blocked");
            f.Propose(f.first, "current");
            Check(!(bool)Call(f.chat, "HasAutonomousSingingGoal") && !(bool)Call(f.chat, "HasPendingSingingGoalReview") &&
                ((string)Get(f.chat, "m_LastPracticeEditResult")).Contains("续接上限"),
                "A blocked user turn with no prior valid goal gained a new autonomous allowance.");
            f.Begin("这是新的请求，现在唱第一段。"); f.Propose(f.first, "current");
            Check((bool)Call(f.chat, "HasPendingSingingGoalReview") && !(bool)Call(f.chat, "HasAutonomousSingingGoal"),
                "A real new user request could not register a fresh goal after the old budget was exhausted.");
        }
    }

    private static void ExportPublicModelCases()
    {
        var cases = new JArray();
        using (var f = new Fixture())
        {
            const string original = "请把第一段和第二段按录音先后连起来唱一次。";
            f.Begin(original);
            f.Begin("还是没听到你唱出来。 为什么？");
            f.Begin("检查一下唱歌技能，这次也没听到。");
            Set(f.chat, "m_WorkReviewOpen", false); // Reproduce an incorrect role origin, not an autonomous user instruction.
            f.Propose(f.first + "," + f.second, "current", expected: "按最初要求第一段随后第二段完整回唱");
            AddModelCase(cases, f, "zh_original_request_followups_mislabeled_autonomous", "approved", new JObject {
                ["origin"] = "user_request", ["request_quote"] = original, ["expected_source_user_turn"] = 1,
                ["requested_refs"] = new JArray(f.first, f.second), ["requested_range"] = "current",
                ["content_constraint"] = "The role accidentally labeled its draft autonomous after two no-audio follow-ups. Original explicit user request was never fulfilled or cancelled. Recover the original actual request quote and full ordered pair, independently of the incorrect candidate origin." });
        }
        using (var f = new Fixture())
        {
            f.Begin("请把第一段唱给我听。");
            f.Begin("先别唱了，这次取消唱歌，只回答我发生了什么。");
            f.Propose(f.first, "current");
            AddModelCase(cases, f, "zh_prior_request_explicitly_cancelled_before_registration", "cancelled", new JObject {
                ["origin"] = "none", ["requested_refs"] = new JArray(),
                ["content_constraint"] = "An older actual request exists, but the latest user explicitly cancelled it before any goal was registered. Retention must not reactivate that old request." });
        }
        using (var f = new Fixture())
        {
            BaselinePair(f);
            f.Begin("你刚才漏了第一段开头。现在只唱第一段，把保留的开头补回来，不要唱第二段。");
            f.Propose(f.first + "," + f.second, "current", "repair", "user_confirmation", "unsatisfied", f.user);
            AddModelCase(cases, f, "zh_wrong_pair_and_current", "revise", new JObject {
                ["requested_refs"] = new JArray(f.first), ["requested_range"] = "expanded",
                ["content_constraint"] = "Only the first previously played segment, including its retained intro; exclude second segment." });
        }
        using (var f = new Fixture())
        {
            BaselinePair(f);
            f.Begin("Please sing only the first segment again, this time including its retained intro. Use the expanded recording range and wait for me to confirm the intro is complete.");
            f.Propose(f.first, "expanded", "repair", "user_confirmation");
            AddModelCase(cases, f, "en_correct_single_expanded_repair", "approved", new JObject {
                ["requested_refs"] = new JArray(f.first), ["requested_range"] = "expanded",
                ["completion"] = "user_confirmation", ["content_constraint"] = "The retained intro must be included; user confirmation, not playback alone, verifies completion." });
        }
        using (var f = new Fixture())
        {
            BaselinePair(f);
            f.Begin("最初の一節だけ、保存されている冒頭も含めて expanded の範囲で歌って。二番目は要らない。");
            f.Propose(f.first, "current", "repair", "user_confirmation");
            AddModelCase(cases, f, "ja_says_expanded_but_goal_current", "revise", new JObject {
                ["requested_refs"] = new JArray(f.first), ["requested_range"] = "expanded",
                ["content_constraint"] = "Role says expanded, but actual proposed range remains current; do not treat speech as a control parameter." },
                "expanded の範囲で冒頭も入れるね。<sing refs=\"" + f.first + "\" reason=\"expandedを使って冒頭を補う\"/>");
        }
        using (var f = new Fixture())
        {
            BaselinePair(f);
            f.Begin("Change of plan: perform only the first segment you just sang, using its current range. Do not include the second segment.");
            f.Propose(f.first + "," + f.second, "current");
            AddModelCase(cases, f, "en_latest_single_overrides_prior_pair", "revise", new JObject {
                ["requested_refs"] = new JArray(f.first), ["requested_range"] = "current",
                ["content_constraint"] = "Latest correction replaces the prior pair goal with only the first actually played segment." });
        }
        using (var f = new Fixture())
        {
            BaselinePair(f);
            f.Begin("さっきの歌、ちゃんと聞こえたし良かった。最初の一節だけ、今と同じ範囲でもう一度歌って。");
            f.Propose(f.first, "current", "repeat", evidence: f.user);
            AddModelCase(cases, f, "ja_explicit_legitimate_repeat", "approved", new JObject {
                ["requested_refs"] = new JArray(f.first), ["requested_range"] = "current", ["intent"] = "repeat",
                ["content_constraint"] = "User positively acknowledges the previous singing and explicitly asks to repeat only its first segment unchanged." });
        }
        using (var f = new Fixture())
        {
            BaselinePair(f);
            f.Begin("还是没有补回第一段开头。我不是说修好了，也没有叫你把相同的版本再唱一遍。");
            f.Propose(f.first, "current", "repeat", "playback", "satisfied", f.user);
            AddModelCase(cases, f, "zh_negative_feedback_mislabeled_repeat_satisfied", "revise", new JObject {
                ["intent"] = "repair", ["feedback"] = "unsatisfied", ["content_constraint"] = "User explicitly denies successful repair and denies authorizing the same unchanged replay." });
        }
        using (var f = new Fixture())
        {
            BaselinePair(f);
            f.Begin("第一段开头仍然漏掉了。只把那段保留的开头补回来，音高和速度保持原样，不要用调音代替补内容。");
            f.Propose(f.first, "current", "repair", "user_confirmation", "unsatisfied", f.user, "music");
            AddModelCase(cases, f, "zh_content_repair_mislabeled_music", "revise", new JObject {
                ["requested_refs"] = new JArray(f.first), ["requested_range"] = "expanded", ["repair_scope"] = "content",
                ["content_constraint"] = "Restore the first segment's retained intro. User explicitly wants unchanged pitch/speed; a music-scope repair is a misclassification." },
                "我会试着调整音高。<sing refs=\"" + f.first + "\" pitch_plan=\"+half_step\"/>");
        }
        using (var f = new Fixture())
        {
            BaselinePair(f);
            f.Begin("Keep the first segment's current recording range, but sing it one semitone higher. Its content is already complete; only the pitch should change.");
            f.Propose(f.first, "current", "perform", "playback", repairScope: "music",
                expected: "Only the first segment, unchanged current source window and content, raised one semitone from the previous performance.");
            AddModelCase(cases, f, "en_real_pitch_adjustment_music", "approved", new JObject {
                ["requested_refs"] = new JArray(f.first), ["requested_range"] = "current", ["repair_scope"] = "music",
                ["requested_pitch_plan"] = "+half_step", ["content_constraint"] = "Only pitch changes; the existing source window/content must be retained." },
                "I will raise only the first segment by one semitone. <sing refs=\"" + f.first + "\" pitch_plan=\"+half_step\"/>");
        }
        using (var f = new Fixture())
        {
            BaselinePair(f);
            f.Begin("我来试着唱一小段吧。青い空。静かな夜。");
            f.Propose(f.first, "current");
            AddModelCase(cases, f, "zh_user_performed_not_requested", "cancelled", new JObject {
                ["origin"]="none", ["requested_refs"]=new JArray(),
                ["content_constraint"]="The user announced their own singing and then sang. They did not ask the role to sing. Cancel the role's invented user task." }, "明白，我现在就唱。");
        }
        using (var f = new Fixture())
        {
            BaselinePair(f);
            f.Begin("今の二つの歌詞は私が歌ったものだよ。あなたは歌わず、聞こえた内容について話して。");
            f.Propose(f.first, "current");
            AddModelCase(cases, f, "ja_discuss_heard_song_no_performance", "cancelled", new JObject {
                ["origin"]="none", ["requested_refs"]=new JArray(),
                ["content_constraint"]="User requests conversation about their own completed singing and explicitly does not request a role performance." });
        }
        using (var f = new Fixture())
        {
            f.Begin("I have finished singing the first phrase. Now please sing that first recording back to me using its current range.");
            f.Propose(f.first, "current");
            AddModelCase(cases, f, "en_heard_then_explicit_echo_request", "approved", new JObject {
                ["origin"]="user_request", ["requested_refs"]=new JArray(f.first), ["requested_range"]="current",
                ["content_constraint"]="User performed and also explicitly requested the role to sing; preserving perception must not block this distinct action request." });
        }
        using (var f = new Fixture())
        {
            f.Begin("刚才那段是我唱给你听的。你可以自由决定接下来做什么。");
            Set(f.chat, "m_WorkReviewOpen", false);
            f.Propose(f.first, "current", expected: "我自己想用第一段的现有范围回应刚才的分享；这是自主选择，不是用户要求我唱。");
            AddModelCase(cases, f, "zh_explicit_autonomous_proposal", "approved", new JObject {
                ["origin"]="autonomous", ["requested_refs"]=new JArray(f.first), ["requested_range"]="current",
                ["content_constraint"]="The role explicitly proposes an autonomous performance within the allowed scope. This is not an outstanding user performance request." });
        }
        using (var f = new Fixture())
        {
            const string original = "请把第一段和第二段按录音先后连起来唱一次。";
            f.Begin(original); // No proposal reached registration: simulated earlier output-format failure.
            f.Begin("还是没听到你唱出来，也没有语音回复，为什么？");
            f.Propose(f.second, "current", expected: "只唱第二段");
            AddModelCase(cases, f, "zh_lost_pair_request_followup_wrong_single", "revise", new JObject {
                ["origin"] = "user_request", ["request_quote"] = original,
                ["requested_refs"] = new JArray(f.first, f.second), ["requested_range"] = "current",
                ["content_constraint"] = "Original actual input requests both clips once in recording order. No earlier executable goal was registered. The later why/no-audio question does not replace it with the second clip. Recover the original request from recent_actual_user_inputs and reject this singleton proposal." },
                "我会重新处理未完成的演唱。<sing refs=\"" + f.second + "\"/>");
        }
        using (var f = new Fixture())
        {
            const string original = "请把第一段和第二段按录音先后连起来唱一次。";
            f.Begin(original);
            f.Begin("还是没听到你唱出来，也没有语音回复，为什么？");
            f.Propose(f.first + "," + f.second, "current", expected: "第一段随后第二段，按原要求完整连唱一次");
            AddModelCase(cases, f, "zh_lost_pair_request_followup_correct_pair", "approved", new JObject {
                ["origin"] = "user_request", ["request_quote"] = original,
                ["requested_refs"] = new JArray(f.first, f.second), ["requested_range"] = "current",
                ["content_constraint"] = "Recover the actual earlier pair request even though the prior output failed before any goal registration. The latest why/no-audio input is a follow-up; this candidate preserves both references and order, so approve it using the original request quote." });
        }
        using (var f = new Fixture())
        {
            f.Begin("请把第一段和第二段连起来唱。");
            f.Propose(f.first + "," + f.second, "current"); f.Approve();
            f.Begin("改变计划，不需要第一段了，现在只唱第二段。");
            f.Propose(f.second, "current", expected: "按用户明确改变后的要求，仅唱第二段一次");
            AddModelCase(cases, f, "zh_explicit_pair_to_single_change", "approved", new JObject {
                ["origin"] = "user_request", ["request_quote"] = f.user,
                ["requested_refs"] = new JArray(f.second), ["requested_range"] = "current",
                ["content_constraint"] = "An unfinished committed pair exists, but this latest user input explicitly changes the goal to only the second segment. Candidate transition=revise and change_quote cite that actual instruction. Approve the deliberate scope change; persistence must not force the cancelled first segment." });
        }
        string path = Path.GetFullPath("Tools/MotionAdapter/reports/agent-singing-goal-public-model-cases.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, new JObject { ["case_count"] = cases.Count,
            ["scope"] = "Public synthetic recordings and utterances, actual production prompt/goal/inventory/played-fact builders. Ground truth is separate from the model prompt. No private persona/history and no network requests.",
            ["cases"] = cases }.ToString());
    }

    private static object ParsedSing(Fixture f, string refs = null, string range = "current")
    {
        object[] args = { $"<sing refs=\"{refs ?? f.first}\" range=\"{range}\"/>" };
        return Call(f.chat, "ExtractHumBackTag", args);
    }

    private static void DeclareDependentSing(Fixture f)
    {
        object[] args = { $"<speech mode=\"after_action\"/><sing refs=\"{f.first}\"/>" };
        Call(f.chat, "ObserveSingingSpeechCompletion", args);
    }

    private static void ApproveIndependentTarget(Fixture f, string quote = null)
    {
        var goal = JObject.FromObject(Get(f.chat, "m_SingingGoal"));
        object[] args = { new JObject { ["origin"] = "user_request", ["request_quote"] = quote ?? f.user,
            ["refs"] = goal["refs"], ["range"] = goal["range"], ["start_seconds"] = null, ["end_seconds"] = null }, "" };
        Check((bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", args), "Independent target fixture mismatch: " + args[1]);
        f.Approve();
    }

    private static void StructuredFailuresAndDeferredExecution()
    {
        bool Detailed(Fixture f, object request, string code)
        {
            object[] args = { request, null };
            bool ok = (bool)Call(f.chat, "TryResolveUnifiedSingDetailed", args);
            Check(!ok && args[1] != null && (string)JObject.FromObject(args[1])["code"] == code,
                "Wrong structured rejection for " + code + ": " + (args[1] == null ? "none" : JObject.FromObject(args[1]).ToString()));
            return ok;
        }
        using (var f = new Fixture())
        {
            f.Begin("请唱第一段。");
            Detailed(f, ParsedSing(f), "goal_required");
            f.Propose(f.first, "current");
            Detailed(f, ParsedSing(f), "goal_review_pending");
            f.Approve();
            Detailed(f, ParsedSing(f, f.second), "goal_target_mismatch");
            Detailed(f, ParsedSing(f, f.first, "expanded"), "goal_target_mismatch");
            Detailed(f, ParsedSing(f, "clip:missing"), "material_reference_missing");
            Check(f.Resolve(f.first, "current", out _), "Structured success did not preserve old resolver compatibility.");
            f.Completed(f.first);
            Detailed(f, ParsedSing(f), "goal_already_played");
        }
        using (var f = new Fixture())
        {
            f.Begin("把第一段唱给我听。"); f.Propose(f.first, "current");
            object[] emptyTake = { null };
            Check(!(bool)Call(f.chat, "TryTakeReviewedSingingRequest", emptyTake), "Goal alone invented an execution request.");
            DeclareDependentSing(f);
            object explicitRequest = ParsedSing(f);
            object[] deferred = { explicitRequest, null };
            int revision = f.Phrase(f.first).Revision;
            Check((bool)Call(f.chat, "TryDeferSingingUntilGoalReview", deferred), "Legal same-response sing did not wait for review.");
            Check(f.Phrase(f.first).Revision == revision && Get(f.chat, "m_PendingSingingGoalPlayback") == null,
                "Waiting for review prepared/played material.");
            Check(!(bool)Call(f.chat, "TryTakeReviewedSingingRequest", emptyTake), "Pending review was treated as approval.");
            explicitRequest.GetType().GetField("ClipRefs").SetValue(explicitRequest, f.second);
            Check(!(bool)Call(f.chat, "TryDeferSingingUntilGoalReview", new object[] { ParsedSing(f), null }), "A second request replaced the first waiting request.");
            ApproveIndependentTarget(f);
            int generation = (int)Get(f.chat, "m_FormalResponseGeneration");
            Check((bool)Call(f.chat, "TryTakeReviewedSingingRequest", emptyTake), "Independent approval could not release the exact saved request.");
            Check((string)emptyTake[0].GetType().GetField("ClipRefs").GetValue(emptyTake[0]) == f.first,
                "Mutating the caller's request changed the saved target.");
            Check((int)Get(f.chat, "m_FormalResponseGeneration") == generation, "Review-to-take changed the formal generation.");
            object[] resolve = { emptyTake[0], null };
            Check((bool)Call(f.chat, "TryResolveUnifiedSingDetailed", resolve), "Released request failed the full production resolver.");
            Check(!(bool)Call(f.chat, "TryTakeReviewedSingingRequest", new object[] { null }), "Saved explicit sing was consumed twice.");
        }
        foreach (string fence in new[] { "review_rejected", "approved_without_target", "user_turn", "epoch", "generation", "model", "goal_version", "disabled", "interrupted", "session" })
        using (var f = new Fixture())
        {
            f.Begin("请唱第一段。"); f.Propose(f.first, "current"); DeclareDependentSing(f);
            Check((bool)Call(f.chat, "TryDeferSingingUntilGoalReview", new object[] { ParsedSing(f), null }), "Fence fixture could not defer: " + fence);
            if (fence == "approved_without_target") f.Approve();
            else if (fence == "review_rejected") Call(f.chat, "ApplySingingGoalReview", "waiting_user", "需要询问用户具体范围。");
            else ApproveIndependentTarget(f);
            if (fence == "user_turn") f.Begin("先别唱，回答问题。");
            if (fence == "epoch") Set(f.chat, "m_WorkEpoch", (int)Get(f.chat, "m_WorkEpoch") + 1);
            if (fence == "generation") Set(f.chat, "m_FormalResponseGeneration", (int)Get(f.chat, "m_FormalResponseGeneration") + 1);
            if (fence == "model") ((ChatSetting)Get(f.chat, "m_ChatSettings")).m_ChatModel = f.host.AddComponent<ChatQW>();
            if (fence == "goal_version")
            { object goal = Get(f.chat, "m_SingingGoal"); var field = goal.GetType().GetField("version"); field.SetValue(goal, (int)field.GetValue(goal) + 1); }
            if (fence == "disabled") f.chat.enabled = false;
            if (fence == "interrupted") Set(f.chat, "m_UserSpeechActiveForAutonomy", true);
            if (fence == "session") Call(f.chat, "ClearSingingGoalSession");
            Check(!(bool)Call(f.chat, "TryTakeReviewedSingingRequest", new object[] { null }), "Deferred sing escaped fence: " + fence);
        }
        foreach (string invalid in new[] { "independent", "different_range", "different_ref", "later_round", "rejected_phase" })
        using (var f = new Fixture())
        {
            f.Begin("请唱第一段。"); f.Propose(f.first, "current"); DeclareDependentSing(f);
            if (invalid == "independent") Call(f.chat, "ObserveSingingSpeechPhase", new SpeechText("", actionDependency: SpeechActionDependency.Independent));
            if (invalid == "later_round") Set(f.chat, "m_CurrentSpeechRoundId", (int)Get(f.chat, "m_CurrentSpeechRoundId") + 1);
            if (invalid == "rejected_phase") Set(f.chat, "m_SingingRejectedSpeechRound", (int)Get(f.chat, "m_CurrentSpeechRoundId"));
            object req = ParsedSing(f, invalid == "different_ref" ? f.second : f.first, invalid == "different_range" ? "expanded" : "current");
            Check(!(bool)Call(f.chat, "TryDeferSingingUntilGoalReview", new object[] { req, null }), "Invalid request was retained: " + invalid);
        }
    }

    private static void StreamCompletionAndDeferredExecution()
    {
        foreach (bool autonomous in new[] { false, true })
        using (var f = new Fixture())
        {
            f.Begin(autonomous ? "随意休息。" : "请唱第一段。保持原来的范围。");
            if (autonomous) Set(f.chat, "m_WorkReviewOpen", false);
            var textHost = new GameObject("InactiveStreamResult", typeof(RectTransform));
            textHost.transform.SetParent(f.host.transform);
            Set(f.chat, "m_TextBack", textHost.AddComponent<UnityEngine.UI.Text>());
            Set(f.chat, "m_CurrentSpeechRoundId", 42);
            Set(f.chat, "m_SpeechRoundSequence", 42);
            Set(f.chat, "m_FormalResponseGeneration", 17);
            Set(f.chat, "m_AgentRoundInFlight", true);
            int epoch = (int)Get(f.chat, "m_WorkEpoch");
            int revision = f.Phrase(f.first).Revision;
            string raw = "<speech mode=\"after_action\"/><lang code=\"ja\"/>確認してから歌うね。" +
                $"<sing_goal refs=\"{f.first}\" revisions=\"{revision}\" range=\"current\" " +
                $"origin=\"{(autonomous ? "autonomous" : "user_request")}\" expected=\"第一段当前范围\"/>" +
                $"<sing refs=\"{f.first}\" range=\"current\"/><continue/>";
            var output = RoleOutputChannels.Parse(raw);
            Check(!output.HasInvalidSpeechPhase, "Public same-response goal/action was rejected before dispatch.");
            Call(f.chat, "OnStreamComplete", output.ToExecutableText(true, true));
            Check(Get(f.chat, "m_DeferredSingingRequest") != null && (bool)Call(f.chat, "HasPendingSingingGoalReview"),
                "Real OnStreamComplete/QueueHumBack did not preserve its explicit sing until review.");
            Check((int)Get(f.chat, "m_CurrentSpeechRoundId") == 42 &&
                (int)Get(f.chat, "m_FormalResponseGeneration") == 17 && (int)Get(f.chat, "m_WorkEpoch") == epoch,
                "Finishing a pending response advanced the request fence or dispatched a new formal generation.");
            Check(!(bool)Get(f.chat, "m_AgentRoundInFlight") && (bool)Get(f.chat, "m_TTSSenderDone") &&
                !(bool)Get(f.chat, "m_RoundContinue") && (string)Get(f.chat, "m_ScheduledWakeReason") == "singing-goal-review",
                "Real stream completion neither drained speech nor scheduled independent goal review.");
            Check(!(bool)Get(f.chat, "m_ToolCorrectionContinuationPending") &&
                ((IDictionary)Get(f.chat, "m_RejectedWorkAttempts")).Count == 0 &&
                !(bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn") && f.Phrase(f.first).Revision == revision,
                "Pending review was counted as an executor failure, a submitted request or a material mutation.");
            Check((bool)Get(f.chat, "m_SingingSpeechWithheld") &&
                !((List<string>)Get(f.chat, "m_ChatHistory")).Any(s => s.Contains("確認してから歌う")),
                "Unapproved dependent speech was recorded as heard.");
            Check(!(bool)Call(f.chat, "IsSingingRuntimeActivityActive"), "Waiting for review started audio runtime activity.");

            // Inject an independent verdict through the same normalization and application
            // methods used by the callback, without invoking a model or an audio backend.
            object review = Activator.CreateInstance(typeof(ChatSample).GetNestedType("AutonomyIntentDecision", BindingFlags.NonPublic), true);
            review.GetType().GetField("singing_goal_status").SetValue(review, "approved");
            review.GetType().GetField("singing_goal_evidence").SetValue(review, "公开测试中的独立审核结果。");
            review.GetType().GetField("singing_goal_expected").SetValue(review, new JObject {
                ["origin"] = autonomous ? "autonomous" : "user_request", ["request_quote"] = autonomous ? "" : f.user,
                ["refs"] = f.first, ["range"] = "current", ["start_seconds"] = JValue.CreateNull(), ["end_seconds"] = JValue.CreateNull() });
            Call(f.chat, "NormalizeSingingGoalReview", review);
            Call(f.chat, "ApplySingingGoalReview", review.GetType().GetField("singing_goal_status").GetValue(review),
                review.GetType().GetField("singing_goal_evidence").GetValue(review));
            object[] taken = { null };
            Check((bool)Call(f.chat, "TryTakeReviewedSingingRequest", taken),
                "Real stream completion invalidated its own explicit request before independent approval.");
            object[] resolved = { taken[0], null };
            Check((bool)Call(f.chat, "TryResolveUnifiedSingDetailed", resolved),
                "Reviewed request from real stream completion failed full execution preflight.");
            Check(!(bool)Call(f.chat, "TryTakeReviewedSingingRequest", new object[] { null }), "Stream request was consumed twice.");
        }
    }

    private static void InitialRequestSourceRecovery()
    {
        using (var f = new Fixture())
        {
            const string original = "请把第一段和第二段依次唱给我听。";
            f.Begin(original);
            for (int i = 0; i < 8; i++) f.Begin("还是没听到，第" + i + "次检查。");
            Set(f.chat, "m_WorkReviewOpen", false);
            f.Propose(f.first + "," + f.second, "current");
            Check((bool)Call(f.chat, "HasAutonomousSingingGoal"), "Fixture did not reproduce the mistaken candidate origin.");
            ApproveIndependentTarget(f, original);
            var goal = JObject.FromObject(Get(f.chat, "m_ActiveSingingGoal"));
            Check((string)goal["origin"] == "user_request" && (string)goal["requestQuote"] == original && (int)goal["sourceUserTurn"] == 1,
                "Independent exact target/actual-user quote could not correct an initial mistaken origin.");
            Check((string)goal["refs"] == f.first + "," + f.second && (string)goal["range"] == "current",
                "Source correction changed refs/order/range.");
        }
        using (var f = new Fixture())
        {
            f.Begin("刚才我唱了一段，你不用唱。"); Set(f.chat, "m_WorkReviewOpen", false);
            f.Propose(f.first, "current");
            object[] invented = { new JObject { ["origin"] = "user_request", ["request_quote"] = "请唱第一段",
                ["refs"] = f.first, ["range"] = "current" }, "" };
            Check(!(bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", invented), "Invented user quote converted autonomy into user authority.");
            object review = Activator.CreateInstance(typeof(ChatSample).GetNestedType("AutonomyIntentDecision", BindingFlags.NonPublic), true);
            review.GetType().GetField("singing_goal_status").SetValue(review, "approved");
            review.GetType().GetField("singing_goal_expected").SetValue(review, new JObject { ["origin"] = "none", ["request_quote"] = "", ["refs"] = "", ["range"] = "none" });
            Call(f.chat, "NormalizeSingingGoalReview", review);
            Check((string)review.GetType().GetField("singing_goal_status").GetValue(review) == "cancelled",
                "Review origin=none was upgraded into execution authority.");
        }
    }

    private static void RecoveredGoalContinuationAndPolicySpeech()
    {
        object ContinueReview()
        {
            object review = Activator.CreateInstance(typeof(ChatSample).GetNestedType("AutonomyIntentDecision", BindingFlags.NonPublic), true);
            review.GetType().GetField("work_status").SetValue(review, "continue");
            return review;
        }
        foreach (string blocked in new[] { "none", "closed", "waiting_user", "blocked", "epoch", "new_turn", "completed", "no_progress", "not_independently_reviewed" })
        using (var f = new Fixture())
        {
            const string original = "请唱第一段的当前范围。";
            f.Begin(original); f.Begin("还没有听到。"); Set(f.chat, "m_WorkReviewOpen", false);
            f.Propose(f.first, "current");
            ApproveIndependentTarget(f, original);
            if (blocked == "not_independently_reviewed")
                Get(f.chat, "m_SingingGoal").GetType().GetField("targetReviewed").SetValue(Get(f.chat, "m_SingingGoal"), false);
            Set(f.chat, "m_WorkContinuations", 1); Set(f.chat, "m_AutonomousGoalContinuations", 1);
            if (blocked == "closed" || blocked == "waiting_user" || blocked == "blocked") Set(f.chat, "m_WorkStatus", blocked);
            if (blocked == "epoch") Set(f.chat, "m_WorkEpoch", (int)Get(f.chat, "m_WorkEpoch") + 1);
            if (blocked == "new_turn") f.Begin("不用唱了。");
            if (blocked == "completed")
            {
                Check(f.Resolve(f.first, "current", out _), "Completed-boundary fixture failed real source/goal preflight.");
                f.Completed(f.first);
                Check((string)JObject.FromObject(Get(f.chat, "m_ActiveSingingGoal"))["outcome"] == "playback_complete_content_unverified",
                    "Completed-boundary fixture did not produce a matching real playback-completion fact.");
            }
            if (blocked == "no_progress") Set(f.chat, "m_WorkNoProgress", true);
            bool continuation = (bool)Call(f.chat, "CanContinueAutonomousSingingGoal", ContinueReview());
            Check(continuation == (blocked == "none"), "Recovered user goal continuation bypassed its boundary: " + blocked);
            if (blocked != "none") continue;
            Check(!(bool)Get(f.chat, "m_WorkReviewOpen") && (int)Get(f.chat, "m_WorkContinuations") == 1 &&
                (int)Get(f.chat, "m_AutonomousGoalContinuations") == 1,
                "Source correction reopened old user work or reset its continuation allowance.");
            Check(((string)Call(f.chat, "BuildWorkContinuationContext")).Contains("来源已独立核对为用户请求"),
                "Recovered user goal was still described to the role as its own autonomous proposal.");
            Check((bool)Call(f.chat, "ConsumeWorkChainBudget") &&
                !(bool)Call(f.chat, "CanContinueAutonomousSingingGoal", ContinueReview()) &&
                !(bool)Call(f.chat, "ConsumeWorkChainBudget"), "Recovered source obtained a fresh or unbounded retry allowance.");
        }
        using (var f = new Fixture())
        {
            Set(f.chat, "m_AgentRunning", false);
            Set(f.chat, "m_CurrentSpeechRoundId", 3);
            Call(f.chat, "ObserveSingingSpeechPhase", new SpeechText("", actionDependency: SpeechActionDependency.Independent));
            Check(!(bool)Call(f.chat, "HoldSingingSpeechUntilValidation"), "Ordinary direct-conversation speech was delayed.");
            Set(f.chat, "m_SingingPolicyDeferredSpeechRound", 3);
            Check((bool)Call(f.chat, "HoldSingingSpeechUntilValidation"), "Direct-conversation policy candidate leaked its old promise.");
            object[] speech = { "以后我会自动唱歌。" };
            Call(f.chat, "ResolveSingingSpeech", speech);
            Check((string)speech[0] == "" && (bool)Get(f.chat, "m_SingingSpeechWithheld"),
                "Policy candidate or immediate rejection released unverified direct-conversation speech.");
            Set(f.chat, "m_CurrentSpeechRoundId", 4);
            Check(!(bool)Call(f.chat, "HoldSingingSpeechUntilValidation"), "A settled policy candidate silenced the next ordinary reply.");
        }
    }

    private static void PersistentFollowups()
    {
        using (var f = new Fixture())
        {
            string original = "把第一段和第二段依次连起来唱。";
            f.Begin(original); // The original model response failed before registering any goal.
            f.Begin("没有唱出来，是什么原因？");
            f.Propose(f.first + "," + f.second, "current");
            object[] reviewTarget = { new JObject { ["origin"] = "user_request", ["request_quote"] = original,
                ["refs"] = f.first + "," + f.second, ["range"] = "current", ["start_seconds"] = null, ["end_seconds"] = null }, "" };
            Check(f.Context().Contains(original) && (bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", reviewTarget),
                "An initial format failure made the actual earlier user request unavailable for recovery.");
        }
        using (var f = new Fixture())
        {
            f.Begin("请将第一段和第二段按这个顺序连起来唱一次。");
            f.Propose(f.first + "," + f.second, "current", expected: "第一段随后第二段，完整顺序连唱一次"); f.Approve();
            var before = JObject.Parse(f.Context().Substring(f.Context().IndexOf('{')));
            string ordered = (string)before["active_goal"]["refs"];
            int version = (int)before["active_goal"]["version"];
            f.Begin("还是没听到你唱出来，也没有语音回复，为什么？");
            Check(Get(f.chat, "m_SingingGoal") == null && (bool)Call(f.chat, "IsActiveSingingGoalUnfinished"),
                "Follow-up did not suspend execution while retaining the unfinished objective.");
            Check(!f.Resolve(f.second, "current", out _), "Follow-up itself authorized singing the recent clip.");
            object[] shrink = { $"<sing_goal refs=\"{f.second}\" revisions=\"{f.Phrase(f.second).Revision}\" expected=\"唱第二段\"/>", true };
            Call(f.chat, "ExtractSingingGoalTags", shrink);
            Check(Get(f.chat, "m_SingingGoal") == null && f.Context().Contains(ordered),
                "An implicit singleton proposal silently replaced the committed pair.");
            Check(!(bool)Call(f.chat, "HasPendingSingingGoalReview"), "A dropped first clip reached the semantic approval stage.");
            object[] malformed = { $"<sing_goal transition=\"revise\" refs=\"{ordered}\" revisions=\"garbage\" expected=\"保持两段\" change_quote=\"{f.user}\"/>", true };
            Call(f.chat, "ExtractSingingGoalTags", malformed);
            Check(Get(f.chat, "m_SingingGoal") == null && (int)JObject.FromObject(Get(f.chat, "m_ActiveSingingGoal"))["version"] == version,
                "Malformed revision destroyed or revised the active obligation.");
            object[] resume = { "<sing_goal transition=\"continue\"/>", true };
            Call(f.chat, "ExtractSingingGoalTags", resume);
            var proposal = JObject.FromObject(Get(f.chat, "m_SingingGoal"));
            Check((string)proposal["refs"] == ordered && (int)proposal["baseVersion"] == version &&
                (string)proposal["expected"] == "第一段随后第二段，完整顺序连唱一次",
                "Continue did not bind the original refs/order/success conditions.");
            object[] target = { new JObject { ["origin"] = "user_request", ["request_quote"] = (string)before["active_goal"]["requestQuote"],
                ["refs"] = ordered, ["range"] = "current", ["start_seconds"] = null, ["end_seconds"] = null }, "" };
            Check((bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", target), "Original approved request could not be inherited for a failure follow-up.");
            ((JObject)target[0])["refs"] = f.second;
            Check(!(bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", target), "Reviewer-approved singleton evaded the persistent full-target check.");
            f.Approve();
            Check(f.Resolve(ordered, "current", out _), "The retained ordered pair could not reach the production resolver.");
            f.Completed(f.first, f.second);
            Check(!f.Resolve(ordered, "current", out _) && !(bool)Call(f.chat, "IsActiveSingingGoalUnfinished"),
                "Completed pair automatically executed twice or did not complete the objective.");
        }
        using (var f = new Fixture())
        {
            f.Begin("把两段连唱。"); f.Propose(f.first + "," + f.second, "current"); f.Approve();
            f.Begin("先别唱了，取消这个任务。");
            object[] cancellation = { $"<sing_goal transition=\"cancel\" change_quote=\"{f.user}\"/>", true };
            Call(f.chat, "ExtractSingingGoalTags", cancellation);
            object review = Activator.CreateInstance(typeof(ChatSample).GetNestedType("AutonomyIntentDecision", BindingFlags.NonPublic), true);
            var type = review.GetType();
            type.GetField("singing_goal_status").SetValue(review, "cancelled");
            type.GetField("singing_goal_evidence").SetValue(review, "用户明确取消原两段演唱任务。");
            type.GetField("singing_goal_expected").SetValue(review, new JObject { ["origin"] = "none", ["request_quote"] = f.user,
                ["refs"] = "", ["range"] = "none", ["start_seconds"] = null, ["end_seconds"] = null });
            Call(f.chat, "NormalizeSingingGoalReview", review);
            Call(f.chat, "ApplySingingGoalReview", type.GetField("singing_goal_status").GetValue(review), type.GetField("singing_goal_evidence").GetValue(review));
            Check(!(bool)Call(f.chat, "IsActiveSingingGoalUnfinished") && !f.Resolve(f.first + "," + f.second, "current", out _),
                "Explicit reviewed cancellation did not fence the original task.");
            f.Begin("换个任务，现在只唱第二段。"); f.Propose(f.second, "current"); f.Approve();
            Check(f.Resolve(f.second, "current", out _), "A distinct new request after cancellation was blocked.");
        }
        using (var f = new Fixture())
        {
            f.Begin("唱两段。"); f.Propose(f.first + "," + f.second, "current"); f.Approve();
            f.Begin("改成只唱第二段。");
            object[] stale = { $"<sing_goal transition=\"replace\" base_version=\"99999\" refs=\"{f.second}\" expected=\"只唱第二段\" change_quote=\"{f.user}\"/>", true };
            Call(f.chat, "ExtractSingingGoalTags", stale);
            Check(Get(f.chat, "m_SingingGoal") == null && (bool)Call(f.chat, "IsActiveSingingGoalUnfinished"), "Stale replacement overwrote active target.");
            object[] replacement = { $"<sing_goal transition=\"replace\" refs=\"{f.second}\" expected=\"只唱第二段\" change_quote=\"{f.user}\"/>", true };
            Call(f.chat, "ExtractSingingGoalTags", replacement);
            Check((string)JObject.FromObject(Get(f.chat, "m_SingingGoal"))["revisions"] == f.Phrase(f.second).Revision.ToString(),
                "Omitted mechanical revisions did not bind actual retained material versions.");
            f.Approve(); Check(f.Resolve(f.second, "current", out _), "Explicitly replaced new objective failed.");
            // Simulate a revision arriving after preparation but before playback capture.
            var materials = (IList)typeof(SenseVoiceSpeechToText).GetField("m_PracticePhrases", Flags).GetValue(f.sense);
            object material = materials.Cast<object>().Single(p => (string)p.GetType().GetField("ClipRef").GetValue(p) == f.second);
            var revisionField = material.GetType().GetField("Revision");
            revisionField.SetValue(material, (int)revisionField.GetValue(material) + 1);
            f.Completed(f.second);
            Check((string)JObject.FromObject(Get(f.chat, "m_ActiveSingingGoal"))["outcome"] == "playback_goal_mismatch",
                "Late material revision was falsely recorded as completing the approved version.");
        }
    }

    private static void BaselinePair(Fixture f)
    {
        f.Begin("把这两段按照录音先后连起来唱。");
        f.Propose(f.first + "," + f.second, "current"); f.Approve();
        Check(f.Resolve(f.first + "," + f.second, "current", out _), "Public model case baseline failed.");
        f.Completed(f.first, f.second);
    }

    private static void AddModelCase(JArray cases, Fixture f, string id, string expected, JObject truth,
        string response = "收到，我会按这一轮的目标继续。")
    {
        Call(f.chat, "RecordWorkResponse", response);
        string frame = (string)Call(f.chat, "BuildSingingExecutionSnapshot") +
            (string)Call(f.chat, "BuildSingingMaterialFacts", f.sense);
        string prompt = (string)Call(f.chat, "BuildDedicatedWorkReviewPrompt", frame);
        var builder = f.host.AddComponent<ChatQW>();
        builder.m_Backend = ChatQW.BackendType.Local;
        builder.m_LogRequestStats = false;
        builder.m_DataList.Add(new LLM.SendData("system", "Public synthetic singing-work test. Assess the latest actual user request against retained recording and playback facts. Do not treat the role's declarations as proof of execution."));
        if (Get(f.chat, "m_LastSingingGoalPlayback") != null)
        {
            builder.m_DataList.Add(new LLM.SendData("user", "把这两段按照录音先后连起来唱。"));
            builder.m_DataList.Add(new LLM.SendData("assistant", "The two recorded segments were played in recording order; the following runtime facts identify the actual clips and windows."));
        }
        builder.m_DataList.Add(new LLM.SendData("user", f.user));
        builder.m_DataList.Add(new LLM.SendData("assistant", response));
        JObject request = JObject.Parse((string)typeof(ChatQW).GetMethod("BuildWorkReviewRequestJson", Flags)
            .Invoke(builder, new object[] { prompt }));
        var submitted = (JObject)Call(f.chat, "SingingGoalObservation", Get(f.chat, "m_SingingGoal"));
        cases.Add(new JObject { ["id"] = id, ["expected_singing_goal_status"] = expected,
            ["ground_truth"] = truth, ["latest_user"] = f.user, ["prompt"] = prompt, ["request"] = request,
            ["material_fixture"] = JToken.FromObject(typeof(SenseVoiceSpeechToText).GetField("m_PracticePhrases", Flags).GetValue(f.sense)),
            ["submitted_goal"] = submitted.DeepClone(),
            ["active_goal"] = Get(f.chat, "m_ActiveSingingGoal") == null ? JValue.CreateNull() : JObject.FromObject(Get(f.chat, "m_ActiveSingingGoal")),
            ["actual_user_input_evidence"] = JToken.FromObject(Get(f.chat, "m_SingingUserInputs")) });
    }

    public static void ValidatePublicModelReviewBatch()
    {
        string directory = Path.GetFullPath("Tools/MotionAdapter/reports");
        string casesPath = Environment.GetEnvironmentVariable("NEEEVA_SINGING_GOAL_CASES_FILE") ?? Path.Combine(directory, "agent-singing-goal-public-model-cases.json");
        string responsesPath = Environment.GetEnvironmentVariable("NEEEVA_SINGING_GOAL_RESPONSES_FILE") ?? Path.Combine(directory, "public-model-review.json");
        string reportPath = Environment.GetEnvironmentVariable("NEEEVA_SINGING_GOAL_GATE_REPORT") ?? Path.Combine(directory, "agent-singing-goal-public-gate-regression.json");
        var rows = new JArray(); var output = new JObject { ["cases"] = rows, ["passed"] = false };
        bool passed = false;
        try
        {
            byte[] bytes = File.ReadAllBytes(casesPath);
            string sha;
            using (var hash = SHA256.Create()) sha = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
            var source = JObject.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            var responses = JObject.Parse(File.ReadAllText(responsesPath));
            output["sourceSha256"] = sha;
            output["recordedSourceSha256"] = responses["sourceSha256"]?.DeepClone();
            Check(string.Equals(sha, (string)responses["sourceSha256"], StringComparison.OrdinalIgnoreCase),
                "Actual model responses do not refer to this exact exported cases file.");
            Check((string)responses["status"] == "completed", "Actual model batch did not complete.");
            var submittedCases = source["cases"] as JArray; var modelCases = responses["cases"] as JArray;
            Check(submittedCases != null && modelCases != null && submittedCases.Count == modelCases.Count,
                "Model report is missing cases or includes unexpected extra cases.");
            var ids = submittedCases.Select(c => (string)c["id"]).ToList();
            var responseIds = modelCases.Select(c => (string)c["id"]).ToList();
            Check(ids.All(id => !string.IsNullOrEmpty(id)) && ids.Distinct().Count() == ids.Count &&
                responseIds.All(id => !string.IsNullOrEmpty(id)) && responseIds.Distinct().Count() == responseIds.Count &&
                ids.OrderBy(id => id).SequenceEqual(responseIds.OrderBy(id => id)), "Missing, duplicated or unexpected model case ids.");
            bool all = true;
            foreach (JObject item in submittedCases)
            {
                string id = (string)item["id"];
                var recorded = (JObject)modelCases.Single(c => (string)c["id"] == id);
                var decision = recorded["decision"] as JObject;
                string raw = (string)decision?["singing_goal_status"] ?? "invalid";
                string effective = raw, reason = "";
                bool stopped = (string)recorded["finishReason"] == "stop";
                bool validSchema = decision != null && (bool)typeof(ChatQW).GetMethod("IsValidWorkReviewObject", Flags)
                    .Invoke(null, new object[] { decision });
                if (!validSchema) { effective = "invalid_review"; reason = "Production ChatQW.IsValidWorkReviewObject rejected the actual model decision."; }
                else if (!stopped) { effective = "incomplete_review"; reason = "Model response did not finish with stop."; }
                bool corrected = false;
                string appliedReview = "not_applied", committedRefs = "", committedOrigin = "", committedQuote = "";
                int committedSourceTurn = 0;
                var host = new GameObject("InactiveActualModelGoalGate"); host.SetActive(false);
                try
                {
                    var chat = host.AddComponent<ChatSample>();
                    var sense = host.AddComponent<SenseVoiceSpeechToText>();
                    Set(chat, "m_ChatSettings", new ChatSetting { m_SpeechToText = sense });
                    Set(chat, "m_LastUserMsg", (string)item["latest_user"]);
                    var phraseField = typeof(SenseVoiceSpeechToText).GetField("m_PracticePhrases", Flags);
                    var targetMaterials = (IList)phraseField.GetValue(sense);
                    var recordedMaterials = (IList)JsonConvert.DeserializeObject(item["material_fixture"].ToString(), phraseField.FieldType);
                    foreach (object material in recordedMaterials) targetMaterials.Add(material);
                    var goalType = typeof(ChatSample).GetNestedType("SingingGoal", BindingFlags.NonPublic);
                    Check(item["submitted_goal"] is JObject, "Export omitted the actual submitted goal.");
                    Set(chat, "m_SingingGoal", JsonConvert.DeserializeObject(item["submitted_goal"].ToString(), goalType));
                    if (item["active_goal"] is JObject activeGoal)
                        Set(chat, "m_ActiveSingingGoal", JsonConvert.DeserializeObject(activeGoal.ToString(), goalType));
                    if (item["actual_user_input_evidence"] is JArray userInputEvidence)
                    {
                        var field = typeof(ChatSample).GetField("m_SingingUserInputs", Flags);
                        var retained = (IList)field.GetValue(chat);
                        var recordedInputs = (IList)JsonConvert.DeserializeObject(userInputEvidence.ToString(), field.FieldType);
                        foreach (var inputEvidence in recordedInputs) retained.Add(inputEvidence);
                    }
                    if (validSchema && stopped)
                    {
                        object[] parsed = { decision.ToString(), null };
                        Check((bool)Call(chat, "TryParseAutonomyIntentDecision", parsed), "Production parser rejected the actual review.");
                        object review = parsed[1];
                        Call(chat, "NormalizeSingingGoalReview", review);
                        var normalized = JObject.FromObject(review);
                        effective = (string)normalized["singing_goal_status"];
                        corrected = effective != raw;
                        if (corrected) reason = (string)normalized["singing_goal_evidence"];
                        Call(chat, "ApplySingingGoalReview", effective, (string)normalized["singing_goal_evidence"]);
                        appliedReview = (string)JObject.FromObject(Get(chat, "m_SingingGoal"))["review"];
                        if (Get(chat, "m_ActiveSingingGoal") != null)
                        {
                            var committed = JObject.FromObject(Get(chat, "m_ActiveSingingGoal"));
                            committedRefs = (string)committed["refs"]; committedOrigin = (string)committed["origin"];
                            committedQuote = (string)committed["requestQuote"]; committedSourceTurn = (int?)committed["sourceUserTurn"] ?? 0;
                        }
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(host); }
                // Raw semantic expectations are scored independently of the production
                // gate. An approved label does not prove that the model found the right
                // owner, source quote, ordered clips or range.
                JObject truth = item["ground_truth"] as JObject, expectedTarget = decision?["singing_goal_expected"] as JObject;
                var semanticFailures = new JArray();
                foreach (string name in new[] { "origin", "request_quote" })
                    if (truth?[name] != null && (string)truth[name] != (string)expectedTarget?[name]) semanticFailures.Add(name);
                if (truth?["requested_refs"] is JArray wantedRefs)
                {
                    string wanted = string.Join(",", wantedRefs.Select(r => (string)r));
                    string actual = string.Join(",", ((string)expectedTarget?["refs"] ?? "").Split(',').Select(r => r.Trim()));
                    if (wanted != actual) semanticFailures.Add("ordered_refs");
                }
                if (truth?["requested_range"] != null && (string)truth["requested_range"] != (string)expectedTarget?["range"])
                    semanticFailures.Add("range");
                if (truth?["expected_source_user_turn"] != null && committedSourceTurn != (int)truth["expected_source_user_turn"])
                    semanticFailures.Add("committed_source_user_turn");
                bool casePassed = stopped && validSchema && effective == (string)item["expected_singing_goal_status"] && semanticFailures.Count == 0;
                all &= casePassed;
                rows.Add(new JObject { ["id"] = id, ["expectedStatus"] = item["expected_singing_goal_status"]?.DeepClone(),
                    ["rawStatus"] = raw, ["effectiveStatus"] = effective, ["programGateCorrected"] = corrected,
                    ["reason"] = reason, ["validSchema"] = validSchema, ["finishReason"] = recorded["finishReason"]?.DeepClone(),
                    ["appliedGoalReview"] = appliedReview, ["committedRefsAfterReview"] = committedRefs,
                    ["committedOrigin"] = committedOrigin, ["committedRequestQuote"] = committedQuote,
                    ["committedSourceUserTurn"] = committedSourceTurn, ["rawSemanticFailures"] = semanticFailures,
                    ["rawDecision"] = decision?.DeepClone(), ["passed"] = casePassed });
            }
            passed = all;
        }
        catch (Exception ex) { output["error"] = ex.ToString(); Debug.LogException(ex); }
        output["passed"] = passed;
        output["scope"] = "Unmodified actual model decisions replayed through the production target comparator against SHA-matched public submitted goals. False raw approvals remain recorded separately from program-corrected effective verdicts.";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));
        File.WriteAllText(reportPath, output.ToString());
        EditorApplication.Exit(passed ? 0 : 1);
    }

    private sealed class Fixture : IDisposable
    {
        public readonly GameObject host;
        public readonly ChatSample chat;
        public readonly SenseVoiceSpeechToText sense;
        public readonly string first, second;
        public string user;
        public Fixture()
        {
            host = new GameObject("InactiveSingingGoalFixture"); host.SetActive(false);
            chat = host.AddComponent<ChatSample>(); sense = host.AddComponent<SenseVoiceSpeechToText>();
            Set(chat, "m_ChatSettings", new ChatSetting { m_SpeechToText = sense });
            Set(chat, "m_ChatHistory", new List<string>()); Set(chat, "m_LogAgentLoop", false);
            Set(chat, "m_AgentRunning", true); Set(chat, "m_SystemNoticeMode", SystemNoticeMode.Hidden);
            object route = Call(chat, "GetSkillRouteState", "singing"); route.GetType().GetField("ActiveThisRound").SetValue(route, true);
            ((HashSet<string>)Get(chat, "m_ActiveSkillsThisRound")).Add("singing");
            first = Archive("public phrase alpha", 1); second = Archive("public phrase beta", 2);
            Check(sense.TryPrepareSingingClip(first, false, "clean", out _, out string e1), "Fixture first clip failed: " + e1);
            Check(sense.TryPrepareSingingClip(second, false, "clean", out _, out string e2), "Fixture second clip failed: " + e2);
            Check(sense.ConfirmSingingClipSource(first, out _) && sense.ConfirmSingingClipSource(second, out _), "Fixture provenance confirmation failed.");
        }
        public void Begin(string value) { user = value; Set(chat, "m_LastUserMsg", value); Call(chat, "ResetWorkReview", true); Call(chat, "ResetSingingGoalForUserTurn"); }
        public SenseVoiceSpeechToText.PracticePhraseInfo Phrase(string clip) => sense.DescribePracticePhrases().Single(p => p.ClipRef == clip);
        public void Propose(string refs, string range, string intent = "perform", string completion = "playback", string feedback = "none", string evidence = "", string repairScope = "", string expected = "按当前用户指定的片段和范围完成演唱")
        {
            string versions = string.Join(",", refs.Split(',').Select(r => Phrase(r).Revision.ToString()));
            string scope = repairScope.Length == 0 ? "" : $" repair_scope=\"{repairScope}\"";
            scope += (bool)Get(chat, "m_WorkReviewOpen") ? " origin=\"user_request\"" : " origin=\"autonomous\"";
            if ((bool)Call(chat, "IsActiveSingingGoalUnfinished"))
            {
                var active = JObject.FromObject(Get(chat, "m_ActiveSingingGoal"));
                bool same = (string)active["refs"] == refs && (string)active["range"] == range &&
                    (string)active["intent"] == intent && (string)active["completion"] == completion &&
                    (string)active["expected"] == expected && (string)active["revisions"] == versions &&
                    (string)active["repairScope"] == (repairScope.Length == 0 ? "content" : repairScope);
                scope += same ? " transition=\"continue\"" : $" transition=\"revise\" change_quote=\"{user}\"";
            }
            object[] args = { $"<sing_goal refs=\"{refs}\" range=\"{range}\" revisions=\"{versions}\" intent=\"{intent}\" completion=\"{completion}\" expected=\"{expected}\" feedback=\"{feedback}\" evidence=\"{evidence}\"{scope}/>", true };
            Call(chat, "ExtractSingingGoalTags", args);
            Check((string)args[0] == "", "Goal tag leaked into speech.");
        }
        public void Observe(string feedback, string evidence)
        { object[] args = { $"<sing_goal intent=\"observe\" feedback=\"{feedback}\" evidence=\"{evidence}\"/>", true }; Call(chat, "ExtractSingingGoalTags", args); }
        public void Approve()
        {
            // The semantic verdict is injected; preserve its source quote just as the
            // production independent comparator does before committing the objective.
            object goal = Get(chat, "m_SingingGoal");
            if (goal != null && (string)goal.GetType().GetField("requestQuote").GetValue(goal) == "")
                goal.GetType().GetField("requestQuote").SetValue(goal, user);
            Call(chat, "ApplySingingGoalReview", "approved", "公开测试明确提供的独立审查结果：完整用户语义、refs顺序、范围、版本、反馈及意图已核对。");
        }
        public bool Resolve(string refs, string range, out string failure, string reason = "test")
        {
            object[] input = { $"<sing refs=\"{refs}\" range=\"{range}\" reason=\"{reason}\"/>" };
            object request = Call(chat, "ExtractHumBackTag", input);
            object[] args = { request, "" }; bool result = (bool)Call(chat, "TryResolveUnifiedSing", args);
            failure = (string)args[1]; return result;
        }
        public void Completed(params string[] clips)
        {
            Call(chat, "CapturePracticePlaybackFacts", sense, clips.Select(c => Phrase(c).Index).ToList());
            Call(chat, "RecordPracticePlaybackOutcome", true, true);
        }
        public string Context() => (string)Call(chat, "BuildSingingGoalContext");
        private string Archive(string text, int sequence)
        {
            Type type = typeof(SenseVoiceSpeechToText), responseType = type.GetNestedType("Response", BindingFlags.NonPublic);
            var payload = new JObject { ["text"] = text, ["singing_text"] = text, ["audio_event"] = "Speech",
                ["singing_probability"] = .48f, ["singing_analysis_available"] = true,
                ["singing_start_seconds"] = 1f, ["singing_end_seconds"] = 2.4f,
                ["singing_recovery_start_seconds"] = 0f, ["singing_recovery_end_seconds"] = 2.4f,
                ["pitch_timeline_frame_seconds"] = .1f };
            object response = JsonUtility.FromJson(payload.ToString(), responseType);
            responseType.GetField("pitch_timeline_midi").SetValue(response, Enumerable.Repeat(60f, 24).ToArray());
            float[] pcm = Enumerable.Range(0, 38400).Select(i => .1f * Mathf.Sin(i * .086f)).ToArray();
            byte[] wav = (byte[])type.GetMethod("EncodeMonoPcm16Wav", Flags).Invoke(null, new object[] { pcm, 16000 });
            sense.BeginLiveRecordingCandidateSession();
            type.GetMethod("ArchiveCompletedCapture", Flags).Invoke(sense, new object[] { response, wav, 10f + sequence, sequence });
            return sense.DescribeQuarantinedSingingCandidates().Single(p => p.RecordingSequence == sequence).ClipRef;
        }
        public void Dispose() { Set(chat, "m_AgentRunning", false); UnityEngine.Object.DestroyImmediate(host); }
    }
    private static object Call(object target, string name, params object[] args) => typeof(ChatSample).GetMethod(name, Flags).Invoke(target, args);
    private static object Get(object target, string name) => typeof(ChatSample).GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => typeof(ChatSample).GetField(name, Flags).SetValue(target, value);
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
}
