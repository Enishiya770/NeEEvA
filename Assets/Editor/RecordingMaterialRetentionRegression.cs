#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Production retention/context/compiler/review-parser/target comparison. PCM is
// synthetic and silent in this fixture; semantic review verdicts are supplied.
// Session text and range measurements are from the immutable 2017 log, not a
// claim that the fixture PCM itself contains or proves the user's song.
public static partial class AgentSingingGoalRegression
{
    private const string MaterialSessionSong = "呃，不是什么意思啊，为什么又在又在复读啊？好吧，那看来跟随现在又出问题了。下一个测试，接下来开始测试你的歌唱能力。 接下初めてのルーブルはなんでことはなかたは私だけ。";
    private const string MaterialSessionAsk = "我已经唱了呀，现在你把它回唱出来呀。";
    private const string MaterialSessionRetry = "好吧，现在好像失败了，再来一次，试着把我刚才唱的那一段回唱给我听。";

    private static RecordingSingingDecisionContext MaterialSession(Fixture f, int stage, out string song, out string request)
    {
        f.Begin(MaterialSessionSong);
        f.StageCompletedEvidence(f.user, "", .432f, 26.68f, 21.27f, 24.87f);
        var evidence = typeof(SenseVoiceSpeechToText).GetField("m_LastSingingEvidence", Flags).GetValue(f.sense);
        evidence.GetType().GetField("CleanStartSeconds").SetValue(evidence, 12.03f);
        evidence.GetType().GetField("PitchStability").SetValue(evidence, .545f);
        Call(f.chat, "RetainCompletedRecordingEvidence", f.sense);
            Call(f.chat, "BindAcceptedRecordingInput", true);
        var context = AcceptedMaterialContext(f);
        song = (string)context.CurrentInput["clip"]; request = null;
        if (stage == 15) return context;
        f.Begin(MaterialSessionAsk);
        f.StageCompletedEvidence(f.user, "", .436f, 5.47f, 1.57f, 4.17f);
        evidence = typeof(SenseVoiceSpeechToText).GetField("m_LastSingingEvidence", Flags).GetValue(f.sense);
        evidence.GetType().GetField("PitchStability").SetValue(evidence, .522f);
        context = AcceptedMaterialContext(f);
        request = (string)context.CurrentInput["clip"];
        if (stage == 16) return context;
        // Reproduce the mistaken earlier proposal as unapproved history. It is
        // not a reason to protect that recording at the expense of the real song.
        var mistaken = MaterialPlan(context, request);
        Check(CompileRecordingFixture(mistaken, context, out string legacy, out _),
            "Could not stage the previous unapproved proposal.");
        Call(f.chat, "ExtractSingingGoalTags", new object[] { legacy, true });
        f.Begin(MaterialSessionRetry);
        f.StageCompletedEvidence(f.user, "", .155f, 7.5f, 6.17f, 6.17f);
        evidence = typeof(SenseVoiceSpeechToText).GetField("m_LastSingingEvidence", Flags).GetValue(f.sense);
        evidence.GetType().GetField("CleanStartSeconds").SetValue(evidence, 3.23f);
        evidence.GetType().GetField("PitchStability").SetValue(evidence, .472f);
        return AcceptedMaterialContext(f);
    }

    private static RecordingSingingDecisionContext AcceptedMaterialContext(Fixture f)
    {
        // Each scripted stage is an accepted user utterance. Retention alone
        // must not lazily replace the frozen identity during a later callback.
        Call(f.chat, "RetainCompletedRecordingEvidence", f.sense);
        Call(f.chat, "BindAcceptedRecordingInput", true);
        return (RecordingSingingDecisionContext)Call(f.chat, "BuildRecordingSingingDecisionContext");
    }

    private static JObject MaterialPlan(RecordingSingingDecisionContext context, string selected, string range = "clean")
    {
        string quote = (string)RecordingSingingDecisionCompiler.QuoteReferences(context).OfType<JObject>()
            .First(q => (string)q["origin"] == "current" && (string)q["text"] == context.UserText)["ref"];
        return new JObject { ["decision"] = "perform", ["source_ref"] = quote, ["request_ref"] = "source",
            ["materials"] = new JArray(context.Materials.Single(m => m.ClipRef == selected).Key),
            ["range"] = range, ["intent"] = "repeat", ["expected"] = "回唱用户刚才唱出的那段歌声，不把后续催促讲话当作歌声。",
            ["thought"] = "", ["speech"] = new JObject { ["text"] = "", ["language"] = "ja" } };
    }

