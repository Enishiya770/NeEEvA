using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class RoomStepContextRegression
{
    private static int CheckAutonomousAttention(ChatQW model, string directory)
    {
        int checks = 0;
        void Require(bool ok, string why) { ++checks; if (!ok) throw new Exception("Autonomy attention: " + why); }
        var cases = new JArray(); var budgets = new JArray();
        foreach (string status in new[] { "exhausted", "route-rejected", "none" })
        {
            string phase = status == "none" ? "goal-not-reached" : "blocked";
            var facts = new JObject { ["execution"] = new JObject { ["actionId"] = "test-parent", ["targetKind"] = "user",
                ["phase"] = phase, ["approachRetargetStatus"] = status } };
            string text = RoomMovementResultPolicy.Fallback(facts, "approach", "test-parent");
            Require(RoomMovementResultPolicy.IsTerminalFailure(phase) && text != null &&
                (string)RoomMovementResultPolicy.Requirement(facts, "approach", "test-parent")["expectedReport"] == "incomplete",
                "An ended, unfinished approach must retain its actual failure and release its running ledger state.");
            Require(RoomMovementResultPolicy.Fallback(facts, "approach", "different-parent") == null, "Failure reason cannot migrate to another action.");
            foreach (string language in new[] { "en", "ja" })
                Require((string)typeof(ChatSample).GetMethod("LocalizeRoomFallback", Flags).Invoke(null, new object[] { text, language }) != text,
                    "Reason-aware failure has a localized report: " + status + "/" + language);
        }
        foreach (JObject fixture in JArray.Parse(File.ReadAllText("Assets/Editor/Fixtures/RoomStepContext.latest-20261012.json")))
        {
            var input = (JObject)fixture["planningInput"].DeepClone();
            input["autonomous"] = true; input["currentUserText"] = ""; input["latestUserText"] = "刚才已经到达了，我后来又移动了。";
            input["taskSequenceRequired"] = false; input["taskSequence"] = null; input.Remove("workContinuation");
            input["currentRequestSemantics"] = null; input["answerRequired"] = false;
            input["autonomousIntention"] = "根据用户新的位置，主动再次走到用户身边";
            input["autonomousAttention"] = new JObject { ["source"] = "current-private-intent-review", ["kind"] = "current-user-body",
                ["intention"] = input["autonomousIntention"].DeepClone(), ["actionAuthorization"] = false };
            input["unknownUserRestriction"] = "只使用已启用的能力，遵守用户最新的停留或取消要求。";
            string before = input.ToString();
            Require(RoomTaskContextPolicy.AutonomousBodyAttention(input), "The current autonomous intention can focus attention.");
            foreach (string invalid in new[] { "other-intent", "authorization", "user", "other-focus", "missing-review" })
            {
                var changed = (JObject)input.DeepClone();
                if (invalid == "other-intent") changed["autonomousIntention"] = "观察窗边";
                if (invalid == "authorization") changed["autonomousAttention"]["actionAuthorization"] = true;
                if (invalid == "user") changed["autonomous"] = false;
                if (invalid == "other-focus") changed["autonomousAttention"]["kind"] = "scene";
                if (invalid == "missing-review") changed.Remove("autonomousAttention");
                Require(!RoomTaskContextPolicy.AutonomousBodyAttention(changed), "Focus must not spread to " + invalid);
            }
            var request = (string)Call(model, "BuildRoomTaskRequestJsonWithImage", input.ToString(Formatting.None), null, false);
            object[] budgetArgs = { request, false, null, null };
            Require((bool)Call(null, "TryBudgetRoomDecisionRequest", budgetArgs), "Full multi-turn fixture must fit with unchanged ceilings.");
            var wire = JObject.Parse((string)budgetArgs[2]); var budget = (JObject)budgetArgs[3];
            object[] find = { wire, null, null };
            Require((bool)Call(null, "TryFindRoomTaskInput", find), "Production wire retains task input.");
            var sent = (JObject)find[2];
            Require((string)sent.SelectToken("stepAttention.source") == "current-autonomous-intent-attention" &&
                (string)sent["unknownUserRestriction"] == (string)input["unknownUserRestriction"], "Attention retains unknown user restrictions.");
            Require(JToken.DeepEquals(sent["currentFacts"], input["currentFacts"]) && input.ToString() == before,
                "Current pose/evidence survives without changing memory storage.");
            Require((bool?)sent["taskSequenceRequired"] == false && !(sent["taskSequence"] is JObject) && (string)sent["currentUserText"] == "",
                "A new intention cannot inherit old-task execution authority.");
            Require(wire["response_format"]["schema"].ToString().Contains("approach"), "Attention does not remove the approach capability.");
            Require((int?)budget.SelectToken("final.totalUnitBudget") == 61440 && (int?)budget.SelectToken("final.textUnitBudget") == 49152,
                "No budget ceiling increase.");
            budgets.Add(new JObject { ["fixture"] = fixture["name"], ["budget"] = budget, ["attention"] = sent["stepAttention"].DeepClone() });
            cases.Add(new JObject { ["name"] = fixture["name"] + "-new-autonomy", ["request"] = wire, ["expectedIntent"] = "approach" });
        }
        File.WriteAllText(Path.Combine(directory, "autonomy-budget.json"), budgets.ToString(Formatting.Indented));
        File.WriteAllText(Path.Combine(directory, "autonomy-model-cases.json"), cases.ToString(Formatting.Indented));
        return checks;
    }
}
