using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
#if UNITY_EDITOR
using System.Reflection;
using UnityEngine;
#endif

/// <summary>Public synthetic protocol and actual provider serialization; no model, HTTP or scene interaction.</summary>
public static class RoomTaskProtocolRegression
{
    private static int checks;
    public static int RunChecks()
    {
        checks = 0;
        GoalUpdateResponseModeCases(); DecisionSchemaCases(); VisualSchemaCases(); DecisionDiagnosticCases(); ReviewSchemaCases(); SpeechGateCases(); SpatialAnswerCases();
#if UNITY_EDITOR
        ProviderCases();
#endif
        return checks;
    }

    private static void GoalUpdateResponseModeCases()
    {
        foreach (string relation in new[] { "continue", "clarify", "unrelated", "new", "cancel" })
        foreach (string kind in new[] { "answer-only", "action", "control", "mixed", "uncertain" })
        {
            var review=new RoomTaskProtocol.DeviceGoalReview { relation=relation, responseMode="answer",
                verdict="approved", referentGoalId="original-goal", referentEntityId="" };
            string expected=kind == "uncertain" || kind == "answer-only" ? "answer" : "act";
            Check(RoomTaskProtocol.ResolveGoalUpdateResponseMode(review,new JObject { ["requestKind"]=kind }) == expected,
                "Current request and old-goal relation stay independent: "+relation+"/"+kind);
            Check(review.relation == relation && review.referentGoalId == "original-goal" &&
                review.verdict == "approved", "Response routing never grants/replaces a goal or entity.");
        }
        var legacy=new RoomTaskProtocol.DeviceGoalReview { relation="continue", responseMode="answer-then-resume" };
        Check(RoomTaskProtocol.ResolveGoalUpdateResponseMode(legacy,null) == "answer-then-resume",
            "Missing/stale current semantics cannot override the existing authorization review.");
        Check(RoomTaskProtocol.ResolveGoalUpdateResponseMode(legacy,new JObject { ["requestKind"]="mixed" }) == "answer-then-resume",
            "Explicitly reviewed answer-before-continuation ordering survives mixed normalization.");
        legacy.responseMode="answer";
        Check(RoomTaskProtocol.ResolveGoalUpdateResponseMode(legacy,new JObject { ["requestKind"]="mixed" }) == "act",
            "Questions plus action do not automatically require another answer-only round first.");
    }

    private static string Decision(string intent, string origin, string evidence) => new JObject {
        ["intent"] = intent, ["origin"] = origin, ["evidence"] = evidence
    }.ToString(Newtonsoft.Json.Formatting.None);

    private static void SpatialAnswerCases()
    {
        Check(RoomTaskProtocol.TryDecision(Decision("none", "none", ""), "Where did you see the clock?", false, out var omitted) &&
            omitted.replyScope == "spatial" && !omitted.IsSpatial, "Missing scope retains zero action but requires a guarded spatial answer.");
        var ordinary = JObject.Parse(Decision("none", "none", "")); ordinary["replyScope"]="ordinary";
        Check(RoomTaskProtocol.TryDecision(ordinary.ToString(), "How was your day?", false, out var parsed) && parsed.replyScope=="ordinary",
            "An explicit ordinary no-action decision preserves ordinary conversation.");
        ordinary["replyScope"]="spatial";
        Check(RoomTaskProtocol.TryDecision(ordinary.ToString(), "なぜそちらを向いたの？", false, out parsed) && parsed.replyScope=="spatial",
            "A spatial question carries a speech obligation without introducing movement authority.");
        ordinary["intent"]="approach"; ordinary["origin"]="user"; ordinary["evidence"]="Come here";
        Check(!RoomTaskProtocol.TryDecision(ordinary.ToString(), "Come here", false, out _), "A reply scope cannot be attached to an action.");
        var schema=JObject.Parse(RoomTaskProtocol.DecisionSchema);
        var none=((JArray)schema["oneOf"]).OfType<JObject>().Single(x=>((JArray)x["properties"]["intent"]["enum"]).Values<string>().Contains("none"));
        Check(((JArray)none["required"]).Values<string>().Contains("replyScope"), "The actual model grammar requires an explicit none reply classification.");
        foreach(string status in new[]{"found","completed","incomplete","blocked","cancelled"})
        {
            var report=new JObject { ["taskId"]="search-fixture",["generation"]=42,["ownerCurrent"]=true,["status"]=status,
                ["searchAttempted"]=true,["steps"]=0,["decisions"]=2,["foundEntityId"]="fixture-clock",["foundCaptureId"]="fresh-proof" };
            var requirement=RoomTaskProtocol.SceneSearchReportRequirement(report,42);
            Check((bool)requirement["required"]&&(bool)requirement["searchAttempted"],"Page-only search still requires its overall result: "+status);
            string expected=(string)requirement["expectedReport"];
            var review=new JObject { ["verdict"]="consistent",["reason"]="Controlled result coverage",["factScope"]="scene-action",["resultReport"]=expected };
            Check(RoomTaskProtocol.ReviewReportsSceneSearch(review.ToString(),requirement),"The actual overall search outcome may be reported: "+status);
            review["resultReport"]="not-applicable";
            Check(!RoomTaskProtocol.ReviewReportsSceneSearch(review.ToString(),requirement),"A consistent last-turn-only sentence is insufficient for the search result: "+status);
            review["resultReport"]=expected;review["factScope"]="none";
            Check(!RoomTaskProtocol.ReviewReportsSceneSearch(review.ToString(),requirement),"A search result cannot evade mandatory execution dependencies: "+status);
            report["ownerCurrent"]=false;
            Check((bool)RoomTaskProtocol.SceneSearchReportRequirement(report,42)["required"]==false,"A revoked report cannot become the current result.");
            report["ownerCurrent"]=true;
            Check((bool)RoomTaskProtocol.SceneSearchReportRequirement(report,43)["required"]==false,"A previous generation's report cannot satisfy a new question.");
        }
    }

