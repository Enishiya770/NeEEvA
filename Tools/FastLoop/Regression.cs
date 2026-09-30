using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// These are deterministic protocol/runtime replays. Only --evaluate-model evaluates
// real model responses; supplied expected decisions are never classified by a mock.
public static class FastLoopRegression
{
    static int checks;
    static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    static string Option(string[] args, string key, string fallback = null)
    {
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }

    static JObject Decision(string route, string candidate = "none", string evidence = "", string speech = "", string language = "zh",
        string control = "none", string target = "", string controlEvidence = "")
        => new JObject { ["version"] = FastLoopProtocol.CurrentVersion,
            ["task_control"] = control, ["control_target_id"] = target, ["control_evidence"] = controlEvidence,
            ["route"] = route, ["candidate"] = candidate, ["evidence"] = evidence, ["speech"] = speech, ["language"] = language };

    static FastLoopDecision Parse(JObject value, string user)
    {
        Check(FastLoopProtocol.TryParse(value.ToString(Formatting.None), user, out var decision, out var reason), "fixture decision rejected: " + reason);
        return decision;
    }

    static FastLoopTask Apply(FastLoopRuntime runtime, string candidate, string user = "调整头部")
    {
        var before = runtime.HeadTask;
        var decision = Parse(Decision("head", candidate, user), user);
        Check(runtime.TryApply(decision, user, before?.Id, before?.Revision ?? 0, out var task, out var reason), "head apply: " + reason);
        return task;
    }

    static FastLoopRuntime Started(bool confirm = true)
    {
        var runtime = new FastLoopRuntime();
        runtime.BeginTurn();
        var task = Apply(runtime, "head.raise");
        Check(task.Active && task.State == "starting", "accepted task starts before confirmation");
        Check(!runtime.CanPublishCompletion(task.Id, task.Revision, runtime.OutputRevision), "no success before execution confirmation");
        if (confirm) Check(runtime.Confirm(task.Id, task.Revision), "execution confirms task");
        return runtime;
    }

