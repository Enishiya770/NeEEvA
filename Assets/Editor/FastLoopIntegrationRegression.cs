using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Edit-mode production ChatSample dispatch, callback and queue gates. Backend
/// replies and attention ownership are controlled facts, not physical avatar tests.
/// </summary>
public static class FastLoopIntegrationRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static readonly JArray Cases = new JArray();

    private static string RoomNone(FastLoopIntegrationModel model)
    {
        var value = new JObject { ["intent"] = "none", ["origin"] = "none", ["evidence"] = "" };
        var plan = JObject.Parse(model.LastRoomInput)["actorProposal"]?["actorPlan"] as JObject;
        if (plan != null)
        { value["planId"] = plan["id"]; value["planRevision"] = plan["revision"]; value["planRemaining"] = plan["remainingGoal"]; }
        return value.ToString(Newtonsoft.Json.Formatting.None);
    }

    public static void RunBatch()
    {
        if (!Application.isBatchMode) throw new InvalidOperationException("Batch entry requires batch mode.");
        EditorApplication.Exit((bool)RunEditMode()["passed"] ? 0 : 1);
    }

    public static JObject RunEditMode()
    {
        string directory = Path.GetFullPath("Logs/fast-loop");
        Directory.CreateDirectory(directory);
        var report = new JObject {
            ["scope"] = "Edit-mode real ChatSample shared first-reply entry, completion and cancellation gates; fake backend; simulated NevaSceneAttention owner/state. No avatar movement, microphone, network, synthesized audio or scene save.",
            ["networkRequests"] = 0, ["playMode"] = Application.isPlaying,
            ["physicalHeadExecutionTested"] = false, ["audioPlaybackTested"] = false,
            ["actorSpeechPreparationScope"] = "Real production preparation/approval/consumer gates with fake disposable stream handles and controlled silent AudioClips; no PCM decoder, HTTP latency, or physical audibility measurement.",
            ["cases"] = Cases
        };
        try
        {
            if (Application.isPlaying)
                throw new InvalidOperationException("Run in edit mode without entering Play.");
            checks = 0; Cases.Clear();
            SharedEntryCases();
            VisualInputCases();
            CallbackCases();
            TaskAndCancellationCases();
            SpeechLeaseCases();
            ActorIntentCases();
            PlannerHandoffCases();
            DialogueRepairCases();
            ActorSpeechPreparationCases();
            report["passed"] = Cases.All(item => (bool)item["passed"]);
        }
        catch (Exception error)
        {
            report["passed"] = false; report["error"] = Describe(error);
        }
        finally
        {
            report["checks"] = checks;
            File.WriteAllText(Path.Combine(directory, "integration.json"), report.ToString());
            Debug.Log("[FastLoop/Integration] " + report.ToString(Newtonsoft.Json.Formatting.None));
        }
        return report;
    }

    private static void Case(string name, Action<Fixture> test)
    {
        var row = new JObject { ["name"] = name, ["passed"] = false };
        Cases.Add(row);
        int before = checks;
        try { using (var fixture = new Fixture()) test(fixture); row["passed"] = true; }
        catch (Exception error) { row["error"] = Describe(error); }
        row["checks"] = checks - before;
    }

    private static void SharedEntryCases()
    {
        foreach (bool realtime in new[] { false, true })
            Case((realtime ? "realtime" : "ptt") + "-same-fast-entry-ordinary-reply", f => {
                f.Arm("你好，今天只聊天。", realtime);
                var pending = f.Dispatch();
                Require(f.Model.FirstCalls == 0 && f.Model.RoleCalls == 0 && f.Model.FormalCalls == 0,
                    "Shared entry must reach the new backend without old first-reply/role/formal requests.");
                Require(pending.CurrentUser == f.Prompt && JObject.Parse(pending.Context)["headTask"].Type == JTokenType.Null,
                    "Backend receives the full current user and explicit empty task snapshot.");
                pending.Answer(Decision("reply", speech: "你好，我听着呢。"), "ok");
                string queued = f.Queued(); int history = f.Model.m_DataList.Count;
                pending.Answer(Decision("legacy"), "duplicate");
                Require(queued == "你好，我听着呢。" && f.Queued() == queued,
                    "Public speech reaches the production queue exactly once.");
                Require(f.Model.m_DataList.Count == history && history == 2 &&
                    f.Model.m_DataList[0].content == f.Prompt && f.Model.FormalCalls == 0,
                    "One accepted reply commits one user/assistant pair and duplicate callbacks cannot hand off.");
                Require(f.Runtime.HeadTask == null && !(bool)Get(f.Chat, "m_SceneActionActive") &&
                    !(bool)Get(f.Chat, "m_HumBackPending"), "Ordinary response does not create an old or new action.");
            });

        Case("legacy-single-handoff-preserves-complete-user", f => {
            f.Arm("走到我身边。最后更正，只解释，不要真的走。", true);
            var pending = f.Dispatch();
            pending.Answer(Decision("legacy"), "ok");
            pending.Answer(Decision("legacy"), "duplicate");
            Require(f.Model.Pending.Count == 1 && f.Model.FormalCalls == 1 &&
                f.Model.FirstCalls == 0 && f.Model.RoleCalls == 0,
                "Legacy selection enters the existing formal path exactly once without fast-loop recursion.");
            Require(f.Model.LastFormalUser == f.Prompt && f.Queued() == "" && f.Runtime.HeadTask == null,
                "Handoff preserves complete input and performs no fast-loop speech or head action.");
        });
    }

    private static void DialogueRepairCases()
    {
        const string repeated = "あら、ごめんなさい。また私の頭が固くなっちゃって、あなたの本当の意図をちゃんと汲み取れてなかったの。あなたの歌、すごく素敵だったわね。さあ、準備はいい？";
        void Seed(Fixture f) => ((List<JObject>)Get(f.Chat, "m_CompletedRoleReplies")).Add(new JObject {
            ["generation"] = f.Generation - 1, ["speechRound"] = f.Round - 1,
            ["userTurn"] = -1, ["text"] = repeated, ["completeChunksOnly"] = true });
        Case("dialogue-repair-before-publication-and-once-only", f => {
            f.Arm("我是要你唱刚才那一段歌，不是让我再唱。", true); Seed(f);
            var original = f.Dispatch();
            original.Answer(Decision("reply", speech: repeated), "ok");
            Require(f.Model.Pending.Count == 2 && f.Queued() == "" && f.Model.m_DataList.Count == 0,
                "Long repeated public history triggers one private review before publication or history commit.");
            var review = f.Model.Pending.Last();
            Require(review.CurrentUser == f.Prompt && (string)JObject.Parse(review.Context)["dialogueRepair"]["previouslySpoken"] == repeated,
                "Review keeps exact current user and labels the actual completed prior speech separately.");
            original.Answer(Decision("legacy"), "duplicate");
            review.Answer(Decision("reply", speech: "听到了，你希望我用刚才保存的那段录音回唱。"), "ok");
            review.Answer(Decision("legacy"), "duplicate");
            Require(f.Model.Pending.Count == 2 && f.Model.FormalCalls == 0 && f.Model.m_DataList.Count == 2 &&
                f.Queued() == "听到了，你希望我用刚才保存的那段录音回唱。", "Only reviewed output commits; duplicate callbacks cannot act.");
        });
        Case("dialogue-repair-keeps-deliberate-repetition-possible", f => {
            f.Arm("请逐字重复你刚才那句话，先不要做其他事。", false); Seed(f);
            f.Dispatch().Answer(Decision("reply", speech: repeated), "ok");
            var requested = Decision("reply", speech: repeated);
            requested.ReplyBasis = "requested_repetition"; requested.ReplyEvidence = f.Prompt;
            f.Model.Pending.Last().Answer(requested, "ok");
            Require(f.Model.Pending.Count == 2 && f.Queued() == repeated && f.Runtime.HeadTask == null,
                "Similarity is a bounded review trigger, not a semantic ban or action permission.");
        });
        Case("dialogue-repair-does-not-publish-another-unrequested-repeat", f => {
            f.Arm("我怎么感觉你又在复读，算了，测试到此为止。", true); Seed(f);
            f.Dispatch().Answer(Decision("reply", speech: repeated), "ok");
            var result = Decision("reply", speech: repeated); result.ReplyBasis = "current_input"; result.ReplyEvidence = "测试到此为止";
            var request = f.Model.Pending.Last(); request.Answer(result, "ok");
            string queued = f.Queued(); request.Answer(Decision("legacy"), "duplicate");
            Require(f.Model.Pending.Count == 2 && !queued.Contains(repeated) && !queued.Contains("换个说法") &&
                queued.Length > 0 && f.Queued() == queued && f.Model.FormalCalls == 0,
                "Unresolved duplicate ends once with a factual failure, not the old test promise or another planner.");
        });
        Case("dialogue-repair-only-actually-completed-long-speech", f => {
            f.Arm("继续这个话题。", false);
            f.Model.m_DataList.Add(new LLM.SendData("assistant", repeated));
            f.Dispatch().Answer(Decision("reply", speech: repeated), "ok");
            Require(f.Model.Pending.Count == 1 && f.Queued() == repeated, "Unplayed model history is not heard repetition evidence.");
            Require(!FastLoopDialogueRepair.IsLongRepeat("わかったわ。", "わかったわ。") &&
                !FastLoopDialogueRepair.IsLongRepeat(repeated, "現在の録音は保存されています。次に音声の範囲と出所を確かめます。まだ歌の再生は始めていません。"),
                "Short acknowledgements and materially different text do not add a model call.");
        });
        foreach (string invalidation in new[] { "generation", "epoch", "cancel", "cancel-reentrant" })
            Case("dialogue-repair-invalidated-" + invalidation, f => {
                f.Arm("你刚才重复了，请处理我这次的请求。", true); Seed(f);
                f.Dispatch().Answer(Decision("reply", speech: repeated), "ok");
                var review = f.Model.Pending.Last();
                if (invalidation == "generation") Set(f.Chat, "m_FormalResponseGeneration", f.Generation + 1);
                else if (invalidation == "epoch") Set(f.Chat, "m_WorkEpoch", f.Epoch + 1);
                else {
                    if (invalidation == "cancel-reentrant") f.Model.OnFastCancel = () => review.Answer(Decision("legacy"), "during-cancel");
                    Call(f.Chat, "CancelFastLoopRequest", "fixture-cancel");
                }
                review.Answer(Decision("reply", speech: "迟到的回复不能公开。"), "late");
                review.Answer(Decision("legacy"), "late-plan");
                Require(f.Queued() == "" && f.Model.m_DataList.Count == 0 && f.Model.FormalCalls == 0,
                    "Stale repair cannot publish, commit history or enter execution, including synchronous cancellation.");
            });
        Case("dialogue-repair-new-intention-still-goes-through-compiler", f => {
            f.Arm("别重复道歉，告诉我你的选择。", true); Seed(f);
            Set(f.Chat, "m_EnableActorIntentCompiler", true);
            f.Dispatch().Answer(Decision("reply", speech: repeated), "ok");
            Require(f.Model.ActorPending.Count == 0, "Rejected draft cannot prepare speech or enter actor compilation.");
            f.Model.Pending.Last().Answer(Decision("reply", speech: "这次我想先听你说。"), "ok");
            Require(f.Model.ActorPending.Count == 1 && f.Queued() == "", "Reviewed role choice retains normal actor approval.");
            f.Model.ActorPending.Last().Answer(ActorDecision("none"), "ok");
            Require(f.Queued() == "这次我想先听你说。" && f.Model.FormalCalls == 0, "A refusal remains ordinary speech.");
        });
    }

    private static void CallbackCases()
    {
        Case("dispatch-trace-reentry-does-not-post-old-request", f => {
            f.Arm("旧轮尚未提交到后端。", true);
            bool reentered = false;
            Exception reentryError = null;
            FastLoopIntegrationModel.PendingRequest newer = null;
            Set(f.Chat, "m_ResponseTrace", new ConversationTrace("fixture-dispatch-reentry", json => {
                if (reentered || (string)JObject.Parse(json)["event"] != "fast_loop.dispatch") return;
                reentered = true;
                try
                {
                    Call(f.Chat, "CancelFastLoopRequest", "fixture-dispatch-trace-reentry");
                    f.Arm("后继请求必须独占后端。", true);
                    newer = f.Dispatch();
                }
                catch (Exception error) { reentryError = error; }
            }));
            var onlyPosted = f.Dispatch();
            Require(reentered && reentryError == null && newer != null &&
                ReferenceEquals(onlyPosted, newer) && f.Model.Pending.Count == 1 &&
                newer.CurrentUser == "后继请求必须独占后端。",
                "A dispatch trace reentry posts only the successor; the resumed old dispatch cannot cancel it with a late Post.");
            Require(f.Queued() == "" && f.Model.m_DataList.Count == 0 &&
                (bool)Get(f.Chat, "m_FormalResponseInFlight"),
                "Only the successor request remains in flight before its result.");
            newer.Answer(Decision("reply", speech: "后继请求正常完成。"), "ok");
            Require(f.Queued() == "后继请求正常完成。" && f.Model.m_DataList.Count == 2 &&
                f.Model.FormalCalls == 0, "Successor publication completes once after dispatch trace reentry.");
        });
        Case("decision-trace-reentry-preserves-new-turn-owner", f => {
            f.Arm("旧轮请求，不得写入后继轮。", true);
            bool reentered = false;
            Exception reentryError = null;
            FastLoopIntegrationModel.PendingRequest newer = null;
            Set(f.Chat, "m_ResponseTrace", new ConversationTrace("fixture-decision-reentry", json => {
                if (reentered || (string)JObject.Parse(json)["event"] != "fast_loop.decision") return;
                reentered = true;
                // The production trace sink is synchronous. Revoke the old
                // request and enter the actual shared dispatch before its
                // callback resumes; do not invoke ApplyFastLoopAnswer directly.
                try
                {
                    Call(f.Chat, "CancelFastLoopRequest", "fixture-trace-reentry");
                    f.Arm("新的用户请求，只接受这一轮的答案。", true);
                    newer = f.Dispatch();
                }
                catch (Exception error) { reentryError = error; }
            }));
            var old = f.Dispatch();
            old.Answer(Decision("reply", speech: "旧轮内容绝不能公开。"), "ok");
            Require(reentered && reentryError == null && newer != null && f.Model.Pending.Count == 2,
                "Synchronous decision trace cancellation enters one actual successor dispatch.");
            Require(f.Queued() == "" && f.Model.m_DataList.Count == 0 && f.Model.FormalCalls == 0 &&
                (bool)Get(f.Chat, "m_FormalResponseInFlight"),
                "The old callback cannot adopt the successor turn or publish into its queue/history.");
            newer.Answer(Decision("reply", speech: "这是新轮的独立回答。"), "ok");
            old.Answer(Decision("reply", speech: "旧轮重复回调也不能公开。"), "duplicate");
            Require(f.Queued() == "这是新轮的独立回答。" && f.Model.m_DataList.Count == 2 &&
                f.Model.FormalCalls == 0 && !(bool)Get(f.Chat, "m_FormalResponseInFlight"),
                "Only the successor reply commits once after the old callback and duplicate return.");
        });
        Case("duplicate-formal-dispatch-keeps-fast-owner", f => {
            f.Arm("这一轮由新路径处理。", true); var pending = f.Dispatch();
            Call(f.Chat, "DispatchFormalStream", f.Prompt, null, f.Generation, true, null, false, false, false);
            Require(f.Model.Pending.Count == 1 && f.Model.FormalCalls == 0,
                "A duplicate outer formal dispatch cannot bypass the waiting fast owner into old planning.");
            pending.Answer(Decision("reply", speech: "这轮只回复一次。"), "ok");
            Call(f.Chat, "DispatchFormalStream", f.Prompt, null, f.Generation, true, null, false, false, false);
            Require(f.Model.FormalCalls == 0 && f.Queued() == "这轮只回复一次。",
                "Completed fast reply retains the dispatch claim for the rest of its generation.");
        });
        Case("scene-cancel-closes-waiting-output", f => {
            f.Arm("等待当前回复。", false); var old = f.Dispatch();
            Call(f.Chat, "ResetSceneVision", "eyes-closed", true);
            Require(!(bool)Get(f.Chat, "m_FormalResponseInFlight") && (bool)Get(f.Chat, "m_StreamComplete") &&
                (bool)Get(f.Chat, "m_TTSSenderDone"), "Scene cancellation cannot leave synthesis waiting for a cancelled callback.");
            old.Answer(Decision("reply", speech: "过期回复。"), "late");
            Require(f.Queued() == "" && f.Model.FormalCalls == 0, "Cancelled scene response never publishes or hands off.");
        });
        foreach (string revocation in new[] { "generation", "epoch", "speaking", "backend-cancel" })
            Case("late-callback-after-" + revocation, f => {
                f.Arm("这一轮尚在等待。", true); var old = f.Dispatch();
                if (revocation == "generation") Set(f.Chat, "m_FormalResponseGeneration", f.Generation + 1);
                else if (revocation == "epoch") Set(f.Chat, "m_WorkEpoch", f.Epoch + 1);
                else if (revocation == "speaking") Set(f.Chat, "m_UserSpeechActiveForAutonomy", true);
                else Call(f.Chat, "CancelFastLoopRequest", "fixture-cancel");
                old.Answer(Decision("reply", speech: "不应出现的旧回答。"), "late");
                old.Answer(Decision("legacy"), "late-legacy");
                Require(f.Queued() == "" && f.Model.m_DataList.Count == 0 && f.Model.FormalCalls == 0,
                    "Revoked callback may neither speak, commit history nor invoke the old path: " + revocation);
            });
    }

    private static void VisualInputCases()
    {
        foreach (bool realtime in new[] { false, true })
            foreach (string state in new[] { "fresh", "closed", "stale", "wrong-generation", "unbound-image" })
                Case((realtime ? "realtime" : "ptt") + "-visual-input-" + state, f => {
                    f.Arm("你现在能看到什么？", realtime);
                    string pixels = f.SeedVision(state);
                    var pending = f.Dispatch(pixels);
                    var vision = JObject.Parse(pending.Context)["vision"];
                    bool attached = state == "fresh";
                    Require((pending.Image == pixels) == attached && (bool)vision["imageAttached"] == attached,
                        "Only the fresh, bound current-turn observation reaches the shared decision backend as actual pixels.");
                    Require(!pending.Context.Contains(pixels), "Visual facts contain metadata, never duplicated base64 pixels.");
                    if (attached)
                    {
                        Require((bool)vision["freshAtDispatch"] && (string)vision["captureId"] != "",
                            "The model receives the actual attachment's freshness and capture identity.");
                        pending.Answer(Decision("reply", speech: "画面里有一块红色区域。"), "ok");
                        Require(f.Queued() == "画面里有一块红色区域。" && f.Runtime.HeadTask == null && f.Model.FormalCalls == 0,
                            "An image-backed plain description can complete without a head task or another planner call.");
                    }
                });
    }

    private static void TaskAndCancellationCases()
    {
        foreach (bool holding in new[] { false, true })
            Case((holding ? "holding" : "starting") + "-survives-conversation", f => {
                var task = f.SeedHead(holding);
                f.Arm("继续聊刚才的话题。", false); long output = f.Runtime.OutputRevision;
                var pending = f.Dispatch();
                Require(f.Runtime.OutputRevision > output && SameTask(task, f.Runtime.HeadTask) &&
                    f.Runtime.HeadTask.State == task.State,
                    "A new dialogue dispatch revokes output but preserves starting/holding task identity and revision.");
                pending.Answer(Decision("reply", speech: "我们继续聊。"), "ok");
                Require(SameTask(task, f.Runtime.HeadTask) && f.Runtime.HeadTask.Active,
                    "Ordinary response completion keeps the independent head task.");
                Call(f.Chat, "ResetSceneVision", "user-started-speaking", false);
                Require(SameTask(task, f.Runtime.HeadTask) && f.Runtime.HeadTask.Active,
                    "Actual dialogue vision reset preserves the independent head task.");
                if (holding) Require(f.Attention.OwnerId == Owner(task) && f.Attention.IsSettled,
                    "Dialogue reset preserves simulated physical ownership as well as logical state.");
            });

        Case("voice-stop-does-not-release-head", f => {
            var task = f.SeedHead(true);
            f.Arm("先别说话。", true); var pending = f.Dispatch(); long output = f.Runtime.OutputRevision;
            pending.Answer(Decision("stop_voice", evidence: "别说话"), "ok");
            Require(SameTask(task, f.Runtime.HeadTask) && f.Runtime.HeadTask.State == "holding" &&
                f.Attention.OwnerId == Owner(task) && f.Attention.IsSettled,
                "Stopping voice preserves logical and controlled physical head ownership.");
            Require(f.Runtime.OutputRevision > output && f.Queued() == "" && f.Model.FormalCalls == 0,
                "Voice stop revokes output and completes silently, without a legacy handoff.");
        });

        Case("explicit-head-release-matching-owner", f => {
            var task = f.SeedHead(true);
            f.Arm("不用保持头部姿势了。", false); var pending = f.Dispatch();
            pending.Answer(Decision("head", "head.release", "不用保持头部姿势了"), "ok");
            Require(f.Runtime.HeadTask.Id == task.Id && f.Runtime.HeadTask.Revision > task.Revision &&
                f.Runtime.HeadTask.State == "cancelled", "Explicit release cancels the matching task revision.");
            Require(f.Attention.OwnerId == Owner(task) && f.Attention.IsReturning && f.Queued().Contains("解除"),
                "Actual attention Release receives the previous physical owner and produces a bounded release receipt.");
            Require(f.Model.FormalCalls == 0, "Fast head release never enters the old tool path.");
        });

        Case("stale-owner-release-cannot-release-successor", f => {
            var task = f.SeedHead(true);
            Set(f.Attention, "<OwnerId>k__BackingField", "different-task:9");
            Call(f.Chat, "CancelFastHeadTask", "fixture-old-task-cancel");
            Require(f.Runtime.HeadTask.State == "cancelled" && f.Attention.OwnerId == "different-task:9" &&
                f.Attention.IsSettled, "Scoped release cannot cancel an unrelated successor controller owner.");
            Require(!f.Runtime.Confirm(task.Id, task.Revision), "Late confirmation cannot revive the cancelled task.");
        });
    }

    private static void SpeechLeaseCases()
    {
        foreach (string invalidation in new[] { "task-failed", "owner-lost", "output-revoked", "task-corrected" })
            Case("pending-completion-revoked-" + invalidation, f => {
                var task = f.SeedHead(true); f.Arm("保持现在的姿势。", false);
                Call(f.Chat, "CompleteFastHeadReply", f.Model, f.Prompt, "zh", f.Generation, true,
                    task.Id, task.Revision, f.Runtime.OutputRevision);
                Require(f.Queued().Contains("保持这个头部姿势") &&
                    (bool)Call(f.Chat, "ValidateFastHeadSpeech", f.Round),
                    "Only current confirmed controlled head facts authorize a completion queue entry.");
                f.AddQueueItem("m_PendingChunks", "PendingSpeechChunk", f.Round + 10, "unrelated-round");
                f.AddQueueItem("m_PendingClips", "PendingSpeechClip", f.Round, "unplayed-completion");
                f.AddQueueItem("m_PendingClips", "PendingSpeechClip", f.Round + 10, "unrelated-clip");
                if (invalidation == "task-failed") f.Runtime.Fail(task.Id, task.Revision, "fixture-failed");
                else if (invalidation == "owner-lost") Set(f.Attention, "<OwnerId>k__BackingField", "new-physical-owner");
                else if (invalidation == "output-revoked") f.Runtime.BeginTurn();
                else Require(f.Runtime.TryApply(Decision("head", "head.lower", "低一点"), "低一点",
                    task.Id, task.Revision, out _, out _), "Correction advances current task revision.");
                Require(!(bool)Call(f.Chat, "ValidateFastHeadSpeech", f.Round),
                    "Changed facts revoke queued completion before playback: " + invalidation);
                Require(f.Queued() == "unrelated-round" && f.QueueCount("m_PendingClips") == 1 &&
                    ((HashSet<int>)Get(f.Chat, "m_SilencedSpeechRoundIds")).Contains(f.Round),
                    "Real production discard removes only this round's text/audio queue and silences in-flight delivery.");
                Require(!((IDictionary)Get(f.Chat, "m_FastHeadSpeech")).Contains(f.Round),
                    "Invalid completion lease is removed.");
            });

        Case("unconfirmed-task-cannot-queue-completion", f => {
            var task = f.SeedHead(false); f.Arm("保持这个姿势。", false);
            Call(f.Chat, "CompleteFastHeadReply", f.Model, f.Prompt, "zh", f.Generation, true,
                task.Id, task.Revision, f.Runtime.OutputRevision);
            Require(f.Queued() == "" && f.Model.m_DataList.Count == 0,
                "Starting task alone cannot be reported as physically holding.");
        });
    }

    private static void ActorIntentCases()
    {
        Case("actor-none-publishes-original-draft-once", f => {
            f.Arm("今天想聊什么？", true);
            const string draft = "我想聊聊今天读的那本书。";
            var pending = f.CompileActor(draft);
            Require(pending.Draft == draft && pending.CurrentUser == f.Prompt && f.Queued() == "",
                "The real reply route submits the exact unpublished actor draft before any public queue entry.");
            pending.Answer(ActorDecision("none"), "ok");
            Require(f.Queued() == draft && f.Runtime.HeadTask == null && WaitingTask(f).Type == JTokenType.Null,
                "A no-intent verdict publishes the original actor speech without an action or wait.");
            int history = f.Model.m_DataList.Count;
            pending.Answer(ActorDecision("wait", "head.raise", "user_count_three", draft), "duplicate");
            Require(f.Queued() == draft && f.Model.m_DataList.Count == history && WaitingTask(f).Type == JTokenType.Null,
                "A duplicate actor callback cannot publish twice or register a new future task.");
        });

        Case("actor-false-claim-corrects-without-backfilling-action", f => {
            f.Arm("你现在抬头了吗？", false);
            const string draft = "我已经抬头了。";
            var pending = f.CompileActor(draft);
            pending.Answer(ActorDecision("claim", "head.raise", "none", "已经抬头"), "ok");
            Require(f.Queued().Length > 0 && !f.Queued().Contains(draft),
                "An unsupported completion claim is replaced before it reaches public speech.");
            Require(f.Runtime.HeadTask == null && WaitingTask(f).Type == JTokenType.Null &&
                f.Model.FormalCalls == 0 && !(bool)Get(f.Chat, "m_SceneActionActive"),
                "Correcting a false past-tense claim cannot execute an action to make the claim true.");
        });

        Case("actor-wait-registers-before-promise-and-survives-dialogue", f => {
            f.Arm("等我数完三下再抬头。", false);
            const string draft = "那我等你数到三再抬头。";
            var pending = f.CompileActor(draft);
            Require(f.Queued() == "" && WaitingTask(f).Type == JTokenType.Null,
                "Unclassified conditional speech cannot be heard or create a task.");
            pending.Answer(ActorDecision("wait", "head.raise", "user_count_three", "等你数到三再抬头"), "ok");
            var waiting = WaitingTask(f);
            Require(waiting.Type == JTokenType.Object && (string)waiting["state"] == "waiting" &&
                (string)waiting["action"] == "head.raise" && f.Runtime.HeadTask == null,
                "A conditional actor intention registers a future task and does not execute its head action early.");
            Require(f.Queued() == draft, "The registered wait permits the actor's original promise to be queued.");
            string identity = (string)waiting["id"];
            Call(f.Chat, "ResetSceneVision", "user-started-speaking", false);
            var after = WaitingTask(f);
            Require((string)after["id"] == identity && (string)after["state"] == "waiting" && f.Runtime.HeadTask == null,
                "Ordinary speech interruption and its actual vision reset preserve the independent waiting task.");
        });

        foreach (string revocation in new[] { "generation", "epoch", "speaking", "output-revoked", "scene-reset" })
            Case("actor-late-callback-after-" + revocation, f => {
                f.Arm("等我数到三再动。", true);
                const string draft = "那我等你数到三再抬头。";
                var pending = f.CompileActor(draft);
                if (revocation == "generation") Set(f.Chat, "m_FormalResponseGeneration", f.Generation + 1);
                else if (revocation == "epoch") Set(f.Chat, "m_WorkEpoch", f.Epoch + 1);
                else if (revocation == "speaking") Set(f.Chat, "m_UserSpeechActiveForAutonomy", true);
                else if (revocation == "output-revoked") f.Runtime.RevokeOutput();
                else Call(f.Chat, "ResetSceneVision", "eyes-closed", true);
                pending.Answer(ActorDecision("none"), "late");
                pending.Answer(ActorDecision("wait", "head.raise", "user_count_three", "等你数到三再抬头"), "late-wait");
                Require(f.Queued() == "" && WaitingTask(f).Type == JTokenType.Null &&
                    f.Runtime.HeadTask == null && f.Model.FormalCalls == 0,
                    "A revoked actor verdict cannot publish, arm a condition, execute or hand off: " + revocation);
            });

        Case("actor-condition-probe-precedes-new-fast-decision", f => {
            f.Arm("等我数到三再抬头。", false);
            var actor = f.CompileActor("那我等你数到三再抬头。");
            actor.Answer(ActorDecision("wait", "head.raise", "user_count_three", "等你数到三再抬头"), "ok");
            string identity = (string)WaitingTask(f)["id"];
            f.Arm("是不是要数到三？", true);
            int prior = f.Model.Pending.Count;
            Require((bool)Call(f.Chat, "TryDispatchFirstReply", f.Prompt, null, f.Generation, true, "fixture-context"),
                "The normal shared entry accepts a later input while a wait exists.");
            Require(f.Model.SignalPending.Count == 1 && f.Model.Pending.Count == prior && f.Queued() == "",
                "The existing waiting condition is evaluated before another fast decision can publish.");
            var signal = f.Model.SignalPending.Last();
            Require(signal.CurrentUser == f.Prompt, "Condition classification receives the complete new input.");
            signal.Answer("none", "ok");
            Require(f.Model.Pending.Count == prior + 1 && (string)WaitingTask(f)["id"] == identity &&
                (string)WaitingTask(f)["state"] == "waiting" && f.Runtime.HeadTask == null,
                "A question about counting continues ordinary routing and cannot consume the future action.");
            signal.Answer("sequence_complete", "duplicate");
            Require(f.Model.Pending.Count == prior + 1 && (string)WaitingTask(f)["state"] == "waiting" &&
                f.Runtime.HeadTask == null, "Duplicate condition callbacks cannot trigger or route twice.");
        });

        Case("actor-reaffirm-wait-preserves-count-progress", f => {
            f.Arm("等我数到三再抬头。", false);
            const string original = "那我等你数到三再抬头。";
            var actor = f.CompileActor(original);
            actor.Answer(ActorDecision("wait", "head.raise", "user_count_three", "等你数到三再抬头"), "ok");
            string identity = (string)WaitingTask(f)["id"];
            f.Arm("一", true);
            int fastBefore = f.Model.Pending.Count;
            Require((bool)Call(f.Chat, "TryDispatchFirstReply", f.Prompt, null, f.Generation, true, "fixture-count-one"),
                "The next real counting input reaches the condition probe.");
            Require(f.Model.SignalPending.Count == 1, "One counting input creates one condition classification.");
            f.Model.SignalPending.Last().Answer("one", "ok");
            Require((int)WaitingTask(f)["progress"] == 1 && f.Model.Pending.Count == fastBefore && f.Queued() == "" &&
                !(bool)Get(f.Chat, "m_FormalResponseInFlight") && (bool)Get(f.Chat, "m_StreamComplete"),
                "A count advances the registered task silently instead of launching a second role reply.");
            f.Arm("你还在等吗？", true);
            Require((bool)Call(f.Chat, "TryDispatchFirstReply", f.Prompt, null, f.Generation, true, "fixture-reaffirm-question"),
                "An independent question still reaches ordinary dialogue through the shared entry.");
            f.Model.SignalPending.Last().Answer("none", "ok");
            Require(f.Model.Pending.Count == fastBefore + 1, "A non-count question can request a natural reaffirmation.");
            const string reaffirm = "我继续等你数到三再抬头。";
            int actorsBefore = f.Model.ActorPending.Count;
            f.Model.Pending.Last().Answer(Decision("reply", speech: reaffirm), "ok");
            Require(f.Model.ActorPending.Count == actorsBefore + 1 && f.Queued() == "",
                "A natural reaffirmation remains unpublished until its actor intention is classified.");
            f.Model.ActorPending.Last().Answer(ActorDecision("wait", "head.raise", "user_count_three", "等你数到三再抬头"), "ok");
            var after = WaitingTask(f);
            Require((string)after["id"] == identity && (string)after["state"] == "waiting" &&
                (int)after["progress"] == 1 && (string)after["draft"] == original,
                "Reaffirming the same future action preserves its original identity, evidence and counting progress.");
            Require(f.Queued() == reaffirm && f.Runtime.HeadTask == null && f.Model.FormalCalls == 0,
                "Reaffirmation can speak without rearming, early execution or legacy planning.");
        });

        Case("actor-partial-counts-never-generate-self-counting-replies", f => {
            f.Arm("等我数到三再抬头。", false);
            f.CompileActor("我等你数到三再抬头。").Answer(
                ActorDecision("wait", "head.raise", "user_count_three", "等你数到三再抬头"), "ok");
            string identity = (string)WaitingTask(f)["id"];
            int decisions = f.Model.Pending.Count, actors = f.Model.ActorPending.Count;
            int index = 0;
            foreach (string signal in new[] { "one", "two" })
            {
                f.Arm(++index == 1 ? "一。" : "二。", true);
                Require((bool)Call(f.Chat, "TryDispatchFirstReply", f.Prompt, null, f.Generation, true, "fixture-partial-count"),
                    "Each count enters the existing condition classifier.");
                var request = f.Model.SignalPending.Last(); request.Answer(signal, "ok");
                Require((string)WaitingTask(f)["id"] == identity && (int)WaitingTask(f)["progress"] == index &&
                    (string)WaitingTask(f)["state"] == "waiting" && f.Runtime.HeadTask == null,
                    "Partial user counts preserve one waiting plan and do not execute it.");
                Require(f.Model.Pending.Count == decisions && f.Model.ActorPending.Count == actors && f.Queued() == "" &&
                    !(bool)Get(f.Chat, "m_FormalResponseInFlight") && (bool)Get(f.Chat, "m_StreamComplete"),
                    "Consumed count events neither ask the role to continue counting nor stall the speech owner.");
                request.Answer("sequence_complete", "late-duplicate");
                Require((string)WaitingTask(f)["state"] == "waiting" && f.Runtime.HeadTask == null,
                    "A duplicate partial-count callback cannot trigger the plan.");
            }
        });

        Case("actor-cancel-wait-revokes-unplayed-promise", f => {
            f.Arm("等我数到三再抬头。", false);
            const string draft = "那我等你数到三再抬头。";
            var actor = f.CompileActor(draft);
            actor.Answer(ActorDecision("wait", "head.raise", "user_count_three", "等你数到三再抬头"), "ok");
            Require(f.Queued() == draft, "The registered promise is still waiting for playback.");
            f.AddQueueItem("m_PendingChunks", "PendingSpeechChunk", f.Round + 10, "unrelated-round");
            f.AddQueueItem("m_PendingClips", "PendingSpeechClip", f.Round, "unplayed-promise");
            f.AddQueueItem("m_PendingClips", "PendingSpeechClip", f.Round + 10, "unrelated-clip");
            Require((bool)Call(f.Chat, "CancelActorWaiting", "fixture-explicit-cancel"),
                "Explicit wait cancellation changes the registered task.");
            Require((string)WaitingTask(f)["state"] == "cancelled" && f.Runtime.HeadTask == null,
                "Cancelling the promise never executes its deferred action.");
            Require(f.Queued() == "unrelated-round" && f.QueueCount("m_PendingClips") == 1 &&
                ((HashSet<int>)Get(f.Chat, "m_SilencedSpeechRoundIds")).Contains(f.Round) &&
                !((IDictionary)Get(f.Chat, "m_ActorWaitSpeech")).Contains(f.Round),
                "Cancellation revokes only this promise's unplayed text/audio and marks in-flight speech silenced.");
        });

        Case("actor-unsupported-backend-cannot-publish-unchecked-draft", f => {
            f.Arm("你接下来准备做什么？", false);
            Set(f.Chat, "m_EnableActorIntentCompiler", true);
            f.Model.ActorSupported = false;
            const string draft = "那我马上抬头。";
            var fast = f.Dispatch();
            fast.Answer(Decision("reply", speech: draft), "ok");
            Require(f.Model.ActorPending.Count == 0 && f.Queued().Length > 0 && !f.Queued().Contains(draft),
                "Unavailable actor classification resolves with a correction rather than an unchecked promise.");
            Require(f.Runtime.HeadTask == null && WaitingTask(f).Type == JTokenType.Null && f.Model.FormalCalls == 0,
                "Unavailable judging neither fabricates an action nor silently re-enters legacy planning.");
        });

        Case("actor-evidence-from-user-cannot-authorize-draft-action", f => {
            f.Arm("用户要求抬头。", false);
            const string draft = "我们先聊聊这件事。";
            var actor = f.CompileActor(draft);
            actor.Answer(ActorDecision("act", "head.raise", "none", "用户要求抬头"), "ok");
            Require(f.Queued().Length > 0 && !f.Queued().Contains(draft) &&
                f.Runtime.HeadTask == null && WaitingTask(f).Type == JTokenType.Null,
                "Only evidence in the actor draft can authorize its meaning; a user quote is rejected at the adapter.");
            Require(f.Model.FormalCalls == 0 && !(bool)Get(f.Chat, "m_SceneActionActive"),
                "Rejected evidence cannot escape through the old action route.");
        });

        Case("actor-replan-preserves-proposal-without-execution", f => {
            f.Arm("你想怎么做？", true);
            const string draft = "我先走到屏幕旁，再唱刚才那段。";
            var actor = f.CompileActor(draft);
            actor.Answer(ActorDecision("replan", "none", "none", draft), "ok");
            var proposal = (JObject)Call(f.Chat, "ReadFastLoopPlannerProposal", f.Generation);
            Require((string)proposal["text"] == draft && !(bool)proposal["executed"] && !(bool)proposal["published"],
                "The exact role proposal reaches same-turn planning as unpublished and unexecuted data.");
            Require(f.Queued() == "" && f.Runtime.HeadTask == null &&
                WaitingTask(f).Type == JTokenType.Null && f.Model.FormalCalls == 1 &&
                !(bool)Get(f.Chat, "m_SceneActionActive") && !(bool)Get(f.Chat, "m_HumBackPending"),
                "Replanning transfers to the formal path once, without a canned final reply or invented execution.");
            actor.Answer(ActorDecision("replan", evidence: draft), "duplicate");
            Require(f.Model.FormalCalls == 1 && f.Model.LastFormalUser == f.Prompt && f.Model.m_DataList.Count == 0,
                "Duplicate verdicts cannot replan twice or insert the private proposal into dialogue history.");
        });

        Case("actor-cancel-releases-matching-legacy-held-gaze", f => {
            f.Arm("可以把头放松下来了。", false);
            var legacy = f.SeedLegacyGaze("legacy-held-gaze:1");
            var actor = f.CompileActor("那我解除头部姿势的保持。");
            actor.Answer(ActorDecision("cancel", "head.release", "none", "解除头部姿势的保持"), "ok");
            Require(!(bool)Get(legacy, "active") && (string)Get(legacy, "state") == "released" &&
                (string)Get(legacy, "reason") == "actor-cancelled",
                "A valid actor cancellation ends the legacy gaze captured for this judgement.");
            Require(f.Attention.OwnerId == "legacy-held-gaze:1" && f.Attention.IsReturning &&
                f.Queued().Contains("解除") && f.Runtime.HeadTask == null && f.Model.FormalCalls == 0,
                "The exact legacy physical owner returns while the new loop reports its release without submitting another action.");
        });

        Case("actor-cancel-cannot-release-newer-legacy-gaze-owner", f => {
            f.Arm("可以把头放松下来了。", true);
            var previous = f.SeedLegacyGaze("legacy-held-gaze:1");
            var actor = f.CompileActor("那我解除头部姿势的保持。");
            var successor = f.SeedLegacyGaze("legacy-held-gaze:2");
            actor.Answer(ActorDecision("cancel", "head.release", "none", "解除头部姿势的保持"), "ok");
            Require(!(bool)Get(previous, "active") && (bool)Get(successor, "active") &&
                (string)Get(successor, "state") == "holding" && ReferenceEquals(Get(f.Chat, "m_PersistentSceneAttention"), successor),
                "A gaze replacement during judgement cannot be cancelled using the predecessor's captured authority.");
            Require(f.Attention.OwnerId == "legacy-held-gaze:2" && f.Attention.IsSettled &&
                !f.Queued().Contains("解除") && f.Runtime.HeadTask == null && f.Model.FormalCalls == 0,
                "Late actor cancellation preserves the newer physical owner and does not falsely announce its release.");
        });
    }

    private static ActorIntentDecision ActorDecision(string kind, string action = "none",
        string condition = "none", string evidence = "") => new ActorIntentDecision {
            Domain = kind == "none" ? "dialogue" : kind == "replan" ? "other_action" : "head",
            Kind = kind, Action = action, Condition = condition, Evidence = evidence,
            MotionScope = kind == "none" || kind == "replan" ? "none" : "head_preset",
            Task = kind == "replan" ? "other" : "none", Target = "none"
        };

    private static void PlannerHandoffCases()
    {
        foreach (bool newerTurn in new[] { false, true })
            Case("room-proposal-revocation-closes-only-its-owner-" + newerTurn, f => {
                f.Arm("我们聊聊吧。", false); f.AttachSpeechProvider(); f.AttachControlledRoom();
                var actor = f.CompileActor("我想走到你身边聊。");
                actor.Answer(ActorDecision("replan", evidence: "走到你身边"), "ok");
                Require(f.Model.RoomCalls == 1, "The room decision is still pending before its output capability is disabled.");
                var oldCallback = f.Model.RoomComplete;
                f.Chat.ConfigureRoomMotionOutput(false);
                if (newerTurn) { f.Arm("现在只说话。", false); f.Dispatch(); }
                int generation = (int)Get(f.Chat, "m_FormalResponseGeneration");
                oldCallback(true, "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", "late");
                Require(f.Model.FormalCalls == 0 && f.Queued() == "", "A revoked room proposal never reaches speech or motion.");
                if (newerTurn)
                    Require((int)Get(f.Chat, "m_FormalResponseGeneration") == generation &&
                        (bool)Get(f.Chat, "m_FormalResponseInFlight") && f.Model.FormalCancelCalls == 0,
                        "An obsolete room callback cannot close the newer active fast request.");
                else
                    Require((int)Get(f.Chat, "m_FormalResponseGeneration") > generation &&
                        !(bool)Get(f.Chat, "m_FormalResponseInFlight") && (bool)Get(f.Chat, "m_StreamComplete") &&
                        (bool)Get(f.Chat, "m_TTSSenderDone") && f.Model.FormalCancelCalls == 1,
                        "Same-generation revocation explicitly ends the wait instead of leaving it in-flight forever.");
            });

        Case("unrepresented-wait-condition-cannot-invent-a-three-counter", f => {
            f.Arm("播放开始了。", false);
            const string draft = "等音乐结束，我再抬头。";
            var actor = f.CompileActor(draft);
            actor.Answer(ActorDecision("wait", "head.raise", "user_count_three", draft), "model-semantic-error");
            Require(WaitingTask(f).Type == JTokenType.Null && f.Runtime.HeadTask == null &&
                f.Model.Pending.Count == 1 && f.Model.FormalCalls == 0 && f.Queued() != "",
                "Unsupported conditions produce a deterministic explanation without scheduling, moving, or an ungrounded free-text verifier.");
            actor.Answer(ActorDecision("wait", "head.raise", "user_count_three", draft), "duplicate");
            Require(WaitingTask(f).Type == JTokenType.Null && f.Runtime.HeadTask == null,
                "A duplicate condition verdict cannot invent a waiting trigger.");
        });
        Case("three-counter-numeric-anchor-is-not-an-intent-detector", f => {
            var method = typeof(ChatSample).GetMethod("ActorCountThreeHasLiteralAnchor", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (string text in new[] { "数到三", "三つ数えたら", "count to three", "on the third count", "数到３", "数到3" })
                Require((bool)method.Invoke(null, new object[] { text }), "The explicit finite counter value is representable: " + text);
            foreach (string text in new[] { "音乐结束", "数到十三", "数到三十", "count to thirteen", "13", "30" })
                Require(!(bool)method.Invoke(null, new object[] { text }), "An absent or different numeric value cannot authorize the three counter: " + text);
        });

        foreach (string result in new[] { "唱完", "开灯", "行走", "转身", "取消" })
            Case("other-action-claim-is-verify-only-" + result, f => {
                f.Arm("刚才完成了吗？", false);
                var tts = f.AttachSpeechProvider(); f.AttachControlledRoom();
                string draft = "我已经完成了" + result + "。";
                var actor = f.CompileActor(draft);
                var claim = new ActorIntentDecision { Domain = "other_action", Kind = "claim", Action = "none",
                    Condition = "none", Evidence = draft };
                actor.Answer(claim, "ok");
                Require(f.Model.Pending.Count == 1 && f.Model.RoomCalls == 0 && f.Model.FormalCalls == 0 &&
                    tts.Prepared.Single().Disposed && f.Queued() != "",
                    "Untyped complete claims are corrected from program authority, never sent to a prose reviewer that could invent evidence.");
                int history = f.Model.m_DataList.Count;
                actor.Answer(claim, "duplicate");
                Require(f.Model.FormalCalls == 0 && f.Model.RoomCalls == 0 && f.Runtime.HeadTask == null &&
                    !(bool)Get(f.Chat, "m_HumBackPending") && f.Model.m_DataList.Count == history && !f.Queued().Contains(draft),
                    "Duplicate claims cannot execute, publish the original promise, or add history twice.");
                if (result == "取消")
                {
                    string beforeCancel = f.Queued();
                    Call(f.Chat, "CancelFastLoopRequest", "cancel-verification");
                    actor.Answer(claim, "late");
                    Require(f.Queued() == beforeCancel && f.Model.m_DataList.Count == history,
                        "A completed deterministic correction has no pending verifier; late actor callbacks cannot add output after request cancellation.");
                }
            });

        Case("accepted-transcript-unwrapped-with-original-handoff-preserved", f => {
            string transcript = new string('歌', 350) + "。最后一句：你能把它唱出来吗？";
            string wrapper = "[程序听觉观测] uncertain-mode\n" + transcript;
            f.Arm(wrapper, true);
            Set(f.Chat, "m_LastUserEvidence", wrapper); Set(f.Chat, "m_LastUserMsg", transcript);
            var fast = f.Dispatch();
            Require(fast.CurrentUser == transcript && (bool)JObject.Parse(fast.Context)["inputObservation"]["acceptedInputBound"],
                "Only the exactly bound input transaction unwraps its full transcript, including the trailing request.");
            fast.Answer(Decision("legacy"), "ok");
            Require(f.Model.LastFormalUser == wrapper, "Formal history keeps the original accepted input rather than a reconstructed user turn.");
            Set(f.Chat, "m_FormalResponseGeneration", f.Generation + 1);
            var stale = JObject.Parse((string)Call(f.Chat, "BuildFastLoopContext"));
            Require(!(bool)stale["inputObservation"]["acceptedInputBound"], "A newer generation cannot inherit earlier auditory facts.");
        });

        Case("production-qwen-history-handoff-retains-exact-user-once", f => {
            var qwen = f.AttachHistoryModel();
            qwen.BeginFastLoopHistory("相同原话", "image:first", true);
            var first = qwen.m_DataList.Single();
            qwen.CancelFastLoopDecision();
            Require(ReferenceEquals(Call(qwen, "ClaimFirstReplyUserHandoff"), first) &&
                Call(qwen, "ClaimFirstReplyUserHandoff") == null && qwen.m_DataList.Count == 1,
                "Actual Qwen lease is claimed once and keeps accepted history across quick-backend cancellation.");
            qwen.BeginFastLoopHistory("相同原话", "image:second", true);
            var second = qwen.m_DataList.Last();
            Require(!ReferenceEquals(first, second) && ReferenceEquals(Call(qwen, "ClaimFirstReplyUserHandoff"), second) &&
                second.imageDataUrl == "image:second" && qwen.m_DataList.Count == 2,
                "Repeated identical user text retains two different turn identities and their own images.");
        });

        Case("planner-handoff-room-pending-owns-entry-until-continuation", f => {
            f.Arm("走过来吧。", false);
            f.AttachSpeechProvider();
            f.AttachControlledRoom();
            var actor = f.CompileActor("我现在走过来。");
            actor.Answer(ActorDecision("replan", evidence: "走过来"), "ok");
            Require(f.Model.RoomCalls == 1 && f.Model.FormalCalls == 0,
                "The actual connected room planner owns an asynchronous request before public transport starts.");
            Call(f.Chat, "DispatchFormalStream", f.Prompt, null, f.Generation, true, null, false, false, false);
            Require(f.Model.RoomCalls == 1 && f.Model.FormalCalls == 0 && f.Queued() == "",
                "Duplicate outer entry during room planning cannot create a second decision request.");
            // No avatar action is requested by this controlled decision.
            f.Model.RoomComplete(true, RoomNone(f.Model), "ok");
            Require(f.Model.FormalCalls == 1 && f.Model.RoomCalls == 1,
                "The completed room decision can enter its single explicit formal continuation.");
            f.Model.RoomComplete(true, "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", "duplicate");
            Require(f.Model.FormalCalls == 1, "A duplicate room completion cannot publish twice.");
        });

        Case("planner-handoff-retains-input-image-material-context", f => {
            f.Arm("走到5身边。只用刚才的录音。", true);
            Set(f.Chat, "m_EnableActorIntentCompiler", true);
            string pixels = f.SeedVision("fresh");
            var fast = f.Dispatch(pixels);
            const string draft = "我现在走到5番旁边。";
            fast.Answer(Decision("reply", speech: draft), "ok");
            f.Model.ActorPending.Last().Answer(ActorDecision("replan", evidence: draft), "ok");
            Require(f.Model.FormalCalls == 1 && f.Model.LastFormalUser == f.Prompt && f.Model.LastFormalImage == pixels,
                "Original uncertain ASR text and its bound image survive; no inferred target replaces the user's text.");
            Require(f.Model.LastFormalContext.Contains("fixture-transient-context") &&
                f.Model.LastFormalContext.Contains(draft) && f.Model.LastFormalContext.Contains("recordingMaterials"),
                "Original transient material facts and live capabilities accompany the proposal into formal planning.");
            var proposal = (JObject)Call(f.Chat, "ReadFastLoopPlannerProposal", f.Generation);
            proposal["text"] = "mutated snapshot";
            Require((string)((JObject)Call(f.Chat, "ReadFastLoopPlannerProposal", f.Generation))["text"] == draft,
                "A planner snapshot cannot mutate the owning proposal.");
            string roomInput = (string)Call(f.Chat, "BuildRoomDecisionInputWithObservation", f.Prompt, false, f.Prompt, null, null);
            var room = JObject.Parse(roomInput);
            Require((string)room["actorProposal"]["text"] == draft && (string)room["currentUserText"] == f.Prompt &&
                !(bool)room["autonomous"], "The spatial planner sees the role proposal without turning it into a user command or autonomous tick.");
            Call(f.Chat, "DispatchFormalStream", f.Prompt, pixels, f.Generation, true, null, false, false, false);
            Require(f.Model.FormalCalls == 1 && f.Queued() == "", "Outer duplicate dispatch cannot seize an in-flight planner turn.");
        });

        foreach (string revoke in new[] { "generation", "epoch", "speaking", "output", "cancel" })
            Case("planner-handoff-cannot-revive-" + revoke, f => {
                f.Arm("过来吧。", false);
                const string draft = "那我走过来。";
                var actor = f.CompileActor(draft);
                if (revoke == "generation") Set(f.Chat, "m_FormalResponseGeneration", f.Generation + 1);
                else if (revoke == "epoch") Set(f.Chat, "m_WorkEpoch", f.Epoch + 1);
                else if (revoke == "speaking") Set(f.Chat, "m_UserSpeechActiveForAutonomy", true);
                else if (revoke == "output") f.Runtime.RevokeOutput();
                else Call(f.Chat, "CancelFastLoopRequest", "fixture-cancel");
                actor.Answer(ActorDecision("replan", evidence: draft), "late");
                Require(f.Model.FormalCalls == 0 && f.Queued() == "" && f.Model.m_DataList.Count == 0,
                    "A revoked actor proposal cannot create a new planner or publish a final answer.");
                Require(Call(f.Chat, "ReadFastLoopPlannerProposal", f.Generation) == null,
                    "Stale proposals cannot leak into a later room decision.");
            });

        foreach (bool cancel in new[] { false, true })
            Case("planner-handoff-disposal-reentry-" + cancel, f => {
                f.Arm("你走过来吧。", false);
                var tts = f.AttachSpeechProvider();
                const string draft = "我现在走过来。";
                var actor = f.CompileActor(draft);
                var prepared = tts.Prepared.Single();
                prepared.OnDispose = () => {
                    if (cancel) Call(f.Chat, "CancelFastLoopRequest", "cancel-from-dispose");
                    else actor.Answer(ActorDecision("replan", evidence: draft), "reentrant-duplicate");
                };
                actor.Answer(ActorDecision("replan", evidence: draft), "ok");
                Require(prepared.DisposeCount == 1 && f.Model.FormalCalls == (cancel ? 0 : 1) && f.Queued() == "",
                    "Cancellation during private audio disposal wins; duplicate callbacks cannot dispatch twice.");
            });

        Case("planner-late-formal-output-after-new-turn", f => {
            f.Arm("可以走过来了。", false);
            var actor = f.CompileActor("那我走过来。");
            actor.Answer(ActorDecision("replan", evidence: "走过来"), "ok");
            var speech = f.Model.FormalSpeech;
            var complete = f.Model.FormalComplete;
            f.Arm("不用走，继续聊天。", true);
            speech(new SpeechText("不应听见的旧规划。", "zh"));
            complete("不应听见的旧规划。");
            Require(f.Queued() == "" && f.Model.m_DataList.Count == 0,
                "A superseded formal handoff cannot publish its eventual completion.");
        });

        foreach (bool room in new[] { false, true })
            Case("planner-cancel-owns-pending-" + (room ? "room" : "formal"), f => {
                f.Arm("那就走过来吧。", false);
                if (room) { f.AttachSpeechProvider(); f.AttachControlledRoom(); }
                var actor = f.CompileActor("好，我走过来。");
                actor.Answer(ActorDecision("replan", evidence: "走过来"), "ok");
                Call(f.Chat, "CancelFastLoopRequest", "explicit-fixture-cancel");
                Require((int)Get(f.Chat, "m_FormalResponseGeneration") > f.Generation &&
                    f.Model.FormalCancelCalls == 1 && !(bool)Get(f.Chat, "m_FormalResponseInFlight"),
                    "Fast-loop cancellation still owns its handed-off planner and revokes generation before cancelling transport.");
                if (room) f.Model.RoomComplete(true, "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}", "late");
                else { f.Model.FormalSpeech(new SpeechText("旧回答。", "zh")); f.Model.FormalComplete("旧回答。"); }
                Require(f.Queued() == "" && Call(f.Chat, "ReadFastLoopPlannerProposal", f.Generation) == null &&
                    f.Model.FormalCalls == (room ? 0 : 1), "No late planner callback revives the cancelled proposal.");
            });
    }

    private static void ActorSpeechPreparationCases()
    {
        Case("actor-speech-prepares-before-judge-without-publishing", f => {
            f.Arm("今天想聊什么？", true);
            var tts = f.AttachSpeechProvider();
            const string draft = "今天想聊聊书。";
            int history = f.Model.m_DataList.Count;
            var judge = f.CompileActor(draft);
            Require(tts.Prepared.Count == 1 && tts.DirectCalls == 0 && tts.AdoptCalls == 0,
                "A silent prepared request exists while the actor judge is still pending.");
            Require(f.Queued() == "" && f.QueueCount("m_PendingClips") == 0 && f.Model.m_DataList.Count == history,
                "Speculative synthesis cannot publish text, queue audio or commit assistant history.");
            var prepared = tts.Prepared[0];
            Require(prepared.Speech.Text == draft && !prepared.Disposed,
                "Preparation receives actual unpublished speech rather than a filler.");
            Require(f.TakePrepared(draft, "zh", tts) == null && !prepared.Disposed,
                "Even an exact consumer match cannot take an unapproved draft.");
            judge.Answer(ActorDecision("none"), "ok");
            Require(f.Queued() == draft && f.Model.m_DataList.Count == history + 2,
                "An accepted no-intent verdict publishes the exact original draft once.");
            var player = (IEnumerator)Call(f.Chat, "StreamDirectTTSPlayer", f.Generation);
            try
            {
                Require(player.MoveNext(), "The actual stream player waits for the controlled silent provider.");
                Require(tts.AdoptCalls == 1 && tts.DirectCalls == 0 && tts.Prepared.Count == 1 &&
                    ReferenceEquals(tts.LastAdopted, prepared),
                    "The production player adopts the prepared request without issuing synthesis again.");
                Require(f.TakePrepared(draft, "zh", tts) == null,
                    "Prepared ownership can be transferred only once.");
            }
            finally { (player as IDisposable)?.Dispose(); }
            judge.Answer(ActorDecision("none"), "duplicate");
            Require(tts.Prepared.Count == 1 && tts.AdoptCalls == 1 && f.Model.m_DataList.Count == history + 2,
                "Duplicate judge delivery neither synthesizes nor commits the draft twice.");
        });

        foreach (string verdict in new[] { "claim", "replan" })
            Case("actor-speech-disposes-rejected-" + verdict, f => {
                f.Arm("现在怎么样了？", false);
                var tts = f.AttachSpeechProvider();
                string draft = verdict == "claim" ? "我已经抬头了。" : "我先走到窗边，再唱一首歌。";
                var judge = f.CompileActor(draft);
                var prepared = tts.Prepared.Single();
                judge.Answer(verdict == "claim" ? ActorDecision("claim", "head.raise", "none", "已经抬头") :
                    ActorDecision("replan", "none", "none", draft), "ok");
                Require(prepared.Disposed && prepared.DisposeCount == 1 && f.Queued() != draft,
                    "A corrected or replanned draft disposes the unapproved PCM exactly once.");
                Require(f.TakePrepared(prepared.Speech.Text, prepared.Speech.LanguageCode, tts) == null &&
                    tts.AdoptCalls == 0 && tts.DirectCalls == 0,
                    "The original rejected audio cannot be adopted under a corrected public reply.");
            });

        Case("actor-speech-full-draft-approval-not-only-first-prefix", f => {
            f.Arm("你接下来准备做什么？", true);
            var tts = f.AttachSpeechProvider();
            const string draft = "好的。接下来我要抬头。";
            f.CompileActor(draft);
            var prepared = tts.Prepared.Single();
            Call(f.Chat, "ResolveActorSpeechPreparation", "好的。现在先不执行。", "zh", f.Generation);
            Require(prepared.Disposed && f.TakePrepared(prepared.Speech.Text, "zh", tts) == null,
                "Matching initial words cannot approve audio from a different full draft.");
            Require(f.Queued() == "" && f.Model.m_DataList.Count == 0,
                "Resolving private speculation alone never publishes a reply.");
        });

        foreach (string invalidation in new[] { "generation", "epoch", "output", "round", "speaking", "actor-cancel" })
            Case("actor-speech-private-cache-revoked-" + invalidation, f => {
                f.Arm("稍等，我还在听。", true);
                var tts = f.AttachSpeechProvider();
                const string draft = "我们可以继续聊。";
                var judge = f.CompileActor(draft);
                var prepared = tts.Prepared.Single();
                if (invalidation == "generation") Set(f.Chat, "m_FormalResponseGeneration", f.Generation + 1);
                else if (invalidation == "epoch") Set(f.Chat, "m_WorkEpoch", f.Epoch + 1);
                else if (invalidation == "output") f.Runtime.RevokeOutput();
                else if (invalidation == "round") Set(f.Chat, "m_CurrentSpeechRoundId", f.Round + 1);
                else if (invalidation == "speaking") Set(f.Chat, "m_UserSpeechActiveForAutonomy", true);
                else Call(f.Chat, "CancelActorIntentRequest");
                Require(f.TakePrepared(prepared.Speech.Text, "zh", tts) == null && prepared.Disposed,
                    "A revoked generation, epoch, output, round or listener state cannot leak the cached audio.");
                if (invalidation != "round") judge.Answer(ActorDecision("none"), "late");
                Require(f.Queued() == "" && f.Model.m_DataList.Count == 0 && tts.AdoptCalls == 0,
                    "Late judge callbacks after revocation never publish private speech or history.");
            });

        Case("actor-speech-old-judge-cannot-adopt-newer-preparation", f => {
            f.Arm("第一句话。", false);
            var tts = f.AttachSpeechProvider();
            var previous = f.CompileActor("旧草稿不能公开。");
            var old = tts.Prepared.Single();
            f.Arm("第二句话。", true);
            const string newText = "现在谈新的话题。";
            var current = f.CompileActor(newText);
            Require(old.Disposed && tts.Prepared.Count == 2, "New draft discards the previous private request before preparing.");
            var fresh = tts.Prepared.Last();
            previous.Answer(ActorDecision("none"), "late");
            Require(f.Queued() == "" && !fresh.Disposed && f.Model.m_DataList.Count == 0,
                "Late old verdict cannot publish or dispose the new draft's preparation.");
            current.Answer(ActorDecision("none"), "ok");
            object taken = f.TakePrepared(fresh.Speech.Text, fresh.Speech.LanguageCode, tts);
            Require(taken != null && ReferenceEquals(Get(taken, "Stream"), fresh) &&
                f.TakePrepared(fresh.Speech.Text, fresh.Speech.LanguageCode, tts) == null,
                "Only the approved current draft transfers its own stream once.");
            fresh.Dispose();
        });

        foreach (string mismatch in new[] { "language", "text", "provider" })
            Case("actor-speech-rejects-consumer-" + mismatch + "-mismatch", f => {
                f.Arm("接着聊吧。", false);
                var tts = f.AttachSpeechProvider();
                const string draft = "我们继续聊这个话题。";
                var judge = f.CompileActor(draft);
                judge.Answer(ActorDecision("none"), "ok");
                var prepared = tts.Prepared.Single();
                string language = mismatch == "language" ? "ja" : prepared.Speech.LanguageCode;
                string text = mismatch == "text" ? "不同的台词。" : prepared.Speech.Text;
                var consumer = mismatch == "provider" ? f.AttachSpeechProvider() : tts;
                Require(f.TakePrepared(text, language, consumer) == null && prepared.Disposed,
                    "Text, resolved language and provider identity must all match before cache adoption.");
                Require(tts.AdoptCalls == 0 && consumer.AdoptCalls == 0,
                    "A mismatched consumer never receives speculative PCM.");
            });

        Case("actor-speech-provider-language-resolution-matches-consumer", f => {
            f.Arm("引き続き話そう。", false);
            var tts = f.AttachSpeechProvider(); tts.ResolvedLanguage = "ja";
            const string draft = "続きも聞かせて。";
            var judge = f.CompileActor(draft);
            var prepared = tts.Prepared.Single();
            Require(prepared.Speech.LanguageCode == "ja", "Private preparation uses provider-resolved speech language.");
            judge.Answer(ActorDecision("none"), "ok");
            object taken = f.TakePrepared(prepared.Speech.Text, "ja", tts);
            Require(taken != null && ReferenceEquals(Get(taken, "Stream"), prepared),
                "Consumer's matching resolved language can adopt the same request.");
            prepared.Dispose();
        });

        foreach (string condition in new[] { "busy", "returns-null", "throws" })
            Case("actor-speech-preparation-fallback-" + condition, f => {
                f.Arm("我们继续说话。", true);
                var tts = f.AttachSpeechProvider();
                tts.AllowPrefetch = condition != "busy";
                tts.ReturnNull = condition == "returns-null";
                tts.ThrowOnPrepare = condition == "throws";
                const string draft = "我在听，你继续。";
                var judge = f.CompileActor(draft);
                Require(f.Queued() == "", "Unavailable speculation never bypasses the actor judge.");
                judge.Answer(ActorDecision("none"), "ok");
                Require(f.Queued() == draft && tts.AdoptCalls == 0,
                    "Unavailable preparation leaves an ordinary approved reply for the standard synthesis path.");
                var player = (IEnumerator)Call(f.Chat, "StreamDirectTTSPlayer", f.Generation);
                try
                {
                    Require(player.MoveNext() && tts.DirectCalls == 1 && tts.AdoptCalls == 0,
                        "The real player falls back to one ordinary synthesis request.");
                }
                finally { (player as IDisposable)?.Dispose(); }
            });

        Case("actor-speech-unsupported-judge-skips-speculation", f => {
            f.Arm("你准备做什么？", false);
            var tts = f.AttachSpeechProvider();
            Set(f.Chat, "m_EnableActorIntentCompiler", true);
            f.Model.ActorSupported = false;
            f.Dispatch().Answer(Decision("reply", speech: "我现在抬头。"), "ok");
            Require(tts.Prepared.Count == 0 && tts.WavRequests.Count == 0 && f.Model.ActorPending.Count == 0,
                "Unavailable judging cannot spend a private synthesis request on an uncheckable draft.");
        });

        Case("actor-speech-streaming-provider-without-lookahead-skips-speculation", f => {
            f.Arm("继续聊天吧。", false);
            var tts = f.AttachSpeechProvider(); tts.Lookahead = false;
            var judge = f.CompileActor("可以，我们继续聊。");
            Require(tts.Prepared.Count == 0 && tts.WavRequests.Count == 0,
                "A streaming provider without owned lookahead support cannot safely speculate.");
            judge.Answer(ActorDecision("none"), "ok");
            Require(f.Queued() == "可以，我们继续聊。", "Unsupported speculation still allows approved ordinary speech.");
        });

        foreach (string changed in new[] { "id", "revision", "text", "timeout" })
            Case("actor-speech-private-source-invalidated-" + changed, f => {
                f.Arm("现在先聊天。", false);
                var tts = f.AttachSpeechProvider();
                f.CompileActor("这是一句尚未批准的话。");
                var prepared = tts.Prepared.Single();
                object slot = Get(f.Chat, "m_ActorSpeechPreparation");
                var source = (ActorDraft)Get(slot, "Draft");
                if (changed == "id") source.Id += "-changed";
                else if (changed == "revision") source.Revision++;
                else if (changed == "text") source.Text += "另一个意思。";
                else Set(slot, "StartedAt", Time.realtimeSinceStartup - 26f);
                var watch = (IEnumerator)Call(f.Chat, "WatchActorSpeechPreparation", slot);
                Require(!watch.MoveNext() && prepared.Disposed && Get(f.Chat, "m_ActorSpeechPreparation") == null,
                    "The ownership watcher disposes mutated source data or a timed-out unpublished request.");
                Require(f.Queued() == "" && f.Model.m_DataList.Count == 0 && tts.GlobalCancelCalls == 0,
                    "Discarding private speech never commits it or cancels a provider-wide request.");
            });

        Case("actor-speech-ready-wav-remains-private-until-approval", f => {
            f.Arm("继续聊聊。", false);
            var tts = f.AttachSpeechProvider(); tts.Streaming = false;
            const string draft = "我们接着聊。";
            var judge = f.CompileActor(draft);
            var request = tts.WavRequests.Single();
            var clip = AudioClip.Create("Controlled silent cached clip", 80, 1, 8000, false);
            try
            {
                request.Answer(clip, request.Speech.Text);
                Require(f.TakePrepared(request.Speech.Text, "zh", tts) == null && f.Queued() == "" &&
                    f.QueueCount("m_PendingClips") == 0 && f.Model.m_DataList.Count == 0,
                    "Ready WAV bytes remain private until the verdict approves the original full draft.");
                judge.Answer(ActorDecision("none"), "ok");
                object taken = f.TakePrepared(request.Speech.Text, "zh", tts);
                Require(taken != null && ReferenceEquals(Get(taken, "Clip"), clip) &&
                    f.TakePrepared(request.Speech.Text, "zh", tts) == null && tts.WavRequests.Count == 1,
                    "Approved ready WAV transfers once with no second synthesis request.");
            }
            finally { UnityEngine.Object.DestroyImmediate(clip); }
        });

        Case("actor-speech-approved-wav-waits-for-existing-request", f => {
            f.Arm("请继续。", false);
            var tts = f.AttachSpeechProvider(); tts.Streaming = false;
            var judge = f.CompileActor("我再说一点。");
            var request = tts.WavRequests.Single();
            judge.Answer(ActorDecision("none"), "ok");
            Require((bool)Call(f.Chat, "IsActorPreparedClipPending", f.Generation, f.Round,
                request.Speech.Text, request.Speech.LanguageCode, tts) &&
                f.TakePrepared(request.Speech.Text, "zh", tts) == null,
                "An approved but unfinished WAV waits for the request already in flight.");
            request.Answer(null, request.Speech.Text);
            Require(!(bool)Call(f.Chat, "IsActorPreparedClipPending", f.Generation, f.Round,
                request.Speech.Text, request.Speech.LanguageCode, tts) && tts.GlobalCancelCalls == 0,
                "A failed preparation releases the wait and allows ordinary synthesis without a global cancellation.");
        });

        Case("actor-speech-late-wav-callback-cannot-touch-newer-slot", f => {
            f.Arm("先聊一句。", false);
            var tts = f.AttachSpeechProvider(); tts.Streaming = false;
            var previous = f.CompileActor("旧的未批准草稿。");
            var oldRequest = tts.WavRequests.Single();
            f.Arm("换一个话题。", true);
            var current = f.CompileActor("新的未批准草稿。");
            object newSlot = Get(f.Chat, "m_ActorSpeechPreparation");
            oldRequest.Answer(null, oldRequest.Speech.Text);
            previous.Answer(ActorDecision("none"), "late");
            Require(ReferenceEquals(Get(f.Chat, "m_ActorSpeechPreparation"), newSlot) &&
                !(bool)Get(newSlot, "Done") && !(bool)Get(newSlot, "Approved") &&
                f.Queued() == "" && f.Model.m_DataList.Count == 0 && tts.GlobalCancelCalls == 0,
                "The obsolete WAV callback and verdict cannot release, approve, or cancel a newer private request.");
            Call(f.Chat, "CancelActorIntentRequest");
            tts.WavRequests.Last().Answer(null, "late-after-cancel");
            current.Answer(ActorDecision("none"), "late-after-cancel");
            Require(Get(f.Chat, "m_ActorSpeechPreparation") == null && f.Queued() == "" &&
                f.Model.m_DataList.Count == 0 && tts.GlobalCancelCalls == 0,
                "Cancelled WAV callbacks cannot resurrect a slot or publish text.");
        });

        foreach (string reentry in new[] { "cancel", "generation", "new-draft" })
            Case("actor-speech-synchronous-provider-reentry-" + reentry, f => {
                f.Arm("先等一下。", false);
                var tts = f.AttachSpeechProvider();
                tts.DuringPrepare = speech => {
                    tts.DuringPrepare = null;
                    if (reentry == "cancel") Call(f.Chat, "CancelActorSpeechPreparation", "provider-reentry");
                    else if (reentry == "generation") Set(f.Chat, "m_FormalResponseGeneration", f.Generation + 1);
                    else Call(f.Chat, "BeginActorSpeechPreparation",
                        new ActorDraft { Id = "newer-private-draft", Revision = 1, Text = "另一个尚未公开的草稿。" },
                        "zh", f.Generation, f.Runtime.OutputRevision);
                };
                Set(f.Chat, "m_EnableActorIntentCompiler", true);
                f.Dispatch().Answer(Decision("reply", speech: "原来的尚未公开草稿。"), "ok");
                Require(f.Model.ActorPending.Count == (reentry == "generation" ? 0 : 1),
                    "A generation invalidated during preparation cannot dispatch a stale actor verdict request.");
                Require(tts.Prepared.First().Disposed && tts.Prepared.First().DisposeCount == 1,
                    "A returned handle from a synchronously revoked request is disposed exactly once.");
                if (reentry == "new-draft")
                {
                    object slot = Get(f.Chat, "m_ActorSpeechPreparation");
                    Require(tts.Prepared.Count == 2 && !tts.Prepared.Last().Disposed && slot != null &&
                        ReferenceEquals(Get(slot, "Stream"), tts.Prepared.Last()) &&
                        (string)Get(slot, "DraftId") == "newer-private-draft",
                        "An old reentrant return cannot overwrite or cancel the newer private slot.");
                }
                else Require(Get(f.Chat, "m_ActorSpeechPreparation") == null,
                    "A synchronously cancelled or obsolete handle cannot resurrect its private slot.");
                Require(f.Queued() == "" && f.Model.m_DataList.Count == 0 && tts.GlobalCancelCalls == 0,
                    "Reentry neither publishes the draft nor globally cancels another request.");
            });

        Case("actor-compile-synchronous-provider-cancel-stops-old-dispatch", f => {
            f.Arm("现在先等一下。", true);
            var tts = f.AttachSpeechProvider();
            Set(f.Chat, "m_EnableActorIntentCompiler", true);
            tts.DuringPrepare = speech => {
                tts.DuringPrepare = null;
                Call(f.Chat, "CancelActorIntentRequest");
            };
            f.Dispatch().Answer(Decision("reply", speech: "已经被同步取消的草稿。"), "ok");
            Require(f.Model.ActorPending.Count == 0 && Get(f.Chat, "m_ActorIntentBackend") == null,
                "Cancelling actor intent inside the production prepare call prevents old compilation from posting or reclaiming its backend.");
            Require(tts.Prepared.Count == 1 && tts.Prepared.Single().Disposed &&
                Get(f.Chat, "m_ActorSpeechPreparation") == null && f.Queued() == "" && f.Model.m_DataList.Count == 0,
                "The cancelled compile leaves no private handle, public output, or committed history.");
            const string replacement = "这是同一轮重新生成的内容。";
            Call(f.Chat, "CompileActorDraft", f.Model, f.Prompt, replacement, "zh", f.Generation, true, f.Runtime.OutputRevision);
            Require(f.Model.ActorPending.Count == 1 && f.Model.ActorPending.Single().Draft == replacement &&
                ReferenceEquals(Get(f.Chat, "m_ActorIntentBackend"), f.Model),
                "A subsequent same-generation compile can acquire the backend after synchronous cancellation.");
            f.Model.ActorPending.Single().Answer(ActorDecision("none"), "ok");
            Require(f.Queued() == replacement && f.Model.m_DataList.Count == 2,
                "Only the replacement draft may publish and commit history.");
        });

        Case("actor-compile-synchronous-provider-new-compile-preserves-new-owner", f => {
            f.Arm("请换一种说法。", true);
            var tts = f.AttachSpeechProvider();
            Set(f.Chat, "m_EnableActorIntentCompiler", true);
            const string oldText = "旧草稿的判定不该再发出。";
            const string newText = "这才是同一轮的新草稿。";
            int replacementSerial = -1;
            tts.DuringPrepare = speech => {
                tts.DuringPrepare = null;
                Call(f.Chat, "CompileActorDraft", f.Model, f.Prompt, newText, "zh", f.Generation, true, f.Runtime.OutputRevision);
                replacementSerial = (int)Get(f.Chat, "m_ActorIntentRequestSerial");
                Require(f.Model.ActorPending.Count == 1 && f.Model.ActorPending.Single().Draft == newText,
                    "The nested production compiler dispatches its own new draft before the old preparation returns.");
            };
            var fastReply = f.Dispatch();
            fastReply.Answer(Decision("reply", speech: oldText), "ok");
            Require(f.Model.ActorPending.Count == 1 && f.Model.ActorPending.Single().Draft == newText &&
                (int)Get(f.Chat, "m_ActorIntentRequestSerial") == replacementSerial &&
                ReferenceEquals(Get(f.Chat, "m_ActorIntentBackend"), f.Model),
                "The old compile cannot dispatch its draft, replace the new backend, or change the new request serial after synchronous reentry.");
            object slot = Get(f.Chat, "m_ActorSpeechPreparation");
            Require(tts.Prepared.Count == 2 && tts.Prepared.First().Disposed && !tts.Prepared.Last().Disposed &&
                slot != null && (string)Get(slot, "FullText") == newText &&
                ReferenceEquals(Get(slot, "Stream"), tts.Prepared.Last()) && f.Queued() == "" && f.Model.m_DataList.Count == 0,
                "The newer compiler retains its private audio while the outer compile's handle is discarded.");
            fastReply.Answer(Decision("reply", speech: oldText), "late-duplicate");
            Require(f.Model.ActorPending.Count == 1 && !tts.Prepared.Last().Disposed,
                "A repeated old fast reply cannot restart compilation or revoke the new private request.");
            var judge = f.Model.ActorPending.Single();
            judge.Answer(ActorDecision("none"), "ok");
            var fresh = tts.Prepared.Last();
            object taken = f.TakePrepared(fresh.Speech.Text, fresh.Speech.LanguageCode, tts);
            Require(f.Queued() == newText && f.Model.m_DataList.Count == 2 && taken != null &&
                ReferenceEquals(Get(taken, "Stream"), fresh),
                "The sole current verdict publishes the replacement and makes only its own prepared request consumable.");
            judge.Answer(ActorDecision("none"), "duplicate");
            Require(f.Model.m_DataList.Count == 2 && f.TakePrepared(fresh.Speech.Text, fresh.Speech.LanguageCode, tts) == null,
                "Neither duplicate verdicts nor the old compile can repeat publication or cache ownership transfer.");
            fresh.Dispose();
        });
    }

    private static JToken WaitingTask(Fixture fixture)
    {
        object context = Call(fixture.Chat, "BuildActorIntentContext");
        var facts = context as JObject ?? JObject.Parse(context.ToString());
        return facts["waitingTask"] ?? JValue.CreateNull();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly GameObject host;
        public readonly ChatSample Chat;
        public readonly FastLoopIntegrationModel Model;
        public readonly NevaSceneAttention Attention;
        public FastLoopRuntime Runtime => (FastLoopRuntime)Get(Chat, "m_FastLoop");
        public int Generation = 9100, Epoch = 9200, Round = 9300;
        public string Prompt;

        public Fixture()
        {
            host = new GameObject("FastLoop edit-mode controlled fixture");
            host.hideFlags = HideFlags.HideAndDontSave;
            Chat = host.AddComponent<ChatSample>();
            Model = host.AddComponent<FastLoopIntegrationModel>();
            Attention = host.AddComponent<NevaSceneAttention>();
            Set(Chat, "m_ChatSettings", new ChatSetting { m_ChatModel = Model });
            Set(Chat, "m_SceneAttention", Attention);
            Set(Chat, "m_FastLoop", new FastLoopRuntime());
            // Phase-one cases keep their original single-backend contract. Actor
            // compiler cases opt in explicitly and drive their own fake verdicts.
            Set(Chat, "m_EnableActorIntentCompiler", false);
            Set(Chat, "m_LogAgentLoop", false); Set(Chat, "m_LogRawLLMOutput", false);
            Set(Chat, "m_LogStreamTimings", false); Set(Chat, "m_SceneTimePaused", true);
            Set(Chat, "m_EnableScreenVision", false); Set(Chat, "m_EnableSceneActions", false);
            Require(Chat.isActiveAndEnabled && !Application.isPlaying, "Fixture is an active edit-mode component without Play startup.");
        }

        public void Arm(string prompt, bool realtime)
        {
            Prompt = prompt; Generation++; Epoch++; Round++;
            Set(Chat, "m_FormalResponseGeneration", Generation); Set(Chat, "m_WorkEpoch", Epoch);
            Set(Chat, "m_CurrentSpeechRoundId", Round); Set(Chat, "m_LastUserMsg", prompt);
            Set(Chat, "m_FirstReplyAttemptGeneration", -1); Set(Chat, "m_FormalTransportDispatchGeneration", -1);
            Set(Chat, "m_RealtimeFormalTurnGeneration", realtime ? Generation : -1);
            Set(Chat, "m_BypassFirstReplyRoutingInRealtime", true);
            foreach (string name in new[] { "m_EnableFastTaskLoop", "m_EnableFirstReplyRouting", "m_UseStreaming", "m_IsVoiceMode" }) Set(Chat, name, true);
            foreach (string name in new[] { "m_AgentRunning", "m_AgentCurrentRoundIsTick", "m_AgentRoundInFlight",
                "m_UserSpeechActiveForAutonomy", "m_SceneVisionApplicationPaused", "m_FormalResponseInFlight", "m_StreamComplete",
                "m_HoldSpeechForSongMemoryResult", "m_HoldSpeechForHumBackResult", "m_RoundContinue", "m_RoundSilent",
                "m_RoundIsInner", "m_RoundSilencedForRepeat", "m_RoundRepeatHold", "m_FirstChunkFlushed", "m_SceneActionActive" }) Set(Chat, name, false);
            Set(Chat, "m_RoundInnerCheckDone", true); Set(Chat, "m_RoundRepeatCheckDone", true);
            Set(Chat, "m_SentenceBuffer", new SpeechTextBuffer()); Set(Chat, "m_AssistantHeardText", new StringBuilder());
            Set(Chat, "m_ChatHistory", new List<string> { prompt });
            foreach (string name in new[] { "m_PendingChunks", "m_PendingClips" }) Set(Chat, name, Activator.CreateInstance(Get(Chat, name).GetType()));
            ((HashSet<int>)Get(Chat, "m_SilencedSpeechRoundIds")).Clear();
        }

        public FastLoopIntegrationModel.PendingRequest Dispatch(string image = null)
        {
            int prior = Model.Pending.Count;
            Require((bool)Call(Chat, "TryDispatchFirstReply", Prompt, image, Generation, true, "fixture-transient-context"),
                "Production shared first-reply entry accepts the turn.");
            Require(Model.Pending.Count == prior + 1, "The shared first-reply entry makes exactly one new backend request.");
            return Model.Pending.Last();
        }

        public string SeedVision(string state)
        {
            const string pixels = "data:image/png;base64,SYNTHETIC_VISUAL_BINDING_FIXTURE";
            Set(Chat, "m_EnableScreenVision", true); Set(Chat, "m_AgentEyesOpen", state != "closed");
            var avatar = host.AddComponent<UniVRM10.Vrm10Instance>();
            var eyes = host.AddComponent<NeEEvA.Motion.NevaEyeTracking>();
            foreach (string field in new[] { "head", "leftEye", "rightEye" }) Set(eyes, field, host.transform);
            Set(eyes, "avatar", avatar); Set(eyes, "calibrated", true);
            Set(eyes, "headFrameLocalRotation", Quaternion.identity);
            var capture = host.AddComponent<NevaSceneVisionCapture>();
            Set(capture, "avatar", avatar); Set(capture, "eyeTracking", eyes);
            Set(capture, "<IsObserving>k__BackingField", true); Set(Chat, "m_SceneVisionCapture", capture);
            string binding = (string)Get(capture, "binding");
            string fingerprint = (string)typeof(NevaSceneVisionCapture).GetMethod("CurrentLoadedSceneFingerprint",
                BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            var constructor = typeof(SceneVisionObservation).GetConstructors(Flags).Single();
            var observation = (SceneVisionObservation)constructor.Invoke(new object[] {
                binding + "-fixture", "ready", "", "2026-09-26T00:00:00Z",
                Time.realtimeSinceStartup - (state == "stale" ? 1000f : 0f), 1,
                new Pose(host.transform.position, host.transform.rotation), 60f, 1, 1, pixels,
                host.scene.handle, host.scene.name, host.scene.path, fingerprint, null });
            Call(Chat, "BindFormalSceneObservation", observation, state == "wrong-generation" ? Generation - 1 : Generation);
            return state == "unbound-image" ? pixels + "_UNBOUND" : pixels;
        }

        public FastLoopIntegrationModel.PendingActorIntent CompileActor(string draft)
        {
            Set(Chat, "m_EnableActorIntentCompiler", true);
            int prior = Model.ActorPending.Count;
            var pending = Dispatch();
            pending.Answer(Decision("reply", speech: draft), "ok");
            Require(Model.ActorPending.Count == prior + 1 && Queued() == "",
                "Production fast reply is held for exactly one actor-intent classification.");
            return Model.ActorPending.Last();
        }

        public FastLoopIntegrationTts AttachSpeechProvider()
        {
            var provider = host.AddComponent<FastLoopIntegrationTts>();
            ((ChatSetting)Get(Chat, "m_ChatSettings")).m_TextToSpeech = provider;
            return provider;
        }

        public ChatQW AttachHistoryModel() => host.AddComponent<ChatQW>();

        public void AttachControlledRoom()
        {
            var navigation = host.AddComponent<NeEEvA.Motion.ArdyRoomNavigation>();
            var data = new UnityEngine.AI.NavMeshData();
            Set(navigation, "data", data);
            Set(navigation, "dataInstance", UnityEngine.AI.NavMesh.AddNavMeshData(data));
            var controller = host.AddComponent<NeEEvA.Motion.ArdyRoomLocomotionController>();
            var bridge = host.AddComponent<ArdyRoomDialogueBridge>();
            bridge.chat = Chat; bridge.connectOnStart = false;
            bridge.avatar = host.AddComponent<UniVRM10.Vrm10Instance>();
            Set(bridge, "navigation", navigation); Set(bridge, "controller", controller);
            Set(bridge, "subscribedChat", Chat);
            Set(Chat, "m_MotionOutputEnabled", true); Set(Chat, "m_RoomMotionOutputEnabled", true);
            Model.RoomSupported = true;
            Require(bridge.IsConnected, "Controlled room has a registered empty NavMesh with no user anchor or physical task.");
        }

        public object TakePrepared(string text, string language, TTS provider) =>
            Call(Chat, "TakeActorSpeechPreparation", Generation, Round, text, language, provider);

        public FastLoopTask SeedHead(bool holding)
        {
            Require(Runtime.TryApply(Decision("head", "head.raise", "抬头"), "请抬头", null, 0, out var task, out _),
                "Controlled task proposal is accepted by production runtime.");
            if (holding) Require(Runtime.Confirm(task.Id, task.Revision), "Controlled task confirmation is accepted.");
            task = Runtime.HeadTask;
            Set(Attention, "<OwnerId>k__BackingField", Owner(task));
            Set(Attention, "<State>k__BackingField", holding ? "settled" : "turning");
            return task;
        }

        public object SeedLegacyGaze(string owner)
        {
            var previous = Get(Chat, "m_PersistentSceneAttention");
            if (previous != null) Set(previous, "active", false);
            var type = typeof(ChatSample).GetNestedType("PersistentSceneAttentionTask", BindingFlags.NonPublic);
            object task = Activator.CreateInstance(type, true);
            Set(task, "actionId", owner); Set(task, "active", true); Set(task, "state", "holding");
            Set(Chat, "m_PersistentSceneAttention", task);
            Set(Attention, "<OwnerId>k__BackingField", owner);
            Set(Attention, "<State>k__BackingField", "settled");
            Set(Attention, "<IsPersistent>k__BackingField", true);
            return task;
        }

        public string Queued() => string.Concat(((IEnumerable)Get(Chat, "m_PendingChunks")).Cast<object>().Select(value => (string)Get(value, "Text"))) + Get(Chat, "m_SentenceBuffer");
        public int QueueCount(string field) => (int)Get(Chat, field).GetType().GetProperty("Count").GetValue(Get(Chat, field));
        public void AddQueueItem(string field, string typeName, int round, string text)
        {
            var type = typeof(ChatSample).GetNestedType(typeName, BindingFlags.NonPublic);
            object item = Activator.CreateInstance(type, true);
            Set(item, "RoundId", round); Set(item, "Text", text); Set(item, "LanguageCode", "zh");
            object queue = Get(Chat, field); queue.GetType().GetMethod("Enqueue").Invoke(queue, new[] { item });
        }
        public void Dispose() { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static FastLoopDecision Decision(string route, string candidate = "none", string evidence = "", string speech = "") =>
        new FastLoopDecision { Version = FastLoopProtocol.CurrentVersion, Route = route, Candidate = candidate, Evidence = evidence, Speech = speech, Language = "zh" };
    private static string Owner(FastLoopTask task) => task.Id + ":" + task.Revision;
    private static bool SameTask(FastLoopTask left, FastLoopTask right) => right != null && left.Id == right.Id && left.Revision == right.Revision;
    private static void Require(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static object Call(object target, string name, params object[] args)
    {
        var method = target.GetType().GetMethods(Flags).Single(value => value.Name == name && value.GetParameters().Length == args.Length);
        return method.Invoke(target, args);
    }
    private static string Describe(Exception error)
    {
        while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
        return error.GetType().Name + ": " + error.Message + "\n" + error.StackTrace;
    }
}

/// <summary>No method on this provider can send HTTP, synthesize speech or move a character.</summary>
public class FastLoopIntegrationModel : LLM, IFastLoopDecisionBackend, IActorIntentBackend, IFastTaskControlBackend
{
    public sealed class PendingRequest
    {
        public string Context, CurrentUser, Image;
        public Action<FastLoopDecision, string> Answer;
    }
    public readonly List<PendingRequest> Pending = new List<PendingRequest>();
    public sealed class PendingActorIntent
    {
        public string Context, Draft, CurrentUser;
        public Action<ActorIntentDecision, string> Answer;
    }
    public sealed class PendingActorSignal
    {
        public string Context, CurrentUser;
        public Action<string, string> Answer;
    }
    public readonly List<PendingActorIntent> ActorPending = new List<PendingActorIntent>();
    public readonly List<PendingActorSignal> SignalPending = new List<PendingActorSignal>();
    public readonly List<PendingActorSignal> ControlPending = new List<PendingActorSignal>();
    public int FirstCalls, RoleCalls, FormalCalls, CancelCalls, RoomCalls, FormalCancelCalls;
    public string LastRoomInput;
    public int ActorCancelCalls;
    public bool ActorSupported = true;
    public Action OnFastCancel;
    public bool RoomSupported;
    public Action<bool, string, string> RoomComplete;
    public string LastFormalUser, LastFormalContext, LastFormalImage;
    public Action<SpeechText> FormalSpeech;
    public Action<string> FormalComplete;
    public bool SupportsFastLoopDecisions => true;
    public bool SupportsActorIntents => ActorSupported;
    public override bool SupportsFirstReplyRouting => true;
    public override bool SupportsRoleDecisionMessages => true;
    public override bool SupportsRoomTaskMessages => RoomSupported;
    public override void PostRoomTaskMessage(string input, string imageDataUrl, bool reviewSpeech,
        Action<bool, string, string> callback) { RoomCalls++; LastRoomInput = input; RoomComplete = callback; }
    public void PostFastLoopDecision(string context, string currentUser, Action<FastLoopDecision, string> callback, ConversationTrace trace = null, string imageDataUrl = null)
        => Pending.Add(new PendingRequest { Context = context, CurrentUser = currentUser, Answer = callback, Image = imageDataUrl });
    public void CancelFastLoopDecision() { CancelCalls++; OnFastCancel?.Invoke(); }
    public void PostFastTaskControlDecision(string context, string currentUser, Action<string, string> callback, ConversationTrace trace = null)
        => ControlPending.Add(new PendingActorSignal { Context = context, CurrentUser = currentUser, Answer = callback });
    public override void CancelActiveResponse() { FormalCancelCalls++; }
    public void PostActorIntent(string context, string draft, string currentUser,
        Action<ActorIntentDecision, string> callback, ConversationTrace trace = null)
        => ActorPending.Add(new PendingActorIntent {
            Context = context, Draft = draft, CurrentUser = currentUser, Answer = callback });
    public void PostActorConditionSignal(string context, string currentUser,
        Action<string, string> callback, ConversationTrace trace = null)
        => SignalPending.Add(new PendingActorSignal { Context = context, CurrentUser = currentUser, Answer = callback });
    public void CancelActorIntent() { ActorCancelCalls++; }
    public override void PostFirstReplyStream(string message, Action<SpeechText> onSpeech, Action<FirstReplyCompletion> onComplete,
        string imageDataUrl = null, string transientContext = null, bool commitHistory = true) { FirstCalls++; }
    public override void PostRoleDecisionMsg(string prompt, string imageDataUrl, Action<bool, string, string> callback, ConversationTrace trace) { RoleCalls++; }
    public override void PostRoleDecisionMsg(string prompt, Action<bool, string, string> callback) { RoleCalls++; }
    public override void PostSpeechStreamForStage(string message, Action<SpeechText> onSpeech, Action<string> onComplete,
        string imageDataUrl, bool recordAssistantHistory, FormalResponseStage stage)
    {
        FormalCalls++; LastFormalUser = message; LastFormalContext = RequestContext;
        LastFormalImage = imageDataUrl; FormalSpeech = onSpeech; FormalComplete = onComplete;
    }
    public override void CheckHistory() { }
}

/// <summary>Controlled private requests only: no network, PCM playback, or audio callbacks.</summary>
public sealed class FastLoopIntegrationTts : TTS
{
    public sealed class Job : PreparedSpeechStream
    {
        public SpeechText Speech;
        public bool Disposed;
        public int DisposeCount;
        public Action OnDispose;
        public override void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            DisposeCount++;
            OnDispose?.Invoke();
        }
    }

    public sealed class WavRequest
    {
        public SpeechText Speech;
        public Action<AudioClip, string> Answer;
    }

    public readonly List<Job> Prepared = new List<Job>();
    public readonly List<WavRequest> WavRequests = new List<WavRequest>();
    public bool AllowPrefetch = true, ReturnNull, ThrowOnPrepare;
    public bool Streaming = true, Lookahead = true;
    public string ResolvedLanguage;
    public Action<SpeechText> DuringPrepare;
    public int DirectCalls, AdoptCalls, GlobalCancelCalls;
    public Job LastAdopted;

    public override bool SupportsStreamingPlayback => Streaming;
    public override bool SupportsStreamingLookahead => Lookahead;
    public override bool CanPrefetchNextSpeech => AllowPrefetch;
    public override SpeechText ResolveSpeech(SpeechText speech) =>
        ResolvedLanguage == null ? speech : new SpeechText(speech.Text, ResolvedLanguage);
    public override PreparedSpeechStream PrepareStreamingSpeech(SpeechText speech)
    {
        if (ThrowOnPrepare) throw new InvalidOperationException("Controlled preparation failure.");
        if (ReturnNull) return null;
        var job = new Job { Speech = speech };
        Prepared.Add(job);
        DuringPrepare?.Invoke(speech);
        return job;
    }
    public override void PrepareSpeech(SpeechText speech, Action<AudioClip, string> callback)
    {
        if (ThrowOnPrepare) throw new InvalidOperationException("Controlled WAV preparation failure.");
        WavRequests.Add(new WavRequest { Speech = speech, Answer = callback });
    }
    public override void SpeakPreparedStreamingWithPlaybackGate(PreparedSpeechStream prepared,
        AudioSource output, Action<string> onStarted, Action<bool, string, float> onCompleted,
        Func<StreamingPlaybackPermission> playbackGate)
    {
        AdoptCalls++;
        LastAdopted = (Job)prepared;
        // Keeping callbacks pending lets the real player be stepped without audible output.
    }
    public override void SpeakStreamingWithPlaybackGate(SpeechText speech, AudioSource output,
        Action<string> onStarted, Action<bool, string, float> onCompleted,
        Func<StreamingPlaybackPermission> playbackGate) { DirectCalls++; }
    public override void CancelPreparedSpeech() { GlobalCancelCalls++; }
    private void OnDestroy()
    {
        foreach (var job in Prepared) job.Dispose();
    }
}