    private static void DecisionDiagnosticCases()
    {
        Check(!RoomTaskProtocol.TryDecision("{", "跟着我", false, out var invalidJson, out string reason) &&
            invalidJson == null && reason == "invalid-decision-json", "Invalid JSON must have a distinct diagnostic and no action.");
        Check(!RoomTaskProtocol.TryDecision(Decision("follow", "user", "跟着我"), "先别跟着走", false,
            out var misquoted, out reason) && misquoted == null && reason == "evidence-not-in-current-user-text",
            "A received but misquoted follow decision must report its actual rejection, never become executable.");
        var conflict = JObject.Parse(Decision("follow", "user", "跟着我"));
        conflict["visualTarget"] = new JObject { ["captureId"]="old", ["x"]=.5, ["y"]=.5, ["label"]="用户" };
        Check(!RoomTaskProtocol.TryDecision(conflict.ToString(), "跟着我", false, out _, out reason) &&
            reason == "unexpected-action-fields", "Unexpected visual fields on user-follow cannot be silently accepted.");
        Check(RoomTaskProtocol.TryDecision(Decision("follow", "user", "跟着我"), "来吧，跟着我。", false,
            out var valid, out reason) && valid.intent == "follow" && reason == null,
            "Diagnostics must preserve a valid speech-follow decision and leave no stale error.");
    }

    private static void ExpectDecision(string json, string user, bool autonomous, bool expected)
    {
        RoomTaskProtocol.Decision decision;
        bool accepted = RoomTaskProtocol.TryDecision(json, user, autonomous, out decision);
        Check(accepted == expected && (accepted ? decision != null : decision == null),
            "Decision schema/source/evidence case did not match its expected acceptance: " + json);
        if (accepted && decision.intent == "none")
            Check(decision.origin == "none" && decision.evidence == "" && !decision.IsSpatial,
                "A non-spatial metadata variant retained action authority instead of canonical none.");
        else if (accepted)
        {
            var original = JObject.Parse(json);
            Check(decision.origin == (string)original["origin"] && decision.evidence == (string)original["evidence"] && decision.IsSpatial,
                "None compatibility changed the evidence or authority of an actual spatial decision.");
        }
    }

