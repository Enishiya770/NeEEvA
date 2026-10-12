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
                Require(tail.Contains("approach仅表示走向当前用户") && tail.Contains("approach-object") &&
                    tail.Contains("无法明确所指对象时不要默认走向用户"),
                    "Object pronouns are resolved from context before selecting the current verb; unknown referents cannot default to user approach.");
                Require(!wire.ToString().Contains("PRIVATE_"), "Decision transport does not read private provider history or formal context.");
                Require(backend == ChatQW.BackendType.Local ? (int?)wire["id_slot"] == 1 &&
                    JToken.DeepEquals(SceneObjectProtocolRegression.ExpandNativeSchema(wire["response_format"]["schema"]), JObject.Parse(emptyGoalSchema)) :
                    wire["id_slot"] == null && wire["response_format"]["schema"] == null,
                    "Current-turn separation preserves backend slot and structured-schema ownership.");
                var review = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithImage", source, Image, true));
                Require((int?)review["max_tokens"] == 240 && ((JArray)review["messages"]).Count == 2 &&
                    !review.ToString().Contains("[本轮唯一行动来源]") && review.SelectTokens("$..image_url.url").Count() == 1 &&
                    (string)review["messages"][0]["content"] == RoomTaskProtocol.ReviewContract + "\n" + RoomTaskProtocol.ReviewSchema &&
                    (backend == ChatQW.BackendType.Local ? JToken.DeepEquals(review["response_format"]["schema"], JObject.Parse(RoomTaskProtocol.ReviewSchema)) :
                        review["response_format"]["schema"] == null),
                    "Speech review retains its 240-token, two-message contract and single image.");
                CheckSweepImageOwnership(model, input);

                var allowed = (JObject)input.DeepClone();
                allowed["decisionContractFacts"] = new JObject { ["allowedObservationGoalIds"] = new JArray("goal-current-a", "goal-current-b", "goal-current-a", "", " ", 7, new string('x', 129)) };
                var terminalReviewInput=JObject.Parse(source);
                terminalReviewInput["sceneActionFacts"]=new JObject { ["resultReportRequirement"]=new JObject { ["required"]=true,["expectedReport"]="too-far" } };
                var terminalReview=JObject.Parse((string)Call(model,"BuildRoomTaskRequestJsonWithImage",terminalReviewInput.ToString(),Image,true));
                var terminalSchema=JObject.Parse(RoomTaskProtocol.BuildSpeechReviewSchemaForInput(terminalReviewInput.ToString()));
                Require(((JArray)terminalSchema["required"]).Values<string>().SequenceEqual(new[]{"verdict","reason","factScope","resultReport"})&&
                    ((JArray)terminalSchema["properties"]["factScope"]["enum"]).Values<string>().Contains("none")&&
                    ((JArray)terminalSchema["properties"]["verdict"]["enum"]).Values<string>().Contains("inconsistent"),
                    "Terminal review requires scope/result classification while retaining honest inconsistent/non-result verdicts.");
                Require((string)terminalReview["messages"][0]["content"]==RoomTaskProtocol.ReviewContract+"\n"+terminalSchema.ToString(Newtonsoft.Json.Formatting.None)&&
                    (backend==ChatQW.BackendType.Local?JToken.DeepEquals(terminalReview["response_format"]["schema"],terminalSchema):terminalReview["response_format"]["schema"]==null),
                    "Local constrained grammar and cloud system contract use the actual terminal four-field review schema.");
                var allowedWire = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithImage", allowed.ToString(), Image, false));
                string allowedSchema = RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(new[] { "goal-current-a", "goal-current-b" });
                CheckDecisionSchemaText(allowedWire, backend, allowedSchema);
                JObject actualSchema = backend == ChatQW.BackendType.Local ? SceneObjectProtocolRegression.ExpandNativeSchema(allowedWire["response_format"]["schema"]) :
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
            CheckUnifiedDeviceGoalHandoff(model, input);
            CheckIndependentRequestWire(model, input);
            CheckAutonomousRepair(model, input);

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

    private static void CheckAutonomousRepair(ChatQW model, JObject original)
    {
        const string intention = "向用户说明当前没有找到灯具，并询问位置。";
        const string oldHuman = "你至少转身的时候得往那个方向看去吧。";
        const string malformedNone = "{\"intent\":\"none\",\"origin\":\"autonomous\",\"evidence\":\"\"}";
        var input = (JObject)original.DeepClone();
        input["autonomous"] = true; input["currentUserText"] = oldHuman; input["latestUserText"] = oldHuman;
        input["autonomousIntention"] = intention; input["currentRequestOnly"] = true;
        input["latestGoalUpdate"] = new JObject { ["cancellationApplied"] = true };
        var current = CurrentData((string)Call(null, "BuildRoomDecisionCurrentTurnMessage", input.ToString()));
        Require((string)current["currentUserText"] == "" && (string)current["latestUserText"] == "" &&
            (string)current["autonomousIntention"] == intention && (bool?)current["cancellationApplied"] == false,
            "The same-turn autonomous tick cannot relabel the preceding human request or cancellation as its current authority.");
        Require(RoomTaskProtocol.IsNoActionRepairCandidate(malformedNone) &&
            RoomTaskProtocol.IsNoActionRepairCandidate("{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\",\"deviceGoal\":null}") &&
            RoomTaskProtocol.IsNoActionRepairCandidate("{\"intent\":\"none\"}") &&
            !RoomTaskProtocol.IsNoActionRepairCandidate("{\"intent\":\"turn-in-place\"}") &&
            !RoomTaskProtocol.IsNoActionRepairCandidate("{\"intent\":\"none\",\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}"),
            "An unambiguous no-action intent narrows repair even if other fields are missing/extra; the malformed object is never accepted as an action.");
        var turn = new JObject { ["intent"] = "turn-in-place", ["origin"] = "autonomous", ["evidence"] = oldHuman,
            ["viewChange"] = new JObject { ["captureId"] = "current", ["yaw"] = 180, ["pitch"] = 0 } };
        Require(!RoomTaskProtocol.TryAutonomousDecision(turn.ToString(), intention, false, out _, out var reason) &&
            reason == "invalid-autonomous-evidence-not-in-current-intention", "An old human sentence cannot become a new autonomous turn's evidence.");
        turn["evidence"] = intention;
        Require(!RoomTaskProtocol.TryAutonomousDecision(turn.ToString(), intention, true, out _, out reason) &&
            reason == "invalid-autonomous-repair-must-remain-none", "No-action repair cannot escalate to movement even with a current intention citation.");
        const string none = "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}";
        Require(RoomTaskProtocol.TryAutonomousDecision(none, intention, true, out var settled, out _) && settled.intent == "none",
            "Correcting the original no-action structure ends repair without manufacturing an action.");
        const string exploration = "向左转身30度，查看侧面的空间。";
        turn["evidence"] = "向左转身30度"; turn["viewChange"]["yaw"] = -30;
        Require(RoomTaskProtocol.TryAutonomousDecision(turn.ToString(), exploration, false, out _, out _) &&
            !RoomTaskProtocol.TryAutonomousDecision(turn.ToString(), "", false, out _, out _),
            "A genuinely current exploration intention still permits a normally bounded autonomous action; a missing intention does not.");
        input["decisionRepair"] = new JObject { ["preserveNoAction"] = true, ["rejectedDecision"] = malformedNone };
        foreach (var backend in new[] { ChatQW.BackendType.Local, ChatQW.BackendType.Cloud })
        {
            model.m_Backend = backend;
            var wire = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithImage", input.ToString(), Image, false));
            var schema = backend == ChatQW.BackendType.Local ? SceneObjectProtocolRegression.ExpandNativeSchema(wire["response_format"]["schema"]) :
                JObject.Parse(((string)wire["messages"][0]["content"]).Substring(RoomTaskProtocol.DecisionContract.Length + 1));
            Require(JToken.DeepEquals(schema["properties"]["intent"]["enum"], new JArray("none")) &&
                JToken.DeepEquals(schema["properties"]["origin"]["enum"], new JArray("none")) &&
                JToken.DeepEquals(schema["properties"]["evidence"]["enum"], new JArray("")) &&
                ((JObject)schema["properties"]).Count == 3 && (bool?)schema["additionalProperties"] == false,
                "Both backend contracts make no-action repair incapable of generating a physical action or a goal proposal.");
        }
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
        var guide = SceneObjectProtocolRegression.ExpandNativeSchema(JObject.Parse(actual.Substring(prefix.Length)));
        var expected = (JObject)schema.DeepClone(); expected.Remove("oneOf");
        var definitions = (JObject)guide.DeepClone(); definitions.Remove("intentConstraints"); definitions.Remove("directionConvention"); definitions.Remove("noActionReply");
        definitions.Remove("intentConstraintEncoding");
        Require(((string)guide["noActionReply"] ?? "").Contains("replyScope=spatial"), "The actual wire teaches no-action spatial answers.");
        Require(JToken.DeepEquals(definitions, expected) && JToken.DeepEquals(guide["properties"], schema["properties"]) &&
            JToken.DeepEquals(guide["required"], schema["required"]) && guide["oneOf"] == null &&
            JToken.DeepEquals(SceneObjectProtocolRegression.ExpandNativeSchema(wire["response_format"]["schema"]), schema) && ((JArray)schema["oneOf"]).Count > 1,
            "Local text deduplicates field definitions; all exact fields and the full native dynamic grammar survive.");
        var constraints = (JArray)guide["intentConstraints"];
        Require(constraints.Count == ((JArray)schema["oneOf"]).Count &&
            constraints.OfType<JObject>().All(x => x["required"] is JArray && x["allowedAdditionalFields"] is JArray),
            "The compact guide retains each alternative's required and allowed fields instead of losing intent-specific restrictions.");
        JObject Turn(string intent) => constraints.OfType<JObject>().Single(x =>
            !((JArray)x["required"]).Values<string>().Contains("deviceGoal") && ((JArray)x["intents"]).Values<string>().Contains(intent));
        var head = Turn("orient-view"); var body = Turn("turn-in-place");
        Require(JToken.DeepEquals(head["required"], new JArray("viewChange")) &&
            JToken.DeepEquals(body["required"], new JArray("viewChange")) &&
            !((JArray)head["allowedAdditionalFields"]).Values<string>().Contains("visualTarget") &&
            (int?)head["fieldOverrides"]["viewChange"]["properties"]["yaw"]["minimum"] == -50 &&
            (int?)head["fieldOverrides"]["viewChange"]["properties"]["yaw"]["maximum"] == 50 &&
            (int?)head["fieldOverrides"]["viewChange"]["properties"]["pitch"]["minimum"] == -25 &&
            (int?)head["fieldOverrides"]["viewChange"]["properties"]["pitch"]["maximum"] == 25 &&
            (int?)body["fieldOverrides"]["viewChange"]["properties"]["yaw"]["minimum"] == -180 &&
            (int?)body["fieldOverrides"]["viewChange"]["properties"]["yaw"]["maximum"] == 180 &&
            (int?)body["fieldOverrides"]["viewChange"]["properties"]["pitch"]["minimum"] == 0 &&
            (int?)body["fieldOverrides"]["viewChange"]["properties"]["pitch"]["maximum"] == 0,
            "Before choosing a branch the model sees head yaw ±50/pitch ±25 versus body yaw ±180/pitch 0, with no target field substitution.");
        Require(((string)guide["directionConvention"]).Contains("left negative, right positive") &&
            ((string)guide["directionConvention"]).Contains("never clamp"),
            "Intent guidance retains signed actor-relative direction and forbids silent angle correction.");
        Require(actual.Length < RoomTaskProtocol.DecisionContract.Length + schemaText.Length - 1000,
            "Local schema guide materially reduces repeated prompt text without changing the native grammar.");
    }

    private static void CheckSweepImageOwnership(ChatQW model, JObject input)
    {
        LLM.RoomObservationImage Frame(string id, string image, string metadataId = null) =>
            new LLM.RoomObservationImage(id, new JObject {
                ["captureId"] = metadataId ?? id, ["source"] = "character-scene-rgb",
                ["state"] = "ready", ["hasImage"] = true, ["capturedAtUtc"] = "fixture-historical-time"
            }.ToString(Newtonsoft.Json.Formatting.None), image);
        var history = new List<LLM.RoomObservationImage> {
            Frame("sweep-a", "data:image/png;base64,U1dFRVBfQQ=="),
            Frame("sweep-a", "data:image/png;base64,DUPLICATE_CANARY"),
            Frame("mismatch", "data:image/png;base64,INVALID_CANARY", "other-capture"),
            Frame("current-fixture-capture", "data:image/png;base64,CURRENT_DUPLICATE_CANARY"),
            Frame("remote", "https://example.invalid/UNREQUESTED_CANARY"),
            Frame("sweep-b", "data:image/png;base64,U1dFRVBfQg=="),
            Frame("sweep-c", "data:image/png;base64,U1dFRVBfQw=="),
            Frame("sweep-d", "data:image/png;base64,U1dFRVBfRA=="),
            Frame("sweep-e", "data:image/png;base64,OVER_BUDGET_CANARY")
        };
        string source = input.ToString(Newtonsoft.Json.Formatting.None);
        string serialized = (string)Call(model, "BuildRoomTaskRequestJsonWithObservationHistory", source, Image, history, false);
        var wire = JObject.Parse(serialized); var messages = (JArray)wire["messages"];
        Require(wire.SelectTokens("$..image_url.url").Select(x => (string)x).SequenceEqual(new[] {
            history[0].ImageDataUrl, history[5].ImageDataUrl, history[6].ImageDataUrl, history[7].ImageDataUrl, Image }),
            "Bounded historical captures precede the exact current picture; duplicates, mismatched metadata and remote images cannot replace it.");
        Require(messages.Count == 7 && Enumerable.Range(1, 4).All(i =>
            ((string)messages[i]["content"][1]["text"]).Contains("角色历史视野图像") &&
            ((string)messages[i]["content"][1]["text"]).Contains("不能作为当前可见性")) &&
            ((string)messages[5]["content"][1]["text"]).EndsWith(source, StringComparison.Ordinal) &&
            (string)CurrentData((string)messages[6]["content"])["currentUserText"] == (string)input["currentUserText"],
            "Each intermediate image is historical evidence; current facts and the exact current user authority remain separately attributed at the end.");
        Require(!serialized.Contains("CANARY"), "Invalid and over-budget attachments are never transmitted.");
        var describedInput = (JObject)input.DeepClone();
        var mismatchedDescription = JObject.Parse(history[5].MetadataJson);
        mismatchedDescription["capturedAtUtc"] = "DIFFERENT_DESCRIPTION_TIME";
        describedInput["turnSweep"] = new JObject { ["frames"] = new JArray(
            "malformed-description", new JObject { ["visualObservation"] = JObject.Parse(history[0].MetadataJson) },
            new JObject { ["visualObservation"] = mismatchedDescription }) };
        var compact = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithObservationHistory", describedInput.ToString(), Image, history, false));
        Require(((string)compact["messages"][1]["content"][1]["text"]).Contains("metadataSource") &&
            !((string)compact["messages"][1]["content"][1]["text"]).Contains("fixture-historical-time") &&
            ((string)compact["messages"][5]["content"][1]["text"]).Contains(history[0].CaptureId),
            "Exact full frame metadata already in this input is referenced rather than duplicated beside its image.");
        Require(((string)compact["messages"][2]["content"][1]["text"]).Contains("fixture-historical-time") &&
            !((string)compact["messages"][2]["content"][1]["text"]).Contains("metadataSource"),
            "A mismatched or malformed description cannot relabel an attachment; its own actual metadata remains transmitted.");
        describedInput["sceneSpatialMemory"] = new JObject { ["dialogueViews"] = new JArray(
            new JObject { ["captureId"] = history[5].CaptureId, ["visualObservation"] = JObject.Parse(history[5].MetadataJson) },
            new JObject { ["captureId"] = history[6].CaptureId, ["visualObservation"] = mismatchedDescription }) };
        var dialogueWire = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithObservationHistory", describedInput.ToString(), Image, history, false));
        string dialogueLabel = (string)dialogueWire["messages"][2]["content"][1]["text"];
        Require(dialogueLabel.Contains("historical-dialogue-view") && dialogueLabel.Contains("\"identityVerified\":false") &&
            !dialogueLabel.Contains("historical-turn-sweep") &&
            !((string)dialogueWire["messages"][3]["content"][1]["text"]).Contains("historical-dialogue-view"),
            "An actual earlier dialogue image has distinct unverified provenance; mismatched metadata cannot borrow that attribution.");
        var review = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithObservationHistory", source, Image, history, true));
        Require(review.SelectTokens("$..image_url.url").Select(x => (string)x).SequenceEqual(new[] { Image }) &&
            ((JArray)review["messages"]).Count == 2, "Public-speech review cannot inherit the planning sweep's old images.");
        var missingCurrent = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithObservationHistory", source, null, history, false));
        Require(!missingCurrent.SelectTokens("$..image_url.url").Any(), "Historical images do not substitute for missing current visual input.");
        var next = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithImage", source, Image, false));
        Require(next.SelectTokens("$..image_url.url").Select(x => (string)x).SequenceEqual(new[] { Image }),
            "A subsequent ordinary decision is independent of the earlier request's image list.");
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

    private static void CheckUnifiedDeviceGoalHandoff(ChatQW model, JObject original)
    {
        var input = (JObject)original.DeepClone();
        const string request = "先向右转身120度，再找到刚才看到的小灯，把它关掉。";
        input["currentUserText"] = request;
        input["latestUserText"] = request;
        var proposal = new JObject { ["operation"] = "set", ["requestQuote"] = "把它关掉",
            ["targetDescription"] = "刚才看到的小灯", ["desiredState"] = "off" };
        var parsedIntents = new JArray();
        foreach (string intent in new[] { "none", "inspect-object", "set-object-state", "search-scene", "turn-in-place" })
        {
            string quote = intent == "turn-in-place" ? "向右转身120度" : "把它关掉";
            var decision = new JObject { ["intent"] = intent, ["origin"] = "user", ["evidence"] = quote,
                ["deviceGoal"] = proposal.DeepClone() };
            if (intent == "inspect-object" || intent == "set-object-state")
                decision["visualTarget"] = new JObject { ["captureId"] = "current-fixture-capture", ["x"] = .5, ["y"] = .6,
                    ["label"] = "待核对的小灯" };
            if (intent == "set-object-state") decision["objectState"] = "off";
            if (intent == "search-scene") decision["searchTask"] = new JObject { ["mode"] = "find" };
            if (intent == "turn-in-place") decision["viewChange"] = new JObject {
                ["captureId"] = "current-fixture-capture", ["yaw"] = 120, ["pitch"] = 0 };
            Require(RoomTaskProtocol.TryDecision(decision.ToString(), request, false, out var parsed, out _) &&
                parsed.origin == "user" && parsed.evidence == quote && parsed.deviceGoal?.operation == "set" &&
                parsed.deviceGoal.targetDescription == "刚才看到的小灯" && parsed.deviceGoal.desiredState == "off",
                "A first device proposal preserves exact user authority independently of its optional initial intent: " + intent);
            if (intent == "turn-in-place")
                Require(parsed.IsViewChange && parsed.viewChange.yaw == 120 && parsed.viewChange.pitch == 0 &&
                    parsed.viewChange.captureId == "current-fixture-capture" && parsed.evidence != request,
                    "An initial compound request retains its exact signed view action, current capture and legal short quote separately from the unapproved device proposal.");
            Require(!RoomTaskProtocol.TryDecision(decision.ToString(), request, true, out _, out _) &&
                !RoomTaskProtocol.TryDecision(decision.ToString(), "只是看看小灯。", false, out _, out _),
                "The unified handoff contract does not allow an autonomous or uncited device proposal: " + intent);
            parsedIntents.Add(intent);
        }
        foreach (var backend in new[] { ChatQW.BackendType.Local, ChatQW.BackendType.Cloud })
        {
            model.m_Backend = backend;
            var wire = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithImage", input.ToString(), Image, false));
            string system = (string)wire["messages"][0]["content"];
            string latest = (string)wire["messages"][2]["content"];
            Require(system.Contains("批准后由程序统一执行寻找") && system.Contains("首步不会直接执行") &&
                latest.Contains("程序统一执行寻找") && latest.Contains("提案所附像素/检查/开关/搜索首步不会直接执行") &&
                latest.Contains("设备提案不要求像素定位") && !latest.Contains("当前恢复约束"),
                "System and final current-turn instructions agree on reviewed device handoff, without a competing pixel-recovery loop: " + backend);
            Require(latest.Contains("独立寻找/环顾用search-scene") &&
                (string)CurrentData(latest)["currentUserText"] == request &&
                wire.SelectTokens("$..image_url.url").Select(x => (string)x).SequenceEqual(new[] { Image }),
                "Unified device instructions preserve standalone search, the actual accepted request and current image ownership: " + backend);
            Require((bool?)CurrentData(latest)["userObservationBeforeDeviceTask"] == false &&
                system.Contains("首次原话若明确要求先转头或转身") && latest.Contains("首次原话若明确要求先转头或转身") &&
                latest.Contains("设备提案批准后统一执行器才执行这个前置观察") && latest.Contains("不为找设备猜转向角度"),
                "Initial proposal transport distinguishes an explicit once-only preface from an existing-goal update and requires independent approval before execution: " + backend);
        }

        // Old runner facts may still appear in serialized fixtures or quoted state.
        // They no longer create a final instruction asking RoomTask to plan another step.
        input["quietSpatialGoalStep"] = true;
        input["spatialGoal"] = new JObject { ["ownerCurrent"] = true, ["status"] = "searching",
            ["originalRequest"] = input["currentUserText"]?.DeepClone() };
        input["decisionContractFacts"] = new JObject { ["deviceGoalAlreadyReviewed"] = true };
        var rejected = new JObject { ["sameView"] = true, ["failedCaptureId"] = "failed-frame",
            ["requestedEntityId"] = null, ["initialGeometryEntityId"] = "rejected-entity",
            ["requestedImagePoint"] = new JObject { ["x"] = .5, ["y"] = .6 },
            ["targetReview"] = new JObject { ["proposedEntityId"] = null, ["reason"] = "UNBOUNDED_REVIEW_CANARY" } };
        input["spatialRunner"] = new JObject { ["rejectedTargetRetry"] = rejected };
        string tail = (string)Call(null, "BuildRoomDecisionCurrentTurnMessage", input.ToString());
        Require(CurrentData(tail)["unchangedViewRejectedAssociation"] == null && !tail.Contains("rejected-entity") &&
            !tail.Contains("UNBOUNDED_REVIEW_CANARY") &&
            ((string)CurrentData(tail)["authorizationSource"]).Contains("execution belongs to the unified runtime"),
            "Retired quiet-step failure facts cannot reintroduce device recovery instructions or raw review text into the final authority message.");
        input["userObservationBeforeDeviceTask"] = true;
        input["latestUserText"] = "你先向左转90度再继续。";
        tail = (string)Call(null, "BuildRoomDecisionCurrentTurnMessage", input.ToString());
        Require((bool?)CurrentData(tail)["userObservationBeforeDeviceTask"] == true &&
            (string)CurrentData(tail)["latestUserText"] == "你先向左转90度再继续。" &&
            tail.Contains("本次只判断latestUserText是否明确要求先转头或转身") && tail.Contains("不要根据originalRequest猜搜寻角度"),
            "An engine-approved latest-user prerequisite survives projection as one explicit view change, without reviving iterative device planning.");
        input["currentRequestOnly"] = true;
        Require((bool?)CurrentData((string)Call(null, "BuildRoomDecisionCurrentTurnMessage", input.ToString()))["userObservationBeforeDeviceTask"] == false,
            "A current-only request cannot borrow the reviewed device prerequisite exception.");
        input["currentRequestOnly"] = false;
        input["autonomous"] = true;
        var autonomous = CurrentData((string)Call(null, "BuildRoomDecisionCurrentTurnMessage", input.ToString()));
        Require(autonomous["unchangedViewRejectedAssociation"] == null && (bool?)autonomous["userObservationBeforeDeviceTask"] == false,
            "An autonomous tick cannot adopt a retired recovery projection or a user's once-only prerequisite exception.");
        input["autonomous"] = false; input["spatialRunner"] = null;
        Require(CurrentData((string)Call(null, "BuildRoomDecisionCurrentTurnMessage", input.ToString()))["unchangedViewRejectedAssociation"] == null,
            "Explicit JSON-null runner state remains serializable without reviving retired recovery instructions.");
        LastReport["deviceProposalHandoff"] = new JObject { ["acceptedInitialIntents"] = parsedIntents,
            ["independentReviewStillRequired"] = true, ["runtimeExecutionVerifiedHere"] = false,
            ["retiredRecoveryProjectionAbsent"] = true };
    }

    private static void CheckIndependentRequestWire(ChatQW model, JObject original)
    {
        var input = (JObject)original.DeepClone();
        input["currentUserText"] = "不要管那个灯了，你先左转90度。";
        input["currentRequestOnly"] = true;
        input["spatialGoal"] = null;
        input["latestGoalUpdate"] = new JObject { ["relation"] = "cancel", ["cancellationApplied"] = true };
        string schemaText = (string)Call(null, "BuildRoomDecisionSchemaForInput", input.ToString());
        var schema = JObject.Parse(schemaText);
        Require(!schema.SelectTokens("$..deviceGoal").Any() && !schema.SelectTokens("$..objectState").Any() &&
            !schema.SelectTokens("$..enum[*]").Any(item => item.Type == JTokenType.String && (string)item == "set-object-state"),
            "The independent-request grammar cannot propose, cancel or write a device goal after an unresolved or applied cancellation review.");
        Require(schema.SelectTokens("$.oneOf[*].properties.intent.enum[*]").Any(item => (string)item == "turn-in-place") &&
            schema.SelectTokens("$.oneOf[*].properties.intent.enum[*]").Any(item => (string)item == "inspect-object") &&
            !schema.SelectTokens("$.oneOf[*].properties.intent.enum").Any(item => ((JArray)item).Count == 0),
            "Removing device authority preserves independent searching and turning with valid nonempty grammar branches.");
        foreach (var backend in new[] { ChatQW.BackendType.Local, ChatQW.BackendType.Cloud })
        {
            model.m_Backend = backend;
            var wire = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithImage", input.ToString(), Image, false));
            CheckDecisionSchemaText(wire, backend, schemaText);
            string tail = (string)wire["messages"][2]["content"];
            var current = CurrentData(tail);
            Require((bool?)current["currentRequestOnly"] == true && (bool?)current["cancellationApplied"] == true &&
                (string)current["currentUserText"] == (string)input["currentUserText"] && tail.Contains("同一句中剩下的动作"),
                "Both backend wires keep the complete compound request and actual cancellation receipt beside the final planning instruction.");
        }
        ((JObject)input["latestGoalUpdate"])["cancellationApplied"] = false;
        Require((bool?)CurrentData((string)Call(null, "BuildRoomDecisionCurrentTurnMessage", input.ToString()))["cancellationApplied"] == false,
            "A raw cancel label without an applied receipt is not reported as an actual cancellation to the planner.");
    }

    private static JObject CurrentData(string text) => JObject.Parse(text.Split('\n')[1]);
    private static JObject Envelope(string content) => new JObject { ["choices"] = new JArray(new JObject {
        ["finish_reason"] = "stop", ["message"] = new JObject { ["role"] = "assistant", ["content"] = content } }) };
    private static int Generation(ChatQW model) => (int)typeof(ChatQW).GetField("m_RoomTaskRequestGeneration", Flags).GetValue(model);
    private static object Call(object model, string method, params object[] args) => typeof(ChatQW).GetMethod(method, Flags).Invoke(model, args);
    private static void Require(bool valid, string reason) { checks++; if (!valid) throw new InvalidOperationException(reason); }
}
