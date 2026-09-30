#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class AgentSingingGoalRegression
{
    private const string RangeSongUser = "好，接下来我要唱一首歌，呃，唱一首日语歌吧。。 なんてことはなたは私だけのモナリザもう遠くに出会てたから。 好，接下来你看看你能不能把我唱的这一段回唱给我听。";
    private const string RangeAsk = "可以把刚才唱的那一段唱给我听吗？";
    private const string RangeTrim = "呃，虽然你唱出来了，但是你把我说话的部分也复读出来了，看一下能不能把我说话的部分给去掉，只唱我唱的有旋律的那部分。";
    private const string RangeClarify = "我是说刚才你把我说话的那一部分录音也复读出来了，我希望你只唱有旋律的那部分，就是我唱歌的那部分把它唱出来。";

    // Immutable session words/range measurements with controlled, non-singing
    // test PCM. This tests preserved facts and identity, never acoustic quality.
    private static RecordingSingingDecisionContext RangeSession(Fixture f, int capture, out string song)
    {
        f.Begin(RangeSongUser);
        f.StageCompletedEvidence(f.user, "", .464f, 23.66f, 16.87f, 21.97f);
        var e = typeof(SenseVoiceSpeechToText).GetField("m_LastSingingEvidence", Flags).GetValue(f.sense);
        var type = e.GetType();
        type.GetField("CleanStartSeconds").SetValue(e, 6.33f);
        type.GetField("HeadExtraText").SetValue(e, "好，接下来我要唱一首歌，呃，唱一首日语歌吧。");
        type.GetField("TailExtraText").SetValue(e, "好，接下来你看看你能不能把我唱的这一段回唱给我听。");
        type.GetField("HeadExtraReviewRequired").SetValue(e, true);
        type.GetField("TailExtraReviewRequired").SetValue(e, true);
        void Boundary(string name, float from, float to, string kind)
        {
            var field = type.GetField(name); var element = field.FieldType.GetElementType();
            var value = Activator.CreateInstance(element, true);
            element.GetField("expanded_start_seconds").SetValue(value, from);
            element.GetField("expanded_end_seconds").SetValue(value, to);
            element.GetField("type").SetValue(value, kind);
            var array = Array.CreateInstance(element, 1); array.SetValue(value, 0); field.SetValue(e, array);
        }
        Boundary("HeadExtraSegments", 0, 6.33f, "non_melodic");
        Boundary("TailExtraSegments", 16.87f, 21.97f, "uncertain");
        Call(f.chat, "RetainCompletedRecordingEvidence", f.sense);
            Call(f.chat, "BindAcceptedRecordingInput", true);
        var context = (RecordingSingingDecisionContext)Call(f.chat, "BuildRecordingSingingDecisionContext");
        song = (string)context.CurrentInput["clip"];
        if (capture == 6) return context;
        f.Begin(RangeAsk); f.StageCompletedEvidence(f.user, "", .18f, 4f, 3f, 4f);
        context = (RecordingSingingDecisionContext)Call(f.chat, "BuildRecordingSingingDecisionContext");
        if (capture == 8) return context;
        // Inject the already-observed broad playback into runtime bookkeeping;
        // no AudioSource, conversion or synthetic audio is played by this test.
        var broad = MaterialPlan(context, song, "expanded");
        Check(CompileRecordingFixture(broad, context, out string compiled, out _), "Cannot stage broad source proposal.");
        Call(f.chat, "ExtractSingingGoalTags", new object[] { compiled, true });
        f.Approve(); Check(f.Resolve(song, "expanded", out string why), "Cannot stage controlled previous range: " + why);
        f.Completed(song);
        f.Begin(RangeTrim); f.StageCompletedEvidence(f.user, "", .19f, 8f, 6f, 8f);
        context = (RecordingSingingDecisionContext)Call(f.chat, "BuildRecordingSingingDecisionContext");
        if (capture == 9) return context;
        f.Begin(RangeClarify); f.StageCompletedEvidence(f.user, "", .18f, 8f, 6f, 8f);
        context = (RecordingSingingDecisionContext)Call(f.chat, "BuildRecordingSingingDecisionContext");
        if (capture == 10) return context;
        f.Begin("可惜好像还是失败了，再试一次吧。"); f.StageCompletedEvidence(f.user, "", .2f, 4f, 3f, 4f);
        return (RecordingSingingDecisionContext)Call(f.chat, "BuildRecordingSingingDecisionContext");
    }

    public static JObject RunRecordingRangeChecks()
    {
        int before = checks;
        foreach (int stage in new[] { 9, 10, 11 })
        foreach (string range in new[] { "clean", "expanded" })
        using (var f = new Fixture())
        {
            var context = RangeSession(f, stage, out string song);
            var selected = context.Materials.Single(m => m.ClipRef == song);
            var projected = ((JArray)RecordingSingingDecisionCompiler.PromptFacts(context)["materials"])
                .OfType<JObject>().Single(r => (string)r["clip"] == song)["description"];
            Check(projected["rangeEvidence"] != null && ((JArray)projected["rangeEvidence"]["ranges"]["expanded"]["boundaryOverlap"]).Count == 2,
                "Dedicated correction prompt lost the broad range's retained head/tail evidence.");
            Check((double)projected["rangeEvidence"]["ranges"]["clean"]["start"] > 6.32 &&
                ((JArray)projected["rangeEvidence"]["ranges"]["clean"]["boundaryOverlap"]).Count == 0,
                "Retained clean geometry was changed by the later spoken correction.");
            var proposal = MaterialPlan(context, song, range);
            Check(CompileRecordingFixture(proposal, context, out string compiled, out string error), error);
            Call(f.chat, "ExtractSingingGoalTags", new object[] { compiled, true });
            string reviewText = (string)Call(f.chat, "BuildRecordingInputReviewContext");
            var reviewFacts = JObject.Parse(reviewText.Substring(reviewText.IndexOf('{')));
            var row = ((JArray)reviewFacts["proposed_recording_facts"]).OfType<JObject>().Single();
            Check((string)row["clip"] == song && (string)row["HeadExtraText"] != "" && (string)row["TailExtraText"] != "" &&
                (string)row["rangeEvidence"]["proposedRange"] == range && (string)reviewFacts["current_user_complete"] == f.user,
                "Independent review must see exact source, current complete correction and the proposed range separately from measurements.");
            var reviewContract = (JObject)reviewFacts["review_contract"];
            Check((string)reviewContract["request_quote"]["source"] == "current_user_complete" &&
                !(bool)reviewContract["request_quote"]["historical_transcript_can_authorize_repeat"] &&
                !(bool)reviewContract["source_status"]["pending_alone_requires_user_reply"] &&
                !(bool)reviewContract["source_status"]["goal_review_confirms_voice_identity"] &&
                (string)reviewContract["range_encoding"]["named_start_end"] == "null" &&
                (string)reviewContract["comparison_target"]["object"] == "proposal" &&
                (string)reviewContract["request_quote"]["current_repeat_quote_data"] == f.user,
                "The bounded review contract must separate current repeat authorization, source identity and named-range encoding.");
            string completeReview = (string)Call(f.chat, "BuildDedicatedWorkReviewPrompt", "controlled-frame");
            Check(completeReview.StartsWith(RecordingSingingDecisionCompiler.ReviewContextMarker, StringComparison.Ordinal) &&
                !completeReview.Contains("controlled-frame") && !completeReview.Contains("只输出紧凑JSON") &&
                JObject.Parse(completeReview.Substring(RecordingSingingDecisionCompiler.ReviewContextMarker.Length))["candidate_recording_facts"] is JArray,
                "Source review must use one pure data packet and all candidate recordings, without the legacy instruction-bearing frame.");
            Check(JToken.DeepEquals(projected["rangeEvidence"]["ranges"], row["rangeEvidence"]["ranges"]),
                "Proposal and independent review disagree about retained geometric facts: proposal=" +
                projected["rangeEvidence"]["ranges"].ToString(Formatting.None) + "; review=" +
                row["rangeEvidence"]["ranges"].ToString(Formatting.None));
            object[] compare = { new JObject { ["origin"] = "user_request", ["source_relation"] = "retained_recording",
                ["request_quote"] = f.user, ["refs"] = song, ["range"] = "clean", ["start_seconds"] = null, ["end_seconds"] = null }, "" };
            bool matches = (bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", compare);
            Check(matches == (range == "clean"), "A review expecting clean silently approved an expanded proposal, or rejected the exact same clean target.");
            Check(!(bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn") && (bool)Call(f.chat, "HasPendingSingingGoalReview") &&
                selected.ClipRef != (string)context.CurrentInput["clip"],
                "Generating correction facts or comparing review targets performed audio or substituted the ordinary request recording.");
        }
        foreach (int stage in new[] { 6, 8, 9 })
        using (var f = new Fixture())
        {
            var context = RangeSession(f, stage, out string song);
            var proposal = MaterialPlan(context, song, "clean");
            Check(CompileRecordingFixture(proposal, context, out string compiled, out string error), error);
            Call(f.chat, "ExtractSingingGoalTags", new object[] { compiled, true });
            var expected = new JObject { ["origin"] = "user_request",
                ["source_relation"] = stage == 6 ? "current_recording" : "retained_recording",
                ["request_quote"] = f.user, ["refs"] = song, ["range"] = "clean",
                ["start_seconds"] = null, ["end_seconds"] = null };
            var playbackBefore = JObject.FromObject(f.sense.DescribeRecordingSingingDecisionFacts(song))["currentPlayback"].DeepClone();
            if (stage == 8)
            {
                var historicalQuote = (JObject)expected.DeepClone(); historicalQuote["request_quote"] = RangeSongUser;
                object[] wrong = { historicalQuote, "" };
                Check(!(bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", wrong),
                    "A plausible old recording request must still fail current-repeat authorization.");
            }
            if (stage == 9)
            {
                var relabelled = (JObject)expected.DeepClone(); relabelled["range"] = "window";
                relabelled["start_seconds"] = 6.33; relabelled["end_seconds"] = 16.87;
                object[] wrong = { relabelled, "" };
                Check(!(bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", wrong),
                    "Relabelling clean's exact coordinates as window must not bypass the typed range fence.");
            }
            object[] exact = { expected, "" };
            Check((bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", exact),
                "Source-pending alone must not reject a current, independently supplied exact target: " + (string)exact[1]);
            var source = JObject.FromObject(f.sense.DescribeRecordingSingingDecisionFacts(song));
            Check((string)source["SourceStatus"] == "pending" &&
                JToken.DeepEquals(playbackBefore, source["currentPlayback"]) &&
                (bool)Call(f.chat, "HasPendingSingingGoalReview") &&
                !(bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn"),
                "Target agreement must not confirm voice identity, prepare a new range or submit playback.");
        }
        return new JObject { ["passed"] = true, ["checks"] = checks - before,
            ["scope"] = "Production retention, range facts, compiler, goal identity and independent target comparison; controlled decisions/review and synthetic PCM, no model accuracy or measured audio-crop quality." };
    }

    // Explicit opt-in by the root runner. Same live fixture owns selection and
    // review; all HTTPs are serial decision requests, never audio execution.
    public static JObject RunRecordingRangeModelChecks(string repositoryRoot, string output)
    {
        var results = new JArray();
        foreach (var item in new[] { Tuple.Create(6, "c6", "perform"), Tuple.Create(8, "c8", "perform"),
            Tuple.Create(9, "c9-trim", "perform"), Tuple.Create(10, "c10-clarify", "perform"),
            Tuple.Create(11, "c11-retry", "perform"), Tuple.Create(11, "quoted-cancel", "reply") })
        using (var f = new Fixture())
        {
            var context = RangeSession(f, item.Item1, out string song);
            if (item.Item2 == "quoted-cancel")
            {
                f.Begin("刚才我说‘只唱有旋律的那部分’，是在说明测试过程。现在不要唱，也不要修改录音。");
                f.StageCompletedEvidence(f.user, "", .2f, 5f, 3f, 5f);
                context = (RecordingSingingDecisionContext)Call(f.chat, "BuildRecordingSingingDecisionContext");
            }
            var rows = new JArray(); ExportMaterialRow(rows, f, context, item.Item2, item.Item3, song, repositoryRoot);
            var result = new JObject { ["id"] = item.Item2, ["expectedDecision"] = item.Item3, ["expectedClip"] = song,
                ["expectedRangeCandidate"] = item.Item3 == "perform" ? "clean" : null,
                ["context"] = JObject.FromObject(context), ["decisionWire"] = rows[0]["wire"].DeepClone(),
                ["semanticPass"] = false, ["audioSubmitted"] = false };
            results.Add(result);
            try
            {
                result["decisionHttp"] = SendMaterialModel((JObject)result["decisionWire"]);
                var envelope = JObject.Parse((string)result["decisionHttp"]["envelope"]);
                var choices = envelope["choices"] as JArray; var message = choices?.Count == 1 ? choices[0]["message"] as JObject : null;
                if ((int)result["decisionHttp"]["httpStatus"] != 200 || message == null || (string)choices[0]["finish_reason"] != "stop" ||
                    (string)message["role"] != "assistant" || message["content"]?.Type != JTokenType.String ||
                    message["function_call"] != null && message["function_call"].Type != JTokenType.Null ||
                    message["tool_calls"] != null && message["tool_calls"].Type != JTokenType.Null &&
                        (!(message["tool_calls"] is JArray tools) || tools.Count != 0) ||
                    message["refusal"] != null && message["refusal"].Type != JTokenType.Null)
                    throw new InvalidOperationException("decision-envelope-invalid");
                string raw = (string)message["content"]; result["rawDecision"] = raw;
                bool ok = RecordingSingingDecisionCompiler.TryCompile(raw, context, out string compiled, out string why);
                result["compilerAccepted"] = ok; result["compilerReason"] = why;
                if (!ok) continue;
                var plan = JObject.Parse(raw); result["decision"] = plan;
                var keys = ((JArray)plan["materials"]).Values<string>().ToArray();
                var selected = context.Materials.Where(m => keys.Contains(m.Key)).ToArray();
                result["selectedClips"] = new JArray(selected.Select(m => m.ClipRef));
                result["handoff"] = compiled == RecordingSingingDecisionCompiler.Handoff;
                result["semanticPass"] = (string)plan["decision"] == item.Item3 && (item.Item3 != "perform" ||
                    selected.Length == 1 && selected[0].ClipRef == song && (string)plan["range"] == "clean");
                if ((string)plan["decision"] != "perform" || compiled == RecordingSingingDecisionCompiler.Handoff) continue;
                Call(f.chat, "ExtractSingingGoalTags", new object[] { compiled, true });
                string reviewPrompt = (string)Call(f.chat, "BuildDedicatedWorkReviewPrompt",
                    (string)Call(f.chat, "BuildPerceptionFrame", "recording-range-live-review"));
                var provider = f.host.GetComponent<ChatQW>();
                result["reviewWire"] = JObject.Parse((string)typeof(ChatQW).GetMethod("BuildWorkReviewRequestJson", Flags).Invoke(provider, new object[] { reviewPrompt }));
                if ((string)result["reviewWire"]["response_format"]["type"] != "json_schema")
                    throw new InvalidOperationException("Live Spark review must use the deployed OpenAI-compatible strict schema format.");
                result["reviewHttp"] = SendMaterialModel((JObject)result["reviewWire"]);
                bool parsed = ReadMaterialReview((string)result["reviewHttp"]["envelope"], out JObject review, out string error);
                result["reviewParserAccepted"] = parsed; result["reviewParserReason"] = error; result["review"] = review;
                if (parsed)
                {
                    object[] compare = { review["singing_goal_expected"], "" };
                    result["targetMatches"] = (bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", compare);
                    result["targetMismatchReason"] = (string)compare[1];
                    result["wouldPassGoalGate"] = (string)review["singing_goal_status"] == "approved" && (bool)result["targetMatches"];
                    result["wrongSelectionWouldPass"] = !(bool)result["semanticPass"] && (bool)result["wouldPassGoalGate"];
                }
                result["audioSubmitted"] = (bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn");
            }
            catch (Exception error) { result["error"] = error.ToString(); }
            finally
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
                File.WriteAllText(output, results.ToString());
            }
        }
        return new JObject { ["cases"] = results.Count, ["semanticPasses"] = results.Count(r => (bool)r["semanticPass"]),
            ["compilerAccepted"] = results.Count(r => (bool?)r["compilerAccepted"] == true),
            ["reviewParserAccepted"] = results.Count(r => (bool?)r["reviewParserAccepted"] == true),
            ["wrongSelectionWouldPass"] = results.Count(r => (bool?)r["wrongSelectionWouldPass"] == true),
            ["scope"] = "Production decision factory/compiler and independent review factory/parser/target comparison. Real 2142 words and bounded measured range facts with controlled synthetic PCM/history. Clean is expected as a candidate, not claimed acoustically perfect. No conversion, playback or model-output ApplySingingGoalReview." };
    }
}
#endif