    private static void VisualSchemaCases()
    {
        foreach (string intent in new[] { "look-at", "approach-object" })
        {
            var value = JObject.Parse(Decision(intent, "autonomous", "想观察眼前的新目标"));
            ExpectDecision(value.ToString(), "", true, false);
            value["visualTarget"] = new JObject { ["captureId"] = "fixture-current-capture", ["x"] = .4, ["y"] = .6, ["label"] = "可见柜体" };
            ExpectDecision(value.ToString(), "", true, true);
            ExpectDecision(value.ToString(), "", false, false);
            foreach (JToken bad in new JToken[] { new JValue(-.1), new JValue(1.1), new JValue("0.5"), JValue.CreateNull(), new JValue(double.NaN) })
            {
                var copy = (JObject)value.DeepClone(); copy["visualTarget"]["x"] = bad;
                ExpectDecision(copy.ToString(), "", true, false);
            }
            foreach (string key in new[] { "captureId", "x", "y", "label" })
            {
                var copy = (JObject)value.DeepClone(); ((JObject)copy["visualTarget"]).Remove(key);
                ExpectDecision(copy.ToString(), "", true, false);
            }
            var extra = (JObject)value.DeepClone(); extra["visualTarget"]["worldPosition"] = new JArray(1, 0, 3);
            ExpectDecision(extra.ToString(), "", true, false);
            extra = (JObject)value.DeepClone(); extra["intent"] = "approach";
            ExpectDecision(extra.ToString(), "", true, false);
        }
    }

    private static void DecisionSchemaCases()
    {
        const string user = "我靠近一点了，现在这个位置能来吗？请再试试看。";
        foreach (string intent in new[] { "approach", "follow", "stop-moving", "inspect" })
        {
            ExpectDecision(Decision(intent, "user", "现在这个位置能来吗？"), user, false, true);
            ExpectDecision(Decision(intent, "user", "现在这个位置能来吗？"), user, true, false);
            ExpectDecision(Decision(intent, "autonomous", "当前交流需要陪伴"), "", true, true);
            ExpectDecision(Decision(intent, "autonomous", "当前交流需要陪伴"), user, false, false);
            ExpectDecision(Decision(intent, "none", ""), user, false, false);
            ExpectDecision(Decision(intent, "user", "刚才那个位置不能走"), user, false, false);
            ExpectDecision(Decision(intent, "user", "现在的位置能来吗？"), user, false, false);
            ExpectDecision(Decision(intent, "user", " "), user, false, false);
            ExpectDecision(Decision(intent, "autonomous", ""), "", true, false);
        }
        ExpectDecision(Decision("none", "none", ""), user, false, true);
        ExpectDecision(Decision("none", "none", ""), "", true, true);
        ExpectDecision(Decision("none", "user", "上一次原话才有的内容"), user, false, false);
        ExpectDecision(Decision("none", "none", "irrelevant"), user, false, false);
        const string nonSpatialUser = "请站在原地挥挥手，不要走动。";
        ExpectDecision(Decision("none", "user", nonSpatialUser), nonSpatialUser, false, true);
        ExpectDecision(Decision("none", "user", "站在原地挥挥手"), nonSpatialUser, false, true);
        ExpectDecision(Decision("none", "autonomous", "这轮安静陪伴，不选择空间动作"), "", true, true);
        ExpectDecision(Decision("none", "user", new string('字', 160)), new string('字', 160), false, true);
        ExpectDecision(Decision("none", "user", "不要走动"), nonSpatialUser, true, false);
        ExpectDecision(Decision("none", "autonomous", "这轮安静陪伴"), nonSpatialUser, false, false);
        ExpectDecision(Decision("none", "user", "原地挥手"), nonSpatialUser, false, false);
        ExpectDecision(Decision("none", "user", "Keep still"), "keep still", false, false);
        ExpectDecision(Decision("none", "user", "不要走动"), null, false, false);
        foreach (string emptyEvidence in new[] { "", " \n" })
        {
            ExpectDecision(Decision("none", "user", emptyEvidence), nonSpatialUser, false, false);
            ExpectDecision(Decision("none", "autonomous", emptyEvidence), "", true, false);
        }
        ExpectDecision(Decision("none", "none", " "), "", true, false);
        ExpectDecision(Decision("none", "user", new string('字', 161)), new string('字', 161), false, false);
        ExpectDecision(Decision("none", "autonomous", new string('字', 161)), "", true, false);
        var extraNone = JObject.Parse(Decision("none", "user", "不要走动")); extraNone["motion"] = "approach";
        ExpectDecision(extraNone.ToString(), nonSpatialUser, false, false);
        ExpectDecision(Decision("approach", "user", "Come"), "come here", false, false);
        ExpectDecision(Decision("approach", "user", "来て"), "こっちに来て", false, true);
        ExpectDecision(Decision("approach", "user", new string('来', 160)), new string('来', 160), false, true);
        ExpectDecision(Decision("approach", "user", new string('来', 161)), new string('来', 161), false, false);
        foreach (string intent in new[] { "Approach", "walk-back", "bow", "generate", "", " approach" })
            ExpectDecision(Decision(intent, "user", "再试试看"), user, false, false);
        foreach (string origin in new[] { "User", "system", "assistant", " user" })
            ExpectDecision(Decision("approach", origin, "再试试看"), user, false, false);

        string valid = Decision("approach", "user", "再试试看");
        foreach (string key in new[] { "intent", "origin", "evidence" })
        {
            var missing = JObject.Parse(valid); missing.Remove(key);
            ExpectDecision(missing.ToString(), user, false, false);
            foreach (JToken badType in new JToken[] { JValue.CreateNull(), new JValue(1), new JValue(true), new JObject(), new JArray() })
            {
                var typed = JObject.Parse(valid); typed[key] = badType;
                ExpectDecision(typed.ToString(), user, false, false);
            }
        }
        foreach (string key in new[] { "position", "motion", "reason", "speech", "after_action" })
        {
            var extra = JObject.Parse(valid); extra[key] = "unowned";
            ExpectDecision(extra.ToString(), user, false, false);
        }
        foreach (string malformed in new[] { null, "", "[]", "null", "```json\n" + valid + "\n```", valid + valid,
            "{\"intent\":\"none\",\"intent\":\"approach\",\"origin\":\"user\",\"evidence\":\"再试试看\"}",
            "{\"intent\":\"approach\",\"origin\":\"autonomous\",\"origin\":\"user\",\"evidence\":\"再试试看\"}",
            "{\"intent\":\"approach\",\"origin\":\"user\",\"evidence\":\"旧话\",\"evidence\":\"再试试看\"}" })
            ExpectDecision(malformed, user, false, false);
    }