    static void RuntimeCase(string scenario)
    {
        var runtime = Started();
        var old = runtime.HeadTask;
        long output = runtime.OutputRevision;
        Check(runtime.CanPublishCompletion(old.Id, old.Revision, output), "confirmed receipt has current authority");
        switch (scenario)
        {
            case "holding_survives_turn":
                runtime.BeginTurn();
                Check(runtime.HeadTask.Id == old.Id && runtime.HeadTask.Revision == old.Revision && runtime.HeadTask.State == "holding", "ordinary turn preserves held posture");
                var reply = Parse(Decision("reply", speech: "今天过得很好。"), "今天好吗？");
                Check(runtime.TryApply(reply, "今天好吗？", old.Id, old.Revision, out var kept, out _), "ordinary reply accepted");
                Check(kept.Id == old.Id && kept.Candidate == "head.raise", "reply leaves head task untouched");
                var hold = Apply(runtime, "head.hold", "保持这个姿势");
                Check(hold.Id == old.Id && hold.Revision == old.Revision && hold.State == "holding", "reaffirming held pose does not restart motion");
                break;
            case "correction_rejects_old_completion":
                var corrected = Apply(runtime, "head.lower", "不是抬头，是低头");
                Check(corrected.Id == old.Id && corrected.Revision > old.Revision && corrected.State == "starting", "correction keeps identity but advances revision");
                Check(!runtime.Confirm(old.Id, old.Revision), "late original confirmation rejected");
                Check(!runtime.Fail(old.Id, old.Revision, "old error"), "late original failure rejected");
                Check(!runtime.CanPublishCompletion(old.Id, old.Revision, output), "old correction receipt rejected");
                Check(runtime.Confirm(corrected.Id, corrected.Revision), "new correction can complete");
                Check(runtime.CanPublishCompletion(corrected.Id, corrected.Revision, output), "current revision may report completion");
                break;
            case "cancel_rejects_late_confirmation":
                var released = Apply(runtime, "head.release", "不要保持头部姿势了");
                Check(!released.Active && released.State == "cancelled", "explicit release cancels head owner");
                Check(!runtime.Confirm(old.Id, old.Revision), "late result cannot revive cancelled task");
                Check(!runtime.CanPublishCompletion(old.Id, old.Revision, output), "cancelled task cannot report success");
                break;
            case "voice_stop_preserves_head":
                string user = "先别说话，保持头部姿势";
                var stop = Parse(Decision("stop_voice", evidence: "先别说话"), user);
                Check(runtime.TryApply(stop, user, old.Id, old.Revision, out var stillHeld, out _), "voice stop accepted");
                Check(stillHeld.Id == old.Id && stillHeld.Revision == old.Revision && stillHeld.State == "holding", "voice scope does not cancel head scope");
                Check(runtime.OutputRevision > output && !runtime.CanPublishCompletion(old.Id, old.Revision, output), "voice stop revokes old speech output");
                break;
            case "failure_has_no_success_receipt":
                Check(runtime.Fail(old.Id, old.Revision, "controller_unavailable"), "execution failure recorded");
                Check(runtime.HeadTask.State == "failed" && !runtime.HeadTask.Active && runtime.HeadTask.Reason == "controller_unavailable", "failure remains factual");
                Check(!runtime.CanPublishCompletion(old.Id, old.Revision, output), "failed task has no success receipt");
                Check(!runtime.Confirm(old.Id, old.Revision), "failure cannot turn into late success");
                break;
            case "new_turn_revokes_old_receipt":
                Check(runtime.BeginTurn() > output, "new turn advances output generation");
                Check(!runtime.CanPublishCompletion(old.Id, old.Revision, output), "old output cannot publish into new turn");
                Check(runtime.HeadTask.Active && runtime.HeadTask.State == "holding", "new turn does not release posture");
                break;
            case "context_reset_releases_head":
                runtime.RevokeOutput();
                Check(runtime.CancelHead("context_reset"), "context reset explicitly cancels head");
                Check(runtime.HeadTask.Reason == "context_reset" && !runtime.HeadTask.Active, "reset reason remains observable");
                Check(!runtime.CanPublishCompletion(old.Id, old.Revision, output), "reset invalidates outstanding result");
                break;
            case "body_yield_releases_head":
                Check(runtime.CancelHead("body_action_yield"), "body controller takes ownership explicitly");
                Check(!runtime.Confirm(old.Id, old.Revision), "yielded owner cannot reclaim bones by callback");
                Check(!runtime.CanPublishCompletion(old.Id, old.Revision, output), "yield invalidates old completion");
                break;
            case "stale_proposal_rejected":
                var newest = Apply(runtime, "head.left", "向左转头");
                var stale = Parse(Decision("head", "head.right", "向右转头"), "向右转头");
                Check(!runtime.TryApply(stale, "向右转头", old.Id, old.Revision, out _, out var reason), "old snapshot proposal rejected");
                Check(reason == "stale_task_snapshot" && runtime.HeadTask.Revision == newest.Revision && runtime.HeadTask.Candidate == "head.left", "stale result cannot overwrite latest intent");
                break;
            case "terminal_snapshot_required":
                runtime.CancelHead("context_reset");
                var terminal = runtime.HeadTask;
                var next = Parse(Decision("head", "head.lower", "低头"), "低头");
                Check(!runtime.TryApply(next, "低头", null, 0, out _, out _), "old empty snapshot cannot ignore terminal task");
                Check(runtime.TryApply(next, "低头", terminal.Id, terminal.Revision, out var renewed, out _), "fresh terminal snapshot may start next task");
                Check(renewed.Id != old.Id && renewed.Revision == 1, "new task after terminal state has new identity");
                break;
            default: throw new Exception("Unknown scenario: " + scenario);
        }
    }

