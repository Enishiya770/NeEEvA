using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Production serialization/validation with synthetic public dialogue; no network.</summary>
public static class AgentWorkReviewTransportRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
    private static int checks;

    [MenuItem("Tools/NeEEvA/Validate work review transport")]
    public static void RunInteractive()
    {
        checks = 0;
        GameObject host = new GameObject("InactiveWorkReviewWireFixture"); host.SetActive(false);
        try
        {
            var model = host.AddComponent<ChatQW>();
            model.m_EphemeralMaxTokens = 48;
            model.m_EphemeralHistoryMessages = 1;
            model.m_LogRequestStats = false;
            model.m_DataList.Add(new LLM.SendData("system", "Public fixture persona"));
            model.m_DataList.Add(new LLM.SendData("user", "Sing the two clips together."));
            model.m_DataList.Add(new LLM.SendData("assistant", "I will check."));
            model.m_DataList.Add(new LLM.SendData("user", "Only the first clip; restore its retained introduction."));
            model.m_DataList.Add(new LLM.SendData("assistant", "I will try the expanded range."));
            model.ActiveSkillContext = "Bounded singing contract";
            model.TrailingContext = "Read-only memory context";
            int count = model.m_DataList.Count;
            const string prompt = "Review JSON from the latest facts: playback completed both clips using current range.";
            foreach (var backend in new[] { ChatQW.BackendType.Local, ChatQW.BackendType.Cloud })
            {
                model.m_Backend = backend;
                var payload = Build(model, "BuildWorkReviewRequestJson", prompt);
                Check((int)payload["max_tokens"] == 1024, "Work review inherited the draft token limit");
                Check(!(bool)payload["stream"] && !(bool)payload["enable_thinking"], "Work review became a spoken/thinking stream");
                Check((string)payload["response_format"]["type"] == "json_object", "Missing structured JSON output mode");
                var messages = (JArray)payload["messages"];
                Check((string)messages[messages.Count - 1]["content"] == prompt, "Fresh facts are not the final review input");
                Check(payload.ToString().Contains("Only the first clip") && payload.ToString().Contains("Sing the two clips together"),
                    "Draft history setting removed user corrections or their reference context");
                Check((string)messages[messages.Count - 2]["role"] == "system" &&
                    ((string)messages[messages.Count - 2]["content"]).Contains("singing_goal_status"), "Remote/local schema contract missing");
                Check(model.m_DataList.Count == count && model.SpokenPrefix == "", "Review serialization mutated conversation");
                if (backend == ChatQW.BackendType.Local)
                {
                    Check((int)payload["id_slot"] == 1 && !(bool)payload["chat_template_kwargs"]["enable_thinking"],
                        "Review overwrote the main conversation slot or enabled thinking");
                    Check((bool)payload["response_format"]["schema"]["additionalProperties"] == false &&
                        ((JArray)payload["response_format"]["schema"]["required"]).Count == 8, "Incomplete local schema");
                    Check(((JArray)payload["response_format"]["schema"]["properties"]["singing_goal_expected"]["required"]).Count == 6,
                        "Expected singing goal lost its required nested fields");
                }
                else Check(payload["id_slot"] == null && payload["chat_template_kwargs"] == null &&
                    payload["response_format"]["schema"] == null, "Cloud received unsupported local-only schema/slot fields");
            }
            model.m_Backend = ChatQW.BackendType.Local;
            var draft = Build(model, "BuildEphemeralRequestJson", "old draft", 1, false);
            var boundary = Build(model, "BuildTurnBoundaryRequestJson", "boundary JSON");
            Check((int)draft["max_tokens"] == 48 && draft["response_format"] == null, "Existing listening draft wire changed");
            Check((int)boundary["max_tokens"] == 256 && boundary["response_format"]["schema"]["properties"]["action"] != null,
                "Existing boundary wire changed");
            ValidateResponses();
            ValidateFormalProjection();
            ValidateSingingDiagnostics(model);
            var fallback = host.AddComponent<WorkReviewFallbackFixture>();
            string result = null;
            ((LLM)fallback).PostWorkReviewMsg("public fallback request", value => result = value);
            Check(fallback.received == "public fallback request" && result == "fallback-result", "Legacy provider fallback bypassed its ephemeral override");
            Debug.Log("[AgentWorkReviewTransportRegression] passed checks=" + checks);
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    public static void RunBatch()
    {
        var report = new JObject { ["scope"] = "Production review wire, local/cloud schema, rejection gates and actual serialized feedback diagnostic. Synthetic dialogue; no network or private scene." };
        try { RunInteractive(); report["passed"] = true; }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); Debug.LogException(error); }
        report["checks"] = checks;
        report["checkedAtUtc"] = DateTime.UtcNow.ToString("o");
        string path = Path.GetFullPath("Tools/MotionAdapter/reports/agent-work-review-transport-regression.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, report.ToString() + "\n");
        EditorApplication.Exit((bool)report["passed"] ? 0 : 1);
    }

    private static JObject Decision() => new JObject {
        ["work_evidence"] = "Playback completed both clips, but the user requested only the first with its retained opening.",
        ["work_status"] = "continue", ["proceed"] = true, ["intent"] = "Submit only the first clip with range expanded.",
        ["singing_goal_status"] = "revise", ["singing_goal_evidence"] = "Two current-range clips do not match the latest request.",
        ["singing_goal_expected"] = new JObject {
            ["origin"] = "user_request", ["request_quote"] = "Only the first clip; restore its retained introduction.",
            ["refs"] = "clip:first", ["range"] = "expanded", ["start_seconds"] = null, ["end_seconds"] = null
        },
        ["wait_seconds"] = 5
    };

    private static string Envelope(string output, string finish = "stop") => new JObject {
        ["choices"] = new JArray(new JObject { ["finish_reason"] = finish, ["message"] = new JObject { ["content"] = output } })
    }.ToString(Newtonsoft.Json.Formatting.None);

    private static object[] Parse(string envelope)
    {
        object[] args = { envelope, null, null, false, false, null };
        bool accepted = (bool)typeof(ChatQW).GetMethod("TryReadWorkReviewResponse", Flags).Invoke(null, args);
        return new object[] { accepted, args[1], args[2], args[3], args[4], args[5] };
    }

    private static void ValidateResponses()
    {
        string good = Decision().ToString(Newtonsoft.Json.Formatting.None);
        var accepted = Parse(Envelope(good));
        Check((bool)accepted[0] && (bool)accepted[3] && (bool)accepted[4] && (string)accepted[1] == good, "Valid complete decision rejected or rewritten");
        var truncated = Parse(Envelope(good, "length"));
        Check(!(bool)truncated[0] && (bool)truncated[3] && (bool)truncated[4] && (string)truncated[5] == "truncated", "Syntactically complete but token-truncated approval accepted");
        foreach (string finish in new[] { "", "tool_calls", "content_filter" })
            Check(!(bool)Parse(Envelope(good, finish))[0], "Incomplete/filtered decision accepted");
        foreach (string invalid in new[] { "", "{", "[]", "```json\n" + good + "\n```", "{\"work_status\":\"closed\"}",
            good.Substring(0, good.Length - 1) + ",\"work_status\":\"closed\"}" })
            Check(!(bool)Parse(Envelope(invalid))[0], "Malformed, partial or duplicate-field decision accepted");
        foreach (string field in new[] { "work_evidence", "work_status", "proceed", "intent", "singing_goal_status", "singing_goal_evidence", "singing_goal_expected", "wait_seconds" })
        {
            var missing = Decision(); missing.Remove(field);
            Check(!(bool)Parse(Envelope(missing.ToString()))[0], "Required field was optional: " + field);
        }
        var variants = new List<JObject>();
        var blank = Decision(); blank["work_evidence"] = "   "; variants.Add(blank);
        var unknown = Decision(); unknown["work_status"] = "success"; variants.Add(unknown);
        var extra = Decision(); extra["tool"] = "sing"; variants.Add(extra);
        var stringBool = Decision(); stringBool["proceed"] = "true"; variants.Add(stringBool);
        var unknownGoal = Decision(); unknownGoal["singing_goal_status"] = "auto_approve"; variants.Add(unknownGoal);
        var noEvidence = Decision(); noEvidence["singing_goal_status"] = "approved"; noEvidence["singing_goal_evidence"] = ""; variants.Add(noEvidence);
        var tooLong = Decision(); tooLong["intent"] = new string('x', 401); variants.Add(tooLong);
        var negative = Decision(); negative["wait_seconds"] = -1; variants.Add(negative);
        var overflow = Decision(); overflow["wait_seconds"] = 3601; variants.Add(overflow);
        var nullExpected = Decision(); nullExpected["singing_goal_expected"] = null; variants.Add(nullExpected);
        var arrayExpected = Decision(); arrayExpected["singing_goal_expected"] = new JArray(); variants.Add(arrayExpected);
        var missingRange = Decision(); ((JObject)missingRange["singing_goal_expected"]).Remove("range"); variants.Add(missingRange);
        var invalidRange = Decision(); invalidRange["singing_goal_expected"]["range"] = "whole"; variants.Add(invalidRange);
        var tooManyRefs = Decision(); tooManyRefs["singing_goal_expected"]["refs"] = new string('x', 1025); variants.Add(tooManyRefs);
        var stringStart = Decision(); stringStart["singing_goal_expected"]["start_seconds"] = "2.5"; variants.Add(stringStart);
        var negativeEnd = Decision(); negativeEnd["singing_goal_expected"]["end_seconds"] = -1; variants.Add(negativeEnd);
        var missingEnd = Decision(); ((JObject)missingEnd["singing_goal_expected"]).Remove("end_seconds"); variants.Add(missingEnd);
        var extraExpected = Decision(); extraExpected["singing_goal_expected"]["reason"] = "extra field"; variants.Add(extraExpected);
        foreach (var invalid in variants) Check(!(bool)Parse(Envelope(invalid.ToString()))[0], "Invalid typed/ranged schema accepted");
        var noGoal = Decision(); noGoal["singing_goal_status"] = "none"; noGoal["singing_goal_evidence"] = "";
        noGoal["singing_goal_expected"] = new JObject { ["origin"] = "none", ["request_quote"] = "", ["refs"] = "", ["range"] = "none", ["start_seconds"] = null, ["end_seconds"] = null };
        Check((bool)Parse(Envelope(noGoal.ToString()))[0], "No-goal empty/none/null expected shape was rejected");
        var window = Decision(); window["singing_goal_expected"]["range"] = "window";
        window["singing_goal_expected"]["start_seconds"] = 2.5; window["singing_goal_expected"]["end_seconds"] = 8.75;
        Check((bool)Parse(Envelope(window.ToString()))[0], "Expected numeric window was rejected");
        Check(!(bool)Parse("not an envelope")[0] && !(bool)Parse("{\"choices\":[]}")[0], "Malformed provider envelope accepted");
    }

    private static void ValidateSingingDiagnostics(ChatQW model)
    {
        var history = new List<LLM.SendData> {
            new LLM.SendData("system", "private-unrelated-content"),
            new LLM.SendData("user", "[Sing/Clip] ref=clip:old source=pending"),
            new LLM.SendData("assistant", "<clip_confirm ref=\"clip:old\"/>")
        };
        const string feedback = "[Sing/Clip] ref=clip:first source=confirmed_user range=expanded\n" +
            "[Sing/Execution] work_active=false state=idle\n[Sing/PlaybackFact] last_completed=clip:first range=expanded\n" +
            "private-feedback-content";
        foreach (bool stream in new[] { false, true })
        {
            var payload = Build(model, "BuildRequestJsonForMessagesWithFeedback", history, stream, "normal contract", feedback);
            string summary = ChatQW.BuildRequestSingingSummary(payload.ToString());
            Check(summary.Contains("fact_source=execution_feedback") && summary.Contains("range=expanded") && summary.Contains("last_completed=clip:first"),
                "Diagnostic missed execution feedback after the latest user");
            Check(!summary.Contains("clip:old") && !summary.Contains("private-"), "Fresh feedback mixed with stale facts or unrelated private context");
        }
        var missing = Build(model, "BuildRequestJsonForMessagesWithFeedback", history, true, "", "Current tool result has no singing inventory.");
        string missingSummary = ChatQW.BuildRequestSingingSummary(missing.ToString());
        Check(missingSummary.Contains("fact_source=execution_feedback") && missingSummary.Contains("facts=missing") && !missingSummary.Contains("clip:old"),
            "Absent current facts silently fell back to the old user frame");
        history[1].imageDataUrl = "data:image/png;base64,DO_NOT_LOG_PIXELS";
        var ordinary = Build(model, "BuildRequestJsonForMessagesWithFeedback", history, true, "", null);
        string ordinarySummary = ChatQW.BuildRequestSingingSummary(ordinary.ToString());
        Check(ordinarySummary.Contains("fact_source=latest_user") && ordinarySummary.Contains("clip:old") && !ordinarySummary.Contains("DO_NOT_LOG_PIXELS"),
            "Normal user/image diagnostics changed or exposed image bytes");
        Check(ChatQW.BuildRequestSingingSummary("broken JSON").Contains("diagnostic_parse_failed"), "Diagnostic parse failure escaped request logging");
        Check(ChatQW.BuildRequestSingingSummary("{\"messages\":[null,2,\"bad\"]}").Contains("facts=missing"),
            "Malformed message structure escaped diagnostics");
    }

    private static void ValidateFormalProjection()
    {
        string Project(string content, bool complete) => (string)typeof(ChatQW).GetMethod("ProjectFormalCompletion", Flags)
            .Invoke(null, new object[] { content, RoleOutputChannels.Parse(content), complete });
        Check(Project("", true) == "" && Project("  \n\t", true) == "", "Empty generation became an intentional silence");
        Check(Project("<silent/>", true) == "<silent/>", "Explicit silent decision was mistaken for failure");
        Check(Project("<lang code=\"ja\"/>", true) == "" && Project("<say></say>", true) == "",
            "Language/empty say metadata became an intentional silence");
        Check(Project("<lang code=\"ja\"/><silent/>", true) == "<silent/>" &&
            Project("<thought>Private review completed.</thought>", true) == "<silent/>", "Metadata filtering discarded explicit silence or private output");
        Check(Project("<body_inspect scope=\"runtime\"/>", true).Contains("<body_inspect"), "Valid tool-only output was discarded");
        Check(Project("<body_inspect scope=\"runtime\"/>", false) == "", "Truncated tool-only output became valid silence");
        string truncated = Project("<lang code=\"en\"/>I will check.<body_inspect scope=\"runtime\"/>", false);
        Check(truncated.Contains("I will check.") && !truncated.Contains("body_inspect"), "Truncation lost existing speech or dispatched tools");
    }

    private static JObject Build(ChatQW model, string method, params object[] args)
        => JObject.Parse((string)typeof(ChatQW).GetMethod(method, Flags).Invoke(model, args));
    private static void Check(bool passed, string message)
    {
        checks++;
        if (!passed) throw new InvalidOperationException(message);
    }
}

public sealed class WorkReviewFallbackFixture : LLM
{
    public string received;
    public override void PostEphemeralMsg(string prompt, Action<string> callback) { received = prompt; callback("fallback-result"); }
}
