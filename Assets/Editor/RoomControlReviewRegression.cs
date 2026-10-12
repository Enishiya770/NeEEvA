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
                new[] { "play-1814-device-return", "去把刚才你关掉的那盏小灯重新打开，然后回到我身边。", "stop-moving" },
                new[] { "play-1814-retry", "再试一遍哦，去把刚才你关掉的那盏小灯重新打开，然后回到我身边。", "stop-moving" },
                new[] { "demo-playback-correction", "今は止まって待っているわ 。 等一下等一下，先先还是先跟着我，还是先跟着我先过来先过来。 . 一直跟着我一直跟着我。 不要管不要管刚才我说的话，现在就一直跟着我。", "none" },
                new[] { "drawer-replacement", "先关上抽屉，再回来跟着我。", "stop-moving" },
                new[] { "conditioned-replacement", "等我说可以了，再去关上抽屉。现在继续跟着我。", "none" },
                new[] { "reported-stop-only", "今は止まって待っているわ。", "none" },
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
            foreach (string directive in new[] { "continue-current", "stop-current", "other-task", "none", "unclear" })
                foreach (string force in new[] { "command-now", "conditional", "reported", "question", "unclear" })
                    foreach (bool replacement in new[] { false, true }) {
                        bool command = force == "command-now" && new[] { "continue-current", "stop-current", "other-task" }.Contains(directive);
                        string json = new JObject { ["explicitStopNow"] = directive == "stop-current" && command, ["bodyDirective"] = directive, ["force"] = force,
                            ["reason"] = "Complete turn reviewed." }.ToString();
                        Check(RoomControlReviewProtocol.TryBodyDecision(json, "当前原话", replacement, out var decision, out _) &&
                            (decision.intent == "stop-moving") == (command && (directive == "stop-current" || replacement && directive == "other-task")),
                            "Directive/force and engine stage gate are independent.");
                    }
            foreach (string json in new[] { "{}", "{\"intent\":\"stop-moving\",\"origin\":\"user\",\"evidence\":\"当前原话\"}",
                "{\"bodyDirective\":\"stop-current\",\"force\":\"command-now\",\"evidence\":\"未说过的内容\",\"reason\":\"test\"}" })
                Check(!RoomControlReviewProtocol.TryBodyDecision(json, "当前原话", true, out _, out _), "Malformed/legacy/ungrounded control cannot stop.");
            Add("quick-channel-explicit-only", RoomControlReviewProtocol.BodyInput(bodyCases[0][1], "follow:owned", 21, "follow-waiting", true, false), "none");
            foreach (var row in bodyCases.Where(c => new[] { "latest-stop-device-return", "latest-repeated-stop", "english", "japanese" }.Contains(c[0])))
                Add("explicit-quick-" + row[0], RoomControlReviewProtocol.BodyInput(row[1], "follow:owned", 21, "follow-waiting", true, false), "stop-moving");
            Check(RoomControlReviewProtocol.TryBodyDecision("{\"explicitStopNow\":true,\"bodyDirective\":\"other-task\",\"force\":\"command-now\",\"reason\":\"Stop first, then switch.\"}",
                "先停下，再关上抽屉。", false, out var explicitThenSwitch, out _) && explicitThenSwitch.intent == "stop-moving" &&
                explicitThenSwitch.evidence == "先停下，再关上抽屉。", "Explicit stop is independent of the next task and preserves the engine-owned full words.");
            var fullFixture = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/TaskBudget.latest-1327-20261012.json"));
            var fullBuilder = typeof(ChatQW).GetMethod("BuildRoomTaskRequestJsonWithImage", BindingFlags.Instance | BindingFlags.NonPublic);
            var budgetMethod = typeof(ChatQW).GetMethod("TryBudgetRoomDecisionRequest", BindingFlags.Static | BindingFlags.NonPublic);
            var fullReports = new JArray();
            var fullModels = new JArray();
            var latestFixture = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/TaskBudget.latest-1817-20261012.json"));
            foreach (JObject originalCase in fullFixture["cases"].Concat(latestFixture["cases"]))
            {
                var fullInput = (JObject)originalCase["planningInput"].DeepClone();
                string compound = ((string)originalCase["name"]).StartsWith("latest-play-") ? (string)fullInput["currentUserText"] :
                    "嗯，那我再说的详细一点哦，你现在应该先停止跟随，停止跟随我，然后呢去把那盏你刚才关掉的小灯重新打开，然后回到我身边。";
                fullInput["currentUserText"] = compound; fullInput["latestUserText"] = compound;
                fullInput["taskSequenceRequired"] = true; fullInput["taskSequence"] = null;
                var wire = (string)fullBuilder.Invoke(model, new object[] { fullInput.ToString(Formatting.None), "data:image/png;base64,AA==", false });
                object[] args = { wire, false, null, null };
                bool admitted = (bool)budgetMethod.Invoke(null, args);
                File.WriteAllText(Path.Combine(output, "budget-" + (string)originalCase["name"] + ".json"), ((JObject)args[3]).ToString());
                File.WriteAllText(Path.Combine(output, "wire-" + (string)originalCase["name"] + ".json"), admitted ? (string)args[2] : wire);
                Check(admitted, "Full accumulated production scene plus latest stop/device/return fits unchanged caps: " + originalCase["name"]);
                Check(((string)args[2]).Contains(compound), "Full latest instruction survives budget projection.");
                fullReports.Add(new JObject { ["fixture"] = originalCase["name"], ["budget"] = (JObject)args[3] });
                object[] textArgs = { (string)fullBuilder.Invoke(model, new object[] { fullInput.ToString(Formatting.None), null, false }), false, null, null };
                Check((bool)budgetMethod.Invoke(null, textArgs), "Text-only semantic replay fits production budget.");
                fullModels.Add(new JObject { ["name"] = "handoff-load-" + originalCase["name"], ["utterance"] = compound,
                    ["expectedKinds"] = new[] { "latest-play-gen20", "latest-play-gen22" }.Contains((string)originalCase["name"]) ?
                        new JArray("device-state", "approach") : new JArray("stop-moving", "device-state", "approach"), ["request"] = JObject.Parse((string)textArgs[2]) });
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
            string[][] continuationCases = {
                new[] { "return-after-device", "关掉灯，然后回到我身边。", "回到我身边", "approach", "execute" },
                new[] { "follow-after-device", "把灯打开，接着跟着我。", "跟着我", "follow", "execute" },
                new[] { "different-target", "关掉灯，然后去小明身边。", "去小明身边", "approach", "defer" },
                new[] { "speech-first", "关掉灯，先告诉我结果，说完以后再回到我身边。", "回到我身边", "approach", "defer" },
                new[] { "confirmation-first", "关掉灯，等我确认以后再回到我身边。", "回到我身边", "approach", "defer" },
                new[] { "retracted", "关掉灯，然后回到我身边。不，先别回来，留在那里。", "回到我身边", "approach", "defer" },
                new[] { "english-return", "Close the drawer, then come back to me.", "come back to me", "approach", "execute" },
                new[] { "japanese-return", "照明を消してから、私のそばに戻って。", "私のそばに戻って", "approach", "execute" },
                new[] { "failed-prerequisite", "关掉灯，然后回到我身边。", "回到我身边", "approach", "defer" }
            };
            foreach (var row in continuationCases) {
                var step = new JObject { ["id"] = "s2", ["quote"] = row[2], ["kind"] = row[3], ["after"] = new JArray("s1") };
                Add("continuation-" + row[0], new JObject { ["roomControlReview"] = RoomControlReviewProtocol.Continuation,
                    ["currentUserText"] = row[1], ["selectedStep"] = step,
                    ["steps"] = new JArray(new JObject { ["id"] = "s1", ["kind"] = "device-state",
                        ["status"] = row[0] == "failed-prerequisite" ? "failed" : "completed" }, step.DeepClone()),
                    ["priorResultSpeechCompleted"] = false }, row[4]);
            }
            var approval = new JObject { ["target"] = "current-user", ["readiness"] = "satisfied", ["stepId"] = "s2", ["evidence"] = "回到我身边", ["reason"] = "Explicit return after completed operation." };
            Check(RoomControlReviewProtocol.AcceptsContinuation(approval.ToString(), "s2", continuationCases[0][1], "回到我身边"), "Exact continuation approval accepted.");
            foreach (string invalid in new[] { "target", "readiness", "stepId", "evidence", "reason", "extra", "duplicate" }) {
                var value = (JObject)approval.DeepClone();
                if (invalid == "reason") value[invalid] = 123;
                else if (invalid != "duplicate") value[invalid] = "invalid";
                string json = invalid == "duplicate" ? approval.ToString(Formatting.None).Insert(1, "\"target\":\"other\",") : value.ToString();
                Check(!RoomControlReviewProtocol.AcceptsContinuation(json, "s2", continuationCases[0][1], "回到我身边"), "Reject ungrounded continuation: " + invalid);
            }
            var preflightRows = new JArray();
            var preflightBuilder = typeof(ChatQW).GetMethod("BuildFirstReplyPreflightWire", BindingFlags.Instance | BindingFlags.NonPublic);
            var routeCases = new JArray(FirstReplyTransportRegression.SemanticCases().OfType<JObject>().Where(item =>
                new[] { "logged-come", "come-en", "ordinary-chat", "negated-action", "quoted-order", "gratitude", "arrival-confirmed", "visual-needed",
                    "mixed-answer-and-movement", "mixed-answer-and-device", "active-view-search", "active-action-stop", "latest-return-reminder",
                    "unfinished-only-explain", "retry-come-near-ja" }.Contains((string)item["id"])));
            routeCases.Add(new JObject { ["id"] = "latest-device-return", ["user"] = "去把刚才你关掉的那盏小灯重新打开，然后回到我身边。",
                ["expectedRoute"] = "tools", ["context"] = "{}" });
            routeCases.Add(new JObject { ["id"] = "generic-compound-action", ["user"] = "先把抽屉关上，再到我身边。",
                ["expectedRoute"] = "tools", ["context"] = "{}" });
            routeCases.Add(new JObject { ["id"] = "control-and-action", ["user"] = "不要再跟着我了，去找书架上那本书。",
                ["expectedRoute"] = "tools", ["context"] = "{}" });
            routeCases.Add(new JObject { ["id"] = "latest-long-stop-replacement", ["user"] = "嗯，那我再说的详细一点哦，你现在应该先停止跟随，停止跟随我，然后呢去把那盏你刚才关掉的小灯重新打开，然后回到我身边。",
                ["expectedRoute"] = "tools", ["context"] = "{}" });
            foreach (var focusCase in new[] {
                new[] { "body-follow", "接下来跟着我走。", "current-user-body" },
                new[] { "body-distance", "跟着我，保持两米距离。", "current-user-body" },
                new[] { "body-stop", "现在停下，别再走了。", "current-user-body" },
                new[] { "body-follow-ja", "これから私について歩いて。", "current-user-body" },
                new[] { "body-follow-en", "Follow me, keeping two metres behind.", "current-user-body" },
                new[] { "conditional-movement", "等门打开了再走到我身边。", "general" },
                new[] { "object-constrained-follow", "跟着我，绕开地上的杯子。", "general" },
                new[] { "history-ellipsis", "继续刚才那件事。", "general" },
                new[] { "follow-and-answer", "跟着我走，并告诉我电视柜在哪边。", "general" }
            }) routeCases.Add(new JObject { ["id"] = focusCase[0], ["user"] = focusCase[1],
                ["expectedRoute"] = "tools", ["expectedPlanningFocus"] = focusCase[2], ["context"] = "{}" });
            foreach (JObject item in routeCases) {
                string user = (string)item["user"];
                string request = (string)preflightBuilder.Invoke(model, new object[] { new LLM.SendData("user", user), (string)item["context"] });
                Check(request != null && request.Length < 22000 && !request.Contains("image_url"), "Bounded initial route: " + item["id"]);
                preflightRows.Add(new JObject { ["id"] = item["id"], ["user"] = user, ["expectedRoute"] = item["expectedRoute"],
                    // This reminder names the complete body action and current user.
                    // Both conservative full attention and body attention are safe.
                    ["expectedPlanningFocus"] = (string)item["id"] == "latest-return-reminder" ? new JArray("general", "current-user-body") : item["expectedPlanningFocus"] ?? new JValue(
                        new[] { "logged-come", "come-en", "retry-come-near-ja" }.Contains((string)item["id"]) ? "current-user-body" : "general"),
                    ["request"] = JObject.Parse(request) });
            }
            File.WriteAllText(Path.Combine(output, "preflight-model-cases.json"), preflightRows.ToString());
            File.WriteAllText(Path.Combine(output, "control-model-cases.json"), rows.ToString(Formatting.Indented));
            void Add(string name, JObject input, string expected)
            {
                var wire = JObject.Parse((string)builder.Invoke(model, new object[] { input.ToString(Formatting.None) }));
                Check(((JArray)wire["messages"]).Count == 2 && (int)wire["max_tokens"] == 256, "Small bounded request: " + name);
                Check((string)wire["messages"][1]["content"] == input.ToString(Formatting.None), "Exact input preserved: " + name);
                if ((string)input["roomControlReview"] == RoomControlReviewProtocol.Continuation) {
                    var schema = wire["response_format"]["schema"];
                    Check(JToken.DeepEquals(schema["properties"]["evidence"]["enum"], new JArray("", (string)input.SelectToken("selectedStep.quote"))) &&
                        JToken.DeepEquals(schema["properties"]["stepId"]["enum"], new JArray((string)input.SelectToken("selectedStep.id"))),
                        "Grammar pins IDs and quotes to the current step without guessing semantics.");
                }
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