    static void StrictWireCases()
    {
        string valid = Decision("reply", speech: "你好。").ToString(Formatting.None);
        var values = new List<string> { "null", "[]", "{}", "{", valid + " {}", "```json\n" + valid + "\n```",
            valid.Replace("\"version\":2", "\"version\":2,\"version\":2"),
            valid.Replace("\"version\":2", "\"version\":02"), valid.Replace("\"version\":2", "\"version\":\"2\""),
            valid.Replace("\"version\":2", "\"version\":2/*comment*/"), valid.Substring(0, valid.Length - 1) + ",}", valid.Replace('"', '\'') };
        foreach (string speech in new[] { "&lt;say&gt;", "&amp;lt;sing/&amp;gt;", "&#60;tool/&#62;", "&#x3c;tool/&#x3e;", "`code`", "bad\u0001" })
            values.Add(Decision("reply", speech: speech).ToString(Formatting.None));
        foreach (string value in values)
            Check(!FastLoopProtocol.TryParse(value, "你好", out _, out _), "reject malformed/tagged wire: " + value);
        for (int length = 0; length < valid.Length; length++)
            Check(!FastLoopProtocol.TryParse(valid.Substring(0, length), "你好", out _, out _), "partial object must never become executable");
        foreach (string route in new[] { "head", "stop_voice" })
        {
            var invalid = new FastLoopDecision { Version = FastLoopProtocol.CurrentVersion, Route = route, Candidate = route == "head" ? "head.raise" : "none", Evidence = "other turn", Speech = "", Language = "zh" };
            var runtime = new FastLoopRuntime();
            Check(!runtime.TryApply(invalid, "current turn", null, 0, out _, out _), "programmatic decisions cannot bypass evidence validation");
            Check(runtime.HeadTask == null && runtime.OutputRevision == 0, "rejected data does not mutate state");
        }
        var protectedRuntime = Started();
        var original = protectedRuntime.HeadTask;
        var detached = protectedRuntime.HeadTask;
        detached.Id = "forged"; detached.Revision = 999; detached.State = "cancelled";
        Check(protectedRuntime.HeadTask.Id == original.Id && protectedRuntime.HeadTask.Revision == original.Revision && protectedRuntime.HeadTask.State == "holding", "mutating a caller snapshot cannot forge runtime state");
        var empty = new FastLoopRuntime();
        var release = Parse(Decision("head", "head.release", "放松头部"), "放松头部");
        Check(!empty.TryApply(release, "放松头部", null, 0, out _, out _), "release with no current task cannot create a fake completed action");
        Check(empty.HeadTask == null, "failed no-task release leaves runtime empty");
        var disabledCapabilities = FastLoopProtocol.ActorDraftCapabilities(false);
        Check(disabledCapabilities.Properties().All(p => p.Value is JArray values && values.Count == 0),
            "disabled actor compiler advertises no draft execution capabilities");
        var capabilities = FastLoopProtocol.ActorDraftCapabilities(true);
        Check(((JArray)capabilities["headActions"]).Count == 6 &&
            ((JArray)capabilities["waitConditions"]).Count == 1 &&
            (string)capabilities["waitConditions"][0] == "user_count_three",
            "enabled draft capabilities remain bounded to actual heads and count condition");
        Check(!((JArray)capabilities["waitActions"]).Values<string>().Contains("head.release"),
            "waiting compiler does not advertise unsupported delayed release");
        var draftProposal = Parse(Decision("reply", speech: "等你从一数到三之后，我再抬头。"), "请等待");
        var draftRuntime = new FastLoopRuntime();
        draftRuntime.TryApply(draftProposal, "请等待", null, 0, out _, out _);
        Check(draftRuntime.HeadTask == null, "reply draft itself never executes an action before actor compilation");
        ControlWireCases();
    }

    static void ControlWireCases()
    {
        const string user = "不用跟着我了，把刚才录的片段唱给我听。";
        var compound = Decision("recording_singing", control: "stop_follow", target: "follow-task:3:9", controlEvidence: "不用跟着我了");
        var parsed = Parse(compound, user);
        Check(parsed.TaskControl == "stop_follow" && parsed.ControlTargetId == "follow-task:3:9" &&
            parsed.Route == "recording_singing", "task control and later recording goal survive in one proposal");
        // The parser validates provenance, not whether this ID still owns a controller.
        // Actual active-ID matching and cancellation are runtime integration checks.
        Check(FastLoopProtocol.Validate(parsed, user, out _), "wire validation grants no assertion of controller acceptance");
        foreach (string field in new[] { "task_control", "control_target_id", "control_evidence" })
        {
            var missing = (JObject)compound.DeepClone(); missing.Remove(field);
            Check(!FastLoopProtocol.TryParse(missing.ToString(), user, out _, out _), "v2 requires " + field);
            var nil = (JObject)compound.DeepClone(); nil[field] = JValue.CreateNull();
            Check(!FastLoopProtocol.TryParse(nil.ToString(), user, out _, out _), "null field rejects " + field);
        }
        foreach (var change in new[] {
            new JProperty("task_control", "pause_follow"), new JProperty("task_control", "none"),
            new JProperty("control_target_id", ""), new JProperty("control_target_id", "old task\nforged"),
            new JProperty("control_target_id", new string('a',129)), new JProperty("control_evidence", ""),
            new JProperty("control_evidence", "a previous user's stop"), new JProperty("control_evidence", new string('a',301)),
            new JProperty("speech", "我已经唱好了"), new JProperty("candidate", "head.raise"),
            new JProperty("evidence", "不用跟着我了") })
        {
            var bad = (JObject)compound.DeepClone(); bad[change.Name] = change.Value.DeepClone();
            Check(!FastLoopProtocol.TryParse(bad.ToString(), user, out _, out _), "reject inconsistent control/recording field " + change.Name);
        }
        foreach (int version in new[] { 0, 1, 3 })
        {
            var old = (JObject)compound.DeepClone(); old["version"] = version;
            Check(!FastLoopProtocol.TryParse(old.ToString(), user, out _, out _), "reject non-v2 wire " + version);
        }
        var noControl = Decision("reply", speech: "我继续跟着你。");
        noControl["control_evidence"] = "不用跟着我了";
        Check(!FastLoopProtocol.TryParse(noControl.ToString(), user, out _, out _), "no-control must not carry a hidden stop evidence");
        var defaults = new FastLoopDecision { Route = "reply", Candidate = "none", Evidence = "", Speech = "你好", Language = "zh" };
        Check(FastLoopProtocol.Validate(defaults, "你好", out _) && defaults.TaskControl == "none" && defaults.ControlTargetId == "",
            "existing in-memory reply constructors default to explicit v2 no-control");
        var schema = (JArray)FastLoopProtocol.ResponseFormat()["json_schema"]["schema"]["anyOf"];
        Check(schema.Count == 15 && schema.Cast<JObject>().All(branch => ((JArray)branch["required"]).Count == 9 &&
            (int)branch["properties"]["version"]["enum"][0] == 2 && !(bool)branch["additionalProperties"]),
            "each route/control combination is a complete strict nine-field v2 branch");
        string valid = compound.ToString(Formatting.None);
        for (int length = 0; length < valid.Length; length++)
            Check(!FastLoopProtocol.TryParse(valid.Substring(0, length), user, out _, out _), "partial compound control never becomes executable");
    }

