#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Exports real formal-response requests over public synthetic material. Does not
/// send HTTP, synthesize speech or play audio. Validation consumes the production
/// parser's action projection; private or quoted examples never supply an action.
/// </summary>
public static class SingingFormalOutputRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const RoleOutputBoundaryPolicy Policy = RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions;
    private static readonly Regex Tags = new Regex(@"<(?<name>[a-z_]+)\b(?<attrs>[^>]*)/>", RegexOptions.IgnoreCase);

    public static JObject ExportPublicFormalCasesOrThrow(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var cases = new JArray();
        string[] ids = { "ready_clip_initial_request", "goal_required_correction", "approved_goal_perform", "comment_only_no_singing" };
        foreach (string id in ids)
        using (var fixture = new ExistingFixture())
        {
            string user = id == "comment_only_no_singing"
                ? "刚才第一段“public phrase alpha”是我自己哼的。只评价这段已有的转写和音高信息，不要回唱，也不要更改录音或歌唱权限。"
                : "请把录音第一段“public phrase alpha”按当前范围唱给我听，只唱这一段，不要第二段。";
            fixture.Begin(user);
            JObject failure = null;
            string correction = "", previous = "";
            if (id == "goal_required_correction")
            {
                previous = "<lang code=\"zh\"/><speech mode=\"after_action\"/><say>我准备唱第一段。</say><sing refs=\"" + fixture.First + "\"/>";
                object[] parse = { previous };
                object sing = Call(fixture.Chat, "ExtractHumBackTag", parse);
                object[] resolve = { sing, null };
                Require(!(bool)Call(fixture.Chat, "TryResolveUnifiedSingDetailed", resolve) && resolve[1] != null,
                    "The missing-goal case did not reach the real structured failure branch.");
                failure = JObject.FromObject(resolve[1]);
                Require((string)failure["code"] == "goal_required", "Unexpected missing-goal rejection: " + failure);
                correction = (string)Call(null, "BuildToolCorrectionFrame", "sing", (string)failure["code"],
                    (string)failure["message"], "singing", (string)failure["nextStep"]);
            }
            if (id == "approved_goal_perform")
            {
                fixture.ProposeFirst();
                // Reuse the existing production comparator and approval commit.
                // The test injects this prior review verdict, not a fabricated JSON status.
                typeof(AgentSingingGoalRegression).GetMethod("ApproveIndependentTarget", Flags)
                    .Invoke(null, new[] { fixture.Value, null });
                Require((string)JObject.FromObject(Get(fixture.Chat, "m_SingingGoal"))["review"] == "approved",
                    "The approved formal case did not create an approved runtime goal.");
                // The prior role proposal precedes the later review result. No new
                // user message is invented when that asynchronous result arrives.
                var registered = JObject.FromObject(Get(fixture.Chat, "m_SingingGoal"));
                previous = "<silent/><sing_goal origin=\"user_request\" refs=\"" + (string)registered["refs"] +
                    "\" range=\"current\" revisions=\"" + (string)registered["revisions"] +
                    "\" intent=\"perform\" completion=\"playback\" expected=\"" + (string)registered["expected"] +
                    "\" feedback=\"none\"/>";
            }

            var model = fixture.Host.AddComponent<ChatQW>();
            model.m_Backend = ChatQW.BackendType.Local;
            model.m_LogRequestStats = false;
            ((ChatSetting)Get(fixture.Chat, "m_ChatSettings")).m_ChatModel = model;
            var behavior = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/AIChatTookit/Prompts/behavior.txt");
            var singing = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/AIChatTookit/Prompts/Skills/singing.txt");
            Require(behavior != null && singing != null, "Production behavior and independent singing assets are required.");
            typeof(LLM).GetField("m_PromptFiles", Flags).SetValue(model, new[] { behavior });
            typeof(LLM).GetField("m_SkillFiles", Flags).SetValue(model, new[] { singing });
            Require(model.SetActiveSkills(new[] { "singing" }), "Production singing skill did not load.");
            model.m_DataList.Clear();
            model.m_DataList.Add(new LLM.SendData("system", model.BuildSystemPrompt()));
            model.m_DataList.Add(new LLM.SendData("user", user));
            if (previous.Length > 0) model.m_DataList.Add(new LLM.SendData("assistant", previous));

            string frame = (string)Call(fixture.Chat, "BuildSingingExecutionSnapshot") +
                (string)Call(fixture.Chat, "BuildSingingMaterialFacts", fixture.Sense) + correction;
            bool isFeedback = previous.Length > 0;
            string feedback = null;
            if (isFeedback)
            {
                Call(fixture.Chat, "RecordWorkResponse", previous);
                feedback = (string)Call(fixture.Chat, "BuildWorkContinuationContext") + frame +
                    (string)Call(fixture.Chat, "BuildFormalWorkStateObservation");
            }
            string transient = (string)typeof(ChatQW).GetMethod("AddSpeechOutputContract", Flags)
                .Invoke(null, new object[] { isFeedback ? "" : frame });
            // Match PostSpeechFeedbackStream: the new result occurred AFTER the
            // old assistant action. BuildRequestJson alone would move it before
            // the old user and accidentally ask the server to continue that assistant.
            string payload = isFeedback
                ? (string)typeof(ChatQW).GetMethod("BuildRequestJsonForMessagesWithFeedback", Flags)
                    .Invoke(model, new object[] { model.m_DataList, true, transient, feedback })
                : (string)typeof(ChatQW).GetMethod("BuildRequestJson", Flags)
                    .Invoke(model, new object[] { true, transient });
            var wire = JObject.Parse(payload);
            string effective = wire["messages"].ToString();
            Require((string)wire["messages"].Last["role"] == (isFeedback ? "system" : "user"),
                "Formal fixture used the wrong production new-request/feedback message ending.");
            if (isFeedback) Require((string)wire["messages"].Last["content"] == feedback,
                "The actual latest execution feedback was not serialized after the old response.");
            Require(effective.Contains("[已加载技能: singing]") && effective.Contains("## 当前事项与独立核对") &&
                effective.Contains(fixture.First) && effective.Contains("source=confirmed_user") &&
                effective.Contains("playback=ready") && effective.Contains("revision="),
                "The formal request omitted production skill or usable material facts.");
            string file = "formal-" + id + ".request.json";
            File.WriteAllText(Path.Combine(outputDirectory, file), payload, new UTF8Encoding(false));
            object goal = Get(fixture.Chat, "m_SingingGoal");
            cases.Add(new JObject {
                ["id"] = id, ["request_file"] = file, ["request_sha256"] = Hash(payload), ["request"] = wire,
                ["latest_user"] = user, ["frame"] = frame, ["execution_feedback"] = feedback,
                ["request_builder"] = isFeedback ? "ChatQW.BuildRequestJsonForMessagesWithFeedback -> BuildStageRequestJsonForMessages(General)" : "ChatQW.BuildRequestJson",
                ["request_kind"] = isFeedback ? "work-feedback" : "formal",
                ["expected"] = new JObject { ["kind"] = id == "comment_only_no_singing" ? "comment_only" :
                    id == "approved_goal_perform" ? "sing" : "register_goal",
                    ["refs"] = fixture.First, ["range"] = "current", ["revisions"] = fixture.FirstRevision.ToString(),
                    ["origin"] = "user_request", ["intent"] = "perform", ["completion"] = "playback" },
                ["actual_goal"] = goal == null ? JValue.CreateNull() : (JToken)Call(null, "SingingGoalObservation", goal),
                ["actual_failure"] = failure,
                ["prior_review_scope"] = id == "approved_goal_perform" ?
                    "Controlled prior semantic approval; exact target checked by production TryMatchReviewedSingingGoalTarget and committed by ApplySingingGoalReview. This case evaluates the subsequent real formal response, not the approval model." : "none",
                ["material_fixture"] = JToken.FromObject(typeof(SenseVoiceSpeechToText).GetField("m_PracticePhrases", Flags).GetValue(fixture.Sense))
            });
        }
        var report = new JObject { ["status"] = "exported", ["cases"] = cases, ["network_requests"] = 0,
            ["scope"] = "Public synthetic user text and synthetic PCM archived/prepared by the existing real production fixture. Actual production behavior, independent singing, current material/goal/failure builders and ChatQW.BuildRequestJson. Ground truth is outside request messages; no image, user recording, private conversation or HTTP submission." };
        File.WriteAllText(Path.Combine(outputDirectory, "singing-formal-public-cases.json"), report.ToString(), new UTF8Encoding(false));
        return report;
    }

    public static JObject ValidatePublicFormalResponses(string casesPath, string responsesPath, string outputDirectory)
    {
        var cases = ReadArray(JToken.Parse(File.ReadAllText(casesPath)), "cases");
        var responses = ReadArray(JToken.Parse(File.ReadAllText(responsesPath)), "responses");
        var results = new JArray();
        foreach (JObject item in cases)
        {
            string id = (string)item["id"];
            var errors = new List<string>();
            var matches = responses.OfType<JObject>().Where(p => (string)p["id"] == id).ToArray();
            JObject envelope = matches.Length == 1 ? matches[0]["envelope"] as JObject : null;
            var choice = envelope?["choices"]?.First;
            string raw = choice?["message"]?["content"]?.Type == JTokenType.String ? (string)choice["message"]["content"] : "";
            if (matches.Length != 1) errors.Add("missing-or-duplicate-response");
            if ((string)choice?["finish_reason"] != "stop" || string.IsNullOrWhiteSpace(raw)) errors.Add("incomplete-or-empty-model-response");
            var parser = RoleOutputChannels.Parse(raw, Policy);
            if (parser.HasInvalidPublicBoundary) errors.Add("public-boundary: " + parser.PublicBoundaryError);
            if (parser.HasInvalidSpeechPhase) errors.Add("speech-phase: " + parser.SpeechPhaseError);
            if (parser.HasInvalidLanguage || parser.HasUndeclaredSpeech || parser.HasMalformedTool) errors.Add("invalid-language-or-malformed-tool");
            foreach (int size in new[] { 1, 7, 31 })
            {
                var streamed = new RoleOutputChannels(boundaryPolicy: Policy);
                for (int offset = 0; offset < raw.Length; offset += size) streamed.Push(raw.Substring(offset, Math.Min(size, raw.Length - offset)));
                streamed.Finish();
                if (streamed.ToExecutableText() != parser.ToExecutableText() || streamed.Speech != parser.Speech ||
                    streamed.HasInvalidPublicBoundary != parser.HasInvalidPublicBoundary || streamed.HasInvalidSpeechPhase != parser.HasInvalidSpeechPhase)
                    errors.Add("stream-split-changed-projection: " + size);
            }
            // Inspect only tokens retained by the actual parser. Never scan raw/thought
            // text to recover a sing that the production boundary did not accept.
            string actions = parser.HasInvalidPublicBoundary || parser.HasInvalidSpeechPhase || parser.HasMalformedTool
                ? "" : ((StringBuilder)typeof(RoleOutputChannels).GetField("actions", Flags).GetValue(parser)).ToString();
            var tags = Tags.Matches(actions).Cast<Match>().ToArray();
            var goals = tags.Where(p => p.Groups["name"].Value.Equals("sing_goal", StringComparison.OrdinalIgnoreCase)).ToArray();
            var sings = tags.Where(p => p.Groups["name"].Value.Equals("sing", StringComparison.OrdinalIgnoreCase)).ToArray();
            var expected = (JObject)item["expected"];
            string kind = (string)expected["kind"];
            foreach (var tag in tags)
            {
                string name = tag.Groups["name"].Value.ToLowerInvariant();
                if (name != "sing" && name != "sing_goal" && (name.StartsWith("singing_") || name.StartsWith("song_") ||
                    name.StartsWith("clip_") || name.StartsWith("practice_") || name == "hum_back" || name == "skill_control"))
                    errors.Add("out-of-scope-singing-operation: " + name);
            }
            if (kind == "comment_only")
            {
                if (sings.Length != 0 || goals.Length != 0 || parser.HasSingingValidationActions) errors.Add("comment-only-became-singing-work");
                if (!parser.HasSpeech) errors.Add("requested-comment-has-no-public-speech");
            }
            else
            {
                if (kind == "register_goal" && goals.Length != 1) errors.Add("explicit-request-did-not-register-exactly-one-goal");
                if (kind == "sing" && (sings.Length != 1 || goals.Length != 0)) errors.Add("approved-goal-did-not-submit-exactly-one-sing");
                if (sings.Length > 1) errors.Add("multiple-sing-requests");
                foreach (var goal in goals) ValidateGoal(goal.Groups["attrs"].Value, expected, errors);
                foreach (var sing in sings) ValidateSing(sing.Value, expected, errors);
            }
            results.Add(new JObject { ["id"] = id, ["passed"] = errors.Count == 0, ["errors"] = new JArray(errors),
                ["raw"] = raw, ["public_speech"] = parser.Speech, ["executable"] = parser.ToExecutableText(),
                ["private_characters"] = parser.PrivateCharacters, ["goal_count"] = goals.Length, ["sing_count"] = sings.Length });
        }
        var result = new JObject { ["passed"] = results.All(p => (bool)p["passed"]), ["cases"] = results,
            ["scope"] = "Real model outputs checked with production ExplicitPublicSayWithActions, streaming splits and executable sing/goal scope. No action recovered from private text. No target semantic approval, audio rendering, playback or subjective sound quality claimed." };
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "singing-formal-response-validation.json"), result.ToString(), new UTF8Encoding(false));
        return result;
    }

    private static void ValidateGoal(string attrs, JObject expected, List<string> errors)
    {
        foreach (string key in new[] { "refs", "range", "revisions", "origin", "intent", "completion" })
        {
            string value = (string)Call(null, "ReadToolAttribute", attrs, key);
            if (string.IsNullOrWhiteSpace(value))
            {
                // These defaults are production protocol behavior, not repaired model output.
                if (key == "range") value = "current";
                else if (key == "origin") value = "user_request";
                else if (key == "intent") value = "perform";
                else if (key == "completion") value = "playback";
                else if (key == "revisions") value = (string)expected[key]; // Bound to current inventory by production.
            }
            if (value != (string)expected[key]) errors.Add("goal-target-mismatch: " + key);
        }
        if (string.IsNullOrWhiteSpace((string)Call(null, "ReadToolAttribute", attrs, "expected"))) errors.Add("goal-missing-success-condition");
        string feedback = (string)Call(null, "ReadToolAttribute", attrs, "feedback");
        if (!string.IsNullOrWhiteSpace(feedback) && feedback != "none") errors.Add("goal-invented-feedback");
    }

    private static void ValidateSing(string token, JObject expected, List<string> errors)
    {
        using (var fixture = new ExistingFixture())
        {
            object[] args = { token };
            object parsed = Call(fixture.Chat, "ExtractHumBackTag", args);
            if (parsed == null) { errors.Add("production-sing-parser-rejected-token"); return; }
            var sing = JObject.FromObject(parsed);
            if (!(bool)sing["UnifiedSing"] || !string.IsNullOrEmpty((string)sing["ValidationError"]) ||
                !string.IsNullOrEmpty((string)sing["SourceValidationError"])) errors.Add("production-sing-parameter-validation-failed");
            if ((string)sing["ClipRefs"] != (string)expected["refs"] || (string)sing["Range"] != (string)expected["range"])
                errors.Add("sing-target-mismatch");
            string attrs = Tags.Match(token).Groups["attrs"].Value;
            if (!string.IsNullOrWhiteSpace((string)Call(null, "ReadToolAttribute", attrs, "start_seconds")) ||
                !string.IsNullOrWhiteSpace((string)Call(null, "ReadToolAttribute", attrs, "end_seconds"))) errors.Add("sing-changed-current-range-to-window");
        }
    }

    // Reuse the existing public PCM fixture instead of maintaining another test-only
    // recording/approval implementation. Its actual methods call production parsers.
    private sealed class ExistingFixture : IDisposable
    {
        public object Value { get; }
        private readonly Type type;
        public ExistingFixture()
        { type = typeof(AgentSingingGoalRegression).GetNestedType("Fixture", BindingFlags.NonPublic); Value = Activator.CreateInstance(type, true); }
        public GameObject Host => (GameObject)type.GetField("host").GetValue(Value);
        public ChatSample Chat => (ChatSample)type.GetField("chat").GetValue(Value);
        public SenseVoiceSpeechToText Sense => (SenseVoiceSpeechToText)type.GetField("sense").GetValue(Value);
        public string First => (string)type.GetField("first").GetValue(Value);
        public int FirstRevision => Sense.DescribePracticePhrases().Single(p => p.ClipRef == First).Revision;
        public void Begin(string user) => type.GetMethod("Begin").Invoke(Value, new object[] { user });
        public void ProposeFirst()
        {
            var method = type.GetMethod("Propose");
            object[] args = method.GetParameters().Select(p => p.DefaultValue).ToArray();
            args[0] = First; args[1] = "current";
            method.Invoke(Value, args);
        }
        public void Dispose() => ((IDisposable)Value).Dispose();
    }
    private static object Call(object target, string name, params object[] args) => typeof(ChatSample).GetMethod(name, Flags).Invoke(target, args);
    private static object Get(object target, string name) => typeof(ChatSample).GetField(name, Flags).GetValue(target);
    private static JArray ReadArray(JToken token, string name) => token as JArray ?? token[name] as JArray ?? throw new InvalidOperationException("Missing " + name + " array.");
    private static string Hash(string value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
#endif