    private static void ReviewSchemaCases()
    {
        const string consistent = "{\"verdict\":\"consistent\",\"reason\":\"台词仅表达正在尝试。\"}";
        Check(RoomTaskProtocol.ReviewAllows(consistent), "A complete consistent review was rejected.");
        foreach (string verdict in new[] { "inconsistent", "uncertain", "Consistent", "true", "", " consistent" })
            Check(!RoomTaskProtocol.ReviewAllows(new JObject { ["verdict"] = verdict, ["reason"] = "检查" }.ToString()),
                "A non-consistent review released speech.");
        foreach (string key in new[] { "verdict", "reason" })
        {
            var missing = JObject.Parse(consistent); missing.Remove(key);
            Check(!RoomTaskProtocol.ReviewAllows(missing.ToString()), "Missing review key was accepted.");
            foreach (JToken type in new JToken[] { JValue.CreateNull(), new JValue(true), new JArray(), new JObject() })
            {
                var invalid = JObject.Parse(consistent); invalid[key] = type;
                Check(!RoomTaskProtocol.ReviewAllows(invalid.ToString()), "Wrong review type was accepted.");
            }
        }
        var longReason = JObject.Parse(consistent); longReason["reason"] = new string('理', 161);
        Check(!RoomTaskProtocol.ReviewAllows(longReason.ToString()), "Oversized review reason was accepted.");
        foreach (string raw in new[] { "[]", "null", "", null, consistent + consistent,
            "{\"verdict\":\"consistent\",\"reason\":\"ok\",\"speech\":\"已经到了\"}",
            "{\"verdict\":\"inconsistent\",\"verdict\":\"consistent\",\"reason\":\"ok\"}",
            "{\"verdict\":\"consistent\",\"reason\":\"bad\",\"reason\":\"ok\"}" })
            Check(!RoomTaskProtocol.ReviewAllows(raw), "Malformed/duplicate/multiple review result released speech.");
    }

