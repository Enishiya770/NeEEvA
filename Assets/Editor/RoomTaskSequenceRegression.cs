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
        bad = (JObject)wrong.DeepClone(); bad["taskPlan"][0]["after"] = new JArray("s2");
        Require(!RoomTaskSequenceProtocol.TryPlan(bad.ToString(), request, out _, out _), "Forward/cyclic prerequisites are rejected.");
        bad = (JObject)wrong.DeepClone(); bad["taskPlan"][1]["id"] = "s1";
        Require(!RoomTaskSequenceProtocol.TryPlan(bad.ToString(), request, out _, out _), "Duplicate step identities are rejected.");
        var input = new JObject { ["taskSequenceRequired"] = true, ["taskSequence"] = new JObject { ["readyStepIds"] = new JArray("s2") } };
        var schema = JObject.Parse(RoomTaskSequenceProtocol.ExtendSchema(RoomTaskProtocol.DecisionSchema, input));
        Require(schema["properties"]["taskPlan"] == null && schema["properties"]["taskStep"]["enum"].Values<string>().Contains("s2"), "Continuation grammar preserves the existing plan and restricts step selection.");

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
            };
            var method = typeof(ChatQW).GetMethod("BuildRoomTaskRequestJsonWithImage", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var row in cases)
            {
                var facts = new JObject { ["currentUserText"] = row[1], ["latestUserText"] = row[1], ["autonomous"] = false,
                    ["taskSequenceRequired"] = true, ["taskSequence"] = null, ["spatialGoal"] = new JObject { ["status"] = "none" },
                    ["currentFacts"] = new JObject { ["connected"] = true },
                    ["decisionContractFacts"] = new JObject { ["deviceGoalAlreadyReviewed"] = false, ["allowedObservationGoalIds"] = new JArray() } };
                var wire = JObject.Parse((string)method.Invoke(model, new object[] { facts.ToString(Formatting.None), null, false }));
                Require(wire.ToString().Contains("taskPlan") && wire.ToString().Contains("taskStep"), "Production wire includes the current plan contract: " + row[0]);
                rows.Add(new JObject { ["name"] = row[0], ["utterance"] = row[1], ["expectedKinds"] = new JArray(row.Skip(2)), ["request"] = wire });
            }
            File.WriteAllText(Path.Combine(output, "sequence-model-cases.json"), rows.ToString(Formatting.Indented));
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
        return checks;
    }
}
