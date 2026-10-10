using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Exercises the actual provider builder supplied by the caller; no Unity/model.
public static class GoalUserUpdateProjectionRegression
{
    public static JObject LastReport { get; private set; }
    public static int RunChecks(Func<string, string> build)
    {
        int checks = 0;
        void Check(bool value, string message) { ++checks; if (!value) throw new InvalidOperationException(message); }
        JObject Input(string wire) => JObject.Parse((string)JObject.Parse(wire)["messages"][1]["content"]);
        string fullTarget = "只关电视柜左侧且不靠窗的那盏灯，不操作另一盏；保留这句最后的限制。" + new string('限', 1400);
        var archive = new JObject { ["quotedDialogue"] = new JArray(
            new JObject { ["role"] = "assistant", ["quotedText"] = "旧目标是电视柜左侧的灯，不是窗边那盏。", ["textTruncated"] = false },
            new JObject { ["role"] = "user", ["quotedText"] = "关掉刚才那个。", ["textTruncated"] = false }) };
        var goal = new JObject { ["goalId"] = "old-goal-1", ["status"] = "waiting_clarification",
            ["originalRequest"] = fullTarget, ["targetDescription"] = fullTarget, ["desiredState"] = "off", ["boundEntityId"] = "entity-lamp-1",
            ["confirmedTarget"] = new JObject { ["entityId"] = "entity-lamp-1", ["captureId"] = "capture-proof-1", ["limitation"] = "不是窗边那盏" },
            ["referentKind"] = "observed", ["referentSourceId"] = "observed-proof-1", ["referenceContext"] = archive,
            ["latestUserText"] = "FROZEN_LATEST_NOT_CURRENT", ["userUpdates"] = new JArray(new JObject {
                ["userTurn"] = 2, ["text"] = "原来那盏是在右边，不是左边；只有蓝色灯罩那盏才可以操作。", ["textTruncated"] = false }),
            ["searchProgress"] = new JObject { ["stepCount"] = 2 } };
        var recent = new JArray(new JObject { ["role"] = "user", ["quotedText"] = "向左转90度。", ["textTruncated"] = false },
            new JObject { ["role"] = "assistant", ["quotedText"] = "刚才没有开始新的动作。", ["textTruncated"] = false });
        const string latest = "重新做刚才那个动作，向左转90度；旧设备任务本轮不要继续。";
        var source = new JObject { ["mode"] = "goal-user-update", ["currentUserText"] = latest, ["latestUserText"] = latest,
            ["existingGoal"] = goal, ["recentDialogue"] = recent,
            ["currentRequestSemantics"] = new JObject { ["requestKind"] = "control", ["actionAuthorization"] = false },
            ["referentSources"] = new JArray(new JObject { ["sourceId"] = "observed-proof-1", ["entityId"] = "entity-lamp-1" }),
            ["verifiedDeviceHistory"] = new JArray(), ["verifiedObservedDeviceHistory"] = new JArray() };
        string original = source.ToString(Formatting.None);
        var wire = JObject.Parse(build(original)); var projected = Input(wire.ToString(Formatting.None));
        Check((string)projected["latestUserText"] == latest && projected["currentUserText"] == null &&
            JToken.DeepEquals(projected["recentDialogue"], recent), "Projection must preserve the complete current tail and ordered actual recent dialogue.");
        foreach (string key in new[] { "goalId", "status", "originalRequest", "targetDescription", "desiredState", "boundEntityId", "confirmedTarget", "referentKind", "referentSourceId" })
            Check(JToken.DeepEquals(projected["existingGoal"][key], goal[key]), "Old target limitation or verified identity link was changed: " + key);
        Check(projected["existingGoal"]["referenceContext"] == null && projected["existingGoal"]["latestUserText"] == null &&
            projected["existingGoal"]["searchProgress"] == null,
            "Frozen old command/progress snapshots must not masquerade as the current instruction in the relation summary.");
        Check(JToken.DeepEquals(projected["existingGoal"]["userUpdates"], goal["userUpdates"]),
            "Accepted human target corrections, exclusions and conditions cannot be discarded as execution history.");
        Check(JToken.DeepEquals(projected["originalTargetReferenceContext"]["context"], archive) &&
            (string)projected["originalTargetReferenceContext"]["goalId"] == "old-goal-1",
            "Old target reference evidence must be preserved separately and bound to the same original goal.");
        Check(JToken.DeepEquals(projected["referentSources"], source["referentSources"]) &&
            JToken.DeepEquals(projected["currentRequestSemantics"], source["currentRequestSemantics"]) && source.ToString(Formatting.None) == original,
            "Projection must not invent permission, lose source IDs, or mutate the stored goal/input.");
        Check(((string)wire["messages"][0]["content"]).StartsWith(RoomTaskProtocol.DeviceGoalUserUpdateReviewContract + "\n", StringComparison.Ordinal) &&
            JToken.DeepEquals(wire["response_format"]["schema"], JObject.Parse(RoomTaskProtocol.DeviceGoalUserUpdateReviewSchema)),
            "Goal updates must use their dedicated actual production contract/schema.");
        var unbound = (JObject)source.DeepClone();
        unbound["existingGoal"]["originalRequest"] = "关掉刚才那个。"; unbound["existingGoal"]["targetDescription"] = "刚才那个。";
        unbound["existingGoal"]["boundEntityId"] = null; unbound["existingGoal"]["confirmedTarget"] = null;
        unbound["existingGoal"]["referentKind"] = "unbound"; unbound["existingGoal"]["referentSourceId"] = "none";
        var ambiguous = Input(build(unbound.ToString(Formatting.None)));
        Check(JToken.DeepEquals(ambiguous["originalTargetReferenceContext"]["context"], archive) &&
            (string)ambiguous["existingGoal"]["targetDescription"] == "刚才那个。" &&
            ambiguous["existingGoal"]["boundEntityId"].Type == JTokenType.Null,
            "A pronoun-only unbound old goal cannot lose its only descriptive reference or acquire an entity.");
        foreach (string shape in new[] { "unknown-constraint", "bad-target-type", "missing-goal-id" })
        {
            var unsupported = (JObject)source.DeepClone(); var old = (JObject)unsupported["existingGoal"];
            if (shape == "unknown-constraint") old["futureOperationConstraint"] = new JObject { ["allowExecution"] = false };
            else if (shape == "bad-target-type") old["targetDescription"] = new JArray("cannot silently coerce");
            else old.Remove("goalId");
            string text = unsupported.ToString(Formatting.None);
            Check((string)JObject.Parse(build(text))["messages"][1]["content"] == text,
                "Unsupported context must remain complete, never become a fabricated safe projection: " + shape);
        }
        string duplicate = original.Replace("\"goalId\":\"old-goal-1\"", "\"goalId\":\"old-goal-1\",\"goalId\":\"forged\"");
        Check((string)JObject.Parse(build(duplicate))["messages"][1]["content"] == duplicate,
            "Strict parse failure must not silently select the last duplicate key and project a forged goal.");
        LastReport = new JObject { ["passed"] = true, ["checks"] = checks,
            ["scope"] = "Actual provider serialization only: current/archived reference separation, complete negative target restrictions, identity links and unsupported-input preservation; no semantic model or execution claim." };
        return checks;
    }
}