    private static void SpeechGateCases()
    {
        Check(RoomTaskProtocol.IsPlainSpeech("我正在尝试走近。") && RoomTaskProtocol.IsPlainSpeech(new string('字', 1600)),
            "Ordinary reviewed speech was not representable.");
        foreach (string raw in new[] { "", null, " \n", "<silent/>隐私", "<thought>隐私</thought>",
            "<motion name=\"approach\"/>", new string('字', 1601) })
            Check(!RoomTaskProtocol.IsPlainSpeech(raw), "Non-prose output was treated as reviewed plain speech.");
        var parts = new List<SpeechText>(); var singing = new RoleOutputChannels(parts.Add);
        singing.Push("<speech mode=\"after_action\"/><lang code=\"ja\"/>確認してから歌うね。<sing refs=\"public\"/>"); singing.Finish();
        Check(RoomTaskProtocol.ReviewAllows("{\"verdict\":\"consistent\",\"reason\":\"一般返答\"}") &&
            !singing.HasInvalidSpeechPhase && singing.SpeechDependency == SpeechActionDependency.AfterAction,
            "Room review changed the independent singing-validation requirement.");
        foreach (SpeechText part in parts)
            Check(part.ActionDependency == SpeechActionDependency.AfterAction, "Reviewed singing prose became independent speech.");
        var emptyPromise = RoleOutputChannels.Parse("<speech mode=\"after_action\"/><lang code=\"ja\"/>そばに来たよ。<motion name=\"approach\"/>");
        Check(emptyPromise.HasInvalidSpeechPhase && !emptyPromise.ToExecutableText().Contains("来たよ"),
            "Room-only after_action was released merely because a review may say consistent.");
    }

#if UNITY_EDITOR
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static object Call(object model, string name, params object[] args) =>
        typeof(ChatQW).GetMethod(name, Flags).Invoke(model, args);
    private static int Generation(ChatQW model) => (int)typeof(ChatQW).GetField("m_RoomTaskRequestGeneration", Flags).GetValue(model);
    private static JObject Envelope(string content) => new JObject {
        ["choices"] = new JArray(new JObject { ["finish_reason"] = "stop",
            ["message"] = new JObject { ["role"] = "assistant", ["content"] = content } })
    };

