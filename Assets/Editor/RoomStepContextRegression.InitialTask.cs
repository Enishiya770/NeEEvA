using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class RoomStepContextRegression
{
    private static JToken ExpandNative(JToken token, JObject definitions)
    {
        if (token is JObject obj)
        {
            if (obj["$ref"] != null) return ExpandNative(definitions[((string)obj["$ref"]).Substring("#/$defs/".Length)], definitions);
            return new JObject(obj.Properties().Where(p => p.Name != "$defs").Select(p => new JProperty(p.Name, ExpandNative(p.Value, definitions))));
        }
        if (token is JArray array) return new JArray(array.Select(v => ExpandNative(v, definitions)));
        return token.DeepClone();
    }

    private static int CheckInitialTaskAndTerminalRepair(ChatQW model, string directory)
    {
        int checks = 0;
        void Require(bool ok, string why) { ++checks; if (!ok) throw new Exception("Initial-task budget: " + why); }
        var fixture = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/TaskBudget.latest-1327-20261012.json"));
        var reports = new JArray(); var modelCases = new JArray();
        foreach (JObject row in fixture["cases"])
        {
            var input = (JObject)row["planningInput"].DeepClone(); string request = (string)row["request"];
            string original = input.ToString(Formatting.None);
            var wire = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithImage", original, "data:image/png;base64,AA==", false));
            var schema = (JObject)wire["response_format"]["schema"];
            string expanded = (string)Call(null, "BuildRoomDecisionSchemaForInput", original);
            Require(JToken.DeepEquals(ExpandNative(schema, (JObject)schema["$defs"]), JObject.Parse(expanded)),
                "Native sharing preserves every field, branch, limit and complete-coverage requirement.");
            Require(RoomTaskProtocol.CompactNativeDecisionSchema(schema.ToString(Formatting.None)) == schema.ToString(Formatting.None), "Compiled native schema is idempotent.");
            var expandedSchema = JObject.Parse(expanded);
            var guide = JObject.Parse((string)Call(null, "BuildRoomDecisionFieldGuide", expanded));
            for (int branchIndex = 0; branchIndex < ((JArray)expandedSchema["oneOf"]).Count; ++branchIndex)
            {
                var branch = (JObject)expandedSchema["oneOf"][branchIndex];
                var rule = (JObject)guide["intentConstraints"][branchIndex];
                Require(JToken.DeepEquals(rule["intents"], branch["properties"]["intent"]["enum"]) &&
                    ((JArray)guide["required"]).Values<string>().Concat(((JArray)rule["allowedAdditionalFields"]).Values<string>()).ToHashSet()
                        .SetEquals(((JObject)branch["properties"]).Properties().Select(p => p.Name)), "Field guide reconstructs exactly the allowed branch fields.");
                Require(((JArray)guide["required"]).Values<string>().Concat(((JArray)rule["required"]).Values<string>()).ToHashSet()
                    .SetEquals(((JArray)branch["required"]).Values<string>()), "Field guide reconstructs every branch's required fields.");
            }
            object[] budgetArgs = { wire.ToString(Formatting.None), false, null, null };
            bool admitted = (bool)Call(null, "TryBudgetRoomDecisionRequest", budgetArgs);
            var budget = (JObject)budgetArgs[3];
            File.WriteAllText(Path.Combine(directory, row["name"] + "-initial-budget.json"), budget.ToString(Formatting.Indented));
            Require(admitted, "Complete post-device initial planning fits unchanged ceilings: " + row["name"] + " " + budget["final"]);
            var sent = JObject.Parse((string)budgetArgs[2]); object[] find = { sent, null, null };
            Require((bool)Call(null, "TryFindRoomTaskInput", find), "Bounded input exists.");
            var facts = (JObject)find[2];
            Require((bool)Call(null, "RestoreRoomDecisionSharedEvidence", facts), "Shared observation facts expand without loss.");
            Require((string)facts["currentUserText"] == request && (string)facts["latestUserText"] == request &&
                facts["stepAttention"] == null && facts["taskSequence"].Type == JTokenType.Null,
                "New task keeps the complete literal request and full action menu; no continuation/keyword shortcut.");
            Require((int)budget["final"]["textUnitBudget"] == 49152 && (int)budget["final"]["totalUnitBudget"] == 61440 &&
                (long)budget["final"]["effectiveTextUnitBudget"] == Math.Min(49152, 61440 - (long)budget["final"]["nonTextReserve"]),
                "Actual schema, image, output and framing costs reduce available text under the original limits.");
            Require(input.ToString(Formatting.None) == original, "Request-only serialization leaves runtime memory intact.");
            // Compare unchanged prior fixed costs on the same reconstruction.
            // Original rejected bytes were not logged: this is not byte replay.
            var before = (JObject)wire.DeepClone();
            before["messages"][0]["content"] = fixture["baselineSystemMessage"].DeepClone();
            before["response_format"]["schema"] = fixture["baselineNativeSchema"].DeepClone();
            Require(JToken.DeepEquals(ExpandNative((JObject)before["response_format"]["schema"], (JObject)before["response_format"]["schema"]["$defs"]),
                ExpandNative(schema, (JObject)schema["$defs"])), "Negative control uses the exact same expanded decision contract.");
            object[] oldBudget = { before.ToString(Formatting.None), false, null, null };
            bool previouslyAdmitted = (bool)Call(null, "TryBudgetRoomDecisionRequest", oldBudget);
            File.WriteAllText(Path.Combine(directory, row["name"] + "-before-budget.json"), ((JObject)oldBudget[3]).ToString(Formatting.Indented));
            var beforeMeasure = (JObject)Call(null, "MeasureRoomDecisionBudget", before.ToString(Formatting.None), 49152);
            var afterMeasure = (JObject)Call(null, "MeasureRoomDecisionBudget", wire.ToString(Formatting.None), 49152);
            long saving = (long)beforeMeasure["estimatedUnits"] - (long)afterMeasure["estimatedUnits"];
            Require(saving > 1000 && (long)row["recordedBudget"]["final"]["estimatedUnits"] - saving < 61440,
                "Measured fixed-cost reduction covers the original recorded overflow without discarding another fact.");
            File.WriteAllText(Path.Combine(directory, row["name"] + "-initial-request.json"), sent.ToString(Formatting.Indented));
            // A backend semantic replay may use text only; placeholder RGB must
            // never be submitted. These mixed-session coordinates authorize no action.
            var textWire = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithImage", original, null, false));
            object[] textBudget = { textWire.ToString(Formatting.None), false, null, null };
            Require((bool)Call(null, "TryBudgetRoomDecisionRequest", textBudget), "Text-only full-load model replay admitted.");
            modelCases.Add(new JObject { ["name"] = row["name"], ["utterance"] = request,
                ["expectedKinds"] = ((string)row["name"] == "gen11" || (string)row["name"] == "gen13") ? new JArray("follow") : new JArray("device-state", "approach"),
                ["request"] = JObject.Parse((string)textBudget[2]) });
            input["unknownMandatoryConstraint"] = new string('证', 65000);
            object[] reject = { (string)Call(model, "BuildRoomTaskRequestJsonWithImage", input.ToString(Formatting.None), null, false), false, null, null };
            Require(!(bool)Call(null, "TryBudgetRoomDecisionRequest", reject), "Unknown mandatory information still fails closed intact.");
            reports.Add(new JObject { ["name"] = row["name"], ["recordedOriginal"] = row["recordedBudget"]["final"].DeepClone(),
                ["fixedCostSaving"] = saving, ["reconstructedBeforeAdmitted"] = previouslyAdmitted,
                ["reconstructedBefore"] = ((JObject)oldBudget[3])["final"].DeepClone(), ["after"] = budget["final"].DeepClone() });
        }
        File.WriteAllText(Path.Combine(directory, "initial-task-report.json"), reports.ToString(Formatting.Indented));
        File.WriteAllText(Path.Combine(directory, "initial-task-model-cases.json"), modelCases.ToString(Formatting.Indented));
        var terminal = (JObject)fixture["terminal"];
        model.m_DataList.Add(new LLM.SendData("user", (string)terminal["asr"]));
        string context = SpatialGoalResultContextPolicy.BuildContext((string)terminal["originalRequest"], terminal["facts"].ToString(Formatting.None));
        var repairs = new JArray();
        foreach (string language in new[] { "ja", "zh", "en" })
        {
            string feedback = SpatialGoalResultContextPolicy.BuildRepairFeedback((string)terminal["candidate"], (JObject)terminal["review"], "speech-language-mismatch", language);
            Require(JToken.DeepEquals(JObject.Parse(feedback)["actualReview"], terminal["review"]), "Independent review including custom fields is unchanged.");
            string wire = (string)Call(model, "BuildSpatialGoalResultRequestJsonForMessages", model.m_DataList, true, context, feedback, null);
            Require(wire != null, "Latest complete result plus bounded language correction fits 6000: " + language);
            var request = JObject.Parse(wire);
            Require(((string)request["messages"][2]["content"]).Contains("[Required terminal correction]") &&
                !((string)request["grammar"]).Contains(" | "), "Typed repair puts the required language in the final instruction and restricts its native tag.");
            int units = ((JArray)request["messages"]).OfType<JObject>().Sum(m => SpatialGoalResultContextPolicy.EstimateTextTokens((string)m["content"])) + 64;
            Require(units <= 6000 && wire.Contains((string)terminal["facts"]["goal"]["completionEvidence"]["actionId"]) && wire.Contains((string)terminal["originalRequest"]), "Exact original request, same-entity proof and full correction fit unchanged budget.");
            Require((string)Call(model, "BuildSpatialGoalResultRequestJsonForMessages", model.m_DataList, false, context,
                feedback + "\nUnknown mandatory correction: " + new string('证', 65000), null) == null, "Unknown feedback is preserved and budget rejected, never silently truncated.");
            File.WriteAllText(Path.Combine(directory, "terminal-repair-" + language + ".json"), request.ToString(Formatting.Indented));
            repairs.Add(new JObject { ["language"] = language, ["textEstimate"] = units, ["budget"] = 6000 });
        }
        model.m_DataList.Clear();
        foreach (string invalid in new[] { "{}", "[generic feedback] requiredOutputLanguage=ja", "{\"kind\":\"terminal-result-speech-repair\",\"attempt\":\"1\",\"maximumAttempts\":1,\"requiredOutputLanguage\":\"ja\"}" })
            Require(SpatialGoalResultContextPolicy.RepairLanguage(invalid) == null, "Generic or malformed feedback cannot select the typed language override.");
        File.WriteAllText(Path.Combine(directory, "terminal-repair-report.json"), repairs.ToString(Formatting.Indented));
        return checks;
    }
}
