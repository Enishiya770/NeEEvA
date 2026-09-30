using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class DecisionClosureRegression
{
    private static int checks;
    private static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    private static JObject Role(string route, string speech = "", string evidence = "") => new JObject {
        ["version"] = 2, ["route"] = route, ["user_evidence"] = evidence, ["thought"] = "静かに聞いている。",
        ["speech"] = new JObject { ["text"] = speech, ["language"] = "ja" } };
    public static RecordingSingingDecisionContext Fixture() {
        var ctx = new RecordingSingingDecisionContext { UserText = "把刚才唱的两段按先后唱给我听，不要唱普通说话。", Facts = "没有播放结果" };
        ctx.Materials.Add(new RecordingSingingMaterial { Key = "material_1", ClipRef = "clip:real-a", Revision = "3", PlaybackStatus = "ready", Description = "先唱的第一段" });
        ctx.Materials.Add(new RecordingSingingMaterial { Key = "material_2", ClipRef = "clip:real-b", Revision = "4", PlaybackStatus = "ready", Description = "后唱的第二段" });
        return ctx;
    }
    public static JObject Plan() => new JObject {
        ["decision"] = "perform", ["materials"] = new JArray("material_1", "material_2"), ["range"] = "clean",
        ["intent"] = "repeat", ["expected"] = "按先后唱两段", ["user_evidence"] = "把刚才唱的两段按先后唱给我听",
        ["thought"] = "両方を覚えている。", ["speech"] = new JObject { ["text"] = "", ["language"] = "ja" } };
    public static int RunChecks()
    {
        checks = PttRoleDecisionRegression.RunChecks();
        foreach (var route in new[] { "tools", "recording_singing" }) {
            var raw = Role(route, "これから歌うね。").ToString();
            Check(DecisionCompiler.TryParse(raw, out var d, out _) && d.HasRouteIntent && d.Speech == "", "route draft is withheld");
            Check(d.Route == route && d.Version == 2, "semantic route and schema version retained");
        }
        Check(DecisionCompiler.TryCompile(Role("reply", "こんにちは。").ToString(), out var reply, out _), "ordinary compiled");
        Check(RoleOutputChannels.Parse(reply, RoleOutputBoundaryPolicy.ExplicitPublicSay).Speech == "こんにちは。", "thought not TTS");
        Check(!DecisionCompiler.TryParse(Role("cancel_work").ToString(), out _, out _), "cancellation needs quote");
        Check(DecisionCompiler.TryParse(Role("cancel_work", "またね。", "测试就到此为止，拜拜").ToString(), out var cancelled, out _) && !cancelled.HasExecutionIntent, "cancel reply does not invoke planner");
        var bad = Role("reply", "<sing/>");
        Check(!DecisionCompiler.TryCompile(bad.ToString(), out _, out _), "no tags in public fields");
        bad = Role("unknown"); Check(!DecisionCompiler.TryParse(bad.ToString(), out _, out _), "unknown route rejected");
        bad = Role("reply"); bad["clip"] = "made-up";
        Check(!DecisionCompiler.TryParse(bad.ToString(), out _, out _), "unknown field rejected");
        Check(!DecisionCompiler.TryParse(Role("reply").ToString() + "{}", out _, out _), "trailing object rejected");

        var ctx = Fixture(); var plan = Plan();
        bool Compile(JObject v, out string output) => RecordingSingingDecisionCompiler.TryCompile(v.ToString(Formatting.None), ctx, out output, out _);
        Check(Compile(plan, out var compiled), "recording plan compiles");
        Check(compiled.Contains("refs=\"clip:real-a,clip:real-b\"") && compiled.Contains("revisions=\"3,4\""), "compiler binds actual order/version");
        var channels = RoleOutputChannels.Parse(compiled, RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
        Check(!channels.HasSpeech && channels.HasActions && channels.SpeechDependency == SpeechActionDependency.AfterAction, "action proposal has no unearned public promise");
        ctx.Materials[1].PlaybackStatus = "evidence_only";
        Check(Compile(plan, out var unavailable) && unavailable == RecordingSingingDecisionCompiler.Handoff, "evidence-only goes to preparation instead of playback");
        ctx.Materials[1].HasCleanRange = true;
        Check(Compile(plan, out var prepared) && prepared.Contains("refs=\"clip:real-a,clip:real-b\"") &&
            prepared.Contains("range=\"clean\"") && prepared.Contains("revisions=\"3,4\""),
            "explicit recoverable range retains exact identities and revisions for reviewed preparation");
        Check(!RoleOutputChannels.Parse(prepared).HasSpeech, "range preparation does not make a public playback promise");
        var missingCurrent = (JObject)plan.DeepClone(); missingCurrent["range"] = "current";
        Check(Compile(missingCurrent, out var currentHandoff) && currentHandoff == RecordingSingingDecisionCompiler.Handoff,
            "evidence-only current never silently chooses clean");
        missingCurrent["range"] = "expanded";
        Check(Compile(missingCurrent, out var expandedHandoff) && expandedHandoff == RecordingSingingDecisionCompiler.Handoff,
            "clean evidence does not invent an expanded range");
        ctx.Materials[1].PlaybackStatus = "unavailable";
        Check(Compile(plan, out var unavailableWithRange) && unavailableWithRange == RecordingSingingDecisionCompiler.Handoff,
            "stale range hint cannot revive unavailable material");
        ctx.Materials[1].HasCleanRange = false;
        ctx.Materials[1].PlaybackStatus = "ready";
        foreach (var mutate in new Action<JObject>[] {
            p => p["materials"] = new JArray("clip:invented"),
            p => p["materials"] = new JArray(),
            p => p["revisions"] = "99",
            p => p["user_evidence"] = "模型自述的要求",
            p => p["expected"] = "<sing/>",
            p => p["range"] = "window",
            p => p["speech"]["text"] = "已经唱了",
            p => p["materials"] = new JArray(5),
            p => p["intent"] = "autonomous" }) {
            var changed = (JObject)plan.DeepClone(); mutate(changed);
            Check(!Compile(changed, out _), "invalid material/evidence/authority rejected");
        }
        var handoff = (JObject)plan.DeepClone(); handoff["decision"] = "handoff"; handoff["materials"] = new JArray();
        Check(Compile(handoff, out var marker) && marker == RecordingSingingDecisionCompiler.Handoff, "unsupported operation explicit handoff");
        var ask = (JObject)handoff.DeepClone(); ask["decision"] = "reply"; ask["speech"]["text"] = "どの部分ですか？";
        Check(Compile(ask, out var question) && !RoleOutputChannels.Parse(question).HasActions, "clarification without tool");
        var copy = (JObject)plan.DeepClone(); copy["materials"] = new JArray("material_2", "material_1");
        Check(Compile(copy, out var reverse) && reverse.Contains("refs=\"clip:real-b,clip:real-a\""), "order preserved");
        ctx.UserText = "他说\"唱给我听\"，但我现在也希望你唱。"; copy = Plan(); copy["user_evidence"] = ctx.UserText;
        Check(Compile(copy, out var quotes) && quotes.Contains("evidence='" + ctx.UserText + "'"), "literal evidence quotations preserved");
        ctx.RequestEvidenceTexts = new[] { Fixture().UserText };
        ctx.UserText = "还没有听到。";
        Check(!Compile(plan, out _), "repeat cannot borrow an earlier request as current repeat authorization");
        plan["intent"] = "perform";
        Check(Compile(plan, out var recovered) && recovered.Contains("clip:real-a"), "recovery may ground prior actual request");
        ctx.RequestEvidenceTexts = new[] { "角色自述，未含用户原话" };
        Check(!Compile(plan, out _), "unrelated history cannot ground request");
        ctx.RequestEvidenceTexts = new string[0];
        ctx.UserText = Fixture().UserText;
        Check(!RecordingSingingDecisionCompiler.TryCompile(plan.ToString() + "{}", ctx, out _, out _), "trailing plan rejected");
        var format = RecordingSingingDecisionCompiler.ResponseFormat(ctx);
        Check(format.ToString().Contains("material_1") && !format.ToString().Contains("clip:real-a"), "schema enumerates selectors, no raw IDs");
        Check(!RecordingSingingDecisionCompiler.Prompt(ctx).Contains("<sing_goal"), "model fills semantic table");
        var first = ProviderTimingInterval.Parse("# header\nvllm:request_queue_time_seconds_sum{model_name=\"a\"} 2e-3\nvllm:request_queue_time_seconds_sum{model_name=\"b\"} 0.001\nvllm:request_queue_time_seconds_count 3\nignored 10\n");
        Check(Math.Abs((double)first["vllm:request_queue_time_seconds_sum"] - .003) < .00001, "labeled metric aggregation");
        Check(first.Count == 2 && (double)first["vllm:request_queue_time_seconds_count"] == 3, "unlabeled counters parsed");
        var second = ProviderTimingInterval.Parse("vllm:request_queue_time_seconds_sum 0.008\nvllm:request_queue_time_seconds_count 5");
        var interval = ProviderTimingInterval.Difference(first, second);
        Check((double)interval["delta"]["vllm:request_queue_time_seconds_count"] == 2 && (string)interval["scope"] != "per-request", "concurrency stays interval evidence");
        Check((bool)ProviderTimingInterval.Difference(second, first)["counterReset"], "server counter reset invalidates interval");
        Check(((JObject)ProviderTimingInterval.Difference(null, second)["delta"]).Count == 0, "missing baseline not treated as zero");
        Check(ProviderTimingInterval.IsLocalMetricsEndpoint(new Uri("http://192.168.114.175:8888/v1/chat/completions")), "configured Spark LAN endpoint is observable");
        Check(!ProviderTimingInterval.IsLocalMetricsEndpoint(new Uri("https://api.example.com/v1/chat/completions")), "public provider is not probed for metrics");
        Check(!ProviderTimingInterval.IsLocalMetricsEndpoint(new Uri("http://172.32.0.1:8888")), "non-private address is not sampled");
        var cached = ProviderTimingInterval.Parse("vllm:prompt_tokens_total 1000\nvllm:prompt_tokens_cached_total 400\nvllm:request_success_total{finished_reason=\"stop\"} 1");
        Check(cached["vllm:prompt_tokens_cached_total"].Value<double>() == 400 && cached["vllm:prompt_tokens_total"].Value<double>() == 1000, "actual cached tokens stay distinct from lookup hits");
        return checks;
    }
    public static void Main(string[] args) {
        Console.WriteLine("Decision closure checks passed: " + RunChecks());
        if (args.Length == 2 && args[0] == "--export")
            System.IO.File.WriteAllText(args[1], new JObject {
                ["roleContract"] = DecisionCompiler.FastLaneContract,
                ["roleThoughtGuidance"] = DecisionCompiler.FastLaneThoughtGuidance,
                ["roleResponseFormat"] = DecisionCompiler.FastLaneResponseFormat(),
                ["recordingPrompt"] = RecordingSingingDecisionCompiler.Prompt(Fixture()),
                ["recordingResponseFormat"] = RecordingSingingDecisionCompiler.ResponseFormat(Fixture())
            }.ToString());
        if (args.Length == 2 && args[0] == "--validate")
            foreach (var row in JArray.Parse(System.IO.File.ReadAllText(args[1]))) {
                if ((string)row["kind"] == "role") {
                    if (!DecisionCompiler.TryParse((string)row["raw"], out _, out var failure)) throw new Exception("Model role rejected: " + failure);
                } else if (!RecordingSingingDecisionCompiler.TryCompile((string)row["raw"], Fixture(), out _, out var failure))
                    throw new Exception("Model plan rejected: " + failure);
            }
    }
}
