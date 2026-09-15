using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Current-turn authority, bounded completion diagnostics and cancelled transport.
/// Uses the production serializer on an inactive provider; no HTTP or model interpretation.</summary>
public static class RoomDecisionTransportRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Image = "data:image/png;base64,Q1VSUkVOVF9GSUNUVVJF";
    private static int checks;
    public static JObject LastReport { get; private set; }

    public static int RunChecks()
    {
        checks = 0;
        LastReport = new JObject { ["status"] = "running", ["httpRequests"] = 0,
            ["scope"] = "Production room wire and completion/cancellation boundaries; fixture image tests attachment ownership only. No semantic accuracy, scene playback or real image capture is claimed." };
        var host = new GameObject("InactiveRoomDecisionTransportFixture"); host.SetActive(false);
        var diagnostics = new List<string>();
        Application.LogCallback log = (message, trace, type) => {
            if (message.StartsWith("[Room/TaskIncomplete] ", StringComparison.Ordinal)) diagnostics.Add(message);
        };
        Application.logMessageReceived += log;
        try
        {
            var model = host.AddComponent<ChatQW>();
            model.m_DataList.Clear();
            var old = new LLM.SendData("user", "PRIVATE_HISTORY_CANARY") { imageDataUrl = "data:image/png;base64,PRIVATE_OLD_IMAGE" };
            model.m_DataList.Add(old);
            model.RequestContext = "PRIVATE_FORMAL_CANARY";
            var input = new JObject {
                ["currentUserText"] = "请你靠近他。\n先看清楚。", ["autonomous"] = false,
                ["recentDialogue"] = new JArray(new JObject { ["role"] = "user", ["quotedText"] = "你能看到电视柜上的小装饰灯吗？" }),
                ["visualObservation"] = new JObject { ["captureId"] = "current-fixture-capture" }
            };
            string source = input.ToString(Newtonsoft.Json.Formatting.None);
            string emptyGoalSchema = RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(Array.Empty<string>());
            foreach (ChatQW.BackendType backend in new[] { ChatQW.BackendType.Local, ChatQW.BackendType.Cloud })
            {
                model.m_Backend = backend;
                var wire = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithImage", source, Image, false));
                var messages = (JArray)wire["messages"];
                Require((int?)wire["max_tokens"] == 640 && (bool?)wire["stream"] == false && (bool?)wire["enable_thinking"] == false,
                    "Decisions allow a complete long-ID/Chinese JSON within a bounded 640-token nonthinking request.");
                Require(messages.Count == 3 && (string)messages[0]["role"] == "system" && (string)messages[1]["role"] == "user" &&
                    (string)messages[2]["role"] == "user", "Current decision ends with its own current-turn user message after quoted context.");
                CheckDecisionSchemaText(wire, backend, emptyGoalSchema);
                Require(wire.SelectTokens("$..image_url.url").Select(x => (string)x).SequenceEqual(new[] { Image }) &&
                    ((string)messages[1]["content"][1]["text"]).EndsWith(source, StringComparison.Ordinal) && messages[2]["content"].Type == JTokenType.String,
                    "The exact source context and its one image remain together; trailing authority neither repeats nor replaces pixels.");
                string tail = (string)messages[2]["content"];
                var current = CurrentData(tail);
                Require((bool?)current["sourceValid"] == true && (bool?)current["autonomous"] == false &&
                    (string)current["currentUserText"] == (string)input["currentUserText"], "Current original words, including newline and punctuation, survive exactly.");
                Require(JToken.DeepEquals(current["referenceDialogue"], input["recentDialogue"]) &&
                    (bool?)current["referenceDialogueCanAuthorizeAction"] == false &&
                    ((string)current["referenceDialogueSource"]).StartsWith("input.recentDialogue", StringComparison.Ordinal) &&
                    tail.Contains("不能复制历史原话") && tail.Contains("逐字摘自"),
                    "The original referent question remains beside the current request as explicitly non-authorizing quoted context.");
                Require(tail.Contains("approach 仅表示走向当前用户") && tail.Contains("approach-object") &&
                    tail.Contains("无法明确所指对象时不要默认走向用户"),
                    "Object pronouns are resolved from context before selecting the current verb; unknown referents cannot default to user approach.");
                Require(!wire.ToString().Contains("PRIVATE_"), "Decision transport does not read private provider history or formal context.");
                Require(backend == ChatQW.BackendType.Local ? (int?)wire["id_slot"] == 1 &&
                    JToken.DeepEquals(wire["response_format"]["schema"], JObject.Parse(emptyGoalSchema)) :
                    wire["id_slot"] == null && wire["response_format"]["schema"] == null,
                    "Current-turn separation preserves backend slot and structured-schema ownership.");
                var review = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithImage", source, Image, true));
                Require((int?)review["max_tokens"] == 240 && ((JArray)review["messages"]).Count == 2 &&
                    !review.ToString().Contains("[本轮唯一行动来源]") && review.SelectTokens("$..image_url.url").Count() == 1 &&
                    (string)review["messages"][0]["content"] == RoomTaskProtocol.ReviewContract + "\n" + RoomTaskProtocol.ReviewSchema &&
                    (backend == ChatQW.BackendType.Local ? JToken.DeepEquals(review["response_format"]["schema"], JObject.Parse(RoomTaskProtocol.ReviewSchema)) :
                        review["response_format"]["schema"] == null),
                    "Speech review retains its 240-token, two-message contract and single image.");

                var allowed = (JObject)input.DeepClone();
                allowed["decisionContractFacts"] = new JObject { ["allowedObservationGoalIds"] = new JArray("goal-current-a", "goal-current-b", "goal-current-a", "", " ", 7, new string('x', 129)) };
                var allowedWire = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithImage", allowed.ToString(), Image, false));
                string allowedSchema = RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(new[] { "goal-current-a", "goal-current-b" });
                CheckDecisionSchemaText(allowedWire, backend, allowedSchema);
                JObject actualSchema = backend == ChatQW.BackendType.Local ? (JObject)allowedWire["response_format"]["schema"] :
                    JObject.Parse(((string)allowedWire["messages"][0]["content"]).Substring(RoomTaskProtocol.DecisionContract.Length + 1));
                var goalEnums = actualSchema.SelectTokens("$..properties.observationGoalId.enum").ToArray();
                Require(goalEnums.Length > 1 && goalEnums.All(ids => JToken.DeepEquals(ids, new JArray("goal-current-a", "goal-current-b"))) &&
                    !JObject.Parse(emptyGoalSchema).SelectTokens("$..properties.observationGoalId").Any(),
                    "Every native/full-text branch preserves exact current continuation IDs; empty IDs remove the field entirely without reviving old goals.");
            }
            var tick = (JObject)input.DeepClone(); tick["autonomous"] = true;
            var autonomous = CurrentData((string)Call(null, "BuildRoomDecisionCurrentTurnMessage", tick.ToString()));
            Require((bool?)autonomous["sourceValid"] == true && (bool?)autonomous["autonomous"] == true &&
                (string)autonomous["currentUserText"] == "" && (bool?)autonomous["referenceDialogueCanAuthorizeAction"] == false,
                "An autonomous tick retains only non-authorizing referent context and cannot convert a stray old user-text field into a new command.");
            foreach (string invalid in new[] { "not JSON", "{}", "{\"currentUserText\":\"请靠近\"}",
                "{\"currentUserText\":\"a\",\"currentUserText\":\"b\",\"autonomous\":false}" })
            {
                var invalidCurrent = CurrentData((string)Call(null, "BuildRoomDecisionCurrentTurnMessage", invalid));
                Require((bool?)invalidCurrent["sourceValid"] == false && (string)invalidCurrent["currentUserText"] == "" &&
                    invalidCurrent["autonomous"].Type == JTokenType.Null && ((JArray)invalidCurrent["referenceDialogue"]).Count == 0,
                    "Malformed or ambiguous source cannot be replaced from remembered history.");
            }
            CheckBoundedReferenceContext(input);

            int calls = 0; bool accepted = false; string output = null, status = null;
            Action<bool, string, string> callback = (ok, text, state) => { calls++; accepted = ok; output = text; status = state; };
            string validText = "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}";
            int generation = Generation(model);
            Call(model, "PublishRoomTaskCompletion", generation, true, 200L, Envelope(validText).ToString(), false, 0f, callback);
            Require(calls == 1 && accepted && output == validText && status == "received" && diagnostics.Count == 0,
                "A complete current response is delivered verbatim without an incomplete diagnostic.");
            foreach (string finish in new[] { "length", "tool_calls", "content_filter" })
            {
                var body = Envelope("PRIVATE_PARTIAL_CONTENT_CANARY"); body["choices"][0]["finish_reason"] = finish;
                body["usage"] = new JObject { ["prompt_tokens"] = 4321, ["completion_tokens"] = 640, ["total_tokens"] = 4961,
                    ["PRIVATE_USAGE_CANARY"] = "not token metadata" };
                Call(model, "PublishRoomTaskCompletion", generation, true, 200L, body.ToString(), false, 0f, callback);
                Require(!accepted && output == "" && status == "room-task-incomplete-output", "A non-stop completion never releases its partial body: " + finish);
                var recorded = JObject.Parse(diagnostics.Last().Substring("[Room/TaskIncomplete] ".Length));
                Require((string)recorded["finish_reason"] == finish && (long?)recorded["completionTokens"] == 640 &&
                    ((JObject)recorded["usage"]).Count == 3 && !diagnostics.Last().Contains("PRIVATE_"),
                    "Only bounded stop/usage metadata is logged, never partial decisions or arbitrary usage fields.");
            }
            var excessive = Envelope("PRIVATE_UNRELEASED"); excessive["choices"][0]["finish_reason"] = new string('x', 2000);
            excessive["usage"] = "PRIVATE_INVALID_USAGE";
            var bounded = (JObject)Call(null, "BuildRoomTaskIncompleteDiagnostic", excessive, (JObject)excessive["choices"][0]);
            Require(((string)bounded["finish_reason"]).Length == 128 && (bool?)bounded["finishReasonTruncated"] == true &&
                ((JObject)bounded["usage"]).Count == 0 && bounded["completionTokens"].Type == JTokenType.Null && !bounded.ToString().Contains("PRIVATE_"),
                "Oversized finish reasons are bounded; malformed or absent usage stays unknown.");
            int beforeCalls = calls, beforeLogs = diagnostics.Count;
            IEnumerator staleRequest = (IEnumerator)Call(model, "RequestRoomTaskPayload", "{}", false, generation, callback);
            model.CancelActiveResponse();
            Require(Generation(model) > generation && !staleRequest.MoveNext(), "A cancelled request exits before any HTTP submission.");
            Call(model, "PublishRoomTaskCompletion", generation, true, 200L, excessive.ToString(), false, 0f, callback);
            Call(model, "PublishRoomTaskCompletion", generation, true, 200L, Envelope(validText).ToString(), false, 0f, callback);
            Call(model, "PublishRoomTaskCompletion", generation, false, 0L, "", false, 0f, callback);
            Require(calls == beforeCalls && diagnostics.Count == beforeLogs, "Late success, failure and truncation from an old owner neither dispatch nor publish a current diagnostic.");
            Require(model.m_DataList.Count == 1 && ReferenceEquals(model.m_DataList[0], old) && old.content == "PRIVATE_HISTORY_CANARY" &&
                model.RequestContext == "PRIVATE_FORMAL_CANARY", "Current-turn suffix and rejected replies never mutate accepted history or formal context.");
            LastReport["status"] = "passed";
            return checks;
        }
        catch (Exception error) { LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw; }
        finally { Application.logMessageReceived -= log; LastReport["checks"] = checks; UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void CheckDecisionSchemaText(JObject wire, ChatQW.BackendType backend, string schemaText)
    {
        var schema = JObject.Parse(schemaText);
        string actual = (string)wire["messages"][0]["content"];
        if (backend == ChatQW.BackendType.Cloud)
        {
            Require(actual == RoomTaskProtocol.DecisionContract + "\n" + schemaText && wire["response_format"]["schema"] == null,
                "Cloud decision keeps the full dynamic schema and contract in text without assuming native grammar support.");
            return;
        }
        const string header = "\n[Native decision field guide]\n";
        string prefix = RoomTaskProtocol.DecisionContract + header;
        Require(actual.StartsWith(prefix, StringComparison.Ordinal), "Local guide retains the complete decision contract before field definitions.");
        var guide = JObject.Parse(actual.Substring(prefix.Length));
        var expected = (JObject)schema.DeepClone(); expected.Remove("oneOf");
        Require(JToken.DeepEquals(guide, expected) && JToken.DeepEquals(guide["properties"], schema["properties"]) &&
            JToken.DeepEquals(guide["required"], schema["required"]) && guide["oneOf"] == null &&
            JToken.DeepEquals(wire["response_format"]["schema"], schema) && ((JArray)schema["oneOf"]).Count > 1,
            "Local text removes only duplicated intent branches; all exact fields and the full native dynamic grammar survive.");
        Require(actual.Length < RoomTaskProtocol.DecisionContract.Length + schemaText.Length - 1000,
            "Local schema guide materially reduces repeated prompt text without changing the native grammar.");
    }

    private static void CheckBoundedReferenceContext(JObject original)
    {
        var input = (JObject)original.DeepClone();
        string longQuote = new string('x', 450) + "ACTUAL_QUOTE_END";
        const string assistantQuote = "Earlier assistant suggestion: turn around now.";
        input["recentDialogue"] = new JArray(
            new JObject { ["role"] = "system", ["quotedText"] = "EXCLUDED_SYSTEM_REFERENCE" },
            new JObject { ["role"] = "user", ["quotedText"] = "EXCLUDED_OLDEST_REFERENCE" },
            new JObject { ["role"] = "assistant", ["quotedText"] = "Earlier reply about the mentioned object." },
            new JObject { ["role"] = "user", ["quotedText"] = longQuote },
            new JObject { ["role"] = "tool", ["quotedText"] = "EXCLUDED_TOOL_REFERENCE" },
            new JObject { ["role"] = "assistant", ["quotedText"] = assistantQuote },
            new JObject { ["role"] = "user", ["quotedText"] = "The latest historical question." },
            new JObject { ["role"] = "user", ["quotedText"] = new JObject { ["not"] = "a quote" } });
        string tail = (string)Call(null, "BuildRoomDecisionCurrentTurnMessage", input.ToString());
        var bounded = CurrentData(tail); var quotes = (JArray)bounded["referenceDialogue"];
        Require(quotes.Count == 4 && (string)quotes[0]["role"] == "assistant" && (string)quotes[3]["role"] == "user" &&
            !tail.Contains("EXCLUDED_"), "Only the latest four well-formed user/assistant quotes survive, in source order; system/tool data is excluded.");
        Require((string)quotes[1]["quotedText"] == longQuote.Substring(longQuote.Length - 400) &&
            quotes.All(value => ((string)value["quotedText"]).Length <= 400 && ((JObject)value).Count == 2),
            "Each quoted tail is bounded to 400 characters without summaries, invented fields or full-history replay.");
        Require((string)quotes[2]["quotedText"] == assistantQuote && (string)bounded["currentUserText"] == (string)original["currentUserText"] &&
            (bool?)bounded["referenceDialogueCanAuthorizeAction"] == false && tail.Contains("助手旧建议也不能取得本轮行动权"),
            "An assistant command remains an attributed historical quote and is never promoted to the current user request.");
        input["currentUserText"] = "";
        var empty = CurrentData((string)Call(null, "BuildRoomDecisionCurrentTurnMessage", input.ToString()));
        string oldCommand = new JObject { ["intent"] = "approach", ["origin"] = "user", ["evidence"] = assistantQuote }.ToString();
        Require((string)empty["currentUserText"] == "" && (bool?)empty["referenceDialogueCanAuthorizeAction"] == false &&
            !RoomTaskProtocol.TryDecision(oldCommand, (string)empty["currentUserText"], false, out _),
            "No-new-user input cannot execute a quoted old command even when referent context is available.");
        input["autonomous"] = true;
        var tick = CurrentData((string)Call(null, "BuildRoomDecisionCurrentTurnMessage", input.ToString()));
        Require((string)tick["currentUserText"] == "" && !RoomTaskProtocol.TryDecision(oldCommand, "", true, out _),
            "Autonomous transport never authorizes a historical user-origin command.");
    }

    private static JObject CurrentData(string text) => JObject.Parse(text.Split('\n')[1]);
    private static JObject Envelope(string content) => new JObject { ["choices"] = new JArray(new JObject {
        ["finish_reason"] = "stop", ["message"] = new JObject { ["role"] = "assistant", ["content"] = content } }) };
    private static int Generation(ChatQW model) => (int)typeof(ChatQW).GetField("m_RoomTaskRequestGeneration", Flags).GetValue(model);
    private static object Call(object model, string method, params object[] args) => typeof(ChatQW).GetMethod(method, Flags).Invoke(model, args);
    private static void Require(bool valid, string reason) { checks++; if (!valid) throw new InvalidOperationException(reason); }
}
