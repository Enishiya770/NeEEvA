using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Complete production-derived device result -> return planning loads.</summary>
public static partial class RoomStepContextRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static object Call(object target, string name, params object[] args) => typeof(ChatQW).GetMethod(name, Flags).Invoke(target, args);
    public static int RunChecks(string output)
    {
        int checks = 0;
        void Require(bool condition, string reason) { ++checks; if (!condition) throw new Exception("Step context: " + reason); }
        var host = new GameObject("StepContextRegression"); host.SetActive(false);
        var assets = new List<TextAsset>();
        string directory = Path.Combine(output, "step-context"); Directory.CreateDirectory(directory);
        var report = new JArray(); var modelCases = new JArray();
        try
        {
            var model = host.AddComponent<ChatQW>(); model.m_Backend = ChatQW.BackendType.Local;
            foreach (string name in new[] { "persona", "language", "behavior" })
                assets.Add(new TextAsset(File.ReadAllText("Assets/AIChatTookit/Prompts/" + name + ".txt")) { name = name });
            typeof(LLM).GetField("m_PromptFiles", Flags).SetValue(model, assets.ToArray());
            var fixtures = JArray.Parse(File.ReadAllText("Assets/Editor/Fixtures/RoomStepContext.latest-20261012.json"));
            foreach (JObject fixture in fixtures)
            {
                string name = (string)fixture["name"];
                var input = (JObject)fixture["planningInput"].DeepClone();
                var original = input.DeepClone(); string utterance = (string)input["currentUserText"];
                var sequence = RoomTaskContextPolicy.Sequence((JObject)input["taskSequence"]);
                Require(JToken.DeepEquals(input, original), "Projection never mutates the engine records.");
                Require(JToken.DeepEquals(sequence, RoomTaskContextPolicy.Sequence(sequence)), "Completed receipt projection is idempotent.");
                Require(sequence.ToString().Length < input["taskSequence"].ToString().Length * .7,
                    "The complete terminal receipt no longer recursively fills the task ledger: " + name);
                foreach (string path in new[] { "goal.goalId", "goal.boundEntityId", "goal.status", "goal.desiredState", "goal.completionEvidence.actionId", "goal.completionEvidence.actual", "goal.operationSummary.operationAttempted", "lastAction.phase", "recordedAtUtc" })
                    Require(JToken.DeepEquals(sequence.SelectToken("steps[0].receipt." + path), input.SelectToken("taskSequence.steps[0].receipt." + path)), "Immutable event proof retained: " + path);
                Require((string)sequence["request"] == utterance && JToken.DeepEquals(sequence["steps"][1], input["taskSequence"]["steps"][1]), "Whole request and pending successor remain literal.");
                Require(RoomTaskContextPolicy.ReadyUserIntents(input)?.SequenceEqual(new[] { "approach" }) == true, "Actual prerequisite permits phase focus.");
                foreach (string boundary in new[] { "initial", "different-work", "different-turn", "different-request", "failed", "running", "next-device", "missing-owner", "reclassification" })
                {
                    var changed = (JObject)input.DeepClone();
                    if (boundary == "initial") changed.Remove("workContinuation");
                    if (boundary == "different-work") changed["workContinuation"]["workEpoch"] = -1;
                    if (boundary == "different-turn") changed["workContinuation"]["userTurn"] = -1;
                    if (boundary == "different-request") changed["currentUserText"] = "现在只解释，不行动。";
                    if (boundary == "failed" || boundary == "running") changed["taskSequence"]["steps"][0]["status"] = boundary;
                    if (boundary == "next-device") changed["taskSequence"]["steps"][1]["kind"] = "device-state";
                    if (boundary == "missing-owner") { ((JObject)changed["taskSequence"]).Remove("workEpoch"); ((JObject)changed["workContinuation"]).Remove("workEpoch"); }
                    if (boundary == "reclassification") changed["workContinuation"]["source"] = "unfulfilled-read-only-request-reclassification";
                    Require(RoomTaskContextPolicy.ReadyUserIntents(changed) == null, "No phase shortcut across " + boundary);
                }
                var custom = (JObject)input.DeepClone(); custom["unknownSafetyConstraint"] = "只走到同层安全地面；等我说开始。";
                var protectedRow = new JObject { ["entityId"] = "protected-row", ["captureId"] = "observed", ["actionAuthorization"] = false,
                    ["customConstraint"] = new JObject { ["forbiddenZone"] = "keep this complete nested restriction" } };
                ((JArray)custom.SelectToken("visibleObjectCatalog.current.candidates")).Add(protectedRow);
                string focused = (string)Call(null, "PrepareRoomPlanningWorkingMemory", custom.ToString(Formatting.None));
                var focusedObject = JObject.Parse(focused);
                Require((string)focusedObject["unknownSafetyConstraint"] == (string)custom["unknownSafetyConstraint"] &&
                    focusedObject.Descendants().OfType<JObject>().Any(o => JToken.DeepEquals(o, protectedRow)), "Unknown top-level and nested restrictions survive attention.");
                Require(JToken.DeepEquals(focusedObject["currentFacts"], custom["currentFacts"]) && JToken.DeepEquals(focusedObject["currentSupport"], custom["currentSupport"]), "Current user position and support evidence stay intact.");

                string wire = (string)Call(model, "BuildRoomTaskRequestJsonWithImage", input.ToString(Formatting.None), "data:image/png;base64,AA==", false);
                object[] budgetArgs = { wire, false, null, null };
                bool admitted = (bool)Call(null, "TryBudgetRoomDecisionRequest", budgetArgs);
                var budget = (JObject)budgetArgs[3];
                File.WriteAllText(Path.Combine(directory, name + "-budget.json"), budget.ToString(Formatting.Indented));
                Require(admitted, "Complete production-derived return wire fits unchanged budget: " + name);
                var bounded = JObject.Parse((string)budgetArgs[2]);
                object[] find = { bounded, null, null };
                Require((bool)Call(null, "TryFindRoomTaskInput", find), "Actual wire contains the complete fact packet.");
                var sentInput = (JObject)find[2];
                Require((string)sentInput["currentUserText"] == utterance && (string)sentInput.SelectToken("taskSequence.steps[0].status") == "completed" &&
                    (string)sentInput.SelectToken("taskSequence.steps[1].status") == "pending" && sentInput["stepAttention"] is JObject,
                    "Return packet retains whole request, actual completion and pending dependency.");
                Require(bounded["response_format"]["schema"].ToString().Contains("approach") && !bounded["response_format"]["schema"].ToString().Contains("set-object-state"), "Native grammar cannot select another object operation during this user return.");
                Require((int?)budget.SelectToken("final.textUnitBudget") == 49152 && (int?)budget.SelectToken("final.totalUnitBudget") == 61440, "Admission ceilings are unchanged.");
                File.WriteAllText(Path.Combine(directory, name + "-request.json"), bounded.ToString(Formatting.Indented));
                // Live semantics checks below are text-only: this fixture is not a new rendered capture.
                string noImageWire = (string)Call(model, "BuildRoomTaskRequestJsonWithImage", input.ToString(Formatting.None), null, false);
                object[] liveBudget = { noImageWire, false, null, null };
                Require((bool)Call(null, "TryBudgetRoomDecisionRequest", liveBudget), "Text-only semantic replay is also admitted.");
                modelCases.Add(new JObject { ["name"] = name, ["utterance"] = utterance, ["expectedStep"] = (string)sequence["readyStepIds"][0], ["expectedIntent"] = "approach", ["request"] = JObject.Parse((string)liveBudget[2]) });

                var resultFacts = (JObject)fixture["resultFacts"];
                string resultContext = SpatialGoalResultContextPolicy.BuildContext(utterance, resultFacts.ToString(Formatting.None));
                string resultWire = (string)Call(model, "BuildSpatialGoalResultRequestJsonForMessages", null, false, resultContext, "", null);
                Require(resultWire != null, "Full completed-step result with real role assets fits 6000: " + name);
                var resultRequest = JObject.Parse(resultWire);
                int estimate = ((JArray)resultRequest["messages"]).OfType<JObject>().Sum(m => SpatialGoalResultContextPolicy.EstimateTextTokens((string)m["content"])) + 64;
                Require(estimate <= 6000 && SpatialGoalResultContextPolicy.TextTokenEstimateBudget == 6000, "No result-budget increase.");
                var resultEvidence = JObject.Parse((string)resultRequest["messages"][1]["content"])["spatialGoalResult"];
                Require((string)resultEvidence["originalUserRequest"] == utterance && resultWire.Contains((string)resultFacts.SelectToken("goal.completionEvidence.actionId")), "Actual completion evidence survives the final wire.");
                File.WriteAllText(Path.Combine(directory, name + "-result-request.json"), resultRequest.ToString(Formatting.Indented));
                var oversized = (JObject)input.DeepClone(); oversized["unknownMandatoryConstraint"] = new string('证', 65000);
                object[] rejected = { (string)Call(model, "BuildRoomTaskRequestJsonWithImage", oversized.ToString(Formatting.None), null, false), false, null, null };
                Require(!(bool)Call(null, "TryBudgetRoomDecisionRequest", rejected), "Protected oversize facts still reject without silent deletion.");
                report.Add(new JObject { ["name"] = name, ["passed"] = true, ["provenance"] = fixture["provenance"].DeepClone(),
                    ["receiptBeforeCharacters"] = input["taskSequence"].ToString(Formatting.None).Length, ["receiptAfterCharacters"] = sequence.ToString(Formatting.None).Length,
                    ["resultTextEstimate"] = estimate, ["planningBudget"] = budget["final"]?.DeepClone(), ["stepAttention"] = sentInput["stepAttention"].DeepClone() });
            }
            File.WriteAllText(Path.Combine(directory, "report.json"), report.ToString(Formatting.Indented));
            File.WriteAllText(Path.Combine(directory, "continuation-model-cases.json"), modelCases.ToString(Formatting.Indented));
            checks += CheckInitialTaskAndTerminalRepair(model, directory);
            checks += CheckAutonomousAttention(model, directory);
        }
        finally { UnityEngine.Object.DestroyImmediate(host); foreach (var asset in assets) UnityEngine.Object.DestroyImmediate(asset); }
        return checks;
    }
}
