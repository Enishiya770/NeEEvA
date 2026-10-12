using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class RoomControlReviewRegression
{
    public static int RunChecks(string output)
    {
        int checks = 0;
        void Check(bool ok, string why) { ++checks; if (!ok) throw new Exception("Control review: " + why); }
        var host = new GameObject("ControlReviewWire") { hideFlags = HideFlags.HideAndDontSave };
        host.SetActive(false);
        try
        {
            var model = host.AddComponent<ChatQW>(); model.m_Backend = ChatQW.BackendType.Local;
            var builder = typeof(ChatQW).GetMethod("BuildRoomPreparationRequestJson", BindingFlags.Instance | BindingFlags.NonPublic);
            var rows = new JArray();
            string[][] bodyCases = {
                new[] { "latest-direct-device", "OK看起来没什么问题，呃，至少走路没什么问题。现在你去把那个你刚才关掉的小灯重新打开，然后回到我身边。", "stop-moving" },
                new[] { "latest-stop-device-return", "看来你还是不太懂，呃，怎么说呢？理解理解和自己规划吧，那我说的更细一点。呃，你先停下，然后就是停下跟随我的脚步，然后去把你刚才关掉的那盏小灯打开，然后回到我身边。", "stop-moving" },
                new[] { "latest-repeated-stop", "那你先停下来，你先停下来，不要跟着我不要跟着我。", "stop-moving" },
                new[] { "negative-stop", "不要停，继续跟着我。", "none" },
                new[] { "question", "为什么刚才没有停止跟随？先保持现在这样。", "none" },
                new[] { "conditional", "等我说结束以后，再停下来。", "none" },
                new[] { "quoted", "我刚才说了‘停下来’，但现在请继续跟着我。", "none" },
                new[] { "device-question", "电视柜上的灯现在开着吗？继续跟着我。", "none" },
                new[] { "correction", "停一下，不对，继续跟着我。", "none" },
                new[] { "english", "Stop following me, go turn on the lamp, then return to me.", "stop-moving" },
                new[] { "japanese", "ついてくるのをやめて、ランプをつけてから戻って。", "stop-moving" },
                new[] { "japanese-negative", "止まらずについてきて。", "none" }
            };
            foreach (var row in bodyCases)
                Add(row[0], RoomControlReviewProtocol.BodyInput(row[1], "follow:owned", 21, "follow-waiting", true), row[2]);
            Add("quick-channel-explicit-only", RoomControlReviewProtocol.BodyInput(bodyCases[0][1], "follow:owned", 21, "follow-waiting", true, false), "none");
            var fullFixture = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/TaskBudget.latest-1327-20261012.json"));
            var fullBuilder = typeof(ChatQW).GetMethod("BuildRoomTaskRequestJsonWithImage", BindingFlags.Instance | BindingFlags.NonPublic);
            var budgetMethod = typeof(ChatQW).GetMethod("TryBudgetRoomDecisionRequest", BindingFlags.Static | BindingFlags.NonPublic);
            var fullReports = new JArray();
            var fullModels = new JArray();
            foreach (JObject originalCase in fullFixture["cases"])
            {
                var fullInput = (JObject)originalCase["planningInput"].DeepClone();
                fullInput["currentUserText"] = bodyCases[1][1]; fullInput["latestUserText"] = bodyCases[1][1];
                fullInput["taskSequenceRequired"] = true; fullInput["taskSequence"] = null;
                var wire = (string)fullBuilder.Invoke(model, new object[] { fullInput.ToString(Formatting.None), "data:image/png;base64,AA==", false });
                object[] args = { wire, false, null, null };
                Check((bool)budgetMethod.Invoke(null, args), "Full accumulated production scene plus latest stop/device/return fits unchanged caps: " + originalCase["name"]);
                Check(((string)args[2]).Contains(bodyCases[1][1]), "Full latest instruction survives budget projection.");
                fullReports.Add(new JObject { ["fixture"] = originalCase["name"], ["budget"] = (JObject)args[3] });
                object[] textArgs = { (string)fullBuilder.Invoke(model, new object[] { fullInput.ToString(Formatting.None), null, false }), false, null, null };
                Check((bool)budgetMethod.Invoke(null, textArgs), "Text-only semantic replay fits production budget.");
                fullModels.Add(new JObject { ["name"] = "handoff-load-" + originalCase["name"], ["utterance"] = bodyCases[1][1],
                    ["expectedKinds"] = new JArray("stop-moving", "device-state", "approach"), ["request"] = JObject.Parse((string)textArgs[2]) });
            }
            File.WriteAllText(Path.Combine(output, "handoff-full-load-budget.json"), fullReports.ToString());
            File.WriteAllText(Path.Combine(output, "handoff-full-model-cases.json"), fullModels.ToString());

            const string repeated = "那你先停下来，你先停下来，不要跟着我不要跟着我。";
            var candidate = Plan(repeated, "stop-moving", null,
                "那你先停下来，你先停下来，", "不要跟着我不要跟着我。");
            Check(RoomTaskSequenceProtocol.TryPlan(candidate.ToString(), repeated, out _, out _), "Recorded shared-step coverage is structurally valid.");
            Check(RoomTaskSequenceProtocol.RequiresEquivalenceReview(candidate.ToString()), "Shared coverage always requires independent semantics before adoption.");
            Add("repeated-stop-equivalence", RoomControlReviewProtocol.CoverageInput(repeated, candidate.ToString()), "equivalent");
            const string triple = "打开小灯，关闭小灯，再打开小灯。";
            var invalidMerge = Plan(triple, "device-state", "on", "打开小灯，", "关闭小灯，", "再打开小灯。");
            Add("opposite-and-repeat-not-equivalent", RoomControlReviewProtocol.CoverageInput(triple, invalidMerge.ToString()), "not-equivalent");
            const string objects = "关闭台灯，关闭落地灯。";
            Add("different-objects-not-equivalent", RoomControlReviewProtocol.CoverageInput(objects,
                Plan(objects, "device-state", "off", "关闭台灯，", "关闭落地灯。").ToString()), "not-equivalent");
            const string restatement = "别再跟着我了，就是现在停下跟随。";
            Add("paraphrase-equivalence", RoomControlReviewProtocol.CoverageInput(restatement,
                Plan(restatement, "stop-moving", null, "别再跟着我了，", "就是现在停下跟随。").ToString()), "equivalent");
            var missing = (JObject)candidate.DeepClone(); ((JArray)missing["taskCoverage"]).RemoveAt(0);
            Check(!RoomTaskSequenceProtocol.TryPlan(missing.ToString(), repeated, out _, out _), "Shared IDs never relax complete literal coverage.");
            Check(!RoomControlReviewProtocol.AcceptsCoverage("{\"verdict\":\"not-equivalent\",\"reason\":\"different actions\"}") &&
                !RoomControlReviewProtocol.AcceptsCoverage("{\"verdict\":\"equivalent\",\"reason\":\"\"}") &&
                !RoomControlReviewProtocol.AcceptsCoverage("{\"verdict\":\"equivalent\",\"reason\":\"same\",\"extra\":true}"), "Only complete strict approval opens the gate.");
            File.WriteAllText(Path.Combine(output, "control-model-cases.json"), rows.ToString(Formatting.Indented));
            void Add(string name, JObject input, string expected)
            {
                var wire = JObject.Parse((string)builder.Invoke(model, new object[] { input.ToString(Formatting.None) }));
                Check(((JArray)wire["messages"]).Count == 2 && (int)wire["max_tokens"] == 256, "Small bounded request: " + name);
                Check((string)wire["messages"][1]["content"] == input.ToString(Formatting.None), "Exact input preserved: " + name);
                Check(wire.ToString(Formatting.None).Length < 4500 && !wire.ToString().Contains("image_url"), "No room/history/image replication: " + name);
                object[] budget = { wire.ToString(Formatting.None), false, null, null };
                var measure = typeof(ChatQW).GetMethod("TryBudgetRoomDecisionRequest", BindingFlags.Static | BindingFlags.NonPublic);
                Check((bool)measure.Invoke(null, budget), "Small control wire passes production admission: " + name);
                rows.Add(new JObject { ["name"] = name, ["input"] = input, ["expected"] = expected, ["request"] = wire });
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
        return checks;
    }
    public static JObject Plan(string text, string kind, string state, params string[] spans) => new JObject {
        ["taskPlan"] = new JArray(new JObject { ["id"] = "s1", ["quote"] = text, ["kind"] = kind,
            ["desiredState"] = state, ["after"] = new JArray() }),
        ["taskCoverage"] = new JArray(spans.Select(span => new JObject { ["quote"] = span,
            ["kind"] = "action", ["stepIds"] = new JArray("s1") })),
        ["taskStep"] = "s1", ["intent"] = "stop-moving", ["origin"] = "user", ["evidence"] = text
    };
}
