using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Small dependency-free regression suite for the text side of the generic Skill router.
/// It intentionally uses reflection so the runtime policy can keep its implementation types private.
/// </summary>
public static partial class SkillRoutingRegression
{
    private struct Case
    {
        public string Text;
        public string Expected;

        public Case(string text, string expected)
        {
            Text = text;
            Expected = expected;
        }
    }

    [MenuItem("Tools/NeEEvA/Run Skill Routing Regression")]
    public static void RunInteractive()
    {
        RunOrThrow();
        Debug.Log("[SkillRoutingRegression] all cases passed");
    }

    public static void RunBatch()
    {
        try
        {
            RunOrThrow();
            Debug.Log("[SkillRoutingRegression] all cases passed");
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorApplication.Exit(1);
        }
    }

    private static void RunOrThrow()
    {
        RunTimeOrderedTranscriptRegression();
        RunCompletedCaptureArchiveRegression();
        RunRecordingEvidenceRetentionRegression();
        RunShortMixedRecordingAdmissionRegression();
        RunRoleOutputChannelsRegression();
        RunPlainSpeechPromptRegression();
        RunRawResponseDiagnosticsRegression();
        RunMalformedToolFeedbackRegression();
        RunSingingRequestFactsRegression();
        RunPerClipPitchRetentionRegression();
        Type chatType = typeof(ChatSample);
        MethodInfo preparation = chatType.GetMethod("CanPreparePlannedSingingChunks", BindingFlags.Static | BindingFlags.NonPublic);
        if (!(bool)preparation.Invoke(null, new object[] { 1, 1, true, false }) ||
            (bool)preparation.Invoke(null, new object[] { 1, 0, true, true }) ||
            (bool)preparation.Invoke(null, new object[] { 1, 1, false, true }))
            throw new Exception("Single-phrase musical plans do not use the planned renderer or bypass malformed-plan checks.");
        FieldInfo definitionsField = chatType.GetField(
            "s_SkillRouteDefinitions", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo classifyMethod = chatType.GetMethod(
            "ClassifySkillUserIntent", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo carryUserTurnSkillMethod = chatType.GetMethod(
            "ShouldCarrySkillForCurrentUserTurn",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo commitPreparedBridgeMethod = chatType.GetMethod(
            "ShouldCommitPreparedBridgeAfterFinal",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (definitionsField == null || classifyMethod == null ||
            carryUserTurnSkillMethod == null || commitPreparedBridgeMethod == null)
            throw new MissingMemberException("Skill routing internals were renamed without updating the regression suite.");

        Array definitions = definitionsField.GetValue(null) as Array;
        if (definitions == null || definitions.Length == 0)
            throw new InvalidOperationException("No Skill route definitions are registered.");
        object singing = definitions.GetValue(0);

        var cases = new[]
        {
            new Case("现在其实已经不需要唱歌了，所以把相关技能关掉。", "Mention"),
            new Case("刚才唱歌的技能你关掉了吗？还是现在仍然开着？", "Mention"),
            new Case("我没事，那个你现在有把那个唱歌相关的智能关掉吗？", "Mention"),
            new Case("把唱歌相关的那些技能给关掉。", "Mention"),
            new Case("这首歌的旋律很特别。", "Mention"),
            new Case("请再把它唱出来。", "Mention"),
            new Case("不要停止唱歌。", "Mention"),
            new Case("先别主动唱，一会儿再说。", "Mention"),
            new Case("歌唱機能をオフにした？", "Mention"),
            new Case("歌唱機能をオフにして。", "Mention"),
            new Case("Did you turn singing off?", "Mention"),
            new Case("Please sing it again.", "Mention"),
            new Case("第一二三段不要唱出来，只需要管第四段和第五段。", "Mention"),
            new Case("第三段在当前基准上再提高半个音。", "Mention"),
            new Case("整首下降一个全音听听。", "Mention"),
            new Case("我从来没有禁止过你，你想唱随时都能唱。", "Mention"),
            new Case("唱歌同意你唱歌。", "Mention"),
        };

        for (int i = 0; i < cases.Length; i++)
        {
            string actual = classifyMethod.Invoke(null, new[] { (object)cases[i].Text, singing }).ToString();
            if (!string.Equals(actual, cases[i].Expected, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Case {i + 1} failed: expected {cases[i].Expected}, got {actual}; text={cases[i].Text}");
        }

        bool chainCarriesSkill = (bool)carryUserTurnSkillMethod.Invoke(
            null, new object[] { false, true });
        bool nextUserTurnReusesPin = (bool)carryUserTurnSkillMethod.Invoke(
            null, new object[] { true, true });
        bool unpinnedChainLoadsSkill = (bool)carryUserTurnSkillMethod.Invoke(
            null, new object[] { false, false });
        if (!chainCarriesSkill || nextUserTurnReusesPin || unpinnedChainLoadsSkill)
            throw new InvalidOperationException(
                "A Skill is no longer pinned exactly to the current real-user chain.");

        bool lowSimilaritySpeechCommits = (bool)commitPreparedBridgeMethod.Invoke(
            null, new object[] { true, false, true, 0.57f, 0.72f });
        bool matchingSpeechCommits = (bool)commitPreparedBridgeMethod.Invoke(
            null, new object[] { true, false, true, 0.86f, 0.72f });
        bool unconfirmedModeCommits = (bool)commitPreparedBridgeMethod.Invoke(
            null, new object[] { false, false, true, 0.99f, 0.72f });
        bool confirmedSingingCommits = (bool)commitPreparedBridgeMethod.Invoke(
            null, new object[] { true, true, true, 0.20f, 0.72f });
        bool staleSpeechBasedSingingCommits = (bool)commitPreparedBridgeMethod.Invoke(
            null, new object[] { true, true, false, 0.99f, 0.72f });
        if (lowSimilaritySpeechCommits || !matchingSpeechCommits ||
            unconfirmedModeCommits || !confirmedSingingCommits ||
            staleSpeechBasedSingingCommits)
            throw new InvalidOperationException(
                "A speculative bridge can be irreversibly played before final evidence approves it, or a speech-based waiting opener survives a completed singing turn.");

        RunAutonomyIntentRegression(chatType, singing);
        RunEvidenceGroundingPromptRegression();
        RunSingingPolicyRegression(chatType);
        RunSongCatalogInspectionRegression(chatType);
        RunToolCorrectionRegression(chatType);
        RunSingingToolRoutingRegression(chatType);
        RunSingingTransactionRegression(chatType);
        RunSingingModeFusionRegression(chatType);
        RunPracticeConfirmationAndMemoryGroundingRegression(chatType);
        RunSubtitleOverlayRegression(chatType);
        RunTurnBoundaryRegression();
        RunEffectiveActivityRegression();
        RunLatencyHandoffRegression();
        RunFormalAudioChoiceRegression();
        RunContinuationDispatchRegression();
        RunCaptureChronologyRegression();
        RunStreamingObservedModeLatchRegression();
        RunAuthoritativeUserSemanticTextRegression();
        RunCacheAndVisualPayloadRegression();
        RunInterruptedQuestionRecoveryRegression();
        RunCoveredQuietReuseRegression();
        RunCalibratedRequestWindowRegression();
    }

    private static void RunTimeOrderedTranscriptRegression()
    {
        Type stt = typeof(SenseVoiceSpeechToText);
        Type responseType = stt.GetNestedType("Response", BindingFlags.NonPublic);
        const string json = "{\"turn_segments_schema\":1,\"whole_text\":\"我继续唱轮\"," +
            "\"turn_segments\":[{\"id\":1,\"start_seconds\":3.4,\"end_seconds\":14.1," +
            "\"type\":\"singing_candidate\",\"text\":\"忘れたくないこと\",\"language\":\"ja\",\"status\":\"ok\"}," +
            "{\"id\":2,\"start_seconds\":14.1,\"end_seconds\":19.1,\"type\":\"uncertain\"," +
            "\"text\":\"请把这次唱出来\",\"status\":\"ok\",\"alternatives\":[{" +
            "\"source\":\"boundary_overlap\",\"text\":\"把我刚才唱的唱出来\"}]}]}";
        object response = JsonUtility.FromJson(json, responseType);
        object segments = responseType.GetField("turn_segments").GetValue(response);
        MethodInfo format = stt.GetMethod("BuildTimeOrderedTranscriptEvidence", BindingFlags.Static | BindingFlags.NonPublic);
        string evidence = (string)format.Invoke(null, new object[] { segments, "我继续唱轮" });
        if (!evidence.Contains("忘れたくないこと") || !evidence.Contains("请把这次唱出来") ||
            !evidence.Contains("boundary_overlap") || !evidence.Contains("我继续唱轮") ||
            !evidence.Contains("可以先询问") || !evidence.Contains("confidence_available"))
            throw new Exception("Mixed ASR timeline lost lyrics, tail, alternatives, or uncertainty facts.");
        if (evidence.IndexOf("忘れたくないこと", StringComparison.Ordinal) >=
            evidence.IndexOf("请把这次唱出来", StringComparison.Ordinal))
            throw new Exception("Mixed ASR evidence changed chronological order.");
    }

    private static void RunCompletedCaptureArchiveRegression()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type stt = typeof(SenseVoiceSpeechToText);
        var host = new GameObject("CompletedCaptureArchiveRegression");
        host.SetActive(false);
        try
        {
            var sense = host.AddComponent<SenseVoiceSpeechToText>();
            Type ticketType = stt.GetNestedType("AnalysisTicket", BindingFlags.NonPublic);
            object ticket = Activator.CreateInstance(ticketType);
            ticketType.GetField("CompletedCapture").SetValue(ticket, true);
            ((System.Collections.IList)stt.GetField("m_AnalysisTickets", flags).GetValue(sense)).Add(ticket);
            sense.CancelInputAnalyses();
            if (!(bool)ticketType.GetField("Detached").GetValue(ticket) ||
                (bool)ticketType.GetField("Cancelled").GetValue(ticket))
                throw new Exception("New speech still cancels completed capture analysis.");

            // Valid 8-second mono PCM WAV; clean is 2–6s, expanded is 1–7s.
            byte[] wav;
            using (var stream = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(stream))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + 16000 * 8 * 2);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
                writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(16000 * 8 * 2);
                for (int i = 0; i < 16000 * 8; i++) writer.Write((short)(1000 * Math.Sin(i * .08)));
                wav = stream.ToArray();
            }
            Type responseType = stt.GetNestedType("Response", BindingFlags.NonPublic);
            object response = JsonUtility.FromJson("{\"text\":\"前轮说话与歌\",\"singing_text\":\"初めて\"," +
                "\"singing_analysis_available\":true,\"singing_start_seconds\":2,\"singing_end_seconds\":6," +
                "\"singing_recovery_start_seconds\":1,\"singing_recovery_end_seconds\":7," +
                "\"pitch_timeline_frame_seconds\":0.1,\"singing_language\":\"ja\"}", responseType);
            responseType.GetField("pitch_timeline_midi").SetValue(response, Enumerable.Repeat(60f, 80).ToArray());
            object oldText = stt.GetProperty("LastText").GetValue(sense);
            object oldEvidence = stt.GetField("m_LastSingingEvidence", flags).GetValue(sense);
            MethodInfo archive = stt.GetMethod("ArchiveCompletedCapture", flags);
            archive.Invoke(sense, new object[] { response, wav, Time.realtimeSinceStartup, 101 });
            archive.Invoke(sense, new object[] { response, wav, Time.realtimeSinceStartup, 101 });
            var candidates = sense.DescribeQuarantinedSingingCandidates();
            if (candidates.Count != 1 || candidates[0].SourceStatus != "pending" ||
                candidates[0].PlaybackStatus != "ready" || Math.Abs(candidates[0].Seconds - 4f) > .05f ||
                candidates[0].SingingSegmentText != "初めて" || sense.PracticePhraseCount != 0 ||
                !Equals(oldText, stt.GetProperty("LastText").GetValue(sense)) ||
                !ReferenceEquals(oldEvidence, stt.GetField("m_LastSingingEvidence", flags).GetValue(sense)))
                throw new Exception("Detached analysis lost audio, duplicated candidates, auto-confirmed, or overwrote current perception.");
            if (!sense.ConfirmQuarantinedSingingCandidate(candidates[0].CandidateId, out int index) || index != 1)
                throw new Exception("Archived audio cannot enter practice after explicit confirmation.");
            if (sense.DescribePracticePhrases()[0].ClipRef != candidates[0].ClipRef)
                throw new Exception("Confirmation replaced the original clip identity.");
            stt.GetField("m_LastSingingAudioBytes", flags).SetValue(sense, wav);
            stt.GetField("m_LastSingingAudioTime", flags).SetValue(sense, Time.realtimeSinceStartup);
            stt.GetField("m_LastSingingCacheCaptureSessionSerial", flags).SetValue(sense, 50);
            MethodInfo resolveSave = stt.GetMethod("TryResolveSongSaveAudio", flags);
            object[] implicitSave = { "", null, null };
            object[] explicitSave = { "stable:1", null, null };
            if ((bool)resolveSave.Invoke(sense, implicitSave) ||
                !(bool)resolveSave.Invoke(sense, explicitSave) ||
                ((byte[])explicitSave[1]).Length >= wav.Length)
                throw new Exception("Save silently substituted old audio, or explicit stable selection failed.");

            responseType.GetField("singing_end_seconds").SetValue(response, 4f);
            archive.Invoke(sense, new object[] { response, wav, Time.realtimeSinceStartup + 1f, 102 });
            var shortCandidate = sense.DescribeQuarantinedSingingCandidates().Single();
            if (shortCandidate.PlaybackStatus != "evidence_only")
                throw new Exception("Short capture was automatically promoted without the role selecting it.");
            if (sense.PrepareQuarantinedCaptureForConfirmation(shortCandidate.CandidateId, "expanded", 9f, 0f, out _) ||
                !sense.PrepareQuarantinedCaptureForConfirmation(shortCandidate.CandidateId, "clean", 0f, 0f, out string recoveryError) ||
                !sense.ConfirmQuarantinedSingingCandidate(shortCandidate.CandidateId, out int shortIndex) || shortIndex != 2)
                throw new Exception("Explicit short-capture recovery failed or accepted an invalid range.");
            var recovered = sense.DescribePracticePhrases()[1];
            if (Math.Abs(recovered.Seconds - 2f) > .05f || Math.Abs(recovered.OriginalExpandedSeconds - 6f) > .05f)
                throw new Exception("Recovery destroyed original boundaries or substituted unrelated audio.");
            if (!sense.TryBuildSingingPracticeComposition(1, 0f, out _, out string compositionError, "stable:2", false))
                throw new Exception("Explicit short phrase cannot execute: " + compositionError);
            RunUnifiedClipExecutionRegression(host, sense, response, responseType, archive, wav);
            MethodInfo observe = stt.GetMethod("PreserveEarlierCaptureTranscript", flags);
            observe.Invoke(sense, new object[] { "私の心のプロジェクター", 10f });
            observe.Invoke(sense, new object[] { "错误的最终碎片", 14f });
            if (!sense.BuildLastMixedTurnEvidence().Contains("私の心のプロジェクター"))
                throw new Exception("Final ASR erased the earlier complete hypothesis.");
            sense.BeginLiveRecordingCandidateSession();
            if (sense.BuildLastMixedTurnEvidence().Contains("私の心のプロジェクター"))
                throw new Exception("Preview hypotheses leaked into another recording.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
        MethodInfo fact = typeof(ChatSample).GetMethod("BuildContinuationExecutionFact", BindingFlags.Static | BindingFlags.NonPublic);
        string idle = (string)fact.Invoke(null, new object[] { 2, false });
        if (!idle.Contains("没有歌声生成") || !idle.Contains("询问") ||
            (string)fact.Invoke(null, new object[] { 2, true }) != "")
            throw new Exception("Execution feedback invents idle work or removes the role's choice to ask.");
    }

    private static void RunRoleOutputChannelsRegression()
    {
        string raw = "<thought>用户要求回唱。clip:secret 和范围推敲不朗读。</thought>" +
            "<thought>示例 <sing refs=\"clip:wrong\"/><say>内部也不说。</say></thought>" +
            "`<sing refs=\"clip:quoted\"/>`" +
            "<say>少し待ってね。</say><thought>接下来仍是心里话。</thought>" +
            "<sing refs=\"clip:real\" reason=\"a > b\"/>" +
            "<say>聞いてね。</say><next in=\"5s\"/>";
        for (int size = 1; size <= raw.Length; size++)
        {
            var router = new RoleOutputChannels();
            var streamed = new System.Text.StringBuilder();
            for (int at = 0; at < raw.Length; at += size)
                streamed.Append(router.Push(raw.Substring(at, Math.Min(size, raw.Length - at))));
            if (streamed.ToString() != "少し待ってね。\n聞いてね。\n" ||
                streamed.ToString() != router.Speech)
                throw new Exception("Private text reached streamed speech at chunk size " + size);
            string routed = router.ToExecutableText();
            if (!routed.Contains("clip:real") || !routed.Contains("<next") || routed.Contains("secret") ||
                routed.Contains("wrong") || routed.Contains("quoted") || routed.Contains("心里"))
                throw new Exception("Thought/example calls leaked into executable output.");
        }
        // Real failed replies from 9/8 and transitions, at every chunk boundary.
        var plainCases = new Dictionary<string, string> {
            { "ふふっ、ユウ。午後のご機嫌はいかがかしら？<next in=\"15s\"/>", "ふふっ、ユウ。午後のご機嫌はいかがかしら？" },
            { "ふふっ、ユウさん。午後のひと時は、穏やかですね。", "ふふっ、ユウさん。午後のひと時は、穏やかですね。" },
            { "ええ、聞こえていますよ、ユウさん。<next in=\"15s\"/>", "ええ、聞こえていますよ、ユウさん。" },
            { "はい、ユウさん。聞こえています。少し声が小さめでしたけど、大丈夫ですよ。何かご用ですか？", "はい、ユウさん。聞こえています。少し声が小さめでしたけど、大丈夫ですよ。何かご用ですか？" },
            { "ふふっ、聞こえてるわ。午後の陽ざしが気持ちいい日ね。ユウ、今日も忙しい？", "ふふっ、聞こえてるわ。午後の陽ざしが気持ちいい日ね。ユウ、今日も忙しい？" },
            { "闻いてね。<thought>内部<think>嵌套</think><sing refs=\"clip:wrong\"/></thought>では。", "闻いてね。では。" },
            { "听到了。<silent/>先静静听。<say>不能撤销静默</say><next in=\"5s\"/>", "听到了。" },
            { "<silent/>心里话。<thought>更多思考</thought>仍是心里话。", "" },
            { "<thought>先想想。</thought>听到了。", "听到了。" },
            { "<think>不朗读的内容</think>听到了。", "听到了。" },
            { "<thought>未闭合，保持隔离。", "" },
            { "<say>听到了。</say>还有一句。", "听到了。\n还有一句。" },
            { "普通正文。<sing refs=\"clip:real\" reason=\"a > b\"/>下一句。", "普通正文。下一句。" },
            { "正文。`<sing refs=\"clip:wrong\"/>`继续。", "正文。继续。" },
            { "正文。```xml\n<sing refs=\"clip:wrong\"/>\n```继续。", "正文。继续。" },
            { "<say>正文。<sing refs=\"clip:wrong\"/></say>", "正文。\n" }
        };
        foreach (var test in plainCases)
        {
            for (int size = 1; size <= test.Key.Length; size++)
            {
                var router = new RoleOutputChannels();
                var streamed = new System.Text.StringBuilder();
                for (int at = 0; at < test.Key.Length; at += size)
                    streamed.Append(router.Push(test.Key.Substring(at, Math.Min(size, test.Key.Length - at))));
                if (streamed.ToString() != test.Value || router.Speech != test.Value ||
                    router.ToExecutableText() != RoleOutputChannels.Parse(test.Key).ToExecutableText() ||
                    router.ToExecutableText().Contains("clip:wrong"))
                    throw new Exception("Plain speech/private/tool routing failed: " + test.Key + " chunk=" + size);
                if (test.Key.Contains("clip:real") && !router.ToExecutableText().Contains("clip:real"))
                    throw new Exception("Default audible mode swallowed a real top-level tool.");
            }
        }
        if (new RoleOutputChannels().Push("听") != "听")
            throw new Exception("Ordinary prose waits for a wrapper or complete response before streaming.");
        var malformedCases = new Dictionary<string, string> {
            { "听到了。《sing refs=\"clip:bad\" reason=\"标点。和 > 也不泄露\" />后面的话。<next in=\"5s\"/>", "听到了。后面的话。" },
            { "＜clip_confirm ref=\"clip:bad\"/＞", "" },
            { "〈sing refs=\"clip:bad\"/〉", "" },
            { "《sing refs=\"未结束", "" },
            { "《One Last Kiss》很好听。", "《One Last Kiss》很好听。" },
            { "《sing》是书名。", "《sing》是书名。" },
            { "《sing", "《sing" },
            { "<thought>《sing refs=\"example\" /></thought>台词。", "台词。" }
        };
        foreach (var test in malformedCases)
            for (int size = 1; size <= test.Key.Length; size++)
            {
                var router = new RoleOutputChannels();
                string spoken = "";
                for (int at = 0; at < test.Key.Length; at += size)
                    spoken += router.Push(test.Key.Substring(at, Math.Min(size, test.Key.Length - at)));
                spoken += router.Finish();
                bool shouldFail = test.Key.Contains("clip:bad") || test.Key.Contains("未结束");
                if (spoken != test.Value || router.HasMalformedTool != shouldFail ||
                    (shouldFail && router.ToExecutableText().Contains("<next")))
                    throw new Exception("Malformed tool leaked, executed, or swallowed a book title: " + test.Key);
            }
        var toolOnly = RoleOutputChannels.Parse("<silent/><sing refs=\"clip:real\"/>");
        if (toolOnly.HasSpeech || !toolOnly.ToExecutableText().Contains("<sing"))
            throw new Exception("Silent action was removed by speech-channel routing.");
        if (toolOnly.ToExecutableText(false).Contains("<sing"))
            throw new Exception("Truncated output still dispatches an action.");
        Type handlerType = typeof(ChatQW).GetNestedType("SSEDownloadHandler", BindingFlags.NonPublic);
        string delivered = "";
        var handler = (UnityEngine.Networking.DownloadHandler)Activator.CreateInstance(handlerType,
            new object[] { new Action<string>(delta => delivered += delta) });
        try
        {
            byte[] finish = System.Text.Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n");
            handlerType.GetMethod("ReceiveData", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(handler, new object[] { finish, finish.Length });
            if ((string)handlerType.GetProperty("FinishReason").GetValue(handler) != "length")
                throw new Exception("Streaming completion lost the truncation reason.");
            byte[] content = System.Text.Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"不能朗读\",\"content\":\"听到了。\"}}]}\n\n");
            foreach (byte b in content)
                handlerType.GetMethod("ReceiveData", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(handler, new object[] { new[] { b }, 1 });
            if (delivered != "听到了。")
                throw new Exception("Provider reasoning leaked into speech or split UTF-8 lost text.");
        }
        finally { handler.Dispose(); }
        var malformed = RoleOutputChannels.Parse("<thought>未闭合<say>不应泄露</say><sing refs=\"clip:wrong\"/>");
        if (malformed.HasSpeech || malformed.HasActions)
            throw new Exception("Unclosed private channel failed open.");
        var boundary = new System.Text.StringBuilder();
        typeof(ChatSample).GetMethod("AppendExpansionEffect", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { boundary, 17.73f, 28.27f, 13.33f, 28.27f });
        if (!boundary.ToString().Contains("头部=4.40s，尾部=0.00s") ||
            !boundary.ToString().Contains("不会补回任何尾音"))
            throw new Exception("Expansion effect can still be mistaken for a restored singing tail.");
        Debug.Log("[SkillRoutingRegression] plain speech, optional say, private/tool isolation and expansion facts passed");
    }

    private static void RunPlainSpeechPromptRegression()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var host = new GameObject("PlainSpeechPromptRegression");
        host.SetActive(false);
        try
        {
            var model = host.AddComponent<ChatQW>();
            var behavior = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/AIChatTookit/Prompts/behavior.txt");
            var singing = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/AIChatTookit/Prompts/Skills/singing.txt");
            typeof(LLM).GetField("m_PromptFiles", flags).SetValue(model, new[] { behavior });
            typeof(LLM).GetField("m_SkillFiles", flags).SetValue(model, new[] { singing });
            string system = model.BuildSystemPrompt();
            if (!system.Contains("实时朗读普通正文") || system.Contains("未标记文字不朗读") ||
                system.Contains("只通过语音合成 (TTS) 实时朗读你放在") ||
                system.Contains("不发声内容\"的唯一信号"))
                throw new Exception("Effective character prompt still requires say or contradicts thought.");
            model.m_DataList.Add(new LLM.SendData("system", system));
            model.m_DataList.Add(new LLM.SendData("user", "听得到吗？"));
            foreach (bool skill in new[] { false, true })
            {
                model.SetActiveSkills(skill ? new[] { "singing" } : new string[0]);
                foreach (bool stream in new[] { false, true })
                {
                    string json = (string)typeof(ChatQW).GetMethod("BuildRequestJson", flags)
                        .Invoke(model, new object[] { stream, "" });
                    var messages = (Newtonsoft.Json.Linq.JArray)Newtonsoft.Json.Linq.JObject.Parse(json)["messages"];
                    string effective = messages.ToString();
                    if (!effective.Contains("实时朗读普通正文") || effective.Contains("未标记文字不朗读") ||
                        effective.Contains("对外发言放在 <say>"))
                        throw new Exception("Main request has missing/conflicting speech protocol.");
                    if (skill && !effective.Contains("普通正文直接作为对外发言"))
                        throw new Exception("Singing skill did not receive the simplified speech protocol.");
                    if (!effective.Contains("先想后问时") || effective.Contains("每句都用") ||
                        effective.Contains("推荐节奏：一句话") || !effective.Contains("没有调度标签时系统采用默认等待"))
                        throw new Exception("Effective prompts still encourage silent questions or per-sentence continuation loops.");
                }
            }
            string auxiliary = (string)typeof(ChatQW).GetMethod("BuildMemoryClaimsRequestJson", flags)
                .Invoke(model, new object[] { "只输出严格 JSON" });
            var auxMessages = (Newtonsoft.Json.Linq.JArray)Newtonsoft.Json.Linq.JObject.Parse(auxiliary)["messages"];
            if (auxMessages.Count != 1 || (string)auxMessages[0]["role"] != "user" ||
                (string)auxMessages[0]["content"] != "只输出严格 JSON")
                throw new Exception("Character speech protocol contaminated an auxiliary JSON request.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
        Debug.Log("[SkillRoutingRegression] effective plain-speech prompts and primary/auxiliary request JSON passed");
    }

    private static void RunRawResponseDiagnosticsRegression()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var host = new GameObject("RawResponseDiagnosticsRegression");
        host.SetActive(false);
        var logs = new List<string>();
        Application.LogCallback capture = (message, stack, kind) =>
        {
            if (message.StartsWith("[LLM原文] ", StringComparison.Ordinal)) logs.Add(message);
        };
        Application.logMessageReceived += capture;
        try
        {
            var chat = host.AddComponent<ChatSample>();
            var model = host.AddComponent<ChatQW>();
            var listener = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), chat,
                typeof(ChatSample).GetMethod("HandleRawLLMResponse", flags));
            var enabled = typeof(ChatSample).GetField("m_LogRawLLMOutput", flags);
            var publish = typeof(LLM).GetMethod("RaiseRawResponse", flags);
            model.OnRawResponse += listener;
            string[] responses = {
                "<silent/>她决定先静静听着。\n<next in=\"5s\"/>",
                "<thought>暂时没有要说的话。</thought><silent/>",
                "<thought>不朗读的内容。</thought><say>聞いてね。</say><sing refs=\"clip:test\"/>"
            };
            foreach (string raw in responses)
            {
                enabled.SetValue(chat, false);
                int before = logs.Count;
                publish.Invoke(model, new object[] { raw });
                if (logs.Count != before) throw new Exception("Raw output ignored the existing logging switch.");
                enabled.SetValue(chat, true);
                string executable = RoleOutputChannels.Parse(raw).ToExecutableText();
                publish.Invoke(model, new object[] { raw });
                if (logs.Count != before + 1 || logs[before] != "[LLM原文] " + raw.Replace("\n", "\\n"))
                    throw new Exception("Silent/thought content was lost in diagnostic output.");
                if (RoleOutputChannels.Parse(raw).ToExecutableText() != executable || model.m_DataList.Count != 0)
                    throw new Exception("Diagnostics changed executable output or wrote assistant history.");
            }
            model.OnRawResponse -= listener;
            int count = logs.Count;
            publish.Invoke(model, new object[] { responses[0] });
            if (logs.Count != count) throw new Exception("Raw response diagnostics did not unsubscribe.");
        }
        finally
        {
            Application.logMessageReceived -= capture;
            UnityEngine.Object.DestroyImmediate(host);
        }
        Debug.Log("[SkillRoutingRegression] raw silent diagnostics, logging switch and channel isolation passed");
    }

    private static void RunMalformedToolFeedbackRegression()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var host = new GameObject("MalformedToolFeedbackRegression");
        host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            var model = host.AddComponent<ChatQW>();
            var type = typeof(ChatSample);
            var listener = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), chat,
                type.GetMethod("HandleLLMOutputFormatError", flags));
            model.OnOutputFormatError += listener;
            var report = typeof(ChatQW).GetMethod("ReportMalformedRoleTool", flags);
            object Read(string name) => type.GetField(name, flags).GetValue(chat);
            void Set(string name, object value) => type.GetField(name, flags).SetValue(chat, value);
            var malformed = RoleOutputChannels.Parse("听到了。《sing refs=\"clip:bad\"/><next in=\"5s\"/>");
            // Autonomous errors are facts, not an invitation to interrupt the user.
            report.Invoke(model, new object[] { malformed });
            if ((bool)Read("m_ToolCorrectionContinuationPending") || model.m_DataList.Count != 1 ||
                !((string)Read("m_StickyToolFailure")).Contains("未执行"))
                throw new Exception("Malformed autonomous output lost its failure fact or forced a new turn.");
            foreach (string field in new[] { "m_AgentRunning", "m_AgentRoundInFlight" }) Set(field, true);
            Set("m_UserTurnAwaitingReplySince", 1f);
            int budget = (int)type.GetField("k_MaxToolCorrectionAttemptsPerUserTurn", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
            for (int attempt = 1; attempt <= budget + 1; attempt++)
            {
                Set("m_ToolCorrectionContinuationPending", false);
                report.Invoke(model, new object[] { malformed });
                if (attempt <= budget && (!(bool)Read("m_ToolCorrectionContinuationPending") ||
                    !((string)Read("m_PendingToolCorrectionFrame")).Contains("malformed_tool_syntax")))
                    throw new Exception("Malformed tool output did not reach the existing LLM correction flow.");
            }
            if (!(bool)Read("m_ToolCorrectionExhaustedThisRound") ||
                (int)Read("m_ToolCorrectionAttemptsThisUserTurn") != budget ||
                malformed.ToExecutableText().Contains("<next"))
                throw new Exception("Malformed tool recovery bypassed its retry budget or executed sibling tools.");
            int before = model.m_DataList.Count;
            report.Invoke(model, new object[] { RoleOutputChannels.Parse("我喜欢《One Last Kiss》。") });
            if (model.m_DataList.Count != before)
                throw new Exception("An ordinary book title was reported as a tool failure.");
            model.OnOutputFormatError -= listener;
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
        Debug.Log("[SkillRoutingRegression] malformed tool facts, real-user correction, autonomous isolation and retry budget passed");
    }

    private static void RunSingingRequestFactsRegression()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var host = new GameObject("SingingRequestFactsRegression");
        host.SetActive(false);
        try
        {
            var model = host.AddComponent<ChatQW>();
            var chat = host.AddComponent<ChatSample>();
            var state = typeof(ChatSample).GetMethod("BuildSingingExecutionSnapshot", flags);
            string idle = (string)state.Invoke(chat, null);
            if (!idle.Contains("request_submitted_since_user_turn=false") || !idle.Contains("state=idle"))
                throw new Exception("Idle singing state falsely claims submission/preparation.");
            var requestType = typeof(ChatSample).GetNestedType("AgentSongSingRequest", BindingFlags.NonPublic);
            typeof(ChatSample).GetMethod("BeginSongSing", flags).Invoke(chat,
                new[] { Activator.CreateInstance(requestType) }); // Voice mode disabled: factual rejection, no network.
            string rejected = (string)state.Invoke(chat, null);
            if (!rejected.Contains("request_submitted_since_user_turn=true") || !rejected.Contains("work_active=false"))
                throw new Exception("Rejected submission was confused with never submitted or active work.");
            typeof(ChatSample).GetField("m_HumBackPending", flags).SetValue(chat, true);
            if (!((string)state.Invoke(chat, null)).Contains("state=queued"))
                throw new Exception("Queued work is reported as idle.");
            typeof(ChatSample).GetField("m_HumBackPending", flags).SetValue(chat, false);
            typeof(ChatSample).GetField("m_HumStreamWaitForTextOutputDrain", flags).SetValue(chat, true);
            if (!((string)state.Invoke(chat, null)).Contains("work_active=true"))
                throw new Exception("Text-drain wait was reported as idle.");
            typeof(ChatSample).GetField("m_HumStreamWaitForTextOutputDrain", flags).SetValue(chat, false);

            string frame = "[感知帧 test]\n" + idle +
                "[Sing/Inventory] retained=2 confirmed_list=1 pending_list=1\n" +
                "[Sing/Clip] recording=1 ref=clip:first source=pending playback=ready lyrics=ルーブル\n" +
                "[Sing/LatestRecording] ref=clip:first raw=11.17 transcript=モナリザ\n" +
                "[Sing/Clip] recording=2 ref=clip:second source=confirmed_user playback=ready lyrics=歯車\n" +
                "[Sing/PlaybackFact] last_completed=播放1=clip:second\n私人记忆不进入摘要";
            model.m_DataList.Add(new LLM.SendData("user", "[感知帧 old]\n[Sing/Clip] ref=clip:stale"));
            model.m_DataList.Add(new LLM.SendData("assistant", "<silent/>旧内心"));
            var current = new LLM.SendData("user", frame);
            model.m_DataList.Add(current);
            string received = "";
            model.OnRequestDiagnostic += json => received = ChatQW.BuildRequestSingingSummary(json);
            var raise = typeof(LLM).GetMethod("RaiseRequestDiagnostic", flags);
            foreach (bool stream in new[] { false, true })
            foreach (bool image in new[] { false, true })
            {
                current.imageDataUrl = image ? "data:image/png;base64,DO_NOT_LOG_IMAGE" : null;
                model.SpokenPrefix = "嗯。";
                string json = (string)typeof(ChatQW).GetMethod("BuildRequestJson", flags).Invoke(model, new object[] { stream, "" });
                raise.Invoke(model, new object[] { json });
                if (!received.Contains("clip:first") || !received.Contains("clip:second") ||
                    !received.Contains("[Sing/LatestRecording]") || !received.Contains("モナリザ") ||
                    !received.Contains("last_completed=播放1=clip:second") || !received.Contains("state=idle") ||
                    received.Contains("clip:stale") || received.Contains("DO_NOT_LOG_IMAGE") || received.Contains("私人记忆"))
                    throw new Exception("Serialized request diagnostics lost current material facts or exposed unrelated history/images.");
            }
            current.content = "普通新用户问题，不含素材摘要";
            string missing = (string)typeof(ChatQW).GetMethod("BuildRequestJson", flags).Invoke(model, new object[] { true, "" });
            if (!ChatQW.BuildRequestSingingSummary(missing).Contains("facts=missing") ||
                ChatQW.BuildRequestSingingSummary(missing).Contains("clip:stale"))
                throw new Exception("Missing current facts were replaced by a stale frame.");
            if (current.content != "普通新用户问题，不含素材摘要" || model.m_DataList.Count != 3)
                throw new Exception("Request diagnostics mutated history.");
            var listener = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), chat,
                typeof(ChatSample).GetMethod("HandleRequestDiagnostic", flags));
            int diagnostics = 0;
            Application.LogCallback capture = (message, stack, kind) =>
            { if (message.StartsWith("[LLM请求/歌唱事实]", StringComparison.Ordinal)) diagnostics++; };
            Application.logMessageReceived += capture;
            model.OnRequestDiagnostic += listener;
            try
            {
                typeof(ChatSample).GetField("m_LogRawLLMOutput", flags).SetValue(chat, false);
                raise.Invoke(model, new object[] { missing });
                if (diagnostics != 0) throw new Exception("Request facts ignored the raw diagnostics switch.");
                typeof(ChatSample).GetField("m_LogRawLLMOutput", flags).SetValue(chat, true);
                raise.Invoke(model, new object[] { missing });
                if (diagnostics != 1) throw new Exception("Actual request event did not reach diagnostic logging.");
            }
            finally
            {
                model.OnRequestDiagnostic -= listener;
                Application.logMessageReceived -= capture;
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
        Debug.Log("[SkillRoutingRegression] actual request material summaries, image isolation and singing execution states passed");
    }

    private static void RunPerClipPitchRetentionRegression()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        Type type = typeof(ChatSample);
        var host = new GameObject("PerClipPitchRetentionRegression");
        host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            Type planType = type.GetNestedType("PracticePitchPlan", BindingFlags.NonPublic);
            Type entryType = type.GetNestedType("PracticePitchPlanEntry", BindingFlags.NonPublic);
            object Plan(int stable, int revision, float target)
            {
                object plan = Activator.CreateInstance(planType);
                planType.GetField("Revision").SetValue(plan, revision);
                // Even an older caller's replacement flag cannot erase another clip.
                planType.GetField("MergeWithCurrentArrangement").SetValue(plan, false);
                object entry = Activator.CreateInstance(entryType);
                entryType.GetField("StableId").SetValue(entry, stable);
                entryType.GetField("RequestedCenterMidi").SetValue(entry, target);
                entryType.GetField("TargetCenterMidi").SetValue(entry, target);
                ((System.Collections.IList)planType.GetField("Entries").GetValue(plan)).Add(entry);
                return plan;
            }
            var commit = type.GetMethod("CommitPracticePitchPlan", flags);
            commit.Invoke(chat, new object[] { Plan(1, 1, 61f), true, null });
            var states = (System.Collections.IDictionary)type.GetField("m_PracticePitchStates", flags).GetValue(chat);
            object first = states[1];
            first.GetType().GetField("Measured").SetValue(first, true);
            first.GetType().GetField("CenterMidi").SetValue(first, 60.55f);
            commit.Invoke(chat, new object[] { Plan(2, 2, 60f), true, null });
            commit.Invoke(chat, new object[] { Plan(2, 3, 62f), true, null });
            commit.Invoke(chat, new object[] { Plan(3, 4, 59f), false, new HashSet<int>() });
            if (states.Count != 2 || !ReferenceEquals(states[1], first) ||
                !(bool)first.GetType().GetField("Measured").GetValue(first) ||
                Math.Abs((float)first.GetType().GetField("CenterMidi").GetValue(first) - 60.55f) > .001f)
                throw new Exception("Singing B erased A's measured pitch, or unheard C was committed.");
            Debug.Log("[SkillRoutingRegression] per-clip measured pitch survives independent new selections passed");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void RunShortMixedRecordingAdmissionRegression()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type stt = typeof(SenseVoiceSpeechToText), chatType = typeof(ChatSample);
        // Reproduce 9/8: raw 19.12s, clean 2.74s, expanded 10.04s. Enter via
        // actual capture/cache + pre-perception admission, NOT a prebuilt candidate.
        byte[] wav;
        using (var stream = new System.IO.MemoryStream())
        using (var writer = new System.IO.BinaryWriter(stream))
        {
            const int samples = 305920;
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
            for (int i = 0; i < samples; i++) writer.Write((short)(1000 * Math.Sin(i * .08)));
            wav = stream.ToArray();
        }
        foreach (bool speechVeto in new[] { false, true })
        {
            var host = new GameObject("ShortMixedRecordingAdmissionRegression");
            host.SetActive(false);
            try
            {
                var sense = host.AddComponent<SenseVoiceSpeechToText>();
                var chat = host.AddComponent<ChatSample>();
                void Property(string name, object value) => stt.GetProperty(name).SetValue(sense, value);
                void Field(string name, object value) => stt.GetField(name, flags).SetValue(sense, value);
                sense.BeginLiveRecordingCandidateSession();
                Property("LastSingingAnalysisAvailable", true);
                Property("LastIsSinging", true);
                Property("LastSingingProbability", .662f);
                Property("LastPitchStability", .627f);
                Property("LastSingingIslandSeconds", 2.74f);
                Property("LastSingingContentSeconds", 18f);
                Property("LastPitchTimelineFrameSeconds", .1f);
                Property("LastPitchTimelineMidi", Enumerable.Repeat(50f, 180).ToArray());
                Property("LastText", "嗯，突然想唱唱歌了。初めてのルーブルは 私だけのモナリザ。对了，你能把这一段唱给我听吗？");
                Field("m_LastResponseSingingText", "特くに出会ってたから。");
                Field("m_LastHeadExtraText", "初めてのルーブルは 私だけのモナリザ。");
                Field("m_LastHeadExtraType", "speech");
                Field("m_LastHeadExtraSeconds", 7.3f);
                Field("m_LastHeadExtraReviewRequired", true);
                stt.GetMethod("CaptureLatestSingingEvidence", flags).Invoke(sense, new object[]
                    { wav, 19.12f, 11.48f, 14.22f, 4.18f, 14.22f, Time.realtimeSinceStartup, .85f });
                stt.GetMethod("CacheLastSingingPerformance", flags).Invoke(sense, new object[]
                    { wav, 11.48f, 10.63f, 10.63f, 14.22f, 13.37f, 13.37f, 4.18f, 3.33f, 14.22f, 13.37f });
                if (sense.CommitRecentSingingToPracticeSession(out _) || sense.PracticePhraseCount != 0 ||
                    sense.DescribeQuarantinedSingingCandidates().Count != 0)
                    throw new Exception("The fixture did not reproduce short-clean admission failure.");
                // A stale recent WAV must not replace this recording's raw evidence.
                if (speechVeto) Field("m_LastSingingAudioBytes", new byte[128]);
                chatType.GetField("m_ChatSettings", flags).SetValue(chat, new ChatSetting { m_SpeechToText = sense });
                chatType.GetField("m_LastUserEvidence", flags).SetValue(chat, "[演唱片段] 混合轮");
                chatType.GetField("m_EouCognitiveSpeechVeto", flags).SetValue(chat, speechVeto);
                chatType.GetField("m_FinalModeSoftDowngrade", flags).SetValue(chat, speechVeto);
                object route = chatType.GetMethod("GetSkillRouteState", flags).Invoke(chat, new object[] { "singing" });
                route.GetType().GetField("ActiveThisRound").SetValue(route, true);
                ((HashSet<string>)chatType.GetField("m_ActiveSkillsThisRound", flags).GetValue(chat)).Add("singing");
                var admit = chatType.GetMethod("CommitConfirmedSingingBeforePerception", flags);
                admit.Invoke(chat, null);
                admit.Invoke(chat, null);
                var candidate = sense.DescribeQuarantinedSingingCandidates().Single();
                if (candidate.PlaybackStatus != "evidence_only" || candidate.SourceStatus != "pending" ||
                    Math.Abs(candidate.RawSeconds - 19.12f) > .02f || sense.PracticePhraseCount != 0)
                    throw new Exception("Admission lost evidence, auto-confirmed, duplicated, or prepared playback.");
                string clip = candidate.ClipRef;
                string facts = (string)chatType.GetMethod("BuildSingingMaterialFacts", flags).Invoke(chat, new object[] { sense });
                if (!facts.Contains(clip) || !facts.Contains("evidence_only") ||
                    !facts.Contains("モナリザ") || !facts.Contains("对了") || facts.Contains("证据=none"))
                    throw new Exception("The role still cannot see this mixed recording and its spoken request.");
                var extract = chatType.GetMethod("ExtractHumBackTag", flags);
                var resolve = chatType.GetMethod("TryResolveUnifiedSing", flags);
                bool Resolve(string attrs, out string failure)
                {
                    object[] text = { "<sing " + attrs + "/>" };
                    object request = extract.Invoke(chat, text);
                    object[] args = { request, "" };
                    bool result = (bool)resolve.Invoke(chat, args);
                    failure = (string)args[1];
                    return result;
                }
                if (Resolve("refs=\"clip:25df3c85d441\"", out string invalid) || !invalid.Contains(clip) ||
                    !invalid.Contains("曲库 ID") || Resolve("refs=\"" + clip + "\" confirm_user=\"true\"", out _))
                    throw new Exception("Unknown catalog-as-clip reference or implicit expanded selection was accepted; failure=" + invalid);
                if (!Resolve("refs=\"" + clip + "\" confirm_user=\"true\" range=\"expanded\"", out string failure))
                    throw new Exception("Explicit role-selected recovery failed: " + failure);
                var phrase = sense.DescribePracticePhrases().Single();
                if (phrase.ClipRef != clip || Math.Abs(phrase.Seconds - 10.04f) > .03f ||
                    Math.Abs(phrase.RawSeconds - 19.12f) > .03f ||
                    !sense.TryBuildSingingPracticeComposition(1, 0f, out _, out failure, "stable:" + phrase.StableId, false))
                    throw new Exception("Recovered recording lost identity/raw data or cannot reach composition: " + failure);
                sense.DropPracticePhraseByStableId(phrase.StableId, out _, out _, out _);
                if (sense.RetainCurrentSingingEvidenceClip(true) || sense.TryResolveSingingClip(clip, out _, out _) ||
                    sense.DescribeQuarantinedSingingCandidates().Count != 0)
                    throw new Exception("Evidence publication resurrected an explicitly dropped recording.");
                sense.BeginLiveRecordingCandidateSession();
                if (sense.RetainCurrentSingingEvidenceClip(true))
                    throw new Exception("A new recording published the previous recording's evidence.");
                // Ordinary low-band speech is not automatically registered as a song.
                Property("LastSingingProbability", .18f);
                stt.GetMethod("CaptureLatestSingingEvidence", flags).Invoke(sense, new object[]
                    { wav, 19.12f, 11.48f, 14.22f, 4.18f, 14.22f, Time.realtimeSinceStartup, .85f });
                if (sense.RetainCurrentSingingEvidenceClip(false) || !sense.RetainCurrentSingingEvidenceClip(true))
                    throw new Exception("Retention ignores evidence or refuses semantic/acoustic disagreement.");
                int rejected = sense.DescribeQuarantinedSingingCandidates().Single().CandidateId;
                sense.DiscardQuarantinedSingingCandidate(rejected);
                if (sense.RetainCurrentSingingEvidenceClip(true))
                    throw new Exception("Rejected source was re-published.");
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }
        Debug.Log("[SkillRoutingRegression] short mixed recording admission before perception, speech veto, explicit recovery and no resurrection passed");
    }

    private static void RunUnifiedClipExecutionRegression(GameObject host, SenseVoiceSpeechToText sense,
        object response, Type responseType, MethodInfo archive, byte[] wav)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type type = typeof(ChatSample);
        var chat = host.AddComponent<ChatSample>();
        type.GetField("m_ChatSettings", flags).SetValue(chat, new ChatSetting { m_SpeechToText = sense });
        object route = type.GetMethod("GetSkillRouteState", flags).Invoke(chat, new object[] { "singing" });
        route.GetType().GetField("ActiveThisRound").SetValue(route, true);
        ((HashSet<string>)type.GetField("m_ActiveSkillsThisRound", flags).GetValue(chat)).Add("singing");
        var extract = type.GetMethod("ExtractHumBackTag", flags);
        var resolve = type.GetMethod("TryResolveUnifiedSing", flags);
        object Parse(string attrs)
        {
            object[] input = { "准备好了。<sing " + attrs + "/>" };
            object request = extract.Invoke(chat, input);
            if (request == null || ((string)input[0]).Contains("<sing")) throw new Exception("sing tag was not consumed.");
            return request;
        }
        bool Resolve(object request)
        {
            object[] args = { request, "" };
            return (bool)resolve.Invoke(chat, args);
        }
        string firstRef = sense.DescribePracticePhrases()[0].ClipRef;
        string secondRef = sense.DescribePracticePhrases()[1].ClipRef;
        if (string.IsNullOrEmpty(firstRef) || firstRef == secondRef)
            throw new Exception("Clips do not have distinct fixed identities.");
        RunIdempotentRangeRegression(chat, sense, firstRef, secondRef);
        archive.Invoke(sense, new object[] { response, wav, Time.realtimeSinceStartup + 2f, 103 });
        var pending = sense.DescribeQuarantinedSingingCandidates().Single();
        string clip = pending.ClipRef;
        string overview = (string)type.GetMethod("BuildSingingClipOverview", flags).Invoke(chat, new object[] { sense });
        if (!overview.Contains("pending_list=1") || !overview.Contains("confirmed_list=2") ||
            !overview.Contains("ref=" + clip + " source=pending") || !overview.Contains("ref=" + firstRef))
            throw new Exception("A pending recording disappeared behind the confirmed clip list.");
        object readonlyRoute = type.GetMethod("GetSkillRouteState", flags).Invoke(chat, new object[] { "singing" });
        var activeField = readonlyRoute.GetType().GetField("ActiveThisRound");
        bool wasActive = (bool)activeField.GetValue(readonlyRoute);
        var evidenceField = type.GetField("m_LastUserEvidence", flags);
        object priorEvidence = evidenceField.GetValue(chat);
        try
        {
            activeField.SetValue(readonlyRoute, false);
            evidenceField.SetValue(chat, "还是没有成功，什么情况？");
            string inactive = (string)type.GetMethod("BuildSingingMaterialFacts", flags).Invoke(chat, new object[] { sense });
            if (!inactive.Contains("ref=" + clip) || !inactive.Contains("ref=" + firstRef) ||
                inactive.Contains("[演唱素材主接口") || (bool)activeField.GetValue(readonlyRoute) ||
                sense.DescribeQuarantinedSingingCandidates().Single().SourceStatus != "pending")
                throw new Exception("Unloaded skill hid read-only clips, loaded detailed rules, or auto-confirmed a source.");
        }
        finally { activeField.SetValue(readonlyRoute, wasActive); evidenceField.SetValue(chat, priorEvidence); }
        object[] combinedFailure = { Parse("refs=\"" + clip + "\""), "" };
        if ((bool)resolve.Invoke(chat, combinedFailure) ||
            ((string)combinedFailure[1]).Contains("confirm_user") ||
            !((string)combinedFailure[1]).Contains("range="))
            throw new Exception("Missing range must be reported without imposing source confirmation.");
        if (Resolve(Parse("refs=\"" + clip + "\"")) ||
            Resolve(Parse("refs=\"" + clip + "\" confirm_user=\"true\"")) ||
            sense.DescribeQuarantinedSingingCandidates().Single().SourceStatus != "pending")
            throw new Exception("sing auto-confirmed an uncertain source or silently chose a recovery range.");
        if (Resolve(Parse("refs=\"" + clip + ",clip:missing\" confirm_user=\"true\" range=\"expanded\"")))
            throw new Exception("Unknown refs did not reject the whole requested selection.");
        object multiRange = Parse("refs=\"" + firstRef + "," + clip + "\" range=\"expanded\"");
        if (!Resolve(multiRange) || sense.DescribeQuarantinedSingingCandidates().Count != 0)
            throw new Exception("Multi-clip expanded did not independently prepare pending evidence without confirmation.");
        var multiFacts = sense.DescribePracticePhrases();
        if (!multiFacts.Single(p => p.ClipRef == clip).PendingConfirmation)
            throw new Exception("Preparing pending audio automatically confirmed its provenance.");
        var subtitle = new GameObject("PlaybackSourceTestSubtitle", typeof(RectTransform), typeof(UnityEngine.UI.Text));
        subtitle.transform.SetParent(host.transform);
        type.GetField("m_TextBack", flags).SetValue(chat, subtitle.GetComponent<UnityEngine.UI.Text>());
        type.GetField("m_PendingHumIsPracticeComposition", flags).SetValue(chat, true);
        type.GetField("m_PendingHumPlayedIndices", flags).SetValue(chat,
            new List<int> { multiFacts.Single(p => p.ClipRef == clip).Index });
        type.GetMethod("FinishHumBack", flags).Invoke(chat, new object[] {
            type.GetField("m_HumBackGeneration", flags).GetValue(chat), true, "source-preservation regression" });
        if (!sense.DescribePracticePhrases().Single(p => p.ClipRef == clip).PendingConfirmation)
            throw new Exception("Completed practice playback automatically confirmed provenance.");
        string expectedMultiOrder = "stable:" + multiFacts.Single(p => p.ClipRef == firstRef).StableId +
            ",stable:" + multiFacts.Single(p => p.ClipRef == clip).StableId;
        if ((string)multiRange.GetType().GetField("Order").GetValue(multiRange) != expectedMultiOrder ||
            multiFacts.Single(p => p.ClipRef == clip).ActiveCapture != "expanded" ||
            multiFacts.Single(p => p.ClipRef == firstRef).ActiveCapture != "expanded")
            throw new Exception("Multi range lost request order or failed to select each clip's own expanded audio.");
        if (!sense.TryBuildSingingPracticeComposition(1, 0f, out var multiComposition, out _, expectedMultiOrder) ||
            multiComposition.PlayedIndices.Count != 2)
            throw new Exception("Multi expanded failed to reach the composition builder.");
        int beforeInvalidRevision = multiFacts.Single(p => p.ClipRef == clip).Revision;
        object[] multiCoordinates = { Parse("refs=\"" + firstRef + "," + clip + "\" start_seconds=\"2.5\" end_seconds=\"6\""), "" };
        if ((bool)resolve.Invoke(chat, multiCoordinates) || !((string)multiCoordinates[1]).Contains("只能指定一个 clip") ||
            sense.DescribePracticePhrases().Single(p => p.ClipRef == clip).Revision != beforeInvalidRevision)
            throw new Exception("Multi-coordinate request mutated a clip or returned the old misleading range conflict.");
        var request = Parse("refs=\"" + clip + "\" range=\"expanded\" pitch_plan=\"A3\"");
        if (!Resolve(request)) throw new Exception("Explicit unified short-clip recovery failed.");
        var prepared = sense.DescribePracticePhrases().Single(p => p.ClipRef == clip);
        if (prepared.Seconds < 5.9f || prepared.StableId <= 0 ||
            (string)request.GetType().GetField("PitchPlan").GetValue(request) != "A3")
            throw new Exception("Recovery changed clip identity, audio, or musical intent.");
        string rendererOrder = (string)request.GetType().GetField("Order").GetValue(request);
        if (!sense.TryBuildSingingPracticeComposition(1, 0f, out var composition, out _, rendererOrder))
            throw new Exception("The concrete refs did not reach the existing composition builder.");
        object[] pitchArgs = { request, composition, null, null, "", "" };
        if (!(bool)type.GetMethod("TryResolvePracticePitchTarget", flags).Invoke(chat, pitchArgs) ||
            ((float[])pitchArgs[3]).Length != 1 || Math.Abs(((float[])pitchArgs[3])[0] - 57f) > .01f)
            throw new Exception("Unified A3 musical target failed to reach the real pitch planner.");
        if (!Resolve(Parse("refs=\"" + clip + "\" start_seconds=\"2.5\" end_seconds=\"6\"")))
            throw new Exception("Original-recording coordinates could not select a window.");
        prepared = sense.DescribePracticePhrases().Single(p => p.ClipRef == clip);
        int revision = prepared.Revision;
        if (Math.Abs(prepared.Seconds - 3.5f) > .05f || Math.Abs(prepared.OriginalExpandedSeconds - 6f) > .05f)
            throw new Exception("Unified range selection damaged immutable original audio.");
        if (!Resolve(Parse("refs=\"" + clip + "\"")) ||
            sense.DescribePracticePhrases().Single(p => p.ClipRef == clip).Revision != revision)
            throw new Exception("Default sing reverted or re-edited the current version.");
        if (Resolve(Parse("refs=\"" + clip + "\" start_seconds=\"oops\" end_seconds=\"6\"")) ||
            Resolve(Parse("refs=\"" + clip + "\" source=\"practice\" mode=\"echo\"")))
            throw new Exception("Malformed/legacy mixed sing parameters were silently ignored.");
        object[] saved = { clip, null, null };
        object[] explicitSource = { "<clip_confirm ref=\"" + clip + "\"/>", true };
        type.GetMethod("ExtractAndApplyClipConfirmTags", flags).Invoke(chat, explicitSource);
        if (sense.DescribePracticePhrases().Single(p => p.ClipRef == clip).PendingConfirmation)
            throw new Exception("Explicit clip_confirm did not confirm a selected stable pending clip.");
        if (!(bool)typeof(SenseVoiceSpeechToText).GetMethod("TryResolveSongSaveAudio", flags).Invoke(sense, saved) ||
            ((byte[])saved[1]).Length != 44 + 16000 * 2 * 7 / 2)
            throw new Exception("Saving a clip did not use its current 3.5-second version.");
        var capturePlayback = type.GetMethod("CapturePracticePlaybackFacts", flags);
        var recordPlayback = type.GetMethod("RecordPracticePlaybackOutcome", flags);
        var completedFacts = type.GetField("m_LastCompletedPracticePlaybackFacts", flags);
        int clipIndex = sense.DescribePracticePhrases().Single(p => p.ClipRef == clip).Index;
        int firstIndex = sense.DescribePracticePhrases().Single(p => p.ClipRef == firstRef).Index;
        capturePlayback.Invoke(chat, new object[] { sense, new List<int> { clipIndex, firstIndex } });
        if ((string)completedFacts.GetValue(chat) != "")
            throw new Exception("Prepared order was reported as actually played.");
        recordPlayback.Invoke(chat, new object[] { true, true });
        string heard = (string)completedFacts.GetValue(chat);
        if (!heard.StartsWith("播放1=" + clip + "(") || !heard.Contains("播放2=" + firstRef) ||
            !heard.Contains("raw=[2.50,6.00]") || heard.IndexOf(firstRef, StringComparison.Ordinal) < heard.IndexOf(clip, StringComparison.Ordinal))
            throw new Exception("Completed playback facts lost actual position order.");
        capturePlayback.Invoke(chat, new object[] { sense, new List<int> { firstIndex, clipIndex } });
        recordPlayback.Invoke(chat, new object[] { true, false });
        if ((string)completedFacts.GetValue(chat) != heard)
            throw new Exception("Interrupted attempt overwrote the last fully heard order.");
        if (!Resolve(Parse("refs=\"" + clip + "\" range=\"expanded\"")) || (string)completedFacts.GetValue(chat) != heard)
            throw new Exception("Later range revision rewrote a historical playback snapshot.");
        var beforeCurrent = sense.DescribePracticePhrases().Single(p => p.ClipRef == clip);
        if (!Resolve(Parse("refs=\"" + clip + "," + firstRef + "\"")) ||
            sense.DescribePracticePhrases().Single(p => p.ClipRef == clip).Revision != beforeCurrent.Revision)
            throw new Exception("Multi current reverted a previously expanded clip.");
        if (!Resolve(Parse("refs=\"" + clip + "," + firstRef + "\" range=\"clean\"")) ||
            sense.DescribePracticePhrases().Single(p => p.ClipRef == clip).ActiveCapture != "clean")
            throw new Exception("Multi clean selection was blocked or silently retained expanded.");
        string chronologyFacts = (string)type.GetMethod("BuildUnifiedClipFacts", flags).Invoke(chat, new object[] { sense });
        if (!chronologyFacts.Contains("录音先后（早→晚") || !chronologyFacts.Contains("位置从左至右1起") ||
            !chronologyFacts.Contains(heard) || !chronologyFacts.Contains("practice:incomplete"))
            throw new Exception("Model-facing facts failed to distinguish capture order, heard order and interrupted work.");
        object[] library = { "<sing refs=\"song:123456abcdef\" lyrics=\"片段\"/>" };
        object song = type.GetMethod("ExtractSongSingTag", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, library);
        if (song == null || (string)song.GetType().GetField("SongId").GetValue(song) != "123456abcdef")
            throw new Exception("Unified library ID did not resolve exactly.");
        object[] invalidLibrary = { "<sing refs=\"song:123456abcdef\" transpose=\"+whole_step\"/>" };
        song = type.GetMethod("ExtractSongSingTag", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, invalidLibrary);
        if (string.IsNullOrEmpty((string)song.GetType().GetField("SourceValidationError").GetValue(song)))
            throw new Exception("Unsupported library pitch was silently discarded.");
        var drop = type.GetMethod("ExtractAndApplyPracticeDropTag", flags);
        object[] badDrop = { "<clip_drop ref=\"" + clip + "\"/><clip_drop ref=\"clip:missing\"/>", true };
        drop.Invoke(chat, badDrop);
        if (sense.PracticePhraseCount != 3) throw new Exception("Invalid clip drop partially deleted audio.");
        object[] goodDrop = { "<clip_drop ref=\"" + clip + "\"/><clip_drop ref=\"" + secondRef + "\"/>", true };
        drop.Invoke(chat, goodDrop);
        if (sense.PracticePhraseCount != 1 || sense.DescribePracticePhrases()[0].ClipRef != firstRef ||
            sense.TryResolveSingingClip(clip, out _, out _))
            throw new Exception("Multiple clip drops changed another identity or left a deleted alias valid.");
        if ((string)completedFacts.GetValue(chat) != heard)
            throw new Exception("Removing a clip rewrote the record of what was previously heard.");
        Debug.Log("[SkillRoutingRegression] unified sing/clip identity, recovery, selection, save and batch-drop passed");
    }