    static JObject Context(JObject supplied)
    {
        var context = new JObject {
            ["headTask"] = null, ["headAvailable"] = true,
            ["headAngles"] = new JObject { ["yaw"] = 0, ["pitch"] = 0 },
            ["legacyGazeActive"] = false, ["bodyBusy"] = false, ["eyesOpen"] = true,
            ["recentDialogue"] = new JArray(),
            ["roomTask"] = new JObject { ["connected"] = false, ["activeTaskId"] = "", ["followActive"] = false, ["taskMode"] = "none" },
            ["scope"] = "Bounded facts. Stop control must match the current task identity; an absent capability is not execution permission."
        };
        if (supplied != null) context.Merge(supplied, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
        if (context["headTask"] is JObject task)
        {
            task["id"] = "fixture-head-task";
            task["revision"] = 1;
            task["reason"] = "confirmed";
        }
        return context;
    }

    static void Export(string path, string root, string defaultLanguage, List<JObject> fixtures)
    {
        string prompts = Path.Combine(root, "Assets", "AIChatTookit", "Prompts");
        string persona = File.ReadAllText(Path.Combine(prompts, "persona.txt"));
        string languages = File.ReadAllText(Path.Combine(prompts, "language.txt"));
        string system = FastLoopPrompt.System(persona, languages, defaultLanguage);
        var cases = new JArray();
        foreach (var item in fixtures.Where(item => (string)item["kind"] == "semantic"))
        {
            JObject context = Context(item["context"] as JObject);
            cases.Add(new JObject { ["id"] = item["id"], ["group"] = item["group"] ?? new JValue("development"), ["user"] = item["user"], ["context"] = context,
                ["messages"] = new JArray(new JObject { ["role"] = "system", ["content"] = system },
                    new JObject { ["role"] = "user", ["content"] = FastLoopPrompt.User(context.ToString(Formatting.None), (string)item["user"]) }),
                ["expected"] = item["expected"].DeepClone() });
        }
        var export = new JObject { ["contract"] = FastLoopProtocol.Contract, ["response_format"] = FastLoopProtocol.ResponseFormat(),
            ["system"] = system, ["cases"] = cases, ["default_language"] = defaultLanguage,
            ["note"] = "Generated by compiled production FastLoopProtocol and FastLoopPrompt; fixture context is synthetic, no Unity execution occurs." };
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, export.ToString(Formatting.Indented), new UTF8Encoding(false));
        Console.WriteLine("Exported production prompt/schema and " + cases.Count + " semantic evaluation requests: " + path);
    }

