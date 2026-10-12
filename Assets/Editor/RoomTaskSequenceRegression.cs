using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class RoomTaskSequenceRegression
{
    public static int RunChecks(string output)
    {
        int checks = 0;
        void Require(bool pass, string message) { ++checks; if (!pass) throw new Exception("Task sequence: " + message); }
        const string request = "先打开窗边的灯，再来我现在的位置。";
        var plan = new JArray(
            new JObject { ["id"] = "s1", ["quote"] = "打开窗边的灯", ["kind"] = "device-state", ["desiredState"] = "on", ["after"] = new JArray() },
            new JObject { ["id"] = "s2", ["quote"] = "来我现在的位置", ["kind"] = "approach", ["desiredState"] = null, ["after"] = new JArray("s1") });
        var wrong = new JObject { ["taskPlan"] = plan, ["taskStep"] = "s2", ["intent"] = "approach", ["origin"] = "user", ["evidence"] = "来我现在的位置" };
        wrong["taskCoverage"] = new JArray(
            new JObject { ["quote"] = "先打开窗边的灯，", ["kind"] = "action", ["stepIds"] = new JArray("s1") },
            new JObject { ["quote"] = "再来我现在的位置。", ["kind"] = "action", ["stepIds"] = new JArray("s2") });
        Require(RoomTaskSequenceProtocol.TryPlan(wrong.ToString(), request, out var steps, out _), "Complete literal two-step plan parses.");
        foreach (var s in steps.OfType<JObject>()) s["status"] = "pending";
        Require(RoomTaskProtocol.TryDecision(RoomTaskSequenceProtocol.DecisionOnly(wrong.ToString()), request, false, out var approach, out _), "Legacy action checks still apply independently.");
        Require(!RoomTaskSequenceProtocol.TrySelect(wrong.ToString(), approach, steps, out _, out string reason) && reason == "invalid-task-sequence-prerequisite", "Moving to the user cannot skip the unexecuted device step.");
        wrong["taskStep"] = "s1";
        Require(!RoomTaskSequenceProtocol.TrySelect(wrong.ToString(), approach, steps, out _, out reason) && reason == "invalid-task-sequence-action-kind", "Relabelling movement as the first step cannot bypass its device outcome.");
        steps[0]["status"] = "failed"; wrong["taskStep"] = "s2";
        Require(!RoomTaskSequenceProtocol.TrySelect(wrong.ToString(), approach, steps, out _, out _), "Failure cannot satisfy a predecessor.");
        steps[0]["status"] = "running";
        Require(!RoomTaskSequenceProtocol.TrySelect(wrong.ToString(), approach, steps, out _, out _), "An in-flight predecessor is not complete.");
        steps[0]["status"] = "completed";
        Require(RoomTaskSequenceProtocol.TrySelect(wrong.ToString(), approach, steps, out _, out _), "Actual completion unlocks the dependent movement.");
        steps[1]["status"] = "completed";
        Require(!RoomTaskSequenceProtocol.TrySelect(wrong.ToString(), approach, steps, out _, out _), "A completed step cannot be submitted again.");
        var bad = (JObject)wrong.DeepClone(); bad["taskPlan"][0]["quote"] = "打开窗边的灯。";
        Require(!RoomTaskSequenceProtocol.TryPlan(bad.ToString(), request, out _, out _), "Invented punctuation is not an original quote.");
        var literalFeedback = RoomTaskSequenceProtocol.LiteralQuoteFeedback(bad.ToString(), request);
        Require((string)literalFeedback[0]["path"] == "taskPlan[0].quote" &&
            (string)literalFeedback[0]["quote"] == "打开窗边的灯。" && RoomTaskProtocol.DecisionRepairConstraint("invalid-task-sequence-plan") != null,
            "Bounded repair identifies the altered field without inventing a replacement action.");
        bad = (JObject)wrong.DeepClone(); bad["taskPlan"][0]["after"] = new JArray("s2");
        Require(!RoomTaskSequenceProtocol.TryPlan(bad.ToString(), request, out _, out _), "Forward/cyclic prerequisites are rejected.");
        bad = (JObject)wrong.DeepClone(); bad["taskPlan"][1]["id"] = "s1";
        Require(!RoomTaskSequenceProtocol.TryPlan(bad.ToString(), request, out _, out _), "Duplicate step identities are rejected.");
        bad = (JObject)wrong.DeepClone(); ((JArray)bad["taskCoverage"]).RemoveAt(0);
        Require((string)RoomTaskSequenceProtocol.LiteralQuoteFeedback(bad.ToString(), request)[0]["quote"] == "先打开窗边的灯，",
            "Coverage repair supplies the exact omitted source text, including original connective.");
        Require(!RoomTaskSequenceProtocol.TryPlan(bad.ToString(), request, out _, out reason) && reason == "invalid-task-sequence-coverage", "Omitted first clause cannot pass as a complete plan.");
        bad = (JObject)wrong.DeepClone(); bad.Remove("taskCoverage");
        Require(!RoomTaskSequenceProtocol.TryPlan(bad.ToString(), request, out _, out _), "An unaccounted request requires bounded repair before action.");
        bad = (JObject)wrong.DeepClone(); bad["taskCoverage"][0]["kind"] = "context";
        Require(!RoomTaskSequenceProtocol.TryPlan(bad.ToString(), request, out _, out _), "A background clause cannot authorize a mapped action.");
        bad = (JObject)wrong.DeepClone(); bad["taskCoverage"][1]["quote"] = "再来我过去的位置。";
        Require(!RoomTaskSequenceProtocol.TryPlan(bad.ToString(), request, out _, out _), "Coverage cannot rewrite a target or constraint.");
        var input = new JObject { ["taskSequenceRequired"] = true, ["taskSequence"] = new JObject { ["readyStepIds"] = new JArray("s2") } };
        var schema = JObject.Parse(RoomTaskSequenceProtocol.ExtendSchema(RoomTaskProtocol.DecisionSchema, input));
        Require(schema["properties"]["taskPlan"] == null && schema["properties"]["taskStep"]["enum"].Values<string>().Contains("s2"), "Continuation grammar preserves the existing plan and restricts step selection.");
        Require(RoomTaskContextPolicy.AvailableDeviceGoalSchema(RoomTaskProtocol.DecisionSchema, new JObject()) == RoomTaskProtocol.DecisionSchema &&
            RoomTaskContextPolicy.AvailableDeviceGoalSchema(RoomTaskProtocol.DecisionSchema, new JObject { ["deviceGoalCancellationAvailable"] = true }) == RoomTaskProtocol.DecisionSchema,
            "A live cancellable goal or unknown availability keeps the original cancellation grammar.");
        var available = JObject.Parse(RoomTaskContextPolicy.AvailableDeviceGoalSchema(RoomTaskProtocol.DecisionSchema,
            new JObject { ["deviceGoalCancellationAvailable"] = false }));
        Require(!((JArray)available["oneOf"]).DescendantsAndSelf().OfType<JProperty>().Any(p => p.Name == "operation" &&
            (p.Value["enum"] as JArray)?.Values<string>().Contains("cancel") == true),
            "Engine-confirmed absence of a cancellable goal removes cancellation branches without a language-specific stop rule.");

        var host = new GameObject("TaskSequenceRegression") { hideFlags = HideFlags.HideAndDontSave };
        host.SetActive(false);
        try
        {
            var model = host.AddComponent<ChatQW>();
            model.m_Backend = ChatQW.BackendType.Local;
            var rows = new JArray();
            string[][] cases = {
                new[] { "lamp-then-user", request, "device-state", "approach" },
                new[] { "latest-full-utterance", (string)JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/TaskSequence.latest-2003-20261011.json"))["request"], "device-state", "approach" },
                new[] { "reverse-order", "先来我身边，再去关闭书架旁的台灯。", "approach", "device-state" },
                new[] { "english", "Turn off the desk lamp, then come to my new position.", "device-state", "approach" },
                new[] { "japanese", "先に窓際のランプをつけて、それから今いる私のところへ来て。", "device-state", "approach" },
                new[] { "negation", "不用去开灯，只走到我身边。", "approach" },
                new[] { "read-only", "为什么没有先开灯？只解释，不要再行动。" },
                new[] { "single-movement", "请到我旁边来。", "approach" },
                new[] { "two-devices", "先打开门旁的落地灯，再关闭书架上的台灯，最后来我身边。", "device-state", "device-state", "approach" }
                ,new[] { "latest-follow-stop-on-return", "现在可以停下来了哦，对了，那个你可以去把那盏灯，就是你刚才关掉的那盏灯打开，然后再回到我身边吗？", "stop-moving", "device-state", "approach" }
                ,new[] { "latest-explicit-stop-off-return", "呃，那再试一次，你先停下跟着我的步伐，然后去把灯关掉，然后回到我身边来。", "stop-moving", "device-state", "approach" }
                ,new[] { "english-handoff", "Stop following me, switch off the desk lamp, then return to me.", "stop-moving", "device-state", "approach" }
                ,new[] { "japanese-handoff", "ついてくるのをやめて、机のライトをつけてから、私のところに戻って。", "stop-moving", "device-state", "approach" }
                ,new[] { "keep-following-question", "不要停下来，继续跟着我。刚才那盏灯是什么颜色？", "follow" }
                ,new[] { "cancel-device-stop-only", "不用开灯了，只要停止跟随。", "stop-moving" }
                ,new[] { "latest-1427-direct-reopen", "OK看起来没什么问题，呃，至少走路没什么问题。现在你去把那个你刚才关掉的小灯重新打开，然后回到我身边。", "device-state", "approach" }
                ,new[] { "latest-1427-explicit-handoff", "看来你还是不太懂，呃，怎么说呢？理解理解和自己规划吧，那我说的更细一点。呃，你先停下，然后就是停下跟随我的脚步，然后去把你刚才关掉的那盏小灯打开，然后回到我身边。", "stop-moving", "device-state", "approach" }
                ,new[] { "latest-1427-repeated-stop", "那你先停下来，你先停下来，不要跟着我不要跟着我。", "stop-moving" }
                ,new[] { "repeated-execution-on-off-on", "打开小灯，关闭小灯，再打开小灯。", "device-state", "device-state", "device-state" }
            };
            var method = typeof(ChatQW).GetMethod("BuildRoomTaskRequestJsonWithImage", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var row in cases)
            {
                var facts = new JObject { ["currentUserText"] = row[1], ["latestUserText"] = row[1], ["autonomous"] = false,
                    ["taskSequenceRequired"] = true, ["taskSequence"] = null, ["spatialGoal"] = new JObject { ["status"] = "none" },
                    ["deviceGoalCancellationAvailable"] = false,
                    ["currentFacts"] = new JObject { ["connected"] = true, ["followActive"] = row[0].Contains("handoff") || row[0].Contains("stop") || row[0].Contains("following") },
                    ["decisionContractFacts"] = new JObject { ["deviceGoalAlreadyReviewed"] = false, ["allowedObservationGoalIds"] = new JArray() } };
                var wire = JObject.Parse((string)method.Invoke(model, new object[] { facts.ToString(Formatting.None), null, false }));
                Require(wire.ToString().Contains("taskPlan") && wire.ToString().Contains("taskStep"), "Production wire includes the current plan contract: " + row[0]);
                rows.Add(new JObject { ["name"] = row[0], ["utterance"] = row[1], ["expectedKinds"] = new JArray(row.Skip(2)), ["request"] = wire });
            }
            File.WriteAllText(Path.Combine(output, "sequence-model-cases.json"), rows.ToString(Formatting.Indented));
            File.WriteAllText(Path.Combine(output, "sequence-repair-constraint.json"), RoomTaskProtocol.DecisionRepairConstraint("invalid-task-sequence-coverage").ToString());
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
        return checks + RoomStepContextRegression.RunChecks(output) + RoomControlReviewRegression.RunChecks(output);
    }
}
