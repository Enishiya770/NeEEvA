using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

// Regression of the observable request boundary, rather than copies of prompt/ack logic.
// Only ASR capture and model/audio transport are fixtures. Chat orchestration, clip parsing,
// result recording, completion/cancellation callbacks and ChatQW JSON assembly are production.
public static class AgentWorkLoopRegression
{
    private const string SessionKey = "NeEEvA.AgentWorkLoop.PlayMode";
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static IEnumerator routine;
    private static Fixture fixture;
    private static int checks;
    private static string failure;
    private static readonly JArray scenarios = new JArray();

    [InitializeOnLoadMethod]
    private static void Resume()
    {
        EditorApplication.playModeStateChanged -= Changed;
        EditorApplication.playModeStateChanged += Changed;
        if (SessionState.GetBool(SessionKey, false) && EditorApplication.isPlaying)
            EditorApplication.delayCall += Begin;
    }

    public static void RunBatch()
    {
        SessionState.SetBool(SessionKey, true);
        EditorApplication.EnterPlaymode();
    }

    private static void Changed(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(SessionKey, false)) Begin();
    }

    private static void Begin()
    {
        if (routine != null) return;
        checks = 0; failure = null; scenarios.Clear(); routine = Run();
        EditorApplication.update += Step;
    }

    private static void Step()
    {
        bool finished;
        try { finished = !routine.MoveNext(); }
        catch (Exception error) { failure = error.ToString(); finished = true; }
        if (!finished) return;
        EditorApplication.update -= Step;
        SessionState.SetBool(SessionKey, false);
        JObject incomplete = failure != null && fixture != null ? new JObject {
            ["name"] = fixture.name, ["review"] = fixture.model.LastReview,
            ["requests"] = new JArray(fixture.model.Requests.Select(p => p.DeepClone()))
        } : null;
        fixture?.Dispose(); fixture = null;
        var report = new JObject {
            ["passed"] = failure == null, ["checks"] = checks, ["error"] = failure,
            ["scope"] = "Production Play Mode orchestration and final ChatQW messages; public synthetic recording evidence, manually delivered model outputs and simulated renderer completion. No remote model, microphone, TTS or physical audibility claim.",
            ["incompleteScenario"] = incomplete,
            ["scenarios"] = scenarios
        };
        string path = Path.GetFullPath("Tools/MotionAdapter/reports/agent-work-loop-regression.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, report.ToString() + "\n");
        EditorApplication.Exit(failure == null ? 0 : 1);
    }

    private static IEnumerator Run()
    {
        foreach (IEnumerator scenario in new[] { CompleteMixedHearing(), PendingAndRepeatedSinging(), RequestOriginAndMaterials(), ConfirmationAndContinue(), PlaybackObservation(), CancellationAndSupersession(), BoundedRecovery(), VoiceEvidence(), ReviewWireContract(), ReviewedGoalMustMatchStructuredExpectation(), ReadUserTextIsNotRoleAction(), EmptyInspectionDeliveryIsBounded(), WorkEpochFencesStreamCallbacks(), ConditionalObservationReceipt(), MemoryAcknowledgementOwnership(), MemoryAcknowledgementSupersession(), NonAgentMemoryDelivery() })
            while (scenario.MoveNext()) yield return scenario.Current;
    }

    private static IEnumerator CompleteMixedHearing()
    {
        fixture = new Fixture("complete_speech_song_survives_wrong_partial"); var f = fixture; yield return null;
        const string user = "我试着唱一段。青い空。静かな夜。";
        Type senseType = typeof(SenseVoiceSpeechToText);
        void Property(string name, object value) => senseType.GetProperty(name).SetValue(f.sense, value);
        Property("LastText", user); Property("LastIsSinging", true); Property("LastSingingProbability", .67f);
        Property("LastPitchStability", .65f); Property("LastNoteSequence", "A3-B3-C4");
        Property("LastPitchLowNote", "A3"); Property("LastPitchHighNote", "C4");
        Property("LastSingingIslandSeconds", 3f);
        Type segment = senseType.GetNestedType("TurnTranscriptSegment", BindingFlags.NonPublic);
        string segments = "[{\"id\":1,\"start_seconds\":0,\"end_seconds\":2,\"region\":\"head\",\"type\":\"uncertain\",\"text\":\"我试着唱一段。\"}," +
            "{\"id\":2,\"start_seconds\":2,\"end_seconds\":5,\"region\":\"island\",\"type\":\"singing_candidate\",\"text\":\"青い空。\"}," +
            "{\"id\":3,\"start_seconds\":5,\"end_seconds\":8,\"region\":\"tail\",\"type\":\"uncertain\",\"text\":\"静かな夜。\"}]";
        senseType.GetField("m_LastTurnSegments", Flags).SetValue(f.sense, Newtonsoft.Json.JsonConvert.DeserializeObject(segments, segment.MakeArrayType()));
        senseType.GetField("m_LastSegmentedPrimaryText", Flags).SetValue(f.sense, user);
        Type draftType = typeof(ChatSample).GetNestedType("SpeculativeDraft", BindingFlags.NonPublic);
        object draft = Activator.CreateInstance(draftType);
        foreach (var item in new Dictionary<string, object> { ["sourceTranscript"]="唱とて。", ["observed_mode"]="speech",
            ["mode_confidence"]=.95f, ["draft"]="The computer uses Tokyo time.", ["sourceAudioMs"]=19000,
            ["sourceInputRevision"]=0 }) draftType.GetField(item.Key).SetValue(draft, item.Value);
        Set(f.chat, "m_SpeculativeDraft", draft); Set(f.chat, "m_StreamingTranscript", "唱とて。");
        Set(f.chat, "m_EouCognitiveSpeechVeto", true); Set(f.chat, "m_AutoSend", true); Set(f.chat, "m_UseStreaming", true);
        Set(f.chat, "m_RecordTips", Get(f.chat, "m_TextBack"));
        typeof(ChatSample).GetMethod("DealingTextCallback", Flags, null, new[] { typeof(string), typeof(bool) }, null)
            .Invoke(f.chat, new object[] { f.sense.BuildLastPerceivedText(), false });
        Check(f.model.Requests.Count == 1, "The actual final ASR callback did not submit a formal request.");
        string payload = f.model.LastRequest.ToString();
        Check(payload.Contains("青い空") && payload.Contains("静かな夜") && payload.Contains("A3-B3-C4"), "Final role request lost lyrics or melody after a wrong speech partial.");
        Check(!payload.Contains("本轮是普通说话") && !payload.Contains("The computer uses Tokyo time."), "Wrong partial became an authoritative final fact or reusable reply.");
        Check(f.sense.LastIsSinging && !(bool)Get(f.chat, "m_EouCognitiveSpeechVeto"), "Rejecting a draft rewrote the final acoustic result.");
        Check(payload.Contains("用户自己唱过不等于请求你回唱") && payload.Contains("matches_complete_transcript"), "Final input lost event/request separation or the partial observation scope.");
        f.model.Complete("<silent/>");
        Check(!(bool)Call(f.chat, "HasPendingSingingGoalReview") && !(bool)Get(f.chat, "m_HumBackPending"), "Hearing a song alone scheduled a singing action.");
        foreach (bool confirmed in new[] { false, true })
        {
            Property("LastIsSinging", confirmed);
            string neutral = f.sense.BuildLastPerceivedText(false);
            Check(neutral.Contains("A3-B3-C4") && neutral.Contains("静かな夜") && !neutral.Contains("[演唱片段"), "Uncertain/speech evidence deleted measured music or claimed confirmed singing.");
        }
        Property("LastText", "");
        Check(f.sense.BuildLastPerceivedText(false).Contains("A3-B3-C4"), "Untranscribed humming lost its melody evidence.");
        Check(Call(f.chat, "FuseSingingModeEvidence", true, false, "", true, true).ToString() == "ConflictStreamingEvidence", "Opposed partial modes were silently collapsed to an acoustic confirmation.");
        SaveScenario(f); f.Dispose(); fixture = null; yield return null;
    }

    private static IEnumerator PendingAndRepeatedSinging()
    {
        fixture = new Fixture("pending_wait_and_semantically_repeated_failed_actions"); var f = fixture; yield return null;
        f.BeginUser("请唱第一段。"); f.Confirm(f.first);
        int revision = f.sense.DescribePracticePhrases().Single(p => p.ClipRef == f.first).Revision;
        string goal = $"<sing_goal origin=\"user_request\" refs=\"{f.first}\" range=\"current\" revisions=\"{revision}\" expected=\"第一段\"/>";
        Set(f.chat, "m_CurrentSpeechRoundId", 101); Set(f.chat, "m_AgentRoundInFlight", true);
        Call(f.chat, "OnSpeechStreamDelta", new SpeechText("今から歌うね。", "ja"));
        Check(((ICollection)Get(f.chat, "m_PendingChunks")).Count == 0, "An unchecked singing promise entered TTS before the full decision.");
        Call(f.chat, "OnStreamComplete", "今から歌うね。" + goal + $"<sing refs=\"{f.first}\"/>");
        Check((bool)Call(f.chat, "HasPendingSingingGoalReview") && !(bool)Get(f.chat, "m_ToolCorrectionContinuationPending"), "Waiting for approval created a failure correction.");
        Check(!(bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn") && (int)Get(f.chat, "m_StickyToolFailureCount") == 0, "Pending approval was counted as a failed/submitted execution.");
        Check(((ICollection)Get(f.chat, "m_PendingChunks")).Count == 0 && !((string)Get(f.chat, "m_LastAIMsgPlain")).Contains("今から"), "Discarded promise was queued or recorded as spoken.");
        Call(f.chat, "ApplySingingGoalReview", "approved", "Test reviewer approved current first clip.");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Set(f.chat, "m_CurrentSpeechRoundId", 102 + attempt); Set(f.chat, "m_AgentRoundInFlight", true);
            Set(f.chat, "m_AgentCurrentRoundIsTick", true); Set(f.chat, "m_ToolCorrectionExhaustedThisRound", false);
            string speech = attempt == 0 ? "準備できたよ。" : "ごめん、今度こそ歌うよ。";
            Call(f.chat, "OnSpeechStreamDelta", new SpeechText(speech, "ja"));
            Call(f.chat, "OnStreamComplete", speech + $"<sing refs=\"{f.first}\" start_seconds=\"1\" end_seconds=\"2.4\" reason=\"different words {attempt}\"/>");
            Check(((ICollection)Get(f.chat, "m_PendingChunks")).Count == 0, "A differently worded failed action escaped the speech gate.");
        }
        Check((bool)Get(f.chat, "m_WorkNoProgress") && !(bool)Get(f.chat, "m_WorkReviewOpen") && !(bool)Get(f.chat, "m_RoundContinue"), "Same failed plan did not stop every automatic continuation path.");
        Check((int)Get(f.chat, "m_StickyToolFailureCount") == 2, "The same rejection was counted twice or reset by display wording.");
        Check(((string)Get(f.chat, "m_WorkProgressFact")).Contains("attempts_same_plan\":2"), "Structured progress did not retain the real repeated failure count.");
        var overlay = (SubtitleOverlay)Get(f.chat, "m_SubtitleOverlay");
        Check(overlay.NoticeHistory.Count(n => n.Code == "singing_no_progress" && n.Severity == SystemNoticeSeverity.Error) == 1,
            "Stopping execution did not leave one notice visible under the default ErrorsOnly setting.");
        Call(f.chat, "StopWorkWithoutProgress", "duplicate stop callback");
        Check(overlay.NoticeHistory.Count(n => n.Code == "singing_no_progress") == 1, "A repeated stop emitted another closing notice.");
        f.BeginUser("这次按新的范围来。");
        Check(!(bool)Get(f.chat, "m_WorkNoProgress"), "New user input could not recover from a stopped plan.");
        Set(f.chat, "m_WorkContinuations", 2);
        Check((bool)Call(f.chat, "ConsumeWorkChainBudget") && (int)Get(f.chat, "m_WorkContinuations") == 3 &&
            !(bool)Call(f.chat, "ConsumeWorkChainBudget"), "Immediate correction bypassed the scheduled work budget.");
        SaveScenario(f); f.Dispose(); fixture = null; yield return null;
    }

    private static IEnumerator RequestOriginAndMaterials()
    {
        fixture = new Fixture("heard_performance_is_not_a_requested_performance"); var f = fixture; yield return null;
        f.BeginUser("我试着唱一段。青い空。静かな夜。");
        string pending = f.UnselectedClip();
        object[] proposal = { $"<sing_goal refs=\"{pending}\" range=\"current\" revisions=\"0\" expected=\"first phrase\"/>", true };
        Call(f.chat, "ExtractSingingGoalTags", proposal); f.PreviousReply("今から歌うね。");
        object[] held = { "今から歌うね。" }; Call(f.chat, "ResolveSingingSpeech", held);
        Check((string)held[0] == "", "Unreviewed singing promise escaped its speech hold.");
        var independent = ExpectedSinging(pending, "current"); independent["request_quote"] = "please sing it back";
        object[] target = { independent, "" };
        Check(!(bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", target) && ((string)target[1]).Contains("引文"), "An invented user request quote authorized singing.");
        independent["request_quote"] = f.user; target = new object[] { independent, "" };
        Check(!(bool)Call(f.chat, "TryMatchReviewedSingingGoalTarget", target) && ((string)target[1]).Contains("版本") && ((string)target[1]).Contains("尚未选定"), "Approval missed simultaneous wrong revision and unselected current range.");
        Call(f.chat, "FireTick", "scheduled");
        var review = JObject.Parse(Continue("回应已发生的用户演唱"));
        review["singing_goal_status"] = "approved"; review["singing_goal_evidence"] = "错误的批准文案";
        review["singing_goal_expected"] = ExpectedSinging("", "none");
        review["work_status"] = "closed"; review["proceed"] = false;
        f.model.Decide(review.ToString());
        Check(((string)Call(f.chat, "BuildSingingGoalContext")).Contains("\"review\":\"cancelled\""), "No-request observation left an executable singing obligation.");
        Check(f.model.LastRequest.ToString().Contains("取消误建草案"), "The formal role did not receive corrected intent evidence.");
        Check(f.model.Requests.Count == 1, "An unheard promise was treated as the user's completed answer after cancellation.");
        f.model.Complete("<silent/>");
        SaveScenario(f); f.Dispose(); fixture = null; yield return null;
    }

    private static IEnumerator ConfirmationAndContinue()
    {
        fixture = new Fixture("confirmation_and_continue");
        var f = fixture; yield return null;
        f.BeginUser("请把我刚才唱的两个片段按录音顺序连接起来唱。");
        f.Confirm(f.first);
        f.PreviousReply("少し待ってて。<clip_confirm ref=\"" + f.first + "\"/><next in=\"5s\"/>");
        Set(f.chat, "m_PendingSkillStatusFrame", "\npublic-skill-note-initial\n");
        Set(f.chat, "m_LastUnknownTagNote", "public-unknown-tag-initial");
        Set(f.chat, "m_PracticeSemanticResolutionNote", "\npublic-resolution-note\n");
        Call(f.chat, "FireTick", "scheduled");
        Check(f.model.reviewRequests == 1 && f.model.Requests.Count == 0, "Pending work did not enter the dedicated progress review.");
        Check((bool)Get(f.chat, "m_PracticeEditResultPending"), "Reading a progress frame consumed the result before the role received it.");
        Check(((string)Get(f.chat, "m_PendingSkillStatusFrame")).Contains("public-skill-note-initial") &&
              (string)Get(f.chat, "m_LastUnknownTagNote") == "public-unknown-tag-initial" &&
              ((string)Get(f.chat, "m_PracticeSemanticResolutionNote")).Contains("public-resolution-note"),
              "Read-only progress inspection consumed metadata notes before formal delivery.");
        f.model.Decide(Continue("确认来源后真正执行连接演唱"));
        Check(f.model.Requests.Count == 1, "Progress review did not submit a formal continuation.");
        AssertOriginalUser(f, 1);
        string result1 = LastSystem(f.model.LastRequest);
        Check(result1.Contains(f.first) && result1.Contains("source=confirmed_user"), "Final request omitted the newly confirmed clip source.");
        Check(result1.Contains("confirmed_list=1") && result1.Contains("pending_list=1"), "Final request did not receive the current two-clip inventory.");
        Check(result1.Contains("\"requestSubmitted\":false") && result1.Contains("\"workActive\":false"), "Final request cannot distinguish confirmation from singing submission.");
        Check(result1.Contains(f.user), "Final execution feedback lost the original work goal.");
        Check(result1.Contains("public-skill-note-initial") && result1.Contains("public-unknown-tag-initial") && result1.Contains("public-resolution-note"),
            "Final role request lost the metadata notes its probe read.");
        Check((bool)Get(f.chat, "m_PracticeEditResultPending"), "An unanswered request acknowledged its input results prematurely.");
        Action<string> firstCallback = f.model.SnapshotCompletion();
        Action<SpeechText> firstDelta = f.model.SnapshotSpeech();
        Set(f.chat, "m_LastUnknownTagNote", "public-unknown-tag-newer");
        f.model.Complete("<silent/><clip_confirm ref=\"" + f.second + "\"/><continue/>");
        Check(f.model.Requests.Count == 2, "The production continue tag did not create its next request.");
        AssertOriginalUser(f, 1);
        string result2 = LastSystem(f.model.LastRequest);
        Check(result2.Contains("confirmed_list=2") && result2.Contains("pending_list=0"), "Continue saw stale clip state after the second confirmation.");
        Check(result2.Contains(f.second) && result2.Contains("source=confirmed_user"), "Continue lost the new result created while completing the previous response.");
        Check(result2.Contains(f.user) && result2.Contains("不是主动闲聊"), "Continue lost work identity or returned to idle chatter.");
        Check(result2.Contains("public-unknown-tag-newer") && (string)Get(f.chat, "m_LastUnknownTagNote") == "public-unknown-tag-newer",
            "The earlier response acknowledged and deleted a newer same-kind metadata event.");
        Check((bool)Get(f.chat, "m_PracticeEditResultPending"), "Acknowledging the first result accidentally erased a newer confirmation result.");
        firstCallback("<silent/><clip_confirm ref=\"" + f.second + "\"/><continue/>");
        Check(f.model.Requests.Count == 2 && (bool)Get(f.chat, "m_FormalResponseInFlight"),
            "A duplicate completed callback reused the shared continue generation to execute old tools or disturb the active request.");
        string sentenceBefore = Get(f.chat, "m_SentenceBuffer").ToString();
        int chunksBefore = ((ICollection)Get(f.chat, "m_PendingChunks")).Count;
        firstDelta(new SpeechText("这个已结束响应不应再发声。", "zh"));
        Check(Get(f.chat, "m_SentenceBuffer").ToString() == sentenceBefore && ((ICollection)Get(f.chat, "m_PendingChunks")).Count == chunksBefore,
            "A delta received after its callback completed entered the newer continue response.");
        f.model.Complete("<silent/>");
        Check(!(bool)Get(f.chat, "m_PracticeEditResultPending"), "An effective completed response did not acknowledge the delivered result.");
        Check((string)Get(f.chat, "m_LastUnknownTagNote") == "", "A valid response did not acknowledge the metadata event it actually received.");
        foreach (JObject request in f.model.Requests)
            Check(LastSystem(request).Length > 0, "Execution observations were not positioned after the completed assistant reply.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }

    private static IEnumerator PlaybackObservation()
    {
        fixture = new Fixture("playback_is_execution_evidence_not_goal_completion");
        var f = fixture; yield return null;
        f.BeginUser("请连接这两段；如果漏掉片段，请检查实际播放内容。");
        f.Confirm(f.first); f.Confirm(f.second);
        f.PreviousReply("これから二つ繋げるね。");
        var firstPhrase = f.sense.DescribePracticePhrases().Single(p => p.ClipRef == f.first);
        Call(f.chat, "CapturePracticePlaybackFacts", f.sense, new List<int> { firstPhrase.Index });
        Call(f.chat, "RecordPracticePlaybackOutcome", true, true);
        const string completed = "public-renderer-result: unity_playback_completed=true physical_audibility=unknown";
        Call(f.chat, "RecordHumBackResult", completed, false, true);
        Call(f.chat, "FireTick", "scheduled");
        Check(f.model.LastReview.Contains("last_completed=") && f.model.LastReview.Contains(f.first), "Progress review could not inspect the actual completed clip.");
        f.model.Decide(Continue("实际只完整播放第一段；核对原要求与结果后决定补全"));
        string facts = LastSystem(f.model.LastRequest);
        Check(facts.Contains(completed), "The latest renderer result stopped at the probe and never reached the formal role.");
        Check(facts.Contains("播放1=" + f.first) && !facts.Contains("播放2=" + f.second), "Playback facts were rewritten to resemble the requested two clips.");
        Check(facts.Contains("revision=" + firstPhrase.Revision) && facts.Contains("source=clean") && facts.Contains("raw=[1.00,2.40]"), "Playback evidence omitted the exact prepared version, source or raw time range.");
        AssertOriginalUser(f, 1);
        Check((bool)Get(f.chat, "m_HumBackResultPending"), "Renderer evidence was acknowledged before response completion.");
        // The transport completes without claiming the requested musical content was correct.
        f.model.Complete("<silent/>");
        Check(!(bool)Get(f.chat, "m_HumBackResultPending"), "Delivered renderer evidence remained perpetually pending.");
        Check((bool)Get(f.chat, "m_WorkReviewOpen"), "Unity playback completion automatically declared the user's goal satisfied.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }

    private static IEnumerator CancellationAndSupersession()
    {
        fixture = new Fixture("interruption_and_new_user_epoch");
        var f = fixture; yield return null;
        f.BeginUser("检查第一段来源，随后再决定怎样唱。");
        f.Confirm(f.first); f.PreviousReply("少し確認するね。");
        Call(f.chat, "FireTick", "scheduled");
        f.chat.NotifyUserStartedSpeaking();
        f.model.Decide(Continue("读取确认结果"));
        Check(f.model.Requests.Count == 0, "A probe spoke over a user who began speaking before its callback.");
        Check((bool)Get(f.chat, "m_PracticeEditResultPending"), "Private/cancelled progress review discarded unreceived tool evidence.");
        // This input candidate ended without a final utterance: the existing work remains current.
        Set(f.chat, "m_UserSpeechActiveForAutonomy", false);
        Call(f.chat, "FireTick", "scheduled");
        f.model.Decide(Continue("读取确认结果"));
        Check(f.model.Requests.Count == 1 && LastSystem(f.model.LastRequest).Contains(f.first), "A deferred observation was lost when the user stopped speaking.");
        Action<string> cancelledReply = f.model.SnapshotCompletion();
        f.chat.NotifyUserStartedSpeaking();
        Check((bool)Get(f.chat, "m_PracticeEditResultPending"), "Cancelling formal generation acknowledged an unread result.");
        Set(f.chat, "m_UserSpeechActiveForAutonomy", false);
        f.BeginUser("不用继续唱了，请只检查当前语音队列。");
        int currentEpoch = (int)Get(f.chat, "m_WorkEpoch");
        cancelledReply("<silent/><body_inspect scope=\"runtime\"/><continue/>");
        Check((string)Get(f.chat, "m_PendingSelfInspection") == "", "An old response executed inspection in the newer user's work.");
        Check((int)Get(f.chat, "m_WorkEpoch") == currentEpoch && (int)Get(f.chat, "m_WorkContinuations") == 0, "An old callback consumed or rewrote the new user's work budget.");
        Check(f.model.Requests.Count == 1, "A cancelled response chained into the new user turn.");
        f.PreviousReply("今の音声状態を確認するね。");
        Call(f.chat, "FireTick", "scheduled");
        Action<string> staleProbe = f.model.SnapshotReview();
        f.BeginUser("取消检查，只回答你好。");
        staleProbe(Continue("旧的语音队列检查"));
        Check(f.model.Requests.Count == 1, "An old progress callback dispatched work after a newer user instruction.");
        string work = (string)Call(f.chat, "BuildWorkReviewContext");
        Check(work.Contains(f.user) && !work.Contains("检查第一段来源"), "New work context retained the superseded user objective.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }

    private static IEnumerator BoundedRecovery()
    {
        fixture = new Fixture("invalid_progress_has_bounded_recovery");
        var f = fixture; yield return null;
        f.BeginUser("检查有哪些可播放的录音，再告诉我。");
        f.PreviousReply("稍等，我先检查。");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Call(f.chat, "FireTick", "scheduled");
            f.model.Decide(attempt == 0 ? "" : attempt == 1 ? "{\"work_status\":" : "{\"work_status\":\"invented\"}");
            Check(f.model.Requests.Count == attempt + 1, "Malformed progress did not perform exactly one bounded recovery.");
            Check((int)Get(f.chat, "m_WorkContinuations") == attempt + 1, "Recovery did not consume the same program-owned work budget.");
            AssertOriginalUser(f, 1);
            f.model.Complete("<silent/>");
            double deadline = EditorApplication.timeSinceStartup + 5;
            while ((bool)Get(f.chat, "m_AgentRoundInFlight") && EditorApplication.timeSinceStartup < deadline) yield return null;
            Check(!(bool)Get(f.chat, "m_AgentRoundInFlight"), "Silent formal response did not drain its production audio pipeline.");
        }
        Call(f.chat, "FireTick", "scheduled");
        f.model.Decide("still malformed");
        Check(f.model.Requests.Count == 3, "Invalid progress escaped its continuation budget.");
        Check((string)Get(f.chat, "m_WorkStatus") == "blocked" && !(bool)Get(f.chat, "m_WorkReviewOpen"), "Recovery exhaustion was declared completed or left unbounded.");
        Check(f.model.reviewRequests == 4 && f.model.ephemeralRequests == 0, "Pending-work progress reused the idle ephemeral request route.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }

    private static IEnumerator VoiceEvidence()
    {
        fixture = new Fixture("capability_configuration_is_not_observed_health");
        var f = fixture; yield return null;
        f.BeginUser("检查当前是否有发声功能，并说明检查到了什么。");
        Set(f.chat, "m_EnableAutonomousHumBack", true); Set(f.chat, "m_EnableNeuralHumSVC", true);
        JObject runtime = (JObject)Call(f.chat, "BuildSelfInspectionSnapshot", "runtime");
        var voice = runtime["voiceCapabilities"];
        Check((bool)voice["ttsBound"] && (bool)voice["audioSourceBound"] && (bool)voice["singing"]["enabled"],
            "Runtime inspection did not expose the actual registered voice components and enablement.");
        Check(voice["singing"]["serviceObservation"]["reachable"].Type == JTokenType.Null &&
              voice["singing"]["serviceObservation"]["workerReady"].Type == JTokenType.Null,
            "Unobserved service health was converted into connected or disconnected.");
        Check(!(bool)voice["singing"]["requestSubmitted"] && (string)voice["singing"]["lastOutcome"] == "none",
            "Enabled singing invented an accepted request or playback outcome.");
        Call(f.chat, "RecordSvcWarmupObservation", true, "{\"ok\":true,\"ready\":true}");
        voice = ((JObject)Call(f.chat, "BuildSelfInspectionSnapshot", "runtime"))["voiceCapabilities"];
        Check((bool)voice["singing"]["serviceObservation"]["workerReady"] &&
              voice["singing"]["serviceObservation"]["reachable"].Type == JTokenType.Null,
            "A worker-ready observation was lost or substituted for a separate health observation.");
        Check(!(bool)voice["singing"]["requestSubmitted"] && (string)voice["singing"]["lastOutcome"] == "none",
            "Service readiness was mistaken for a submitted or completed singing action.");
        Call(f.chat, "RecordSvcWarmupObservation", true, "{\"ok\":true}");
        voice = ((JObject)Call(f.chat, "BuildSelfInspectionSnapshot", "runtime"))["voiceCapabilities"];
        Check(voice["singing"]["serviceObservation"]["workerReady"].Type == JTokenType.Null,
            "A response without readiness metadata retained a stale ready assertion.");
        Set(f.chat, "m_EnableAutonomousHumBack", false);
        voice = ((JObject)Call(f.chat, "BuildSelfInspectionSnapshot", "runtime"))["voiceCapabilities"];
        Check(!(bool)voice["singing"]["enabled"] && (bool)voice["ttsBound"], "Disabling singing incorrectly disabled the observed TTS binding.");
        scenarios.Add(new JObject { ["name"] = f.name, ["passed"] = true, ["runtimeObservation"] = voice.DeepClone() });
        f.Dispose(); fixture = null;
    }

    private static IEnumerator ReviewWireContract()
    {
        fixture = new Fixture("dedicated_progress_wire_contract");
        var f = fixture; yield return null;
        f.BeginUser("先连接两个片段。"); f.PreviousReply("准备好了。");
        f.BeginUser("改为只唱第一段，并保留开头。");
        f.model.builder.m_DataList = f.model.m_DataList;
        f.model.builder.m_EphemeralHistoryMessages = 0;
        var build = typeof(ChatQW).GetMethod("BuildWorkReviewRequestJson", Flags);
        int historyCount = f.model.m_DataList.Count;
        JObject request = JObject.Parse((string)build.Invoke(f.model.builder, new object[] { "public latest execution state" }));
        Check((int)request["max_tokens"] >= 512 && (string)request["response_format"]["type"] == "json_object",
            "Progress review reused the tiny unstructured listening-draft budget.");
        Check(request["messages"].ToString().Contains(f.user) && request["messages"].ToString().Contains("先连接两个片段"),
            "Ephemeral history limits pruned the real user's progress context or latest correction.");
        Check(f.model.m_DataList.Count == historyCount, "Building the progress request modified saved conversation history.");
        var parse = typeof(ChatQW).GetMethod("TryReadWorkReviewResponse", Flags);
        string decision = Continue("按最新要求核对第一段和范围");
        object[] Parse(string content, string finish)
        {
            string envelope = new JObject { ["choices"] = new JArray(new JObject {
                ["finish_reason"] = finish, ["message"] = new JObject { ["content"] = content } }) }.ToString();
            object[] args = { envelope, null, null, false, false, null };
            bool accepted = (bool)parse.Invoke(null, args);
            return new object[] { accepted, args[2], args[3], args[4], args[5] };
        }
        var valid = Parse(decision, "stop");
        Check((bool)valid[0] && (bool)valid[2] && (bool)valid[3] && (string)valid[4] == "ok", "Valid structured progress was rejected.");
        var truncated = Parse(decision, "length");
        Check(!(bool)truncated[0] && (string)truncated[4] == "truncated", "A length-limited response was accepted merely because its JSON happened to parse.");
        var missingFinish = Parse(decision, "");
        Check(!(bool)missingFinish[0] && (string)missingFinish[4] == "incomplete_finish", "A progress response without a complete finish reason was accepted.");
        var unknown = JObject.Parse(decision); unknown["work_status"] = "invented";
        var badStatus = Parse(unknown.ToString(), "stop");
        Check(!(bool)badStatus[0] && (string)badStatus[4] == "invalid_schema", "An invented progress status passed schema validation.");
        var wrongType = JObject.Parse(decision); wrongType["proceed"] = "true";
        Check(!(bool)Parse(wrongType.ToString(), "stop")[0], "String coercion bypassed the boolean progress schema.");
        var extra = JObject.Parse(decision); extra["execute_tool"] = "<sing/>";
        Check(!(bool)Parse(extra.ToString(), "stop")[0], "Unknown control fields bypassed progress-only output validation.");
        var missingExpected = JObject.Parse(decision); missingExpected.Remove("singing_goal_expected");
        Check(!(bool)Parse(missingExpected.ToString(), "stop")[0], "A review without an explicit structured singing expectation passed the wire contract.");
        var wrongExpected = JObject.Parse(decision); wrongExpected["singing_goal_expected"]["range"] = "full";
        Check(!(bool)Parse(wrongExpected.ToString(), "stop")[0], "An invented expected singing range passed the wire contract.");
        scenarios.Add(new JObject { ["name"] = f.name, ["passed"] = true, ["request"] = request });
        f.Dispose(); fixture = null;
    }

    private static IEnumerator ReviewedGoalMustMatchStructuredExpectation()
    {
        fixture = new Fixture("approved_prose_cannot_override_a_structural_goal_mismatch");
        var f = fixture; yield return null;
        f.BeginUser("请把第一段扩到 expanded 范围，补回遗漏的开头再唱。");
        f.Confirm(f.first);
        var before = f.sense.DescribePracticePhrases().Single(p => p.ClipRef == f.first);
        int revision = before.Revision;
        string capture = before.ActiveCapture;
        float trimHead = before.ActiveTrimHeadSeconds, trimTail = before.ActiveTrimTailSeconds;
        object[] proposal = { "<sing_goal refs=\"" + f.first + "\" range=\"current\" revisions=\"" + revision +
            "\" intent=\"perform\" completion=\"user_confirmation\" expected=\"补回第一段的开头\" feedback=\"none\"/>", true };
        Call(f.chat, "ExtractSingingGoalTags", proposal);
        Check((bool)Call(f.chat, "HasPendingSingingGoalReview"), "The mismatched proposal did not enter the independent review path.");
        f.PreviousReply("范围已经按要求扩展了，准备演唱。");
        Call(f.chat, "FireTick", "singing-goal-review");
        Check(f.model.reviewRequests == 1 && f.model.Requests.Count == 0,
            "The proposed goal bypassed the real dedicated review dispatch.");
        var review = JObject.Parse(Continue("执行经审核的演唱目标"));
        review["singing_goal_status"] = "approved";
        review["singing_goal_evidence"] = "已核对，当前目标已经按用户要求设为 expanded，可执行。";
        review["singing_goal_expected"] = ExpectedSinging(f.first, "expanded");
        f.model.Decide(review.ToString());
        string goal = (string)Call(f.chat, "BuildSingingGoalContext");
        Check(goal.Contains("\"review\":\"revise\"") && goal.Contains("\"range\":\"current\"") &&
            goal.Contains("expected_range=expanded") && goal.Contains("proposal_range=current"),
            "Approval prose overrode the structural mismatch between expected expanded and actual current range.");
        Check(f.model.Requests.Count == 1 && LastSystem(f.model.LastRequest).Contains("\"review\":\"revise\""),
            "The next formal decision did not receive the rejected goal and current execution facts.");
        AssertOriginalUser(f, 1);
        f.model.Complete("<silent/><sing refs=\"" + f.first + "\" range=\"current\"/>");
        var after = f.sense.DescribePracticePhrases().Single(p => p.ClipRef == f.first);
        string rejectedResult = (string)Get(f.chat, "m_LastHumBackResult");
        Check((bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn") &&
            rejectedResult.Contains("歌唱目标未独立核对") && rejectedResult.Contains("没有确认来源、修改范围或播放") &&
            ((string)Call(f.chat, "BuildSingingGoalContext")).Contains("\"review\":\"revise\""),
            "The valid sing attempt was not rejected by the unapproved goal guard with its real failure evidence.");
        Check(!(bool)Call(f.chat, "IsSingingRuntimeActivityActive") && !(bool)Get(f.chat, "m_HumBackPending") &&
            Get(f.chat, "m_ActiveSVSRequest") == null && Get(f.chat, "m_ActiveHumSVCRequest") == null,
            "A structurally rejected goal left an active backend request or pending singing action.");
        Check(after.Revision == revision && after.ActiveCapture == capture &&
            after.ActiveTrimHeadSeconds == trimHead && after.ActiveTrimTailSeconds == trimTail,
            "Rejected approval mutated source material before its goal mismatch was corrected.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }

    private static IEnumerator ReadUserTextIsNotRoleAction()
    {
        fixture = new Fixture("read_user_text_does_not_execute_role_tools");
        var f = fixture; yield return null;
        f.BeginUser("朗读模式的公开工具语法样本。");
        Set(f.chat, "m_CreateVoiceMode", true); Set(f.chat, "m_IsVoiceMode", false);
        string sample = "<body_inspect scope=\"runtime\"/><sing_goal refs=\"" + f.first +
            "\" range=\"current\" revisions=\"pending\" intent=\"perform\" completion=\"playback\" expected=\"仅作为朗读输入\" feedback=\"none\"/>";
        f.chat.SendData(sample);
        Check((string)Get(f.chat, "m_PendingSelfInspection") == "" && (string)Get(f.chat, "m_LastSelfInspection") == "",
            "Reading user-provided tool syntax executed a body inspection.");
        Check(Get(f.chat, "m_SingingGoal") == null, "Reading user-provided goal syntax registered an executable role goal.");
        Check(((Queue<string>)Get(f.chat, "m_WorkResponses")).Count == 0,
            "The user's reading sample was recorded as the role's completed work response.");
        Check(f.model.Requests.Count == 0 && f.model.reviewRequests == 0,
            "Literal input reading dispatched a work or model request.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }

    private static IEnumerator EmptyInspectionDeliveryIsBounded()
    {
        fixture = new Fixture("empty_self_inspection_delivery_has_budget");
        var f = fixture; yield return null;
        f.BeginUser("检查当前声音队列，再说明结果。"); f.PreviousReply("现在检查。");
        object[] inspect = { "<body_inspect scope=\"runtime\"/>", true };
        Call(f.chat, "ExtractSelfInspection", inspect);
        string evidence = (string)Get(f.chat, "m_PendingSelfInspection");
        Check(evidence.Length > 0, "The actual runtime inspection produced no evidence.");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Call(f.chat, "FireTick", "self-inspection-result");
            Check(f.model.Requests.Count == attempt + 1, "Inspection delivery was duplicated or refused before its bounded retry budget.");
            f.model.CompleteEmpty();
            double deadline = EditorApplication.timeSinceStartup + 5;
            while ((bool)Get(f.chat, "m_AgentRoundInFlight") && EditorApplication.timeSinceStartup < deadline) yield return null;
            Check(!(bool)Get(f.chat, "m_AgentRoundInFlight"), "An empty inspection reply left its formal output pipeline permanently in flight.");
            Check((string)Get(f.chat, "m_PendingSelfInspection") == evidence,
                "An empty response acknowledged and lost the unreceived inspection evidence.");
        }
        Call(f.chat, "FireTick", "self-inspection-result");
        Check(f.model.Requests.Count == 3, "Repeated empty replies caused an unbounded self-inspection delivery loop.");
        Check((string)Get(f.chat, "m_PendingSelfInspection") == evidence,
            "Exhausting delivery retries discarded the evidence instead of retaining it for the next user decision.");
        Check(((string)Get(f.chat, "m_WorkStatus")).Contains("blocked"),
            "Exhausted inspection delivery was marked complete rather than blocked.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }

    private static IEnumerator WorkEpochFencesStreamCallbacks()
    {
        fixture = new Fixture("new_work_epoch_fences_stream_without_generation_change");
        var f = fixture; yield return null;
        f.BeginUser("先检查动作资源。"); f.PreviousReply("稍等。");
        Call(f.chat, "FireTick", "scheduled"); f.model.Decide(Continue("检查资源"));
        Action<string> older = f.model.SnapshotCompletion();
        Action<SpeechText> olderDelta = f.model.SnapshotSpeech();
        int generation = (int)Get(f.chat, "m_FormalResponseGeneration");
        // Non-streaming input accepts its user/work before starting a formal response
        // generation at callback time. A stale stream must not use this narrow window.
        f.BeginUser("取消那个检查；只回答新的问题。");
        Check((int)Get(f.chat, "m_FormalResponseGeneration") == generation, "Fixture failed to exercise the same-generation/new-work window.");
        olderDelta(new SpeechText("旧请求不要发声。", "zh"));
        older("<silent/><body_inspect scope=\"runtime\"/><continue/>");
        Check(((ICollection)Get(f.chat, "m_PendingChunks")).Count == 0 && Get(f.chat, "m_SentenceBuffer").ToString().Length == 0,
            "A previous work epoch emitted speech before the new non-streaming generation began.");
        Check((string)Get(f.chat, "m_PendingSelfInspection") == "" && ((Queue<string>)Get(f.chat, "m_WorkResponses")).Count == 0,
            "Only acknowledgement was fenced: an older epoch still executed its role tools or recorded work in the new request.");
        Check(f.model.Requests.Count == 1, "An old epoch chained into a newly accepted user's request.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }

    private static IEnumerator ConditionalObservationReceipt()
    {
        fixture = new Fixture("only_actually_delivered_conditional_notes_are_acknowledged");
        var f = fixture; yield return null;
        f.BeginUser("保存结果回来后，请如实告诉我。");
        const string note = "public-dropped-song-title-note";
        const string result = "public-song-memory-save-completed";
        Set(f.chat, "m_DroppedSongTitleNote", note);
        Set(f.chat, "m_SongMemoryInFlight", true); Set(f.chat, "m_SongMemoryResultPending", false);
        int generation = (int)Call(f.chat, "BeginFormalResponseGeneration");
        Call(f.chat, "DispatchWorkFeedback", "public formal continuation while storage remains pending", null, generation);
        Check(!LastSystem(f.model.LastRequest).Contains(note), "An incomplete save published a completion-only title note prematurely.");
        f.model.Complete("<silent/>");
        Check((string)Get(f.chat, "m_DroppedSongTitleNote") == note,
            "A response acknowledged a conditional observation that was never sent to it.");
        Set(f.chat, "m_SongMemoryInFlight", false);
        Set(f.chat, "m_LastSongMemoryResult", result); Set(f.chat, "m_SongMemoryResultPending", true);
        generation = (int)Call(f.chat, "BeginFormalResponseGeneration");
        Call(f.chat, "DispatchWorkFeedback", "public completed save result", null, generation);
        string completedFacts = LastSystem(f.model.LastRequest);
        Check(completedFacts.Contains(note) && completedFacts.Contains(result), "A ready save result omitted its title provenance note.");
        Check((string)Get(f.chat, "m_DroppedSongTitleNote") == note && (bool)Get(f.chat, "m_SongMemoryResultPending"),
            "Dispatching a ready result acknowledged it before a formal response completed.");
        f.model.Complete("<silent/>");
        Check((string)Get(f.chat, "m_DroppedSongTitleNote") == "" && !(bool)Get(f.chat, "m_SongMemoryResultPending"),
            "A valid response failed to acknowledge the completed result and accompanying note it received.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }

    private static IEnumerator MemoryAcknowledgementOwnership()
    {
        fixture = new Fixture("memory_acknowledgement_waits_and_retains_undelivered_results");
        var f = fixture; yield return null;
        f.BeginUser("请记住这段歌，保存结果出来后告诉我。");
        f.chat.NotifyUserStartedSpeaking();
        f.BeginRejectedSongMemory(true);
        Check((int)Get(f.chat, "m_SongMemoryOriginWorkEpoch") == (int)Get(f.chat, "m_WorkEpoch"),
            "Memory operation did not capture its originating user before service completion.");
        double until = EditorApplication.timeSinceStartup + .15;
        while (EditorApplication.timeSinceStartup < until) yield return null;
        Check(f.model.Requests.Count == 0 && (bool)Get(f.chat, "m_SongMemoryResultPending"),
            "Memory acknowledgement spoke over the user or consumed its undelivered result.");
        Set(f.chat, "m_UserSpeechActiveForAutonomy", false); Set(f.chat, "m_AutonomyProbeInFlight", true);
        until = EditorApplication.timeSinceStartup + .15;
        while (EditorApplication.timeSinceStartup < until) yield return null;
        Check(f.model.Requests.Count == 0, "Memory acknowledgement cancelled an ongoing private progress review.");
        Set(f.chat, "m_AutonomyProbeInFlight", false);
        until = EditorApplication.timeSinceStartup + 5;
        while (f.model.Requests.Count == 0 && EditorApplication.timeSinceStartup < until) yield return null;
        Check(f.model.Requests.Count == 1, "Memory acknowledgement did not resume when its owner became idle.");
        AssertOriginalUser(f, 1);
        Check(LastSystem(f.model.LastRequest).Contains((string)Get(f.chat, "m_LastSongMemoryResult")) &&
            (bool)Get(f.chat, "m_SongMemoryResultPending"), "Dispatch of the actual save result cleared its pending receipt.");
        f.model.CompleteEmpty();
        until = EditorApplication.timeSinceStartup + 5;
        while ((bool)Get(f.chat, "m_SongMemoryAcknowledgementInFlight") && EditorApplication.timeSinceStartup < until) yield return null;
        Check(!(bool)Get(f.chat, "m_SongMemoryAcknowledgementInFlight") && (bool)Get(f.chat, "m_SongMemoryResultPending"),
            "An empty memory acknowledgement lost evidence or left its delivery permanently in flight.");
        until = EditorApplication.timeSinceStartup + .15;
        while (EditorApplication.timeSinceStartup < until) yield return null;
        Check(f.model.Requests.Count == 1 && Get(f.chat, "m_SongMemoryAcknowledgementCoroutine") == null,
            "An empty acknowledgement automatically requeued an unbounded confirmation loop.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }

    private static IEnumerator MemoryAcknowledgementSupersession()
    {
        fixture = new Fixture("memory_result_survives_old_origin_and_cancelled_delivery");
        var f = fixture; yield return null;
        f.BeginUser("请保存这段歌曲。");
        f.BeginRejectedSongMemory(false);
        int origin = (int)Get(f.chat, "m_SongMemoryOriginWorkEpoch");
        f.BeginUser("先别说保存结果，回答新的问题。");
        // A service result can queue its acknowledgement after a new user was accepted.
        // Its owner comes from the real operation above, never from this delivery callback.
        Call(f.chat, "QueueSongMemoryAcknowledgement", false);
        double until = EditorApplication.timeSinceStartup + .15;
        while (EditorApplication.timeSinceStartup < until) yield return null;
        Check(origin != (int)Get(f.chat, "m_WorkEpoch") && (int)Get(f.chat, "m_SongMemoryOriginWorkEpoch") == origin,
            "Queuing a late result rebound the older save operation to the new user.");
        Check(f.model.Requests.Count == 0 && (bool)Get(f.chat, "m_SongMemoryResultPending"),
            "A result queued after user supersession forced an old confirmation or discarded its storage fact.");

        f.BeginUser("现在请处理保存结果。");
        f.chat.NotifyUserStartedSpeaking();
        // Exercise immediate validation ownership separately from the accepted operation.
        Set(f.chat, "m_SongMemoryAcknowledgementRequired", true);
        Call(f.chat, "CompleteSongMemoryImmediately", "public-current-memory-result", -1);
        until = EditorApplication.timeSinceStartup + .1;
        while (EditorApplication.timeSinceStartup < until) yield return null;
        f.BeginUser("取消刚才的确认，换一个话题。");
        Set(f.chat, "m_UserSpeechActiveForAutonomy", false);
        until = EditorApplication.timeSinceStartup + .15;
        while (EditorApplication.timeSinceStartup < until) yield return null;
        Check(f.model.Requests.Count == 0 && Get(f.chat, "m_SongMemoryAcknowledgementCoroutine") == null &&
            (bool)Get(f.chat, "m_SongMemoryResultPending"), "A queued result escaped its original user while waiting for idle.");

        Set(f.chat, "m_SongMemoryAcknowledgementRequired", true);
        Call(f.chat, "CompleteSongMemoryImmediately", "public-latest-memory-result", -1);
        until = EditorApplication.timeSinceStartup + 5;
        while (f.model.Requests.Count == 0 && EditorApplication.timeSinceStartup < until) yield return null;
        Check(f.model.Requests.Count == 1, "A current immediate memory failure did not retain current ownership.");
        Action<string> oldCallback = f.model.SnapshotCompletion();
        f.chat.NotifyUserStartedSpeaking();
        oldCallback("<silent/><body_inspect scope=\"runtime\"/><continue/>");
        Check((bool)Get(f.chat, "m_SongMemoryResultPending") && (string)Get(f.chat, "m_PendingSelfInspection") == "" &&
            f.model.Requests.Count == 1, "Cancelled memory confirmation consumed evidence or executed a late action.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }

    private static IEnumerator NonAgentMemoryDelivery()
    {
        fixture = new Fixture("non_agent_memory_confirmation_is_one_shot");
        var f = fixture; yield return null;
        Set(f.chat, "m_AgentRunning", false);
        f.BeginUser("请保存这段歌。");
        f.BeginRejectedSongMemory(true);
        double until = EditorApplication.timeSinceStartup + 5;
        while (f.model.Requests.Count == 0 && EditorApplication.timeSinceStartup < until) yield return null;
        Check(f.model.Requests.Count == 1 && (bool)Get(f.chat, "m_SongMemoryResultPending"),
            "Direct conversation lost the save confirmation or cleared its evidence without a receipt.");
        Check(LastSystem(f.model.LastRequest).Contains((string)Get(f.chat, "m_LastSongMemoryResult")),
            "Direct conversation did not receive its explicit storage result.");
        f.model.Complete("<silent/>");
        until = EditorApplication.timeSinceStartup + 5;
        while ((bool)Get(f.chat, "m_SongMemoryAcknowledgementInFlight") && EditorApplication.timeSinceStartup < until) yield return null;
        Check(!(bool)Get(f.chat, "m_SongMemoryAcknowledgementInFlight") && (bool)Get(f.chat, "m_SongMemoryResultPending"),
            "Without Agent receipts, the one-shot acknowledgement must finish while retaining the observation.");
        until = EditorApplication.timeSinceStartup + .15;
        while (EditorApplication.timeSinceStartup < until) yield return null;
        Check(f.model.Requests.Count == 1 && Get(f.chat, "m_SongMemoryAcknowledgementCoroutine") == null,
            "Non-Agent mode introduced an autonomous retry of its retained memory result.");
        Set(f.chat, "m_SongMemoryAcknowledgementRequired", true);
        Call(f.chat, "CompleteSongMemoryImmediately", "public-non-agent-empty-delivery", -1);
        until = EditorApplication.timeSinceStartup + 5;
        while (f.model.Requests.Count < 2 && EditorApplication.timeSinceStartup < until) yield return null;
        Check(f.model.Requests.Count == 2 && (bool)Get(f.chat, "m_SongMemoryAcknowledgementInFlight"),
            "A subsequent direct-conversation memory result could not start its own confirmation.");
        f.model.CompleteEmpty();
        until = EditorApplication.timeSinceStartup + 5;
        while ((bool)Get(f.chat, "m_SongMemoryAcknowledgementInFlight") && EditorApplication.timeSinceStartup < until) yield return null;
        Check(!(bool)Get(f.chat, "m_SongMemoryAcknowledgementInFlight") && (bool)Get(f.chat, "m_SongMemoryResultPending") &&
            (string)Get(f.chat, "m_LastSongMemoryResult") == "public-non-agent-empty-delivery",
            "A true empty non-Agent response failed to release delivery ownership or discarded its unread result.");
        until = EditorApplication.timeSinceStartup + .15;
        while (EditorApplication.timeSinceStartup < until) yield return null;
        Check(f.model.Requests.Count == 2 && Get(f.chat, "m_SongMemoryAcknowledgementCoroutine") == null,
            "A true empty non-Agent acknowledgement automatically retried itself.");
        SaveScenario(f); f.Dispose(); fixture = null;
    }

    private static void SaveScenario(Fixture f)
    {
        scenarios.Add(new JObject { ["name"] = f.name, ["passed"] = true,
            ["dedicatedReviews"] = f.model.reviewRequests, ["regularRequests"] = f.model.regularRequests,
            ["finalRequests"] = new JArray(f.model.Requests.Select(p => p.DeepClone())) });
    }

    private static string Continue(string intent) => new JObject { ["work_status"] = "continue", ["proceed"] = true,
        ["intent"] = intent, ["work_evidence"] = "公开测试中的原请求尚未由真实结果满足。",
        ["singing_goal_status"] = "none", ["singing_goal_evidence"] = "",
        ["singing_goal_expected"] = ExpectedSinging("", "none"), ["wait_seconds"] = 45 }.ToString();

    private static JObject ExpectedSinging(string refs, string range) => new JObject {
        ["origin"] = refs.Length == 0 ? "none" : "user_request", ["request_quote"] = refs.Length == 0 ? "" : fixture.user,
        ["refs"] = refs, ["range"] = range, ["start_seconds"] = null, ["end_seconds"] = null };

    private static string LastSystem(JObject request)
    {
        var last = ((JArray)request["messages"]).Last;
        return (string)last?["role"] == "system" ? (string)last["content"] ?? "" : "";
    }

    private static void AssertOriginalUser(Fixture f, int count)
    {
        var users = ((JArray)f.model.LastRequest["messages"]).Where(p => (string)p["role"] == "user").ToList();
        Check(users.Count == count && (string)users.Last()["content"] == f.user, "A tool/progress/continue frame became a fake user message or replaced the real utterance.");
        Check(f.model.m_DataList.Count(p => p.role == "user") == count, "Transient observations mutated the saved user history.");
        Check(f.model.regularRequests == 0, "A same-work continuation used the normal new-user request API.");
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string name;
        public readonly GameObject host;
        public readonly ChatSample chat;
        public readonly AgentWorkLoopFakeLLM model;
        public readonly SenseVoiceSpeechToText sense;
        public readonly string first, second;
        public string user;

        public Fixture(string scenario)
        {
            name = scenario; host = new GameObject("AgentWorkLoop_" + scenario); host.SetActive(false);
            chat = host.AddComponent<ChatSample>(); chat.enabled = false;
            model = host.AddComponent<AgentWorkLoopFakeLLM>(); model.enabled = false;
            model.builder = host.AddComponent<ChatQW>(); model.builder.enabled = false;
            model.builder.m_Backend = ChatQW.BackendType.Local;
            sense = host.AddComponent<SenseVoiceSpeechToText>(); sense.enabled = false;
            var tts = host.AddComponent<TTS>(); tts.enabled = false;
            var audio = host.AddComponent<AudioSource>(); audio.playOnAwake = false;
            var text = new GameObject("Text", typeof(RectTransform), typeof(UnityEngine.UI.Text)); text.transform.SetParent(host.transform);
            var button = new GameObject("Send", typeof(RectTransform), typeof(UnityEngine.UI.Button)); button.transform.SetParent(host.transform);
            var input = new GameObject("Input", typeof(RectTransform), typeof(UnityEngine.UI.InputField)); input.transform.SetParent(host.transform);
            Set(chat, "m_TextBack", text.GetComponent<UnityEngine.UI.Text>()); Set(chat, "m_CommitMsgBtn", button.GetComponent<UnityEngine.UI.Button>());
            Set(chat, "m_InputWord", input.GetComponent<UnityEngine.UI.InputField>());
            Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = model, m_TextToSpeech = tts, m_SpeechToText = sense });
            Set(chat, "m_AudioSource", audio); Set(chat, "m_ChatHistory", new List<string>());
            Set(chat, "m_PersistSubtitleSettings", false); Set(chat, "m_EnableSubtitleTranslation", false);
            Set(chat, "m_SystemNoticeMode", SystemNoticeMode.Hidden); Set(chat, "m_EnableLatencyFiller", false);
            Set(chat, "m_PrewarmHumSVCOnSingingSkillLoad", false);
            Set(chat, "m_EnableMemoryRecall", false); Set(chat, "m_EnableScreenVision", false);
            Set(chat, "m_LogAgentLoop", true); Set(chat, "m_LogStreamTimings", false); Set(chat, "m_MinTickSec", 300f); Set(chat, "m_DefaultTickSec", 300f);
            model.m_DataList.Add(new LLM.SendData("system", "公开的角色测试。不把说过的承诺或输入资料视为已执行结果。"));
            first = Archive("public phrase alpha", 1); second = Archive("public phrase beta", 2);
            host.SetActive(true); audio.Stop(); Set(chat, "m_AgentRunning", true);
        }

        public void BeginUser(string utterance)
        {
            user = utterance; Call(chat, "SetCurrentUserInput", utterance, "public fixture");
            model.m_DataList.Add(new LLM.SendData("user", utterance));
            Call(chat, "PrepareActiveSkillsForRound", utterance, "user-spoke");
        }

        public void PreviousReply(string reply)
        {
            model.m_DataList.Add(new LLM.SendData("assistant", reply)); Call(chat, "RecordWorkResponse", reply);
        }

        public void Confirm(string clip)
        {
            object[] args = { "<clip_confirm ref=\"" + clip + "\"/>", true };
            Call(chat, "ExtractAndApplyClipConfirmTags", args);
            Check((string)args[0] == "" && sense.DescribePracticePhrases().Any(p => p.ClipRef == clip && !p.PendingConfirmation), "Synthetic recording failed actual source-confirmation processing.");
        }

        public void BeginRejectedSongMemory(bool acknowledgement)
        {
            Type requestType = typeof(ChatSample).GetNestedType("AgentSongMemoryRequest", BindingFlags.NonPublic);
            object request = Activator.CreateInstance(requestType);
            requestType.GetField("Action").SetValue(request, "remember");
            requestType.GetField("SourceRef").SetValue(request, "clip:public-nonexistent-memory-source");
            // The real source validator returns before network/disk I/O. This exercises the
            // production operation and completion path without persisting fixture material.
            Call(chat, "BeginSongMemory", request, acknowledgement);
            Check(!(bool)Get(chat, "m_SongMemoryInFlight") && (bool)Get(chat, "m_SongMemoryResultPending") &&
                ((string)Get(chat, "m_LastSongMemoryResult")).Contains("未改用最近录音"),
                "The public invalid-source memory fixture did not reach real service validation.");
        }

        public string UnselectedClip() => Archive("public phrase gamma", 3, false);
        private string Archive(string text, int session, bool stage = true)
        {
            Type type = typeof(SenseVoiceSpeechToText);
            Type responseType = type.GetNestedType("Response", BindingFlags.NonPublic);
            var payload = new JObject { ["text"] = text, ["singing_text"] = text, ["audio_event"] = "Speech",
                ["singing_probability"] = .48f, ["singing_analysis_available"] = true,
                ["singing_start_seconds"] = 1f, ["singing_end_seconds"] = 2.4f,
                ["singing_recovery_start_seconds"] = 0f, ["singing_recovery_end_seconds"] = 2.4f,
                ["pitch_timeline_frame_seconds"] = .1f };
            object response = JsonUtility.FromJson(payload.ToString(), responseType);
            responseType.GetField("pitch_timeline_midi").SetValue(response, Enumerable.Repeat(60f, 24).ToArray());
            float[] pcm = Enumerable.Range(0, 38400).Select(i => .1f * Mathf.Sin(i * .086f)).ToArray();
            byte[] wav = (byte[])type.GetMethod("EncodeMonoPcm16Wav", Flags).Invoke(null, new object[] { pcm, 16000 });
            sense.BeginLiveRecordingCandidateSession();
            type.GetMethod("ArchiveCompletedCapture", Flags).Invoke(sense, new object[] { response, wav, 10f + session, session });
            string clip = sense.DescribeQuarantinedSingingCandidates().Single(p => p.RecordingSequence == session).ClipRef;
            // Capture admission and source ownership are separate. Stage this known,
            // public fixture's clean window while leaving its source unconfirmed.
            if (stage && !sense.TryPrepareSingingClip(clip, false, "clean", out _, out string error))
                throw new InvalidOperationException("Public capture window could not be prepared: " + error);
            return clip;
        }

        public void Dispose()
        {
            if (host == null) return;
            Set(chat, "m_AgentRunning", false); chat.StopAllCoroutines(); UnityEngine.Object.DestroyImmediate(host);
        }
    }

    private static object Call(object target, string name, params object[] args) => typeof(ChatSample).GetMethod(name, Flags).Invoke(target, args);
    private static object Get(object target, string name) => typeof(ChatSample).GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => typeof(ChatSample).GetField(name, Flags).SetValue(target, value);
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
}

public sealed class AgentWorkLoopFakeLLM : LLM
{
    public ChatQW builder;
    public int reviewRequests, ephemeralRequests, regularRequests;
    public string LastReview;
    public readonly List<JObject> Requests = new List<JObject>();
    public JObject LastRequest => Requests.Last();
    private Action<string> review, completion;
    private Action<SpeechText> speech;

    public override void PostWorkReviewMsg(string prompt, Action<string> callback)
    { reviewRequests++; LastReview = prompt; review = callback; }
    public override void PostEphemeralMsg(string prompt, Action<string> callback)
    { ephemeralRequests++; LastReview = prompt; review = callback; }
    public override void PostSpeechFeedbackStream(string context, string feedback, Action<SpeechText> delta, Action<string> done, string imageDataUrl = null)
    { Capture(context, feedback, delta, done); }
    public override void PostSpeechContinuationStream(string context, Action<SpeechText> delta, Action<string> done, string imageDataUrl = null)
    { Capture(context, null, delta, done); }
    public override void PostSpeechStream(string prompt, Action<SpeechText> delta, Action<string> done, string imageDataUrl = null, bool recordAssistantHistory = true)
    { regularRequests++; m_DataList.Add(new SendData("user", prompt)); Capture(RequestContext, null, delta, done); }

    private void Capture(string context, string facts, Action<SpeechText> delta, Action<string> done)
    {
        var method = typeof(ChatQW).GetMethod("BuildRequestJsonForMessagesWithFeedback", BindingFlags.Instance | BindingFlags.NonPublic);
        Requests.Add(JObject.Parse((string)method.Invoke(builder, new object[] { m_DataList, true, context, facts })));
        speech = delta; completion = done;
    }

    public void Decide(string output) => review?.Invoke(output);
    public Action<string> SnapshotReview() => review;
    public Action<string> SnapshotCompletion() => completion;
    public Action<SpeechText> SnapshotSpeech() => speech;
    public void CompleteEmpty() => completion?.Invoke("");
    public void Complete(string output)
    {
        var done = completion;
        var channels = new RoleOutputChannels(speech); channels.Push(output); channels.Finish();
        m_DataList.Add(new SendData("assistant", output));
        done?.Invoke(channels.ToExecutableText());
    }
}
