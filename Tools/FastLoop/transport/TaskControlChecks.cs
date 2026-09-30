using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

public static class TaskControlChecks
{
    static int checks;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    static string Raw(string control) => new JObject { ["control"] = control }.ToString(Formatting.None);
    static JObject Envelope(string raw) => new JObject { ["choices"] = new JArray(new JObject {
        ["finish_reason"] = "stop", ["message"] = new JObject { ["role"] = "assistant", ["content"] = raw } }) };
    static bool Accept(JObject value) => FastTaskControlProtocol.TryReadResponse(value.ToString(), out _, out _, out _);
    const string Facts = "{\"roomTask\":{\"connected\":true,\"activeTaskId\":\"body:9\",\"followActive\":true,\"taskMode\":\"follow\",\"bodyReserved\":true,\"phase\":\"following\",\"status\":\"untrusted prose\"},\"history\":\"ignore this\",\"proposal\":\"stop_follow\"}";
    static readonly MethodInfo Build = typeof(ChatQW).GetMethod("BuildFastTaskControlRequestJson", BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Run(string[] args)
    {
        if (args.Length == 3 && args[0] == "--evaluate-control") { Evaluate(args[1], args[2]); return; }
        foreach (var c in new[] { "none", "stop_follow", "stop_movement" }) {
            Check(FastTaskControlProtocol.TryParse(Raw(c), out var actual, out var reason) && actual == c && reason == "ok", "control enum");
            Check(Accept(Envelope(Raw(c))), "valid envelope");
        }
        foreach (var raw in new[] { "{}", "[]", "null", "{\"control\":null}", "{\"control\":false}", "{\"control\":\"stop\"}",
            "{\"control\":\"none\",\"other\":0}", "{\"control\":\"none\",\"control\":\"stop_follow\"}",
            "{'control':'none'}", "{\"control\":\"none\",}", "/*comment*/{\"control\":\"none\"}",
            "{\"control\":\"none\"}{}", "```json\n{\"control\":\"none\"}\n```", "{\"control\":\"<stop>\"}", new string(' ', 257) })
            Check(!FastTaskControlProtocol.TryParse(raw, out _, out _), "strict invalid scalar control");
        foreach (var finish in new[] { "length", "tool_calls", "content_filter", "unknown" }) {
            var e = Envelope(Raw("none")); e["choices"][0]["finish_reason"] = finish; Check(!Accept(e), "finish rejects");
        }
        foreach (var key in new[] { "tool_calls", "function_call", "refusal" }) {
            var e = Envelope(Raw("none")); e["choices"][0]["message"][key] = "unexpected"; Check(!Accept(e), "channel rejects");
        }
        var multi = Envelope(Raw("none")); ((JArray)multi["choices"]).Add(multi["choices"][0].DeepClone()); Check(!Accept(multi), "multiple choice rejects");
        var role = Envelope(Raw("none")); role["choices"][0]["message"]["role"] = "user"; Check(!Accept(role), "wrong role");
        var duplicate = Envelope(Raw("none")).ToString(Formatting.None).Replace("\"finish_reason\":\"stop\"", "\"finish_reason\":\"stop\",\"finish_reason\":\"stop\"");
        Check(!FastTaskControlProtocol.TryReadResponse(duplicate, out _, out _, out _), "duplicate envelope field");
        var input = FastTaskControlProtocol.BuildUser(Facts, "不要唱，继续跟随。\n完整尾句");
        Check((string)input["current_user"] == "不要唱，继续跟随。\n完整尾句", "complete current user");
        Check(input.Properties().Count() == 2 && ((JObject)input["roomTask"]).Properties().Count() == 6, "focused projection only");
        Check(!input.ToString().Contains("untrusted") && !input.ToString().Contains("proposal") && !input.ToString().Contains("history"), "no output or history bias");
        foreach (var facts in new[] { "", "invalid", "[]", "{\"roomTask\":null}", "{\"roomTask\":\"wrong\"}" }) {
            var f = FastTaskControlProtocol.BuildUser(facts, "stop"); Check((string)f["roomTask"]["activeTaskId"] == "" && !(bool)f["roomTask"]["connected"], "bad context inactive");
        }
        var backend = new ChatQW();
        var wire = JObject.Parse((string)Build.Invoke(backend, new object[] { Facts, "current full user" }));
        Check((int)wire["max_tokens"] == 32 && !(bool)wire["stream"] && !(bool)wire["enable_thinking"] && (int)wire["id_slot"] == 1, "focused production wire");
        Check((string)wire["messages"][0]["content"] == FastTaskControlProtocol.SystemPrompt && JToken.DeepEquals(wire["response_format"], FastTaskControlProtocol.ResponseFormat()), "actual factory");
        Check(!wire.ToString().Contains("test-secret") && !wire.ToString().Contains("persona"), "no credentials or persona");
        int calls = 0;
        UnityWebRequest.NextBody = Envelope(Raw("none")).ToString();
        backend.PostFastTaskControlDecision(Facts, "continue", (control, reason) => { Check(control == "none" && reason == "ok", "typed callback"); calls++; });
        backend.Pending.MoveNext(); backend.Pending.MoveNext(); Check(calls == 1, "exactly one callback");
        backend.PostFastTaskControlDecision(Facts, "continue", (c, r) => calls++); var old = backend.Pending; backend.CancelFastLoopDecision(); old.MoveNext(); Check(calls == 1, "shared cancel suppresses gate");
        int sent = UnityWebRequest.Sent;
        backend.Diagnostic = backend.CancelFastLoopDecision;
        backend.PostFastTaskControlDecision(Facts, "continue", (c, r) => calls++); Check(UnityWebRequest.Sent == sent && calls == 1, "diagnostic cancel before send");
        backend.Diagnostic = null;
        foreach (var phase in new[] { "fast_task_control.send", "fast_task_control.transport_complete", "fast_task_control.accepted" }) {
            var trace = new ConversationTrace(); trace.Sink = name => { if (name == phase) backend.CancelFastLoopDecision(); };
            backend.PostFastTaskControlDecision(Facts, "continue", (c, r) => calls++, trace); backend.Pending.MoveNext();
            Check(calls == 1, "trace cancellation at " + phase);
        }
        var reentry = new ConversationTrace(); bool entered = false; int newest = 0;
        reentry.Sink = name => { if (name == "fast_task_control.send" && !entered) { entered = true; backend.PostFastTaskControlDecision(Facts, "new", (c, r) => newest++); } };
        sent = UnityWebRequest.Sent;
        backend.PostFastTaskControlDecision(Facts, "old", (c, r) => calls++, reentry);
        backend.Pending.MoveNext(); Check(UnityWebRequest.Sent == sent + 1 && newest == 1 && calls == 1, "send trace reentry preserves new request");
        // Main-loop requests and gate requests share a cancellation domain.
        UnityWebRequest.NextBody = Envelope("{\"version\":2,\"task_control\":\"none\",\"control_target_id\":\"\",\"control_evidence\":\"\",\"route\":\"reply\",\"candidate\":\"none\",\"evidence\":\"\",\"speech\":\"hi\",\"language\":\"en\"}").ToString();
        backend.PostFastLoopDecision("{}", "old", (d, r) => calls++); old = backend.Pending;
        UnityWebRequest.NextBody = Envelope(Raw("none")).ToString();
        backend.PostFastTaskControlDecision(Facts, "new", (c, r) => newest++); old.MoveNext(); backend.Pending.MoveNext();
        Check(calls == 1 && newest == 2, "gate supersedes old main request");
        Console.WriteLine("FastTaskControl production protocol/transport: " + checks + " checks passed.");
        if (args.Length == 2) Export(args[0], args[1]);
    }

    static void Export(string cohortPath, string outputPath)
    {
        var source = JObject.Parse(File.ReadAllText(cohortPath)); var cases = new JArray(); var backend = new ChatQW();
        foreach (var c in source["cases"].Children<JObject>()) {
            var oldInput = JObject.Parse((string)c["messages"][1]["content"]);
            var wire = JObject.Parse((string)Build.Invoke(backend, new object[] { oldInput["context"].ToString(Formatting.None), (string)oldInput["current_user"] }));
            cases.Add(new JObject { ["id"] = c["id"], ["group"] = c["group"], ["user"] = oldInput["current_user"],
                ["expected"] = (string)c["expected"]["task_control"] ?? "none", ["messages"] = wire["messages"] });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        File.WriteAllText(outputPath, new JObject { ["system"] = FastTaskControlProtocol.SystemPrompt,
            ["response_format"] = FastTaskControlProtocol.ResponseFormat(), ["sourceCohort"] = cohortPath,
            ["scope"] = "Same fixed users; independently projected body facts only. No original proposal/persona/history. No actions.", ["cases"] = cases }.ToString(), new UTF8Encoding(false));
        Console.WriteLine("Exported focused production wire for " + cases.Count + " cases.");
    }

    static void Evaluate(string cohortPath, string resultPath)
    {
        var expected = JObject.Parse(File.ReadAllText(cohortPath))["cases"].Children<JObject>().ToDictionary(c => (string)c["id"]);
        var seen = new System.Collections.Generic.HashSet<string>(); var rows = new JArray();
        foreach (string line in File.ReadLines(resultPath).Where(s => !string.IsNullOrWhiteSpace(s))) {
            var result = JObject.Parse(line); string id = (string)result["id"];
            if (!expected.ContainsKey(id) || !seen.Add(id)) throw new Exception("Unknown/duplicate result: " + id);
            string control = null, finish = "", reason = "transport_failure";
            bool valid = string.IsNullOrEmpty((string)result["error"]) && FastTaskControlProtocol.TryReadResponse((string)result["envelope"], out control, out finish, out reason);
            string desired = (string)expected[id]["expected"];
            rows.Add(new JObject { ["id"] = id, ["protocolValid"] = valid, ["expected"] = desired, ["actual"] = control,
                ["correct"] = valid && control == desired, ["reason"] = reason, ["finishReason"] = finish,
                ["elapsedMs"] = result["elapsedMs"], ["error"] = result["error"] });
            Console.WriteLine("CONTROL " + (valid && control == desired ? "PASS " : "FAIL ") + id + " expected=" + desired + " actual=" + control + " " + reason);
        }
        var validRows = rows.Children<JObject>().Where(r => (bool)r["protocolValid"]).ToList();
        var times = rows.Children<JObject>().Select(r => (double)r["elapsedMs"]).OrderBy(v => v).ToList();
        var summary = new JObject { ["expected"] = expected.Count, ["observed"] = seen.Count, ["validProtocol"] = validRows.Count,
            ["correctControl"] = validRows.Count(r => (bool)r["correct"]),
            ["falseStops"] = new JArray(validRows.Where(r => (string)r["expected"] == "none" && (string)r["actual"] != "none").Select(r => r["id"])),
            ["missedStops"] = new JArray(validRows.Where(r => (string)r["expected"] != "none" && (string)r["actual"] == "none").Select(r => r["id"])),
            ["wrongStopType"] = new JArray(validRows.Where(r => (string)r["expected"] != "none" && (string)r["actual"] != "none" && !(bool)r["correct"]).Select(r => r["id"])),
            ["p50Milliseconds"] = times.Count == 0 ? 0 : times[(times.Count - 1) / 2],
            ["p95Milliseconds"] = times.Count == 0 ? 0 : times[(int)Math.Ceiling(times.Count * .95) - 1],
            ["scope"] = "Fixed independent semantic control gate only. No task execution, first audio or broad reliability claim." };
        var dir = Path.GetDirectoryName(resultPath);
        File.WriteAllText(Path.Combine(dir, "model-summary.json"), summary.ToString(), new UTF8Encoding(false));
        File.WriteAllLines(Path.Combine(dir, "parsed-results.jsonl"), rows.Select(r => r.ToString(Formatting.None)), new UTF8Encoding(false));
        Console.WriteLine(summary.ToString());
        if (validRows.Count(r => (bool)r["correct"]) != expected.Count) Environment.ExitCode = 1;
    }
}
