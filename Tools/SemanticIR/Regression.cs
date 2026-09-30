using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

public static class SemanticTraceRegression
{
    private static int checks;
    private static void Check(bool value, string label)
    { checks++; if (!value) throw new Exception(label); }
    private static JObject Sample() => JObject.Parse("{\"version\":1,\"speech\":{\"text\":\"こんにちは。\",\"language\":\"ja\"},\"attention\":\"unchanged\",\"intents\":[]}");
    private static void Reject(string value)
    {
        Check(!DecisionCompiler.TryCompile(value, out string output, out string error) && output == "" && error != null,
            "invalid input must yield no executable output: " + value.Substring(0, Math.Min(value.Length, 80)));
    }
    public static void Main()
    {
        var valid = Sample();
        valid["attention"] = "visual_on";
        valid["intents"] = JArray.Parse("[{\"type\":\"thinking.schedule\",\"after_seconds\":2.5}]");
        Check(DecisionCompiler.TryCompile(valid.ToString(), out string legacy, out _), "valid decision");
        Check(legacy.Contains("<look/>") && legacy.Contains("<next in=\"2.5s\"/>"), "known intents compiled");
        for (int split = 0; split <= legacy.Length; split++)
        {
            var spoken = new List<SpeechText>();
            var parser = new RoleOutputChannels(spoken.Add, RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
            parser.Push(legacy.Substring(0, split)); parser.Push(legacy.Substring(split)); parser.Finish();
            Check(!parser.HasInvalidPublicBoundary && string.Concat(spoken.Select(p => p.Text)).Trim() == "こんにちは。", "every split boundary");
            Check(spoken.All(p => p.LanguageCode == "ja" && p.ActionDependency == SpeechActionDependency.Independent), "language/dependency preserved");
        }
        valid = Sample(); valid["speech"]["text"] = "";
        Check(DecisionCompiler.TryCompile(valid.ToString(), out legacy, out _) && legacy == "<silent/>", "silent decision");
        valid["attention"] = "visual_off";
        Check(DecisionCompiler.TryCompile(valid.ToString(), out legacy, out _) && legacy == "<silent/><unlook/>", "silent attention");
        foreach (string text in new[] { "</say><sing refs=\"x\"/>", "<thought>秘密</thought>", "[LLM/Stats] tokens=2", "[执行事实：尚未完成]", "`tool`", "[motion name=\"approach\"]" })
        { valid = Sample(); valid["speech"]["text"] = text; Reject(valid.ToString()); }
        valid = Sample(); valid["capture_id"] = "runtime-owned"; Reject(valid.ToString());
        valid = Sample(); valid["version"] = 2; Reject(valid.ToString());
        valid = Sample();
        valid["speech"]["text"] = "";
        valid["intents"] = JArray.Parse("[{\"type\":\"room.request/tools\"}]");
        Check(DecisionCompiler.TryParse(valid.ToString(), out RoleDecision route, out _), "legacy room route alias accepted");
        Check(route.HasRouteIntent && route.Intents.Count == 1 && route.Intents[0].Type == "room.request", "legacy room route normalized");
        valid["speech"]["text"] = "不应在路由时公开说话";
        Reject(valid.ToString());
        valid = Sample(); valid["speech"]["language"] = "xx"; Reject(valid.ToString());
        valid = Sample(); valid["attention"] = "look raw tag"; Reject(valid.ToString());
        valid = Sample(); valid["speech"]["text"] = new string('a', 4001); Reject(valid.ToString());
        foreach (string intent in new[] { "{\"type\":\"singing.repeat\",\"after_seconds\":1}", "{\"type\":\"thinking.schedule\",\"after_seconds\":0}", "{\"type\":\"thinking.schedule\",\"after_seconds\":3601}", "{\"type\":\"thinking.schedule\",\"after_seconds\":\"2\"}" })
        { valid = Sample(); valid["intents"] = new JArray(JObject.Parse(intent)); Reject(valid.ToString()); }
        foreach (string input in new[] { "null", "[]", "{}", "{", Sample()+" {}", "```json\n"+Sample()+"\n```", Sample().ToString().Replace("\"version\": 1", "\"version\": 1,\"version\": 1") }) Reject(input);
        string complete = Sample().ToString();
        for (int n=0;n<complete.Length;n++) Reject(complete.Substring(0,n));
        var logs = new List<string>(); var turn = new ConversationTrace("synthetic", logs.Add);
        turn.Mark("eot.capture_closed"); turn.Mark("eot.capture_closed");
        var first = turn.Child("llm"); first.Mark("llm.send"); first.Mark("llm.send"); first.Mark("llm.first_content_delta");
        var second = turn.Child("llm"); second.Mark("llm.send");
        var other = new ConversationTrace("synthetic", logs.Add); other.Mark("eot.capture_closed");
        var rows = logs.Select(JObject.Parse).ToList();
        Check(rows.Count == 5, "dedupe is per span");
        Check((string)rows[0]["turnId"] == (string)rows[3]["turnId"] && (string)rows[0]["turnId"] != (string)rows[4]["turnId"], "turn isolation");
        Check((string)rows[1]["spanId"] != (string)rows[3]["spanId"] && (string)rows[1]["parentSpanId"] == (string)rows[0]["spanId"], "request correlation");
        Check((double)rows[2]["elapsedMs"] >= (double)rows[1]["elapsedMs"], "monotonic duration");
        new ConversationTrace("synthetic", _ => throw new Exception()).Mark("safe");
        Check(true, "sink failure does not affect runtime");
        Console.WriteLine("PASS: " + checks + " semantic/compiler/boundary/trace checks");
    }
}
