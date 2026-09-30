using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

// The real private provider entry and its production callback run here. A controlled
// provider supplies verdicts; the final renderer intentionally has no AudioSource.
// Reaching that renderer's explicit failure proves dispatch without emitting sound.
public sealed class DirectSingingReviewFakeLLM : LLM
{
    public int requests, cancels;
    public string prompt;
    public Action<string> reply;
    public override void PostWorkReviewMsg(string value, string image, Action<string> callback, ConversationTrace trace)
    { requests++; prompt = value; reply = callback; }
    public override void CancelEphemeralMsg() { cancels++; }
}

public static class DirectSingingReviewRegression
{
    public static IEnumerator RunChecks(string output) => AgentSingingGoalRegression.RunDirectSingingReviewChecks(output);
    public static IEnumerator RunLiveChecks(string source, string output) => AgentSingingGoalRegression.RunDirectSingingLiveChecks(source, output);
}

public sealed class DirectSingingLiveTTS : GPTSoVITSFASTAPI
{
    public override void WarmUp() { } // This test covers SVC, never the TTS server.
}

// Capture the real production request and verdict without replacing either the
// provider transport/schema checks or the private review callback.
public sealed class DirectSingingLiveQW : ChatQW
{
    public int requests;
    public string prompt, response, requestJson;
    public float elapsedSeconds;
    public override void PostWorkReviewMsg(string value, string image, Action<string> callback, ConversationTrace trace)
    {
        requests++; prompt = value;
        requestJson = (string)typeof(ChatQW).GetMethod("BuildWorkReviewRequestJsonWithImage",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(this, new object[] { value, image });
        float started = Time.realtimeSinceStartup;
        base.PostWorkReviewMsg(value, image, answer => {
            response = answer; elapsedSeconds = Time.realtimeSinceStartup - started;
            callback(answer);
        }, trace);
    }
}

public static partial class AgentSingingGoalRegression
{
    public static IEnumerator RunDirectSingingReviewChecks(string output)
    {
        if (Directory.Exists(output) || !Path.HasExtension(output)) output = Path.Combine(output, "direct-singing-review.json");
        int before = checks;
        VerifySilentSingingProjection();
        foreach (string scenario in new[] { "approved", "rejected", "mismatched_target", "invalid", "timeout",
            "new_turn", "model", "version", "material", "generation", "disabled", "cancel", "manual_capture", "agent_started" })
        using (var f = new Fixture())
        {
            var model = SetUpDirectReviewFixture(f);
            f.Begin("那把这一段唱给我听。");
            Set(f.chat, "m_CurrentSpeechRoundId", 12); Set(f.chat, "m_FormalResponseGeneration", 9);
            Set(f.chat, "m_AgentRunning", false);
            string projected = CompileRecordingProposal(f.user, f.first, f.Phrase(f.first).Revision.ToString(),
                "current", "ready", out _, out _);
            Call(f.chat, "OnStreamComplete", projected);
            Check(Get(f.chat, "m_DeferredSingingRequest") != null,
                "Compiled and provider-projected PTT form lost its deferred sing: " + scenario);
            Check(Get(f.chat, "m_DirectSingingReview") != null, "Compiled PTT form did not schedule review: " + scenario);
            Check(!(bool)Call(f.chat, "TryBeginDirectSingingGoalReview"), "PTT scheduled duplicate review: " + scenario);
            for (int frame = 0; frame < 12 && model.requests == 0; frame++) yield return null;
            Check(model.requests == 1 && model.reply != null && model.prompt.Contains(f.first),
                "PTT failed to call real PostWorkReviewMsg once with its material: " + scenario);
            Check(!(bool)Get(f.chat, "m_AgentRunning") && !(bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn"),
                "Private review enabled Agent or submitted audio before approval: " + scenario);
            var owner = Get(f.chat, "m_DirectSingingReview");
            if (scenario == "approved") Set(f.chat, "m_UserTurnAwaitingReplySince", 1f);
            string response = DirectSingingReviewResponse(f, scenario == "rejected" ? "waiting_user" : "approved",
                scenario == "mismatched_target" ? f.second : f.first);
            if (scenario == "new_turn") f.Begin("先不要唱。");
            if (scenario == "model") ((ChatSetting)Get(f.chat, "m_ChatSettings")).m_ChatModel = f.host.AddComponent<LLM>();
            if (scenario == "version")
            { var goal = Get(f.chat, "m_SingingGoal"); var field = goal.GetType().GetField("version"); field.SetValue(goal, (int)field.GetValue(goal) + 1); }
            if (scenario == "material")
                Check(f.sense.TryPrepareSingingClip(f.first, false, "expanded", out _, out _), "Could not change material revision for fence test.");
            if (scenario == "generation") Set(f.chat, "m_FormalResponseGeneration", 10);
            if (scenario == "disabled") f.chat.enabled = false;
            if (scenario == "cancel") Call(f.chat, "CancelDirectSingingGoalReview", "test-cancel");
            if (scenario == "manual_capture")
            {
                f.chat.NotifyManualCaptureStarted();
                Check(!(bool)Get(f.chat, "m_UserSpeechActiveForAutonomy") && !(bool)Get(f.chat, "m_AgentRunning"),
                    "Manual capture cancellation enabled realtime listening or Agent.");
            }
            if (scenario == "agent_started") Set(f.chat, "m_AgentRunning", true);
            if (scenario == "timeout")
            {
                owner.GetType().GetField("deadline").SetValue(owner, -1f);
                yield return null; yield return null;
                Check(model.cancels == 1 && (string)Get(f.chat, "m_WorkStatus") == "review_unavailable",
                    "Timeout did not cancel its private request and record unavailable.");
            }
            model.reply(scenario == "invalid" ? "{}" : response);
            if (scenario == "approved")
            {
                Check((bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn"), "Approved PTT request never entered full QueueHumBack.");
                Check(((string)Get(f.chat, "m_LastHumBackResult")).Contains("没有可用的音频播放器"),
                    "Approved PTT request did not reach the real TryBeginPendingHumBack renderer entry.");
                Check((bool)JObject.FromObject(Get(f.chat, "m_SingingGoal"))["targetReviewed"],
                    "PTT bypassed the independent target comparator.");
                var notices = f.host.GetComponent<SubtitleOverlay>().NoticeHistory;
                Check((float)Get(f.chat, "m_UserTurnAwaitingReplySince") < 0f &&
                    notices.Count(n => n.Code == "singing_execution_not_started") == 1 &&
                    notices.Last().Message.Contains("没有可用的音频播放器"),
                    "Missing-player preflight left PTT waiting or failed to deliver its exact non-character failure once.");
            }
            else Check(!(bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn"),
                "Unapproved/stale PTT review submitted audio: " + scenario);
            Check(Get(f.chat, "m_DirectSingingReview") == null && Get(f.chat, "m_DeferredSingingRequest") == null,
                "Completed/cancelled PTT review retained executable ownership: " + scenario);
            model.reply(response);
            if (scenario == "approved") Check(f.host.GetComponent<SubtitleOverlay>().NoticeHistory.Count(
                n => n.Code == "singing_execution_not_started") == 1, "Duplicate review callback repeated the preflight notice.");
            Check(Get(f.chat, "m_DirectSingingReview") == null && !(bool)Get(f.chat, "m_HumBackPending") &&
                !(bool)Call(f.chat, "IsSingingRuntimeActivityActive"), "A duplicate callback recreated audio work: " + scenario);
        }
        using (var f = new Fixture())
        {
            SetUpDirectReviewFixture(f); f.Begin("请唱第一段。"); Set(f.chat, "m_AgentRunning", false); f.Propose(f.first, "current");
            Check(!(bool)Call(f.chat, "TryBeginDirectSingingGoalReview"), "Goal alone created a direct execution request.");
            Set(f.chat, "m_FormalResponseInFlight", true);
            int oldGeneration = (int)Get(f.chat, "m_FormalResponseGeneration");
            f.chat.NotifyManualCaptureStarted();
            Check((int)Get(f.chat, "m_FormalResponseGeneration") != oldGeneration &&
                !(bool)Get(f.chat, "m_FormalResponseInFlight") && !(bool)Get(f.chat, "m_UserSpeechActiveForAutonomy"),
                "PTT started before a goal owner existed but did not fence the old formal callback generation.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        File.WriteAllText(output, new JObject { ["passed"] = true, ["checks"] = checks - before,
            ["scope"] = "Observed recording-decision JSON shape → actual compiler → ChatQW completion projection → OnStreamComplete → PTT private review dispatch; production review parsing/target match, owner/cancel/timeout fences and full resolver through real renderer entry. Fake model verdict, intentionally absent audio player, no service calls or playback." }.ToString());
    }

    public static IEnumerator RunDirectSingingLiveChecks(string source, string output)
    {
        if (Directory.Exists(output) || !Path.HasExtension(output)) output = Path.Combine(output, "direct-singing-live.json");
        source = Path.GetFullPath(source); output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = new JObject { ["passed"] = false, ["source"] = source,
            ["scope"] = "Observed recording-decision JSON shape → actual compiler → ChatQW completion projection → OnStreamComplete → real DGX ChatQW private review transport and unmodified verdict → full material resolver → real existing 9882 conversion → muted AudioSource sample/DSP progression → goal completion. Latest actual recording and measured clean range; fixture pitch timeline is reconstructed, not a new acoustic accuracy test. No service start or audible output." };
        var f = new Fixture(); TextAsset[] promptFiles = null;
        float started = Time.realtimeSinceStartup;
        try
        {
            SetUpDirectReviewFixture(f);
            string repository = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "../../.."));
            var metadata = JObject.Parse(File.ReadAllText(Path.ChangeExtension(source, ".json")));
            string transcript = (string)metadata["text"];
            float raw = (float)metadata["duration"], clean = (float)metadata["singing_end_seconds"];
            float expanded = (float)metadata["singing_recovery_end_seconds"];
            f.Begin(transcript); Set(f.chat, "m_AgentRunning", false);
            Set(f.chat, "m_CurrentSpeechRoundId", 12); Set(f.chat, "m_FormalResponseGeneration", 9);
            f.StageCompletedEvidence(transcript, (string)metadata["singing_text"] ?? "",
                (float)metadata["singing_probability"], raw, clean, expanded);
            var evidence = typeof(SenseVoiceSpeechToText).GetField("m_LastSingingEvidence", Flags).GetValue(f.sense);
            evidence.GetType().GetProperty("RawWavBytes").SetValue(evidence, File.ReadAllBytes(source));
            Call(f.chat, "RetainCompletedRecordingEvidence", f.sense);
            var candidate = f.sense.DescribeQuarantinedSingingCandidates().Single();
            Check(candidate.SourceStatus == "pending" && candidate.PlaybackStatus == "evidence_only", "Live fixture pre-approved source.");
            report["clip"] = candidate.ClipRef; report["cleanSeconds"] = clean;
            var audio = f.host.AddComponent<AudioSource>(); audio.playOnAwake = false; audio.mute = true;
            if (UnityEngine.Object.FindObjectOfType<AudioListener>() == null) f.host.AddComponent<AudioListener>();
            Set(f.chat, "m_AudioSource", audio);
            var voice = f.host.AddComponent<DirectSingingLiveTTS>(); voice.enabled = false;
            typeof(GPTSoVITSFASTAPI).GetField("m_VoiceConversionReferencePath", Flags).SetValue(voice,
                Path.Combine(repository, "Assets/Model/41041.wav"));
            ((ChatSetting)Get(f.chat, "m_ChatSettings")).m_TextToSpeech = voice;
            Set(f.chat, "m_EnableNeuralHumSVC", true); Set(f.chat, "m_EnableSingingVoiceSynthesis", false);
            Set(f.chat, "m_AllowLegacyHumFallback", false); Set(f.chat, "m_HumSVCTimeoutSeconds", 120);
            var model = f.host.AddComponent<DirectSingingLiveQW>(); model.enabled = false;
            model.m_Backend = ChatQW.BackendType.OpenAICompatible;
            ((ChatSetting)Get(f.chat, "m_ChatSettings")).m_ChatModel = model;
            promptFiles = new[] { "persona", "language", "behavior" }.Select(name =>
                new TextAsset(File.ReadAllText(Path.Combine(repository, "Assets/AIChatTookit/Prompts/" + name + ".txt"))) { name = name }).ToArray();
            typeof(LLM).GetField("m_PromptFiles", Flags).SetValue(model, promptFiles);
            model.m_DataList.Clear(); model.m_DataList.Add(new LLM.SendData("system", model.BuildSystemPrompt()));
            model.m_DataList.Add(new LLM.SendData("user", transcript));
            const string request = "那把这一段唱给我听。";
            f.Begin(request); Set(f.chat, "m_AgentRunning", false);
            model.m_DataList.Add(new LLM.SendData("user", request));
            model.SkillCatalogContext = (string)Call(f.chat, "BuildSkillCatalogContext"); model.SetActiveSkills();
            string projected = CompileRecordingProposal(request, candidate.ClipRef, "pending", "clean", "evidence_only",
                out string rawDecision, out string compiled);
            File.WriteAllText(output + ".decision.json", rawDecision);
            File.WriteAllText(output + ".compiled.txt", compiled);
            File.WriteAllText(output + ".projected.txt", projected);
            Call(f.chat, "OnStreamComplete", projected);
            for (int frame = 0; frame < 20 && model.requests == 0; frame++) yield return null;
            Check(model.requests == 1, "Compiler/provider-projected OnStreamComplete did not dispatch real PTT review.");
            File.WriteAllText(output + ".review-prompt.txt", model.prompt);
            File.WriteAllText(output + ".review-request.json", model.requestJson);

            int maxSamples = 0, totalSamples = 0; float clipSeconds = 0f;
            double firstDsp = -1, lastDsp = -1, rms = 0;
            bool sawRequest = Get(f.chat, "m_ActiveHumSVCRequest") != null;
            AudioClip measuredClip = null;
            while (Time.realtimeSinceStartup - started < 180f)
            {
                sawRequest |= Get(f.chat, "m_ActiveHumSVCRequest") != null;
                if (audio.clip != null && audio.clip != measuredClip)
                {
                    measuredClip = audio.clip; totalSamples = audio.clip.samples; clipSeconds = audio.clip.length;
                    var samples = new float[audio.clip.samples * audio.clip.channels];
                    if (audio.clip.GetData(samples, 0)) rms = Math.Sqrt(samples.Select(v => (double)v * v).Average());
                }
                if (audio.isPlaying && audio.timeSamples > 0)
                { if (firstDsp < 0) firstDsp = AudioSettings.dspTime; lastDsp = AudioSettings.dspTime; maxSamples = Math.Max(maxSamples, audio.timeSamples); }
                var goal = Get(f.chat, "m_SingingGoal");
                string outcome = goal == null ? "none" : (string)JObject.FromObject(goal)["outcome"];
                if (outcome == "playback_complete_content_unverified" || outcome == "interrupted_or_failed") break;
                // A refused/unavailable review completes this bounded request. Do
                // not spend the full playback timeout waiting for work that no
                // longer has a review owner or an active audio operation.
                if (model.response != null && Get(f.chat, "m_DirectSingingReview") == null &&
                    !(bool)Call(f.chat, "IsSingingRuntimeActivityActive"))
                {
                    report["stoppedReason"] = "review_finished_without_audio_work";
                    break;
                }
                yield return null;
            }
            report["sawSvcRequest"] = sawRequest; report["audioMuted"] = audio.mute;
            report["reviewSeconds"] = model.elapsedSeconds;
            report["reviewResponse"] = model.response;
            report["workStatus"] = (string)Get(f.chat, "m_WorkStatus");
            if (model.response != null) File.WriteAllText(output + ".review-response.json", model.response);
            report["maxTimeSamples"] = maxSamples; report["clipSamples"] = totalSamples;
            report["clipSeconds"] = clipSeconds; report["dspElapsedWhilePlaying"] = lastDsp - firstDsp; report["outputRms"] = rms;
            report["goal"] = Get(f.chat, "m_SingingGoal") == null ? null : JObject.FromObject(Get(f.chat, "m_SingingGoal"));
            report["lastResult"] = (string)Get(f.chat, "m_LastHumBackResult");
            report["sourceStillPending"] = f.sense.DescribePracticePhrases().SingleOrDefault(p => p.ClipRef == candidate.ClipRef)?.PendingConfirmation == true;
            report["passed"] = model.requests == 1 && !string.IsNullOrWhiteSpace(model.response) &&
                sawRequest && audio.mute && rms > .00001 && totalSamples > 0 && maxSamples >= totalSamples * .9 &&
                clipSeconds > clean - .3f && lastDsp - firstDsp >= clipSeconds * .85 &&
                (string)report["goal"]?["outcome"] == "playback_complete_content_unverified" && (bool)report["sourceStillPending"];
        }
        finally
        {
            report["elapsedSeconds"] = Time.realtimeSinceStartup - started;
            if (report["lastResult"] == null) report["lastResult"] = (string)Get(f.chat, "m_LastHumBackResult");
            File.WriteAllText(output, report.ToString());
            Call(f.chat, "CancelDirectSingingGoalReview", "live-test-finished");
            Call(f.chat, "CancelPendingHumBack", "live-test-finished", false);
            f.Dispose();
            if (promptFiles != null) foreach (var prompt in promptFiles) UnityEngine.Object.DestroyImmediate(prompt);
        }
        Check((bool)report["passed"], "Live PTT chain failed; see " + output);
    }

    private static string ProjectProviderCompletion(string compiled, bool complete = true) =>
        (string)typeof(ChatQW).GetMethod("ProjectFormalCompletion", Flags).Invoke(null,
            new object[] { compiled, RoleOutputChannels.Parse(compiled,
                RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions), complete });

    private static string CompileRecordingProposal(string request, string clip, string revision, string range,
        string playback, out string raw, out string compiled)
    {
        var context = new RecordingSingingDecisionContext { UserText = request };
        context.Materials.Add(new RecordingSingingMaterial { Key = "material_2", ClipRef = clip,
            Revision = revision, PlaybackStatus = playback, HasCleanRange = true, HasExpandedRange = true });
        // The failed scene's semantic decision, rebinding only the immutable
        // material identity/revision and requested range for each fixture.
        raw = new JObject { ["decision"] = "perform", ["materials"] = new JArray("material_2"),
            ["range"] = range, ["intent"] = "perform", ["expected"] = "我唱给你听吧。",
            ["user_evidence"] = request, ["thought"] = "", ["speech"] = new JObject {
                ["text"] = "", ["language"] = "zh" } }.ToString();
        Check(CompileRecordingFixture(JObject.Parse(raw), context, out compiled, out string error),
            "Observed recording decision did not compile: " + error);
        Check(compiled.StartsWith("<speech mode=\"after_action\"/><silent/>", StringComparison.Ordinal),
            "Silent recording compiler omitted its action dependency.");
        string projected = ProjectProviderCompletion(compiled);
        Check(projected.StartsWith("<speech mode=\"after_action\"/><silent/>", StringComparison.Ordinal),
            "Actual provider completion projection discarded the silent singing action dependency.");
        Check(projected.Contains("<sing_goal ") && projected.Contains("<sing ") &&
            !RoleOutputChannels.Parse(compiled).HasSpeech,
            "Projection manufactured speech or lost the recording action/goal pair.");
        return projected;
    }

    private static void VerifySilentSingingProjection()
    {
        const string sing = "<sing refs=\"clip:public-2\" range=\"clean\"/>";
        const string expected = "<speech mode=\"after_action\"/><silent/>" + sing;
        for (int split = 0; split <= expected.Length; split++)
        {
            var channel = new RoleOutputChannels(boundaryPolicy: RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
            channel.Push(expected.Substring(0, split)); channel.Push(expected.Substring(split)); channel.Finish();
            Check(channel.ToExecutableText(true, true) == expected && !channel.HasSpeech,
                "Silent action lost phase at streaming boundary " + split);
            Check(channel.ToExecutableText() == "<silent/>" + sing,
                "Opt-out compatibility projection unexpectedly retained metadata.");
        }
        Check(ProjectProviderCompletion(expected, false) == "", "Truncated silent action became executable.");
        string undeclared = ProjectProviderCompletion("<silent/>" + sing);
        Check(!undeclared.Contains("<speech"), "Projection granted an undeclared singing action a new phase.");
        foreach (string prefix in new[] { "<speech mode=\"independent\"/>",
            "<speech mode=\"after_action\" rejected=\"true\"/>", "<speech mode=\"invalid\"/>" })
        {
            string invalid = ProjectProviderCompletion(prefix + "<silent/>" + sing);
            Check(!invalid.Contains("<sing ") && invalid.Contains("rejected=\"true\""),
                "Silent invalid phase lost rejection or recovered a dependent action.");
        }
        Check(ProjectProviderCompletion("<thought>private</thought><silent/>") == "<silent/>",
            "Private silence acquired an execution phase.");
    }

    private static DirectSingingReviewFakeLLM SetUpDirectReviewFixture(Fixture f)
    {
        var model = f.host.AddComponent<DirectSingingReviewFakeLLM>(); model.enabled = false;
        ((ChatSetting)Get(f.chat, "m_ChatSettings")).m_ChatModel = model;
        var text = new GameObject("PTTText", typeof(RectTransform), typeof(Text)); text.transform.SetParent(f.host.transform);
        var button = new GameObject("PTTSend", typeof(RectTransform), typeof(Button)); button.transform.SetParent(f.host.transform);
        var input = new GameObject("PTTInput", typeof(RectTransform), typeof(InputField)); input.transform.SetParent(f.host.transform);
        Set(f.chat, "m_TextBack", text.GetComponent<Text>()); Set(f.chat, "m_CommitMsgBtn", button.GetComponent<Button>());
        Set(f.chat, "m_InputWord", input.GetComponent<InputField>());
        Set(f.chat, "m_PersistSubtitleSettings", false); Set(f.chat, "m_EnableSubtitleTranslation", false);
        Set(f.chat, "m_PrewarmHumSVCOnSingingSkillLoad", false);
        Set(f.chat, "m_AutoStartHumSVC", false); Set(f.chat, "m_AutoStartSVS", false);
        Set(f.chat, "m_EnableScreenVision", false); Set(f.chat, "m_EnableMemoryRecall", false);
        Set(f.chat, "m_AgentRunning", false); Set(f.chat, "m_StreamComplete", true); Set(f.chat, "m_TTSSenderDone", true);
        f.sense.enabled = false; f.host.SetActive(true);
        return model;
    }

    private static string DirectSingingReviewResponse(Fixture f, string status, string target) => new JObject {
        ["proceed"] = status == "approved", ["work_status"] = status == "approved" ? "continue" : "waiting_user",
        ["work_evidence"] = "Controlled regression verdict, not a live semantic measurement.",
        ["singing_goal_status"] = status, ["singing_goal_evidence"] = "测试独立审核：指定第一段及当前范围。",
        ["singing_goal_expected"] = new JObject { ["origin"] = "user_request", ["request_quote"] = f.user,
            ["refs"] = target, ["range"] = "current", ["start_seconds"] = JValue.CreateNull(), ["end_seconds"] = JValue.CreateNull() },
        ["intent"] = "完成当前用户要求", ["novelty"] = "", ["wait_seconds"] = 0
    }.ToString();
}
