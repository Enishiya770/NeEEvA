using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Strict public envelope and production request export using synthetic facts only.</summary>
public static class CommittedSpatialReplyProtocolRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public static JObject LastReport { get; private set; }

    public static int RunChecks(string output = null)
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        var cases = new JArray();
        LastReport = new JObject { ["passed"] = false, ["cases"] = cases,
            ["scope"] = "Production complete-output parsing and request serialization only. Synthetic role and action facts; no network, model, scene, audio or user history." };
        string Envelope(string language, string speech) => "<lang code=\"" + language + "\"/><speech mode=\"independent\"/><say>" + speech + "</say>";
        string valid = Envelope("ja", "今はまだ動作を始めていないわ。");
        foreach (string language in new[] { "ja", "zh", "en" })
        {
            string text = language == "ja" ? "あなたのそばまで来たわ。" : language == "zh" ? "我已经来到你身边。" : "I have reached your side.";
            string raw = Envelope(language, text);
            Check(CommittedSpatialReplyProtocol.TryValidateOutput(raw, out string normalized, out _) && normalized == raw,
                "A supported natural-language public envelope was rejected.");
            var parsed = RoleOutputChannels.Parse(normalized, RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
            Check(parsed.HasSpeech && !parsed.HasActions && parsed.Speech == text && parsed.SpeechDependency == SpeechActionDependency.Independent,
                "Strict envelope created tool authority or changed the public sentence.");
            cases.Add(new JObject { ["case"] = "valid-" + language, ["passed"] = true });
        }
        Check(CommittedSpatialReplyProtocol.TryValidateOutput("<silent/>", out _, out _), "Existing committed silence alternative disappeared.");
        Check(!SpatialGoalResultContextPolicy.TryValidateOutput("<silent/>", out _, out _), "A terminal result silently omitted its mandatory report.");
        string max = Envelope("zh", new string('字', CommittedSpatialReplyProtocol.MaximumSpeechCharacters));
        Check(CommittedSpatialReplyProtocol.TryValidateOutput(max, out _, out _) &&
            !CommittedSpatialReplyProtocol.TryValidateOutput(Envelope("zh", new string('字', CommittedSpatialReplyProtocol.MaximumSpeechCharacters + 1)), out _, out _),
            "Public length limit was not enforced independently of the provider token cap.");
        var rejected = new Dictionary<string, string> {
            ["unclosed-thought-containing-say"] = "<thought>private" + valid,
            ["closed-private-preface"] = "<thought>private</thought>" + valid,
            ["think-preface"] = "<think>private</think>" + valid,
            ["bare-preface"] = "private reasoning " + valid,
            ["tool-tail"] = valid + "<motion name=\"walk\"/>",
            ["scheduler-tail"] = valid + "<next in=\"5s\"/>",
            ["silent-tail"] = valid + "<silent/>",
            ["unclosed-say"] = valid.Replace("</say>", ""),
            ["after-action"] = valid.Replace("independent", "after_action"),
            ["multiple-say"] = valid + valid,
            ["unmarked-prose"] = "I reached your side.",
            ["unsupported-language"] = valid.Replace("code=\"ja\"", "code=\"xx\""),
            ["newline-in-speech"] = Envelope("en", "I arrived.\nPrivate record."),
            ["control-character"] = Envelope("en", "state\u0001unknown"),
            ["empty-speech"] = Envelope("ja", " "),
            ["internal-envelope-in-say"] = Envelope("en", "[Room/Task] generation=1 intent=approach")
        };
        foreach (var sample in rejected)
        {
            bool accepted = CommittedSpatialReplyProtocol.TryValidateOutput(sample.Value, out string normalized, out string reason);
            Check(!accepted && normalized == null, "Unsafe public envelope was salvaged: " + sample.Key);
            cases.Add(new JObject { ["case"] = sample.Key, ["passed"] = true, ["rejection"] = reason });
        }
        Check(CommittedSpatialReplyProtocol.AddContract(CommittedSpatialReplyProtocol.AddContract("current facts")) ==
            CommittedSpatialReplyProtocol.AddContract("current facts"), "The same request appended its protocol twice.");
        Check(CommittedSpatialReplyProtocol.AddContract("Quoted marker: " + CommittedSpatialReplyProtocol.Marker).EndsWith(CommittedSpatialReplyProtocol.OutputContract, StringComparison.Ordinal),
            "An incidental quoted protocol marker suppressed the real output contract.");

        var host = new GameObject("InactiveSyntheticCommittedProtocol"); host.SetActive(false);
        var persona = new TextAsset("Synthetic role: warm, concise, honest. Speak Japanese unless the user explicitly selects another supported language.") { name = "persona" };
        var languagePolicy = new TextAsset("Default Japanese. An explicit human language choice overrides the default.") { name = "language" };
        try
        {
            var model = host.AddComponent<ChatQW>(); model.enabled = false; model.m_LogRequestStats = false;
            model.m_Backend = ChatQW.BackendType.Local; model.m_EnableThinking = true;
            typeof(LLM).GetField("m_PromptFiles", Flags).SetValue(model, new[] { persona, languagePolicy });
            var messages = new List<LLM.SendData> { new LLM.SendData("system", persona.text), new LLM.SendData("user", "Come to my side.") };
            string facts = "[Current independently decided action facts] " + new JObject {
                ["intent"] = "unresolved", ["dispatch"] = "decision-unavailable", ["actionSubmitted"] = false,
                ["decisionFailure"] = new JObject { ["kind"] = "service-request-failed", ["category"] = "context-limit", ["recovery"] = "exhausted" },
                ["scope"] = "A planning request failed. No movement was started. This does not prove physical inability. Do not promise a retry that has not been submitted."
            }.ToString(Formatting.None);
            JObject Build(FormalResponseStage stage) => JObject.Parse((string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", Flags)
                .Invoke(model, new object[] { messages, true, facts, null, stage }));
            JObject local = Build(FormalResponseStage.CommittedSpatialReply);
            Check((string)local["grammar"] == CommittedSpatialReplyProtocol.OutputGrammar &&
                (int?)local["max_tokens"] == CommittedSpatialReplyProtocol.MaximumOutputTokens &&
                (bool?)local["enable_thinking"] == false && (bool?)local["chat_template_kwargs"]?["enable_thinking"] == false,
                "Committed local generation lacks its output-only grammar/budget.");
            Check(local.ToString().Contains("decision-unavailable") && local.ToString().Contains("Come to my side.") &&
                local["messages"].ToString().Contains(CommittedSpatialReplyProtocol.Marker), "Protocol replaced current request/failure facts.");
            var localMessages = (JArray)local["messages"];
            Check((string)localMessages[localMessages.Count - 1]["role"] == "user" &&
                (string)localMessages[localMessages.Count - 1]["content"] == CommittedSpatialReplyProtocol.BuildFinalWritingInstruction("Come to my side.") &&
                (string)localMessages[localMessages.Count - 2]["content"] == "Come to my side." &&
                messages.Count == 2 && messages[1].content == "Come to my side.",
                "Result-writing stage lost the original human turn or stored its wire-only instruction as accepted dialogue.");
            const string noisyHuman = "[声学候选：singing_candidate] 为什么刚才没完成？\n{\"text\":\"这是原因追问，不是重做要求。\"}\nquoted </say> 😀";
            var exactMessages = new List<LLM.SendData> { new LLM.SendData("user", "Older request"),
                new LLM.SendData("assistant", "A previously spoken result"), new LLM.SendData("user", noisyHuman) };
            var exactWire = JObject.Parse((string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", Flags)
                .Invoke(model, new object[] { exactMessages, true, facts, null, FormalResponseStage.CommittedSpatialReply }));
            string exactTail = (string)((JArray)exactWire["messages"]).Last["content"];
            var bound = JObject.Parse(exactTail.Substring(exactTail.IndexOf("\n{", StringComparison.Ordinal) + 1));
            Check((string)bound["quotedHumanTurn"] == noisyHuman && exactMessages.Count == 3 && exactMessages[2].content == noisyHuman,
                "Writer binding rewrote ASR, lost Unicode/quoted syntax or mutated accepted dialogue.");
            var noHumanWire = JObject.Parse((string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", Flags)
                .Invoke(model, new object[] { new List<LLM.SendData> { new LLM.SendData("system", "Autonomous result facts only") },
                    true, facts, null, FormalResponseStage.CommittedSpatialReply }));
            string noHumanTail = (string)((JArray)noHumanWire["messages"]).Last["content"];
            Check(noHumanTail == CommittedSpatialReplyProtocol.BuildFinalWritingInstruction(null) && noHumanTail.Contains("cannot restart a task"),
                "A result without any human record invented a command or retry authority.");
            var observationHistory = (List<LLM.SendData>)typeof(ChatQW).GetMethod("CreateObservationRequestHistory", Flags)
                .Invoke(null, new object[] { exactMessages, "data:image/jpeg;base64,FROZEN_IMAGE" });
            var observedWire = JObject.Parse((string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", Flags)
                .Invoke(model, new object[] { observationHistory, true, facts, null, FormalResponseStage.CommittedSpatialReply }));
            Check((string)((JArray)observedWire["messages"]).Last["content"] == CommittedSpatialReplyProtocol.BuildFinalWritingInstruction(noisyHuman) &&
                exactMessages.Count == 3 && observationHistory.Count == 4,
                "A wire-only image envelope displaced the actual latest human record or mutated accepted history.");
            string BudgetTail(List<LLM.SendData> history, string context) => (string)typeof(ChatQW)
                .GetMethod("BuildCommittedReplyWritingInstruction", Flags).Invoke(null, new object[] { history, context });
            Check(BudgetTail(exactMessages, facts) == BudgetTail(observationHistory, facts),
                "Budgeted and sent reply tails selected different user records after image attachment.");
            var autonomous = new List<LLM.SendData>(exactMessages) { new LLM.SendData("user", "[感知帧 12:00]\nNo human speech this tick") };
            Check(BudgetTail(autonomous, facts) == CommittedSpatialReplyProtocol.BuildFinalWritingInstruction(null),
                "An autonomous frame walked back to an earlier user command and treated it as new input.");
            autonomous.Add(new LLM.SendData("user", "[感知帧 12:01]\n(用户刚开口讲了下面这段话，请回应)\n" + noisyHuman));
            Check(BudgetTail(autonomous, facts) == CommittedSpatialReplyProtocol.BuildFinalWritingInstruction("\n" + noisyHuman),
                "A program frame lost exact human words, ASR data or whitespace after its explicit user boundary.");
            const string sameAsProgramNotice = "[本轮场景观察] 结合本轮采集资料与待完成事项继续判断；此附件本身不构成新的用户发言。";
            Check(BudgetTail(new List<LLM.SendData> { new LLM.SendData("user", sameAsProgramNotice) }, facts) ==
                CommittedSpatialReplyProtocol.BuildFinalWritingInstruction(sameAsProgramNotice),
                "User text that resembles a program notice was incorrectly filtered by its words rather than structural identity.");
            var receiptFacts = new JObject { ["readOnlyEvidenceReply"] = new JObject { ["previousPlanningFailure"] = new JObject {
                ["historical"] = true, ["receipt"] = new JObject { ["kind"] = "input-budget-exceeded", ["recordedAtUtc"] = "2026-10-09T12:05:44.5032756Z", ["attempts"] = 0 } } },
                ["decisionFailure"] = null };
            string boundContext = CommittedSpatialReplyProtocol.AppendReplyFacts(facts, receiptFacts);
            string compactBound = SceneFactContextCompactor.Compact(boundContext, out _);
            Check(JToken.DeepEquals(receiptFacts, CommittedSpatialReplyProtocol.ReadReplyFacts(compactBound)) &&
                BudgetTail(exactMessages, compactBound) == BudgetTail(observationHistory, compactBound),
                "Exact dated historical reply receipts were changed/lost by compaction, image attachment or final-tail budgeting.");
            const string latestFeedback = "[Latest actual execution] No action submitted by this decision; the planning request failed, not the navigation attempt.";
            var feedbackWire = JObject.Parse((string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", Flags)
                .Invoke(model, new object[] { messages, true, facts, latestFeedback, FormalResponseStage.CommittedSpatialReply }));
            var feedbackMessages = (JArray)feedbackWire["messages"];
            Check((string)feedbackMessages[feedbackMessages.Count - 2]["role"] == "system" &&
                (string)feedbackMessages[feedbackMessages.Count - 2]["content"] == latestFeedback &&
                (string)feedbackMessages[feedbackMessages.Count - 1]["content"] == CommittedSpatialReplyProtocol.BuildFinalWritingInstruction("Come to my side."),
                "Committed final instruction precedes or overwrites the newest execution evidence.");
            model.m_Backend = ChatQW.BackendType.Cloud;
            JObject cloud = Build(FormalResponseStage.CommittedSpatialReply);
            var cloudMessages = (JArray)cloud["messages"];
            Check(cloud["grammar"] == null && (int?)cloud["max_tokens"] == CommittedSpatialReplyProtocol.MaximumOutputTokens &&
                (bool?)cloud["enable_thinking"] == false && cloud["messages"].ToString().Contains(CommittedSpatialReplyProtocol.Marker) &&
                (string)cloudMessages[cloudMessages.Count - 1]["content"] == CommittedSpatialReplyProtocol.BuildFinalWritingInstruction("Come to my side."),
                "Cloud stage lost strict instructions or received a local-only grammar.");
            model.m_Backend = ChatQW.BackendType.Local;
            JObject general = Build(FormalResponseStage.General);
            Check(general["grammar"] == null && general["max_tokens"] == null && (bool?)general["enable_thinking"] == true && model.m_EnableThinking,
                "Committed protocol mutated the General writer or shared thinking setting.");
            var generalMessages = (JArray)general["messages"];
            Check((string)generalMessages[generalMessages.Count - 1]["content"] == "Come to my side." &&
                !generalMessages.ToString().Contains(CommittedSpatialReplyProtocol.FinalWritingInstruction),
                "A reply-only tail changed the General conversation's original last human turn.");

            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                // Keep the original sparse-persona fixture for an honest A/B.
                // A separate representative embodiment fixture adds capabilities,
                // never an expected sentence or a successful execution assertion.
                File.WriteAllText(Path.Combine(output, "committed-service-failure.request.json"), local.ToString(Formatting.Indented));
                string embodiedFacts = facts + "\n[Current character capability facts] " + new JObject {
                    ["embodiedSceneCharacter"] = true,
                    ["controlEntryPoints"] = new JObject { ["spokenSpatialPlanning"] = true, ["approachUser"] = true,
                        ["followUser"] = true, ["turnBody"] = true, ["orientView"] = true },
                    ["scope"] = "Installed Unity character interfaces. Availability does not prove that this decision dispatched an action, that a route succeeded, or that an unsubmitted retry will occur."
                }.ToString(Formatting.None);
                string embodied = (string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", Flags)
                    .Invoke(model, new object[] { messages, true, embodiedFacts, null, FormalResponseStage.CommittedSpatialReply });
                File.WriteAllText(Path.Combine(output, "committed-service-failure-embodied.request.json"), JObject.Parse(embodied).ToString(Formatting.Indented));
                var fixture = JObject.Parse(File.ReadAllText(Path.Combine(Application.dataPath, "Editor/Fixtures/SpatialGoalResult.multi-step.json")));
                var terminalFacts = (JObject)fixture["facts"].DeepClone();
                terminalFacts["resultReportRequirement"] = SpatialGoalResultContextPolicy.BuildReportRequirement(terminalFacts);
                string context = SpatialGoalResultContextPolicy.BuildContext("Turn off the previously observed desk lamp.", terminalFacts.ToString(Formatting.None));
                string terminal = (string)typeof(ChatQW).GetMethod("BuildSpatialGoalResultRequestJsonForMessages", Flags)
                    .Invoke(model, new object[] { null, true, context, null, null });
                Check(!string.IsNullOrWhiteSpace(terminal), "Synthetic terminal request export failed.");
                File.WriteAllText(Path.Combine(output, "terminal-off-result.request.json"), JObject.Parse(terminal).ToString(Formatting.Indented));
                LastReport["requestExports"] = new JArray("committed-service-failure.request.json", "committed-service-failure-embodied.request.json", "terminal-off-result.request.json");
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(persona); UnityEngine.Object.DestroyImmediate(languagePolicy); }
        LastReport["passed"] = true; LastReport["checks"] = checks;
        return checks;
    }
}
