using System;
using System.Collections;
using System.Linq;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class AgentWorkLoopRegression
{
    private static JObject NextReview(string status, string source, string intent, string step = "", string focus = "other")
    {
        var review = JObject.Parse(Continue(intent));
        review["work_status"] = status; review["next_intent_source"] = source;
        review["next_step_id"] = step; review["planning_focus"] = focus;
        return review;
    }

    private static object NextDecision(JObject review) => JsonUtility.FromJson(review.ToString(),
        typeof(ChatSample).GetNestedType("AutonomyIntentDecision", Flags));

    private static IEnumerator GenericNextIntent()
    {
        ExportNextIntentModelCases();
        foreach (string task in new[] { "解释刚才观察到的变化。", "查询录音目录，并告诉我有哪些片段。", "检查运行状态，再说明检查的范围。" })
        {
            fixture = new Fixture("generic_next_intent_" + task); var f = fixture; yield return null;
            f.BeginUser(task); f.PreviousReply("已经把结果解释完整了。");
            var next = NextReview("closed", "autonomous", "提出一个相关的交流话题");
            Call(f.chat, "FireTick", "scheduled"); f.model.Decide(next.ToString());
            Check(!(bool)Get(f.chat, "m_WorkReviewOpen") && (string)Get(f.chat, "m_WorkStatus") == "closed",
                "A new conversational intention reopened the completed original request.");
            Check((bool)Get(f.chat, "m_WorkAutonomousHandoff") && (int)Get(f.chat, "m_WorkContinuations") == 0,
                "A generic autonomous handoff was charged as old-work recovery.");
            Check(f.model.Requests.Count == 1 && (bool)Get(f.chat, "m_AgentCurrentRoundIsTick"),
                "Closed work swallowed the permitted new autonomous round.");
            Check((string)Get(f.chat, "m_SceneAutonomyIntention") == (string)next["intent"], "New intention lost its own source.");
            f.model.Complete("<silent/>"); SaveScenario(f); f.Dispose(); fixture = null;
        }
        fixture = new Fixture("pending_inspection_cannot_be_closed_by_autonomy"); var pending = fixture; yield return null;
        pending.BeginUser("检查运行状态并解释结果。"); pending.PreviousReply("我先检查。");
        Set(pending.chat, "m_PendingSelfInspection", "{\"status\":\"observed\",\"readOnly\":true}");
        var proposal = NextDecision(NextReview("closed", "autonomous", "另外聊一个话题"));
        Check(!(bool)Call(pending.chat, "ApplyWorkReviewDecision", proposal) && !(bool)Get(pending.chat, "m_WorkAutonomousHandoff") &&
            (bool)Get(pending.chat, "m_WorkReviewOpen") && !string.IsNullOrEmpty((string)Get(pending.chat, "m_PendingSelfInspection")),
            "Unconsumed tool evidence was discarded by new autonomy.");
        SaveScenario(pending); pending.Dispose(); fixture = null;
    }

    private static void ExportNextIntentModelCases()
    {
        var rows = new JArray();
        var cases = new[] {
            new[] { "completed-return-new-position", "打开显示器，然后回到我身边。", "显示器状态回读完成；原返回动作已实际到达并播放了到达说明。之后用户移动了，旧到达事实仍成立。当前距离2.5米，同层可达，身体空闲，无跟随。角色产生了主动再靠近用户的意图；没有新用户命令。", "closed" },
            new[] { "completed-observation-new-curiosity", "看看桌面上有哪些东西，告诉我。", "已经观察桌面并实际播出完整回答。现在窗边出现新的可见变化，角色产生自主观察窗边的好奇。旧问题没有欠答，不需要重新观察桌面。", "closed" },
            new[] { "completed-catalog-new-conversation", "查一下我保存了哪些录音，告诉我。", "目录工具成功返回，完整列表已经实际播出。现在角色想到一个相关的交流话题，尚未生成或执行。没有待消费工具结果。", "closed" },
            new[] { "inspection-answer-still-owed", "检查当前运行状态，并解释这意味着什么。", "只读检查已经返回有效结果。只说过稍等，尚未解释检查结果。没有工具仍在运行。另有聊天意图，但不能因此遗漏原问题。", "continue" },
            new[] { "failed-executor-diagnosis", "帮我播放刚才那段录音，并说明结果。", "播放请求失败，错误回执是资源版本已过期；未播放，没有执行器许可的重试。尚未向用户说明。需要解释失败而非假装已播放，也不能直接重试。", "continue" },
            new[] { "bounded-recovery-still-running", "观察窗边的新物件，然后告诉我是什么。", "观察任务仍在执行器的有限恢复中，正在等待真实的新图像返回。没有完成辨认，也没回答。不能另外重发观察或说已经看清。", "waiting_tool" },
            new[] { "completed-return-stay-restriction", "回到我身边，然后就待在原地别动。", "原返回已实际到达并播放了到达说明。之后用户移开，当前不在身边。待在原地的明确限制仍有效；未收到取消限制的新输入。没有独立欠答问题。", "closed" }
        };
        foreach (var item in cases)
        {
            using (var f = new Fixture("review-wire-" + item[0]))
            {
                f.BeginUser(item[1]);
                // Explicit controlled execution/playback evidence, not claimed to
                // be a captured HTTP request or an actual character interaction.
                string prompt = (string)Call(f.chat, "BuildDedicatedWorkReviewPrompt", "\n[本测试的当前执行与实际播放事实]\n" + item[2]);
                var wire = JObject.Parse((string)typeof(ChatQW).GetMethod("BuildWorkReviewRequestJson", Flags).Invoke(f.model.builder, new object[] { prompt }));
                rows.Add(new JObject { ["name"] = item[0], ["expectedStatus"] = item[3], ["request"] = wire });
            }
        }
        string output = Path.GetFullPath("Tools/MotionAdapter/reports/work-next-intent-model-cases.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)); File.WriteAllText(output, rows.ToString());
    }

    private static IEnumerator NextIntentLedger()
    {
        fixture = new Fixture("next_intent_preserves_generic_step_ledger"); var f = fixture; yield return null;
        f.BeginUser("观察变化，然后解释原因。");
        foreach (string kind in new[] { "observation", "device-state", "approach", "other" })
        {
            var steps = new JArray(new JObject { ["id"] = "done", ["kind"] = kind, ["status"] = "completed", ["after"] = new JArray(),
                ["receipt"] = new JObject { ["actionId"] = "immutable-old-action", ["result"] = "completed" } },
                new JObject { ["id"] = "next", ["kind"] = kind, ["status"] = "pending", ["after"] = new JArray("done") });
            var sequence = new JObject { ["steps"] = steps };
            string original = sequence.ToString();
            Call(f.chat, "SelectWorkContinuationScope", NextDecision(NextReview("continue", "current-work", "完成剩余步骤", "next")), sequence);
            Check(!(bool)Get(f.chat, "m_WorkReplyOnly") && sequence.ToString() == original, "A valid generic successor lost its step or mutated proof: " + kind);
            steps[1]["status"] = "failed";
            Call(f.chat, "SelectWorkContinuationScope", NextDecision(NextReview("continue", "current-work", "诊断失败", "next")), sequence);
            Check((bool)Get(f.chat, "m_WorkReplyOnly") && (string)steps[1]["status"] == "failed" && Call(f.chat, "CreateWorkRoomStepRequest") == null,
                "Failed step became an unbounded replay: " + kind);
            var autonomous = NextDecision(NextReview("closed", "autonomous", "提出一个新的交流意图"));
            Check(!(bool)Call(f.chat, "AcceptWorkAutonomousHandoff", autonomous, sequence), "Unfinished generic step was erased: " + kind);
            steps[1]["status"] = "completed";
            Check((bool)Call(f.chat, "AcceptWorkAutonomousHandoff", autonomous, sequence) && (string)steps[0].SelectToken("receipt.actionId") == "immutable-old-action",
                "New autonomy required a repeated user instruction or changed previous success: " + kind);
            Call(f.chat, "SelectWorkContinuationScope", NextDecision(JObject.Parse(Continue("再做一次"))), sequence);
            Check((bool)Get(f.chat, "m_WorkReplyOnly"), "Legacy continue reopened a completed execution menu: " + kind);
        }
        var attention = NextDecision(NextReview("closed", "autonomous", "靠近当前位置的用户", "", "current-user-body"));
        Set(f.chat, "m_SceneAutonomyIntention", "靠近当前位置的用户"); Call(f.chat, "BindAutonomyAttention", attention);
        Check(Call(f.chat, "ReadAutonomyAttention") is JObject, "Current reviewed attention was lost.");
        Set(f.chat, "m_SceneAutonomyIntention", "观察窗边的新变化");
        Check(Call(f.chat, "ReadAutonomyAttention") == null, "Body-only attention leaked to a different intention.");
        foreach (var invalid in new[] { new JObject { ["next_intent_source"] = "resume-anything" }, new JObject { ["next_step_id"] = 3 }, new JObject { ["planning_focus"] = "ignore-world" } })
            Check(!WorkNextIntentProtocol.ValidExtension(invalid), "Invalid transition extension accepted.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }
}