    private static bool ReadMaterialReview(string envelope, out JObject verdict, out string reason)
    {
        object[] args = { envelope, null, null, false, false, null };
        bool parsed = (bool)typeof(ChatQW).GetMethod("TryReadWorkReviewResponse", Flags).Invoke(null, args);
        reason = (string)args[5]; verdict = parsed ? JObject.Parse((string)args[1]) : null;
        return parsed;
    }

    public static JObject RunRecordingMaterialChecks()
    {
        int before = checks; var sizes = new JArray();
        foreach (int stage in new[] { 16, 17 })
        foreach (bool wrong in new[] { false, true })
        using (var f = new Fixture())
        {
            var context = MaterialSession(f, stage, out string song, out string request);
            var source = context.Materials.Single(m => m.ClipRef == song);
            JObject table = RecordingSingingDecisionCompiler.PromptFacts(context);
            var projected = ((JArray)table["materials"]).OfType<JObject>().Single(r => (string)r["clip"] == song);
            Check(!source.RequiresExpansion && (string)projected["detail"] == "complete" &&
                (string)projected["recorded_content"]["transcript"] == MaterialSessionSong &&
                (string)projected["recorded_content"]["singingText"] == "" &&
                (string)projected["description"]["AcousticMode"] == "speech",
                "The older low-score song/intro recording was hidden by a later ordinary request.");
            Check((double)projected["description"]["CleanStartSeconds"] > 12 &&
                ((JArray)projected["proposalRanges"]).Values<string>().Contains("clean") &&
                ((JArray)projected["proposalRanges"]).Values<string>().Contains("expanded"),
                "Actual measured raw-audio ranges were lost or implicitly selected.");
            Check(RecordingSingingDecisionCompiler.TryBuildBoundedContext(new string('背', 69000), context, 8000,
                out string bounded, out string budgetError), "Complete session evidence exceeded the budget: " + budgetError);
            sizes.Add(new JObject { ["stage"] = stage, ["characters"] = bounded.Length, ["materials"] = context.Materials.Count });
            var fast = (JObject)Call(f.chat, "FastLoopRecordingMaterials", f.sense);
            var fastSong = ((JArray)fast["recent"]).OfType<JObject>().Single(r => (string)r["clipRef"] == song);
            Check((string)fastSong["transcriptExcerpt"] == MaterialSessionSong &&
                !(bool)fastSong["transcriptTruncated"] && (string)fast["fullEvidenceRoute"] == "recording_singing" &&
                ((JArray)fast["recent"]).Count <= 6,
                "Bounded fast route lost the source or mislabeled shortened evidence as complete.");
            string selected = wrong ? request : song;
            var raw = MaterialPlan(context, selected);
            Check(CompileRecordingFixture(raw, context, out string compiled, out string error) &&
                compiled != RecordingSingingDecisionCompiler.Handoff,
                "A retained low-score source with present facts could not be proposed: " + error);
            Check(!RoleOutputChannels.Parse(compiled).HasSpeech, "Unreviewed candidate published a performance promise.");
            Call(f.chat, "ExtractSingingGoalTags", new object[] { compiled, true });
            Check((bool)Call(f.chat, "HasPendingSingingGoalReview") &&
                !(bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn") && !f.Resolve(selected, "clean", out _),
                "Retained evidence or a valid compiler result bypassed independent review.");
            var expected = new JObject { ["origin"] = "user_request", ["source_relation"] = "retained_recording",
                ["request_quote"] = f.user, ["refs"] = song, ["range"] = "clean",
                ["start_seconds"] = null, ["end_seconds"] = null };
            var controlled = new JObject { ["work_evidence"] = "受控测试：旧歌声与新催促录音是不同来源。",
                ["work_status"] = "continue", ["proceed"] = true, ["intent"] = "只核对原歌声来源与范围。",
                ["singing_goal_status"] = "approved", ["singing_goal_evidence"] = "此注入结论仅测试传输及独立目标比较，不证明音频边界或实际模型理解。",
                ["singing_goal_expected"] = expected, ["wait_seconds"] = 1 };
            string envelope = new JObject { ["choices"] = new JArray(new JObject {
                ["finish_reason"] = "stop", ["message"] = new JObject { ["role"] = "assistant", ["content"] = controlled.ToString(Formatting.None) } }) }.ToString();
            Check(ReadMaterialReview(envelope, out JObject accepted, out string why), "Production seven-field review parser rejected a valid result: " + why);
            object[] compare = { accepted["singing_goal_expected"], "" };
            bool matched = (bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", compare);
            Check(matched != wrong, "Real goal comparator failed to reject the intervening request recording or accept the exact older source.");
            string reviewFacts = (string)Call(f.chat, "BuildRecordingInputReviewContext");
            Check(reviewFacts.Contains(f.user) && reviewFacts.Contains(selected) &&
                reviewFacts.Contains("RecordingTranscript"), "Source review lost the current complete request or proposed-source transcript.");
            Check(f.sense.PracticePhraseCount == 2 && !(bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn"),
                "Read-only source/target checks promoted pending PCM or submitted audio.");
        }
        return new JObject { ["passed"] = true, ["checks"] = checks - before, ["promptSizes"] = sizes,
            ["scope"] = "Production retention/context/IR/review transport/target comparison; original session words and measured song ranges with synthetic PCM and controlled review; no network, conversion or playback." };
    }

    public static JObject RunStableRecordingSourceChecks()
    {
        int before = checks;
        using (var f = new Fixture())
        {
            const string secondVerse = "初めてあなたを見たあの日をき出した車止められない喪失のよ。 No you can.";
            f.Begin(secondVerse);
            f.StageCompletedEvidence(secondVerse, "", .58f, 12f, 10.9f, 11.4f);
            Call(f.chat, "RetainCompletedRecordingEvidence", f.sense);
            Call(f.chat, "BindAcceptedRecordingInput", true);
            var context = AcceptedMaterialContext(f);
            string pending = (string)context.CurrentInput["clip"];
            var identityBefore = context.Materials.ToDictionary(m => m.ClipRef, m => m.Key);
            Check(context.Materials.All(m => m.Key == m.ClipRef) &&
                context.Materials.Select(m => m.RecordingSequence).SequenceEqual(context.Materials.Select(m => m.RecordingSequence).OrderBy(n => n)),
                "Production recording keys must equal stable clips and interleave ready/pending by event sequence.");
            Check(context.Materials.Single(m => m.ClipRef == pending).PlaybackStatus == "evidence_only", "Fixture did not retain pending second-verse evidence.");
            var rawFacts = JObject.Parse(context.Materials.Single(m => m.ClipRef == pending).Description);
            Check((bool)rawFacts["RawAudioRetained"] && (int)rawFacts["RawAudioByteCount"] > 44 &&
                (bool)rawFacts["HasAvailableOriginalAudio"] && !(bool)rawFacts["PreparedAudioAvailable"] &&
                (string)rawFacts["SourceStatus"] == "pending",
                "Pending source must expose actual retained audio bytes independently of preparation and source confirmation.");
            f.Begin("你把第二段唱给我听呀，我唱的那个第二段，你唱给我听。");
            f.StageCompletedEvidence(f.user, "", .1f, 4f, 3f, 3.7f);
            context = AcceptedMaterialContext(f);
            var wrong = MaterialPlan(context, f.first);
            wrong["expected"] = "回唱第二段，即初めてあなたを見たあの日那段。";
            Check(CompileRecordingFixture(wrong, context, out string compiled, out string error), error);
            Call(f.chat, "ExtractSingingGoalTags", new object[] { compiled, true });
            string packet = (string)Call(f.chat, "BuildDedicatedWorkReviewPrompt", "<legacy-frame-must-not-enter-review/>");
            var facts = JObject.Parse(packet.Substring(RecordingSingingDecisionCompiler.ReviewContextMarker.Length));
            var candidates = ((JArray)facts["candidate_recording_facts"]).OfType<JObject>().ToArray();
            Check(candidates.Length == context.Materials.Count && candidates.Any(c => (string)c["clip"] == pending &&
                (string)c["RecordingTranscript"] == secondVerse && (string)c["playbackReadiness"] == "evidence_only" &&
                (string)c["relationToCurrentInput"] == "retained_recording"),
                "Independent review lost the unselected pending source's full content evidence.");
            Check((string)facts["goal"]["refs"] == f.first && (string)facts["current_user_complete"] == f.user &&
                !packet.Contains("<legacy-frame-must-not-enter-review/>") && facts["recent_actual_user_inputs"] is JArray,
                "Source review must preserve actual proposal/current request/history without the legacy frame instructions.");
            object[] compare = { new JObject { ["origin"] = "user_request", ["source_relation"] = "retained_recording",
                ["request_quote"] = f.user, ["refs"] = pending, ["range"] = "clean",
                ["start_seconds"] = null, ["end_seconds"] = null }, "" };
            Check(!(bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", compare) && !f.Resolve(f.first, "clean", out _),
                "An independently identified second source must not approve/execute a first-source proposal.");
            var provider = f.host.AddComponent<ChatQW>(); provider.m_Backend = ChatQW.BackendType.OpenAICompatible;
            provider.ActiveSkillContext = "<legacy-skill-manual/>";
            string wire = (string)typeof(ChatQW).GetMethod("BuildWorkReviewRequestJsonWithImage", Flags)
                .Invoke(provider, new object[] { packet, null });
            var messages = (JArray)JObject.Parse(wire)["messages"];
            Check(messages.Count == 2 && ((string)messages[0]["content"]).StartsWith(RecordingSingingDecisionCompiler.SourceReviewContract) &&
                (string)messages[1]["content"] == packet && !wire.Contains("<legacy-skill-manual/>"),
                "Source review transport must contain its single independent policy and pure data only.");
            var schema = JObject.Parse(wire)["response_format"]["json_schema"]["schema"];
            var expectedBranches = (JArray)schema["properties"]["singing_goal_expected"]["anyOf"];
            Check(expectedBranches.OfType<JObject>().Any(b => (string)b["properties"]["start_seconds"]["type"] == "null" &&
                ((JArray)b["properties"]["range"]["enum"]).Values<string>().Contains("clean")) &&
                expectedBranches.OfType<JObject>().Any(b => (string)b["properties"]["start_seconds"]["type"] == "number" &&
                    ((JArray)b["properties"]["range"]["enum"]).Values<string>().SequenceEqual(new[] { "window" })),
                "Source review schema must distinguish named ranges/null coordinates from an explicit numeric window.");
            Check(f.sense.TryPrepareSingingClip(pending, false, "clean", out _, out _) && f.sense.ConfirmSingingClipSource(pending, out _),
                "Explicit fixture preparation/source confirmation failed.");
            context = AcceptedMaterialContext(f);
            Check(identityBefore.All(p => context.Materials.Single(m => m.ClipRef == p.Key).Key == p.Value),
                "Source promotion changed another recording's identity or its own key.");
            Check(!(bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn"), "Context/independent comparison submitted audio.");
        }
        return new JObject { ["passed"] = true, ["checks"] = checks - before,
            ["scope"] = "Production material snapshot, stable identity, review packet/wire and actual target comparator. Recorded second-verse text with synthetic PCM; no model call or playback." };
    }

    // Exact production context/projection/wire export. The words are from the
    // 1308 session; PCM and runtime state are reconstructed, not a captured wire.
    public static void ExportStableRecordingSourceCases(string decisionsOutput, string reviewsOutput, string repositoryRoot)
    {
        var decisions = new JArray(); var reviews = new JArray();
        foreach (var item in new[] {
            Tuple.Create("second-request", "你把第二段唱给我听呀，我唱的那个第二段，你唱给我听。", "second"),
            Tuple.Create("lyric-correction", "不是第一段不是第一段，是第二段，是那个唱着。 はめて安卡米塔那一段。", "second"),
            Tuple.Create("return-to-first", "这次不要第二段了，还是把最开始初めてのルーブル那段再唱给我听。", "first"),
            Tuple.Create("two-ordered", "把我唱过的两段按原来第一段、第二段的顺序回唱给我听。", "both"),
            Tuple.Create("cancel", "不是第一段不是第一段，我是说第二段。不过不用再试了，现在不要唱。", "none"),
            Tuple.Create("quotation", "我是在复述刚才的要求‘你把第二段唱给我听’，现在只问你是不是选错了，不让你再唱。", "none") })
        using (var f = new Fixture())
        {
            var context = StableRecordingSession(f, item.Item2, out string first, out string second);
            string expected = item.Item3 == "first" ? first : item.Item3 == "both" ? first + "," + second : second;
            ExportMaterialRow(decisions, f, context, "production-1308-" + item.Item1,
                item.Item3 == "none" ? "reply" : "perform", expected, repositoryRoot);
            decisions.Last["scope"] = "Actual production retained context, profile and wire builders; 1308-session words and controlled two-recording state, synthetic PCM. Stable clips are fixture identities, not the original scene refs. No conversion/playback.";
        }
        foreach(bool refusal in new[] { false, true })
        using(var f = new Fixture())
        {
            var context = StableRecordingSession(f, "你把第二段唱给我听呀，我唱的那个第二段，你唱给我听。", out _, out string second);
            var proposal = new JObject { ["source"] = "unpublished-current-role-draft", ["draftId"] = "controlled-adopted-recording-choice",
                ["text"] = refusal ? "我现在想休息一下，这次先不唱。" : "我把你刚才唱的第二段回唱给你听。",
                ["task"] = refusal ? "none" : "recording_singing", ["goal"] = refusal ? "" : "回唱用户提供的第二段录音",
                ["originalUserText"] = f.user, ["purpose"] = "plan", ["executed"] = false, ["published"] = false };
            context.CurrentIntent = (JObject)Call(f.chat, "BuildProtectedRecordingIntent", proposal, 0);
            ExportMaterialRow(decisions, f, context, refusal ? "production-1308-adopted-refusal" : "production-1308-adopted-second",
                refusal ? "reply" : "perform", second, repositoryRoot);
            decisions.Last["scope"] = "Actual production context/protected-intent/profile/wire builders; controlled current role choice (adoption or refusal), retained original two-verse words and synthetic PCM. Additional to all six original null-proposal cases; no playback.";
        }
        foreach (bool wrong in new[] { true, false })
        using (var f = new Fixture())
        {
            var context = StableRecordingSession(f, "你把第二段唱给我听呀，我唱的那个第二段，你唱给我听。", out string first, out string second);
            string selected = wrong ? first : second;
            var plan = MaterialPlan(context, selected);
            plan["expected"] = "回唱第二段，即初めてあなたを見たあの日那段。";
            Check(CompileRecordingFixture(plan, context, out string compiled, out string error), error);
            Call(f.chat, "ExtractSingingGoalTags", new object[] { compiled, true });
            var provider = f.host.AddComponent<ChatQW>(); provider.m_Backend = ChatQW.BackendType.OpenAICompatible;
            provider.m_LogRequestStats = false;
            string prompt = (string)Call(f.chat, "BuildDedicatedWorkReviewPrompt", "<legacy-frame-must-not-enter-review/>");
            var wire = JObject.Parse((string)typeof(ChatQW).GetMethod("BuildWorkReviewRequestJsonWithImage", Flags)
                .Invoke(provider, new object[] { prompt, null }));
            reviews.Add(new JObject { ["id"] = wrong ? "second-verse-wrong-first-clip" : "second-verse-correct-pending-clip",
                ["expectedAllowed"] = !wrong, ["currentClip"] = context.CurrentInput["clip"].DeepClone(),
                ["selectedClip"] = selected, ["requestedClip"] = second, ["compiledProposal"] = compiled, ["wire"] = wire,
                ["scope"] = "Actual production source-review packet/wire. Both proposals claim second-verse content; one points to first-verse audio. Full competing pending evidence is retained. Controlled session words and synthetic PCM; no playback or source confirmation." });
        }
        foreach (var output in new[] { decisionsOutput, reviewsOutput }) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        File.WriteAllText(decisionsOutput, decisions.ToString()); File.WriteAllText(reviewsOutput, reviews.ToString());
    }

    private static RecordingSingingDecisionContext StableRecordingSession(Fixture f, string request, out string first, out string second)
    {
        // Remove only this fixture's unrelated starter rows, never runtime data.
        int a = f.Phrase(f.first).StableId, b = f.Phrase(f.second).StableId;
        Check(f.sense.DropPracticePhraseByStableId(a, out _, out _, out _) &&
            f.sense.DropPracticePhraseByStableId(b, out _, out _, out _), "Could not clear fixture-only starter recordings.");
        const string firstText = "初めてのルーブルはなんでこはなかたは私だけのモナリザもどこに出あっ？ たか。";
        const string secondText = "初めてあなたを見たあの日をき出した車止められない喪失のよ。 No you can.";
        f.Begin(firstText); f.StageCompletedEvidence(f.user, "", .58f, 12f, 9.87f, 11.4f);
        Call(f.chat, "RetainCompletedRecordingEvidence", f.sense);
            Call(f.chat, "BindAcceptedRecordingInput", true);
        var context = AcceptedMaterialContext(f);
        first = (string)context.CurrentInput["clip"];
        Check(f.sense.TryPrepareSingingClip(first, false, "clean", out _, out _) && f.sense.ConfirmSingingClipSource(first, out _),
            "Fixture first verse could not be explicitly prepared/confirmed.");
        f.Begin("很好啊，那接下来先停下来吧。"); f.StageCompletedEvidence(f.user, "", .1f, 3f, 2.4f, 2.8f);
        AcceptedMaterialContext(f);
        f.Begin(secondText); f.StageCompletedEvidence(f.user, "", .58f, 12f, 10.9f, 11.4f);
        context = AcceptedMaterialContext(f);
        second = (string)context.CurrentInput["clip"];
        f.Begin(request); f.StageCompletedEvidence(f.user, "", .1f, 5f, 4.4f, 4.8f);
        return AcceptedMaterialContext(f);
    }

    // Export uses production prompt projection and wire builders. Context is a
    // controlled reconstruction; it is not an exact capture of the private scene.
    public static void ExportRecordingMaterialCases(string output, string repositoryRoot)
    {
        var rows = new JArray();
        foreach (int stage in new[] { 15, 16, 17 })
        using (var f = new Fixture())
        {
            var context = MaterialSession(f, stage, out string song, out string request);
            ExportMaterialRow(rows, f, context, "session2017-c" + stage, stage == 15 ? "reply" : "perform", song, repositoryRoot);
        }
        foreach (var item in new[] {
            Tuple.Create("explicit-older", "这次不要刚才的新段，还是再唱第一段 public phrase alpha。", "perform"),
            Tuple.Create("quoted-not-requested", "我刚才说‘把它回唱出来’，是在复述测试过程。现在不要唱。", "reply"),
            Tuple.Create("cancel-after-retry", "不用再试了，唱歌测试结束。现在不要唱。", "reply") })
        using (var f = new Fixture())
        {
            MaterialSession(f, 17, out _, out _);
            f.Begin(item.Item2); f.StageCompletedEvidence(f.user, "", .1f, 5f, 2f, 4f);
            var context = AcceptedMaterialContext(f);
            ExportMaterialRow(rows, f, context, item.Item1, item.Item3, f.first, repositoryRoot);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        File.WriteAllText(output, rows.ToString());
    }

    private static void ExportMaterialRow(JArray rows, Fixture f, RecordingSingingDecisionContext context,
        string id, string kind, string expectedSource, string repositoryRoot)
    {
        var provider = f.host.AddComponent<ChatQW>(); provider.m_Backend = ChatQW.BackendType.OpenAICompatible;
        provider.m_LogRequestStats = false;
        var assets = new[] { "persona", "language", "behavior" }.Select(name => new TextAsset(
            File.ReadAllText(Path.Combine(repositoryRoot, "Assets/AIChatTookit/Prompts/" + name + ".txt"))) { name = name }).ToArray();
        try
        {
            typeof(LLM).GetField("m_PromptFiles", Flags).SetValue(provider, assets);
            provider.m_DataList.Clear(); provider.m_DataList.Add(new LLM.SendData("system", provider.BuildSystemPrompt()));
            foreach (JObject prior in context.RequestEvidence)
                if ((string)prior["text"] != context.UserText) provider.m_DataList.Add(new LLM.SendData("user", (string)prior["text"]));
            provider.m_DataList.Add(new LLM.SendData("user", context.UserText));
            provider.RecordingDecisionContext = context;
            string transient = (string)Call(f.chat, "BuildMotionRequestContext", (string)Call(f.chat, "BuildFirstReplyContext", "")) +
                SpeechText.OutputContract + "\n\n" + RoleOutputPublicBoundary.OutputContract;
            object[] args = { FormalResponseStage.RecordingSingingDecision, transient, false, null, 0 };
            var history = (List<LLM.SendData>)typeof(ChatQW).GetMethod("ProjectFormalSource", Flags).Invoke(provider, args);
            Check((string)args[3] == "recording-singing-ir-v1", "Export did not use the actual production recording profile.");
            string projected = (string)args[1];
            string mandatory = RecordingSingingDecisionCompiler.Prompt(context);
            Check(projected.EndsWith(mandatory, StringComparison.Ordinal), "Protected full material table absent from actual profile.");
            Check(RecordingSingingDecisionCompiler.TryBuildBoundedContext(projected.Substring(0, projected.Length - mandatory.Length),
                context, 8000, out string bounded, out _), "Export rejected over-budget source evidence.");
            string wire = (string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", Flags).Invoke(provider,
                new object[] { history, false, bounded, null, FormalResponseStage.RecordingSingingDecision });
            var payload = JObject.Parse(wire); payload["response_format"] = RecordingSingingDecisionCompiler.ResponseFormat(context);
            rows.Add(new JObject { ["id"] = id, ["expectedDecision"] = kind,
                ["expectedClip"] = kind == "perform" ? expectedSource : null,
                ["context"] = JObject.FromObject(context), ["promptCharacters"] = mandatory.Length, ["wire"] = payload,
                ["scope"] = "Actual retained synthetic PCM and current production context/profile/wire factories; original 2017 words plus explicit counterexamples, controlled runtime context, not a captured scene request. Decision only, no audio execution." });
        }
        finally { foreach (var asset in assets) UnityEngine.Object.DestroyImmediate(asset); }
    }

    // Explicit opt-in live entry. The ordinary regression/export never calls it.
    // The same fixture stays alive from recording selection through independent
    // review, so no ref/capture/evidence is remapped between model requests.
    public static JObject RunRecordingMaterialModelChecks(string repositoryRoot, string output)
    {
        var results = new JArray();
        void Case(Fixture f, RecordingSingingDecisionContext context, string id, string expectedKind, string expectedClip)
        {
            var exports = new JArray();
            ExportMaterialRow(exports, f, context, id, expectedKind, expectedClip, repositoryRoot);
            var source = (JObject)exports[0];
            var result = new JObject { ["id"] = id, ["expectedDecision"] = expectedKind,
                ["expectedClip"] = expectedKind == "perform" ? expectedClip : null,
                ["scope"] = source["scope"].DeepClone(), ["context"] = source["context"].DeepClone(),
                ["decisionWire"] = source["wire"].DeepClone(), ["semanticPass"] = false };
            results.Add(result);
            try
            {
                result["decisionHttp"] = SendMaterialModel((JObject)source["wire"]);
                var http = (JObject)result["decisionHttp"];
                if ((int)http["httpStatus"] != 200) throw new InvalidOperationException("decision-http-failure");
                var envelope = JObject.Parse((string)http["envelope"]);
                var choices = envelope["choices"] as JArray;
                if (choices == null || choices.Count != 1 || (string)choices[0]["finish_reason"] != "stop" ||
                    !(choices[0]["message"] is JObject message) || (string)message["role"] != "assistant" ||
                    message["content"]?.Type != JTokenType.String ||
                    message["tool_calls"] != null && message["tool_calls"].Type != JTokenType.Null &&
                        (!(message["tool_calls"] is JArray tools) || tools.Count != 0) ||
                    message["function_call"] != null && message["function_call"].Type != JTokenType.Null ||
                    message["refusal"] != null && message["refusal"].Type != JTokenType.Null)
                    throw new InvalidOperationException("decision-envelope-invalid");
                string raw = (string)message["content"];
                result["rawDecision"] = raw;
                bool compiledOk = RecordingSingingDecisionCompiler.TryCompile(raw, context, out string compiled, out string compileReason);
                result["compilerAccepted"] = compiledOk; result["compilerReason"] = compileReason;
                if (!compiledOk) return;
                var decision = JObject.Parse(raw);
                string kind = (string)decision["decision"];
                var selected = context.Materials.Where(m => ((JArray)decision["materials"]).Values<string>().Contains(m.Key)).ToArray();
                result["selectedClips"] = new JArray(selected.Select(m => m.ClipRef));
                result["semanticPass"] = kind == expectedKind && (kind != "perform" || selected.Length == 1 && selected[0].ClipRef == expectedClip);
                result["handoff"] = compiled == RecordingSingingDecisionCompiler.Handoff;
                if (kind != "perform" || compiled == RecordingSingingDecisionCompiler.Handoff) return;
                Call(f.chat, "ExtractSingingGoalTags", new object[] { compiled, true });
                Check(!(bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn"), "A model proposal submitted real audio during evaluation.");
                string frame = (string)Call(f.chat, "BuildPerceptionFrame", "recording-material-live-review");
                string prompt = (string)Call(f.chat, "BuildDedicatedWorkReviewPrompt", frame);
                var provider = f.host.GetComponent<ChatQW>();
                string reviewWire = (string)typeof(ChatQW).GetMethod("BuildWorkReviewRequestJson", Flags).Invoke(provider, new object[] { prompt });
                result["reviewWire"] = JObject.Parse(reviewWire);
                result["reviewHttp"] = SendMaterialModel((JObject)result["reviewWire"]);
                var reviewHttp = (JObject)result["reviewHttp"];
                bool parsed = ReadMaterialReview((string)reviewHttp["envelope"], out JObject review, out string reviewReason);
                bool accepted = (int)reviewHttp["httpStatus"] == 200 && parsed;
                result["reviewParserAccepted"] = accepted; result["reviewParserReason"] = reviewReason;
                result["review"] = review;
                if (accepted)
                {
                    object[] target = { review["singing_goal_expected"], "" };
                    bool matches = (bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", target);
                    result["targetMatches"] = matches; result["targetMismatchReason"] = (string)target[1];
                    result["wouldPassGoalGate"] = (string)review["singing_goal_status"] == "approved" && matches;
                    result["wrongSourceWouldPass"] = !(bool)result["semanticPass"] && (bool)result["wouldPassGoalGate"];
                }
                result["audioSubmitted"] = (bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn");
            }
            catch (Exception e) { result["error"] = e.ToString(); }
            finally
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
                File.WriteAllText(output, results.ToString());
                Debug.Log("[RecordingMaterial/Model] id=" + id + " semantic=" + result["semanticPass"] +
                    " compiler=" + result["compilerAccepted"] + " reviewParser=" + result["reviewParserAccepted"] +
                    " targetMatches=" + result["targetMatches"] + " error=" + (result["error"] != null));
            }
        }
        foreach (int stage in new[] { 15, 16, 17 })
        using (var f = new Fixture())
        {
            var context = MaterialSession(f, stage, out string song, out _);
            Case(f, context, "session2017-c" + stage, stage == 15 ? "reply" : "perform", song);
        }
        foreach (var entry in new[] {
            Tuple.Create("explicit-older", "这次不要刚才的新段，还是再唱第一段 public phrase alpha。", "perform"),
            Tuple.Create("quoted-not-requested", "我刚才说‘把它回唱出来’，是在复述测试过程。现在不要唱。", "reply"),
            Tuple.Create("cancel-after-retry", "不用再试了，唱歌测试结束。现在不要唱。", "reply") })
        using (var f = new Fixture())
        {
            MaterialSession(f, 17, out _, out _);
            f.Begin(entry.Item2); f.StageCompletedEvidence(f.user, "", .1f, 5f, 2f, 4f);
            Case(f, AcceptedMaterialContext(f), entry.Item1, entry.Item3, f.first);
        }
        return new JObject { ["cases"] = results.Count, ["semanticPasses"] = results.Count(v => (bool)v["semanticPass"]),
            ["compilerAccepted"] = results.Count(v => (bool?)v["compilerAccepted"] == true),
            ["errors"] = results.Count(v => v["error"] != null),
            ["reviewRequests"] = results.Count(v => v["reviewHttp"] != null),
            ["reviewParserAccepted"] = results.Count(v => (bool?)v["reviewParserAccepted"] == true),
            ["wrongSourceWouldPass"] = results.Count(v => (bool?)v["wrongSourceWouldPass"] == true),
            ["scope"] = "Serial live Qwen decisions and independent reviews via current production factories/compiler/parser/comparator; controlled retained synthetic PCM and session words. No ApplySingingGoalReview, conversion, playback, or proof of audio boundaries." };
    }

    private static JObject SendMaterialModel(JObject payload)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var request = (HttpWebRequest)WebRequest.Create("http://192.168.114.175:8888/v1/chat/completions");
        request.Proxy = null; request.Method = "POST"; request.ContentType = "application/json";
        request.Timeout = 45000; request.ReadWriteTimeout = 45000;
        byte[] bytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)); request.ContentLength = bytes.Length;
        using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        try
        {
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return new JObject { ["httpStatus"] = (int)response.StatusCode, ["envelope"] = reader.ReadToEnd(), ["seconds"] = timer.Elapsed.TotalSeconds };
        }
        finally { timer.Stop(); request.Abort(); }
    }
}
#endif