    static int EvaluateModel(string path, List<JObject> fixtures)
    {
        // Pin evaluation to the requests exported before a run. Adding held-out
        // cases while a prior run is in flight must not change that run's cohort.
        string cohortPath = Path.Combine(Path.GetDirectoryName(path), "production-protocol.json");
        var cohort = File.Exists(cohortPath)
            ? JObject.Parse(File.ReadAllText(cohortPath))["cases"].Children<JObject>().ToList()
            : fixtures.Where(item => (string)item["kind"] == "semantic").ToList();
        var expected = cohort.ToDictionary(item => (string)item["id"]);
        var seen = new HashSet<string>();
        var correctIds = new HashSet<string>();
        var rows = new JArray();
        int matched = 0, routeMatched = 0, controlMatched = 0, valid = 0, transport = 0;
        var latencies = new List<double>();
        foreach (string line in File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var result = JObject.Parse(line);
            string id = (string)result["id"];
            if (!expected.TryGetValue(id, out var item) || !seen.Add(id)) throw new Exception("Unknown/duplicate model result: " + id);
            string error = (string)result["error"];
            if (!string.IsNullOrEmpty(error)) { transport++; Console.WriteLine("MODEL FAIL " + id + " transport=" + error); rows.Add(new JObject { ["id"] = id, ["correct"] = false, ["error"] = error }); continue; }
            if (result["elapsedMs"] != null) latencies.Add((double)result["elapsedMs"]);
            FastLoopDecision decision = null;
            bool parsed = FastLoopProtocol.TryReadResponse((string)result["envelope"], (string)item["user"],
                out decision, out string finishReason, out string reason);
            if (parsed) valid++;
            bool routeCorrect = parsed && decision.Route == (string)item["expected"]["route"] && decision.Candidate == (string)item["expected"]["candidate"];
            bool controlCorrect = parsed && decision.TaskControl == ((string)item["expected"]["task_control"] ?? "none") &&
                decision.ControlTargetId == ((string)item["expected"]["control_target_id"] ?? "");
            bool correct = routeCorrect && controlCorrect;
            if (routeCorrect) routeMatched++;
            if (controlCorrect) controlMatched++;
            if (correct) { matched++; correctIds.Add(id); }
            rows.Add(new JObject { ["id"] = id, ["group"] = item["group"] ?? "development", ["protocolValid"] = parsed,
                ["correct"] = correct, ["routeCandidateCorrect"] = routeCorrect, ["controlCorrect"] = controlCorrect,
                ["route"] = decision?.Route, ["candidate"] = decision?.Candidate,
                ["taskControl"] = decision?.TaskControl, ["controlTargetId"] = decision?.ControlTargetId,
                ["controlEvidence"] = decision?.ControlEvidence, ["speech"] = decision?.Speech,
                ["reason"] = reason, ["finishReason"] = finishReason, ["elapsedMs"] = result["elapsedMs"] });
            Console.WriteLine("MODEL " + (correct ? "PASS " : "FAIL ") + id + " " + (parsed ? decision.TaskControl + "/" + decision.Route + "/" + decision.Candidate : "protocol=" + reason));
        }
        latencies.Sort();
        double median = latencies.Count == 0 ? 0 : latencies[(latencies.Count - 1) / 2];
        double p95 = latencies.Count == 0 ? 0 : latencies[(int)Math.Ceiling(latencies.Count * .95) - 1];
        var report = new JObject {
            ["observed"] = seen.Count, ["expected"] = expected.Count, ["validProtocol"] = valid,
            ["correctRouteCandidate"] = routeMatched, ["correctControlAndTarget"] = controlMatched,
            ["correctAllDimensions"] = matched, ["transportErrors"] = transport,
            ["p50Milliseconds"] = median, ["p95Milliseconds"] = p95,
            ["failedCaseIds"] = new JArray(expected.Keys.Where(id => !correctIds.Contains(id))),
            ["groups"] = new JArray(cohort.GroupBy(item => (string)item["group"] ?? "development").Select(group =>
                new JObject { ["name"] = group.Key, ["correct"] = group.Count(item => correctIds.Contains((string)item["id"])),
                    ["total"] = group.Count() })),
            ["expectationSource"] = cohortPath,
            ["scope"] = "Bounded route/candidate/control-target and strict production protocol evaluation. Evidence is checked for provenance, not semantic truth. Reply wording still needs manual review; request duration is not first-audio latency. No physical execution."
        };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path), "model-summary.json"), report.ToString(), new UTF8Encoding(false));
        File.WriteAllLines(Path.Combine(Path.GetDirectoryName(path), "parsed-results.jsonl"), rows.Select(row => row.ToString(Formatting.None)), new UTF8Encoding(false));
        Console.WriteLine("MODEL SUMMARY: observed=" + seen.Count + "/" + expected.Count + ", valid_protocol=" + valid + ", correct_all_dimensions=" + matched + ", transport_errors=" + transport + ", p50_ms=" + median.ToString("F1") + ", p95_ms=" + p95.ToString("F1"));
        foreach (var group in cohort.GroupBy(item => (string)item["group"] ?? "development"))
            Console.WriteLine("MODEL GROUP " + group.Key + ": correct=" + group.Count(item => correctIds.Contains((string)item["id"])) + "/" + group.Count() + ", observed=" + group.Count(item => seen.Contains((string)item["id"])));
        Console.WriteLine("Model expectation source: " + (File.Exists(cohortPath) ? cohortPath : "current fixtures"));
        Console.WriteLine("This model check measures route/candidate/control-target expectations and protocol validity; it does not prove controller cancellation, singing execution or role dialogue quality.");
        return seen.Count == expected.Count && matched == expected.Count ? 0 : 1;
    }

    // Mirrors the production ChatQW envelope acceptance rules; content is then
    // checked by the actual production protocol parser, not a mock classifier.
    static bool EnvelopeContent(string envelope, out string raw, out string reason)
    {
        raw = null;
        reason = "invalid_envelope";
        if (string.IsNullOrWhiteSpace(envelope) || envelope.Length > 65536) return false;
        try
        {
            var root = JObject.Parse(envelope, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (!(root["choices"] is JArray choices) || choices.Count != 1 || !(choices[0] is JObject choice) || !(choice["message"] is JObject message)) return false;
            if (choice["finish_reason"]?.Type != JTokenType.String || (string)choice["finish_reason"] != "stop")
            { reason = "finish_not_stop"; return false; }
            if (message["content"]?.Type != JTokenType.String) { reason = "missing_message_content"; return false; }
            if (message["role"] != null && (message["role"].Type != JTokenType.String || (string)message["role"] != "assistant"))
            { reason = "unexpected_message_role"; return false; }
            var calls = message["tool_calls"];
            var function = message["function_call"];
            var refusal = message["refusal"];
            if ((calls != null && calls.Type != JTokenType.Null && !(calls is JArray empty && empty.Count == 0)) ||
                (function != null && function.Type != JTokenType.Null) ||
                (refusal != null && refusal.Type != JTokenType.Null && !(refusal.Type == JTokenType.String && (string)refusal == "")))
            { reason = "unexpected_message_channel"; return false; }
            raw = (string)message["content"];
            reason = "";
            return true;
        }
        catch (JsonException) { return false; }
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new Exception("Expected cases.jsonl path");
            var fixtures = File.ReadLines(args[0]).Where(line => !string.IsNullOrWhiteSpace(line)).Select(JObject.Parse).ToList();
            Check(fixtures.Count >= 48, "existing 48 named replay/evaluation cases retained");
            Check(fixtures.Select(item => (string)item["id"]).Distinct().Count() == fixtures.Count, "case identifiers unique");
            foreach (var item in fixtures)
            {
                switch ((string)item["kind"])
                {
                    case "semantic":
                        var supplied = (JObject)item["expected"].DeepClone(); supplied.AddFirst(new JProperty("version", FastLoopProtocol.CurrentVersion));
                        Parse(supplied, (string)item["user"]); // Tests fixture/protocol compatibility, not model understanding.
                        break;
                    case "invalid":
                        Check(!FastLoopProtocol.TryParse((string)item["raw"], (string)item["user"], out _, out _), (string)item["id"] + " should reject illegal data");
                        break;
                    case "runtime": RuntimeCase((string)item["scenario"]); break;
                    default: throw new Exception("Unknown fixture kind");
                }
                Console.WriteLine("REPLAY PASS " + item["id"] + " (" + item["kind"] + ")");
            }
            StrictWireCases();
            Console.WriteLine("REPLAY PASS: " + fixtures.Count + " fixtures, " + checks + " deterministic protocol/runtime assertions. Supplied decisions are fixtures, not model accuracy measurements.");
            string export = Option(args, "--export-protocol");
            if (export != null) Export(export, Option(args, "--project-root") ?? Directory.GetCurrentDirectory(), Option(args, "--default-language", "ja"), fixtures);
            string model = Option(args, "--evaluate-model");
            return model == null ? 0 : EvaluateModel(model, fixtures);
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); return 1; }
    }
}