    private static void RunIdempotentRangeRegression(ChatSample chat, SenseVoiceSpeechToText sense, string firstRef, string secondRef)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var list = (System.Collections.IList)typeof(SenseVoiceSpeechToText).GetField("m_PracticePhrases", flags).GetValue(sense);
        var type = list[0].GetType();
        int first = -1, second = -1;
        for (int i = 0; i < list.Count; i++)
        {
            string clip = (string)type.GetField("ClipRef").GetValue(list[i]);
            if (clip == firstRef) first = i;
            if (clip == secondRef) second = i;
        }
        object oldFirst = list[first], oldSecond = list[second];
        var clone = typeof(object).GetMethod("MemberwiseClone", flags);
        object phrase = clone.Invoke(oldFirst, null), other = clone.Invoke(oldSecond, null);
        list[first] = phrase; list[second] = other;
        var states = (System.Collections.IDictionary)typeof(ChatSample).GetField("m_PracticePitchStates", flags).GetValue(chat);
        int stable = (int)type.GetField("StableId").GetValue(phrase);
        object oldPitch = states.Contains(stable) ? states[stable] : null;
        var pitchType = typeof(ChatSample).GetNestedType("PracticePitchState", BindingFlags.NonPublic);
        object pitch = Activator.CreateInstance(pitchType);
        pitchType.GetField("Measured").SetValue(pitch, true);
        pitchType.GetField("CenterMidi").SetValue(pitch, 60.15f);
        states[stable] = pitch;
        try
        {
            object evidence = clone.Invoke(type.GetField("RecordingEvidence").GetValue(phrase), null);
            type.GetField("RecordingEvidence").SetValue(phrase, evidence);
            type.GetField("LatestRecordingEvidence").SetValue(phrase, evidence);
            byte[] clean = (byte[])type.GetField("SourceCleanWavBytes").GetValue(phrase);
            // Non-silent, high-amplitude PCM catches decode/re-encode rounding;
            // silence alone would incorrectly pass a byte-only identity check.
            object[] decoded = { clean, null, 0 };
            typeof(SenseVoiceSpeechToText).GetMethod("TryDecodePcmWav", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, decoded);
            var samples = (float[])decoded[1];
            int sampleRate = (int)decoded[2];
            for (int i = 0; i < samples.Length; i++) samples[i] = .9f * Mathf.Sin(2f * Mathf.PI * 220f * i / sampleRate);
            clean = (byte[])typeof(SenseVoiceSpeechToText).GetMethod("EncodeMonoPcm16Wav", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { samples, sampleRate });
            type.GetField("SourceCleanWavBytes").SetValue(phrase, clean);
            float[] timeline = (float[])type.GetField("SourceCleanMidiTimeline").GetValue(phrase);
            float duration = (float)typeof(SenseVoiceSpeechToText).GetMethod("GetWavDurationSeconds", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { clean });
            foreach (string field in new[] { "CleanStartSeconds", "RecoveryStartSeconds" }) evidence.GetType().GetField(field).SetValue(evidence, 0f);
            foreach (string field in new[] { "CleanEndSeconds", "RecoveryEndSeconds" }) evidence.GetType().GetField(field).SetValue(evidence, duration);
            foreach (object item in new[] { phrase, other })
                foreach (string field in new[] { "SourceExpandedWavBytes", "SourceExpandedMidiTimeline", "RecoveryWavBytes", "RecoveryMidiTimeline" })
                    type.GetField(field).SetValue(item, null);
            type.GetField("WavBytes").SetValue(phrase, clean);
            type.GetField("MidiTimeline").SetValue(phrase, timeline);
            type.GetField("ActiveCapture").SetValue(phrase, "clean");
            type.GetField("ActiveTrimHeadSeconds").SetValue(phrase, 0f);
            type.GetField("ActiveTrimTailSeconds").SetValue(phrase, 0f);
            int revision = (int)type.GetField("Revision").GetValue(phrase);
            object[] Resolve(string attrs)
            {
                object[] text = { "<sing " + attrs + "/>" };
                object request = typeof(ChatSample).GetMethod("ExtractHumBackTag", flags).Invoke(chat, text);
                object[] args = { request, "" };
                bool ok = (bool)typeof(ChatSample).GetMethod("TryResolveUnifiedSing", flags).Invoke(chat, args);
                return new object[] { ok, args[1] };
            }
            for (int repeat = 0; repeat < 2; repeat++)
                if (!(bool)Resolve("refs=\"" + firstRef + "\" range=\"expanded\"")[0] ||
                    !ReferenceEquals(states[stable], pitch) || (int)type.GetField("Revision").GetValue(phrase) != revision ||
                    !ReferenceEquals(type.GetField("WavBytes").GetValue(phrase), clean))
                    throw new Exception("Equivalent clean/expanded selection regenerated audio or erased measured pitch.");
            // A later clip lacks recovery audio and has genuinely different coordinates.
            object otherEvidence = clone.Invoke(type.GetField("RecordingEvidence").GetValue(other), null);
            type.GetField("RecordingEvidence").SetValue(other, otherEvidence);
            type.GetField("LatestRecordingEvidence").SetValue(other, otherEvidence);
            float cleanStart = (float)otherEvidence.GetType().GetField("CleanStartSeconds").GetValue(otherEvidence);
            otherEvidence.GetType().GetField("RecoveryStartSeconds").SetValue(otherEvidence, cleanStart - 1f);
            var failure = Resolve("refs=\"" + firstRef + "," + secondRef + "\" range=\"expanded\"");
            if ((bool)failure[0] || !((string)failure[1]).Contains(secondRef) || !ReferenceEquals(states[stable], pitch))
                throw new Exception("Later batch failure erased the unchanged first clip's pitch or silently aliased unequal ranges.");
            if (!(bool)Resolve("refs=\"" + firstRef + "\" range=\"clean\"")[0] || !ReferenceEquals(states[stable], pitch))
                throw new Exception("Returning to equivalent clean erased pitch.");
        }
        finally
        {
            list[first] = oldFirst; list[second] = oldSecond;
            if (oldPitch == null) states.Remove(stable); else states[stable] = oldPitch;
        }
        Debug.Log("[SkillRoutingRegression] identical ranges preserve audio/revision/pitch, unequal missing recovery fails truthfully passed");
    }

    private static void RunSingingTransactionRegression(Type chatType)
    {
        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags staticFlags = BindingFlags.Static | BindingFlags.NonPublic;
        MethodInfo duplicateMethod = chatType.GetMethod(
            "IsIdempotentHumBackDuplicate", staticFlags);
        MethodInfo continueMethod = chatType.GetMethod(
            "ShouldContinueAgentChainAfterHumBack", staticFlags);
        MethodInfo activeMethod = chatType.GetMethod(
            "HasHumBackExecutionWork", instanceFlags);
        MethodInfo textDrainMethod = chatType.GetMethod(
            "IsHumBackTextOutputDrained", staticFlags);
        if (duplicateMethod == null || continueMethod == null || activeMethod == null ||
            textDrainMethod == null)
            throw new MissingMemberException(
                "The hum-back transaction lifecycle helpers are missing.");

        bool sameChainDuplicate = (bool)duplicateMethod.Invoke(
            null, new object[] { "practice|1,2", "practice|1,2", true, 7, 7 });
        bool differentParameters = (bool)duplicateMethod.Invoke(
            null, new object[] { "practice|2,1", "practice|1,2", true, 7, 7 });
        bool newerUserTurn = (bool)duplicateMethod.Invoke(
            null, new object[] { "practice|1,2", "practice|1,2", true, 8, 7 });
        bool inactiveDuplicate = (bool)duplicateMethod.Invoke(
            null, new object[] { "practice|1,2", "practice|1,2", false, 7, 7 });
        if (!sameChainDuplicate || differentParameters || newerUserTurn || inactiveDuplicate)
            throw new InvalidOperationException(
                "Hum-back idempotency no longer distinguishes an LLM-chain retry from a new user turn or new parameters.");

        bool ordinaryContinue = (bool)continueMethod.Invoke(
            null, new object[] { true, false });
        bool actionContinue = (bool)continueMethod.Invoke(
            null, new object[] { true, true });
        if (!ordinaryContinue || actionContinue)
            throw new InvalidOperationException(
                "An accepted hum-back can launch another LLM decision before playback returns a factual result.");

        bool directPcmDrained = (bool)textDrainMethod.Invoke(
            null, new object[] { true, false, true, false, false, false });
        bool noCompletionSignal = (bool)textDrainMethod.Invoke(
            null, new object[] { true, false, false, false, false, false });
        bool pendingText = (bool)textDrainMethod.Invoke(
            null, new object[] { true, true, true, true, false, false });
        bool textStillPlaying = (bool)textDrainMethod.Invoke(
            null, new object[] { true, true, true, false, false, true });
        bool noDrainWait = (bool)textDrainMethod.Invoke(
            null, new object[] { false, false, false, true, true, true });
        if (!directPcmDrained || noCompletionSignal || pendingText ||
            textStillPlaying || !noDrainWait)
            throw new InvalidOperationException(
                "Direct PCM text completion can still deadlock or prematurely release cached singing playback.");

        if (chatType.GetField("m_TextOutputDrainedGeneration", instanceFlags) == null)
            throw new MissingFieldException(
                "The generation-scoped objective text playback completion signal is missing.");

        //The exact state missed in the 9/4 log: conversion has produced/owns a
        //stream transaction, but playback is waiting for spoken text to drain.
        GameObject host = new GameObject("SingingTransactionRegression");
        host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            chatType.GetField("m_HumStreamWaitForTextOutputDrain", instanceFlags)
                .SetValue(chat, true);
            if (!(bool)activeMethod.Invoke(chat, null))
                throw new InvalidOperationException(
                    "A prepared streamed song waiting for text drain is still reported idle.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }

        //No user-wording classifier may synthesize a song-memory write.  The LLM
        //must choose <song_remember/>; the program only validates its real result.
        if (chatType.GetMethod("IsExplicitSongRememberRequest", staticFlags) != null ||
            chatType.GetMethod("ShouldFallbackToExplicitSongRemember", instanceFlags) != null ||
            chatType.GetField("m_EnforceExplicitSongRemember", instanceFlags) != null)
            throw new InvalidOperationException(
                "Mechanical song-memory inference can again turn '把我唱的两段连起来唱' into a save request.");
    }

    private static void RunInterruptedQuestionRecoveryRegression()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject host = new GameObject("InterruptedQuestionRecoveryRegression");
        host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            void Set(string name, object value) => typeof(ChatSample).GetField(name, flags).SetValue(chat, value);
            void Preserve() => typeof(ChatSample).GetMethod("PreservePendingReplyForInputCandidate", flags).Invoke(chat, null);
            string Consume() => (string)typeof(ChatSample).GetMethod("ConsumeInterruptedReplyContext", flags).Invoke(chat, null);
            Set("m_LastUserMsg", "你知道怪物猎人世界？");
            Set("m_UserTurnAwaitingReplySince", 1f);
            Set("m_AgentRunning", true);
            Set("m_ToolCorrectionAttemptsThisUserTurn", 2);
            Preserve(); Preserve();
            string context = Consume();
            if (context == null || !context.Contains("怪物猎人世界") || !context.Contains("保持沉默") ||
                !context.Contains("不证明用户没有发声") || !context.Contains("未播放的草稿") || Consume() != null)
                throw new InvalidOperationException("Empty input recovery lost the question, forced speech, or can replay twice.");
            if ((int)typeof(ChatSample).GetField("m_ToolCorrectionAttemptsThisUserTurn", flags).GetValue(chat) != 2)
                throw new InvalidOperationException("Recovery reset the real-user tool budget.");
            Preserve();
            typeof(ChatSample).GetMethod("SetCurrentUserInput", flags).Invoke(chat, new object[] { "新的问题", "新证据" });
            if (Consume() != null) throw new InvalidOperationException("A valid new question revived an obsolete recovery.");
            Set("m_UserTurnAwaitingReplySince", -1f); Preserve();
            if (Consume() != null) throw new InvalidOperationException("A completed/silent turn was resurrected.");
            Set("m_UserTurnAwaitingReplySince", 1f); Preserve(); Set("m_AgentRunning", false);
            if (Consume() != null) throw new InvalidOperationException("Recovery restarted a stopped session.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void RunCoveredQuietReuseRegression()
    {
        var evidence = new TurnActivityEvidence();
        evidence.Reset(0f);
        evidence.ObserveAsr("原文字", "", 1000, 1f, false, 0f, 0f, 0f, 0.002f,
            audioEndTime: 1f);
        evidence.ObserveAsr("修订文字", "", 2000, 2f, false, 0f, 0f, 0f, 0.002f,
            audioEndTime: 2f);
        if (!evidence.PendingText || evidence.ConfirmPendingText("另一句话", "修订文字", 2.1f))
            throw new InvalidOperationException("Disagreeing ASR results were merged as confirmed text.");
        float soundAt = evidence.LatestSoundAt;
        if (!evidence.ConfirmPendingText("修订文字", "修订文字", 2.1f) ||
            evidence.PendingText || !evidence.CoversQuietTail || evidence.LatestSoundAt != soundAt)
            throw new InvalidOperationException("Full-ASR corroboration restarted sound time or demanded duplicate quiet coverage.");
        MethodInfo delay = typeof(RTSpeechHandler).GetMethod("ResolveReviewDelayWithCoveredQuiet",
            BindingFlags.Static | BindingFlags.NonPublic);
        float Resolve(float quiet, bool covered) => (float)delay.Invoke(null, new object[] { 0.65f, quiet, covered });
        if (Resolve(1.5f, true) != 0f || Resolve(0.2f, true) != 0.65f || Resolve(1.5f, false) != 0.65f)
            throw new InvalidOperationException("Covered quiet reuse either waits twice or bypasses missing/short evidence.");
        for (float t = 2.2f; t < 3f; t += 0.06f) evidence.ObserveLevel(0.05f, t, 0.01f, 0.002f);
        if (evidence.CoversQuietTail) throw new InvalidOperationException("Old ASR quiet coverage survived fresh speech.");
    }

    private static void RunCalibratedRequestWindowRegression()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject host = new GameObject("CalibratedRequestWindowRegression");
        host.SetActive(false);
        try
        {
            var provider = host.AddComponent<ChatQW>();
            provider.m_MaxPromptTokens = 6000;
            provider.m_DataList.Add(new LLM.SendData("system", new string('设', 1000)));
            for (int i = 0; i < 10; i++)
                provider.m_DataList.Add(new LLM.SendData(i % 2 == 0 ? "user" : "assistant", new string('文', 1000)));
            provider.m_DataList[1].content += "ARCHIVED_UNIQUE_INPUT";
            var current = new LLM.SendData("user", "当前真实问题");
            provider.m_DataList.Add(current);
            var toolContinuation = new LLM.SendData("assistant", "已完成工具操作");
            provider.m_DataList.Add(toolContinuation);
            var create = typeof(ChatQW).GetMethod("CreateRequestHistory", flags);
            List<LLM.SendData> View(string extra) => (List<LLM.SendData>)create.Invoke(provider, new object[] { extra });
            List<LLM.SendData> view = View(new string('事', 1500));
            if (provider.m_DataList.Count != 13 || view.Count >= 13 ||
                !view.Contains(current) || !view.Contains(toolContinuation) ||
                typeof(ChatQW).GetField("m_RequestHistoryAnchor", flags).GetValue(provider) != null)
                throw new InvalidOperationException("Request selection mutated history, advanced before success or lost current input.");
            if (View(new string('事', 1500)).Count != view.Count)
                throw new InvalidOperationException("Cancelled requests repeatedly advance history selection.");
            typeof(ChatQW).GetMethod("CommitRequestHistory", flags).Invoke(provider, new object[] { view });
            if (provider.m_DataList.Count != 13 ||
                typeof(ChatQW).GetField("m_RequestHistoryAnchor", flags).GetValue(provider) == null)
                throw new InvalidOperationException("Successful request did not commit its view independently of full history.");
            string auxiliary = (string)typeof(ChatQW).GetMethod("BuildEphemeralRequestJson", flags)
                .Invoke(provider, new object[] { "最新输入候选", 1, false });
            if (auxiliary.Contains("ARCHIVED_UNIQUE_INPUT") || !auxiliary.Contains("当前真实问题"))
                throw new InvalidOperationException("Auxiliary requests resurrected archived context or lost the latest question.");
            List<LLM.SendData> oversized = View(new string('事', 8000));
            if (!oversized.Contains(current) || !oversized.Contains(toolContinuation))
                throw new InvalidOperationException("Oversized dynamic context silently deleted the current question.");
            typeof(ChatQW).GetMethod("ObservePromptUsage", flags).Invoke(provider, new object[] { 30000, 42000, false });
            var calibrate = typeof(ChatQW).GetMethod("CalibratedPromptTokens", flags);
            int calibrated = (int)calibrate.Invoke(provider, new object[] { 42000, false });
            if (calibrated < 30000 || calibrated >= 42000 ||
                (int)calibrate.Invoke(provider, new object[] { 42000, true }) != 42000)
                throw new InvalidOperationException("Token usage calibration lost its safety margin or discounted image input.");

            Type handlerType = typeof(ChatQW).GetNestedType("SSEDownloadHandler", BindingFlags.NonPublic);
            var text = new System.Text.StringBuilder();
            var handler = (UnityEngine.Networking.DownloadHandler)Activator.CreateInstance(handlerType,
                new object[] { (Action<string>)(delta => text.Append(delta)) });
            try
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"你好\"}}]}\n" +
                    "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":31500}}\n\ndata: [DONE]\n");
                MethodInfo receive = handlerType.GetMethod("ReceiveData", flags);
                for (int i = 0; i < bytes.Length; i++)
                    receive.Invoke(handler, new object[] { new[] { bytes[i] }, 1 });
                if (text.ToString() != "你好" || (int)handlerType.GetProperty("PromptTokens").GetValue(handler) != 31500)
                    throw new InvalidOperationException("SSE split UTF-8 characters or cached prompt token usage were lost.");
            }
            finally { handler.Dispose(); }

            provider.m_DataList.Clear();
            const string frame = "[感知帧]\n距用户上句:3秒\n(用户刚开口讲了下面这段话，请回应)\n原来的问题";
            var old = new LLM.SendData("user", frame);
            provider.m_DataList.Add(old);
            provider.m_DataList.Add(new LLM.SendData("user", "你知道这个游戏？") { imageDataUrl = "data:image/jpeg;base64,IMAGE" });
            string json = (string)typeof(ChatQW).GetMethod("BuildRequestJson", flags).Invoke(provider, new object[] { true, "一次性证据" });
            var parsed = Newtonsoft.Json.Linq.JObject.Parse(json);
            var messages = (Newtonsoft.Json.Linq.JArray)parsed["messages"];
            var parts = (Newtonsoft.Json.Linq.JArray)messages[messages.Count - 1]["content"];
            if ((string)parts[0]["type"] != "image_url" || (string)parts[1]["type"] != "text" ||
                !((string)parts[1]["text"]).EndsWith("你知道这个游戏？") ||
                old.content != frame || json.Contains("距用户上句:3秒") || !json.Contains("原来的问题") ||
                provider.m_DataList.Exists(m => m.content.Contains("一次性证据")))
                throw new InvalidOperationException("Visual evidence replaced the current question or transient facts leaked into history.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void RunCacheAndVisualPayloadRegression()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject host = new GameObject("CacheAndVisualPayloadRegression");
        host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            MethodInfo format = typeof(ChatSample).GetMethod("FormatDuration", flags);
            var cases = new Dictionary<float, string> {
                { 0.5f, "刚刚" }, { 59.9f, "59秒" }, { 60f, "1分0秒" },
                { 90f, "1分30秒" }, { 100f, "1分40秒" }, { 119.9f, "1分59秒" },
                { 120f, "2分0秒" }, { 3599.9f, "59分59秒" }, { 3600f, "1小时0分" },
                { 5400f, "1小时30分" }, { float.NaN, "时间未知" },
            };
            foreach (var item in cases)
                if ((string)format.Invoke(chat, new object[] { item.Key }) != item.Value)
                    throw new InvalidOperationException($"Duration carries/rounding failed at {item.Key}s.");

            var provider = host.AddComponent<ChatQW>();
            provider.m_Backend = ChatQW.BackendType.Local;
            provider.m_DataList.Add(new LLM.SendData("system", "persona"));
            var screenshot = new LLM.SendData("user", "old visual question") { imageDataUrl = "data:image/jpeg;base64,OLD_PIXELS" };
            provider.m_DataList.Add(screenshot);
            provider.m_DataList.Add(new LLM.SendData("assistant", "observed visual facts"));
            var main = typeof(ChatQW).GetMethod("BuildRequestJson", flags);
            string BuildMain() => (string)main.Invoke(provider, new object[] { true, null });
            string before = BuildMain();
            if (!before.Contains("OLD_PIXELS")) throw new InvalidOperationException("Open-eye screenshot is missing.");

            typeof(ChatQW).GetField("m_LocalSlotCount", flags).SetValue(provider, 3);
            string boundary = (string)typeof(ChatQW).GetMethod("BuildTurnBoundaryRequestJson", flags)
                .Invoke(provider, new object[] { "current input evidence" });
            if (!boundary.Contains("\"id_slot\":2") || !before.Contains("\"id_slot\":0") || BuildMain() != before)
                throw new InvalidOperationException("Boundary inference changes the formal request or its cache slot.");

            if (provider.ArchiveHistoricalImagePixels() != 1 || provider.ArchiveHistoricalImagePixels() != 0)
                throw new InvalidOperationException("Closed-eye image archival is not idempotent.");
            string closed = BuildMain();
            if (closed.Contains("OLD_PIXELS") || !closed.Contains("observed visual facts") ||
                !closed.Contains("old visual question") || !closed.Contains("本轮未重新附送像素") ||
                screenshot.imageDataUrl == null || provider.m_DataList.Count != 3 || BuildMain() != closed)
                throw new InvalidOperationException("Closed eyes resend pixels, erase evidence, or repeatedly invalidate the stable prefix.");

            provider.m_DataList.Add(new LLM.SendData("user", "new visual question") {
                imageDataUrl = "data:image/jpeg;base64,NEW_PIXELS" });
            string reopened = BuildMain();
            if (!reopened.Contains("NEW_PIXELS") || reopened.Contains("OLD_PIXELS"))
                throw new InvalidOperationException("Reopening eyes revives archived pixels or prevents a fresh screenshot.");
            provider.m_DataList.Add(new LLM.SendData("user", "third visual question") {
                imageDataUrl = "data:image/jpeg;base64,THIRD_PIXELS" });
            BuildMain();
            if (screenshot.imageDataUrl != null || !screenshot.imageArchived)
                throw new InvalidOperationException("Evicted screenshot pixels lack an explicit historical-attachment marker.");
            provider.m_Backend = ChatQW.BackendType.Cloud;
            if (BuildMain().Contains("id_slot")) throw new InvalidOperationException("Cloud requests leaked local slot settings.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void RunCaptureChronologyRegression()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type senseType = typeof(SenseVoiceSpeechToText);
        var items = new List<SenseVoiceSpeechToText.PracticePhraseInfo> {
            new SenseVoiceSpeechToText.PracticePhraseInfo { Index = 1, StableId = 1, AgoSeconds = 10, Lyrics = "同一句歌词" },
            new SenseVoiceSpeechToText.PracticePhraseInfo { Index = 2, StableId = 2, AgoSeconds = 30, Lyrics = "同一句歌词" },
            new SenseVoiceSpeechToText.PracticePhraseInfo { Index = 3, StableId = 3, AgoSeconds = 5, Lyrics = "另一首歌" },
        };
        senseType.GetMethod("AnnotateCaptureOrder", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { items });
        senseType.GetMethod("AnnotateTakeGroups", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { items });
        if (items[0].Index != 1 || items[1].Index != 2 || items[0].CaptureOrder != 2 ||
            items[1].CaptureOrder != 1 || items[2].CaptureOrder != 3 ||
            items[0].TakeIndex != 2 || items[1].TakeIndex != 1)
            throw new InvalidOperationException("Delayed confirmation changes material identity or mislabels take chronology.");

        GameObject host = new GameObject("CaptureChronologyRegression");
        host.SetActive(false);
        try
        {
            var sense = host.AddComponent<SenseVoiceSpeechToText>();
            Type phraseType = senseType.GetNestedType("PracticePhrase", BindingFlags.NonPublic);
            object NewPhrase(float at) {
                object value = Activator.CreateInstance(phraseType);
                phraseType.GetField("AtRealtime").SetValue(value, at);
                return value;
            }
            MethodInfo store = senseType.GetMethod("StorePracticeCapture", flags);
            float currentCapture = Mathf.Max(.001f, Time.realtimeSinceStartup);
            var first = NewPhrase(currentCapture);
            store.Invoke(sense, new[] { first });
            var continued = NewPhrase(currentCapture);
            int index = (int)store.Invoke(sense, new[] { continued });
            var phrases = (System.Collections.IList)senseType.GetField("m_PracticePhrases", flags).GetValue(sense);
            if (index != 1 || phrases.Count != 1 ||
                (int)phraseType.GetField("StableId").GetValue(first) != (int)phraseType.GetField("StableId").GetValue(continued))
                throw new InvalidOperationException("A resumed capture is being stored as two performances.");

            Type candidateType = senseType.GetNestedType(
                "QuarantinedSingingCandidate", BindingFlags.NonPublic);
            var candidates = (System.Collections.IList)senseType
                .GetField("m_QuarantinedSingingCandidates", flags).GetValue(sense);
            object NewCandidate(int id, float at, string lyrics) {
                object phrase = NewPhrase(at);
                // Production reserves this at capture start, before async analysis completion.
                phraseType.GetField("RecordingSequence").SetValue(phrase, id - 6);
                phraseType.GetField("Lyrics").SetValue(phrase, lyrics);
                phraseType.GetField("Seconds").SetValue(phrase, 8f + id);
                phraseType.GetField("WavBytes").SetValue(phrase, new byte[45]);
                phraseType.GetField("MidiTimeline").SetValue(
                    phrase, new float[] { 60f, 60f, 60f, 60f });
                object value = Activator.CreateInstance(candidateType);
                candidateType.GetField("CandidateId").SetValue(value, id);
                candidateType.GetField("Phrase").SetValue(value, phrase);
                return value;
            }

            float olderCapturedAt = Time.realtimeSinceStartup - 10f;
            float newerCapturedAt = Time.realtimeSinceStartup - 2f;
            // Deliberately insert newest first: public facts must still reflect recording chronology.
            candidates.Add(NewCandidate(8, newerCapturedAt, "后唱的候选"));
            candidates.Add(NewCandidate(7, olderCapturedAt, "先唱的候选"));
            var described = sense.DescribeQuarantinedSingingCandidates();
            if (described.Count != 2 || described[0].CandidateId != 7 ||
                described[0].CaptureOrder != 1 || described[1].CandidateId != 8 ||
                described[1].CaptureOrder != 2)
                throw new InvalidOperationException(
                    "Multiple quarantined performances overwrite each other or lose capture chronology.");
            MethodInfo buildTargets = typeof(ChatSample).GetMethod(
                "BuildPendingPracticeResolutionTargets",
                BindingFlags.Static | BindingFlags.NonPublic);
            var sameTurnTargets = buildTargets != null
                ? buildTargets.Invoke(null, new object[] { sense }) as System.Collections.IList
                : null;
            if (sameTurnTargets == null || sameTurnTargets.Count != 2 ||
                (int)sameTurnTargets[0].GetType().GetField("CandidateId").GetValue(sameTurnTargets[0]) != 7 ||
                (int)sameTurnTargets[1].GetType().GetField("CandidateId").GetValue(sameTurnTargets[1]) != 8)
                throw new InvalidOperationException(
                    "Same-turn semantic confirmation cannot see every newly isolated performance.");

            object olderCandidate = candidateType.GetField("Phrase").GetValue(candidates[1]);
            if (!sense.ConfirmQuarantinedSingingCandidate(7, out int confirmedIndex) || confirmedIndex != 2 ||
                (float)phraseType.GetField("AtRealtime").GetValue(olderCandidate) != olderCapturedAt ||
                (float)phraseType.GetField("ConfirmedAtRealtime").GetValue(olderCandidate) < olderCapturedAt + 9f ||
                (int)phraseType.GetField("OriginCandidateId").GetValue(olderCandidate) != 7)
                throw new InvalidOperationException(
                    "Candidate confirmation loses its stable identity or original capture time.");
            described = sense.DescribeQuarantinedSingingCandidates();
            if (described.Count != 1 || described[0].CandidateId != 8)
                throw new InvalidOperationException(
                    "Confirming one candidate removes another unresolved performance.");
            sense.ResumeSingingPracticeSession(6f, out int kept, out int dropped);
            if (kept != 1 || dropped != 1)
                throw new InvalidOperationException("An old recording appended after a newer one cannot expire.");
            object newerCandidatePhrase = candidateType.GetField("Phrase").GetValue(candidates[0]);
            phraseType.GetField("HeadExtraSeconds").SetValue(newerCandidatePhrase, 1.8f);
            phraseType.GetField("HeadExtraType").SetValue(newerCandidatePhrase, "speech");
            phraseType.GetField("HeadExtraText").SetValue(newerCandidatePhrase, "那我继续唱喽");
            phraseType.GetField("CleanLeadInUnverifiedSeconds").SetValue(
                newerCandidatePhrase, 0.6f);
            phraseType.GetField("CleanLeadInEvidenceType").SetValue(
                newerCandidatePhrase, "uncertain");
            phraseType.GetField("CleanLeadInEvidenceText").SetValue(
                newerCandidatePhrase, "那我继续唱喽");
            if (!sense.ConfirmQuarantinedSingingCandidate(8, out int newerIndex) || newerIndex != 2)
                throw new InvalidOperationException(
                    "A later unresolved candidate cannot be confirmed after an earlier one expires.");
            if (!sense.TryResolveConfirmedSingingCandidate(
                    8, out int confirmedStableId, out int resolvedIndex) ||
                confirmedStableId <= 0 || resolvedIndex != newerIndex ||
                !sense.HasKnownSingingCandidate(8) ||
                !sense.ConfirmQuarantinedSingingCandidateWithPlaybackStatus(
                    8, out int repeatedIndex, out string repeatedPlayback) ||
                repeatedIndex != newerIndex || repeatedPlayback != "ready")
                throw new InvalidOperationException(
                    "A consumed candidate confirmation is no longer an idempotent stable mapping.");

            if (sense.TryValidatePracticeBoundarySelection(
                    "2", true, 0f, 0f, true, out string boundaryConflict) ||
                string.IsNullOrWhiteSpace(boundaryConflict) ||
                !sense.TryValidatePracticeBoundarySelection(
                    "2", true, 2.4f, 0f, true, out string _) ||
                !sense.TryValidatePracticeBoundarySelection(
                    "2", false, 0f, 0f, true, out string _))
                throw new InvalidOperationException(
                    "Expanded speech evidence is not checked before playback, explicit crop is ignored, " +
                    "or non-independent clean-lead evidence is being treated as a hard veto.");
            phraseType.GetField("HeadExtraReviewRequired").SetValue(
                newerCandidatePhrase, true);
            if (!sense.TryValidatePracticeBoundarySelection(
                    "2", true, 0f, 0f, true, out string _))
                throw new InvalidOperationException(
                    "A timestamped melodic-window conflict is still being overruled by the aggregate speech label.");
            phraseType.GetField("HeadExtraReviewRequired").SetValue(
                newerCandidatePhrase, false);
            if (sense.TryValidatePracticeBoundarySelection(
                    "2", false, 1.8f, 0f, false, out string coordinateConflict) ||
                string.IsNullOrWhiteSpace(coordinateConflict) ||
                !coordinateConflict.Contains("独立时间坐标") ||
                !sense.TryValidatePracticeBoundarySelection(
                    "2", false, 0.45f, 0f, false, out string _))
                throw new InvalidOperationException(
                    "Expanded margin duration can still be silently reused as a clean-local trim, " +
                    "or an unrelated clean-local crop is incorrectly blocked.");
            phraseType.GetField("RecoveryWavBytes").SetValue(
                newerCandidatePhrase, new byte[45]);
            phraseType.GetField("RecoveryMidiTimeline").SetValue(
                newerCandidatePhrase, null);
            var availability = sense.DescribePracticePhrases();
            if (availability.Count < 2 || availability[1].HasExpandedCapture)
                throw new InvalidOperationException(
                    "Expanded capture is advertised even though its recovery melody is missing.");
            if (sense.TryBuildSingingPracticeComposition(
                    1, 0f, out SenseVoiceSpeechToText.PracticeComposition _,
                    out string missingExpanded, "2", true) ||
                string.IsNullOrWhiteSpace(missingExpanded) ||
                !missingExpanded.Contains("没有可用的 expanded"))
                throw new InvalidOperationException(
                    "An unavailable expanded capture silently falls back to clean.");

            Type evidenceType = senseType.GetNestedType(
                "SingingEvidenceSnapshot", BindingFlags.NonPublic);
            object evidence = Activator.CreateInstance(evidenceType);
            evidenceType.GetField("CaptureSessionSerial").SetValue(evidence, 101);
            evidenceType.GetField("RawWavBytes").SetValue(evidence, new byte[45]);
            evidenceType.GetField("PitchTimelineMidi").SetValue(
                evidence, new float[] { 60f, 61f });
            evidenceType.GetField("FrameSeconds").SetValue(evidence, 0.10f);
            evidenceType.GetField("AtRealtime").SetValue(
                evidence, Time.realtimeSinceStartup);
            evidenceType.GetField("Text").SetValue(evidence, "短旋律原始证据");
            evidenceType.GetField("SingingText").SetValue(evidence, "ルーブル");
            evidenceType.GetField("Language").SetValue(evidence, "ja");
            evidenceType.GetField("RawSeconds").SetValue(evidence, 10.8f);
            evidenceType.GetField("SingingProbability").SetValue(evidence, 0.511f);
            evidenceType.GetField("PitchStability").SetValue(evidence, 0.65f);
            evidenceType.GetField("MelodicIslandSeconds").SetValue(evidence, 2.07f);
            evidenceType.GetField("ContentSeconds").SetValue(evidence, 10.8f);
            senseType.GetField("m_LastSingingEvidence", flags).SetValue(sense, evidence);
            if (!sense.QuarantineRecentSingingCandidate(
                    out int rawCandidateId, out float rawSeconds) ||
                rawCandidateId <= 0 || Mathf.Abs(rawSeconds - 10.8f) > 0.01f)
                throw new InvalidOperationException(
                    "Raw semantic/acoustic conflict evidence is still gated by playback readiness.");
            if (!sense.ConfirmQuarantinedSingingCandidateWithPlaybackStatus(
                rawCandidateId, out int rawIndex, out string rawPlayback) || rawIndex != 0 ||
                rawPlayback != "evidence_only")
                throw new InvalidOperationException(
                    "Confirming raw evidence incorrectly promotes it through the playback gate.");
            described = sense.DescribeQuarantinedSingingCandidates();
            if (described.Count != 1 || described[0].CandidateId != rawCandidateId ||
                described[0].SourceStatus != "confirmed_user" ||
                described[0].PlaybackStatus != "evidence_only")
                throw new InvalidOperationException(
                    "Source-confirmed unplayable evidence was discarded or its state axes collapsed.");
            sameTurnTargets = buildTargets.Invoke(
                null, new object[] { sense }) as System.Collections.IList;
            if (sameTurnTargets == null || sameTurnTargets.Count != 0)
                throw new InvalidOperationException(
                    "Already source-confirmed raw evidence is still repeatedly sent for confirmation.");
            if (!sense.DiscardQuarantinedSingingCandidate(rawCandidateId))
                throw new InvalidOperationException(
                    "A quarantined recording could not be explicitly rejected.");
            //同一真实录音的迟到分析可能多出采样、改变 raw/clean 字节；身份仍应由
            //录音会话而不是 WAV 签名锚定。
            evidenceType.GetField("RawWavBytes").SetValue(evidence, new byte[57]);
            if (sense.QuarantineRecentSingingCandidate(
                    out int repeatedRejectedId, out float _))
                throw new InvalidOperationException(
                    "A user-rejected recording can re-enter quarantine after its WAV version changes.");

            MethodInfo encodeWav = senseType.GetMethod(
                "EncodeMonoPcm16Wav", BindingFlags.Static | BindingFlags.NonPublic);
            byte[] previousWav = (byte[])encodeWav.Invoke(
                null, new object[] { new float[32000], 16000 });
            byte[] pendingWav = (byte[])encodeWav.Invoke(
                null, new object[] { new float[64000], 16000 });
            float[] previousMidi = Enumerable.Repeat(58f, 32).ToArray();
            float[] pendingMidi = Enumerable.Repeat(62f, 40).ToArray();
            int aliasSerial = 73;
            senseType.GetField("m_LastCompletedAsrSerial", flags).SetValue(sense, aliasSerial);
            senseType.GetField("m_LastSingingCacheSerial", flags).SetValue(sense, aliasSerial);
            senseType.GetField("m_LastSingingAudioBytes", flags).SetValue(sense, pendingWav);
            senseType.GetField("m_LastSingingAudioTime", flags).SetValue(
                sense, Time.realtimeSinceStartup);
            senseType.GetField("m_LastSingingPerformanceTime", flags).SetValue(
                sense, Time.realtimeSinceStartup);
            senseType.GetField("m_LastSingingPerformanceMidi", flags).SetValue(
                sense, pendingMidi);
            senseType.GetField("m_LastSingingPerformanceFrameSeconds", flags).SetValue(
                sense, 0.10f);
            senseType.GetField("m_RollbackSingingAudioBytes", flags).SetValue(
                sense, previousWav);
            senseType.GetField("m_RollbackSingingAudioTime", flags).SetValue(
                sense, Time.realtimeSinceStartup - 1f);
            senseType.GetField("m_RollbackSingingPerformanceTime", flags).SetValue(
                sense, Time.realtimeSinceStartup - 1f);
            senseType.GetField("m_RollbackSingingPerformanceMidi", flags).SetValue(
                sense, previousMidi);
            evidenceType.GetField("CaptureSessionSerial").SetValue(evidence, 102);
            evidenceType.GetField("RawWavBytes").SetValue(evidence, pendingWav);
            evidenceType.GetField("PitchTimelineMidi").SetValue(evidence, pendingMidi);
            evidenceType.GetField("RawSeconds").SetValue(evidence, 4f);
            evidenceType.GetField("HeadExtraText").SetValue(
                evidence, "best-preview-boundary");
            evidenceType.GetField("HeadExtraType").SetValue(evidence, "speech");
            senseType.GetField("m_LastSingingCacheEvidence", flags).SetValue(sense, evidence);
            senseType.GetField("m_LastSingingCacheCaptureSessionSerial", flags)
                .SetValue(sense, 102);
            object finalEvidence = Activator.CreateInstance(evidenceType);
            evidenceType.GetField("CaptureSessionSerial").SetValue(finalEvidence, 102);
            evidenceType.GetField("RawWavBytes").SetValue(finalEvidence, new byte[61]);
            evidenceType.GetField("PitchTimelineMidi").SetValue(finalEvidence, pendingMidi);
            evidenceType.GetField("FrameSeconds").SetValue(finalEvidence, 0.10f);
            evidenceType.GetField("AtRealtime").SetValue(
                finalEvidence, Time.realtimeSinceStartup);
            evidenceType.GetField("RawSeconds").SetValue(finalEvidence, 4.2f);
            evidenceType.GetField("HeadExtraText").SetValue(
                finalEvidence, "different-final-boundary");
            evidenceType.GetField("HeadExtraType").SetValue(finalEvidence, "uncertain");
            senseType.GetField("m_LastSingingEvidence", flags).SetValue(
                sense, finalEvidence);
            if (!sense.QuarantineRecentSingingCandidate(
                    out int isolatedPlayableId, out float _) ||
                sense.HasCurrentSingingPerformanceCandidate())
                throw new InvalidOperationException(
                    "A source-pending playable candidate leaked through the generic recent_turn alias.");
            described = sense.DescribeQuarantinedSingingCandidates();
            if (described.Count != 1 ||
                described[0].HeadExtraText != "best-preview-boundary")
                throw new InvalidOperationException(
                    "A playable preview candidate was paired with boundary evidence from a different final analysis.");
            if (sense.TryGetRecentSingingPerformance(
                    out float[] restoredMidi, out float _, out string _) ||
                string.IsNullOrEmpty(sense.RecentSingingMaterialConflict))
                throw new InvalidOperationException(
                    "A newer pending candidate silently aliases the preceding valid recording.");
            var restoredCache = (float[])senseType.GetField("m_LastSingingPerformanceMidi", flags).GetValue(sense);
            if (restoredCache == null || restoredCache.Length != previousMidi.Length)
                throw new InvalidOperationException("Identity protection destroyed the old recording instead of retaining it.");
            sense.DiscardQuarantinedSingingCandidate(isolatedPlayableId);
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void RunStreamingObservedModeLatchRegression()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type chatType = typeof(ChatSample);
        Type draftType = chatType.GetNestedType(
            "SpeculativeDraft", BindingFlags.NonPublic);
        MethodInfo store = chatType.GetMethod(
            "StoreStreamingObservedMode", flags);
        if (draftType == null || store == null)
            throw new MissingMemberException(
                "Streaming observed-mode evidence recorder is missing.");

        GameObject host = new GameObject("StreamingObservedModeLatchRegression");
        host.SetActive(false);
        try
        {
            ChatSample chat = host.AddComponent<ChatSample>();
            chatType.GetField("m_ModeReviewSingingConfidence", flags)
                .SetValue(chat, 0.58f);
            object singing = Activator.CreateInstance(draftType);
            draftType.GetField("draft").SetValue(singing, "");
            draftType.GetField("observed_mode").SetValue(singing, "singing");
            draftType.GetField("mode_confidence").SetValue(singing, 0.83f);
            store.Invoke(chat, new[] { singing, "无草稿但语义判为歌唱" });
            if (!(bool)chatType.GetField(
                    "m_StreamingSemanticSingingObservedThisTurn", flags).GetValue(chat) ||
                (string)chatType.GetField(
                    "m_StreamingSemanticSingingTranscriptThisTurn", flags).GetValue(chat) !=
                    "无草稿但语义判为歌唱")
                throw new InvalidOperationException(
                    "An empty reply draft discards the LLM's independent singing judgment.");

            object speech = Activator.CreateInstance(draftType);
            draftType.GetField("draft").SetValue(speech, "");
            draftType.GetField("observed_mode").SetValue(speech, "speech");
            draftType.GetField("mode_confidence").SetValue(speech, 0.84f);
            store.Invoke(chat, new[] { speech, "唱完后的普通说话" });
            if (!(bool)chatType.GetField(
                    "m_StreamingSemanticSpokenLeadInObserved", flags).GetValue(chat))
                throw new InvalidOperationException(
                    "An empty reply draft discards the LLM's independent speech judgment.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void RunAuthoritativeUserSemanticTextRegression()
    {
        MethodInfo resolve = typeof(ChatSample).GetMethod(
            "ResolveAuthoritativeUserSemanticText",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (resolve == null)
            throw new MissingMethodException(
                "Complete-turn semantic authority resolver is missing.");

        const string truncatedFinal = "呃那我继续唱喽";
        const string stableComplete =
            "呃那我继续唱喽，你能把我刚才唱的包括现在唱的这几段连起来吗";
        object[] extensionArgs = { truncatedFinal, stableComplete, true, null };
        string promoted = (string)resolve.Invoke(null, extensionArgs);
        if (promoted != stableComplete ||
            !(extensionArgs[3] as string).StartsWith("stable-stream-mixed-extension"))
            throw new InvalidOperationException(
                "A stable mixed-turn tail request is overwritten by truncated final ASR.");

        object[] conflictArgs = {
            "今天我们讨论天气", "请把刚才唱的全部连起来", true, null };
        string conflict = (string)resolve.Invoke(null, conflictArgs);
        if (conflict != (string)conflictArgs[0] || (string)conflictArgs[3] != "final-asr")
            throw new InvalidOperationException(
                "A genuinely conflicting streaming hypothesis overrides final ASR.");
    }

    private static void RunContinuationDispatchRegression()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type rt = typeof(RTSpeechHandler);
        MethodInfo canResume = rt.GetMethod("CanResumeUnheardCapture", BindingFlags.Static | BindingFlags.NonPublic);
        bool Resume(bool audio, float gap, bool replied, bool sameMic) =>
            (bool)canResume.Invoke(null, new object[] { audio, gap, replied, sameMic });
        if (!Resume(true, .3f, false, true) || Resume(true, .3f, true, true) ||
            Resume(true, 4f, false, true) || Resume(false, .3f, false, true) ||
            Resume(true, .3f, false, false))
            throw new InvalidOperationException("Unheard capture continuation eligibility regressed.");
        if (!TurnBoundaryDecision.CanClose("interrupt_user", .4f, .78f))
            throw new InvalidOperationException("Intentional interruption is not available to the role.");
        GameObject host = new GameObject("ContinuationDispatchRegression");
        host.SetActive(false);
        AudioClip combined = null;
        try
        {
            var handler = host.AddComponent<RTSpeechHandler>();
            void Set(string field, object value) => rt.GetField(field, flags).SetValue(handler, value);
            bool Flag(string field) => (bool)rt.GetField(field, flags).GetValue(handler);
            Set("m_TentativeSeq", 9);
            Set("m_TentativePreviewInFlight", true);
            rt.GetMethod("OnPreviewAsrResult", flags).Invoke(handler, new object[] { 8, "obsolete" });
            if (!Flag("m_TentativePreviewInFlight"))
                throw new InvalidOperationException("Obsolete preview callback clears the newer request's in-flight state.");

            var chunks = (List<float[]>)rt.GetField("m_RecordingPcmChunks", flags).GetValue(handler);
            chunks.Add(new float[] { .1f, .2f });
            chunks.Add(new float[] { .3f, .4f });
            Set("m_RecordingCapturedFrames", 4);
            Set("m_RecordingCaptureChannels", 1);
            Set("m_RecordingCaptureFrequency", 16000);
            Set("m_RecordingCloseReason", "llm-take_turn");
            rt.GetMethod("RetainUnheardCapture", flags).Invoke(handler, new object[] { 4 });
            combined = (AudioClip)rt.GetMethod("BuildAccumulatedRecordingClip", flags).Invoke(handler, new object[] { true });
            var retained = (List<float[]>)rt.GetField("m_UnheardCapture", flags).GetValue(handler);
            if (combined == null || combined.samples != 4 || chunks.Count != 0 || retained.Count != 2 || retained[1][1] != .4f)
                throw new InvalidOperationException("Final ASR consumes the PCM needed for unheard continuation.");
            Set("m_RecordingCloseReason", "llm-interrupt_user");
            rt.GetMethod("RetainUnheardCapture", flags).Invoke(handler, new object[] { 4 });
            if (retained.Count != 0)
                throw new InvalidOperationException("An intentional interruption is being undone by automatic continuation.");

            var activity = (TurnActivityEvidence)rt.GetField("m_TurnActivity", flags).GetValue(handler);
            activity.Reset(10f);
            for (int i = 0; i < 20; i++) activity.ObserveLevel(.05f, 10f + i * .06f, .01f, .003f);
            int revision = activity.Revision;
            float reviewedSound = activity.LatestSoundAt;
            for (int i = 20; i < 30; i++) activity.ObserveLevel(.05f, 10f + i * .06f, .01f, .003f);
            if (activity.Revision != revision || activity.LatestSoundAt - reviewedSound < .3f)
                throw new InvalidOperationException("Test fixture did not reproduce a continuing vowel with no new onset.");
            Set("m_IsRecording", true);
            Set("m_LatestStreamPartial", "啦");
            Set("m_StalledReviewActivityRevision", revision);
            Set("m_StalledReviewSoundAt", reviewedSound);
            var receive = rt.GetMethod("HandleStalledUserTurnDecision", flags);
            receive.Invoke(handler, new object[] { "take_turn", .9f, "singing", .9f, "user", "啦", 1000 });
            if (Flag("m_PendingSemanticEou"))
                throw new InvalidOperationException("Continuous voice without new ASR words accepts an obsolete handoff.");
            receive.Invoke(handler, new object[] { "interrupt_user", .6f, "singing", .9f, "user", "啦", 1000 });
            if (!Flag("m_PendingSemanticEou"))
                throw new InvalidOperationException("The role cannot intentionally interrupt a sustained vowel.");
        }
        finally
        {
            if (combined != null) UnityEngine.Object.DestroyImmediate(combined);
            UnityEngine.Object.DestroyImmediate(host);
        }
    }

    private static void RunFormalAudioChoiceRegression()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        GameObject host = new GameObject("FormalAudioChoiceRegression");
        host.SetActive(false);
        AudioClip audio = AudioClip.Create("test-unspoken-candidate", 1600, 1, 16000, false);
        try
        {
            ChatSample chat = host.AddComponent<ChatSample>();
            Type type = typeof(ChatSample);
            type.GetField("m_PreparedSingingBridgeClip", flags).SetValue(chat, audio);
            type.GetField("m_PreparedSingingBridgeText", flags).SetValue(chat, "わかったわ。");
            type.GetField("m_PreparedBridgeSourceTranscript", flags).SetValue(chat, "不完整的原先发言");
            string hint = (string)type.GetMethod("FinalizeSpeculativeTurn", flags).Invoke(chat,
                new object[] { "不，我改变主意了。" });
            if (!hint.Contains("最终输入") || !hint.Contains("保持沉默") ||
                type.GetField("m_PreparedSingingBridgeClip", flags).GetValue(chat) != audio || chat.IsAISpeaking)
                throw new InvalidOperationException("An unchosen candidate was played/destroyed before the final model could decide.");
            MethodInfo take = type.GetMethod("TakePreparedFormalReply", flags);
            if (take.Invoke(chat, new object[] { "今回はやめましょう。" }) != null ||
                take.Invoke(chat, new object[] { "わかったわ。" }) != audio ||
                take.Invoke(chat, new object[] { "わかったわ。" }) != null)
                throw new InvalidOperationException("Prepared audio was substituted for different words or consumed twice.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
            UnityEngine.Object.DestroyImmediate(audio);
        }
        Debug.Log("[FormalAudioChoiceRegression] final semantics, exact audio reuse and single ownership passed");
    }

    private static void RunEffectiveActivityRegression()
    {
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("EffectiveActivity: " + message);
        }
        var state = new TurnActivityEvidence();
        state.Reset(0f);
        state.ObserveAsr("旧歌词", "旧歌词", 100, 0f, true, .005f, .95f, .95f, .0055f);
        for (int i = 1; i <= 200; i++)
        {
            float t = i * .06f;
            state.ObserveLevel(i == 60 ? .035f : .004f + (i % 3) * .001f, t, .01f, .0055f);
            if (i % 15 == 0)
                state.ObserveAsr("旧歌词", "旧歌词", i * 60, t, true, .005f, .95f, .95f, .0055f);
        }
        Check(state.QuietSeconds(12f) > 11f && !state.MelodyActive && !state.LevelActive,
            "Noise, old lyrics, or one short spike repeatedly reset the silence clock.");
        Check(state.AsrHealthy(12f) && state.CoversQuietTail && state.LastTextAt == 0f,
            "ASR coverage was confused with new linguistic content.");
        Check(state.CoversRecentAudio(12000, 1000) && !state.CoversRecentAudio(18000, 1000),
            "A newly received packet describing old audio was accepted as current coverage.");
        int revision = state.Revision;
        Check(!state.ObserveAsr("旧歌词不", "旧歌词", 12100, 12.1f, true, .005f, 0f, 0f, .0055f) && state.PendingText,
            "An unstable quiet-tail correction should be pending, not counted as voice.");
        Check(state.ObserveAsr("旧歌词不", "旧歌词不", 12900, 12.9f, true, .005f, 0f, 0f, .0055f) &&
            state.Revision > revision && state.ActivityRevision == 0 && state.QuietSeconds(12.9f) > 12f,
            "A late one-character update must invalidate semantics without inventing a new sound.");
        Check(!state.ObserveAsr("旧歌词不啊", "旧歌词不啊", 12900, 13f, true, .005f, 0f, 0f, .0055f),
            "Duplicate audio packets were accepted as new coverage.");
        Check(!state.ObserveAsr("旧歌词", "旧歌词", 13800, 13.8f, true, .005f, 0f, 0f, .0055f),
            "A transcript rollback restarted the activity clock.");

        state.Reset(0f);
        string quietSpeech = "";
        for (int i = 0; i <= 250; i++)
        {
            float t = i * .06f;
            state.ObserveLevel(.006f, t, .01f, .004f);
            if (i % 15 == 0)
            {
                quietSpeech += "字";
                state.ObserveAsr(quietSpeech, quietSpeech, i * 60, t, true, .006f, .3f, .2f, .004f,
                    true, 600, 0, t);
            }
            Check(state.QuietSeconds(t) < 1f, "Quiet speech with real ASR advances can be cut off.");
        }

        state.Reset(0f);
        for (int i = 0; i <= 100; i++)
        {
            float t = i * .06f;
            state.ObserveLevel(.008f, t, .01f, .005f); // uncalibrated environment from the real log
            if (i % 15 == 0)
                state.ObserveAsr("旧歌词修订" + i, "旧歌词修订" + i, i * 60, t,
                    true, .008f, .3f, .2f, .005f, true, 0, -1, t);
        }
        Check(state.ActivityRevision == 0 && state.QuietSeconds(6f) > 5.9f,
            "ASR revisions plus above-default-floor room noise still invent new soft speech.");
        state.ObserveAsr("迟到的人声证据", "迟到的人声证据", 6100, 6.1f,
            true, .008f, .3f, .2f, .005f, true, 600, 100, 3f);
        Check(state.QuietSeconds(6.1f) > 6f, "Old VAD evidence was timestamped at response arrival.");
        state.Reset(0f);
        state.ObserveLevel(.006f, 1f, .01f, .004f);
        state.ObserveAsr("低声", "低声", 1000, 1.1f, true, .006f, .3f, .2f, .004f,
            true, 500, 200, 1f);
        state.ObserveLevel(.006f, 1.2f, .01f, .004f);
        Check(Mathf.Abs(state.QuietSince - .8f) < .01f,
            "The low-speech endpoint drifted forward with UI frames instead of audio evidence.");

        state.Reset(0f);
        for (int i = 0; i <= 350; i++)
        {
            float t = i * .06f;
            if (i % 15 == 0)
                state.ObserveAsr("", "", i * 60, t, true, .008f, .9f, .95f, .0035f);
            state.ObserveLevel(.008f, t, .01f, .0035f);
            Check(state.QuietSeconds(t) < .2f, "A long soft hummed note below 0.01 was treated as silence.");
        }
        for (int i = 351; i <= 450; i++)
        {
            float t = i * .06f;
            state.ObserveLevel(.0035f, t, .01f, .0035f);
            if (i % 15 == 0)
                state.ObserveAsr("", "", i * 60, t, true, .0035f, .9f, .95f, .0035f);
        }
        Check(!state.MelodyActive && state.QuietSeconds(27f) > 5f,
            "Historical melody protects a quiet tail forever.");
        Check(!state.AsrHealthy(30f), "A stalled ASR service is presented as current evidence.");

        state.Reset(0f);
        for (int i = 0; i < 200; i++) state.ObserveLevel(.02f, i * .06f, .01f, .004f);
        Check(state.LevelActive && state.QuietSeconds(12f) < .2f,
            "Sustained voice requires changing words to stay alive.");
        state.Reset(15f);
        Check(state.Revision == 0 && !state.TailAvailable && !state.AsrHealthy(15f) && !state.PendingText,
            "Activity evidence leaked into a new recording.");

        GameObject host = new GameObject("ActivityPreviewRegression");
        host.SetActive(false);
        AudioClip preview = null, final = null;
        try
        {
            var handler = host.AddComponent<RTSpeechHandler>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Type type = typeof(RTSpeechHandler);
            var chunks = (List<float[]>)type.GetField("m_RecordingPcmChunks", flags).GetValue(handler);
            chunks.Add(new float[16000]);
            type.GetField("m_RecordingCapturedFrames", flags).SetValue(handler, 16000);
            MethodInfo build = type.GetMethod("BuildAccumulatedRecordingClip", flags);
            preview = (AudioClip)build.Invoke(handler, new object[] { false });
            Check(preview != null && chunks.Count == 1, "Preview consumed the long-recording PCM accumulator.");
            chunks.Add(new float[16000]);
            type.GetField("m_RecordingCapturedFrames", flags).SetValue(handler, 32000);
            final = (AudioClip)build.Invoke(handler, new object[] { true });
            Check(final != null && Math.Abs(final.length - 2f) < .01f && chunks.Count == 0,
                "Resuming speech after a preview lost the start of the recording.");
        }
        finally
        {
            if (preview != null) UnityEngine.Object.DestroyImmediate(preview);
            if (final != null) UnityEngine.Object.DestroyImmediate(final);
            UnityEngine.Object.DestroyImmediate(host);
        }
        Debug.Log("[EffectiveActivityRegression] noise, ASR, soft speech/humming, stale evidence and non-destructive preview passed");
    }

    private static void RunLatencyHandoffRegression()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        GameObject host = new GameObject("LatencyHandoffRegression");
        host.SetActive(false);
        AudioClip opener = AudioClip.Create("speech-opener", 16000, 1, 16000, false);
        try
        {
            ChatSample chat = host.AddComponent<ChatSample>();
            Type type = typeof(ChatSample);
            void Set(string field, object value) => type.GetField(field, flags).SetValue(chat, value);
            object Get(string field) => type.GetField(field, flags).GetValue(chat);
            Set("m_PreparedSingingBridgeClip", opener);
            Set("m_PreparedBridgeSourceTranscript", "刚才只是练歌，你无视它吧");
            Set("m_PreparedSingingBridgeText", "练歌呀，那不用在意啦。");
            Set("m_PreparedBridgeIsSinging", false);
            Set("m_SingingBridgeTtsInFlight", true);
            Set("m_PendingBridgeIsSinging", false);
            MethodInfo rejectSinging = type.GetMethod("ReleasePreparedSingingOnly", flags);
            rejectSinging.Invoke(chat, null);
            if (Get("m_PreparedSingingBridgeClip") != opener || !(bool)Get("m_SingingBridgeTtsInFlight") ||
                (string)Get("m_PreparedBridgeSourceTranscript") != "刚才只是练歌，你无视它吧")
                throw new InvalidOperationException("Speech confirmation still destroys valid speech preparation.");
            Set("m_PendingBridgeIsSinging", true);
            rejectSinging.Invoke(chat, null);
            if ((bool)Get("m_SingingBridgeTtsInFlight") || Get("m_PreparedSingingBridgeClip") != opener)
                throw new InvalidOperationException("Cancelling a pending singing opener discards the ready speech opener.");
            // A singing slot can be discarded without cancelling an independent speech synthesis.
            Set("m_PreparedSingingBridgeClip", null);
            Set("m_PreparedBridgeIsSinging", true);
            Set("m_SingingBridgeTtsInFlight", true);
            Set("m_PendingBridgeIsSinging", false);
            rejectSinging.Invoke(chat, null);
            if (!(bool)Get("m_SingingBridgeTtsInFlight") || (string)Get("m_PreparedBridgeSourceTranscript") != "")
                throw new InvalidOperationException("Selective preparation cleanup crosses ready/pending slots.");

            MethodInfo gate = type.GetMethod("ResolveStreamingHandoff", statics);
            TTS.StreamingPlaybackPermission Resolve(bool current, bool playing, float time) =>
                (TTS.StreamingPlaybackPermission)gate.Invoke(null, new object[] { current, playing, time, 2.86f });
            if (Resolve(true, true, 1f) != TTS.StreamingPlaybackPermission.Wait ||
                Resolve(true, false, 2.86f) != TTS.StreamingPlaybackPermission.Play ||
                Resolve(false, false, 1f) != TTS.StreamingPlaybackPermission.Cancel ||
                Resolve(true, true, 20f) != TTS.StreamingPlaybackPermission.Cancel)
                throw new InvalidOperationException("Buffered formal speech can overlap an opener, survive cancellation, or wait forever.");

            RTSpeechHandler handler = host.AddComponent<RTSpeechHandler>();
            Type rt = typeof(RTSpeechHandler);
            void SetRt(string field, object value) => rt.GetField(field, flags).SetValue(handler, value);
            var activity = (TurnActivityEvidence)rt.GetField("m_TurnActivity", flags).GetValue(handler);
            activity.Reset(Time.realtimeSinceStartup);
            SetRt("m_IsRecording", true);
            SetRt("m_TentativeFired", true);
            SetRt("m_TentativeUsedFullAsr", true);
            rt.GetMethod("OnStreamingTranscript", flags).Invoke(handler, new object[] {
                new SenseVoiceSpeechToText.StreamingTranscript { Text = "旧音频的新修订", StableText = "旧音频的新修订",
                    AudioMs = 1000, ActivityAvailable = true, ActivityWindowMs = 800, ActivityRms = .003f }
            });
            if (!(bool)rt.GetField("m_TentativeUsedFullAsr", flags).GetValue(handler))
                throw new InvalidOperationException("A text-only revision discards full ASR that already covers its audio.");

            // An explicit clock avoids negative "before startup" timestamps in batch
            // mode, where the first editor update can occur only 0.1 seconds after reload.
            const float audioNow = 10f;
            activity.Reset(audioNow - 2f);
            activity.ObserveLevel(.008f, audioNow - .1f, .01f, .005f);
            activity.ObserveAsr("旧音频的新修订", "旧音频的新修订", 1000, audioNow - .2f,
                true, .008f, 0f, 0f, .005f);
            SetRt("m_TentativeFired", true);
            SetRt("m_StreamSubmittedSeconds", 2f);
            SetRt("m_LastStreamAudioSubmittedAt", audioNow);
            rt.GetMethod("ObserveStreamingTurnActivity", flags).Invoke(handler, new object[] {
                new SenseVoiceSpeechToText.StreamingTranscript { Text = "旧音频的新修订", StableText = "旧音频的新修订",
                    AudioMs = 2000, ActivityAvailable = true, ActivityWindowMs = 800, ActivityRms = .008f,
                    ActivityVadAvailable = true, ActivitySpeechMs = 600, ActivitySpeechEndAgeMs = 20 },
                audioNow + .1f
            });
            if ((bool)rt.GetField("m_TentativeUsedFullAsr", flags).GetValue(handler))
                throw new InvalidOperationException($"Newly confirmed voice with unchanged words kept an incomplete old preview. " +
                    $"now={audioNow:F3}, quietSince={activity.QuietSince:F3}, rms={activity.RecentRms:F4}, " +
                    $"revision={activity.ActivityRevision}, vadActive={activity.TextActivityActive}, " +
                    $"noiseUpper={rt.GetProperty("EffectiveNoiseUpper", flags).GetValue(handler)}");

            SetRt("m_PendingSemanticEou", true);
            SetRt("m_PendingSemanticEouStatus", "take_turn");
            SetRt("m_PendingSemanticEouConfidence", .9f);
            SetRt("m_PendingSemanticEouTextKey", "旧音频的新修订");
            SetRt("m_StalledReviewActivityRevision", activity.Revision);
            SetRt("m_LastMeaningfulStreamTextTime", Time.realtimeSinceStartup - 2f);
            SetRt("m_StalledRequiredUnchangedSeconds", .65f);
            SetRt("m_StreamSubmittedSeconds", 4f); // one delayed coverage packet
            SetRt("m_PendingSemanticDecisionAt", Time.realtimeSinceStartup);
            MethodInfo apply = rt.GetMethod("TryApplyPendingSemanticEou", flags);
            apply.Invoke(handler, null);
            if (!(bool)rt.GetField("m_PendingSemanticEou", flags).GetValue(handler))
                throw new InvalidOperationException("A valid role choice is thrown away while ASR coverage catches up.");
            SetRt("m_LatestStreamPartial", "新的后半句话");
            apply.Invoke(handler, null);
            if ((bool)rt.GetField("m_PendingSemanticEou", flags).GetValue(handler))
                throw new InvalidOperationException("Holding a role choice bypasses new-input invalidation.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
            UnityEngine.Object.DestroyImmediate(opener);
        }
        Debug.Log("[LatencyHandoffRegression] selective preparation, playback gate, late revisions and coverage hold passed");
        MethodInfo boundary = typeof(ChatSample).GetMethod("PreparedReplyBoundary", BindingFlags.Static | BindingFlags.NonPublic);
        int Prefix(string generated, bool complete) => (int)boundary.Invoke(null,
            new object[] { generated, "ふふっ、行くわね。", complete });
        if (Prefix("ふふっ、", false) != -2 || Prefix("ふふっ、", true) != -1 ||
            Prefix("ふふっ、待って。", false) != -1 || Prefix("ふふっ、行くわね。続き", false) != 8)
            throw new InvalidOperationException("Prepared audio may precede final model choice or block a revised reply.");
        MethodInfo prefetch = typeof(RTSpeechHandler).GetMethod("CanPrefetchFinalAnalysis", BindingFlags.Static | BindingFlags.NonPublic);
        bool Prefetch(float quiet, float drop, bool melody, float since) =>
            (bool)prefetch.Invoke(null, new object[] { quiet, drop, melody, since });
        if (!Prefetch(.35f, .4f, false, 2f) || Prefetch(.35f, .4f, true, 2f) ||
            Prefetch(.35f, 0f, false, 2f) || Prefetch(1f, 1f, false, .2f))
            throw new InvalidOperationException("Early analysis ignores real melody or repeatedly floods the ASR worker.");
    }

    private static void RunTurnBoundaryRegression()
    {
        const string ask = "{\"action\":\"ask_user\",\"confidence\":0.60,\"mode\":\"uncertain\",\"source\":\"uncertain\",\"turn_state\":\"uncertain\",\"reason\":\"想确认\"}";
        if (!TurnBoundaryDecision.TryParse(ask, out TurnBoundaryDecision parsed) ||
            !TurnBoundaryDecision.CanClose(parsed.action, parsed.confidence, 0.78f) ||
            !TurnBoundaryDecision.CanClose("take_turn", 0.4f, 0.78f) ||
            TurnBoundaryDecision.CanClose("continue", 1f, 0.78f) ||
            TurnBoundaryDecision.CanClose("complete", 0.6f, 0.78f) ||
            TurnBoundaryDecision.CanClose("take_turn", float.NaN, 0.78f))
            throw new InvalidOperationException("Uncertain asking/taking a turn is being blocked, or invalid certainty is accepted.");
        foreach (string invalid in new[]
        {
            "", "{}", ask.Substring(0, ask.Length - 1), ask.Replace("ask_user", "sing"),
            ask.Replace("0.60", "1.60"), ask.Replace("\"confidence\":0.60,", ""),
            ask.Replace("\"source\":\"uncertain\",", ""),
            ask.Replace("\"turn_state\":\"uncertain\",", ""),
            ask.Replace("\"action\":\"ask_user\"", "\"action\":\"take_turn\"")
               .Replace("\"turn_state\":\"uncertain\"", "\"turn_state\":\"open\""),
            ask.Replace("\"action\":\"ask_user\"", "\"action\":\"complete\""),
            ask.Replace("0.60", "null"), ask.Replace("0.60", "\"0.60\""),
            ask.Replace("\"reason\":\"想确认\"", "\"reason\":null"),
            ask.Replace("\"confidence\":0.60", "\"nested\":{\"confidence\":0.60}"),
        })
            if (TurnBoundaryDecision.TryParse(invalid, out _))
                throw new InvalidOperationException("Malformed or truncated boundary output can dispatch a turn: " + invalid);

        var evidence = new TurnBoundaryAcoustics();
        for (int i = 0; i < 30; i++) evidence.Observe(0.1f, i * 0.06f, 0.005f, 10f);
        if (evidence.DropSince >= 0f)
            throw new InvalidOperationException("A sustained note is mistaken for an amplitude drop.");
        //One short trough must not become a held drop.
        evidence.Observe(0.001f, 1.80f, 0.005f, 10f);
        evidence.Observe(0.1f, 1.86f, 0.005f, 10f);
        if (evidence.DropSince >= 0f)
            throw new InvalidOperationException("A single RMS trough became a turn boundary candidate.");
        for (int i = 32; i < 100; i++) evidence.Observe(0.01f, i * 0.06f, 0.005f, 10f);
        if (evidence.DropSince < 0f || Math.Abs(evidence.DropDb - 20f) > 0.1f ||
            evidence.QuietSince >= 0f || evidence.ReferenceRms < 0.09f)
            throw new InvalidOperationException("A held relative drop above ambient is lost or its reference follows the tail.");
        int revision = evidence.ActivityRevision;
        for (int i = 100; i < 110; i++) evidence.Observe(0.1f, i * 0.06f, 0.005f, 10f);
        if (evidence.DropSince >= 0f || evidence.ActivityRevision <= revision)
            throw new InvalidOperationException("Renewed voice does not invalidate the old boundary candidate.");
        evidence.Reset();
        if (evidence.DropSince >= 0f || evidence.ActivityRevision != 0)
            throw new InvalidOperationException("Acoustic evidence leaks across user turns.");

        MethodInfo delay = typeof(RTSpeechHandler).GetMethod("ResolveStalledReviewDelay", BindingFlags.Static | BindingFlags.NonPublic);
        if ((float)delay.Invoke(null, new object[] { true, false, 2.8f, 0.65f }) > 0.66f ||
            (float)delay.Invoke(null, new object[] { false, true, 2.8f, 0.65f }) > 1.21f)
            throw new InvalidOperationException("The fast boundary path serializes another fixed wait.");
        GameObject host = new GameObject("TurnBoundaryRegression");
        host.SetActive(false);
        try
        {
            ChatSample chat = host.AddComponent<ChatSample>();
            const string lyric = "もう一つ増やしましょう。";
            const string internalReason = "用户确认有歌，且刚唱完一句，顺势接话询问下一首。";
            const BindingFlags instancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
            const BindingFlags staticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
            typeof(ChatSample).GetMethod("SetCurrentUserInput", instancePrivate).Invoke(chat,
                new object[] { lyric, "[演唱片段] " + internalReason + lyric });
            string raw = (string)typeof(ChatSample).GetField("m_LastUserMsg", instancePrivate).GetValue(chat);
            MethodInfo invitation = typeof(ChatSample).GetMethod("IsSingAlongInvitation", staticPrivate);
            if (raw != lyric || (bool)invitation.Invoke(null, new object[] { raw }) ||
                !(bool)invitation.Invoke(null, new object[] { internalReason }) ||
                !(bool)typeof(ChatSample).GetMethod("IsCurrentTurnConfirmedSinging", instancePrivate).Invoke(chat, null))
                throw new InvalidOperationException("Internal evidence can still impersonate user intent, or singing facts were lost.");
            string guidance = (string)typeof(ChatSample).GetMethod("BuildTurnBoundaryGuidance", staticPrivate).Invoke(null, null);
            if (!guidance.Contains("不需要证明") || !guidance.Contains("新增音频覆盖=0") ||
                !guidance.Contains("现在适合接话"))
                throw new InvalidOperationException("The boundary prompt again pressures every available thought to speak.");
            string chatSource = System.IO.File.ReadAllText(Application.dataPath + "/AIChatTookit/Scripts/Chat/ChatSample.cs");
            int directStart = chatSource.IndexOf("private bool TryHandleDirectSingAlongTurn(", StringComparison.Ordinal);
            int directEnd = chatSource.IndexOf("private ", directStart + 14, StringComparison.Ordinal);
            if (chatSource.Substring(directStart, directEnd - directStart).Contains("ArmSingAlongForNextPerformance"))
                throw new InvalidOperationException("The non-tool fast path can still arm a standing follow request.");
            object[] tagArgs = { "<hum_back mode=\"follow\" reason=\"接下来轮唱\"/>" };
            object follow = typeof(ChatSample).GetMethod("ExtractHumBackTag", instancePrivate).Invoke(chat, tagArgs);
            if (follow == null || (string)follow.GetType().GetField("Mode").GetValue(follow) != "follow")
                throw new InvalidOperationException("Explicit follow intent is lost during tool parsing.");
            typeof(ChatSample).GetField("m_LogStreamTimings", instancePrivate).SetValue(chat, false);
            chat.CommitStalledTurnDecisionNote("continue", .7f, "uncertain", 0f, "uncertain");
            var filler = (System.Collections.IEnumerator)typeof(ChatSample)
                .GetMethod("MaybePlayLatencyFiller", instancePrivate).Invoke(chat,
                    new object[] { 0, "ja", "" });
            if (filler.MoveNext() ||
                !((string)typeof(ChatSample).GetField("m_StalledTurnDecisionNote", instancePrivate)
                    .GetValue(chat)).Contains("<silent/>"))
                throw new InvalidOperationException("Closing capture while the role waits can automatically play a filler.");
            typeof(ChatSample).GetField("m_ListeningClosedAt", instancePrivate).SetValue(chat, 1f);
            chat.RecordListeningEndEvidence(0f, "test", 0f, 0f, "test");
            typeof(ChatSample).GetMethod("ReportPerceivedFirstAudio", instancePrivate).Invoke(chat, null);
            if (!(bool)typeof(ChatSample).GetField("m_ListeningFormalLatencyArmed", instancePrivate).GetValue(chat))
                throw new InvalidOperationException("An opening sound consumes the separate formal-response latency measurement.");
            typeof(ChatSample).GetMethod("ReportPerceivedFormalAudio", instancePrivate).Invoke(chat, new object[] { "reply" });
            if ((bool)typeof(ChatSample).GetField("m_ListeningFormalLatencyArmed", instancePrivate).GetValue(chat))
                throw new InvalidOperationException("Formal first audio is counted more than once.");

            RTSpeechHandler handler = host.AddComponent<RTSpeechHandler>();
            Type handlerType = typeof(RTSpeechHandler);
            void Set(string field, object value) => handlerType.GetField(field,
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(handler, value);
            var levels = new List<float>();
            var times = new List<float>();
            for (int i = 0; i < 30; i++) { levels.Add(i >= 20 ? 0.05f : 0.004f); times.Add(i * 0.2f); }
            var mature = (List<float>)handlerType.GetMethod("SelectMatureAmbientSamples", staticPrivate)
                .Invoke(null, new object[] { levels, times, 5.8f, 2f });
            if (mature.Count < 8 || mature.Exists(v => v > 0.005f))
                throw new InvalidOperationException("Recent voice already changes its own ambient threshold.");
            Set("m_AmbientNoiseCalibrated", true);
            Set("m_AmbientRmsFloor", 0.004f);
            ((List<float>)handlerType.GetField("m_AmbientRmsSamples", instancePrivate).GetValue(handler)).Add(0.05f);
            ((List<float>)handlerType.GetField("m_AmbientRmsSampleTimes", instancePrivate).GetValue(handler)).Add(Time.realtimeSinceStartup);
            handlerType.GetMethod("DiscardRecentAmbientSamples", instancePrivate).Invoke(handler, new object[] { 2f });
            if (!(bool)handlerType.GetField("m_AmbientNoiseCalibrated", instancePrivate).GetValue(handler) ||
                Math.Abs((float)handlerType.GetField("m_AmbientRmsFloor", instancePrivate).GetValue(handler) - 0.004f) > 0.0001f)
                throw new InvalidOperationException("Onset rollback discards the last trusted environment calibration.");
            Set("m_AmbientHandoffHoldUntil", Time.realtimeSinceStartup + 2f);
            handlerType.GetMethod("ObserveAmbientRms", instancePrivate).Invoke(handler, new object[] { 0.05f, true });
            if (((List<float>)handlerType.GetField("m_AmbientRmsSamples", instancePrivate).GetValue(handler)).Count != 0)
                throw new InvalidOperationException("A negative VAD probe bypasses handoff quarantine.");
            Set("m_IsRecording", true);
            Set("m_PendingSemanticEou", true);
            Set("m_PendingSemanticEouStatus", "complete");
            Set("m_PendingSemanticEouConfidence", 0.6f);
            Set("m_PendingSemanticEouTextKey", "已经说完");
            Set("m_LatestStreamPartial", "已经说完");
            Set("m_LastMeaningfulStreamTextTime", Time.realtimeSinceStartup - 10f);
            Set("m_LastStreamProgressTime", Time.realtimeSinceStartup);
            Set("m_StalledRequiredUnchangedSeconds", 0.65f);
            bool accepted = (bool)handlerType.GetMethod("TryApplyPendingSemanticEou",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(handler, null);
            if (accepted || (bool)handlerType.GetField("m_PendingSemanticEou",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handler))
                throw new InvalidOperationException("Rejected low-confidence completion still locks pending forever.");

            MethodInfo receive = handlerType.GetMethod("HandleStalledUserTurnDecision",
                BindingFlags.Instance | BindingFlags.NonPublic);
            bool ReadFlag(string field) => (bool)handlerType.GetField(field,
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handler);
            Set("m_StalledTranscriptReviewRequested", true);
            receive.Invoke(handler, new object[] { "", 0f, "uncertain", 0f, "uncertain", "已经说完", 1000 });
            if (ReadFlag("m_StalledTranscriptReviewRequested") || ReadFlag("m_PendingSemanticEou"))
                throw new InvalidOperationException("Invalid wire output does not release the review.");
            //Uncertain asking is a choice, not a certainty claim.
            receive.Invoke(handler, new object[] { "ask_user", 0.6f, "uncertain", 0f, "uncertain", "已经说完", 1000 });
            if (!ReadFlag("m_PendingSemanticEou"))
                throw new InvalidOperationException("A valid uncertain question cannot reach the commit path.");
            //Even a short genuine new word, ignored by coarse drift timing, invalidates the old choice.
            Set("m_LatestStreamPartial", "已经说完不");
            receive.Invoke(handler, new object[] { "take_turn", 0.9f, "speech", 0f, "user", "已经说完", 1000 });
            if (ReadFlag("m_PendingSemanticEou"))
                throw new InvalidOperationException("A stale boundary choice ignores newly arrived short words.");
            Set("m_LatestStreamPartial", "已经说完");
            Set("m_StalledReviewActivityRevision", -1);
            receive.Invoke(handler, new object[] { "take_turn", 0.9f, "speech", 0f, "user", "已经说完", 1000 });
            if (ReadFlag("m_PendingSemanticEou"))
                throw new InvalidOperationException("Renewed sound does not invalidate an in-flight turn choice.");

            ChatQW provider = host.AddComponent<ChatQW>();
            provider.m_Backend = ChatQW.BackendType.Local;
            provider.m_EphemeralMaxTokens = 48; //Regression: boundary budget must not inherit this.
            provider.m_DataList.Add(new LLM.SendData("user", "[感知帧] STALE_RECORDING_STATE\n" +
                "(用户刚开口讲了下面这段话，请回应)上一次的说明"));
            provider.m_DataList.Add(new LLM.SendData("assistant", "上一次的回答"));
            provider.ActiveSkillContext = "STALE_SKILL_PROTOCOL";
            provider.TrailingContext = "STALE_LIVE_PERCEPTION";
            int historyCount = provider.m_DataList.Count;
            string json = (string)typeof(ChatQW).GetMethod("BuildTurnBoundaryRequestJson",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(provider, new object[] { "test JSON" });
            if (!json.Contains("\"max_tokens\":256") || !json.Contains("\"schema\"") ||
                !json.Contains("\"take_turn\"") || !json.Contains("\"id_slot\":1") ||
                provider.m_DataList.Count != historyCount || !provider.SupportsTurnBoundaryMessages)
                throw new InvalidOperationException("Boundary wire format, token budget, slot or history isolation regressed.");
            var boundaryJson = Newtonsoft.Json.Linq.JObject.Parse(json);
            var messages = (Newtonsoft.Json.Linq.JArray)boundaryJson["messages"];
            int userMessages = 0, assistantMessages = 0;
            foreach (var message in messages)
            {
                if ((string)message["role"] == "user") userMessages++;
                if ((string)message["role"] == "assistant") assistantMessages++;
            }
            if (userMessages != 1 || assistantMessages != 0 || json.Contains("STALE_") ||
                !json.Contains("上一次的说明") || !json.Contains("interrupt_user"))
                throw new InvalidOperationException("Boundary context replays stale perception or history as current input.");
            string draftJson = (string)typeof(ChatQW).GetMethod("BuildEphemeralRequestJson",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(provider, new object[] { "draft", 1, false });
            if (!draftJson.Contains("STALE_SKILL_PROTOCOL") || !draftJson.Contains("STALE_LIVE_PERCEPTION"))
                throw new InvalidOperationException("Boundary isolation incorrectly removed context from normal private drafts.");
            if ((string)messages[messages.Count - 2]["role"] != "system" ||
                !((string)messages[messages.Count - 2]["content"]).Contains("本轮唯一的新输入") ||
                (string)messages[messages.Count - 1]["content"] != "test JSON")
                throw new InvalidOperationException("Boundary history and current input do not have an explicit evidence scope.");
            if (typeof(ChatSample).GetMethod("RecordListeningEndEvidence") == null ||
                typeof(ChatSample).GetMethod("ReportPerceivedFirstAudio", BindingFlags.Instance | BindingFlags.NonPublic) == null)
                throw new MissingMemberException("Capture-close delay is missing from perceived latency instrumentation.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void RunPracticeConfirmationAndMemoryGroundingRegression(Type chatType)
    {
        MethodInfo parseMethod = chatType.GetMethod(
            "ParseIndexedClassifierDecision",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo groundedFactsMethod = chatType.GetMethod(
            "SelectFullyGroundedFactOrdinals",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo normalizePracticeDropsMethod = chatType.GetMethod(
            "NormalizePracticeDropIndices",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo knownAgentTagMethod = chatType.GetMethod(
            "IsKnownAgentTagName",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (parseMethod == null || groundedFactsMethod == null ||
            normalizePracticeDropsMethod == null || knownAgentTagMethod == null)
            throw new MissingMemberException(
                "Practice tool routing, indexed semantic-decision, batch-drop, or atomic-grounding helper was renamed without updating the regression suite.");

        if (!(bool)knownAgentTagMethod.Invoke(null, new object[] { "practice_confirm" }) ||
            !(bool)knownAgentTagMethod.Invoke(null, new object[] { "practice_revise" }) ||
            !(bool)knownAgentTagMethod.Invoke(null, new object[] { "practice_drop" }))
            throw new InvalidOperationException(
                "A main-role practice confirmation, revision, or destructive-drop tag is no longer recognized by the agent protocol.");

        var allowed = new List<int> { 1, 2, 3 };
        List<int> confirmed = parseMethod.Invoke(null, new object[]
        {
            "confirm:1,3,9", "confirm:", allowed,
        }) as List<int>;
        List<int> rejected = parseMethod.Invoke(null, new object[]
        {
            "reject:2", "reject:", allowed,
        }) as List<int>;
        List<int> none = parseMethod.Invoke(null, new object[]
        {
            "none", "confirm:", allowed,
        }) as List<int>;
        List<int> quarantined = parseMethod.Invoke(null, new object[]
        {
            "confirm:0", "confirm:", new List<int> { 0, 2 },
        }) as List<int>;
        if (confirmed == null || confirmed.Count != 2 ||
            confirmed[0] != 1 || confirmed[1] != 3 ||
            rejected == null || rejected.Count != 1 || rejected[0] != 2 ||
            none == null || none.Count != 0 ||
            quarantined == null || quarantined.Count != 1 || quarantined[0] != 0)
            throw new InvalidOperationException(
                "Practice confirmation decisions no longer stay within pending segment indices.");

        object[] validDropArgs = { new[] { 2, 4, 2 }, 5, "" };
        int[] normalizedDrops = normalizePracticeDropsMethod.Invoke(null, validDropArgs) as int[];
        if (normalizedDrops == null || normalizedDrops.Length != 2 ||
            normalizedDrops[0] != 4 || normalizedDrops[1] != 2 ||
            !string.IsNullOrEmpty(validDropArgs[2] as string))
            throw new InvalidOperationException(
                "Multiple practice_drop tags no longer share one pre-deletion checklist or descending execution order.");

        object[] invalidDropArgs = { new[] { 2, 6 }, 5, "" };
        int[] rejectedDrops = normalizePracticeDropsMethod.Invoke(null, invalidDropArgs) as int[];
        if (rejectedDrops == null || rejectedDrops.Length != 0 ||
            string.IsNullOrWhiteSpace(invalidDropArgs[2] as string))
            throw new InvalidOperationException(
                "An invalid batch practice_drop can still partially mutate the checklist.");

        var atomToFact = new Dictionary<int, int>
        {
            { 1, 1 }, { 2, 1 }, { 3, 2 },
        };
        List<int> grounded = groundedFactsMethod.Invoke(null, new object[]
        {
            new List<int> { 1, 2 }, atomToFact, new List<int> { 1, 3 },
        }) as List<int>;
        if (grounded == null || grounded.Count != 1 || grounded[0] != 2)
            throw new InvalidOperationException(
                "A composite memory was accepted even though one atomic claim lacked evidence.");

        if (typeof(SenseVoiceSpeechToText).GetMethod("TryRevisePracticePhrase") == null ||
            typeof(SenseVoiceSpeechToText).GetMethod("DropPracticePhraseByStableId") == null ||
            typeof(SenseVoiceSpeechToText).GetMethod("ConfirmQuarantinedSingingCandidateWithPlaybackStatus") == null ||
            typeof(SenseVoiceSpeechToText).GetMethod("HasKnownSingingCandidate") == null ||
            typeof(SenseVoiceSpeechToText).GetMethod("TryResolveConfirmedSingingCandidate") == null ||
            typeof(SenseVoiceSpeechToText).GetMethod("TryResolvePracticeStableId") == null ||
            typeof(ChatQW).GetMethod("DecomposeMemoryClaims") == null ||
            typeof(ChatQW).GetMethod("ValidateMemoryGrounding") == null)
            throw new MissingMemberException(
                "Main-role practice confirmation/revision or atomic memory grounding contract is missing.");

        TextAsset behavior = AssetDatabase.LoadAssetAtPath<TextAsset>(
            "Assets/AIChatTookit/Prompts/behavior.txt");
        TextAsset singing = AssetDatabase.LoadAssetAtPath<TextAsset>(
            "Assets/AIChatTookit/Prompts/Skills/singing.txt");
        if (behavior == null ||
            !behavior.text.Contains("不能因此记成“用户喜欢/最爱/偏好这首歌”") ||
            !behavior.text.Contains("长期事实写入前系统会"))
            throw new InvalidOperationException(
                "Memory prompt no longer distinguishes singing/mentioning from preference evidence.");
        if (singing == null ||
            !singing.text.Contains("<sing refs=") ||
            !singing.text.Contains("<clip_confirm ref=") ||
            !singing.text.Contains("<clip_revise ref=") ||
            !singing.text.Contains("<clip_drop ref=") ||
            !singing.text.Contains("以该份原录音开始为 0 秒") ||
            !singing.text.Contains("来源确认成功但 playback 仍非 ready") ||
            !singing.text.Contains("原始录音仍保留") ||
            !singing.text.Contains("不确定时，你始终可以先询问") ||
            !singing.text.Contains("删除具有破坏性") ||
            !singing.text.Contains("说话→唱歌→说话") ||
            !singing.text.Contains("不能只看单个标签判定没唱"))
            throw new InvalidOperationException(
                "Singing Skill lost main-role confirmation, non-destructive revision, stable identity, or direct routing guidance.");
    }

    private static void RunSingingModeFusionRegression(Type chatType)
    {
        if (!SenseVoiceSpeechToText.IsAcousticProbabilityUncertain(0.52f) ||
            !SenseVoiceSpeechToText.IsAcousticProbabilityUncertain(0.5799f) ||
            SenseVoiceSpeechToText.IsAcousticProbabilityUncertain(0.5199f) ||
            SenseVoiceSpeechToText.IsAcousticProbabilityUncertain(0.58f))
            throw new InvalidOperationException(
                "The acoustic uncertainty band is no longer exactly [0.52, 0.58).");

        MethodInfo fuseMethod = chatType.GetMethod(
            "FuseSingingModeEvidence", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo melodicReviewMethod = chatType.GetMethod(
            "ShouldRequestRoleModeReviewFromMelody",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo failureKindMethod = chatType.GetMethod(
            "ClassifySongSingFailureCode",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo precedingSpeechMethod = typeof(SenseVoiceSpeechToText).GetMethod(
            "ExtractPrecedingSpeechFromMixedTranscript",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo mixedTurnEvidenceMethod = typeof(SenseVoiceSpeechToText).GetMethod(
            "BuildMixedTurnEvidence",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo informativeMixedEvidenceMethod = typeof(SenseVoiceSpeechToText).GetMethod(
            "ClassifyInformativeMixedTurnEvidence",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo replaceSingingCandidateMethod = typeof(SenseVoiceSpeechToText).GetMethod(
            "ShouldReplacePlayableCandidate",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo preferAcousticIslandMethod = typeof(SenseVoiceSpeechToText).GetMethod(
            "ShouldPreferAcousticIslandForSpokenLeadIn",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo scheduleSingingReviewMethod = chatType.GetMethod(
            "ShouldScheduleSingingReview",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo acceptSingingReviewMethod = chatType.GetMethod(
            "ShouldAcceptSingingReviewResult",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo directAcousticLatchMethod = typeof(RTSpeechHandler).GetMethod(
            "ShouldApplyDirectAcousticSingingLatch",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo recordingActivityThresholdMethod = typeof(RTSpeechHandler).GetMethod(
            "ResolveRecordingActivityThreshold",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo ambientFloorMethod = typeof(RTSpeechHandler).GetMethod(
            "EstimateAmbientFloor",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo adaptiveStartMethod = typeof(RTSpeechHandler).GetMethod(
            "ResolveAdaptiveStartThreshold",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo adaptiveEouMethod = typeof(RTSpeechHandler).GetMethod(
            "ResolveAdaptiveEouThreshold",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo stalledDecisionMethod = typeof(RTSpeechHandler).GetMethod(
            "ShouldAcceptStalledTurnDecision",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo meaningfulTranscriptMethod = typeof(RTSpeechHandler).GetMethod(
            "HasMeaningfulTranscriptChange",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo repeatedMelodicEntryMethod = chatType.GetMethod(
            "ShouldSuppressRepeatedMelodicEntry",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (fuseMethod == null || melodicReviewMethod == null || failureKindMethod == null ||
            precedingSpeechMethod == null || mixedTurnEvidenceMethod == null ||
            informativeMixedEvidenceMethod == null || replaceSingingCandidateMethod == null ||
            preferAcousticIslandMethod == null || scheduleSingingReviewMethod == null ||
            acceptSingingReviewMethod == null || directAcousticLatchMethod == null ||
            recordingActivityThresholdMethod == null || ambientFloorMethod == null ||
            adaptiveStartMethod == null || adaptiveEouMethod == null ||
            stalledDecisionMethod == null || meaningfulTranscriptMethod == null ||
            repeatedMelodicEntryMethod == null)
            throw new MissingMemberException(
                "Singing evidence review helpers were renamed without updating the regression suite.");

        string separatedPrefix = (string)precedingSpeechMethod.Invoke(null, new object[]
        {
            "这一段我再唱一次。望月的旋律歌词",
            "望月的旋律歌词",
        });
        string ungroundedPrefix = (string)precedingSpeechMethod.Invoke(null, new object[]
        {
            "整轮转写与分段转写对不上",
            "另一份歌词",
        });
        if (separatedPrefix != "这一段我再唱一次。" || ungroundedPrefix != "")
            throw new InvalidOperationException(
                "Mixed-turn lyrics are being retained as preceding speech without textual evidence.");

        string mixedTurnEvidence = (string)mixedTurnEvidenceMethod.Invoke(null, new object[]
        {
            "这一次没问题了，接下来我接着唱。",
            "もういっぱいあるけど、もう一つ増やしましょう。",
            "ja",
            5.53f,
            17.74f,
            1.92f,
            "",
        });
        if (string.IsNullOrWhiteSpace(mixedTurnEvidence) ||
            !mixedTurnEvidence.Contains("同轮分轨观测") ||
            !mixedTurnEvidence.Contains("前置音频约5.5秒") ||
            !mixedTurnEvidence.Contains("可播放旋律岛约17.7秒") ||
            !mixedTurnEvidence.Contains("整轮ASR文字通道") ||
            !mixedTurnEvidence.Contains("歌唱岛独立ASR文字通道") ||
            !mixedTurnEvidence.Contains("不等于这一轮没有发生歌声") ||
            !mixedTurnEvidence.Contains("接下来我接着唱") ||
            !mixedTurnEvidence.Contains("もういっぱいあるけど"))
            throw new InvalidOperationException(
                "A speech-then-singing turn can again hide the recorded singing channel from the role LLM.");

        string singingThenSpeechEvidence = (string)mixedTurnEvidenceMethod.Invoke(
            null, new object[]
            {
                "望月的旋律歌词。很好听，我接下来继续唱。",
                "望月的旋律歌词",
                "ja",
                0f,
                10.2f,
                3.1f,
                "很好听，我接下来继续唱。",
            });
        string speechSingingSpeechEvidence = (string)mixedTurnEvidenceMethod.Invoke(
            null, new object[]
            {
                "我先试一下。望月的旋律歌词。唱到这里。",
                "望月的旋律歌词",
                "ja",
                2.4f,
                10.2f,
                2.2f,
                "唱到这里。",
            });
        if (!singingThenSpeechEvidence.Contains("可播放旋律岛约10.2秒") ||
            !singingThenSpeechEvidence.Contains("尾部音频约3.1秒") ||
            !singingThenSpeechEvidence.Contains("尾部独立ASR文字通道") ||
            !speechSingingSpeechEvidence.Contains("前置音频约2.4秒") ||
            !speechSingingSpeechEvidence.Contains("可播放旋律岛约10.2秒") ||
            !speechSingingSpeechEvidence.Contains("尾部音频约2.2秒"))
            throw new InvalidOperationException(
                "Singing-then-speech or speech-singing-speech evidence lost an independent timeline channel.");

        string missingIslandLyricsReason = (string)informativeMixedEvidenceMethod.Invoke(
            null, new object[]
            {
                "接下来我唱下一段。もういっぱい増や。",
                "もういっぱいあるけど、もう一つ増やしましょう。",
                0.15f,
                23.17f,
                0f,
                "",
                false,
            });
        string equivalentPureSingingReason = (string)informativeMixedEvidenceMethod.Invoke(
            null, new object[]
            {
                "もういっぱいあるけど、もう一つ増やしましょう。",
                "もういっぱいあるけど、もう一つ増やしましょう。",
                0.12f,
                17.74f,
                0.08f,
                "",
                true,
            });
        string explicitLeadInReason = (string)informativeMixedEvidenceMethod.Invoke(
            null, new object[]
            {
                "下一段开始。望月的旋律歌词",
                "望月的旋律歌词",
                2.4f,
                11.2f,
                0f,
                "",
                true,
            });
        string spokenTailReason = (string)informativeMixedEvidenceMethod.Invoke(
            null, new object[]
            {
                "望月的旋律歌词",
                "望月的旋律歌词",
                0f,
                10.2f,
                3.1f,
                "这一段不算，我们重新唱。",
                false,
            });
        string pureSpeechTimelineReason = (string)informativeMixedEvidenceMethod.Invoke(
            null, new object[]
            {
                "把唱过的几段合起来。",
                "",
                0.72f,
                4.37f,
                0f,
                "",
                false,
            });
        if (missingIslandLyricsReason != "singing_text_adds_information" ||
            !string.IsNullOrEmpty(equivalentPureSingingReason) ||
            explicitLeadInReason != "temporal_channel_split" ||
            spokenTailReason != "tail_text_adds_information" ||
            !string.IsNullOrEmpty(pureSpeechTimelineReason))
            throw new InvalidOperationException(
                "Conditional mixed-turn evidence no longer covers missing lyrics, lead-ins, and tails without duplicating pure singing.");

        bool shorterSpokenTailReplaces = (bool)replaceSingingCandidateMethod.Invoke(
            null, new object[] { 21.0f, 1.8f, 4.37f, 2.7f });
        bool longerSingingPrefixReplaces = (bool)replaceSingingCandidateMethod.Invoke(
            null, new object[] { 6.2f, 2.4f, 10.6f, 2.1f });
        if (shorterSpokenTailReplaces || !longerSingingPrefixReplaces)
            throw new InvalidOperationException(
                "A short spoken tail can again overwrite the best singing candidate from the same recording.");

        bool explicitSpeechPrefixUsesIsland = (bool)preferAcousticIslandMethod.Invoke(
            null, new object[] { true, true, 8.33f, 1.94f });
        bool ordinaryOnsetDisagreementUsesIsland = (bool)preferAcousticIslandMethod.Invoke(
            null, new object[] { false, true, 8.33f, 1.94f });
        bool marginalBoundaryDifferenceUsesIsland = (bool)preferAcousticIslandMethod.Invoke(
            null, new object[] { true, true, 3.20f, 1.94f });
        if (!explicitSpeechPrefixUsesIsland || ordinaryOnsetDisagreementUsesIsland ||
            marginalBoundaryDifferenceUsesIsland)
            throw new InvalidOperationException(
                "Semantic speech lead-ins no longer select the later acoustic singing island conservatively.");

        bool firstSingingFrameSchedulesReview = (bool)scheduleSingingReviewMethod.Invoke(
            null, new object[] { false, false });
        bool nextFrameCancelsPendingReview = (bool)scheduleSingingReviewMethod.Invoke(
            null, new object[] { true, false });
        bool parallelReviewIsScheduled = (bool)scheduleSingingReviewMethod.Invoke(
            null, new object[] { false, true });
        bool stableQuestionAcceptsReview = (bool)acceptSingingReviewMethod.Invoke(
            null, new object[]
            {
                true,
                "那你能把我唱的这段唱出来吗？",
                "那你能把我唱的这段唱出来吗？",
            });
        bool supersededTextAcceptsReview = (bool)acceptSingingReviewMethod.Invoke(
            null, new object[] { true, "请唱这一段", "我们换个完全不同的话题" });
        bool finishedTurnAcceptsReview = (bool)acceptSingingReviewMethod.Invoke(
            null, new object[] { false, "同一句", "同一句" });
        if (!firstSingingFrameSchedulesReview || nextFrameCancelsPendingReview ||
            parallelReviewIsScheduled || !stableQuestionAcceptsReview ||
            supersededTextAcceptsReview || finishedTurnAcceptsReview)
            throw new InvalidOperationException(
                "Singing semantic review can again starve on frame cadence or accept a superseded turn.");

        bool coordinatedSingleFrameLatch = (bool)directAcousticLatchMethod.Invoke(
            null, new object[] { true, true, false, 0.73f, 0.58f });
        bool fallbackSingleFrameLatch = (bool)directAcousticLatchMethod.Invoke(
            null, new object[] { false, true, false, 0.73f, 0.58f });
        if (coordinatedSingleFrameLatch || !fallbackSingleFrameLatch)
            throw new InvalidOperationException(
                "A single acoustic frame can lock singing despite an active semantic coordinator.");

        float adaptiveRecordingThreshold = (float)recordingActivityThresholdMethod.Invoke(
            null, new object[] { 0.01f, 0.0126f, 1.20f });
        if (Math.Abs(adaptiveRecordingThreshold - 0.01512f) > 0.0001f)
            throw new InvalidOperationException(
                "Recording EOU no longer raises its activity threshold above persistent ambient noise.");

        float robustFloor = (float)ambientFloorMethod.Invoke(null, new object[]
        {
            new List<float> { 0.0010f, 0.0011f, 0.0012f, 0.0013f, 0.0300f },
            0.30f,
        });
        float quietStart = (float)adaptiveStartMethod.Invoke(null, new object[]
        {
            0.01f, robustFloor, true, 0.004f, 2.50f, 0.05f,
        });
        float quietEou = (float)adaptiveEouMethod.Invoke(null, new object[]
        {
            0.01f, robustFloor, true, 0.003f, 1.25f, quietStart,
        });
        float noisyStart = (float)adaptiveStartMethod.Invoke(null, new object[]
        {
            0.01f, 0.0126f, true, 0.004f, 2.50f, 0.05f,
        });
        if (robustFloor >= 0.002f || Math.Abs(quietStart - 0.004f) > 0.0001f ||
            Math.Abs(quietEou - 0.003f) > 0.0001f || noisyStart <= 0.03f ||
            quietEou >= quietStart)
            throw new InvalidOperationException(
                "Adaptive RMS thresholds no longer lower in quiet rooms, rise in noise, or preserve onset/offset hysteresis.");

        float externalSpeakerFloor = (float)ambientFloorMethod.Invoke(null, new object[]
        {
            new List<float>
            {
                0.0121f, 0.0124f, 0.0125f, 0.0126f, 0.0127f,
                0.0128f, 0.0130f, 0.031f, 0.044f, 0.050f,
            },
            0.30f,
        });
        if (externalSpeakerFloor < 0.0123f || externalSpeakerFloor > 0.0128f)
            throw new InvalidOperationException(
                "Persistent external-speaker ambience is no longer learned robustly while speech spikes remain outliers.");

        bool acceptsComplete = (bool)stalledDecisionMethod.Invoke(null, new object[]
        {
            "complete", 0.86f, 0.78f, "已经说完", "已经说完", 3.2f, 2.8f,
        });
        bool acceptsAskUser = (bool)stalledDecisionMethod.Invoke(null, new object[]
        {
            "ask_user", 0.82f, 0.78f, "同一句", "同一句", 3.2f, 2.8f,
        });
        bool acceptsContinue = (bool)stalledDecisionMethod.Invoke(null, new object[]
        {
            "continue", 0.95f, 0.78f, "同一句", "同一句", 5f, 2.8f,
        });
        bool acceptsSuperseded = (bool)stalledDecisionMethod.Invoke(null, new object[]
        {
            "complete", 0.95f, 0.78f, "旧文字", "新文字", 5f, 2.8f,
        });
        bool acceptsLowConfidence = (bool)stalledDecisionMethod.Invoke(null, new object[]
        {
            "complete", 0.70f, 0.78f, "同一句", "同一句", 5f, 2.8f,
        });
        if (!acceptsComplete || !acceptsAskUser || acceptsContinue ||
            acceptsSuperseded || acceptsLowConfidence)
            throw new InvalidOperationException(
                "Semantic EOU can again close a continuing, stale, or low-confidence user turn.");

        bool exactTextChanged = (bool)meaningfulTranscriptMethod.Invoke(
            null, new object[] { "我已经说完", "我已经说完" });
        bool oneCharacterAsrDriftChanged = (bool)meaningfulTranscriptMethod.Invoke(
            null, new object[] { "我已经说完", "我已经说完了" });
        bool fourCharacterTailDriftChanged = (bool)meaningfulTranscriptMethod.Invoke(
            null, new object[] { "羊を数えるように", "羊を数えるようにTheI" });
        bool rollbackTailDriftChanged = (bool)meaningfulTranscriptMethod.Invoke(
            null, new object[] { "羊を数えるようにTheI", "羊を数えるように" });
        bool meaningfulGrowthChanged = (bool)meaningfulTranscriptMethod.Invoke(
            null, new object[] { "我已经", "我已经说完这件事" });
        if (exactTextChanged || oneCharacterAsrDriftChanged ||
            fourCharacterTailDriftChanged || rollbackTailDriftChanged ||
            !meaningfulGrowthChanged)
            throw new InvalidOperationException(
                "Streaming transcript stability no longer ignores short ASR tail drift or notices real growth.");

        bool suppressSameSpeech = (bool)repeatedMelodicEntryMethod.Invoke(null, new object[]
        {
            "speech", 0.91f, 0.82f, "我已经说完了", "我已经说完了",
        });
        bool suppressChangedContent = (bool)repeatedMelodicEntryMethod.Invoke(null, new object[]
        {
            "speech", 0.91f, 0.82f, "我已经说完了", "后面开始真正哼唱",
        });
        bool suppressSemanticSinging = (bool)repeatedMelodicEntryMethod.Invoke(null, new object[]
        {
            "singing", 0.95f, 0.82f, "同一句", "同一句",
        });
        if (!suppressSameSpeech || suppressChangedContent || suppressSemanticSinging)
            throw new InvalidOperationException(
                "Same-text acoustic frames can thrash back into singing, or new singing content is being suppressed.");

        if (typeof(LLM).GetMethod("PostTurnBoundaryMsg") == null ||
            typeof(LLM).GetMethod("CancelTurnBoundaryMsg") == null ||
            typeof(ChatQW).GetMethod("PostTurnBoundaryMsg") == null ||
            chatType.GetMethod("RequestStalledTurnReview") == null ||
            chatType.GetMethod("CommitStalledTurnDecisionNote") == null ||
            typeof(SenseVoiceSpeechToText).GetMethod("QuarantineRecentSingingCandidate") == null ||
            typeof(SenseVoiceSpeechToText).GetMethod("DescribeQuarantinedSingingCandidates") == null ||
            typeof(SenseVoiceSpeechToText).GetMethod("TryGetQuarantinedSingingCandidateFacts") == null ||
            typeof(SenseVoiceSpeechToText).GetMethod("ConfirmQuarantinedSingingCandidate") == null ||
            typeof(SenseVoiceSpeechToText).GetMethod("DiscardQuarantinedSingingCandidate") == null)
            throw new MissingMemberException(
                "Stalled-turn semantic review or source-uncertain singing quarantine contract is missing.");

        if (chatType.GetMethod("BeginDeferredFinalAsr") == null ||
            chatType.GetMethod("AcceptDeferredFinalAsrText") == null ||
            chatType.GetMethod("FallbackDeferredFinalAsrToClip") == null)
            throw new MissingMemberException(
                "Tentative full-ASR promotion can no longer keep formal work pending or fall back safely.");

        string Fuse(
            bool acousticSinging,
            bool acousticUncertain,
            string finalVerdict,
            bool streamingSinging,
            bool streamingSpeech)
        {
            object result = fuseMethod.Invoke(null, new object[]
            {
                acousticSinging,
                acousticUncertain,
                finalVerdict,
                streamingSinging,
                streamingSpeech,
            });
            return result != null ? result.ToString() : "";
        }

        if (Fuse(false, false, "singing", true, false) != "ConflictSemanticSinging" ||
            Fuse(true, false, "speech", false, true) != "ConflictSemanticSpeech" ||
            Fuse(false, false, "", true, false) != "ConflictSemanticSinging" ||
            Fuse(true, false, "", false, true) != "ConflictSemanticSpeech")
            throw new InvalidOperationException(
                "Opposite semantic/acoustic judgments no longer enter confirmation state.");

        if (Fuse(false, false, "speech", true, false) != "AcousticSpeech" ||
            Fuse(true, false, "singing", false, true) != "AcousticSinging" ||
            Fuse(false, false, "uncertain", true, false) != "AcousticSpeech" ||
            Fuse(false, false, "", false, false) != "AcousticSpeech")
            throw new InvalidOperationException(
                "Final semantic evidence no longer supersedes stale streaming judgments.");

        if (Fuse(false, true, "singing", false, true) !=
                "AcousticUncertainSemanticSinging" ||
            Fuse(true, true, "speech", true, false) !=
                "AcousticUncertainSemanticSpeech" ||
            Fuse(false, true, "uncertain", true, false) != "AcousticUncertain")
            throw new InvalidOperationException(
                "The acoustic 0.52-0.58 uncertainty band no longer remains non-binary.");

        bool loggedMelodicSampleTriggersReview = (bool)melodicReviewMethod.Invoke(
            null, new object[] { true, 9.67f, 10.40f, 0.64f, 4f, 0.80f, 0.60f });
        bool shortSpeechTriggersReview = (bool)melodicReviewMethod.Invoke(
            null, new object[] { true, 2.44f, 8.0f, 0.70f, 4f, 0.80f, 0.60f });
        if (!loggedMelodicSampleTriggersReview || shortSpeechTriggersReview)
            throw new InvalidOperationException(
                "Long stable melodic evidence no longer triggers role review, or short speech now does.");

        string notFound = failureKindMethod.Invoke(null, new object[] { "not_found" }).ToString();
        string transport = failureKindMethod.Invoke(null, new object[] { "transport" }).ToString();
        string duration = failureKindMethod.Invoke(null, new object[] { "duration_limit" }).ToString();
        if (notFound != "NotFound" || transport != "Transport" || duration != "DurationLimit")
            throw new InvalidOperationException(
                "Song-sing failures are being collapsed back into a generic not-found state.");
    }

    private static void RunSubtitleOverlayRegression(Type chatType)
    {
        if (!SubtitleOverlay.ShouldShowOriginal(SubtitleDisplayMode.OriginalOnly) ||
            !SubtitleOverlay.ShouldShowOriginal(SubtitleDisplayMode.Bilingual) ||
            SubtitleOverlay.ShouldShowOriginal(SubtitleDisplayMode.TranslationOnly) ||
            !SubtitleOverlay.ShouldShowTranslation(SubtitleDisplayMode.TranslationOnly) ||
            !SubtitleOverlay.ShouldShowTranslation(SubtitleDisplayMode.Bilingual) ||
            SubtitleOverlay.ShouldShowTranslation(SubtitleDisplayMode.OriginalOnly))
            throw new InvalidOperationException(
                "Subtitle display modes no longer keep original and translation channels independent.");

        if (SubtitleOverlay.ShouldShowNotice(
                SystemNoticeMode.ErrorsOnly, SystemNoticeSeverity.Warning) ||
            !SubtitleOverlay.ShouldShowNotice(
                SystemNoticeMode.ErrorsOnly, SystemNoticeSeverity.Error) ||
            SubtitleOverlay.ShouldShowNotice(
                SystemNoticeMode.Hidden, SystemNoticeSeverity.Error) ||
            !SubtitleOverlay.ShouldShowNotice(
                SystemNoticeMode.All, SystemNoticeSeverity.Info))
            throw new InvalidOperationException("System notice filtering regressed.");

        string prompt = SubtitleOverlay.BuildTranslationSystemPrompt(
            SubtitleLanguage.Japanese);
        if (!prompt.Contains("Japanese") || !prompt.Contains("Output only the translation") ||
            !prompt.Contains("Do not answer the text"))
            throw new InvalidOperationException(
                "The subtitle utility prompt no longer enforces translation-only output.");

        if (SubtitleOverlay.ComputeAuthorizedTranslationCharacters(10, 0, 24) != 0 ||
            SubtitleOverlay.ComputeAuthorizedTranslationCharacters(10, 5, 24) != 12 ||
            SubtitleOverlay.ComputeAuthorizedTranslationCharacters(10, 10, 24) != 24 ||
            typeof(SubtitleOverlay).GetMethod("ReportSourceCharacterRevealed") == null)
            throw new InvalidOperationException(
                "Translated subtitles no longer follow incremental spoken-source progress.");

        string clean = SubtitleOverlay.CleanTranslation(
            "```text\nTranslation: “おはよう”\n```");
        if (clean != "おはよう")
            throw new InvalidOperationException(
                "Subtitle translation cleanup no longer removes wrappers safely: " + clean);

        if (typeof(LLM).GetEvent("OnSystemNotice") == null ||
            typeof(LLM).GetMethod("PostUtilityMessage") == null ||
            typeof(ChatQW).GetProperty("SupportsUtilityMessages") == null)
            throw new MissingMemberException(
                "The non-character notice or stateless utility inference contract is missing.");

        FieldInfo overlayField = chatType.GetField(
            "m_SubtitleOverlay", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo noticeMethod = chatType.GetMethod(
            "PublishPendingNonCharacterToolNotice",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (overlayField == null || noticeMethod == null)
            throw new MissingMemberException(
                "ChatSample is no longer connected to the independent subtitle/notice layer.");

        GameObject providerObject = new GameObject("SubtitleRegressionProvider");
        try
        {
            ChatQW provider = providerObject.AddComponent<ChatQW>();
            provider.ActiveSkillContext = "CHARACTER_SKILL_SENTINEL";
            provider.TrailingContext = "CHARACTER_MEMORY_SENTINEL";
            provider.SkillCatalogContext = "CHARACTER_CATALOG_SENTINEL";
            MethodInfo jsonMethod = typeof(ChatQW).GetMethod(
                "BuildUtilityRequestJson", BindingFlags.Instance | BindingFlags.NonPublic);
            if (jsonMethod == null)
                throw new MissingMemberException(
                    "Stateless subtitle utility request builder was renamed.");
            string json = (string)jsonMethod.Invoke(
                provider, new object[] { "SYSTEM_SENTINEL", "INPUT_SENTINEL" });
            if (!json.Contains("SYSTEM_SENTINEL") || !json.Contains("INPUT_SENTINEL") ||
                json.Contains("CHARACTER_SKILL_SENTINEL") ||
                json.Contains("CHARACTER_MEMORY_SENTINEL") ||
                json.Contains("CHARACTER_CATALOG_SENTINEL"))
                throw new InvalidOperationException(
                    "Subtitle translation request unexpectedly inherited character context.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(providerObject);
        }
    }

    private static void RunEvidenceGroundingPromptRegression()
    {
        TextAsset behavior = AssetDatabase.LoadAssetAtPath<TextAsset>(
            "Assets/AIChatTookit/Prompts/behavior.txt");
        TextAsset singing = AssetDatabase.LoadAssetAtPath<TextAsset>(
            "Assets/AIChatTookit/Prompts/Skills/singing.txt");
        if (behavior == null ||
            !behavior.text.Contains("不确定性、自主判断与询问权（最高原则") ||
            !behavior.text.Contains("向用户询问始终必须保留为一个可选行动") ||
            !behavior.text.Contains("不等于每次都必须询问") ||
            !behavior.text.Contains("相信自己的主观判断") ||
            !behavior.text.Contains("前提本身有歧义时，本节优先") ||
            !behavior.text.Contains("事实与记忆的依据（上述最高原则") ||
            !behavior.text.Contains("自己先前没有依据地说过的话") ||
            !behavior.text.Contains("没有依据不等于事实不存在") ||
            !behavior.text.Contains("可以选择马上查工具，也可以暂时不查") ||
            !behavior.text.Contains("生成正文前，静默检查每一句"))
            throw new InvalidOperationException(
                "Behavior prompt is missing the general evidence-grounding principle.");

        if (singing == null ||
            !singing.text.Contains("不确定性、自主判断与询问权") ||
            !singing.text.Contains("你始终可以先询问") ||
            !singing.text.Contains("用户提过歌名") ||
            !singing.text.Contains("不能证明本机曲库里有它") ||
            !singing.text.Contains("不能凭聊天印象列歌名") ||
            !singing.text.Contains("承认刚才没有根据"))
            throw new InvalidOperationException(
                "Singing Skill is missing its domain-specific memory evidence boundary.");
    }

    private static void RunToolCorrectionRegression(Type chatType)
    {
        MethodInfo frameMethod = chatType.GetMethod(
            "BuildToolCorrectionFrame", BindingFlags.Static | BindingFlags.NonPublic);
        if (frameMethod == null)
            throw new MissingMemberException(
                "Tool correction feedback builder was renamed without updating the regression suite.");

        string frame = (string)frameMethod.Invoke(null, new object[]
        {
            "skill_request",
            "missing_required_attributes",
            "不存在 Skill ''; raw=<skill_request>",
            "singing",
            "真实用户轮不要重新申请；按已加载规则调用正确业务工具。",
        });
        if (!frame.Contains("status=rejected") ||
            !frame.Contains("code=missing_required_attributes") ||
            !frame.Contains("loaded_skill_rules=singing") ||
            !frame.Contains("本地程序") ||
            !frame.Contains("程序不替你决定") ||
            !frame.Contains("同轮必须提交可验证的工具请求") ||
            !frame.Contains("‹silent/› 或 ‹silence/›") ||
            frame.Contains("<skill_request>"))
            throw new InvalidOperationException(
                "Structured tool correction feedback lost facts or failed to neutralize tag-like text.");

        TextAsset behavior = AssetDatabase.LoadAssetAtPath<TextAsset>(
            "Assets/AIChatTookit/Prompts/behavior.txt");
        if (behavior == null || !behavior.text.Contains("工具纠错事实") ||
            !behavior.text.Contains("真实用户刚说完的回复中禁止"))
            throw new InvalidOperationException(
                "Behavior prompt is missing the user-turn tool correction protocol.");
    }

    private static void RunSongCatalogInspectionRegression(Type chatType)
    {
        MethodInfo extractMethod = chatType.GetMethod(
            "ExtractSongCatalogTag", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo summaryMethod = chatType.GetMethod(
            "BuildSongCatalogInspectionSummary", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo unknownTagMethod = chatType.GetMethod(
            "DetectUnknownAgentTag", BindingFlags.Static | BindingFlags.NonPublic);
        if (extractMethod == null || summaryMethod == null || unknownTagMethod == null)
            throw new MissingMemberException(
                "Song catalog inspection internals were renamed without updating the regression suite.");

        object[] extractArgs =
        {
            "我先查一下。<song_catalog query=\"One Last Kiss\" offset=\"-3\" " +
            "limit=\"500\" include_unnamed=\"true\" action=\"forget\" reason=\"核对曲库\"/>",
        };
        object request = extractMethod.Invoke(null, extractArgs);
        if (request == null || extractArgs[0].ToString().Contains("song_catalog"))
            throw new InvalidOperationException("song_catalog tag was not extracted cleanly.");
        Type requestType = request.GetType();
        if ((string)requestType.GetField("Query").GetValue(request) != "One Last Kiss" ||
            (int)requestType.GetField("Offset").GetValue(request) != 0 ||
            (int)requestType.GetField("Limit").GetValue(request) != 50 ||
            !(bool)requestType.GetField("IncludeUnnamed").GetValue(request))
            throw new InvalidOperationException("song_catalog attributes were parsed or clamped incorrectly.");
        if (requestType.GetField("Action") != null)
            throw new InvalidOperationException("The read-only song_catalog request unexpectedly exposes a mutation action.");

        string unknown = (string)unknownTagMethod.Invoke(
            null, new object[] { "<song_catalog query=\"Sprinter\" offset=\"0\"/>" });
        if (!string.IsNullOrEmpty(unknown))
            throw new InvalidOperationException("song_catalog is missing from the known tag registry.");

        var result = new SenseVoiceSpeechToText.SongCatalogInspectionResult
        {
            Ok = true,
            Query = "",
            IncludeUnnamed = false,
            TotalEntries = 71,
            NamedEntries = 23,
            UnnamedEntries = 48,
            UniqueExactTitleGroups = 19,
            MatchedEntries = 23,
            Offset = 0,
            Limit = 2,
            HasMore = true,
            NextOffset = 2,
            Entries = new[]
            {
                new SenseVoiceSpeechToText.SongCatalogEntry
                {
                    song_id = "f2afd759241e", title = "One Last Kiss", artist = "宇多田ヒカル",
                    display_name = "One Last Kiss", named = true,
                    reference_count = 6, unique_segment_count = 3, duplicate_variant_count = 3,
                    can_continue = true,
                },
                new SenseVoiceSpeechToText.SongCatalogEntry
                {
                    song_id = "af6348930f5a", title = "Sprinter", display_name = "Sprinter",
                    named = true, reference_count = 3, unique_segment_count = 2,
                },
            },
        };
        string summary = (string)summaryMethod.Invoke(null, new object[] { result });
        if (!summary.Contains("总条目 71") || !summary.Contains("未命名条目 48") ||
            !summary.Contains("One Last Kiss") || !summary.Contains("offset=2") ||
            !summary.Contains("不得声称") || summary.Contains("catalog_path") ||
            summary.Contains("audio_dir"))
            throw new InvalidOperationException("Song catalog summary lost counts, pagination, or privacy constraints.");

        TextAsset prompt = AssetDatabase.LoadAssetAtPath<TextAsset>(
            "Assets/AIChatTookit/Prompts/Skills/singing.txt");
        TextAsset behavior = AssetDatabase.LoadAssetAtPath<TextAsset>(
            "Assets/AIChatTookit/Prompts/behavior.txt");
        if (prompt == null || !prompt.text.Contains("<song_catalog") ||
            !prompt.text.Contains("只有、全部、一共") ||
            behavior == null || !behavior.text.Contains("<song_catalog"))
            throw new InvalidOperationException("Song catalog self-inspection protocol is missing from prompts.");
    }

    private static void RunSingingPolicyRegression(Type chatType)
    {
        MethodInfo extractMethod = chatType.GetMethod(
            "ExtractSingingPolicyTag", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo validateMethod = chatType.GetMethod(
            "ValidateSingingPolicyFields", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo stopMethod = chatType.GetMethod(
            "ExtractSingingStopTag", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo songSingMethod = chatType.GetMethod(
            "ExtractSongSingTag", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo unknownTagMethod = chatType.GetMethod(
            "DetectUnknownAgentTag", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo obsoleteAuthorization = chatType.GetMethod(
            "ValidateUserSingingActionAuthorization", BindingFlags.Static | BindingFlags.NonPublic);
        if (extractMethod == null || validateMethod == null || stopMethod == null ||
            songSingMethod == null ||
            unknownTagMethod == null)
            throw new MissingMemberException(
                "Singing policy internals were renamed without updating the regression suite.");
        if (obsoleteAuthorization != null)
            throw new InvalidOperationException(
                "Ordinary singing is still coupled to the obsolete duplicate intent authorization gate.");

        object[] extractArgs =
        {
            "好，我以后不主动唱。<singing_policy value=\"suppress_autonomy\"/>",
        };
        object policy = extractMethod.Invoke(null, extractArgs);
        if (policy == null || extractArgs[0].ToString().Contains("singing_policy"))
            throw new InvalidOperationException("singing_policy tag was not extracted cleanly.");
        FieldInfo value = policy.GetType().GetField("Value");
        FieldInfo evidence = policy.GetType().GetField("Evidence");
        if (value == null || evidence != null ||
            (string)value.GetValue(policy) != "suppress_autonomy")
            throw new InvalidOperationException("The simplified singing_policy was parsed incorrectly.");

        // 旧模型偶尔仍会附带 evidence；兼容读取时应忽略它，而不是让整次权限决定失败。
        object[] oldPolicyShape =
        {
            "<singing_policy value=\"disable\" evidence=\"语义概括也不再由程序核对\"/>",
        };
        object oldPolicy = extractMethod.Invoke(null, oldPolicyShape);
        if (oldPolicy == null ||
            (string)oldPolicy.GetType().GetField("Value").GetValue(oldPolicy) != "disable" ||
            oldPolicyShape[0].ToString().Contains("singing_policy"))
            throw new InvalidOperationException(
                "The previous singing_policy shape is no longer accepted as a safe migration input.");

        object[] reenable =
        {
            "enable", null,
        };
        if (!(bool)validateMethod.Invoke(null, reenable))
            throw new InvalidOperationException(
                "Explicit singing reauthorization was rejected: " + reenable[1]);

        // 语义是否成立由 LLM 决定；本地验证不再要求或比对逐字 evidence。
        object[] semanticSummaryNoLongerRejected =
        {
            "disable", null,
        };
        if (!(bool)validateMethod.Invoke(null, semanticSummaryNoLongerRejected))
            throw new InvalidOperationException(
                "A valid LLM policy decision is still coupled to literal user evidence.");

        object[] invalidValue =
        {
            "no_change", null,
        };
        if ((bool)validateMethod.Invoke(null, invalidValue))
            throw new InvalidOperationException(
                "The removed no_change action-approval state is still accepted as a policy change.");

        // 本次实测中的旧式坏标签必须被安静剥掉，不能再否决紧随其后的真实歌唱工具。
        object[] malformedLegacy =
        {
            "现在唱吧。<singing_intent intent=\"sing\" actor=\"安东诺瓦\" reason=\"想唱\"/>" +
            "<song_sing id=\"0956c4830577\" mode=\"memory\"/>",
        };
        object ignoredLegacy = extractMethod.Invoke(null, malformedLegacy);
        string legacyRemainder = malformedLegacy[0].ToString();
        if (ignoredLegacy != null || legacyRemainder.Contains("singing_intent") ||
            !legacyRemainder.Contains("song_sing"))
            throw new InvalidOperationException(
                "A malformed legacy singing_intent still blocks or removes the ordinary song tool.");

        object[] validLegacy =
        {
            "可以唱。<singing_intent permission=\"enable\" evidence=\"旧协议引文\"/>",
        };
        object migrated = extractMethod.Invoke(null, validLegacy);
        if (migrated == null ||
            (string)migrated.GetType().GetField("Value").GetValue(migrated) != "enable" ||
            !(bool)migrated.GetType().GetField("FromLegacyIntent").GetValue(migrated))
            throw new InvalidOperationException(
                "A valid legacy permission change was not migrated safely.");

        object[] stopArgs = { "停一下。<singing_stop reason=\"用户要停\"/>" };
        object stop = stopMethod.Invoke(null, stopArgs);
        if (stop == null || stopArgs[0].ToString().Contains("singing_stop") ||
            (string)stop.GetType().GetField("Reason").GetValue(stop) != "用户要停")
            throw new InvalidOperationException("singing_stop was not parsed cleanly.");

        object[] transitionArgs =
        {
            "换成这一首。<singing_stop reason=\"停止旧歌\"/>" +
            "<song_sing id=\"0956c4830577\" mode=\"memory\" reason=\"演唱新歌\"/>",
        };
        object replacementSong = songSingMethod.Invoke(null, transitionArgs);
        object transitionStop = stopMethod.Invoke(null, transitionArgs);
        string transitionRemainder = transitionArgs[0].ToString();
        if (replacementSong == null || transitionStop == null ||
            transitionRemainder.Contains("singing_stop") ||
            transitionRemainder.Contains("song_sing"))
            throw new InvalidOperationException(
                "A singing_stop + replacement action response no longer retains both operations in sequence.");

        foreach (string tag in new[]
        {
            "<singing_policy value=\"enable\"/>",
            "<singing_stop reason=\"停一下\"/>",
            "<singing_intent intent=\"sing\"/>",
        })
        {
            string unknown = (string)unknownTagMethod.Invoke(null, new object[] { tag });
            if (!string.IsNullOrEmpty(unknown))
                throw new InvalidOperationException(
                    "A supported or migration singing tag is missing from the known tag registry: " + tag);
        }

        TextAsset prompt = AssetDatabase.LoadAssetAtPath<TextAsset>(
            "Assets/AIChatTookit/Prompts/Skills/singing.txt");
        TextAsset behavior = AssetDatabase.LoadAssetAtPath<TextAsset>(
            "Assets/AIChatTookit/Prompts/behavior.txt");
        if (prompt == null || behavior == null ||
            !prompt.text.Contains("<singing_policy value=\"enable|disable|suppress_autonomy\"") ||
            !prompt.text.Contains("普通点歌、回唱和练唱直接") ||
            !prompt.text.Contains("程序不会再用关键词或逐字引文重新裁决语义") ||
            !prompt.text.Contains("<singing_stop") ||
            !prompt.text.Contains("先停旧动作再保留新动作") ||
            prompt.text.Contains("<singing_policy value=\"enable|disable|suppress_autonomy\" evidence") ||
            behavior.text.Contains("<singing_policy value=\"enable|disable|suppress_autonomy\" evidence") ||
            prompt.text.Contains("confidence=\"0.00~1.00\"") ||
            behavior.text.Contains("<singing_intent/>"))
            throw new InvalidOperationException(
                "Prompts still expose the obsolete seven-field singing action approval protocol.");
    }

    private static void RunSingingToolRoutingRegression(Type chatType)
    {
        MethodInfo practiceIntent = chatType.GetMethod(
            "IsSongSingPracticeIntent", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo titleCompatibility = chatType.GetMethod(
            "AreSongTitlesCompatible", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo resolveExpectedTitle = chatType.GetMethod(
            "ResolveExpectedSongTitle", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo upcomingUserSinging = chatType.GetMethod(
            "AnnouncesUpcomingUserSinging", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo recentPerformanceReference = chatType.GetMethod(
            "IsRecentPerformanceMaterialReference", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo explicitSongSelector = chatType.GetMethod(
            "UserExplicitlySuppliedSongSelector", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo extractSongSing = chatType.GetMethod(
            "ExtractSongSingTag", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo buildSongSingPracticeReroute = chatType.GetMethod(
            "BuildSongSingPracticeReroute", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo requiresPracticeOrder = chatType.GetMethod(
            "RequiresExplicitPracticeOrder", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo buildMissingPracticeOrderFailure = chatType.GetMethod(
            "BuildMissingPracticeOrderFailure", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo validatePitch = chatType.GetMethod(
            "ValidateHumBackPitchAttributes", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo parseMidiNote = chatType.GetMethod(
            "TryParseMidiNote", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo parseInterval = chatType.GetMethod(
            "TryParseMusicalInterval", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo parsePitchPlanMatch = chatType.GetMethod(
            "TryParsePitchPlanMatchToken", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo sourcePitchToken = chatType.GetMethod(
            "IsSourcePitchToken", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo mergeArrangement = chatType.GetMethod(
            "ShouldMergePracticeArrangement", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo quantizeCalibratedShift = chatType.GetMethod(
            "QuantizeCalibratedPitchShift", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo resolveCarriedShift = chatType.GetMethod(
            "ResolveCarriedPracticeShift", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo shiftTimeline = chatType.GetMethod(
            "ShiftPitchTimeline", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo parseHumBackAttributes = chatType.GetMethod(
            "ParseHumBackCompatibleAttributes",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo normalizeSingingSource = chatType.GetMethod(
            "NormalizeSingingMaterialSource",
            BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo preserveMixedSingingIsland = typeof(SenseVoiceSpeechToText).GetMethod(
            "PreserveLastPlayableSingingIslandFromMixedTurn",
            BindingFlags.Instance | BindingFlags.Public);
        Type songSingRequestType = chatType.GetNestedType(
            "AgentSongSingRequest", BindingFlags.NonPublic);
        Type humBackRequestType = chatType.GetNestedType(
            "AgentHumBackRequest", BindingFlags.NonPublic);
        if (practiceIntent == null || titleCompatibility == null || resolveExpectedTitle == null ||
            upcomingUserSinging == null || recentPerformanceReference == null ||
            explicitSongSelector == null || songSingRequestType == null ||
            extractSongSing == null || buildSongSingPracticeReroute == null ||
            requiresPracticeOrder == null || buildMissingPracticeOrderFailure == null ||
            validatePitch == null || parseMidiNote == null || parseInterval == null ||
            parsePitchPlanMatch == null || sourcePitchToken == null ||
            mergeArrangement == null || quantizeCalibratedShift == null ||
            resolveCarriedShift == null ||
            shiftTimeline == null || parseHumBackAttributes == null ||
            normalizeSingingSource == null || preserveMixedSingingIsland == null ||
            humBackRequestType == null)
            throw new MissingMemberException(
                "Singing tool routing internals were renamed without updating the regression suite.");

        object expandedHumBack = parseHumBackAttributes.Invoke(
            null, new object[] {
                " mode=\"practice\" order=\"2\" capture=\"expanded\"" +
                " trim_head_seconds=\"1.80\" trim_tail_seconds=\"0.25\"" +
                " exclude_speech=\"true\""
            });
        FieldInfo captureField = humBackRequestType.GetField("Capture");
        FieldInfo trimHeadField = humBackRequestType.GetField("TrimHeadSeconds");
        FieldInfo trimTailField = humBackRequestType.GetField("TrimTailSeconds");
        FieldInfo excludeSpeechField = humBackRequestType.GetField("ExcludeSpeech");
        if (expandedHumBack == null || captureField == null ||
            trimHeadField == null || trimTailField == null || excludeSpeechField == null ||
            (string)captureField.GetValue(expandedHumBack) != "expanded" ||
            Mathf.Abs((float)trimHeadField.GetValue(expandedHumBack) - 1.80f) > 0.001f ||
            Mathf.Abs((float)trimTailField.GetValue(expandedHumBack) - 0.25f) > 0.001f ||
            !(bool)excludeSpeechField.GetValue(expandedHumBack))
            throw new InvalidOperationException(
                "hum_back boundary selection is no longer preserved by the parser.");

        object recentHumBack = parseHumBackAttributes.Invoke(
            null, new object[] { " source=\"recent_turn\" mode=\"echo\"" });
        object mismatchedHumBack = parseHumBackAttributes.Invoke(
            null, new object[] { " source=\"library\" mode=\"echo\"" });
        FieldInfo humSourceField = humBackRequestType.GetField("Source");
        FieldInfo humSourceErrorField = humBackRequestType.GetField("SourceValidationError");
        string normalizedCatalogSource = (string)normalizeSingingSource.Invoke(
            null, new object[] { "catalog" });
        if (recentHumBack == null || mismatchedHumBack == null ||
            humSourceField == null || humSourceErrorField == null ||
            (string)humSourceField.GetValue(recentHumBack) != "recent_turn" ||
            !string.IsNullOrEmpty((string)humSourceErrorField.GetValue(recentHumBack)) ||
            string.IsNullOrWhiteSpace(
                (string)humSourceErrorField.GetValue(mismatchedHumBack)) ||
            normalizedCatalogSource != "library")
            throw new InvalidOperationException(
                "Structured singing material sources are no longer parsed or validated consistently.");

        bool userPractice = (bool)practiceIntent.Invoke(null, new object[]
        {
            "把我刚刚录的那三段 One Last Kiss 连着唱一遍", "", ""
        });
        bool reasonPractice = (bool)practiceIntent.Invoke(null, new object[]
        {
            "再试一次", "重新尝试演唱《One Last Kiss》的三段练习内容。", ""
        });
        bool orderPractice = (bool)practiceIntent.Invoke(null, new object[]
        {
            "再唱一次", "", "1,2,3"
        });
        bool catalogRequest = (bool)practiceIntent.Invoke(null, new object[]
        {
            "请唱《One Last Kiss》", "从长期曲库演唱", ""
        });
        if (!userPractice || !reasonPractice || !orderPractice || catalogRequest)
            throw new InvalidOperationException("song_sing → hum_back practice routing is incorrect.");

        bool missingMultiOrder = (bool)requiresPracticeOrder.Invoke(
            null, new object[] { "practice", "", 2 });
        bool explicitMultiOrder = (bool)requiresPracticeOrder.Invoke(
            null, new object[] { "practice", "1,2", 2 });
        bool singlePhraseCanDefault = (bool)requiresPracticeOrder.Invoke(
            null, new object[] { "practice", "", 1 });
        bool echoHasNoPracticeOrder = (bool)requiresPracticeOrder.Invoke(
            null, new object[] { "echo", "", 3 });
        string missingOrderFact = (string)buildMissingPracticeOrderFailure.Invoke(
            null, new object[] { 2 });
        if (!missingMultiOrder || explicitMultiOrder || singlePhraseCanDefault ||
            echoHasNoPracticeOrder || string.IsNullOrWhiteSpace(missingOrderFact) ||
            !missingOrderFact.Contains("有 2 段") ||
            !missingOrderFact.Contains("没有显式 order") ||
            !missingOrderFact.Contains("拿不准就先询问"))
            throw new InvalidOperationException(
                "A multi-segment practice request can again lose order without a factual correction path.");

        object[] pitchPlanTag =
        {
            "<song_sing mode=\"memory\" source=\"library\" order=\"1,2,3\" " +
            "pitch_plan=\"C4,C4,C4\" pace=\"1.05\" expression=\"0.7\"/>",
        };
        object parsedPitchPlanSongSing = extractSongSing.Invoke(null, pitchPlanTag);
        object reroutedPitchPlan = buildSongSingPracticeReroute.Invoke(
            null, new[] { parsedPitchPlanSongSing, (object)"practice", "1,2,3" });
        FieldInfo reroutedPitchPlanField = humBackRequestType.GetField("PitchPlan");
        FieldInfo reroutedPaceField = humBackRequestType.GetField("Pace");
        FieldInfo reroutedExpressionField = humBackRequestType.GetField("Expression");
        FieldInfo reroutedValidationField = humBackRequestType.GetField("ValidationError");
        if (parsedPitchPlanSongSing == null || reroutedPitchPlan == null ||
            reroutedPitchPlanField == null || reroutedPaceField == null ||
            reroutedExpressionField == null || reroutedValidationField == null ||
            (string)reroutedPitchPlanField.GetValue(reroutedPitchPlan) != "C4,C4,C4" ||
            Math.Abs((float)reroutedPaceField.GetValue(reroutedPitchPlan) - 1.05f) > 0.001f ||
            Math.Abs((float)reroutedExpressionField.GetValue(reroutedPitchPlan) - 0.7f) > 0.001f ||
            !string.IsNullOrEmpty((string)reroutedValidationField.GetValue(reroutedPitchPlan)) ||
            !string.IsNullOrEmpty((string)humBackRequestType.GetField("SourceValidationError").GetValue(reroutedPitchPlan)))
            throw new InvalidOperationException(
                "song_sing → hum_back rerouting silently dropped compatible performance parameters.");

        object[] invalidTransposeTag =
        {
            "<song_sing mode=\"practice\" order=\"1,2,3\" transpose=\"+6st\"/>",
        };
        object parsedInvalidTranspose = extractSongSing.Invoke(null, invalidTransposeTag);
        object reroutedInvalidTranspose = buildSongSingPracticeReroute.Invoke(
            null, new[] { parsedInvalidTranspose, (object)"practice", "1,2,3" });
        string preservedValidation = reroutedInvalidTranspose == null
            ? ""
            : (string)reroutedValidationField.GetValue(reroutedInvalidTranspose);
        if (string.IsNullOrWhiteSpace(preservedValidation) ||
            !preservedValidation.Contains("有歧义"))
            throw new InvalidOperationException(
                "Invalid pitch data on a rerouted song_sing request is still being lost silently.");

        bool announcedByUser = (bool)upcomingUserSinging.Invoke(
            null, new object[] { "好，那我再唱一遍。" });
        bool characterWasAsked = (bool)upcomingUserSinging.Invoke(
            null, new object[] { "接下来你再唱一遍。" });
        bool currentPhrase = (bool)recentPerformanceReference.Invoke(
            null, new object[] { "把我刚才唱的这一段唱出来。" });
        bool broadSongReference = (bool)recentPerformanceReference.Invoke(
            null, new object[] { "把最近那首歌完整唱出来。" });
        if (!announcedByUser || characterWasAsked || !currentPhrase || broadSongReference)
            throw new InvalidOperationException(
                "Upcoming-user-singing or current-material source hints became over-broad.");

        object explicitRequest = Activator.CreateInstance(songSingRequestType, true);
        songSingRequestType.GetField("Title").SetValue(explicitRequest, "One Last Kiss");
        bool selectorCameFromUser = (bool)explicitSongSelector.Invoke(null, new[]
        {
            (object)"把刚才提到的 One Last Kiss 唱一遍。", explicitRequest
        });
        bool selectorInventedByModel = (bool)explicitSongSelector.Invoke(null, new[]
        {
            (object)"把我刚才唱的这一段唱出来。", explicitRequest
        });
        bool compactEnglishSelector = (bool)explicitSongSelector.Invoke(null, new[]
        {
            (object)"这歌是onelast kiss。", explicitRequest
        });
        if (!selectorCameFromUser || !compactEnglishSelector || selectorInventedByModel)
            throw new InvalidOperationException(
                "A model-invented catalog title is being treated as if the user selected it.");

        object[] recentSourceSongTag =
        {
            "<song_sing source=\"recent_turn\" id=\"f2afd759241e\" mode=\"memory\"/>",
        };
        object recentSourceSong = extractSongSing.Invoke(null, recentSourceSongTag);
        FieldInfo songSourceField = songSingRequestType.GetField("Source");
        FieldInfo songSourceErrorField = songSingRequestType.GetField("SourceValidationError");
        if (recentSourceSong == null || songSourceField == null ||
            songSourceErrorField == null ||
            (string)songSourceField.GetValue(recentSourceSong) != "recent_turn" ||
            !string.IsNullOrEmpty(
                (string)songSourceErrorField.GetValue(recentSourceSong)))
            throw new InvalidOperationException(
                "song_sing no longer preserves a structured recent-turn source for safe rerouting.");

        bool sameEnglishTitle = (bool)titleCompatibility.Invoke(
            null, new object[] { "One Last Kiss", "ONE LAST KISS" });
        bool displaySuffix = (bool)titleCompatibility.Invoke(
            null, new object[] { "One Last Kiss", "One Last Kiss（宇多田光）" });
        bool wrongSong = (bool)titleCompatibility.Invoke(
            null, new object[] { "One Last Kiss", "忘记时间" });
        if (!sameEnglishTitle || !displaySuffix || wrongSong)
            throw new InvalidOperationException("Resolved song title identity checks are incorrect.");

        string expectedFromReason = (string)resolveExpectedTitle.Invoke(null, new object[]
        {
            "", "再试一次", "重新尝试演唱《One Last Kiss》的三段练习内容。"
        });
        if (expectedFromReason != "One Last Kiss")
            throw new InvalidOperationException("Explicit title was lost before resolved identity validation.");

        object[] d4Args = { "D4", 0f };
        object[] eb4Args = { "Eb4", 0f };
        object[] invalidNoteArgs = { "62", 0f };
        if (!(bool)parseMidiNote.Invoke(null, d4Args) ||
            Math.Abs((float)d4Args[1] - 62f) > 0.01f ||
            !(bool)parseMidiNote.Invoke(null, eb4Args) ||
            Math.Abs((float)eb4Args[1] - 63f) > 0.01f ||
            (bool)parseMidiNote.Invoke(null, invalidNoteArgs))
            throw new InvalidOperationException(
                "Absolute hum-back target-note parsing no longer follows MIDI note semantics.");

        object[] majorSecondArgs = { "+M2", 0f };
        object[] minorSecondDownArgs = { "-m2", 0f };
        object[] semitoneArgs = { "+3st", 0f };
        object[] oneSemitoneArgs = { "+1st", 0f };
        object[] namedHalfStepArgs = { "+half_step", 0f };
        object[] namedWholeStepDownArgs = { "-whole_step", 0f };
        if (!(bool)parseInterval.Invoke(null, majorSecondArgs) ||
            Math.Abs((float)majorSecondArgs[1] - 2f) > 0.01f ||
            !(bool)parseInterval.Invoke(null, minorSecondDownArgs) ||
            Math.Abs((float)minorSecondDownArgs[1] + 1f) > 0.01f ||
            !(bool)parseInterval.Invoke(null, semitoneArgs) ||
            Math.Abs((float)semitoneArgs[1] - 3f) > 0.01f ||
            !(bool)parseInterval.Invoke(null, oneSemitoneArgs) ||
            Math.Abs((float)oneSemitoneArgs[1] - 1f) > 0.01f ||
            !(bool)parseInterval.Invoke(null, namedHalfStepArgs) ||
            Math.Abs((float)namedHalfStepArgs[1] - 1f) > 0.01f ||
            !(bool)parseInterval.Invoke(null, namedWholeStepDownArgs) ||
            Math.Abs((float)namedWholeStepDownArgs[1] + 2f) > 0.01f)
            throw new InvalidOperationException(
                "Musical interval notation is no longer converted to semitones correctly.");
        object[] matchTokenArgs = { "match:4", 0 };
        object[] sameAsTokenArgs = { "same_as:2", 0 };
        object[] invalidMatchTokenArgs = { "match:last", 0 };
        if (!(bool)parsePitchPlanMatch.Invoke(null, matchTokenArgs) ||
            (int)matchTokenArgs[1] != 4 ||
            !(bool)parsePitchPlanMatch.Invoke(null, sameAsTokenArgs) ||
            (int)sameAsTokenArgs[1] != 2 ||
            (bool)parsePitchPlanMatch.Invoke(null, invalidMatchTokenArgs))
            throw new InvalidOperationException(
                "Canonical pitch_plan match references are not parsed deterministically.");
        if (!(bool)sourcePitchToken.Invoke(null, new object[] { "source" }) ||
            !(bool)sourcePitchToken.Invoke(null, new object[] { "用户原唱" }) ||
            (bool)sourcePitchToken.Invoke(null, new object[] { "keep" }))
            throw new InvalidOperationException(
                "Canonical source/original pitch-plan token is not recognized deterministically.");
        bool partialPlanMerges = (bool)mergeArrangement.Invoke(
            null, new object[] { new[] { 11, 12, 13 }, new[] { 13 } });
        bool samePlanMerges = (bool)mergeArrangement.Invoke(
            null, new object[] { new[] { 11, 12, 13 }, new[] { 11, 12, 13 } });
        bool newPlanReplaces = (bool)mergeArrangement.Invoke(
            null, new object[] { new[] { 11, 12, 13 }, new[] { 21 } });
        if (!partialPlanMerges || !samePlanMerges || !newPlanReplaces)
            throw new InvalidOperationException(
                "Selecting a new clip must preserve earlier clips' pitch states.");
        int calibratedShift = (int)quantizeCalibratedShift.Invoke(
            null, new object[] { 55f, 55f, -0.69f });
        if (calibratedShift != 1)
            throw new InvalidOperationException(
                "Reliable renderer bias is no longer compensated in the next integer pitch command.");
        int carriedCalibratedShift = (int)resolveCarriedShift.Invoke(
            null, new object[] { 52.54f, 60.54f, 8, true, -0.77f });
        int carriedUncalibratedShift = (int)resolveCarriedShift.Invoke(
            null, new object[] { 52.54f, 60.54f, 8, false, -0.77f });
        if (carriedCalibratedShift != 9 || carriedUncalibratedShift != 8)
            throw new InvalidOperationException(
                "Implicit current-arrangement replay no longer applies reliable renderer bias.");
        float[] shifted = shiftTimeline.Invoke(
            null, new object[] { new[] { 60f, 0f, 62f }, 2 }) as float[];
        if (shifted == null || shifted.Length != 3 || shifted[0] != 62f ||
            shifted[1] != 0f || shifted[2] != 64f)
            throw new InvalidOperationException(
                "Absolute musical targets no longer transpose the independent-SVS score path.");

        FieldInfo keyField = humBackRequestType.GetField("Key");
        FieldInfo keyListField = humBackRequestType.GetField("KeyPerSegment");
        FieldInfo targetField = humBackRequestType.GetField("TargetNote");
        FieldInfo alignField = humBackRequestType.GetField("AlignToPracticeIndex");
        FieldInfo pitchTargetField = humBackRequestType.GetField("PitchTarget");
        FieldInfo pitchDeltaField = humBackRequestType.GetField("PitchDelta");
        FieldInfo pitchMatchField = humBackRequestType.GetField("PitchMatch");
        FieldInfo pitchPlanField = humBackRequestType.GetField("PitchPlan");
        FieldInfo transposeField = humBackRequestType.GetField("Transpose");
        FieldInfo orderField = humBackRequestType.GetField("Order");
        if (keyField == null || keyListField == null || targetField == null || alignField == null ||
            pitchTargetField == null || pitchDeltaField == null || pitchMatchField == null ||
            pitchPlanField == null || transposeField == null || orderField == null)
            throw new MissingMemberException("Hum-back pitch protocol fields are missing.");

        object scalarMidiAsKey = Activator.CreateInstance(humBackRequestType, true);
        keyField.SetValue(scalarMidiAsKey, 62f);
        string scalarError = (string)validatePitch.Invoke(
            null, new[] { (object)"62", "", scalarMidiAsKey });
        if (string.IsNullOrWhiteSpace(scalarError) || !scalarError.Contains("pitch_plan"))
            throw new InvalidOperationException(
                "A MIDI note written as relative key is still being silently clamped or accepted.");

        object outOfRangeList = Activator.CreateInstance(humBackRequestType, true);
        keyListField.SetValue(outOfRangeList, new[] { 0f, 13f });
        string listError = (string)validatePitch.Invoke(
            null, new[] { (object)"0,13", "", outOfRangeList });
        if (string.IsNullOrWhiteSpace(listError) || !listError.Contains("−12～+12"))
            throw new InvalidOperationException("Out-of-range per-segment key is no longer rejected.");

        object targetRequest = Activator.CreateInstance(humBackRequestType, true);
        targetField.SetValue(targetRequest, "D4");
        string targetError = (string)validatePitch.Invoke(
            null, new[] { (object)"", "", targetRequest });
        if (!string.IsNullOrEmpty(targetError))
            throw new InvalidOperationException("A valid D4 target_note is being rejected.");

        object alignRequest = Activator.CreateInstance(humBackRequestType, true);
        string alignError = (string)validatePitch.Invoke(
            null, new[] { (object)"", "3", alignRequest });
        if (!string.IsNullOrEmpty(alignError) || (int)alignField.GetValue(alignRequest) != 3)
            throw new InvalidOperationException("A valid align_to practice index is not retained.");

        object musicalTargetRequest = Activator.CreateInstance(humBackRequestType, true);
        pitchTargetField.SetValue(musicalTargetRequest, "keep,keep,D#4");
        string musicalTargetError = (string)validatePitch.Invoke(
            null, new[] { (object)"", "", musicalTargetRequest });
        if (!string.IsNullOrEmpty(musicalTargetError))
            throw new InvalidOperationException(
                "A valid per-segment pitch_target list is being rejected.");

        object incompleteTargetRequest = Activator.CreateInstance(humBackRequestType, true);
        pitchTargetField.SetValue(incompleteTargetRequest, "D#");
        string incompleteTargetError = (string)validatePitch.Invoke(
            null, new[] { (object)"", "", incompleteTargetRequest });
        if (string.IsNullOrEmpty(incompleteTargetError) ||
            !incompleteTargetError.Contains("完整音名"))
            throw new InvalidOperationException(
                "A pitch class without octave is still being accepted as an absolute target.");

        object musicalDeltaRequest = Activator.CreateInstance(humBackRequestType, true);
        pitchDeltaField.SetValue(musicalDeltaRequest, "keep,keep,+M2");
        string musicalDeltaError = (string)validatePitch.Invoke(
            null, new[] { (object)"", "", musicalDeltaRequest });
        if (!string.IsNullOrEmpty(musicalDeltaError))
            throw new InvalidOperationException(
                "A valid per-segment musical interval list is being rejected.");

        object canonicalPlanRequest = Activator.CreateInstance(humBackRequestType, true);
        pitchPlanField.SetValue(canonicalPlanRequest, "keep,source,match:1");
        string canonicalPlanError = (string)validatePitch.Invoke(
            null, new[] { (object)"", "", canonicalPlanRequest });
        if (!string.IsNullOrEmpty(canonicalPlanError))
            throw new InvalidOperationException(
                "A valid mixed pitch_plan is being rejected.");

        object ambiguousMatchScopeRequest = Activator.CreateInstance(humBackRequestType, true);
        orderField.SetValue(ambiguousMatchScopeRequest, "1,2,3");
        pitchPlanField.SetValue(ambiguousMatchScopeRequest, "match:1");
        string ambiguousMatchScopeError = (string)validatePitch.Invoke(
            null, new[] { (object)"", "", ambiguousMatchScopeRequest });
        if (string.IsNullOrEmpty(ambiguousMatchScopeError) ||
            !ambiguousMatchScopeError.Contains("作用域有歧义"))
            throw new InvalidOperationException(
                "A scalar match target can again silently retune every segment in a multi-segment order.");

        object transposeRequest = Activator.CreateInstance(humBackRequestType, true);
        transposeField.SetValue(transposeRequest, "+half_step");
        string transposeError = (string)validatePitch.Invoke(
            null, new[] { (object)"", "", transposeRequest });
        if (!string.IsNullOrEmpty(transposeError))
            throw new InvalidOperationException(
                "A valid canonical transpose interval is being rejected.");

        object ambiguousPlanRequest = Activator.CreateInstance(humBackRequestType, true);
        pitchPlanField.SetValue(ambiguousPlanRequest, "keep,keep,+1st");
        string ambiguousPlanError = (string)validatePitch.Invoke(
            null, new[] { (object)"", "", ambiguousPlanRequest });
        object ambiguousTransposeRequest = Activator.CreateInstance(humBackRequestType, true);
        transposeField.SetValue(ambiguousTransposeRequest, "-2st");
        string ambiguousTransposeError = (string)validatePitch.Invoke(
            null, new[] { (object)"", "", ambiguousTransposeRequest });
        if (string.IsNullOrEmpty(ambiguousPlanError) ||
            !ambiguousPlanError.Contains("有歧义") ||
            string.IsNullOrEmpty(ambiguousTransposeError) ||
            !ambiguousTransposeError.Contains("有歧义"))
            throw new InvalidOperationException(
                "The canonical pitch protocol still accepts ambiguous +Nst notation.");

        object conflictingMusicalRequest = Activator.CreateInstance(humBackRequestType, true);
        pitchTargetField.SetValue(conflictingMusicalRequest, "D4");
        pitchMatchField.SetValue(conflictingMusicalRequest, "1");
        string conflictError = (string)validatePitch.Invoke(
            null, new[] { (object)"", "", conflictingMusicalRequest });
        if (string.IsNullOrEmpty(conflictError) || !conflictError.Contains("一次只能选择一种"))
            throw new InvalidOperationException(
                "Conflicting musical pitch goals are no longer rejected explicitly.");

        object conflictingCanonicalRequest = Activator.CreateInstance(humBackRequestType, true);
        pitchPlanField.SetValue(conflictingCanonicalRequest, "keep,+M2");
        pitchTargetField.SetValue(conflictingCanonicalRequest, "D4");
        string canonicalConflictError = (string)validatePitch.Invoke(
            null, new[] { (object)"", "", conflictingCanonicalRequest });
        if (string.IsNullOrEmpty(canonicalConflictError) ||
            !canonicalConflictError.Contains("一次只能选择一种"))
            throw new InvalidOperationException(
                "Canonical and legacy pitch goals can conflict silently.");

        TextAsset singingPrompt = AssetDatabase.LoadAssetAtPath<TextAsset>(
            "Assets/AIChatTookit/Prompts/Skills/singing.txt");
        if (singingPrompt == null ||
            !singingPrompt.text.Contains("pitch_plan=\"keep,keep,+half_step\"") ||
            !singingPrompt.text.Contains("pitch_plan=\"keep,keep,+whole_step\"") ||
            !singingPrompt.text.Contains("pitch_plan=\"A3,Bb3,A3\"") ||
            !singingPrompt.text.Contains("pitch_plan=\"source\"") ||
            !singingPrompt.text.Contains("transpose=\"+half_step\"") ||
            !singingPrompt.text.Contains("不写 +Nst") ||
            !singingPrompt.text.Contains("局部重唱只更新对应段") ||
            !singingPrompt.text.Contains("片段的中心音高不是歌曲调性") ||
            !singingPrompt.text.Contains("ready") ||
            !singingPrompt.text.Contains("不要再组合 source、mode、order") ||
            !singingPrompt.text.Contains("不原样重复同一无效请求"))
            throw new InvalidOperationException(
                "Singing Skill no longer explains action consistency, current-material routing, or pitch rejection semantics.");

        if (chatType.GetMethod("AcquireSkillExecutionLease",
                BindingFlags.Instance | BindingFlags.NonPublic) == null ||
            chatType.GetMethod("ReleaseSkillExecutionLease",
                BindingFlags.Instance | BindingFlags.NonPublic) == null ||
            chatType.GetMethod("PrewarmHumSVCForSkill",
                BindingFlags.Instance | BindingFlags.NonPublic) == null)
            throw new MissingMemberException(
                "Singing action lease or nonblocking SVC prewarm integration is missing.");

        if (chatType.GetField("m_UserTurnSkillPins",
                BindingFlags.Instance | BindingFlags.NonPublic) == null)
            throw new MissingMemberException(
                "The real-user-turn Skill lifecycle pin is missing.");

        if (typeof(SenseVoiceSpeechToText).GetMethod(
                "AnalyzeRenderedSingingPitch",
                BindingFlags.Instance | BindingFlags.Public) == null ||
            chatType.GetMethod(
                "RequestRenderedPitchMeasurement",
                BindingFlags.Instance | BindingFlags.NonPublic) == null)
            throw new MissingMemberException(
                "Side-effect-free rendered singing pitch verification is missing.");
    }

    private static void RunAutonomyIntentRegression(Type chatType, object singing)
    {
        MethodInfo bypassMethod = chatType.GetMethod(
            "ShouldBypassAutonomyIntentProbe", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo parseMethod = chatType.GetMethod(
            "TryParseAutonomyIntentDecision", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo resolveWakeMethod = chatType.GetMethod(
            "ResolveScheduledWakeReason", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo contextualMethod = chatType.GetMethod(
            "ClassifyContextualSkillUserIntent", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo resolveReferenceMethod = chatType.GetMethod(
            "ResolveRecentSkillReference", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo extractControlMethod = chatType.GetMethod(
            "ExtractSkillControlTag", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo unknownTagMethod = chatType.GetMethod(
            "DetectUnknownAgentTag", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo removeUnknownTagMethod = chatType.GetMethod(
            "RemoveUnknownAgentTags", BindingFlags.Static | BindingFlags.NonPublic);
        if (bypassMethod == null || parseMethod == null || resolveWakeMethod == null ||
            contextualMethod == null || resolveReferenceMethod == null ||
            extractControlMethod == null || unknownTagMethod == null ||
            removeUnknownTagMethod == null)
            throw new MissingMemberException(
                "Autonomy intent internals were renamed without updating the regression suite.");

        bool scheduledBypasses = (bool)bypassMethod.Invoke(null, new object[] { "scheduled" });
        bool toolBypasses = (bool)bypassMethod.Invoke(null, new object[] { "song-search-result" });
        bool songActionBypasses = (bool)bypassMethod.Invoke(null, new object[] { "song-action-result" });
        bool skillBypasses = (bool)bypassMethod.Invoke(null, new object[] { "skill-request:singing" });
        if (scheduledBypasses || songActionBypasses || !toolBypasses || !skillBypasses)
            throw new InvalidOperationException("Autonomy probe bypass routing is incorrect.");

        string preservedToolReason = (string)resolveWakeMethod.Invoke(
            null, new object[] { "song-search-result", "clock" });
        string ordinaryClockReason = (string)resolveWakeMethod.Invoke(
            null, new object[] { "llm-requested", "clock" });
        string surfacedMemoryReason = (string)resolveWakeMethod.Invoke(
            null, new object[] { "default", "memory" });
        if (preservedToolReason != "song-search-result" ||
            ordinaryClockReason != "scheduled" ||
            surfacedMemoryReason != "memory-surfaced")
            throw new InvalidOperationException("Scheduled wake reasons are not preserved correctly.");

        string contextualDisable = contextualMethod.Invoke(
            null, new[] { (object)"那现在关掉吧。", singing }).ToString();
        string contextualStatus = contextualMethod.Invoke(
            null, new[] { (object)"那现在关掉吗？", singing }).ToString();
        if (contextualDisable != "Mention" || contextualStatus != "Mention")
            throw new InvalidOperationException(
                "Contextual Skill routing must load semantics without deciding permission direction.");

        string freshReference = (string)resolveReferenceMethod.Invoke(
            null, new object[] { "singing", 10f, 20f, 180f });
        string expiredReference = (string)resolveReferenceMethod.Invoke(
            null, new object[] { "singing", 10f, 400f, 180f });
        if (freshReference != "singing" || expiredReference != "")
            throw new InvalidOperationException("Recent Skill reference expiry is incorrect.");

        object[] controlArgs =
        {
            "好，我现在关掉它。<skill_control name=\"singing\" action=\"disable\" reason=\"用户含蓄请求\"/>",
        };
        object control = extractControlMethod.Invoke(null, controlArgs);
        if (control == null || controlArgs[0].ToString().Contains("skill_control"))
            throw new InvalidOperationException("skill_control tag was not extracted cleanly.");
        FieldInfo controlName = control.GetType().GetField(
            "Name", BindingFlags.Instance | BindingFlags.Public);
        FieldInfo controlAction = control.GetType().GetField(
            "Action", BindingFlags.Instance | BindingFlags.Public);
        if (controlName == null || controlAction == null ||
            (string)controlName.GetValue(control) != "singing" ||
            (string)controlAction.GetValue(control) != "disable")
            throw new InvalidOperationException("skill_control attributes were parsed incorrectly.");
        string unknown = (string)unknownTagMethod.Invoke(
            null, new object[] { "<skill_control name=\"singing\" action=\"disable\"/>" });
        if (!string.IsNullOrEmpty(unknown))
            throw new InvalidOperationException("skill_control is missing from the known tag registry.");

        string isolated = (string)removeUnknownTagMethod.Invoke(null, new object[]
        {
            "正文<hum_back mode=\"practice\" order=\"2\"/><silence in=\"1s\"/>结尾"
        });
        if (!isolated.Contains("正文") || !isolated.Contains("结尾") ||
            !isolated.Contains("<hum_back mode=\"practice\" order=\"2\"/>") ||
            isolated.Contains("<silence"))
            throw new InvalidOperationException(
                "An unknown tag is still discarding valid text/tool output from the same response.");

        MethodInfo removeQuotedActionTags = chatType.GetMethod(
            "RemoveQuotedAgentActionTags", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo extractHumBack = chatType.GetMethod(
            "ExtractHumBackTag", BindingFlags.Instance | BindingFlags.NonPublic);
        if (removeQuotedActionTags == null || extractHumBack == null)
            throw new MissingMemberException("Quoted tool/action separation helpers are missing.");
        GameObject parserHost = new GameObject("QuotedToolActionRegression");
        parserHost.SetActive(false);
        try
        {
            var parserChat = parserHost.AddComponent<ChatSample>();
            string quotedThenReal = "说明里提到 `<hum_back/>`，随后执行：" +
                                    "<hum_back mode=\"practice\" order=\"1,2,3,4\"/>";
            string actionable = (string)removeQuotedActionTags.Invoke(null, new object[] { quotedThenReal });
            object[] humBackArgs = { actionable };
            object humBack = extractHumBack.Invoke(parserChat, humBackArgs);
            FieldInfo humBackMode = humBack?.GetType().GetField("Mode", BindingFlags.Instance | BindingFlags.Public);
            FieldInfo humBackOrder = humBack?.GetType().GetField("Order", BindingFlags.Instance | BindingFlags.Public);
            if (humBack == null || humBackMode == null || humBackOrder == null ||
                (string)humBackMode.GetValue(humBack) != "practice" ||
                (string)humBackOrder.GetValue(humBack) != "1,2,3,4")
                throw new InvalidOperationException(
                    "A Markdown-quoted tool example still outranks the real hum_back action.");
            string quotedOnly = (string)removeQuotedActionTags.Invoke(
                null, new object[] { "这里只是在解释 `<hum_back mode=\"practice\"/>`。" });
            object[] quotedOnlyArgs = { quotedOnly };
            if (extractHumBack.Invoke(parserChat, quotedOnlyArgs) != null)
                throw new InvalidOperationException("A quoted hum_back example was executed as an action.");
        }
        finally { UnityEngine.Object.DestroyImmediate(parserHost); }

        object[] validArgs =
        {
            "prefix {\"proceed\":true,\"intent\":\"换个新话题\"," +
            "\"novelty\":\"不是上一句的改写\",\"wait_seconds\":30} suffix",
            null,
        };
        bool valid = (bool)parseMethod.Invoke(null, validArgs);
        if (!valid || validArgs[1] == null)
            throw new InvalidOperationException("Valid autonomy intent JSON was not parsed.");
        FieldInfo proceedField = validArgs[1].GetType().GetField(
            "proceed", BindingFlags.Instance | BindingFlags.Public);
        if (proceedField == null || !(bool)proceedField.GetValue(validArgs[1]))
            throw new InvalidOperationException("Parsed autonomy intent lost proceed=true.");

        object[] invalidArgs = { "{\"intent\":\"missing proceed\"}", null };
        if ((bool)parseMethod.Invoke(null, invalidArgs))
            throw new InvalidOperationException("Autonomy intent JSON without proceed must fail closed to fallback.");
    }
}