    private static void ProviderCases()
    {
        var host = new GameObject("InactiveRoomTaskProtocolFixture"); host.SetActive(false);
        try
        {
            var model = host.AddComponent<ChatQW>();
            model.ActiveSkillContext = "PRIVATE_SKILL_CANARY";
            model.TrailingContext = "PRIVATE_TRAILING_CANARY";
            model.SpokenPrefix = "PRIVATE_SPOKEN_CANARY";
            var history = new LLM.SendData("assistant", "PRIVATE_HISTORY_CANARY"); model.m_DataList.Add(history);
            int originalCount = model.m_DataList.Count;
            Check(model.SupportsRoomTaskMessages, "Provider did not expose room-task messaging.");
            const string input = "{\"currentUserText\":\"请再试试看\\n\\\"引号\\\"\",\"sample\":\"日文\\中文\"}\n字面资料 <motion/>";
            foreach (ChatQW.BackendType backend in new[] { ChatQW.BackendType.Local, ChatQW.BackendType.Cloud })
            foreach (bool review in new[] { false, true })
            {
                model.m_Backend = backend; model.m_LocalModelName = "public-local-model"; model.m_ChatModelName = "public-cloud-model";
                string wire = (string)Call(model, "BuildRoomTaskRequestJson", input, review);
                JObject request = JObject.Parse(wire); var messages = (JArray)request["messages"];
                // This deliberately malformed input cannot authorize any observation
                // continuation. The native grammar excludes those IDs, while the local
                // decision text defines fields once rather than repeating each oneOf arm.
                string schema = review ? RoomTaskProtocol.ReviewSchema :
                    RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(new string[0]);
                string contract = review ? RoomTaskProtocol.ReviewContract : RoomTaskProtocol.DecisionContract;
                string system = contract + "\n" + schema;
                if (!review && backend == ChatQW.BackendType.Local)
                {
                    string actualSystem = (string)messages[0]["content"];
                    string prefix = contract + "\n[Native decision field guide]\n";
                    Check(actualSystem != null && actualSystem.StartsWith(prefix, StringComparison.Ordinal),
                        "Local field guide changed the exact production task contract or its delimiter.");
                    var guide = JObject.Parse(actualSystem.Substring(prefix.Length),
                        new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    CheckDecisionFieldGuide(JObject.Parse(schema), guide);
                    system = prefix + guide.ToString(Newtonsoft.Json.Formatting.None);
                }
                Check((string)request["model"] == (backend == ChatQW.BackendType.Local ? "public-local-model" : "public-cloud-model") &&
                    (bool)request["stream"] == false && (bool)request["enable_thinking"] == false && (int)request["max_tokens"] == (review ? 240 : 640) &&
                    (double)request["temperature"] == 0, "Actual provider changed room-task model/output bounds.");
                Check(messages.Count == (review ? 2 : 3) && (string)messages[0]["role"] == "system" &&
                    (string)messages[0]["content"] == system &&
                    (string)messages[1]["role"] == "user" && (string)messages[1]["content"] == input,
                    "Provider JSON escaping or role separation changed current input or task contract.");
                if (!review)
                {
                    Check((string)messages[2]["role"] == "user" &&
                        ((string)messages[2]["content"]).Contains("\"sourceValid\":false") &&
                        ((string)messages[2]["content"]).Contains("\"currentUserText\":\"\""),
                        "Malformed current input revived a historical command in the final authority message.");
                }
                Check(!wire.Contains("PRIVATE_") && !wire.Contains("本次发声输出协议"),
                    "Auxiliary planning/review received private history or spoken-output instructions.");
                Check((string)request["response_format"]["type"] == "json_object", "Provider lost structured JSON mode.");
                if (backend == ChatQW.BackendType.Local)
                    Check((int)request["id_slot"] == 1 && JToken.DeepEquals(request["response_format"]["schema"], JObject.Parse(schema)) &&
                        (bool)request["chat_template_kwargs"]["enable_thinking"] == false, "Local task used main slot or omitted schema/thinking control.");
                else
                    Check(request["id_slot"] == null && request["chat_template_kwargs"] == null && request["response_format"]["schema"] == null,
                        "Cloud request received local-only schema/slot fields.");
            }

            string validBody = Envelope(Decision("none", "none", "")).ToString();
            object[] validRead = { validBody, null, null };
            Check((bool)Call(null, "TryReadRoomTaskCompletion", validRead) && (string)validRead[2] == "received" &&
                (string)validRead[1] == Decision("none", "none", ""), "Provider rejected a complete assistant JSON result.");
            var malformedBodies = new List<string> { "", "[]", "null", "{", "{\"choices\":[]}", "{\"choices\":{}}", validBody + validBody,
                "{\"choices\":[],\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"x\"}}]}",
                "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"a\",\"content\":\"b\"}}]}" };
            foreach (string reason in new[] { "length", "tool_calls", "content_filter", "", "Stop" })
            {
                var body = Envelope("{}"); body["choices"][0]["finish_reason"] = reason; malformedBodies.Add(body.ToString());
            }
            var multiple = Envelope("{}"); ((JArray)multiple["choices"]).Add(((JArray)multiple["choices"])[0].DeepClone()); malformedBodies.Add(multiple.ToString());
            foreach (JToken content in new JToken[] { JValue.CreateNull(), new JValue(""), new JValue(" \n"), new JArray(), new JValue(true) })
            {
                var body = Envelope("{}"); body["choices"][0]["message"]["content"] = content; malformedBodies.Add(body.ToString());
            }
            foreach (string role in new[] { "user", "system", "tool", "" })
            {
                var body = Envelope("{}"); body["choices"][0]["message"]["role"] = role; malformedBodies.Add(body.ToString());
            }
            foreach (string extra in new[] { "tool_calls", "function_call", "refusal" })
            {
                var body = Envelope("{}"); body["choices"][0]["message"][extra] = "unexpected"; malformedBodies.Add(body.ToString());
            }
            foreach (string body in malformedBodies)
            {
                object[] parsed = { body, null, null };
                Check(!(bool)Call(null, "TryReadRoomTaskCompletion", parsed) && (string)parsed[1] == "" &&
                    (string)parsed[2] != "received", "Partial, ambiguous, wrong-role or malformed completion was accepted.");
            }

            int callbacks = 0; bool accepted = false; string received = null;
            Action<bool, string, string> callback = (ok, output, status) => { callbacks++; accepted = ok; received = output; };
            int generation = Generation(model);
            Call(model, "PublishRoomTaskCompletion", generation, true, 200L, validBody, false, 0f, callback);
            Check(callbacks == 1 && accepted && !string.IsNullOrEmpty(received), "Current completed decision was not delivered.");
            model.CancelActiveResponse();
            Check(Generation(model) > generation, "Cancelling a formal response did not invalidate room-task requests.");
            Call(model, "PublishRoomTaskCompletion", generation, true, 200L, validBody, false, 0f, callback);
            Call(model, "PublishRoomTaskCompletion", generation, false, 0L, "", true, 0f, callback);
            Check(callbacks == 1, "Cancelled task's late completion or failure called its recipient.");
            Call(model, "PublishRoomTaskCompletion", Generation(model), false, 200L, validBody, false, 0f, callback);
            Check(callbacks == 2 && !accepted && received == "", "Transport failure released a plausible JSON body.");
            var truncated = Envelope("{}"); truncated["choices"][0]["finish_reason"] = "length";
            Call(model, "PublishRoomTaskCompletion", Generation(model), true, 200L, truncated.ToString(), true, 0f, callback);
            Check(callbacks == 3 && !accepted && received == "", "Length-truncated review released its text.");
            Check(model.m_DataList.Count == originalCount && ReferenceEquals(model.m_DataList[originalCount - 1], history),
                "Auxiliary request serialization/completion/cancellation wrote accepted chat history.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void CheckDecisionFieldGuide(JObject schema, JObject guide)
    {
        Check(guide.Property("oneOf") == null && JToken.DeepEquals(guide["properties"], schema["properties"]),
            "Local field guide omits repeated oneOf arms while retaining every exact field definition.");
        var expectedBase = (JObject)schema.DeepClone(); expectedBase.Remove("oneOf");
        var actualBase = (JObject)guide.DeepClone(); actualBase.Remove("intentConstraints"); actualBase.Remove("directionConvention"); actualBase.Remove("noActionReply");
        Check(((string)guide["noActionReply"] ?? "").Contains("replyScope=spatial"), "The guide teaches the no-action spatial answer classification.");
        Check(JToken.DeepEquals(actualBase, expectedBase), "Field-guide compaction changes no other schema root constraints or fields.");
        var branches = (JArray)schema["oneOf"]; var constraints = guide["intentConstraints"] as JArray;
        Check(branches != null && constraints != null && constraints.Count == branches.Count,
            "Every original intent/target alternative retains its own field constraint record.");
        var common = new[] { "intent", "origin", "evidence" };
        for (int index = 0; index < branches.Count; index++)
        {
            var branch = (JObject)branches[index]; var properties = (JObject)branch["properties"];
            var constraint = constraints[index] as JObject;
            Check(constraint != null && constraint.Properties().All(item =>
                new[] { "intents", "required", "allowedAdditionalFields", "fieldOverrides" }.Contains(item.Name)) &&
                JToken.DeepEquals(constraint["intents"], properties["intent"]["enum"]),
                "Compacted intent constraint preserves the exact branch intent set without unrelated fields.");
            var required = constraint["required"] as JArray; var allowed = constraint["allowedAdditionalFields"] as JArray;
            Check(required != null && allowed != null &&
                required.Values<string>().OrderBy(value => value).SequenceEqual(((JArray)branch["required"]).Values<string>()
                    .Where(value => !common.Contains(value)).OrderBy(value => value)) &&
                allowed.Values<string>().OrderBy(value => value).SequenceEqual(properties.Properties().Select(item => item.Name)
                    .Where(value => !common.Contains(value)).OrderBy(value => value)),
                "Compacted required and allowed fields exactly retain the branch, including target alternatives and search mode.");
            var overrides = constraint["fieldOverrides"] as JObject;
            int differingFields = 0;
            foreach (var property in properties.Properties().Where(item => !common.Contains(item.Name)))
            {
                bool differs = !JToken.DeepEquals(property.Value, schema["properties"][property.Name]);
                if (differs) differingFields++;
                Check(differs ? overrides != null && JToken.DeepEquals(overrides[property.Name], property.Value) : overrides?[property.Name] == null,
                    "Branch-specific bounds and enums survive compaction exactly: " + property.Name);
            }
            Check(differingFields == 0 ? constraint.Property("fieldOverrides") == null : overrides != null && overrides.Count == differingFields,
                "Only necessary branch field overrides are emitted; no added permissions or discarded bounds.");
        }
        const string directions = "Actor-relative yaw: left negative, right positive. Pitch: up positive, down negative. orient-view is head/gaze yaw [-50,50], pitch [-25,25]; turn-in-place is body yaw [-180,180], pitch 0. A larger required yaw needs a body-turn decision; never clamp, wrap, reverse, or silently replace the requested angle.";
        Check((string)guide["directionConvention"] == directions,
            "Field guide preserves signed actor-relative axes, separate head/body limits and the prohibition on silently changing a requested angle.");
    }
#endif

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
