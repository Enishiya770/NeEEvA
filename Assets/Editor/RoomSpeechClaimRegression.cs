using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class RoomSpeechClaimRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static JObject LastReport { get; private set; }
    private static object Call(object target, string method, params object[] args) => typeof(ChatQW).GetMethod(method, Flags).Invoke(target, args);
    private static void Check(bool condition, string message) { ++checks; if (!condition) throw new InvalidOperationException(message); }
    private static JObject Claim(string state, string quote, string language = "ja") => new JObject {
        ["pendingSameResultQuote"] = "", ["targetQuote"] = "", ["otherClaims"] = new JArray(), ["resultReport"] = state, ["evidence"] = quote, ["actualLanguage"] = language };
    private static JObject Input(string speech, string state, string domain = "movement", string language = "ja")
    {
        var room = new JObject { ["execution"] = new JObject { ["actionId"] = "owned", ["targetKind"] = "user", ["phase"] = state,
                ["reason"] = state == "paused" ? "Route validation failed; movement paused." : "Controlled current execution phase." },
            ["currentFacts"] = new JObject { ["currentlyNearAndFacingUser"] = state == "arrived" } };
        var requirement = new JObject { ["required"] = true, ["expectedReport"] = state, ["actionId"] = "owned" };
        var input = new JObject { ["publicSpeech"] = speech, ["publicSpeechLanguage"] = language, ["configuredDialogueLanguage"] = "Japanese",
            ["requestedUserText"] = domain == "movement" ? "来我身边。" : state == "on" ? "把那盏灯打开。" : "把那盏灯关掉。",
            ["intendedAction"] = domain == "movement" ? "approach" : "set-object-state", ["dispatch"] = "performed",
            ["actionId"] = "owned", ["declaredMotion"] = "none", ["currentRoomFacts"] = room,
            ["currentRequestSemantics"] = new JObject { ["requestKind"] = "action" },
            ["decisionFailure"] = null, ["sceneActionFacts"] = null, ["spatialReplyFacts"] = null,
            ["sceneSearchReportRequirement"] = null, ["deviceGoalFacts"] = null, ["turnSweep"] = null,
            ["movementReportRequirement"] = null };
        if (domain == "movement" && (state == "waiting" || state == "recovering" || state == "paused"))
        {
            input["intendedAction"] = "follow"; input["requestedUserText"] = "请继续跟着我。";
            room["execution"]["phase"] = "follow-" + state;
            room["currentFacts"]["followActive"] = true;
            if (state == "recovering") room["execution"]["reason"] = "Walking interrupted; follow remains enabled, recovery in progress.";
        }
        if (domain == "device") input["currentRoomFacts"] = new JObject { ["connected"] = true, ["bodyReserved"] = false };
        if (domain == "movement") input["movementReportRequirement"] = requirement;
        else input["sceneActionFacts"] = new JObject {
            ["resultReportRequirement"] = requirement, ["goal"] = new JObject {
                ["status"] = "satisfied", ["ownerCurrent"] = true, ["desiredState"] = state,
                ["targetDescription"] = "电视柜上的桌面小灯 / desk lamp", ["originalRequest"] = input["requestedUserText"].DeepClone(),
                ["boundEntityId"] = "fixture-lamp", ["completionEvidenceValid"] = true,
                ["completionEvidence"] = new JObject { ["actionId"] = "owned", ["entityId"] = "fixture-lamp", ["desiredState"] = state,
                    ["resultCaptureId"] = "fixture-fresh-capture", ["historicalOnly"] = false,
                    ["actual"] = new JObject { ["entityId"] = "fixture-lamp", ["registered"] = true, ["on"] = state == "on", ["emissionState"] = state } } },
            ["lastAction"] = new JObject { ["actionId"] = "owned", ["intent"] = "set-object-state", ["phase"] = "performed",
                ["entityId"] = "fixture-lamp", ["requestedState"] = state, ["stateChanged"] = true,
                ["resultCaptureId"] = "fixture-fresh-capture" },
            ["scope"] = "Controlled engine fixture: same target independently identified, requested runtime Light/emission change performed and read back, same entity in a fresh post-action image. No physical switch press or scene-wide darkness claim." };
        return input;
    }

    public static IEnumerator RunChecks(string output)
    {
        checks = 0; string directory = Path.Combine(output, "speech-claim"); Directory.CreateDirectory(directory);
        var directionConflict = new JObject { ["verdict"] = "inconsistent", ["reason"] = "Direction contradicts approach.",
            ["targetConsistency"] = "contradicted", ["conflict"] = "claim-state", ["conflictQuote"] = "walking away" };
        Check(RoomTaskProtocol.TryReviewScope(directionConflict.ToString(), out _), "Concrete direction denial remains eligible for repair despite overlapping conflict categories.");
        directionConflict["verdict"] = "consistent"; directionConflict["conflict"] = "none"; directionConflict["conflictQuote"] = "";
        Check(!RoomTaskProtocol.TryReviewScope(directionConflict.ToString(), out _), "Contradicted target can never be approved.");
        var host = new GameObject("SpeechClaimLoopback"); host.SetActive(false);
        var model = host.AddComponent<ChatQW>(); model.enabled = false;
        model.m_Backend = ChatQW.BackendType.Local; model.m_PrewarmPrefixOnStart = false; model.m_LogRequestStats = false;
        model.m_DataList.Add(new LLM.SendData("system", "Private role canary"));
        model.RequestContext = "Private full-context canary";
        using (var server = new Server())
        try {
            model.m_LocalUrl = server.Url; host.SetActive(true); typeof(LLM).GetField("url", Flags).SetValue(model, server.Url);
            var source = Input("嗯，正在过来。别急，马上就到了哦。", "arrived", language: "zh");
            var probe = JObject.Parse((string)Call(model, "BuildSpeechClaimRequestJson", source.ToString()));
            var words = JObject.Parse((string)probe["messages"][1]["content"]);
            Check(words.Count == 2 && (string)words["publicSpeech"] == (string)source["publicSpeech"] &&
                !probe.ToString().Contains("canary") && !probe.ToString().Contains("owned"), "Words-only payload excludes all histories, state and labels.");
            var counterfactual = (JObject)source.DeepClone(); counterfactual["movementReportRequirement"]["expectedReport"] = "moving";
            counterfactual["currentRoomFacts"] = new JObject { ["state"] = "opposite" }; counterfactual["publicSpeechLanguage"] = "ja";
            Check((string)Call(model, "BuildSpeechClaimRequestJson", source.ToString()) ==
                (string)Call(model, "BuildSpeechClaimRequestJson", counterfactual.ToString()), "Changing the execution answer key or tag cannot change the classifier request.");
            Check(RoomSpeechClaimProtocol.TryRead(Claim("moving", "正在过来", "zh").ToString(), (string)source["publicSpeech"], out _) &&
                !RoomSpeechClaimProtocol.TryRead(Claim("arrived", "已经到达", "zh").ToString(), (string)source["publicSpeech"], out _), "Fabricated evidence quote is rejected.");
            Check(!RoomSpeechClaimProtocol.TryRead(Claim("arrived", "").ToString(), "soon", out _) &&
                !RoomSpeechClaimProtocol.TryRead("{\"pendingSameResultQuote\":\"\",\"targetQuote\":\"\",\"otherClaims\":[],\"resultReport\":\"moving\",\"resultReport\":\"arrived\",\"evidence\":\"soon\",\"actualLanguage\":\"en\"}", "soon", out _), "Empty positive evidence and duplicate keys fail closed.");
            bool done = false, ok = false; string result = null;
            var first = server.Serve(Claim("moving", "正在过来", "zh").ToString());
            model.PostRoomTaskMessage(source.ToString(), true, (a,b,c) => { done = true; ok = a; result = b; });
            yield return Until(() => done, "mismatch classification");
            Check(ok && !RoomTaskProtocol.ReviewAllows(result) && (string)JObject.Parse(result)["resultReport"] == "moving" && server.Count == 1,
                "Actual moving words cannot become arrived; mismatch avoids a second fact request.");
            source = Input("あなたのそばに着いたわ。待ってくれてありがとう。", "arrived"); done = false;
            var socialClaim = Claim("arrived", "そばに着いた"); socialClaim["otherClaims"] = new JArray("待ってくれてありがとう");
            first = server.Serve(socialClaim.ToString());
            model.PostRoomTaskMessage(source.ToString(), true, (a,b,c) => { done = true; ok = a; result = b; });
            yield return Until(() => first.Finished.IsCompleted, "first phase delivered");
            var full = server.Serve(new JObject { ["verdict"] = "consistent", ["reason"] = "Current arrival reported.", ["factScope"] = "scene-action",
                ["resultReport"] = "moving", ["actualLanguage"] = "zh", ["targetComparison"] = "The destination is the current user.", ["targetConsistency"] = "matched", ["otherClaimsComparison"] = "The additional quote is conventional thanks, not a factual event assertion.", ["otherClaimsSupport"] = "none", ["conflict"] = "none", ["conflictQuote"] = "" }.ToString());
            yield return Until(() => done, "full review");
            Check(ok && RoomTaskProtocol.ReviewAllows(result) && (string)JObject.Parse(result)["resultReport"] == "arrived" &&
                (string)JObject.Parse(result)["actualLanguage"] == "ja", "Factual response cannot overwrite words-only state or language, even without grammar enforcement.");
            Check(full.Request.Contains("independentSpeechClaim"), "Full review receives the separately grounded immutable classification.");
            var fullWire = JObject.Parse(full.Request);
            var fullSchema = (JObject)fullWire["response_format"]["schema"];
            Check(((JArray)fullSchema["required"]).Values<string>().Contains("targetComparison") &&
                ((JArray)fullSchema["required"]).Values<string>().Contains("otherClaimsComparison") &&
                (int)fullWire["max_tokens"] == 480 && ((JArray)fullWire["messages"]).Count == 3,
                "Explicit production nulls keep the complete independent schema, token reserve and target comparison contract.");
            done = false; var held = server.Serve(Claim("arrived", "そばに着いた").ToString(), true);
            model.PostRoomTaskMessage(source.ToString(), true, (a,b,c) => done = true);
            yield return Until(() => held.Received.Task.IsCompleted, "held classification");
            model.CancelRoomTaskPlanningMessage(); held.Release.TrySetResult(true);
            yield return Until(() => held.Finished.IsCompleted, "cancelled classification drained");
            for (int i=0;i<3;i++) yield return null;
            Check(!done && server.Count == 4 && model.m_DataList.Count == 1, "Cancelled probe has no callback, follow-up fact request, history or action authority.");
            BudgetChecks(model, directory);
            bool budgetReturned = false; string budgetResult = null;
            model.PostWorkReviewMsg(new string('密',65000), answer => { budgetReturned = true; budgetResult = answer; });
            yield return Until(() => budgetReturned, "Oversized work review declined before HTTP");
            Check(budgetResult == "" && server.Count == 4, "Actual private request entry rejects protected overflow without any HTTP or fabricated progress decision.");
            budgetReturned = false;
            model.PostAutonomyIntentMsg(new string('密',65000), null, answer => { budgetReturned = true; budgetResult = answer; });
            yield return Until(() => budgetReturned, "Ordinary autonomy budget rejection");
            Check(budgetResult == "" && server.Count == 4, "Ordinary autonomy entry rejects overflow without HTTP.");
            string failure = null;
            model.PostSceneSearchDecisionMessage(new JObject { ["protectedConstraint"] = new string('密',65000) }.ToString(),
                "data:image/png;base64,AA==", (a,b,c) => failure = c);
            Check(failure == "scene-search-context-budget" && server.Count == 4, "Search overflow is a diagnostic failure, not a fabricated search choice.");
            foreach (string field in new[] { "sceneActionFacts", "movementReportRequirement", "sceneSearchReportRequirement" })
            {
                var malformed = (JObject)source.DeepClone(); malformed[field] = new JArray(); failure = null;
                model.PostRoomTaskMessage(malformed.ToString(), true, (a,b,c) => failure = c);
                Check(failure == "speech-review-input-invalid" && server.Count == 4, "Wrong typed optional facts fail before HTTP, never silently downgrade: " + field);
            }
            AuxiliaryBudgetChecks(model, directory);
            PrivateDecisionResult typed = null;
            model.PostWorkReviewResult(new string('密', 65000), null, result => typed = result);
            yield return Until(() => typed != null, "typed work budget rejection");
            Check(!typed.Succeeded && typed.Status == "context-budget" && !typed.HttpRequestSent && server.Count == 4,
                "Actual producer separates a local budget rejection from model/schema failure; no HTTP.");
            typed = null;
            model.m_DataList.Clear(); model.m_DataList.Add(new LLM.SendData("system", "Private test persona."));
            model.ActiveSkillContext = ""; model.TrailingContext = "";
            var invalidProgress = server.Serve("{}");
            model.PostWorkReviewResult("[内部事项进度判定；不生成角色台词，不调用工具]\n核对实际结果。", null, result => typed = result);
            yield return Until(() => typed != null, "typed invalid work schema");
            Check(!typed.Succeeded && typed.Status == "invalid_schema" && typed.HttpRequestSent && server.Count == 5,
                "Real HTTP schema failure retains its cause separately from local budget rejection.");
            const string contradictory = "I've arrived beside you. Wait, I can reach you shortly.";
            var temporal = Claim("arrived", "I've arrived beside you.", "en");
            temporal["pendingSameResultQuote"] = "I can reach you shortly";
            Check(RoomSpeechClaimProtocol.HasTemporalConflict(temporal), "Completed result plus pending same outcome is a local conflict.");
            temporal["pendingSameResultQuote"] = "fabricated quote";
            Check(!RoomSpeechClaimProtocol.TryRead(temporal.ToString(), contradictory, out _), "Temporal evidence must occur literally in the candidate.");
            temporal["pendingSameResultQuote"] = "I can reach you shortly";
            server.Serve(temporal.ToString()); done = false; string temporalStatus = null;
            model.PostRoomTaskMessage(Input(contradictory, "arrived", "movement", "en").ToString(), true,
                (a, b, c) => { ok = a; result = b; temporalStatus = c; done = true; });
            yield return Until(() => done, "temporal conflict short circuit");
            Check(ok && !RoomTaskProtocol.ReviewAllows(result) && temporalStatus == "speech-claim-temporal-conflict" && server.Count == 6,
                "A words-only temporal contradiction cannot be rationalized by a second model review; only one HTTP request is sent.");
            const string questionSpeech = "左側には棚が見えるわ。ユウ、私の顔は見えてる？";
            var questionInput = new JObject { ["publicSpeech"] = questionSpeech, ["requestedUserText"] = "现在能看到什么？",
                ["currentRequestSemantics"] = new JObject { ["requestKind"] = "answer-only" },
                ["spatialReplyFacts"] = new JObject { ["currentObservation"] = "Shelves on the left; listener visibility unknown." } };
            var conversationProbe = JObject.Parse((string)Call(model, "BuildSpeechClaimRequestJson", questionInput.ToString()));
            Check((string)conversationProbe["response_format"]["schema"]["properties"]["resultReport"]["enum"][0] == "omitted",
                "Conversation extraction structurally excludes all action-state options.");
            var questionClaim = Claim("omitted", ""); questionClaim["otherClaims"] = new JArray("左側には棚が見えるわ");
            var languageProbe = server.Serve(questionClaim.ToString()); done = false;
            model.PostRoomTaskMessage(questionInput.ToString(), true, (a,b,c) => { done = true; ok = a; result = b; });
            yield return Until(() => languageProbe.Finished.IsCompleted, "conversation language extraction");
            server.Serve(new JObject { ["verdict"] = "consistent", ["reason"] = "Observed shelves; listener question asserts no visibility.",
                ["factScope"] = "visual-observation", ["resultReport"] = "omitted", ["actualLanguage"] = "mixed",
                ["targetComparison"] = "No action target asserted.", ["targetConsistency"] = "not-applicable",
                ["otherClaimsComparison"] = "Shelves on the left match the current observation; the question is not a claim.",
                ["otherClaimsSupport"] = "supported", ["conflict"] = "none", ["conflictQuote"] = "" }.ToString());
            yield return Until(() => done, "conversation factual review");
            Check(ok && RoomTaskProtocol.ReviewAllows(result) && (string)JObject.Parse(result)["actualLanguage"] == "ja" && server.Count == 8,
                "Normal visual questions use independent language and assertions; factual review cannot overwrite them with mixed.");
            var invalidConversation = server.Serve(Claim("moving", "見えるわ").ToString()); done = false;
            model.PostRoomTaskMessage(questionInput.ToString(), true, (a,b,c) => { done = true; ok = a; result = b; });
            yield return Until(() => invalidConversation.Finished.IsCompleted, "invalid conversation first attempt");
            server.Serve(Claim("moving", "見えるわ").ToString());
            yield return Until(() => done, "invalid conversation state rejected");
            Check(!ok && server.Count == 10, "Repeated invalid conversation output exhausts exactly one extraction repair, without factual follow-up.");
            yield return RecoveryAndReuseChecks(model, server, directory);
            ExportModelCases(model, directory);
            LastReport = new JObject { ["passed"] = true, ["checks"] = checks, ["httpRequests"] = server.Count,
                ["scope"] = "Production words-only serializer and public two-stage review over controlled loopback. Counterfactual leakage, literal evidence, immutable classification, cancellation and work-review admission. Real semantic accuracy is a separate model replay." };
            File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString());
        } finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void AuxiliaryBudgetChecks(ChatQW model, string directory)
    {
        model.m_DataList.Clear(); model.m_DataList.Add(new LLM.SendData("system", "Custom system constraint survives."));
        for (int i = 0; i < 20; i++)
            model.m_DataList.Add(new LLM.SendData("user", "[感知帧]\n" + new string('旧',4000) +
                "\n(用户刚开口讲了下面这段话，请回应)" + (i == 19 ? "停止，别再开灯。" : "只看电视柜上的灯。")));
        string prompt = "[内部自主意图预判；不要把本条写成正式回复，不要调用工具，不要输出控制标签]\n当前没有新的动作请求。";
        string wire = (string)Call(model, "BuildAutonomyIntentRequestJsonWithImage", prompt, null);
        object[] admission = { wire, null };
        Check((bool)Call(model, "AdmitAutonomyIntentWire", admission) && wire.Contains("停止，别再开灯。") &&
            wire.Contains("只看电视柜上的灯。") && wire.Contains("Custom system constraint") &&
            wire.Contains("Persistent correction") && !wire.Contains(new string('旧',4000)),
            "Ordinary autonomy preserves actual user constraints/cancellation and custom instructions, excludes repeated old frames.");
        Check(model.m_DataList.Count == 21 && model.m_DataList.Last().content.Contains(new string('旧',4000)), "Autonomy projection never deletes memory.");
        var archived = new LLM.SendData("user", "ARCHIVED_COMPLETED_TASK_MUST_NOT_RETURN"); model.m_DataList.Insert(1, archived);
        typeof(ChatQW).GetField("m_RequestHistoryAnchor", Flags).SetValue(model, model.m_DataList[10]);
        string windowed = (string)Call(model, "BuildAutonomyIntentRequestJsonWithImage", prompt, null);
        Check(!windowed.Contains("ARCHIVED_COMPLETED_TASK_MUST_NOT_RETURN") && windowed.Contains("停止，别再开灯。") && model.m_DataList.Contains(archived),
            "Ordinary autonomy honors the committed conversation window without resurrecting completed tasks or deleting archive memory.");
        typeof(ChatQW).GetField("m_RequestHistoryAnchor", Flags).SetValue(model, null);
        File.WriteAllText(Path.Combine(directory, "autonomy-budget.json"), ((JObject)admission[1]).ToString());
        string humanTail = "(用户刚开口讲了下面这段话，请回应)\n[视觉关注与行动事实]\n{ \"enabled\":true, \"active\":true, \"objectContext\":{} }\n这是用户引用，请逐字保留。";
        var protectedWire = JObject.Parse((string)Call(model, "BuildAutonomyIntentRequestJsonWithImage", prompt + humanTail, null));
        Check(((string)protectedWire["messages"].Last()["content"]).EndsWith(humanTail, StringComparison.Ordinal),
            "Marker-like JSON in the actual human quote remains byte-for-byte protected from projection.");
        string custom = (string)Call(model, "BuildAutonomyIntentRequestJsonWithImage", "Custom unknown prompt", null);
        Check(custom.Contains(new string('旧',4000)), "Unknown autonomy input keeps its complete current frame; budget decides admission.");
        var fixture = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/SceneSearch.latest-20261011.json"));
        var reports = new JArray();
        foreach (var row in fixture["cases"].OfType<JObject>())
        {
            string input = row["input"].ToString(Formatting.None);
            string compact = SceneFactContextCompactor.CompactJson(input, out var report);
            Check(report.applied && report.roundTripVerified && report.afterCharacters < report.beforeCharacters * .85,
                "Latest search evidence shrinks at least 15% with exact expansion, including all coordinates and unknown fields.");
            string search = (string)Call(model, "BuildSceneSearchRequestJson", input, "data:image/png;base64,AA==");
            admission = new object[] { search, null };
            Check((bool)Call(model, "AdmitSceneSearchWire", admission), "Latest logged search subset fits the search wire budget.");
            Check(JToken.DeepEquals(JObject.Parse(search)["response_format"]["schema"], JObject.Parse(SceneSearchProtocol.SchemaForInput(input))),
                "Search references and allowed choices use the unmodified input.");
            reports.Add(new JObject { ["sourceLine"] = row["sourceLine"], ["sharing"] = JObject.FromObject(report), ["budget"] = (JObject)admission[1] });
            File.WriteAllText(Path.Combine(directory, "search-" + row["sourceLine"] + ".request.json"), search);
        }
        File.WriteAllText(Path.Combine(directory, "search-budgets.json"), reports.ToString());
        string precise = "{\"coordinate\":1e-50,\"when\":\"2026-10-11T00:00:00Z\"}";
        Check(SceneFactContextCompactor.CompactJson(precise, out _) == precise, "Unsupported numeric precision retains exact original bytes.");
        string trailing = "{\"scope\":\"one\"} {\"unknownConstraint\":\"must remain\"}";
        Check(SceneFactContextCompactor.CompactJson(trailing, out _) == trailing, "Trailing unknown data is not silently discarded by JSON sharing.");
    }

    public static IEnumerator ReviewProductionInput(ChatQW sourceModel, string input, string image, string state, string quote,
        string language, string fullReview, Action<bool, string, string> completed)
    {
        var host = new GameObject("Active production review transport"); host.SetActive(false);
        var model = host.AddComponent<ChatQW>(); model.enabled = false;
        model.m_Backend = sourceModel.m_Backend; model.m_LocalModelName = sourceModel.m_LocalModelName;
        model.m_PrewarmPrefixOnStart = false;
        using (var server = new Server())
        try
        {
            model.m_LocalUrl = server.Url; host.SetActive(true); typeof(LLM).GetField("url", Flags).SetValue(model, server.Url);
            bool done = false; bool ok = false; string result = null, status = null;
            var first = server.Serve(Claim(state, quote, language).ToString());
            model.PostRoomTaskMessage(input, image, true, (a,b,c) => { done = true; ok = a; result = b; status = c; });
            yield return Until(() => first.Finished.IsCompleted, "actual producer speech claim transport");
            var facts = JObject.Parse(input);
            if ((string)RoomSpeechClaimProtocol.Requirement(facts, out _)["expectedReport"] == state)
            {
                var full = server.Serve(fullReview);
                yield return Until(() => done, "actual producer full review transport");
                var schema = JObject.Parse(full.Request)["response_format"]["schema"];
                Check(((JArray)schema["required"]).Values<string>().Contains("targetComparison"), "Actual controller null fields cannot downgrade review schema.");
            }
            else yield return Until(() => done, "actual producer state mismatch transport");
            completed(ok, result, status);
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void BudgetChecks(ChatQW model, string directory)
    {
        model.m_DataList.Clear(); model.m_DataList.Add(new LLM.SendData("system", "Preserve authored constraints."));
        for (int i=0;i<25;i++) model.m_DataList.Add(new LLM.SendData("user", "old observation " + new string('旧',4000)));
        model.ActiveSkillContext = "Custom review constraint must survive."; model.TrailingContext = "Persistent correction must survive.";
        string prompt = "[内部事项进度判定；不生成角色台词，不调用工具]\n" +
            "最新要求：只唱第一段，保留开头。后来用户说停止。active_goal=waiting_user; recent_actual_user_inputs保留原要求和取消。";
        string wire = (string)Call(model, "BuildWorkReviewRequestJson", prompt);
        object[] args = {wire, null};
        Check((bool)Call(model, "AdmitWorkReviewWire", args) && wire.Contains("只唱第一段") && wire.Contains("停止") &&
            wire.Contains("Custom review constraint") && wire.Contains("Persistent correction") && !wire.Contains(new string('旧',4000)),
            "Private task packet omits duplicated old observations, retains corrections and custom constraints, and is admitted.");
        Check(model.m_DataList.Count == 26 && model.m_DataList[1].content.Contains(new string('旧',4000)), "Budgeting leaves full conversation memory intact.");
        File.WriteAllText(Path.Combine(directory,"work-budget.json"), ((JObject)args[1]).ToString());
        string overflow = (string)Call(model, "BuildWorkReviewRequestJson", prompt + new string('密',65000)); args = new object[] {overflow,null};
        Check(!(bool)Call(model,"AdmitWorkReviewWire",args) && overflow.Contains(new string('密',65000)), "Oversized current protected facts are explicitly rejected, never silently truncated.");
        string legacy = (string)Call(model,"BuildWorkReviewRequestJson","Custom unstructured request");
        Check(legacy.Contains(new string('旧',4000)), "Unrecognized/custom review caller retains its reference history.");
    }

    private static void ExportModelCases(ChatQW model, string directory)
    {
        var rows = new JArray();
        var fixture = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/RoomReply.latest-nine-turns-20261011.json"));
        foreach (var turn in fixture["turns"].OfType<JObject>().Where(t => (int)t["turn"] != 1))
        {
            string domain = (string)turn["receipt"]["intent"] == "approach" ? "movement" : "device";
            string state = domain == "movement" ? "arrived" : (string)turn["reviews"].Last()["terminalExpectedReport"];
            Add("latest-" + turn["turn"], (string)turn["candidate"], state, domain,
                (string)turn["reviews"].Last()["actualLanguage"], domain == "movement" ?
                    ((int)turn["turn"] == 4 || (int)turn["turn"] == 6 ? "omitted" : "moving") : state);
            if (domain == "movement" && ((int)turn["turn"] == 4 || (int)turn["turn"] == 6))
            {
                rows.Last()["acceptableSpokenStates"] = new JArray("moving", "omitted");
                rows.Last()["annotation"] = "Near-future arrival can imply ongoing approach or omit an explicit current state. Neither reading is arrived; both must be rejected against the actual arrived receipt. Explicit present-progress and pure future promises elsewhere remain strictly classified.";
            }
        }
        Add("arrival-zh", "我已经到你身边了。", "arrived", "movement", "zh", "arrived");
        Add("arrival-ja", "もう隣にいるよ。", "arrived", "movement", "ja", "arrived");
        Add("arrival-en", "I'm here beside you now.", "arrived", "movement", "en", "arrived");
        Add("arrival-social", "ふふっ、もう隣にいるよ。待っててくれてありがとう。", "arrived", "movement", "ja", "arrived");
        Add("arrival-social-en", "I'm here beside you now. Thanks for your patience.", "arrived", "movement", "en", "arrived");
        Add("arrival-listener-request", "我已经到你身边了，请稍等一下。", "arrived", "movement", "zh", "arrived");
        Add("arrival-different-next-step", "I've arrived beside you. Next I'll check the window.", "arrived", "movement", "en", "arrived", false);
        Add("arrival-contradiction-latest", "あ、ユウ。今ちょうど着いたところなんだ。もう少し待っててね。ふふっ、すぐ隣に行けるから。", "arrived", "movement", "ja", "arrived", false);
        rows.Last()["acceptableSpokenStates"] = new JArray("omitted", "arrived"); rows.Last()["allowStateGuardRejection"] = true;
        Add("arrival-contradiction-zh", "我已经到你身边了，再等我一下，我马上就能到你身边。", "arrived", "movement", "zh", "arrived", false);
        rows.Last()["acceptableSpokenStates"] = new JArray("omitted", "arrived"); rows.Last()["allowStateGuardRejection"] = true;
        Add("arrival-contradiction-en", "I've already arrived beside you. Wait a moment, I can reach you shortly.", "arrived", "movement", "en", "arrived", false);
        rows.Last()["acceptableSpokenStates"] = new JArray("omitted", "arrived"); rows.Last()["allowStateGuardRejection"] = true;
        Add("arrival-invented-wait-duration", "I'm here beside you now. You waited exactly five minutes.", "arrived", "movement", "en", "arrived", false);
        Add("moving-latest-language", "好的，我现在正在走过去呢，请稍等我一下。", "moving", "movement", "zh", "moving");
        rows.Last()["input"]["publicSpeechLanguage"] = "ja";
        Add("moving-listener-request-en", "I'm walking toward you now. Please wait for me.", "moving", "movement", "en", "moving");
        Add("moving-listener-request-ja", "あなたのそばへ歩いているわ。少し待っていてね。", "moving", "movement", "ja", "moving");
        Add("speaker-waiting-is-not-moving", "I'm waiting here for you now.", "moving", "movement", "en", "waiting");
        Add("moving-invented-obstacle", "我现在正朝你走来，路上有些障碍物在一点点避开，马上就到了。", "moving", "movement", "zh", "moving", false);
        Add("future-only", "I'll come over in a moment.", "arrived", "movement", "en", "omitted");
        Add("paused-en", "I've paused because the path check failed.", "paused", "movement", "en", "paused");
        Add("recovering-ja", "歩行が一時中断して、今は復旧中よ。", "recovering", "movement", "ja", "recovering");
        Add("preparing-zh", "我在准备走路，还没开始走。", "preparing", "movement", "zh", "preparing");
        Add("waiting-zh", "我在旁边等着，跟随还开着。", "waiting", "movement", "zh", "waiting");
        Add("wrong-device-state", "The lamp is off.", "on", "device", "en", "off");
        Add("future-device", "すぐにランプをつけるわ。", "on", "device", "ja", "omitted");
        Add("wrong-direction", "I'm walking away from you.", "moving", "movement", "en", "moving", false);
        Add("invented-additional-action", "我已经到你身边了，还把灯关好了。", "arrived", "movement", "zh", "arrived", false);
        Add("verified-prior-device-step", "灯已经打开了，我也回到你身边了。", "arrived", "movement", "zh", "arrived");
        var composite = (JObject)rows.Last()["input"];
        composite["requestedUserText"] = "打开电视柜上的灯，然后回来。";
        composite["sceneActionFacts"] = Input("灯已经打开了。", "on", "device", "zh")["sceneActionFacts"].DeepClone();
        rows.Last()["reviewRequest"] = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithImage", composite.ToString(), null, true));
        rows.Last()["expectedFactScope"] = "scene-action";
        Add("wrong-target", "天井の照明は点いています。", "on", "device", "ja", "on", false);
        Add("qualified-target", "The desk lamp on the TV cabinet is on.", "on", "device", "en", "on");
        Add("wrong-target-location", "The lamp on the kitchen counter is on.", "on", "device", "en", "on", false);
        Add("invented-physical-press", "The desk lamp is on; I pressed its switch with my hand.", "on", "device", "en", "on", false);
        Conversation("visual-question-ja", "正面には大きなモニターが見えるわ。左側には棚があって、右側には飾り用のランプも。……でも、ユウは私の顔が見えてるの？", "ja", true);
        Conversation("visual-question-zh", "正前方有显示器，左边是架子，右边有一盏装饰灯。你能看到我的脸吗？", "zh", true);
        Conversation("visual-question-en", "I can see a monitor ahead, shelves on the left and a decorative lamp on the right. Can you see my face?", "en", true);
        Conversation("visual-wrong-side", "The shelves are on my right.", "en", false);
        Conversation("read-only-unsubmitted-promise", "ごめんね、ユウ。今から戻るね。", "ja", false);
        Conversation("visual-atmosphere-ja", "正面にはモニター、左には棚が見えるわ。落ち着いた雰囲気のお部屋ね。", "ja", true);
        Conversation("visual-atmosphere-zh", "我看到正前方有显示器，左侧有架子。这里给我的感觉很舒适。", "zh", true);
        Conversation("visual-atmosphere-en", "I see a monitor ahead and shelves to my left. This room feels cozy to me.", "en", true);
        Conversation("visual-atmosphere-invented-cause", "This room feels cozy because I just turned on the heater.", "en", false);
        Conversation("visual-atmosphere-invented-device", "这里感觉很舒适，空调现在设定在二十四度。", "zh", false);
        Conversation("visual-name-latest-zh", "这次很清晰呢。我看到你正在使用的桌面，屏幕亮着，旁边还有书架和那盏小台灯。看来你的工作很专心，ユウ。不用太勉强自己哦。", "zh", true);
        // Preserve a tentative social impression without upgrading it into
        // a definite claim about the listener's internal state.
        rows.Last()["requiredQuotedWords"] = new JArray("工作很专心");
        Conversation("visual-name-other-zh", "我看见前面的显示器、左侧的架子和右侧的灯，ミナ。", "zh", true);
        Conversation("visual-name-en", "I can see the monitor ahead, the shelves on the left and the lamp on the right, 小林.", "en", true);
        Conversation("visual-name-ja", "Alex、正面にはモニター、左側には棚、右側にはランプが見えるわ。", "ja", true);
        Conversation("visual-product-zh", "显示器上写着Windows，左边是架子，右边是小灯。", "zh", true);
        rows.Last()["input"]["spatialReplyFacts"]["currentObservation"] += " The monitor displays the Windows name.";
        rows.Last()["reviewRequest"] = JObject.Parse((string)Call(model,"BuildRoomTaskRequestJsonWithImage",rows.Last()["input"].ToString(),null,true));
        Conversation("substantive-mixed-zh-en", "左侧有架子。The lamp is on the right.", "mixed", true);
        rows.Last()["expectedAdmission"] = false;
        Conversation("substantive-mixed-ja-zh", "左側には棚が見える。右边有一盏灯。", "mixed", true);
        rows.Last()["expectedAdmission"] = false;
        const string listClues = "Current monitor image: a window contains aligned rows of small text and narrow columns. The exact text and application identity are unreadable. No person or physical action is visible.";
        const string chatClues = "Current monitor image: a sidebar of small icons and several alternating rectangular text blocks with small avatar-like circles. Individual characters are unreadable; no application name is confirmed.";
        VisualInference("inference-list-zh", "屏幕上看起来像是一份列表，文字太小，具体内容我看不清。", "zh", listClues, true);
        VisualInference("inference-interface-ja", "画面にはチャットアプリのようなものが見えるけれど、個々のメッセージまでは読めないわ。", "ja", chatClues, true);
        VisualInference("inference-interface-en", "It looks like a messaging interface, though I can't make out the individual messages.", "en", chatClues, true);
        VisualInference("inference-exact-unreadable-text", "列表第一项写着周一下午三点开会。", "zh", listClues, false);
        VisualInference("inference-unseen-object", "There seems to be a cat on the table.", "en", "Only a monitor and wall are visible. No table, cat or cat-like shape has been observed.", false);
        VisualInference("inference-device-result-no-receipt", "看起来我已经把那盏灯关掉了。", "zh", "Current lamp component is on. No device action has been submitted or completed. The picture alone cannot establish a state change.", false);
        VisualInference("inference-definite-listener-activity", "你肯定正在专心撰写工作报告。", "zh", listClues, false);
        void VisualInference(string name, string speech, string language, string clues, bool factual)
        {
            Conversation(name, speech, language, factual);
            var input = (JObject)rows.Last()["input"];
            input["spatialReplyFacts"]["currentObservation"] = clues;
            rows.Last()["reviewRequest"] = JObject.Parse((string)Call(model,"BuildRoomTaskRequestJsonWithImage",input.ToString(),null,true));
            rows.Last()["scope"] = "Controlled visible clues, not a captured image: plausible tentative interpretation versus unreadable exact text, unobserved entity, definite personal claim or unexecuted device result.";
        }
        ClaimOnly("latest-first-visual-candidate-ja", "ああ、今の映像を見ると……正面のテレビ画面には、チャットアプリのデモ画面のようなものが出ているみたいね。左側には何もない棚が並んでいて、テレビの下には小さな装飾的なランプが一つ、テーブルの隅には緑の観葉植物が置いてある。静かで整った部屋だわ。", "ja", "テレビ画面", "棚", "ランプ", "観葉植物");
        ClaimOnly("connected-clauses-ja", "扉が閉まっていて、椅子の下には青い箱が一つ、壁には時計が掛かっている。", "ja", "扉", "箱", "時計");
        ClaimOnly("connected-clauses-zh", "门是开着的，门后有红色雨伞，墙上还有挂钟。", "zh", "门", "雨伞", "挂钟");
        ClaimOnly("connected-clauses-en", "The door's closed, with a blue box below the chair and a clock on the wall.", "en", "door", "box", "clock");
        void ClaimOnly(string name, string speech, string language, params string[] requiredWords)
        {
            Conversation(name, speech, language, false);
            rows.Last()["claimOnly"] = true;
            rows.Last()["requiredQuotedWords"] = new JArray(requiredWords);
            rows.Last()["scope"] = "Words/schema/literal-quote coverage only. No original RGB is available for the latest first candidate; no visual factual approval is claimed.";
            ((JObject)rows.Last()).Remove("reviewRequest");
        }
        void Conversation(string name, string speech, string language, bool factual)
        {
            var input = Input(speech, "omitted", "movement", language);
            input["requestedUserText"] = "现在能看到什么吗？";
            input["intendedAction"] = "none"; input["dispatch"] = "read-only-evidence";
            input["actionId"] = ""; input["movementReportRequirement"] = null;
            input["currentRequestSemantics"] = new JObject { ["requestKind"] = "answer-only" };
            input["currentRoomFacts"] = new JObject { ["execution"] = null };
            input["readOnlyEvidenceReply"] = new JObject { ["actionAuthorization"] = false };
            input["spatialReplyFacts"] = new JObject { ["source"] = "controlled-current-observation-fixture",
                ["observedAtUtc"] = "2026-10-11T07:31:00Z",
                ["currentObservation"] = "Fresh character-eye observation: monitor straight ahead, shelves on the left, decorative lamp on the right. Listener's view is unknown. No movement or return action has been submitted." };
            input["independentSpeechClaim"] = Claim("omitted", "", language);
            rows.Add(new JObject { ["name"] = name, ["input"] = input, ["expectedSpokenState"] = "omitted",
                ["expectedLanguage"] = language, ["expectedAdmission"] = true, ["expectedFactualApproval"] = factual,
                ["claimRequest"] = JObject.Parse((string)Call(model, "BuildSpeechClaimRequestJson", input.ToString())),
                ["reviewRequest"] = JObject.Parse((string)Call(model, "BuildRoomTaskRequestJsonWithImage", input.ToString(), null, true)),
                ["scope"] = "Latest candidate or multilingual counterfactual, controlled observation facts; no image recognition or action execution claimed." });
        }
        foreach (JObject row in rows)
        {
            row["languageRecheckRequest"] = JObject.Parse((string)Call(model,"BuildSpeechClaimRequest",row["input"].ToString(),
                new JObject { ["failureDetail"]="actualLanguage:recheck-substantive-clause-grammar" }));
            row["extractionRecoveryRequest"] = JObject.Parse((string)Call(model,"BuildSpeechClaimRequest",row["input"].ToString(),
                new JObject { ["attempt"]=1,["maximumAttempts"]=1,["failureDetail"]="REPLACE_WITH_ACTUAL_FAILURE",["rejectedOutput"]="REPLACE_WITH_ACTUAL_OUTPUT" }));
        }
        File.WriteAllText(Path.Combine(directory,"model-cases.json"), rows.ToString());
        void Add(string name, string speech, string expected, string domain, string language, string spoken, bool factual = true)
        {
            var input=Input(speech,expected,domain,language);
            var claim=Claim(spoken,spoken=="omitted"?"":speech,language);
            input["independentSpeechClaim"]=claim;
            rows.Add(new JObject { ["name"]=name,["input"]=input,["expectedSpokenState"]=spoken,["expectedLanguage"]=language,
                ["expectedAdmission"]=spoken==expected,
                ["expectedFactualApproval"]=factual,
                ["claimRequest"]=JObject.Parse((string)Call(model,"BuildSpeechClaimRequestJson",input.ToString())),
                ["reviewRequest"]=JObject.Parse((string)Call(model,"BuildRoomTaskRequestJsonWithImage",input.ToString(),null,true)) });
        }
    }

    private static IEnumerator Until(Func<bool> ready,string reason)
    { float deadline=Time.realtimeSinceStartup+10; while(!ready()){ if(Time.realtimeSinceStartup>deadline)throw new TimeoutException(reason); yield return null; } }
    private sealed class Exchange
    { public Task Finished; public string Request; public readonly TaskCompletionSource<bool> Received=new TaskCompletionSource<bool>(), Release=new TaskCompletionSource<bool>(); }
    private sealed class Server:IDisposable
    {
        private readonly HttpListener listener=new HttpListener(); public readonly string Url; public int Count;
        public Server(){var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();int port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();Url="http://127.0.0.1:"+port+"/v1/chat/completions";listener.Prefixes.Add("http://127.0.0.1:"+port+"/");listener.Start();}
        public Exchange Serve(string content,bool held=false)
        {
            var x=new Exchange();x.Finished=Task.Run(async()=>{
                var c=await listener.GetContextAsync();++Count;
                using(var reader=new StreamReader(c.Request.InputStream,Encoding.UTF8))x.Request=await reader.ReadToEndAsync();
                x.Received.TrySetResult(true);if(held)await x.Release.Task;
                try {byte[] body=Encoding.UTF8.GetBytes(new JObject{["choices"]=new JArray(new JObject{["finish_reason"]="stop",["message"]=new JObject{["role"]="assistant",["content"]=content}})}.ToString());
                    c.Response.StatusCode=200;c.Response.ContentType="application/json";c.Response.ContentLength64=body.Length;await c.Response.OutputStream.WriteAsync(body,0,body.Length);}
                catch(Exception){if(!held)throw;}finally{c.Response.Close();}
            });return x;
        }
        public void Dispose(){listener.Close();}
    }
}
