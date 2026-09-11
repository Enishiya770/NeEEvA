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
        ApprovalAndCorrections(); ReviewedTargetMatching(); RetryAndAcceptance(); Lifecycle(); ExportPublicModelCases();
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
        string path = Path.GetFullPath("Tools/MotionAdapter/reports/agent-singing-goal-public-model-cases.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, new JObject { ["case_count"] = cases.Count,
            ["scope"] = "Public synthetic recordings and utterances, actual production prompt/goal/inventory/played-fact builders. Ground truth is separate from the model prompt. No private persona/history and no network requests.",
            ["cases"] = cases }.ToString());
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
            ["submitted_goal"] = submitted.DeepClone() });
    }

    public static void ValidatePublicModelReviewBatch()
    {
        string directory = Path.GetFullPath("Tools/MotionAdapter/reports");
        string casesPath = Path.Combine(directory, "agent-singing-goal-public-model-cases.json");
        string responsesPath = Path.Combine(directory, "public-model-review.json");
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
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(host); }
                bool casePassed = stopped && validSchema && effective == (string)item["expected_singing_goal_status"];
                all &= casePassed;
                rows.Add(new JObject { ["id"] = id, ["expectedStatus"] = item["expected_singing_goal_status"]?.DeepClone(),
                    ["rawStatus"] = raw, ["effectiveStatus"] = effective, ["programGateCorrected"] = corrected,
                    ["reason"] = reason, ["validSchema"] = validSchema, ["finishReason"] = recorded["finishReason"]?.DeepClone(),
                    ["rawDecision"] = decision?.DeepClone(), ["passed"] = casePassed });
            }
            passed = all;
        }
        catch (Exception ex) { output["error"] = ex.ToString(); Debug.LogException(ex); }
        output["passed"] = passed;
        output["scope"] = "Unmodified actual model decisions replayed through the production target comparator against SHA-matched public submitted goals. False raw approvals remain recorded separately from program-corrected effective verdicts.";
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "agent-singing-goal-public-gate-regression.json"), output.ToString());
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
            object[] args = { $"<sing_goal refs=\"{refs}\" range=\"{range}\" revisions=\"{versions}\" intent=\"{intent}\" completion=\"{completion}\" expected=\"{expected}\" feedback=\"{feedback}\" evidence=\"{evidence}\"{scope}/>", true };
            Call(chat, "ExtractSingingGoalTags", args);
            Check((string)args[0] == "", "Goal tag leaked into speech.");
        }
        public void Observe(string feedback, string evidence)
        { object[] args = { $"<sing_goal intent=\"observe\" feedback=\"{feedback}\" evidence=\"{evidence}\"/>", true }; Call(chat, "ExtractSingingGoalTags", args); }
        public void Approve() => Call(chat, "ApplySingingGoalReview", "approved", "公开测试明确提供的独立审查结果：完整用户语义、refs顺序、范围、版本、反馈及意图已核对。");
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
