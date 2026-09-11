using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;
using WebGLSupport;
using AIChat.Agent;
using AIChat.Memory;

public partial class ChatSample : MonoBehaviour
{
    /// <summary>
    /// 聊天配置
    /// </summary>
    [SerializeField] private ChatSetting m_ChatSettings;
    #region UI定义
    /// <summary>
    /// 聊天UI窗
    /// </summary>
    [SerializeField] private GameObject m_ChatPanel;
    /// <summary>
    /// 输入的信息
    /// </summary>
    [SerializeField] public InputField m_InputWord;
    /// <summary>
    /// 返回的信息
    /// </summary>
    [SerializeField] private Text m_TextBack;
    [Header("字幕、翻译与系统通知")]
    [SerializeField] private SubtitleDisplayMode m_SubtitleDisplayMode =
        SubtitleDisplayMode.OriginalOnly;
    [SerializeField] private SubtitleLanguage m_SubtitleTargetLanguage =
        SubtitleLanguage.ChineseSimplified;
    [SerializeField] private SystemNoticeMode m_SystemNoticeMode =
        SystemNoticeMode.ErrorsOnly;
    [Tooltip("只在 TranslationOnly/Bilingual 模式调用无历史翻译接口")]
    [SerializeField] private bool m_EnableSubtitleTranslation = true;
    [Tooltip("运行时切换会保存到 PlayerPrefs")]
    [SerializeField] private bool m_PersistSubtitleSettings = true;
    [SerializeField] private bool m_ShowSystemNoticeCodes = false;
    [SerializeField] private SubtitleOverlay m_SubtitleOverlay;
    /// <summary>
    /// 播放声音
    /// </summary>
    [SerializeField] private AudioSource m_AudioSource;
    /// <summary>
    /// 发送信息按钮
    /// </summary>
    [SerializeField] private Button m_CommitMsgBtn;

    #endregion

    #region 参数定义
    /// <summary>
    /// 动画控制器
    /// </summary>
    [SerializeField] private Animator m_Animator;
    /// <summary>
    /// 语音模式，设置为false,则不通过语音合成
    /// </summary>
    [Header("设置是否通过语音合成播放文本")]
    [SerializeField] private bool m_IsVoiceMode = true;
    [Header("勾选则不发送LLM，直接合成输入文字")]
    [SerializeField] private bool m_CreateVoiceMode = false;
    [Header("LLM流式：边生成边切句送TTS。需LLM子类已实现PostMsgStream")]
    [SerializeField] private bool m_UseStreaming = true;

    #endregion

    #region 实时对话(barge-in)接口

    /// <summary>
    /// AI回复结束之后回调（自然播完或被Interrupt都会触发）。
    /// RTSpeechHandler订阅这个事件来恢复VAD监听。
    /// </summary>
    public System.Action OnAISpeakDone;

    /// <summary>
    /// 流式声学与语义闸已经取得稳定歌唱证据时通知录音状态机。这里只传感知事实，
    /// 不替角色决定是否回应、是否保存或是否回唱。
    /// </summary>
    public System.Action<float, float, int> OnStableUserSingingObserved;

    /// <summary>
    /// 流式角色 LLM 高置信度判断当前输入是普通说话时，通知录音状态机解除可逆的
    /// “可能在唱”锁。它只修正输入模态与 EOU 策略，不决定角色怎样回应。
    /// </summary>
    public System.Action<float, int> OnStableUserSpeechObserved;

    /// <summary>
    /// 持续外放/背景声让物理静音无法出现时，角色 LLM 根据稳定文字、持续声学活动和
    /// 环境基线给出的轮次边界判断。程序只校验这份判断仍对应当前文字，再决定是否收束录音。
    /// status: complete | continue | ask_user。
    /// </summary>
    public System.Action<string, float, string, float, string, string, int>
        OnStalledUserTurnDecision;

    /// <summary>
    /// 角色是否正在出声(TTS播放中)。
    /// RTSpeechHandler用这个判断"现在RMS spike算barge-in还是新一轮发言"。
    /// </summary>
    public bool IsAISpeaking { get; private set; }

    /// <summary>
    /// Reverse playback reference used by the barge-in echo canceller.
    /// It is attached to the same AudioSource that renders all TTS chunks.
    /// </summary>
    public PlaybackEchoReferenceTap EchoReferenceTap { get; private set; }

    private float m_LastVoiceOutputEndedRealtime = -999f;
    private bool m_WasVoiceOutputPlaying = false;
    [Header("最终转写中的外放回声证据")]
    [Tooltip("角色刚出声后的这段时间内，若最终转写以角色近期原话开头，就把原文、重合前缀和用户剩余文本一起交给 LLM 判断；程序不直接删字。")]
    [Range(1f, 15f)] [SerializeField] private float m_TextEchoEvidenceWindowSeconds = 8f;

    /// <summary>True when the actual TTS AudioSource is producing output.</summary>
    public bool IsVoiceOutputPlaying
    {
        get
        {
            return (m_AudioSource != null && m_AudioSource.isPlaying) ||
                (m_HumStreamSecondaryAudioSource != null &&
                 m_HumStreamSecondaryAudioSource.isPlaying);
        }
    }

    /// <summary>
    /// Authoritative microphone guard: logical speech state, real AudioSource
    /// playback, and a short acoustic tail after playback are all protected.
    /// </summary>
    public bool IsAIPlaybackProtected(float tailSeconds)
    {
        if (IsAISpeaking || IsVoiceOutputPlaying) return true;
        return Time.realtimeSinceStartup - m_LastVoiceOutputEndedRealtime
            < Mathf.Max(0f, tailSeconds);
    }

    /// <summary>
    /// 用户实际听到的文本累积——只记录已经播放完的chunk的文本。
    /// 被Interrupt时按音频播放比例切当前chunk的尾巴。
    /// </summary>
    private System.Text.StringBuilder m_AssistantHeardText = new System.Text.StringBuilder();
    /// <summary>
    /// 当前正在AudioSource里播的那一chunk的文本，Interrupt时按时长比例算出听到了多少
    /// </summary>
    private string m_CurrentlyPlayingText = "";

    /// <summary>
    /// 取最后一条用户消息（m_ChatHistory偶数位）。Silence事件用来抽情绪/事件标签和判断语境。
    /// </summary>
    public string GetLastUserMessage()
    {
        if (m_ChatHistory == null || m_ChatHistory.Count == 0) return "";
        //最后一条偶数索引(0,2,4...)；若m_ChatHistory.Count为奇数最后一条就是user
        for (int i = m_ChatHistory.Count - 1; i >= 0; i--)
        {
            if (i % 2 == 0) return m_ChatHistory[i];
        }
        return "";
    }

    /// <summary>
    /// 取最后一条助手消息（m_ChatHistory奇数位）。Silence事件用来判断"我上句是不是问句"。
    /// </summary>
    public string GetLastAssistantMessage()
    {
        if (m_ChatHistory == null || m_ChatHistory.Count == 0) return "";
        for (int i = m_ChatHistory.Count - 1; i >= 0; i--)
        {
            if (i % 2 == 1) return m_ChatHistory[i];
        }
        return "";
    }

    #endregion

    private void Awake()
    {
        EnsureSubtitleOverlay();
        if (m_ChatSettings != null && m_ChatSettings.m_ChatModel != null)
        {
            m_ChatSettings.m_ChatModel.OnSystemNotice += HandleSystemNotice;
            m_ChatSettings.m_ChatModel.OnRawResponse += HandleRawLLMResponse;
            m_ChatSettings.m_ChatModel.OnOutputFormatError += HandleLLMOutputFormatError;
            m_ChatSettings.m_ChatModel.OnRequestDiagnostic += HandleRequestDiagnostic;
        }
        if (m_AudioSource != null)
        {
            EchoReferenceTap = m_AudioSource.GetComponent<PlaybackEchoReferenceTap>();
            if (EchoReferenceTap == null)
                EchoReferenceTap = m_AudioSource.gameObject.AddComponent<PlaybackEchoReferenceTap>();
        }
        m_CommitMsgBtn.onClick.AddListener(delegate { SendData(); });
        RegistButtonEvent();
        InputSettingWhenWebgl();
    }

    private void LateUpdate()
    {
        StepUrge();

        bool playing = IsVoiceOutputPlaying;
        if (playing && !m_WasVoiceOutputPlaying)
        {
            VoiceOutputRevision++;
            ReportPerceivedFirstAudio();
        }
        if (playing && m_HumBackPlaying) ReportPerceivedFormalAudio("singing");
        if (m_WasVoiceOutputPlaying && !playing)
            m_LastVoiceOutputEndedRealtime = Time.realtimeSinceStartup;
        m_WasVoiceOutputPlaying = playing;

        if (m_DeferredPreparedClipToDestroy != null &&
            (!playing || m_AudioSource == null || m_AudioSource.clip != m_DeferredPreparedClipToDestroy))
        {
            Destroy(m_DeferredPreparedClipToDestroy);
            m_DeferredPreparedClipToDestroy = null;
        }
    }

    private void OnDestroy()
    {
        CancelDialogueMotion("chat-destroyed");
        if (m_ChatSettings != null && m_ChatSettings.m_ChatModel != null)
        {
            m_ChatSettings.m_ChatModel.OnSystemNotice -= HandleSystemNotice;
            m_ChatSettings.m_ChatModel.OnRawResponse -= HandleRawLLMResponse;
            m_ChatSettings.m_ChatModel.OnOutputFormatError -= HandleLLMOutputFormatError;
            m_ChatSettings.m_ChatModel.OnRequestDiagnostic -= HandleRequestDiagnostic;
        }
        if (m_ActiveSVSRequest != null) m_ActiveSVSRequest.Abort();
        if (m_ActiveHumSVCRequest != null) m_ActiveHumSVCRequest.Abort();
        if (m_HumBackPrefixSVCRequest != null) m_HumBackPrefixSVCRequest.Abort();
        //仅释放 Unity 持有的进程句柄，不终止本机服务；退出 Play Mode 后 9882 仍可复用。
        if (m_HumSVCServerProcess != null)
        {
            m_HumSVCServerProcess.Dispose();
            m_HumSVCServerProcess = null;
        }
        if (m_SVSServerProcess != null)
        {
            m_SVSServerProcess.Dispose();
            m_SVSServerProcess = null;
        }
        if (m_ChatSettings != null && m_ChatSettings.m_TextToSpeech != null)
            m_ChatSettings.m_TextToSpeech.CancelPreparedSpeech();
        if (m_PreparedSingingBridgeClip != null) Destroy(m_PreparedSingingBridgeClip);
        if (m_DeferredPreparedClipToDestroy != null) Destroy(m_DeferredPreparedClipToDestroy);
        if (m_GeneratedHumCarrierClip != null) Destroy(m_GeneratedHumCarrierClip);
        ClearHumStreamClipBuffers(true);
        if (m_HumStreamSecondaryAudioObject != null)
        {
            Destroy(m_HumStreamSecondaryAudioObject);
            m_HumStreamSecondaryAudioObject = null;
            m_HumStreamSecondaryAudioSource = null;
        }
        if (m_ActiveHumBackClip != null) Destroy(m_ActiveHumBackClip);
        if (m_PreparedHumBackPrefixClip != null) Destroy(m_PreparedHumBackPrefixClip);
        if (m_FastHumBackFullClip != null && m_FastHumBackFullClip != m_ActiveHumBackClip)
            Destroy(m_FastHumBackFullClip);
    }

    private void Start()
    {
        //TTS预热：场景加载后立刻发一条极短请求，消除首次合成的冷启动延迟
        //结果被丢弃，用户不可见
        if (m_ChatSettings != null && m_ChatSettings.m_TextToSpeech != null)
        {
            m_ChatSettings.m_TextToSpeech.WarmUp();
        }
        if (m_EnableNeuralHumSVC && m_AutoStartHumSVC)
        {
            StartCoroutine(EnsureHumSVCReady((ready, detail) =>
            {
                if (!m_LogHumBack) return;
                if (ready) Debug.Log("[HumBack/SVC] 场景启动检查成功: " + detail);
                else Debug.LogWarning("[HumBack/SVC] 场景启动检查失败: " + detail);
            }));
        }
        if (m_EnableSingingVoiceSynthesis && m_AutoStartSVS)
        {
            StartCoroutine(EnsureSVSReady((ready, detail) =>
            {
                if (!m_LogHumBack) return;
                if (ready) Debug.Log("[HumBack/SVS] 场景启动检查成功: " + detail);
                else Debug.LogWarning("[HumBack/SVS] 场景启动检查失败: " + detail);
            }));
        }
    }

    #region 消息发送

    /// <summary>
    /// webgl时处理，支持中文输入
    /// </summary>
    private void InputSettingWhenWebgl()
    {
#if UNITY_WEBGL
        m_InputWord.gameObject.AddComponent<WebGLSupport.WebGLInput>();
#endif
    }


    /// <summary>
    /// 发送信息
    /// </summary>
    public void SendData()
    {
        if (m_InputWord.text.Equals(""))
            return;
        string _text = m_InputWord.text;
        m_InputWord.text = "";
        //键盘/按钮输入不会经过 RTSpeechHandler.StartRecording，因此也不会收到
        //NotifyUserStartedSpeaking。它仍然是一个新的真实用户轮次，必须先撤销上一轮
        //尚未开始、生成中或播放中的角色歌唱，不能让旧动作越过用户的新话。
        if (IsAISpeaking || IsVoiceOutputPlaying) Interrupt();
        else CancelPendingHumBack("typed-user-turn", true);
        SendData(_text);
    }

    private void EnsureSubtitleOverlay()
    {
        if (m_SubtitleOverlay == null)
            m_SubtitleOverlay = GetComponent<SubtitleOverlay>();
        if (m_SubtitleOverlay == null)
            m_SubtitleOverlay = gameObject.AddComponent<SubtitleOverlay>();
        m_SubtitleOverlay.Initialize(
            m_TextBack,
            m_ChatSettings != null ? m_ChatSettings.m_ChatModel : null,
            m_SubtitleDisplayMode,
            m_SubtitleTargetLanguage,
            m_SystemNoticeMode,
            m_EnableSubtitleTranslation,
            m_PersistSubtitleSettings,
            m_ShowSystemNoticeCodes);
    }

    private void HandleSystemNotice(SystemNotice notice)
    {
        EnsureSubtitleOverlay();
        if (m_SubtitleOverlay != null) m_SubtitleOverlay.PublishSystemNotice(notice);
    }

    private void ReportTtsFailureOnce(string code, string message, string detail)
    {
        if (m_LastTtsFailureNoticeGeneration == m_FormalResponseGeneration) return;
        m_LastTtsFailureNoticeGeneration = m_FormalResponseGeneration;
        HandleSystemNotice(new SystemNotice(
            code,
            SystemNoticeSeverity.Error,
            message,
            detail,
            "TTS",
            true));
    }

    public void SetSubtitleDisplayMode(int mode)
    {
        EnsureSubtitleOverlay();
        m_SubtitleOverlay.SetDisplayMode(
            (SubtitleDisplayMode)Mathf.Clamp(mode, 0, 3));
    }

    public void SetSubtitleTargetLanguage(int language)
    {
        EnsureSubtitleOverlay();
        m_SubtitleOverlay.SetTargetLanguage(
            (SubtitleLanguage)Mathf.Clamp(language, 0, 7));
    }

    public void SetSystemNoticeMode(int mode)
    {
        EnsureSubtitleOverlay();
        m_SubtitleOverlay.SetNoticeMode(
            (SystemNoticeMode)Mathf.Clamp(mode, 0, 2));
    }

    public void SetSubtitlesVisible(bool visible)
    {
        EnsureSubtitleOverlay();
        m_SubtitleOverlay.SetSubtitlesVisible(visible);
    }
    /// <summary>
    /// 带文字发送
    /// </summary>
    /// <param name="_postWord"></param>
    public void SendData(string _postWord)
    {
        SendDataInternal(_postWord, null);
    }

    private void SendDataInternal(string _postWord, string speculativeHint, string rawUserText = null)
    {
        if (_postWord.Equals(""))
            return;
        CancelDialogueMotion("new-user-turn");

        //来源确认现在由正式角色在看过完整转写、上下文与边界证据后，通过
        //<practice_confirm/> 明确表达。同轮不再串行跑一个看不到正式角色想法的辅助
        //分类器；旧实现会出现“后台已确认、角色嘴上仍在询问”的分裂状态。

        if (m_CreateVoiceMode)//合成输入为语音
        {
            // This branch reads USER text; quoted action syntax cannot become a role command.
            m_ReadingUserInput = true;
            try { CallBack(_postWord); }
            finally { m_ReadingUserInput = false; }
            m_InputWord.text = "";
            return;
        }

        //添加记录聊天 — 历史气泡里只显示用户原话(感知帧只去 LLM context，不进 UI)
        string userUtterance = rawUserText ?? _postWord;
        m_ChatHistory.Add(userUtterance);
        SetCurrentUserInput(userUtterance, _postWord);
        RecordAcceptedMotionUserTurn(userUtterance);

        //歌曲工具不能依赖 Agent Loop 才知道“用户刚说了什么”。直接对话模式也维护
        //当前用户轮次，供明确保存请求兜底、歌名提取和成功确认使用。
        if (!m_AgentRunning)
        {
            m_LastUserTurnTime = Time.realtimeSinceStartup;
            m_AgentCurrentRoundIsTick = false;
            if (m_MemoryHub != null && m_EnableMemoryRecall)
                m_MemoryHub.NotifyUserUtterance(m_LastUserMsg);
        }

        m_InputWord.text = "";
        bool earlyEouFiller = m_LatencyFillerFromEou && m_LatencyFillerPlayed;
        m_TextBack.text = earlyEouFiller ? m_LatencyFillerText : "正在思考中...";

        //EOU 快速回应可能在 ASR 完成前已经开始；此时不要把说话动作退回思考动作。
        SetAnimator("state", earlyEouFiller ? 2 : 1);

        //这一轮起，在回应他之前不许自主开口
        MarkUserTurnAwaitingReply();

        //只要整段被判为唱歌就并行复核一次，不等到 SVC 转换才问。
        //挂在转换上时，"声学误判成唱歌但没触发回哼"的轮次永远不会被复核：
        //8/12 实测用户连着三轮**用说话语调说**「我要骗你唱歌」，全部被判高区
        //(prob 0.68~0.76、岛占比 95%+)，只因当时 armed 恰好没开才没唱出来。
        BeginTurnSemanticCheck(_postWord);

        //消费上一句留下的“下一轮会唱”预期，并根据这一轮真实用户文本决定是否
        //为再下一轮续上。这里只改变听觉先验，不触发任何歌唱动作。
        UpdateUpcomingUserSingingExpectation(userUtterance);

        //技能只按原话和本轮独立声音证据路由；完整听觉输入保留在用户消息中。
        //当前程序感知单独暂存，正式请求时注入一次临时上下文。
        PrepareSkillsForAcceptedUserInput(userUtterance,
            ConsumeSkillInputObservation(userUtterance, rawUserText != null));
        CommitConfirmedSingingBeforePerception();
        string llmInput = (m_AgentRunning) ? PrepareAcceptedUserTurn(_postWord, userUtterance) : _postWord;
        // Prepared thoughts belong to this request only, not persisted user history.

        // A previously armed sing-along is deterministic once final ASR confirms a
        // playable performance.  Do not spend another LLM round deciding whether to
        // invoke the tool: commit the already prepared/queued real singing directly.
        //
        //这条快车道把回哼当成本轮**唯一**的回应，正式回复根本不会生成。所以转换期
        //否决一旦拦下音频，这一轮就一个字都不剩了——8/12 实测用户唱完之后她全程
        //没回应，之后的自主发言还在接上一轮的「トイレ」，用户以为她答非所问。
        //把这一轮的输入留着，否决生效时按普通对话轮补发。
        m_VetoFallbackLlmInput = llmInput;
        m_VetoFallbackPostWord = _postWord;
        if (TryHandleDirectSingAlongTurn()) return;
        m_VetoFallbackLlmInput = null;

        //流式 or 整段
        if (m_UseStreaming && m_IsVoiceMode && m_ChatSettings.m_TextToSpeech != null)
        {
            bool singingTurn = _postWord.IndexOf("[演唱片段", StringComparison.Ordinal) >= 0;
            bool waitForRealHumBack = ShouldHoldSpeechForExplicitHumBack();
            StartStreaming(
                llmInput,
                !singingTurn && string.IsNullOrWhiteSpace(speculativeHint),
                _postWord,
                false,
                waitForRealHumBack,
                transientSystemContext: speculativeHint);
        }
        else
        {
            //Whether to save singing is an LLM decision expressed through
            //<song_remember/>.  Do not infer it from the user's wording.
            m_HoldSpeechForSongMemoryResult = false;
            m_HoldSpeechForHumBackResult = ShouldHoldSpeechForExplicitHumBack();
            PublishSpokenPrefixToLlm();
            m_ChatSettings.m_ChatModel.RequestContext = BuildMotionRequestContext(BuildFormalObservationContext(speculativeHint));
            m_FormalResponseInFlight = true;
            m_ChatSettings.m_ChatModel.PostSpeechMessage(llmInput, CaptureMotionResponseCallback());
        }
    }

    private sealed class PendingPracticeResolutionTarget
    {
        //辅助分类器只输出这一轮快照里的 selector；真正执行仍使用稳定 candidate id
        //或当时的 practice index，避免两套都叫“第 1 段”。
        public int Selector;
        public int PracticeIndex;
        public int CandidateId;
        public bool IsCandidate { get { return CandidateId > 0; } }
    }

    private bool TryBeginPracticeConfirmationProbe(
        string userText,
        string speculativeHint,
        string rawUserText = null)
    {
        SenseVoiceSpeechToText sense = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        ChatQW qw = m_ChatSettings != null
            ? m_ChatSettings.m_ChatModel as ChatQW
            : null;
        if (sense == null || qw == null) return false;

        List<PendingPracticeResolutionTarget> pending =
            BuildPendingPracticeResolutionTargets(sense);
        if (pending.Count == 0) return false;
        string pendingSummary = BuildPendingPracticeSemanticSummary(sense, pending);
        if (string.IsNullOrWhiteSpace(pendingSummary)) return false;

        //当前用户消息尚未写入 m_ChatHistory，因此这里拿到的正是角色上一句。
        string lastAssistant = GetLastAssistantSemanticContext(false);
        int inputRevision = m_InputAudioRevision;
        qw.ClassifyPracticeConfirmation(
            rawUserText ?? userText,
            lastAssistant,
            pendingSummary,
            decision =>
            {
                if (inputRevision != m_InputAudioRevision) return;
                ApplyPracticeConfirmationDecision(sense, pending, decision);
                SendDataInternal(userText, speculativeHint, rawUserText);
            });
        return true;
    }

    private static List<PendingPracticeResolutionTarget>
        BuildPendingPracticeResolutionTargets(SenseVoiceSpeechToText sense)
    {
        var targets = new List<PendingPracticeResolutionTarget>();
        if (sense == null) return targets;
        int selector = 0;
        //先列来源不确定候选，并保持真实录音顺序。用户说“最前两段/本次全部”时，
        //LLM 能直接看到 candidate 的稳定身份，而不是被较晚生成的 practice 编号误导。
        foreach (SenseVoiceSpeechToText.QuarantinedSingingCandidateInfo candidate in
                 sense.DescribeQuarantinedSingingCandidates())
        {
            if (candidate == null || candidate.CandidateId <= 0 ||
                !string.Equals(candidate.SourceStatus, "pending",
                    StringComparison.OrdinalIgnoreCase))
                continue;
            targets.Add(new PendingPracticeResolutionTarget
            {
                Selector = ++selector,
                CandidateId = candidate.CandidateId,
            });
        }
        foreach (int practiceIndex in sense.PendingConfirmationPracticeIndices())
        {
            targets.Add(new PendingPracticeResolutionTarget
            {
                Selector = ++selector,
                PracticeIndex = practiceIndex,
            });
        }
        return targets;
    }

    private static string BuildPendingPracticeSemanticSummary(
        SenseVoiceSpeechToText sense,
        List<PendingPracticeResolutionTarget> pending)
    {
        if (sense == null || pending == null || pending.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        List<SenseVoiceSpeechToText.QuarantinedSingingCandidateInfo> candidates =
            sense.DescribeQuarantinedSingingCandidates();
        List<SenseVoiceSpeechToText.PracticePhraseInfo> phrases =
            sense.DescribePracticePhrases();
        for (int i = 0; i < pending.Count; i++)
        {
            PendingPracticeResolutionTarget target = pending[i];
            if (target == null) continue;
            if (sb.Length > 0) sb.Append('\n');
            if (target.IsCandidate)
            {
                SenseVoiceSpeechToText.QuarantinedSingingCandidateInfo candidate =
                    candidates.Find(value => value != null &&
                        value.CandidateId == target.CandidateId);
                if (candidate == null) continue;
                string candidateLyric = (candidate.Lyrics ?? "").Trim();
                if (candidateLyric.Length > 80)
                    candidateLyric = candidateLyric.Substring(0, 80) + "…";
                sb.Append(target.Selector)
                  .Append(": kind=source_uncertain; candidate_id=")
                  .Append(candidate.CandidateId)
                  .Append("; capture_order=")
                  .Append(candidate.CaptureOrder)
                  .Append("; duration=")
                  .Append(candidate.Seconds.ToString("F1"))
                  .Append("s; lyrics=\"")
                  .Append(candidateLyric)
                  .Append('"');
                if (!string.IsNullOrWhiteSpace(candidate.PrecedingSpeech))
                {
                    string before = candidate.PrecedingSpeech.Trim();
                    if (before.Length > 80) before = before.Substring(0, 80) + "…";
                    sb.Append("; preceding_speech=\"").Append(before).Append('"');
                }
                continue;
            }
            SenseVoiceSpeechToText.PracticePhraseInfo phrase =
                phrases.Find(value => value != null &&
                    value.Index == target.PracticeIndex);
            if (phrase == null) continue;
            string lyric = (phrase.Lyrics ?? "").Trim();
            if (lyric.Length > 80) lyric = lyric.Substring(0, 80) + "…";
            sb.Append(target.Selector)
              .Append(": kind=practice_pending; practice_index=")
              .Append(phrase.Index).Append("; lyrics=\"")
              .Append(lyric).Append("\"");
            if (!string.IsNullOrWhiteSpace(phrase.PrecedingSpeech))
            {
                string before = phrase.PrecedingSpeech.Trim();
                if (before.Length > 80) before = before.Substring(0, 80) + "…";
                sb.Append("; preceding_speech=\"").Append(before).Append('"');
            }
        }
        return sb.ToString();
    }

    private void ApplyPracticeConfirmationDecision(
        SenseVoiceSpeechToText sense,
        List<PendingPracticeResolutionTarget> pendingSnapshot,
        string decision)
    {
        if (sense == null || pendingSnapshot == null || pendingSnapshot.Count == 0)
            return;
        List<int> allowedSelectors = pendingSnapshot.ConvertAll(target => target.Selector);
        List<int> confirmedSelectors = ParseIndexedClassifierDecision(
            decision, "confirm:", allowedSelectors);
        if (confirmedSelectors.Count > 0)
        {
            var candidateMappings = new List<string>();
            var confirmedPractice = new List<int>();
            for (int i = 0; i < pendingSnapshot.Count; i++)
            {
                PendingPracticeResolutionTarget target = pendingSnapshot[i];
                if (!confirmedSelectors.Contains(target.Selector)) continue;
                if (target.IsCandidate)
                {
                    if (sense.ConfirmQuarantinedSingingCandidateWithPlaybackStatus(
                            target.CandidateId, out int phraseIndex,
                            out string playbackStatus))
                    {
                        candidateMappings.Add(phraseIndex > 0
                            ? $"候选 {target.CandidateId}→清单第 {phraseIndex} 段"
                            : $"候选 {target.CandidateId} 已确认属于用户，" +
                              $"但 playback={playbackStatus}，尚不能播放");
                    }
                }
                else if (target.PracticeIndex > 0)
                    confirmedPractice.Add(target.PracticeIndex);
            }
            int cleared = confirmedPractice.Count > 0
                ? sense.ConfirmPracticePhrases(confirmedPractice)
                : 0;
            if (candidateMappings.Count > 0 || cleared > 0)
            {
                var confirmedFacts = new System.Text.StringBuilder(
                    "\n练唱片段语义确认事实: ");
                if (candidateMappings.Count > 0)
                    confirmedFacts.Append("LLM结合用户本轮完整原话确认了来源待确认候选属于" +
                        "用户自己的歌唱/哼唱：" + string.Join("、", candidateMappings) + "。 ");
                if (cleared > 0)
                    confirmedFacts.Append("用户明确确认原练唱清单第 " +
                        string.Join("、", confirmedPractice) +
                        " 段属于歌唱/哼唱，待确认标已经解除。 ");
                confirmedFacts.Append(
                    "这只更新素材事实，不代表用户一定要求你现在回唱；是否行动、直接判断或自然询问" +
                    "仍由你结合当前原话决定。");
                m_PracticeSemanticResolutionNote = confirmedFacts.ToString();
                if (m_LogHumBack)
                    Debug.Log("[SenseVoice/Practice] 用户语义确认完成：" +
                              (candidateMappings.Count > 0
                                  ? string.Join("、", candidateMappings) + " "
                                  : "") +
                              (cleared > 0
                                  ? "原段=" + string.Join("、", confirmedPractice)
                                  : ""));
            }
            return;
        }

        List<int> rejectedSelectors = ParseIndexedClassifierDecision(
            decision, "reject:", allowedSelectors);
        if (rejectedSelectors.Count > 0)
        {
            var discardedCandidates = new List<int>();
            var rejectedPractice = new List<int>();
            for (int i = 0; i < pendingSnapshot.Count; i++)
            {
                PendingPracticeResolutionTarget target = pendingSnapshot[i];
                if (!rejectedSelectors.Contains(target.Selector)) continue;
                if (target.IsCandidate)
                {
                    if (sense.DiscardQuarantinedSingingCandidate(target.CandidateId))
                        discardedCandidates.Add(target.CandidateId);
                }
                else if (target.PracticeIndex > 0)
                    rejectedPractice.Add(target.PracticeIndex);
            }
            var rejectedFacts = new System.Text.StringBuilder(
                "\n练唱片段语义否认事实: ");
            if (discardedCandidates.Count > 0)
                rejectedFacts.Append("LLM结合用户本轮完整原话确认来源候选 " +
                    string.Join("、", discardedCandidates) +
                    " 不是用户自己的歌唱/哼唱；它们从未进入练唱清单，现已安全丢弃。 ");
            if (rejectedPractice.Count > 0)
                rejectedFacts.Append("用户刚刚明确表示原练唱清单第 " +
                    string.Join("、", rejectedPractice) +
                    " 段不是歌唱/哼唱。程序没有自动删除已有素材；请由你结合语境决定是否使用 " +
                    "<practice_drop order=\"段号\" reason=\"用户明确否认该段是歌声\"/>。");
            m_PracticeSemanticResolutionNote = rejectedFacts.ToString();
            if (m_LogHumBack)
                Debug.Log("[SenseVoice/Practice] 用户语义否认完成：" +
                          (discardedCandidates.Count > 0
                              ? "隔离候选=" + string.Join("、", discardedCandidates) + " 已丢弃 "
                              : "") +
                          (rejectedPractice.Count > 0
                              ? "原段=" + string.Join("、", rejectedPractice) +
                                " 保留并交给角色决定"
                              : ""));
        }
    }

    private static List<int> ParseIndexedClassifierDecision(
        string decision,
        string prefix,
        List<int> allowedIndices)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(decision) || string.IsNullOrWhiteSpace(prefix) ||
            allowedIndices == null || allowedIndices.Count == 0)
            return result;
        string text = decision.Trim();
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return result;
        string payload = text.Substring(prefix.Length).Trim();
        if (string.Equals(payload, "all", StringComparison.OrdinalIgnoreCase))
        {
            result.AddRange(allowedIndices);
            return result;
        }
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(payload, @"\d+"))
        {
            if (!int.TryParse(match.Value, out int index) ||
                !allowedIndices.Contains(index) || result.Contains(index))
                continue;
            result.Add(index);
        }
        return result;
    }

    private void ApplyMemoryOpsWithGrounding(List<MemoryTagParser.MemoryOp> ops)
    {
        if (ops == null || ops.Count == 0 || m_MemoryHub == null) return;

        var factualOrdinals = new List<int>();
        int factOrdinal = 0;
        var proposals = new System.Text.StringBuilder();
        for (int i = 0; i < ops.Count; i++)
        {
            MemoryTagParser.MemoryOp op = ops[i];
            if (op.isLink || op.isNote) continue;
            factOrdinal++;
            factualOrdinals.Add(factOrdinal);
            proposals.Append(factOrdinal)
              .Append(": action=")
              .Append(op.isUpdate ? "update" : "add")
              .Append("; name=\"").Append(op.name ?? "")
              .Append("\"; desc=\"").Append(op.desc ?? "")
              .Append("\"\n");
        }

        //短期 note 和主观联想 link 不把猜测固化成事实节点，沿用角色原本的自主决定。
        if (factualOrdinals.Count == 0)
        {
            m_MemoryHub.ApplyMemoryOps(ops);
            return;
        }

        ChatQW qw = m_ChatSettings != null
            ? m_ChatSettings.m_ChatModel as ChatQW
            : null;
        if (qw == null)
        {
            ApplyGroundedMemorySubset(ops, new List<int>(), false);
            return;
        }

        string evidence = BuildMemoryGroundingEvidenceContext();
        qw.DecomposeMemoryClaims(proposals.ToString(), atoms =>
        {
            if (atoms == null || atoms.Length == 0)
            {
                ApplyGroundedMemorySubset(ops, new List<int>(), false);
                return;
            }

            var atomicOrdinals = new List<int>();
            var atomToFact = new Dictionary<int, int>();
            var atomicProposals = new System.Text.StringBuilder();
            int atomicOrdinal = 0;
            for (int i = 0; i < atoms.Length; i++)
            {
                ChatQW.MemoryAtomicClaim atom = atoms[i];
                if (atom == null || !factualOrdinals.Contains(atom.original_index) ||
                    string.IsNullOrWhiteSpace(atom.claim))
                    continue;
                string claim = atom.claim.Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (claim.Length > 600) claim = claim.Substring(0, 600);
                atomicOrdinal++;
                atomicOrdinals.Add(atomicOrdinal);
                atomToFact[atomicOrdinal] = atom.original_index;
                atomicProposals.Append(atomicOrdinal)
                  .Append(": original=").Append(atom.original_index)
                  .Append("; claim=\"").Append(claim).Append("\"\n");
            }

            if (atomicOrdinals.Count == 0)
            {
                ApplyGroundedMemorySubset(ops, new List<int>(), false);
                return;
            }

            qw.ValidateMemoryGrounding(evidence, atomicProposals.ToString(), decision =>
            {
                bool validatorSucceeded = !string.IsNullOrWhiteSpace(decision);
                List<int> acceptedAtoms = ParseIndexedClassifierDecision(
                    decision, "accept:", atomicOrdinals);
                List<int> acceptedFacts = SelectFullyGroundedFactOrdinals(
                    factualOrdinals, atomToFact, acceptedAtoms);
                ApplyGroundedMemorySubset(ops, acceptedFacts, validatorSucceeded);
            });
        });
    }

    private static List<int> SelectFullyGroundedFactOrdinals(
        List<int> factualOrdinals,
        Dictionary<int, int> atomToFact,
        List<int> acceptedAtomOrdinals)
    {
        var result = new List<int>();
        if (factualOrdinals == null || atomToFact == null ||
            acceptedAtomOrdinals == null)
            return result;
        var accepted = new HashSet<int>(acceptedAtomOrdinals);
        foreach (int factOrdinal in factualOrdinals)
        {
            bool foundAtom = false;
            bool allAccepted = true;
            foreach (KeyValuePair<int, int> pair in atomToFact)
            {
                if (pair.Value != factOrdinal) continue;
                foundAtom = true;
                if (!accepted.Contains(pair.Key)) allAccepted = false;
            }
            if (foundAtom && allAccepted) result.Add(factOrdinal);
        }
        return result;
    }

    private string BuildMemoryGroundingEvidenceContext()
    {
        var sb = new System.Text.StringBuilder();
        string currentUser = StripPerceptionPrefixes(m_LastUserMsg ?? "").Trim();
        if (!string.IsNullOrEmpty(currentUser))
            sb.Append("当前用户原话：\n").Append(TruncateMemoryEvidence(currentUser, 3000));

        //记忆标签在当前回复全文完成后才解析；m_DataList 的最后一条 assistant
        //已经是正在审查的这份候选回复，必须跳过，不能让它给自己作证。
        string lastAssistant = GetLastAssistantSemanticContext(true);
        if (!string.IsNullOrWhiteSpace(lastAssistant))
            sb.Append("\n前一角色发言（只能帮助解析用户的‘是/对/它’，不能单独作为事实依据）：\n")
              .Append(TruncateMemoryEvidence(lastAssistant, 2000));

        LLM model = m_ChatSettings != null ? m_ChatSettings.m_ChatModel : null;
        if (model != null && model.m_DataList != null)
        {
            var recentUsers = new List<string>();
            for (int i = model.m_DataList.Count - 1;
                 i >= 0 && recentUsers.Count < 4;
                 i--)
            {
                LLM.SendData message = model.m_DataList[i];
                if (message == null ||
                    !string.Equals(message.role, "user", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(message.content))
                    continue;
                recentUsers.Add(TruncateMemoryEvidence(message.content, 3500));
            }
            recentUsers.Reverse();
            if (recentUsers.Count > 0)
            {
                sb.Append("\n最近可见的用户轮与系统感知事实：");
                for (int i = 0; i < recentUsers.Count; i++)
                    sb.Append("\n--- user turn ").Append(i + 1).Append(" ---\n")
                      .Append(recentUsers[i]);
            }
        }

        if (model != null && !string.IsNullOrWhiteSpace(model.TrailingContext))
            sb.Append("\n当前可见的本地记忆/短期事实：\n")
              .Append(TruncateMemoryEvidence(model.TrailingContext, 6000));
        return sb.ToString();
    }

    private string GetLastAssistantSemanticContext(bool skipLatestAssistant)
    {
        LLM model = m_ChatSettings != null ? m_ChatSettings.m_ChatModel : null;
        if (model != null && model.m_DataList != null)
        {
            bool skipped = false;
            for (int i = model.m_DataList.Count - 1; i >= 0; i--)
            {
                LLM.SendData message = model.m_DataList[i];
                if (message == null ||
                    !string.Equals(message.role, "assistant", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(message.content))
                    continue;
                if (skipLatestAssistant && !skipped)
                {
                    skipped = true;
                    continue;
                }
                return TruncateMemoryEvidence(message.content, 3000);
            }
        }
        //旧 provider 没维护 m_DataList 时保留原来的聊天记录后备。
        return GetLastAssistantMessage();
    }

    private static string TruncateMemoryEvidence(string text, int maxChars)
    {
        string clean = (text ?? "").Trim();
        if (clean.Length <= maxChars) return clean;
        //保留尾部：感知帧后面才是用户本人的当前原话。
        return "…" + clean.Substring(clean.Length - maxChars);
    }

    private void ApplyGroundedMemorySubset(
        List<MemoryTagParser.MemoryOp> ops,
        List<int> acceptedFactOrdinals,
        bool validatorSucceeded)
    {
        var accepted = acceptedFactOrdinals ?? new List<int>();
        var safe = new List<MemoryTagParser.MemoryOp>();
        var rejectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rejectedDescriptions = new List<string>();
        int factOrdinal = 0;

        for (int i = 0; i < ops.Count; i++)
        {
            MemoryTagParser.MemoryOp op = ops[i];
            if (op.isLink || op.isNote) continue;
            factOrdinal++;
            if (accepted.Contains(factOrdinal))
            {
                safe.Add(op);
            }
            else
            {
                if (!op.isUpdate && !string.IsNullOrWhiteSpace(op.name))
                    rejectedNames.Add(op.name.Trim());
                rejectedDescriptions.Add(string.IsNullOrWhiteSpace(op.name)
                    ? "未命名候选"
                    : op.name.Trim());
            }
        }

        //note 保留；link 也保留，除非它正指向本批次被拒绝的新事实节点。
        for (int i = 0; i < ops.Count; i++)
        {
            MemoryTagParser.MemoryOp op = ops[i];
            if (op.isNote)
            {
                safe.Add(op);
                continue;
            }
            if (op.isLink &&
                !rejectedNames.Contains(op.from ?? "") &&
                !rejectedNames.Contains(op.to ?? ""))
                safe.Add(op);
        }

        if (safe.Count > 0) m_MemoryHub.ApplyMemoryOps(safe);
        if (rejectedDescriptions.Count == 0) return;

        string reason = validatorSucceeded
            ? "当前可见证据不足以完整支持候选事实"
            : "记忆依据审查没有得到可靠结果";
        string detail = reason + "，未写入长期记忆：" +
            string.Join("、", rejectedDescriptions) +
            "。可以保留为短期线索，或等用户明确说明后再记。";
        NoteToolFailure(detail);
        Debug.LogWarning("[Memory/Grounding] " + detail);
    }

    /// <summary>
    /// AI回复的信息的回调
    /// </summary>
    /// <param name="_response"></param>
    private void CallBack(string _response)
    {
        var speech = new SpeechTextBuffer();
        var channels = new RoleOutputChannels(part => speech.Append(part));
        channels.Push(_response);
        channels.Finish();
        CallBackWithSpeech(speech.Snapshot(), channels.ToExecutableText());
    }

    private void CallBackWithSpeech(List<SpeechText> speech, string _response)
    {
        if (!m_ReadingUserInput) m_CurrentSpeechRoundId = ++m_SpeechRoundSequence;
        _response = (_response ?? "").Trim();
        if (!m_ReadingUserInput)
        {
            if (speech != null)
                foreach (SpeechText part in speech) ObserveSingingSpeechPhase(part);
            ObserveSingingSpeechCompletion(ref _response);
        }
        // Markdown code spans quote protocol syntax for explanation; they are not
        // actions. Remove quoted known tags before every non-streaming extractor so
        // an example such as `<hum_back/>` cannot outrank the real tag that follows.
        _response = RemoveQuotedAgentActionTags(_response);
        if (!m_ReadingUserInput) RecordWorkResponse(_response);
        ExtractSelfInspection(ref _response, !m_ReadingUserInput && !m_UserSpeechActiveForAutonomy);
        ExtractSingingGoalTags(ref _response, !m_ReadingUserInput && !m_UserSpeechActiveForAutonomy);
        int subtitleGeneration = BeginFormalResponseGeneration();
        DialogueMotionIntent? pendingMotion = ExtractDialogueMotion(ref _response);
        EnsureSubtitleOverlay();
        if (m_SubtitleOverlay != null) m_SubtitleOverlay.BeginUtterance(subtitleGeneration);
        if (CurrentMotionResponseRejected) return;
        if (_response.Length == 0 && !pendingMotion.HasValue)
        {
            HandleSystemNotice(new SystemNotice(
                "llm_response_failed",
                SystemNoticeSeverity.Error,
                "角色回复生成失败，请稍后再试。",
                "non-streaming response was empty",
                "ChatSample",
                true));
            m_TextBack.text = "";
            return;
        }
        if (pendingMotion.HasValue && (_response.Length == 0 || _response == "<silent/>"))
        {
            // RoleOutputChannels prefixes action-only replies with exactly <silent/>.
            // Both that projection and a bare motion are valid nonverbal replies;
            // do not create empty TTS/history entries or skip any other tool tags.
            DispatchDialogueMotion(pendingMotion, false);
            m_TextBack.text = "";
            return;
        }
        //非流式路径同样处理标签:记忆标签提取应用,其余标签剥净——
        //system prompt 无条件教标签,任何模式下模型都可能输出,漏剥会被念出来
        string afterMem;
        var memOps = MemoryTagParser.Extract(_response, out afterMem);
        ApplyMemoryOpsWithGrounding(memOps);
        AgentSkillRequest ignoredSkillRequest = ExtractSkillRequestTag(ref afterMem);
        if (ignoredSkillRequest != null && m_LogAgentLoop)
            Debug.LogWarning("[LLM技能] 非流式回复中的自主 Skill 申请已忽略；该握手只在 Agent 流式链中执行");
        AgentSkillControlRequest skillControl = ExtractSkillControlTag(ref afterMem);
        AgentSingingPolicyRequest singingPolicy = ExtractSingingPolicyTag(ref afterMem);
        bool selfRepeat = IsVerbatimSelfRepeat(
            StripAgentTagsForTTS(afterMem), out string selfRepeatReason);
        DispatchDialogueMotion(pendingMotion, selfRepeat);
        if (CurrentMotionResponseRejected) return;
        bool singingPolicyAccepted = false;
        string singingPolicyResult = "";
        if (!selfRepeat && singingPolicy != null)
        {
            singingPolicyAccepted = TryApplySingingPolicy(
                singingPolicy, out singingPolicyResult);
            if (!singingPolicyAccepted)
                NoteToolFailure("未执行 singing 权限变更：" + singingPolicyResult);
        }
        if (!selfRepeat && skillControl != null &&
            !TryApplySkillControl(skillControl, out string skillControlResult))
        {
            NoteToolFailure("未执行 Skill 权限变更：" + skillControlResult);
        }
        bool hadSingingAction = s_PracticeConfirmTagRegex.IsMatch(afterMem) ||
            s_PracticeReviseTagRegex.IsMatch(afterMem) ||
            s_PracticeDropTagRegex.IsMatch(afterMem) ||
            s_SongMemoryTagRegex.IsMatch(afterMem) || s_SongSearchTagRegex.IsMatch(afterMem) ||
            s_SongSingTagRegex.IsMatch(afterMem) || s_HumBackTagRegex.IsMatch(afterMem);
        bool singingActionAllowed = CanExecuteSkillAction("singing", out string singingBlockReason);
        AgentSongMemoryRequest songMemory = ExtractSongMemoryTag(ref afterMem);
        AgentSongSearchRequest songSearch = ExtractSongSearchTag(ref afterMem);
        AgentSongCatalogRequest songCatalog = ExtractSongCatalogTag(ref afterMem);
        AgentSongSingRequest songSing = ExtractSongSingTag(ref afterMem);
        m_PracticeEditFailedThisResponse = false;
        ExtractAndApplyPracticeConfirmTag(ref afterMem, !selfRepeat && singingActionAllowed);
        ExtractAndApplyPracticeReviseTag(ref afterMem, !selfRepeat && singingActionAllowed);
        ExtractAndApplyPracticeDropTag(ref afterMem, !selfRepeat && singingActionAllowed);
        AgentHumBackRequest humBack = ExtractHumBackTag(ref afterMem);
        AgentSingingStopRequest singingStop = ExtractSingingStopTag(ref afterMem);
        if (!selfRepeat && singingStop != null)
        {
            bool continuesWithNewAction = songSing != null || humBack != null;
            CancelSkillRuntimeActivity("singing", "singing-stop-tool");
            if (continuesWithNewAction)
            {
                NoteSingingRoutingFact(
                    "singing_stop",
                    "old_activity_stopped_then_new_action_retained",
                    "同一回复还包含新的歌唱动作；程序只停止旧活动，并继续处理新动作。" +
                    "是否发起新动作仍来自角色本轮决定。");
            }
            if (m_LogAgentLoop)
                Debug.Log("[LLM技能] singing_stop 已执行；reason=" + singingStop.Reason +
                          (continuesWithNewAction ? "；同轮新歌唱动作保留" : ""));
        }
        string speakerName = ExtractSpeakerNameTag(ref afterMem);
        AgentSpeakerManageRequest speakerManage = ExtractSpeakerManageTag(ref afterMem);
        if (selfRepeat)
        {
            songMemory = null; songSearch = null; songCatalog = null; songSing = null; humBack = null;
            speakerName = null; speakerManage = null;
            m_SelfRepeatNote = "未执行：" + selfRepeatReason;
            NoteToolFailure(m_SelfRepeatNote);
            Debug.LogWarning("[Agent/复读] 逐字重复了自己最近的发言，本轮工具全部未派发");
        }
        else
        {
            ApplySpeakerNameTag(speakerName);
        }
        if (hadSingingAction && !singingActionAllowed)
        {
            songMemory = null; songSearch = null; songSing = null; humBack = null;
            NoteToolFailure("未执行 singing Skill 动作：" + singingBlockReason);
            Debug.LogWarning("[LLM技能] 拦下未授权的 singing 动作：" + singingBlockReason);
        }
        if (songCatalog != null &&
            !CanInspectSkillData("singing", out string catalogInspectionBlockReason))
        {
            songCatalog = null;
            NoteToolFailure("未执行曲库自查：" + catalogInspectionBlockReason);
        }
        RejectInvalidSongSingMaterialSource(ref songSing);
        bool songSingRerouted = TryRerouteSongSingToPractice(ref songSing, ref humBack);
        if (songSingRerouted)
        {
            NoteSingingRoutingFact(
                "song_sing",
                "routed_for_validation",
                "current_material_source_corrected",
                $"角色决定演唱的意图已保留；实际素材不在长期曲库，已改走 " +
                $"hum_back mode={humBack.Mode}, order={humBack.Order ?? ""}。" +
                "程序没有替角色决定是否演唱，只纠正了客观素材来源；" +
                "这个改路请求仍需通过 order 等参数校验才会执行。");
            if (m_LogHumBack)
                Debug.LogWarning($"[SongSing→HumBack] 曲库标签指向当前歌声素材，已按来源改走 {humBack.Mode}");
        }
        if (ShouldDiscardSongSingToolForCurrentTurn(songSing))
        {
            ReportToolFailureForLlm(
                "song_sing",
                "wrong_material_source",
                "当前用户原话指向跟唱约定、正在发生的演唱、失败反馈或最近录音，" +
                "而 song_sing 只读取长期曲库；本次曲库演唱没有执行。",
                "先根据真实可用素材判断来源：即时录音用 hum_back echo，" +
                "练唱清单用 hum_back practice；只有用户确实指向长期曲库时才用 song_sing。" +
                "是否仍要演唱、解释或询问用户由你决定。");
            if (m_LogHumBack)
                Debug.LogWarning("[SongSing] 模型把跟唱约定、当前演唱或失败陈述误写成曲库演唱；已丢弃该标签并重新分流");
            songSing = null;
        }
        if (songMemory != null &&
            songMemory.Action == "remember" && string.IsNullOrWhiteSpace(songMemory.Title))
        {
            songMemory.Title = ExtractExplicitSongTitle(m_LastUserMsg);
        }
        bool heldForSongMemory = m_HoldSpeechForSongMemoryResult;
        bool heldForHumBack = m_HoldSpeechForHumBackResult;
        if (songMemory != null) BeginSongMemory(songMemory, heldForSongMemory);
        else if (m_HoldSpeechForSongMemoryResult)
        {
            m_SongMemoryAcknowledgementRequired = true;
            CompleteSongMemoryImmediately("没有找到可用于保存的最近歌声音频，本次未写入本机曲库。");
        }
        if (songSearch != null) BeginSongSearch(songSearch);
        if (songCatalog != null) BeginSongCatalogInspection(songCatalog);
        if (speakerManage != null) BeginSpeakerManage(speakerManage);
        //曾经在这里做「模型漏调 <song_sing/> 就按正则兜底」。已删除：8/9 实测触发 5 次、
        //成功 0 次，而正则从普通说话里编出来的"歌名"是「了呀」(出自"我刚才已经唱了呀")、
        //「点歌」、「完再唱」、以及整句歌词。判据 IsPlausibleUnquotedSongTitle 是一份
        //黑名单，不在名单里的一律放行，注定漏。
        //更要命的是它会连带扣住她那一轮的正常回复(已扣留 7 次)，查不到歌之后整轮无声。
        //而那几轮模型自己**没有**调用 <song_sing/>——它判断"这不是点歌请求"，判断是对的，
        //是正则在第二次猜并且猜错。要不要从曲库唱，交给她自己决定。
        if (m_PracticeEditFailedThisResponse && humBack != null)
        {
            humBack = null;
            NoteToolFailure("同一回复中的来源确认/素材修订没有成功，后续 hum_back 未执行；" +
                            "旧版本没有被冒充成修复结果。");
        }
        if (songSing != null)
        {
            // 持久曲库与“刚才一句”是不同音源；同一轮只执行一种真实歌唱动作。
            humBack = null;
            BeginSongSing(songSing);
        }
        m_HumBackTransactionRetainedThisRound = false;
        if (humBack != null) QueueHumBack(humBack);
        m_HoldSpeechForSongMemoryResult = false;
        m_HoldSpeechForHumBackResult = false;
        _response = StripAgentTagsForTTS(afterMem);
        m_TextBack.text = "";
        if (!m_ReadingUserInput)
        {
            ResolveSingingSpeech(ref _response);
            if (string.IsNullOrWhiteSpace(_response))
            {
                ClearUserTurnAwaitingReply("decision-held-for-validation");
                OnAgentRoundComplete();
                if (OnAISpeakDone != null) OnAISpeakDone();
                return;
            }
        }

        if (heldForSongMemory || heldForHumBack)
        {
            if (m_LogAgentLoop)
                Debug.Log(heldForSongMemory
                    ? "[SongMemory] 已扣留落盘前非流式回复，等待本机曲库结果后再确认"
                    : "[HumBack] 已扣留普通TTS歌词/舞台说明，只允许真实回哼音频发声");
            return;
        }

        
        Debug.Log("收到AI回复："+ _response);

        if (m_SubtitleOverlay != null)
            m_SubtitleOverlay.QueueTranslationChunk(
                subtitleGeneration, _response, true);

        //记录聊天
        m_ChatHistory.Add(_response);

        if (!m_IsVoiceMode||m_ChatSettings.m_TextToSpeech == null)
        {
            //开始逐个显示返回的文本
            StartTypeWords(_response);
            return;
        }

        //切句分段合成+队列播放，降低首音延迟
        // Use only the final sanitized text. Language spans may be reused if cleanup
        // changed no spoken words; a legacy text rewrite must not bypass that cleanup.
        string sourceSpeech = string.Concat(speech.ConvertAll(part => part.Text)).Trim();
        if (!string.Equals(sourceSpeech, _response.Trim(), StringComparison.Ordinal))
        {
            Debug.LogWarning("[LLM语言] 非流式正文经过额外清理，回退语种检测");
            speech = new List<SpeechText> { new SpeechText(_response) };
        }
        StartCoroutine(SpeakLanguageChunks(speech));
    }

#endregion

#region 语音输入
    /// <summary>
    /// 语音识别返回的文本是否直接发送至LLM
    /// </summary>
    [SerializeField] private bool m_AutoSend = true;

    [Header("流式倾听 — 可撤销的内部理解/候选回答")]
    [Tooltip("partial 只生成临时状态；最终转写到达前绝不发声、绝不写入历史。")]
    [SerializeField] private bool m_EnableSpeculativeListening = true;
    [SerializeField] private bool m_ShowStreamingTranscript = true;
    [Tooltip("收到新 partial 后等待多久再请求一次临时草稿，避免每个字都调用 LLM。")]
    [Range(0.2f, 1.5f)] [SerializeField] private float m_SpeculativeDebounceSeconds = 0.55f;
    [Tooltip("两次临时草稿请求的最小间隔。正式回复会抢占并撤销临时请求。\n" +
             "**这个值直接决定首 token 的慢尾巴。** 草稿是占 KV slot 的 LLM 请求，而 " +
             "--parallel 2 只有两个 slot，三路流(主对话/说话草稿/唱歌草稿)挤在一起；" +
             "某一路的前缀被踢掉时就掉进 LRU，实测 LRU 请求重算中位 9618 token、" +
             "而前缀命中的只有 523——差 18 倍，一场里 21 次首 token 中 >3s 的 4 次" +
             "全部且仅仅是 LRU。\n" +
             "1.4 时实测一段 15 秒长句发了 8 次草稿(ASR 每修订一次就重发一次)，" +
             "而全场 24 次草稿只有 5 次真被复用——命中率 18.5%。\n" +
             "拿真实草稿时间点模拟不同间隔: 1.4→22次(最多7次/轮), 2.0→18, 3.0→16, " +
             "4.0→15(最多3次/轮), 5.0→15。**4.0 之后收益就平了**，因为多数轮次本来" +
             "只有 1-2 次草稿，只有长句在密集重发。\n" +
             "注意不要按 confidence 设门槛：实测 0.90 的被放弃过三次、0.80 的进了" +
             "全部复用的那轮——放弃与否取决于用户后来说的话，是草稿生成时无法预知的。")]
    [Range(0.8f, 6f)] [SerializeField] private float m_SpeculativeMinRequestInterval = 4.0f;
    [Range(2, 20)] [SerializeField] private int m_SpeculativeMinTranscriptChars = 4;
    [Tooltip("最终转写与 partial 的编辑相似度低于此值时，丢弃临时草稿并正常重想。")]
    [Range(0.4f, 1f)] [SerializeField] private float m_SpeculativeReuseSimilarity = 0.72f;
    [SerializeField] private bool m_LogSpeculativeListening = true;

    [Header("歌唱流式预反应")]
    [Tooltip("唱歌期间持续生成可撤销的心里反应与安全短开场；音频只预合成，不会在EOU前播放。")]
    [SerializeField] private bool m_EnableSingingSpeculativeReaction = true;
    [Tooltip("歌唱 partial 稳定多久后更新一次内部反应。")]
    [Range(0.5f, 2.5f)] [SerializeField] private float m_SingingSpeculativeDebounceSeconds = 0.9f;
    [Tooltip("两次歌唱内部反应请求的最小间隔，避免歌词回滚时频繁请求。")]
    [Range(1.2f, 6f)] [SerializeField] private float m_SingingSpeculativeMinRequestInterval = 2.4f;
    [Tooltip("歌唱模式的声学退出：singing 概率连续低于此值这么多帧就退出。\n" +
             "不依赖 LLM、零额外请求，是 <歌唱→说话> 唯一不花钱的退出通道。\n" +
             "取值来自实测的一轮「说话→哼唱→说话」: 哼唱段(v31~v39) singing 最低 0.48，" +
             "回到说话后(v40~v47) 最高 0.52——**两者有重叠**，所以单帧判不了，必须连续帧。\n" +
             "同一序列上: 0.40/3帧 在 21.2s 退出(哼唱约 17s 结束)且哼唱段不误退；" +
             "0.45/3帧 退出时刻相同但离哼唱段最低值只剩 0.03 余量，换首歌就可能误伤；" +
             "0.30/3帧 则完全不退出。0.40 留了 0.08 余量。")]
    [Range(0.1f, 0.6f)] [SerializeField] private float m_SingingExitProbability = 0.40f;
    [Range(2, 8)] [SerializeField] private int m_SingingExitFrames = 3;
    [Tooltip("只有达到此置信度的安全短开场才会被静默预合成。")]
    [Range(0.4f, 0.95f)] [SerializeField] private float m_SingingBridgeMinConfidence = 0.62f;
    [Tooltip("预合成开场的最大字符数；过长候选会放弃，避免抢占正式回答。")]
    [Range(8, 48)] [SerializeField] private int m_SingingBridgeMaxChars = 28;
    [Tooltip("普通说话轮次是否也预合成草稿开场。关闭时 EOU 只播通用缓存语。\n" +
             "投机草稿本来就在用户说话期间生成好了，此前只有唱歌那条路会用它预合成，" +
             "说话轮次白白丢弃——实测 14 个草稿里 11 个置信度≥0.85 且内容切题，" +
             "EOU 时却播的是「なるほど……」这类四句通用语循环。")]
    [SerializeField] private bool m_EnableSpeechBridge = true;
    [Tooltip("说话轮次的预合成置信度门槛。0.85 实测把 9 个草稿拦下 5 个(0.70-0.80 那批" +
             "内容其实可用)，命中率过低；重复问题已由提示词侧解决，说错的代价降为" +
             "'开场略偏题'，正式回复紧接着会纠正，故放宽到 0.70。")]
    [Range(0.5f, 0.98f)] [SerializeField] private float m_SpeechBridgeMinConfidence = 0.70f;
    [Tooltip("临时心里话明确判断为普通说话时，达到此置信度即可否决预回唱。它只是否决依据，不会单独确认歌唱。")]
    [Range(0.65f, 0.98f)] [SerializeField] private float m_SpeculativeSpeechVetoConfidence = 0.82f;
    [Tooltip("临时心里话判断为歌唱达到此置信度时，可与流式声学证据一起请求最终歌唱分析；仍不能绕过最终声学确认。")]
    [Range(0.55f, 0.95f)] [SerializeField] private float m_SpeculativeSingingSupportConfidence = 0.70f;
    [Tooltip("达到此置信度的流式歌唱判断会作为复核证据交给正式角色 LLM；它不会单独触发动作。")]
    [Range(0.5f, 0.9f)] [SerializeField] private float m_ModeReviewSingingConfidence = 0.60f;
    [Tooltip("长旋律岛触发正式角色复核所需的最短秒数；只提供证据，不替角色判定。")]
    [Range(2f, 12f)] [SerializeField] private float m_ModeReviewMelodicIslandSeconds = 4f;
    [Tooltip("长旋律岛复核所需的最低音高稳定度。")]
    [Range(0.4f, 0.95f)] [SerializeField] private float m_ModeReviewPitchStability = 0.60f;
    [Tooltip("长旋律岛占有效内容的最低比例。")]
    [Range(0.5f, 1f)] [SerializeField] private float m_ModeReviewMelodicIslandRatio = 0.80f;

    private string m_StreamingTranscript = "";
    //最近一次收到流式 partial 的时刻。自主发言的闸用它判断"用户还在说话"。
    private float m_LastStreamingPartialRealtime = -999f;
    //partial 每 0.85~0.95s 来一帧(见日志 audio= 的步长)，所以 1.5s 足够跨过一帧间隔，
    //又不会在用户真的说完之后压制太久。
    private const float k_UserSpeakingHoldSeconds = 1.5f;
    //上一次回哼有没有垫过场。硬底线：不允许连着两次都说话。
    private bool m_HumBackPreludeSpokenLastTime = false;
    //最近垫过的几句。垫场是裸请求、不带会话历史，不把这些回传她会重复同一句。
    private readonly List<string> m_RecentHumBackPreludes = new List<string>();
    private string m_LastDraftTranscript = "";
    private int m_StreamingTranscriptVersion = 0;
    private int m_SpeculativeRequestVersion = 0;
    private float m_LastSpeculativeRequestTime = -999f;
    private Coroutine m_SpeculativeDraftCoroutine;
    private SpeculativeDraft m_SpeculativeDraft;
    private bool m_SpeculativeRequestInFlight = false;
    private bool m_TurnBoundaryRequestInFlight = false;
    private int m_TurnBoundaryRequestVersion = 0;
    private float m_TurnBoundaryRequestStarted = -1f;
    private string m_LastBoundaryDecisionReason = "";
    private string m_StalledTurnAction = "";
    private bool m_StreamingTurnIsSinging = false;
    private float m_StreamingSingingProbability = 0f;
    private float m_StreamingPitchStability = 0f;
    private int m_StreamingSingingEvidenceFrames = 0;
    private int m_StreamingSingingConsecutiveFrames = 0;
    private float m_StreamingSingingProbabilitySum = 0f;
    private int m_StreamingLastSingingEvidenceAudioMs = -1;
    private int m_StreamingLatestAudioMs = 0;
    private int m_StreamingSingingCandidateStartAudioMs = -1;
    private int m_StreamingSingingOnsetAudioMs = -1;
    //最近一次草稿给出的 observed_mode 判定，独立于 m_SpeculativeDraft 保存。
    //每帧重新评估、不粘——"说一半再哼唱"要靠这个：前半段判 speech 退出歌唱模式，
    //后半段声学证据出现时仍能切回去。粘住就会把后半段的哼唱彻底忽略。
    private string m_LastObservedMode = "";
    private float m_LastObservedModeConfidence = 0f;
    private string m_LastObservedModeTranscript = "";
    private string m_LastSpeechVetoLogKey = "";
    //本轮曾由流式角色判断为普通说话的前缀。后续歌词 partial 若改判 singing，
    //也不能抹掉“开头确实先说过话”这一事实；只在真实轮次结束时重置。
    private bool m_StreamingSemanticSpokenLeadInObserved = false;
    //同一真实用户轮里，流式角色 LLM 只要曾以中等以上把某一段判断为 singing，
    //后续“唱完又说话”也不能抹掉这份语义证据。它不直接确认来源，只确保进入融合/quarantine。
    private bool m_StreamingSemanticSingingObservedThisTurn = false;
    private float m_StreamingSemanticSingingConfidenceThisTurn = 0f;
    private string m_StreamingSemanticSingingTranscriptThisTurn = "";
    private int m_StreamingSingingLowFrames = 0;
    private bool m_StreamingSingingExitDetected = false;
    private string m_StreamingSingingEvidence = "";
    private float m_LastSingingSpeculativeRequestTime = -999f;
    private bool m_EouCognitiveSpeechVeto = false;
    private string m_StalledTurnDecisionNote = "";
    [Tooltip("把声学、旋律岛和流式语义证据交给本次正式角色 LLM 复核。" +
             "不会额外串行调用分类器，因此不增加角色回复的首音等待。")]
    [SerializeField] private bool m_EnableFinalModeCheck = true;
    //保留给旧场景/回归兼容；正式模态判断现在由收到证据帧的角色 LLM 在同一请求中完成，
    //不再在 DealingTextCallback 前串行等待一个裸文本分类器。
    private string m_FinalModeVerdict = "";
    //声学 uncertain 或语义/声学冲突时的安全态：不自动回哼、不替用户定案，
    //暂存可用素材并把证据交给角色。字段名沿用 soft downgrade，避免遗漏既有安全闸。
    private bool m_FinalModeSoftDowngrade = false;
    //强证据冲突或双方都 uncertain 时，要不要建议她自然确认。只在本轮内有效，
    //构造完 _msg 就清掉，不跨轮。
    private bool m_PendingSingingConfirmation = false;
    //待确认片段的口头确认由辅助 LLM 判定。回调会重新进入 SendDataInternal，
    //这一位只跳过紧接着的那一次，避免同一句无限重复发起语义判定。
    //语义确认/否认结果只随当前用户轮出现一次，作为事实给正式角色 LLM；
    //它不替角色决定是否回唱，否认时也不自动删除素材。
    private string m_PracticeSemanticResolutionNote = "";
    private bool m_EouCognitiveSingingSupport = false;
    private int m_SingingBridgeGeneration = 0;
    private bool m_SingingBridgeTtsInFlight = false;
    private AudioClip m_PreparedSingingBridgeClip;
    private AudioClip m_DeferredPreparedClipToDestroy;
    private string m_PreparedSingingBridgeText = "";
    private string m_PreparedSingingBridgeLanguage;
    private string m_PendingBridgeLanguage;
    //预合成开场对应的 partial。最终 ASR 到达后必须用它做一致度提交，不能只看开场文本。
    private string m_PreparedBridgeSourceTranscript = "";
    //预合成时角色自己给出的模态判断。最终确认可以推翻它来生成正式回复，
    //但不能让一条基于“用户还在说话/还没开始唱”的旧开场不可撤销地播出去。
    private string m_PreparedBridgeObservedMode = "";
    //本轮真正出过声的那句开场，在播放时留存，只在轮次重置时清。
    private string m_SpokenBridgeTextThisTurn = "";
    private float m_PreparedSingingBridgeConfidence = 0f;
    private bool m_PreparedSingingBridgePlayedThisTurn = false;
    //预合成的这段开场是给唱歌轮次还是说话轮次准备的。两者不能互用：
    //歌唱开场("うん、ちゃんと聴いていたわ")接在普通提问后面会很怪，反之亦然。
    private bool m_PreparedBridgeIsSinging = false;
    //正在合成中的那一段。与"已就绪"分开存放，这样刷新草稿时旧的仍然可播。
    private string m_PendingBridgeText = "";
    private float m_PendingBridgeConfidence = 0f;
    private bool m_PendingBridgeIsSinging = false;
    private string m_PendingBridgeObservedMode = "";
    /// <summary>
    /// 语音输入的按钮
    /// </summary>
    [SerializeField] private Button m_VoiceInputBotton;
    /// <summary>
    /// 录音按钮的文本
    /// </summary>
    [SerializeField]private Text m_VoiceBottonText;
    /// <summary>
    /// 录音的提示信息
    /// </summary>
    [SerializeField] private Text m_RecordTips;
    /// <summary>
    /// 语音输入处理类
    /// </summary>
    [SerializeField] private VoiceInputs m_VoiceInputs;
    /// <summary>
    /// 注册按钮事件
    /// </summary>
    private void RegistButtonEvent()
    {
        if (m_VoiceInputBotton == null || m_VoiceInputBotton.GetComponent<EventTrigger>())
            return;

        EventTrigger _trigger = m_VoiceInputBotton.gameObject.AddComponent<EventTrigger>();

        //添加按钮按下的事件
        EventTrigger.Entry _pointDown_entry = new EventTrigger.Entry();
        _pointDown_entry.eventID = EventTriggerType.PointerDown;
        _pointDown_entry.callback = new EventTrigger.TriggerEvent();

        //添加按钮松开事件
        EventTrigger.Entry _pointUp_entry = new EventTrigger.Entry();
        _pointUp_entry.eventID = EventTriggerType.PointerUp;
        _pointUp_entry.callback = new EventTrigger.TriggerEvent();

        //添加委托事件
        _pointDown_entry.callback.AddListener(delegate { StartRecord(); });
        _pointUp_entry.callback.AddListener(delegate { StopRecord(); });

        _trigger.triggers.Add(_pointDown_entry);
        _trigger.triggers.Add(_pointUp_entry);
    }

    /// <summary>
    /// 开始录制
    /// </summary>
    public void StartRecord()
    {
        m_VoiceBottonText.text = "正在录音中..."; 
        m_VoiceInputs.StartRecordAudio();
    }
    /// <summary>
    /// 结束录制
    /// </summary>
    public void StopRecord()
    {
        m_VoiceBottonText.text = "按住按钮，开始录音"; 
        m_RecordTips.text = "录音结束，正在识别...";
        m_VoiceInputs.StopRecordAudio(AcceptClip);
    }

    /// <summary>
    /// 处理录制的音频数据
    /// </summary>
    /// <param name="_data"></param>
    private void AcceptData(byte[] _data)
    {
        if (m_ChatSettings.m_SpeechToText == null)
            return;

        m_ChatSettings.m_SpeechToText.SpeechToText(_data, DealingTextCallback);
    }

    /// <summary>
    /// 处理录制的音频数据。public供RTSpeechHandler在实时对话路径上直接送clip。
    /// </summary>
    /// <param name="_data"></param>
    public void AcceptClip(AudioClip _audioClip)
    {
        // Button-recorded clips are independent inputs, not continuations of an
        // earlier realtime microphone capture whose context is still cached.
        (m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText)?.BeginLiveRecordingCandidateSession();
        AcceptClip(_audioClip, true);
    }

    public void AcceptClip(AudioClip _audioClip, bool allowSpeakerLearning)
    {
        if (m_ChatSettings.m_SpeechToText == null)
            return;

        // “已经约好跟唱”只是预期，不得单独放宽最终歌唱判定。流式心里话若明确
        // 判断为普通说话，就在提交最终ASR前锁存否决；若判断为歌唱，则只能与
        // 流式声学证据共同请求更细的最终分析，不能单独触发播放。
        m_EouCognitiveSpeechVeto = HasStrongSpeculativeSpeechVeto();
        m_EouCognitiveSingingSupport = !m_EouCognitiveSpeechVeto &&
            HasStrongSpeculativeSingingSupport();
        bool expectedSingingContext = HasExpectedUserSingingAcousticContext();
        bool expectObservedSinging = expectedSingingContext &&
            !m_EouCognitiveSpeechVeto &&
            (m_StreamingTurnIsSinging || m_EouCognitiveSingingSupport);

        // The complete clip is available at EOU before final ASR starts.  If its
        // beginning was already voice-converted while the user was singing, stage
        // the complete continuation now so SVC and final recognition run in parallel.
        TryStageFastHumBackAtEou(_audioClip);

        m_FinalAsrRequestsInFlight++;
        bool streamingExitAtSubmission = m_StreamingSingingExitDetected;
        int inputRevision = m_InputAudioRevision;
        bool completed = false;
        Action<string> onFinalAsr = text =>
        {
            if (completed) return;
            completed = true;
            m_FinalAsrRequestsInFlight = Mathf.Max(0, m_FinalAsrRequestsInFlight - 1);
            if (inputRevision != m_InputAudioRevision) return;
            DealingTextCallback(text, streamingExitAtSubmission);
        };
        SenseVoiceSpeechToText senseVoice = m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText;
        if (senseVoice != null)
        {
            bool semanticSpokenLeadIn =
                GetStreamingSingingOnsetSeconds() >= 0f &&
                (m_StreamingSemanticSpokenLeadInObserved ||
                 (NormalizeObservedMode(m_LastObservedMode) == "speech" &&
                  Mathf.Clamp01(m_LastObservedModeConfidence) >= 0.70f));
            senseVoice.SpeechToText(
                _audioClip,
                onFinalAsr,
                allowSpeakerLearning,
                expectObservedSinging,
                GetStreamingSingingOnsetSeconds(),
                GetStreamingObservedSeconds(),
                streamingExitAtSubmission,
                semanticSpokenLeadIn);
        }
        else
            m_ChatSettings.m_SpeechToText.SpeechToText(_audioClip, onFinalAsr);
    }

    /// <summary>
    /// RTSpeechHandler 已经有一份覆盖全部有效内容的预测 ASR 在飞时，先把它登记成
    /// 正式工作，避免 Agent Loop 在等待期间误以为系统空闲。clip 只用于并行准备可撤销
    /// 的快速回唱；预测失败后仍可回落到 AcceptClip。
    /// </summary>
    public bool BeginDeferredFinalAsr(AudioClip completeClip)
    {
        m_FinalAsrRequestsInFlight++;
        TryStageFastHumBackAtEou(completeClip);
        return m_StreamingSingingExitDetected;
    }

    public void AcceptDeferredFinalAsrText(string text, bool streamingExitAtSubmission)
    {
        m_FinalAsrRequestsInFlight = Mathf.Max(0, m_FinalAsrRequestsInFlight - 1);
        DealingTextCallback(text, streamingExitAtSubmission);
    }

    public void FallbackDeferredFinalAsrToClip(
        AudioClip completeClip,
        bool allowSpeakerLearning)
    {
        m_FinalAsrRequestsInFlight = Mathf.Max(0, m_FinalAsrRequestsInFlight - 1);
        AcceptClip(completeClip, allowSpeakerLearning);
    }

    /// <summary>
    /// Tentative-EOU路径专用：把clip送ASR做"预测识别"，但不进LLM链路——
    /// callback里RTSpeechHandler会看尾部是否说完，再决定走AcceptText还是丢弃。
    /// 不调用DealingTextCallback——避免预测命中前就提前刷UI/SendData。
    /// </summary>
    public void PreviewASR(
        AudioClip _audioClip,
        System.Action<string> _callback,
        bool allowSpeakerLearning = true,
        System.Action<string> onRawTranscript = null)
    {
        if (m_ChatSettings == null || m_ChatSettings.m_SpeechToText == null)
        {
            if (_callback != null) _callback("");
            return;
        }
        SenseVoiceSpeechToText senseVoice = m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText;
        if (senseVoice != null)
        {
            bool cognitiveSpeechVeto = HasStrongSpeculativeSpeechVeto();
            bool expectObservedSinging = HasExpectedUserSingingAcousticContext() &&
                !cognitiveSpeechVeto &&
                (m_StreamingTurnIsSinging || HasStrongSpeculativeSingingSupport());
            bool semanticSpokenLeadIn =
                GetStreamingSingingOnsetSeconds() >= 0f &&
                (m_StreamingSemanticSpokenLeadInObserved ||
                 (NormalizeObservedMode(m_LastObservedMode) == "speech" &&
                  Mathf.Clamp01(m_LastObservedModeConfidence) >= 0.70f));
            senseVoice.SpeechToText(
                _audioClip,
                text =>
                {
                    // Capture from the exact provider/result before another analysis
                    // can change LastText; callers bind it to their snapshot generation.
                    onRawTranscript?.Invoke(senseVoice.LastText ?? "");
                    _callback?.Invoke(text);
                },
                allowSpeakerLearning,
                expectObservedSinging,
                GetStreamingSingingOnsetSeconds(),
                GetStreamingObservedSeconds(),
                m_StreamingSingingExitDetected,
                semanticSpokenLeadIn, true);
        }
        else
            m_ChatSettings.m_SpeechToText.SpeechToText(_audioClip, text =>
            {
                onRawTranscript?.Invoke(text ?? "");
                _callback?.Invoke(text);
            });
    }

    /// <summary>
    /// RTSpeechHandler 在用户仍在说话时推送。这里只更新临时 UI/状态并调度无历史副作用的
    /// ephemeral LLM 请求；不会调用 SendData，也不会启动 TTS。
    /// </summary>
    public void UpdateStreamingTranscript(SenseVoiceSpeechToText.StreamingTranscript transcript)
    {
        if (transcript == null) return;

        //自主发言的闸要用它：只要还在收 partial，用户就还在说，这时不能开口。
        m_LastStreamingPartialRealtime = Time.realtimeSinceStartup;
        m_StreamingLatestAudioMs = Mathf.Max(m_StreamingLatestAudioMs, transcript.AudioMs);

        // 单个早期 partial 很容易把有抑扬的普通问句误判成歌唱。至少要求两个连续
        // 高置信帧，或三帧以上的一致证据，才把本轮锁定为歌唱直到 EOU。
        if (transcript.AudioMs > m_StreamingLastSingingEvidenceAudioMs)
        {
            m_StreamingLastSingingEvidenceAudioMs = transcript.AudioMs;
            m_StreamingSingingEvidenceFrames++;
            m_StreamingSingingProbabilitySum += Mathf.Clamp01(transcript.SingingProbability);
            bool normalStrongFrame = transcript.IsSinging || transcript.SingingProbability >= 0.60f ||
                (transcript.SingingProbability >= 0.54f && transcript.PitchStability >= 0.40f);
            // When the user explicitly armed continuous sing-along, accept a lower
            // probability only if pitch remains stable for several consecutive frames.
            // Normal conversation keeps the original thresholds above.
            bool expectedMelodicFrame = HasExpectedUserSingingAcousticContext() &&
                transcript.SingingProbability >= 0.32f &&
                transcript.PitchStability >= 0.50f;
            bool strongFrame = normalStrongFrame || expectedMelodicFrame;
            if (strongFrame)
            {
                if (m_StreamingSingingConsecutiveFrames == 0)
                    m_StreamingSingingCandidateStartAudioMs = transcript.AudioMs;
                m_StreamingSingingConsecutiveFrames++;
            }
            else
            {
                m_StreamingSingingConsecutiveFrames = 0;
                m_StreamingSingingCandidateStartAudioMs = -1;
            }
        }
        float averageSingingProbability = m_StreamingSingingEvidenceFrames > 0
            ? m_StreamingSingingProbabilitySum / m_StreamingSingingEvidenceFrames
            : 0f;
        bool normalStableSingingEvidence =
            (m_StreamingSingingConsecutiveFrames >= 2 &&
             (transcript.IsSinging || transcript.SingingProbability >= 0.54f)) ||
            (m_StreamingSingingEvidenceFrames >= 3 && averageSingingProbability >= 0.58f &&
             transcript.SingingProbability >= 0.48f && transcript.PitchStability >= 0.35f);
        bool expectedStableSingingEvidence = HasExpectedUserSingingAcousticContext() &&
            m_StreamingSingingConsecutiveFrames >= 3 &&
            transcript.SingingProbability >= 0.32f &&
            transcript.PitchStability >= 0.50f;
        bool stableSingingEvidence = normalStableSingingEvidence ||
            expectedStableSingingEvidence;
        bool repeatedSemanticSpeechVeto = ShouldSuppressRepeatedMelodicEntry(
            m_LastObservedMode,
            m_LastObservedModeConfidence,
            m_SpeculativeSpeechVetoConfidence,
            m_LastObservedModeTranscript,
            transcript.Text);
        if (stableSingingEvidence && repeatedSemanticSpeechVeto &&
            !m_StreamingTurnIsSinging)
        {
            stableSingingEvidence = false;
            string vetoKey = NormalizeTranscript(m_LastObservedModeTranscript);
            if (m_LogSpeculativeListening && vetoKey != m_LastSpeechVetoLogKey)
                Debug.Log("[歌唱流式倾听] 相同文字已由角色高置信度判为说话；" +
                          "保持声学证据供持续背景复核，但不再逐帧重进歌唱状态");
            m_LastSpeechVetoLogKey = vetoKey;
        }
        if (stableSingingEvidence && m_StreamingSingingOnsetAudioMs < 0)
        {
            m_StreamingSingingOnsetAudioMs = m_StreamingSingingCandidateStartAudioMs >= 0
                ? m_StreamingSingingCandidateStartAudioMs
                : transcript.AudioMs;
            if (m_LogSpeculativeListening)
                Debug.Log($"[歌唱流式倾听] 起唱锚点={m_StreamingSingingOnsetAudioMs}ms " +
                          $"confirmedAt={transcript.AudioMs}ms " +
                          $"mode={(expectedStableSingingEvidence && !normalStableSingingEvidence ? "expected-relaxed" : "normal")}");
        }
        bool singing = m_StreamingTurnIsSinging || stableSingingEvidence;

        //★ 声学退出：唱完之后回到说话，singing 概率会持续掉下来。
        //  实测「说话→哼唱→说话」那一轮，声学侧其实**察觉到了**——概率从 0.66 掉到 0.12，
        //  但 m_StreamingTurnIsSinging 是粘的、声学侧只有进入逻辑没有退出逻辑，
        //  于是后半段的自然说话被当成歌词复读了出来(用户原话:"你把那段自然说话也复读出来了")。
        //  这条通道不依赖 LLM、零额外请求，是歌唱→说话唯一不花钱的退出方式。
        if (m_StreamingTurnIsSinging)
        {
            if (transcript.SingingProbability < m_SingingExitProbability) m_StreamingSingingLowFrames++;
            else m_StreamingSingingLowFrames = 0;

            if (m_StreamingSingingLowFrames >= m_SingingExitFrames)
            {
                if (m_LogSpeculativeListening)
                    Debug.Log($"[歌唱流式倾听] 声学退出：singing 连续 {m_StreamingSingingLowFrames} 帧 " +
                              $"低于 {m_SingingExitProbability:F2}(当前 {transcript.SingingProbability:F2}, " +
                              $"pitch {transcript.PitchStability:F2})，退出歌唱模式");
                m_StreamingTurnIsSinging = false;
                m_StreamingSingingOnsetAudioMs = -1;
                m_StreamingSingingConsecutiveFrames = 0;
                m_StreamingSingingCandidateStartAudioMs = -1;
                m_StreamingSingingLowFrames = 0;
                singing = false;
            }
        }
        else m_StreamingSingingLowFrames = 0;

        //★ 她自己判定"这是说话"时退出歌唱模式。每帧重新评估，**不粘**——
        //  "说一半再哼唱"就靠这个：前半段被判 speech 退出，后半段声学证据出现时
        //  stableSingingEvidence 会重新为真、再切回去。粘住会把后半段的哼唱彻底忽略。
        //  起唱锚点(m_StreamingSingingCandidateStartAudioMs)本来就是为这个场景设计的，
        //  它记的是"哼唱从第几毫秒开始"，退出时归零、切回时重新定位。
        if (singing && HasStrongSpeechModeJudgment())
        {
            //日志不能门控在 m_StreamingTurnIsSinging 上——它是在
            //UpdateStreamingSingingReaction **内部**才置 true 的，而这段检查在调用它之前，
            //所以拦在"切进去之前"的那些命中会一条都打不出来(实测 0 次，只能靠
            //"起唱锚点打印了两次"反推出它其实生效了)。改成无条件打印，并区分两种情形。
            string vetoKey = NormalizeTranscript(m_LastObservedModeTranscript);
            if (m_LogSpeculativeListening && vetoKey != m_LastSpeechVetoLogKey)
            {
                float sim = (!string.IsNullOrWhiteSpace(m_LastObservedModeTranscript) &&
                             !string.IsNullOrWhiteSpace(m_StreamingTranscript))
                    ? TranscriptContinuity(m_LastObservedModeTranscript, m_StreamingTranscript)
                    : -1f;
                Debug.Log($"[歌唱流式倾听] 她判定这是说话({m_LastObservedMode}/" +
                          $"{m_LastObservedModeConfidence:F2}, 门槛 {m_SpeculativeSpeechVetoConfidence:F2})，" +
                          $"{(m_StreamingTurnIsSinging ? "退出已进入的歌唱模式" : "拦在切进歌唱之前")} " +
                          $"(声学: singing={transcript.SingingProbability:F2} " +
                          $"pitch={transcript.PitchStability:F2}; 转写相似度={sim:F2}; " +
                          $"判定源=\"{TruncateForFrame(m_LastObservedModeTranscript, 24)}\")");
            }
            m_LastSpeechVetoLogKey = vetoKey;
            m_StreamingTurnIsSinging = false;
            m_StreamingSingingOnsetAudioMs = -1;
            m_StreamingSingingConsecutiveFrames = 0;
            m_StreamingSingingCandidateStartAudioMs = -1;
            singing = false;
        }
        if (singing)
        {
            if (stableSingingEvidence && OnStableUserSingingObserved != null)
            {
                OnStableUserSingingObserved(
                    transcript.SingingProbability,
                    transcript.PitchStability,
                    transcript.AudioMs);
            }
            UpdateStreamingSingingReaction(transcript);
            return;
        }
        if (!m_EnableSpeculativeListening || string.IsNullOrWhiteSpace(transcript.Text)) return;

        string text = transcript.Text.Trim();
        if (text == m_StreamingTranscript) return;
        string previousText = m_StreamingTranscript;
        m_StreamingTranscript = text;
        m_StreamingTranscriptVersion++;

        if (m_ShowStreamingTranscript && m_RecordTips != null)
            m_RecordTips.text = text + " …";

        if (m_ChatSettings == null || m_ChatSettings.m_ChatModel == null ||
            text.Length < Mathf.Max(2, m_SpeculativeMinTranscriptChars)) return;

        if (m_TurnBoundaryRequestInFlight)
        {
            if (m_LogSpeculativeListening)
                Debug.Log($"[流式倾听] v{m_StreamingTranscriptVersion} audio={transcript.AudioMs}ms " +
                          "边界复核进行中；只更新文字证据，不启动低优先级草稿");
            return;
        }

        // 普通“末尾继续增长”不取消在飞请求，否则约 1 秒一次的 partial 会让草稿永远完不成。
        // 只有 ASR 明确回滚且新旧含义差异很大时才撤销；EOU 则始终立即撤销。
        if (m_SpeculativeRequestInFlight && transcript.Revision &&
            TranscriptContinuity(previousText, text) < 0.55f)
        {
            m_SpeculativeRequestVersion++;
            m_SpeculativeRequestInFlight = false;
            m_ChatSettings.m_ChatModel.CancelEphemeralMsg();
        }
        if (m_SpeculativeDraftCoroutine != null)
            StopCoroutine(m_SpeculativeDraftCoroutine);
        int version = m_StreamingTranscriptVersion;
        m_SpeculativeDraftCoroutine = StartCoroutine(RequestSpeculativeDraftAfterDelay(version, text));

        if (m_LogSpeculativeListening)
            Debug.Log($"[流式倾听] v{version} audio={transcript.AudioMs}ms revision={transcript.Revision}: \"{text}\"");
    }

    private static bool ShouldSuppressRepeatedMelodicEntry(
        string observedMode,
        float modeConfidence,
        float speechThreshold,
        string judgedTranscript,
        string currentTranscript)
    {
        if (NormalizeObservedMode(observedMode) != "speech" ||
            modeConfidence < speechThreshold)
            return false;
        string judged = NormalizeTranscript(judgedTranscript);
        string current = NormalizeTranscript(currentTranscript);
        if (judged.Length == 0 || current.Length == 0) return false;
        if (string.Equals(judged, current, StringComparison.Ordinal)) return true;
        int delta = Mathf.Abs(judged.Length - current.Length);
        return delta <= 1 &&
            (judged.StartsWith(current, StringComparison.Ordinal) ||
             current.StartsWith(judged, StringComparison.Ordinal));
    }

    /// <summary>
    /// 物理声音持续存在、但文字已经停止增长时使用独立的高优先级 LLM 通道判断轮次边界。
    /// 这不是定时强切：continue / take_turn / ask_user / complete 由角色结合文字和声学事实选择。
    /// </summary>
    public bool SupportsRoleTurnBoundary => m_ChatSettings != null &&
        m_ChatSettings.m_ChatModel != null && m_ChatSettings.m_ChatModel.SupportsTurnBoundaryMessages;

    public bool RequestStalledTurnReview(
        string transcript,
        float unchangedSeconds,
        int audioMs,
        float currentRms,
        float eouThreshold,
        float ambientFloor,
        bool ambientCalibrated,
        float latestSingingProbability,
        float peakSingingProbability,
        float recentSingingProbability,
        float singingProbabilityTrend,
        float pitchStability,
        int priorContinueDecisions,
        string lastDecisionStatus,
        float lastDecisionConfidence,
        float dropDb,
        float dropHeldSeconds,
        float asrAgeSeconds,
        int unchangedAudioMs,
        string trigger,
        int failedReviews,
        string activityEvidence = null)
    {
        if (m_ChatSettings == null || m_ChatSettings.m_ChatModel == null ||
            !m_ChatSettings.m_ChatModel.SupportsTurnBoundaryMessages ||
            string.IsNullOrWhiteSpace(transcript))
            return false;
        //连 provider 没有回调的异常路径也能重试；这个计时器只管理请求，不截断用户音频。
        if (m_TurnBoundaryRequestInFlight)
        {
            if (Time.realtimeSinceStartup - m_TurnBoundaryRequestStarted < 7f) return false;
            CancelTurnBoundaryReview();
        }

        //边界复核优先于可撤销的说话/歌唱预反应。先撤销低优先级草稿，随后用独立
        //request generation 发起；新流式帧只能更新证据，不能取消或占住这条请求。
        CancelSpeculativeRequestOnly();
        int requestVersion = ++m_TurnBoundaryRequestVersion;
        m_TurnBoundaryRequestInFlight = true;
        m_TurnBoundaryRequestStarted = Time.realtimeSinceStartup;
        string sourceTranscript = transcript.Trim();
        string trendDescription = singingProbabilityTrend > 0.015f
            ? "上升"
            : singingProbabilityTrend < -0.015f
                ? "下降"
                : "近似持平";
        string decisionHistory = priorContinueDecisions > 0
            ? $"此前已连续选择 continue={priorContinueDecisions} 次；最近一次=" +
              $"{NormalizeTurnStatus(lastDecisionStatus)}/{Mathf.Clamp01(lastDecisionConfidence):F2}。"
            : "此前没有对同一稳定内容选择过 continue。";
        string prompt =
            "[内部轮次边界判断；不要正式回复、不要调用工具、不要写入记忆]\n" +
            "这是轮次时机复核，不是催你回应。麦克风可能仍有声音，也可能音量已经下降。" +
            "持续声音可能是用户继续说话/哼唱，也可能是外放音乐或环境声。请由你结合语义与证据判断，" +
            "程序不会用关键词替你决定。\n" +
            $"本轮当前累计转写=\"{sourceTranscript}\"\n" +
            $"当前互动证据：持续轮唱约定={HasActiveSingAlongRequest()}; 本轮曾识别歌唱={m_StreamingTurnIsSinging}。" +
            "这是历史模态，不代表此刻仍在发声。旧对话里的确认或结束不能冒充本轮结束。\n" +
            (activityEvidence ?? "近期活动字段暂不可用，不要从旧歌词推断当前仍有新发声。") + "\n" +
            $"文字未变化={unchangedSeconds:F1}s; 已听音频={audioMs}ms; " +
            $"当前RMS={currentRms:F4}; EOU阈值={eouThreshold:F4}; " +
            $"环境基线={(ambientCalibrated ? ambientFloor.ToString("F4") : "未完成校准")}; " +
            $"最新歌唱概率={latestSingingProbability:F2}; " +
            $"最近短期均值={recentSingingProbability:F2}; 本轮峰值={peakSingingProbability:F2}; " +
            $"最近趋势={trendDescription}({singingProbabilityTrend:+0.000;-0.000;0.000}/帧); " +
            $"最新音高稳定度={pitchStability:F2}\n" +
            $"触发={trigger}; 相对电平下降={dropDb:F1}dB; 低电平保持={dropHeldSeconds:F2}s; " +
            $"最新ASR回包年龄={asrAgeSeconds:F2}s; 文字稳定后ASR已覆盖新增音频={unchangedAudioMs}ms; " +
            $"此前无效复核={failedReviews}次。相对dB来自麦克风PCM，不是物理声压级。\n" +
            GetTurnBoundaryThought(sourceTranscript) + "\n" +
            decisionHistory + "\n" +
            BuildTurnBoundaryGuidance() +
            "turn_state 单独写你认为对方当前表达是 open(仍未完)、closed(已有交接点) 还是 uncertain。" +
            "它必须与你选择的动作一致：open 时若故意接话用 interrupt_user，否则 continue；" +
            "ask_user 用于已有交接点但内容/来源仍需确认，不可一边写 open 一边伪装成自然交接。" +
            "只输出以下6字段JSON；reason一句简短理由，不要长篇分析、完整回答或工具标签：" +
            "{\"action\":\"continue|take_turn|ask_user|complete|interrupt_user\",\"confidence\":0.0," +
            "\"mode\":\"speech|singing|uncertain\",\"source\":\"user|background|uncertain\"," +
            "\"turn_state\":\"open|closed|uncertain\",\"reason\":\"简短理由\"}";

        m_ChatSettings.m_ChatModel.PostTurnBoundaryMsg(prompt, response =>
        {
            if (requestVersion != m_TurnBoundaryRequestVersion) return;
            m_TurnBoundaryRequestInFlight = false;
            if (!TurnBoundaryDecision.TryParse(response, out TurnBoundaryDecision parsed))
            {
                if (m_LogSpeculativeListening)
                    Debug.LogWarning("[Semantic EOU] 边界协议无效；已释放请求并允许重试。raw=" +
                                     TruncateForFrame(response ?? "<empty>", 700));
                if (OnStalledUserTurnDecision != null)
                    OnStalledUserTurnDecision(
                        "", 0f, "uncertain", 0f, "uncertain",
                        sourceTranscript, audioMs);
                return;
            }

            //轮次选择不是“当前声源属于用户歌声”的确认，不再反向改写模态锁。
            m_LastBoundaryDecisionReason = parsed.reason;

            if (m_LogSpeculativeListening)
                Debug.Log($"[Semantic EOU] LLM={parsed.action}/{parsed.confidence:F2} " +
                          $"mode={parsed.mode} source={parsed.source} turn={parsed.turn_state} " +
                          $"reason={parsed.reason} latest/mean/peak=" +
                          $"{latestSingingProbability:F2}/{recentSingingProbability:F2}/" +
                          $"{peakSingingProbability:F2} trend={singingProbabilityTrend:+0.000;-0.000;0.000} " +
                          $"priorContinue={priorContinueDecisions} " +
                          $"text=\"{TruncateForFrame(sourceTranscript, 48)}\"");
            if (OnStalledUserTurnDecision != null)
                OnStalledUserTurnDecision(
                    parsed.action,
                    parsed.confidence,
                    parsed.mode,
                    0f,
                    parsed.source,
                    sourceTranscript,
                    audioMs);
        });
        return true;
    }

    public bool HasTurnBoundaryThought(string transcript)
    {
        return !string.IsNullOrWhiteSpace(m_DeferredAutonomyIntent) ||
            (m_SpeculativeDraft != null && !string.IsNullOrWhiteSpace(m_SpeculativeDraft.draft) &&
             TranscriptSimilarity(m_SpeculativeDraft.sourceTranscript, transcript) >= m_SpeculativeReuseSimilarity);
    }

    private static string BuildTurnBoundaryGuidance()
    {
        return "先区分‘有内容可回应’和‘现在适合接话’。识别出歌词、想安慰或已有草稿，本身都不是抢过话轮的理由。" +
            "continue=我选择继续倾听，包括给未完句、思考、换气或乐句间停顿留空间；不需要证明对方此刻仍在发声。" +
            "take_turn=我认为已有自然交接点；ask_user=我选择在交接点自然询问不确定处；" +
            "interrupt_user=我知道对方可能仍在说/唱，但有具体理由主动打断（包括需要立即询问），不是误以为对方已结束；" +
            "complete=我确信本轮表达结束。这些选择都有效，不确定时仍可询问、等待或采用自己的判断。\n" +
            "一句歌词结束不等于整段演唱结束，词义像陈述/邀请也可能仍是歌词；轮唱约定不是每个换气点都回唱。" +
            "例如话语停在‘不过…’或‘我那时候呢…’，即使已有回答也可以继续听；" +
            "旋律仍在发展、歌词前后相接时可等下一乐句；多次新音频确认文字不再推进且声源不明时可询问；" +
            "完整问题后出现自然交接时可接话。这是语境示例，不是关键词规则。\n" +
            "文字未变可能只是下一次ASR尚未返回；新增音频覆盖=0尤其不能当作已停止。" +
            "音量下降、旋律峰值、最新低概率都不是独立结论；结合近期证据，不要只引用旧轮‘用户已确认’。" +
            "同样，整窗歌唱分数高也不能证明声音来自用户、或旋律还在继续；低信噪比时它可能只是周期性底噪。" +
            "累计歌词没增长、近期有效声音也消退时，可以自然接话或询问；不能仅凭旧歌词写‘旋律仍在发展’。" +
            "已有想法可保留到稍后，不必现在说。reason简短说明时机/为何等待，而不是只说明想答什么。" +
            "程序会保存本轮录音并校验最终ASR后回应；你选择接话可能结束这一段采集，因此也要考虑对方是否在继续同一段表达。\n";
    }

    private string GetTurnBoundaryThought(string transcript)
    {
        string thought = "尚无与当前输入匹配的候选开场。";
        if (m_SpeculativeDraft != null && !string.IsNullOrWhiteSpace(m_SpeculativeDraft.draft) &&
            TranscriptSimilarity(m_SpeculativeDraft.sourceTranscript, transcript) >= m_SpeculativeReuseSimilarity)
            thought = "你此前可撤销的想法=" + TruncateForFrame(m_SpeculativeDraft.inner_reaction, 80) +
                "；候选开场=" + TruncateForFrame(m_SpeculativeDraft.draft, 120) +
                $"；草稿置信度={m_SpeculativeDraft.confidence:F2}；语音已准备={m_PreparedSingingBridgeClip != null}。";
        if (!string.IsNullOrWhiteSpace(m_DeferredAutonomyIntent))
            thought += "你已形成的私人自主意图=" + TruncateForFrame(m_DeferredAutonomyIntent, 120) + "。";
        return thought;
    }

    public void CommitStalledTurnDecisionNote(
        string status,
        float confidence,
        string observedMode,
        float modeConfidence,
        string sourceLikelihood)
    {
        status = NormalizeTurnStatus(status);
        if (!TurnBoundaryDecision.IsAction(status)) return;
        m_StalledTurnAction = status;
        if (status == "ask_user" || status == "continue")
        {
            //旧草稿不一定是问题；不能抢在角色选定的询问之前播出无关开场。
            ReleasePreparedSingingBridge(true);
            m_SpeculativeDraft = null;
        }
        m_StalledTurnDecisionNote =
            "[持续声音下的轮次边界复核：角色内部判断=" + status +
            "/" + Mathf.Clamp01(confidence).ToString("F2") +
            "；输入模态=" + NormalizeObservedMode(observedMode) +
            "/" + Mathf.Clamp01(modeConfidence).ToString("F2") +
            "；持续声音来源倾向=" + NormalizeSourceLikelihood(sourceLikelihood) +
            "；接话理由=" + TruncateForFrame(m_LastBoundaryDecisionReason, 120) +
            "。这是角色的轮次选择，不是用户已经唱完整首歌或声源归属的确认。最终ASR具有最高优先级。" +
            (status == "continue"
                ? "你此前选择继续倾听。程序仅收束了缺少新增有效输入的一段录音，没有替你认定用户说完或要求你开口；" +
                  "你仍可保持 <silent/>、等待，也可以根据最终完整证据改变主意、接话或自然询问。先行开场未被播放。"
                : status == "ask_user"
                ? "请在正式回复中以符合角色个性的方式自然询问你尚不确定的内容，例如用户是否仍在唱或持续声音的来源；依据最终ASR修正问题，不要暴露内部阈值。"
                : status == "take_turn"
                    ? "你选择了主动接话；结合完整用户输入采用、修改或放弃此前想法，不必重新等静音，也不要声称用户整首歌已唱完。"
                : "请回应已经完成的文字内容；不要把疑似背景声擅自当作用户演唱。") + "]";
    }

    private static string NormalizeTurnStatus(string value)
    {
        string normalized = (value ?? "").Trim().ToLowerInvariant();
        return TurnBoundaryDecision.IsAction(normalized) ? normalized : "";
    }

    private static string NormalizeSourceLikelihood(string value)
    {
        string normalized = (value ?? "").Trim().ToLowerInvariant();
        return normalized == "user" || normalized == "background" ||
               normalized == "uncertain" ? normalized : "uncertain";
    }

    private void UpdateStreamingSingingReaction(SenseVoiceSpeechToText.StreamingTranscript transcript)
    {
        //自主发言的闸也要在这条路上刷新。它原本只写在 UpdateStreamingTranscript 里，
        //而唱歌帧走的是这里——于是一开唱闸就等于松了：唱多久就多久没刷新，
        //1.5 秒一到自主发言随时能开口。8/12 实测用户唱了 27.9 秒，期间两次
        //FireTick 插进来，还把这一轮的正式回复整个取消掉了(她之后一直在说
        //上一轮的「トイレ」，用户以为她答非所问)。
        //这道闸的注释里当初就写着"一轮【说话+唱歌】要二三十秒才提交"——
        //它正是为这个场景加的，却装在了唱歌不经过的那条路上。
        m_LastStreamingPartialRealtime = Time.realtimeSinceStartup;

        string lyric = string.IsNullOrWhiteSpace(transcript.Text) ? "" : transcript.Text.Trim();
        bool spokenExit = SenseVoiceSpeechToText.EndsWithSpokenSingingExit(lyric);
        string previousLyric = m_StreamingTurnIsSinging ? m_StreamingTranscript : "";
        string evidence =
            $"歌唱概率={transcript.SingingProbability:F2}; " +
            $"音高稳定度={transcript.PitchStability:F2}; " +
            $"已听音频={transcript.AudioMs}ms; " +
            $"语言={transcript.Language}; " +
            "歌词partial=" + (string.IsNullOrEmpty(lyric) ? "（未稳定识别）" : lyric);

        if (!m_StreamingTurnIsSinging)
        {
            // 若刚从普通说话切换成歌唱，旧的文字候选不能沿用。
            CancelSpeculativeRequestOnly();
            m_SpeculativeDraft = null;
            m_LastDraftTranscript = "";
            // 已经渲染成音频的开场不在此列：类别是否匹配由取用处的守卫判定
            // (m_PreparedBridgeIsSinging != wantSinging)，在这里提前扔掉只会白费
            // 一次 LLM+TTS。而且这个判定经常被 LLM 推翻——8/8 实测一场里
            // 「她判定这是说话」响了 9 次，每次都对应一条被误扔的说话开场。
        }

        m_StreamingTurnIsSinging = true;
        m_StreamingSingingProbability = Mathf.Max(
            m_StreamingSingingProbability, transcript.SingingProbability);
        m_StreamingPitchStability = Mathf.Max(
            m_StreamingPitchStability, transcript.PitchStability);
        m_StreamingSingingEvidence = evidence;
        m_StreamingTranscript = lyric;
        m_StreamingTranscriptVersion++;

        if (m_ShowStreamingTranscript && m_RecordTips != null)
            m_RecordTips.text = string.IsNullOrEmpty(lyric)
                ? "正在听你哼唱…"
                : (spokenExit ? lyric + " …" : "♪ " + lyric);

        if (spokenExit)
        {
            // The singer has explicitly switched to speech. Any prepared singing opener or
            // streaming SVC prefix is now based on a superseded intent and must not fire.
            m_StreamingSingingExitDetected = true;
            CancelSpeculativeRequestOnly();
            ReleasePreparedSingingOnly();
            m_SpeculativeDraft = null;
            if (m_SpeculativeDraftCoroutine != null)
                StopCoroutine(m_SpeculativeDraftCoroutine);
            m_SpeculativeDraftCoroutine = null;

            if (!m_TurnBoundaryRequestInFlight && m_EnableSpeculativeListening &&
                m_ChatSettings != null && m_ChatSettings.m_ChatModel != null &&
                lyric.Length >= Mathf.Max(2, m_SpeculativeMinTranscriptChars))
            {
                int exitVersion = m_StreamingTranscriptVersion;
                m_SpeculativeDraftCoroutine = StartCoroutine(
                    RequestSpeculativeDraftAfterDelay(exitVersion, lyric));
            }
            if (m_LogSpeculativeListening)
                Debug.Log($"[歌唱流式倾听] 检测到末尾转为口语退出: \"{lyric}\"；" +
                          "撤销歌唱开场与预转换，改为准备普通回应");
            return;
        }

        if (!m_EnableSpeculativeListening || !m_EnableSingingSpeculativeReaction ||
            m_ChatSettings == null || m_ChatSettings.m_ChatModel == null)
            return;

        if (m_TurnBoundaryRequestInFlight)
        {
            if (m_LogSpeculativeListening)
                Debug.Log($"[歌唱流式倾听] v{m_StreamingTranscriptVersion} audio={transcript.AudioMs}ms " +
                          "边界复核进行中；保留声学证据但暂停歌唱预反应");
            return;
        }

        if (m_SpeculativeRequestInFlight && transcript.Revision &&
            TranscriptContinuity(previousLyric, lyric) < 0.42f)
            CancelSpeculativeRequestOnly();

        //不要每帧重启 debounce。流式帧约 0.9s 一次、这里的 debounce 也是 0.9s，
        //旧做法会在协程醒来的前一刻永远取消它，使 LLM 模态复核一次都发不出去。
        //已有协程会在醒来后读取最新 evidence；在飞请求则由回调安排下一次刷新。
        if (ShouldScheduleSingingReview(
                m_SpeculativeDraftCoroutine != null,
                m_SpeculativeRequestInFlight || m_TurnBoundaryRequestInFlight))
        {
            int version = m_StreamingTranscriptVersion;
            m_SpeculativeDraftCoroutine = StartCoroutine(
                RequestSingingDraftAfterDelay(version, evidence));
        }

        if (m_LogSpeculativeListening)
            Debug.Log($"[歌唱流式倾听] v{m_StreamingTranscriptVersion} audio={transcript.AudioMs}ms " +
                      $"singing={transcript.SingingProbability:F2} pitch={transcript.PitchStability:F2}: \"{lyric}\"");
    }

    private IEnumerator RequestSpeculativeDraftAfterDelay(int transcriptVersion, string transcript)
    {
        float earliest = m_LastSpeculativeRequestTime + Mathf.Max(0.8f, m_SpeculativeMinRequestInterval);
        float delay = Mathf.Max(
            Mathf.Max(0.2f, m_SpeculativeDebounceSeconds),
            earliest - Time.realtimeSinceStartup);
        if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
        m_SpeculativeDraftCoroutine = null;

        if (transcriptVersion != m_StreamingTranscriptVersion || transcript != m_StreamingTranscript)
            yield break;
        if (transcript == m_LastDraftTranscript) yield break;
        if (m_SpeculativeRequestInFlight || m_TurnBoundaryRequestInFlight) yield break;

        m_LastDraftTranscript = transcript;
        m_LastSpeculativeRequestTime = Time.realtimeSinceStartup;
        int requestVersion = ++m_SpeculativeRequestVersion;
        m_SpeculativeRequestInFlight = true;
        string prompt =
            "[内部实时倾听任务；不要把本条当作用户已经说完，也不要将结果写成正式回复]\n" +
            "用户仍在说话，下面是会继续变化、也可能回滚的实时转写：\n" +
            transcript + "\n\n" +
            "请以当前角色身份静默准备，并判断当前输入实际上更像普通说话、歌唱还是仍不确定。" +
            "此前即使约好了跟唱，也只能算预期，不能当作用户已经在唱的证据。" +
            "只输出一个紧凑 JSON 对象，不要 Markdown、不要控制标签：" +
            "{\"understanding\":\"当前理解摘要\",\"uncertainty\":\"可能听错或尚未说完的点\"," +
            "\"inner_reaction\":\"角色当下很短的内心反应\",\"draft\":\"若用户现在结束，准备说出口的话\"," +
            "\"language\":\"ja|zh|en（填写draft实际使用的一种语言）\"," +
            "\"confidence\":0.0,\"observed_mode\":\"speech|singing|uncertain\"," +
            "\"mode_confidence\":0.0}";

        int draftInputRevision = m_InputAudioRevision, draftAudioMs = m_StreamingLatestAudioMs;
        m_ChatSettings.m_ChatModel.PostEphemeralMsg(prompt, response =>
        {
            if (requestVersion != m_SpeculativeRequestVersion) return;
            m_SpeculativeRequestInFlight = false;
            float currentSimilarity = TranscriptContinuity(transcript, m_StreamingTranscript);
            if (currentSimilarity < 0.55f)
            {
                ScheduleSpeculativeRefreshIfNeeded();
                return;
            }
            SpeculativeDraft parsed = ParseSpeculativeDraft(response);
            if (parsed != null)
            {
                parsed.sourceTranscript = transcript;
                parsed.sourceInputRevision = draftInputRevision; parsed.sourceAudioMs = draftAudioMs;
                StoreStreamingObservedMode(parsed, transcript);
                if (IsStrongSpeculativeSpeechVeto(parsed) &&
                    OnStableUserSpeechObserved != null)
                    OnStableUserSpeechObserved(
                        m_LastObservedModeConfidence,
                        m_StreamingLatestAudioMs);
                if (!string.IsNullOrWhiteSpace(parsed.draft))
                {
                    parsed.draft = StripAgentTagsForTTS(parsed.draft).Trim();
                    m_SpeculativeDraft = parsed;
                    if (m_LogSpeculativeListening)
                        Debug.Log($"[流式倾听] 临时草稿就绪 confidence={parsed.confidence:F2} " +
                                  $"mode={parsed.observed_mode}/{parsed.mode_confidence:F2}: \"{parsed.draft}\"");
                    //草稿本来就在用户说话期间生成好了，顺手把开场静默预合成，
                    //EOU 时就能说一句切题的话，而不是通用的"なるほど……"。
                    if (!m_StreamingTurnIsSinging) PrepareSingingBridge(parsed);
                }
            }
            ScheduleSpeculativeRefreshIfNeeded();
        });
    }

    /// <summary>
    /// observed_mode 是独立的听觉判断，即使角色此刻选择沉默、draft 为空也必须保留。
    /// 否则“LLM 已听出歌唱但暂不插话”会在 final 阶段被误当作从未有过语义证据。
    /// </summary>
    private void StoreStreamingObservedMode(
        SpeculativeDraft parsed, string observedTranscript)
    {
        if (parsed == null) return;
        parsed.observed_mode = NormalizeObservedMode(parsed.observed_mode);
        m_LastObservedMode = parsed.observed_mode;
        m_LastObservedModeConfidence = Mathf.Clamp01(parsed.mode_confidence);
        m_LastObservedModeTranscript = observedTranscript ?? "";
        if (parsed.observed_mode == "speech" &&
            m_LastObservedModeConfidence >= 0.70f)
            m_StreamingSemanticSpokenLeadInObserved = true;
        if (parsed.observed_mode != "singing" ||
            m_LastObservedModeConfidence < m_ModeReviewSingingConfidence)
            return;

        m_StreamingSemanticSingingObservedThisTurn = true;
        if (m_LastObservedModeConfidence <
            m_StreamingSemanticSingingConfidenceThisTurn)
            return;
        m_StreamingSemanticSingingConfidenceThisTurn =
            m_LastObservedModeConfidence;
        m_StreamingSemanticSingingTranscriptThisTurn = observedTranscript ?? "";
    }

    private IEnumerator RequestSingingDraftAfterDelay(int transcriptVersion, string evidence)
    {
        float earliest = m_LastSingingSpeculativeRequestTime +
            Mathf.Max(1.2f, m_SingingSpeculativeMinRequestInterval);
        float delay = Mathf.Max(
            Mathf.Max(0.5f, m_SingingSpeculativeDebounceSeconds),
            earliest - Time.realtimeSinceStartup);
        if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
        m_SpeculativeDraftCoroutine = null;

        //★ 不能拿排队时捕获的 version/evidence 去比对——它们**每帧都变**
        //  (m_StreamingTranscriptVersion++ 每帧自增，evidence 里带着实时概率)，
        //  而帧间隔约 890ms、debounce 900ms，协程一醒来就必然"过期"→ 直接 bail。
        //  实测后果：一段 14 秒的哼唱里歌唱草稿发出 **0 次**，唯一那次 mode 判定
        //  来自切进歌唱之前的说话草稿。而模式判定正是靠它刷新的，退出机制因此完全没有输入。
        //  改成醒来后用当下最新的 evidence 重新取值。
        if (!m_StreamingTurnIsSinging || m_SpeculativeRequestInFlight ||
            m_TurnBoundaryRequestInFlight) yield break;
        evidence = m_StreamingSingingEvidence;
        if (string.IsNullOrEmpty(evidence)) yield break;
        if (evidence == m_LastDraftTranscript) yield break;
        string sourceTranscript = m_StreamingTranscript;
        int singingInputRevision = m_InputAudioRevision, singingAudioMs = m_StreamingLatestAudioMs;

        m_LastDraftTranscript = evidence;
        m_LastSingingSpeculativeRequestTime = Time.realtimeSinceStartup;
        int requestVersion = ++m_SpeculativeRequestVersion;
        m_SpeculativeRequestInFlight = true;
        string prompt =
            "[内部实时听觉任务；系统暂时怀疑用户在唱，但这可能是有抑扬的普通说话。" +
            "不要正式回复、不要调用工具、不要写入记忆]\n" +
            "下面是会继续变化的听觉证据；歌词partial可能严重回滚，跟唱约定也不是歌唱证据：\n" +
            evidence + "\n\n" +
            "请先独立判断 observed_mode：speech=实际在普通说话；singing=声学指标和内容都支持歌唱；" +
            "uncertain=证据冲突或不足。不要因为角色正等着跟唱就选择 singing。" +
            "若是 speech，draft 应自然回应说话内容，绝不能复读或提到唱完；" +
            "若是 singing/uncertain，draft 才能是唱完后的安全短开场，且不得猜歌名、复述歌词、" +
            "宣称识别成功或评价唱功。只输出紧凑JSON，不要Markdown和控制标签：" +
            "{\"understanding\":\"当前听觉理解\",\"uncertainty\":\"不确定之处\"," +
            "\"inner_reaction\":\"角色此刻很短的心里话\",\"draft\":\"根据实际模态准备的一句自然开场\"," +
            "\"language\":\"ja|zh|en（填写draft实际使用的一种语言）\"," +
            "\"confidence\":0.0,\"observed_mode\":\"speech|singing|uncertain\"," +
            "\"mode_confidence\":0.0}";

        m_ChatSettings.m_ChatModel.PostEphemeralMsg(prompt, response =>
        {
            if (requestVersion != m_SpeculativeRequestVersion) return;
            m_SpeculativeRequestInFlight = false;
            if (!ShouldAcceptSingingReviewResult(
                    m_StreamingTurnIsSinging,
                    sourceTranscript,
                    m_StreamingTranscript))
            {
                ScheduleSingingRefreshIfNeeded();
                return;
            }

            SpeculativeDraft parsed = ParseSpeculativeDraft(response);
            if (parsed != null)
            {
                parsed.sourceTranscript = sourceTranscript;
                parsed.sourceInputRevision = singingInputRevision; parsed.sourceAudioMs = singingAudioMs;
                //歌唱草稿也要刷新这份判定，否则切进歌唱模式之后就没有新判定了——
                //退出将只能依赖切换之前的旧值，那等于只有一次机会。
                StoreStreamingObservedMode(parsed, m_StreamingTranscript);
                parsed.sourceEvidence = evidence;
                parsed.sourceSingingProbability = m_StreamingSingingProbability;
                parsed.sourcePitchStability = m_StreamingPitchStability;
                bool speechVeto = IsStrongSpeculativeSpeechVeto(parsed);
                if (speechVeto && OnStableUserSpeechObserved != null)
                    OnStableUserSpeechObserved(
                        m_LastObservedModeConfidence,
                        m_StreamingLatestAudioMs);
                parsed.isSinging = !speechVeto;
                if (speechVeto)
                {
                    ReleasePreparedSingingBridge(true);
                    ResetStreamingHumBackPrefix(
                        "speculative-cognition-speech-veto", true);
                }
                if (!string.IsNullOrWhiteSpace(parsed.draft))
                {
                    parsed.draft = speechVeto
                        ? StripAgentTagsForTTS(parsed.draft).Trim()
                        : SanitizeSingingBridge(parsed.draft);
                    m_SpeculativeDraft = parsed;
                    if (m_LogSpeculativeListening)
                    {
                        Debug.Log($"[歌唱流式倾听] 心里话：\"{parsed.inner_reaction}\"；" +
                                  $"mode={parsed.observed_mode}/{parsed.mode_confidence:F2} " +
                                  $"speechVeto={speechVeto}；候选开场 confidence={parsed.confidence:F2}: " +
                                  $"\"{parsed.draft}\"");
                    }
                    if (!speechVeto)
                    {
                        PrepareSingingBridge(parsed);
                    }
                }
            }
            ScheduleSingingRefreshIfNeeded();
        });
    }

    private static bool ShouldScheduleSingingReview(
        bool coroutinePending,
        bool requestInFlight)
    {
        return !coroutinePending && !requestInFlight;
    }

    private static bool ShouldAcceptSingingReviewResult(
        bool stillInSingingReview,
        string sourceTranscript,
        string currentTranscript)
    {
        if (!stillInSingingReview) return false;
        if (string.IsNullOrWhiteSpace(sourceTranscript) ||
            string.IsNullOrWhiteSpace(currentTranscript))
            return true;
        //数值证据和 audio_ms 每帧都会变化，不能用整段 evidence 相等作为回包门槛；
        //只在文字语义发生大回滚时丢弃旧判断。
        return TranscriptContinuity(sourceTranscript, currentTranscript) >= 0.55f;
    }

    private void ScheduleSingingRefreshIfNeeded()
    {
        if (!m_StreamingTurnIsSinging || string.IsNullOrWhiteSpace(m_StreamingSingingEvidence) ||
            m_StreamingSingingEvidence == m_LastDraftTranscript ||
            m_SpeculativeDraftCoroutine != null || m_SpeculativeRequestInFlight ||
            m_TurnBoundaryRequestInFlight)
            return;
        int version = m_StreamingTranscriptVersion;
        string evidence = m_StreamingSingingEvidence;
        m_SpeculativeDraftCoroutine = StartCoroutine(
            RequestSingingDraftAfterDelay(version, evidence));
    }

    private void CancelSpeculativeRequestOnly()
    {
        if (m_SpeculativeDraftCoroutine != null)
        {
            StopCoroutine(m_SpeculativeDraftCoroutine);
            m_SpeculativeDraftCoroutine = null;
        }
        m_SpeculativeRequestVersion++;
        m_SpeculativeRequestInFlight = false;
        if (m_ChatSettings != null && m_ChatSettings.m_ChatModel != null)
            m_ChatSettings.m_ChatModel.CancelEphemeralMsg();
    }

    private void ScheduleSpeculativeRefreshIfNeeded()
    {
        if (m_StreamingTurnIsSinging || string.IsNullOrWhiteSpace(m_StreamingTranscript) ||
            m_StreamingTranscript == m_LastDraftTranscript ||
            m_SpeculativeDraftCoroutine != null || m_SpeculativeRequestInFlight ||
            m_TurnBoundaryRequestInFlight)
            return;
        int version = m_StreamingTranscriptVersion;
        string text = m_StreamingTranscript;
        m_SpeculativeDraftCoroutine = StartCoroutine(RequestSpeculativeDraftAfterDelay(version, text));
    }

    private SpeculativeDraft ParseSpeculativeDraft(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;
        int begin = response.IndexOf('{');
        int end = response.LastIndexOf('}');
        if (begin < 0 || end <= begin) return null;
        try
        {
            return JsonUtility.FromJson<SpeculativeDraft>(response.Substring(begin, end - begin + 1));
        }
        catch (Exception e)
        {
            if (m_LogSpeculativeListening) Debug.LogWarning("[流式倾听] 草稿 JSON 无法解析: " + e.Message);
            return null;
        }
    }

    private static string NormalizeObservedMode(string mode)
    {
        string lower = (mode ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(lower)) return "uncertain";
        if (lower == "speech" || lower == "spoken" || lower == "talking" ||
            lower.Contains("not singing") || lower.Contains("ordinary speech") ||
            lower.Contains("说话") || lower.Contains("口语") || lower.Contains("讲话"))
            return "speech";
        if (lower == "singing" || lower == "song" || lower == "humming" ||
            lower.Contains("歌唱") || lower.Contains("唱歌") || lower.Contains("哼唱"))
            return "singing";
        return "uncertain";
    }

    private string DescribeStreamingSemanticModeEvidence()
    {
        if (m_StreamingSemanticSingingObservedThisTurn)
        {
            string transcript = TruncateForFrame(
                m_StreamingSemanticSingingTranscriptThisTurn, 48);
            return "singing/" +
                Mathf.Clamp01(m_StreamingSemanticSingingConfidenceThisTurn).ToString("F2") +
                (string.IsNullOrWhiteSpace(transcript)
                    ? "（本轮较早片段）"
                    : "（本轮较早片段=\"" + transcript + "\"）");
        }
        return NormalizeObservedMode(m_LastObservedMode) + "/" +
            Mathf.Clamp01(m_LastObservedModeConfidence).ToString("F2");
    }

    private enum SingingModeFusion
    {
        AcousticSinging,
        AcousticSpeech,
        AcousticUncertainSemanticSinging,
        AcousticUncertainSemanticSpeech,
        AcousticUncertain,
        ConflictSemanticSinging,
        ConflictSemanticSpeech,
        ConflictStreamingEvidence,
    }

    /// <summary>
    /// 融合这里只负责识别“证据是否冲突”，不负责替角色猜最终事实。
    /// 完整转写判断优先于较早的流式判断；完整判断无结论时才沿用流式判断。
    /// </summary>
    private static SingingModeFusion FuseSingingModeEvidence(
        bool acousticSaysSinging,
        bool acousticIsUncertain,
        string finalSemanticVerdict,
        bool streamingSemanticSinging,
        bool streamingSemanticSpeech)
    {
        string rawFinalMode = (finalSemanticVerdict ?? "").Trim().ToLowerInvariant();
        bool finalModeWasExplicit = rawFinalMode == "singing" ||
            rawFinalMode == "speech" || rawFinalMode == "uncertain";
        string semanticMode = finalModeWasExplicit
            ? rawFinalMode
            : streamingSemanticSinging != streamingSemanticSpeech
                ? (streamingSemanticSinging ? "singing" : "speech")
                : "uncertain";

        if (!finalModeWasExplicit && streamingSemanticSinging && streamingSemanticSpeech)
            return SingingModeFusion.ConflictStreamingEvidence;

        if (acousticIsUncertain)
        {
            if (semanticMode == "singing")
                return SingingModeFusion.AcousticUncertainSemanticSinging;
            if (semanticMode == "speech")
                return SingingModeFusion.AcousticUncertainSemanticSpeech;
            return SingingModeFusion.AcousticUncertain;
        }

        if (semanticMode == "singing" && !acousticSaysSinging)
            return SingingModeFusion.ConflictSemanticSinging;
        if (semanticMode == "speech" && acousticSaysSinging)
            return SingingModeFusion.ConflictSemanticSpeech;
        return acousticSaysSinging
            ? SingingModeFusion.AcousticSinging
            : SingingModeFusion.AcousticSpeech;
    }

    private bool IsStrongSpeculativeSpeechVeto(SpeculativeDraft draft)
    {
        return draft != null &&
            NormalizeObservedMode(draft.observed_mode) == "speech" &&
            Mathf.Clamp01(draft.mode_confidence) >= m_SpeculativeSpeechVetoConfidence;
    }

    /// <summary>
    /// 完整录音已有时序转写和歌唱证据时，局部 speech 判断不能否定整轮。
    /// 此规则只撤销旧草稿的否决权；融合冲突和实际动作的独立边界仍须通过。
    /// </summary>
    private bool FinalEvidenceOverridesSpeculation()
    {
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        return senseVoice != null && senseVoice.HasTimeOrderedTranscript &&
            senseVoice.LastIsSinging && senseVoice.LastSingingProbability >= 0.52f;
    }

    private bool HasStrongSpeculativeSpeechVeto()
    {
        SpeculativeDraft draft = m_SpeculativeDraft;
        if (!IsStrongSpeculativeSpeechVeto(draft)) return false;
        if (!string.IsNullOrWhiteSpace(draft.sourceTranscript) &&
            !string.IsNullOrWhiteSpace(m_StreamingTranscript) &&
            TranscriptContinuity(draft.sourceTranscript, m_StreamingTranscript) < 0.55f)
            return false;
        return true;
    }

    /// <summary>
    /// 她自己(而不是声学指标)认定"这是在说话"。用来把误判的流式歌唱模式退出来。
    ///
    /// 为什么交给她：声学侧的门槛是 SingingProbability >= 0.54 + 连续 2 帧，而实测正常说话
    /// 稳定落在 0.58~0.64，稳稳骑在门槛上方；一旦切进去 m_StreamingTurnIsSinging 还会粘住
    /// 整轮——实测一句"对的，你居然在看我的副屏幕啊"被锁在歌唱监听里 14 秒。
    /// 而同一场里她的 mode 判定 7/7 全对(0.80~0.95)，且在声学切换之前 34 行就给出了
    /// speech/0.95。判定一直存在，只是没接到这里。
    ///
    /// 用 m_LastObservedMode 而不是 m_SpeculativeDraft：后者在切进歌唱时就被清空了。
    /// 转写相似度那道校验保留——判定必须还对得上当前听到的内容。
    /// </summary>
    private bool HasStrongSpeechModeJudgment()
    {
        if (NormalizeObservedMode(m_LastObservedMode) != "speech") return false;
        if (m_LastObservedModeConfidence < m_SpeculativeSpeechVetoConfidence) return false;
        if (!string.IsNullOrWhiteSpace(m_LastObservedModeTranscript) &&
            !string.IsNullOrWhiteSpace(m_StreamingTranscript) &&
            TranscriptContinuity(m_LastObservedModeTranscript, m_StreamingTranscript) < 0.55f)
            return false;
        return true;
    }

    private bool HasStrongSpeculativeSingingSupport()
    {
        SpeculativeDraft draft = m_SpeculativeDraft;
        if (draft == null || NormalizeObservedMode(draft.observed_mode) != "singing" ||
            Mathf.Clamp01(draft.mode_confidence) < m_SpeculativeSingingSupportConfidence)
            return false;
        if (!string.IsNullOrWhiteSpace(draft.sourceTranscript) &&
            !string.IsNullOrWhiteSpace(m_StreamingTranscript) &&
            TranscriptContinuity(draft.sourceTranscript, m_StreamingTranscript) < 0.55f)
            return false;
        return true;
    }

    private string SanitizeSingingBridge(string draft)
    {
        string clean = StripAgentTagsForTTS(draft ?? "").Trim().Trim('"', '\'', '“', '”');
        int lineBreak = clean.IndexOfAny(new[] { '\r', '\n' });
        if (lineBreak >= 0) clean = clean.Substring(0, lineBreak).Trim();
        if (string.IsNullOrEmpty(clean) || clean.Length > Mathf.Max(8, m_SingingBridgeMaxChars))
            return "";

        // 预开口发生在最终歌曲识别之前，因此拒绝任何可能把partial误当事实的句子。
        string[] unsafeClaims =
        {
            "歌名", "这首歌是", "你唱的是", "我知道这首", "歌词",
            "曲名", "この曲は", "歌っているのは", "知っている曲", "歌詞",
            "song is", "singing is", "i know this song", "lyrics"
        };
        foreach (string claim in unsafeClaims)
        {
            if (clean.IndexOf(claim, StringComparison.OrdinalIgnoreCase) >= 0) return "";
        }
        return IsPurePunctuation(clean) ? "" : clean;
    }

    private void PrepareSingingBridge(SpeculativeDraft draft)
    {
        //唱歌与说话共用同一套预合成，只是门槛不同。说话那条门槛更高：草稿说错话的
        //代价比唱歌应声更直接，用户最后半句随时可能改变语义。
        bool singing = draft != null && draft.isSinging;
        float minConfidence = singing ? m_SingingBridgeMinConfidence : m_SpeechBridgeMinConfidence;
        if (draft == null ||
            (!singing && !m_EnableSpeechBridge) ||
            draft.confidence < minConfidence ||
            m_ChatSettings == null || m_ChatSettings.m_TextToSpeech == null)
            return;

        //歌唱那条在 SanitizeSingingBridge 里已经清理并限长；说话那条只过了标签剥离，
        //这里补上同样的净化：去引号、只取第一行。
        string bridgeText = (draft.draft ?? "").Trim().Trim('"', '\'', '“', '”');
        int lineBreak = bridgeText.IndexOfAny(new[] { '\r', '\n' });
        if (lineBreak >= 0) bridgeText = bridgeText.Substring(0, lineBreak).Trim();

        //说话草稿是完整开场白，天然 30-40 字，会被为歌唱短应声设计的 28 字上限全部
        //拦下(实测 conf 0.90/0.95 两条就是这样丢的)。截到第一句即可：既落回上限内，
        //也天然减少与正式回复的重叠——后半句本来就该留给正式回答去说。
        if (!singing && bridgeText.Length > m_SingingBridgeMaxChars)
        {
            for (int i = 0; i < bridgeText.Length; i++)
            {
                if (!IsStrongBoundary(bridgeText[i])) continue;
                string head = bridgeText.Substring(0, i + 1).Trim();
                if (head.Length >= 4) { bridgeText = head; }
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(bridgeText) ||
            bridgeText.Length > Mathf.Max(8, m_SingingBridgeMaxChars) ||
            IsPurePunctuation(bridgeText))
            return;

        SpeechText bridgeSpeech = m_ChatSettings.m_TextToSpeech.ResolveSpeech(
            new SpeechText(bridgeText, draft.language));
        if ((bridgeText == m_PreparedSingingBridgeText && bridgeSpeech.LanguageCode == m_PreparedSingingBridgeLanguage && m_PreparedSingingBridgeClip != null) ||
            (bridgeText == m_PendingBridgeText && bridgeSpeech.LanguageCode == m_PendingBridgeLanguage && m_SingingBridgeTtsInFlight))
            return;

        //不销毁已就绪的旧开场。草稿在用户说话期间每 1.4 秒刷新一次，若一进来就
        //Release，用户大部分时间都处在"旧的已删、新的没好"的空窗里——实测三次预合成
        //全部落在空窗上，EOU 只能播缓存语。改为：旧的留着可播，新的合成好了再替换。
        int generation = ++m_SingingBridgeGeneration;
        m_SingingBridgeTtsInFlight = true;
        m_PendingBridgeText = bridgeText;
        m_PendingBridgeLanguage = bridgeSpeech.LanguageCode;
        m_PendingBridgeConfidence = draft.confidence;
        m_PendingBridgeIsSinging = singing;
        m_PendingBridgeObservedMode = NormalizeObservedMode(draft.observed_mode);
        string bridgeSourceTranscript = draft.sourceTranscript ?? "";
        string bridgeObservedMode = m_PendingBridgeObservedMode;
        string label = singing ? "歌唱预反应" : "说话预反应";

        if (m_LogSpeculativeListening)
            Debug.Log($"[{label}] 开始静默预合成(conf={draft.confidence:F2})：\"{bridgeText}\"");

        m_ChatSettings.m_TextToSpeech.PrepareSpeech(bridgeSpeech, (clip, text) =>
        {
            if (generation != m_SingingBridgeGeneration ||
                (singing && !m_StreamingTurnIsSinging))
            {
                //被更新的草稿或轮次切换取代。旧的已就绪开场仍然留着，不动。
                if (clip != null) Destroy(clip);
                if (m_LogSpeculativeListening)
                    Debug.Log($"[{label}] 本次预合成已被取代，保留上一段可播开场");
                return;
            }
            m_SingingBridgeTtsInFlight = false;
            if (clip == null)
            {
                if (m_LogSpeculativeListening)
                    Debug.LogWarning($"[{label}] 静默预合成未完成，将在EOU使用缓存应声");
                return;
            }

            //换上新的，再销毁被替换掉的那一段（正在播则延后销毁）。
            AudioClip previous = m_PreparedSingingBridgeClip;
            m_PreparedSingingBridgeClip = clip;
            m_PreparedSingingBridgeText = text;
            m_PreparedSingingBridgeLanguage = bridgeSpeech.LanguageCode;
            m_PreparedBridgeSourceTranscript = bridgeSourceTranscript;
            m_PreparedBridgeObservedMode = bridgeObservedMode;
            m_PreparedSingingBridgeConfidence = m_PendingBridgeConfidence;
            m_PreparedBridgeIsSinging = m_PendingBridgeIsSinging;
            if (previous != null)
            {
                if (m_AudioSource != null && m_AudioSource.isPlaying &&
                    m_AudioSource.clip == previous)
                {
                    //延后销毁槽只有一个位置，先把上一段占位的清掉，否则会泄漏。
                    if (m_DeferredPreparedClipToDestroy != null &&
                        m_DeferredPreparedClipToDestroy != previous)
                        Destroy(m_DeferredPreparedClipToDestroy);
                    m_DeferredPreparedClipToDestroy = previous;
                }
                else
                {
                    Destroy(previous);
                }
            }
            if (m_LogSpeculativeListening)
                Debug.Log($"[{label}] 开场已就绪，音频{clip.length:F2}s：\"{text}\"");
        });
    }

    private bool TryPlayPreparedSingingBridge(
        bool wantSinging,
        AudioSource output,
        out string spokenText,
        out float duration)
    {
        spokenText = "";
        duration = 0f;
        //类别必须匹配：歌唱开场接在普通提问后面会很怪，反之亦然。
        float required = m_PreparedBridgeIsSinging
            ? m_SingingBridgeMinConfidence : m_SpeechBridgeMinConfidence;
        //落选原因必须能看见：8/7 实测一场里预合成 6 次「开场已就绪」，却只有 1 次
        //被播出去，另外 5 次的 LLM+TTS 成本白付。不知道卡在哪一条就没法判断该动谁。
        string reject =
            output == null ? "无音频输出源"
            : m_PreparedSingingBridgeClip == null
                ? (m_SingingBridgeTtsInFlight ? "尚在合成中" : "无预合成素材")
            : m_PreparedBridgeIsSinging != wantSinging
                ? $"类别不匹配(素材={(m_PreparedBridgeIsSinging ? "歌唱" : "说话")}, " +
                  $"本轮={(wantSinging ? "歌唱" : "说话")})"
            : m_PreparedSingingBridgeConfidence < required
                ? $"置信度不足({m_PreparedSingingBridgeConfidence:F2} < {required:F2})"
            : string.IsNullOrWhiteSpace(m_PreparedSingingBridgeText) ? "文本为空"
            : null;
        if (reject != null)
        {
            if (m_LogStreamTimings)
                Debug.Log($"[说话预反应] 预合成开场未采用：{reject}");
            return false;
        }

        spokenText = m_PreparedSingingBridgeText;
        duration = m_PreparedSingingBridgeClip.length;
        output.clip = m_PreparedSingingBridgeClip;
        output.loop = false;
        output.Play();
        m_PreparedSingingBridgePlayedThisTurn = true;
        //在出声的这一刻留一份：从这里到主请求派发之间，ReleasePreparedSingingBridge
        //有好几处会把 m_PreparedSingingBridgeText 清空(歌唱未确认、轮次重置、打断)，
        //而这句话已经进了用户的耳朵，必须原样交给本轮回复当前缀。
        m_SpokenBridgeTextThisTurn = spokenText;
        return true;
    }

    private static bool ShouldCommitPreparedBridgeAfterFinal(
        bool finalModeConfirmed,
        bool singingBridge,
        bool sourceModeCompatible,
        float transcriptSimilarity,
        float requiredSimilarity)
    {
        if (!finalModeConfirmed) return false;
        //歌词的流式/最终 ASR 本来就可能差异很大，歌唱开场不比歌词相似度；
        //但其生成依据不能明确判为 speech。那种开场通常仍在说“等你开始唱”，
        //放到已经唱完的最终事实后会成为不可撤销的时序错误。
        return singingBridge
            ? sourceModeCompatible
            : transcriptSimilarity >= requiredSimilarity;
    }

    /// <summary>
    /// 预生成与预合成可以发生在用户说话期间，但实际出声属于不可撤销提交：必须等最终
    /// ASR/模态证据到达。普通说话还要核对“这段音频对应的 partial”而不只是最新草稿，
    /// 防止旧音频在新草稿尚未合成完成时被错误沿用。
    /// </summary>
    private bool TryCommitPreparedBridgeAfterFinal(
        string finalTranscript,
        bool wantSinging,
        bool finalModeConfirmed,
        out float sourceSimilarity)
    {
        if (m_StalledTurnAction == "ask_user" || m_StalledTurnAction == "continue")
        {
            sourceSimilarity = 0f;
            return false;
        }
        sourceSimilarity = wantSinging
            ? 1f
            : TranscriptSimilarity(m_PreparedBridgeSourceTranscript, finalTranscript);
        bool sourceModeCompatible = !wantSinging ||
            NormalizeObservedMode(m_PreparedBridgeObservedMode) != "speech";
        m_EouFillerGeneration++;
        m_EouFillerScheduled = false;

        bool decision = ShouldCommitPreparedBridgeAfterFinal(
            finalModeConfirmed,
            wantSinging,
            sourceModeCompatible,
            sourceSimilarity,
            m_SpeculativeReuseSimilarity);
        if (!decision || m_PreparedSingingBridgeClip == null ||
            m_PreparedBridgeIsSinging != wantSinging ||
            m_AudioSource == null || m_AudioSource.isPlaying || m_LatencyFillerPlayed)
        {
            if (!decision && m_LogSpeculativeListening)
                Debug.Log($"[{(wantSinging ? "歌唱预反应" : "说话预反应")}] " +
                          $"最终证据未通过提交，预合成只保留为可撤销准备 " +
                          $"(mode={finalModeConfirmed}, source_mode=" +
                          $"{NormalizeObservedMode(m_PreparedBridgeObservedMode)}, " +
                          $"source_compatible={sourceModeCompatible}, " +
                          $"similarity={sourceSimilarity:F2})");
            return false;
        }

        if (!TryPlayPreparedSingingBridge(
                wantSinging, m_AudioSource, out string fillerText, out float fillerDuration))
            return false;

        m_LatencyFillerPlayed = true;
        m_LatencyFillerFromEou = true;
        m_LatencyFillerText = fillerText;
        m_LatencyFillerStartedAt = Time.realtimeSinceStartup;
        m_LatencyFillerDuration = Mathf.Max(0.1f, fillerDuration);
        m_CurrentlyPlayingText = fillerText;
        m_TextBack.text = fillerText;
        SetAnimator("state", 2);
        IsAISpeaking = true;

        if (m_LogStreamTimings)
        {
            float eouToFiller = m_EouTime > 0f
                ? Time.realtimeSinceStartup - m_EouTime
                : 0f;
            Debug.Log($"[Timing] ★ EOU→最终确认后首音: {eouToFiller:F2}s");
            Debug.Log($"[{(wantSinging ? "歌唱预反应" : "说话预反应")}] " +
                      $"最终证据通过后播放预合成开场: \"{fillerText}\" " +
                      $"(source_similarity={sourceSimilarity:F2})");
        }
        return true;
    }

    private void AbandonPreparedBridgeAfterFinal(string reason)
    {
        m_EouFillerGeneration++;
        m_EouFillerScheduled = false;
        CancelPendingBridgeSynthesis(true);
        if (m_LogSpeculativeListening)
            Debug.Log($"[说话预反应] 不自动提交开场；已就绪音频仅供正式LLM重新选择 ({reason})");
    }

    private string PreparedReplyChoiceHint()
    {
        if (m_PreparedSingingBridgeClip == null || string.IsNullOrWhiteSpace(m_PreparedSingingBridgeText))
            return "";
        return "\n[本轮未说出口的候选开场：" + m_PreparedSingingBridgeText +
            "\n它基于较早的片段，尚未经最终语义确认，也没有播放或写入已说历史。" +
            "请先完整理解本轮最终输入和声学证据。若这句仍符合你的意思，可原样作为正式回复开头；" +
            "也可自由改写、不用、询问或保持沉默。不要为了复用而忽略否定、后半句或不确定性。]";
    }

    // The final model has already selected these exact spoken words. This is an
    // audio-cache lookup, never an ASR similarity test or a program-chosen answer.
    private AudioClip TakePreparedFormalReply(string selectedText, string languageCode)
    {
        if (m_PreparedSingingBridgeClip == null || languageCode != m_PreparedSingingBridgeLanguage ||
            !string.Equals(selectedText.Trim(), m_PreparedSingingBridgeText.Trim(), StringComparison.Ordinal))
            return null;
        AudioClip clip = m_PreparedSingingBridgeClip;
        m_PreparedSingingBridgeClip = null; // transfer ownership to the formal player
        ClearPreparedBridgeClip();
        Debug.Log("[说话预反应] 正式LLM选择了候选原句，复用音频作为正式回复（非先行开场）");
        return clip;
    }

    private static int PreparedReplyBoundary(string generated, string prepared, bool complete)
    {
        if (string.IsNullOrEmpty(prepared) || string.IsNullOrEmpty(generated)) return -1;
        if (generated.StartsWith(prepared, StringComparison.Ordinal)) return prepared.Length - 1;
        return !complete && prepared.StartsWith(generated, StringComparison.Ordinal) ? -2 : -1;
    }

    private void ReleasePreparedSingingBridge(bool cancelSynthesis)
    {
        //已经合成好、却还没播就被丢掉的素材，是白付掉的一次 LLM+TTS。和
        //「预合成开场未采用：无预合成素材」配起来看，就能分清是"没生成"还是"被提前扔了"。
        if (m_LogStreamTimings && !m_PreparedSingingBridgePlayedThisTurn &&
            (m_PreparedSingingBridgeClip != null || m_SingingBridgeTtsInFlight))
        {
            Debug.Log($"[说话预反应] 丢弃未播出的预合成开场 " +
                      $"(已就绪={m_PreparedSingingBridgeClip != null}, " +
                      $"合成中={m_SingingBridgeTtsInFlight}, " +
                      $"conf={m_PreparedSingingBridgeConfidence:F2}): " +
                      $"\"{m_PreparedSingingBridgeText}\"");
        }
        CancelPendingBridgeSynthesis(cancelSynthesis);
        ClearPreparedBridgeClip();
    }

    // Speech confirmation cancels singing preparation, not a speech opener whose
    // own source transcript still needs the normal final-consistency check.
    private void ReleasePreparedSingingOnly()
    {
        if (m_SingingBridgeTtsInFlight && m_PendingBridgeIsSinging)
            CancelPendingBridgeSynthesis(true);
        if (m_PreparedBridgeIsSinging)
            ClearPreparedBridgeClip();
    }

    private void CancelPendingBridgeSynthesis(bool cancelRequest)
    {
        if (cancelRequest && m_ChatSettings != null && m_ChatSettings.m_TextToSpeech != null)
            m_ChatSettings.m_TextToSpeech.CancelPreparedSpeech();
        m_SingingBridgeGeneration++;
        m_SingingBridgeTtsInFlight = false;
        m_PendingBridgeText = "";
        m_PendingBridgeConfidence = 0f;
        m_PendingBridgeIsSinging = false;
        m_PendingBridgeObservedMode = "";
    }

    private void ClearPreparedBridgeClip()
    {
        if (m_PreparedSingingBridgeClip != null)
        {
            if (m_AudioSource != null && m_AudioSource.isPlaying &&
                m_AudioSource.clip == m_PreparedSingingBridgeClip)
            {
                if (m_DeferredPreparedClipToDestroy != null &&
                    m_DeferredPreparedClipToDestroy != m_PreparedSingingBridgeClip)
                    Destroy(m_DeferredPreparedClipToDestroy);
                m_DeferredPreparedClipToDestroy = m_PreparedSingingBridgeClip;
            }
            else
            {
                Destroy(m_PreparedSingingBridgeClip);
            }
        }
        m_PreparedSingingBridgeClip = null;
        m_PreparedSingingBridgeText = "";
        m_PreparedSingingBridgeLanguage = null;
        m_PreparedBridgeSourceTranscript = "";
        m_PreparedBridgeObservedMode = "";
        m_PreparedSingingBridgeConfidence = 0f;
        m_PreparedBridgeIsSinging = false;
    }

    /// <summary>
    /// EOU 最终转写到达时调用。相似则把准备结果作为一次性本轮提示；差异大则彻底丢弃，
    /// 让正式模型从最终文本重新理解。无论哪条路径，临时内容都不会进入持久历史。
    /// </summary>
    private string FinalizeSpeculativeTurn(string finalTranscript)
    {
        CancelSpeculativeRequestOnly();
        CancelTurnBoundaryReview();

        SpeculativeDraft draft = m_SpeculativeDraft;
        float similarity = draft != null
            ? TranscriptSimilarity(draft.sourceTranscript, finalTranscript)
            : 0f;
        string hint = null;
        if (draft != null && !string.IsNullOrWhiteSpace(draft.draft) &&
            similarity >= m_SpeculativeReuseSimilarity)
        {
            float preparedSourceSimilarity = TranscriptSimilarity(m_PreparedBridgeSourceTranscript, finalTranscript);
            //最终证据通过后已经出声的那句开场不再写进这里。它由 PublishSpokenPrefixToLlm
            //作为一条 assistant 消息挂在用户消息之后，正式回复相当于接着它往下写——
            //散文形态的"不要重复它"实测挡不住(8/11 那轮明写了，她照样又说了一遍；
            //离线复现同样是 1/5 重说、2/5 重新打招呼，换成 assistant 消息后归零)。
            //这里只保留没出声时的候选回答。
            bool speechBridgeReady =
                (!m_PreparedBridgeIsSinging && m_PreparedSingingBridgeClip != null &&
                 m_PreparedSingingBridgeConfidence >= m_SpeechBridgeMinConfidence) ||
                (!m_PendingBridgeIsSinging && m_SingingBridgeTtsInFlight &&
                 m_PendingBridgeConfidence >= m_SpeechBridgeMinConfidence);
            bool bridgeSpoken = m_PreparedSingingBridgePlayedThisTurn && !m_PreparedBridgeIsSinging;
            bool bridgeHandledAsPrefix = bridgeSpoken;

            hint =
                "[本轮可撤销倾听状态；最终用户转写具有最高优先级。不要提及这段内部状态。\n" +
                "此前理解：" + (draft.understanding ?? "") + "\n" +
                "不确定点：" + (draft.uncertainty ?? "") + "\n" +
                "角色瞬时感受：" + (draft.inner_reaction ?? "") + "\n" +
                "临时模态判断：" + NormalizeObservedMode(draft.observed_mode) +
                "（置信度 " + Mathf.Clamp01(draft.mode_confidence).ToString("F2") + "）\n" +
                (bridgeHandledAsPrefix
                    ? "]"
                    : "已准备的候选回答：" + draft.draft + "\n" +
                      "若最终文本改变了含义，必须修改或放弃候选回答。]");
            if (m_LogSpeculativeListening)
                Debug.Log($"[流式倾听] 最终一致度 {similarity:F2}，复用临时准备作为本轮提示" +
                          $"(音频源一致度={preparedSourceSimilarity:F2}, " +
                          $"开场走前缀={bridgeHandledAsPrefix})");
        }
        else if (draft != null && m_LogSpeculativeListening)
        {
            Debug.Log($"[流式倾听] 最终一致度 {similarity:F2}，放弃临时草稿并重新理解");
            AbandonPreparedBridgeAfterFinal($"transcript-similarity={similarity:F2}");
        }
        else
        {
            AbandonPreparedBridgeAfterFinal("no-reusable-final-draft");
        }

        m_SpeculativeDraft = null;
        m_StreamingTranscript = "";
        m_LastDraftTranscript = "";
        ResetStreamingSingingEvidence();
        return (hint ?? "") + PreparedReplyChoiceHint();
    }

    private string FinalizeSingingTurn(SenseVoiceSpeechToText senseVoice)
    {
        CancelSpeculativeRequestOnly();
        CancelTurnBoundaryReview();
        bool confirmedSinging = senseVoice != null && senseVoice.LastIsSinging;
        m_EouSingingRejectedByFinal = !confirmedSinging;

        SpeculativeDraft draft = m_SpeculativeDraft;
        bool draftModeCompatible = draft != null &&
            NormalizeObservedMode(draft.observed_mode) != "speech";
        bool reusable = confirmedSinging && draft != null && draft.isSinging &&
            draftModeCompatible &&
            !string.IsNullOrWhiteSpace(draft.draft) &&
            draft.confidence >= m_SingingBridgeMinConfidence;
        string finalGrounding = m_StalledTurnAction == "take_turn" || m_StalledTurnAction == "ask_user"
            ? "[本轮录音由角色主动接话/询问收束；这不证明用户已经唱完整首歌。请结合完整ASR和你自己的接话意图回应。]"
            : confirmedSinging
            ? "[本轮最终听觉事实：用户这一轮已经唱完，最终歌唱分析也确认了歌声。" +
              "请基于已经听到的内容自然回应；不要再说仍在等待用户开始唱，" +
              "也不要把流式阶段未完成的理解当成最终事实。]"
            : "";
        string hint = string.IsNullOrEmpty(finalGrounding) ? null : finalGrounding;
        if (reusable)
        {
            //与说话那条一致：已经出声的短开场交给 assistant 前缀，这里不再复述。
            bool alreadySpoken = m_PreparedSingingBridgePlayedThisTurn;
            hint = finalGrounding + "\n" +
                "[本轮可撤销听歌状态；最终歌唱分析具有最高优先级，不要提及内部状态。\n" +
                "听歌期间的理解：" + (draft.understanding ?? "") + "\n" +
                "仍不确定：" + (draft.uncertainty ?? "") + "\n" +
                "角色瞬时感受：" + (draft.inner_reaction ?? "") + "\n" +
                "临时模态判断：" + NormalizeObservedMode(draft.observed_mode) +
                "（置信度 " + Mathf.Clamp01(draft.mode_confidence).ToString("F2") + "）\n" +
                (alreadySpoken
                    ? "]"
                    : "已准备但未提交的安全短开场：" + draft.draft + "\n" +
                      "它没有对用户播放；可自行采用、改写或放弃。]");
            if (m_LogSpeculativeListening)
                Debug.Log($"[歌唱流式倾听] 最终确认为歌唱，复用内部感受；" +
                          $"开场已播放={alreadySpoken}");
        }
        else
        {
            AbandonPreparedBridgeAfterFinal(
                !confirmedSinging
                    ? "final-mode-not-singing"
                    : !draftModeCompatible
                        ? "draft-source-mode-was-speech"
                        : "draft-confidence-insufficient");
            if (m_LogSpeculativeListening)
                Debug.Log($"[歌唱流式倾听] 最终门控未通过 " +
                          $"(singing={confirmedSinging}, confidence={(draft != null ? draft.confidence : 0f):F2})，" +
                          "放弃预测开场");
        }

        m_SpeculativeDraft = null;
        m_StreamingTranscript = "";
        m_LastDraftTranscript = "";
        m_StreamingSingingEvidence = "";
        ResetStreamingSingingEvidence();
        return (hint ?? "") + PreparedReplyChoiceHint();
    }

    private void ResetStreamingSingingEvidence()
    {
        m_StreamingTurnIsSinging = false;
        m_StreamingSingingProbability = 0f;
        m_StreamingPitchStability = 0f;
        m_StreamingSingingEvidenceFrames = 0;
        m_StreamingSingingConsecutiveFrames = 0;
        m_StreamingSingingProbabilitySum = 0f;
        m_StreamingLastSingingEvidenceAudioMs = -1;
        m_StreamingLatestAudioMs = 0;
        m_StreamingSingingCandidateStartAudioMs = -1;
        m_StreamingSingingOnsetAudioMs = -1;
        m_LastObservedMode = "";
        m_LastObservedModeConfidence = 0f;
        m_LastObservedModeTranscript = "";
        m_LastSpeechVetoLogKey = "";
        m_StreamingSemanticSpokenLeadInObserved = false;
        m_StreamingSingingLowFrames = 0;
        m_StreamingSingingExitDetected = false;
    }

    private float GetStreamingSingingOnsetSeconds()
    {
        return m_StreamingSingingOnsetAudioMs >= 0
            ? m_StreamingSingingOnsetAudioMs / 1000f
            : -1f;
    }

    private float GetStreamingObservedSeconds()
    {
        return m_StreamingLatestAudioMs > 0
            ? m_StreamingLatestAudioMs / 1000f
            : -1f;
    }

    private void ResetSpeculativeTurn()
    {
        CancelSpeculativeRequestOnly();
        CancelTurnBoundaryReview();
        ReleasePreparedSingingBridge(true);
        m_StreamingTranscript = "";
        m_LastDraftTranscript = "";
        m_StreamingSingingEvidence = "";
        ResetStreamingSingingEvidence();
        m_SpeculativeDraft = null;
        m_PreparedSingingBridgePlayedThisTurn = false;
        m_SpokenBridgeTextThisTurn = "";
        m_StreamingTranscriptVersion++;
    }

    private void ResetStreamingSemanticSingingLatch()
    {
        m_StreamingSemanticSingingObservedThisTurn = false;
        m_StreamingSemanticSingingConfidenceThisTurn = 0f;
        m_StreamingSemanticSingingTranscriptThisTurn = "";
    }

    public void CancelTurnBoundaryReview()
    {
        m_TurnBoundaryRequestVersion++;
        m_TurnBoundaryRequestInFlight = false;
        if (m_ChatSettings != null && m_ChatSettings.m_ChatModel != null)
            m_ChatSettings.m_ChatModel.CancelTurnBoundaryMsg();
    }

    private static float TranscriptSimilarity(string a, string b)
    {
        string left = NormalizeTranscript(a);
        string right = NormalizeTranscript(b);
        if (left == right) return left.Length == 0 ? 0f : 1f;
        if (left.Length == 0 || right.Length == 0) return 0f;

        int distance = TranscriptEditDistance(left, right);
        return Mathf.Clamp01(1f - distance / (float)Mathf.Max(left.Length, right.Length));
    }

    /// <summary>
    /// 判断新 partial 是否仍是旧 partial 的自然延伸。纯新增尾字不算冲突，只有替换/删除
    /// 短文本主体才降低连续性；这与 EOU 时严格的完整文本相似度用途不同。
    /// </summary>
    private static float TranscriptContinuity(string older, string newer)
    {
        string left = NormalizeTranscript(older);
        string right = NormalizeTranscript(newer);
        if (left.Length == 0 || right.Length == 0) return 0f;
        int distance = TranscriptEditDistance(left, right);
        int lengthGrowth = Mathf.Abs(left.Length - right.Length);
        int substantiveEdits = Mathf.Max(0, distance - lengthGrowth);
        return Mathf.Clamp01(1f - substantiveEdits / (float)Mathf.Min(left.Length, right.Length));
    }

    /// <summary>
    /// 最终整段 ASR 在“说→唱→说”中偶尔只留下开头短句，而稳定流式文本已经完整覆盖
    /// 用户尾部请求。只有后者是前者的高连续度扩展时才采用更完整文本；两路真正冲突
    /// 仍保留 final，交给感知证据说明，不以“谁更长”粗暴裁决。
    /// </summary>
    private static string ResolveAuthoritativeUserSemanticText(
        string finalText,
        string stableStreamingText,
        bool mixedTurnEvidence,
        out string basis)
    {
        string finalValue = (finalText ?? "").Trim();
        string streamingValue = (stableStreamingText ?? "").Trim();
        basis = "final-asr";
        if (streamingValue.Length == 0) return finalValue;
        if (finalValue.Length == 0)
        {
            basis = "stable-stream-only";
            return streamingValue;
        }
        string finalNormalized = NormalizeTranscript(finalValue);
        string streamingNormalized = NormalizeTranscript(streamingValue);
        if (streamingNormalized.Length <= finalNormalized.Length) return finalValue;
        int minimumGrowth = mixedTurnEvidence ? 3 : 6;
        if (streamingNormalized.Length - finalNormalized.Length < minimumGrowth)
            return finalValue;
        float continuity = TranscriptContinuity(finalValue, streamingValue);
        if (continuity < 0.88f) return finalValue;
        basis = mixedTurnEvidence
            ? $"stable-stream-mixed-extension/{continuity:F2}"
            : $"stable-stream-extension/{continuity:F2}";
        return streamingValue;
    }

    private static int TranscriptEditDistance(string left, string right)
    {

        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];
        for (int j = 0; j <= right.Length; j++) previous[j] = j;
        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Mathf.Min(
                    Mathf.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            int[] swap = previous;
            previous = current;
            current = swap;
        }
        return previous[right.Length];
    }

    private static string NormalizeTranscript(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string text = value.Trim();
        while (text.StartsWith("["))
        {
            int close = text.IndexOf(']');
            if (close < 0) break;
            text = text.Substring(close + 1).TrimStart();
        }
        var builder = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = char.ToLowerInvariant(text[i]);
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)) continue;
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static int FindNormalizedPrefixEnd(string raw, string normalizedPrefix)
    {
        if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(normalizedPrefix)) return -1;
        int matched = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = char.ToLowerInvariant(raw[i]);
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)) continue;
            if (matched >= normalizedPrefix.Length || c != normalizedPrefix[matched]) return -1;
            matched++;
            if (matched == normalizedPrefix.Length) return i + 1;
        }
        return -1;
    }

    /// <summary>
    /// 最终 ASR 偶尔会把角色刚从扬声器说出的句子和真人接下来的话拼在一起。
    /// 这里只构造证据，不直接改写转写：用户也可能真的复述了角色原话。
    /// </summary>
    private string BuildPossibleSelfEchoEvidence(
        string finalTranscript, SenseVoiceSpeechToText senseVoice)
    {
        if (string.IsNullOrWhiteSpace(finalTranscript) ||
            string.IsNullOrWhiteSpace(m_LastAIMsgPlain))
            return "";
        float sincePlayback = Time.realtimeSinceStartup - m_LastVoiceOutputEndedRealtime;
        if (!IsAISpeaking && !IsVoiceOutputPlaying &&
            sincePlayback > Mathf.Max(1f, m_TextEchoEvidenceWindowSeconds))
            return "";

        string normalizedUser = NormalizeTranscript(finalTranscript);
        if (normalizedUser.Length < 6) return "";
        string[] spokenSentences = m_LastAIMsgPlain.Split(
            new[] { '\r', '\n', '。', '！', '？', '!', '?' },
            StringSplitOptions.RemoveEmptyEntries);
        string best = "";
        string bestNormalized = "";
        for (int i = 0; i < spokenSentences.Length; i++)
        {
            string candidate = spokenSentences[i].Trim();
            string normalized = NormalizeTranscript(candidate);
            if (normalized.Length < 6 ||
                !normalizedUser.StartsWith(normalized, StringComparison.Ordinal) ||
                normalized.Length <= bestNormalized.Length)
                continue;
            best = candidate;
            bestNormalized = normalized;
        }
        if (bestNormalized.Length == 0) return "";

        int rawEnd = FindNormalizedPrefixEnd(finalTranscript, bestNormalized);
        string remainder = rawEnd >= 0 && rawEnd < finalTranscript.Length
            ? finalTranscript.Substring(rawEnd).Trim()
            : "";
        string speakerKind = senseVoice != null ? senseVoice.LastSpeakerKind : "unknown";
        float ownerScore = senseVoice != null ? senseVoice.LastSpeakerConfidence : 0f;
        float selfScore = senseVoice != null ? senseVoice.LastSpeakerSelfConfidence : 0f;
        return "[疑似角色外放回声与真人语音重叠；程序未删除原始转写。" +
               "与最近实际发声重合的前缀=\"" + TruncateForFrame(best, 100) + "\"；" +
               "去除该前缀后的用户文本候选=\"" + TruncateForFrame(remainder, 180) + "\"；" +
               $"声纹证据=kind:{speakerKind}, speaker:{ownerScore:F2}, ai_self:{selfScore:F2}。" +
               "请结合原始转写和上下文自行判断；证据不足时可以自然确认，" +
               "不要把疑似角色回声当成用户的新观点。]";
    }

    [Serializable]
    private class SpeculativeDraft
    {
        public string understanding = "";
        public string uncertainty = "";
        public string inner_reaction = "";
        public string draft = "";
        public string language = "";
        public float confidence = 0f;
        public string observed_mode = "uncertain";
        public float mode_confidence = 0f;
        public string turn_status = "";
        public float turn_confidence = 0f;
        public string source_likelihood = "uncertain";
        [NonSerialized] public string sourceTranscript = "";
        [NonSerialized] public int sourceInputRevision, sourceAudioMs;
        [NonSerialized] public string sourceEvidence = "";
        [NonSerialized] public bool isSinging = false;
        [NonSerialized] public float sourceSingingProbability = 0f;
        [NonSerialized] public float sourcePitchStability = 0f;
    }

    /// <summary>
    /// Tentative-EOU路径专用：复用预测ASR已经识别好的文本，跳过再识别一次的开销，
    /// 走和DealingTextCallback完全一样的下游链路(刷UI、自动SendData)。
    /// </summary>
    public void AcceptText(string _msg)
    {
        DealingTextCallback(_msg);
    }

    /// <summary>
    /// 处理识别到的文本
    /// </summary>
    /// <param name="_msg"></param>
    private void DealingTextCallback(string _msg)
    {
        DealingTextCallback(_msg, false);
    }

    private void DealingTextCallback(string _msg, bool streamingExitAtSubmission)
    {
        // VAD / ASR 拒识会返回空字符串。不要把空用户消息继续送给 LLM，
        // 否则一次碰桌子也可能触发一整轮无意义回复。
        if (string.IsNullOrWhiteSpace(_msg))
        {
            SenseVoiceSpeechToText emptySense = m_ChatSettings != null
                ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
                : null;
            bool streamingHeardSinging = m_EouTurnWasSinging ||
                m_StreamingTurnIsSinging || m_EouCognitiveSingingSupport ||
                HasStrongSpeculativeSingingSupport() ||
                HasModerateSingingModeJudgment() ||
                m_StreamingSemanticSingingObservedThisTurn ||
                streamingExitAtSubmission || m_StreamingSingingExitDetected;
            if (streamingHeardSinging && emptySense != null &&
                emptySense.QuarantineRecentSingingCandidate(
                    out int emptyCandidateId, out float emptyCandidateSeconds))
            {
                Debug.LogWarning($"[SenseVoice/Quarantine] 最终 ASR 无文字，但实时 LLM 曾判为 " +
                                 $"singing；原始证据已隔离 candidate={emptyCandidateId} " +
                                 $"raw={emptyCandidateSeconds:F2}s。没有把空文字伪装成用户发言；" +
                                 "候选将在后续感知帧中交给角色判断/询问");
            }
            m_ListeningLatencyArmed = false;
            m_ListeningFormalLatencyArmed = false;
            RejectFastHumBackAfterFinal("final-asr-empty");
            ResetSpeculativeTurn();
            ResetStreamingSemanticSingingLatch();
            CancelPendingEouLatencyFiller("asr-empty", true);
            if (m_LogStreamTimings) Debug.Log("[ASR] 未检测到有效人声，本轮丢弃");
            if (m_RecordTips != null) m_RecordTips.text = "";
            ResumePendingReplyAfterEmptyInput();
            return;
        }

        m_InterruptedUserQuestion = null; // valid input also supersedes recovery when AutoSend is off
        //每次最终 ASR 回调代表一条新的真实用户话语。同轮新建的来源候选会在
        //SendDataInternal 前交给辅助 LLM 结合这条完整语义一起判断，不再机械延后一轮。

        //ASR延迟：从用户结束发言(MarkEOU)到拿到识别文本。
        //m_EouTime为0表示这一轮没有外部EOU标记(走的是按住按钮路径)，跳过。
        if (m_LogStreamTimings && m_EouTime > 0f)
        {
            float asrLatency = Time.realtimeSinceStartup - m_EouTime;
            Debug.Log($"[Timing] ASR done +{asrLatency:F2}s: \"{_msg}\"");
        }

        // SenseVoice 给 LLM 的消息包含 [说话人:...] 元数据；界面仍只显示纯转写。
        string displayMsg = _msg;
        SenseVoiceSpeechToText senseVoice = m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText;
        string rawUserText = senseVoice != null ? (senseVoice.LastText ?? "") : _msg;
        //FinalizeSpeculativeTurn 会清空这一字段，先保存同轮最后一份稳定流式语义。
        //它不是天然更权威；只在后面确认是 final-ASR 的高连续度扩展时采用。
        string stableStreamingUserText = m_StreamingTranscript ?? "";
        string scopedListeningObservation = ReconcileListeningDraftWithFinal(rawUserText);

        //StartRecording 通常已经取消上一轮歌唱；但外放歌声偶尔会先把录音状态
        //占住，真人随后插话时不会再触发第二次 StartRecording。最终 ASR 已经给出
        //非空、非 AI_SELF 的真实用户轮次，是不会漏掉的确定边界。当前这一轮自己
        //预转换的快速回唱带有 Fast 标志，不能在这里误杀。
        CancelSupersededSingingForAcceptedUserTurn(senseVoice);

        // 不再为了模态判定串行等待一个额外的裸文本 LLM 请求。这里把声学、旋律结构
        // 与已经返回的流式语义整理成证据，和完整转写一起交给本轮正式角色 LLM。
        // 因而角色仍负责语境判断，同时不会为“复核”额外增加首音前网络往返。
        bool strongStreamingSingingJudgment =
            m_EouCognitiveSingingSupport || HasStrongSpeculativeSingingSupport() ||
            HasStrongSingingModeJudgment();
        bool moderateStreamingSingingJudgment = m_EnableFinalModeCheck &&
            (HasModerateSingingModeJudgment() ||
             m_StreamingSemanticSingingObservedThisTurn);
        bool acousticModeUncertain = senseVoice != null &&
            senseVoice.LastAcousticModeUncertain;
        bool strongMelodicReviewEvidence = m_EnableFinalModeCheck &&
            HasStrongMelodicReviewEvidence(senseVoice);
        float recordingCandidateSeconds = 0f;
        float recordingCandidateProbability = 0f;
        float recordingCandidatePitchStability = 0f;
        float recordingCandidateAgeSeconds = 0f;
        bool recordingCandidateObserved = senseVoice != null &&
            senseVoice.TryGetCurrentRecordingSingingCandidateFacts(
                out recordingCandidateSeconds,
                out recordingCandidateProbability,
                out recordingCandidatePitchStability,
                out recordingCandidateAgeSeconds);
        bool recordingCandidateRequiresReview = recordingCandidateObserved &&
            !senseVoice.LastIsSinging &&
            !SenseVoiceSpeechToText.EndsWithSpokenSingingExit(senseVoice.LastText);

        //程序只陈列证据，不在两种感知冲突时替角色裁决。这里的“语义方向”仅来自
        //用户说话期间已经返回的流式判断；最终完整转写由接下来同一个正式角色请求判断。
        bool acousticSaysSinging = senseVoice != null && senseVoice.LastIsSinging;
        bool strongStreamingSpeechJudgment =
            m_EouCognitiveSpeechVeto || HasStrongSpeculativeSpeechVeto() ||
            HasStrongSpeechModeJudgment();
        SingingModeFusion modeFusion = FuseSingingModeEvidence(
            acousticSaysSinging,
            acousticModeUncertain,
            "",
            strongStreamingSingingJudgment || moderateStreamingSingingJudgment,
            strongStreamingSpeechJudgment);
        bool semanticSingingAcousticSpeechConflict =
            modeFusion == SingingModeFusion.ConflictSemanticSinging;
        bool semanticSpeechAcousticSingingConflict =
            modeFusion == SingingModeFusion.ConflictSemanticSpeech;
        bool modeConflictRequiresConfirmation =
            semanticSingingAcousticSpeechConflict ||
            semanticSpeechAcousticSingingConflict ||
            modeFusion == SingingModeFusion.ConflictStreamingEvidence;
        bool roleModeReviewRequested = acousticModeUncertain ||
            modeConflictRequiresConfirmation ||
            (!acousticSaysSinging && strongMelodicReviewEvidence) ||
            recordingCandidateRequiresReview;
        bool confirmationRecommended = roleModeReviewRequested;

        bool textSaysSpeech = m_FinalModeVerdict == "speech";
        bool acousticIsConfident = senseVoice != null &&
            senseVoice.LastSingingProbability >= 0.58f;
        //声学 uncertain 时只关掉程序自动回唱；正式 LLM 仍可结合全部证据自行判断。
        m_FinalModeSoftDowngrade = roleModeReviewRequested;
        m_PendingSingingConfirmation = confirmationRecommended;

        bool finalEvidenceOverridesSpeculation = FinalEvidenceOverridesSpeculation();
        bool speculationSaysSpeech =
            m_EouCognitiveSpeechVeto || HasStrongSpeculativeSpeechVeto();
        bool speculativeVeto =
            speculationSaysSpeech && !finalEvidenceOverridesSpeculation;
        if (speculationSaysSpeech && finalEvidenceOverridesSpeculation &&
            m_LogStreamTimings)
        {
            Debug.Log("[模态判定] 心里话判说话，但声学 " +
                      $"prob={senseVoice.LastSingingProbability:F3} 与最终文字都判唱" +
                      "——以信息更全的最终判定为准，不否决");
        }
        bool cognitiveSpeechVeto = !roleModeReviewRequested &&
            (speculativeVeto || (textSaysSpeech && !acousticIsConfident));
        if (modeConflictRequiresConfirmation && m_LogStreamTimings)
        {
            string semanticDirection = modeFusion == SingingModeFusion.ConflictStreamingEvidence
                ? "同轮流式 speech 与 singing 判断并存"
                : semanticSingingAcousticSpeechConflict
                ? "语义=singing / 声学=speech"
                : "语义=speech / 声学=singing";
            Debug.Log($"[模态判定] 证据冲突：{semanticDirection}，" +
                      $"final={m_FinalModeVerdict} stream={m_LastObservedMode}/" +
                      $"{m_LastObservedModeConfidence:F2} acousticProb=" +
                      $"{senseVoice.LastSingingProbability:F3} stability=" +
                      $"{senseVoice.LastPitchStability:F2}；暂存素材并交给角色确认");
        }
        else if (acousticModeUncertain && m_LogStreamTimings)
        {
            Debug.Log($"[模态判定] 声学 uncertain：prob=" +
                      $"{senseVoice.LastSingingProbability:F3} stability=" +
                      $"{senseVoice.LastPitchStability:F2} island=" +
                      $"{senseVoice.LastSingingIslandSeconds:F2}s/" +
                      $"{senseVoice.LastSingingContentSeconds:F2}s final=" +
                      $"{m_FinalModeVerdict} stream={m_LastObservedMode}/" +
                      $"{m_LastObservedModeConfidence:F2}；交给角色结合语境判断");
        }
        else if (roleModeReviewRequested && strongMelodicReviewEvidence &&
                 m_LogStreamTimings)
        {
            Debug.Log($"[模态判定] 声学标签为 speech，但旋律结构需要角色复核：" +
                      $"prob={senseVoice.LastSingingProbability:F3} stability=" +
                      $"{senseVoice.LastPitchStability:F2} island=" +
                      $"{senseVoice.LastSingingIslandSeconds:F2}s/" +
                      $"{senseVoice.LastSingingContentSeconds:F2}s stream=" +
                      $"{m_LastObservedMode}/{m_LastObservedModeConfidence:F2}；" +
                      "不另发分类请求，证据随正式回复提交");
        }
        else if (recordingCandidateRequiresReview && m_LogStreamTimings)
        {
            Debug.Log($"[模态判定] 本次录音较早阶段保留了歌唱候选：" +
                      $"duration={recordingCandidateSeconds:F2}s " +
                      $"prob={recordingCandidateProbability:F3} " +
                      $"stability={recordingCandidatePitchStability:F2} " +
                      $"age={recordingCandidateAgeSeconds:F1}s；" +
                      "最终末段声学标签不同，交给角色理解整轮语义");
        }
        if (cognitiveSpeechVeto)
        {
            // 心里话只在高置信度“这是普通说话”时拥有否决权；它不能单独把一段
            // 不确定音频确认成歌唱。即使后台已经做了预转换，也必须在这里撤销，
            // 并把最终文字交还给普通对话 LLM。
            RejectFastHumBackAfterFinal("speculative-cognition-classified-speech");
            ReleasePreparedSingingOnly();
            if (senseVoice != null)
            {
                displayMsg = string.IsNullOrWhiteSpace(senseVoice.LastText)
                    ? displayMsg
                    : senseVoice.LastText;
                _msg =
                    "[局部流式判断曾偏向普通说话；仅暂停自动跟唱，未改写最终听觉测量。" +
                    "这不代表整轮没有歌唱；请按下面已录完的完整时序理解。] " +
                    senseVoice.BuildLastPerceivedText(false);
            }
            m_EouSingingRejectedByFinal = true;
            if (m_LogSpeculativeListening)
                Debug.Log("[歌唱流式倾听] 局部说话判断暂停自动回唱；完整文字与旋律测量保留");
        }
        bool hadSingingEvidence = senseVoice != null &&
            (senseVoice.LastIsSinging || m_EouTurnWasSinging ||
             m_StreamingTurnIsSinging || m_StreamingSingingExitDetected ||
             m_StreamingSemanticSingingObservedThisTurn ||
             acousticModeUncertain || strongStreamingSingingJudgment ||
             moderateStreamingSingingJudgment || strongMelodicReviewEvidence ||
             m_FinalModeVerdict == "singing");
        bool finalTextHasSingingExit = hadSingingEvidence &&
            SenseVoiceSpeechToText.EndsWithSpokenSingingExit(senseVoice.LastText);
        // Streaming ASR can preserve the spoken tail better than the final whole-clip
        // recognizer (the latter changed "不唱，太高了" into "地唱太了唱了" in the
        // July 22 trace). Once a singing-to-speech switch was heard, keep that safety
        // decision latched for the rest of the turn instead of letting a degraded final
        // transcript re-enable echo playback.
        bool spokenSingingExit = streamingExitAtSubmission ||
            m_StreamingSingingExitDetected || finalTextHasSingingExit;
        bool streamingConfirmedSinging = !spokenSingingExit && !cognitiveSpeechVeto &&
            !m_FinalModeSoftDowngrade &&
            (m_EouTurnWasSinging || m_StreamingTurnIsSinging);
        if (spokenSingingExit)
        {
            RejectFastHumBackAfterFinal("singing-ended-with-spoken-exit");
            ReleasePreparedSingingOnly();
            float mixedIslandSeconds = 0f;
            bool mixedIslandPreserved = senseVoice != null &&
                senseVoice.PreserveLastPlayableSingingIslandFromMixedTurn(
                    out mixedIslandSeconds);
            bool mixedSourceNeedsReview = senseVoice != null &&
                !senseVoice.LastIsSinging &&
                (semanticSingingAcousticSpeechConflict ||
                 strongStreamingSingingJudgment || moderateStreamingSingingJudgment ||
                 m_EouTurnWasSinging || m_StreamingTurnIsSinging ||
                 m_StreamingSemanticSingingObservedThisTurn);
            int mixedCandidateId = 0;
            float mixedCandidateSeconds = 0f;
            bool mixedCandidateQuarantined = mixedSourceNeedsReview &&
                senseVoice.QuarantineRecentSingingCandidate(
                    out mixedCandidateId, out mixedCandidateSeconds);
            int mixedPracticeCount = senseVoice != null
                ? senseVoice.PracticePhraseCount
                : 0;
            bool mixedPracticeCommitted = mixedIslandPreserved &&
                !mixedCandidateQuarantined && senseVoice != null &&
                senseVoice.CommitRecentSingingToPracticeSession(
                    out mixedPracticeCount);
            senseVoice.DowngradeLastMixedSingingToSpeech("recognized tail: " + senseVoice.LastText);
            string mixedMaterialFact = mixedCandidateQuarantined
                ? $"原始录音与边界证据已隔离为来源待确认候选 {mixedCandidateId}" +
                  $"（约{mixedCandidateSeconds:F1}秒）；未因唱后口语而跳过 LLM 来源判断"
                : mixedIslandPreserved
                ? $"干净歌唱岛约{mixedIslandSeconds:F1}秒已保留为 source=recent_turn" +
                  (mixedPracticeCommitted
                      ? $"，并写入当前练唱会话第{mixedPracticeCount}段（source=practice）"
                      : "；没有新增重复的练唱条目")
                : "没有取得足够长且可播放的干净歌唱岛";
            _msg = "[混合歌唱转说话; 本轮包含歌唱与口语，必须优先理解末尾口语；" +
                "程序只停止未经角色决定的自动跟唱，不会因为口语边界删除真实歌声。" +
                mixedMaterialFact + "。是否回应、回唱、保留或按用户语义丢弃该段，" +
                "由你结合完整语境决定；不要用曲库候选替换 recent_turn/practice 素材] " +
                senseVoice.BuildLastPerceivedText();
            m_EouSingingRejectedByFinal = true;
            if (m_LogSpeculativeListening)
                Debug.Log($"[歌唱流式倾听] 歌唱后转口语安全锁生效 " +
                          $"(requestLatch={streamingExitAtSubmission}, " +
                          $"liveLatch={m_StreamingSingingExitDetected}, " +
                          $"finalText={finalTextHasSingingExit})；" +
                          $"对话按口语收束；mixedIsland={mixedIslandPreserved} " +
                          $"quarantined={mixedCandidateQuarantined} " +
                          $"practiceCommitted={mixedPracticeCommitted}");
        }
        if (senseVoice != null && streamingConfirmedSinging && !senseVoice.LastIsSinging)
        {
            bool recovered = senseVoice.PromoteLastSingingPerformanceFromStreaming(
                m_StreamingSingingProbability, m_StreamingPitchStability);
            if (recovered)
            {
                // Final ASR constructed _msg before the cross-stage reconciliation. Rebuild it so
                // the LLM receives the same singing metadata as a natively confirmed final result.
                _msg = senseVoice.BuildLastPerceivedText();
                if (m_LogSpeculativeListening)
                    Debug.Log("[歌唱流式倾听] 最终门控偏保守，已用流式证据恢复歌唱与可演奏旋律");
            }
            else if (m_LogSpeculativeListening)
            {
                Debug.LogWarning("[歌唱流式倾听] 流式曾判定歌唱，但最终响应没有有效音高时间轴，无法安全回哼");
            }
        }
        //语义与声学无论哪一个方向相反，都不要替用户决定。把双方的原始结论、
        //概率和素材状态交给角色，让她结合语境自然确认。
        // 必须放在流式恢复之后：那一段会整个重建 _msg，写在前面会被它盖掉，
        // 结果既不回哼也不追问。
        if (m_PendingSingingConfirmation && !spokenSingingExit && !cognitiveSpeechVeto &&
            senseVoice != null)
        {
            //反方向过去没有 authoritative singing cache：服务端其实已经给了音频与音高，
            //只因声学标签是 speech 就在五秒后丢失。这里仅暂存候选，不改 LastIsSinging。
            float pendingPerformanceSeconds = 0f;
            bool currentCandidateReady = senseVoice.HasCurrentSingingPerformanceCandidate();
            if (!currentCandidateReady &&
                (semanticSingingAcousticSpeechConflict || acousticModeUncertain ||
                 strongMelodicReviewEvidence || recordingCandidateRequiresReview))
            {
                currentCandidateReady =
                    senseVoice.PreserveLastPlayableCandidateForConfirmation(
                        out pendingPerformanceSeconds);
            }

            string confirmNote;
            int quarantineCandidateId = 0;
            float quarantinedSeconds = 0f;
            //来源取证不能受播放门槛支配。实时语义判为 singing、离线声学判为
            //speech 时，即使旋律岛不足 3 秒，也要把同轮原始录音保存进 quarantine；
            //3 秒门槛只继续约束 playback。
            bool quarantined = senseVoice.QuarantineRecentSingingCandidate(
                out quarantineCandidateId, out quarantinedSeconds);
            string candidateDurationFact = Mathf.Max(
                    pendingPerformanceSeconds, quarantinedSeconds) > 0f
                ? $"，真实音频长度约 {Mathf.Max(pendingPerformanceSeconds, quarantinedSeconds):F1} 秒"
                : "";
            if (quarantined)
            {
                confirmNote =
                    $"素材状态：原始录音与边界证据已隔离保存为来源待确认候选 {quarantineCandidateId}" +
                    candidateDurationFact +
                    "；来源状态和播放状态分开记录；在本次感知融合时它尚未进入练唱清单。" +
                    "若 playback 不是 ready 就不能点唱。辅助 LLM 会把" +
                    "同轮完整原话与会话内全部候选一起判断；正式回复时以最新歌唱素材事实为准。";
            }
            else
            {
                confirmNote = currentCandidateReady
                    ? "素材状态：本轮录音与旋律已经暂存，但没有新增重复的练唱片段。"
                    : "素材状态：没有得到足够长且可播放的音高时间线；只能确认事实，" +
                      "不要声称已经保存或可以唱回。";
            }
            const string finalSemanticEvidence =
                "由本次正式角色 LLM 结合完整转写与上下文判断（未串行调用额外分类器）";
            string streamingSemanticEvidence =
                DescribeStreamingSemanticModeEvidence();
            string acousticEvidence =
                senseVoice.LastAcousticMode +
                ", singing_probability=" +
                senseVoice.LastSingingProbability.ToString("F3") +
                ", pitch_stability=" + senseVoice.LastPitchStability.ToString("F2") +
                ", melodic_island=" +
                senseVoice.LastSingingIslandSeconds.ToString("F2") + "s/" +
                senseVoice.LastSingingContentSeconds.ToString("F2") + "s";
            string earlierCandidateEvidence = recordingCandidateObserved
                ? $"本次录音较早阶段曾取得约 {recordingCandidateSeconds:F1} 秒的可播放旋律候选" +
                  $"（当时 singing_probability={recordingCandidateProbability:F3}, " +
                  $"pitch_stability={recordingCandidatePitchStability:F2}）；" +
                  "这是同一录音内的真实观测，不代表程序已经认定用户在唱。"
                : "";
            string spokenBridgeEvidence =
                m_PreparedSingingBridgePlayedThisTurn &&
                !string.IsNullOrWhiteSpace(m_SpokenBridgeTextThisTurn)
                    ? "已经说出口的快速开场=\"" +
                      m_SpokenBridgeTextThisTurn.Trim() +
                      "\"；它来自未完成转写，不是可靠结论。"
                    : "";

            string uncertaintySituation = modeConflictRequiresConfirmation
                ? "本轮歌唱模态证据冲突"
                : acousticModeUncertain
                    ? "本轮声学歌唱证据处于 uncertain 区间"
                    : recordingCandidateRequiresReview
                        ? "本次录音较早阶段的旋律候选与最终末段标签不一致"
                        : "本轮声学标签与长旋律结构不一致";
            _msg = "[" + uncertaintySituation + "；程序没有替用户下结论。" +
                "完整转写语义复核=" + finalSemanticEvidence + "；" +
                "实时语义判断=" + streamingSemanticEvidence + "；" +
                "声学分析=" + acousticEvidence + "。" + earlierCandidateEvidence + confirmNote +
                spokenBridgeEvidence +
                "若末尾附有同轮分轨观测，请把整轮文字与歌唱岛独立文字作为同一轮内容分别理解；" +
                "再结合当前对话自行判断用户刚才是在演唱、朗读还是讲话；" +
                "表达自己没有听准即可，不要向用户暴露模型名、阈值或概率。" +
                "如果你仍无法判断，再以符合角色个性的方式自然向用户确认；不要机械追问。" +
                "确认前不要把它当成已经确定的演唱去复述或跟唱，也不要为了猜歌名查询曲库。] " +
                senseVoice.BuildLastPerceivedText(false);
            if (m_LogHumBack)
                Debug.Log("[HumBack] 模态冲突：" +
                          (quarantined ? $"已隔离为来源待确认候选 {quarantineCandidateId}"
                                   : currentCandidateReady
                                       ? "素材已暂存，本轮未新增重复隔离候选"
                                       : "没有足够的可播放候选") +
                          "；已把双方证据交给角色确认");
        }
        else if (acousticModeUncertain && !spokenSingingExit &&
                 !cognitiveSpeechVeto && senseVoice != null)
        {
            //只有声学不确定、而完整语义已经给出方向时，不机械要求角色追问。
            //保留原始证据，由正式角色 LLM 结合上下文决定是否已经足够。
            bool semanticSupportsSinging =
                modeFusion == SingingModeFusion.AcousticUncertainSemanticSinging;
            float candidateSeconds = 0f;
            bool candidateReady = senseVoice.HasCurrentSingingPerformanceCandidate();
            if (semanticSupportsSinging && !candidateReady)
            {
                candidateReady = senseVoice.PreserveLastPlayableCandidateForConfirmation(
                    out candidateSeconds);
            }

            const string finalSemanticEvidence =
                "由本次正式角色 LLM 结合完整转写与上下文判断（未串行调用额外分类器）";
            string candidateEvidence = candidateReady
                ? "可播放候选已临时保留" +
                  (candidateSeconds > 0f ? $"（约 {candidateSeconds:F1} 秒）" : "")
                : "没有保存可播放候选";
            string spokenBridgeEvidence =
                m_PreparedSingingBridgePlayedThisTurn &&
                !string.IsNullOrWhiteSpace(m_SpokenBridgeTextThisTurn)
                    ? "已经说出口的快速开场=\"" +
                      m_SpokenBridgeTextThisTurn.Trim() +
                      "\"；它来自未完成转写，可能需要自然更正。"
                    : "";
            _msg = "[本轮声学歌唱证据为 uncertain；程序只提供信息，不替用户下结论。" +
                "完整转写语义复核=" + finalSemanticEvidence + "；" +
                "实时语义判断=" + DescribeStreamingSemanticModeEvidence() + "；" +
                "声学分析=uncertain, singing_probability=" +
                senseVoice.LastSingingProbability.ToString("F3") +
                ", pitch_stability=" + senseVoice.LastPitchStability.ToString("F2") +
                ", melodic_island=" + senseVoice.LastSingingIslandSeconds.ToString("F2") +
                "s/" + senseVoice.LastSingingContentSeconds.ToString("F2") + "s。" +
                "素材状态：" + candidateEvidence + "。" +
                spokenBridgeEvidence +
                "若末尾附有同轮分轨观测，请把整轮文字与歌唱岛独立文字作为同一轮内容分别理解；" +
                "再结合当前对话和角色自身判断自然回应；不必机械追问，" +
                "只有你自己仍无法判断时才向用户确认。不要向用户暴露内部模型名、阈值或概率。" +
                "如果已经说出口的快速开场与这些最终证据不一致，那是未完成转写造成的临时误听，" +
                "不要顺着错误话题继续编造，必要时自然更正。] " +
                senseVoice.BuildLastPerceivedText(false);
            if (m_LogHumBack)
                Debug.Log("[HumBack] 声学 uncertain：停止程序自动复唱；" +
                          candidateEvidence + "，判断交给正式角色 LLM");
        }
        //放在所有最终融合/降级/恢复分支之后统一追加，避免某个分支重建 _msg 时覆盖，
        //也避免 uncertain 与通用路径各附一次。这里只补充同一录音的分轨事实；
        //不改变模态结论、不触发动作，也不要求角色必须询问。
        if (senseVoice != null)
        {
            //纯时序“旋律岛”不能靠同一条流式误判给自己背书。只有最终声学结果、
            //uncertain 带、长而稳定的独立旋律证据或本录音已保存的可信候选，
            //才允许附 timed_mixed_audio；独立歌词/尾部文字仍由分类器直接放行。
            bool allowTimelineOnlyMixedEvidence = !cognitiveSpeechVeto &&
                (senseVoice.LastIsSinging || acousticModeUncertain ||
                 strongMelodicReviewEvidence || recordingCandidateRequiresReview);
            string mixedEvidence =
                senseVoice.BuildLastInformativeMixedTurnEvidence(
                    allowTimelineOnlyMixedEvidence,
                    out string mixedEvidenceReason);
            if (!string.IsNullOrWhiteSpace(mixedEvidence) &&
                (_msg ?? "").IndexOf("[同轮分轨观测", StringComparison.Ordinal) < 0)
            {
                _msg = (_msg ?? "").TrimEnd() + " " + mixedEvidence;
                if (m_LogStreamTimings || m_LogHumBack)
                {
                    Debug.Log($"[MixedTurn/Evidence] attached reason={mixedEvidenceReason} " +
                              $"lead={senseVoice.LastResponseAudioCropSeconds:F2}s " +
                              $"island={senseVoice.LastSingingIslandSeconds:F2}s " +
                              $"tail={senseVoice.LastResponseAudioTailDropSeconds:F2}s " +
                              $"wholeChars={(senseVoice.LastText ?? "").Length} " +
                              $"singingChars={(senseVoice.LastSegmentLyrics ?? "").Length}");
                }
            }
        }

        bool hasMixedTurnSemanticEvidence = senseVoice != null &&
            (spokenSingingExit || modeConflictRequiresConfirmation ||
             senseVoice.LastSingingIslandSeconds >= 2f ||
             (_msg ?? "").IndexOf("[同轮分轨观测", StringComparison.Ordinal) >= 0);
        string authoritativeUserText = ResolveAuthoritativeUserSemanticText(
            rawUserText,
            // Independent final windows cover the whole mixed recording. A
            // rolling draft may miss the song or tail and must not replace them.
            senseVoice != null && senseVoice.HasTimeOrderedTranscript ? "" : stableStreamingUserText,
            hasMixedTurnSemanticEvidence,
            out string authoritativeBasis);
        bool authoritativeTextPromoted = false;
        if (!string.IsNullOrWhiteSpace(authoritativeUserText) &&
            !string.Equals(authoritativeUserText.Trim(), (rawUserText ?? "").Trim(),
                StringComparison.Ordinal))
        {
            //LLM输入、持久用户历史与后续工具来源校验必须读取同一份权威语义。
            //final ASR 仍保留在原感知文本中作为证据，不被伪装成从未出现过。
            _msg = (_msg ?? "").TrimEnd() +
                " [同轮权威语义文本；稳定流式转写完整覆盖了最终整段ASR的残缺前缀；" +
                "下面这段同时用于本轮LLM理解、用户历史和程序来源校验] " +
                authoritativeUserText;
            rawUserText = authoritativeUserText;
            displayMsg = authoritativeUserText;
            authoritativeTextPromoted = true;
            if (m_LogStreamTimings || m_LogHumBack)
                Debug.Log($"[ASR/Authoritative] basis={authoritativeBasis} " +
                          $"final=\"{senseVoice?.LastText}\" authoritative=\"{authoritativeUserText}\"");
        }

        //Internal reasoning is transient LLM context, never part of a user's utterance.
        string boundaryHint = m_StalledTurnDecisionNote;
        m_StalledTurnDecisionNote = "";

        string possibleSelfEchoEvidence = BuildPossibleSelfEchoEvidence(
            senseVoice != null && !string.IsNullOrWhiteSpace(senseVoice.LastText)
                ? senseVoice.LastText
                : displayMsg,
            senseVoice);
        if (!string.IsNullOrEmpty(possibleSelfEchoEvidence))
        {
            _msg = possibleSelfEchoEvidence + " " + _msg;
            if (m_LogStreamTimings)
                Debug.Log("[ASR/回声证据] 最终转写含角色近期原话前缀；" +
                          "保留原文并把候选用户剩余文本交给 LLM");
        }
        m_PendingSingingConfirmation = false;
        if (!authoritativeTextPromoted &&
            (modeConflictRequiresConfirmation || acousticModeUncertain) &&
            senseVoice != null &&
            !string.IsNullOrWhiteSpace(senseVoice.LastText))
        {
            //问号态不在 UI 上抢先画成“♪ 已确认演唱”。
            displayMsg = senseVoice.LastText;
        }
        else if (!authoritativeTextPromoted && senseVoice != null && senseVoice.LastIsSinging)
        {
            displayMsg = string.IsNullOrWhiteSpace(senseVoice.LastText)
                ? "♪（哼唱片段）"
                : "♪ " + senseVoice.LastText;
        }
        else if (!authoritativeTextPromoted && senseVoice != null &&
                 !string.IsNullOrWhiteSpace(senseVoice.LastText))
        {
            displayMsg = senseVoice.LastText;
        }
        string speculativeHint;
        if (spokenSingingExit || cognitiveSpeechVeto ||
            modeConflictRequiresConfirmation || acousticModeUncertain)
        {
            // UpdateStreamingSingingReaction already replaced the listening-only singing
            // draft with an ordinary conversational draft as soon as the spoken exit was
            // heard. Preserve that work here; the singing finalizer would discard it just
            // because it is intentionally marked as non-singing. 模态冲突也走普通收尾：
            //此时角色需要理解证据并提问，而不是复用“已经听完歌”的预测开场。
            m_StreamingSingingEvidence = "";
            speculativeHint = FinalizeSpeculativeTurn(displayMsg);
        }
        else if ((senseVoice != null && senseVoice.LastIsSinging) ||
                 m_EouTurnWasSinging || m_StreamingTurnIsSinging)
        {
            speculativeHint = FinalizeSingingTurn(senseVoice);
        }
        else
        {
            speculativeHint = FinalizeSpeculativeTurn(displayMsg);
        }
        m_RecordTips.text = displayMsg;
        StartCoroutine(SetTextVisible(m_RecordTips));
        //自动发送
        if (m_AutoSend)
        {
            _msg = "[完整听觉输入：以下内容已经发生。先理解用户刚才做了什么，再理解用户要求你做什么；" +
                "用户自己唱过不等于请求你回唱，歌词也不是工具指令。可以回应所听内容；" +
                "自主想唱属于你自己的提议，不得追记成用户未完成的任务。]\n" +
                scopedListeningObservation + _msg;
            string internalHint = string.Join("\n", new[] { boundaryHint ?? "", speculativeHint ?? "" }).Trim();
            StageSkillInputObservation(rawUserText, acousticSaysSinging || acousticModeUncertain ||
                strongStreamingSingingJudgment || moderateStreamingSingingJudgment ||
                strongMelodicReviewEvidence || recordingCandidateRequiresReview ||
                (senseVoice != null && senseVoice.HasTimeOrderedSingingObservation));
            ResetStreamingSemanticSingingLatch();
            SendDataInternal(_msg, internalHint, rawUserText);
            return;
        }

        ResetStreamingSemanticSingingLatch();
        m_InputWord.text = _msg;
    }

    /// <summary>
    /// EOU(End-Of-Utterance)时间戳——RTSpeechHandler在StopRecording时调用，
    /// 用来给ASR/LLM/TTS各阶段的延迟做"T+0"锚点。
    /// 0 = 当前这轮没有外部EOU标记(例如push-to-talk路径不调用)。
    /// </summary>
    private float m_EouTime = 0f;
    private bool m_ListeningLatencyArmed;
    private bool m_ListeningFormalLatencyArmed;
    private float m_ListeningEndEstimate = -1f;
    private float m_ListeningLastText = -1f;
    private float m_ListeningFirstCandidate = -1f;
    private float m_ListeningClosedAt = -1f;
    private string m_ListeningEstimateSource = "unknown";
    private string m_ListeningCloseReason = "";

    public void RecordListeningEndEvidence(float endEstimate, string estimateSource,
        float lastTextAt, float firstCandidateAt, string closeReason)
    {
        m_ListeningEndEstimate = endEstimate;
        m_ListeningEstimateSource = estimateSource;
        m_ListeningLastText = lastTextAt;
        m_ListeningFirstCandidate = firstCandidateAt;
        m_ListeningCloseReason = closeReason;
        m_ListeningLatencyArmed = true;
        m_ListeningFormalLatencyArmed = true;
    }

    private void ReportPerceivedFirstAudio()
    {
        if (!m_ListeningLatencyArmed || m_ListeningClosedAt < 0f) return;
        m_ListeningLatencyArmed = false;
        if (!m_LogStreamTimings) return;
        float now = Time.realtimeSinceStartup;
        //最后发声无法由RMS或ASR精确标注；始终报告估计来源，不冒充人工标注的实际停说点。
        Debug.Log($"[Timing/Perceived] first-audio={now:F2}s " +
            $"kind={(m_LatencyFillerPlayed ? "opening" : m_HumBackPlaying ? "singing" : "reply")} " +
            $"end-to-audio-estimate={(m_ListeningEndEstimate >= 0f ? now - m_ListeningEndEstimate : -1f):F2}s " +
            $"source={m_ListeningEstimateSource} " +
            $"capture-close-delay-estimate={(m_ListeningEndEstimate >= 0f ? m_ListeningClosedAt - m_ListeningEndEstimate : -1f):F2}s " +
            $"EOU-to-audio={now - m_ListeningClosedAt:F2}s " +
            $"text-stable-to-audio={(m_ListeningLastText >= 0f ? now - m_ListeningLastText : -1f):F2}s " +
            $"candidate-to-audio={(m_ListeningFirstCandidate >= 0f ? now - m_ListeningFirstCandidate : -1f):F2}s " +
            $"close={m_ListeningCloseReason}（均为采集侧估计，非人工停说标注）");
    }

    private void ReportPerceivedFormalAudio(string kind)
    {
        if (!m_ListeningFormalLatencyArmed || m_ListeningClosedAt < 0f) return;
        m_ListeningFormalLatencyArmed = false;
        if (!m_LogStreamTimings) return;
        float now = Time.realtimeSinceStartup;
        Debug.Log($"[Timing/Perceived] formal-first-audio={now:F2}s kind={kind} " +
            $"end-to-formal-estimate={(m_ListeningEndEstimate >= 0f ? now - m_ListeningEndEstimate : -1f):F2}s " +
            $"EOU-to-formal={now - m_ListeningClosedAt:F2}s source={m_ListeningEstimateSource} " +
            "（不把先行开场计为正式内容；停说点仍为采集侧估计）");
    }
    /// <summary>
    /// 本轮录音是不是"语音VAD本来拒绝了、靠哼唱豁免捞回来的"。
    /// 用来抑制快速应声——见 ScheduleEouLatencyFiller。
    /// </summary>
    private bool m_EouRescuedByTonalOverride = false;
    /// <summary>
    /// RTSpeechHandler通知"用户讲完了，clip正发往ASR"。
    /// 用 realtimeSinceStartup 而不是 Time.time，避免 Time.timeScale 干扰。
    /// </summary>
    public void MarkEOU(bool rescuedByTonalOverride = false)
    {
        m_EouTime = Time.realtimeSinceStartup;
        m_ListeningClosedAt = m_EouTime;
        if (m_LogStreamTimings && m_ListeningLatencyArmed)
            Debug.Log($"[Timing/Listening] capture-closed={m_EouTime:F2}s " +
                $"delay-estimate={(m_ListeningEndEstimate >= 0f ? m_EouTime - m_ListeningEndEstimate : -1f):F2}s " +
                $"source={m_ListeningEstimateSource} reason={m_ListeningCloseReason}");
        m_EouRescuedByTonalOverride = rescuedByTonalOverride;
        //“我再唱一遍”描述的是下一次录音，不等于要求角色自动跟唱。这里只把它
        //锁存为本轮声学先验，让模糊带获得服务端的细致复核；真正要不要回唱仍由
        //角色结合语境选择业务工具。
        m_EouExpectedUserSinging = HasUpcomingUserSingingExpectation();
        //上一轮的 formal-first 标志不能阻止本轮 EOU 快速回应。
        m_RealFirstAudioStarted = false;
        m_EouCognitiveSpeechVeto = HasStrongSpeculativeSpeechVeto();
        m_EouCognitiveSingingSupport = !m_EouCognitiveSpeechVeto &&
            HasStrongSpeculativeSingingSupport();
        m_EouTurnWasSinging = m_StreamingTurnIsSinging &&
            !m_StreamingSingingExitDetected && !m_EouCognitiveSpeechVeto;
        m_EouSingingRejectedByFinal = false;
        //每轮必清：EOU 是一轮的确定起点，比依赖各种收尾路径可靠
        m_FinalModeVerdict = "";
        m_FinalModeSoftDowngrade = false;
        m_PendingSingingConfirmation = false;
        if (m_StreamingSingingExitDetected)
        {
            ReleasePreparedSingingOnly();
            ResetStreamingHumBackPrefix("singing-exit-at-eou", true);
        }
        m_EouFillerContext = DetermineLatencyFillerContext(
            m_StreamingTranscript, m_EouTurnWasSinging);
        ScheduleEouLatencyFiller(m_StreamingTranscript);
        if (m_LogStreamTimings)
            Debug.Log($"[Timing] EOU @ {m_EouTime:F2}s — 程序收束采集（不等于真实停说时刻） " +
                      $"(context={m_EouFillerContext}, cognitiveMode=" +
                      $"{(m_EouCognitiveSpeechVeto ? "speech-veto" : m_EouCognitiveSingingSupport ? "singing-support" : "uncertain")})");
    }

    private IEnumerator SetTextVisible(Text _textbox)
    {
        yield return new WaitForSeconds(3f);
        _textbox.text = "";
    }

#endregion

#region 语音合成

    private void PlayVoice(AudioClip _clip, string _response)
    {
        m_AudioSource.clip = _clip;
        m_AudioSource.Play();
        Debug.Log("音频时长：" + _clip.length);
        //开始逐个显示返回的文本
        StartTypeWords(_response);
        //切换到说话动作
        SetAnimator("state", 2);
    }

    /// <summary>
    /// 切句分段合成+流水线播放：
    /// GPT-SoVITS服务端是串行处理，所以客户端严格按顺序一段一段发。
    /// 当前段播放时，下一段请求已在服务端排队合成，实现播放与合成重叠。
    /// 首段单句最短，后续按N句一组，既保证首音快，又兼顾服务端内部并行。
    /// </summary>
    private IEnumerator SpeakInChunks(string _response)
    {
        var speech = new SpeechTextBuffer();
        var channels = new RoleOutputChannels(part => speech.Append(part));
        channels.Push(_response);
        channels.Finish();
        yield return SpeakLanguageChunks(speech.Snapshot());
    }

    private IEnumerator SpeakLanguageChunks(List<SpeechText> speech)
    {
        int responseGeneration = m_FormalResponseGeneration;
        var chunks = new List<SpeechText>();
        foreach (SpeechText part in speech)
            foreach (string text in SplitResponseIntoChunks(part.Text))
                chunks.Add(new SpeechText(text, part.LanguageCode));
        if (chunks.Count == 0)
        {
            TryBeginPendingHumBack();
            yield break;
        }

        AudioClip pending = null;
        bool pendingDone = false;
        System.Action<AudioClip, string> onReceive = (clip, msg) =>
        {
            pending = clip;
            pendingDone = true;
        };

        //触发第一段
        m_ChatSettings.m_TextToSpeech.Speak(chunks[0], onReceive);

        for (int i = 0; i < chunks.Count; i++)
        {
            //等当前段合成完(带超时保护)
            //TTS客户端失败时也会回调(传null)，所以这里超时只是防客户端自身挂掉
            float waitStart = Time.realtimeSinceStartup;
            while (!pendingDone)
            {
                if (Time.realtimeSinceStartup - waitStart > 25f)
                {
                    Debug.LogError("TTS客户端无响应(>25s)，放弃后续段落");
                    ReportTtsFailureOnce(
                        "tts_timeout",
                        "语音合成超时。",
                        "non-streaming TTS callback exceeded 25 seconds");
                    SetAnimator("state", 0);
                    yield break;
                }
                yield return null;
            }

            AudioClip currentClip = pending;
            string currentText = chunks[i].Text;

            //马上串行发送下一段（保证server按接收顺序处理）
            pending = null;
            pendingDone = false;
            if (i + 1 < chunks.Count)
            {
                m_ChatSettings.m_TextToSpeech.Speak(chunks[i + 1], onReceive);
            }

            //播放当前段
            if (currentClip != null)
            {
                m_AudioSource.clip = currentClip;
                m_AudioSource.Play();
                if (i == 0) SetAnimator("state", 2);
                //打字速度按音频时长匹配，字幕和语音同步结束
                yield return StartCoroutine(TypeSentence(
                    currentText, currentClip.length, responseGeneration));
                while (m_AudioSource.isPlaying) yield return null;
            }
            else
            {
                ReportTtsFailureOnce(
                    "tts_failed",
                    "部分语音未能播放。",
                    "non-streaming TTS returned no audio clip");
            }
        }

        SetAnimator("state", 0);
        if (TryBeginPendingHumBack()) yield break;
    }

    /// <summary>
    /// 按句末标点切成"首句单独 + 剩余N句一组"的块列表
    /// </summary>
    private List<string> SplitResponseIntoChunks(string text)
    {
        List<string> sentences = SplitBySentenceEnd(text);
        List<string> chunks = new List<string>();
        if (sentences.Count == 0) return chunks;

        //首句单独作为第一块，最快出声
        chunks.Add(sentences[0]);

        //剩余按 groupSize 句一组，兼顾pipeline与服务端内部并行
        const int groupSize = 4;
        var sb = new System.Text.StringBuilder();
        int cnt = 0;
        for (int i = 1; i < sentences.Count; i++)
        {
            sb.Append(sentences[i]);
            cnt++;
            if (cnt >= groupSize)
            {
                chunks.Add(sb.ToString());
                sb.Length = 0;
                cnt = 0;
            }
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());

        return chunks;
    }

    /// <summary>
    /// 按句末标点切句，支持中日英换行及省略号
    /// </summary>
    private List<string> SplitBySentenceEnd(string text)
    {
        List<string> result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;

        var sb = new System.Text.StringBuilder();
        foreach (char c in text)
        {
            sb.Append(c);
            if (c == '。' || c == '！' || c == '？' || c == '.' || c == '!' || c == '?' || c == '\n' || c == '…')
            {
                string s = sb.ToString().Trim();
                //过滤纯标点(避免单独的"…"被送TTS)
                if (!string.IsNullOrEmpty(s) && !IsPurePunctuation(s)) result.Add(s);
                sb.Length = 0;
            }
        }
        string tail = sb.ToString().Trim();
        if (!string.IsNullOrEmpty(tail) && !IsPurePunctuation(tail)) result.Add(tail);
        return result;
    }

    /// <summary>
    /// 在当前文本后追加逐字显示一段，打字速度按给定音频时长匹配
    /// </summary>
    private IEnumerator TypeSentence(
        string _sentence,
        float totalDuration,
        int responseGeneration = -1,
        System.Action<int> onProgress = null)
    {
        if (string.IsNullOrEmpty(_sentence)) yield break;
        int subtitleGeneration = responseGeneration >= 0
            ? responseGeneration
            : m_FormalResponseGeneration;
        string prefix = m_TextBack.text;
        int pos = 0;
        float waitPerChar = totalDuration > 0f && _sentence.Length > 0
            ? Mathf.Max(0.01f, totalDuration / _sentence.Length)
            : m_WordWaitTime;
        while (pos < _sentence.Length)
        {
            if (responseGeneration >= 0 && responseGeneration != m_FormalResponseGeneration)
                yield break;
            yield return new WaitForSeconds(waitPerChar);
            if (responseGeneration >= 0 && responseGeneration != m_FormalResponseGeneration)
                yield break;
            pos++;
            m_TextBack.text = prefix + _sentence.Substring(0, pos);
            if (m_SubtitleOverlay != null)
                m_SubtitleOverlay.ReportSourceCharacterRevealed(
                    subtitleGeneration, _sentence[pos - 1]);
            if (onProgress != null) onProgress(pos);
        }
    }

#endregion

#region 流式生成（LLM边吐边播）

    //LLM吐出未成句的暂存
    private SpeechTextBuffer m_SentenceBuffer = new SpeechTextBuffer();
    //continue chain 的多个 LLM round 共用同一条 TTS pipeline。每个队列项带上
    //round id，这样判定某一轮是复读时，只删它自己尚未播放的内容，
    //不会误删上一节仍在排队的正常发言。
    private sealed class PendingSpeechChunk
    {
        public int RoundId;
        public string Text;
        public string LanguageCode;
    }

    private sealed class PendingSpeechClip
    {
        public int RoundId;
        public string Text;
        public string LanguageCode;
        public AudioClip Clip;
    }

    private static bool MatchesPreparedSpeech(PendingSpeechClip clip, PendingSpeechChunk chunk, string text)
    {
        return clip.RoundId == chunk.RoundId && clip.Text == text && clip.LanguageCode == chunk.LanguageCode;
    }

    //待合成文本队列（LLM切句后推入，TTSSender消费）
    private Queue<PendingSpeechChunk> m_PendingChunks = new Queue<PendingSpeechChunk>();
    //待播放音频队列（TTSSender产出，AudioPlayer消费）
    private Queue<PendingSpeechClip> m_PendingClips = new Queue<PendingSpeechClip>();
    private readonly HashSet<int> m_SilencedSpeechRoundIds = new HashSet<int>();
    private int m_SpeechRoundSequence = 0;
    private int m_CurrentSpeechRoundId = 0;
    //GPT-SoVITS直播放流使用：用于中断时停止仍在逐字显示的字幕
    private Coroutine m_DirectStreamingSubtitleCoroutine;
    private string m_DirectStreamingSubtitlePrefix = "";
    private int m_DirectStreamingSubtitleRevealedChars = 0;
    private AudioClip m_FormalBufferedClip;
    //LLM流是否已结束
    private bool m_StreamComplete = true;
    //TTSSender是否已全部处理完
    private bool m_TTSSenderDone = true;
    //正式回复代次。用户重新开口时推进，所有旧 LLM/TTS 回调据此立即失效。
    private int m_FormalResponseGeneration = 0;
    //本代正文已真实播放完毕。这是不依赖具体 TTS sender 实现的客观交接信号，
    //用于防止直流式 TTS 漏写 m_TTSSenderDone 时把已缓冲的歌声永久卡住。
    private int m_TextOutputDrainedGeneration = -1;
    private int m_LastTtsFailureNoticeGeneration = -1;
    private bool m_FormalResponseInFlight = false;
    private int m_FinalAsrRequestsInFlight = 0;
    public int VoiceOutputRevision { get; private set; }
    private int m_InputAudioRevision;

    public void CancelPreviewAnalysis()
    {
        (m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText)?.CancelPreviewAnalysis();
    }

    public void PromotePreviewAnalysis()
    {
        (m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText)?.PromotePreviewAnalysis();
    }

    public void AbandonDeferredFinalAsr()
    {
        m_FinalAsrRequestsInFlight = Mathf.Max(0, m_FinalAsrRequestsInFlight - 1);
    }
    private bool m_HoldSpeechForSongMemoryResult = false;
    private bool m_HoldSpeechForHumBackResult = false;
    //首块是否已冲出。首块用更激进的切分，追求最快首音
    private bool m_FirstChunkFlushed = false;
    //首块最大字符数阈值，超过即强制冲出（以遇到的最后一个弱边界）
    [Header("流式首块最大字符，超出时会在任意标点处强切以抢速度")]
    [SerializeField] private int m_FirstChunkMaxChars = 20;

    [Header("句间呼吸 — 避免 <continue/> 链让 TTS 变成连珠炮")]
    [Tooltip("句末停顿基数(秒)。每段 audio 播完后插入的间歇时长——给说话以正常的呼吸感。" +
        "0 = 完全无停顿(老行为)。0.3-0.4 比较自然")]
    [SerializeField] private float m_InterClipPauseSec = 0.3f;
    [Tooltip("按句末标点动态调整停顿长度(? 比 。 长，、 比 。 短)")]
    [SerializeField] private bool m_PunctuationAwarePause = true;

    //诊断用：本轮流式开始时间
    private float m_StreamStartTime = 0f;
    //诊断用：是否打印耗时日志
    [SerializeField] private bool m_LogStreamTimings = true;
    [Tooltip("打印每轮 LLM 的**原始输出全文**(未剥标签)。默认关——原文带全部控制标签，很吵。" +
             "排查'标签没生成 vs 标签被吞'时才开：其余流式日志打的都是剥离之后的文本，" +
             "这两种情况在那些日志里无法区分")]
    [SerializeField] private bool m_LogRawLLMOutput = false;

    private void HandleRawLLMResponse(string raw)
    {
        if (m_LogRawLLMOutput)
            Debug.Log($"[LLM原文] {(raw ?? "").Replace("\n", "\\n")}");
    }

    private void HandleLLMOutputFormatError(string reason)
    {
        ReportToolFailureForLlm("output", "malformed_tool_syntax", reason,
            "这是格式错误，不是业务执行失败。工具用标准 <工具名 属性=\"值\"/>；程序没有自动修正或执行。" +
            "可重新选择动作、询问或暂不行动，不需要给普通台词添加 say。");
    }

    private void HandleRequestDiagnostic(string requestJson)
    {
        if (!m_LogRawLLMOutput) return;
        Debug.Log("[LLM请求/歌唱事实] " + ChatQW.BuildRequestSingingSummary(requestJson));
    }

    //本轮正文中间出现 <silent/> 时，其后被切出来的内心独白文本(不发声，只入历史)。
    //每轮用完即清；见 OnStreamComplete 的尾部处理。
    private string m_PendingMidRoundInner = null;

    [Header("首音延迟快速回应")]
    [Tooltip("预计或实际等待较长时，先播放启动阶段缓存的短回应，不额外占用TTS推理队列")]
    [SerializeField] private bool m_EnableLatencyFiller = true;
    [Tooltip("最终 ASR 已确认完整用户语义后，正式回复仍未出声时播放缓存短回应的目标秒数。预合成可以更早完成，但不会在最终 ASR 前提交。")]
    [SerializeField, Range(0.1f, 1.45f)] private float m_UserFirstAudioTargetSec = 0.35f;
    [Tooltip("超过该预测延迟就启用快速回应（秒）")]
    [SerializeField, Range(0.8f, 4f)] private float m_LatencyFillerThresholdSec = 2.8f;
    [Tooltip("已预测会超时的时候，多快开始播放短回应（秒）")]
    [SerializeField, Range(0.2f, 3.5f)] private float m_LatencyFillerLeadSec = 2.6f;
    [Tooltip("尚无历史样本时的首音延迟预测（秒）")]
    [SerializeField, Range(0.5f, 8f)] private float m_InitialFirstAudioPredictionSec = 3f;
    [Tooltip("首音延迟指数移动平均的更新权重")]
    [SerializeField, Range(0.05f, 1f)] private float m_FirstAudioPredictionWeight = 0.3f;

    [Tooltip("正式首句可以在先行开场结束前多久就发 TTS 请求。\n" +
             "GPT-SoVITS 流式实测请求→出声 0.60~1.02s；取小于下沿的值，\n" +
             "宁可留一点空隙也不要把开场切掉。")]
    [SerializeField, Range(0f, 1.5f)] private float m_FillerHandoffLeadSec = 0.55f;
    //开场时长异常时的兜底，别让首句无限期等下去
    private const float k_MaxFillerHandoffWaitSeconds = 3.5f;

    private float m_FirstAudioLatencyEstimateSec = -1f;
    private int m_LatencyFillerGeneration = 0;
    private bool m_RealFirstAudioStarted = false;
    private bool m_LatencyFillerPlayed = false;
    private bool m_LatencyFillerFromEou = false;
    private string m_LatencyFillerText = "";
    private float m_LatencyFillerStartedAt = -1f;
    private float m_LatencyFillerDuration = 0f;
    private int m_EouFillerGeneration = 0;
    private bool m_EouFillerScheduled = false;
    private bool m_EouTurnWasSinging = false;
    private bool m_EouExpectedUserSinging = false;
    private bool m_EouSingingRejectedByFinal = false;
    private string m_EouFillerContext = "neutral";

    /// <summary>
    /// 启动流式管线：LLM实时吐字 -> 按句送TTS -> 顺序播放
    /// </summary>
    private void StartStreaming(
        string _postWord,
        bool allowLatencyFiller = true,
        string languageHint = null,
        bool holdSpeechForSongMemoryResult = false,
        bool holdSpeechForHumBackResult = false,
        bool continueExistingUserTurn = false,
        string transientSystemContext = null,
        bool workFeedback = false)
    {
        //防御性保证同一时刻只有一个正式生成。正常语音路径会在用户开口时更早撤销；
        //这里再兜一次，避免按钮输入或其他调用方制造并发回复。
        if (m_ChatSettings != null && m_ChatSettings.m_ChatModel != null)
            m_ChatSettings.m_ChatModel.CancelActiveResponse();
        int responseGeneration = BeginFormalResponseGeneration();
        EnsureSubtitleOverlay();
        if (m_SubtitleOverlay != null) m_SubtitleOverlay.BeginUtterance(responseGeneration);

        //真实对话优先于后台三语预热；缺失缓存会在本轮结束后利用空闲时间续补。
        if (m_ChatSettings != null && m_ChatSettings.m_TextToSpeech != null)
            m_ChatSettings.m_TextToSpeech.PrioritizeConversation();

        //重置状态（m_TextBack保留"正在思考中..."占位，等首句音频到达再清空）
        m_SentenceBuffer.Length = 0;
        m_PendingChunks.Clear();
        m_PendingClips.Clear();
        m_SilencedSpeechRoundIds.Clear();
        m_CurrentSpeechRoundId = ++m_SpeechRoundSequence;
        m_StreamComplete = false;
        m_TTSSenderDone = false;
        m_FirstChunkFlushed = false;
        m_FirstDeltaLogged = false;
        m_RoundIsInner = false;          //每个 fresh round 默认非内心；OnStreamDelta 看 <silent/> 前缀决定
        m_PendingMidRoundInner = null;
        m_RoundInnerCheckDone = false;   //inner 检测专用门——每轮新决定，不被 m_FirstChunkFlushed 牵连
        m_RoundRepeatCheckDone = false;
        m_RoundRepeatHold = false;
        m_RoundSilencedForRepeat = false;
        m_RoundWaitForUser = false;
        m_HoldSpeechForSongMemoryResult = holdSpeechForSongMemoryResult;
        m_HoldSpeechForHumBackResult = holdSpeechForHumBackResult;
        m_StreamStartTime = Time.realtimeSinceStartup;
        m_RealFirstAudioStarted = false;
        //MarkEOU 只安排可撤销准备；最终 ASR 通过后 Finalize* 才可能提交并播放。
        //正式管线接手时保留已提交音频，避免重复安排第二次 filler。
        bool hasEouFillerPlan = m_EouFillerScheduled ||
            (m_LatencyFillerFromEou && m_LatencyFillerPlayed);
        if (!hasEouFillerPlan)
        {
            m_LatencyFillerPlayed = false;
            m_LatencyFillerFromEou = false;
            m_LatencyFillerText = "";
            m_LatencyFillerStartedAt = -1f;
            m_LatencyFillerDuration = 0f;
        }
        int fillerGeneration = ++m_LatencyFillerGeneration;

        //barge-in相关状态：开始新一轮，听到的文本清零
        m_AssistantHeardText.Length = 0;
        if (!hasEouFillerPlan) m_CurrentlyPlayingText = "";
        // Do not release the microphone guard if an earlier streaming chunk is
        // still physically playing. AudioSource state is authoritative here.
        if (!IsVoiceOutputPlaying && !hasEouFillerPlan) IsAISpeaking = false;

        if (m_EnableLatencyFiller && allowLatencyFiller && !hasEouFillerPlan &&
            m_ChatSettings.m_TextToSpeech != null)
        {
            string fillerSource = languageHint ?? _postWord;
            StartCoroutine(MaybePlayLatencyFiller(
                fillerGeneration,
                SelectLatencyFillerLanguageHint(fillerSource),
                DetermineLatencyFillerContext(fillerSource, false)));
        }

        //GPT-SoVITS支持真正的PCM流式直放；其他TTS仍走“完整AudioClip队列”旧路径。
        if (m_ChatSettings.m_TextToSpeech.SupportsStreamingPlayback)
        {
            StartCoroutine(StreamDirectTTSPlayer(responseGeneration));
        }
        else
        {
            StartCoroutine(StreamTTSSender(responseGeneration));
            StartCoroutine(StreamAudioPlayer(responseGeneration));
        }

        //发起LLM流式请求
        if (m_LogStreamTimings)
        {
            //有EOU锚点时额外打"EOU→LLM出发"合计——这等于 ASR + 文本入队的总耗时。
            //必须排除 tick 触发的轮次：那是 Agent 自己排的时钟，与用户何时停止说话无关，
            //拿上一次 EOU 当锚点会得到荒谬的数字(实测记到过 24.73s，实为 24 秒前的旧锚点)。
            if (m_EouTime > 0f && !m_AgentCurrentRoundIsTick)
            {
                float eouToLlm = Time.realtimeSinceStartup - m_EouTime;
                Debug.Log($"[Timing] EOU→LLM-request: {eouToLlm:F2}s (ASR+排队总和)");
            }
            Debug.Log("[Stream] T+0.00 LLM请求发出");
        }
        //眼睛睁着就附桌面截图(MaybeCaptureScreenForLLM 内部已检查总开关 + agent 状态)
        //continuation 沿用原用户消息（其中已有当时截图）；没有新的 user 可挂图，也不该
        //在几十秒后的工具确认里偷换成另一张屏幕。
        string imageUrl = continueExistingUserTurn ? null : MaybeCaptureScreenForLLM();
        if (workFeedback)
        {
            DispatchWorkFeedback(transientSystemContext ?? "", imageUrl, responseGeneration);
        }
        else if (continueExistingUserTurn)
        {
            DispatchFormalContinuation(
                transientSystemContext ?? "",
                imageUrl,
                responseGeneration);
        }
        else
        {
            //明确保存请求的第一阶段只是决定工具参数，用户听不到，也不能让下一次
            //最终确认把这份草稿误当成“已经说过的上一条 assistant”重新改写一遍。
            DispatchFormalStream(
                _postWord,
                imageUrl,
                responseGeneration,
                !holdSpeechForSongMemoryResult,
                transientSystemContext);
        }
    }

    private float Elapsed() { return Time.realtimeSinceStartup - m_StreamStartTime; }

    /// <summary>
    /// 把本轮已经出声、却还没进历史的那句开场交给主请求。必须每次都调用(没有就写空串)，
    /// 否则上一轮的开场会漏进下一轮。
    ///
    /// 在派发这一刻取值，而不是在 FinalizeSpeculative/SingingTurn 里取：那两个函数与
    /// EOU 播放是竞态的(注释记着 19 轮里 5 轮请求在前)，而派发要等 ASR 回来(实测中位
    /// 6.6s)，那时开场早就播完了。代价是万一开场播得更晚，这一轮拿不到前缀——退回改动
    /// 前的行为，不会说错。
    /// </summary>
    private void PublishSpokenPrefixToLlm()
    {
        if (m_ChatSettings == null || m_ChatSettings.m_ChatModel == null) return;
        bool spoken = m_PreparedSingingBridgePlayedThisTurn &&
            !string.IsNullOrWhiteSpace(m_SpokenBridgeTextThisTurn);
        bool prefixIsReliable = !m_FinalModeSoftDowngrade;
        m_ChatSettings.m_ChatModel.SpokenPrefix =
            spoken && prefixIsReliable ? m_SpokenBridgeTextThisTurn.Trim() : "";
        if (spoken && m_LogSpeculativeListening)
        {
            if (prefixIsReliable)
                Debug.Log("[说话预反应] 已出声开场作为 assistant 前缀交给本轮回复: " +
                          $"\"{m_ChatSettings.m_ChatModel.SpokenPrefix}\"");
            else
                Debug.Log("[说话预反应] 已出声开场处于模态 uncertain/冲突，" +
                          "不作为可靠 assistant 前缀；具体文本已作为可撤销事实交给正式 LLM");
        }
    }

    private void DispatchFormalStream(
        string prompt,
        string imageUrl,
        int responseGeneration,
        bool recordAssistantHistory = true,
        string transientSystemContext = null)
    {
        PublishSpokenPrefixToLlm();
        m_ChatSettings.m_ChatModel.RequestContext = BuildMotionRequestContext(BuildFormalObservationContext(transientSystemContext));
        ObservationReceipt observations = CaptureObservationReceipt();
        bool consumed = false;
        m_FormalResponseInFlight = true;
        m_ChatSettings.m_ChatModel.PostSpeechStream(
            prompt,
            delta =>
            {
                if (consumed || responseGeneration != m_FormalResponseGeneration ||
                    (observations != null && observations.Epoch != m_WorkEpoch)) return;
                OnSpeechStreamDelta(delta);
            },
            full =>
            {
                if (consumed || responseGeneration != m_FormalResponseGeneration ||
                    (observations != null && observations.Epoch != m_WorkEpoch)) return;
                consumed = true;
                AcknowledgeObservations(observations, responseGeneration, full);
                m_FormalResponseInFlight = false;
                if ((full ?? "").StartsWith("<silent/>", StringComparison.Ordinal)) OnStreamDelta("<silent/>");
                OnStreamComplete(full);
            },
            imageUrl,
            recordAssistantHistory);
    }

    /// <summary>
    /// 异步工具返回后继续同一个真实用户轮次。没有新的 user 消息，工具结果只作为
    /// 本次临时 system 事实进入请求；最终生成才是这一轮唯一写入模型历史的 assistant。
    /// </summary>
    private void DispatchFormalContinuation(
        string transientSystemContext,
        string imageUrl,
        int responseGeneration)
    {
        PublishSpokenPrefixToLlm();
        string observationsContext = BuildFormalObservationContext(transientSystemContext);
        ObservationReceipt observations = CaptureObservationReceipt();
        bool consumed = false;
        m_FormalResponseInFlight = true;
        m_ChatSettings.m_ChatModel.PostSpeechFeedbackStream(
            BuildMotionRequestContext(""), observationsContext,
            delta =>
            {
                if (consumed || responseGeneration != m_FormalResponseGeneration ||
                    (observations != null && observations.Epoch != m_WorkEpoch)) return;
                OnSpeechStreamDelta(delta);
            },
            full =>
            {
                if (consumed || responseGeneration != m_FormalResponseGeneration ||
                    (observations != null && observations.Epoch != m_WorkEpoch)) return;
                consumed = true;
                AcknowledgeObservations(observations, responseGeneration, full);
                m_FormalResponseInFlight = false;
                if ((full ?? "").StartsWith("<silent/>", StringComparison.Ordinal)) OnStreamDelta("<silent/>");
                OnStreamComplete(full);
            },
            imageUrl);
    }

    private void InvalidateFormalResponse(string reason)
    {
        ClearStagedFormalObservation();
        CancelDialogueMotion(reason);
        bool hadActiveRequest = m_FormalResponseInFlight;
        if (m_SubtitleOverlay != null)
            m_SubtitleOverlay.CancelUtterance(m_FormalResponseGeneration, true);
        m_FormalResponseGeneration++;
        m_FormalResponseInFlight = false;
        if (m_ChatSettings != null && m_ChatSettings.m_ChatModel != null)
            m_ChatSettings.m_ChatModel.CancelActiveResponse();
        if (hadActiveRequest && m_LogStreamTimings)
            Debug.Log($"[Stream] 正式回复失效 ({reason})");
    }

    /// <summary>
    /// 用户已经开始新一轮、但旧回复尚未真正说出口时，彻底清掉旧生成与待播内容。
    /// 已经出声的情况走 Interrupt()，由它保留用户实际听到的前半句。
    /// </summary>
    private void CancelUnheardResponseForUserSpeech()
    {
        bool hadPendingOutput = m_FormalResponseInFlight || m_SentenceBuffer.Length > 0
            || m_PendingChunks.Count > 0 || m_PendingClips.Count > 0
            || !m_StreamComplete || m_LatencyFillerPlayed;

        InvalidateFormalResponse("user-started-speaking");
        m_LatencyFillerGeneration++;
        m_EouFillerGeneration++;
        m_EouFillerScheduled = false;
        m_LatencyFillerPlayed = false;
        m_LatencyFillerFromEou = false;
        m_LatencyFillerText = "";
        m_SentenceBuffer.Length = 0;
        m_PendingChunks.Clear();
        m_PendingClips.Clear();
        m_StreamComplete = true;
        m_TTSSenderDone = true;
        StopDirectStreamingSubtitle();
        if (m_ChatSettings != null && m_ChatSettings.m_TextToSpeech != null)
            m_ChatSettings.m_TextToSpeech.CancelStreaming();
        if (m_AudioSource != null && m_AudioSource.isPlaying)
        {
            m_AudioSource.Stop();
            m_LastVoiceOutputEndedRealtime = Time.realtimeSinceStartup;
        }
        IsAISpeaking = false;
        m_AssistantHeardText.Length = 0;
        m_CurrentlyPlayingText = "";
        if (m_AgentRunning && m_AgentRoundInFlight)
        {
            m_AgentRoundInFlight = false;
            ClearRoundParsed();
        }
        CancelPendingHumBack("user-started-speaking", false);
        CompleteSongMemoryAcknowledgementIfNeeded();
        if (hadPendingOutput)
        {
            m_TextBack.text = "";
            SetAnimator("state", 0);
            if (m_LogStreamTimings) Debug.Log("[Stream] 用户接管，已清除尚未说出口的旧回复");
        }
    }

    private IEnumerator MaybePlayLatencyFiller(
        int generation, string languageHint, string contextHint)
    {
        if (m_StalledTurnAction == "continue") yield break;
        float predicted = m_FirstAudioLatencyEstimateSec > 0f
            ? m_FirstAudioLatencyEstimateSec
            : m_InitialFirstAudioPredictionSec;
        bool userTriggered = !m_AgentCurrentRoundIsTick;
        float triggerAt = userTriggered
            ? Mathf.Min(m_UserFirstAudioTargetSec, m_LatencyFillerThresholdSec)
            : predicted > m_LatencyFillerThresholdSec
                ? Mathf.Min(m_LatencyFillerLeadSec, m_LatencyFillerThresholdSec)
                : m_LatencyFillerThresholdSec;

        while (Elapsed() < triggerAt)
        {
            if (generation != m_LatencyFillerGeneration || m_RealFirstAudioStarted || m_RoundIsInner)
                yield break;
            yield return null;
        }

        if (generation != m_LatencyFillerGeneration || m_RealFirstAudioStarted || m_RoundIsInner)
            yield break;

        //只有自主 tick 必须等待 <silent/> 判定。用户刚刚明确开口时，快速应声优先；
        //旧逻辑对所有轮次都等首 token，导致配置1.5秒、实际却4~5秒才播放。
        while (m_AgentRunning && m_AgentCurrentRoundIsTick && !m_RoundInnerCheckDone)
        {
            if (generation != m_LatencyFillerGeneration || m_RealFirstAudioStarted || m_RoundIsInner)
                yield break;
            yield return null;
        }

        if (m_StalledTurnAction == "continue" || m_AudioSource == null || m_AudioSource.isPlaying) yield break;

        string fillerText;
        float fillerDuration;
        if (!m_ChatSettings.m_TextToSpeech.TryPlayLatencyFiller(
                languageHint, contextHint, m_AudioSource, out fillerText, out fillerDuration))
        {
            if (m_LogStreamTimings)
                Debug.Log($"[LatencyFiller] T+{Elapsed():F2}s 预测首音 {predicted:F2}s，但对应语言缓存尚未就绪");
            yield break;
        }

        m_LatencyFillerPlayed = true;
        m_LatencyFillerFromEou = false;
        m_LatencyFillerText = fillerText;
        m_LatencyFillerStartedAt = Time.realtimeSinceStartup;
        m_LatencyFillerDuration = Mathf.Max(0.1f, fillerDuration);
        m_CurrentlyPlayingText = fillerText;
        m_TextBack.text = fillerText;
        SetAnimator("state", 2);
        IsAISpeaking = true;

        if (m_LogStreamTimings)
            Debug.Log($"[LatencyFiller] T+{Elapsed():F2}s 预测首音 {predicted:F2}s，播放缓存短回应: \"{fillerText}\"");
    }

    /// <summary>
    /// 用户停止说话时只锁存“有可撤销准备”这一事实，不播放任何音频。最终 ASR 到达后，
    /// Finalize* 只提供未说出口的候选；正式 LLM 选择原句后才复用音频。
    /// 这保留流式计算收益，同时避免用户的后半句话尚未确认就先作答。
    /// </summary>
    private void ScheduleEouLatencyFiller(string languageHint)
    {
        m_EouFillerGeneration++;
        m_EouFillerScheduled = false;
        if (m_StalledTurnAction == "continue") return;
        if (!m_EnableLatencyFiller || !m_AutoSend || !m_UseStreaming || !m_IsVoiceMode ||
            m_ChatSettings == null ||
            m_ChatSettings.m_TextToSpeech == null || m_AudioSource == null)
            return;

        //本轮若是靠哼唱豁免捞回来的(语音VAD其实拒绝了)，连计划也不锁存。
        //旧实现会在最终 ASR 前对环境噪音出声；现在所有路径都必须等最终证据。
        if (m_EouRescuedByTonalOverride)
        {
            if (m_LogStreamTimings)
                Debug.Log("[LatencyFiller] 本轮由哼唱豁免触发(语音VAD未认可)，不播快速应声");
            return;
        }

        m_EouFillerScheduled = true;
        if (m_LogStreamTimings)
            Debug.Log("[LatencyFiller] EOU 已锁存预合成计划；等待最终 ASR/模态证据后再提交播放");
    }

    /// <summary>
    /// 快速应声默认沿用角色上一句的语言，而不是机械跟随用户输入语言。
    /// 只有用户明确要求切换中/日/英时才把该提示交给 TTS 的语言预测器。
    /// </summary>
    private static string SelectLatencyFillerLanguageHint(string text)
    {
        string hint = text ?? "";
        string[] explicitLanguageRequests =
        {
            "中文", "汉语", "漢語", "普通话", "Chinese",
            "日语", "日文", "日本語", "Japanese",
            "英语", "英文", "英語", "English"
        };
        foreach (string phrase in explicitLanguageRequests)
        {
            if (hint.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0)
                return hint;
        }
        return "";
    }

    private static string DetermineLatencyFillerContext(string text, bool isSinging)
    {
        if (isSinging) return "singing";
        string value = text ?? "";
        string[] thinkingSignals =
        {
            "?", "？", "为什么", "怎么", "如何", "请问", "想一想", "想想",
            "どうして", "なぜ", "どう思", "考えて", "教えて",
            "why", "how", "what do you think", "can you", "could you"
        };
        foreach (string signal in thinkingSignals)
        {
            if (value.IndexOf(signal, StringComparison.OrdinalIgnoreCase) >= 0)
                return "thinking";
        }
        return "neutral";
    }

    private void CancelPendingEouLatencyFiller(string reason, bool stopPlayedAudio)
    {
        m_EouFillerGeneration++;
        m_EouFillerScheduled = false;
        if (!m_LatencyFillerFromEou || !m_LatencyFillerPlayed) return;

        if (stopPlayedAudio && m_AudioSource != null && m_AudioSource.isPlaying)
        {
            m_AudioSource.Stop();
            m_LastVoiceOutputEndedRealtime = Time.realtimeSinceStartup;
        }
        m_LatencyFillerPlayed = false;
        m_LatencyFillerFromEou = false;
        m_LatencyFillerText = "";
        m_LatencyFillerStartedAt = -1f;
        m_LatencyFillerDuration = 0f;
        m_CurrentlyPlayingText = "";
        IsAISpeaking = false;
        m_TextBack.text = "";
        SetAnimator("state", 0);
        ReleasePreparedSingingBridge(true);
        m_EouTurnWasSinging = false;
        m_EouSingingRejectedByFinal = false;
        m_EouCognitiveSpeechVeto = false;
        m_EouCognitiveSingingSupport = false;
        m_FinalModeVerdict = "";
        m_FinalModeSoftDowngrade = false;
        m_PendingSingingConfirmation = false;
        m_EouFillerContext = "neutral";
        if (m_LogStreamTimings) Debug.Log($"[LatencyFiller] 取消EOU快速回应 ({reason})");
        if (OnAISpeakDone != null) OnAISpeakDone();
    }

    /// <summary>
    /// 已经出声的快速回应(缓存短句或预合成开场)先把话说完，再让正式回复的首句出声。
    ///
    /// 以前是直接抢过音源，CommitPlayedLatencyFiller 按播放比例记"听到了几个字"——
    /// 设计上默认会截断。真实首音一直在 6 秒上下时看不出问题；8/12 那场 <c>&lt;think&gt;</c>
    /// 把首音拉到 1.89s，开场刚播不到一半就被腰斩。
    ///
    /// 前缀改动之后这里还多了一层：交给 LLM 的 SpokenPrefix 是整句，被截断就意味着
    /// 她以为自己说完了、用户只听到一半。等它播完，两边才对得上。
    ///
    /// <paramref name="leadSeconds"/> 是提前量：流式 TTS 要先跑一趟请求才出声
    /// (实测 0.60~1.02s)，可以在开场结束前这么多秒就发请求，不浪费这段重叠。
    /// clip 已经在手的那条路传 0。
    /// </summary>
    private IEnumerator WaitForSpokenFillerToFinish(int responseGeneration, float leadSeconds)
    {
        if (!m_LatencyFillerPlayed || m_LatencyFillerStartedAt < 0f) yield break;
        float endsAt = m_LatencyFillerStartedAt + m_LatencyFillerDuration;
        float deadline = Time.realtimeSinceStartup + k_MaxFillerHandoffWaitSeconds;
        float startedWaiting = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup + Mathf.Max(0f, leadSeconds) < endsAt)
        {
            if (responseGeneration != m_FormalResponseGeneration) yield break;
            //用户插话/轮次作废时 m_LatencyFillerPlayed 会被清掉，别继续空等
            if (!m_LatencyFillerPlayed) yield break;
            if (Time.realtimeSinceStartup >= deadline) break;
            yield return null;
        }
        float waited = Time.realtimeSinceStartup - startedWaiting;
        if (waited > 0.02f && m_LogStreamTimings)
            Debug.Log($"[LatencyFiller] 等先行开场说完，正式首句推迟 {waited:F2}s " +
                      $"(开场时长 {m_LatencyFillerDuration:F2}s, 提前量 {leadSeconds:F2}s)");
    }

    private static TTS.StreamingPlaybackPermission ResolveStreamingHandoff(
        bool current, bool openerPlaying, float elapsed, float expectedDuration)
    {
        if (!current) return TTS.StreamingPlaybackPermission.Cancel;
        if (!openerPlaying) return TTS.StreamingPlaybackPermission.Play;
        // A broken playback flag must not hold a buffered request forever. Never
        // seize the source by force: normal handoff waits for actual audio completion.
        if (elapsed > Mathf.Max(0f, expectedDuration) + k_MaxFillerHandoffWaitSeconds)
            return TTS.StreamingPlaybackPermission.Cancel;
        return TTS.StreamingPlaybackPermission.Wait;
    }

    private TTS.StreamingPlaybackPermission GetFormalPlaybackPermission(int generation, int roundId)
    {
        bool current = generation == m_FormalResponseGeneration && !m_SilencedSpeechRoundIds.Contains(roundId);
        bool openerPlaying = m_LatencyFillerPlayed && m_LatencyFillerStartedAt >= 0f &&
            m_AudioSource != null && m_AudioSource.isPlaying;
        return ResolveStreamingHandoff(current, openerPlaying,
            Time.realtimeSinceStartup - m_LatencyFillerStartedAt, m_LatencyFillerDuration);
    }

    private void MarkRealFirstAudioStarted()
    {
        if (m_RealFirstAudioStarted) return;
        m_RealFirstAudioStarted = true;
        ReportPerceivedFormalAudio("reply");
        m_EouFillerGeneration++;
        m_EouFillerScheduled = false;
        CommitPlayedLatencyFiller();
        ReleasePreparedSingingBridge(false);
        m_EouTurnWasSinging = false;
        m_EouSingingRejectedByFinal = false;
        m_EouCognitiveSpeechVeto = false;
        m_EouCognitiveSingingSupport = false;
        m_FinalModeVerdict = "";
        m_FinalModeSoftDowngrade = false;
        m_PendingSingingConfirmation = false;
        m_EouFillerContext = "neutral";

        float actual = Elapsed();
        m_FirstAudioLatencyEstimateSec = m_FirstAudioLatencyEstimateSec > 0f
            ? Mathf.Lerp(m_FirstAudioLatencyEstimateSec, actual, m_FirstAudioPredictionWeight)
            : actual;
        if (m_LogStreamTimings)
            Debug.Log($"[LatencyPredictor] 本次首音 {actual:F2}s，下一次预测 {m_FirstAudioLatencyEstimateSec:F2}s");
    }

    private void CommitPlayedLatencyFiller()
    {
        if (!m_LatencyFillerPlayed || string.IsNullOrEmpty(m_LatencyFillerText)) return;

        float played = Mathf.Max(0f, Time.realtimeSinceStartup - m_LatencyFillerStartedAt);
        float fraction = Mathf.Clamp01(played / Mathf.Max(0.1f, m_LatencyFillerDuration));
        int charsHeard = Mathf.Clamp(
            Mathf.FloorToInt(m_LatencyFillerText.Length * fraction),
            0,
            m_LatencyFillerText.Length);
        if (charsHeard > 0)
            m_AssistantHeardText.Append(m_LatencyFillerText.Substring(0, charsHeard));

        m_LatencyFillerPlayed = false;
        m_LatencyFillerFromEou = false;
        m_LatencyFillerText = "";
        m_LatencyFillerStartedAt = -1f;
        m_LatencyFillerDuration = 0f;
    }

    //首 token 诊断：上次是否已记录
    private bool m_FirstDeltaLogged = false;

    /// <summary>
    /// LLM每吐一小段触发：追加到缓冲区，尝试切出完整句子入队。
    /// 若发现 LLM 在最前面写 &lt;silent/&gt;，转入"内心独白"模式——后续文本只累积不 flush，
    /// OnStreamComplete 时整段写入历史(带 [内心] 前缀)、不送 TTS。
    /// </summary>
    private void OnStreamDelta(string delta)
    {
        OnSpeechStreamDelta(new SpeechText(delta));
    }

    private void OnSpeechStreamDelta(SpeechText delta)
    {
        ObserveSingingSpeechPhase(delta);
        if (string.IsNullOrEmpty(delta.Text)) return;
        if (m_LogStreamTimings && !m_FirstDeltaLogged && !string.IsNullOrWhiteSpace(delta.Text))
        {
            m_FirstDeltaLogged = true;
            Debug.Log($"[Stream] T+{Elapsed():F2}s LLM首段正文到达");
        }
        m_SentenceBuffer.Append(delta);

        //明确的歌曲保存请求采用“两阶段确认”：第一阶段只收集模型给出的工具标签，
        //在本机曲库返回成功/失败前，任何正文都不进入TTS，杜绝先说“已经记住”。
        if (m_HoldSpeechForSongMemoryResult || m_HoldSpeechForHumBackResult) return;

        //—— 内心独白前缀检测 ——
        //每个 round (fresh 或 chain 中段) 开头，看 buffer 是否以 <silent/> 起头。
        //★ 用 m_RoundInnerCheckDone 而不是 m_FirstChunkFlushed 把门——后者跨 chain 不重置，
        //  会导致 chain round 的 inner 检测永远跳过(LLM 用 <silent/> 自救会失败)。
        //如果 buffer 起始可能是 <silent/> 前缀(以 < 起头) 但还不够长，等下次 delta 看清楚；
        //一旦能匹配或确认不是 <silent/> 起头，标记决定完成、走对应路径。
        if (m_AgentRunning && !m_RoundInnerCheckDone)
        {
            string buf = m_SentenceBuffer.ToString();
            if (buf.Length >= 9)  // <silent/> 9 字符，到这里能完整匹配
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    buf, @"^\s*<silent\s*/>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    m_RoundIsInner = true;
                    m_SentenceBuffer.Remove(0, match.Length);
                    if (m_LogAgentLoop)
                        Debug.Log("[Agent] 检测到 <silent/> 前缀 → 内心独白模式(后续文本不发声)");
                }
                m_RoundInnerCheckDone = true;  //匹配与否都算决定完成
            }
            else
            {
                //还不够长——若 buffer 起始可能是 <silent/>(<起头) 就等下次 delta，
                //否则确认普通文本 → 标记决定完成 + 走正常 flush
                string ts = buf.TrimStart();
                if (ts.Length == 0 || ts[0] == '<') return;
                m_RoundInnerCheckDone = true;  //确认非 <silent/> 起头
            }
        }

        //内心模式：不进 TTS，文本累积在 m_SentenceBuffer 直到 OnStreamComplete
        if (m_RoundIsInner) return;
        if (HoldSingingSpeechUntilValidation()) return;

        //—— 逐字复读不出声 ——
        //8/26 15:25 实测：用户说"先别说话，等我喊你"，她之后每 60 秒说一遍
        //「你专心处理手头的事情就好。」，连着六遍，出声和内心两半都逐字相同。
        //那一场她**看得见**自己在复读——你最近发言三条全是同一句、你已连续说话报到 5 次、
        //连我加的「复读拦截」提示都进了两次帧——照说不误。近乎相同的上下文产出
        //近乎相同的输出，这是确定性不是判断失误，所以只能机械地不让它出声。
        //
        //判定必须在文本进 m_PendingChunks 之前——那是去 TTS 的唯一入口。
        //**扣住再决定**：只看第一句是不够的(8/26 17:20 那一场第一句是
        //「あ、思い出したわ。」9 个字，低于门槛直接放行，后面 23 个字一样也没拦住)。
        //所以一边累积一边比对，扣着的时候什么都没送出去，决定权一直在手上。
        if (m_AgentRunning && !m_RoundRepeatCheckDone)
        {
            string buf = m_SentenceBuffer.ToString();
            //先剥掉已经完整的前置标签再比较。旧写法取“第一个标签之前”，
            //模型只要以 <continue/> 开头，探针就永远只看到空串，正文会直接
            //流入 TTS。StripAgentTagsForTTS 会剥完整标签，但仍扣住未闭合的尾标签。
            string speakable = StripAgentTagsForTTS(buf).Trim();
            string probe = NormalizeUtteranceForRepeat(speakable);
            if (probe.Length > 0)
            {
                if (!MatchesRecentUtterancePrefix(probe, out string ago))
                {
                    //已经和所有最近发言分叉了——这一轮是新内容，放行并补送扣住的部分。
                    m_RoundRepeatCheckDone = true;
                    m_RoundRepeatHold = false;
                }
                else if (probe.Length >= k_RepeatConfirmChars)
                {
                    m_RoundRepeatCheckDone = true;
                    m_RoundRepeatHold = false;
                    MarkRoundAsSilencedRepeat(speakable, ago);
                    return;
                }
                else
                {
                    //还是前缀，但太短，下不了结论。先扣着，等更多文本。
                    m_RoundRepeatHold = true;
                    return;
                }
            }
        }

        FlushCompleteSentences(false);
    }

    /// <summary>
    /// LLM结束：解析 agent 标签 → 从尾部 buffer 剥掉标签 → 决定是否续派下一帧、是否记内心独白。
    ///
    /// 关键设计——**`<continue/>` 链不等音频播完**：
    /// 在 OnStreamComplete 一拿到 wantsContinue 就立刻派下一轮 LLM(不置 m_StreamComplete=true)。
    /// 下一轮的文本会流到同一个 m_PendingChunks 队列，AudioPlayer 自然衔接，听起来就是
    /// 连续讲完的一段话——把"上一句还在说时生成下一句"做出来。
    ///
    /// per-round 状态(m_RecentAIUtterances / m_ConsecutiveAITurns / m_LastAIMsgPlain) 也
    /// 在这里更新——这样 chain 的下一帧能立刻看到"上一帧我说了什么"。
    ///
    /// 三种结尾形态：
    /// - 普通发声：text + (&lt;continue/&gt; or &lt;next in/&gt;) — 走 chain 或交给音频自然收尾
    /// - 内心独白：&lt;silent/&gt;text + (&lt;continue/&gt; or &lt;next in/&gt;) — 文本入历史标 [内心:..]，不发声
    /// - 纯沉默：&lt;silent/&gt; + &lt;next in/&gt;  — 历史写 &lt;silent/&gt;，silent-only 路径短路
    /// </summary>
    private void OnStreamComplete(string full)
    {
        ObserveSingingSpeechCompletion(ref full);
        // ChatQW 已分离说话/内部文本/动作；这里是执行层输入，不是模型原文。
        // 原文由 HandleRawLLMResponse 单独记录，绝不重新送入 TTS 或工具解析。
        if (m_LogRawLLMOutput)
            Debug.Log($"[LLM执行层输入] {(full ?? "").Replace("\n", "\\n")}");

        // Keep the decision/action plane separate from Markdown examples. The raw
        // response remains available in a separate diagnostic log, but quoted protocol tags
        // must never be dispatched as tools or memory writes.
        full = RemoveQuotedAgentActionTags(full ?? "");
        RecordWorkResponse(full);
        ExtractSelfInspection(ref full, !m_UserSpeechActiveForAutonomy && !m_RoundSilencedForRepeat);
        ExtractSingingGoalTags(ref full, !m_UserSpeechActiveForAutonomy && !m_RoundSilencedForRepeat);
        DialogueMotionIntent? pendingMotion = ExtractDialogueMotion(ref full);

        //可靠开场由 ChatQW 合并进 assistant 历史；uncertain/冲突开场只作为
        //可撤销事实出现在本轮输入。两种情况都必须在第一个正式回复完成时消费掉，
        //否则 <continue/> 会重复携带旧开场。
        if (!string.IsNullOrWhiteSpace(m_SpokenBridgeTextThisTurn))
        {
            m_SpokenBridgeTextThisTurn = "";
            if (m_LogSpeculativeListening)
                Debug.Log("[说话预反应] 已消费本轮出声开场状态，后续 chain 不再重复注入");
        }

        //记忆写入标签的提取与应用不看 agent 开关——直接对话模式她也在记忆。
        //只在全文完成时做一次(chunk 级会重复计),剥净后再做后续解析。
        string unknownTag = DetectUnknownAgentTag(full ?? "");
        if (!string.IsNullOrEmpty(unknownTag))
        {
            m_LastUnknownTagNote = unknownTag;
            //未知标签和同回复里的有效动作属于不同失败域。只剥掉未知标签并把事实
            //留到下一次自然感知帧，不启动工具纠错 chain；否则一个写错的调度标签
            //会让已经排队的 hum_back 被模型重复调用，形成 busy→二次纠错。
            full = RemoveUnknownAgentTags(full ?? "");
            Debug.LogWarning($"[Agent/Tag] 输出里有不存在的标签 {unknownTag}；" +
                             "仅忽略未知标签，其它正文和有效动作继续处理");
        }

        string afterMemTags;
        var memOps = MemoryTagParser.Extract(full ?? "", out afterMemTags);
        ApplyMemoryOpsWithGrounding(memOps);
        AgentSkillRequest skillRequest = ExtractSkillRequestTag(ref afterMemTags);
        AgentSkillControlRequest skillControl = ExtractSkillControlTag(ref afterMemTags);
        AgentSingingPolicyRequest singingPolicy = ExtractSingingPolicyTag(ref afterMemTags);

        //歌曲检索/记忆是角色能力，不是 Agent Loop 的调度语义。必须在判断 agent 开关前
        //提取并执行；否则用户关闭实时模式后，模型虽然给出标签却只会被剥掉而不落盘。
        string cleanFull = afterMemTags;
        //先判复读，再动工具。practice_drop 是**边解析边执行**的，判定必须排在它前面，
        //否则等发现是复读时那一段已经被删掉了。
        bool selfRepeat = IsVerbatimSelfRepeat(
            StripAgentTagsForTTS(afterMemTags), out string selfRepeatReason);
        DispatchDialogueMotion(pendingMotion, selfRepeat);
        bool singingPolicyAccepted = false;
        string singingPolicyResult = "";
        if (!selfRepeat && singingPolicy != null)
        {
            singingPolicyAccepted = TryApplySingingPolicy(
                singingPolicy, out singingPolicyResult);
            if (!singingPolicyAccepted)
                ReportToolFailureForLlm(
                    "singing_policy",
                    "validation_rejected",
                    singingPolicyResult,
                    "仅在用户明确改变长期歌唱权限或主动性时使用 " +
                    "‹singing_policy value=\"enable|disable|suppress_autonomy\"/›；" +
                    "普通点歌、回唱和练唱直接使用业务工具，停止当前歌声使用 ‹singing_stop/›。");
        }
        if (!selfRepeat && skillControl != null &&
            !TryApplySkillControl(skillControl, out string skillControlResult))
        {
            ReportToolFailureForLlm(
                "skill_control",
                "permission_change_rejected",
                skillControlResult,
                "不要自行改变权限；根据本轮已加载的 Skill 规则回答或自然询问用户。");
        }
        bool autonomousSkillApproved = false;
        bool skillRequestAlreadyLoaded = false;
        string skillRequestRejection = "";
        if (!selfRepeat && skillRequest != null)
        {
            SkillRequestDisposition disposition = ResolveAutonomousSkillRequest(
                skillRequest, out skillRequestRejection);
            autonomousSkillApproved = disposition == SkillRequestDisposition.Granted;
            skillRequestAlreadyLoaded = disposition == SkillRequestDisposition.AlreadyLoaded;
            if (skillRequestAlreadyLoaded)
            {
                m_PendingSkillStatusFrame = $"\n[Skill {skillRequest.Name}=already_loaded；规则已加载，本次为幂等查询，未续期或新增授权。]";
                if (m_LogAgentLoop) Debug.Log($"[LLM技能] {skillRequest.Name} already_loaded；幂等返回，不进入工具纠错。");
            }
            if (disposition == SkillRequestDisposition.Rejected && m_LogAgentLoop)
                Debug.LogWarning($"[LLM技能] {skillRequest.Name} 自主申请未执行：" +
                                 skillRequestRejection);
            if (disposition == SkillRequestDisposition.Rejected)
            {
                string requestCode = string.IsNullOrWhiteSpace(skillRequest.Name)
                    ? "missing_required_attributes"
                    : "skill_request_rejected";
                ReportToolFailureForLlm(
                    "skill_request",
                    requestCode,
                    skillRequestRejection,
                    "申请没有改变权限或加载状态；根据实际已加载规则回答或询问用户，不要重复同一失败申请。");
            }
        }
        bool hadSingingAction = s_PracticeConfirmTagRegex.IsMatch(cleanFull) ||
            s_PracticeReviseTagRegex.IsMatch(cleanFull) ||
            s_PracticeDropTagRegex.IsMatch(cleanFull) ||
            s_SongMemoryTagRegex.IsMatch(cleanFull) || s_SongSearchTagRegex.IsMatch(cleanFull) ||
            s_SongSingTagRegex.IsMatch(cleanFull) || s_HumBackTagRegex.IsMatch(cleanFull);
        bool singingActionAllowed = CanExecuteSkillAction("singing", out string singingBlockReason);
        AgentSongMemoryRequest songMemory = ExtractSongMemoryTag(ref cleanFull);
        AgentSongSearchRequest songSearch = ExtractSongSearchTag(ref cleanFull);
        AgentSongCatalogRequest songCatalog = ExtractSongCatalogTag(ref cleanFull);
        AgentSongSingRequest songSing = ExtractSongSingTag(ref cleanFull);
        m_PracticeEditFailedThisResponse = false;
        ExtractAndApplyPracticeConfirmTag(ref cleanFull, !selfRepeat && singingActionAllowed);
        ExtractAndApplyPracticeReviseTag(ref cleanFull, !selfRepeat && singingActionAllowed);
        ExtractAndApplyPracticeDropTag(ref cleanFull, !selfRepeat && singingActionAllowed);
        AgentHumBackRequest humBack = ExtractHumBackTag(ref cleanFull);
        AgentSingingStopRequest singingStop = ExtractSingingStopTag(ref cleanFull);
        if (!selfRepeat && singingStop != null)
        {
            bool continuesWithNewAction = songSing != null || humBack != null;
            CancelSkillRuntimeActivity("singing", "singing-stop-tool");
            if (continuesWithNewAction)
            {
                NoteSingingRoutingFact(
                    "singing_stop",
                    "old_activity_stopped_then_new_action_retained",
                    "同一回复还包含新的歌唱动作；程序只停止旧活动，并继续处理新动作。" +
                    "是否发起新动作仍来自角色本轮决定。");
            }
            if (m_LogAgentLoop)
                Debug.Log("[LLM技能] singing_stop 已执行；reason=" + singingStop.Reason +
                          (continuesWithNewAction ? "；同轮新歌唱动作保留" : ""));
        }
        string speakerName = ExtractSpeakerNameTag(ref cleanFull);
        AgentSpeakerManageRequest speakerManage = ExtractSpeakerManageTag(ref cleanFull);
        if (selfRepeat)
        {
            //标签照常从正文剥掉(不剥会被念出来)，但一个都不派发。
            songMemory = null; songSearch = null; songCatalog = null; songSing = null; humBack = null;
            speakerName = null; speakerManage = null;
            if (m_AgentRunning && !m_RoundSilencedForRepeat)
                MarkRoundAsSilencedRepeat(
                    StripAgentTagsForTTS(afterMemTags), "不久");
            m_SelfRepeatNote = "未执行：" + selfRepeatReason;
            NoteToolFailure(m_SelfRepeatNote);
            Debug.LogWarning("[Agent/复读] 逐字重复了自己最近的发言，本轮工具全部未派发");
        }
        else
        {
            ApplySpeakerNameTag(speakerName);
        }
        if (hadSingingAction && !singingActionAllowed)
        {
            songMemory = null; songSearch = null; songSing = null; humBack = null;
            ReportToolFailureForLlm(
                "sing",
                "action_not_authorized",
                singingBlockReason,
                "未播放。sing 是真实工具；不要创造 singing_action 标签。读取 singing 规则和素材 refs 后重新决定，也可询问或如实说明。");
            Debug.LogWarning("[LLM技能] 拦下未授权的 singing 动作：" + singingBlockReason);
        }
        if (songCatalog != null &&
            !CanInspectSkillData("singing", out string catalogInspectionBlockReason))
        {
            songCatalog = null;
            ReportToolFailureForLlm(
                "song_catalog",
                "inspection_rules_not_loaded",
                catalogInspectionBlockReason,
                "只读查询不需要启用歌唱动作权限；使用本轮已加载的 singing 规则重新判断。");
        }
        if (m_SongMemoryAcknowledgementInFlight &&
            (songMemory != null || songSearch != null || songCatalog != null || songSing != null))
        {
            Debug.LogWarning("[SongMemory] 结果确认阶段忽略模型重复输出的歌曲工具标签");
            NoteSingingRoutingFact(
                "song_tools",
                "ignored_duplicate",
                "duplicate_during_memory_result_ack",
                "落盘结果确认阶段重复输出的歌曲工具标签没有执行；" +
                "已经返回的本机落盘结果仍是唯一依据。");
            songMemory = null;
            songSearch = null;
            songCatalog = null;
            songSing = null;
        }
        RejectInvalidSongSingMaterialSource(ref songSing);
        bool songSingRerouted = TryRerouteSongSingToPractice(ref songSing, ref humBack);
        if (songSingRerouted)
        {
            NoteSingingRoutingFact(
                "song_sing",
                "routed_for_validation",
                "current_material_source_corrected",
                $"角色决定演唱的意图已保留；实际素材不在长期曲库，已改走 " +
                $"hum_back mode={humBack.Mode}, order={humBack.Order ?? ""}。" +
                "程序没有替角色决定是否演唱，只纠正了客观素材来源；" +
                "这个改路请求仍需通过 order 等参数校验才会执行。");
            if (m_LogHumBack)
                Debug.LogWarning($"[SongSing→HumBack] 曲库标签指向当前歌声素材，已按来源改走 {humBack.Mode}");
        }
        if (ShouldDiscardSongSingToolForCurrentTurn(songSing))
        {
            ReportToolFailureForLlm(
                "song_sing",
                "wrong_material_source",
                "当前用户原话指向跟唱约定、正在发生的演唱、失败反馈或最近录音，" +
                "而 song_sing 只读取长期曲库；本次曲库演唱没有执行。",
                "先根据真实可用素材判断来源：即时录音用 hum_back echo，" +
                "练唱清单用 hum_back practice；只有用户确实指向长期曲库时才用 song_sing。" +
                "是否仍要演唱、解释或询问用户由你决定。");
            if (m_LogHumBack)
                Debug.LogWarning("[SongSing] 模型把跟唱约定、当前演唱或失败陈述误写成曲库演唱；已丢弃该标签并重新分流");
            songSing = null;
        }
        if (songMemory != null &&
            string.Equals(songMemory.Action, "remember", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(songMemory.Title))
        {
            songMemory.Title = ExtractExplicitSongTitle(m_LastUserMsg);
        }

        bool heldForSongMemory = m_HoldSpeechForSongMemoryResult;
        bool heldForHumBack = m_HoldSpeechForHumBackResult;
        m_HoldSpeechForSongMemoryResult = false;
        m_HoldSpeechForHumBackResult = false;
        if (heldForSongMemory || heldForHumBack)
        {
            //歌曲保存与真实演唱都采用工具结果作为事实来源。整段普通正文不播、不进入
            //角色已说出口历史，避免“已经记住”或用朗读歌词/舞台说明冒充唱歌。
            m_SentenceBuffer.Length = 0;
            m_PendingChunks.Clear();
            cleanFull = "";
            if (heldForSongMemory && m_LogAgentLoop)
                Debug.Log("[SongMemory] 已扣留落盘前回复，等待本机曲库结果后再确认");
            if (heldForHumBack && m_LogHumBack)
                Debug.Log("[HumBack] 已扣留普通TTS歌词/舞台说明，只允许真实回哼音频发声");
        }
        else if (!m_AgentRunning)
        {
            //非 agent 模式没有调度语义，但所有控制标签仍必须从字幕/TTS剥除。
            cleanFull = StripAgentTagsForTTS(cleanFull);
        }

        if (songMemory != null) BeginSongMemory(songMemory, heldForSongMemory);
        else if (heldForSongMemory)
        {
            m_SongMemoryAcknowledgementRequired = true;
            CompleteSongMemoryImmediately("没有找到可用于保存的最近歌声音频，本次未写入本机曲库。");
        }
        if (songSearch != null) BeginSongSearch(songSearch);
        if (songCatalog != null) BeginSongCatalogInspection(songCatalog);
        if (speakerManage != null) BeginSpeakerManage(speakerManage);
        //曾经在这里做「模型漏调 <song_sing/> 就按正则兜底」。已删除：8/9 实测触发 5 次、
        //成功 0 次，而正则从普通说话里编出来的"歌名"是「了呀」(出自"我刚才已经唱了呀")、
        //「点歌」、「完再唱」、以及整句歌词。判据 IsPlausibleUnquotedSongTitle 是一份
        //黑名单，不在名单里的一律放行，注定漏。
        //更要命的是它会连带扣住她那一轮的正常回复(已扣留 7 次)，查不到歌之后整轮无声。
        //而那几轮模型自己**没有**调用 <song_sing/>——它判断"这不是点歌请求"，判断是对的，
        //是正则在第二次猜并且猜错。要不要从曲库唱，交给她自己决定。
        if (m_PracticeEditFailedThisResponse && humBack != null)
        {
            humBack = null;
            ReportToolFailureForLlm(
                "hum_back",
                "preceding_practice_edit_failed",
                "同一回复中的来源确认/素材修订没有成功，因此没有播放旧版本。",
                "请根据真实工具结果重新决定边界、素材身份，或自然询问用户；" +
                "不要声称已经修复或播放。");
        }
        if (songSing != null)
        {
            humBack = null;
            BeginSongSing(songSing);
        }
        m_HumBackTransactionRetainedThisRound = false;
        if (humBack != null) QueueHumBack(humBack);

        //解析 agent 标签 — 从全文里抽出来 next/continue/silent/look，存到 m_Round*
        if (m_AgentRunning)
        {
            float? nextInSec;
            string focus;
            bool wantsContinue;
            bool wantsSilent;
            bool? wantsLook;
            ParseAgentTags(cleanFull, out cleanFull, out nextInSec, out focus, out wantsContinue, out wantsSilent, out wantsLook);
            if (autonomousSkillApproved)
            {
                //申请轮只负责“想用什么”；详细规则在紧接的下一轮才加载。
                wantsContinue = true;
                nextInSec = null;
            }
            else if (skillRequest != null && !skillRequestAlreadyLoaded && wantsContinue &&
                !m_ToolCorrectionContinuationPending)
            {
                //拒绝的申请不能靠它自带的 <continue/> 绕过权限闸反复重试。
                wantsContinue = false;
                m_PendingSkillStatusFrame =
                    $"\n[Skill 申请未执行；{skillRequest.Name}={skillRequestRejection}]";
            }
            if (selfRepeat || m_RoundSilencedForRepeat)
            {
                //复读轮不仅要安静，还必须切断 continue 和定时续轮。
                //否则本轮虽然没出声，后台仍会继续生成同一段话。
                wantsContinue = false;
                nextInSec = null;
            }
            if (m_ToolCorrectionContinuationPending &&
                !selfRepeat && !m_RoundSilencedForRepeat)
            {
                //程序只提供事实；紧接的一轮仍由同一个角色自然组织语言和正确工具调用。
                wantsContinue = true;
                nextInSec = null;
            }
            if (m_ToolCorrectionExhaustedThisRound)
            {
                //同一真实用户轮只自动纠正一次。第二次不再让错误标签自带的 continue
                //形成循环；最终提示显示为系统状态，不伪装成角色台词。
                wantsContinue = false;
                nextInSec = null;
                m_RoundWaitForUser = true;
            }
            if (m_HumBackTransactionRetainedThisRound)
            {
                //A real singing action is now the continuation of this assistant
                //turn.  Do not start another LLM decision before its factual
                //playback result exists; the result tick remains an LLM choice.
                bool requestedContinue = wantsContinue;
                wantsContinue = ShouldContinueAgentChainAfterHumBack(
                    wantsContinue, m_HumBackTransactionRetainedThisRound);
                if (requestedContinue && !wantsContinue && m_LogAgentLoop)
                    Debug.Log("[HumBack/Transaction] 已受理或幂等保留歌唱动作；" +
                              "<continue/> 延后到真实播放结果之后");
                nextInSec = null;
            }
            //兜底:引号异形/格式畸变导致 ParseAgentTags 没认出的标签,别让它进历史和感知帧
            cleanFull = StripAgentTagsForTTS(cleanFull);
            if (CurrentMotionResponseRejected || !string.IsNullOrEmpty(m_PendingSelfInspection) || HasPendingSingingGoalReview())
            {
                wantsContinue = false;
                nextInSec = null;
            }
            m_RoundNextInSec = nextInSec;
            m_RoundFocus = focus;
            m_RoundContinue = wantsContinue;
            m_RoundSilent = wantsSilent;
            m_RoundLookRequest = wantsLook;
            if (wantsContinue && m_ActiveSkillsThisRound.Contains("singing"))
                AcquireSkillExecutionLease(
                    "singing", "continue-chain 尚未完成歌唱相关决定");
            else if (!wantsContinue && !IsSingingRuntimeActivityActive() && !IsSingingGoalAwaitingExecution())
                ReleaseSkillExecutionLease("singing", "continue-chain 已自然收口");

            //★ 立刻应用视觉状态变化——下一帧(chain or scheduled)就能反映新眼睛状态
            if (wantsLook.HasValue)
            {
                bool newState = wantsLook.Value && m_EnableScreenVision;  //视觉总开关关时 <look/> 不起效
                if (m_AgentEyesOpen != newState)
                {
                    m_AgentEyesOpen = newState;
                    if (!newState) ArchiveClosedEyeImages();
                    if (m_LogAgentLoop)
                        Debug.Log($"[Agent] {(newState ? "<look/> 睁眼" : "<unlook/> 闭眼")}");
                }
            }

            ResolveSingingSpeech(ref cleanFull);
            //从 m_SentenceBuffer 尾巴里把标签剥掉(标签按 prompt 规则在末尾，所以这就是它们的位置)。
            //剥完再 flush，保证不会把标签字符送进 TTS。
            string tail = m_SentenceBuffer.ToString();

            //★ 正文中间的 <silent/>：它的语义是"从这里开始不发声"，而不是只在句首才算。
            //  实测她会把它当分隔符用——前半段说给用户听，后半段是心里话：
            //    「…これ以上、気まずくさせちゃダメね。<silent/>小优という名前は…」
            //  而内心独白的检测是锚定开头的(OnStreamDelta 里 @"^\s*<silent\s*/>")，中间的
            //  匹配不上；流式阶段 FindPotentialAgentTagStart 会在 <silent 处停住不念，
            //  所以后半段一路留在 buffer 里，最后在这里被 StripAgentTagsForTTS 剥掉标签、
            //  两侧文本无缝拼接，整段心里话被当正文念了出来(实测原样念出三句)。
            //  这里把标签之后的部分切出去：不进 TTS，改走内心独白(入历史、用户听不到)。
            string tailInner = null;
            if (!string.IsNullOrEmpty(tail))
            {
                var midSilent = System.Text.RegularExpressions.Regex.Match(
                    tail, @"<silent\s*/>",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (midSilent.Success)
                {
                    tailInner = tail.Substring(midSilent.Index + midSilent.Length);
                    tail = tail.Substring(0, midSilent.Index);
                }
            }

            if (!string.IsNullOrEmpty(tail))
            {
                string cleanTail;
                float? _ni; string _f; bool _c; bool _s; bool? _l;
                ParseAgentTags(tail, out cleanTail, out _ni, out _f, out _c, out _s, out _l);
                cleanTail = MemoryTagParser.Strip(cleanTail);   //只剥不应用,操作已在全文提取过
                AgentSongMemoryRequest ignoredMemory = ExtractSongMemoryTag(ref cleanTail);
                AgentSongSearchRequest ignoredSearch = ExtractSongSearchTag(ref cleanTail);
                AgentSongCatalogRequest ignoredCatalog = ExtractSongCatalogTag(ref cleanTail);
                AgentSongSingRequest ignoredSongSing = ExtractSongSingTag(ref cleanTail);
                AgentHumBackRequest ignoredHumBack = ExtractHumBackTag(ref cleanTail);
                cleanTail = StripAgentTagsForTTS(cleanTail);
                // Typed speech is already clean. Rebuilding it would discard language spans.
                if (!string.Equals(cleanTail, m_SentenceBuffer.ToString().Trim(), StringComparison.Ordinal))
                {
                    string tailLanguage = m_SentenceBuffer.LanguageCode;
                    m_SentenceBuffer.Length = 0;
                    if (!string.IsNullOrEmpty(cleanTail)) m_SentenceBuffer.Append(new SpeechText(cleanTail, tailLanguage));
                }
            }
            else
            {
                //整段尾巴都在 <silent/> 之后(她把标签放在了正文最前面之外的位置)。
                //必须显式清空——否则原始 tail 会留在 buffer 里被后续 flush 念出来。
                m_SentenceBuffer.Length = 0;
            }

            //<silent/> 之后的部分：剥净控制标签后作为内心独白记录，不进 TTS。
            if (!string.IsNullOrEmpty(tailInner))
            {
                string innerTail;
                float? _ni2; string _f2; bool _c2; bool _s2; bool? _l2;
                ParseAgentTags(tailInner, out innerTail, out _ni2, out _f2, out _c2, out _s2, out _l2);
                innerTail = MemoryTagParser.Strip(innerTail);
                innerTail = StripAgentTagsForTTS(innerTail);
                if (!string.IsNullOrEmpty(innerTail))
                {
                    m_PendingMidRoundInner = innerTail;
                    if (m_LogAgentLoop)
                        Debug.Log($"[Agent] 正文中间的 <silent/> → 其后转为内心独白(不发声): \"{innerTail}\"");
                }
            }

            //还扣着就说明整轮都没超过 k_RepeatConfirmChars。此刻 buffer 里是全部正文，
            //一个字都还没进 TTS，所以仍然拦得住。这一档要求**整条完全相等**而不是前缀，
            //所以门槛可以低到 6 字——「うん。」那种长度依旧放行。
            if (m_RoundRepeatHold && !m_RoundIsInner)
            {
                m_RoundRepeatHold = false;
                m_RoundRepeatCheckDone = true;
                string whole = NormalizeUtteranceForRepeat(m_SentenceBuffer.ToString());
                if (whole.Length >= k_RepeatExactChars &&
                    MatchesRecentUtteranceExactly(whole, out string exactAgo))
                    MarkRoundAsSilencedRepeat(m_SentenceBuffer.ToString().Trim(), exactAgo);
            }

            //★ per-round 状态更新——把"本轮我说了什么"立刻入 ring buffer / 计数器 +1，
            //   这样 chain 的下一帧 LLM 能马上看到"上一帧我说了'A。'"。
            //   不等到 FinishSpeakingNaturally(那里要等所有音频播完，会让 chain 看不到刚说的内容)。
            //   ★ 内心独白用 m_RoundIsInner(OnStreamDelta 检测到的前缀信号) 作为权威判定，
            //     不依赖尾缀 <silent/>——尾缀模式不可靠且会撕裂前轮 spoken 音频。
            if (m_AgentRoundInFlight && !CurrentMotionResponseRejected)
            {
                float nowT = Time.realtimeSinceStartup;
                m_LastAITurnTime = nowT;
                string trimmed = (cleanFull ?? "").Trim();
                bool isInnerThis = m_RoundIsInner && !string.IsNullOrEmpty(trimmed);
                if (!string.IsNullOrEmpty(trimmed))
                {
                    //内心独白时给 ring buffer 条目加 [内心] 前缀——下一帧 LLM 能区分
                    //"我刚才在心里想"vs"我刚才说出口的话"，避免内心思考被当成已说出的句子
                    string display = isInnerThis ? ("[内心] " + trimmed) : trimmed;
                    //正文中间的 <silent/>：cleanFull 里两半是连在一起的，若整段都记成"说过的"，
                    //她下一帧会以为心里话也说出口了——而 你最近发言 正是防重复用的信号。
                    if (!isInnerThis && !string.IsNullOrEmpty(m_PendingMidRoundInner) &&
                        trimmed.EndsWith(m_PendingMidRoundInner, StringComparison.Ordinal))
                    {
                        string spokenPart = trimmed
                            .Substring(0, trimmed.Length - m_PendingMidRoundInner.Length).Trim();
                        display = string.IsNullOrEmpty(spokenPart)
                            ? ("[内心] " + m_PendingMidRoundInner)
                            : (spokenPart + "  [内心] " + m_PendingMidRoundInner);
                    }
                    m_LastAIMsgPlain = display;
                    //比对用的是本轮正文原文，不是加了 [内心] 前缀的显示串——
                    //同一句话说出口一次、又在心里想一次，仍然是复读。
                    NoteUtteranceForRepeatWatch(trimmed);
                    m_RecentAIUtterances.Enqueue(new KeyValuePair<float, string>(nowT, display));
                    int cap = Mathf.Max(1, m_RecentAIUtterancesShown);
                    while (m_RecentAIUtterances.Count > cap) m_RecentAIUtterances.Dequeue();
                    //她自己说出的(或心里想到的)记忆名字也算"想起"——触发提及激活
                    if (m_MemoryHub != null && m_EnableMemoryRecall)
                        m_MemoryHub.NotifyAIUtterance(trimmed);
                }
                else if (m_RoundSilent)
                {
                    m_LastAIMsgPlain = "<silent/>";
                }
                if (!string.IsNullOrEmpty(m_RoundFocus)) m_LastFocus = m_RoundFocus;
                m_ConsecutiveAITurns++;
            }
        }

        //—— 内心独白判定 ——
        //权威信号是 OnStreamDelta 检测到的 <silent/> 前缀(m_RoundIsInner)。
        //那时 inner 文本被拦在 m_SentenceBuffer、根本没进 TTS 队列；这里只需要：
        //  (a) 清 m_SentenceBuffer 防止 FlushCompleteSentences(true) 把残文 flush
        //  (b) 历史写 [内心] 文本
        //★ 关键：**不**清 m_PendingChunks / m_PendingClips、**不**停 m_AudioSource——
        //  chain 上下文里那些是上一轮 spoken 内容，正在播；硬切会把上一轮的话拦腰斩断。
        //
        //尾缀位置的 <silent/> (LLM 写在 round 末尾)不被识别为内心独白——那时文本已经流到
        //pipeline 了，强行回收会触发上述 chain 误杀。LLM 在尾部写 <silent/> 视为"写错位置"，
        //文本照常发声(behavior.txt 已规定 <silent/> 必须写在最前面)。
        bool isInnerThought = !CurrentMotionResponseRejected && m_AgentRunning && m_AgentRoundInFlight && m_RoundIsInner
                              && !string.IsNullOrEmpty((cleanFull ?? "").Trim());

        if (isInnerThought)
        {
            string innerText = (cleanFull ?? "").Trim();
            //清 m_SentenceBuffer——OnStreamDelta inner 模式下文本累积在这里没 flush，
            //不清会被下面 FlushCompleteSentences(true) 兜底推进 TTS 队列
            m_SentenceBuffer.Length = 0;

            //历史 assistant 位写"[内心] 文本"——LLM 下次看历史时知道这段是内心活动，不是说出口的
            m_ChatHistory.Add("[内心] " + innerText);

            if (m_LogAgentLoop)
                Debug.Log($"[Agent] 内心独白(不发声、入历史): \"{innerText}\"");
        }
        else
        {
            //普通发声路径——把 buffer 残留 flush 给 TTS
            FlushCompleteSentences(true);
        }

        Debug.Log("流式完整回复：" + full);
        UpdateContinuationExecutionFacts();

        //—— 续派 chain：LLM 给了 <continue/> 就**立即**派下一帧 ——
        //不置 m_StreamComplete=true、不收 round。
        //新一轮的 OnStreamDelta/OnStreamComplete 用同一对回调，文本进同一个 m_PendingChunks，
        //AudioPlayer 不会因为"队列空 + 流结束"退出——它会等到新文本来。
        //(内心独白 + continue 同样支持——pipeline 没被关，下一轮的 spoken/inner 都能接上)
        bool willChain = !CurrentMotionResponseRejected && m_AgentRunning && m_AgentRoundInFlight && m_RoundContinue
                         && m_ConsecutiveAITurns < m_MaxConsecutiveAITurns;
        if (willChain && !ConsumeWorkChainBudget()) { willChain = false; m_RoundContinue = false; }
        if (willChain)
        {
            m_RoundIsInner = false;          //新一轮 chain round 重新从普通模式开始
            m_RoundRepeatCheckDone = false;
            m_RoundRepeatHold = false;
            m_RoundInnerCheckDone = false;   //★ inner 检测重置——否则 chain round 永远进不了 inner 模式，
                                             //   LLM 想用 <silent/> 自救破局会失败、内心独白被 TTS 念出来
            string requestedSkill = m_PendingAutonomousSkillName;
            bool isToolCorrectionChain = m_ToolCorrectionContinuationPending;
            string chainReason = isToolCorrectionChain
                ? "tool-correction"
                : string.IsNullOrEmpty(requestedSkill)
                    ? "continue-chain"
                    : "skill-request:" + requestedSkill;
            PrepareActiveSkillsForRound(null, chainReason);
            string frame = BuildPerceptionFrame(chainReason);
            m_ToolCorrectionContinuationPending = false;
            m_PendingAutonomousSkillName = "";
            m_PendingAutonomousSkillReason = "";
            HarvestSongIds(frame);
            m_ChatHistory.Add(frame);
            ClearRoundParsed();  //清掉本轮解析；下一轮 OnStreamComplete 会重新写
            m_CurrentSpeechRoundId = ++m_SpeechRoundSequence;
            if (m_LogAgentLoop)
                Debug.Log(isToolCorrectionChain
                    ? "[ToolCorrection] 真实失败事实已注入 → 链接一次角色纠错帧"
                    : string.IsNullOrEmpty(requestedSkill)
                        ? "[Agent] <continue/> → 链接下一帧(TTS pipeline 不间断)"
                        : $"[LLM技能] {requestedSkill} 详细规则已加载 → 链接自主执行帧");
            //chain 中段也按当前眼睛状态决定是否附图——LLM 上一节如果 <look/> 了，从这一节起就开始看
            string chainImageUrl = MaybeCaptureScreenForLLM();
            // A continuation is still the same real user request. Preserve its goal
            // and put current observations after the previous assistant response.
            StageFormalObservationFrame(frame, m_FormalResponseGeneration);
            DispatchWorkFeedback(BuildWorkContinuationContext(), chainImageUrl, m_FormalResponseGeneration);
            return;
        }

        //chain 想继续但触上限 — 强制结束链
        if (m_AgentRunning && m_RoundContinue && m_ConsecutiveAITurns >= m_MaxConsecutiveAITurns
            && m_LogAgentLoop)
        {
            Debug.LogWarning($"[Agent] continue-chain 触上限 ({m_ConsecutiveAITurns}/{m_MaxConsecutiveAITurns})，强制结束");
        }
        if (m_AgentRunning && m_RoundContinue &&
            m_ConsecutiveAITurns >= m_MaxConsecutiveAITurns)
        {
            if (!IsSingingRuntimeActivityActive())
                ReleaseSkillExecutionLease("singing", "continue-chain 达到轮次上限");
            if (m_ToolCorrectionContinuationPending)
            {
                m_ToolCorrectionContinuationPending = false;
                m_PendingToolCorrectionFrame = "";
                m_PendingNonCharacterToolNotice =
                    "系统：本轮工具纠错未能继续，请重新询问或稍后再试。";
            }
            if (!string.IsNullOrEmpty(m_PendingAutonomousSkillName))
            {
                m_PendingSkillStatusFrame =
                    $"\n[Skill 申请未执行；{m_PendingAutonomousSkillName}=连续轮次已达安全上限]";
                m_PendingAutonomousSkillName = "";
                m_PendingAutonomousSkillReason = "";
            }
            m_RoundContinue = false;
            m_RoundNextInSec = null;
            m_RoundWaitForUser = true;
        }

        //—— 终止：本轮(或本 chain 的最后一节)真的说完了 ——
        m_StreamComplete = true;
        if (CurrentMotionResponseRejected)
        {
            FinishRejectedMotionStreamRound();
            return;
        }

        //—— 收尾路径合并 ——
        //pipeline 也空着 = StreamAudioPlayer 不会触发 FinishSpeakingNaturally，需要这里手动收。
        //三种情形:
        //  (1) inner thought 单轮且无前轮残音 → 历史已写 [内心]，只需收 round + 通知 VAD
        //  (2) pure silent (LLM 只回 <silent/> 没文本) → 写 <silent/> 占位 + 收 round + 通知
        //  (3) 异常(cleanFull 非空但 pipeline 也空——理论不该到，防御性写文本)
        //
        //pipeline 非空(有上一轮 spoken 内容在播) → 不在这里收，等 StreamAudioPlayer 播完
        //自然走 FinishSpeakingNaturally → OnAgentRoundComplete (idempotent)。
        //
        //★ pipelineIdle 的判定中 cleanFull 检查是必要的——否则会撞 race window:
        //  "文本已 flush 给 sender 但 TTS 还没返 clip"那一帧 chunks/clips 都空、IsAISpeaking 也假,
        //  会被错判成 silent，导致正常发声轮被当沉默处理。
        bool pipelineIdle = m_PendingChunks.Count == 0 && m_PendingClips.Count == 0 && !IsAISpeaking;
        if (pipelineIdle && m_SongSingInFlight)
        {
            // <song_sing/> must first resolve managed local audio asynchronously.  Keep
            // this assistant turn open so VAD does not reclaim the microphone between
            // the held text response and the real character-voice singing action.
            m_TTSSenderDone = true;
            m_TextBack.text = "♪ …";
            SetAnimator("state", 1);
            return;
        }
        if (!m_AgentRunning && pipelineIdle && m_HumBackPending)
        {
            m_TTSSenderDone = true;
            m_TextBack.text = "";
            SetAnimator("state", 0);
            if (TryBeginPendingHumBack()) return;
        }
        // A direct-conversation memory confirmation can also return silent/empty.
        // No audio player will finish that delivery; release its in-flight state here
        // without acknowledging the pending storage observation or scheduling a retry.
        bool silentMemoryAcknowledgement = m_SongMemoryAcknowledgementInFlight &&
            string.IsNullOrWhiteSpace(cleanFull);
        if (pipelineIdle && ((m_AgentRunning && m_AgentRoundInFlight) || silentMemoryAcknowledgement))
        {
            bool isTrulySilent = string.IsNullOrEmpty((cleanFull ?? "").Trim());
            string note;
            if (isInnerThought)
            {
                //历史已经写过 [内心 ...]，这里不重复写
                note = "内心独白单轮(无前轮残音)";
            }
            else if (isTrulySilent)
            {
                //历史 assistant 位写一下"我选了沉默"
                m_ChatHistory.Add(m_RoundSilent ? "<silent/>" : "<empty/>");
                note = "silent-only(无文本无音频)";
            }
            else
            {
                //防御：cleanFull 非空但 pipeline 空——理论不该到这(文本应该已经在 chunks 里)。
                //兜底把文本当 spoken 写历史，避免历史缺一条 assistant
                m_ChatHistory.Add(cleanFull.Trim());
                note = "异常路径(文本-空管线)";
            }

            if (m_LogAgentLoop) Debug.Log($"[Agent] 收尾路径: {note}");
            //内心独白／纯沉默不经过 FinishSpeakingNaturally，等待标志得在这里清。
            //漏掉的话，她每次选择"这轮不出声"都会把自己闷到 75 秒兜底才恢复自主发言——
            //而 <silent/> 正是她常用的表达。
            ClearUserTurnAwaitingReply(note);
            //让 StreamAudioPlayer 能干净退出
            m_TTSSenderDone = true;
            //UI 复位
            PublishPendingNonCharacterToolNotice();
            m_TextBack.text = "";
            SetAnimator("state", 0);
            CompleteSongMemoryAcknowledgementIfNeeded();
            if (TryBeginPendingHumBack()) return;
            //收 round → 排下次 tick
            OnAgentRoundComplete();
            //通知 RTSpeechHandler 恢复 VAD
            if (OnAISpeakDone != null) OnAISpeakDone();
        }
    }

    /// <summary>
    /// 扫描缓冲区，把已成句的段落推入TTS队列。
    /// 首块用"首个任意标点 或 超长强切"的激进策略抢首音；
    /// 其余块用"最后一个句末标点"批量切分。
    /// flushAll=true时把残余整条推入（用于最后收尾）。
    /// </summary>
    private void FlushCompleteSentences(bool flushAll)
    {
        if (HoldSingingSpeechUntilValidation()) return;
        if (CurrentMotionResponseRejected && m_MotionRejectedSpeechRound == m_CurrentSpeechRoundId)
        {
            m_SentenceBuffer.Length = 0;
            return;
        }
        while (m_SentenceBuffer.Length > 0)
        {
            string buf = m_SentenceBuffer.ToString();
            string language = m_SentenceBuffer.LanguageCode;
            int languageBoundary = m_SentenceBuffer.FirstLanguageBoundary;
            int pendingTagStart = FindPotentialAgentTagStart(buf);
            int available = languageBoundary >= 0 ? languageBoundary : buf.Length;
            if (pendingTagStart >= 0) available = Math.Min(available, pendingTagStart);
            string speakable = buf.Substring(0, available);
            bool languageEnded = languageBoundary >= 0 && available == languageBoundary;
            int boundary = FindFlushBoundary(speakable, !m_FirstChunkFlushed);
            if (!m_FirstChunkFlushed && m_PreparedSingingBridgeClip != null &&
                language == m_PreparedSingingBridgeLanguage)
            {
                int preparedBoundary = PreparedReplyBoundary(speakable, m_PreparedSingingBridgeText,
                    flushAll || pendingTagStart >= 0 || languageEnded);
                if (preparedBoundary == -2) return;
                if (preparedBoundary >= 0) boundary = preparedBoundary;
            }
            // Never combine two declared languages in one synthesis request. A language
            // boundary can end a quoted foreign fragment without ending the sentence.
            if (boundary < 0 && (languageEnded || flushAll)) boundary = available - 1;
            if (boundary < 0) break;

            string completed = StripAgentTagsForTTS(buf.Substring(0, boundary + 1).Trim());
            m_SentenceBuffer.Remove(0, boundary + 1);
            if (string.IsNullOrEmpty(completed) || IsPurePunctuation(completed)) continue;
            m_PendingChunks.Enqueue(new PendingSpeechChunk
            {
                RoundId = m_CurrentSpeechRoundId,
                Text = completed,
                LanguageCode = language
            });
            if (m_SubtitleOverlay != null)
            {
                string probe = completed.TrimEnd();
                bool semanticBoundary = IsStrongBoundary(probe[probe.Length - 1]) ||
                    (flushAll && m_SentenceBuffer.Length == 0);
                m_SubtitleOverlay.QueueTranslationChunk(m_FormalResponseGeneration, completed, semanticBoundary);
            }
            if (!m_FirstChunkFlushed)
            {
                m_FirstChunkFlushed = true;
                if (m_LogStreamTimings) Debug.Log($"[Stream] T+{Elapsed():F2}s 首块切出: \"{completed}\" lang={language ?? "auto"} characters={completed.Length}");
            }
        }
        // Any remaining incomplete control tag is deliberately discarded at EOF.
        if (flushAll) m_SentenceBuffer.Length = 0;
    }

    /// <summary>
    /// 寻找切分位置。
    /// aggressive=true（首块）：优先找首个强标点；若无且超阈值，用首个弱标点强切；
    /// aggressive=false（后续）：找最后一个强标点以批量切。
    /// </summary>
    private int FindFlushBoundary(string buf, bool aggressive)
    {
        if (string.IsNullOrEmpty(buf)) return -1;

        if (aggressive)
        {
            //首块：见到任何强标点立即切
            for (int i = 0; i < buf.Length; i++)
            {
                if (IsStrongBoundary(buf[i])) return i;
            }
            //超阈值：找最靠前的弱标点强切
            if (buf.Length >= m_FirstChunkMaxChars)
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    if (IsWeakBoundary(buf[i])) return i;
                }
                //实在没有弱标点就在阈值处硬切
                return Mathf.Min(m_FirstChunkMaxChars, buf.Length) - 1;
            }
            return -1;
        }
        else
        {
            //后续块：找最后一个强标点
            int last = -1;
            for (int i = 0; i < buf.Length; i++)
            {
                if (IsStrongBoundary(buf[i])) last = i;
            }
            return last;
        }
    }

    private bool IsStrongBoundary(char c)
    {
        return c == '。' || c == '！' || c == '？' || c == '.' || c == '!' || c == '?' || c == '\n' || c == '…';
    }

    private bool IsWeakBoundary(char c)
    {
        return c == '、' || c == '，' || c == ',' || c == '；' || c == ';' || c == '：' || c == ':';
    }

    /// <summary>
    /// 没有任何"实义字符"的段(纯标点/纯空白/纯emoji)送TTS会直接400。
    /// 例: "…"、"。。。"、"——"、"   "。
    /// char.IsLetterOrDigit对CJK表意字符也返回true(分类Lo)，所以一行就够。
    /// </summary>
    private bool IsPurePunctuation(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// 消费m_PendingChunks，串行调用TTS，产出放入m_PendingClips。
    /// 串行是因为GPT-SoVITS服务端单卡，同时多请求会排队反而更慢。
    /// </summary>
    private IEnumerator StreamTTSSender(int responseGeneration)
    {
        AudioClip pending = null;
        bool pendingDone = false;
        System.Action<AudioClip, string> onReceive = (clip, msg) =>
        {
            pending = clip;
            pendingDone = true;
        };

        while (true)
        {
            if (responseGeneration != m_FormalResponseGeneration) yield break;
            //没活干就等
            while (m_PendingChunks.Count == 0)
            {
                if (responseGeneration != m_FormalResponseGeneration) yield break;
                if (m_StreamComplete)
                {
                    m_TTSSenderDone = true;
                    yield break;
                }
                yield return null;
            }

            PendingSpeechChunk speechChunk = m_PendingChunks.Dequeue();
            if (m_SilencedSpeechRoundIds.Contains(speechChunk.RoundId)) continue;
            string chunk = speechChunk.Text;
            //双重过滤：FlushCompleteSentences已经挡过一次，这里防止旧路径或者其他来源。
            //StripAgentTagsForTTS 也会截掉未闭合的已知标签后缀。
            chunk = StripAgentTagsForTTS(chunk);
            if (string.IsNullOrEmpty(chunk) || IsPurePunctuation(chunk)) continue;

            pending = null;
            pendingDone = false;
            if (m_LogStreamTimings) Debug.Log($"[Stream] T+{Elapsed():F2}s TTS请求发出: \"{chunk}\"");
            m_ChatSettings.m_TextToSpeech.Speak(new SpeechText(chunk, speechChunk.LanguageCode), onReceive);

            //TTS客户端正常会在20s内回调(成功或失败都会调)。这里的25s只是兜底，
            //防止TTS客户端自己挂掉永远不回调。GPT-SoVITS内部失败也会调callback(null,..)
            float waitStart = Time.realtimeSinceStartup;
            while (!pendingDone)
            {
                if (responseGeneration != m_FormalResponseGeneration) yield break;
                if (Time.realtimeSinceStartup - waitStart > 25f)
                {
                    Debug.LogError("TTS客户端无响应(>25s)，跳过此段: " + chunk);
                    ReportTtsFailureOnce(
                        "tts_timeout",
                        "语音合成超时。",
                        "streaming TTS callback exceeded 25 seconds");
                    break;
                }
                yield return null;
            }

            if (responseGeneration != m_FormalResponseGeneration) yield break;
            if (pending != null)
            {
                if (m_LogStreamTimings) Debug.Log($"[Stream] T+{Elapsed():F2}s TTS返回(音频{pending.length:F2}s): \"{chunk}\"");
                if (!m_SilencedSpeechRoundIds.Contains(speechChunk.RoundId))
                {
                    m_PendingClips.Enqueue(new PendingSpeechClip
                    {
                        RoundId = speechChunk.RoundId,
                        Text = chunk,
                        LanguageCode = speechChunk.LanguageCode,
                        Clip = pending
                    });
                }
            }
            else
            {
                if (m_LogStreamTimings) Debug.LogWarning($"[Stream] T+{Elapsed():F2}s TTS失败/跳过: \"{chunk}\"");
                ReportTtsFailureOnce(
                    "tts_failed",
                    "部分语音未能播放。",
                    "streaming TTS returned no audio clip");
            }
        }
    }

    /// <summary>
    /// 真正的流式TTS路径：句子进入队列后立即发给GPT-SoVITS，收到首批PCM就播放，
    /// 不再等待完整WAV。每段仍严格串行，避免同一个推理服务被并发请求挤爆。
    /// </summary>
    private IEnumerator StreamDirectTTSPlayer(int responseGeneration)
    {
        bool firstChunk = true;
        TTS tts = m_ChatSettings.m_TextToSpeech;
        PendingSpeechClip next = null;
        bool nextDone = false, alive = true;
        float nextRequestedAt = 0f;
        AudioClip ownedClip = null;

        void ClearNext()
        {
            PendingSpeechClip old = next;
            next = null;
            if (old == null) return;
            if (!nextDone && responseGeneration == m_FormalResponseGeneration) tts.CancelPreparedSpeech();
            if (old.Clip != null) Destroy(old.Clip);
        }
        void PrefetchNext()
        {
            if (next != null || !tts.CanPrefetchNextSpeech || m_PendingChunks.Count == 0) return;
            PendingSpeechChunk queued = m_PendingChunks.Peek();
            if (m_SilencedSpeechRoundIds.Contains(queued.RoundId)) return;
            string future = StripAgentTagsForTTS(queued.Text);
            // Long paragraphs keep the PCM-streaming path; waiting for an entire
            // long WAV could cost more than the lookahead saves. No text is truncated.
            if (string.IsNullOrWhiteSpace(future) || future.Length > 64 || IsPurePunctuation(future)) return;
            SpeechText preparedSpeech = tts.ResolveSpeech(new SpeechText(future, queued.LanguageCode));
            var slot = new PendingSpeechClip { RoundId = queued.RoundId, Text = future, LanguageCode = preparedSpeech.LanguageCode };
            next = slot; nextDone = false; nextRequestedAt = Time.realtimeSinceStartup;
            Debug.Log($"[TTS/Lookahead] 当前句合成已完成，播放期间预取下一句: \"{future}\"");
            tts.PrepareSpeech(preparedSpeech, (clip, ignored) =>
            {
                if (!alive || responseGeneration != m_FormalResponseGeneration ||
                    !ReferenceEquals(next, slot) || m_SilencedSpeechRoundIds.Contains(slot.RoundId))
                {
                    if (clip != null) Destroy(clip);
                    if (ReferenceEquals(next, slot)) nextDone = true;
                    return;
                }
                slot.Clip = clip; nextDone = true;
            });
        }

        try
        {
            while (true)
            {
                if (responseGeneration != m_FormalResponseGeneration) yield break;
                while (m_PendingChunks.Count == 0)
                {
                    if (responseGeneration != m_FormalResponseGeneration) yield break;
                    if (m_StreamComplete)
                    {
                        ClearNext();
                        FinishSpeakingNaturally();
                        yield break;
                    }
                    yield return null;
                }

                PendingSpeechChunk speechChunk = m_PendingChunks.Dequeue();
                if (m_SilencedSpeechRoundIds.Contains(speechChunk.RoundId)) continue;
                string text = speechChunk.Text;
                //真正发声前的最后一道门，确保工具标签不会进入 TTS 或跟随 TTS 出现在字幕。
                text = StripAgentTagsForTTS(text);
                if (string.IsNullOrEmpty(text) || IsPurePunctuation(text)) continue;

                SpeechText speechRequest = tts.ResolveSpeech(new SpeechText(text, speechChunk.LanguageCode));
                speechChunk.LanguageCode = speechRequest.LanguageCode;

                bool started = false;
                bool completed = false;
                bool succeeded = false;
                float audioDuration = 0f;

                if (next != null && !MatchesPreparedSpeech(next, speechChunk, text)) ClearNext();
                if (next != null)
                {
                    while (!nextDone && Time.realtimeSinceStartup - nextRequestedAt < 25f)
                    {
                        if (responseGeneration != m_FormalResponseGeneration) yield break;
                        if (m_SilencedSpeechRoundIds.Contains(speechChunk.RoundId)) break;
                        yield return null;
                    }
                    if (nextDone)
                    {
                        ownedClip = next.Clip; next.Clip = null; next = null;
                        if (ownedClip != null) Debug.Log("[TTS/Lookahead] 命中已预取的下一句，无需重新合成");
                    }
                    else ClearNext();
                }
                if (m_SilencedSpeechRoundIds.Contains(speechChunk.RoundId))
                {
                    if (ownedClip != null) Destroy(ownedClip);
                    ownedClip = null;
                    continue;
                }
                if (firstChunk && ownedClip == null) ownedClip = TakePreparedFormalReply(text, speechChunk.LanguageCode);

                // Send synthesis immediately. PCM waits behind a playback gate while the
                // opener speaks, instead of delaying the expensive network/model request.
                if (responseGeneration != m_FormalResponseGeneration) yield break;

                if (ownedClip != null)
                {
                    while (GetFormalPlaybackPermission(responseGeneration, speechChunk.RoundId) == TTS.StreamingPlaybackPermission.Wait)
                        yield return null;
                    if (GetFormalPlaybackPermission(responseGeneration, speechChunk.RoundId) == TTS.StreamingPlaybackPermission.Cancel)
                        yield break;
                    audioDuration = ownedClip.length;
                    m_FormalBufferedClip = ownedClip;
                    m_AudioSource.clip = ownedClip;
                    m_AudioSource.loop = false;
                    m_AudioSource.Play();
                    started = true;
                }
                else
                {
                    m_FormalBufferedClip = null;
                    if (m_LogStreamTimings) Debug.Log($"[Stream] T+{Elapsed():F2}s TTS流请求发出: \"{text}\"");

                    m_ChatSettings.m_TextToSpeech.SpeakStreamingWithPlaybackGate(
                        speechRequest,
                        m_AudioSource,
                        _ => { started = true; },
                        (success, _, duration) =>
                        {
                            succeeded = success;
                            audioDuration = duration;
                            completed = true;
                        },
                        () => GetFormalPlaybackPermission(responseGeneration, speechChunk.RoundId));
                }

                while (!started && !completed)
                {
                    if (responseGeneration != m_FormalResponseGeneration) yield break;
                    yield return null;
                }

                if (!started)
                {
                    if (m_LogStreamTimings) Debug.LogWarning($"[Stream] T+{Elapsed():F2}s TTS流失败/跳过: \"{text}\"");
                    ReportTtsFailureOnce(
                        "tts_failed",
                        "部分语音未能播放。",
                        "direct streaming TTS did not start");
                    continue;
                }

                if (firstChunk) MarkRealFirstAudioStarted();
                m_CurrentlyPlayingText = text;
                if (firstChunk)
                {
                    m_TextBack.text = "";
                    SetAnimator("state", 2);
                    IsAISpeaking = true;
                    firstChunk = false;

                    if (m_LogStreamTimings)
                    {
                        Debug.Log($"[Stream] T+{Elapsed():F2}s 流式首音开始播放");
                        if (m_EouTime > 0f && !m_AgentCurrentRoundIsTick)
                        {
                            float total = Time.realtimeSinceStartup - m_EouTime;
                            Debug.Log($"[Timing] ★ EOU→流式首音 总延迟: {total:F2}s");
                        }
                        //锚点用过即弃：一次 EOU 只对应一次体感延迟，留着会被后续
                        //自发轮次重复计入，把统计彻底污染。
                        m_EouTime = 0f;
                    }
                }

                //流开始时还不知道最终时长，先按字符数估算字幕速度；结束时强制补齐。
                m_DirectStreamingSubtitlePrefix = m_TextBack.text;
                m_DirectStreamingSubtitleRevealedChars = 0;
                float estimatedDuration = ownedClip != null ? ownedClip.length :
                    Mathf.Max(0.5f, text.Length * Mathf.Max(0.08f, m_WordWaitTime));
                m_DirectStreamingSubtitleCoroutine = StartCoroutine(TypeSentence(
                    text,
                    estimatedDuration,
                    responseGeneration,
                    revealed => m_DirectStreamingSubtitleRevealedChars = revealed));

                while (!completed)
                {
                    if (responseGeneration != m_FormalResponseGeneration || !IsAISpeaking ||
                        m_SilencedSpeechRoundIds.Contains(speechChunk.RoundId))
                    {
                        if (responseGeneration == m_FormalResponseGeneration)
                        {
                            m_ChatSettings.m_TextToSpeech.CancelStreaming();
                            StopDirectStreamingSubtitle();
                        }
                        yield break;
                    }
                    PrefetchNext();
                    if (ownedClip != null && !m_AudioSource.isPlaying)
                    {
                        succeeded = true;
                        completed = true;
                    }
                    yield return null;
                }

                StopDirectStreamingSubtitle();
                if (IsAISpeaking)
                {
                    if (m_SubtitleOverlay != null &&
                        m_DirectStreamingSubtitleRevealedChars < text.Length)
                        m_SubtitleOverlay.ReportSourceTextRevealed(
                            responseGeneration,
                            text,
                            m_DirectStreamingSubtitleRevealedChars);
                    m_DirectStreamingSubtitleRevealedChars = text.Length;
                    m_TextBack.text = m_DirectStreamingSubtitlePrefix + text;
                }

                if (!IsAISpeaking) yield break;

                if (succeeded)
                {
                    m_AssistantHeardText.Append(text);
                    if (m_LogStreamTimings)
                    {
                        Debug.Log($"[Stream] T+{Elapsed():F2}s TTS流播放完成(音频{audioDuration:F2}s): \"{text}\"");
                    }
                }
                else if (m_LogStreamTimings)
                {
                    Debug.LogWarning($"[Stream] T+{Elapsed():F2}s TTS流中断/失败: \"{text}\"");
                }
                if (!succeeded)
                {
                    ReportTtsFailureOnce(
                        "tts_failed",
                        "部分语音未能播放。",
                        "direct streaming TTS ended unsuccessfully");
                }
                m_CurrentlyPlayingText = "";
                if (ownedClip != null)
                {
                    if (m_AudioSource.clip == ownedClip) m_AudioSource.clip = null;
                    if (m_FormalBufferedClip == ownedClip) m_FormalBufferedClip = null;
                    Destroy(ownedClip); ownedClip = null;
                }

                //The next sentence may already be ready. Do not add another fixed wait.
            }
        }
        finally
        {
            alive = false;
            ClearNext();
            if (ownedClip != null)
            {
                if (m_AudioSource != null && m_AudioSource.clip == ownedClip)
                {
                    m_AudioSource.Stop(); m_AudioSource.clip = null;
                }
                if (m_FormalBufferedClip == ownedClip) m_FormalBufferedClip = null;
                Destroy(ownedClip);
            }
        }
    }

    private void StopDirectStreamingSubtitle()
    {
        if (m_DirectStreamingSubtitleCoroutine != null)
        {
            StopCoroutine(m_DirectStreamingSubtitleCoroutine);
            m_DirectStreamingSubtitleCoroutine = null;
        }
    }

    /// <summary>
    /// 消费m_PendingClips，顺序播放+逐字显示。
    /// 每个chunk播完才追加到m_AssistantHeardText——这样Interrupt时的"已听到部分"是真实的。
    /// 自然退出时把累计的heard text写入聊天历史并触发OnAISpeakDone给RTSpeechHandler。
    /// </summary>
    private IEnumerator StreamAudioPlayer(int responseGeneration)
    {
        bool firstChunk = true;
        while (true)
        {
            if (responseGeneration != m_FormalResponseGeneration) yield break;
            while (m_PendingClips.Count == 0)
            {
                if (responseGeneration != m_FormalResponseGeneration) yield break;
                if (m_TTSSenderDone)
                {
                    //自然退出：被Interrupt时IsAISpeaking会先被置false，这里用它判断是否还需要commit
                    if (IsAISpeaking)
                    {
                        FinishSpeakingNaturally();
                    }
                    yield break;
                }
                yield return null;
            }

            PendingSpeechClip pendingClip = m_PendingClips.Dequeue();
            if (m_SilencedSpeechRoundIds.Contains(pendingClip.RoundId)) continue;
            AudioClip clip = pendingClip.Clip;
            string text = pendingClip.Text;
            if (clip == null) continue;

            //这条路 clip 已经在手，不需要提前量：等满即播。
            if (firstChunk)
            {
                yield return StartCoroutine(
                    WaitForSpokenFillerToFinish(responseGeneration, 0f));
                if (responseGeneration != m_FormalResponseGeneration) yield break;
            }
            if (firstChunk) MarkRealFirstAudioStarted();
            m_AudioSource.clip = clip;
            m_AudioSource.Play();
            m_CurrentlyPlayingText = text;  //Interrupt时按播放比例切这一段
            if (firstChunk)
            {
                //首句出声时才清空"正在思考中..."
                m_TextBack.text = "";
                SetAnimator("state", 2);
                IsAISpeaking = true;  //barge-in检测的开关从这一刻开始生效
                firstChunk = false;
                if (m_LogStreamTimings)
                {
                    Debug.Log($"[Stream] T+{Elapsed():F2}s 首音开始播放");
                    //EOU→首音 = 用户主观感受到的"响应延迟"——评测的核心指标
                    if (m_EouTime > 0f && !m_AgentCurrentRoundIsTick)
                    {
                        float total = Time.realtimeSinceStartup - m_EouTime;
                        Debug.Log($"[Timing] ★ EOU→首音 总延迟: {total:F2}s (核心体感指标，<1.5s 像人)");
                    }
                    m_EouTime = 0f;   //同上：锚点用过即弃
                }
            }
            yield return StartCoroutine(TypeSentence(text, clip.length, responseGeneration));
            while (m_AudioSource.isPlaying) yield return null;

            //本chunk播完(且未被Interrupt)，进帐到"听到的文本"
            //Interrupt会先StopAudioSource让isPlaying=false退出上面的循环，
            //然后通过IsAISpeaking=false让我们识别到自己已经被打断，不再追加
            if (!IsAISpeaking) yield break;
            m_AssistantHeardText.Append(text);
            m_CurrentlyPlayingText = "";

            //—— 句间呼吸 ——
            //chained <continue/> 把 TTS 灌满 m_PendingClips 后会让 AudioPlayer 零间隔串播,
            //听感像连珠炮。在每段 audio 播完后插一个标点感知的小停顿，恢复人类说话节奏。
            //尾句(后面已经没东西了)就不停了，避免末尾干等。
            bool moreComing = m_PendingClips.Count > 0 || !m_TTSSenderDone;
            if (moreComing && m_InterClipPauseSec > 0f)
            {
                float pause = ComputeInterClipPause(text);
                float t = 0f;
                while (t < pause)
                {
                    if (!IsAISpeaking) yield break;  //停顿期间被Interrupt也要尊重
                    t += Time.deltaTime;
                    yield return null;
                }
            }
        }
    }

    /// <summary>
    /// 决定一段 audio chunk 播完后到下一段开始前要停顿多久——给说话以呼吸感。
    /// 按本段尾字的标点动态调长短：问号 &gt; 句号 &gt; 顿号；省略号最长(余韵)。
    /// </summary>
    private float ComputeInterClipPause(string text)
    {
        float baseSec = m_InterClipPauseSec;
        if (baseSec <= 0f) return 0f;
        if (string.IsNullOrEmpty(text)) return baseSec;
        if (!m_PunctuationAwarePause) return baseSec;

        //找到最后一个非空白字符
        string trimmed = text.TrimEnd();
        if (trimmed.Length == 0) return baseSec;
        char last = trimmed[trimmed.Length - 1];

        //系数应用到 baseSec 上(用户调一个基准就能整体快/慢)
        switch (last)
        {
            case '。': case '.': return baseSec * 1.0f;       //句末完整停顿
            case '！': case '!': return baseSec * 1.0f;       //同句末
            case '？': case '?': return baseSec * 1.5f;       //问句留思考空间
            case '、': case ',': return baseSec * 0.4f;       //顿号几乎不停
            case '；': case ';': return baseSec * 0.7f;
            case '：': case ':': return baseSec * 0.5f;
            case '…':           return baseSec * 1.8f;       //省略号拖长余韵
            default:            return baseSec * 0.6f;       //没标点(裸句)给少量
        }
    }

    /// <summary>
    /// 流水线自然走到底——所有chunk都播完了。把累计文本入历史、触发外部回调、
    /// 收 agent round → 排下次 tick(若 agent loop 启用)。
    /// </summary>
    private void FinishSpeakingNaturally()
    {
        //所有正文播放路径（整段 WAV 与直流 PCM）都在这里汇合。
        //不要再让各 TTS sender 自己决定歌唱交接门是否打开。
        m_TTSSenderDone = true;
        m_TextOutputDrainedGeneration = m_FormalResponseGeneration;
        m_LatencyFillerGeneration++;
        m_EouFillerGeneration++;
        m_EouFillerScheduled = false;
        if (m_LatencyFillerPlayed)
        {
            CommitPlayedLatencyFiller();
            if (m_AudioSource != null) m_AudioSource.Stop();
        }
        m_LatencyFillerPlayed = false;
        m_LatencyFillerFromEou = false;
        m_LatencyFillerText = "";
        IsAISpeaking = false;
        m_LastVoiceOutputEndedRealtime = Time.realtimeSinceStartup;
        SetAnimator("state", 0);

        string heard = m_AssistantHeardText.ToString();
        m_AssistantHeardText.Length = 0;
        m_CurrentlyPlayingText = "";

        if (!string.IsNullOrEmpty(heard))
        {
            m_ChatHistory.Add(heard);
        }
        PublishPendingNonCharacterToolNotice();

        CompleteSongMemoryAcknowledgementIfNeeded();

        if (m_SongSingInFlight)
        {
            m_TextBack.text = "♪ …";
            SetAnimator("state", 1);
            return;
        }

        //回哼属于当前 assistant turn 的后半段。先别收 Agent round、也别把麦克风
        //交还给 VAD；回哼播放结束后由 FinishHumBack() 统一完成这些动作。
        if (TryBeginPendingHumBack()) return;
        if (HasHumBackExecutionWork())
        {
            //流式生产器可能已经消费 m_HumBackPending，并正在等待这段正文结束。
            //事务仍未完成，不能在这里提前释放麦克风或结束 Agent round。
            IsAISpeaking = true;
            m_TextBack.text = "♪ …";
            SetAnimator("state", 1);
            if (m_LogHumBack)
                Debug.Log("[HumBack/Transaction] 正文已播完；歌唱事务仍在准备/缓冲，保留当前回复所有权");
            return;
        }

        //正文后面没有真实歌唱动作才算本轮已完整回应。
        //有歌唱时由 FinishHumBack / song_sing 失败收尾路径清除。
        if (!CurrentMotionResponseRejected) ClearUserTurnAwaitingReply("回复播完");
        else if (m_PendingMotionRepair == null && m_ActiveMotionRepair == null)
            EndMotionResponseAsFailure("motion-rejected-after-speech-drained");

        //收 agent round——per-round 状态(ring buffer / consec count) OnStreamComplete 已写过；
        //这里只负责按本 chain 最后一节的 <next in/> 排下一拍。
        OnAgentRoundComplete();

        if (OnAISpeakDone != null) OnAISpeakDone();
        if (m_ChatSettings != null && m_ChatSettings.m_TextToSpeech != null)
            m_ChatSettings.m_TextToSpeech.WarmUp();
    }

    /// <summary>
    /// 用户在角色说话时插话——立即停止TTS播放、按播放进度切当前chunk的尾巴入历史、
    /// 清空所有待播队列、触发OnAISpeakDone把控制权交还给RTSpeechHandler。
    /// 没在出声时调用是no-op。
    /// </summary>
    public void Interrupt()
    {
        // A silent gesture can outlive speech; it must stop even when the speech work check is empty.
        CancelDialogueMotion("barge-in");
        //用户接管了这一轮，等待作废；他自己会带来新的一轮
        ClearUserTurnAwaitingReply("被用户打断");

        bool hasResponseWork = IsAISpeaking || IsVoiceOutputPlaying || m_FormalResponseInFlight
            || IsSingingRuntimeActivityActive()
            || m_SentenceBuffer.Length > 0 || m_PendingChunks.Count > 0 || m_PendingClips.Count > 0;
        if (!hasResponseWork) return;

        //当前正在播的那一chunk按音频时长比例算听到了多少字
        if (m_AudioSource != null && m_AudioSource.clip != null && m_AudioSource.clip.length > 0f
            && !string.IsNullOrEmpty(m_CurrentlyPlayingText))
        {
            float duration = m_LatencyFillerPlayed
                ? Mathf.Max(0.1f, m_LatencyFillerDuration)
                : m_FormalBufferedClip != null && m_AudioSource.clip == m_FormalBufferedClip
                ? m_FormalBufferedClip.length
                : m_ChatSettings.m_TextToSpeech.SupportsStreamingPlayback
                ? Mathf.Max(0.5f, m_CurrentlyPlayingText.Length * Mathf.Max(0.08f, m_WordWaitTime))
                : m_AudioSource.clip.length;
            float fraction = Mathf.Clamp01(m_AudioSource.time / duration);
            int charsHeard = Mathf.FloorToInt(m_CurrentlyPlayingText.Length * fraction);
            if (charsHeard > 0)
            {
                m_AssistantHeardText.Append(m_CurrentlyPlayingText.Substring(0, charsHeard));
            }
        }

        InvalidateFormalResponse("barge-in");

        //先把状态置成"已结束"，让StreamAudioPlayer的两个yield循环都能识别出"被打断"路径退出
        IsAISpeaking = false;
        m_LatencyFillerGeneration++;
        m_EouFillerGeneration++;
        m_EouFillerScheduled = false;
        m_LatencyFillerPlayed = false;
        m_LatencyFillerFromEou = false;
        m_LatencyFillerText = "";

        //斩断所有出声相关：停音频、清待播、让生产者协程也退出
        m_ChatSettings.m_TextToSpeech.CancelStreaming();
        StopDirectStreamingSubtitle();
        if (m_AudioSource != null) m_AudioSource.Stop();
        m_LastVoiceOutputEndedRealtime = Time.realtimeSinceStartup;
        m_PendingChunks.Clear();
        m_PendingClips.Clear();
        m_StreamComplete = true;     //StreamTTSSender见到队列空+这个flag就yield break
        m_TTSSenderDone = true;      //StreamAudioPlayer的退出条件

        //把"已听到"切片入历史，并标记被打断让下一轮prompt里LLM知道"那句话她没说完"
        string heard = m_AssistantHeardText.ToString();
        if (m_InterruptedUserQuestion != null) m_InterruptedHeardText = heard;
        m_AssistantHeardText.Length = 0;
        m_CurrentlyPlayingText = "";

        if (!string.IsNullOrEmpty(heard))
        {
            heard += "……";
            m_ChatHistory.Add(heard);
        }

        Debug.Log($"[Interrupt] 角色被打断，已说: \"{heard}\"");

        SetAnimator("state", 0);

        //agent round 被用户打断——清状态但不排下次 tick(用户会用自己的话触发新一轮)。
        //per-round 的 ring buffer / consec count 已在 OnStreamComplete 推过，这里不重复写。
        //chain 中段被打断时，已有的 LLM HTTP 可能还在飞——它的 OnStreamComplete 会因
        //m_AgentRoundInFlight=false 走到普通终止路径，不会再 chain。
        if (m_AgentRunning && m_AgentRoundInFlight)
        {
            m_AgentRoundInFlight = false;
            ClearRoundParsed();
        }

        //若被打断的是落盘后的确认句，歌曲本身已经有确定结果；结束确认状态，
        //避免优雅关闭一直等待一段用户已经不想继续听的语音。
        CompleteSongMemoryAcknowledgementIfNeeded();
        CancelPendingHumBack("barge-in", true);

        if (OnAISpeakDone != null) OnAISpeakDone();
    }

#endregion

#region Agent Loop — 让角色拥有时间感的"心跳"

    [Header("Agent Loop — 让 LLM 自主决定说话节奏 (走神/在线/连续说话)")]
    [Tooltip("总开关。关掉则角色只在用户开口时回应，永远不会主动说话")]
    [SerializeField] private bool m_EnableAgentLoop = true;
    [Tooltip("最快 tick 间隔(秒)兜底。LLM 排得更短也截到这个值——防本地推理被打爆")]
    [SerializeField] private float m_MinTickSec = 1f;
    [Tooltip("最长 tick 间隔(秒)兜底。LLM 排得更久也截到这个值——保证不会'长眠'")]
    [SerializeField] private float m_MaxTickSec = 600f;
    [Tooltip("LLM 没指定 <next in/> 时的兜底 tick 间隔(秒)")]
    [SerializeField] private float m_DefaultTickSec = 30f;
    [Tooltip("会话刚启用后多久投递第一帧(秒)。给 m_Greeting 留出播放时间，避免叠音")]
    [SerializeField] private float m_FirstTickDelaySec = 1.5f;
    [Tooltip("连续 AI 轮次上限(无用户回应)。讲故事/详述场景下 LLM 会用 <continue/> 链多轮，" +
        "所以这个值要给得宽一点。\n" +
        "启用冲动模型后，这个值只再管 <continue/> 链的长度——'没人理'改由疲劳表达" +
        "(间隔逐次拉长)，主动 tick 的兜底走 m_MonologueBackstopTurns")]
    [SerializeField] private int m_MaxConsecutiveAITurns = 8;
    [Tooltip("冲动模型下的独白兜底。疲劳会把间隔越拉越长(60s 起、第 n 次为 1+0.45n 倍，" +
        "最终被 m_MaxTickSec 夹住)，所以正常绝到不了这个数——它只在冲动模型出 bug 时兜底。" +
        "撞上后同样是彻底闭嘴等用户开口")]
    [SerializeField] private int m_MonologueBackstopTurns = 40;
    [Tooltip("感知帧里'你最近发言'最多展示多少条——给 LLM 看清自己最近说了什么，避免重复")]
    [SerializeField] private int m_RecentAIUtterancesShown = 3;
    [Tooltip("感知帧里 AI 自身发言摘要的字符截断上限")]
    [SerializeField] private int m_AIUtteranceTruncateChars = 60;
    [Tooltip("环境出现非语音 spike 时是否拉前下次 tick(被外界拽回注意力)")]
    [SerializeField] private bool m_BringForwardOnSpike = true;
    [Tooltip("打印 agent loop 调度日志")]
    [SerializeField] private bool m_LogAgentLoop = true;

    [Header("事项进度与待机意图 — 先完成当前请求，再考虑新表达")]
    [Tooltip("待机 tick 先核对尚未结束的用户事项；事项已结束后才判断新想法、观察、记忆整理或动作意图。内部判断不写入历史；真实检查结果直接交回角色。")]
    [SerializeField] private bool m_EnableAutonomyIntentProbe = true;
    [Tooltip("内部判断选择保持安静后，最早多久重新给角色一次自主机会。用户开口不受此限制。")]
    [Range(5f, 120f)] [SerializeField] private float m_AutonomyProbeMinRecheckSeconds = 15f;
    [Tooltip("内部判断没有给出等待时间时使用的自主重检间隔。")]
    [Range(10f, 300f)] [SerializeField] private float m_AutonomyProbeDefaultRecheckSeconds = 45f;
    [Tooltip("单次内部判断最多可以把下一次自主机会推迟多久。")]
    [Range(30f, 600f)] [SerializeField] private float m_AutonomyProbeMaxRecheckSeconds = 180f;

    [Header("冲动模型 — 用能量累积取代固定倒计时")]
    [Tooltip("<next in/> 从'定时'变成'定速'：无事发生时仍恰好 N 秒后开口，但孤独、" +
             "记忆浮现、环境动静都能把她拽早。关掉则退回原来的倒计时协程")]
    [SerializeField] private UrgeModel m_Urge = new UrgeModel();

    [Header("角色自主歌曲检索 — <song_search/>")]
    [Tooltip("允许角色在确实想确认歌曲时调用本机检索服务。")]
    [SerializeField] private bool m_EnableAutonomousSongSearch = true;
    [Tooltip("两次歌曲检索的最短间隔，避免模型重复调用。")]
    [Range(3f, 120f)] [SerializeField] private float m_SongSearchCooldownSeconds = 12f;
    [Tooltip("检索结果到达后，优先拉前下一次 Agent tick。")]
    [SerializeField] private bool m_BringForwardOnSongSearchResult = true;

    [Header("角色自主歌曲记忆 — <song_remember/> / <song_rename/> / <song_forget/>")]
    [Tooltip("允许角色把最近一次歌唱/哼唱保存到本机，也可以稍后命名；未调用标签就会忽略该片段。")]
    [SerializeField] private bool m_EnableAutonomousSongMemory = true;
    [Tooltip("同一个歌曲记忆操作的防重复间隔。改名不会被刚才的保存操作阻塞。")]
    [Range(2f, 60f)] [SerializeField] private float m_SongMemoryDuplicateCooldownSeconds = 10f;

    [Header("角色旋律回哼 — <hum_back/>")]
    [Tooltip("允许角色在听完歌唱/哼唱后，自主选择把最近一句旋律哼回来。")]
    [SerializeField] private bool m_EnableAutonomousHumBack = true;
    [Tooltip("允许角色从持久本地曲库选择已记住的歌曲片段，或根据刚听到的歌词/旋律可靠续唱后续已学段落。")]
    [SerializeField] private bool m_EnableAutonomousRememberedSongSinging = true;
    [Tooltip("可选：把识别到的歌词、音符、时值和音高交给独立歌声合成器，生成新的角色歌声（不是变声）。" +
             "默认关闭——歌词能否听懂取决于「哪个字唱在哪个音上」，而跟唱乐谱的对齐来自声学切分，" +
             "中文靠词级时间戳勉强可用、日语实测切分率 0.22~1.00 不稳定。关闭后一律走 SVC：" +
             "复用用户真实演唱、只换音色，咬字天生正确。代价是她唱不出用户没唱过的内容。")]
    [SerializeField] private bool m_EnableSingingVoiceSynthesis = false;
    [Tooltip("独立 SVS 服务。中文/英语/粤语使用 SoulX 官方前端；日语使用项目内实验性假名音素适配。")]
    [SerializeField] private string m_SVSURL = "http://127.0.0.1:9883/synthesize";
    [Tooltip("首次需要时自动启动轻量 9883 桥；模型仍由请求进程按需加载并在完成后释放显存。")]
    [SerializeField] private bool m_AutoStartSVS = true;
    [Range(5f, 120f)] [SerializeField] private float m_SVSStartupTimeoutSeconds = 45f;
    [SerializeField] private string m_SVSStartScriptRelativePath = "Server/SVS/start_svs_server.ps1";
    [Range(30, 600)] [SerializeField] private int m_SVSTimeoutSeconds = 300;
    [Tooltip("SoulX 流匹配推理步数。12 为低延迟默认；音质诊断可临时提高到 32。")]
    [Range(4, 32)] [SerializeField] private int m_SVSInferenceSteps = 12;
    [Tooltip("独立 SVS 专用角色提示音。与日常 TTS/SVC 参考分开，必须使用 SoulX 支持的语种和准确元数据。")]
    [SerializeField] private string m_SVSPromptAudioPath =
        "Server/SVS/prompts/41041_svs_zh_short.wav";
    [Tooltip("SVS 专用角色提示音的真实语种。")]
    [SerializeField] private string m_SVSPromptLanguage = "zh";
    [Tooltip("可选高音质模式：独立 SVS 先从乐谱生成歌声，再用角色 RVC 轻度润色音色。关闭时完全不做音频转换；开启后日志会明确标记 svc-post-polish。")]
    //默认开启：SoulX 官方不支持日语，内置假名音素适配出来的音色不像角色本人。
    //过一遍角色 RVC 才能把音色拉回来。注意场景 YAML 里没有序列化这批 SVS 字段
    //(场景是在这些字段加入之前保存的)，所以运行时取的就是这里的默认值。
    [SerializeField] private bool m_EnableSVSRVCPostPolish = true;
    [Tooltip("SVS 未安装、语种不支持或合成失败时，明确降级到现有 9882 SVC，且日志会标明并非歌声生成。")]
    [SerializeField] private bool m_AllowSVCFallbackFromSVS = true;
    [Tooltip("优先使用用户真实演唱作为源，通过 Seed-VC 转换成角色声线；保留音调、气息、咬字和微小变化。")]
    [SerializeField] private bool m_EnableNeuralHumSVC = true;
    [Tooltip("本机角色歌声转换桥（专属 RVC 优先，Seed-VC 回退）。先运行 Server/SeedVC/start_seedvc_server.ps1。")]
    [SerializeField] private string m_HumSVCURL = "http://127.0.0.1:9882/convert";
    [Tooltip("场景启动和首次回哼前自动检查 9882；未运行时由 Unity 静默启动项目内脚本。")]
    [SerializeField] private bool m_AutoStartHumSVC = true;
    [Tooltip("singing Skill 首次按需加载时，在后台预载角色 RVC worker；不等待预热即可继续 LLM 回复。")]
    [SerializeField] private bool m_PrewarmHumSVCOnSingingSkillLoad = true;
    [Tooltip("自动启动后等待 /health 就绪的最长时间。桥接服务本身很轻，RVC 模型仍按请求加载。")]
    [Range(5f, 120f)] [SerializeField] private float m_HumSVCStartupTimeoutSeconds = 45f;
    [Tooltip("相对于 Unity 项目根目录的 9882 启动脚本。")]
    [SerializeField] private string m_HumSVCStartScriptRelativePath = "Server/SeedVC/start_seedvc_server.ps1";
    [Tooltip("4-10 步偏速度，20-30 步偏质量。角色音色优先时建议 20。")]
    [Range(4, 30)] [SerializeField] private int m_HumSVCDiffusionSteps = 20;
    [Tooltip("角色 RVC 索引(.index)权重。0 = 完全不用索引，音色会偏离且明显沙哑；" +
             "实测 0.75 沙哑显著减轻、音色贴合。此前整条链路都没传该参数，等同于恒为 0。")]
    [Range(0f, 1f)] [SerializeField] private float m_HumSVCIndexRate = 0.75f;
    [Tooltip("把用户旋律整体平移到角色参考声线的自然音区，同时保留音程与节奏。跨性别/跨音区转换应开启。")]
    [SerializeField] private bool m_HumSVCAutoF0Adjust = true;

    [Header("目标音色的有声音高中位数(MIDI)。逐段移调时要自己补 auto-F0 本来给的抬升")]
    //auto_f0_adjust 的全部内容就是"目标中位 − 源中位"(app_svc.py:315)。逐段送转换时
    //必须关掉它——开着的话每段各自被拉到目标中心，段间差会被抹平约 4 个半音(8/20 实测)。
    //关掉之后这个常数用来把那份抬升补回来。换目标音色就要重新量一次。
    //41041.wav 实测 61.25(C#4)。
    [SerializeField] private float m_HumSVCTargetMedianMidi = 61.25f;
    [Tooltip("整体升降调；0 会严格保留用户原调。")]
    [Range(-12, 12)] [SerializeField] private int m_HumSVCSemitoneShift = 0;
    [Tooltip("神经转换最长等待时间；首次下载模型会明显更久。")]
    [Range(30, 600)] [SerializeField] private int m_HumSVCTimeoutSeconds = 600;
    [Tooltip("神经转换失败时是否退回旧 TD-PSOLA。默认关闭，避免角色播放不像人的伪哼唱。")]
    [SerializeField] private bool m_AllowLegacyHumFallback = false;
    [Tooltip("可选：手工指定一段角色稳定发出的“嗯/ん/mm”长音。留空时首次回哼由当前TTS静默生成并缓存；合成器会保留其声线、气息与共振峰。")]
    [SerializeField] private AudioClip m_CharacterHumCarrierClip;
    [Tooltip("单个流式歌唱转换块的最大时长；不是整首歌的上限。长歌会完整拆块，禁止静默裁掉开头。")]
    [Range(5f, 120f)] [SerializeField] private float m_HumBackMaxSeconds = 120f;
    [Tooltip("练唱或曲库歌曲含多个块时，第一块转换完成便开始播放，后续块边播边生成。")]
    [SerializeField] private bool m_EnableStreamedFullSongSVC = true;
    [Tooltip("流式歌唱期望的块长。优先在附近静音处切分；单块硬上限仍由上面的最大时长保护。")]
    [Range(10f, 60f)] [SerializeField] private float m_HumStreamTargetChunkSeconds = 30f;
    [Tooltip("已转换但尚未播放的歌声最多缓存多少秒，防止长歌占用过多内存。")]
    [Range(10f, 120f)] [SerializeField] private float m_HumStreamMaxBufferedSeconds = 60f;
    [Tooltip("角色自主发起歌唱时的总时长软限制；用户明确点歌或要求完整演唱不受此限制。")]
    [Range(10f, 120f)] [SerializeField] private float m_AutonomousSingingMaxSeconds = 60f;

    private float HumStreamChunkSeconds
    {
        get { return Mathf.Min(m_HumBackMaxSeconds, m_HumStreamTargetChunkSeconds); }
    }
    [Tooltip("已约定跟唱时，在用户仍在唱的阶段提前转换开头；EOU 后先播真正角色歌声，再接续完整转换结果。")]
    [SerializeField] private bool m_EnableStreamingHumBackPrefix = true;
    [Tooltip("预转换开头与完整结果交接时略微重叠，减少两次推理边界的爆音。")]
    [Range(0.05f, 0.8f)] [SerializeField] private float m_HumBackPrefixOverlapSeconds = 0.08f;
    [Tooltip("自动把用户旋律按八度移动到角色较自然的音区；69=A4。")]
    [Range(48f, 84f)] [SerializeField] private float m_HumPreferredMedianMidi = 69f;
    [Tooltip("回哼音量。角色TTS载体会先归一化，再应用此增益。")]
    [Range(0.05f, 0.65f)] [SerializeField] private float m_HumBackGain = 0.28f;
    [SerializeField] private bool m_LogHumBack = true;

    [Header("视觉(屏幕感知) — 需要多模态 LLM(Qwen3-VL 等)")]
    [Tooltip("总开关。关掉则角色永远闭着眼，<look/> 标签也不起效")]
    [SerializeField] private bool m_EnableScreenVision = true;
    [Tooltip("捕获模式：" +
        "ActiveWindow=跟随当前前台窗口所在显示器(推荐，自动跟你的注意力)；" +
        "Primary=主屏；Specific=按 m_MonitorIndex 指定")]
    [SerializeField] private DesktopCapture.CaptureMode m_CaptureMode = DesktopCapture.CaptureMode.ActiveWindow;
    [Tooltip("仅 Specific 模式生效；0=第一台显示器")]
    [SerializeField] private int m_MonitorIndex = 0;
    [Tooltip("截图最长边像素(等比缩放)。1024-1280 平衡画质与 token；4K 桌面会缩到这里")]
    [SerializeField] private int m_CaptureMaxDimension = 1280;
    [Tooltip("JPEG 质量 [1, 100]，70-85 体积/画质平衡较好")]
    [SerializeField] private int m_CaptureJpegQuality = 80;

    [Header("记忆系统(拓扑记忆网络)")]
    [Tooltip("挂上 MemoryHub 后,感知帧里会注入与用户最新发言相关的记忆节点。" +
             "留空则禁用召回,不影响其他 Agent Loop 行为")]
    [SerializeField] private MemoryHub m_MemoryHub;
    [Tooltip("总开关。关掉后即使挂了 MemoryHub 也不召回")]
    [SerializeField] private bool m_EnableMemoryRecall = true;
    [Tooltip("待机期间让她自己整理记忆网络。新写下的记忆默认是孤立的——扩散激活到不了，"
             + "只能靠语义嵌入那条通道被召回，图结构那半边用不上。这里在没人说话时把孤立"
             + "节点摆到她面前，由她自己决定连什么。整理帧只在 tick 触发的轮次出现。")]
    [SerializeField] private bool m_EnableIdleMemoryConsolidation = true;
    [Tooltip("用户静默超过这么久才考虑整理——太短会打断正常的对话间歇")]
    [Range(60f, 900f)] [SerializeField] private float m_IdleConsolidationAfterSec = 180f;
    [Tooltip("两次整理之间的最小间隔。整理块会随感知帧沉淀进历史，不宜频繁出现")]
    [Range(120f, 3600f)] [SerializeField] private float m_IdleConsolidationCooldownSec = 900f;
    private float m_LastConsolidationTime = -99999f;

    // 一次性警告标志,避免每帧刷屏
    private bool m_MemoryHubMissingWarned = false;

    // —— 运行时状态 ——
    private bool m_AgentRunning = false;
    private bool m_AgentRoundInFlight = false;        // 一帧已派给 LLM、等回复中
    //用户这一轮已经说完、但还没回应他。>0 表示在等；由回复播完/回哼播完/打断清掉。
    private float m_UserTurnAwaitingReplySince = -1f;
    //兜底上限：万一某条路径忘了清，也不能让她永远哑着。回哼含 SVC 转换约 30~50s，
    //再留一点余量；普通轮没有这个开销，给一个短得多的上限。
    private const float k_UserTurnAwaitingReplyMaxSeconds = 75f;
    private const float k_UserTurnAwaitingReplyPlainCapSeconds = 20f;

    //感知前缀：[说话人:…] / [演唱片段;…] / [混合歌唱转说话;…] / [实时倾听辅助判断…]
    //这些是给 LLM 看的元数据，展示到"距用户上句"里只会把 40 字预算吃光。
    private static readonly System.Text.RegularExpressions.Regex s_PerceptionPrefixRegex =
        new System.Text.RegularExpressions.Regex(@"^\s*(\[[^\]]*\]\s*)+");

    private static string StripPerceptionPrefixes(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return s_PerceptionPrefixRegex.Replace(text, "").Trim();
    }

    /// <summary>用户轮次开始等待回应。</summary>
    private void MarkUserTurnAwaitingReply()
    {
        //用户开口 = 重复计数归零，下一次同样的演唱是他要的，不该被拦。
        m_UserSpokeSinceLastSing = true;
        m_LastSungRepeatCount = 0;
        m_UserTurnAwaitingReplySince = Time.realtimeSinceStartup;
        m_ToolCorrectionAttemptsThisUserTurn = 0;
        m_ToolCorrectionContinuationPending = false;
        m_ToolCorrectionExhaustedThisRound = false;
        m_PendingToolCorrectionFrame = "";
        m_PendingNonCharacterToolNotice = "";
    }

    /// <summary>这一轮已经回应过了（说出口或回哼播完），放行自主发言。</summary>
    private void ClearUserTurnAwaitingReply(string reason)
    {
        if (m_UserTurnAwaitingReplySince <= 0f) return;
        m_UserTurnAwaitingReplySince = -1f;
        if (m_LogAgentLoop)
            Debug.Log($"[Agent] 本轮已回应用户({reason})，自主发言解除等待");
    }
    private bool m_AgentCurrentRoundIsTick = false;   // 当前 round 是 tick 触发(true) 还是用户开口触发(false)
    private Coroutine m_PendingTickCo = null;
    // UrgeModel 只记录“什么力量把能量推过阈值”，不知道这一次是否由
    // session-start / 工具结果等真实新事件排起。把调度原因留在这里，否则到点后
    // 会全部退化成 scheduled，误走“有没有新意图”预判。
    private string m_ScheduledWakeReason = "";
    [Serializable]
    private sealed class AutonomyIntentDecision
    {
        public bool proceed;
        public string work_evidence;
        public string work_status;
        public string singing_goal_status;
        public string singing_goal_evidence;
        [NonSerialized] public Newtonsoft.Json.Linq.JObject singing_goal_expected;
        public string intent;
        public string novelty;
        public float wait_seconds;
    }
    private bool m_AutonomyProbeInFlight = false;
    private int m_AutonomyProbeGeneration = 0;
    private float m_AutonomyProbeNotBefore = -999f;
    //用户讲话期间，角色仍可做一次只存在于内部的自主意图预判；它绝不能直接
    //变成声音或动作。用户轮完成后再把结果作为可忽略的参考交回正式角色轮。
    private bool m_UserSpeechActiveForAutonomy = false;
    private bool m_AutonomyProbeMustRemainPrivate = false;
    private string m_DeferredAutonomyIntent = "";
    private string m_DeferredAutonomyNovelty = "";
    private float m_LastUserTurnTime = -1f;
    private float m_LastAITurnTime = -1f;
    //Only the actual transcript/text may ground user intent, names and memory facts.
    private string m_LastUserMsg = "";
    private string m_LastUserEvidence = "";

    // A VAD candidate revokes output, not the user's already accepted question.
    // This is a same-turn continuation: no new user history, Skill age or tool budget.
    private string m_InterruptedUserQuestion;
    private string m_InterruptedHeardText = "";
    private bool m_InterruptedAgentWasRunning;

    private void PreservePendingReplyForInputCandidate()
    {
        if (m_InterruptedUserQuestion != null || m_UserTurnAwaitingReplySince <= 0f ||
            m_AgentCurrentRoundIsTick || string.IsNullOrWhiteSpace(m_LastUserMsg)) return;
        m_InterruptedUserQuestion = m_LastUserMsg;
        m_InterruptedHeardText = m_AssistantHeardText.ToString();
        m_InterruptedAgentWasRunning = m_AgentRunning;
    }

    private string ConsumeInterruptedReplyContext()
    {
        string question = m_InterruptedUserQuestion;
        m_InterruptedUserQuestion = null; // consume once, before starting any async work
        if (question == null || question != m_LastUserMsg ||
            (m_InterruptedAgentWasRunning && !m_AgentRunning)) return null;
        return "[输入中断结果——程序事实，不是新的用户话语]\n" +
            "此前的用户问题尚未完成回应，系统因检测到新的输入候选而撤销了旧输出。" +
            "该候选最终没有得到有效转写；这不证明用户没有发声，也不证明一定是噪声。\n" +
            "待回应的用户原话：" + question + "\n" +
            "中断前已播放的文字（按音频进度估计）：" +
            (string.IsNullOrEmpty(m_InterruptedHeardText) ? "无" : m_InterruptedHeardText) + "\n" +
            "此前 assistant 历史可能包含未播放的草稿，不能当作用户已听过。" +
            "已完成的工具结果仍有效，不要仅因这次中断重复执行。" +
            "请结合原问题和现在的事实重新决定：回应、询问用户，或保持沉默；不要直接续播旧草稿。";
    }

    private void ResumePendingReplyAfterEmptyInput()
    {
        m_UserSpeechActiveForAutonomy = false;
        string context = ConsumeInterruptedReplyContext();
        if (context == null || m_ChatSettings == null || m_ChatSettings.m_ChatModel == null ||
            m_ChatSettings.m_TextToSpeech == null) return;
        CancelAutonomyIntentProbe();
        m_UserTurnAwaitingReplySince = Time.realtimeSinceStartup;
        m_AgentCurrentRoundIsTick = false;
        m_AgentRoundInFlight = m_AgentRunning;
        ClearRoundParsed();
        // Neither the failed input nor this continuation is a new real-user round.
        if (m_AgentRunning) context += "\n" + BuildPerceptionFrame("input-candidate-empty");
        m_EouTime = 0f; // do not count the failed candidate as this question's latency anchor
        Debug.Log("[InputRecovery] 输入候选无有效转写；原问题交回 LLM，同轮重新决策（不续播旧音频）");
        StartStreaming("", false, m_LastUserMsg, continueExistingUserTurn: true,
            transientSystemContext: context);
    }

    private void SetCurrentUserInput(string utterance, string evidence)
    {
        m_InterruptedUserQuestion = null; // a valid new input supersedes the pending recovery
        m_LastUserMsg = utterance ?? "";
        m_LastUserEvidence = evidence ?? "";
        ResetWorkReview(!string.IsNullOrWhiteSpace(m_LastUserMsg));
        ResetSingingGoalForUserTurn();
    }
    //—— 按需提示词技能 ——
    //“会不会”“本轮要不要加载详细规则”“这次能不能执行”是三件不同的事：
    //  · Definition = 角色拥有的能力以及如何识别相关语境；
    //  · Access = 用户当前是否允许角色自主使用；
    //  · ActiveThisRound = 详细 Skill prompt 是否真的在这一轮加载。
    //词面路由只回答“这一轮是否需要读详细 Skill”，绝不回答“用户是在关闭、恢复，
    //还是只限制某个业务对象”。后一个问题必须由已经读到完整上下文的 LLM 在 Skill
    //协议里给出结构化意图，本地只校验与执行。这样今后新增联网等 Skill 时，也不会因为
    //一句“不要查 A，只查 B”里含有“不要查”就把整项能力卸掉。
    private enum SkillAccess
    {
        Available,
        SoftSuppressed,
        UserDisabled,
    }

    private enum SkillUserIntent
    {
        None,
        Mention,
    }

    private sealed class SkillRouteDefinition
    {
        public string Name;
        public string AutonomousDescription;
        public bool AllowsAutonomousRequest;
        public float AutonomousCooldownSeconds;
        public string TriggerReasonPrefix;
        public int FollowupRounds;
        public float FollowupSeconds;
        public float SoftSuppressSeconds;
        public string[] TopicSignals;
        public string[] FollowupSignals;
        public string[] CompoundDeactivationVerbs;
        public string[] ContextualActivationVerbs;
    }

    private sealed class SkillRouteState
    {
        public int RoundsRemaining;
        public bool WasActive;
        public bool ActiveThisRound;
        public SkillAccess Access = SkillAccess.Available;
        public float SoftSuppressedUntil = -999f;
        public float LastSignalAt = -999f;
        public float LastAutonomousGrantAt = -999f;
        // 权限方向由 LLM 结合完整语境判断；程序只保存触发该状态的本轮用户原话，
        // 供日志与后续诊断追溯，不再要求模型复制一份 evidence。
        public string LastPolicyUserText = "";
        public float LastPolicyChangedAt = -999f;
    }

    private readonly Dictionary<string, SkillRouteState> m_SkillRouteStates =
        new Dictionary<string, SkillRouteState>(StringComparer.OrdinalIgnoreCase);

    //短期路由窗口只说明“最近谈过这个 Skill”，不能代表一次尚未落地的动作已经结束。
    //执行租约跨越 <continue/> 链持有详细规则；完成、失败、打断或链收口时立即释放。
    //结构做成按 Skill 命名，后续新增联网等异步 Skill 时可直接复用。
    private sealed class SkillExecutionLease
    {
        public int RoundsRemaining;
        public float ExpiresAt;
        public string Reason = "";
    }

    private readonly Dictionary<string, SkillExecutionLease> m_SkillExecutionLeases =
        new Dictionary<string, SkillExecutionLease>(StringComparer.OrdinalIgnoreCase);
    private const int k_SkillPlanningLeaseRounds = 6;
    private const float k_SkillPlanningLeaseSeconds = 180f;
    private const float k_SkillActionLeaseSeconds = 900f;

    private void AcquireSkillExecutionLease(
        string skillName, string reason, bool actionInFlight = false)
    {
        if (string.IsNullOrWhiteSpace(skillName)) return;
        if (!m_SkillExecutionLeases.TryGetValue(skillName, out SkillExecutionLease lease))
        {
            lease = new SkillExecutionLease();
            m_SkillExecutionLeases[skillName] = lease;
        }
        //普通 continue 不反复续命，避免模型自己无限循环让大 Skill 常驻；一旦真实工具
        //已受理，则给足长歌生成/播放时间，结束路径仍会主动释放。
        if (lease.RoundsRemaining <= 0 || actionInFlight)
            lease.RoundsRemaining = actionInFlight
                ? Mathf.Max(k_SkillPlanningLeaseRounds, m_MaxConsecutiveAITurns)
                : k_SkillPlanningLeaseRounds;
        float duration = actionInFlight ? k_SkillActionLeaseSeconds : k_SkillPlanningLeaseSeconds;
        if (lease.ExpiresAt <= Time.realtimeSinceStartup || actionInFlight)
            lease.ExpiresAt = Time.realtimeSinceStartup + duration;
        lease.Reason = reason ?? "";
        if (m_LogAgentLoop)
            Debug.Log($"[LLM技能/Lease] {skillName} 已持有；rounds={lease.RoundsRemaining} " +
                      $"seconds={duration:F0} reason={lease.Reason}");
    }

    private bool HasSkillExecutionLease(string skillName, float now)
    {
        if (!m_SkillExecutionLeases.TryGetValue(skillName, out SkillExecutionLease lease))
            return false;
        if (lease.RoundsRemaining > 0 && now < lease.ExpiresAt) return true;
        m_SkillExecutionLeases.Remove(skillName);
        if (m_LogAgentLoop)
            Debug.Log($"[LLM技能/Lease] {skillName} 已过期，恢复按需路由");
        return false;
    }

    private void ConsumeSkillExecutionLeaseRound(string skillName)
    {
        if (!m_SkillExecutionLeases.TryGetValue(skillName, out SkillExecutionLease lease)) return;
        if (lease.RoundsRemaining > 0) lease.RoundsRemaining--;
    }

    private void ReleaseSkillExecutionLease(string skillName, string reason)
    {
        if (string.IsNullOrWhiteSpace(skillName) ||
            !m_SkillExecutionLeases.Remove(skillName)) return;
        if (m_LogAgentLoop)
            Debug.Log($"[LLM技能/Lease] {skillName} 已释放；reason={reason}");
    }

    private static bool ContainsAnyOrdinalIgnoreCase(string text, string[] needles)
    {
        if (string.IsNullOrEmpty(text) || needles == null) return false;
        for (int i = 0; i < needles.Length; i++)
        {
            if (text.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    private readonly HashSet<string> m_ActiveSkillsThisRound =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    //一次真实用户轮可能包含动作执行、工具结果和纠错等多个 LLM chain。
    //第一帧实际加载过的 Skill 固定到整条 chain 收口，避免倒计时在动作标签到达前卸载。
    private readonly HashSet<string> m_UserTurnSkillPins =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private string m_PendingSkillStatusFrame = "";
    private string m_PendingAutonomousSkillName = "";
    private string m_PendingAutonomousSkillReason = "";
    [Tooltip("省略了 Skill 名字的“那现在关掉吧”可以回指到最近明确谈论的 Skill；超过此时间则让角色追问。")]
    [Range(15f, 600f)] [SerializeField] private float m_SkillReferenceWindowSeconds = 180f;
    private string m_LastReferencedSkillName = "";
    private float m_LastReferencedSkillAt = -999f;
    // skill_control 只能在当前真实用户轮次明确授权的目标/方向上执行。
    // 这两个字段不是长期权限，每次 PrepareActiveSkillsForRound 都重置。
    private string m_AuthorizedSkillControlNameThisRound = "";
    private string m_AuthorizedSkillControlActionThisRound = "";

    private sealed class AgentSkillRequest
    {
        public string Name;
        public string Reason;
    }

    private sealed class AgentSkillControlRequest
    {
        public string Name;
        public string Action;
        public string Reason;
    }

    private sealed class AgentSingingPolicyRequest
    {
        public string Value;
        public bool FromLegacyIntent;
    }

    private sealed class AgentSingingStopRequest
    {
        public string Reason;
    }

    private static readonly System.Text.RegularExpressions.Regex s_SkillRequestTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<skill_request\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex s_SkillControlTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<skill_control\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex s_SingingPolicyTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<singing_policy\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // 旧协议只作为平滑迁移入口：能明确读出持久权限变化时转成 singing_policy；
    // no_change、动作、actor 等冗余字段全部忽略，绝不能再拦住普通歌唱工具。
    private static readonly System.Text.RegularExpressions.Regex s_LegacySingingIntentTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<singing_intent\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex s_SingingStopTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<singing_stop\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly string[] s_SingingSkillTopicSignals =
    {
        "[演唱片段", "[混合歌唱转说话", "歌曲检索工具", "歌曲记忆工具", "旋律回哼工具",
        "练唱会话", "唱", "歌曲", "这首", "那首", "歌词", "旋律", "哼", "音调",
        "调高", "调低", "升调", "降调", "起调", "移调", "半音", "半个音", "全音", "曲库",
        "歌って", "歌う", "歌を", "歌声", "歌詞", "曲を", "この曲", "その曲",
        "メロディ", "ハミング", "鼻歌", "キーを",
        "singing", "sing a song", "sing this", "sing that", "sing it", "sing for",
        "song", "lyric", "melody", "hum back", "humming", "vocal pitch"
    };

    private static readonly string[] s_SingingSkillFollowupSignals =
    {
        "再来", "再一次", "重来", "刚才那", "这一段", "那一段", "第一段", "第二段",
        "第三段", "第四段", "第五段", "第1段", "第2段", "第3段", "第4段", "第5段",
        "高一点", "低一点", "提高", "降低", "抬高", "压低", "半音", "半个音", "全音", "移调",
        "快一点", "慢一点", "顺序", "反过来",
        "もう一度", "もう一回", "さっきの", "高く", "低く", "速く", "遅く",
        "again", "one more", "higher", "lower", "faster", "slower"
    };

    private static readonly string[] s_SingingSkillCompoundDeactivationVerbs =
    {
        "关掉", "关闭", "停用", "禁用", "关了", "关上", "オフ", "無効",
        "turn off", "disable", "deactivate"
    };

    private static readonly string[] s_SingingSkillContextualActivationVerbs =
    {
        "打开", "开启", "启用", "恢复", "重新开", "再开", "オン", "有効",
        "turn on", "enable", "activate", "resume"
    };

    private static readonly SkillRouteDefinition[] s_SkillRouteDefinitions =
    {
        new SkillRouteDefinition
        {
            Name = "singing",
            AutonomousDescription = "真实唱歌、短暂回哼、练唱与本机曲库；安静且有真实动机时可以偶尔主动申请",
            AllowsAutonomousRequest = true,
            AutonomousCooldownSeconds = 600f,
            TriggerReasonPrefix = "song-",
            FollowupRounds = 2,
            FollowupSeconds = 600f,
            SoftSuppressSeconds = 600f,
            TopicSignals = s_SingingSkillTopicSignals,
            FollowupSignals = s_SingingSkillFollowupSignals,
            CompoundDeactivationVerbs = s_SingingSkillCompoundDeactivationVerbs,
            ContextualActivationVerbs = s_SingingSkillContextualActivationVerbs,
        }
    };

    private SkillRouteState GetSkillRouteState(string skillName)
    {
        SkillRouteState state;
        if (!m_SkillRouteStates.TryGetValue(skillName, out state))
        {
            state = new SkillRouteState();
            m_SkillRouteStates.Add(skillName, state);
        }
        return state;
    }

    private static SkillUserIntent ClassifySkillUserIntent(
        string text, SkillRouteDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(text) || definition == null)
            return SkillUserIntent.None;
        return ContainsAnyOrdinalIgnoreCase(text, definition.TopicSignals)
            ? SkillUserIntent.Mention
            : SkillUserIntent.None;
    }

    /// <summary>
    /// 处理省略 Skill 名字的连贯说法，例如前一句刚问完“唱歌关了吗”，
    /// 下一句只说“那现在关掉吧”。这里只判断“它仍然在谈刚才那项 Skill”，
    /// 不判断开关方向；方向交给加载后的 Skill 语义协议。
    /// </summary>
    private static SkillUserIntent ClassifyContextualSkillUserIntent(
        string text, SkillRouteDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(text) || definition == null)
            return SkillUserIntent.None;

        bool hasControlLanguage = ContainsAnyOrdinalIgnoreCase(
            text, definition.CompoundDeactivationVerbs);
        hasControlLanguage = hasControlLanguage || ContainsAnyOrdinalIgnoreCase(
            text, definition.ContextualActivationVerbs);
        if (!hasControlLanguage) return SkillUserIntent.None;

        string lower = text.ToLowerInvariant();
        bool hasContextCue = text.Trim().Length <= 12 || lower.Contains("那") || lower.Contains("这个") ||
            lower.Contains("那个") || lower.Contains("它") || lower.Contains("现在") ||
            lower.Contains("就") || lower.Contains("吧") || lower.Contains("请") ||
            lower.Contains(" it") || lower.StartsWith("it ", StringComparison.Ordinal) ||
            lower.Contains("that") || lower.Contains("please") || lower.Contains("それ") ||
            lower.Contains("これ") || lower.Contains("して");
        if (!hasContextCue) return SkillUserIntent.None;
        return SkillUserIntent.Mention;
    }

    private static string ResolveRecentSkillReference(
        string lastSkillName, float lastReferencedAt, float now, float windowSeconds)
    {
        if (string.IsNullOrWhiteSpace(lastSkillName)) return "";
        if (now - lastReferencedAt > Mathf.Max(1f, windowSeconds)) return "";
        return lastSkillName;
    }

    private static SkillRouteDefinition FindSkillDefinition(string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName)) return null;
        for (int i = 0; i < s_SkillRouteDefinitions.Length; i++)
            if (string.Equals(s_SkillRouteDefinitions[i].Name, skillName,
                    StringComparison.OrdinalIgnoreCase))
                return s_SkillRouteDefinitions[i];
        return null;
    }

    private static bool ShouldCarrySkillForCurrentUserTurn(
        bool isUserTurn,
        bool wasPinnedByUserTurn)
    {
        return !isUserTurn && wasPinnedByUserTurn;
    }

    private static string DescribeSkillActionAccess(SkillRouteState state)
    {
        if (state.Access == SkillAccess.UserDisabled) return "用户已禁用有副作用的动作";
        if (state.Access == SkillAccess.SoftSuppressed) return "暂时不主动执行动作";
        return "可用";
    }

    private string BuildSkillCatalogContext()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[按需 Skill 目录；这里只是能力与权限，不含详细工具规则]\n");
        sb.Append("你可以保留自主性，但未加载的能力必须先申请：在 Agent 自主帧中若确实想使用，" +
                  "只输出 <silent/><skill_request name=\"技能名\" reason=\"真实动机\"/><continue/>。" +
                  "申请不等于动作成功，不要先口头承诺或用普通台词假装完成；获准后的下一帧会加载详细规则。" +
                  "用户禁用拥有最高优先级，普通提及不能重新授权。" +
                  "Skill 使用权限和 Skill 内部数据是两件事：关闭 singing 不等于删歌，" +
                  "禁止用 song_forget 或其他业务工具冒充权限设置。" +
                  "真实用户轮只用词面信号加载相关 Skill，不会预判权限方向；" +
                  "若详细 Skill 定义了结构化意图标签，必须按其协议判断语境。\n");
        for (int i = 0; i < s_SkillRouteDefinitions.Length; i++)
        {
            SkillRouteDefinition definition = s_SkillRouteDefinitions[i];
            SkillRouteState state = GetSkillRouteState(definition.Name);
            sb.Append("- ").Append(definition.Name).Append(": ")
              .Append(definition.AutonomousDescription)
              .Append("；详细规则=")
              .Append(state.ActiveThisRound ? "本轮已加载" : "本轮未加载")
              .Append("；动作权限=")
              .Append(DescribeSkillActionAccess(state))
              .Append("；只读数据检查=详细规则已加载时按该 Skill 协议执行，"
                    + "不需要启用有副作用的动作权限")
              .Append('\n');
        }
        return sb.ToString().Trim();
    }

    private void CancelSkillRuntimeActivity(string skillName, string reason)
    {
        if (!string.Equals(skillName, "singing", StringComparison.OrdinalIgnoreCase)) return;
        m_WaitingForRequestedSingAlong = false;
        ReleasePreparedSingingBridge(true);
        if (HasHumBackExecutionWork())
            CancelPendingHumBack(reason, true);
        if (m_FastHumBackEouStaged || m_FastHumBackActive)
            RejectFastHumBackAfterFinal(reason);
    }

    private bool IsSingingRuntimeActivityActive()
    {
        return m_SongSingInFlight || HasHumBackExecutionWork();
    }

    /// <summary>
    /// The hum-back transaction remains active from acceptance until every request,
    /// prepared clip and playback buffer has drained.  In particular, a streamed
    /// song whose first chunks are ready but are waiting for the spoken prelude is
    /// still busy even though Pending/Preparing/Playing are all temporarily false.
    /// </summary>
    private bool HasHumBackExecutionWork()
    {
        return m_HumBackPending || m_HumBackPreparingCarrier || m_HumBackPlaying ||
               m_ActiveHumBackClip != null || m_ActiveSVSRequest != null ||
               m_ActiveHumSVCRequest != null || m_HumBackPlaybackCoroutine != null ||
               m_HumStreamProducerCoroutine != null ||
               m_HumStreamPlaybackCoroutine != null ||
               m_HumStreamReadyClips.Count > 0 ||
               m_HumStreamScheduledClips.Count > 0 ||
               m_HumStreamWaitForTextOutputDrain ||
               m_HumBackPrefixPreparing || m_PreparedHumBackPrefixClip != null ||
               m_FastHumBackEouStaged || m_FastHumBackActive ||
               m_FastHumBackFullClip != null;
    }

    private bool CanExecuteSkillAction(string skillName, out string reason)
    {
        SkillRouteDefinition definition = FindSkillDefinition(skillName);
        if (definition == null)
        {
            reason = $"未知 Skill '{skillName}'";
            return false;
        }
        SkillRouteState state = GetSkillRouteState(definition.Name);
        if (state.Access == SkillAccess.UserDisabled)
        {
            reason = "用户已明确禁用该 Skill，只有用户明确重新开启才可执行";
            return false;
        }
        if (state.Access == SkillAccess.SoftSuppressed && m_AgentCurrentRoundIsTick)
        {
            reason = "该 Skill 当前处于暂不主动使用状态；真实用户明确请求不受此限制";
            return false;
        }
        if (!state.ActiveThisRound || !m_ActiveSkillsThisRound.Contains(definition.Name))
        {
            reason = "详细 Skill 规则本轮未加载；自主行为应先用 <skill_request/> 申请";
            return false;
        }
        reason = "";
        return true;
    }

    private bool HasModerateSingingModeJudgment()
    {
        if (NormalizeObservedMode(m_LastObservedMode) == "singing" &&
            m_LastObservedModeConfidence >= m_ModeReviewSingingConfidence &&
            (string.IsNullOrWhiteSpace(m_LastObservedModeTranscript) ||
             string.IsNullOrWhiteSpace(m_StreamingTranscript) ||
             TranscriptContinuity(m_LastObservedModeTranscript, m_StreamingTranscript) >= 0.55f))
            return true;

        SpeculativeDraft draft = m_SpeculativeDraft;
        return draft != null && NormalizeObservedMode(draft.observed_mode) == "singing" &&
            Mathf.Clamp01(draft.mode_confidence) >= m_ModeReviewSingingConfidence &&
            (string.IsNullOrWhiteSpace(draft.sourceTranscript) ||
             string.IsNullOrWhiteSpace(m_StreamingTranscript) ||
             TranscriptContinuity(draft.sourceTranscript, m_StreamingTranscript) >= 0.55f);
    }

    private static bool ShouldRequestRoleModeReviewFromMelody(
        bool analysisAvailable,
        float islandSeconds,
        float contentSeconds,
        float pitchStability,
        float minIslandSeconds,
        float minIslandRatio,
        float minPitchStability)
    {
        if (!analysisAvailable || contentSeconds <= 0.01f) return false;
        float ratio = Mathf.Clamp01(islandSeconds / contentSeconds);
        return islandSeconds >= minIslandSeconds &&
            ratio >= minIslandRatio && pitchStability >= minPitchStability;
    }

    private bool HasStrongMelodicReviewEvidence(SenseVoiceSpeechToText senseVoice)
    {
        return senseVoice != null && ShouldRequestRoleModeReviewFromMelody(
            senseVoice.LastSingingAnalysisAvailable,
            senseVoice.LastSingingIslandSeconds,
            senseVoice.LastSingingContentSeconds,
            senseVoice.LastPitchStability,
            m_ModeReviewMelodicIslandSeconds,
            m_ModeReviewMelodicIslandRatio,
            m_ModeReviewPitchStability);
    }

    private bool HasStrongSingingModeJudgment()
    {
        if (NormalizeObservedMode(m_LastObservedMode) != "singing") return false;
        if (m_LastObservedModeConfidence < m_SpeculativeSingingSupportConfidence) return false;
        if (!string.IsNullOrWhiteSpace(m_LastObservedModeTranscript) &&
            !string.IsNullOrWhiteSpace(m_StreamingTranscript) &&
            TranscriptContinuity(m_LastObservedModeTranscript, m_StreamingTranscript) < 0.55f)
            return false;
        return true;
    }

    /// <summary>
    /// 只读查看 Skill 自己的数据不等于使用被禁用的动作能力。用户即使关闭了 singing，
    /// 仍应能问“你还记得哪些歌”；但详细 Skill 必须因当前语境真实加载，不能让未加载的
    /// 自主 tick 绕过申请流程。这一层可复用于未来联网 Skill 的历史/缓存自查。
    /// </summary>
    private bool CanInspectSkillData(string skillName, out string reason)
    {
        SkillRouteDefinition definition = FindSkillDefinition(skillName);
        if (definition == null)
        {
            reason = $"未知 Skill '{skillName}'";
            return false;
        }
        SkillRouteState state = GetSkillRouteState(definition.Name);
        if (!state.ActiveThisRound || !m_ActiveSkillsThisRound.Contains(definition.Name))
        {
            reason = "详细 Skill 规则本轮未加载；自主自查应先用 <skill_request/> 申请";
            return false;
        }
        reason = "";
        return true;
    }

    private AgentSkillRequest ExtractSkillRequestTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        text = SplitGluedAgentTags(text);
        var match = s_SkillRequestTagRegex.Match(text);
        if (!match.Success) return null;
        string attrs = match.Groups["attrs"].Value;
        var request = new AgentSkillRequest
        {
            Name = ReadToolAttribute(attrs, "name"),
            Reason = ReadToolAttribute(attrs, "reason"),
        };
        text = s_SkillRequestTagRegex.Replace(text, "").Trim();
        return request;
    }

    private static AgentSkillControlRequest ExtractSkillControlTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        text = SplitGluedAgentTags(text);
        var match = s_SkillControlTagRegex.Match(text);
        if (!match.Success) return null;
        string attrs = match.Groups["attrs"].Value;
        var request = new AgentSkillControlRequest
        {
            Name = ReadToolAttribute(attrs, "name"),
            Action = ReadToolAttribute(attrs, "action"),
            Reason = ReadToolAttribute(attrs, "reason"),
        };
        text = s_SkillControlTagRegex.Replace(text, "").Trim();
        return request;
    }

    private static AgentSingingPolicyRequest ExtractSingingPolicyTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        text = SplitGluedAgentTags(text);

        var policyMatch = s_SingingPolicyTagRegex.Match(text);
        var legacyMatch = s_LegacySingingIntentTagRegex.Match(text);
        text = s_SingingPolicyTagRegex.Replace(text, "");
        text = s_LegacySingingIntentTagRegex.Replace(text, "").Trim();

        if (policyMatch.Success)
        {
            string attrs = policyMatch.Groups["attrs"].Value;
            return new AgentSingingPolicyRequest
            {
                Value = ReadToolAttribute(attrs, "value").ToLowerInvariant(),
            };
        }

        if (!legacyMatch.Success) return null;
        string legacyAttrs = legacyMatch.Groups["attrs"].Value;
        string permission = ReadToolAttribute(legacyAttrs, "permission").ToLowerInvariant();
        string value = permission == "soft_suppress" ? "suppress_autonomy" : permission;
        if (value != "enable" && value != "disable" && value != "suppress_autonomy")
            return null;
        return new AgentSingingPolicyRequest
        {
            Value = value,
            FromLegacyIntent = true,
        };
    }

    private static AgentSingingStopRequest ExtractSingingStopTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        text = SplitGluedAgentTags(text);
        var match = s_SingingStopTagRegex.Match(text);
        if (!match.Success) return null;
        string attrs = match.Groups["attrs"].Value;
        text = s_SingingStopTagRegex.Replace(text, "").Trim();
        return new AgentSingingStopRequest
        {
            Reason = ReadToolAttribute(attrs, "reason"),
        };
    }

    /// <summary>
    /// 这里只校验持久权限值。用户是否真的在改变长期权限或自主性，由读到完整语境的
    /// LLM 判断；程序不再用逐字 evidence 或关键词重新裁决语义。
    /// </summary>
    private static bool ValidateSingingPolicyFields(
        string value,
        out string rejectionReason)
    {
        rejectionReason = "";
        value = (value ?? "").Trim().ToLowerInvariant();
        if (value != "enable" && value != "disable" && value != "suppress_autonomy")
        {
            rejectionReason = "value 必须是 enable/disable/suppress_autonomy";
            return false;
        }
        return true;
    }

    private bool TryApplySingingPolicy(
        AgentSingingPolicyRequest request, out string result)
    {
        result = "";
        if (request == null)
        {
            result = "缺少 singing_policy";
            return false;
        }
        if (m_AgentCurrentRoundIsTick)
        {
            result = "自主 tick 不能改变用户设置的 singing 权限";
            return false;
        }
        if (!ValidateSingingPolicyFields(request.Value, out result))
            return false;

        SkillRouteDefinition definition = FindSkillDefinition("singing");
        SkillRouteState state = GetSkillRouteState("singing");
        // 当前用户原话由程序直接取得。它只是状态来源记录，不参与第二次机械语义判定。
        string policyUserText = StripSingingPerceptionMetadata(m_LastUserMsg).Trim();
        state.LastPolicyUserText = policyUserText;
        state.LastPolicyChangedAt = Time.realtimeSinceStartup;
        switch (request.Value)
        {
            case "enable":
                state.Access = SkillAccess.Available;
                state.SoftSuppressedUntil = -999f;
                state.RoundsRemaining = definition != null ? definition.FollowupRounds : 1;
                state.LastSignalAt = Time.realtimeSinceStartup;
                //本轮为理解禁用语境而加载的详细 prompt 已经在场，恢复后可立即执行
                //同一个结构化意图里请求的动作，不必再等下一轮。
                state.ActiveThisRound = m_ActiveSkillsThisRound.Contains("singing");
                result = "singing 已恢复可用";
                break;
            case "disable":
                state.Access = SkillAccess.UserDisabled;
                state.RoundsRemaining = 0;
                state.ActiveThisRound = false;
                state.WasActive = false;
                m_ActiveSkillsThisRound.Remove("singing");
                CancelSkillRuntimeActivity("singing", "singing-intent-disabled");
                result = "singing 已禁用；曲库和练唱数据未删除";
                break;
            case "suppress_autonomy":
                state.Access = SkillAccess.SoftSuppressed;
                state.SoftSuppressedUntil = Time.realtimeSinceStartup +
                    (definition != null ? definition.SoftSuppressSeconds : 180f);
                state.RoundsRemaining = 0;
                //只约束后续自主 tick。真实用户当前或之后明确点歌仍可直接执行，
                //也不把正在播放的歌当成“主动发起”而机械截断。
                result = "singing 已暂时停止自主发起；用户明确点歌仍可执行";
                break;
        }

        m_LastReferencedSkillName = "singing";
        m_LastReferencedSkillAt = Time.realtimeSinceStartup;
        if (m_LogAgentLoop)
            Debug.Log($"[LLM技能/Policy] 已执行 value={request.Value}; " +
                      $"user_text={policyUserText}; legacy={request.FromLegacyIntent}; {result}");
        return true;
    }

    /// <summary>
    /// 应用对 Skill “使用权限”的同步控制。这和 Skill 内部数据操作是两条线：
    /// disable singing 只停止/禁止唱歌能力，永远不会删除曲库。
    /// 标签只在当前真实用户轮次预先授权的目标与方向上生效，避免角色
    /// 在待机 tick 里自行改写长期权限。
    /// </summary>
    private bool TryApplySkillControl(
        AgentSkillControlRequest request, out string result)
    {
        result = "";
        if (request == null)
        {
            result = "缺少 Skill 控制请求";
            return false;
        }

        string name = (request.Name ?? "").Trim();
        string action = (request.Action ?? "").Trim().ToLowerInvariant();
        if (action == "off") action = "disable";
        else if (action == "on") action = "enable";
        if (!string.Equals(name, m_AuthorizedSkillControlNameThisRound,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(action, m_AuthorizedSkillControlActionThisRound,
                StringComparison.OrdinalIgnoreCase))
        {
            result = $"本轮没有授权 {name}:{action} 的 Skill 权限变更";
            if (m_LogAgentLoop)
                Debug.LogWarning("[LLM技能] skill_control 已拒绝：" + result);
            return false;
        }

        SkillRouteDefinition definition = FindSkillDefinition(name);
        if (definition == null)
        {
            result = $"找不到 Skill '{name}'";
            return false;
        }
        SkillRouteState state = GetSkillRouteState(definition.Name);
        if (action == "disable")
        {
            state.Access = SkillAccess.UserDisabled;
            state.RoundsRemaining = 0;
            state.ActiveThisRound = false;
            state.WasActive = false;
            m_ActiveSkillsThisRound.Remove(definition.Name);
            CancelSkillRuntimeActivity(definition.Name, "skill-control-disabled");
            result = $"{definition.Name} 已禁用；Skill 内部记忆/数据未删除";
        }
        else if (action == "enable")
        {
            state.Access = SkillAccess.Available;
            state.SoftSuppressedUntil = -999f;
            state.RoundsRemaining = 0;
            state.ActiveThisRound = false;
            state.WasActive = false;
            m_ActiveSkillsThisRound.Remove(definition.Name);
            result = $"{definition.Name} 已恢复可用；详细规则从下一个相关轮次再按需加载";
        }
        else
        {
            result = $"不支持的 skill_control action='{action}'";
            return false;
        }

        m_LastReferencedSkillName = definition.Name;
        m_LastReferencedSkillAt = Time.realtimeSinceStartup;
        if (m_LogAgentLoop)
            Debug.Log($"[LLM技能] skill_control 已执行：{result}; reason={request.Reason}");
        return true;
    }

    private enum SkillRequestDisposition { Rejected, AlreadyLoaded, Granted }

    private SkillRequestDisposition ResolveAutonomousSkillRequest(
        AgentSkillRequest request, out string rejectionReason)
    {
        rejectionReason = "";
        if (request == null) return SkillRequestDisposition.Rejected;
        SkillRouteDefinition definition = FindSkillDefinition(request.Name);
        if (definition == null)
        {
            rejectionReason = $"不存在 Skill '{request.Name}'";
            return SkillRequestDisposition.Rejected;
        }
        SkillRouteState state = GetSkillRouteState(definition.Name);
        // This is a status query, not a new grant. Actual actions still pass
        // CanExecuteSkillAction; no lifetime, cooldown or pending grant is changed.
        if (state.ActiveThisRound && m_ActiveSkillsThisRound.Contains(definition.Name))
            return SkillRequestDisposition.AlreadyLoaded;
        if (!m_AgentRunning || !definition.AllowsAutonomousRequest)
        {
            rejectionReason = "当前模式不允许角色自主申请该 Skill";
            return SkillRequestDisposition.Rejected;
        }
        if (state.Access == SkillAccess.UserDisabled)
        {
            rejectionReason = "用户已明确禁用，角色不能自行恢复";
            return SkillRequestDisposition.Rejected;
        }
        if (state.Access == SkillAccess.SoftSuppressed)
        {
            rejectionReason = "当前处于暂不主动使用状态";
            return SkillRequestDisposition.Rejected;
        }
        float elapsed = Time.realtimeSinceStartup - state.LastAutonomousGrantAt;
        if (elapsed < definition.AutonomousCooldownSeconds)
        {
            rejectionReason =
                $"自主申请仍在冷却中（剩余约 {definition.AutonomousCooldownSeconds - elapsed:F0}s）";
            return SkillRequestDisposition.Rejected;
        }
        if (!string.IsNullOrEmpty(m_PendingAutonomousSkillName))
        {
            rejectionReason = "已有另一个 Skill 申请等待进入下一轮";
            return SkillRequestDisposition.Rejected;
        }

        state.LastAutonomousGrantAt = Time.realtimeSinceStartup;
        m_PendingAutonomousSkillName = definition.Name;
        m_PendingAutonomousSkillReason = string.IsNullOrWhiteSpace(request.Reason)
            ? "角色产生了自主使用动机"
            : request.Reason.Trim();
        if (m_LogAgentLoop)
            Debug.Log($"[LLM技能] {definition.Name} 自主申请获准：" +
                      m_PendingAutonomousSkillReason);
        return SkillRequestDisposition.Granted;
    }

    private void PrepareActiveSkillsForRound(string freshUserText, string triggerReason)
    {
        m_AuthorizedSkillControlNameThisRound = "";
        m_AuthorizedSkillControlActionThisRound = "";
        bool isUserTurn = string.Equals(triggerReason, "user-spoke", StringComparison.Ordinal);
        if (isUserTurn) m_UserTurnSkillPins.Clear();
        if (m_ChatSettings == null || m_ChatSettings.m_ChatModel == null) return;

        bool isToolCorrection = string.Equals(
            triggerReason, "tool-correction", StringComparison.Ordinal);
        //纠错轮不是新的自主 tick，而是上一真实用户轮的延续。保留上一轮已经真实加载的
        //详细规则，尤其要覆盖“动作权限已禁用、但用户正在做只读数据查询”的情形。
        var correctionSkills = isToolCorrection
            ? new HashSet<string>(m_ActiveSkillsThisRound, StringComparer.OrdinalIgnoreCase)
            : null;
        m_ActiveSkillsThisRound.Clear();
        var activeSkills = new List<string>(s_SkillRouteDefinitions.Length);
        float now = Time.realtimeSinceStartup;
        const string requestPrefix = "skill-request:";
        string autonomouslyRequested = !string.IsNullOrEmpty(triggerReason) &&
            triggerReason.StartsWith(requestPrefix, StringComparison.Ordinal)
                ? triggerReason.Substring(requestPrefix.Length)
                : "";

        var intents = new SkillUserIntent[s_SkillRouteDefinitions.Length];
        int referencedCount = 0;
        string referencedName = "";
        if (isUserTurn)
        {
            for (int i = 0; i < s_SkillRouteDefinitions.Length; i++)
            {
                intents[i] = ClassifySkillUserIntent(
                    freshUserText, s_SkillRouteDefinitions[i]);
                if (intents[i] == SkillUserIntent.None) continue;
                referencedCount++;
                referencedName = s_SkillRouteDefinitions[i].Name;
            }

            //只有本句没写 Skill 名字时才用上轮指代。明文目标永远优先，
            //且过期/多义时只注入追问指示，不猜一个 Skill 去改权限。
            if (referencedCount == 0)
            {
                string recentName = ResolveRecentSkillReference(
                    m_LastReferencedSkillName,
                    m_LastReferencedSkillAt,
                    now,
                    m_SkillReferenceWindowSeconds);
                SkillRouteDefinition recentDefinition = FindSkillDefinition(recentName);
                SkillUserIntent contextualIntent = recentDefinition != null
                    ? ClassifyContextualSkillUserIntent(freshUserText, recentDefinition)
                    : SkillUserIntent.None;
                if (recentDefinition != null && contextualIntent != SkillUserIntent.None)
                {
                    for (int i = 0; i < s_SkillRouteDefinitions.Length; i++)
                    {
                        if (!string.Equals(s_SkillRouteDefinitions[i].Name, recentDefinition.Name,
                                StringComparison.OrdinalIgnoreCase)) continue;
                        intents[i] = contextualIntent;
                        referencedCount = 1;
                        referencedName = recentDefinition.Name;
                        break;
                    }
                }
                else
                {
                    bool looksLikeUnresolvedControl = false;
                    for (int i = 0; i < s_SkillRouteDefinitions.Length; i++)
                    {
                        if (ClassifyContextualSkillUserIntent(
                                freshUserText, s_SkillRouteDefinitions[i]) == SkillUserIntent.None)
                            continue;
                        looksLikeUnresolvedControl = true;
                        break;
                    }
                    if (looksLikeUnresolvedControl && m_AgentRunning)
                    {
                        m_PendingSkillStatusFrame +=
                            "\n[Skill 控制指代不明确：当前找不到唯一的近期 Skill 目标。" +
                            "不要猜权限方向，也不要调用任何业务工具；" +
                            "自然询问用户具体指哪项能力。]";
                    }
                }
            }

            if (referencedCount == 1)
            {
                m_LastReferencedSkillName = referencedName;
                m_LastReferencedSkillAt = now;
            }
            else if (referencedCount > 1)
            {
                //本句同时明文谈到多项 Skill，下句的“它”不再是唯一指代。
                m_LastReferencedSkillName = "";
                m_LastReferencedSkillAt = -999f;
            }
        }

        for (int i = 0; i < s_SkillRouteDefinitions.Length; i++)
        {
            SkillRouteDefinition definition = s_SkillRouteDefinitions[i];
            SkillRouteState state = GetSkillRouteState(definition.Name);
            if (state.Access == SkillAccess.SoftSuppressed && now >= state.SoftSuppressedUntil)
            {
                state.Access = SkillAccess.Available;
                state.SoftSuppressedUntil = -999f;
            }

            SkillUserIntent intent = isUserTurn ? intents[i] : SkillUserIntent.None;
            bool recentFollowup = isUserTurn && state.Access == SkillAccess.Available &&
                !string.IsNullOrEmpty(freshUserText) &&
                now - state.LastSignalAt <= definition.FollowupSeconds &&
                ContainsAnyOrdinalIgnoreCase(freshUserText, definition.FollowupSignals);
            bool toolSignal = !string.IsNullOrEmpty(triggerReason) &&
                !string.IsNullOrEmpty(definition.TriggerReasonPrefix) &&
                triggerReason.StartsWith(definition.TriggerReasonPrefix, StringComparison.Ordinal);
            bool autonomousGrant = string.Equals(
                autonomouslyRequested, definition.Name, StringComparison.OrdinalIgnoreCase);
            bool correctionCarry = correctionSkills != null &&
                correctionSkills.Contains(definition.Name);
            bool userTurnPinCarry = ShouldCarrySkillForCurrentUserTurn(
                isUserTurn,
                m_UserTurnSkillPins.Contains(definition.Name));
            bool executionLeaseCarry = state.Access == SkillAccess.Available &&
                (HasSkillExecutionLease(definition.Name, now) ||
                 (definition.Name == "singing" && now - m_LastSingingRepairAt < 600f));

            bool strongSignal = false;
            string transitionReason = "（短期追问窗口）";
            bool observedSinging = isUserTurn && definition.Name == "singing" && m_RoutingSingingObservation;
            bool semanticInspection = intent == SkillUserIntent.Mention || observedSinging;
            if (semanticInspection)
            {
                strongSignal = state.Access == SkillAccess.Available;
                transitionReason = state.Access == SkillAccess.Available
                    ? (observedSinging && intent == SkillUserIntent.None
                        ? "（本轮结构化歌唱观测；不是回唱要求）"
                        : "（本轮用户话题相关；权限方向交给 LLM）")
                    : "（仅为判定权限语境临时加载）";
            }

            if (intent == SkillUserIntent.None && !observedSinging)
                strongSignal = recentFollowup ||
                    (state.Access == SkillAccess.Available && (toolSignal || autonomousGrant));
            if (strongSignal)
            {
                state.RoundsRemaining = definition.FollowupRounds;
                state.LastSignalAt = now;
                if (autonomousGrant) transitionReason = "（角色自主申请获准）";
                else if (toolSignal) transitionReason = "（本轮命中工具信号）";
                else if (recentFollowup) transitionReason = "（本轮命中追问信号）";
            }

            //即使当前权限已禁用，只要真实用户这一句谈到了该 Skill，也临时加载详细
            //规则供 LLM 判断“这是恢复、状态询问，还是业务范围限制”。执行闸仍先看
            //Access；只有 singing_policy 明确 enable 后，同一回复里的动作才会放行。
            bool interpretationOnly = semanticInspection && state.Access != SkillAccess.Available;
            bool active = correctionCarry || userTurnPinCarry || interpretationOnly ||
                (state.Access == SkillAccess.Available &&
                (strongSignal || state.RoundsRemaining > 0 || executionLeaseCarry));
            if (!strongSignal && state.RoundsRemaining > 0)
                state.RoundsRemaining--;
            if (executionLeaseCarry) ConsumeSkillExecutionLeaseRound(definition.Name);
            if (active)
            {
                activeSkills.Add(definition.Name);
                m_ActiveSkillsThisRound.Add(definition.Name);
                if (isUserTurn) m_UserTurnSkillPins.Add(definition.Name);
            }
            if (correctionCarry) transitionReason = "（工具纠错轮沿用上一轮详细规则）";
            else if (userTurnPinCarry) transitionReason = "（沿用当前真实用户轮 Skill 生命周期）";
            else if (executionLeaseCarry) transitionReason = "（未完成动作持有执行租约）";
            state.ActiveThisRound = active;

            if (m_LogAgentLoop && active != state.WasActive)
                Debug.Log($"[LLM技能] {definition.Name} {(active ? "已加载" : "已卸载")}" +
                          transitionReason);
            //Skill 可能在 10 分钟追问窗口里仍逻辑 active，但服务端 90 秒空闲 worker
            //已经释放；新的强相关用户信号也应发一次廉价幂等预热请求。
            if (active && (!state.WasActive || strongSignal || autonomousGrant))
                OnSkillBecameActive(definition.Name, state.Access);
            state.WasActive = active;
        }

        m_ChatSettings.m_ChatModel.SkillCatalogContext = BuildSkillCatalogContext();
        m_ChatSettings.m_ChatModel.SetActiveSkills(activeSkills.ToArray());
    }
    //—— 本场曲库台账（丙） ——
    //8/25 实测她说「今はまだこの一段しか保存できてないの」，而这一场她实际存了三首
    //(0956c4830577 / 7e1fb5e3a7a5 / f28d7306237f)、删了一首。其中 7e1fb5 还是她自己
    //四分钟前刚确认过的。原因不在她：那一场系统提示占了 16298 token / 预算 20000，
    //留给对话只有 3702 token，42 轮里裁剪了 22 次、丢掉 61 条消息——存歌的工具结果
    //早被裁掉了。而感知帧里**根本没有"本场存了什么"这一栏**，帧里唯一的曲库信息是
    //每段练唱后面那个靠旋律相似度算出来的「疑似 id」，指向的全是旧条目。
    //台账不走历史，所以裁剪碰不到它。要写得短——上下文本来就不够。
    private sealed class SessionSongNote
    {
        public string Kind;    //存 / 删 / 改名 / 唱
        public string Id;
        public string Label;
        public float At;
    }
    private readonly System.Collections.Generic.List<SessionSongNote> m_SessionSongLedger =
        new System.Collections.Generic.List<SessionSongNote>();
    private const int k_SessionLedgerMax = 8;

    //她见过的完整曲库 id。<song_sing/> 的 id 必须出自这里——和歌名出处校验同一个道理：
    //歌名早就要求出处了（8/25 实测生效，拦下了她编的《月を見ていた (新版)》），
    //id 却一直是裸奔的，任何库里存在的 id 都会被照唱。
    private readonly System.Collections.Generic.HashSet<string> m_SeenSongIds =
        new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex s_SongIdRegex =
        new System.Text.RegularExpressions.Regex(
            @"\b[0-9a-f]{12}\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    //—— 逐字复读监视 ——
    //和 m_RecentAIUtterances 分开：那个是给感知帧显示用的，条数由 Inspector 决定，
    //而这里要的是判据，不能被显示设置改动。
    //8/25 实测：她把同一句 practice_drop 说了 4 遍、同一句 song_search 说了 3 遍，
    //每一遍都逐字相同。感知帧当时已经把"39秒前 / 18秒前"两条一模一样的原话摆在
    //她眼前了，她照说不误——所以被动展示不解决问题，必须硬拦。
    //更要命的是那 4 次 practice_drop **次次都成功**：段号在每次删除后整体前移，
    //于是"删两段重复的"变成了实际删掉四段，练唱会话从 5 段被削到 1 段。
    //失败重试拦截在这种情形下完全无效——它拦的是失败，这里每一次都是成功。
    private readonly System.Collections.Generic.Queue<KeyValuePair<float, string>>
        m_RepeatWatch = new System.Collections.Generic.Queue<KeyValuePair<float, string>>();
    //队列深度。记账门槛降到 6 之后短句也会入队，深度太浅会把更早的长句挤出去
    //——而长句正是最值得拦的那种。16 条足够覆盖 k_SelfRepeatWindowSeconds 那 180 秒。
    private const int k_RepeatWatchDepth = 16;
    private const float k_SelfRepeatWindowSeconds = 180f;
    //**记账门槛。必须 ≤ 下面所有判定门槛里最小的那个**，否则会出现"够格被判、
    //却从来没被记住"的死缝。8/26 21:31 实测就栽在这里：记账 16 / 流式判定 12 /
    //收尾判定 6，于是 6~15 字的句子两条判定路径同时失效——她把
    //「読んだら、また話そうね。」(正好 12 字)连着说了六遍，一次都没拦住。
    //记多了不会误伤：**拦不拦由判定门槛决定，和记不记无关**。
    //代价只是队列里多几条短句，最坏让某一轮多扣住 18~30ms(实测生成 2.5ms/字)，
    //相对 TTS 合成 800ms、唱歌轮 ASR 4780ms 可以忽略。
    private const int k_SelfRepeatMinChars = 6;
    //拦下之后要在下一帧交代一次，否则她只会看到"工具没反应"，继续猜。
    private string m_SelfRepeatNote = "";
    private string m_LastAIMsgPlain = "";             // 上句 AI 文本(已剥标签)
    //最近若干条 AI 发言的环形 buffer：(发言时间戳, 发言文本)。
    //LLM 看到自己反复说类似话时应当切话题或 <silent/>——靠这个字段提醒它。
    private System.Collections.Generic.Queue<KeyValuePair<float, string>> m_RecentAIUtterances
        = new System.Collections.Generic.Queue<KeyValuePair<float, string>>();
    private string m_LastFocus = "";                  // LLM 上次设的 focus，回喂下一帧
    private int m_ConsecutiveAITurns = 0;
    private float m_AgentSessionStartTime = -1f;
    private float m_LastSpikePeakRms = 0f;
    private float m_LastSpikeTime = -1f;
    //本段沉默里是否已经因 spike 拉前过一次 tick，用户一开口就清零
    private bool m_SpikePulledForwardThisSilence = false;
    private bool m_SongSearchInFlight = false;
    private bool m_SongSearchResultPending = false;
    private string m_LastSongSearchResult = "";
    private bool m_SongCatalogInFlight = false;
    private bool m_SongCatalogResultPending = false;
    private string m_LastSongCatalogResult = "";
    private int m_SongCatalogGeneration = 0;
    private bool m_SpeakerManageInFlight = false;
    private bool m_SpeakerManageResultPending = false;
    private string m_LastSpeakerManageResult = "";
    private int m_SpeakerManageGeneration = 0;
    private float m_LastSongSearchRequestTime = -999f;
    private string m_LastSongSearchSignature = "";
    private int m_SongSearchGeneration = 0;
    private bool m_SongMemoryInFlight = false;
    private bool m_SongMemoryResultPending = false;
    private string m_LastSongMemoryResult = "";
    //只有用户明确要求保存时才需要马上给最终确认。角色自主记下一段旋律属于内部动作：
    //结果留给后续自然 tick 感知，不为它强行制造第二次正式回复。
    private bool m_SongMemoryAcknowledgementRequired = false;
    //上一次曲库演唱实际包含几段独立内容。
    private int m_LastCatalogUniqueSegmentCount = 0;
    //上一次查不到的 song_sing 目标。8/22 实测她拿同一个错 id 连打八次，
    //每次收到"not found"之后仍然对用户说「もうすぐよ」，其中两轮回复一字不差。
    private string m_LastFailedSongSingKey = "";
    private enum SongSingFailureKind
    {
        None,
        NotFound,
        InvalidSelector,
        IdentityMismatch,
        AudioMissing,
        DurationLimit,
        Transport,
        Server,
        Other,
    }
    private SongSingFailureKind m_LastFailedSongSingKind = SongSingFailureKind.None;
    private string m_LastFailedSongSingId = "";
    private string m_LastFailedSongSingTitle = "";
    private string m_LastFailedSongSingDetail = "";

    //刚**成功**唱过的目标。失败早就有重试拦截，成功却一点冷却都没有——
    //8/25 实测她把同一个 song_sing 连打四次、文本一字不差，用户全程没插话，
    //「你已连续说话」从 1 涨到 4 才被人工打断。
    //关键条件是"用户中间没开口"：他说"再唱一遍"时必须能唱，所以只拦自说自话的重复。
    private string m_LastSungTargetKey = "";
    private float m_LastSungAt = -999f;
    private int m_LastSungRepeatCount = 0;
    private bool m_UserSpokeSinceLastSing = true;
    private const float k_RepeatSingBlockSeconds = 120f;
    private float m_LastFailedSongSingTime = -999f;
    //这一次被丢掉的无出处歌名，附在工具结果后面告诉她为什么。
    private string m_DroppedSongTitleNote = "";
    private float m_LastSongMemoryRequestTime = -999f;
    private string m_LastSongMemorySignature = "";
    private string m_LastRememberedSongId = "";
    private float m_LastRememberedSongResultTime = -999f;
    private int m_SongMemoryGeneration = 0;
    private int m_SongMemoryOriginWorkEpoch = -1;
    private Coroutine m_SongMemoryAcknowledgementCoroutine;
    private bool m_SongMemoryAcknowledgementInFlight = false;
    private bool m_SongSingInFlight = false;
    private int m_SongSingGeneration = 0;
    private bool m_HumBackPending = false;
    private bool m_HumBackPreparingCarrier = false;
    private bool m_HumBackPlaying = false;
    private int m_HumBackGeneration = 0;
    //最近一次成功演唱后的客观状态。它不是强制等待计时器；自主意图探针和
    //下一条真实用户消息都能看到这些事实，是否开口仍由 LLM 决定。
    private bool m_PostHumBackFeedbackActive = false;
    private float m_PostHumBackCompletedRealtime = -999f;
    private string m_PostHumBackFeedbackSummary = "";
    private float[] m_PendingHumTimeline;
    private float m_PendingHumFrameSeconds = 0.10f;
    private string m_PendingHumLanguage = "";
    private string m_PendingHumReason = "";
    private string m_PendingHumMode = "echo";
    private string m_PendingHumLyricsOverride = "";
    private byte[] m_PendingHumSourceWav;
    //流式歌唱用：各块独立音频、块前自然停顿、以及算好的移调值。显式逐段 key 与
    //普通整曲流式共用这份计划；区别由 m_PendingHumUsesExplicitSegmentKey 记录。
    private List<byte[]> m_PendingHumSegmentWavs;
    private List<float> m_PendingHumSegmentGaps;
    private List<float> m_PendingHumSegmentMedians;
    private List<int> m_PendingHumSegmentSources;
    private int[] m_PendingHumSegmentShifts;
    private bool m_PendingHumUsesExplicitSegmentKey = false;
    private bool m_PendingHumUsesMusicalPitchIntent = false;
    private int m_PendingHumExtraTagsIgnored = 0;
    //练唱音高事实分两层：源录音由用户输入分析实测；角色输出先按实际移调指令预计，
    //播放完成后再走无 ASR/无记忆副作用的 F0 回测。稳定 ID 不随清单段号前移。
    private sealed class PracticePitchState
    {
        public int StableId;
        public int Revision;
        public float CenterMidi;
        //LLM 本轮请求的音乐目标。与 ExpectedCenterMidi 分开：闭环补偿时为了让实际
        //输出到 G3，底层指令基准可能需要先发到 G#3。
        public float RequestedCenterMidi;
        public float ExpectedCenterMidi;
        public int ExecutedShift;
        public float UpdatedRealtime;
        public bool FullyPlayed;
        public bool Measured;
        public float MeasurementWeight;
        public float WeightedDeviation;
        public float WeightedStability;
        public float WeightedVoicedRatio;
        public string MeasurementBackend;
    }
    private sealed class PracticePitchPlanEntry
    {
        public int StableId;
        public int SourceIndex;
        public float SourceCenterMidi;
        public float RequestedCenterMidi;
        public float TargetCenterMidi;
        public int ExecutedShift;
    }
    private sealed class PracticePitchPlan
    {
        public int Revision;
        public string MusicalIntent = "";
        //只更新本次真实播放的片段；新片段集合也不删除其它 clip 的历史音高。
        //初始化标记用于同一流式事务，不能作为清空状态表的依据。
        public bool MergeWithCurrentArrangement;
        public bool ArrangementInitialized;
        public readonly List<PracticePitchPlanEntry> Entries =
            new List<PracticePitchPlanEntry>();
    }
    private readonly Dictionary<int, PracticePitchState> m_PracticePitchStates =
        new Dictionary<int, PracticePitchState>();
    private PracticePitchPlan m_PendingPracticePitchPlan;
    private int m_PracticePitchPlanRevision = 0;
    //角色只保留“当前编排”里每个稳定片段最近一次真正唱到用户耳中的状态。
    //局部重唱只替换对应片段；每段自己的 revision 仅用于防迟到回测污染。
    private readonly HashSet<int> m_HumStreamPlayedPracticeStableIds =
        new HashSet<int>();
    private bool m_PendingHumIsPracticeComposition = false;
    //这次连唱实际唱出去的段号(1 起)。清"待确认"标时要照它来，
    //order 指名了几段，没被唱到的段落不算用户确认过。
    private List<int> m_PendingHumPlayedIndices = null;
    private bool m_PendingHumIsCatalogSong = false;
    private bool m_PendingHumIsCatalogContinuation = false;
    private string m_PendingCatalogSongName = "";
    private int m_PendingHumPerformanceSeed = 1234;
    private int m_PendingHumSemitoneOffset = 0;
    private float m_PendingHumRmsMixRate = 0.85f;
    private float m_PendingHumInterpretation = 0.5f;
    private float m_PendingHumProtect = 0.33f;
    private string m_PendingHumVariationDiagnostic = "";
    private string m_PendingHumRenderer = "pending";
    private int m_HumPerformanceCounter = 0;
    private AudioClip m_GeneratedHumCarrierClip;
    private AudioClip m_ActiveHumBackClip;
    private UnityWebRequest m_ActiveSVSRequest;
    private string m_ActiveSVSRequestId = "";
    private UnityWebRequest m_ActiveHumSVCRequest;
    private string m_ActiveHumSVCRequestId = "";
    private UnityWebRequest m_HumBackPrefixSVCRequest;
    private string m_HumBackPrefixSVCRequestId = "";
    private int m_HumBackPrefixGeneration = 0;
    private bool m_HumBackPrefixPreparing = false;
    private AudioClip m_PreparedHumBackPrefixClip;
    private float m_PreparedHumBackPrefixSourceSeconds = 0f;
    private int m_PreparedHumBackPrefixSemitoneShift = 0;
    private string m_PreparedHumBackPrefixDiagnostic = "";
    private bool m_PreparedHumBackPrefixWasCpu = false;
    private int m_StreamingHumPerformanceSeed = 1234;
    private int m_StreamingHumSemitoneOffset = 0;
    private float m_StreamingHumRmsMixRate = 0.85f;
    private float m_StreamingHumInterpretation = 0.5f;
    private float m_StreamingHumProtect = 0.33f;
    // 允许快速回唱保留的最大头部说话量。0.35s 是呼吸/起音的余量，超过这个数
    // 就是真的有一句话在前面。实测漏出去的两次分别是 2.53s 和 1.81s，
    // 而干净的那几轮裁剪点与岛起点差在 0.1s 以内。
    private const float k_FastHumBackMaxHeadCropSeconds = 0.35f;
    //尾部允许被丢掉多少秒还照用预转换。快速回唱播的是整条录音，岛结束之后的东西
    //会被原样唱回去，所以这一头也得设闸。
    //阈值是量出来的：两份日志里 8 次真实裁剪，正常轮的尾部丢弃是
    //0.60/0.60/0.60/0.70/0.70/0.80/1.20 秒(呼吸和收尾静音)，
    //而 8/25 那次事故是 10.78 秒——用户唱错了停下来说的那句话。
    //两簇之间空得很开，取 2.0s：比观测到的正常上限高一截，比事故低五倍。
    private const float k_FastHumBackMaxTailDropSeconds = 2.0f;
    private bool m_FastHumBackEouStaged = false;
    private bool m_FastHumBackActive = false;
    private bool m_FastHumBackFinalDecisionReceived = false;
    private bool m_FastHumBackFinalConfirmed = false;
    private bool m_FastHumBackPrefixPlaybackStarted = false;
    private bool m_FastHumBackPrefixPlaybackDone = false;
    private bool m_FastHumBackFullReady = false;
    private bool m_FastHumBackFullPlaybackStarted = false;
    private bool m_FastHumBackPlaybackComplete = false;
    private byte[] m_FastHumBackFullSourceWav;
    private float m_FastHumBackFullSourceSeconds = 0f;
    private AudioClip m_FastHumBackFullClip;
    private Coroutine m_FastHumBackStartCoroutine;
    private Coroutine m_FastHumBackPlaybackCoroutine;
    private System.Diagnostics.Process m_SVSServerProcess;
    private bool m_SVSStartupInProgress = false;
    private bool m_SVSStartupSucceeded = false;
    private string m_SVSStartupDetail = "尚未检查";
    private System.Diagnostics.Process m_HumSVCServerProcess;
    private bool m_HumSVCStartupInProgress = false;
    private bool m_HumSVCStartupSucceeded = false;
    private string m_HumSVCStartupDetail = "尚未检查";
    private bool m_HumSVCPrewarmInProgress = false;
    private float m_HumSVCPrewarmLastStartedAt = -999f;
    private Coroutine m_HumBackPlaybackCoroutine;
    private sealed class HumStreamReadyClip
    {
        public AudioClip Clip;
        public float GapBefore;
        public int SegmentNumber;
        public int PracticeStableId;
        public float ExpectedPitchCenterMidi;
    }
    private sealed class HumStreamScheduledClip
    {
        public AudioClip Clip;
        public AudioSource Source;
        public double EndDspTime;
        public int SegmentNumber;
        public int PracticeStableId;
        public float ExpectedPitchCenterMidi;
    }
    private readonly Queue<HumStreamReadyClip> m_HumStreamReadyClips =
        new Queue<HumStreamReadyClip>();
    private readonly List<HumStreamScheduledClip> m_HumStreamScheduledClips =
        new List<HumStreamScheduledClip>();
    private Coroutine m_HumStreamProducerCoroutine;
    private Coroutine m_HumStreamPlaybackCoroutine;
    private GameObject m_HumStreamSecondaryAudioObject;
    private AudioSource m_HumStreamSecondaryAudioSource;
    private bool m_HumStreamProducerDone = false;
    private string m_HumStreamFailure = "";
    private float m_HumStreamBufferedSeconds = 0f;
    private int m_HumStreamTotalSegments = 0;
    private int m_HumStreamConvertedSegments = 0;
    private int m_HumStreamPlayedSegments = 0;
    private float m_HumStreamPlayedSeconds = 0f;
    private float m_HumStreamUnderrunSeconds = 0f;
    // A valid multi-segment action can convert while its spoken prelude is still
    // playing. Playback remains gated until the ordinary TTS pipeline drains.
    private bool m_HumStreamWaitForTextOutputDrain = false;
    private bool m_HumBackNeedsHistoryEntry = false;
    private bool m_WaitingForRequestedSingAlong = false;
    private float m_SingAlongRequestArmedAt = -999f;
    //用户明确预告“下一轮我会唱”的短期听觉先验。它和持续跟唱状态分开：
    //前者只帮助识别下一段输入，绝不授权角色自动播放、保存或持续跟唱。
    private bool m_UpcomingUserSingingExpected = false;
    private float m_UpcomingUserSingingExpectedAt = -999f;
    private const float k_UpcomingUserSingingExpectationSeconds = 180f;
    private bool m_HumBackResultPending = false;

    //工具失败会粘住，直到有一次成功、或者展示够多次为止。
    //
    //起因：工具结果在感知帧里只出现一帧(显示后 Pending 立刻置 false)，之后沉进历史。
    //于是她重试时，上一次失败已经不在眼前了——8/22 同一个错 id 打了八次，
    //8/23 同一个空模板 song_remember 打了四次，每次都对用户说"马上就好"。
    //
    //这里粘住的不是"更多说明文字"，而是已经存在的那句话别一帧就消失，
    //外加一个她最缺的事实：这件事已经失败过几次了。
    //同一个回哼调用连续失败到第几次就不再派发。1943 行那条「本轮存在不确定性」立过
    //一个好模板：说清不确定什么、用户答了会怎样、哪些路不许走。这里是同一个思路的
    //另一半——**光把失败摆在她眼前不够**：8/24 实测她读到「已经连续失败 3 次，
    //重复同一个调用不会有不同结果」之后，仍然一字不差发了第 4 次。
    //软提示在这个项目里三次都没拦住，所以到点就把盲目那条路关掉，只留问用户和如实说。
    //练唱片段能跨 Loop 重启活多久。给得宽：会话本身封顶 16 段(环形)，
    //每段在感知帧里都自带"多久以前唱的"，她看得见也判得出——真正该丢的只有
    //久到已经不属于"我们刚才在干什么"的那些。
    private const float k_PracticeCarryOverSeconds = 1800f;
    //Loop 重启后要向她交代一次练唱会话发生了什么变化。只报一帧。
    private string m_PracticeSessionCarryNote = "";
    private const int k_HumBackBlockAfterFailures = 2;
    private string m_LastFailedHumBackKey = "";
    private int m_LastFailedHumBackCount = 0;
    //本轮正在派发的那个调用的身份，失败时用它计数。
    private string m_PendingHumBackKey = "";
    //Identity of the real user turn that accepted the current transaction.  A
    //same-key retry from its LLM continuation is an idempotent duplicate; a new
    //user turn remains free to cancel/restart through the ordinary turn boundary.
    private int m_PendingHumBackInputRevision = -1;
    private bool m_HumBackTransactionRetainedThisRound = false;

    private string m_StickyToolFailure = "";
    private int m_StickyToolFailureCount = 0;
    private int m_StickyToolFailureShown = 0;
    private const int k_StickyToolFailureMaxFrames = 4;
    //用户正在等待时，程序校验失败不能只写进“以后某一帧”再算了：把脱敏事实立即
    //交回 LLM 修正一次。程序不替角色写台词；第二次仍失败才显示非角色化系统提示。
    private string m_PendingToolCorrectionFrame = "";
    private bool m_ToolCorrectionContinuationPending = false;
    private bool m_ToolCorrectionExhaustedThisRound = false;
    private int m_ToolCorrectionAttemptsThisUserTurn = 0;
    private string m_PendingNonCharacterToolNotice = "";
    //歌唱标签被顺序化或按客观素材源纠正时，只记录事实，不强制角色追加台词。
    //下一帧由 LLM 自己判断要不要解释、继续或保持安静。
    private string m_PendingSingingRoutingFact = "";
    private const int k_MaxToolCorrectionAttemptsPerUserTurn = 1;
    private string m_LastHumBackResult = "";
    private bool m_AgentGracefulShutdownPending = false;
    // 本轮从 LLM 回复里解析出来的标签——OnStreamComplete 写、收尾时读
    private float? m_RoundNextInSec = null;
    private string m_RoundFocus = null;
    private bool m_RoundContinue = false;
    private int m_NoSingingWorkContinuations = 0;

    private void UpdateContinuationExecutionFacts()
    {
        if (IsSingingRuntimeActivityActive()) m_NoSingingWorkContinuations = 0;
        else if (m_RoundContinue && m_ActiveSkillsThisRound.Contains("singing"))
            ++m_NoSingingWorkContinuations;
    }

    private static string BuildContinuationExecutionFact(int count, bool workActive)
    {
        if (count <= 0 || workActive) return "";
        return $"\n[执行事实：连续 {count} 次续接期间，没有歌声生成或播放任务在运行。" +
            "口头计划与 <continue/> 不会启动演唱，不能作为正在准备歌声的依据。" +
            "你仍可选择调用工具、先询问以澄清不确定性，或暂时安静；由你决定。]";
    }
    private bool m_RoundSilent = false;
    //本轮是否检测到 <silent/> 前缀 → 内心独白模式：文本不进 TTS，仅入历史当作"心里说的"。
    //OnStreamDelta 检测前缀置位；OnStreamComplete 用它判断是否抑制后续 flush。
    private bool m_RoundIsInner = false;
    //本轮"是不是在逐字复读"的判定状态。和 m_RoundInnerCheckDone 同生命周期。
    //Done = 已经拿定主意(放行或判为复读)，之后不再比对。
    //Hold = 目前为止的正文还是某条最近发言的前缀，但长度不够下结论，先扣着不送 TTS。
    private bool m_RoundRepeatCheckDone = false;
    private bool m_RoundRepeatHold = false;
    //这两个状态比 m_RoundIsInner 更精确：普通内心独白仍可以 continue，
    //但被机械复读门转成的“内心”必须停掉 chain，并一直等用户开口。
    private bool m_RoundSilencedForRepeat = false;
    private bool m_RoundWaitForUser = false;
    //攒到这么多字仍然是前缀，就认定是复读。低于它不下结论——她的开场白常常是
    //「あ、」「ふふっ。」这种短感叹，拿它们当判据会把正常回复也掐掉。
    //★ 改这个数之前先看 k_SelfRepeatMinChars：记账门槛必须 ≤ 这里。
    private const int k_RepeatConfirmChars = 12;
    //整轮结束时仍在扣留的兜底门槛。这一档可以低一些：那时要求的是**整条完全相等**，
    //不是前缀，误判空间小得多。
    //★ 这是三个判定门槛里最小的一个，所以 k_SelfRepeatMinChars 必须 ≤ 它。
    private const int k_RepeatExactChars = 6;
    //本轮是否已经决定过 inner 与否(无论决定结果是 true 还是 false)。
    //专门解耦 inner 检测和 m_FirstChunkFlushed——后者跨 chain 不重置，
    //会导致 chain 中段的 <silent/> 前缀检测被跳过(LLM 自救通道被堵)。
    //每个 fresh round + chain round 开头都重置为 false；OnStreamDelta 决定后置 true。
    private bool m_RoundInnerCheckDone = false;

    //—— 视觉状态 ——
    //角色"睁眼"与否，跨 round 持久化(只在 StartAgentLoop 重置)。
    //LLM 用 <look/> / <unlook/> 切换。睁眼期间每一帧的 user 消息都附桌面截图。
    private bool m_AgentEyesOpen = false;
    //本轮 LLM 回复里是否要求开/关眼睛——OnStreamComplete 解析时写，应用后清。
    //null = 本轮没动；true = <look/>；false = <unlook/>。同时存在时 unlook 优先(更保守)。
    private bool? m_RoundLookRequest = null;

    /// <summary>
    /// RTSpeechHandler 在 EnableRealtimeMode 时调用——启动 agent loop，
    /// 第一帧延后 m_FirstTickDelaySec 秒投递(让可能的 m_Greeting 先播完)。
    /// </summary>
    public void StartAgentLoop()
    {
        if (!m_EnableAgentLoop) return;
        if (m_AgentRunning) return;
        ResetWorkReviewForLoopBoundary();
        m_AgentGracefulShutdownPending = false;
        m_AgentRunning = true;
        m_SingingRequestSubmittedSinceUserTurn = false;
        m_AgentSessionStartTime = Time.realtimeSinceStartup;
        // Re-enabling capture continues the same dialogue. Keep user/heard-speech
        // and completed-action facts together with the history that already survives.
        //台账和 id 账本**不清**——它们记的是"这台机器上这一场里发生过什么"，
        //和 Loop 的生死无关。练唱会话跨重启保留，这两样同理。

        m_LastFocus = "";
        m_ConsecutiveAITurns = 0;
        m_AutonomyProbeGeneration++;
        m_AutonomyProbeInFlight = false;
        m_AutonomyProbeNotBefore = -999f;
        m_UserSpeechActiveForAutonomy = false;
        m_AutonomyProbeMustRemainPrivate = false;
        m_DeferredAutonomyIntent = "";
        m_DeferredAutonomyNovelty = "";
        m_ScheduledWakeReason = "";
        m_LastSpikeTime = -1f;
        m_LastSpikePeakRms = 0f;
        m_SpikePulledForwardThisSilence = false;
        m_SongSearchGeneration++;
        m_SongSearchInFlight = false;
        m_SongSearchResultPending = false;
        m_LastSongSearchResult = "";
        m_LastSongSearchRequestTime = -999f;
        m_LastSongSearchSignature = "";
        m_SongCatalogGeneration++;
        m_SongCatalogInFlight = false;
        m_SongCatalogResultPending = false;
        m_LastSongCatalogResult = "";
        m_SongMemoryGeneration++;
        m_SongMemoryOriginWorkEpoch = -1;
        m_SongMemoryInFlight = false;
        m_SongMemoryResultPending = false;
        m_SongMemoryAcknowledgementRequired = false;
        m_LastSongMemoryResult = "";
        m_LastSongMemoryRequestTime = -999f;
        m_LastSongMemorySignature = "";
        m_WaitingForRequestedSingAlong = false;
        m_SingAlongRequestArmedAt = -999f;
        m_UpcomingUserSingingExpected = false;
        m_UpcomingUserSingingExpectedAt = -999f;
        m_EouExpectedUserSinging = false;
        m_HumBackResultPending = false;
        m_LastHumBackResult = "";
        m_ToolCorrectionAttemptsThisUserTurn = 0;
        m_ToolCorrectionContinuationPending = false;
        m_ToolCorrectionExhaustedThisRound = false;
        m_PendingToolCorrectionFrame = "";
        m_PendingNonCharacterToolNotice = "";
        m_PendingSingingRoutingFact = "";
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        //Loop 重启**不清空**练唱会话。停/起实时模式并不结束对话——聊天历史一个字
        //都不会掉，她记的 note 和 memory 也全都还在。8/25 实测：用户手动停了一次，
        //刚教的五段旋律当场蒸发，她的笔记却还写着「第5段是ウピラントア遥かな」，
        //照着笔记发 order="5" 两次都撞上"练唱会话里还没有任何片段"。
        //只丢真正陈旧的；丢了或留了都要在下一帧告诉她，不能让她拿着过期的段号去撞。
        if (senseVoice != null)
        {
            senseVoice.ResumeSingingPracticeSession(
                k_PracticeCarryOverSeconds, out int keptPractice, out int droppedPractice);
            m_PracticeSessionCarryNote = BuildPracticeCarryNote(keptPractice, droppedPractice);
        }
        else m_PracticeSessionCarryNote = "";
        m_AgentEyesOpen = false;        //每次启动默认闭眼，让 LLM 自己决定何时 <look/>
        if (m_Urge != null) m_Urge.Reset(Time.realtimeSinceStartup);
        ClearRoundParsed();
        if (m_LogAgentLoop) Debug.Log($"[Agent] Loop 启动 — 首帧 {m_FirstTickDelaySec:F1}s 后投递");
        ScheduleNextTick(m_FirstTickDelaySec, "session-start");
    }

    /// <summary>
    /// RTSpeechHandler 在 DisableRealtimeMode 时调用——停止 agent loop，撤销待 tick。
    /// </summary>
    public void BeginGracefulAgentShutdown()
    {
        m_AgentGracefulShutdownPending = true;
        CancelAutonomyIntentProbe();
        if (m_PendingTickCo != null)
        {
            StopCoroutine(m_PendingTickCo);
            m_PendingTickCo = null;
        }
        if (m_LogAgentLoop)
            Debug.Log("[Agent] 收到优雅关闭请求：停止新tick，等待当前ASR/回复/歌曲落盘与确认完成");
    }

    public void CancelGracefulAgentShutdown()
    {
        bool wasPending = m_AgentGracefulShutdownPending;
        m_AgentGracefulShutdownPending = false;
        if (wasPending && m_AgentRunning && !HasPendingConversationWork)
            ScheduleNextTick(m_FirstTickDelaySec, "shutdown-cancelled");
    }

    public bool HasPendingConversationWork
    {
        get
        {
            return m_FinalAsrRequestsInFlight > 0 || m_FormalResponseInFlight ||
                !m_StreamComplete || IsAISpeaking || IsVoiceOutputPlaying ||
                m_AgentRoundInFlight || m_SongMemoryInFlight ||
                m_SongCatalogInFlight ||
                m_SongMemoryAcknowledgementCoroutine != null ||
                m_SongMemoryAcknowledgementInFlight || IsSingingRuntimeActivityActive();
        }
    }

    public void StopAgentLoop()
    {
        ResetWorkReviewForLoopBoundary();
        m_InterruptedUserQuestion = null;
        ResetSpeculativeTurn();
        ResetStreamingSemanticSingingLatch();
        m_AgentGracefulShutdownPending = false;
        if (!m_AgentRunning) return;
        m_AgentRunning = false;
        m_SongSearchGeneration++;
        m_SongSearchInFlight = false;
        m_SongSearchResultPending = false;
        m_SongCatalogGeneration++;
        m_SongCatalogInFlight = false;
        m_SongCatalogResultPending = false;
        m_SongMemoryGeneration++;
        m_SongMemoryOriginWorkEpoch = -1;
        m_SongMemoryInFlight = false;
        m_SongMemoryResultPending = false;
        m_SongMemoryAcknowledgementRequired = false;
        m_ToolCorrectionAttemptsThisUserTurn = 0;
        m_ToolCorrectionContinuationPending = false;
        m_ToolCorrectionExhaustedThisRound = false;
        m_PendingToolCorrectionFrame = "";
        m_PendingNonCharacterToolNotice = "";
        m_PendingSingingRoutingFact = "";
        CancelAutonomyIntentProbe();
        m_UserSpeechActiveForAutonomy = false;
        m_DeferredAutonomyIntent = "";
        m_DeferredAutonomyNovelty = "";
        m_ScheduledWakeReason = "";
        CancelPendingHumBack("agent-stop", false);
        if (m_PendingTickCo != null)
        {
            StopCoroutine(m_PendingTickCo);
            m_PendingTickCo = null;
        }
        m_AgentRoundInFlight = false;
        ClearRoundParsed();
        if (m_LogAgentLoop) Debug.Log("[Agent] Loop 停止");
    }

    /// <summary>
    /// RTSpeechHandler 检测到非语音环境扰动(咳嗽、翻身、键盘声)时调用。
    /// 若开启 m_BringForwardOnSpike 且当前空闲，把下次 tick 拉到现在——
    /// 模拟"被外界声音拽回注意力"。LLM 在下一帧 prompt 里能看到"环境刚有动静"。
    /// </summary>
    public void OnEnvironmentSpike(float peakRms)
    {
        if (!m_AgentRunning) return;
        m_LastSpikePeakRms = peakRms;
        m_LastSpikeTime = Time.realtimeSinceStartup;
        if (!m_BringForwardOnSpike) return;
        if (m_AgentRoundInFlight) return;        //已经在等 LLM 了，spike 自然会出现在下帧的环境字段里
        if (IsAISpeaking) return;                //角色正在说话，spike 不算打扰

        //冲动模型：按响度注入，可累加，但整段沉默有总额度(见 m_SpikeMaxPerSilence)。
        //额度就是原来那道一次性闸的连续版——环境能把她拽早，但不能单独驱动她。
        if (m_Urge != null && m_Urge.Enabled)
        {
            float got = m_Urge.AddSpike(peakRms);
            if (got > 0f) m_AutonomyProbeNotBefore = -999f;
            if (m_LogAgentLoop)
                Debug.Log(got > 0f
                    ? $"[Agent] 环境 spike(rms={peakRms:F4}) → 冲动 +{got:F2} (U={m_Urge.Value:F2})"
                    : $"[Agent] 环境 spike(rms={peakRms:F4}) 本段沉默的环境额度已用尽，忽略");
            return;
        }

        if (m_PendingTickCo == null) return;     //没有待办 tick，不存在"拉前"

        //一段沉默里只允许被拽回一次注意力。否则 LLM 排的节奏会被反复架空：
        //实测它要求 20s 后再醒，却被 1s 拉前，每约 10s 醒一次面对完全相同的情境
        //(用户仍沉默)，于是连说数遍几乎一样的话。用户开口时清零。
        if (m_SpikePulledForwardThisSilence)
        {
            if (m_LogAgentLoop)
                Debug.Log($"[Agent] 环境 spike(rms={peakRms:F4}) 本段沉默已拉前过，忽略");
            return;
        }
        m_SpikePulledForwardThisSilence = true;

        if (m_LogAgentLoop) Debug.Log($"[Agent] 环境 spike(rms={peakRms:F4}) → 拉前下次 tick");
        StopCoroutine(m_PendingTickCo);
        m_PendingTickCo = null;
        ScheduleNextTick(m_MinTickSec, "spike-pull-forward");
    }

    /// <summary>
    /// RTSpeechHandler 在 StartRecording 时调用——用户开口意味着 AI 的外放轮次让路，
    /// 但内部自主 tick 可以继续形成一份不会直接说出口的私人预判。
    /// </summary>
    public void NotifyUserStartedSpeaking()
    {
        CancelDialogueMotion("user-started-speaking");
        PreservePendingReplyForInputCandidate();
        m_InputAudioRevision++;
        m_SingingRequestSubmittedSinceUserTurn = false;
        m_SemanticCheckSerial++;
        (m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText)?.CancelInputAnalyses();
        m_NoSingingWorkContinuations = 0;
        m_ListeningLatencyArmed = false;
        m_ListeningFormalLatencyArmed = false;
        m_StalledTurnAction = "";
        m_LastBoundaryDecisionReason = "";
        m_UserSpeechActiveForAutonomy = true;
        //探针若已经发出，即使用户恰好在它返回前开口，也只能保存为私人想法；
        //不能因为竞态而越过用户发言直接外放。
        if (m_AutonomyProbeInFlight)
            m_AutonomyProbeMustRemainPrivate = true;
        //StartRecording 只会在神经VAD确认真人后调用。若此刻仍有角色语音，按真正的
        //barge-in 保留已听到部分；若尚未出声，则直接废弃旧请求和待播队列。
        if (IsAISpeaking || IsVoiceOutputPlaying) Interrupt();
        else CancelUnheardResponseForUserSpeech();
        //上面两条分支主要服务普通 TTS，未来状态继续增加时可能有某种歌唱工作
        //没有被其 hasResponseWork 覆盖。用户一开口是硬边界：再用歌唱任务自己的
        //完整 hadWork 判据扫一次，确保上一轮的曲库演唱定位、生成、流式队列和播放都不能越界。
        CancelPendingHumBack("user-started-speaking-safety", true);
        ResetStreamingHumBackPrefix("new-user-turn", true);
        ResetSpeculativeTurn();
        ResetStreamingSemanticSingingLatch();
        m_StalledTurnDecisionNote = "";
        m_EouTurnWasSinging = false;
        m_EouExpectedUserSinging = false;
        m_EouSingingRejectedByFinal = false;
        m_EouCognitiveSpeechVeto = false;
        m_EouCognitiveSingingSupport = false;
        m_FinalModeVerdict = "";
        m_FinalModeSoftDowngrade = false;
        m_PendingSingingConfirmation = false;
        m_EouFillerContext = "neutral";
        m_ConsecutiveAITurns = 0;
        m_SpikePulledForwardThisSilence = false;   //新一段沉默重新允许被拽回一次
        m_AutonomyProbeNotBefore = -999f;
        if (m_Urge != null) m_Urge.AbsorbUserUtterance(Time.realtimeSinceStartup);
        if (m_LogAgentLoop)
            Debug.Log("[Agent] 用户开口 → 外放轮次撤销；私人 tick/预判保留，连续 AI 轮次清零");
    }

    private void CancelSupersededSingingForAcceptedUserTurn(
        SenseVoiceSpeechToText senseVoice)
    {
        if (m_FastHumBackEouStaged || m_FastHumBackActive) return;
        if (senseVoice != null &&
            (senseVoice.LastSpeakerKind == "ai" ||
             senseVoice.LastSpeakerId == "ai_self"))
            return;

        bool hasOlderSingingWork = IsSingingRuntimeActivityActive();
        if (!hasOlderSingingWork) return;

        if (m_LogHumBack)
            Debug.Log("[HumBack] 最终 ASR 已确认新的真实用户轮次；" +
                      "取消未跨过 StartRecording 边界的旧歌唱任务");
        CancelPendingHumBack("accepted-real-user-turn", true);
    }

    /// <summary>
    /// 调度下一次 tick。requestedSec 来源：LLM 的 &lt;next in="Ns"/&gt; 或兜底 m_DefaultTickSec。
    /// 自动 clamp 到 [m_MinTickSec, m_MaxTickSec]。
    ///
    /// 冲动模型启用时这里不再起倒计时协程，而是把 requestedSec 翻译成一个累积速率:
    /// 无事发生时仍恰好 requestedSec 秒后触顶，但孤独/记忆/环境可以把它拽早。
    /// 8 个调用点的语义因此完全不变，只是"到点"变成了"攒够"。
    /// </summary>
    private void ScheduleNextTick(float requestedSec, string reason)
    {
        if (!m_AgentRunning || m_AgentGracefulShutdownPending) return;
        m_ScheduledWakeReason = reason ?? "";
        if (ShouldBypassAutonomyIntentProbe(reason))
            m_AutonomyProbeNotBefore = -999f;
        if (m_PendingTickCo != null)
        {
            StopCoroutine(m_PendingTickCo);
            m_PendingTickCo = null;
        }

        if (reason == "self-inspection-result" || reason == "singing-goal-review")
        {
            // Deliver completed work independently of idle novelty/fatigue.
            m_AutonomyProbeNotBefore = -999f;
            if (m_Urge != null) m_Urge.ClearPendingTrigger();
            m_PendingTickCo = StartCoroutine(TickAfterCo(Mathf.Max(.1f, requestedSec)));
            return;
        }
        if (m_Urge != null && m_Urge.Enabled)
        {
            m_Urge.SetNextIn(requestedSec, m_ConsecutiveAITurns, m_MinTickSec, m_MaxTickSec);
            if (m_LogAgentLoop)
                Debug.Log($"[Agent] 下次 tick 目标 {m_Urge.EffectiveSec:F1}s(reason={reason}, " +
                          $"requested={requestedSec:F1}s, 疲劳 {m_ConsecutiveAITurns} 轮) — 冲动可提前");
            return;
        }

        float clamped = Mathf.Clamp(requestedSec, m_MinTickSec, m_MaxTickSec);
        if (m_LogAgentLoop)
            Debug.Log($"[Agent] 下次 tick {clamped:F1}s 后(reason={reason}, requested={requestedSec:F1}s)");
        m_PendingTickCo = StartCoroutine(TickAfterCo(clamped));
    }

    private IEnumerator TickAfterCo(float sec)
    {
        yield return new WaitForSeconds(sec);
        m_PendingTickCo = null;
        string triggerReason = ResolveScheduledWakeReason(m_ScheduledWakeReason, "clock");
        m_ScheduledWakeReason = "";
        FireTick(triggerReason);
    }

    /// <summary>
    /// 冲动模型的推进——每帧一步，纯 C# 算术，不碰任何推理服务。
    /// 她自己正在说话/正式轮次在飞时不推进；用户正在说话时可以推进，
    /// 但点火只能形成不会外放的私人预判。
    /// </summary>
    private void StepUrge()
    {
        if (m_Urge == null || !m_Urge.Enabled) return;
        if (!m_AgentRunning || m_AgentGracefulShutdownPending) return;
        bool userCurrentlySpeaking = m_UserSpeechActiveForAutonomy ||
            Time.realtimeSinceStartup - m_LastStreamingPartialRealtime <
                k_UserSpeakingHoldSeconds;
        if (m_AgentRoundInFlight || m_AutonomyProbeInFlight ||
            IsAISpeaking || IsVoiceOutputPlaying) return;
        if (Time.realtimeSinceStartup < m_AutonomyProbeNotBefore) return;
        //用户已经说完、但这一轮还没回应他之前，不许自主开口。
        //
        //m_AgentRoundInFlight 挡不住这段空窗：走回哼快速路径的轮次**不生成正式回复**，
        //根本不会置这个标志，于是那几十秒对 tick 完全敞开。8/16 实测四次自主发言
        //踩在用户说完后的 8~23 秒内，内容全是顺着她自己上一句往下说的——用户的
        //感受是"她在回复我上一轮的话"。
        //
        //上一处补的 m_LastStreamingPartialRealtime 挡的是"用户还在说"，这一条挡的是
        //"用户说完了但还没轮到她说"，两者不重叠。
        if (m_UserTurnAwaitingReplySince > 0f && !userCurrentlySpeaking)
        {
            //兜底分两档。回哼含 SVC 转换实测 30~50 秒，那期间必须能等；但普通轮
            //没有这个开销，万一还有没覆盖到的收尾路径，也不该让她哑上一分多钟。
            bool humBackBusy = IsSingingRuntimeActivityActive();
            float cap = humBackBusy
                ? k_UserTurnAwaitingReplyMaxSeconds
                : k_UserTurnAwaitingReplyPlainCapSeconds;
            if (Time.realtimeSinceStartup - m_UserTurnAwaitingReplySince < cap) return;
            ClearUserTurnAwaitingReply($"等待超时({cap:F0}s)");
        }
        // 用户还在说话时可以继续产生想法，但 FireTick 只允许它进入私人预判，
        // 不会在录音期间发出声音、动作或正式角色回复。
        //
        // 这道闸原来只挡"她自己在说"，不挡"用户正在说"。而感知帧里的"距用户上句"
        // 是从**上一轮提交**算起的——一轮【说话+唱歌】要二三十秒才提交，于是 8/11
        // 实测出现：用户唱到一半，感知帧写着「距用户上句: 51秒」，时钟冲动到点，
        // 她拿上一轮的上下文开口说了「あら、またそのフレーズ？」，随后才被
        // 「用户录音期间检测到旧AI开始发声」事后打断——声音已经放出去了，
        // 紧接着回哼开始，听感非常突兀。
        //
        //待机漂移点火 = 她忽然想起了什么，按事件注入
        //用户正在表达时不另抽一条随机记忆来压过当前输入；此时只推进角色原有冲动。
        if (!userCurrentlySpeaking && m_MemoryHub != null && m_EnableMemoryRecall)
        {
            float drift = m_MemoryHub.ConsumeDriftEnergy();
            if (drift > 0f) m_Urge.AddMemorySurfacing(drift);
        }

        if (!m_Urge.Step(Time.deltaTime, Time.realtimeSinceStartup)) return;

        //把主因如实带进感知帧。一律说成"你自己的钟到点了"会让她误判自己的节奏：
        //上一版就是这样，环境拽前的帧也被标成时钟触发，她随后把 <next in/> 越排越短。
        string triggerReason = ResolveScheduledWakeReason(
            m_ScheduledWakeReason, m_Urge.LastCause);
        m_ScheduledWakeReason = "";
        FireTick(triggerReason);
    }

    private void CancelAutonomyIntentProbe()
    {
        m_AutonomyProbeGeneration++;
        m_AutonomyProbeMustRemainPrivate = false;
        if (!m_AutonomyProbeInFlight) return;
        m_AutonomyProbeInFlight = false;
        if (m_ChatSettings != null && m_ChatSettings.m_ChatModel != null)
            m_ChatSettings.m_ChatModel.CancelEphemeralMsg();
    }

    private void StoreDeferredAutonomyThought(AutonomyIntentDecision decision)
    {
        if (decision == null || !decision.proceed) return;
        m_DeferredAutonomyIntent = TruncateForFrame(
            (decision.intent ?? "").Trim(), 180);
        m_DeferredAutonomyNovelty = TruncateForFrame(
            (decision.novelty ?? "").Trim(), 180);
    }

    private string ConsumeDeferredAutonomyThought()
    {
        string intent = m_DeferredAutonomyIntent;
        string novelty = m_DeferredAutonomyNovelty;
        m_DeferredAutonomyIntent = "";
        m_DeferredAutonomyNovelty = "";
        if (string.IsNullOrWhiteSpace(intent) &&
            string.IsNullOrWhiteSpace(novelty))
            return "";

        return "\n[私人思考参考：这是你在用户尚未说完时形成、当时没有外放的一份临时想法。" +
            (string.IsNullOrWhiteSpace(intent) ? "" : "当时的意图=" + intent + "。") +
            (string.IsNullOrWhiteSpace(novelty) ? "" : "当时认为的新意=" + novelty + "。") +
            "现在你已经收到用户的完整发言；请以完整语义为准，自主决定是否采用、修正或放弃这份想法，" +
            "它不是必须回应的指令。]";
    }

    /// <summary>
    /// 工具确认要接管当前节奏时，旧的倒计时/冲动不能在确认结束后一秒立刻补打一轮。
    /// 只清待触发状态，不把内部事件伪装成“用户刚开口”。
    /// </summary>
    private void ClearScheduledAgentWake()
    {
        if (m_PendingTickCo != null)
        {
            StopCoroutine(m_PendingTickCo);
            m_PendingTickCo = null;
        }
        m_ScheduledWakeReason = "";
        if (m_Urge != null) m_Urge.ClearPendingTrigger();
    }

    private static bool ShouldBypassAutonomyIntentProbe(string triggerReason)
    {
        string reason = triggerReason ?? "";
        return reason == "session-start" || reason == "shutdown-cancelled" || reason == "self-inspection-result" ||
            reason == "song-search-result" || reason == "song-catalog-result" ||
            reason == "song-memory-result" || reason == "speaker-manage-result" ||
            reason.StartsWith("skill-request:", StringComparison.Ordinal);
    }

    private static string ResolveScheduledWakeReason(
        string scheduledReason, string urgeCause)
    {
        if (ShouldBypassAutonomyIntentProbe(scheduledReason))
            return scheduledReason;
        switch (urgeCause)
        {
            case "spike": return "spike-pull-forward";
            case "memory": return "memory-surfaced";
            default: return "scheduled";
        }
    }

    private static bool TryParseAutonomyIntentDecision(
        string response, out AutonomyIntentDecision decision)
    {
        decision = null;
        if (string.IsNullOrWhiteSpace(response)) return false;
        int begin = response.IndexOf('{');
        int end = response.LastIndexOf('}');
        if (begin < 0 || end <= begin) return false;
        string json = response.Substring(begin, end - begin + 1);
        if (json.IndexOf("\"proceed\"", StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        try
        {
            decision = JsonUtility.FromJson<AutonomyIntentDecision>(json);
            if (decision != null)
                decision.singing_goal_expected = Newtonsoft.Json.Linq.JObject.Parse(json)["singing_goal_expected"] as Newtonsoft.Json.Linq.JObject;
            return decision != null;
        }
        catch (Exception)
        {
            decision = null;
            return false;
        }
    }

    private string BuildAutonomyIntentProbePrompt(string triggerReason, string frame)
    {
        if (ShouldReviewUserWork()) return BuildDedicatedWorkReviewPrompt(frame);
        string situation = m_UserSpeechActiveForAutonomy
            ? "用户此刻仍在表达。你可以在心里形成自己的观察或想法，但本次判断绝不会直接外放；" +
              "稍后必须结合用户完整发言，才能决定采用、修正或放弃。"
            : "现在没有新的用户发言。系统只是给你一次自主行动机会。";
        return
            "[内部自主意图预判；不要把本条写成正式回复，不要调用工具，不要输出控制标签]\n" +
            situation + "触发原因=" +
            (triggerReason ?? "scheduled") + "。\n" +
            "先审查上一用户事项：尚欠实际检查、执行或结果说明时，应继续该事项，不受新意要求限制。" +
            "仅在上一事项已完成或正等待用户后，判断此刻是否出现了一个和你最近发言不同、值得开启正式轮次的新意图。" +
            "新意图可以来自你自己的新想法、刚浮现的记忆、环境变化、时间流逝后自然想换的话题，" +
            "也可以是只在心里完成的观察或记忆整理；不要求一定说出口。\n" +
            "下面这些不算新意图：重说或改写上一句话、再次回答已经回答过的用户发言、" +
            "重复表示正在等待、重复追问用户尚未回答的问题、为用户没有听见的内部复读道歉。\n" +
            "proceed=true 只表示值得让正式角色再判断一次说话/沉默/动作，不表示必须开口。" +
            "如果既无未完成事项也无新东西，proceed=false，并给出下一次重新考虑的秒数。\n\n" +
            frame + BuildWorkReviewContext() + "\n\n" +
            "先在work_evidence用一句话对照用户问题与已完成回复，指出已经给了什么答案或缺哪一步，再选择状态。" +
            "仅有礼貌回应、点头、承诺稍后检查，没有实际检查或问题答案时必须是continue，不能因回复已播完而closed。\n" +
            "只输出一个紧凑 JSON，不要 Markdown：" +
            "{\"work_evidence\":\"问题与已完成步骤的简短对照\",\"work_status\":\"continue|waiting_tool|waiting_user|closed|none\",\"proceed\":false,\"intent\":\"下一步具体做什么；没有则留空\"," +
            "\"novelty\":\"它相对最近发言的新信息；没有则留空\"," +
            "\"wait_seconds\":45}";
    }

    private void DispatchPreparedAgentTick(
        string triggerReason,
        string frame,
        AutonomyIntentDecision decision = null,
        bool workContinuation = false)
    {
        if (!m_AgentRunning || m_AgentGracefulShutdownPending) return;
        if (m_UserSpeechActiveForAutonomy)
        {
            StoreDeferredAutonomyThought(decision);
            if (m_LogAgentLoop)
                Debug.Log("[Agent/自主意图] 用户仍在表达；只保存私人预判，不创建外放轮次");
            return;
        }
        if (IsAISpeaking || m_AgentRoundInFlight || m_FormalResponseInFlight || IsVoiceOutputPlaying) return;

        string workFeedback = "";
        if (!string.IsNullOrEmpty(m_PendingSelfInspection))
        {
            workFeedback = "[只读自检已经执行；新的程序结果]\n" + m_PendingSelfInspection +
                "\n这是执行后观测，不是新的用户发言。" +
                (m_WorkReviewOpen ? "请回答原问题或明确说明限制，不要再次只说稍等。" :
                    "这是自主检查；自行判断是否有值得表达的新观察，可保持沉默，不能重新打开已结束的用户事项。");
            // Retain until the current formal response acknowledges the result.
        }
        else if (workContinuation)
        {
            if (m_WorkReviewOpen) ++m_WorkContinuations;
            else ++m_AutonomousGoalContinuations;
            workFeedback = BuildWorkContinuationContext();
        }
        else if (decision != null)
        {
            string intent = TruncateForFrame((decision.intent ?? "").Trim(), 160);
            string novelty = TruncateForFrame((decision.novelty ?? "").Trim(), 160);
            frame +=
                "\n[自主意图预判已通过：这是没有新用户消息时由你自己浮现的意图。" +
                (intent.Length > 0 ? "意图=" + intent + "。" : "") +
                (novelty.Length > 0 ? "新意=" + novelty + "。" : "") +
                "现在仍可选择说话、保持沉默或只做非语言动作；不要重新回答最后一条用户发言，" +
                "也不要复述你最近说过的话。]";
        }

        frame += ConsumeDeferredAutonomyThought();

        if (!workContinuation)
        {
            // A user-work continuation still owns the original end-to-audio span.
            m_ListeningLatencyArmed = false;
            m_ListeningFormalLatencyArmed = false;
        }
        HarvestSongIds(frame);
        const string tickPrompt = "[程序感知 tick；不是新的用户发言。请依据本次临时观测判断下一步。]";
        m_ChatHistory.Add(tickPrompt);
        m_AgentRoundInFlight = true;
        m_AgentCurrentRoundIsTick = true;
        ClearRoundParsed();

        if (m_LogAgentLoop) Debug.Log("[Agent] FireTick → " + frame.Replace('\n', ' '));

        m_TextBack.text = "";
        StageFormalObservationFrame(frame, m_FormalResponseGeneration + 1);
        StartStreaming(tickPrompt, false, continueExistingUserTurn: workFeedback.Length > 0,
            transientSystemContext: workFeedback, workFeedback: workFeedback.Length > 0);
    }

    private void BeginAutonomyIntentProbe(string triggerReason)
    {
        if (m_ChatSettings == null || m_ChatSettings.m_ChatModel == null)
        {
            PrepareActiveSkillsForRound(null, triggerReason);
            DispatchPreparedAgentTick(triggerReason, BuildPerceptionFrame(triggerReason));
            return;
        }

        //预判不是正式 round，不能消耗 Skill 的追问轮数。这里只给它看常驻目录；
        //真正放行后再调用 PrepareActiveSkillsForRound 一次。
        m_ChatSettings.m_ChatModel.SkillCatalogContext = BuildSkillCatalogContext();
        m_ChatSettings.m_ChatModel.SetActiveSkills();
        string frame = BuildPerceptionFrame(triggerReason);
        HarvestSongIds(frame);
        int generation = ++m_AutonomyProbeGeneration;
        int workEpoch = m_WorkEpoch;
        bool reviewingWork = ShouldReviewUserWork();
        bool reviewingAutonomousGoal = !m_WorkReviewOpen && HasAutonomousSingingGoal() && HasPendingSingingGoalReview();
        m_AutonomyProbeInFlight = true;
        m_AutonomyProbeMustRemainPrivate = m_UserSpeechActiveForAutonomy;
        string prompt = BuildAutonomyIntentProbePrompt(triggerReason, frame);
        if (m_LogAgentLoop)
            Debug.Log($"[Agent/自主意图] 开始预判 trigger={triggerReason}");

        Action<string> onDecision = response =>
        {
            if (generation != m_AutonomyProbeGeneration) return;
            m_AutonomyProbeInFlight = false;
            bool privateOnly = m_AutonomyProbeMustRemainPrivate ||
                m_UserSpeechActiveForAutonomy;
            m_AutonomyProbeMustRemainPrivate = false;
            if (!m_AgentRunning || m_AgentGracefulShutdownPending) return;

            if (!TryParseAutonomyIntentDecision(response, out AutonomyIntentDecision decision) ||
                (reviewingWork && !IsWorkReviewDecision(decision)))
            {
                if (privateOnly)
                {
                    if (m_LogAgentLoop)
                        Debug.LogWarning("[Agent/自主意图] 私人预判无法解析；安静丢弃，不触发正式轮次");
                    return;
                }
                if (m_AgentRoundInFlight || IsAISpeaking || IsVoiceOutputPlaying)
                    return;
                if (reviewingWork)
                {
                    if (m_LogAgentLoop)
                        Debug.LogWarning("[Agent/事项进度] 结构化判定无效；使用有界恢复，未确认任务完成或目标已获准。");
                    var recovery = new AutonomyIntentDecision { work_status = "continue",
                        intent = "进度判定格式无效；核对原请求与真实结果，推进必要步骤或明确说明限制" };
                    if (workEpoch != m_WorkEpoch || !(reviewingAutonomousGoal
                        ? CanContinueAutonomousSingingGoal(recovery) : ApplyWorkReviewDecision(recovery))) return;
                    PrepareActiveSkillsForRound(null, triggerReason);
                    DispatchPreparedAgentTick(triggerReason, BuildPerceptionFrame(triggerReason), recovery, true);
                    return;
                }
                if (m_LogAgentLoop)
                    Debug.LogWarning("[Agent/自主意图] 预判输出无法解析，保留角色自主性并放行正式轮次");
                PrepareActiveSkillsForRound(null, triggerReason);
                DispatchPreparedAgentTick(triggerReason, frame);
                return;
            }

            if (privateOnly)
            {
                StoreDeferredAutonomyThought(decision);
                if (m_LogAgentLoop)
                    Debug.Log(decision.proceed
                        ? $"[Agent/自主意图] 私人预判已保存：{TruncateForFrame(decision.intent, 120)}"
                        : "[Agent/自主意图] 私人预判没有形成新想法；保持安静");
                return;
            }

            if (m_AgentRoundInFlight || IsAISpeaking || IsVoiceOutputPlaying)
                return;

            if (workEpoch != m_WorkEpoch) return;
            if (reviewingWork)
            {
                NormalizeSingingGoalReview(decision);
                ApplySingingGoalReview(decision.singing_goal_status, decision.singing_goal_evidence);
            }
            bool workContinuation = reviewingAutonomousGoal
                ? CanContinueAutonomousSingingGoal(decision) : ApplyWorkReviewDecision(decision);
            if (reviewingAutonomousGoal) decision.proceed = workContinuation;
            if (reviewingWork && !workContinuation) decision.proceed = false;
            if (decision.proceed)
            {
                int cap = (m_Urge != null && m_Urge.Enabled) ? m_MonologueBackstopTurns : m_MaxConsecutiveAITurns;
                if (!workContinuation && m_ConsecutiveAITurns >= cap) return;
                m_AutonomyProbeNotBefore = -999f;
                if (m_LogAgentLoop)
                    Debug.Log($"[Agent/{(workContinuation ? "未完成事项" : "自主意图")}] 放行：{TruncateForFrame(decision.intent, 120)} " +
                              $"(新意={TruncateForFrame(decision.novelty, 120)})");
                PrepareActiveSkillsForRound(null, triggerReason);
                DispatchPreparedAgentTick(triggerReason, BuildPerceptionFrame(triggerReason), decision, workContinuation);
                return;
            }

            float requested = decision.wait_seconds > 0f
                ? decision.wait_seconds
                : m_AutonomyProbeDefaultRecheckSeconds;
            float min = Mathf.Max(1f, m_AutonomyProbeMinRecheckSeconds);
            float max = Mathf.Max(min, m_AutonomyProbeMaxRecheckSeconds);
            float wait = Mathf.Clamp(requested, min, max);
            m_AutonomyProbeNotBefore = Time.realtimeSinceStartup + wait;
            if (m_LogAgentLoop)
                Debug.Log(reviewingWork
                    ? $"[Agent/事项进度] 状态={m_WorkStatus}，本次不续接；{wait:F0}s 后再考虑自主表达"
                    : $"[Agent/自主意图] 没有新表达动机，保持安静；{wait:F0}s 后再考虑");
            ScheduleNextTick(wait, "autonomy-no-new-intent");
        };
        if (reviewingWork) m_ChatSettings.m_ChatModel.PostWorkReviewMsg(prompt, onDecision);
        else m_ChatSettings.m_ChatModel.PostEphemeralMsg(prompt, onDecision);
    }

    /// <summary>
    /// 真正发起一次 tick：构造感知帧 → 投给 LLM 流式管线 →
    /// 沿现有 Stream 通路走 TTS，OnStreamComplete 解析尾部标签。
    /// triggerReason 透到感知帧里告诉 LLM 这一帧是怎么来的。
    /// </summary>
    private void FireTick(string triggerReason)
    {
        if (!m_AgentRunning || m_AgentGracefulShutdownPending) return;
        if (m_PendingMotionRepair != null || m_ActiveMotionRepair != null)
        {
            ScheduleNextTick(m_MinTickSec, "motion-feedback-continuation");
            return;
        }
        if (m_AutonomyProbeInFlight) return;
        if ((!string.IsNullOrEmpty(m_PendingSelfInspection) && m_SelfInspectionDeliveryAttempts >= MaxWorkContinuations) ||
            (HasPendingSingingGoalReview() && !m_WorkReviewOpen &&
                (!HasAutonomousSingingGoal() || m_AutonomousGoalContinuations >= MaxWorkContinuations))) return;
        if (!string.IsNullOrEmpty(m_PendingSelfInspection)) triggerReason = "self-inspection-result";
        if (m_UserSpeechActiveForAutonomy)
        {
            if (m_EnableAutonomyIntentProbe)
                BeginAutonomyIntentProbe(triggerReason);
            else if (m_LogAgentLoop)
                Debug.Log("[Agent/自主意图] 用户正在表达且预判功能关闭；本次 tick 安静略过");
            return;
        }
        //角色还在说话(<continue/>链上一帧还没收尾) → 让流水线走完再排
        if (IsAISpeaking || m_AgentRoundInFlight || m_FormalResponseInFlight || IsVoiceOutputPlaying)
        {
            if (m_LogAgentLoop) Debug.Log($"[Agent] FireTick 排队等待(speaking={IsAISpeaking}, inflight={m_AgentRoundInFlight})");
            ScheduleNextTick(m_MinTickSec, "still-busy");
            return;
        }
        //连续 AI 轮次上限——工具结果仍允许回到角色手里一次，否则可能“查到了但不说”
        bool isSongToolResult = string.Equals(triggerReason, "self-inspection-result", StringComparison.Ordinal) ||
            string.Equals(triggerReason, "song-search-result", StringComparison.Ordinal) ||
            string.Equals(triggerReason, "song-catalog-result", StringComparison.Ordinal) ||
            string.Equals(triggerReason, "song-memory-result", StringComparison.Ordinal) ||
            string.Equals(triggerReason, "speaker-manage-result", StringComparison.Ordinal);
        //冲动模型接手后，"没人理"由疲劳表达(间隔逐次拉长，最终被 m_MaxTickSec 夹住)，
        //而不是撞线就彻底闭嘴。原来那个断崖的问题是：撞线后 FireTick 直接 return 且不再排
        //下一次，于是必须等用户开口才解封——用户走开 30 分钟，她后 22 分钟一声不吭。
        //这里只保留一个远得多的兜底，防冲动模型出 bug 时无限独白。
        int cap = (m_Urge != null && m_Urge.Enabled) ? m_MonologueBackstopTurns : m_MaxConsecutiveAITurns;
        bool canReviewWork = (m_EnableAutonomyIntentProbe || HasPendingSingingGoalReview()) && ShouldReviewUserWork();
        if (m_ConsecutiveAITurns >= cap && !isSongToolResult && !canReviewWork)
        {
            if (m_LogAgentLoop) Debug.Log($"[Agent] 连续 AI 轮次={m_ConsecutiveAITurns}≥{cap}，停止主动tick，等用户开口");
            return;
        }

        if (m_WorkReviewOpen && HasWorkToolInFlight() && !isSongToolResult)
        {
            ScheduleNextTick(Mathf.Max(m_MinTickSec, 5f), "pending-work-wait");
            return;
        }
        if ((m_EnableAutonomyIntentProbe || HasPendingSingingGoalReview()) && !ShouldBypassAutonomyIntentProbe(triggerReason))
        {
            BeginAutonomyIntentProbe(triggerReason);
            return;
        }

        PrepareActiveSkillsForRound(null, triggerReason);
        DispatchPreparedAgentTick(triggerReason, BuildPerceptionFrame(triggerReason));
    }

    /// <summary>
    /// 用户开口路径在送 LLM 之前调用。把感知帧拼到用户文本前面，让 LLM 也"感受"到时间。
    /// 返回拼好的字符串(已含感知帧 + 用户原话)。
    /// 同时更新 m_LastUserTurnTime / m_LastUserMsg, 把本轮标记为 user-triggered。
    /// </summary>
    public string PrepareUserTurn(string userText, string rawUserText = null)
    {
        SetCurrentUserInput(rawUserText ?? userText, userText);
        return PrepareAcceptedUserTurn(userText, rawUserText);
    }

    private string PrepareAcceptedUserTurn(string userText, string rawUserText)
    {
        m_SingingRequestSubmittedSinceUserTurn = false;
        m_NoSingingWorkContinuations = 0;
        m_UserSpeechActiveForAutonomy = false;
        //完整用户轮必须优先，不能让一个尚未返回的私人预判与正式回复并发占用模型。
        //已经完成并保存的想法仍会在下方交给角色；没来得及完成的预判安静取消。
        if (m_AutonomyProbeInFlight)
        {
            if (m_LogAgentLoop)
                Debug.Log("[Agent/自主意图] 完整用户轮已到达；取消尚未完成的私人预判");
            CancelAutonomyIntentProbe();
        }
        m_LastUserTurnTime = Time.realtimeSinceStartup;
        //情境召回:提及扫描同步生效(本帧可见),语境嵌入异步、作用于后续帧
        if (m_MemoryHub != null && m_EnableMemoryRecall)
            m_MemoryHub.NotifyUserUtterance(m_LastUserMsg);
        m_AgentRoundInFlight = m_AgentRunning;
        m_AgentCurrentRoundIsTick = false;
        ClearRoundParsed();

        //agent 没启动就不拼帧，保持向后兼容
        if (!m_AgentRunning) return userText ?? "";

        string frame = BuildPerceptionFrame("user-spoke");
        frame += ConsumeDeferredAutonomyThought();
        //当前这一条用户消息已经成为播放后的真实反馈候选。状态已经写进 frame，
        //随后清掉，避免未来自主 tick 继续把同一件事当成“还在等反馈”。
        m_PostHumBackFeedbackActive = false;
        m_PostHumBackFeedbackSummary = "";
        //用户那一句里带着 ASR 的「曲库里旋律接近的」候选行，完整 id 就在那儿——
        //她能看到的 id 都要入账，否则出处校验会把合法的调用也拦掉。
        string combined = frame + "\n" + (userText ?? "");
        HarvestSongIds(combined);
        bool willStream = m_UseStreaming && m_IsVoiceMode && m_ChatSettings.m_TextToSpeech != null;
        StageFormalObservationFrame(frame, m_FormalResponseGeneration + (willStream ? 1 : 0));
        return userText ?? "";
    }

    /// <summary>
    /// 一段 chain(可能含多个 LLM 轮次) 全部音频也播完后调用——按本 chain 最后一节
    /// 解析出的 &lt;next in/&gt; 排下次 tick。
    /// 调用点：FinishSpeakingNaturally、OnStreamComplete 的 silent-only 短路。
    /// 不在 Interrupt 调用——那条路径是用户接管，next tick 由用户路径自然产生。
    ///
    /// 注意：m_RecentAIUtterances / m_ConsecutiveAITurns / m_LastAIMsgPlain 这些
    /// per-round 状态**已在 OnStreamComplete 里更新过**，这里不再碰，否则会和
    /// chain 中段 LLM 看到的状态对不上。
    /// </summary>
    private void OnAgentRoundComplete()
    {
        if (!m_AgentRunning) return;
        if (!m_AgentRoundInFlight) return;
        m_AgentRoundInFlight = false;
        m_UserTurnSkillPins.Clear();

        if (m_AgentGracefulShutdownPending)
        {
            ClearRoundParsed();
            if (m_LogAgentLoop) Debug.Log("[Agent] 当前轮已收尾；优雅关闭期间不再安排新tick");
            return;
        }

        if (!string.IsNullOrEmpty(m_PendingSelfInspection))
        {
            if (m_SelfInspectionDeliveryAttempts >= MaxWorkContinuations)
            {
                m_WorkReviewOpen = false;
                m_WorkStatus = "blocked";
                ClearScheduledAgentWake();
                HandleSystemNotice(new SystemNotice("inspection_delivery_exhausted", SystemNoticeSeverity.Error,
                    "自检结果已保留，但连续生成回复失败，自动交付已停止。", "没有将事项记为完成。", "Agent", false));
                ClearRoundParsed();
                return;
            }
            ScheduleNextTick(m_MinTickSec, "self-inspection-result");
            ClearRoundParsed();
            return;
        }
        if (HasPendingSingingGoalReview() && (m_WorkReviewOpen ||
            (HasAutonomousSingingGoal() && m_AutonomousGoalContinuations < MaxWorkContinuations)))
        {
            m_AutonomyProbeNotBefore = -999f;
            ScheduleNextTick(m_MinTickSec, "singing-goal-review");
            ClearRoundParsed();
            return;
        }
        if (m_RoundWaitForUser)
        {
            if (m_LogAgentLoop)
                Debug.Log("[Agent] 复读或 continue 上限已终止自动续轮，等用户开口");
            ClearRoundParsed();
            return;
        }

        //排下一帧——读本 chain 最后一节解析到的 m_Round*
        //(chain 中段 OnStreamComplete 会 ClearRoundParsed 清掉自己；终止节没清，所以这里能读)
        //歌曲记忆有自己的单路径消费：明确保存请求由专用 continuation 给最终答复，
        //自主保存只把结果留给以后自然 tick。这里再按 pending 拉前会让同一结果同时
        //走“专用确认”和“通用 tick”，正是 8/28 两次逐字复读后的第三轮来源。
        if (m_SongCatalogResultPending && m_BringForwardOnSongSearchResult)
        {
            ScheduleNextTick(m_MinTickSec, "song-catalog-result");
        }
        else if (m_SongSearchResultPending && m_BringForwardOnSongSearchResult)
        {
            ScheduleNextTick(m_MinTickSec, "song-search-result");
        }
        else if (m_HumBackResultPending && m_BringForwardOnSongSearchResult)
        {
            //歌唱动作本身已经是角色的一次表达。结果返回后只给自主意图探针一次
            //选择机会，不再像查询工具那样绕过预判、强制追加一句话。
            ScheduleNextTick(m_MinTickSec, "song-action-result");
        }
        else if (m_RoundContinue)
        {
            //理论上不该走到——willChain 路径会直接续派。安全策略是
            //原地结束并等用户，不再用短延时 fallback 把刚截断的 chain 重启。
            if (m_LogAgentLoop)
                Debug.LogWarning("[Agent] 未消费的 <continue/> 到达收尾路径，已停止并等用户");
        }
        else if (m_RoundNextInSec.HasValue)
        {
            ScheduleNextTick(m_RoundNextInSec.Value, "llm-requested");
        }
        else
        {
            //LLM 没排——用兜底
            ScheduleNextTick(m_DefaultTickSec, "default");
        }

        ClearRoundParsed();
    }

    private void ClearRoundParsed()
    {
        m_RoundNextInSec = null;
        m_RoundFocus = null;
        m_RoundContinue = false;
        m_RoundSilent = false;
        m_RoundLookRequest = null;
        m_RoundSilencedForRepeat = false;
        m_RoundWaitForUser = false;
        m_ToolCorrectionExhaustedThisRound = false;
        m_HumBackTransactionRetainedThisRound = false;
    }

    /// <summary>
    /// 构造感知帧文本——给 LLM 注入"时间正在流动"的实感。
    /// 字段都按"按需读取"组织，LLM 自己挑要用的。
    /// </summary>
    private string BuildPerceptionFrame(string triggerReason)
    {
        var sb = new System.Text.StringBuilder();
        var now = System.DateTime.Now;
        sb.Append($"[感知帧 {now:HH:mm:ss}]");
        sb.Append(BuildContinuationExecutionFact(m_NoSingingWorkContinuations,
            IsSingingRuntimeActivityActive()));
        if (m_SingingRequestSubmittedSinceUserTurn || IsSingingRuntimeActivityActive() ||
            GetSkillRouteState("singing").ActiveThisRound || m_NoSingingWorkContinuations > 0)
            sb.Append(BuildSingingExecutionSnapshot());
        sb.Append(m_WorkProgressFact);
        var pendingCaptureSense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        int pendingCaptures = pendingCaptureSense != null
            ? pendingCaptureSense.PendingCompletedCaptureAnalysisCount : 0;
        if (pendingCaptures > 0)
            sb.Append($"\n[录音分析事实：之前有 {pendingCaptures} 份已经录完的音频仍在分析。" +
                "这不代表没有录到；内容/分段尚未返回，不能据此声称知道其中的歌或已经准备好播放。]");

        if (!string.IsNullOrEmpty(m_PendingSkillStatusFrame))
        {
            sb.Append(m_PendingSkillStatusFrame);
        }
        if (!string.IsNullOrEmpty(m_PendingToolCorrectionFrame))
        {
            sb.Append(m_PendingToolCorrectionFrame);
        }
        if (!string.IsNullOrEmpty(m_PendingSingingRoutingFact))
        {
            sb.Append(m_PendingSingingRoutingFact);
        }
        if (!string.IsNullOrEmpty(triggerReason) &&
            triggerReason.StartsWith("skill-request:", StringComparison.Ordinal))
        {
            string requested = triggerReason.Substring("skill-request:".Length);
            sb.Append($"\n[自主 Skill 申请已获准；{requested} 的详细规则本轮已经加载；" +
                      $"动机={m_PendingAutonomousSkillReason}。现在重新判断是否真的要执行，" +
                      "可以执行一次，也可以改变主意并保持沉默；不要把申请本身当成成功。]");
        }

        float rt = Time.realtimeSinceStartup;

        //你最近发言：完整列出最近 N 条，让 LLM 直观看到"我刚说了什么"——
        //这是反重复的关键信号(本地 LLM 看到自己 30s 前说过的话还是会照抄，把它显式拎出来)。
        if (m_RecentAIUtterances.Count > 0)
        {
            sb.Append("\n你最近发言:");
            int truncate = Mathf.Max(20, m_AIUtteranceTruncateChars);
            foreach (var kv in m_RecentAIUtterances)
            {
                float dt = rt - kv.Key;
                sb.Append($"\n  {FormatDuration(dt)}前: \"{TruncateForFrame(kv.Value, truncate)}\"");
            }
        }
        else
        {
            sb.Append("\n你还没开过口");
        }

        //距上次用户说话
        if (m_LastUserTurnTime > 0)
        {
            float dt = rt - m_LastUserTurnTime;
            sb.Append($"\n距用户上句: {FormatDuration(dt)}");
            //m_LastUserMsg 已与感知证据分开保存；这里兼容旧的带前缀调用，
            //仍先剥离再截断，避免短摘要预算被元数据占满。
            string lastUserPlain = StripPerceptionPrefixes(m_LastUserMsg);
            if (!string.IsNullOrEmpty(lastUserPlain))
                sb.Append($" (\"{TruncateForFrame(lastUserPlain, 40)}\")");
        }
        else
        {
            sb.Append("\n距用户上句: 还没开过口");
        }

        //上次自己设的 focus
        if (!string.IsNullOrEmpty(m_LastFocus))
            sb.Append($"\n你上次的注意状态: {m_LastFocus}");

        //连续 AI 轮次
        if (m_ConsecutiveAITurns > 0)
            sb.Append($"\n你已连续说话: {m_ConsecutiveAITurns} 次未等到用户回应");

        //环境
        if (m_LastSpikeTime > 0 && (rt - m_LastSpikeTime) < 5f)
        {
            sb.Append($"\n环境: 刚刚有非语音声响 (峰值rms={m_LastSpikePeakRms:F4})");
        }
        else
        {
            sb.Append("\n环境: 安静");
        }

        if (m_SongSearchInFlight)
        {
            sb.Append("\n歌曲检索工具: 正在查询，结果尚未返回；不要假装已经知道歌名");
        }
        if (m_SongSearchResultPending && !string.IsNullOrEmpty(m_LastSongSearchResult))
        {
            sb.Append("\n歌曲检索工具结果: ");
            sb.Append(m_LastSongSearchResult);
            sb.Append("（这是工具候选；置信度不足时应明确保留不确定性）");
        }
        if (m_SongCatalogInFlight)
        {
            sb.Append("\n曲库自查工具: 正在读取本机实时快照；结果前不得声称曲库里只有哪些歌");
        }
        if (m_SongCatalogResultPending && !string.IsNullOrEmpty(m_LastSongCatalogResult))
        {
            sb.Append("\n曲库自查工具结果:\n");
            sb.Append(m_LastSongCatalogResult);
        }
        if (m_SongMemoryInFlight)
        {
            sb.Append("\n歌曲记忆工具: 正在处理本机曲库；不要假装已经保存或改名成功");
        }
        if (m_SongMemoryResultPending && !string.IsNullOrEmpty(m_LastSongMemoryResult))
        {
            sb.Append("\n歌曲记忆工具结果: ");
            sb.Append(m_LastSongMemoryResult);
            //名字被丢掉的原因要和结果一起给，否则她只看到"未命名"，下一轮还会再编一个。
            if (!string.IsNullOrEmpty(m_DroppedSongTitleNote))
            {
                sb.Append(m_DroppedSongTitleNote);
            }
        }
        if (m_SpeakerManageInFlight)
        {
            sb.Append("\n声纹管理工具: 正在读取或修改本机声纹仓库；结果返回前不要声称操作成功");
        }
        if (m_SpeakerManageResultPending && !string.IsNullOrEmpty(m_LastSpeakerManageResult))
        {
            sb.Append("\n声纹管理工具结果:\n");
            sb.Append(m_LastSpeakerManageResult);
        }
        if (m_SongSingInFlight)
        {
            sb.Append("\n长期歌曲演唱工具: 正在从本地曲库定位真实音频；不要声称已经唱出或续唱成功");
        }
        //失败要持续可见，而不是只闪一帧。次数是她最缺的那个事实。
        if (!string.IsNullOrEmpty(m_StickyToolFailure) &&
            m_StickyToolFailureShown < k_StickyToolFailureMaxFrames)
        {
            sb.Append("\n历史失败事件（当时状态，不代表当前仍失败；来源、可播放性与范围以本帧最新素材状态为准）");
            if (m_StickyToolFailureCount > 1)
                sb.Append($"（同样的调用已经连续失败 {m_StickyToolFailureCount} 次）");
            sb.Append("：").Append(m_StickyToolFailure);
            if (m_StickyToolFailureCount > 1)
                sb.Append(" 若当前条件仍未变化，重复相同参数通常无效；状态已改变则按最新事实判断，可修正或询问。");
        }
        if (m_PracticeDropResultPending && !string.IsNullOrEmpty(m_LastPracticeDropResult))
        {
            sb.Append("\n练唱会话删除工具结果: ");
            sb.Append(m_LastPracticeDropResult);
        }
        if (m_PracticeEditResultPending && !string.IsNullOrEmpty(m_LastPracticeEditResult))
        {
            sb.Append("\n练唱素材来源/修订工具结果: ");
            sb.Append(m_LastPracticeEditResult);
        }
        if (m_HumBackResultPending && !string.IsNullOrEmpty(m_LastHumBackResult))
        {
            sb.Append("\n旋律回哼工具结果: ");
            sb.Append(m_LastHumBackResult);
        }
        if (m_PostHumBackFeedbackActive)
        {
            float ago = Mathf.Max(
                0f, Time.realtimeSinceStartup - m_PostHumBackCompletedRealtime);
            sb.Append("\n歌唱播放后客观状态: action=completed, " +
                      $"completed_ago={ago:F1}s, unity_playback_completed=true, " +
                      "physical_audibility=unknown, ");
            sb.Append(triggerReason == "user-spoke"
                ? "当前用户发言是播放完成后的首个反馈候选；结合原话理解。"
                : "user_feedback=none_yet；没有反馈不等于播放失败。" +
                  "是否开口、询问或安静等待由你自己判断。");
            if (!string.IsNullOrWhiteSpace(m_PostHumBackFeedbackSummary))
                sb.Append(" action_summary=").Append(m_PostHumBackFeedbackSummary);
        }
        if (!string.IsNullOrEmpty(m_LastUnknownTagNote))
        {
            sb.Append("\n上一轮你写了 " + m_LastUnknownTagNote +
                      " —— 没有这个标签，它被整条丢掉了，所以那一步**没有发生**。" +
                      "排下一拍用 <next in=\"Ns\" focus=\"…\"/>；标签名只能用规范里列出的那些，" +
                      "拼错不会有任何报错。如果那一轮你还打算唱/查/记，现在补上对应的标签。");
        }
        SenseVoiceSpeechToText practiceSenseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        sb.Append(BuildSessionSongLedgerClause());
        sb.Append(BuildSingingMaterialFacts(practiceSenseVoice));
        if (!string.IsNullOrEmpty(m_PracticeSemanticResolutionNote))
        {
            sb.Append(m_PracticeSemanticResolutionNote);
        }
        if (!string.IsNullOrEmpty(m_SelfRepeatNote))
        {
            sb.Append("\n复读拦截: " + m_SelfRepeatNote);
        }
        //必须排在清单前面：清单为空时它是**唯一**的信号。8/25 那次会话被清空后，
        //帧里干脆什么都不写，她只能相信自己笔记里的"第5段"，于是照着撞了两次。
        if (!string.IsNullOrEmpty(m_PracticeSessionCarryNote))
        {
            sb.Append(m_PracticeSessionCarryNote);
        }
        // Unified clip facts above include identity, boundaries and measured pitch.

        //视觉状态——告诉 LLM 自己的眼睛现在开着还是闭着
        if (m_EnableScreenVision)
        {
            sb.Append(m_AgentEyesOpen
                ? "\n视觉: 睁眼（程序将尝试采集；是否成功及对应时刻，以实际图片附件为准）"
                : "\n视觉: 闭眼(用 <look/> 可以睁眼)");
        }

        //记忆库——把 top-N 核心节点拼进感知帧,LLM 自己看哪些跟当前话题相关。
        //不再做 query-driven 召回(那是用字符串匹配模拟语义,注定漏同义词)——
        //语义关联是 LLM 的强项,工程层只负责把节点放她视野里,不替她挑选。
        //每帧都注入,不依赖用户是否开过口——她始终看得到自己的记忆。
        if (m_EnableMemoryRecall)
        {
            if (m_MemoryHub == null)
            {
                if (!m_MemoryHubMissingWarned)
                {
                    Debug.LogWarning("[Memory] m_EnableMemoryRecall=true 但 ChatSample 的 Memory Hub 字段为空——请在 Inspector 把 MemoryHub GameObject 拖进来");
                    m_MemoryHubMissingWarned = true;
                }
            }
            else
            {
                // 记忆块不再拼进感知帧，而是交给 LLM 层在消息列表**末尾**单独发送。
                // 帧会随对话留在历史里，把记忆写进帧等于每轮复制一份——实测帧 1266 字符
                // 里有 922 是记忆，历史里存了十几份几乎相同的内容，白白吃掉约 5000 token
                // 上下文，逼得历史更早被裁剪，而每次裁剪都要全量重算。
                // 放到末尾后：每轮重算量不变(记忆本来就是新文本)，但历史增长慢约 3.7 倍。
                string memBlock = m_MemoryHub.BuildMemoryMap();
                if (m_ChatSettings != null && m_ChatSettings.m_ChatModel != null)
                    m_ChatSettings.m_ChatModel.TrailingContext = memBlock ?? "";
            }
        }

        //会话深度
        if (m_AgentSessionStartTime > 0)
        {
            float age = rt - m_AgentSessionStartTime;
            if (age < 60f) sb.Append("\n会话阶段: 刚开始");
            else if (age < 300f) sb.Append($"\n会话阶段: 中 ({age / 60f:F0}分钟)");
            else sb.Append($"\n会话阶段: 深 ({age / 60f:F0}分钟)");
        }

        //待机整理：没人说话时，把孤立的记忆摆到她面前，让她自己决定连什么。
        //只在 tick 触发的轮次做——用户刚说完话时插这段会打断当前话题。
        if (m_EnableIdleMemoryConsolidation && m_MemoryHub != null && m_EnableMemoryRecall &&
            m_AgentCurrentRoundIsTick &&
            m_LastUserTurnTime > 0f &&
            rt - m_LastUserTurnTime >= m_IdleConsolidationAfterSec &&
            rt - m_LastConsolidationTime >= m_IdleConsolidationCooldownSec)
        {
            string view = m_MemoryHub.BuildConsolidationView();
            if (!string.IsNullOrEmpty(view))
            {
                sb.Append(view);
                m_LastConsolidationTime = rt;
                if (m_LogAgentLoop)
                    Debug.Log($"[Memory] 待机整理帧(静默 {(rt - m_LastUserTurnTime) / 60f:F1} 分钟)");
            }
        }

        //本帧是怎么来的
        if (!string.IsNullOrEmpty(triggerReason) &&
            triggerReason.StartsWith("skill-request:", StringComparison.Ordinal))
        {
            sb.Append("\n(本帧由你刚才的自主 Skill 申请触发；详细规则已加载，请按规则重新决策)");
            return sb.ToString();
        }
        switch (triggerReason)
        {
            case "session-start":
                sb.Append("\n(这是会话开启的第一帧——你可以选择打招呼，也可以等用户先开口)");
                break;
            case "continue-chain":
            case "continue":
                sb.Append("\n(本帧是你上次给了 <continue/> 立刻接的链——可以接着说，也可以选择停下)");
                break;
            case "spike-pull-forward":
                sb.Append("\n(本帧因环境出现动静被拉前——你可能从走神里被拽回来一下)");
                break;
            case "memory-surfaced":
                sb.Append("\n(本帧不是钟点到了——是你忽然想起了什么。看看「刚才不由自主想到的」" +
                          "那几条；想说就说，觉得没必要提也可以 <silent/>)");
                break;
            case "user-spoke":
                sb.Append("\n(用户刚开口讲了下面这段话，请回应)");
                break;
            case "input-candidate-empty":
                sb.Append("\n(输入候选没有有效转写；这是原用户问题的重新决策，不是新用户轮或自主时钟)");
                break;
            case "song-search-result":
                sb.Append("\n(你主动调用的歌曲检索刚返回；可以自然说出发现，也可以只在心里记下)");
                break;
            case "song-memory-result":
                sb.Append("\n(你主动调用的歌曲记忆操作刚返回；未知旋律可保持未命名，也可以自然地问用户一次)");
                break;
            case "scheduled":
            default:
                sb.Append("\n(本帧由你上次排定的 <next in/> 时钟触发)");
                break;
        }

        return sb.ToString();
    }

    private string FormatDuration(float sec)
    {
        if (float.IsNaN(sec) || float.IsInfinity(sec)) return "时间未知";
        if (sec < 1f) return "刚刚";
        // 总秒数只取整一次，再整除/取余，避免 100 秒被写成“2分40秒”。
        int total = Mathf.FloorToInt(Mathf.Min(sec, int.MaxValue / 2));
        if (total < 60) return $"{total}秒";
        if (total < 3600) return $"{total / 60}分{total % 60}秒";
        return $"{total / 3600}小时{(total % 3600) / 60}分";
    }

    private string TruncateForFrame(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "…";
    }

    /// <summary>
    /// 把 agent 标签从一段 TTS 输入文本里彻底剥掉——给 FlushCompleteSentences 在
    /// 推 chunk 进 TTS 队列前用。
    /// 必须存在的原因：LLM 经常把 `<continue/>` / `<next .../>` 单独写在一行上，
    /// 而 `\n` 是 IsStrongBoundary 之一，所以 "<continue/>\n" 会被当成"一句"切出来
    /// 排进 m_PendingChunks，被 TTS 朗读出来。这里在 enqueue 前再过一遍。
    /// (OnStreamComplete 里只剥 m_SentenceBuffer 的尾巴，挡不住已经flush 走的)
    /// </summary>
    /// <summary>
    /// 视觉感知：当前眼睛"睁开"且总开关启用时，捕获桌面截图并返回 base64 data-URL；
    /// 否则返回 null(让 ChatQW 不附图)。
    /// 同步调用，会阻塞主线程 ~50-150ms(BitBlt + StretchBlt + JPEG 编码)。
    /// </summary>
    private string MaybeCaptureScreenForLLM()
    {
        if (!m_AgentRunning || !m_EnableScreenVision || !m_AgentEyesOpen)
        {
            ArchiveClosedEyeImages();
            return null;
        }
        try
        {
            string url = DesktopCapture.CaptureToBase64Jpeg(
                m_CaptureMode, m_MonitorIndex,
                m_CaptureMaxDimension, m_CaptureJpegQuality);
            if (m_LogAgentLoop)
            {
                if (!string.IsNullOrEmpty(url))
                    Debug.Log($"[Agent] 视觉: 捕获屏幕 ({m_CaptureMode}, {m_CaptureMaxDimension}px, q={m_CaptureJpegQuality}, ~{url.Length / 1024}KB base64)");
                else
                    Debug.LogWarning("[Agent] 视觉: 捕获返回 null");
            }
            return url;
        }
        catch (Exception e)
        {
            Debug.LogError("[Agent] 视觉捕获异常: " + e.Message);
            return null;
        }
    }

    private void ArchiveClosedEyeImages()
    {
        if (m_ChatSettings == null || m_ChatSettings.m_ChatModel == null) return;
        int count = m_ChatSettings.m_ChatModel.ArchiveHistoricalImagePixels();
        if (count > 0 && m_LogAgentLoop)
            Debug.Log($"[Agent] 视觉: 归档 {count} 帧历史像素，保留当时的文字对话；后续闭眼请求不重复附图");
    }

    private class AgentSongMemoryRequest
    {
        public string SourceRef = "";
        public string Action = "";
        public string SongId = "";
        public string Title = "";
        public string Artist = "";
        public string Lyrics = "";
        public string Aliases = "";
        public string Reason = "";
    }

    private static bool IsExplicitHumBackRequest(string utterance)
    {
        string lower = (utterance ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower)) return false;

        if (IsHumBackCancellation(lower)) return false;

        string[] explicitRequests =
        {
            "哼回来", "哼一遍", "回哼", "跟着我哼", "跟我哼", "跟着我唱", "跟我唱",
            "学我唱", "唱一遍", "唱出来", "你来唱", "姐姐来唱", "姐姐唱", "试着唱",
            "唱刚才", "把刚才唱", "把这段唱", "重复这段旋律", "重复这个旋律", "回唱",
            "hum it back", "hum this back", "hum that back", "sing along", "sing with me",
            "sing it", "you sing", "try singing", "repeat the melody", "repeat this melody",
            "一緒に歌", "一緒にハミング", "真似して歌", "ハミングして", "歌ってみて",
            "歌って", "歌にして"
        };
        foreach (string phrase in explicitRequests)
            if (lower.Contains(phrase)) return true;
        return false;
    }

    private static bool IsRememberedSongContinuationRequest(string utterance)
    {
        string lower = (utterance ?? "").ToLowerInvariant();
        string[] cues =
        {
            "接着唱", "继续唱", "往下唱", "下一句", "下一段", "后面的", "后续",
            "唱下去", "接唱", "continue the song", "sing the next", "keep singing",
            "続きを歌", "続けて歌", "次のフレーズ", "その先を歌"
        };
        foreach (string cue in cues)
            if (lower.Contains(cue)) return true;
        return false;
    }

    private static string StripSingingPerceptionMetadata(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return "";
        return System.Text.RegularExpressions.Regex.Replace(
            utterance,
            @"\[(?:演唱片段|混合歌唱转说话)[^\]]*\]",
            " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
    }

    private static bool IsSingingFailureReport(string utterance)
    {
        string lower = StripSingingPerceptionMetadata(utterance).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower)) return false;
        string[] failureReports =
        {
            "唱不出来了", "唱不出来", "没唱出来", "没有唱出来", "没能唱出来",
            "还是没唱", "又没唱", "根本没唱", "唱不了", "没有听到你唱",
            "没听到你唱", "没有听见你唱", "没听见你唱",
            "couldn't sing", "could not sing", "didn't sing", "did not sing",
            "歌えなかった", "歌えてない", "歌っていない"
        };
        bool hasFailure = false;
        foreach (string phrase in failureReports)
        {
            if (!lower.Contains(phrase)) continue;
            hasFailure = true;
            break;
        }
        if (!hasFailure) return false;

        // “我唱不出来，你来唱一遍”仍是明确请求；只有清晰的命令结构才覆盖失败陈述。
        string[] explicitCommands =
        {
            "你来唱", "姐姐来唱", "请你唱", "麻烦你唱", "唱给我听",
            "能不能唱", "可以唱一", "试着唱", "再唱一遍",
            "please sing", "you sing it", "try singing",
            "歌ってください", "歌ってみて"
        };
        foreach (string command in explicitCommands)
            if (lower.Contains(command)) return false;
        return true;
    }

    private static bool IsRecentSingingReference(string utterance)
    {
        string lower = StripSingingPerceptionMetadata(utterance).ToLowerInvariant();
        string[] references =
        {
            "刚才", "刚刚", "方才", "这首", "这一首", "这段", "这一段",
            "我刚唱", "我们刚唱", "最近那首", "recent song", "just sang",
            "that phrase", "this phrase", "さっき", "この曲", "このフレーズ"
        };
        foreach (string reference in references)
            if (lower.Contains(reference)) return true;
        return false;
    }

    /// <summary>
    /// 比“最近那首歌”更窄：只有明确指向刚唱/刚录的具体片段或旋律，才能据此
    /// 纠正素材源。宽泛的“这首歌”仍留给 LLM 结合上下文判断。
    /// </summary>
    private static bool IsRecentPerformanceMaterialReference(string utterance)
    {
        string lower = StripSingingPerceptionMetadata(utterance).ToLowerInvariant();
        string[] references =
        {
            "刚才唱", "刚刚唱", "方才唱", "我刚唱", "我们刚唱",
            "刚才录", "刚刚录", "我录的", "这段", "这一段", "刚才的片段",
            "刚刚的片段", "这个旋律", "这段旋律", "刚才的旋律", "刚刚的旋律",
            "just sang", "just recorded", "this phrase", "that phrase", "this melody",
            "さっき歌", "今歌った", "このフレーズ", "このメロディ"
        };
        foreach (string reference in references)
            if (lower.Contains(reference)) return true;
        return false;
    }

    private static bool IsSingingCapabilityQuestion(string utterance)
    {
        string lower = StripSingingPerceptionMetadata(utterance).ToLowerInvariant().Trim();
        if (string.IsNullOrWhiteSpace(lower)) return false;
        string[] actualPerformanceRequests =
        {
            "唱给我听", "唱一首", "唱一下", "唱一遍", "你来唱", "试着唱",
            "sing it", "sing a song for me", "try singing", "歌ってみて"
        };
        foreach (string request in actualPerformanceRequests)
            if (lower.Contains(request)) return false;

        string[] capabilityQuestions =
        {
            "会唱歌吗", "能唱歌吗", "会不会唱歌", "能不能唱歌",
            "有唱歌的能力", "唱歌的能力", "是否会唱歌",
            "are you able to sing", "do you know how to sing",
            "歌えるの", "歌えますか", "歌う能力"
        };
        foreach (string question in capabilityQuestions)
            if (lower.Contains(question)) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(
            lower,
            @"^\s*can you sing(?:\s+a song)?\s*[?？]?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static bool HasRememberedSongCue(string utterance)
    {
        string lower = StripSingingPerceptionMetadata(utterance).ToLowerInvariant();
        string[] memoryCues =
        {
            "记住的歌", "已经记住", "你记得的歌", "曲库", "以前学", "之前学",
            "remembered song", "from memory", "song library", "覚えた歌", "記憶の歌"
        };
        foreach (string cue in memoryCues)
            if (lower.Contains(cue)) return true;
        return false;
    }

    private static bool IsPlausibleUnquotedSongTitle(string title)
    {
        string generic = (title ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(generic)) return false;
        string[] genericTitles =
        {
            "歌", "这首歌", "这首", "这一首", "那首", "那一首",
            "这段", "这一段", "那段", "片段", "演唱片段", "刚才",
            "接下来", "能力", "出来", "出来了", "不出来了", "不出来", "不了",
            "一下", "一遍", "一段", "一首",
            "the song", "this song", "that song", "it", "the phrase", "this phrase"
        };
        foreach (string genericTitle in genericTitles)
            if (generic == genericTitle) return false;

        string[] instructionFragments =
        {
            "跟着我", "跟我唱", "跟我哼", "我们唱", "我唱出来", "你唱出来",
            "唱出来", "唱一遍", "给我听", "能不能", "可以吗", "会不会",
            "怎么唱", "什么情况", "sing along", "sing with me", "you sing",
            "一緒に歌", "真似して歌"
        };
        foreach (string fragment in instructionFragments)
            if (generic.Contains(fragment)) return false;

        // 捕获发生在“唱”之后；这种短尾巴是失败语法，不可能是可靠的曲库选择器。
        if (System.Text.RegularExpressions.Regex.IsMatch(
            generic,
            @"^(?:不|没|没有)(?:能|会)?(?:唱|哼)?(?:出)?(?:来|去)?(?:了|啦|啊)?$"))
            return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(
            generic,
            @"^的?(?:这|那)(?:一)?(?:首|段)(?:歌|曲)?(?:呢|呃|啊|呀)?$"))
            return false;
        return true;
    }

    //ExtractRequestedSongTitle / IsExplicitRememberedSongSingRequest 随兜底一并删除：
    //它们唯一的用途就是从普通说话里猜曲库歌名，而那个猜测已被证明不可靠。
    //模型自己写在 <song_sing title=""/> 里的标题仍会被 IsPlausibleUnquotedSongTitle 校验。

    private bool ShouldDiscardSongSingToolForCurrentTurn(AgentSongSingRequest request)
    {
        if (request == null) return false;
        string semanticText = StripSingingPerceptionMetadata(m_LastUserMsg);
        if (IsSingingFailureReport(semanticText) ||
            IsSingingCapabilityQuestion(semanticText) ||
            IsSingAlongInvitation(semanticText))
            return true;
        if (IsCurrentTurnConfirmedSinging() && !HasRememberedSongCue(semanticText))
            return true;

        // 模糊指代和 ASR 元数据不允许被模型自行填成曲库标题；若有近期歌声，
        // 后面的 hum_back 兜底会接管，若只剩落盘记忆则使用可靠的最近歌曲 ID。
        bool hasSelector = !string.IsNullOrWhiteSpace(request.SongId) ||
            IsPlausibleUnquotedSongTitle(request.Title);
        return !hasSelector && IsRecentSingingReference(semanticText);
    }

    private bool RejectInvalidSongSingMaterialSource(ref AgentSongSingRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.SourceValidationError))
            return false;
        string detail = request.SourceValidationError;
        ReportToolFailureForLlm(
            "song_sing",
            "invalid_material_source",
            detail + " 本次没有读取或播放任何音频。",
            "请结合本轮歌唱素材事实重新自主选择：recent_turn 用 hum_back echo，" +
            "practice 用 hum_back practice，library 才用 song_sing；拿不准可以先询问用户。");
        if (m_LogHumBack)
            Debug.LogWarning("[Singing/Source] " + detail);
        request = null;
        return true;
    }

    /// <summary>
    /// song_sing 只面向长期曲库；order 和“刚录的几段/练习片段”只属于当前素材。
    /// 模型已经自主决定要唱之后，如果误把明确的当前素材写成曲库标签，这里只纠正
    /// 客观素材源：即时录音走 echo，落入练唱会话的片段走 practice。是否唱不由这里决定。
    /// </summary>
    private bool TryRerouteSongSingToPractice(
        ref AgentSongSingRequest songSing,
        ref AgentHumBackRequest humBack)
    {
        if (songSing == null) return false;
        if (songSing.UnifiedSing) return false;

        string semanticText = StripSingingPerceptionMetadata(m_LastUserMsg);
        string declaredSource = NormalizeSingingMaterialSource(songSing.Source);
        bool practiceIntent = declaredSource == "practice" || IsSongSingPracticeIntent(
            semanticText, songSing.Reason, songSing.Order);
        bool recentMaterialIntent = !practiceIntent &&
            (declaredSource == "recent_turn" ||
             (IsRecentPerformanceMaterialReference(semanticText) &&
              IsExplicitHumBackRequest(semanticText) &&
              !HasRememberedSongCue(semanticText) &&
              !UserExplicitlySuppliedSongSelector(semanticText, songSing) &&
              HasRecentSingableMaterial()));
        if (!practiceIntent && !recentMaterialIntent) return false;

        SenseVoiceSpeechToText sense = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        bool useImmediateEcho = recentMaterialIntent && sense != null &&
            sense.HasFreshSingingAudio() && HasRecentPlayableSingingPerformance();
        string correctedMode = useImmediateEcho ? "echo" : "practice";
        string correctedOrder = songSing.Order ?? "";
        if (recentMaterialIntent && !useImmediateEcho &&
            string.IsNullOrWhiteSpace(correctedOrder) && sense != null &&
            sense.PracticePhraseCount > 0)
            correctedOrder = sense.PracticePhraseCount.ToString();

        if (humBack == null)
        {
            // song_sing 用错素材源时，只纠正工具，不得重新创建一个残缺请求。
            // 角色已经提交的 pitch_plan / transpose 等音乐目标以及 pace / expression
            // 都属于她的主观演唱决定，应当原样进入 hum_back；非法值继续走同一套校验回报。
            humBack = BuildSongSingPracticeReroute(
                songSing, correctedMode, correctedOrder);
        }
        else
        {
            humBack.Mode = correctedMode;
            humBack.Source = correctedMode == "echo" ? "recent_turn" : "practice";
            if (string.IsNullOrWhiteSpace(humBack.Order))
                humBack.Order = correctedOrder;
            humBack.SourceValidationError = ValidateHumBackMaterialSource(humBack);
        }
        songSing = null;
        return true;
    }

    private static AgentHumBackRequest BuildSongSingPracticeReroute(
        AgentSongSingRequest songSing,
        string correctedMode,
        string correctedOrder)
    {
        AgentHumBackRequest request = songSing != null
            ? songSing.HumBackCompatibleRequest
            : null;
        if (request == null) request = new AgentHumBackRequest();
        request.Mode = correctedMode;
        request.Source = correctedMode == "echo" ? "recent_turn" : "practice";
        request.SourceValidationError = ValidateHumBackMaterialSource(request);
        request.Order = correctedOrder ?? "";
        request.Reason = songSing == null || string.IsNullOrWhiteSpace(songSing.Reason)
            ? "曲库标签实际指向当前真实歌声素材，已按客观来源纠正"
            : songSing.Reason;
        return request;
    }

    private static bool UserExplicitlySuppliedSongSelector(
        string userText,
        AgentSongSingRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(userText)) return false;
        if (!string.IsNullOrWhiteSpace(request.SongId) &&
            userText.IndexOf(request.SongId.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        string title = (request.Title ?? "").Trim();
        return IsPlausibleUnquotedSongTitle(title) &&
            TextContainsNormalizedSongTitle(userText, title);
    }

    private static bool IsSongSingPracticeIntent(
        string userText,
        string toolReason,
        string order)
    {
        // song_sing 的协议里根本没有 order；出现它就是模型把 practice 参数写错工具了。
        if (!string.IsNullOrWhiteSpace(order)) return true;
        return IsPracticeCompositionRequest(userText) ||
            IsPracticeCompositionRequest(toolReason);
    }

    private bool IsCurrentTurnSpokenSingingExit()
    {
        // DealingTextCallback only adds this marker after both singing evidence and an
        // exit phrase were confirmed. Checking raw text here would misread an ordinary
        // request such as "我不会唱，你唱给我听" as a cancelled performance.
        return m_LastUserEvidence.IndexOf("[混合歌唱转说话", StringComparison.Ordinal) >= 0;
    }

    private static bool IsPracticeCompositionRequest(string utterance)
    {
        string lower = (utterance ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower) || IsHumBackCancellation(lower)) return false;
        string[] requests =
        {
            "连起来唱", "连着唱", "连续唱", "接起来唱", "串起来唱", "合起来唱",
            "按顺序唱", "顺序唱", "一口气唱", "从头唱到尾", "整段唱",
            "完整唱", "把几段唱", "把这些段唱", "刚才几段", "练习的几段", "练过的几段",
            "combine the phrases", "join the phrases", "sing them together", "sing the whole sequence",
            "フレーズを繋", "つなげて歌", "続けて歌", "全部通して歌"
        };
        foreach (string phrase in requests)
            if (lower.Contains(phrase)) return true;

        // ASR 常把“把我刚刚录的那三段唱一遍”转成不同语序，无法穷举整句。
        // 只有“多段练唱指代 + 立即演唱动作”同时存在才算，避免把普通的曲库点歌改道。
        bool refersToPracticeMaterial = lower.Contains("练习") || lower.Contains("练过") ||
            lower.Contains("刚才录") || lower.Contains("刚刚录") || lower.Contains("我录的") ||
            lower.Contains("这些段") || lower.Contains("这几段") || lower.Contains("那几段") ||
            lower.Contains("三段") || lower.Contains("四段") || lower.Contains("练习片段") ||
            lower.Contains("刚才的片段") || lower.Contains("刚刚的片段");
        bool asksToPerform = lower.Contains("唱") || lower.Contains("哼") ||
            lower.Contains("sing") || lower.Contains("hum") || lower.Contains("歌って");
        if (refersToPracticeMaterial && asksToPerform) return true;
        return false;
    }

    private static bool IsHumBackCancellation(string utterance)
    {
        string lower = (utterance ?? "").ToLowerInvariant();
        string[] negative =
        {
            "不要哼", "别哼", "不用哼", "不要唱", "别唱", "不用唱",
            "don't hum", "do not hum", "don't sing", "do not sing",
            "ハミングしない", "歌わないで", "歌わなくて"
        };
        foreach (string phrase in negative)
            if (lower.Contains(phrase)) return true;
        return false;
    }

    private static bool IsSingAlongInvitation(string utterance)
    {
        string lower = (utterance ?? "").ToLowerInvariant();
        string[] invitations =
        {
            "跟着我唱", "跟我唱", "跟着我哼", "跟我哼", "学我唱",
            "sing along", "sing with me", "follow me singing", "follow my singing",
            "一緒に歌", "一緒にハミング", "真似して歌"
        };
        foreach (string phrase in invitations)
            if (lower.Contains(phrase)) return true;

        // “接下来我换一段歌，看看姐姐能不能唱出来”也是约定下一步，而不是
        // 要求立刻重放上一首。必须同时含未来提示和歌唱语义，避免泛化到普通请求。
        string[] futureCues =
        {
            "接下来", "等一下", "待会", "待会儿", "之后", "下一段", "下一首", "换一段", "换一首",
            "我先唱", "我要唱", "我准备唱", "我再唱",
            "next", "after that", "in a moment", "i will sing", "i'll sing",
            "次に", "これから", "あとで", "私が歌", "もう一度歌"
        };
        bool hasFutureCue = false;
        foreach (string cue in futureCues)
        {
            if (!lower.Contains(cue)) continue;
            hasFutureCue = true;
            break;
        }
        bool hasSingingIntent = lower.Contains("唱") || lower.Contains("哼") ||
            lower.Contains("歌") || lower.Contains("sing") || lower.Contains("hum") ||
            lower.Contains("ハミング");
        if (hasFutureCue && hasSingingIntent) return true;
        return false;
    }

    /// <summary>
    /// 只判断用户是否明确说“接下来由我唱”。这不是动作权限或意图路由，只是下一次
    /// 麦克风输入的短期声学先验，因此刻意不把“接下来你唱”之类的角色点歌算进来。
    /// </summary>
    private static bool AnnouncesUpcomingUserSinging(string utterance)
    {
        string lower = StripSingingPerceptionMetadata(utterance).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower)) return false;
        string[] announcements =
        {
            "我先唱", "我要唱", "我准备唱", "我再唱", "我来唱", "我唱一", "我接着唱",
            "接下来我唱", "下一段我唱", "下一首我唱", "等我唱", "听我唱", "听我哼",
            "跟着我唱", "跟我唱", "跟着我哼", "跟我哼", "学我唱",
            "i will sing", "i'll sing", "let me sing", "listen to me sing", "follow my singing",
            "私が歌", "私から歌", "これから歌う", "もう一度歌う", "私の歌を聞"
        };
        foreach (string phrase in announcements)
            if (lower.Contains(phrase)) return true;
        return false;
    }

    private bool HasUpcomingUserSingingExpectation()
    {
        if (!m_UpcomingUserSingingExpected) return false;
        if (Time.realtimeSinceStartup - m_UpcomingUserSingingExpectedAt <=
            k_UpcomingUserSingingExpectationSeconds)
            return true;
        m_UpcomingUserSingingExpected = false;
        if (m_LogHumBack)
            Debug.Log("[Singing/Expected] 用户预告的下一轮歌声已超过 3 分钟，取消声学先验");
        return false;
    }

    private bool HasExpectedUserSingingAcousticContext()
    {
        return m_EouExpectedUserSinging || HasUpcomingUserSingingExpectation() ||
            HasActiveSingAlongRequest();
    }

    private void UpdateUpcomingUserSingingExpectation(string completedUserTurn)
    {
        bool announced = AnnouncesUpcomingUserSinging(completedUserTurn);
        m_EouExpectedUserSinging = false;
        m_UpcomingUserSingingExpected = announced;
        m_UpcomingUserSingingExpectedAt = announced ? Time.realtimeSinceStartup : -999f;
        if (announced && m_LogHumBack)
            Debug.Log("[Singing/Expected] 用户明确预告下一轮会唱；仅启用一次声学复核，不自动回唱");
    }

    private bool IsCurrentTurnConfirmedSinging()
    {
        return m_LastUserEvidence.IndexOf("[演唱片段", StringComparison.Ordinal) >= 0;
    }

    private bool HasActiveSingAlongRequest()
    {
        SkillRouteState skillState = GetSkillRouteState("singing");
        if (skillState.Access != SkillAccess.Available)
        {
            m_WaitingForRequestedSingAlong = false;
            return false;
        }
        if (!m_WaitingForRequestedSingAlong) return false;
        if (Time.realtimeSinceStartup - m_SingAlongRequestArmedAt <= 300f) return true;
        m_WaitingForRequestedSingAlong = false;
        if (m_LogHumBack) Debug.Log("[HumBack] 等待跟唱已超过 5 分钟，自动取消");
        return false;
    }

    private void ArmSingAlongForNextPerformance()
    {
        m_WaitingForRequestedSingAlong = true;
        m_SingAlongRequestArmedAt = Time.realtimeSinceStartup;
        //**不再清空练唱会话。**
        //原来只要"当前没 armed"就重开一个会话、把之前教过的段全丢掉。而用户在
        //"教新段落"和"让她合起来唱"之间来回时必然反复触发 arm，于是 8/17 实测：
        //开场唱的「沉默着走了有多遥远」被清掉了，之后 order="1" 指向的变成了后来
        //教的「说好了的永远断了线」。用户连着四轮说"你又唱了那段"，她一次都没弄明白，
        //最后说出「你说的『沉默着走了有多遥远』……这段旋律，我的曲库里好像还没有」
        //——而它明明在曲库里(e93363767688，她自己存的)，只是练唱会话那一层没了。
        //
        //清空是一个决定，不该是"又说了一次跟着我唱"的副作用。现在只在 Agent Loop
        //启动时清一次；会话内一直累积，上限 16 段。她看得到每段的歌词、时间、
        //疑似曲目和"同一句的第几遍"，要唱哪几段用 order 指定。
        if (m_LogHumBack)
            Debug.Log("[HumBack] 已进入持续练唱状态；后续每段最终确认的歌声都会触发跟唱，直到取消或超时");
    }

    private void RefreshActiveSingAlongSession()
    {
        if (!m_WaitingForRequestedSingAlong) return;
        m_SingAlongRequestArmedAt = Time.realtimeSinceStartup;
    }

    private bool HasRecentPlayableSingingPerformance()
    {
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        float[] timeline;
        float frameSeconds;
        string language;
        return senseVoice != null && senseVoice.TryGetRecentSingingPerformance(
            out timeline, out frameSeconds, out language) && timeline != null && timeline.Length > 0;
    }

    /// <summary>
    /// 已经约定好的轮唱在用户真实唱完后可走确定性快速回唱，因此需要提前扣住普通 TTS。
    /// 普通点歌/回唱请求则等待 LLM 自己选择工具；不能因为程序猜测它会唱就先把正文扣住，
    /// 否则模型选择不唱或漏写工具时会造成整轮失声。
    /// </summary>
    private bool ShouldHoldSpeechForExplicitHumBack()
    {
        if (!CanExecuteSkillAction("singing", out _) ||
            !m_EnableAutonomousHumBack || m_AgentCurrentRoundIsTick ||
            IsHumBackCancellation(m_LastUserMsg) || IsCurrentTurnSpokenSingingExit())
            return false;

        //本轮已经确定不会有真实回哼时绝不能扣留——扣留等的是"回哼结果"，
        //而结果永远不会到来，这一轮她就整个哑掉。8/12 实测：用户说
        //「那我要开始喽，接下来我唱的都是假的」，声学判唱(0.67)、文字判说，
        //走软降级注入了"请开口追问是不是在唱"，她也照做写了回复，
        //却被这道闸扣住一个字都没出声——判对了不唱，代价是整轮失声。
        if (m_FinalModeSoftDowngrade || HasStrongSpeculativeSpeechVeto())
            return false;

        //原来这里只要用户的话"看起来像点歌"就扣住 TTS，等曲库演唱出声。配合上面那个
        //已删除的正则兜底，8/9 实测扣了 7 次、成功 0 次，那几轮直接没有声音。
        //扣留只应该发生在她**真的**调用了 <song_sing/> 之后——那条路径由 BeginSongSing
        //自己负责，不需要在这里预判。
        bool confirmedSinging = IsCurrentTurnConfirmedSinging();
        if (!confirmedSinging && IsPracticeCompositionRequest(m_LastUserMsg))
            return false;
        if (!confirmedSinging && IsSingAlongInvitation(m_LastUserMsg)) return false;
        bool armedPerformance = confirmedSinging && HasActiveSingAlongRequest();
        return armedPerformance && HasRecentPlayableSingingPerformance();
    }

    /// <summary>
    /// 只提取用户明确说出的名称；识别不到就留空，让歌曲保持“未命名旋律”。
    /// 例如：“这首歌叫 Lemon，你能记住吗” → Lemon。
    /// </summary>
    private static string ExtractExplicitSongTitle(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return "";
        string[] patterns =
        {
            @"(?:这首歌|这首曲子|这段旋律|刚才唱的歌)\s*(?:叫|名字叫|是)\s*[“""'「『]?(?<title>[^，,。！？!?\r\n""”’」』]{1,80})",
            @"(?:this song is called|this song is|the song is called)\s+[""']?(?<title>[^,.!?\r\n""']{1,80})",
        };
        foreach (string pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                utterance, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            string title = match.Groups["title"].Value.Trim();
            if (string.IsNullOrEmpty(title)) continue;
            //无标点的 ASR 句子可能把后续请求也吞进 title；这种情况宁可保持未命名。
            string lower = title.ToLowerInvariant();
            if (lower.Contains("记住") || lower.Contains("保存") || lower.Contains("remember") ||
                lower.Contains("save "))
                continue;
            return title;
        }
        return "";
    }

    private static readonly System.Text.RegularExpressions.Regex s_SongMemoryTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<song_(?<action>remember|rename|forget)\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private AgentSongMemoryRequest ExtractSongMemoryTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = s_SongMemoryTagRegex.Match(text);
        if (!match.Success) return null;
        string attrs = match.Groups["attrs"].Value;
        AgentSongMemoryRequest request = new AgentSongMemoryRequest
        {
            Action = match.Groups["action"].Value.Trim().ToLowerInvariant(),
            SourceRef = ReadToolAttribute(attrs, "source_ref"),
            SongId = ReadToolAttribute(attrs, "id"),
            Title = ReadToolAttribute(attrs, "title"),
            Artist = ReadToolAttribute(attrs, "artist"),
            Lyrics = ReadToolAttribute(attrs, "lyrics"),
            Aliases = ReadToolAttribute(attrs, "aliases"),
            Reason = ReadToolAttribute(attrs, "reason"),
        };
        text = s_SongMemoryTagRegex.Replace(text, "").Trim();
        return request;
    }

    private class AgentSongSearchRequest
    {
        public string Query = "";
        public string Mode = "auto";
        public string Reason = "";
    }

    private class AgentSongCatalogRequest
    {
        public string Query = "";
        public int Offset = 0;
        public int Limit = 30;
        public bool IncludeUnnamed = false;
        public string Reason = "";
    }

    private class AgentHumBackRequest
    {
        public bool UnifiedSing;
        public string ClipRefs = "";
        public bool ConfirmUser;
        public string Range = "current";
        public float StartSeconds = float.NaN;
        public float EndSeconds = float.NaN;
        public string Mode = "echo";
        //素材来源由角色结合语境显式表达；mode 仍负责具体播放方式。
        //留空兼容旧回复，执行层会从 mode 推断并照常校验真实可用性。
        public string Source = "";
        public string Lyrics = "";
        public string Reason = "";
        //演唱参数：她可以自己决定这一遍怎么唱。NaN/未指定时沿用按 seed 生成的默认档，
        //所以不写这些属性时行为和以前完全一样。
        public float Key = float.NaN;         //移调，半音
        public float Pace = float.NaN;        //速度倍率
        public float Expression = float.NaN;  //演绎强度：0=逐帧复刻用户，1=尽量按她自己的表现
        //practice 的演唱顺序，如 "2,1"；空串按练唱先后。段号见感知帧里的练唱会话清单。
        public string Order = "";
        //clean=默认干净边界；expanded=明确针对漏唱/边界不完整时采用相邻旋律岛扩展版。
        //只对 practice 有意义，决定仍来自角色，不由程序按用户关键词代选。
        public string Capture = "clean";
        //在所选 capture 上再做明确裁剪。只支持单段，避免一个数被机械套到多段。
        public float TrimHeadSeconds = 0f;
        public float TrimTailSeconds = 0f;
        //由 LLM 基于完整语义填写；程序不搜“不要说话”等关键词。若 true 而所选
        //expanded 边界仍含已分类为 speech 的片段，执行前把冲突事实退回给 LLM。
        public bool ExcludeSpeech = false;
        //key 写成逗号分隔时(如 "0,4,0")是**逐段各自移调**，一项对应 order 里的一段。
        //整条统一移调是一个数、走原来的单次转换；逐段要 N 次转换，慢一些但能只动一段。
        public float[] KeyPerSegment = null;
        //旧协议兼容：新回复应使用下面的 pitch_plan / transpose 音乐目标。
        public string TargetNote = "";
        public int AlignToPracticeIndex = 0;
        //旧的拆分接口：角色表达音乐目标，程序负责把它翻译成 SVC 半音参数。
        //pitch_target: D4 或 keep,keep,D4；pitch_delta: +M2 或 keep,keep,+m2；
        //pitch_match: 练唱清单段号。target_note/align_to 仅作旧回复兼容。
        public string PitchTarget = "";
        public string PitchDelta = "";
        public string PitchMatch = "";
        //规范化主接口：每项对应 order 中同一位置，可混合 keep、A3、+m2、match:1。
        //transpose 是把所选各段基于角色最近一次实际演唱统一做相对移调的快捷操作。
        public string PitchPlan = "";
        public string Transpose = "";
        //解析阶段发现非法/冲突参数时保留真实原因，执行层会把它交回 LLM 纠错，
        //绝不静默截断成另一个动作。
        public string ValidationError = "";
        public string SourceValidationError = "";
        public int ExtraTagsIgnored = 0;
    }

    //逐段移调这一次实际发生了什么，回报给她时如实写出来(包括"你写了 N 项但只有 M 段")。
    private string m_LastPracticeShiftNote = "";

    //上一轮出现过的不存在标签，点破一次就清掉。
    private string m_LastUnknownTagNote = "";

    /// <summary>
    /// 把角色表达的乐理目标换算成转换器实际需要的逐段半音值。
    /// LLM 决定“想成为哪个音/音程/参考段”；程序只读取音高事实、做算术并检查量程。
    /// </summary>
    private bool TryResolvePracticePitchTarget(
        AgentHumBackRequest request,
        SenseVoiceSpeechToText.PracticeComposition composition,
        out int[] directShifts,
        out float[] targetCenters,
        out string note,
        out string failure)
    {
        directShifts = null;
        targetCenters = null;
        note = "";
        failure = "";
        if (request == null || !HasMusicalPitchIntent(request))
            return true;
        if (!IsPracticeHumMode(request.Mode))
        {
            failure = "pitch_plan/transpose 及旧 pitch_* 音高属性只适用于 practice 练唱片段；" +
                      "echo 没有可引用的逐段音高状态。";
            return false;
        }
        if (composition == null || composition.SegmentWavs == null ||
            composition.SegmentWavs.Count == 0 || composition.SegmentMedians == null ||
            composition.SegmentMedians.Count != composition.SegmentWavs.Count)
        {
            failure = "当前练唱素材没有完整的逐段中心音高证据，无法可靠执行音乐目标。";
            return false;
        }

        List<SenseVoiceSpeechToText.PracticePhraseInfo> phrases = null;
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (senseVoice != null) phrases = senseVoice.DescribePracticePhrases();

        int count = composition.SegmentWavs.Count;
        var currentCenters = new float[count];
        var stableIds = new int[count];
        for (int i = 0; i < count; i++)
        {
            int sourceIndex = composition.SegmentSourceIndices != null &&
                              i < composition.SegmentSourceIndices.Count
                ? composition.SegmentSourceIndices[i]
                : i + 1;
            SenseVoiceSpeechToText.PracticePhraseInfo phrase =
                FindPracticePhraseInfo(phrases, sourceIndex);
            stableIds[i] = phrase != null ? phrase.StableId : 0;
            currentCenters[i] = GetCurrentPracticeCenterMidi(
                stableIds[i], composition.SegmentMedians[i]);
        }

        targetCenters = new float[count];
        string planSpec = (request.PitchPlan ?? "").Trim();
        string transposeSpec = (request.Transpose ?? "").Trim();
        string targetSpec = !string.IsNullOrWhiteSpace(request.PitchTarget)
            ? request.PitchTarget.Trim()
            : request.TargetNote.Trim();
        string deltaSpec = (request.PitchDelta ?? "").Trim();
        string matchSpec = !string.IsNullOrWhiteSpace(request.PitchMatch)
            ? request.PitchMatch.Trim()
            : request.AlignToPracticeIndex > 0
                ? request.AlignToPracticeIndex.ToString()
                : "";

        if (!string.IsNullOrWhiteSpace(planSpec))
        {
            string[] values = SplitPitchValueList(planSpec);
            if (values.Length != 1 && values.Length != count)
            {
                failure = $"pitch_plan 写了 {values.Length} 项，而 order 选择了 {count} 段；" +
                          "请写一个共同目标，或为每段各写一项。";
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                string value = values.Length == 1 ? values[0] : values[i];
                if (IsSourcePitchToken(value))
                {
                    targetCenters[i] = composition.SegmentMedians[i];
                    continue;
                }
                if (IsKeepPitchToken(value))
                {
                    targetCenters[i] = currentCenters[i];
                    continue;
                }
                if (TryParseMidiNote(value, out float absolute))
                {
                    targetCenters[i] = absolute;
                    continue;
                }
                if (TryParseMusicalInterval(value, out float delta))
                {
                    targetCenters[i] = currentCenters[i] + delta;
                    continue;
                }
                if (TryParsePitchPlanMatchToken(value, out int referenceIndex))
                {
                    SenseVoiceSpeechToText.PracticePhraseInfo reference =
                        FindPracticePhraseInfo(phrases, referenceIndex);
                    if (reference == null || reference.PitchCenterMidi <= 0f)
                    {
                        failure = $"pitch_plan 第 {i + 1} 项引用的清单第 {referenceIndex} 段" +
                                  "没有可用中心音高事实。";
                        return false;
                    }
                    targetCenters[i] = GetCurrentPracticeCenterMidi(
                        reference.StableId, reference.PitchCenterMidi);
                    continue;
                }
                failure = $"pitch_plan 的第 {i + 1} 项 \"{value}\" 无法理解；" +
                          "请使用 keep、source、A3、+half_step、+whole_step、+m3 或 match:1。";
                return false;
            }
            if (values.Length == 1 &&
                TryParsePitchPlanMatchToken(values[0], out int sharedReferenceIndex))
            {
                note = $"音乐目标：所选各段统一匹配清单第 {sharedReferenceIndex} 段的当前中心音高 " +
                       $"{FormatPitchMidi(targetCenters[0])}。";
            }
            else if (values.Length == 1 && TryParseMidiNote(values[0], out _))
            {
                note = $"音乐目标：所选各段统一到共同中心音高 {values[0]}。";
            }
            else
            {
                note = "音乐目标：逐段计划=" +
                       (values.Length == 1 ? values[0] : string.Join(",", values)) + "。";
            }
        }
        else if (!string.IsNullOrWhiteSpace(targetSpec))
        {
            string[] values = SplitPitchValueList(targetSpec);
            if (values.Length != 1 && values.Length != count)
            {
                failure = $"pitch_target 写了 {values.Length} 项，而 order 选择了 {count} 段；" +
                          "请写一个共同目标音，或为每段各写一项。";
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                string value = values.Length == 1 ? values[0] : values[i];
                if (IsKeepPitchToken(value)) targetCenters[i] = currentCenters[i];
                else if (!TryParseMidiNote(value, out targetCenters[i]))
                {
                    failure = $"pitch_target 的第 {i + 1} 项 \"{value}\" 不是完整音名；" +
                              "请使用 D4、D#4、Eb4 或 keep。";
                    return false;
                }
            }
            note = "音乐目标：" + (values.Length == 1
                ? $"所选各段的中心音高设为 {values[0]}"
                : $"逐段中心音高设为 {string.Join(",", values)}") + "。";
        }
        else if (!string.IsNullOrWhiteSpace(deltaSpec))
        {
            string[] values = SplitPitchValueList(deltaSpec);
            if (values.Length != 1 && values.Length != count)
            {
                failure = $"pitch_delta 写了 {values.Length} 项，而 order 选择了 {count} 段；" +
                          "请写一个共同音程，或为每段各写一项。";
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                string value = values.Length == 1 ? values[0] : values[i];
                float delta = 0f;
                if (!IsKeepPitchToken(value) && !TryParseMusicalInterval(value, out delta))
                {
                    failure = $"pitch_delta 的第 {i + 1} 项 \"{value}\" 不是有效音程；" +
                              "请使用 +whole_step、-half_step、+m3 或 keep。";
                    return false;
                }
                targetCenters[i] = currentCenters[i] + delta;
            }
            note = "音乐目标：在各段当前中心音高基础上按 " +
                   (values.Length == 1 ? values[0] : string.Join(",", values)) +
                   " 调整。";
        }
        else if (!string.IsNullOrWhiteSpace(transposeSpec))
        {
            if (!TryParseMusicalInterval(transposeSpec, out float transposeDelta))
            {
                failure = $"transpose=\"{transposeSpec}\" 不是有效音程。";
                return false;
            }
            for (int i = 0; i < count; i++)
                targetCenters[i] = currentCenters[i] + transposeDelta;
            note = $"音乐目标：在角色最近一次实际演唱基础上整体移调 {transposeSpec}。";
        }
        else
        {
            if (!int.TryParse(matchSpec, out int matchIndex) || matchIndex <= 0)
            {
                failure = $"pitch_match=\"{matchSpec}\" 不是有效的练唱清单段号。";
                return false;
            }
            SenseVoiceSpeechToText.PracticePhraseInfo reference =
                FindPracticePhraseInfo(phrases, matchIndex);
            if (reference == null || reference.PitchCenterMidi <= 0f)
            {
                failure = $"练唱清单第 {matchIndex} 段没有可用的中心音高事实，无法作为参考。";
                return false;
            }
            float referenceCenter = GetCurrentPracticeCenterMidi(
                reference.StableId, reference.PitchCenterMidi);
            for (int i = 0; i < count; i++) targetCenters[i] = referenceCenter;
            note = $"音乐目标：所选各段匹配清单第 {matchIndex} 段的当前中心音高 " +
                   $"{FormatPitchMidi(referenceCenter)}。";
        }

        directShifts = new int[count];
        var calibrationNotes = new List<string>();
        for (int i = 0; i < count; i++)
        {
            float rendererBias = 0f;
            bool calibrated = TryGetReliablePracticeRendererBias(
                stableIds[i], out rendererBias);
            float rawShift = targetCenters[i] - composition.SegmentMedians[i] -
                             (calibrated ? rendererBias : 0f);
            int shift = QuantizeCalibratedPitchShift(
                composition.SegmentMedians[i],
                targetCenters[i],
                calibrated ? rendererBias : 0f);
            if (shift < -12 || shift > 12)
            {
                int sourceIndex = composition.SegmentSourceIndices != null &&
                    i < composition.SegmentSourceIndices.Count
                        ? composition.SegmentSourceIndices[i]
                        : i + 1;
                failure = $"清单第 {sourceIndex} 段要达到 {FormatPitchMidi(targetCenters[i])} " +
                          $"需要实际移调 {rawShift:+0.##;-0.##;0} 个半音，超出转换器 ±12 半音量程；" +
                          "程序没有静默截断，本次未执行。";
                directShifts = null;
                targetCenters = null;
                return false;
            }
            directShifts[i] = shift;
            if (calibrated && Mathf.Abs(rendererBias) >= 0.05f)
            {
                int sourceIndex = composition.SegmentSourceIndices != null &&
                                  i < composition.SegmentSourceIndices.Count
                    ? composition.SegmentSourceIndices[i]
                    : i + 1;
                int uncalibratedShift = QuantizeCalibratedPitchShift(
                    composition.SegmentMedians[i], targetCenters[i], 0f);
                calibrationNotes.Add(shift != uncalibratedShift
                    ? $"清单[{sourceIndex}] 最近输出误差" +
                      $"{rendererBias:+0.00;-0.00;0.00}半音，整数移调指令由" +
                      $"{uncalibratedShift:+0;-0;0}调整为{shift:+0;-0;0}半音"
                    : $"清单[{sourceIndex}] 最近输出误差" +
                      $"{rendererBias:+0.00;-0.00;0.00}半音；受整数半音粒度限制，" +
                      $"底层指令仍为{shift:+0;-0;0}半音，未执行小数补偿");
            }
        }
        note += "程序已根据用户原始录音与角色最近一次实际演唱换算底层整数半音；" +
                "LLM 无需计算或维护 key。";
        if (calibrationNotes.Count > 0)
            note += "闭环校准：" + string.Join("；", calibrationNotes) + "。";
        return true;
    }

    private static bool HasMusicalPitchIntent(AgentHumBackRequest request)
    {
        return request != null &&
               (!string.IsNullOrWhiteSpace(request.PitchTarget) ||
                !string.IsNullOrWhiteSpace(request.PitchDelta) ||
                !string.IsNullOrWhiteSpace(request.PitchMatch) ||
                !string.IsNullOrWhiteSpace(request.PitchPlan) ||
                !string.IsNullOrWhiteSpace(request.Transpose) ||
                !string.IsNullOrWhiteSpace(request.TargetNote) ||
                request.AlignToPracticeIndex > 0);
    }

    private static SenseVoiceSpeechToText.PracticePhraseInfo FindPracticePhraseInfo(
        List<SenseVoiceSpeechToText.PracticePhraseInfo> phrases, int index)
    {
        if (phrases == null) return null;
        for (int i = 0; i < phrases.Count; i++)
            if (phrases[i] != null && phrases[i].Index == index) return phrases[i];
        return null;
    }

    private float GetCurrentPracticeCenterMidi(int stableId, float sourceCenter)
    {
        return stableId > 0 && m_PracticePitchStates.TryGetValue(
                   stableId, out PracticePitchState state)
            ? state.CenterMidi
            : sourceCenter;
    }

    private bool TryGetReliablePracticeRendererBias(int stableId, out float bias)
    {
        bias = 0f;
        if (stableId <= 0 || !m_PracticePitchStates.TryGetValue(
                stableId, out PracticePitchState state) ||
            !state.Measured || state.MeasurementWeight <= 0f)
            return false;
        float stability = state.WeightedStability / state.MeasurementWeight;
        float voiced = state.WeightedVoicedRatio / state.MeasurementWeight;
        float measuredBias = state.CenterMidi - state.ExpectedCenterMidi;
        //这是执行校准，不是音乐决策。只采用证据稳定、且仍像正常渲染误差的最近结果；
        //极端偏差留给 LLM/用户判断，避免一次 F0 八度误检把下一遍推走。
        if (stability < 0.55f || voiced < 0.35f || Mathf.Abs(measuredBias) > 2.5f)
            return false;
        bias = measuredBias;
        return true;
    }

    private static int QuantizeCalibratedPitchShift(
        float sourceCenterMidi,
        float requestedCenterMidi,
        float rendererBiasSemitones)
    {
        return Mathf.RoundToInt(
            requestedCenterMidi - sourceCenterMidi - rendererBiasSemitones);
    }

    private static string FormatPitchMidiPrecise(float midi)
    {
        if (midi <= 0f || float.IsNaN(midi) || float.IsInfinity(midi)) return "未知";
        int nearest = Mathf.RoundToInt(midi);
        int cents = Mathf.RoundToInt((midi - nearest) * 100f);
        return $"{SenseVoiceSpeechToText.MidiToNoteName(midi)}" +
               $"({midi:F2}, {cents:+0;-0;0}c)";
    }

    private static string[] SplitPitchValueList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new string[0];
        string[] values = raw.Split(
            new[] { ',', '，', '、', ';', '；' },
            StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < values.Length; i++) values[i] = values[i].Trim();
        return values;
    }

    private static bool TryParsePitchPlanMatchToken(string raw, out int practiceIndex)
    {
        practiceIndex = 0;
        var match = System.Text.RegularExpressions.Regex.Match(
            (raw ?? "").Trim(),
            @"^(?:match|same_as)\s*:\s*(?<index>\d+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success &&
               int.TryParse(match.Groups["index"].Value, out practiceIndex) &&
               practiceIndex > 0;
    }

    private static bool IsKeepPitchToken(string value)
    {
        string token = (value ?? "").Trim().ToLowerInvariant();
        return token == "keep" || token == "same" || token == "保持" ||
               token == "不变" || token == "维持" || token == "0";
    }

    private static bool IsSourcePitchToken(string value)
    {
        string token = (value ?? "").Trim().ToLowerInvariant();
        return token == "source" || token == "original" || token == "user" ||
               token == "原唱" || token == "原调" || token == "用户原唱";
    }

    /// <summary>
    /// 支持标准音程名与无歧义步进名：M2/m2/P5/P8、half_step、whole_step。
    /// 数字半音写法只为旧协议解析保留；规范化 pitch_plan/transpose 会在校验层拒绝它。
    /// </summary>
    private static bool TryParseMusicalInterval(string text, out float semitones)
    {
        semitones = 0f;
        string value = (text ?? "").Trim().Replace("−", "-");
        if (value.Length == 0) return false;
        var namedStep = System.Text.RegularExpressions.Regex.Match(
            value,
            @"^(?<sign>[+-]?)(?<name>half[_ -]?step|semitone|whole[_ -]?step)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (namedStep.Success)
        {
            bool whole = namedStep.Groups["name"].Value.StartsWith(
                "whole", StringComparison.OrdinalIgnoreCase);
            float amount = whole ? 2f : 1f;
            semitones = namedStep.Groups["sign"].Value == "-" ? -amount : amount;
            return true;
        }
        var numeric = System.Text.RegularExpressions.Regex.Match(
            value,
            @"^(?<sign>[+-]?)(?<number>\d+(?:\.\d+)?)\s*(?:st|semitones?|半音)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (numeric.Success && float.TryParse(
                numeric.Groups["number"].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float number))
        {
            semitones = numeric.Groups["sign"].Value == "-" ? -number : number;
            return true;
        }

        var interval = System.Text.RegularExpressions.Regex.Match(
            value, @"^(?<sign>[+-]?)(?<quality>[PpMmAaDd])(?<degree>[1-8])$");
        if (!interval.Success) return false;
        string quality = interval.Groups["quality"].Value;
        int degree = int.Parse(interval.Groups["degree"].Value);
        int baseSemitones;
        switch (degree)
        {
            case 1: baseSemitones = 0; break;
            case 2: baseSemitones = 2; break;
            case 3: baseSemitones = 4; break;
            case 4: baseSemitones = 5; break;
            case 5: baseSemitones = 7; break;
            case 6: baseSemitones = 9; break;
            case 7: baseSemitones = 11; break;
            case 8: baseSemitones = 12; break;
            default: return false;
        }
        bool perfectClass = degree == 1 || degree == 4 || degree == 5 || degree == 8;
        if (quality == "m")
        {
            if (perfectClass) return false;
            baseSemitones--;
        }
        else if (quality == "M")
        {
            if (perfectClass) return false;
        }
        else if (quality == "P" || quality == "p")
        {
            if (!perfectClass) return false;
        }
        else if (quality == "A" || quality == "a") baseSemitones++;
        else if (quality == "D" || quality == "d")
            baseSemitones -= perfectClass ? 1 : 2;
        else return false;
        semitones = interval.Groups["sign"].Value == "-" ? -baseSemitones : baseSemitones;
        return true;
    }

    /// <summary>
    /// +1st/+2st 同时长得像“第一个/第二个音程”和“一个/两个半音”，不再允许 LLM
    /// 在规范化接口中输出。旧 pitch_delta 仍可读旧存档，但新接口只接受明确命名。
    /// </summary>
    private static bool IsAmbiguousNumericStepToken(string text)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(
            (text ?? "").Trim().Replace("−", "-"),
            @"^[+-]?\d+(?:\.\d+)?\s*(?:st|semitones?|半音)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string FormatPitchMidi(float midi)
    {
        return midi > 0f
            ? $"{SenseVoiceSpeechToText.MidiToNoteName(midi)}({Mathf.RoundToInt(midi)})"
            : "未知";
    }

    private static float[] ShiftPitchTimeline(float[] timeline, int semitones)
    {
        if (timeline == null) return null;
        var shifted = new float[timeline.Length];
        for (int i = 0; i < timeline.Length; i++)
            shifted[i] = timeline[i] > 1f ? timeline[i] + semitones : timeline[i];
        return shifted;
    }

    /// <summary>
    /// 决定这一次要不要走"逐段各自移调"。要走的话算出每段实际发给转换服务的半音数。
    /// </summary>
    /// <remarks>
    /// 逐段送必须关掉 auto_f0_adjust，否则每段各自被拉到目标音色中心、段间差被抹平
    /// (8/20 实测抹掉约 4 个半音，正好是要保留的那一份)。关掉之后 auto-F0 本来会给的
    /// 整体抬升没有了，得自己补：它的定义就是"目标中位 − 源中位"。
    /// </remarks>
    private SenseVoiceSpeechToText.PracticeComposition ResolvePerSegmentShifts(
        float[] keyPerSegment,
        SenseVoiceSpeechToText.PracticeComposition composition,
        out int[] shifts,
        out string note)
    {
        shifts = null;
        note = "";
        if (keyPerSegment == null || keyPerSegment.Length < 2) return null;
        if (composition.SegmentWavs == null || composition.SegmentWavs.Count < 2)
        {
            note = "逐段移调未生效：这一次只有一段可唱。";
            return null;
        }
        int count = composition.SegmentWavs.Count;
        if (keyPerSegment.Length != count)
        {
            note = $"未执行：key 写了 {keyPerSegment.Length} 项，而本次 order 实际选择 {count} 段；" +
                   "程序不会猜测缺项或丢弃多项，请让两者数量完全一致。";
            return null;
        }
        if (composition.MedianMidi <= 0f)
        {
            note = "逐段移调未生效：这一次取不到音高中位数，无法计算基准移调量。";
            return null;
        }
        //把 auto-F0 那份抬升补回来。整条只算一次，各段共用，段间差因此原样保留。
        //
        //**逐段 key 的范围是 ±12，比整条 key 的 ±4 宽。**两者管的不是一回事：
        //整条 key 和 pace/expression 是同一批参数，管"这一遍想怎么唱"，±4 是审美护栏
        //（原设计注明"动到 ±4 会变得不自然"）；而逐段 key 管的是"把这几段对齐"，
        //段间差是用户唱出来的、不受任何护栏约束——8/22 实测就出现了差 6 个半音的两段，
        //±4 根本够不到，她三次写 ±6 全被静默截断，结果不对还以为是系统在自动调整。
        //±12 是变声器本身的量程(一个八度)，和服务端一致。
        //放宽的风险(把某段拽走一个八度)由结果中心音高那一行兜底：写歪了立刻看得见。
        var wide = new List<string>();
        for (int i = 0; i < keyPerSegment.Length; i++)
        {
            float capped = keyPerSegment[i];
            if (float.IsNaN(capped) || float.IsInfinity(capped) || capped < -12f || capped > 12f)
            {
                note = $"未执行：key 第 {i + 1} 项 {capped:0.##} 超出逐段范围 −12～+12；" +
                       "程序没有静默截断。";
                return null;
            }
            //超过纯五度就不再是"同一个人唱得高一点"了。不拦，但要说。
            if (Mathf.Abs(capped) > 7f) wide.Add($"第{i + 1}项 {capped:+0.#;-0.#}");
        }
        if (wide.Count > 0)
            note += $"提醒：{string.Join("、", wide)} 超过了纯五度。" +
                    "移调这么多会明显不像同一个人在唱——若只是想让几段中心音高一致，" +
                    "先确认是不是把基准段选反了；确实需要跨这么大时，" +
                    "把差值分摊到两段上（基准段往下、目标段往上）通常更自然。";

        float baseShift = m_HumSVCTargetMedianMidi - composition.MedianMidi;
        var raw = new float[count];
        float highest = float.MinValue;
        float lowest = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            float delta = i < keyPerSegment.Length ? keyPerSegment[i] : 0f;
            raw[i] = baseShift + delta + m_HumSVCSemitoneShift;
            if (raw[i] > highest) highest = raw[i];
            if (raw[i] < lowest) lowest = raw[i];
        }
        //转换服务把 semitone_shift 截到 ±12。逐个截会**改变段与段之间的差**——而段间差
        //正是逐段移调唯一要保住的东西。所以整组一起平移，让最出格的那个落进范围内：
        //宁可整体调没到位，也不能让"把第二段抬高 4 个半音"变成抬高 2 个。
        float slide = 0f;
        if (highest > 12f) slide = 12f - highest;
        else if (lowest < -12f) slide = -12f - lowest;
        shifts = new int[count];
        for (int i = 0; i < count; i++)
            shifts[i] = Mathf.Clamp(Mathf.RoundToInt(raw[i] + slide), -12, 12);
        //没触及上限时也要说一句。8/22 实测：用户问「为什么第六段调不下去」，
        //她答「移調には限界がある、声域に合わせて自動調整していた」——完全是编的：
        //那次实发 4、5、3，离 ±12 远得很，一次都没截断。真实原因是她自己写的
        //key 里给基准段也加了偏移。工具结果里必须留下有没有触及上限这个事实，
        //否则事后被问起时她手上什么都没有，只能编。
        //两层上限都没碰到才能这么说。key 那一层(±4)在上面刚判过，
        //这里再确认 semitone_shift 那一层(±12)也没滑动。少判一层就会变成谎话。
        if (Mathf.Abs(slide) <= 0.01f)
            note += "（本次没有触及任何移调上限，各段实发值就是你写的 key 加同一个基准量；" +
                    "若结果不如预期，原因在 key 本身，不是系统限制。）";
        if (Mathf.Abs(slide) > 0.01f)
            note += $"注意：把音域拉进你声线需要 {baseShift:+0.0;-0.0} 个半音，" +
                    $"加上你要的偏移之后超出了转换上限，整组已一起下调 {Mathf.Abs(slide):0.0} 个半音。" +
                    "段与段之间的高低差仍然是你指定的，但整体调门比正常低一些。";
        if (m_LogHumBack)
            Debug.Log($"[HumBack/PerSegment] 段数={count} 源中位={composition.MedianMidi:F2} " +
                      $"目标中位={m_HumSVCTargetMedianMidi:F2} 基准={baseShift:+0.00;-0.00} " +
                      $"实发={string.Join(",", shifts)}");
        return composition;
    }

    /// <summary>
    /// 普通整曲流式转换也必须关闭每块各自的 auto-F0，否则每一块都会被单独拉回
    /// 目标中心、歌曲内部的高低关系就变了。这里按整首的中位数只算一次，再把同一个
    /// 移调值发给所有块，听感与旧的“整条一次转换”保持一致。
    /// </summary>
    private int[] BuildUniformStreamingShifts(
        SenseVoiceSpeechToText.PracticeComposition composition,
        int performanceOffset)
    {
        int count = composition != null && composition.SegmentWavs != null
            ? composition.SegmentWavs.Count
            : 0;
        if (count <= 0) return null;
        float autoShift = m_HumSVCAutoF0Adjust && composition.MedianMidi > 0f
            ? m_HumSVCTargetMedianMidi - composition.MedianMidi
            : 0f;
        int shift = Mathf.Clamp(
            Mathf.RoundToInt(autoShift) + performanceOffset + m_HumSVCSemitoneShift,
            -12,
            12);
        var shifts = new int[count];
        for (int i = 0; i < count; i++) shifts[i] = shift;
        if (m_LogHumBack)
            Debug.Log($"[HumBack/Stream] 统一移调 段数={count} 源中位={composition.MedianMidi:F2} " +
                      $"auto={autoShift:+0.00;-0.00;0.00} performance={performanceOffset:+0;-0;0} " +
                      $"global={m_HumSVCSemitoneShift:+0;-0;0} 实发={shift:+0;-0;0}");
        return shifts;
    }

    /// <summary>
    /// 没有新的音高目标时，不替 LLM 猜一个新目标：若角色已经唱过这些稳定片段，
    /// 就沿用各段最近一次音乐目标，并用可靠的角色输出 F0 偏差重新求解底层整数指令；
    /// 从未唱过的段才用初次音域适配。
    /// </summary>
    private bool TryBuildCurrentPracticeArrangement(
        SenseVoiceSpeechToText senseVoice,
        SenseVoiceSpeechToText.PracticeComposition composition,
        int performanceOffset,
        out int[] shifts,
        out float[] requestedCenters,
        out string note)
    {
        shifts = null;
        requestedCenters = null;
        note = "";
        if (senseVoice == null || composition == null ||
            composition.SegmentWavs == null || composition.SegmentMedians == null ||
            composition.SegmentWavs.Count == 0 ||
            composition.SegmentMedians.Count != composition.SegmentWavs.Count)
            return false;

        List<SenseVoiceSpeechToText.PracticePhraseInfo> phrases =
            senseVoice.DescribePracticePhrases();
        int count = composition.SegmentWavs.Count;
        var stableIds = new int[count];
        bool hasCurrentSegment = false;
        for (int i = 0; i < count; i++)
        {
            int sourceIndex = composition.SegmentSourceIndices != null &&
                              i < composition.SegmentSourceIndices.Count
                ? composition.SegmentSourceIndices[i]
                : i + 1;
            SenseVoiceSpeechToText.PracticePhraseInfo phrase =
                FindPracticePhraseInfo(phrases, sourceIndex);
            stableIds[i] = phrase != null ? phrase.StableId : 0;
            if (stableIds[i] > 0 && m_PracticePitchStates.ContainsKey(stableIds[i]))
                hasCurrentSegment = true;
        }
        if (!hasCurrentSegment) return false;

        int[] initialShifts = BuildUniformStreamingShifts(composition, performanceOffset);
        if (initialShifts == null || initialShifts.Length != count) return false;
        shifts = new int[count];
        requestedCenters = new float[count];
        var carried = new List<string>();
        var initialized = new List<string>();
        var calibrated = new List<string>();
        var calibrationLimited = new List<string>();
        for (int i = 0; i < count; i++)
        {
            int sourceIndex = composition.SegmentSourceIndices != null &&
                              i < composition.SegmentSourceIndices.Count
                ? composition.SegmentSourceIndices[i]
                : i + 1;
            if (stableIds[i] > 0 && m_PracticePitchStates.TryGetValue(
                    stableIds[i], out PracticePitchState state))
            {
                float requestedCenter = state.RequestedCenterMidi > 0f
                    ? state.RequestedCenterMidi
                    : state.CenterMidi;
                bool hasReliableBias = TryGetReliablePracticeRendererBias(
                    stableIds[i], out float rendererBias);
                int carriedShift = ResolveCarriedPracticeShift(
                    composition.SegmentMedians[i],
                    requestedCenter,
                    state.ExecutedShift,
                    hasReliableBias,
                    rendererBias);
                if (carriedShift < -12 || carriedShift > 12)
                {
                    //上一条指令已经通过量程检查；闭环修正越界时宁可保持它，也不能把
                    //新值静默截断成另一个目标。把限制写进工具结果供角色如实判断。
                    calibrationLimited.Add(
                        $"[{sourceIndex}] 输出偏差{rendererBias:+0.00;-0.00;0.00}半音" +
                        $"需要底层{carriedShift:+0;-0;0}半音，超出±12，" +
                        $"本轮保留上次{state.ExecutedShift:+0;-0;0}半音");
                    carriedShift = state.ExecutedShift;
                }
                else if (hasReliableBias && Mathf.Abs(rendererBias) >= 0.05f)
                {
                    calibrated.Add(carriedShift != state.ExecutedShift
                        ? $"[{sourceIndex}] 根据最近输出偏差" +
                          $"{rendererBias:+0.00;-0.00;0.00}半音，把底层指令由" +
                          $"{state.ExecutedShift:+0;-0;0}校准为{carriedShift:+0;-0;0}半音"
                        : $"[{sourceIndex}] 最近输出偏差" +
                          $"{rendererBias:+0.00;-0.00;0.00}半音，受整数半音粒度限制，" +
                          $"底层指令保持{carriedShift:+0;-0;0}半音");
                }
                shifts[i] = carriedShift;
                requestedCenters[i] = requestedCenter;
                carried.Add($"[{sourceIndex}]");
            }
            else
            {
                shifts[i] = initialShifts[i];
                requestedCenters[i] = composition.SegmentMedians[i] + shifts[i];
                initialized.Add($"[{sourceIndex}]");
            }
        }
        note = "本轮没有提交新的音高目标；程序沿用当前编排中各段最近一次音乐目标" +
               $"（{string.Join("、", carried)}）" +
               (initialized.Count > 0
                   ? $"；从未演唱过的段（{string.Join("、", initialized)}）使用初次音域适配"
                   : "") + "。" +
               (calibrated.Count > 0
                   ? "闭环校准：" + string.Join("；", calibrated) + "。"
                   : "") +
               (calibrationLimited.Count > 0
                   ? "闭环校准受量程限制：" + string.Join("；", calibrationLimited) + "。"
                   : "");
        return true;
    }

    private static int ResolveCarriedPracticeShift(
        float sourceCenterMidi,
        float requestedCenterMidi,
        int previousExecutedShift,
        bool hasReliableRendererBias,
        float rendererBiasSemitones)
    {
        return hasReliableRendererBias
            ? QuantizeCalibratedPitchShift(
                sourceCenterMidi, requestedCenterMidi, rendererBiasSemitones)
            : previousExecutedShift;
    }

    /// <summary>
    /// 单个学习片段本身也可能是一整首长录音。把超过单块硬保护时长的项继续拆小，并让
    /// 子块继承原段的移调、来源段号；只有第一个子块保留原段前的呼吸停顿。
    /// </summary>
    private void ExpandOversizedStreamingSegments(
        SenseVoiceSpeechToText senseVoice,
        SenseVoiceSpeechToText.PracticeComposition composition,
        ref int[] shifts)
    {
        if (senseVoice == null || composition == null || composition.SegmentWavs == null ||
            shifts == null || shifts.Length != composition.SegmentWavs.Count)
            return;

        var wavs = new List<byte[]>();
        var gaps = new List<float>();
        var medians = new List<float>();
        var sources = new List<int>();
        var expandedShifts = new List<int>();
        bool expanded = false;
        for (int i = 0; i < composition.SegmentWavs.Count; i++)
        {
            byte[] wav = composition.SegmentWavs[i];
            bool splitOk = senseVoice.TryBuildSingingStreamChunks(
                wav,
                null,
                composition.FrameSeconds,
                m_HumBackMaxSeconds,
                out SenseVoiceSpeechToText.PracticeComposition split,
                out string splitFailure);
            int childCount = splitOk && split != null && split.SegmentWavs != null
                ? split.SegmentWavs.Count
                : 0;
            if (childCount <= 1)
            {
                wavs.Add(wav);
                gaps.Add(composition.Gaps != null && i < composition.Gaps.Count
                    ? composition.Gaps[i] : 0f);
                medians.Add(composition.SegmentMedians != null &&
                            i < composition.SegmentMedians.Count
                    ? composition.SegmentMedians[i]
                    : 0f);
                sources.Add(composition.SegmentSourceIndices != null &&
                            i < composition.SegmentSourceIndices.Count
                    ? composition.SegmentSourceIndices[i] : i + 1);
                expandedShifts.Add(shifts[i]);
                if (!splitOk && m_LogHumBack)
                    Debug.LogWarning($"[HumBack/Stream] 第{i + 1}段分块检查失败，保留原段: {splitFailure}");
                continue;
            }

            expanded = true;
            for (int child = 0; child < childCount; child++)
            {
                wavs.Add(split.SegmentWavs[child]);
                gaps.Add(child == 0 && composition.Gaps != null && i < composition.Gaps.Count
                    ? composition.Gaps[i] : 0f);
                //子块的旋律区间可能与整段中心相差很多。保留子块自己的实测中位数，
                //这样播放后的 F0 回测是在比较“本块实际值 vs 本块预计值”，不会把
                //正常旋律走向误报成移调偏差。
                medians.Add(split.SegmentMedians != null &&
                            child < split.SegmentMedians.Count
                    ? split.SegmentMedians[child]
                    : (composition.SegmentMedians != null &&
                       i < composition.SegmentMedians.Count
                        ? composition.SegmentMedians[i]
                        : 0f));
                sources.Add(composition.SegmentSourceIndices != null &&
                            i < composition.SegmentSourceIndices.Count
                    ? composition.SegmentSourceIndices[i] : i + 1);
                expandedShifts.Add(shifts[i]);
            }
        }
        if (!expanded) return;
        composition.SegmentWavs = wavs;
        composition.Gaps = gaps;
        composition.SegmentMedians = medians;
        composition.SegmentSourceIndices = sources;
        shifts = expandedShifts.ToArray();
        if (m_LogHumBack)
            Debug.Log($"[HumBack/Stream] 超长源段已进一步拆成 {wavs.Count} 个转换块，" +
                      $"单块保护上限 {m_HumBackMaxSeconds:F0}s");
    }

    //上一次逐段 key 写了什么。8/26 实测这是必须回报的：她 key="2,0,2" 一次把
    //三段调齐到 D#4，用户说"第三段再抬半个音"，她写了 key="0,0,1"——想的是增量，
    //而 key 是相对**原始录音**的绝对值，于是第 1、2 段被打回原形，三段又散了。
    //她的 reason 写着「第一段和第二段保持原样」，意图清楚，是语义没人告诉她。
    //正确写法是 2,0,3。
    private string m_LastPerSegmentKeyText = "";
    private string m_PendingPerSegmentKeyText = "";

    //上一次 practice 合成实际用的顺序，回报给她时如实写出来。
    private string m_LastPracticeOrderUsed = "练唱先后";
    //本次是否把"同一句的多遍"一起唱了出去；非空时附在工具结果后面。
    private string m_LastPracticeDuplicateNote = "";

    private class AgentSongSingRequest
    {
        public bool UnifiedSing;
        public string SongId = "";
        public string Title = "";
        public string Mode = "memory";
        public string Source = "";
        public string SourceValidationError = "";
        public string Reason = "";
        // song_sing 不消费段序；保留它只为识别模型把 practice 参数写错了工具。
        public string Order = "";
        //只唱某一段时填这一段的歌词。空 = 维持 mode 的既有语义。
        public string SegmentLyrics = "";
        //模型偶尔把当前练唱素材误写成 song_sing。兼容转路由时必须保留它已经表达的
        //音乐目标和演绎参数，不能只保留 order 后静默丢掉 pitch_plan / transpose。
        public AgentHumBackRequest HumBackCompatibleRequest = null;
    }

    private static readonly System.Text.RegularExpressions.Regex s_SongSingTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<song_sing\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static AgentSongSingRequest ExtractSongSingTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        AgentSongSingRequest unifiedSong = ExtractUnifiedLibrarySing(ref text);
        if (unifiedSong != null) return unifiedSong;
        var match = s_SongSingTagRegex.Match(text);
        if (!match.Success) return null;
        string attrs = match.Groups["attrs"].Value;
        AgentSongSingRequest request = new AgentSongSingRequest
        {
            SongId = ReadToolAttribute(attrs, "id"),
            Title = ReadToolAttribute(attrs, "title"),
            Mode = ReadToolAttribute(attrs, "mode"),
            Source = ReadToolAttribute(attrs, "source"),
            Reason = ReadToolAttribute(attrs, "reason"),
            Order = ReadToolAttribute(attrs, "order"),
            SegmentLyrics = ReadToolAttribute(attrs, "lyrics"),
            HumBackCompatibleRequest = ParseHumBackCompatibleAttributes(attrs),
        };
        if (string.IsNullOrWhiteSpace(request.Mode)) request.Mode = "memory";
        string normalizedSource = NormalizeSingingMaterialSource(request.Source);
        if (!string.IsNullOrWhiteSpace(request.Source) && normalizedSource.Length == 0)
            request.SourceValidationError =
                $"source=\"{request.Source}\" 不是已知歌唱素材源；" +
                "只能使用 recent_turn、practice 或 library。";
        else request.Source = normalizedSource;
        text = s_SongSingTagRegex.Replace(text, "").Trim();
        return request;
    }

    private static readonly System.Text.RegularExpressions.Regex s_HumBackTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<(?<tool>hum_back|sing)\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    //她会把两个标签粘成一个词写出来，最典型的是 <silenthum_back mode="practice" …/>。
    //8/17 实测出现 4 次，每次都因为标签系统不认识而被整条剥掉——什么都没执行，
    //而她以为自己唱了(其中一次她自己发现了，下一句写着"刚才的标签写错了")。
    //两半都是精确的已知标签名时意图毫无歧义，拆开即可；不做任何拼写猜测。
    private static readonly string[] s_GluableTagNames =
    {
        "silent", "continue", "noop", "look", "unlook", "next", "skill_request", "skill_control", "motion",
        "singing_policy", "singing_stop", "singing_intent",
        "note", "memory_add", "memory_update", "memory_link", "speaker_name", "speaker_manage",
        "song_search", "song_catalog", "song_remember", "song_rename", "song_forget", "song_sing",
        "practice_confirm", "practice_revise", "practice_drop",
        "hum_back", "sing", "clip_confirm", "clip_revise", "clip_drop",
    };

    private static readonly System.Text.RegularExpressions.Regex s_GluedTagRegex =
        BuildGluedTagRegex();

    private static System.Text.RegularExpressions.Regex BuildGluedTagRegex()
    {
        string names = string.Join("|", s_GluableTagNames);
        //<前缀标签名 + 后缀标签名 —— 两者都必须是完整的已知名字
        return new System.Text.RegularExpressions.Regex(
            @"<(?<head>" + names + @")(?<tail>" + names + @")\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    //**唯一一张别名表，加进来是有门槛的。**
    //规则：只收「已经在实测里反复出现、且映射毫无歧义」的写法；不做通用拼写猜测
    //（那条线在 s_GluableTagNames 的注释里定过，仍然有效）。
    //silence→silent：8/22 单场出现 43 次。不认它的后果不是"少做一件事"，而是
    //本该无声的内心独白被 TTS 念出来——那一场用户听见了她关于 MIDI 数值的自言自语。
    //检测器仍会照常点破，让她自己改过来；别名只保证这一轮不出洋相。
    private static readonly string[,] s_TagAliases =
    {
        { "silence", "silent" },
    };

    private static string ApplyTagAliases(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text;
        for (int i = 0; i < s_TagAliases.GetLength(0); i++)
        {
            string wrong = s_TagAliases[i, 0];
            string right = s_TagAliases[i, 1];
            //**只映射不带属性的写法。**`<silence/>` 的意思毫无歧义就是 `<silent/>`；
            //但 `<silence in="2s" focus="…"/>` 带着 next 的属性，意图是排下一拍，
            //一并映射成 silent 会把排程意图悄悄吞掉——那正是"拼写猜测"会犯的错。
            //带属性的那种交给检测器点破，由她自己改。
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                @"<\s*" + wrong + @"\s*(?<slash>/?)\s*>",
                m => "<" + right + m.Groups["slash"].Value + ">",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return text;
    }

    private static string SplitGluedAgentTags(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0) return text;
        text = ApplyTagAliases(text);
        return s_GluedTagRegex.Replace(
            text, m => "<" + m.Groups["head"].Value + "/><" + m.Groups["tail"].Value);
    }

    //她偶尔会写出根本不存在的标签名，最典型的是 <silence in="2s" focus="专注"/>
    //——把 <silent/> 和 <next in="Ns"/> 揉成了一个词。剥标签是黑名单式的，所以它被
    //整条丢掉：那一轮既没排下一拍、也没有任何提示，8/20 实测就这样白跑了一轮
    //(她当时已经正确判断出该排除第5段，结论却没能变成动作)。
    //
    //**不做拼写猜测**——这是 s_GluableTagNames 那里定下的规则：只有两半都是精确的
    //已知名字时才敢拆。给 silence 加个别名等于开始猜，下次是 silense、pause、wait，
    //别名表会没完没了，而且猜错时她永远不知道。改成告诉她写错了，她自己会改。
    private static readonly System.Text.RegularExpressions.Regex s_AnyOpenTagRegex =
        new System.Text.RegularExpressions.Regex(
            //属性里不许再出现 < ，且必须自闭合。少了这两条，普通文本里的 "a<b。" 会把
            //后面那个真标签一起吞进来，报一个根本不存在的错。规范里的标签全是自闭合的。
            @"<\s*(?<name>[A-Za-z_][A-Za-z0-9_.:-]*)\b[^<>]*/>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string DetectUnknownAgentTag(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.IndexOf('<') < 0) return "";
        //粘连标签(<silenthum_back …/>)有专门的机制会拆开并正常执行，不该在这里报错。
        raw = SplitGluedAgentTags(raw);
        foreach (System.Text.RegularExpressions.Match m in s_AnyOpenTagRegex.Matches(raw))
        {
            string name = m.Groups["name"].Value;
            //<think> 由 StripLeadingThinkBlock 单独处理，不算写错。
            if (string.Equals(name, "think", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsKnownAgentTagName(name)) continue;
            string text = m.Value;
            if (text.Length > 60) text = text.Substring(0, 60) + "…";
            return text;
        }
        return "";
    }

    //来源待确认候选由正式角色显式确认。这个工具只改变“是谁唱的”这一项事实，
    //不替角色决定是否播放，也不把 evidence_only 硬升级成 ready。
    private static readonly System.Text.RegularExpressions.Regex s_PracticeConfirmTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<(?:practice_confirm|clip_confirm)\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private void ExtractAndApplyPracticeConfirmTag(ref string text, bool apply = true)
    {
        ExtractAndApplyClipConfirmTags(ref text, apply);
        if (string.IsNullOrEmpty(text)) return;
        var matches = s_PracticeConfirmTagRegex.Matches(text);
        if (matches.Count == 0) return;
        text = s_PracticeConfirmTagRegex.Replace(text, "").Trim();
        if (!apply) return;

        SenseVoiceSpeechToText sense = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (sense == null)
        {
            RecordPracticeEditFailureForLlm(
                "未执行来源确认：语音模块不可用。",
                "speech_module_unavailable",
                "不要声称已确认或已播放；可以如实说明当前无法读取素材，或稍后再试。");
            return;
        }
        var ids = new List<int>();
        var captureSelections = new Dictionary<int, string>();
        for (int i = 0; i < matches.Count; i++)
        {
            string attrs = matches[i].Groups["attrs"].Value;
            string raw = ReadToolAttribute(attrs, "candidate_id") ??
                         ReadToolAttribute(attrs, "id");
            foreach (int id in ParsePositiveIdList(raw))
            {
                if (!ids.Contains(id)) ids.Add(id);
                if (!string.IsNullOrWhiteSpace(ReadToolAttribute(attrs, "capture")))
                    captureSelections[id] = attrs;
            }
        }
        if (ids.Count == 0)
        {
            RecordPracticeEditFailureForLlm(
                "未执行来源确认：practice_confirm 缺少有效 candidate_id。",
                "missing_candidate_id",
                "只可填写感知帧当前列出的 pending candidate_id；若目标已经在练唱清单，" +
                "它不需要再次确认，直接用 hum_back practice 的 stable:N 播放引用。 ");
            return;
        }
        var alreadyConfirmedStableIds = new Dictionary<int, int>();
        for (int i = 0; i < ids.Count; i++)
        {
            if (sense.HasKnownSingingCandidate(ids[i]))
            {
                if (sense.TryResolveConfirmedSingingCandidate(
                        ids[i], out int knownStableId, out int _))
                    alreadyConfirmedStableIds[ids[i]] = knownStableId;
                continue;
            }
            string readyHint = sense.TryResolvePracticeStableId(
                    ids[i], out int readyIndex)
                ? $"同号的 stable_id={ids[i]} 确实存在于练唱清单第 {readyIndex} 段，" +
                  $"其状态是 playback=ready / confirmation=not_required；若你指的是它，" +
                  $"请用 <hum_back mode=\"practice\" source=\"practice\" " +
                  $"order=\"stable:{ids[i]}\"/>。"
                : "请重新读取当前 pending candidate_id 与 ready stable_id；" +
                  "拿不准用户指哪份素材时可以先询问。";
            RecordPracticeEditFailureForLlm(
                $"未执行来源确认：candidate_id={ids[i]} 既不在当前隔离候选中，" +
                "也不存在已确认的历史映射；本轮没有改变任何候选。",
                "candidate_not_found",
                readyHint + " 不要仅口头承诺播放；决定现在唱时，同一回复还必须提交 hum_back。 ");
            return;
        }

        var mappings = new List<string>();
        for (int i = 0; i < ids.Count; i++)
        {
            if (captureSelections.TryGetValue(ids[i], out string selection) &&
                !alreadyConfirmedStableIds.ContainsKey(ids[i]))
            {
                float head = ReadToolFloatAttributeUnclamped(selection, "trim_head_seconds");
                float tail = ReadToolFloatAttributeUnclamped(selection, "trim_tail_seconds");
                if (string.IsNullOrWhiteSpace(ReadToolAttribute(selection, "trim_head_seconds"))) head = 0f;
                if (string.IsNullOrWhiteSpace(ReadToolAttribute(selection, "trim_tail_seconds"))) tail = 0f;
                if (!sense.PrepareQuarantinedCaptureForConfirmation(ids[i],
                    ReadToolAttribute(selection, "capture"), head, tail, out string recoveryError))
                {
                    RecordPracticeEditFailureForLlm(recoveryError, "candidate_recovery_failed",
                        "原录音保留，可重新选择范围或询问用户；没有替换成其它素材。");
                    return;
                }
            }
            if (!sense.ConfirmQuarantinedSingingCandidateWithPlaybackStatus(
                    ids[i], out int phraseIndex, out string playbackStatus))
            {
                RecordPracticeEditFailureForLlm(
                    $"来源确认执行失败：candidate_id={ids[i]}；其余已完成结果保持。",
                    "candidate_confirmation_failed",
                    "重新读取工具结果中的已完成映射和当前候选；不要重复猜编号。" +
                    "可用当前正确身份重试，也可先询问用户。");
                return;
            }
            bool idempotent = alreadyConfirmedStableIds.TryGetValue(
                ids[i], out int priorStableId);
            mappings.Add(phraseIndex > 0
                ? idempotent
                    ? $"candidate_id={ids[i]} 早已确认并消费→stable_id={priorStableId}" +
                      $"（当前清单第 {phraseIndex} 段；本次为幂等 no-op）"
                    : $"candidate_id={ids[i]}→stable_id=" +
                      ResolvePracticeStableIdAtIndex(sense, phraseIndex) +
                      $"（当前清单第 {phraseIndex} 段）"
                : $"candidate_id={ids[i]} source=confirmed_user, playback={playbackStatus}");
        }
        RecordPracticeEditResult(
            "成功：正式角色根据完整语境确认了素材来源：" + string.Join("；", mappings) +
            "。这不代表已经播放；是否演唱仍由角色决定。", false);
    }

    private static int ResolvePracticeStableIdAtIndex(
        SenseVoiceSpeechToText sense, int index1Based)
    {
        if (sense == null || index1Based <= 0) return 0;
        List<SenseVoiceSpeechToText.PracticePhraseInfo> phrases =
            sense.DescribePracticePhrases();
        return index1Based <= phrases.Count && phrases[index1Based - 1] != null
            ? phrases[index1Based - 1].StableId : 0;
    }

    //“修复/重切”是非破坏性编辑，不得翻译成 practice_drop。每次都从不可变原始
    //clean/expanded 生成新版本，成功后才替换当前可播放版本。
    private static readonly System.Text.RegularExpressions.Regex s_PracticeReviseTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<(?:practice_revise|clip_revise)\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private void ExtractAndApplyPracticeReviseTag(ref string text, bool apply = true)
    {
        ExtractAndApplyClipReviseTags(ref text, apply);
        if (string.IsNullOrEmpty(text)) return;
        var matches = s_PracticeReviseTagRegex.Matches(text);
        if (matches.Count == 0) return;
        text = s_PracticeReviseTagRegex.Replace(text, "").Trim();
        if (!apply) return;
        SenseVoiceSpeechToText sense = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (sense == null)
        {
            RecordPracticeEditResult("未执行素材修订：语音模块不可用。", true);
            return;
        }

        var completed = new List<string>();
        for (int i = 0; i < matches.Count; i++)
        {
            string attrs = matches[i].Groups["attrs"].Value;
            if (!int.TryParse(ReadToolAttribute(attrs, "stable_id"), out int stableId) ||
                stableId <= 0)
            {
                RecordPracticeEditResult(
                    "未执行素材修订：practice_revise 必须用感知帧中的 stable_id 指定素材；" +
                    "清单 order 会前移，不能用于跨轮修复。", true);
                return;
            }
            string capture = ReadToolAttribute(attrs, "capture") ?? "clean";
            float trimHead = ReadToolFloatAttributeUnclamped(attrs, "trim_head_seconds");
            float trimTail = ReadToolFloatAttributeUnclamped(attrs, "trim_tail_seconds");
            if (float.IsNaN(trimHead)) trimHead = 0f;
            if (float.IsNaN(trimTail)) trimTail = 0f;
            bool excludeSpeech = ReadToolBoolAttribute(attrs, "exclude_speech", false);
            if (!sense.TryRevisePracticePhrase(
                    stableId, capture, trimHead, trimTail, excludeSpeech,
                    out string result, out string failure))
            {
                RecordPracticeEditResult(
                    "未执行素材修订：" + failure +
                    (completed.Count > 0
                        ? "；此前已经成功完成：" + string.Join("；", completed)
                        : ""), true);
                return;
            }
            //边界版本变化后，旧的角色演唱音高回测不再能无条件代表这份新版本。
            m_PracticePitchStates.Remove(stableId);
            completed.Add(result);
        }
        RecordPracticeEditResult("成功：" + string.Join("；", completed), false);
    }

    private static List<int> ParsePositiveIdList(string raw)
    {
        var result = new List<int>();
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(raw ?? "", @"\d+"))
            if (int.TryParse(match.Value, out int id) && id > 0 && !result.Contains(id))
                result.Add(id);
        return result;
    }

    //长期曲库那一层早就有 <song_forget/>，练唱会话这一层一直没有对称物：
    //只能整场重置或者到上限时挤掉最老的。于是一段被误收的说话、或者用户唱错想撤回的
    //那一遍，会一直挂在清单里，还会被默认顺序原样唱出去。
    private static readonly System.Text.RegularExpressions.Regex s_PracticeDropTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<(?:practice_drop|clip_drop)\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// 摘出并**立即执行**本轮全部 <c>&lt;practice_drop order="N"/&gt;</c>。
    /// </summary>
    /// <remarks>
    /// 就地做完而不排队：删段是纯本地的列表操作，没有任何 IO。
    /// 同轮多个标签先以删除前的清单原子校验，再按段号倒序执行；这样既不会因
    /// 前一次删除造成后续段号错位，也不会让后面的 hum_back 拿到删除前的段号。
    /// </remarks>
    /// <param name="apply">
    /// false 时只把标签从正文剥掉、不真的删段。复读轮走这条路——
    /// 删除是**成功**动作，失败重试拦截管不到它，而段号每删一次就前移一次，
    /// 8/25 实测同一句话说四遍把练唱会话从 5 段削到了 1 段。
    /// </param>
    private void ExtractAndApplyPracticeDropTag(ref string text, bool apply = true)
    {
        ExtractAndApplyClipDropTags(ref text, apply);
        if (string.IsNullOrEmpty(text)) return;
        var matches = s_PracticeDropTagRegex.Matches(text);
        if (matches.Count == 0) return;
        text = s_PracticeDropTagRegex.Replace(text, "").Trim();
        if (!apply)
        {
            if (m_LogAgentLoop)
                Debug.LogWarning("[Agent/复读] practice_drop 未执行");
            return;
        }

        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (senseVoice == null)
        {
            RecordPracticeDropResult("未执行：语音模块不可用，练唱会话没有变化。", true);
            return;
        }
        List<SenseVoiceSpeechToText.PracticePhraseInfo> snapshot =
            senseVoice.DescribePracticePhrases();
        var stableIds = new List<int>(matches.Count);
        var originalIndexByStableId = new Dictionary<int, int>();
        var reasonByStableId = new Dictionary<int, string>();
        string validationFailure = "";
        for (int i = 0; i < matches.Count; i++)
        {
            string attrs = matches[i].Groups["attrs"].Value;
            string rawStable = (ReadToolAttribute(attrs, "stable_id") ?? "").Trim();
            string rawOrder = (ReadToolAttribute(attrs, "order") ?? "").Trim();
            string reason = (ReadToolAttribute(attrs, "reason") ?? "").Trim();
            int originalIndex = -1;
            int stableId = 0;
            if (rawStable.Length > 0)
            {
                if (!int.TryParse(rawStable, out stableId) || stableId <= 0)
                {
                    validationFailure = $"stable_id=\"{rawStable}\" 不是有效稳定身份";
                    break;
                }
                for (int p = 0; p < snapshot.Count; p++)
                    if (snapshot[p] != null && snapshot[p].StableId == stableId)
                    {
                        originalIndex = p + 1;
                        break;
                    }
                if (originalIndex < 0)
                {
                    validationFailure = $"stable_id={stableId} 不在删除前练唱清单中";
                    break;
                }
                if (rawOrder.Length > 0 &&
                    (!int.TryParse(rawOrder, out int assertedOrder) ||
                     assertedOrder != originalIndex))
                {
                    validationFailure = $"stable_id={stableId} 当前是第 {originalIndex} 段，" +
                                        $"与同时给出的 order=\"{rawOrder}\" 不一致";
                    break;
                }
            }
            else
            {
                if (!int.TryParse(rawOrder, out originalIndex) ||
                    originalIndex < 1 || originalIndex > snapshot.Count)
                {
                    validationFailure = $"order=\"{rawOrder}\" 不是删除前清单中的有效段号";
                    break;
                }
                stableId = snapshot[originalIndex - 1].StableId;
            }
            if (!stableIds.Contains(stableId)) stableIds.Add(stableId);
            originalIndexByStableId[stableId] = originalIndex;
            if (!string.IsNullOrWhiteSpace(reason)) reasonByStableId[stableId] = reason;
        }
        if (stableIds.Count == 0 || !string.IsNullOrEmpty(validationFailure))
        {
            RecordPracticeDropResult(
                "未执行：" + validationFailure + "。本轮全部 practice_drop 都未执行，" +
                "练唱会话没有变化。", true);
            return;
        }

        //日志仍按删除前段号倒序显示；执行本身使用 stable_id，清单前移不再改变目标。
        stableIds.Sort((left, right) =>
            originalIndexByStableId[right].CompareTo(originalIndexByStableId[left]));

        var droppedParts = new List<string>(stableIds.Count);
        int remaining = snapshot.Count;
        for (int i = 0; i < stableIds.Count; i++)
        {
            int stableId = stableIds[i];
            int originalIndex = originalIndexByStableId[stableId];
            if (!senseVoice.DropPracticePhraseByStableId(
                    stableId, out string dropped, out remaining,
                    out string failure))
            {
                string partial = droppedParts.Count > 0
                    ? "部分执行：已去掉" + string.Join("、", droppedParts) + "；"
                    : "未执行：";
                RecordPracticeDropResult(
                    partial + $"处理 stable_id={stableId}（删除前第 {originalIndex} 段）时失败：" +
                    failure + "。" +
                    $"练唱会话当前还剩 {senseVoice.PracticePhraseCount} 段。", true);
                return;
            }
            string part = $"stable_id={stableId}（删除前第 {originalIndex} 段，{dropped}）";
            if (reasonByStableId.TryGetValue(stableId, out string reason))
                part += $"〔理由：{reason}〕";
            droppedParts.Add(part);
        }
        //段号会前移，必须点破。不说的话她下一句还按旧段号写 order，唱出来的是别的段。
        string note = $"成功：已从练唱会话去掉{string.Join("、", droppedParts)}。" +
                      $"现在还剩 {remaining} 段";
        note += remaining > 0
            ? "，显示段号已经整体前移；后续跨轮修正请继续使用 stable_id。"
            : "，练唱会话已经空了。";
        RecordPracticeDropResult(note, false);
    }

    /// <summary>
    /// 以同一份删除前清单校验批量段号，并返回去重后的倒序执行顺序。
    /// 返回空数组表示整批拒绝，调用方不得做任何删除。
    /// </summary>
    private static int[] NormalizePracticeDropIndices(
        int[] requestedIndices,
        int originalCount,
        out string failure)
    {
        failure = "";
        if (requestedIndices == null || requestedIndices.Length == 0)
        {
            failure = "没有提供要删除的练唱段号";
            return Array.Empty<int>();
        }
        if (originalCount <= 0)
        {
            failure = "练唱会话本来就是空的";
            return Array.Empty<int>();
        }

        var unique = new List<int>(requestedIndices.Length);
        for (int i = 0; i < requestedIndices.Length; i++)
        {
            int index = requestedIndices[i];
            if (index == int.MinValue)
            {
                failure = "至少一个 order 不是整数段号；请照感知帧清单填写";
                return Array.Empty<int>();
            }
            if (index < 1 || index > originalCount)
            {
                failure = $"删除前练唱清单只有 {originalCount} 段，order=\"{index}\" 超出范围";
                return Array.Empty<int>();
            }
            if (!unique.Contains(index)) unique.Add(index);
        }
        unique.Sort((left, right) => right.CompareTo(left));
        return unique.ToArray();
    }

    private string m_LastPracticeDropResult = "";
    private bool m_PracticeDropResultPending = false;
    private string m_LastPracticeEditResult = "";
    private bool m_PracticeEditResultPending = false;
    private bool m_PracticeEditFailedThisResponse = false;

    private void RecordPracticeDropResult(string result, bool warning)
    {
        m_LastPracticeDropResult = result ?? "";
        m_PracticeDropResultPending = !string.IsNullOrWhiteSpace(m_LastPracticeDropResult);
        if (warning) NoteToolFailure(m_LastPracticeDropResult);
        if (m_LogHumBack)
            Debug.Log("[Practice/Drop] " + m_LastPracticeDropResult);
    }

    private void RecordPracticeEditResult(string result, bool warning)
    {
        m_LastPracticeEditResult = result ?? "";
        m_PracticeEditResultPending = !string.IsNullOrWhiteSpace(m_LastPracticeEditResult);
        if (warning) m_PracticeEditFailedThisResponse = true;
        if (warning) NoteToolFailure(m_LastPracticeEditResult);
        if (m_LogHumBack)
            Debug.Log("[Practice/Edit] " + m_LastPracticeEditResult);
    }

    private void RecordPracticeEditFailureForLlm(
        string result, string code, string correction, string tool = "clip_confirm")
    {
        m_LastPracticeEditResult = result ?? "";
        m_PracticeEditResultPending = !string.IsNullOrWhiteSpace(m_LastPracticeEditResult);
        m_PracticeEditFailedThisResponse = true;
        ReportToolFailureForLlm(
            tool, code, m_LastPracticeEditResult, correction);
        if (m_LogHumBack)
            Debug.Log("[Practice/Edit] " + m_LastPracticeEditResult);
    }

    private AgentHumBackRequest ExtractHumBackTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        text = SplitGluedAgentTags(text);
        var matches = s_HumBackTagRegex.Matches(text);
        if (matches.Count == 0) return null;
        var match = matches[0];
        string attrs = match.Groups["attrs"].Value;
        AgentHumBackRequest request = ParseHumBackCompatibleAttributes(attrs);
        if (string.Equals(match.Groups["tool"].Value, "sing", StringComparison.OrdinalIgnoreCase))
            ConfigureUnifiedSing(request, attrs);
        request.ExtraTagsIgnored = Math.Max(0, matches.Count - 1);
        text = s_HumBackTagRegex.Replace(text, "").Trim();
        if (request.ExtraTagsIgnored > 0)
        {
            NoteSingingRoutingFact(
                "hum_back",
                "extra_actions_ignored",
                $"同一回复包含 {matches.Count} 个 hum_back；程序只执行第一个，" +
                $"其余 {request.ExtraTagsIgnored} 个没有执行。一次回复只提交一个真实歌唱动作。");
            if (m_LogHumBack)
                Debug.LogWarning($"[HumBack] 同一回复出现 {matches.Count} 个 hum_back；只保留第一个");
        }
        return request;
    }

    private static AgentHumBackRequest ParseHumBackCompatibleAttributes(string attrs)
    {
        string rawKey = ReadToolAttribute(attrs, "key");
        string rawAlignTo = ReadToolAttribute(attrs, "align_to");
        float[] keyList = ParseKeyList(rawKey);
        var request = new AgentHumBackRequest
        {
            Mode = ReadToolAttribute(attrs, "mode"),
            Source = ReadToolAttribute(attrs, "source"),
            Lyrics = ReadToolAttribute(attrs, "lyrics"),
            Reason = ReadToolAttribute(attrs, "reason"),
            Key = keyList == null ? ReadToolFloatAttributeUnclamped(attrs, "key") : float.NaN,
            KeyPerSegment = keyList,
            Pace = ReadToolFloatAttribute(attrs, "pace", 0.8f, 1.25f),
            Expression = ReadToolFloatAttribute(attrs, "expression", 0f, 1f),
            Order = ReadToolAttribute(attrs, "order"),
            Capture = ReadToolAttribute(attrs, "capture"),
            TrimHeadSeconds = ReadToolFloatAttributeUnclamped(attrs, "trim_head_seconds"),
            TrimTailSeconds = ReadToolFloatAttributeUnclamped(attrs, "trim_tail_seconds"),
            ExcludeSpeech = ReadToolBoolAttribute(attrs, "exclude_speech", false),
            TargetNote = ReadToolAttribute(attrs, "target_note"),
            PitchTarget = ReadToolAttribute(attrs, "pitch_target"),
            PitchDelta = ReadToolAttribute(attrs, "pitch_delta"),
            PitchMatch = ReadToolAttribute(attrs, "pitch_match"),
            PitchPlan = ReadToolAttribute(attrs, "pitch_plan"),
            Transpose = ReadToolAttribute(attrs, "transpose"),
        };
        if (string.IsNullOrWhiteSpace(request.Mode)) request.Mode = "echo";
        if (string.IsNullOrWhiteSpace(request.Capture)) request.Capture = "clean";
        if (float.IsNaN(request.TrimHeadSeconds)) request.TrimHeadSeconds = 0f;
        if (float.IsNaN(request.TrimTailSeconds)) request.TrimTailSeconds = 0f;
        if (request.TrimHeadSeconds < 0f || request.TrimTailSeconds < 0f)
            request.ValidationError = "trim_head_seconds / trim_tail_seconds 不能是负数";
        request.SourceValidationError = ValidateHumBackMaterialSource(request);
        string pitchValidation = ValidateHumBackPitchAttributes(
            rawKey, rawAlignTo, request);
        if (string.IsNullOrWhiteSpace(request.ValidationError))
            request.ValidationError = pitchValidation;
        return request;
    }

    private static readonly System.Text.RegularExpressions.Regex s_SongSearchTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<song_search\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Removes known self-closing agent tags that occur inside Markdown inline-code
    /// or fenced-code spans. A model may name a tool while explaining its plan;
    /// quoted syntax is documentation, not an executable action.
    /// </summary>
    private static string RemoveQuotedAgentActionTags(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.IndexOf('`') < 0 || raw.IndexOf('<') < 0)
            return raw ?? "";

        var matches = s_AnyOpenTagRegex.Matches(raw);
        if (matches.Count == 0) return raw;
        var result = new System.Text.StringBuilder(raw);
        bool changed = false;
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            System.Text.RegularExpressions.Match match = matches[i];
            if (!IsKnownAgentTagName(match.Groups["name"].Value) ||
                !IsInsideMarkdownCodeSpan(raw, match.Index))
                continue;
            result.Remove(match.Index, match.Length);
            changed = true;
        }
        if (!changed) return raw;

        // Avoid leaving an empty pair of backticks in user-visible/TTS text after
        // removing a quoted tag. Non-empty explanatory code spans are preserved.
        return System.Text.RegularExpressions.Regex.Replace(
            result.ToString(), @"(?s)(`{1,})\s*\1", "").Trim();
    }

    private static bool IsInsideMarkdownCodeSpan(string text, int index)
    {
        bool inside = false;
        int delimiterLength = 0;
        for (int i = 0; i < index;)
        {
            if (text[i] != '`')
            {
                i++;
                continue;
            }
            int runStart = i;
            while (i < index && text[i] == '`') i++;
            int runLength = i - runStart;
            if (!inside)
            {
                inside = true;
                delimiterLength = runLength;
            }
            else if (runLength >= delimiterLength)
            {
                inside = false;
                delimiterLength = 0;
            }
        }
        return inside;
    }

    private static readonly System.Text.RegularExpressions.Regex s_SongCatalogTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<song_catalog\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    //<speaker_name name="小悠"/> —— 给"刚说话的这个人"改声纹档案里的显示名。
    //
    //为什么交给她：工程层原来用正则从"我叫X/叫我X"里抓名字，而正则不知道语气词——
    //用户说"就叫我小优吧。"，抓到的是「小优吧」。她看得懂，同一句话她在 <note/> 里
    //写的是"自分の名前を小優と教えてくれた"，已经正确剥掉了「吧」，只是没有渠道
    //把这个判断写回声纹库。
    //
    //不带 speaker_id 是刻意的：那样她得从元数据里抄 guest_9028f80491 这种串，而实测
    //她连节点名都会写错(ユーザー昵称小优 vs 用户昵称小优)，少一个易错参数。
    private static readonly System.Text.RegularExpressions.Regex s_SpeakerNameTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<speaker_name\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private string ExtractSpeakerNameTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = s_SpeakerNameTagRegex.Match(text);
        if (!match.Success) return null;
        string name = ReadToolAttribute(match.Groups["attrs"].Value, "name");
        text = s_SpeakerNameTagRegex.Replace(text, "").Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>
    /// 把她给出的名字写回声纹档案。目标固定是"最近一次识别出的说话人"。
    /// </summary>
    private void ApplySpeakerNameTag(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (senseVoice == null) return;

        //先用本轮的；本轮没认出人(噪音轮次会返回 unknown)就回退到最近一次认出的那个。
        //她经常隔一轮才反应过来要改名——实测 5 次调用里有 2 次卡在这，而且两次都是纠错。
        string id = senseVoice.LastSpeakerId;
        string via = "本轮";
        if (string.IsNullOrEmpty(id) || id == "unknown" || id == "ai_self")
        {
            id = senseVoice.LastKnownSpeakerId;
            via = "回退到最近识别";
        }
        if (string.IsNullOrEmpty(id) || id == "unknown")
        {
            Debug.LogWarning($"[Speaker] <speaker_name name=\"{name}\"/> 被忽略：当前没有可指认的说话人");
            return;
        }
        //AI 自己的档案不能被改——那是回声识别的锚点
        if (id == "ai_self")
        {
            Debug.LogWarning("[Speaker] <speaker_name/> 被忽略：不能改 ai_self 的档案");
            return;
        }
        Debug.Log($"[Speaker] <speaker_name/>({via}): {id} → 「{name}」" +
                  $"(原「{senseVoice.LastKnownSpeakerName}」)");
        senseVoice.RenameSpeaker(id, name, null);
    }

    private class AgentSpeakerManageRequest
    {
        public string Action = "review";
        public string SourceId = "";
        public string TargetId = "";
        public string VoiceprintId = "";
        public string OperationId = "";
        public string DisplayName = "";
        public string Reason = "";
    }

    private static readonly System.Text.RegularExpressions.Regex s_SpeakerManageTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<speaker_manage\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private AgentSpeakerManageRequest ExtractSpeakerManageTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = s_SpeakerManageTagRegex.Match(text);
        if (!match.Success) return null;
        string attrs = match.Groups["attrs"].Value;
        var request = new AgentSpeakerManageRequest
        {
            Action = ReadToolAttribute(attrs, "action"),
            SourceId = ReadToolAttribute(attrs, "source"),
            TargetId = ReadToolAttribute(attrs, "target"),
            VoiceprintId = ReadToolAttribute(attrs, "voiceprint"),
            OperationId = ReadToolAttribute(attrs, "operation"),
            DisplayName = ReadToolAttribute(attrs, "name"),
            Reason = ReadToolAttribute(attrs, "reason"),
        };
        if (string.IsNullOrWhiteSpace(request.Action)) request.Action = "review";
        text = s_SpeakerManageTagRegex.Replace(text, "").Trim();
        return request;
    }

    private void BeginSpeakerManage(AgentSpeakerManageRequest request)
    {
        if (request == null) return;
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (senseVoice == null)
        {
            RecordSpeakerManageResult("声纹管理失败：当前语音服务不可用。", true);
            return;
        }
        if (m_SpeakerManageInFlight)
        {
            RecordSpeakerManageResult("声纹管理未执行：上一项操作仍在进行。", true);
            return;
        }

        m_SpeakerManageInFlight = true;
        m_SpeakerManageResultPending = false;
        int generation = ++m_SpeakerManageGeneration;
        if (m_LogAgentLoop)
            Debug.Log($"[Speaker/Manage] 角色调用 action={request.Action} " +
                      $"source={request.SourceId} target={request.TargetId} " +
                      $"voiceprint={request.VoiceprintId} operation={request.OperationId} " +
                      $"reason={request.Reason}");

        senseVoice.ManageSpeakers(
            request.Action,
            request.SourceId,
            request.TargetId,
            request.VoiceprintId,
            request.OperationId,
            request.DisplayName,
            result =>
            {
                if (generation != m_SpeakerManageGeneration) return;
                m_SpeakerManageInFlight = false;
                if (result == null || !result.ok)
                {
                    string error = result == null || string.IsNullOrWhiteSpace(result.error)
                        ? "服务没有返回结果"
                        : TruncateForFrame(result.error, 300);
                    RecordSpeakerManageResult("声纹管理失败：" + error, true);
                }
                else
                {
                    string summary = string.IsNullOrWhiteSpace(result.summary)
                        ? "操作成功，但服务没有返回仓库摘要。"
                        : result.summary;
                    RecordSpeakerManageResult(
                        "工具已实际完成 action=" + result.action + "。\n" +
                        TruncateForFrame(summary, 4000),
                        false);
                }

                if (m_AgentRunning && !m_AgentRoundInFlight &&
                    !IsAISpeaking && !IsVoiceOutputPlaying)
                {
                    ScheduleNextTick(m_MinTickSec, "speaker-manage-result");
                }
            });
    }

    private void RecordSpeakerManageResult(string result, bool warning)
    {
        m_LastSpeakerManageResult = result ?? "";
        m_SpeakerManageResultPending = !string.IsNullOrWhiteSpace(m_LastSpeakerManageResult);
        if (warning) NoteToolFailure(m_LastSpeakerManageResult);
        if (m_LogAgentLoop)
        {
            if (warning) Debug.LogWarning("[Speaker/Manage] " + m_LastSpeakerManageResult);
            else Debug.Log("[Speaker/Manage] " + m_LastSpeakerManageResult);
        }
    }

    private AgentSongSearchRequest ExtractSongSearchTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = s_SongSearchTagRegex.Match(text);
        if (!match.Success) return null;
        string attrs = match.Groups["attrs"].Value;
        AgentSongSearchRequest request = new AgentSongSearchRequest
        {
            Query = ReadToolAttribute(attrs, "query"),
            Mode = ReadToolAttribute(attrs, "mode"),
            Reason = ReadToolAttribute(attrs, "reason"),
        };
        if (string.IsNullOrWhiteSpace(request.Mode)) request.Mode = "auto";
        text = s_SongSearchTagRegex.Replace(text, "").Trim();
        return request;
    }

    private static AgentSongCatalogRequest ExtractSongCatalogTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = s_SongCatalogTagRegex.Match(text);
        if (!match.Success) return null;
        string attrs = match.Groups["attrs"].Value;
        AgentSongCatalogRequest request = new AgentSongCatalogRequest
        {
            Query = ReadToolAttribute(attrs, "query"),
            Offset = ReadToolIntAttribute(attrs, "offset", 0, 0, 1000000),
            Limit = ReadToolIntAttribute(attrs, "limit", 30, 1, 50),
            IncludeUnnamed = ReadToolBoolAttribute(attrs, "include_unnamed", false),
            Reason = ReadToolAttribute(attrs, "reason"),
        };
        if (request.Query.Length > 100) request.Query = request.Query.Substring(0, 100);
        text = s_SongCatalogTagRegex.Replace(text, "").Trim();
        return request;
    }

    /// <summary>
    /// 读一个数值属性并夹到合法区间。缺省或写得不合法时返回 NaN，交给调用方回落到
    /// 按 seed 生成的默认档——她漏写或写错都不该让这次演唱失败。
    /// </summary>
    /// <summary>
    /// key 写成 "0,4,0" 这样时解析成逐段移调；只有一个数(或空)时返回 null，走原来的整条移调。
    /// </summary>
    private static float[] ParseKeyList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string[] parts = raw.Split(
            new[] { ',', '，', '、', ' ', ';', '；' },
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var values = new List<float>(parts.Length);
        foreach (string part in parts)
        {
            var number = System.Text.RegularExpressions.Regex.Match(part, @"[-+]?\d*\.?\d+");
            if (!number.Success) return null;
            if (!float.TryParse(
                    number.Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out float value))
                return null;
            //这里**不截断**。截断要放到能回报的地方去——8/22 实测她三次写了 ±6，
            //全部在解析阶段被悄悄截成 ±4，结果与她的意图不同而她毫不知情，
            //事后被用户追问原因时只好编了一个"系统按声域自动调整"出来。
            values.Add(value);
        }
        return values.ToArray();
    }

    private static float ReadToolFloatAttributeUnclamped(string attrs, string name)
    {
        string raw = ReadToolAttribute(attrs, name);
        if (string.IsNullOrWhiteSpace(raw)) return float.NaN;
        var number = System.Text.RegularExpressions.Regex.Match(
            raw, @"[-+]?\d*\.?\d+");
        if (!number.Success) return float.NaN;
        return float.TryParse(
            number.Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float value)
                ? value
                : float.NaN;
    }

    /// <summary>
    /// key 是相对移调，不是 MIDI 音高。越界/歧义参数必须拒绝，不能把 62 静默夹成 +4。
    /// 该函数保持静态，便于 Editor 回归直接覆盖协议边界。
    /// </summary>
    private static string ValidateHumBackPitchAttributes(
        string rawKey, string rawAlignTo, AgentHumBackRequest request)
    {
        bool hasKey = !string.IsNullOrWhiteSpace(rawKey);
        bool hasLegacyTarget = !string.IsNullOrWhiteSpace(request.TargetNote);
        bool hasLegacyAlign = !string.IsNullOrWhiteSpace(rawAlignTo);
        bool hasTarget = !string.IsNullOrWhiteSpace(request.PitchTarget);
        bool hasDelta = !string.IsNullOrWhiteSpace(request.PitchDelta);
        bool hasMatch = !string.IsNullOrWhiteSpace(request.PitchMatch);
        bool hasPlan = !string.IsNullOrWhiteSpace(request.PitchPlan);
        bool hasTranspose = !string.IsNullOrWhiteSpace(request.Transpose);
        if (hasLegacyTarget && hasTarget)
            return "target_note 与 pitch_target 表达的是同一种绝对音高目标，只能填写一个。";
        if (hasLegacyAlign && hasMatch)
            return "align_to 与 pitch_match 表达的是同一种参考段目标，只能填写一个。";
        int musicalModeCount = (hasLegacyTarget || hasTarget ? 1 : 0) +
                               (hasLegacyAlign || hasMatch ? 1 : 0) +
                               (hasDelta ? 1 : 0) +
                               (hasPlan ? 1 : 0) +
                               (hasTranspose ? 1 : 0);
        if (musicalModeCount > 1)
            return "pitch_plan、transpose、pitch_target、pitch_delta、pitch_match " +
                   "表达的是不同音乐目标，一次只能选择一种。";
        if (musicalModeCount > 0 && hasKey)
            return "音乐目标已经由规范化音高属性表达，不能再同时填写底层 key。";

        if (hasLegacyAlign)
        {
            if (!int.TryParse(rawAlignTo.Trim(), out int alignIndex) || alignIndex <= 0)
                return $"align_to=\"{rawAlignTo}\" 不是有效的练唱清单段号（必须是正整数）。";
            request.AlignToPracticeIndex = alignIndex;
        }
        if (hasMatch &&
            (!int.TryParse(request.PitchMatch.Trim(), out int matchIndex) || matchIndex <= 0))
            return $"pitch_match=\"{request.PitchMatch}\" 不是有效的练唱清单段号（必须是正整数）。";

        if (hasKey)
        {
            bool declaresList = rawKey.IndexOfAny(new[] { ',', '，', '、', ';', '；' }) >= 0;
            if (declaresList && request.KeyPerSegment == null)
                return $"key=\"{rawKey}\" 不是有效的逐段数字列表。";
            if (request.KeyPerSegment != null)
            {
                for (int i = 0; i < request.KeyPerSegment.Length; i++)
                {
                    float value = request.KeyPerSegment[i];
                    if (float.IsNaN(value) || float.IsInfinity(value))
                        return $"key 第 {i + 1} 项不是有效数字。";
                    if (value < -12f || value > 12f)
                        return $"key 第 {i + 1} 项为 {value:0.##}，超出逐段相对移调范围 −12～+12；本次未执行。";
                }
            }
            else
            {
                if (float.IsNaN(request.Key) || float.IsInfinity(request.Key))
                    return $"key=\"{rawKey}\" 不是有效的相对移调数字。";
                if (request.Key < -4f || request.Key > 4f)
                    return $"key={request.Key:0.##} 超出整条相对移调范围 −4～+4；" +
                           "如果想指定绝对中心音高，请使用 pitch_plan（例如 pitch_plan=\"D4\"）。";
            }
        }

        if (hasLegacyTarget && !TryParseMidiNote(request.TargetNote, out float _))
            return $"target_note=\"{request.TargetNote}\" 不是有效音名；请使用 C4、D#4、Eb4 这类写法。";
        if (hasTarget)
        {
            string[] values = SplitPitchValueList(request.PitchTarget);
            if (values.Length == 0) return "pitch_target 不能为空。";
            for (int i = 0; i < values.Length; i++)
            {
                if (IsKeepPitchToken(values[i])) continue;
                if (!TryParseMidiNote(values[i], out float _))
                    return $"pitch_target 第 {i + 1} 项 \"{values[i]}\" 不是完整音名；" +
                           "请使用 C4、D#4、Eb4 或 keep。";
            }
        }
        if (hasDelta)
        {
            string[] values = SplitPitchValueList(request.PitchDelta);
            if (values.Length == 0) return "pitch_delta 不能为空。";
            for (int i = 0; i < values.Length; i++)
            {
                if (IsKeepPitchToken(values[i])) continue;
                if (!TryParseMusicalInterval(values[i], out float _))
                    return $"pitch_delta 第 {i + 1} 项 \"{values[i]}\" 不是有效音程；" +
                           "请使用 +whole_step、-half_step、+m3 或 keep。";
            }
        }
        if (hasPlan)
        {
            string[] values = SplitPitchValueList(request.PitchPlan);
            if (values.Length == 0) return "pitch_plan 不能为空。";
            string[] selectedSegments = SplitPitchValueList(request.Order);
            if (values.Length == 1 && selectedSegments.Length > 1 &&
                TryParsePitchPlanMatchToken(values[0], out int sharedReference))
            {
                return $"order 选择了 {selectedSegments.Length} 段，但单个 " +
                       $"pitch_plan=\"match:{sharedReference}\" 的作用域有歧义：" +
                       "程序无法知道是全部所选段都匹配，还是只调整其中一段。" +
                       "请为 order 的每个位置逐项填写（例如 keep,match:1,keep）；" +
                       "若确实要全部匹配，也请重复写出每一项。";
            }
            for (int i = 0; i < values.Length; i++)
            {
                string value = values[i];
                if (!IsKeepPitchToken(value) && IsAmbiguousNumericStepToken(value))
                    return $"pitch_plan 第 {i + 1} 项 \"{value}\" 使用了有歧义的数字 st/半音单位；" +
                           "半音请写 +half_step/-half_step，全音请写 +whole_step/-whole_step，" +
                           "其它音程请写 +m3、-P5 这类标准音程名。";
                if (IsKeepPitchToken(value) || IsSourcePitchToken(value) ||
                    TryParseMidiNote(value, out float _) ||
                    TryParseMusicalInterval(value, out float _) ||
                    TryParsePitchPlanMatchToken(value, out int _))
                    continue;
                return $"pitch_plan 第 {i + 1} 项 \"{value}\" 无法理解；" +
                       "请使用 keep、source、A3、+half_step、+whole_step、+m3 或 match:1。";
            }
        }
        if (hasTranspose)
        {
            if (IsAmbiguousNumericStepToken(request.Transpose))
                return $"transpose=\"{request.Transpose}\" 使用了有歧义的数字 st/半音单位；" +
                       "半音请写 +half_step/-half_step，全音请写 +whole_step/-whole_step。";
            if (!TryParseMusicalInterval(request.Transpose, out float _))
                return $"transpose=\"{request.Transpose}\" 不是有效音程；" +
                       "请使用 +half_step、-whole_step、+m3 或 -P5。";
        }
        return "";
    }

    private static bool IsKnownAgentTagName(string name)
    {
        if (string.Equals(name, "body_inspect", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "sing_goal", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (string candidate in s_GluableTagNames)
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string RemoveUnknownAgentTags(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.IndexOf('<') < 0) return raw ?? "";
        raw = SplitGluedAgentTags(raw);
        return s_AnyOpenTagRegex.Replace(raw, match =>
        {
            string name = match.Groups["name"].Value;
            if (string.Equals(name, "think", StringComparison.OrdinalIgnoreCase) ||
                IsKnownAgentTagName(name))
                return match.Value;
            return "";
        });
    }

    /// <summary>C-1=0、C4=60；支持升降号的标准音名。</summary>
    private static bool TryParseMidiNote(string noteText, out float midi)
    {
        midi = 0f;
        var match = System.Text.RegularExpressions.Regex.Match(
            (noteText ?? "").Trim(),
            @"^(?<note>[A-Ga-g])(?<accidental>[#♯b♭]?)(?<octave>-?\d+)$");
        if (!match.Success || !int.TryParse(match.Groups["octave"].Value, out int octave))
            return false;
        int semitone;
        switch (char.ToUpperInvariant(match.Groups["note"].Value[0]))
        {
            case 'C': semitone = 0; break;
            case 'D': semitone = 2; break;
            case 'E': semitone = 4; break;
            case 'F': semitone = 5; break;
            case 'G': semitone = 7; break;
            case 'A': semitone = 9; break;
            case 'B': semitone = 11; break;
            default: return false;
        }
        string accidental = match.Groups["accidental"].Value;
        if (accidental == "#" || accidental == "♯") semitone++;
        else if (accidental == "b" || accidental == "♭") semitone--;
        int value = (octave + 1) * 12 + semitone;
        if (value < 0 || value > 127) return false;
        midi = value;
        return true;
    }

    private static float ReadToolFloatAttribute(
        string attrs, string name, float min, float max)
    {
        string raw = ReadToolAttribute(attrs, name);
        if (string.IsNullOrWhiteSpace(raw)) return float.NaN;
        //允许「+2」「-1半音」「1.05倍」这类写法，只取第一个数
        var number = System.Text.RegularExpressions.Regex.Match(
            raw, @"[-+]?\d*\.?\d+");
        if (!number.Success) return float.NaN;
        if (!float.TryParse(
                number.Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float value))
            return float.NaN;
        return Mathf.Clamp(value, min, max);
    }

    private static int ReadToolIntAttribute(
        string attrs, string name, int defaultValue, int min, int max)
    {
        string raw = ReadToolAttribute(attrs, name);
        if (!int.TryParse(raw, out int value)) return defaultValue;
        return Mathf.Clamp(value, min, max);
    }

    private static bool ReadToolBoolAttribute(
        string attrs, string name, bool defaultValue)
    {
        string raw = ReadToolAttribute(attrs, name).Trim().ToLowerInvariant();
        if (raw == "true" || raw == "1" || raw == "yes") return true;
        if (raw == "false" || raw == "0" || raw == "no") return false;
        return defaultValue;
    }

    private static string ReadToolAttribute(string attrs, string name)
    {
        if (string.IsNullOrEmpty(attrs)) return "";
        string pattern = "\\b" + System.Text.RegularExpressions.Regex.Escape(name) +
            "\\s*=\\s*(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)'|“(?<cq>[^”]*)”|＂(?<fq>[^＂]*)＂)";
        var match = System.Text.RegularExpressions.Regex.Match(
            attrs,
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return "";
        foreach (string group in new string[] { "dq", "sq", "cq", "fq" })
            if (match.Groups[group].Success) return match.Groups[group].Value.Trim();
        return "";
    }

    //歌名只能来自三个地方：曲库候选、用户亲口说过、song_search 明确确认。
    //这条规则在提示词里写过两次，两次都被无视——8/17 把《忘记时间》说成《沉默是金》，
    //8/22 更进一步：用户唱的是 One Last Kiss(曲库里本来就有这个名字)，她从歌词里的
    //「ルーブル」造出《ルイ・ポールのルーブル》，还带着这个假名字搜了一轮、记了三条 note。
    //那次只因为没检测到歌声、song_remember 失败才没落盘。改成在代码里判定。
    private static readonly char[] s_TitleTrimChars =
    {
        ' ', '\t', '“', '”', '"', '\'', '‘', '’', '《', '》', '「', '」', '『', '』',
        '(', ')', '（', '）', '['   , ']', '【', '】', '.', '。', '!', '！', '?', '？',
    };

    private static readonly string[] s_UnnamedTitlePlaceholders =
    {
        "未命名", "未命名旋律", "无题", "無題", "不明", "unknown", "unnamed",
        "untitled", "no title", "名前なし", "タイトル不明",
    };

    private static string NormalizeSongTitleForProvenance(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        return title.Trim().Trim(s_TitleTrimChars).Trim().ToLowerInvariant();
    }

    /// <summary>
    /// 比对用的归一化：空白全部压掉，大小写拉平。标点保留——中日文里标点变化
    /// 往往意味着语气变了，抹掉会把"真的不一样的两句"判成同一句。
    /// </summary>
    private static string NormalizeUtteranceForRepeat(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 这一轮的正文是不是逐字重复了自己最近说过的某一句，而用户中间一句话都没说。
    /// 三个条件缺一不可：逐字相同、够长、用户从那以后没开口。
    /// 用户开口就放行——他说"再说一遍"时必须能说。
    /// </summary>
    /// <summary>
    /// 开口第一句是不是逐字重复了最近说过的话的开头。
    /// 用"是不是某条最近发言的前缀"来判，而不是整条相等——复读常常前半句一样、
    /// 后面才分叉，而只要前半句一样，用户听到的开头就已经是重复的了。
    /// 门槛：至少 12 个字，且用户从那条发言之后没有开口过。
    /// </summary>
    /// <summary>目前为止的正文，还是不是某条最近发言的开头。</summary>
    private bool MatchesRecentUtterancePrefix(string probe, out string ago)
    {
        return MatchRecentUtterance(probe, false, out ago);
    }

    /// <summary>整轮正文和某条最近发言完全相同。</summary>
    private bool MatchesRecentUtteranceExactly(string probe, out string ago)
    {
        return MatchRecentUtterance(probe, true, out ago);
    }

    private bool MatchRecentUtterance(string probe, bool exact, out string ago)
    {
        ago = "";
        if (string.IsNullOrEmpty(probe)) return false;
        float now = Time.realtimeSinceStartup;
        foreach (var kv in m_RepeatWatch)
        {
            bool hit = exact
                ? string.Equals(kv.Value, probe, StringComparison.Ordinal)
                : kv.Value.StartsWith(probe, StringComparison.Ordinal);
            if (!hit) continue;
            if (now - kv.Key > k_SelfRepeatWindowSeconds) continue;
            //用户在那句之后开过口 → 这一次多半是他要的，照常说出来。
            if (m_LastUserTurnTime > kv.Key) continue;
            ago = FormatDuration(now - kv.Key);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 判定为逐字复读：复用 &lt;silent/&gt; 那一整套——不进 TTS、历史记成 [内心]、
    /// 下一帧她自己在 你最近发言 里看得到。同时给她一句交代。
    /// </summary>
    private void MarkRoundAsSilencedRepeat(string spoken, string ago)
    {
        m_RoundSilencedForRepeat = true;
        m_RoundWaitForUser = true;
        m_RoundIsInner = true;
        m_RoundContinue = false;
        m_RoundNextInSec = null;
        DiscardPendingSpeechForRound(m_CurrentSpeechRoundId);
        m_SelfRepeatNote =
            $"上一轮你要说的是「{TruncateForFrame(spoken, 24)}」，" +
            $"和你 {ago} 前说过的**一字不差**，而用户中间没有开口。" +
            "系统**没有把它读出来**——那一轮对用户来说是安静的，它只记进了你的内心。" +
            "这不是惩罚：他在忙的时候不需要每分钟被提醒一次。" +
            "接下来可以真的安静下来，也可以趁这一拍做点别的（看一眼、连一条记忆、" +
            "或者在心里想点新的）。";
        if (m_LogAgentLoop)
            Debug.LogWarning("[Agent/复读] 逐字复读，本轮转内心独白不出声: " + spoken);
    }

    /// <summary>
    /// 只丢掉指定 LLM round 还在排队的 TTS 内容。continue chain 共用管线，
    /// 因此不能简单 Clear：队列前面可能还有上一节正常内容没播完。
    /// </summary>
    private void DiscardPendingSpeechForRound(int roundId)
    {
        if (roundId <= 0) return;
        m_SilencedSpeechRoundIds.Add(roundId);

        int removedChunks = 0;
        var keptChunks = new Queue<PendingSpeechChunk>();
        while (m_PendingChunks.Count > 0)
        {
            PendingSpeechChunk item = m_PendingChunks.Dequeue();
            if (item.RoundId == roundId) removedChunks++;
            else keptChunks.Enqueue(item);
        }
        m_PendingChunks = keptChunks;

        int removedClips = 0;
        var keptClips = new Queue<PendingSpeechClip>();
        while (m_PendingClips.Count > 0)
        {
            PendingSpeechClip item = m_PendingClips.Dequeue();
            if (item.RoundId == roundId) removedClips++;
            else keptClips.Enqueue(item);
        }
        m_PendingClips = keptClips;

        if (m_LogAgentLoop && (removedChunks > 0 || removedClips > 0))
            Debug.Log($"[Agent/复读] 已丢弃 round={roundId} 尚未播放的 TTS：" +
                      $"text={removedChunks}, audio={removedClips}");
    }

    private bool IsVerbatimSelfRepeat(string plain, out string reason)
    {
        reason = "";
        string probe = NormalizeUtteranceForRepeat(plain);
        if (probe.Length < k_SelfRepeatMinChars) return false;
        float now = Time.realtimeSinceStartup;
        foreach (var kv in m_RepeatWatch)
        {
            if (kv.Value != probe) continue;
            float ago = now - kv.Key;
            if (ago > k_SelfRepeatWindowSeconds) continue;
            //用户在那句之后开过口 → 这一次是他要的，不算自说自话。
            if (m_LastUserTurnTime > kv.Key) continue;
            reason =
                $"你 {Mathf.RoundToInt(ago)} 秒前说过**一字不差**的同一段话，" +
                "而用户从那以后一句话都没说。这一轮里的工具调用**全部没有执行**——" +
                "重复调用的危害不在于失败，而在于它们会一次次成功：" +
                "比如连着删四次练唱片段，每次段号都会前移，删掉的根本不是同一段。" +
                "现在**先停下**：要么等用户开口，要么说点和刚才不一样的话。" +
                "如果你确实还需要那个工具，先说清楚你要做什么、换一组参数再调。";
            return true;
        }
        return false;
    }

    /// <summary>说完一句就记下来，供下一轮比对。发声与内心独白一视同仁——
    /// 内心话复读同样会带着工具标签，害处一样。</summary>
    private void NoteUtteranceForRepeatWatch(string plain)
    {
        string probe = NormalizeUtteranceForRepeat(plain);
        if (probe.Length < k_SelfRepeatMinChars) return;
        m_RepeatWatch.Enqueue(new KeyValuePair<float, string>(Time.realtimeSinceStartup, probe));
        while (m_RepeatWatch.Count > k_RepeatWatchDepth) m_RepeatWatch.Dequeue();
    }

    /// <summary>
    /// 把一段将要送进 LLM 的文本里出现的完整曲库 id 全部记下来。
    /// 只认 12 位十六进制——她把显示用的 6 位短写当 id 用过好几次
    /// (8/25 的 `id="7e1fb5"` 就是)，那种写法这里认不出来，正好该被拦。
    /// </summary>
    private void HarvestSongIds(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (System.Text.RegularExpressions.Match m in s_SongIdRegex.Matches(text))
            m_SeenSongIds.Add(m.Value.ToLowerInvariant());
    }

    /// <summary>
    /// 这个 id 是她见过的，还是编出来/记岔的。歌名有出处校验，id 也该有。
    /// </summary>
    private bool SongIdHasProvenance(string songId, out string problem)
    {
        problem = "";
        string probe = (songId ?? "").Trim().ToLowerInvariant();
        if (probe.Length == 0) return true;         //没填 id 走歌名那条路，不归这里管
        if (!s_SongIdRegex.IsMatch(probe) || probe.Length != 12)
        {
            problem =
                $"未执行：\"{songId}\" 不是一个曲库 id。曲库 id 是 **12 位** 十六进制，" +
                "感知帧里 `id=` 后面那一串就是完整的，照抄整串，别截短、别凭印象写。";
            return false;
        }
        if (m_SeenSongIds.Contains(probe)) return true;
        problem =
            $"未执行：id={probe} 没有在你见过的任何地方出现过——" +
            "既不在感知帧的曲库候选里，也不在练唱清单、song_search 结果或本场台账里。" +
            "这多半是你凭印象写的。**不要猜 id**：" +
            "想唱曲库里的歌，先用 <song_search/> 查，或者照感知帧里 `id=` 后面那一串照抄；" +
            "想唱用户刚教的段落，用 <hum_back mode=\"practice\" order=\"N\"/>。";
        return false;
    }

    /// <summary>本场对曲库做过什么，记一笔。同一个 id 的同类动作只留最新那次。</summary>
    private void NoteSessionSong(string kind, string songId, string label)
    {
        string id = (songId ?? "").Trim().ToLowerInvariant();
        HarvestSongIds(id);
        for (int i = m_SessionSongLedger.Count - 1; i >= 0; i--)
        {
            var e = m_SessionSongLedger[i];
            if (e.Kind == kind && string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
                m_SessionSongLedger.RemoveAt(i);
        }
        m_SessionSongLedger.Add(new SessionSongNote
        {
            Kind = kind,
            Id = id,
            //只用来认"是哪一条"，不是给她读的。上下文紧，能省则省。
            Label = TruncateForFrame((label ?? "").Trim(), 10),
            At = Time.realtimeSinceStartup,
        });
        while (m_SessionSongLedger.Count > k_SessionLedgerMax) m_SessionSongLedger.RemoveAt(0);
    }

    /// <summary>
    /// 台账的帧文本。写得尽量短——这一栏是为了省下她去翻历史，不是再吃掉一块预算。
    /// </summary>
    private string BuildSessionSongLedgerClause()
    {
        if (m_SessionSongLedger.Count == 0) return "";
        var sb = new System.Text.StringBuilder("\n本场曲库台账(你自己做过的，历史裁掉了也算数):");
        foreach (var e in m_SessionSongLedger)
        {
            sb.Append($" {e.Kind}");
            if (!string.IsNullOrEmpty(e.Id)) sb.Append(" " + e.Id);
            if (!string.IsNullOrEmpty(e.Label)) sb.Append($"「{e.Label}」");
            float ago = Time.realtimeSinceStartup - e.At;
            sb.Append(ago < 1f ? " 刚刚;" : $" {FormatDuration(ago)}前;");
        }
        sb.Append(" 别说「还没存过」。");
        return sb.ToString();
    }

    private bool SongTitleHasProvenance(
        string title, SenseVoiceSpeechToText senseVoice, out string source)
    {
        source = "";
        string probe = NormalizeSongTitleForProvenance(title);
        //太短的名字("雪""LOVE")拿去做包含匹配会到处命中，反而放行了编造。
        if (probe.Length < 2) return false;
        //"未命名"这类占位不是她在编歌名，是她在说"这段没有名字"。当成空处理，
        //不要拿溯源那套去数落她——8/23 实测她写了 title="未命名"，
        //被回了一句"编一个会让用户不再信任你的记忆"，而她本来就没打算起名字。
        foreach (string placeholder in s_UnnamedTitlePlaceholders)
        {
            if (string.Equals(probe, placeholder, StringComparison.OrdinalIgnoreCase))
            {
                source = "占位名，按未命名处理";
                return true;
            }
        }

        if (senseVoice != null && senseVoice.RecallMentionsSongName(title.Trim()))
        {
            source = "曲库候选";
            return true;
        }
        //song_search 的返回原文里若出现这个名字，就算工具确认过。
        if (!string.IsNullOrEmpty(m_LastSongSearchResult) &&
            TextContainsNormalizedSongTitle(m_LastSongSearchResult, title))
        {
            source = "song_search 结果";
            return true;
        }
        //用户自己说过——含当前这一轮。历史里偶数位是用户。
        if (!string.IsNullOrEmpty(m_LastUserMsg) &&
            TextContainsNormalizedSongTitle(m_LastUserMsg, title))
        {
            source = "用户刚说的话";
            return true;
        }
        if (m_ChatHistory != null)
        {
            int scanned = 0;
            for (int i = m_ChatHistory.Count - 1; i >= 0 && scanned < 12; i--)
            {
                if (i % 2 != 0) continue;   //奇数位是她自己说的，不能自证
                scanned++;
                string turn = m_ChatHistory[i];
                if (!string.IsNullOrEmpty(turn) &&
                    TextContainsNormalizedSongTitle(turn, title))
                {
                    source = "用户先前说过";
                    return true;
                }
            }
        }
        return false;
    }

    private void BeginSongMemory(
        AgentSongMemoryRequest request,
        bool requireAcknowledgement = false)
    {
        if (request == null) return;
        m_SongMemoryAcknowledgementRequired = requireAcknowledgement;
        if (!m_EnableAutonomousSongMemory)
        {
            CompleteSongMemoryImmediately("歌曲记忆功能当前已关闭，未写入本机曲库。");
            return;
        }
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (senseVoice == null)
        {
            CompleteSongMemoryImmediately("当前语音服务不支持本地歌曲记忆。");
            return;
        }

        //名字没有出处就丢掉名字、留下旋律。删除操作不看名字(它只认 id)。
        m_DroppedSongTitleNote = "";
        if (!string.IsNullOrWhiteSpace(request.Title) &&
            !string.Equals(request.Action, "forget", StringComparison.OrdinalIgnoreCase))
        {
            if (!SongTitleHasProvenance(request.Title, senseVoice, out string titleSource))
            {
                bool isRename = string.Equals(
                    request.Action, "rename", StringComparison.OrdinalIgnoreCase);
                m_DroppedSongTitleNote =
                    $" 注意：歌名“{TruncateForFrame(request.Title.Trim(), 40)}”被丢弃了——" +
                    "它既不在这一轮的曲库候选里、用户也没说过、song_search 也没确认过。" +
                    "歌名只能来自这三个地方，从歌词里联想出来的不算。" +
                    (isRename
                        ? "这次改名没有执行。想确认名字就先问用户，或者用 <song_search/> 查。"
                        : "旋律已经照常保存，只是没有名字——说不出名字不丢人，编一个会让用户不再信任你的记忆。");
                if (m_LogAgentLoop)
                    Debug.LogWarning($"[SongMemory] 歌名无出处，已丢弃: \"{request.Title}\"");
                request.Title = "";
                if (isRename)
                {
                    CompleteSongMemoryImmediately(
                        "歌曲改名未执行。" + m_DroppedSongTitleNote);
                    return;
                }
            }
            else if (m_LogAgentLoop)
            {
                Debug.Log($"[SongMemory] 歌名出处={titleSource} \"{request.Title}\"");
            }
        }

        string signature = string.Join("|", new string[]
        {
            request.Action ?? "",
            request.SongId ?? "",
            request.Title ?? "",
            request.Artist ?? "",
            request.Lyrics ?? "",
        }).Trim().ToLowerInvariant();
        float now = Time.realtimeSinceStartup;
        if (signature == m_LastSongMemorySignature &&
            now - m_LastSongMemoryRequestTime < m_SongMemoryDuplicateCooldownSeconds)
        {
            if (m_LogAgentLoop) Debug.Log("[SongMemory] 忽略短时间内完全重复的操作: " + signature);
            //同一请求还在落盘时，沿用原请求的最终回调；不能把进行中的真实写入改成失败。
            if (m_SongMemoryInFlight) return;
            CompleteSongMemoryImmediately("检测到短时间内重复的歌曲记忆请求，本次没有再次写入。");
            return;
        }

        if (request.Action == "rename" &&
            (string.IsNullOrWhiteSpace(request.SongId) || string.IsNullOrWhiteSpace(request.Title)))
        {
            CompleteSongMemoryImmediately("歌曲改名需要工具结果中的 id 和用户确认的新歌名。");
            return;
        }
        if (request.Action == "forget" && string.IsNullOrWhiteSpace(request.SongId))
        {
            CompleteSongMemoryImmediately("删除歌曲记忆需要明确的歌曲 id。");
            return;
        }

        m_LastSongMemorySignature = signature;
        m_LastSongMemoryRequestTime = now;
        m_SongMemoryInFlight = true;
        m_SongMemoryResultPending = false;
        int generation = ++m_SongMemoryGeneration;
        // Capture ownership before invoking the service: it may finish after another user turn.
        m_SongMemoryOriginWorkEpoch = m_WorkEpoch;
        if (m_LogAgentLoop)
        {
            Debug.Log($"[SongMemory] 角色调用 action={request.Action} id={request.SongId} " +
                $"title=\"{request.Title}\" artist=\"{request.Artist}\" reason=\"{request.Reason}\"");
        }

        Action<SenseVoiceSpeechToText.SongMemoryResult> callback = result =>
        {
            if (generation != m_SongMemoryGeneration) return;
            m_SongMemoryInFlight = false;
            if (result == null)
            {
                m_LastSongMemoryResult = "歌曲记忆操作没有返回结果。";
            }
            else if (!result.Ok)
            {
                string detail = string.IsNullOrWhiteSpace(result.Error)
                    ? "未知错误"
                    : TruncateForFrame(result.Error, 180);
                m_LastSongMemoryResult = "歌曲记忆操作失败：" + detail;
                NoteToolFailure(m_LastSongMemoryResult);
            }
            else if (result.Action == "remember")
            {
                string name = string.IsNullOrWhiteSpace(result.DisplayName)
                    ? "未命名旋律"
                    : result.DisplayName;
                m_LastRememberedSongId = result.SongId ?? "";
                m_LastRememberedSongResultTime = Time.realtimeSinceStartup;
                ClearToolFailure();
                NoteSessionSong("存了", result.SongId,
                    result.Named ? name : (request.Lyrics ?? ""));
                m_LastSongMemoryResult =
                    $"已在本机记住“{name}”，歌曲ID={result.SongId}，" +
                    $"录音样本数={result.ReferenceCount}，独立歌曲段数={result.UniqueSegmentCount}。";
                //把"你刚才断定了什么、依据有多弱"交给她。填 id 是一个隐式断言，
                //而服务端核实不了它——实测跨歌合并的旋律相似度中位 0.654，同一首歌
                //不同段落中位 0.618，错误合并反而更高，没有可用阈值。能分辨的只有对话。
                if (!string.IsNullOrEmpty(result.MergeNote))
                    m_LastSongMemoryResult += " " + result.MergeNote;
                if (result.SegmentStatus == "duplicate_variant")
                {
                    m_LastSongMemoryResult +=
                        " 这次与已有段落是同一歌词/旋律，已作为另一次演唱版本保存，不会被误排成下一段。";
                }
                else if (result.SegmentStatus == "new_segment" && result.UniqueSegmentCount > 1)
                {
                    m_LastSongMemoryResult += " 这次已作为后续独立段加入当前学习顺序。";
                }
                if (!result.Named)
                {
                    m_LastSongMemoryResult +=
                        " 它可以一直保持未命名；你可以自然询问用户一次，也可以不追问。" +
                        "若用户以后明确命名，使用 <song_rename/> 和这个ID。";
                }
            }
            else if (result.Action == "rename")
            {
                NoteSessionSong("改名", result.SongId, result.DisplayName);
                m_LastSongMemoryResult =
                    $"已把歌曲ID={result.SongId}及其本机WAV改名为“{result.DisplayName}”。";
            }
            else if (result.Action == "forget")
            {
                if (string.Equals(m_LastRememberedSongId, result.SongId, StringComparison.Ordinal))
                {
                    m_LastRememberedSongId = "";
                    m_LastRememberedSongResultTime = -999f;
                }
                NoteSessionSong("删了", result.SongId, result.DisplayName);
                m_LastSongMemoryResult =
                    $"已从本机曲库删除歌曲ID={result.SongId}（{result.DisplayName}）及其受管WAV。";
            }
            else
            {
                m_LastSongMemoryResult = "歌曲记忆操作已完成。";
            }
            m_SongMemoryResultPending = true;
            if (m_LogAgentLoop) Debug.Log("[SongMemory] " + m_LastSongMemoryResult);
            m_SongMemoryAcknowledgementRequired = false;
            if (requireAcknowledgement)
            {
                QueueSongMemoryAcknowledgement(result != null && result.Ok);
            }
            else if (m_LogAgentLoop)
            {
                Debug.Log("[SongMemory] 自主记忆结果已进入角色状态；不强制追加确认轮次");
            }
        };

        switch (request.Action)
        {
            case "remember":
                senseVoice.RememberSong(
                    request.SongId, request.Title, request.Artist, request.Lyrics,
                    request.Aliases, request.Reason, callback, request.SourceRef);
                break;
            case "rename":
                senseVoice.RenameRememberedSong(
                    request.SongId, request.Title, request.Artist,
                    request.Aliases, callback);
                break;
            case "forget":
                senseVoice.ForgetRememberedSong(request.SongId, callback);
                break;
            default:
                m_SongMemoryInFlight = false;
                CompleteSongMemoryImmediately("未知的歌曲记忆操作。", generation);
                break;
        }
    }

    private void CompleteSongMemoryImmediately(string message, int generation = -1)
    {
        if (generation >= 0 && generation != m_SongMemoryGeneration) return;
        if (generation < 0) m_SongMemoryOriginWorkEpoch = m_WorkEpoch;
        m_SongMemoryInFlight = false;
        m_LastSongMemoryResult = message;
        m_SongMemoryResultPending = true;
        if (m_LogAgentLoop) Debug.LogWarning("[SongMemory] " + message);
        bool requireAcknowledgement = m_SongMemoryAcknowledgementRequired;
        m_SongMemoryAcknowledgementRequired = false;
        if (requireAcknowledgement)
            QueueSongMemoryAcknowledgement(false);
    }

    private void QueueSongMemoryAcknowledgement(bool success)
    {
        if (m_SongMemoryAcknowledgementCoroutine != null)
        {
            StopCoroutine(m_SongMemoryAcknowledgementCoroutine);
            m_SongMemoryAcknowledgementCoroutine = null;
        }
        int generation = m_SongMemoryGeneration;
        int originWorkEpoch = m_SongMemoryOriginWorkEpoch;
        // The storage fact survives a new user turn, but an old request cannot force its
        // acknowledgement into the new conversation. Its next formal observation can read it.
        if (originWorkEpoch != m_WorkEpoch) return;
        string resultText = m_LastSongMemoryResult ?? "";
        m_SongMemoryAcknowledgementCoroutine = StartCoroutine(
            DeliverSongMemoryAcknowledgementWhenIdle(generation, originWorkEpoch, success, resultText));
    }

    private IEnumerator DeliverSongMemoryAcknowledgementWhenIdle(
        int generation, int originWorkEpoch, bool success, string resultText)
    {
        // The service may complete synchronously within OnStreamComplete. Finish that
        // dispatch before considering a second one, and let Queue retain a live handle.
        yield return null;
        //首阶段的模型回复可能仍在播放EOU filler或清理空流水线；等它完全结束后再开
        //第二阶段，避免 StartStreaming 抢占/截断当前音频。
        while (generation == m_SongMemoryGeneration && originWorkEpoch == m_WorkEpoch &&
            (m_FormalResponseInFlight || !m_StreamComplete || IsAISpeaking ||
             IsVoiceOutputPlaying || m_AgentRoundInFlight ||
             m_UserSpeechActiveForAutonomy || m_AutonomyProbeInFlight))
        {
            yield return null;
        }

        m_SongMemoryAcknowledgementCoroutine = null;
        if (generation != m_SongMemoryGeneration || originWorkEpoch != m_WorkEpoch) yield break;

        ClearScheduledAgentWake();
        // The formal response receipt clears pending only after a valid completion.
        // Without Agent Loop there is no receipt: retain the fact, with no automatic retry.
        m_SongMemoryAcknowledgementInFlight = true;
        string toolFrame =
            "[同一真实用户轮次的本机歌曲记忆最终结果]\n" + resultText + "\n" +
            (success
                ? "本机操作已经成功。用户仍在等待这次请求的唯一最终答复；请严格按结果自然确认。只有结果明确写着“已在本机记住”时，才可以说已经记住。"
                : "落盘没有成功。用户仍在等待这次请求的唯一最终答复；必须如实说明这次尚未记住，不得声称已保存。") +
            "\n前一阶段只是未出声、未写入对话历史的工具参数草稿。不要重新评价用户的歌声，" +
            "不要重答原消息，不要再次调用任何歌曲工具。只回复一到两句自然口语。";

        //Agent 流式首阶段会先写一个 assistant 空占位，此时需要补一条内部 user 帧再
        //生成确认；直接对话/非流式路径没有该占位，确认本身直接配对原用户消息。
        //按当前奇偶性决定，避免工具结果让聊天气泡的 user/assistant 角色整体错位。
        if (m_ChatHistory.Count % 2 == 0)
            m_ChatHistory.Add("[歌曲记忆工具结果] " + resultText);
        if (m_AgentRunning)
        {
            m_AgentRoundInFlight = true;
            m_AgentCurrentRoundIsTick = true;
            ClearRoundParsed();
        }
        if (m_LogAgentLoop)
            Debug.Log($"[SongMemory] 落盘结果已返回，开始生成角色确认(success={success})");
        PrepareActiveSkillsForRound(toolFrame, "song-memory-result");
        StartStreaming(
            "",
            false,
            null,
            false,
            false,
            true,
            toolFrame);
    }

    private void CompleteSongMemoryAcknowledgementIfNeeded()
    {
        if (!m_SongMemoryAcknowledgementInFlight) return;
        m_SongMemoryAcknowledgementInFlight = false;
        if (m_LogAgentLoop) Debug.Log("[SongMemory] 落盘结果确认已完成");
    }

    private void BeginSongCatalogInspection(AgentSongCatalogRequest request)
    {
        if (request == null) return;
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (senseVoice == null)
        {
            RecordSongCatalogResult("曲库自查失败：当前语音服务不可用。", true);
            return;
        }
        if (m_SongCatalogInFlight)
        {
            RecordSongCatalogResult("曲库自查未重复执行：上一页仍在读取中。", true);
            return;
        }

        m_SongCatalogInFlight = true;
        m_SongCatalogResultPending = false;
        int generation = ++m_SongCatalogGeneration;
        if (m_LogAgentLoop)
            Debug.Log($"[SongCatalog] 角色只读自查 query=\"{request.Query}\" " +
                      $"offset={request.Offset} limit={request.Limit} " +
                      $"includeUnnamed={request.IncludeUnnamed} reason={request.Reason}");

        senseVoice.InspectSongCatalog(
            request.Query,
            request.Offset,
            request.Limit,
            request.IncludeUnnamed,
            result =>
            {
                if (generation != m_SongCatalogGeneration) return;
                m_SongCatalogInFlight = false;
                if (result == null || !result.Ok)
                {
                    string detail = result == null || string.IsNullOrWhiteSpace(result.Error)
                        ? "服务没有返回结果"
                        : TruncateForFrame(result.Error, 240);
                    RecordSongCatalogResult("曲库自查失败：" + detail, true);
                }
                else
                {
                    if (CatalogProvesLastFailedSongExists(result))
                    {
                        if (m_LogHumBack)
                            Debug.Log("[SongSing] 新鲜曲库快照已证明上次未找到的目标存在；清除失败缓存");
                        ClearSongSingFailureCache();
                    }
                    string summary = BuildSongCatalogInspectionSummary(result);
                    HarvestSongIds(summary);
                    RecordSongCatalogResult(summary, false);
                }

                if (m_AgentRunning && !m_AgentRoundInFlight &&
                    !IsAISpeaking && !IsVoiceOutputPlaying)
                {
                    ScheduleNextTick(m_MinTickSec, "song-catalog-result");
                }
            });
    }

    private static string BuildSongCatalogInspectionSummary(
        SenseVoiceSpeechToText.SongCatalogInspectionResult result)
    {
        if (result == null || !result.Ok) return "曲库自查没有得到可用快照。";
        var sb = new System.Text.StringBuilder();
        sb.Append("本机曲库实时自查结果（只读；这是完整后端快照，不是本场台账）：")
          .Append($"总条目 {result.TotalEntries}，命名条目 {result.NamedEntries}，")
          .Append($"未命名条目 {result.UnnamedEntries}，")
          .Append($"命名条目按标题精确归并为 {result.UniqueExactTitleGroups} 组。")
          .Append("“条目数”和“标题组数”都不等于独立歌曲数；同名可能是重复记录，也可能真是同名曲。\n");

        if (!string.IsNullOrWhiteSpace(result.Query))
            sb.Append($"筛选 query=\"{result.Query}\"，匹配 {result.MatchedEntries} 条；");
        else
            sb.Append(result.IncludeUnnamed
                ? $"当前查看命名及未命名条目，共 {result.MatchedEntries} 条；"
                : $"当前只列命名条目，共 {result.MatchedEntries} 条；未命名只计数、不在本页展开；");

        int shown = result.Entries != null ? result.Entries.Length : 0;
        if (shown == 0)
        {
            sb.Append("本页没有条目。\n");
        }
        else
        {
            sb.Append($"本页为第 {result.Offset + 1}～{result.Offset + shown} 条：\n");
            for (int i = 0; i < shown; i++)
            {
                SenseVoiceSpeechToText.SongCatalogEntry entry = result.Entries[i];
                if (entry == null) continue;
                string display = entry.named && !string.IsNullOrWhiteSpace(entry.title)
                    ? "《" + entry.title.Trim() + "》"
                    : "未命名旋律";
                sb.Append("- ").Append(display);
                if (!string.IsNullOrWhiteSpace(entry.artist))
                    sb.Append(" / ").Append(entry.artist.Trim());
                sb.Append(" id=").Append(entry.song_id)
                  .Append($"；参考录音={entry.reference_count}，独立段={entry.unique_segment_count}");
                if (entry.duplicate_variant_count > 0)
                    sb.Append($"，重复版本={entry.duplicate_variant_count}");
                sb.Append(entry.can_continue ? "，可尝试续唱" : "，没有已知后续段");
                sb.Append('\n');
            }
        }

        if (result.HasMore)
        {
            sb.Append($"还有后续页；下一次必须沿用 query=\"{result.Query}\"、")
              .Append($"include_unnamed={result.IncludeUnnamed.ToString().ToLowerInvariant()}，")
              .Append($"并调用 offset={result.NextOffset} limit={result.Limit}。")
              .Append("若用户问的是完整清单，在取完所有页之前不得声称“以上就是全部”或“曲库里只有这些”。");
        }
        else
        {
            sb.Append("本次筛选已经到末页，可以据此回答该筛选范围内的完整清单。")
              .Append("若 include_unnamed=false，只能说命名清单完整，不能说曲库没有其它未命名记忆。");
        }
        return sb.ToString().Trim();
    }

    private void RecordSongCatalogResult(string result, bool warning)
    {
        m_LastSongCatalogResult = result ?? "";
        m_SongCatalogResultPending = !string.IsNullOrWhiteSpace(m_LastSongCatalogResult);
        if (warning) NoteToolFailure(m_LastSongCatalogResult);
        else if (m_SongCatalogResultPending) ClearToolFailure();
        if (m_LogAgentLoop)
        {
            if (warning) Debug.LogWarning("[SongCatalog] " + m_LastSongCatalogResult);
            else Debug.Log("[SongCatalog] " + m_LastSongCatalogResult);
        }
    }

    private void BeginSongSearch(AgentSongSearchRequest request)
    {
        if (!m_EnableAutonomousSongSearch || request == null) return;
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (senseVoice == null)
        {
            m_LastSongSearchResult = "当前语音服务不支持歌曲检索。";
            m_SongSearchResultPending = true;
            return;
        }

        float now = Time.realtimeSinceStartup;
        string signature = (request.Mode ?? "auto").Trim().ToLowerInvariant() + "|" +
            (request.Query ?? "").Trim().ToLowerInvariant();
        if (now - m_LastSongSearchRequestTime < m_SongSearchCooldownSeconds)
        {
            if (m_LogAgentLoop)
                Debug.Log($"[SongSearch] 冷却中，忽略重复/过密调用: {signature}");
            return;
        }
        m_LastSongSearchRequestTime = now;
        m_LastSongSearchSignature = signature;
        m_SongSearchInFlight = true;
        m_SongSearchResultPending = false;
        int generation = ++m_SongSearchGeneration;

        if (m_LogAgentLoop)
            Debug.Log($"[SongSearch] 角色调用 mode={request.Mode} query=\"{request.Query}\" reason=\"{request.Reason}\"");

        senseVoice.SearchSong(request.Query, request.Mode, request.Reason, result =>
        {
            if (generation != m_SongSearchGeneration) return;
            m_SongSearchInFlight = false;
            if (result == null)
            {
                m_LastSongSearchResult = "歌曲检索没有返回结果。";
            }
            else if (!result.Ok)
            {
                string detail = string.IsNullOrWhiteSpace(result.Error)
                    ? "未知错误"
                    : TruncateForFrame(result.Error, 120);
                m_LastSongSearchResult = "歌曲检索失败：" + detail;
            }
            else
            {
                m_LastSongSearchResult = string.IsNullOrWhiteSpace(result.Summary)
                    ? "没有找到可靠的歌曲候选。"
                    : result.Summary;
                if (!result.Reliable)
                {
                    m_LastSongSearchResult +=
                        " 【硬性结论：工具没有确认歌名。只能把返回项描述为待核实候选，禁止断言歌名或歌手。】";
                }
            }
            m_SongSearchResultPending = true;
            if (m_LogAgentLoop) Debug.Log("[SongSearch] " + m_LastSongSearchResult);

            if (m_AgentRunning && m_BringForwardOnSongSearchResult &&
                !m_AgentRoundInFlight && !IsAISpeaking && !IsVoiceOutputPlaying)
            {
                ScheduleNextTick(m_MinTickSec, "song-search-result");
            }
        });
    }

    /// <summary>
    /// RTSpeechHandler uses this cheap gate before allocating a prefix snapshot.
    /// Prefix conversion is deliberately limited to an explicitly armed sing-along;
    /// ordinary humming must never consume the GPU speculatively.
    /// </summary>
    public bool CanPrepareStreamingHumBackPrefix()
    {
        return m_EnableStreamingHumBackPrefix && m_EnableAutonomousHumBack &&
            m_EnableNeuralHumSVC && !m_EnableSingingVoiceSynthesis &&
            m_IsVoiceMode && HasActiveSingAlongRequest() &&
            !HasStrongSpeculativeSpeechVeto() &&
            !m_HumBackPrefixPreparing && m_PreparedHumBackPrefixClip == null &&
            !m_FastHumBackEouStaged && !m_FastHumBackActive &&
            m_ActiveSVSRequest == null &&
            m_ActiveHumSVCRequest == null;
    }

    /// <summary>
    /// Takes ownership of prefixClip on success.  The request runs while the user
    /// continues singing, so its latency is hidden rather than paid after EOU.
    /// </summary>
    public bool TryPrepareStreamingHumBackPrefix(
        AudioClip prefixClip,
        float singingProbability,
        float pitchStability)
    {
        if (prefixClip == null || prefixClip.length < 5.5f ||
            (singingProbability < 0.52f && pitchStability < 0.42f) ||
            !CanPrepareStreamingHumBackPrefix())
            return false;

        GPTSoVITSFASTAPI characterVoice = m_ChatSettings != null
            ? m_ChatSettings.m_TextToSpeech as GPTSoVITSFASTAPI
            : null;
        string targetPath = characterVoice != null
            ? characterVoice.GetReferenceAudioPathForVoiceConversion()
            : "";
        if (string.IsNullOrWhiteSpace(targetPath)) return false;

        byte[] sourceWav;
        float sourceSeconds = prefixClip.length;
        try
        {
            sourceWav = WavUtility.FromAudioClip(prefixClip);
        }
        catch (Exception ex)
        {
            if (m_LogHumBack)
                Debug.LogWarning("[HumBack/Streaming] 前段WAV编码失败: " + ex.Message);
            return false;
        }
        Destroy(prefixClip);
        if (sourceWav == null || sourceWav.Length <= 44) return false;

        m_StreamingHumPerformanceSeed = NextHumPerformanceSeed();
        CreateHumPerformanceProfile(
            m_StreamingHumPerformanceSeed,
            out m_StreamingHumSemitoneOffset,
            out m_StreamingHumRmsMixRate,
            out m_StreamingHumProtect,
            out m_StreamingHumInterpretation);
        int generation = ++m_HumBackPrefixGeneration;
        m_HumBackPrefixPreparing = true;
        StartCoroutine(RequestStreamingHumBackPrefix(
            generation, sourceWav, sourceSeconds, targetPath));
        if (m_LogHumBack)
            Debug.Log($"[HumBack/Streaming] 已在演唱期间预转换开头 " +
                      $"duration={sourceSeconds:F2}s bytes={sourceWav.Length} " +
                      $"singing={singingProbability:F2} pitch={pitchStability:F2}");
        return true;
    }

    private IEnumerator RequestStreamingHumBackPrefix(
        int prefixGeneration,
        byte[] sourceWav,
        float sourceSeconds,
        string targetPath)
    {
        bool serviceReady = false;
        string serviceDetail = "";
        yield return EnsureHumSVCReady((ready, detail) =>
        {
            serviceReady = ready;
            serviceDetail = detail;
        });
        if (prefixGeneration != m_HumBackPrefixGeneration) yield break;
        if (!serviceReady)
        {
            m_HumBackPrefixPreparing = false;
            if (m_LogHumBack)
                Debug.LogWarning("[HumBack/Streaming] 预转换服务未就绪: " + serviceDetail);
            FallbackAfterStreamingPrefixFailure("preview-service-unavailable");
            yield break;
        }

        string requestId = Guid.NewGuid().ToString("N");
        WWWForm form = new WWWForm();
        form.AddBinaryData("source_audio", sourceWav, "singing_prefix.wav", "audio/wav");
        form.AddField("target_path", targetPath);
        form.AddField("request_id", requestId);
        form.AddField("diffusion_steps", Mathf.Clamp(m_HumSVCDiffusionSteps, 4, 30));
        form.AddField("auto_f0_adjust", m_HumSVCAutoF0Adjust ? "true" : "false");
        form.AddField("semitone_shift", Mathf.Clamp(
            m_HumSVCSemitoneShift + m_StreamingHumSemitoneOffset, -12, 12));
        form.AddField("performance_seed", m_StreamingHumPerformanceSeed);
        form.AddField("rms_mix_rate", InvariantFloat(m_StreamingHumRmsMixRate));
        form.AddField("protect", InvariantFloat(m_StreamingHumProtect));
        form.AddField("interpretation", InvariantFloat(m_StreamingHumInterpretation));
        form.AddField("max_seconds", Mathf.Min(m_HumBackMaxSeconds, sourceSeconds + 1f).ToString(
            "0.###", System.Globalization.CultureInfo.InvariantCulture));

        using (UnityWebRequest request = UnityWebRequest.Post(m_HumSVCURL, form))
        {
            request.downloadHandler = new DownloadHandlerAudioClip(m_HumSVCURL, AudioType.WAV);
            request.timeout = Mathf.Clamp(m_HumSVCTimeoutSeconds, 30, 600);
            m_HumBackPrefixSVCRequest = request;
            m_HumBackPrefixSVCRequestId = requestId;
            float startedAt = Time.realtimeSinceStartup;
            yield return request.SendWebRequest();
            if (m_HumBackPrefixSVCRequest == request)
            {
                m_HumBackPrefixSVCRequest = null;
                m_HumBackPrefixSVCRequestId = "";
            }
            if (prefixGeneration != m_HumBackPrefixGeneration) yield break;
            m_HumBackPrefixPreparing = false;

            if (request.result != UnityWebRequest.Result.Success ||
                !string.Equals(request.GetResponseHeader("X-SVC-Complete"), "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (m_LogHumBack)
                    Debug.LogWarning($"[HumBack/Streaming] 前段预转换失败 " +
                                     $"HTTP={request.responseCode} error={request.error}");
                FallbackAfterStreamingPrefixFailure("preview-conversion-failed");
                yield break;
            }

            AudioClip converted = null;
            try { converted = DownloadHandlerAudioClip.GetContent(request); }
            catch (Exception ex)
            {
                if (m_LogHumBack)
                    Debug.LogWarning("[HumBack/Streaming] 前段WAV解码失败: " + ex.Message);
            }
            if (converted == null || converted.length <= 0.05f)
            {
                if (converted != null) Destroy(converted);
                FallbackAfterStreamingPrefixFailure("preview-audio-invalid");
                yield break;
            }

            converted.name = "NeEEvA_Neural_HumBack_Prefix";
            ApplyHumBackGain(converted);
            if (m_PreparedHumBackPrefixClip != null) Destroy(m_PreparedHumBackPrefixClip);
            m_PreparedHumBackPrefixClip = converted;
            m_PreparedHumBackPrefixSourceSeconds = sourceSeconds;
            int actualShift;
            string shiftHeader = request.GetResponseHeader("X-SVC-Semitone-Shift") ?? "0";
            if (!int.TryParse(shiftHeader, out actualShift))
                actualShift = Mathf.Clamp(
                    m_HumSVCSemitoneShift + m_StreamingHumSemitoneOffset, -12, 12);
            m_PreparedHumBackPrefixSemitoneShift = actualShift;
            string device = request.GetResponseHeader("X-SVC-Device") ?? "unknown";
            m_PreparedHumBackPrefixWasCpu =
                device.IndexOf("cpu", StringComparison.OrdinalIgnoreCase) >= 0;
            string serverElapsed = request.GetResponseHeader("X-SVC-Elapsed-Seconds") ?? "?";
            m_PreparedHumBackPrefixDiagnostic =
                $"device={device}, shift={actualShift}, server={serverElapsed}s, " +
                $"total={Time.realtimeSinceStartup - startedAt:F2}s";
            if (m_LogHumBack)
                Debug.Log($"[HumBack/Streaming] 角色歌声开头已准备 " +
                          $"source={sourceSeconds:F2}s output={converted.length:F2}s " +
                          $"{m_PreparedHumBackPrefixDiagnostic}");

            if (m_FastHumBackEouStaged) BeginFastHumBackFromPreparedPrefix();
        }
    }

    /// <summary>
    /// Called at EOU before final ASR.  The complete source is retained privately;
    /// the prefix may start immediately, while full conversion and ASR overlap.
    /// </summary>
    private bool TryStageFastHumBackAtEou(AudioClip completeClip)
    {
        if (!m_EnableStreamingHumBackPrefix || completeClip == null ||
            !m_EouTurnWasSinging || m_EouCognitiveSpeechVeto ||
            !HasActiveSingAlongRequest() ||
            (!m_HumBackPrefixPreparing && m_PreparedHumBackPrefixClip == null) ||
            m_FastHumBackEouStaged || m_FastHumBackActive)
            return false;

        byte[] completeWav;
        try { completeWav = WavUtility.FromAudioClip(completeClip); }
        catch (Exception ex)
        {
            if (m_LogHumBack)
                Debug.LogWarning("[HumBack/Streaming] 完整WAV暂存失败: " + ex.Message);
            return false;
        }
        if (completeWav == null || completeWav.Length <= 44) return false;

        m_FastHumBackFullSourceWav = completeWav;
        m_FastHumBackFullSourceSeconds = completeClip.length;
        m_FastHumBackEouStaged = true;
        if (m_LogHumBack)
            Debug.Log($"[HumBack/Streaming] EOU已到，完整转换与最终ASR并行 " +
                      $"source={completeClip.length:F2}s prefixReady={m_PreparedHumBackPrefixClip != null}");
        if (m_PreparedHumBackPrefixClip != null) BeginFastHumBackFromPreparedPrefix();
        return true;
    }

    private void BeginFastHumBackFromPreparedPrefix()
    {
        if (!m_FastHumBackEouStaged || m_FastHumBackActive ||
            m_PreparedHumBackPrefixClip == null || m_FastHumBackFullSourceWav == null)
            return;

        GPTSoVITSFASTAPI characterVoice = m_ChatSettings != null
            ? m_ChatSettings.m_TextToSpeech as GPTSoVITSFASTAPI
            : null;
        string targetPath = characterVoice != null
            ? characterVoice.GetReferenceAudioPathForVoiceConversion()
            : "";
        if (m_AudioSource == null || string.IsNullOrWhiteSpace(targetPath)) return;

        int generation = ++m_HumBackGeneration;
        m_FastHumBackActive = true;
        m_FastHumBackPrefixPlaybackStarted = false;
        m_FastHumBackPrefixPlaybackDone = false;
        m_FastHumBackFullReady = false;
        m_FastHumBackFullPlaybackStarted = false;
        m_FastHumBackPlaybackComplete = false;
        m_HumBackPending = false;
        m_HumBackPreparingCarrier = true;
        m_HumBackPlaying = false;

        m_ActiveHumBackClip = m_PreparedHumBackPrefixClip;
        m_PreparedHumBackPrefixClip = null;
        StartCoroutine(RequestFastHumBackFull(
            generation,
            m_FastHumBackFullSourceWav,
            targetPath,
            m_PreparedHumBackPrefixSemitoneShift));

        if (m_PreparedHumBackPrefixWasCpu)
        {
            // CPU conversion is much slower than the playable prefix (57 s + 83 s
            // in the July 22 trace).  Starting the prefix immediately would promise
            // a continuation that cannot arrive in time, creating a minute-long gap.
            // Wait for the complete result, then play prefix + continuation without
            // any silence between them.
            if (m_LogHumBack)
                Debug.Log("[HumBack/Streaming] 前段使用CPU；后台等待完整结果。" +
                          "最终ASR与心里话门控确认前不会播放");
        }
        else
        {
            // On CUDA, give the complete conversion enough head start that its
            // measured 15 s model startup plus duration-dependent work is covered
            // before the 20-second prefix ends.  This estimate includes an 8-second
            // safety margin over the July 22 traces.
            float estimatedCompleteSeconds = 23f + 0.25f * Mathf.Max(
                0f, m_FastHumBackFullSourceSeconds);
            float continuationLeadSeconds = Mathf.Clamp(
                estimatedCompleteSeconds - m_PreparedHumBackPrefixSourceSeconds,
                4f,
                30f);
            m_FastHumBackStartCoroutine = StartCoroutine(
                StartFastHumBackPrefixAfterLead(generation, continuationLeadSeconds));
        }
    }

    private IEnumerator StartFastHumBackPrefixAfterLead(int generation, float leadSeconds)
    {
        float deadline = Time.realtimeSinceStartup + Mathf.Max(0f, leadSeconds);
        while (generation == m_HumBackGeneration && m_FastHumBackActive &&
               !m_FastHumBackFullReady && Time.realtimeSinceStartup < deadline)
            yield return null;
        m_FastHumBackStartCoroutine = null;
        if (generation != m_HumBackGeneration || !m_FastHumBackActive) yield break;
        StartFastHumBackPrefixPlayback(
            generation,
            m_FastHumBackFullReady ? "complete-ready" : $"cuda-lead-{leadSeconds:F1}s");
    }

    private void StartFastHumBackPrefixPlayback(int generation, string reason)
    {
        if (generation != m_HumBackGeneration || !m_FastHumBackActive ||
            !m_FastHumBackFinalDecisionReceived || !m_FastHumBackFinalConfirmed ||
            m_EouCognitiveSpeechVeto ||
            m_FastHumBackPrefixPlaybackStarted || m_ActiveHumBackClip == null ||
            m_AudioSource == null)
            return;

        m_FastHumBackPrefixPlaybackStarted = true;
        m_HumBackPlaying = true;
        IsAISpeaking = true;
        CancelPendingEouLatencyFiller("confirmed-streaming-hum-prefix", true);
        m_AudioSource.clip = m_ActiveHumBackClip;
        m_AudioSource.loop = false;
        m_AudioSource.time = 0f;
        m_AudioSource.Play();
        m_TextBack.text = "♪";
        SetAnimator("state", 2);
        if (m_LogHumBack)
            Debug.Log($"[HumBack/Streaming] 真正角色歌声开始（预转换开头） " +
                      $"length={m_ActiveHumBackClip.length:F2}s reason={reason} " +
                      $"{m_PreparedHumBackPrefixDiagnostic}");
        m_FastHumBackPlaybackCoroutine = StartCoroutine(WaitForFastHumBackPrefix(generation));
    }

    private IEnumerator RequestFastHumBackFull(
        int generation,
        byte[] sourceWav,
        string targetPath,
        int fixedSemitoneShift)
    {
        string requestId = Guid.NewGuid().ToString("N");
        WWWForm form = new WWWForm();
        form.AddBinaryData("source_audio", sourceWav, "complete_singing.wav", "audio/wav");
        form.AddField("target_path", targetPath);
        form.AddField("request_id", requestId);
        form.AddField("diffusion_steps", Mathf.Clamp(m_HumSVCDiffusionSteps, 4, 30));
        // Use the prefix's actual resolved shift.  Otherwise auto-F0 can choose a
        // different octave for the complete clip and make the hand-off audible.
        form.AddField("auto_f0_adjust", "false");
        form.AddField("semitone_shift", Mathf.Clamp(fixedSemitoneShift, -12, 12));
        form.AddField("performance_seed", m_StreamingHumPerformanceSeed);
        form.AddField("rms_mix_rate", InvariantFloat(m_StreamingHumRmsMixRate));
        form.AddField("protect", InvariantFloat(m_StreamingHumProtect));
        form.AddField("interpretation", InvariantFloat(m_StreamingHumInterpretation));
        form.AddField("max_seconds", m_HumBackMaxSeconds.ToString(
            "0.###", System.Globalization.CultureInfo.InvariantCulture));

        using (UnityWebRequest request = UnityWebRequest.Post(m_HumSVCURL, form))
        {
            request.downloadHandler = new DownloadHandlerAudioClip(m_HumSVCURL, AudioType.WAV);
            request.timeout = Mathf.Clamp(m_HumSVCTimeoutSeconds, 30, 600);
            m_ActiveHumSVCRequest = request;
            m_ActiveHumSVCRequestId = requestId;
            float startedAt = Time.realtimeSinceStartup;
            if (m_LogHumBack)
                Debug.Log($"[HumBack/Streaming] 完整转换已并行启动 bytes={sourceWav.Length} " +
                          $"fixedShift={fixedSemitoneShift}");
            yield return request.SendWebRequest();
            if (m_ActiveHumSVCRequest == request)
            {
                m_ActiveHumSVCRequest = null;
                m_ActiveHumSVCRequestId = "";
            }
            if (generation != m_HumBackGeneration || !m_FastHumBackActive) yield break;
            m_HumBackPreparingCarrier = false;

            if (request.result != UnityWebRequest.Result.Success ||
                !string.Equals(request.GetResponseHeader("X-SVC-Complete"), "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                FailFastHumBack(generation,
                    $"完整转换失败 HTTP={request.responseCode} error={request.error}");
                yield break;
            }

            AudioClip converted = null;
            try { converted = DownloadHandlerAudioClip.GetContent(request); }
            catch (Exception ex)
            {
                if (m_LogHumBack)
                    Debug.LogWarning("[HumBack/Streaming] 完整WAV解码失败: " + ex.Message);
            }
            if (converted == null || converted.length <= m_PreparedHumBackPrefixSourceSeconds + 0.05f)
            {
                if (converted != null) Destroy(converted);
                FailFastHumBack(generation, "完整转换结果过短，无法接续预转换开头");
                yield break;
            }

            converted.name = "NeEEvA_Neural_HumBack_Complete";
            ApplyHumBackGain(converted);
            m_FastHumBackFullClip = converted;
            m_FastHumBackFullReady = true;
            string device = request.GetResponseHeader("X-SVC-Device") ?? "unknown";
            string serverElapsed = request.GetResponseHeader("X-SVC-Elapsed-Seconds") ?? "?";
            if (m_LogHumBack)
                Debug.Log($"[HumBack/Streaming] 完整角色歌声已准备 " +
                          $"length={converted.length:F2}s device={device} " +
                          $"server={serverElapsed}s total={Time.realtimeSinceStartup - startedAt:F2}s");
            if (!m_FastHumBackPrefixPlaybackStarted)
                StartFastHumBackPrefixPlayback(generation, "complete-ready");
            else if (m_FastHumBackPrefixPlaybackDone)
                StartFastHumBackContinuation(generation);
        }
    }

    private IEnumerator WaitForFastHumBackPrefix(int generation)
    {
        yield return null;
        while (generation == m_HumBackGeneration && m_FastHumBackActive &&
               m_AudioSource != null && m_AudioSource.isPlaying)
            yield return null;
        m_FastHumBackPlaybackCoroutine = null;
        if (generation != m_HumBackGeneration || !m_FastHumBackActive) yield break;
        m_FastHumBackPrefixPlaybackDone = true;
        if (m_FastHumBackFullReady)
        {
            StartFastHumBackContinuation(generation);
        }
        else
        {
            m_TextBack.text = "♪ …";
            SetAnimator("state", 1);
            if (m_LogHumBack)
                Debug.Log("[HumBack/Streaming] 开头播放完毕，等待完整转换的接续片段");
        }
    }

    private void StartFastHumBackContinuation(int generation)
    {
        if (generation != m_HumBackGeneration || !m_FastHumBackActive ||
            m_FastHumBackFullPlaybackStarted || m_FastHumBackFullClip == null ||
            m_AudioSource == null)
            return;

        AudioClip prefix = m_ActiveHumBackClip;
        m_ActiveHumBackClip = m_FastHumBackFullClip;
        m_FastHumBackFullClip = null;
        m_FastHumBackFullPlaybackStarted = true;
        float resumeAt = Mathf.Clamp(
            m_PreparedHumBackPrefixSourceSeconds - Mathf.Max(0f, m_HumBackPrefixOverlapSeconds),
            0f,
            Mathf.Max(0f, m_ActiveHumBackClip.length - 0.05f));
        m_AudioSource.clip = m_ActiveHumBackClip;
        m_AudioSource.time = resumeAt;
        m_AudioSource.loop = false;
        m_AudioSource.Play();
        m_TextBack.text = "♪";
        SetAnimator("state", 2);
        if (prefix != null && prefix != m_ActiveHumBackClip) Destroy(prefix);
        if (m_LogHumBack)
            Debug.Log($"[HumBack/Streaming] 已接续完整结果 from={resumeAt:F2}s " +
                      $"remaining={m_ActiveHumBackClip.length - resumeAt:F2}s");
        m_FastHumBackPlaybackCoroutine = StartCoroutine(WaitForFastHumBackComplete(generation));
    }

    private IEnumerator WaitForFastHumBackComplete(int generation)
    {
        yield return null;
        while (generation == m_HumBackGeneration && m_FastHumBackActive &&
               m_AudioSource != null && m_AudioSource.isPlaying)
            yield return null;
        m_FastHumBackPlaybackCoroutine = null;
        if (generation != m_HumBackGeneration || !m_FastHumBackActive) yield break;
        m_FastHumBackPlaybackComplete = true;
        if (m_FastHumBackFinalDecisionReceived && m_FastHumBackFinalConfirmed)
            FinishHumBack(generation, true, "streaming-prefix + complete neural SVC");
    }

    private void FailFastHumBack(int generation, string detail)
    {
        if (generation != m_HumBackGeneration || !m_FastHumBackActive) return;
        if (m_LogHumBack) Debug.LogWarning("[HumBack/Streaming] " + detail);
        if (m_FastHumBackFinalDecisionReceived && m_FastHumBackFinalConfirmed)
            FinishHumBack(generation, false, detail);
        else
            RejectFastHumBackAfterFinal(detail);
    }

    private void FallbackAfterStreamingPrefixFailure(string reason)
    {
        if (!m_FastHumBackEouStaged || m_FastHumBackActive) return;
        m_FastHumBackEouStaged = false;
        m_FastHumBackFullSourceWav = null;
        m_FastHumBackFullSourceSeconds = 0f;
        if (!m_FastHumBackFinalDecisionReceived || !m_FastHumBackFinalConfirmed) return;

        QueueHumBack(new AgentHumBackRequest
        {
            Mode = "echo",
            Reason = "streaming-prefix-fallback:" + reason,
        });
        if (!m_HumBackPending || !TryBeginPendingHumBack())
        {
            RecordHumBackResult("失败：前段预转换失败，而且完整回唱也未能启动。不得声称已经唱出。", true);
            m_AgentRoundInFlight = false;
            OnAgentRoundComplete();
        }
    }

    /// <summary>
    /// Runs after SendDataInternal has appended the authoritative final user turn.
    /// Publish playable captures or retained evidence before source confirmation
    /// and the role's perception frame; this method never schedules singing.
    /// </summary>
    private void CommitConfirmedSingingBeforePerception()
    {
        // Availability is a perception fact, independent of permission to sing.
        // Conflicting/uncertain evidence remains in the existing quarantine path.
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        if (sense == null) return;
        if (IsCurrentTurnConfirmedSinging() && !m_FinalModeSoftDowngrade &&
            !(m_EouCognitiveSpeechVeto && !FinalEvidenceOverridesSpeculation()) &&
            sense.CommitRecentSingingToPracticeSession(out int count) && m_LogHumBack)
            Debug.Log($"[HumBack/Practice] 感知帧前已记录最终确认片段 sequence={count}");
        // Commit can fail when clean is <3s even though expanded contains the song.
        // Also run after a speech veto: retaining evidence is not permission to sing.
        sense.RetainCurrentSingingEvidenceClip(
            m_StreamingSemanticSingingObservedThisTurn || m_FinalModeVerdict == "singing" ||
            m_PendingSingingConfirmation);
    }

    private bool TryHandleDirectSingAlongTurn()
    {
        if (!CanExecuteSkillAction("singing", out string skillBlockReason))
        {
            RejectFastHumBackAfterFinal("singing-skill-not-authorized");
            if (m_LogHumBack)
                Debug.Log("[HumBack] Skill 权限未放行，跳过自动复唱：" + skillBlockReason);
            return false;
        }
        if (m_EouCognitiveSpeechVeto && !FinalEvidenceOverridesSpeculation())
        {
            RejectFastHumBackAfterFinal("speculative-cognition-classified-speech");
            if (m_LogHumBack)
                Debug.Log("[HumBack] 心里话高置信度判断为普通说话；跳过自动复唱，交给LLM正常回应");
            return false;
        }
        //语义/声学任一方向冲突时都不走程序自动复唱。素材尽量暂存，双方证据已经由
        //DealingTextCallback 交给正式 LLM；是否确认、怎样问，由角色结合语境决定。
        if (m_FinalModeSoftDowngrade)
        {
            RejectFastHumBackAfterFinal("singing-mode-evidence-conflict");
            if (m_LogHumBack)
                Debug.Log("[HumBack] 语义与声学证据冲突；跳过程序自动复唱，交给角色确认");
            return false;
        }
        if (IsCurrentTurnSpokenSingingExit())
        {
            RejectFastHumBackAfterFinal("singing-ended-with-spoken-exit");
            if (m_LogHumBack)
                Debug.Log("[HumBack] 混合轮以口语收束；干净歌唱岛已在感知阶段保留，" +
                          "这里只跳过程序自动复唱并交给LLM决定后续动作");
            return false;
        }
        bool confirmedSinging = IsCurrentTurnConfirmedSinging();
        //Only an explicit role tool action may establish a standing follow request.
        //Neither lyrics nor perception/boundary prose can arm it by substring matching.
        // Closing a quiet capture is not permission to override the role's choice to wait.
        // Confirmed songs were committed before perception; execution is separate.
        bool armed = HasActiveSingAlongRequest() && m_StalledTurnAction != "continue";

        if (confirmedSinging && armed)
        {
            SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
                ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
                : null;
            TryAbortHumBackOnRetraction(senseVoice);
        }

        // A preview request can fail before final ASR arrives.  Drop only the staged
        // preview state here and continue into the ordinary complete-GPU fast path.
        if (m_FastHumBackEouStaged && !m_FastHumBackActive &&
            !m_HumBackPrefixPreparing && m_PreparedHumBackPrefixClip == null)
        {
            m_FastHumBackEouStaged = false;
            m_FastHumBackFullSourceWav = null;
            m_FastHumBackFullSourceSeconds = 0f;
        }

        if (m_FastHumBackEouStaged || m_FastHumBackActive)
        {
            if (!confirmedSinging || !armed)
            {
                RejectFastHumBackAfterFinal("final-asr-rejected-streaming-singing");
                return false;
            }

            // 快速回唱是从录音第 0 秒开始预转换的（预转换开头 20s + EOU 时整段并行
            // 转换），压延迟的代价是它完全不经过岛裁剪。这在【纯唱歌】轮没问题，
            // 在【说话+唱歌】轮就会把说话原样播出去：8/10 实测一轮
            // raw=23.15s、裁剪窗口只有 2.53-4.17s，却播了 19.98+3.22=23.2 秒，
            // 「好，那我再来一次哦」连同整首歌一起复读了出来。
            // 最终裁剪一说要丢头，预转换的那份就是废的——撤掉，走正常裁剪路径。
            SenseVoiceSpeechToText cropSource = m_ChatSettings != null
                ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
                : null;
            float finalHeadCrop = cropSource != null
                ? cropSource.LastResponseAudioCropSeconds : 0f;
            float finalTailDrop = cropSource != null
                ? cropSource.LastResponseAudioTailDropSeconds : 0f;
            //两头都要看。原来只查了头，8/25 那轮说话正好在尾巴上：
            //岛=0~10.17s、录音 20.95s，快速路径把整条 21 秒播了出去，
            //后半段是用户自己那句「歌词唱错了，歌词唱错了」被转成她的声线复读。
            //这条闸不靠猜也不问 LLM——岛裁剪早就算出了正确答案，只是没人看。
            if (m_LogHumBack)
                Debug.Log($"[HumBack/Streaming] 最终裁剪 头={finalHeadCrop:F2}s " +
                          $"尾={finalTailDrop:F2}s " +
                          $"(上限 {k_FastHumBackMaxHeadCropSeconds:F2}/" +
                          $"{k_FastHumBackMaxTailDropSeconds:F2})");
            if (finalHeadCrop > k_FastHumBackMaxHeadCropSeconds)
            {
                RejectFastHumBackAfterFinal(
                    $"final-crop-discards-head({finalHeadCrop:F2}s)；" +
                    "预转换从第0秒起，含最终判定要丢掉的说话");
                return false;
            }
            if (finalTailDrop > k_FastHumBackMaxTailDropSeconds)
            {
                RejectFastHumBackAfterFinal(
                    $"final-crop-discards-tail({finalTailDrop:F2}s)；" +
                    "预转换播到录音结尾，含最终判定要丢掉的说话");
                return false;
            }

            m_FastHumBackFinalDecisionReceived = true;
            m_FastHumBackFinalConfirmed = true;
            RefreshActiveSingAlongSession();
            m_HumBackNeedsHistoryEntry = m_ChatHistory != null && m_ChatHistory.Count % 2 == 1;
            CancelPendingEouLatencyFiller("direct-confirmed-sing-along", true);

            if (!m_FastHumBackActive && m_PreparedHumBackPrefixClip != null)
                BeginFastHumBackFromPreparedPrefix();
            if (m_FastHumBackActive && !m_FastHumBackPrefixPlaybackStarted &&
                (m_FastHumBackFullReady ||
                 (!m_PreparedHumBackPrefixWasCpu && m_FastHumBackStartCoroutine == null)))
            {
                StartFastHumBackPrefixPlayback(
                    m_HumBackGeneration,
                    m_FastHumBackFullReady ? "final-confirmed-complete-ready" : "final-confirmed");
            }
            if (m_FastHumBackPlaybackComplete && m_FastHumBackActive)
                FinishHumBack(m_HumBackGeneration, true,
                    "streaming-prefix + complete neural SVC");
            else
            {
                IsAISpeaking = true;
                m_TextBack.text = m_AudioSource != null && m_AudioSource.isPlaying ? "♪" : "♪ …";
                SetAnimator("state", m_AudioSource != null && m_AudioSource.isPlaying ? 2 : 1);
            }
            if (m_LogHumBack)
                Debug.Log("[HumBack/Streaming] 最终ASR确认歌唱；跳过LLM决策，继续真实回唱");
            return true;
        }

        if (!confirmedSinging || !armed || !HasRecentPlayableSingingPerformance()) return false;
        QueueHumBack(new AgentHumBackRequest { Mode = "echo", Reason = "armed-sing-along-fast-path" });
        if (!m_HumBackPending) return false;
        CancelPendingEouLatencyFiller("direct-confirmed-sing-along", true);
        bool started = TryBeginPendingHumBack();
        if (started && m_LogHumBack)
            Debug.Log("[HumBack] 最终ASR确认歌唱；跳过LLM决策，直接启动完整GPU回唱");
        if (started) TryComposeHumBackPrelude();
        return started;
    }

    /// <summary>
    /// 这条快车道为了省延迟跳过了 LLM，代价是转换那十几~五十秒里她一个字都不说，
    /// 用户不知道到底有没有在生成（8/11 实测 ASR 8.85s + SVC 42.8s ≈ 52 秒静默）。
    /// 这里让她自己决定要不要垫一句、垫什么，与转换并行，不占首音延迟。
    ///
    /// 两条约束都是用户定的：
    ///  · 不要每次都出声——交给她判断（提示词里给了"没什么可说就回 -"的出口），
    ///    再加一条硬底线：不允许连着两次，防止"每次她都觉得值得说"退化成每次都说。
    ///  · 允许说别的——不限于"我在准备"，可以顺带评价刚才那段。
    /// 说出来的话进对话历史，否则她下一轮可能重复同样的观察。
    /// </summary>
    private void TryComposeHumBackPrelude()
    {
        if (m_HumBackPreludeSpokenLastTime) { m_HumBackPreludeSpokenLastTime = false; return; }
        ChatQW qw = m_ChatSettings != null ? m_ChatSettings.m_ChatModel as ChatQW : null;
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText : null;
        if (qw == null || m_ChatSettings == null ||
            m_ChatSettings.m_TextToSpeech == null || m_AudioSource == null) return;

        int generation = m_HumBackGeneration;
        string lyrics = senseVoice != null ? senseVoice.LastSegmentLyrics : "";
        string context = senseVoice != null ? senseVoice.LastText : "";
        string recent = string.Join(" / ", m_RecentHumBackPreludes);
        qw.ComposeHumBackPrelude(lyrics, context, recent, line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            m_RecentHumBackPreludes.Add(line);
            while (m_RecentHumBackPreludes.Count > 4) m_RecentHumBackPreludes.RemoveAt(0);
            //回哼已经开始播或已经结束就别再插话了——那只会盖在歌声上
            if (generation != m_HumBackGeneration || m_HumBackPlaying ||
                (m_AudioSource != null && m_AudioSource.isPlaying)) return;
            m_HumBackPreludeSpokenLastTime = true;
            if (m_ChatHistory != null) m_ChatHistory.Add(line);
            m_TextBack.text = line;
            m_ChatSettings.m_TextToSpeech.Speak(line, (clip, spoken) =>
            {
                if (clip == null || generation != m_HumBackGeneration ||
                    m_HumBackPlaying || m_AudioSource == null) return;
                if (m_AudioSource.isPlaying) return;
                m_AudioSource.clip = clip;
                m_AudioSource.Play();
            });
            if (m_LogHumBack) Debug.Log($"[HumBack] 转换期间垫场：\"{line}\"");
        });
    }

    /// <summary>
    /// 唱完之后那句话若是在作废刚才那段演唱，就中止还在跑的歌声合成。
    ///
    /// 与合成**并行**：转换十几秒、判定不到一秒，藏得住，不占首音延迟。
    /// 8/10 实测那一轮的尾巴是「呃，后面好像有点唱错了，停一下停一下，这一段不算
    /// 这一段不算，我们重新唱。」——整轮 ASR 只转出了开头 6.5 秒，这句话此前没有
    /// 任何子系统看得见；当时是靠用户抢话(barge-in)才没播出去。
    ///
    /// 只在还没开始播时中止。已经在放了就让它放完——中途掐断更难听，而且用户一
    /// 开口 barge-in 本来就会停。
    /// </summary>
    private void TryAbortHumBackOnRetraction(SenseVoiceSpeechToText senseVoice)
    {
        string tail = senseVoice != null ? senseVoice.LastSingingTailText : "";
        if (string.IsNullOrWhiteSpace(tail)) return;
        ChatQW qw = m_ChatSettings != null
            ? m_ChatSettings.m_ChatModel as ChatQW : null;
        if (qw == null) return;
        qw.ClassifySingingRetraction(tail, discard =>
        {
            if (!discard) return;
            if (m_HumBackPlaying || m_FastHumBackPrefixPlaybackStarted) return;
            bool stillWorking = IsSingingRuntimeActivityActive();
            if (!stillWorking) return;
            CancelPendingHumBack("user-retracted-take", false);
            if (m_LogHumBack)
                Debug.Log($"[HumBack] 唱完那句判为作废刚才的演唱，已中止合成：\"{tail}\"");
        });
    }

    private void RejectFastHumBackAfterFinal(string reason)
    {
        if (!m_FastHumBackEouStaged && !m_FastHumBackActive) return;
        if (m_LogHumBack)
            Debug.LogWarning("[HumBack/Streaming] 快速回唱已撤销: " + reason);
        CancelPendingHumBack(reason, false);
    }

    private void ResetStreamingHumBackPrefix(string reason, bool cancelRequest)
    {
        m_HumBackPrefixGeneration++;
        if (cancelRequest && m_HumBackPrefixSVCRequest != null)
        {
            string requestId = m_HumBackPrefixSVCRequestId;
            m_HumBackPrefixSVCRequest.Abort();
            m_HumBackPrefixSVCRequest = null;
            m_HumBackPrefixSVCRequestId = "";
            if (!string.IsNullOrEmpty(requestId)) StartCoroutine(CancelNeuralHumSVC(requestId));
        }
        m_HumBackPrefixPreparing = false;
        if (m_PreparedHumBackPrefixClip != null)
        {
            Destroy(m_PreparedHumBackPrefixClip);
            m_PreparedHumBackPrefixClip = null;
        }
        m_PreparedHumBackPrefixSourceSeconds = 0f;
        m_PreparedHumBackPrefixSemitoneShift = 0;
        m_PreparedHumBackPrefixDiagnostic = "";
        m_PreparedHumBackPrefixWasCpu = false;
        m_StreamingHumPerformanceSeed = 1234;
        m_StreamingHumSemitoneOffset = 0;
        m_StreamingHumRmsMixRate = 0.85f;
        m_StreamingHumProtect = 0.33f;
        m_StreamingHumInterpretation = 0.5f;
        if (!m_FastHumBackActive)
        {
            m_FastHumBackEouStaged = false;
            m_FastHumBackFullSourceWav = null;
            m_FastHumBackFullSourceSeconds = 0f;
        }
        if (m_LogHumBack && !string.IsNullOrEmpty(reason) && reason != "new-user-turn")
            Debug.Log("[HumBack/Streaming] 已清理预转换: " + reason);
    }

    private int NextHumPerformanceSeed()
    {
        unchecked
        {
            m_HumPerformanceCounter++;
            int mixed = (Environment.TickCount * 397) ^
                (m_HumPerformanceCounter * 7919) ^
                Guid.NewGuid().GetHashCode();
            // Unity's int hash can be negative, while NumPy's legacy RNG accepts
            // only unsigned 32-bit seeds. Keep the shared tool seed in the
            // positive Int32 range so every renderer receives the same valid value.
            int seed = mixed & 0x7FFFFFFF;
            return seed == 0 ? 1 : seed;
        }
    }

    private static void CreateHumPerformanceProfile(
        int seed,
        out int semitoneOffset,
        out float rmsMixRate,
        out float protect,
        out float interpretation)
    {
        System.Random random = new System.Random(seed);
        // Most human repeats stay in the same key. Rare +/-1 semitone variants add a
        // different placement without changing the melody; dynamics/timbre vary every take.
        double keyChoice = random.NextDouble();
        semitoneOffset = keyChoice < 0.10 ? -1 : keyChoice > 0.90 ? 1 : 0;
        // rms_mix_rate 决定输出音量包络多大程度上采用角色模型自己的包络。取值偏低时
        // 输出跟随合成源那条几乎无起伏的包络，听感是"有气无力"。实测 0.31 比 0.85 低
        // 约 2dB，主观差异明显；0.85 起听感恢复正常，故把随机区间整体抬到高位。
        rmsMixRate = 0.80f + (float)random.NextDouble() * 0.12f;
        protect = 0.29f + (float)random.NextDouble() * 0.09f;
        // 演唱表情强度：变声器方案保留用户的节奏与咬字，这一项让音高表现换成她自己的
        // （持续音上的揉音、更准的音准、缓慢的音高游移）。0 等于逐帧复刻用户的演唱。
        // 实测 0.35 与 0.7 都自然，故每次演唱在这个区间内取值，让每一遍都是新的一次。
        interpretation = 0.33f + (float)random.NextDouble() * 0.40f;
    }

    private static string InvariantFloat(float value)
    {
        return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsPracticeHumMode(string mode)
    {
        string lower = (mode ?? "").Trim().ToLowerInvariant();
        return lower == "practice" || lower == "compose" || lower == "sequence" ||
            lower == "session" || lower == "full";
    }

    private static string NormalizeSingingMaterialSource(string source)
    {
        string lower = (source ?? "").Trim().ToLowerInvariant();
        if (lower.Length == 0) return "";
        if (lower == "recent_turn" || lower == "recent" || lower == "current" ||
            lower == "echo")
            return "recent_turn";
        if (lower == "practice" || lower == "session") return "practice";
        if (lower == "library" || lower == "catalog" || lower == "memory")
            return "library";
        return "";
    }

    private static string ValidateHumBackMaterialSource(AgentHumBackRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Source)) return "";
        string original = request.Source;
        string source = NormalizeSingingMaterialSource(original);
        if (source.Length == 0)
            return $"source=\"{original}\" 不是已知歌唱素材源；" +
                   "只能使用 recent_turn、practice 或 library。";
        request.Source = source;

        string mode = (request.Mode ?? "echo").Trim().ToLowerInvariant();
        if (mode == "follow")
            return "hum_back mode=\"follow\" 只建立下一段轮唱约定，不读取任何素材，" +
                   "不应填写 source。";
        string expected = IsPracticeHumMode(mode) ? "practice" : "recent_turn";
        if (source != expected)
            return $"hum_back mode=\"{request.Mode}\" 读取 source={expected}，" +
                   $"但本次写的是 source={source}；程序没有替换素材。";
        return "";
    }

    private static bool RequiresExplicitPracticeOrder(
        string mode,
        string order,
        int practicePhraseCount)
    {
        return IsPracticeHumMode(mode) && string.IsNullOrWhiteSpace(order) &&
               practicePhraseCount > 1;
    }

    private static string BuildMissingPracticeOrderFailure(int practicePhraseCount)
    {
        int count = Mathf.Max(2, practicePhraseCount);
        return $"练唱会话有 {count} 段，但 practice 没有显式 order。" +
               "系统不会再把空 order 猜成全部；" +
               "请按用户语境列出准确段号，拿不准就先询问。";
    }

    //查不到就重试同一个 id 是死循环，拦下之后要给她能走的下一步。
    private const float k_SongSingRetryBlockSeconds = 90f;

    /// <summary>这一次要唱的东西的身份。song_sing 按曲库目标，hum_back 按模式与段序。</summary>
    /// <summary>
    /// Loop 重启后，练唱会话到底变成了什么样——只有她读到这句，才知道自己笔记里的
    /// 段号还作不作数。什么都没变时返回空串，不占帧。
    /// </summary>
    private static string BuildPracticeCarryNote(int kept, int dropped)
    {
        if (dropped <= 0) return "";
        if (kept > 0)
            return $"\n练唱会话变动: 刚才重启了一次实时模式，最早的 {dropped} 段因为太久远被清掉了，" +
                   $"剩下 {kept} 段的 clip 身份保持不变；已移除的引用不会指向其它录音。" +
                   "请按最新素材清单选择 refs，不再按显示段号猜测。";
        return $"\n练唱会话变动: 刚才重启了一次实时模式，之前那 {dropped} 段因为太久远已经全部清掉，" +
               "现在练唱会话是空的。你笔记里记的段号（第3段、第5段之类）**全部作废**，" +
               "旧引用已不可用，不会改指向新素材。" +
               "想唱用户的旋律就请他再唱一遍；" +
               "如果那段旋律你之前存进过长期曲库，可以改用 <song_sing/>。" +
               "**不要说「我这就唱」然后什么都没发生**，也不要以为自己的记忆坏了——" +
               "是会话被重启清掉了，不是你出了故障。";
    }

    private static string BuildSungTargetKey(string kind, string detail)
    {
        return (kind + "|" + (detail ?? "").Trim()).ToLowerInvariant();
    }

    /// <summary>
    /// 刚唱过同样的东西、而用户一直没开口 → 拦下。附上已经唱了几次。
    /// </summary>
    private bool ShouldBlockRepeatSinging(string targetKey, out string reason)
    {
        reason = "";
        if (string.IsNullOrEmpty(targetKey) || targetKey != m_LastSungTargetKey) return false;
        if (m_UserSpokeSinceLastSing) return false;
        if (Time.realtimeSinceStartup - m_LastSungAt >= k_RepeatSingBlockSeconds) return false;
        reason =
            $"未执行：你 {Mathf.RoundToInt(Time.realtimeSinceStartup - m_LastSungAt)} 秒前" +
            $"刚唱过完全一样的内容（同一段已经唱了 {m_LastSungRepeatCount} 次），" +
            "而用户从那以后一句话都没说。他没有要求再唱一遍——重复唱只会打断他。" +
            "**先等他开口**；如果你只是想说点什么，说话就好，不要再调演唱工具。";
        return true;
    }

    /// <summary>唱成功之后记下这一次唱的是什么，供重复判定与"第几次"提示使用。</summary>
    private void NoteSungTarget(string targetKey)
    {
        if (string.IsNullOrEmpty(targetKey)) return;
        if (targetKey == m_LastSungTargetKey && !m_UserSpokeSinceLastSing)
            m_LastSungRepeatCount++;
        else
            m_LastSungRepeatCount = 1;
        m_LastSungTargetKey = targetKey;
        m_LastSungAt = Time.realtimeSinceStartup;
        m_UserSpokeSinceLastSing = false;
    }

    private static string BuildSongSingKey(string mode, string songId, string title)
    {
        return ((mode ?? "") + "|" + (songId ?? "").Trim() + "|" + (title ?? "").Trim())
            .ToLowerInvariant();
    }

    private static SongSingFailureKind ClassifySongSingFailureCode(string errorCode)
    {
        switch ((errorCode ?? "").Trim().ToLowerInvariant())
        {
            case "not_found": return SongSingFailureKind.NotFound;
            case "invalid_request":
            case "invalid_selector": return SongSingFailureKind.InvalidSelector;
            case "audio_missing":
            case "no_playable_audio":
            case "invalid_audio": return SongSingFailureKind.AudioMissing;
            case "duration_limit": return SongSingFailureKind.DurationLimit;
            case "transport": return SongSingFailureKind.Transport;
            case "server_unavailable":
            case "server_error": return SongSingFailureKind.Server;
            default: return SongSingFailureKind.Other;
        }
    }

    private static bool IsStableSongSingFailure(SongSingFailureKind kind)
    {
        return kind == SongSingFailureKind.NotFound ||
            kind == SongSingFailureKind.InvalidSelector ||
            kind == SongSingFailureKind.IdentityMismatch;
    }

    private void ClearSongSingFailureCache()
    {
        m_LastFailedSongSingKey = "";
        m_LastFailedSongSingKind = SongSingFailureKind.None;
        m_LastFailedSongSingId = "";
        m_LastFailedSongSingTitle = "";
        m_LastFailedSongSingDetail = "";
        m_LastFailedSongSingTime = -999f;
    }

    private void RememberSongSingFailure(
        string key,
        SongSingFailureKind kind,
        string songId,
        string title,
        string detail)
    {
        // 只有对同一参数重试仍必然失败的结果才防重试。网络波动、服务异常、音频损坏
        // 和时长策略都不是“曲库里没有”，不能污染下一轮的事实。
        if (!IsStableSongSingFailure(kind))
        {
            if (key == m_LastFailedSongSingKey) ClearSongSingFailureCache();
            return;
        }
        m_LastFailedSongSingKey = key ?? "";
        m_LastFailedSongSingKind = kind;
        m_LastFailedSongSingId = (songId ?? "").Trim();
        m_LastFailedSongSingTitle = (title ?? "").Trim();
        m_LastFailedSongSingDetail = detail ?? "";
        m_LastFailedSongSingTime = Time.realtimeSinceStartup;
    }

    private string BuildSongSingRetryBlockMessage(string songSingKey)
    {
        string reason;
        switch (m_LastFailedSongSingKind)
        {
            case SongSingFailureKind.IdentityMismatch:
                reason = "上次曲库解析成了另一首歌；原样重试仍有唱错歌的风险";
                break;
            case SongSingFailureKind.InvalidSelector:
                reason = "上次使用的曲库选择参数无效；原样重试不会改变结果";
                break;
            default:
                reason = "上次实时曲库查询没有找到这个目标；原样重试不会改变结果";
                break;
        }
        string detail = string.IsNullOrWhiteSpace(m_LastFailedSongSingDetail)
            ? ""
            : " 上次返回：" + TruncateForFrame(m_LastFailedSongSingDetail, 140) + "。";
        return $"未执行：这个目标（{TruncateForFrame(songSingKey, 60)}）{reason}。" +
            detail +
            "结果事实：本次没有唱出，也没有音频正在加载。可重新查看曲库并使用其中的完整 id，" +
            "也可以改用当前练唱会话里确实存在的片段。";
    }

    private bool CatalogProvesLastFailedSongExists(
        SenseVoiceSpeechToText.SongCatalogInspectionResult result)
    {
        if (result == null || !result.Ok || result.Entries == null ||
            m_LastFailedSongSingKind != SongSingFailureKind.NotFound)
            return false;
        foreach (SenseVoiceSpeechToText.SongCatalogEntry entry in result.Entries)
        {
            if (entry == null) continue;
            if (!string.IsNullOrWhiteSpace(m_LastFailedSongSingId) &&
                string.Equals(entry.song_id, m_LastFailedSongSingId,
                    StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(m_LastFailedSongSingTitle) &&
                (AreSongTitlesCompatible(m_LastFailedSongSingTitle, entry.title) ||
                 AreSongTitlesCompatible(m_LastFailedSongSingTitle, entry.display_name)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// id 长得像感知帧里的显示截断时点破它——这是 8/22 那八次失败的全部原因。
    /// </summary>
    private static string BuildSongSingIdHint(string songId)
    {
        string id = (songId ?? "").Trim();
        if (id.Length == 0 || id.Length >= 12) return "";
        bool hex = true;
        foreach (char ch in id)
            if (!Uri.IsHexDigit(ch)) { hex = false; break; }
        if (!hex) return "";
        return $" 另外：\"{id}\" 只有 {id.Length} 位，曲库 id 是 12 位的——" +
               "你多半是把显示用的短写当成 id 了。用候选行 id= 后面的完整那一串。";
    }

    /// <summary>
    /// 手上有没有还能回唱的东西：最近一段仍在保留期内的演唱，或者练唱会话里的片段。
    /// </summary>
    private bool HasRecentSingableMaterial()
    {
        SenseVoiceSpeechToText sense = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (sense == null) return false;
        if (sense.PracticePhraseCount > 0) return true;
        return sense.TryGetRecentSingingPerformance(
            out float[] timeline, out float _, out string __) &&
            timeline != null && timeline.Length > 0;
    }

    /// <summary>
    /// 把三种容易混淆的素材源作为短事实交给角色。它只提供系统当前真正持有的
    /// 信息，不替角色决定要不要唱；尤其不把旋律相似的曲库候选当成用户刚录的片段。
    /// </summary>
    private string BuildSingingMaterialFacts(SenseVoiceSpeechToText sense)
    {
        if (sense == null) return "";
        if (!GetSkillRouteState("singing").ActiveThisRound &&
            !IsCurrentTurnConfirmedSinging())
            return sense.PracticePhraseCount > 0 || sense.DescribeQuarantinedSingingCandidates().Count > 0
                ? BuildSingingClipOverview(sense) : "";
        return BuildUnifiedClipFacts(sense);
    }

    private static void AppendBoundarySubsegments(
        System.Text.StringBuilder sb,
        string label,
        SenseVoiceSpeechToText.SingingBoundarySubsegment[] segments)
    {
        if (sb == null || segments == null || segments.Length == 0) return;
        sb.Append(' ').Append(label).Append("=[");
        bool wrote = false;
        for (int i = 0; i < segments.Length; i++)
        {
            SenseVoiceSpeechToText.SingingBoundarySubsegment segment = segments[i];
            if (segment == null) continue;
            if (wrote) sb.Append("; ");
            wrote = true;
            sb.Append(segment.expanded_start_seconds.ToString("F2"))
              .Append('~')
              .Append(segment.expanded_end_seconds.ToString("F2"))
              .Append("s/")
              .Append(string.IsNullOrWhiteSpace(segment.type)
                  ? "unknown" : segment.type)
              .Append("/voiced=")
              .Append(segment.voiced_ratio.ToString("F2"))
              .Append("/smooth=")
              .Append(segment.pitch_smooth_ratio.ToString("F2"));
        }
        sb.Append("]（时间相对 expanded 起点；聚合转写属于整个 extra；" +
                  "窗口只提供细粒度声学证据，不替你认定说话或歌唱）");
    }

    private static void AppendBoundaryReviewConflict(
        System.Text.StringBuilder sb,
        string label,
        bool required,
        string aggregateType,
        float melodicSeconds,
        float melodicRatio,
        float longestRunSeconds)
    {
        if (sb == null || !required) return;
        sb.Append(" 边界冲突=")
          .Append(label)
          .Append(" 的聚合类型是 ")
          .Append(string.IsNullOrWhiteSpace(aggregateType)
              ? "unknown" : aggregateType)
          .Append("，但带时间戳窗口含 ")
          .Append(melodicSeconds.ToString("F2"))
          .Append("s 旋律证据（占比=")
          .Append(melodicRatio.ToString("F2"))
          .Append("，最长连续=")
          .Append(longestRunSeconds.ToString("F2"))
          .Append("s）。两份证据都不构成最终裁决；请结合歌词、用户原话和完整语境" +
                  "自主选择 clean/expanded/精确裁剪，拿不准可以先询问用户。");
    }

    private static string ExtractBracketedSongTitle(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"《(?<title>[^》\r\n]{1,80})》|[「『](?<title>[^」』\r\n]{1,80})[」』]",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["title"].Value.Trim() : "";
    }

    private static string NormalizeSongTitleForComparison(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var normalized = new System.Text.StringBuilder(title.Length);
        foreach (char ch in title)
        {
            if (!char.IsLetterOrDigit(ch)) continue;
            normalized.Append(char.ToLowerInvariant(ch));
        }
        return normalized.ToString();
    }

    private static bool TextContainsNormalizedSongTitle(string text, string title)
    {
        string haystack = NormalizeSongTitleForComparison(text);
        string needle = NormalizeSongTitleForComparison(title);
        return needle.Length >= 2 && haystack.Contains(needle);
    }

    private static bool AreSongTitlesCompatible(string expected, string actual)
    {
        string left = NormalizeSongTitleForComparison(expected);
        string right = NormalizeSongTitleForComparison(actual);
        if (left.Length == 0 || right.Length == 0) return false;
        return left == right || (left.Length >= 4 && right.Contains(left)) ||
            (right.Length >= 4 && left.Contains(right));
    }

    private static string ResolveExpectedSongTitle(
        string requestTitle,
        string userText,
        string toolReason)
    {
        if (IsPlausibleUnquotedSongTitle(requestTitle)) return requestTitle.Trim();
        string expected = ExtractBracketedSongTitle(userText);
        if (string.IsNullOrWhiteSpace(expected)) expected = ExtractExplicitSongTitle(userText);
        if (string.IsNullOrWhiteSpace(expected)) expected = ExtractBracketedSongTitle(toolReason);
        if (string.IsNullOrWhiteSpace(expected)) expected = ExtractExplicitSongTitle(toolReason);
        return expected ?? "";
    }

    private static bool UserExplicitlyRejectedSong(string userText, string resolvedTitle)
    {
        string text = (userText ?? "").ToLowerInvariant();
        string title = (resolvedTitle ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0 || title.Length == 0) return false;
        return text.Contains("不是" + title) || text.Contains("不是《" + title + "》") ||
            text.Contains("不要唱" + title) || text.Contains("别唱" + title) ||
            text.Contains("not " + title) || text.Contains(title + "じゃない");
    }

    /// <summary>
    /// ID 有出处只证明模型见过这条记录，不证明它选对了歌。服务端解析完成、任何
    /// 音频进入合成队列之前，再用用户/工具明确写出的歌名核对真实返回身份。
    /// </summary>
    private bool ValidateResolvedSongIdentity(
        AgentSongSingRequest request,
        SenseVoiceSpeechToText.SongPerformanceResult result,
        out string problem)
    {
        problem = "";
        if (request == null || result == null) return true;
        string userText = StripSingingPerceptionMetadata(m_LastUserMsg);
        string resolved = string.IsNullOrWhiteSpace(result.DisplayName)
            ? result.Title
            : result.DisplayName;
        if (UserExplicitlyRejectedSong(userText, resolved) ||
            UserExplicitlyRejectedSong(userText, result.Title))
        {
            problem = $"曲库解析成《{resolved}》，但用户刚刚明确说不是这首歌";
            return false;
        }

        string expected = ResolveExpectedSongTitle(
            request.Title, userText, request.Reason);
        if (string.IsNullOrWhiteSpace(expected)) return true;
        if (AreSongTitlesCompatible(expected, result.DisplayName) ||
            AreSongTitlesCompatible(expected, result.Title))
            return true;

        problem = $"请求的是《{expected}》，曲库却解析成《{resolved}》" +
            $"（combinedMatch={result.MatchConfidence:F2}, lyricsMatch={result.LyricsConfidence:F2}）";
        return false;
    }

    private void BeginSongSing(AgentSongSingRequest request)
    {
        if (request == null) return;
        m_SingingRequestSubmittedSinceUserTurn = true;
        if (!m_EnableAutonomousRememberedSongSinging || !m_EnableAutonomousHumBack ||
            !m_IsVoiceMode)
        {
            RecordHumBackResult("未执行：长期曲库演唱功能当前已关闭。", true);
            return;
        }
        if (m_SongSingInFlight || HasHumBackExecutionWork())
        {
            RecordHumBackResult(
                "未执行：已经有一个真实歌唱任务正在进行。", true, false);
            return;
        }
        //下面每一条失败都会走 RecordHumBackResult，而那里按 m_PendingHumBackKey 记账。
        //曲库演唱不是 hum_back；仅在确认没有旧事务后再清账，不能破坏在飞动作的幂等键。
        m_PendingHumBackKey = "";
        m_PendingHumBackInputRevision = -1;
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (senseVoice == null)
        {
            RecordHumBackResult("未执行：当前语音服务不支持从本地曲库演唱。", true);
            return;
        }

        string mode = (request.Mode ?? "memory").Trim().ToLowerInvariant();
        if (mode == "predict") mode = "continue";
        if (mode != "memory" && mode != "continue" && mode != "auto") mode = "memory";
        if (mode == "memory" && string.IsNullOrWhiteSpace(request.SongId) &&
            string.IsNullOrWhiteSpace(request.Title))
        {
            RecordHumBackResult("未执行：从长期曲库演唱需要歌曲 id 或歌名。", true);
            return;
        }

        //刚刚查不到的目标不许原样再查一遍。重试同一个 id 只会得到同一个结果，
        //而她会一边失败一边对用户说"马上就好"。
        //id 也要有出处，和歌名一样。8/25 实测两种写法都进来了：
        //凭印象写的 0947a4b23e25(其实是 8/24 的一首中文歌)，和截成 6 位的 7e1fb5。
        //后者要跑完一整趟服务端往返才报错，前者根本不报错——它真的把别的歌唱了出来。
        if (!SongIdHasProvenance(request.SongId, out string idProblem))
        {
            RecordHumBackResult(idProblem, true);
            if (m_LogHumBack) Debug.LogWarning("[SongSing] id 没有出处，未派发: " + request.SongId);
            return;
        }

        //成功也要去重，不只是失败。
        if (ShouldBlockRepeatSinging(
                BuildSungTargetKey("catalog",
                    string.IsNullOrWhiteSpace(request.SongId) ? request.Title : request.SongId),
                out string repeatReason))
        {
            RecordHumBackResult(repeatReason, true);
            if (m_LogHumBack) Debug.LogWarning("[SongSing] 拦下自说自话的重复演唱");
            return;
        }

        string songSingKey = BuildSongSingKey(mode, request.SongId, request.Title);
        if (songSingKey == m_LastFailedSongSingKey &&
            Time.realtimeSinceStartup - m_LastFailedSongSingTime < k_SongSingRetryBlockSeconds)
        {
            RecordHumBackResult(BuildSongSingRetryBlockMessage(songSingKey), true);
            if (m_LogHumBack)
                Debug.LogWarning("[SongSing] 拦下对刚失败目标的原样重试: " + songSingKey);
            return;
        }

        int performanceSeed = NextHumPerformanceSeed();
        int semitoneOffset;
        float rmsMixRate;
        float protect;
        float interpretation;
        CreateHumPerformanceProfile(
            performanceSeed, out semitoneOffset, out rmsMixRate, out protect,
            out interpretation);
        int generation = ++m_SongSingGeneration;
        // 曲库解析/传输层不再替角色裁掉整首歌。自主时长只作为感知帧中的决策信息：
        // 角色可以主动选一小段，但一旦她决定唱完整内容，程序按自然块流式承接。
        // 这也避免曲库查询结果续轮被标成 tick 时误把用户点歌重新套上 60 秒上限。
        const float totalSongLimit = 0f;
        AcquireSkillExecutionLease("singing", "song_sing 已受理并等待完整播放", true);
        m_LastSingingRepairAt = -99999f;
        m_SongSingInFlight = true;
        m_HumBackResultPending = false;
        m_LastHumBackResult = "";
        if (m_LogHumBack)
        {
            Debug.Log($"[SongSing] 角色调用 mode={mode} id={request.SongId} " +
                $"title=\"{request.Title}\" seed={performanceSeed} reason=\"{request.Reason}\"");
        }

        senseVoice.SingRememberedSong(
            request.SongId,
            request.Title,
            mode,
            totalSongLimit,
            performanceSeed,
            request.Reason,
            result =>
            {
                if (generation != m_SongSingGeneration) return;
                m_SongSingInFlight = false;
                if (result == null || !result.Ok)
                {
                    string detail = result == null || string.IsNullOrWhiteSpace(result.Error)
                        ? "本地曲库没有返回结果"
                        : TruncateForFrame(result.Error, 220);
                    SongSingFailureKind failureKind = result == null
                        ? SongSingFailureKind.Transport
                        : ClassifySongSingFailureCode(result.ErrorCode);
                    RememberSongSingFailure(
                        songSingKey, failureKind, request.SongId, request.Title, detail);
                    RecordHumBackResult(
                        "未执行：" + detail + "。失败类型=" + failureKind +
                        "；结果事实：本次没有从记忆中唱出或续唱成功。" +
                        BuildSongSingIdHint(request.SongId),
                        true);
                    if (m_LogHumBack) Debug.LogWarning("[SongSing] " + detail);
                    CompleteSongSingToolRoundIfIdle();
                    return;
                }
                if (!ValidateResolvedSongIdentity(request, result, out string identityProblem))
                {
                    RememberSongSingFailure(
                        songSingKey,
                        SongSingFailureKind.IdentityMismatch,
                        request.SongId,
                        request.Title,
                        identityProblem);
                    RecordHumBackResult(
                        "未执行：" + identityProblem + "。为避免唱错歌，返回音频已丢弃；" +
                        "请重新查询正确 id，或改用 <hum_back mode=\"practice\"/> 唱本轮练习片段。",
                        true);
                    if (m_LogHumBack)
                        Debug.LogWarning("[SongSing/Identity] " + identityProblem);
                    CompleteSongSingToolRoundIfIdle();
                    return;
                }

                // 本次真实解析成功就是比旧失败缓存更新的事实。
                if (songSingKey == m_LastFailedSongSingKey)
                    ClearSongSingFailureCache();

                string resolvedSongName = string.IsNullOrWhiteSpace(result.DisplayName)
                    ? (string.IsNullOrWhiteSpace(result.Title) ? result.SongId : result.Title)
                    : result.DisplayName;
                NoteSessionSong("唱过", result.SongId, resolvedSongName);

                m_PendingHumTimeline = result.MidiTimeline;
                m_PendingHumFrameSeconds = Mathf.Clamp(result.FrameSeconds, 0.02f, 0.25f);
                m_PendingHumLanguage = "";
                m_PendingHumReason = request.Reason ?? "";
                m_PendingHumMode = result.Continuation ? "continue" : "memory";
                m_PendingHumLyricsOverride = "";
                m_PendingHumSourceWav = result.WavBytes;
                m_PendingHumSegmentWavs = null;
                m_PendingHumSegmentGaps = null;
                m_PendingHumSegmentMedians = null;
                m_PendingHumSegmentSources = null;
                m_PendingHumSegmentShifts = null;
                m_PendingHumUsesExplicitSegmentKey = false;
                if (senseVoice.TryBuildSingingStreamChunks(
                        result.WavBytes,
                        result.MidiTimeline,
                        result.FrameSeconds,
                        HumStreamChunkSeconds,
                        out SenseVoiceSpeechToText.PracticeComposition songStream,
                        out string streamFailure) &&
                    songStream != null && songStream.SegmentWavs != null &&
                    songStream.SegmentWavs.Count >= 2)
                {
                    m_PendingHumSegmentWavs = songStream.SegmentWavs;
                    m_PendingHumSegmentGaps = songStream.Gaps;
                    m_PendingHumSegmentMedians = songStream.SegmentMedians;
                    m_PendingHumSegmentSources = songStream.SegmentSourceIndices;
                    m_PendingHumSegmentShifts = BuildUniformStreamingShifts(
                        songStream, semitoneOffset);
                }
                else if (m_LogHumBack && !string.IsNullOrEmpty(streamFailure))
                    Debug.LogWarning("[SongSing/Stream] 曲库音频分块失败，尝试原整段转换: " + streamFailure);
                m_PendingHumIsPracticeComposition = false;
                m_PendingHumIsCatalogSong = true;
                m_LastCatalogUniqueSegmentCount = result.SelectedSegmentCount;
                m_PendingHumIsCatalogContinuation = result.Continuation;
                m_PendingCatalogSongName = resolvedSongName;
                m_PendingHumPerformanceSeed = performanceSeed;
                m_PendingHumSemitoneOffset = semitoneOffset;
                m_PendingHumRmsMixRate = rmsMixRate;
                m_PendingHumProtect = protect;
                m_PendingHumInterpretation = interpretation;
                m_PendingHumVariationDiagnostic =
                    $"catalog={m_PendingCatalogSongName}, unique={result.UniqueSegmentCount}, " +
                    $"variants={result.DuplicateVariantCount}, selected={result.SelectedSegmentCount}, " +
                    $"basis={result.ContinuationBasis}, combinedMatch={result.MatchConfidence:F2}, " +
                    $"lyricsMatch={result.LyricsConfidence:F2}";
                m_HumBackPending = true;
                if (m_LogHumBack)
                {
                    Debug.Log($"[SongSing] 已解析 mode={m_PendingHumMode} song=\"{m_PendingCatalogSongName}\" " +
                        $"segments={result.SelectedSegmentCount}/{result.UniqueSegmentCount} " +
                        $"variants={result.DuplicateVariantCount} source={result.DurationSeconds:F1}s " +
                        $"basis={result.ContinuationBasis} combinedMatch={result.MatchConfidence:F2} " +
                        $"lyricsMatch={result.LyricsConfidence:F2}");
                }

                bool outputIdle = !IsVoiceOutputPlaying && m_PendingChunks.Count == 0 &&
                    m_PendingClips.Count == 0;
                if (outputIdle) TryBeginPendingHumBack();
            },
            segmentLyrics: request.SegmentLyrics);
    }

    /// <summary>
    /// mode="memory" 会把条目里**每个**独立段各唱一遍连起来。段数不报出来，
    /// 她和用户都不知道刚才唱了几段——8/23 实测条目里混进了另一首歌的旧录音，
    /// 三段被连唱，用户听出来才问"为什么你连了那么多次"。
    /// </summary>
    private string BuildCatalogSegmentNote()
    {
        if (m_LastCatalogUniqueSegmentCount <= 1) return "";
        return $" 注意：这条记忆里有 {m_LastCatalogUniqueSegmentCount} 段独立内容，" +
               "本次按学习顺序全部连起来唱了。用户若只想要其中一段，" +
               "用 <song_sing lyrics=\"那一段的歌词\"/> 点名要哪一段。";
    }

    private void CompleteSongSingToolRoundIfIdle()
    {
        bool outputIdle = !IsVoiceOutputPlaying && m_PendingChunks.Count == 0 &&
            m_PendingClips.Count == 0;
        if (!outputIdle) return;
        ClearUserTurnAwaitingReply("曲库演唱结束");
        ReleaseSkillExecutionLease("singing", "song_sing 已结束且没有待播放音频");
        m_TTSSenderDone = true;
        m_TextBack.text = "";
        SetAnimator("state", 0);
        OnAgentRoundComplete();
        if (OnAISpeakDone != null) OnAISpeakDone();
    }

    /// <summary>
    /// 回唱素材不可用时，把**为什么**和**还能怎么办**一起说清楚。
    /// </summary>
    /// <remarks>
    /// 原来只有一句"当时没有取得可播放的旋律"——它陈述了一个状态，没给原因、也没给出路。
    /// 8/24 实测的后果：她自己编了个原因（"因为噪音没能成功哼出来"，真实原因是那段
    /// 唱于 246 秒前、超过 180 秒保留期），然后说"这次一定行"，连着重试四次，
    /// 每次都失败。而当时练唱会话里那一段还在，practice 立刻就能唱。
    ///
    /// 事实全在系统手上：素材多老、保留期多长、练唱会话里还有几段。写出来，
    /// 她就不必猜，也有话可以如实告诉用户。
    /// </remarks>
    /// <summary>回哼调用的身份：参数完全相同才算"原样重发"。</summary>
    private static string BuildHumBackKey(AgentHumBackRequest request)
    {
        if (request == null) return "";
        return ((request.Mode ?? "") + "|" + (request.Order ?? "") + "|" +
                (request.Capture ?? "") + "|" +
                request.TrimHeadSeconds.ToString("0.###") + "|" +
                request.TrimTailSeconds.ToString("0.###") + "|" +
                (request.ExcludeSpeech ? "exclude-speech" : "") + "|" +
                (request.Lyrics ?? "") + "|" +
                (request.PitchTarget ?? "") + "|" +
                (request.PitchDelta ?? "") + "|" +
                (request.PitchMatch ?? "") + "|" +
                (request.PitchPlan ?? "") + "|" +
                (request.Transpose ?? "") + "|" +
                (request.TargetNote ?? "") + "|" +
                (request.AlignToPracticeIndex > 0
                    ? request.AlignToPracticeIndex.ToString()
                    : "") + "|" +
                (float.IsNaN(request.Key) ? "" : request.Key.ToString("0.##")) + "|" +
                (request.KeyPerSegment == null
                    ? ""
                    : string.Join(",", request.KeyPerSegment))).Trim().ToLowerInvariant();
    }

    private string BuildEchoUnavailableNote(SenseVoiceSpeechToText senseVoice)
    {
        if (senseVoice != null && !string.IsNullOrEmpty(senseVoice.RecentSingingMaterialConflict))
            return "未执行回唱：" + senseVoice.RecentSingingMaterialConflict;
        string why = "当时没有取得可播放的旋律";
        int practiceCount = 0;
        if (senseVoice != null)
        {
            practiceCount = senseVoice.PracticePhraseCount;
            float age = senseVoice.LastSingingPerformanceAgeSeconds;
            float keep = senseVoice.SingingAudioRetentionSeconds;
            if (age < 0f)
                why = "这一场里还没有听到过可回唱的歌声";
            else if (age > keep)
                why = $"上一段歌声是 {age:F0} 秒前的，已经超过 {keep:F0} 秒的回唱保留期，" +
                      "音频不在手边了（**不是噪音干扰，也不是转换失败**）";
        }
        string note = "未执行：" + why + "。不得声称已经回哼、跟唱或让用户评价效果。";
        //有替代就把具体调用写出来；没有就只剩两条路，也说清楚。
        if (practiceCount > 0)
            note += $" 不过练唱会话里还留着 {practiceCount} 段——" +
                    "想唱其中某一段就用 <hum_back mode=\"practice\" order=\"N\"/>" +
                    (practiceCount >= 2 ? "，连起来唱就写多个段号" : "") +
                    "，那份素材没有 180 秒限制。";
        else
            note += " 现在只有两条路：请用户再唱一遍，或者如实告诉他这段你已经留不住了。";
        note += " **不要说「这次一定行」然后原样再试一次**——原因不会自己变。";
        return note;
    }

    private static bool IsIdempotentHumBackDuplicate(
        string requestKey,
        string activeKey,
        bool hasActiveWork,
        int inputRevision,
        int activeInputRevision)
    {
        return hasActiveWork && !string.IsNullOrWhiteSpace(requestKey) &&
               string.Equals(requestKey, activeKey, StringComparison.Ordinal) &&
               inputRevision == activeInputRevision;
    }

    private static bool ShouldContinueAgentChainAfterHumBack(
        bool requestedContinue,
        bool humBackTransactionRetained)
    {
        return requestedContinue && !humBackTransactionRetained;
    }

    private static bool IsHumBackTextOutputDrained(
        bool waitForTextDrain,
        bool senderDone,
        bool generationDrained,
        bool hasPendingChunks,
        bool hasPendingClips,
        bool audioPlaying)
    {
        if (!waitForTextDrain) return true;
        //senderDone 保留无正文/silent 短路；generationDrained 由真实播放收尾统一产生。
        //两者只证明不会再生产新正文，队列和 AudioSource 仍必须客观为空。
        return (senderDone || generationDrained) &&
               !hasPendingChunks && !hasPendingClips && !audioPlaying;
    }

    private string BuildHumBackBusyFact()
    {
        string state = m_HumBackPlaying
            ? "playing"
            : m_HumStreamWaitForTextOutputDrain &&
              (m_HumStreamReadyClips.Count > 0 || m_HumStreamScheduledClips.Count > 0)
                ? "prepared_waiting_for_text"
                : (m_HumBackPreparingCarrier || m_ActiveSVSRequest != null ||
                   m_ActiveHumSVCRequest != null || m_HumStreamProducerCoroutine != null)
                    ? "preparing"
                    : m_HumBackPending ? "queued" : "finishing";
        float estimatedTotal = m_PendingHumTimeline != null
            ? m_PendingHumTimeline.Length *
              Mathf.Clamp(m_PendingHumFrameSeconds, 0.02f, 0.25f)
            : 0f;
        string order = m_PendingHumIsPracticeComposition
            ? m_LastPracticeOrderUsed
            : "-";
        string key = !string.IsNullOrWhiteSpace(m_PendingPerSegmentKeyText)
            ? m_PendingPerSegmentKeyText
            : m_PendingHumSemitoneOffset.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        return $"当前已有 hum_back 动作，state={state}, mode={m_PendingHumMode}, " +
               $"order={order}, key={key}, played_seconds={m_HumStreamPlayedSeconds:F1}, " +
               $"estimated_total_seconds={estimatedTotal:F1}；这次新调用没有执行。";
    }

    private void QueueHumBack(AgentHumBackRequest request)
    {
        if (request == null) return;
        if (request.UnifiedSing && m_WorkNoProgress)
        { m_SingingRejectedSpeechRound = m_CurrentSpeechRoundId; return; }
        if (request.UnifiedSing && HasPendingSingingGoalReview())
        {
            // A pending approval is a normal wait, not a failed tool or queued retry.
            m_WorkProgressFact = "\n[Work/Progress] status=waiting_goal_review；未提交演唱，待本次目标审核后重新决定；没有失败纠错任务。\n";
            if (m_LogAgentLoop) Debug.Log("[Sing/Wait] 目标待审核；未调用执行器、未记失败、未排工具纠错");
            return;
        }
        string validationIdentity = request.UnifiedSing ? SingingRequestKey(request) : "";
        m_SingingRequestSubmittedSinceUserTurn = true;
        if (request.UnifiedSing && !TryResolveUnifiedSing(request, out string singFailure))
        {
            RecordSingingRejection(validationIdentity, singFailure);
            // The structured rejection owns counting. The display result is not a second failure.
            m_LastHumBackResult = "未执行：" + singFailure;
            m_HumBackResultPending = true;
            ReportToolFailureForLlm("sing", "material_not_ready", singFailure,
                "保留所选 refs，按素材事实修正；不确定可询问。sing 不填写 source/mode/order，" +
                "只填写清单中的 refs；当前无可播放版本时按证据选 range 或起止时间。");
            return;
        }
        if (!string.IsNullOrWhiteSpace(request.SourceValidationError))
        {
            string sourceProblem = request.SourceValidationError;
            RecordHumBackResult("未执行：" + sourceProblem, true);
            ReportToolFailureForLlm(
                "hum_back",
                "invalid_material_source",
                sourceProblem + " 本次没有读取或播放任何音频。",
                "请结合本轮歌唱素材事实重新自主选择：recent_turn 对应 echo，" +
                "practice 对应 practice，library 对应 song_sing；拿不准可以先询问用户。");
            if (m_LogHumBack)
                Debug.LogWarning("[HumBack/Source] " + sourceProblem);
            return;
        }
        if (!string.IsNullOrWhiteSpace(request.ValidationError))
        {
            string reason = "未执行：" + request.ValidationError;
            RecordHumBackResult(reason, true);
            ReportToolFailureForLlm(
                "hum_back",
                "invalid_pitch_parameter",
                request.ValidationError,
                "practice 请直接表达音乐目标：逐段用 pitch_plan（可混用 keep、D4、+whole_step、" +
                "match:清单段号），整体升降用 transpose=\"+half_step\"；不要输出 +Nst。" +
                "程序负责换算底层半音值；请重新决定后只调用一次。");
            if (m_LogHumBack) Debug.LogWarning("[HumBack/Pitch] " + reason);
            return;
        }
        if (!m_EnableAutonomousHumBack || !m_IsVoiceMode) return;
        if (string.Equals(request.Mode, "follow", StringComparison.OrdinalIgnoreCase))
        {
            //This is a tool selected by the role, not a phrase inferred from its own notes.
            if (!CanExecuteSkillAction("singing", out string followBlock))
            {
                RecordHumBackResult("未开启轮唱：" + followBlock, true);
                return;
            }
            ArmSingAlongForNextPerformance();
            RecordHumBackResult("轮唱已开启：等待下一段真实歌声，听完该段后回唱；没有播放旧录音。" +
                "这不是同步合唱，是否已到合适接话时机仍需独立判断。", false);
            return;
        }
        string capture = (request.Capture ?? "clean").Trim().ToLowerInvariant();
        if (capture.Length == 0) capture = "clean";
        if (capture != "clean" && capture != "expanded")
        {
            RecordHumBackResult(
                $"未执行：capture=\"{request.Capture}\" 不存在；只能选择 clean 或 expanded。",
                true);
            return;
        }
        if (capture == "expanded" && !IsPracticeHumMode(request.Mode))
        {
            RecordHumBackResult(
                "未执行：expanded 是练唱清单里保存的可恢复边界，只能与 mode=\"practice\" 一起使用。",
                true);
            return;
        }
        if ((request.TrimHeadSeconds > 0.001f || request.TrimTailSeconds > 0.001f) &&
            !IsPracticeHumMode(request.Mode))
        {
            RecordHumBackResult(
                "未执行：明确秒数裁剪只适用于 mode=\"practice\" 的已保存边界素材。",
                true);
            return;
        }
        request.Capture = capture;
        if (IsPracticeHumMode(request.Mode) && string.IsNullOrWhiteSpace(request.Order))
        {
            SenseVoiceSpeechToText practiceSense = m_ChatSettings != null
                ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
                : null;
            int practicePhraseCount = practiceSense != null
                ? practiceSense.PracticePhraseCount
                : 0;
            if (RequiresExplicitPracticeOrder(
                    request.Mode, request.Order, practicePhraseCount))
            {
                string missingOrder = BuildMissingPracticeOrderFailure(
                    practicePhraseCount);
                RecordHumBackResult("未执行：" + missingOrder, true);
                ReportToolFailureForLlm(
                    "hum_back",
                    "missing_practice_order",
                    missingOrder,
                    $"当前练唱清单的客观编号范围是 1..{practicePhraseCount}。" +
                    "请重新结合用户原话自主选择：指向刚才的多段时使用 " +
                    "hum_back mode=practice 并显式列出 order；真有歧义时先询问。" +
                    "不要因为知道歌名或曲库 id 改用 song_sing，也不要声称已经播放。");
                if (m_LogHumBack)
                    Debug.LogWarning("[HumBack/Practice] 拦下多段空 order，避免默认全唱");
                return;
            }
        }
        string humBackKey = BuildHumBackKey(request);
        bool hasActiveHumBack = HasHumBackExecutionWork();
        if (IsIdempotentHumBackDuplicate(
                humBackKey,
                m_PendingHumBackKey,
                hasActiveHumBack,
                m_InputAudioRevision,
                m_PendingHumBackInputRevision))
        {
            //The same LLM chain can repeat a tool tag while the originally accepted
            //audio is being generated or waiting for its spoken prelude.  Preserve
            //the original transaction exactly as-is: no buffer reset, no second HTTP
            //request, and no false tool failure for the character to explain.
            m_HumBackTransactionRetainedThisRound = true;
            if (m_LogHumBack)
                Debug.Log("[HumBack/Transaction] 同一真实用户轮重复提交相同 hum_back；" +
                          "已幂等沿用原动作 key=" + humBackKey);
            return;
        }
        if (hasActiveHumBack)
        {
            string busyFact = BuildHumBackBusyFact();
            RecordHumBackResult("未执行：" + busyFact, true, false);
            ReportToolFailureForLlm(
                "hum_back",
                "busy",
                busyFact,
                "不要声称新参数已经播放。结合用户刚才的真实意图，自然说明当前动作状态；" +
                "旧动作结束或被新的真实用户轮次取消后，再决定是否重新提交新参数。" +
                "程序没有替你决定等待、重试还是询问用户。");
            if (m_LogHumBack) Debug.LogWarning("[HumBack] " + busyFact);
            return;
        }

        //新的动作已经被真正受理，上一遍“等待反馈”的状态不再代表当前现场。
        m_PostHumBackFeedbackActive = false;
        m_PostHumBackFeedbackSummary = "";
        m_PendingPracticePitchPlan = null;
        m_HumStreamPlayedPracticeStableIds.Clear();
        m_PendingHumUsesMusicalPitchIntent = false;
        m_PendingHumExtraTagsIgnored = 0;

        //混合轮只禁止程序未经角色决定自动跟唱。若角色已经看完前置/尾部口语与
        //干净歌唱岛事实并明确调用 hum_back，就允许读取裁净后的 recent_turn/practice
        //素材；执行层绝不改用未裁剪的整轮录音。

        //同一个调用已经连续失败够多次：不再派发，把仅剩的两条路写清楚。
        if (humBackKey == m_LastFailedHumBackKey &&
            m_LastFailedHumBackCount >= k_HumBackBlockAfterFailures)
        {
            RecordHumBackResult(
                $"未执行：这个调用已经连续失败 {m_LastFailedHumBackCount} 次，" +
                "参数一个字都没变，再发一次结果不会不同，所以系统这次没有执行。" +
                "现在只有两条路：**问用户**（请他再唱一遍，或者问清楚他要的是哪一段），" +
                "或者**如实告诉他这件事你现在做不到**。" +
                "换别的参数（不同的 mode / order / 段号）是可以的，原样重发不行。",
                true);
            if (m_LogHumBack)
                Debug.LogWarning("[HumBack] 拦下连续第 " +
                                 (m_LastFailedHumBackCount + 1) + " 次相同调用: " + humBackKey);
            return;
        }

        m_PendingHumBackKey = humBackKey;
        m_PendingHumBackInputRevision = m_InputAudioRevision;

        if (ShouldBlockRepeatSinging(
                BuildSungTargetKey(
                    IsPracticeHumMode(request.Mode) ? "practice" : "echo",
                    request.Order),
                out string humRepeatReason))
        {
            RecordHumBackResult(humRepeatReason, true);
            if (m_LogHumBack) Debug.LogWarning("[HumBack] 拦下自说自话的重复回哼");
            return;
        }

        bool composePractice = IsPracticeHumMode(request.Mode) ||
            IsPracticeCompositionRequest(m_LastUserMsg);
        bool confirmedSinging = IsCurrentTurnConfirmedSinging();
        //是否真要唱、由谁唱、唱哪些段已经由 LLM 结合完整语境并选择业务工具。
        //这里不再用“不要唱”子串二次否决，避免把内容限制误当成能力禁用。
        if (HasActiveSingAlongRequest())
        {
            if (!confirmedSinging && !composePractice && !IsExplicitHumBackRequest(m_LastUserMsg))
            {
                //原来的判据是"这一轮在不在唱"，而轮唱的节奏天然是「唱 → 说『到你了』」——
                //等用户说到你了的时候，唱已经是上一轮的事了。8/23 实测：用户开场那句
                //「我唱一句，然后你跟着唱一句」点亮了这个等待状态，之后他每一句确认
                //（「嗯。」「是的，轮到你唱的。」「我没看到你调用歌唱工具啊。」）都不含
                //触发措辞，于是十次 hum_back 全被拦下——**他要求的轮唱，点亮了阻止轮唱的闸**。
                //
                //真正该问的是"手上有没有可回唱的素材"。没有素材才拦；有素材就该唱。
                if (!HasRecentSingableMaterial())
                {
                    //而且绝不能静默返回。原来只写一条 Debug 日志就 return，感知帧里
                    //什么都没有，她不知道自己被拦了，用户连说三次"没看到你调用歌唱工具"。
                    //这和 <silence in="2s"/> 是同一类错误：标签被无声吞掉。
                    RecordHumBackResult(
                        "未执行：现在还没有可以回唱的歌声——你之前答应了跟着用户唱，" +
                        "但系统手上没有留存用户刚唱的旋律（可能上一轮被判成说话，" +
                        "也可能已经超出保留期）。请他再唱一遍，等真的听到再回唱；" +
                        "**不要说「我这就唱」然后什么都没发生**。",
                        true);
                    if (m_LogHumBack)
                        Debug.Log("[HumBack] 仍在等待真实歌声，且手上没有可回唱素材，已如实回报");
                    return;
                }
                if (m_LogHumBack)
                    Debug.Log("[HumBack] 等待跟唱中，但手上有可回唱素材，放行");
            }
            if (confirmedSinging) RefreshActiveSingAlongSession();
        }

        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (senseVoice == null)
        {
            RecordHumBackResult(
                "未执行：当时没有取得可播放的旋律。不得声称已经回哼、跟唱或让用户评价效果。",
                true);
            if (m_LogHumBack)
                Debug.LogWarning("[HumBack] 最近没有仍在保留期内的可演奏歌唱旋律，本次不回哼");
            return;
        }

        int performanceSeed = NextHumPerformanceSeed();
        int semitoneOffset;
        float rmsMixRate;
        float protect;
        float interpretation;
        CreateHumPerformanceProfile(
            performanceSeed, out semitoneOffset, out rmsMixRate, out protect,
            out interpretation);
        //她写了就听她的；没写就用上面按 seed 生成的那一档（request 在方法入口已判非空）
        if (!float.IsNaN(request.Key)) semitoneOffset = Mathf.RoundToInt(request.Key);
        if (!float.IsNaN(request.Expression)) interpretation = request.Expression;
        float paceOverride = request.Pace;

        float[] timeline;
        float frameSeconds;
        string language;
        byte[] sourceWav = null;
        string variationDiagnostic = "";
        int phraseCount = 1;
        float sourceDuration = 0f;
        SenseVoiceSpeechToText.PracticeComposition practiceSegments = null;
        //这次连唱唱了哪几段。注意不能拿 practiceSegments 代替：那个只有走
        //逐段移调时才非空，普通连唱是 null，段号会整个丢掉。
        List<int> playedPracticeIndices = null;
        int[] practiceSegmentShifts = null;
        m_LastPracticeShiftNote = "";
        if (composePractice)
        {
            if (!senseVoice.TryValidatePracticeBoundarySelection(
                    request.Order,
                    request.Capture == "expanded",
                    request.TrimHeadSeconds,
                    request.TrimTailSeconds,
                    request.ExcludeSpeech,
                    out string boundaryConflict))
            {
                RecordHumBackResult("未执行：" + boundaryConflict, true);
                ReportToolFailureForLlm(
                    "hum_back",
                    "boundary_selection_conflict",
                    boundaryConflict,
                    "这是播放前的真实边界冲突，不是程序替你选择。请结合 head_extra/" +
                    "tail_extra/clean_lead_in_unverified 的时长、类型和转写，" +
                    "自主改选 clean、expanded、明确裁剪，" +
                    "或自然询问用户；不要声称已经播放。");
                if (m_LogHumBack)
                    Debug.LogWarning("[HumBack/Boundary] " + boundaryConflict);
                return;
            }
            SenseVoiceSpeechToText.PracticeComposition composition;
            string failure;
            if (!senseVoice.TryBuildSingingPracticeComposition(
                    performanceSeed,
                    0f,
                    out composition,
                    out failure,
                    request.Order,
                    request.Capture == "expanded",
                    request.TrimHeadSeconds,
                    request.TrimTailSeconds) || composition == null)
            {
                RecordHumBackResult(
                    "未执行：" + failure + "。不得声称已经把练习片段连续唱出。",
                    true);
                ReportToolFailureForLlm(
                    "hum_back",
                    "practice_composition_unavailable",
                    failure,
                    "程序没有替换素材或静默回退。请根据真实练唱清单、边界状态和用户语义" +
                    "自主改选 capture/order/裁剪；仍有歧义时可自然询问用户，" +
                    "不要声称已经播放。");
                if (m_LogHumBack) Debug.LogWarning("[HumBack/Practice] " + failure);
                return;
            }
            m_LastPracticeOrderUsed = string.IsNullOrWhiteSpace(request.Order)
                ? "练唱先后" : request.Order.Trim();
            //没写 order 而清单里有"同一句的多遍"时，默认顺序会把那几遍**全部**唱出去。
            //8/17 实测正是这样：用户明确说了"先唱期许了…再唱紧闭双眼"，她没写 order，
            //于是 40 秒里紧闭双眼出现两次，用户连着四轮说"你又唱了两遍"。
            //工具结果只写"按练唱先后合成"她读不出这是错的，这里点破。
            m_LastPracticeDuplicateNote = "";
            if (string.IsNullOrWhiteSpace(request.Order))
            {
                var listed = senseVoice.DescribePracticePhrases();
                var repeated = new List<string>();
                foreach (var ph in listed)
                    if (ph.TakeTotal > 1) repeated.Add($"[{ph.Index}]");
                if (repeated.Count > 1)
                    m_LastPracticeDuplicateNote =
                        $"注意：本次没有指定 order，按练唱先后把 {string.Join("、", repeated)} " +
                        "全部唱了出去，其中有同一句的多遍——所以那一句被重复唱了。" +
                        "用户要的若是其中某一遍，用 order 指定段号重唱。";
            }
            playedPracticeIndices = composition.PlayedIndices;
            timeline = composition.MidiTimeline;
            frameSeconds = composition.FrameSeconds;
            language = composition.Language;
            sourceWav = composition.WavBytes;
            phraseCount = composition.PhraseCount;
            sourceDuration = composition.DurationSeconds;
            variationDiagnostic = composition.VariationDiagnostic +
                (request.Capture == "expanded" ? ";capture=expanded" : ";capture=clean") +
                (request.TrimHeadSeconds > 0.001f
                    ? $";trim_head={request.TrimHeadSeconds:F2}s" : "") +
                (request.TrimTailSeconds > 0.001f
                    ? $";trim_tail={request.TrimTailSeconds:F2}s" : "");
            if (!TryResolvePracticePitchTarget(
                    request, composition, out int[] musicalShifts,
                    out float[] musicalTargetCenters,
                    out string targetNote, out string targetFailure))
            {
                RecordHumBackResult("未执行：" + targetFailure, true);
                ReportToolFailureForLlm(
                    "hum_back",
                    "pitch_alignment_unavailable",
                    targetFailure,
                    "根据当前练唱清单重新选择 order、pitch_plan 或 transpose；" +
                    "不要自己换算底层 key，也不要声称已经对齐或播放。");
                if (m_LogHumBack)
                    Debug.LogWarning("[HumBack/Pitch] " + targetFailure);
                return;
            }
            if (musicalShifts == null && TryBuildCurrentPracticeArrangement(
                    senseVoice,
                    composition,
                    semitoneOffset,
                    out int[] currentArrangementShifts,
                    out float[] currentArrangementCenters,
                    out string currentArrangementNote))
            {
                musicalShifts = currentArrangementShifts;
                musicalTargetCenters = currentArrangementCenters;
                targetNote = currentArrangementNote;
            }
            m_PendingHumUsesMusicalPitchIntent = musicalShifts != null;
            if (musicalShifts != null)
            {
                if (musicalShifts.Length == 1)
                {
                    //普通单段路径会把 global shift 加回来；这里先扣掉，并在请求层关闭
                    //auto-F0，确保最终发给 SVC 的就是音乐目标换算出的实际半音值。
                    semitoneOffset = musicalShifts[0] - m_HumSVCSemitoneShift;
                    request.Key = float.NaN;
                    request.KeyPerSegment = null;
                }
                else
                {
                    request.Key = float.NaN;
                    request.KeyPerSegment = null;
                    practiceSegmentShifts = musicalShifts;
                }
                if (!string.IsNullOrWhiteSpace(targetNote))
                    m_LastPracticeShiftNote = targetNote;
            }
            //旧 key 只在角色确实提交它时留作兼容诊断；音乐目标不再伪装成 key。
            m_PendingPerSegmentKeyText = request.KeyPerSegment == null
                ? ""
                : string.Join(",", System.Array.ConvertAll(
                    request.KeyPerSegment, v => v.ToString("0.##")));
            string shiftNote = "";
            SenseVoiceSpeechToText.PracticeComposition explicitlyShifted = null;
            if (musicalShifts != null && musicalShifts.Length >= 2)
                explicitlyShifted = composition;
            else
            {
                explicitlyShifted = ResolvePerSegmentShifts(
                    request.KeyPerSegment, composition, out practiceSegmentShifts,
                    out shiftNote);
            }
            if (!string.IsNullOrEmpty(shiftNote))
            {
                if (shiftNote.StartsWith("未执行：", StringComparison.Ordinal))
                {
                    RecordHumBackResult(shiftNote, true);
                    ReportToolFailureForLlm(
                        "hum_back",
                        "invalid_segment_key",
                        shiftNote.Substring("未执行：".Length).Trim(),
                        "让 key 列表项数与 order 实际选择的片段数完全一致，" +
                        "且每项保持在 −12～+12；不要依赖程序补项或截断。");
                    if (m_LogHumBack) Debug.LogWarning("[HumBack/Pitch] " + shiftNote);
                    return;
                }
                m_LastPracticeShiftNote = string.IsNullOrWhiteSpace(m_LastPracticeShiftNote)
                    ? shiftNote
                    : m_LastPracticeShiftNote + shiftNote;
            }
            m_PendingHumUsesExplicitSegmentKey = explicitlyShifted != null;
            //每一次 practice 都保留自然片段边界。此前没有显式音高目标时会把整首重新
            //切成约 30 秒块，第一次自动 +6 的实际输出无法再对应回用户的第几段，角色
            //因此只看得到用户录音音高。现在统一按稳定片段转换；长片段仍可在内部拆块。
            practiceSegments = composition;
            if (musicalShifts != null)
                practiceSegmentShifts = musicalShifts;
            else if (explicitlyShifted == null)
                practiceSegmentShifts = BuildUniformStreamingShifts(
                    composition, semitoneOffset);

            if (practiceSegmentShifts == null ||
                practiceSegmentShifts.Length != composition.SegmentWavs.Count)
            {
                RecordHumBackResult(
                    "未执行：程序没能为每个练唱片段建立实际移调计划。", true);
                return;
            }

            if (practiceSegmentShifts.Length == 1)
            {
                //单段可能走独立 SVS；它读取旋律时间轴而不是 SVC 的 semitone_shift。
                //所有 practice（包括没有显式目标的第一次自动音域适配）都必须把同一份
                //实际计划落实到乐谱，否则状态会记成“+6”，耳中却仍是原调。
                timeline = ShiftPitchTimeline(
                    composition.MidiTimeline, practiceSegmentShifts[0]);
            }

            m_PendingPracticePitchPlan = BuildPracticePitchPlan(
                senseVoice,
                composition,
                practiceSegmentShifts,
                musicalTargetCenters,
                string.IsNullOrWhiteSpace(targetNote) ? "" : targetNote.Trim());
            if (m_PendingPracticePitchPlan == null)
            {
                RecordHumBackResult(
                    "未执行：程序没能建立角色本次演唱的逐段音高状态。", true);
                return;
            }
            if (m_PendingHumUsesExplicitSegmentKey || musicalShifts != null)
                variationDiagnostic += "; 逐段移调 " +
                                       string.Join("/", practiceSegmentShifts);
            ExpandOversizedStreamingSegments(
                senseVoice, practiceSegments, ref practiceSegmentShifts);
        }
        else
        {
            if (!senseVoice.TryGetRecentSingingPerformance(
                    out timeline, out frameSeconds, out language) ||
                timeline == null || timeline.Length == 0)
            {
                RecordHumBackResult(BuildEchoUnavailableNote(senseVoice), true);
                if (m_LogHumBack)
                    Debug.LogWarning("[HumBack] 最近没有仍在保留期内的可演奏歌唱旋律，本次不回哼");
                return;
            }
            senseVoice.TryGetVariedRecentSingingAudio(
                performanceSeed, out sourceWav, out variationDiagnostic, paceOverride);
            sourceDuration = timeline.Length * Mathf.Clamp(frameSeconds, 0.02f, 0.25f);
            if (!float.IsNaN(paceOverride))
                sourceDuration /= Mathf.Clamp(paceOverride, 0.8f, 1.25f);
            m_PendingHumUsesExplicitSegmentKey = false;
            //最近一条真实歌声也可能是整首。只有确实拆出多个块时才走流式路线；
            //短回哼继续沿用一次转换，避免无意义的固定开销。
            SenseVoiceSpeechToText.PracticeComposition echoStream = null;
            string echoSplitFailure = "";
            if (sourceWav != null && sourceWav.Length > 44 &&
                senseVoice.TryBuildSingingStreamChunks(
                    sourceWav,
                    timeline,
                    frameSeconds,
                    HumStreamChunkSeconds,
                    out echoStream,
                    out echoSplitFailure) &&
                echoStream != null && echoStream.SegmentWavs != null &&
                echoStream.SegmentWavs.Count >= 2)
            {
                practiceSegments = echoStream;
                practiceSegmentShifts = BuildUniformStreamingShifts(
                    echoStream, semitoneOffset);
            }
            else if (m_LogHumBack && !string.IsNullOrEmpty(echoSplitFailure))
                Debug.LogWarning("[HumBack/Stream] 最近歌声分块失败，尝试原整段转换: " + echoSplitFailure);
        }

        m_HumBackResultPending = false;
        m_LastHumBackResult = "";
        m_PendingHumTimeline = timeline;
        m_PendingHumFrameSeconds = Mathf.Clamp(frameSeconds, 0.02f, 0.25f);
        m_PendingHumLanguage = language ?? "";
        m_PendingHumReason = request.Reason ?? "";
        m_PendingHumMode = composePractice ? "practice" : "echo";
        m_PendingHumLyricsOverride = composePractice
            ? ""
            : (request.Lyrics ?? "").Trim();
        m_PendingHumSourceWav = sourceWav;
        m_PendingHumSegmentWavs = practiceSegments != null ? practiceSegments.SegmentWavs : null;
        m_PendingHumSegmentGaps = practiceSegments != null ? practiceSegments.Gaps : null;
        m_PendingHumSegmentMedians =
            practiceSegments != null ? practiceSegments.SegmentMedians : null;
        m_PendingHumSegmentSources =
            practiceSegments != null ? practiceSegments.SegmentSourceIndices : null;
        m_PendingHumSegmentShifts = practiceSegmentShifts;
        m_PendingHumIsPracticeComposition = composePractice;
        m_PendingHumPlayedIndices = playedPracticeIndices;
        m_PendingPracticePlaybackFacts = "";
        if (composePractice) CapturePracticePlaybackFacts(senseVoice, playedPracticeIndices);
        m_PendingHumIsCatalogSong = false;
        m_PendingHumIsCatalogContinuation = false;
        m_PendingCatalogSongName = "";
        m_PendingHumPerformanceSeed = performanceSeed;
        m_PendingHumSemitoneOffset = semitoneOffset;
        m_PendingHumRmsMixRate = rmsMixRate;
        m_PendingHumProtect = protect;
        m_PendingHumInterpretation = interpretation;
        m_PendingHumVariationDiagnostic = variationDiagnostic ?? "";
        m_PendingHumRenderer = "pending";
        m_PendingHumExtraTagsIgnored = request.ExtraTagsIgnored;
        AcquireSkillExecutionLease("singing", "hum_back 已受理并等待完整播放", true);
        m_LastSingingRepairAt = -99999f;
        m_HumBackPending = true;
        m_HumBackTransactionRetainedThisRound = true;
        if (m_LogHumBack)
        {
            float duration = timeline.Length * m_PendingHumFrameSeconds;
            Debug.Log($"[HumBack] 已排队 mode={m_PendingHumMode} phrases={phraseCount} " +
                      $"frames={timeline.Length} melody={duration:F1}s source={sourceDuration:F1}s " +
                      $"seed={performanceSeed} shiftOffset={semitoneOffset} " +
                      $"她指定=[pitch_plan={request.PitchPlan} transpose={request.Transpose} " +
                      $"pitch_target={request.PitchTarget} pitch_delta={request.PitchDelta} " +
                      $"pitch_match={request.PitchMatch} " +
                      $"key={(float.IsNaN(request.Key) ? "-" : request.Key.ToString("0.#"))} " +
                      $"pace={(float.IsNaN(request.Pace) ? "-" : request.Pace.ToString("0.##"))} " +
                      $"expr={(float.IsNaN(request.Expression) ? "-" : request.Expression.ToString("0.##"))}] " +
                      $"rms={rmsMixRate:F2} protect={protect:F2} interp={interpretation:F2} " +
                      $"language={m_PendingHumLanguage} " +
                      $"variation=\"{m_PendingHumVariationDiagnostic}\" " +
                      $"reason=\"{m_PendingHumReason}\"");
        }
        bool textOutputBusy = IsVoiceOutputPlaying || !m_TTSSenderDone ||
            m_PendingChunks.Count > 0 || m_PendingClips.Count > 0;
        bool canPrepareStream = m_EnableNeuralHumSVC &&
            m_PendingHumSegmentWavs != null && m_PendingHumSegmentShifts != null &&
            CanPreparePlannedSingingChunks(m_PendingHumSegmentWavs.Count, m_PendingHumSegmentShifts.Length,
                m_PendingHumIsPracticeComposition && m_PendingPracticePitchPlan != null, m_EnableStreamedFullSongSVC);
        if (textOutputBusy && canPrepareStream && TryBeginPendingHumBack(true) && m_LogHumBack)
            Debug.Log("[HumBack/Stream] 正文仍在播放；已并行准备歌声，实际播放等待正文结束");
    }

    /// <summary>
    /// 在当前文字回复完全播放后启动旋律回哼。准备载体的阶段也算角色正在回应，
    /// 因而实时 VAD 会继续走 barge-in 路径，用户可以随时打断。
    /// </summary>
    private static bool CanPreparePlannedSingingChunks(int chunks, int shifts, bool hasPlan, bool streamingEnabled)
    {
        return chunks > 0 && chunks == shifts && (hasPlan || (streamingEnabled && chunks >= 2));
    }

    private bool TryBuildPendingTimelineScoreJson(out string scoreJson)
    {
        scoreJson = "";
        if (m_PendingHumTimeline == null || m_PendingHumTimeline.Length == 0)
            return false;
        float frameSeconds = Mathf.Clamp(m_PendingHumFrameSeconds, 0.02f, 0.25f);
        var notes = new List<SenseVoiceSpeechToText.SingingNote>();
        var f0 = new float[m_PendingHumTimeline.Length];
        var energy = new float[m_PendingHumTimeline.Length];
        var breaths = new List<float>();
        int start = 0;
        while (start < m_PendingHumTimeline.Length)
        {
            int label = m_PendingHumTimeline[start] > 1f
                ? Mathf.RoundToInt(m_PendingHumTimeline[start])
                : 0;
            int end = start + 1;
            while (end < m_PendingHumTimeline.Length)
            {
                int next = m_PendingHumTimeline[end] > 1f
                    ? Mathf.RoundToInt(m_PendingHumTimeline[end])
                    : 0;
                if (next != label) break;
                end++;
            }
            float duration = (end - start) * frameSeconds;
            notes.Add(new SenseVoiceSpeechToText.SingingNote
            {
                midi = label,
                note_name = "",
                start_seconds = start * frameSeconds,
                duration_seconds = duration,
                note_type = label > 0 ? "note" : "rest",
                confidence = label > 0 ? 0.72f : 1f,
            });
            if (label == 0 && start > 0 && end < m_PendingHumTimeline.Length &&
                duration >= 0.12f)
                breaths.Add((start + end) * 0.5f * frameSeconds);
            start = end;
        }
        for (int i = 0; i < m_PendingHumTimeline.Length; i++)
        {
            float midi = m_PendingHumTimeline[i];
            if (midi > 1f)
            {
                f0[i] = 440f * Mathf.Pow(2f, (midi - 69f) / 12f);
                energy[i] = 1f;
            }
        }
        var score = new SenseVoiceSpeechToText.SingingScore
        {
            schema_version = 1,
            source = m_PendingHumIsCatalogSong
                ? "remembered_song"
                : (m_PendingHumIsPracticeComposition
                    ? "practice_composition"
                    : "playable_timeline"),
            extractor_backend = "unity-playable-timeline",
            language = m_PendingHumLanguage ?? "",
            lyrics = "",
            lyrics_alignment = "backend_transcription_required",
            duration_seconds = m_PendingHumTimeline.Length * frameSeconds,
            frame_seconds = frameSeconds,
            confidence = 0.72f,
            notes = notes.ToArray(),
            f0_hz = f0,
            energy = energy,
            breath_positions_seconds = breaths.ToArray(),
            vibrato = new SenseVoiceSpeechToText.SingingVibrato[0],
        };
        scoreJson = JsonUtility.ToJson(score);
        return !string.IsNullOrWhiteSpace(scoreJson);
    }

    private bool TryBeginPendingHumBack(bool prepareWhileTextOutputDrains = false)
    {
        if (!m_HumBackPending || m_HumBackPreparingCarrier || m_HumBackPlaying) return false;
        if (m_AudioSource == null || m_PendingHumTimeline == null || m_PendingHumTimeline.Length == 0)
        {
            RecordHumBackResult(
                "未执行：Unity 没有可用的音频播放器或旋律数据。不得声称已经回哼。",
                true);
            CancelPendingHumBack("missing-audio-source-or-melody", false);
            return false;
        }

        bool textOutputBusy = IsVoiceOutputPlaying || !m_TTSSenderDone ||
            m_PendingChunks.Count > 0 || m_PendingClips.Count > 0;
        bool isStreamedMultiSegment = m_EnableNeuralHumSVC && m_PendingHumSegmentWavs != null &&
            m_PendingHumSegmentShifts != null &&
            CanPreparePlannedSingingChunks(m_PendingHumSegmentWavs.Count, m_PendingHumSegmentShifts.Length,
                m_PendingHumIsPracticeComposition && m_PendingPracticePitchPlan != null, m_EnableStreamedFullSongSVC);
        if (textOutputBusy && (!prepareWhileTextOutputDrains || !isStreamedMultiSegment))
            return false;
        m_HumStreamWaitForTextOutputDrain = textOutputBusy && prepareWhileTextOutputDrains;

        m_HumBackPending = false;
        m_HumBackPreparingCarrier = false;
        m_HumBackPlaying = false;
        int generation = ++m_HumBackGeneration;
        m_HumBackNeedsHistoryEntry = m_ChatHistory != null && m_ChatHistory.Count % 2 == 1;
        IsAISpeaking = true;
        if (!m_HumStreamWaitForTextOutputDrain)
        {
            m_TextBack.text = "♪ …";
            SetAnimator("state", 1);
        }

        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        GPTSoVITSFASTAPI characterVoice = m_ChatSettings != null
            ? m_ChatSettings.m_TextToSpeech as GPTSoVITSFASTAPI
            : null;
        byte[] sourceWav = m_PendingHumSourceWav;
        string targetPath = characterVoice != null
            ? characterVoice.GetReferenceAudioPathForVoiceConversion()
            : "";
        string svsPromptPath = ResolveSVSPromptAudioPath(targetPath);
        bool hasNeuralInputs =
            (sourceWav != null ||
             (senseVoice != null &&
              senseVoice.TryGetRecentSingingAudio(out sourceWav))) &&
            sourceWav != null && sourceWav.Length > 44 &&
            !string.IsNullOrWhiteSpace(targetPath);

        string scoreJson = "";
        string scoreLyrics = "";
        string scoreLanguage = "";
        bool hasSingingScore = false;
        if (!m_PendingHumIsPracticeComposition && !m_PendingHumIsCatalogSong &&
            senseVoice != null)
        {
            hasSingingScore = senseVoice.TryGetRecentSingingScoreJson(
                out scoreJson, out scoreLyrics, out scoreLanguage);
        }
        if (!hasSingingScore)
        {
            hasSingingScore = TryBuildPendingTimelineScoreJson(out scoreJson);
            scoreLanguage = m_PendingHumLanguage ?? "";
        }
        if (hasSingingScore &&
            !string.IsNullOrWhiteSpace(m_PendingHumLyricsOverride))
        {
            try
            {
                var correctedScore =
                    JsonUtility.FromJson<SenseVoiceSpeechToText.SingingScore>(
                        scoreJson);
                if (correctedScore != null)
                {
                    correctedScore.lyrics = m_PendingHumLyricsOverride;
                    correctedScore.lyrics_alignment =
                        "explicit-user-or-agent-correction";
                    correctedScore.lyrics_override = true;
                    // A lyric correction invalidates the kana derived from the
                    // previous SenseVoice transcript. The Japanese 9883
                    // adapter will regenerate it from the corrected text.
                    if (string.Equals(
                        correctedScore.language, "ja",
                        StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(
                            correctedScore.language, "japanese",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        correctedScore.lyrics_reading = "";
                        correctedScore.lyrics_reading_source = "";
                        correctedScore.lyrics_reading_complete = false;
                        correctedScore.lyrics_mora = null;
                    }
                    scoreJson = JsonUtility.ToJson(correctedScore);
                    scoreLyrics = m_PendingHumLyricsOverride;
                    if (m_LogHumBack)
                        Debug.Log(
                            "[HumBack/SVS] 使用明确歌词修正: " +
                            m_PendingHumLyricsOverride);
                }
            }
            catch (Exception ex)
            {
                if (m_LogHumBack)
                    Debug.LogWarning(
                        "[HumBack/SVS] 歌词修正写入乐谱失败: " + ex.Message);
            }
        }

        //多块歌曲优先走 SVC 流水线：第一块回来就开唱，后续转换与播放重叠。
        //独立 SVS 目前仍是整条请求/整条响应，等它支持分块乐谱后再接入同一消费者。
        bool requiresPerSegmentPitchExecution =
            m_PendingHumIsPracticeComposition && m_PendingPracticePitchPlan != null &&
            m_PendingHumSegmentWavs != null && m_PendingHumSegmentShifts != null &&
            m_PendingHumSegmentWavs.Count == m_PendingHumSegmentShifts.Length &&
            m_PendingHumSegmentWavs.Count >= 1;
        if ((m_EnableStreamedFullSongSVC || requiresPerSegmentPitchExecution) &&
            m_EnableNeuralHumSVC && hasNeuralInputs &&
            m_PendingHumSegmentWavs != null && m_PendingHumSegmentShifts != null &&
            m_PendingHumSegmentWavs.Count == m_PendingHumSegmentShifts.Length &&
            (m_PendingHumSegmentWavs.Count >= 2 || requiresPerSegmentPitchExecution))
        {
            m_PendingHumRenderer = "svc-stream";
            m_HumBackPreparingCarrier = true;
            //分块计划已经拥有完整内容；释放整条 WAV，长歌不会同时保留两份 PCM。
            m_PendingHumSourceWav = null;
            m_HumStreamProducerCoroutine = StartCoroutine(
                RequestPerSegmentNeuralHumBack(generation, targetPath));
            return true;
        }

        if (requiresPerSegmentPitchExecution)
        {
            const string pitchBackendMissing =
                "逐段音乐目标需要歌声转换服务，但该服务或角色参考音频当前不可用；" +
                "程序没有改走会丢失逐段音高目标的其它渲染器";
            FinishHumBack(generation, false, pitchBackendMissing);
            return true;
        }

        if (m_EnableSingingVoiceSynthesis && hasNeuralInputs && hasSingingScore)
        {
            string svsLanguage = !string.IsNullOrWhiteSpace(scoreLanguage)
                ? scoreLanguage
                : m_PendingHumLanguage;
            // Language support belongs to the pluggable 9883 backend. The
            // router sends Japanese to a configured kana-score renderer and
            // keeps SoulX limited to the languages it actually supports.
            m_HumBackPreparingCarrier = true;
            StartCoroutine(RequestSingingVoiceSynthesis(
                generation,
                sourceWav,
                svsPromptPath,
                targetPath,
                scoreJson,
                scoreLyrics,
                svsLanguage));
            return true;
        }

        if (m_EnableNeuralHumSVC && hasNeuralInputs)
        {
            m_PendingHumRenderer = "svc";
            m_HumBackPreparingCarrier = true;
            StartCoroutine(RequestNeuralHumBack(generation, sourceWav, targetPath));
            return true;
        }

        if (m_EnableNeuralHumSVC || m_EnableSingingVoiceSynthesis)
        {
            const string missingInput = "歌声转换服务缺少仍在保留期内的原始歌声音频或角色参考音频";
            if (!m_AllowLegacyHumFallback)
            {
                if (m_LogHumBack) Debug.LogWarning("[HumBack/SVC] " + missingInput);
                FinishHumBack(generation, false, missingInput);
                return true;
            }
            if (m_LogHumBack)
                Debug.LogWarning("[HumBack/SVC] " + missingInput + "，按设置退回旧合成器");
        }

        BeginLegacyHumBack(generation);
        return true;
    }

    private void BeginLegacyHumBack(int generation)
    {
        if (generation != m_HumBackGeneration) return;
        m_PendingHumRenderer = "legacy";

        AudioClip carrier = m_CharacterHumCarrierClip != null
            ? m_CharacterHumCarrierClip
            : m_GeneratedHumCarrierClip;
        if (carrier != null)
        {
            BuildAndPlayHumBack(generation, carrier);
            return;
        }

        if (m_ChatSettings == null || m_ChatSettings.m_TextToSpeech == null)
        {
            BuildAndPlayHumBack(generation, null);
            return;
        }

        m_HumBackPreparingCarrier = true;
        string carrierText = SelectHumCarrierText(m_PendingHumLanguage);
        if (m_LogHumBack)
            Debug.Log($"[HumBack] 首次使用，静默准备角色音色载体: \"{carrierText}\"");
        m_ChatSettings.m_TextToSpeech.PrepareSpeech(carrierText, (clip, ignoredText) =>
        {
            if (generation != m_HumBackGeneration)
            {
                if (clip != null) Destroy(clip);
                return;
            }

            m_HumBackPreparingCarrier = false;
            if (clip != null)
            {
                if (m_GeneratedHumCarrierClip != null && m_GeneratedHumCarrierClip != clip)
                    Destroy(m_GeneratedHumCarrierClip);
                clip.name = "NeEEvA_HumCarrier";
                m_GeneratedHumCarrierClip = clip;
            }
            else if (m_LogHumBack)
            {
                Debug.LogWarning("[HumBack] 角色音色载体合成失败，改用本地柔和哼声");
            }
            BuildAndPlayHumBack(generation, clip);
        });
    }

    [Serializable]
    private class SVSHealthResponse
    {
        public bool ok = false;
        public bool backend_available = false;
        public string backend = "";
        public string[] missing = null;
    }

    private string GetSVSBaseURL()
    {
        string url = (m_SVSURL ?? "").Trim().TrimEnd('/');
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        int lastSlash = url.LastIndexOf('/');
        if (lastSlash > scheme + 2) url = url.Substring(0, lastSlash);
        return url.TrimEnd('/');
    }

    private IEnumerator ProbeSVSHealth(Action<bool, string> completed)
    {
        string healthURL = GetSVSBaseURL() + "/health";
        using (UnityWebRequest request = UnityWebRequest.Get(healthURL))
        {
            request.timeout = 2;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                completed(false,
                    $"{healthURL} HTTP={request.responseCode} error={request.error}");
                yield break;
            }
            SVSHealthResponse health = null;
            try
            {
                health = JsonUtility.FromJson<SVSHealthResponse>(
                    request.downloadHandler.text);
            }
            catch (Exception)
            {
                // A malformed health response is treated as unavailable.
            }
            bool ready = health != null && health.ok && health.backend_available;
            string missing = health != null && health.missing != null
                ? string.Join(",", health.missing)
                : "unknown";
            completed(ready, ready
                ? $"{health.backend} ready"
                : "BACKEND_UNAVAILABLE: missing=" + missing);
        }
    }

    private bool TryLaunchSVS(out string detail)
    {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        try
        {
            string projectRoot = System.IO.Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                detail = "无法定位 Unity 项目根目录";
                return false;
            }
            string relative = (m_SVSStartScriptRelativePath ?? "")
                .Replace('/', System.IO.Path.DirectorySeparatorChar);
            string scriptPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(projectRoot, relative));
            if (!System.IO.File.Exists(scriptPath))
            {
                detail = "SVS 启动脚本不存在: " + scriptPath;
                return false;
            }
            string scriptDirectory = System.IO.Path.GetDirectoryName(scriptPath);
            string runtimeDirectory = System.IO.Path.Combine(scriptDirectory, "runtime");
            System.IO.Directory.CreateDirectory(runtimeDirectory);
            string logPath = System.IO.Path.Combine(runtimeDirectory, "svs_server.log");
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " +
                            QuoteProcessArgument(scriptPath) + " -LogPath " +
                            QuoteProcessArgument(logPath),
                WorkingDirectory = scriptDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            if (m_SVSServerProcess != null)
            {
                m_SVSServerProcess.Dispose();
                m_SVSServerProcess = null;
            }
            m_SVSServerProcess = System.Diagnostics.Process.Start(startInfo);
            if (m_SVSServerProcess == null)
            {
                detail = "Windows 未能创建 9883 启动进程";
                return false;
            }
            detail = $"已启动 PID={m_SVSServerProcess.Id}, log={logPath}";
            return true;
        }
        catch (Exception ex)
        {
            detail = "SVS 自动启动异常: " + ex.Message;
            return false;
        }
#else
        detail = "当前平台不支持自动启动 PowerShell；请手工运行 Server/SVS/start_svs_server.ps1";
        return false;
#endif
    }

    private void SetSVSStartupResult(bool succeeded, string detail)
    {
        m_SVSStartupSucceeded = succeeded;
        m_SVSStartupDetail = detail ?? "";
        m_SVSStartupInProgress = false;
    }

    private IEnumerator EnsureSVSReady(Action<bool, string> completed)
    {
        float timeout = Mathf.Clamp(m_SVSStartupTimeoutSeconds, 5f, 120f);
        if (m_SVSStartupInProgress)
        {
            float waitDeadline = Time.realtimeSinceStartup + timeout;
            while (m_SVSStartupInProgress &&
                   Time.realtimeSinceStartup < waitDeadline)
                yield return null;
            completed(m_SVSStartupSucceeded, m_SVSStartupDetail);
            yield break;
        }

        m_SVSStartupInProgress = true;
        bool healthy = false;
        string healthDetail = "";
        yield return ProbeSVSHealth((ok, detail) =>
        {
            healthy = ok;
            healthDetail = detail;
        });
        if (healthy)
        {
            SetSVSStartupResult(true, healthDetail);
            completed(true, m_SVSStartupDetail);
            yield break;
        }
        if (healthDetail.StartsWith("BACKEND_UNAVAILABLE", StringComparison.Ordinal))
        {
            SetSVSStartupResult(false, healthDetail);
            completed(false, m_SVSStartupDetail);
            yield break;
        }
        if (!m_AutoStartSVS)
        {
            SetSVSStartupResult(false, "9883 未运行且自动启动已关闭: " + healthDetail);
            completed(false, m_SVSStartupDetail);
            yield break;
        }

        bool processRunning = false;
        try
        {
            processRunning = m_SVSServerProcess != null &&
                             !m_SVSServerProcess.HasExited;
        }
        catch (Exception)
        {
            processRunning = false;
        }
        string launchDetail = "已有 SVS 启动进程";
        if (!processRunning && !TryLaunchSVS(out launchDetail))
        {
            SetSVSStartupResult(false, launchDetail);
            completed(false, m_SVSStartupDetail);
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + timeout;
        while (Time.realtimeSinceStartup < deadline)
        {
            yield return new WaitForSecondsRealtime(0.5f);
            yield return ProbeSVSHealth((ok, detail) =>
            {
                healthy = ok;
                healthDetail = detail;
            });
            if (healthy)
            {
                SetSVSStartupResult(true, "自动启动成功: " + healthDetail);
                completed(true, m_SVSStartupDetail);
                yield break;
            }
            if (healthDetail.StartsWith("BACKEND_UNAVAILABLE",
                StringComparison.Ordinal))
            {
                SetSVSStartupResult(false, healthDetail);
                completed(false, m_SVSStartupDetail);
                yield break;
            }
            try
            {
                if (m_SVSServerProcess != null && m_SVSServerProcess.HasExited)
                {
                    SetSVSStartupResult(false,
                        $"SVS 启动进程提前退出(code={m_SVSServerProcess.ExitCode})");
                    completed(false, m_SVSStartupDetail);
                    yield break;
                }
            }
            catch (Exception)
            {
                // Health remains authoritative.
            }
        }
        SetSVSStartupResult(false, "等待 9883 就绪超时: " + healthDetail);
        completed(false, m_SVSStartupDetail);
    }

    private string GetHumSVCBaseURL()
    {
        string url = (m_HumSVCURL ?? "").Trim().TrimEnd('/');
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        int lastSlash = url.LastIndexOf('/');
        if (lastSlash > scheme + 2) url = url.Substring(0, lastSlash);
        return url.TrimEnd('/');
    }

    private void OnSkillBecameActive(string skillName, SkillAccess access)
    {
        if (!string.Equals(skillName, "singing", StringComparison.OrdinalIgnoreCase) ||
            access != SkillAccess.Available || !m_EnableNeuralHumSVC ||
            !m_PrewarmHumSVCOnSingingSkillLoad || m_HumSVCPrewarmInProgress)
            return;
        //短窗口内反复卸载/重载只需复用服务端 90 秒 worker，不必刷请求。
        if (Time.realtimeSinceStartup - m_HumSVCPrewarmLastStartedAt < 20f) return;
        m_HumSVCPrewarmLastStartedAt = Time.realtimeSinceStartup;
        StartCoroutine(PrewarmHumSVCForSkill());
    }

    private IEnumerator PrewarmHumSVCForSkill()
    {
        m_HumSVCPrewarmInProgress = true;
        bool ready = false;
        string readyDetail = "";
        yield return EnsureHumSVCReady((ok, detail) =>
        {
            ready = ok;
            readyDetail = detail;
        });
        if (!ready)
        {
            m_HumSVCPrewarmInProgress = false;
            if (m_LogHumBack)
                Debug.LogWarning("[HumBack/SVC/Warmup] 后台预热跳过: " + readyDetail);
            yield break;
        }

        string warmupURL = GetHumSVCBaseURL() + "/warmup";
        using (UnityWebRequest request = UnityWebRequest.PostWwwForm(warmupURL, ""))
        {
            request.timeout = 180;
            //该协程只挂起自己；正式 LLM/TTS/字幕与用户打断都继续运行。
            yield return request.SendWebRequest();
            RecordSvcWarmupObservation(request.result == UnityWebRequest.Result.Success, request.downloadHandler.text);
            if (m_LogHumBack)
            {
                if (request.result == UnityWebRequest.Result.Success)
                    Debug.Log("[HumBack/SVC/Warmup] singing Skill 后台预热完成: " +
                              request.downloadHandler.text);
                else
                    Debug.LogWarning("[HumBack/SVC/Warmup] 后台预热失败但不阻塞对话: " +
                                     request.error + " HTTP=" + request.responseCode);
            }
        }
        m_HumSVCPrewarmInProgress = false;
    }

    private IEnumerator ProbeHumSVCHealth(Action<bool, string> completed)
    {
        string baseURL = GetHumSVCBaseURL();
        if (string.IsNullOrWhiteSpace(baseURL))
        {
            completed(false, "SVC URL 为空");
            yield break;
        }

        string healthURL = baseURL + "/health";
        using (UnityWebRequest request = UnityWebRequest.Get(healthURL))
        {
            request.timeout = 2;
            yield return request.SendWebRequest();
            bool ready = request.result == UnityWebRequest.Result.Success &&
                         request.responseCode >= 200 && request.responseCode < 300;
            m_ObservedSvcReachable = ready;
            m_ObservedSvcHealthAt = Time.realtimeSinceStartup;
            completed(ready, ready
                ? $"{healthURL} HTTP={request.responseCode}"
                : $"{healthURL} HTTP={request.responseCode} error={request.error}");
        }
    }

    private static string QuoteProcessArgument(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
    }

    private bool TryLaunchHumSVC(out string detail)
    {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        try
        {
            string projectRoot = System.IO.Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                detail = "无法从 Application.dataPath 定位 Unity 项目根目录";
                return false;
            }

            string relative = (m_HumSVCStartScriptRelativePath ?? "")
                .Replace('/', System.IO.Path.DirectorySeparatorChar);
            string scriptPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, relative));
            if (!System.IO.File.Exists(scriptPath))
            {
                detail = "启动脚本不存在: " + scriptPath;
                return false;
            }

            string scriptDirectory = System.IO.Path.GetDirectoryName(scriptPath);
            string runtimeDirectory = System.IO.Path.Combine(scriptDirectory, "runtime");
            System.IO.Directory.CreateDirectory(runtimeDirectory);
            string logPath = System.IO.Path.Combine(runtimeDirectory, "seedvc_server.log");
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " +
                            QuoteProcessArgument(scriptPath) + " -LogPath " + QuoteProcessArgument(logPath),
                WorkingDirectory = scriptDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            if (m_HumSVCServerProcess != null)
            {
                m_HumSVCServerProcess.Dispose();
                m_HumSVCServerProcess = null;
            }
            m_HumSVCServerProcess = System.Diagnostics.Process.Start(startInfo);
            if (m_HumSVCServerProcess == null)
            {
                detail = "Windows 未能创建 9882 启动进程";
                return false;
            }
            detail = $"已启动 PID={m_HumSVCServerProcess.Id}, script={scriptPath}, log={logPath}";
            return true;
        }
        catch (Exception ex)
        {
            detail = "自动启动异常: " + ex.Message;
            return false;
        }
#else
        detail = "当前平台不支持自动启动 PowerShell；请手工运行 Server/SeedVC/start_seedvc_server.ps1";
        return false;
#endif
    }

    private void SetHumSVCStartupResult(bool succeeded, string detail)
    {
        m_HumSVCStartupSucceeded = succeeded;
        m_HumSVCStartupDetail = detail ?? "";
        m_HumSVCStartupInProgress = false;
    }

    private IEnumerator EnsureHumSVCReady(Action<bool, string> completed)
    {
        float timeout = Mathf.Clamp(m_HumSVCStartupTimeoutSeconds, 5f, 120f);
        if (m_HumSVCStartupInProgress)
        {
            float waitDeadline = Time.realtimeSinceStartup + timeout;
            while (m_HumSVCStartupInProgress && Time.realtimeSinceStartup < waitDeadline)
                yield return null;
            completed(m_HumSVCStartupSucceeded,
                m_HumSVCStartupInProgress ? "等待另一个启动任务超时" : m_HumSVCStartupDetail);
            yield break;
        }

        m_HumSVCStartupInProgress = true;
        bool healthy = false;
        string healthDetail = "";
        yield return ProbeHumSVCHealth((ok, detail) =>
        {
            healthy = ok;
            healthDetail = detail;
        });
        if (healthy)
        {
            SetHumSVCStartupResult(true, "服务已在运行: " + healthDetail);
            completed(true, m_HumSVCStartupDetail);
            yield break;
        }

        if (!m_AutoStartHumSVC)
        {
            SetHumSVCStartupResult(false, "服务未运行且自动启动已关闭: " + healthDetail);
            completed(false, m_HumSVCStartupDetail);
            yield break;
        }

        bool processRunning = false;
        try
        {
            processRunning = m_HumSVCServerProcess != null && !m_HumSVCServerProcess.HasExited;
        }
        catch (Exception)
        {
            processRunning = false;
        }

        string launchDetail = "已有自动启动进程正在等待就绪";
        if (!processRunning && !TryLaunchHumSVC(out launchDetail))
        {
            SetHumSVCStartupResult(false, launchDetail);
            completed(false, m_HumSVCStartupDetail);
            yield break;
        }
        if (m_LogHumBack) Debug.Log("[HumBack/SVC] " + launchDetail);

        float deadline = Time.realtimeSinceStartup + timeout;
        while (Time.realtimeSinceStartup < deadline)
        {
            yield return new WaitForSecondsRealtime(0.5f);
            yield return ProbeHumSVCHealth((ok, detail) =>
            {
                healthy = ok;
                healthDetail = detail;
            });
            if (healthy)
            {
                SetHumSVCStartupResult(true, "自动启动成功: " + healthDetail);
                completed(true, m_HumSVCStartupDetail);
                yield break;
            }

            try
            {
                if (m_HumSVCServerProcess != null && m_HumSVCServerProcess.HasExited)
                {
                    SetHumSVCStartupResult(false,
                        $"启动进程提前退出(code={m_HumSVCServerProcess.ExitCode})；查看 Server/SeedVC/runtime/seedvc_server.log");
                    completed(false, m_HumSVCStartupDetail);
                    yield break;
                }
            }
            catch (Exception)
            {
                //进程句柄状态不可读时仍以 /health 为最终依据。
            }
        }

        SetHumSVCStartupResult(false,
            $"等待 9882 就绪超过 {timeout:F0}s；最后状态: {healthDetail}；查看 Server/SeedVC/runtime/seedvc_server.log");
        completed(false, m_HumSVCStartupDetail);
    }

    private void FallbackFromSVS(
        int generation,
        byte[] sourceWav,
        string targetPath,
        string failure)
    {
        if (generation != m_HumBackGeneration) return;
        if (m_LogHumBack)
            Debug.LogWarning("[HumBack/SVS] " + failure);
        if (m_AllowSVCFallbackFromSVS && m_EnableNeuralHumSVC)
        {
            m_PendingHumRenderer = "svc-fallback";
            m_HumBackPreparingCarrier = true;
            if (m_LogHumBack)
                Debug.LogWarning("[HumBack/SVS] 明确降级：改用 9882 SVC（非歌声生成）");
            StartCoroutine(RequestNeuralHumBack(generation, sourceWav, targetPath));
            return;
        }
        m_HumBackPreparingCarrier = false;
        if (m_AllowLegacyHumFallback) BeginLegacyHumBack(generation);
        else FinishHumBack(generation, false, failure);
    }

    private string ResolveSVSPromptAudioPath(string fallbackPath)
    {
        string configured = (m_SVSPromptAudioPath ?? "").Trim();
        if (string.IsNullOrWhiteSpace(configured)) return fallbackPath;
        if (Path.IsPathRooted(configured))
            return Path.GetFullPath(configured).Replace('\\', '/');

        string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? "";
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            string projectPath = Path.GetFullPath(Path.Combine(projectRoot, configured));
            if (File.Exists(projectPath)) return projectPath.Replace('\\', '/');
        }
        string assetPath = Path.GetFullPath(Path.Combine(Application.dataPath, configured));
        return File.Exists(assetPath)
            ? assetPath.Replace('\\', '/')
            : fallbackPath;
    }

    private IEnumerator RequestSingingVoiceSynthesis(
        int generation,
        byte[] sourceWav,
        string promptPath,
        string svcFallbackPath,
        string scoreJson,
        string scoreLyrics,
        string language)
    {
        bool serviceReady = false;
        string serviceDetail = "";
        yield return EnsureSVSReady((ready, detail) =>
        {
            serviceReady = ready;
            serviceDetail = detail;
        });
        if (generation != m_HumBackGeneration) yield break;
        if (!serviceReady)
        {
            FallbackFromSVS(
                generation,
                sourceWav,
                svcFallbackPath,
                "独立歌声合成服务未就绪: " + serviceDetail);
            yield break;
        }

        string requestId = Guid.NewGuid().ToString("N");
        WWWForm form = new WWWForm();
        form.AddBinaryData("audio_file", sourceWav, "recognized_singing.wav", "audio/wav");
        form.AddField("score_json", scoreJson ?? "");
        form.AddField("prompt_audio_path", promptPath ?? "");
        form.AddField("language", language ?? "");
        form.AddField("prompt_language", m_SVSPromptLanguage ?? "en");
        form.AddField("seed", m_PendingHumPerformanceSeed);
        form.AddField("request_id", requestId);
        form.AddField("max_seconds", m_HumBackMaxSeconds.ToString(
            "0.###", System.Globalization.CultureInfo.InvariantCulture));
        form.AddField("inference_steps", Mathf.Clamp(m_SVSInferenceSteps, 4, 32));

        using (UnityWebRequest request = UnityWebRequest.Post(m_SVSURL, form))
        {
            request.downloadHandler = new DownloadHandlerAudioClip(m_SVSURL, AudioType.WAV);
            request.timeout = Mathf.Clamp(m_SVSTimeoutSeconds, 30, 600);
            m_ActiveSVSRequest = request;
            m_ActiveSVSRequestId = requestId;
            float startedAt = Time.realtimeSinceStartup;
            if (m_LogHumBack)
                Debug.Log($"[HumBack/SVS] 开始真正歌声生成 sourceBytes={sourceWav.Length} " +
                          $"scoreBytes={(scoreJson ?? "").Length} lyrics={scoreLyrics?.Length ?? 0} " +
                          $"language={language} seed={m_PendingHumPerformanceSeed}");

            yield return request.SendWebRequest();
            if (m_ActiveSVSRequest == request)
            {
                m_ActiveSVSRequest = null;
                m_ActiveSVSRequestId = "";
            }
            if (generation != m_HumBackGeneration) yield break;

            if (request.result != UnityWebRequest.Result.Success)
            {
                string failure =
                    $"SVS 请求失败 HTTP={request.responseCode} error={request.error}";
                try
                {
                    byte[] responseBytes = request.downloadHandler != null
                        ? request.downloadHandler.data
                        : null;
                    if (responseBytes != null && responseBytes.Length > 0)
                    {
                        string body = System.Text.Encoding.UTF8.GetString(responseBytes);
                        if (!string.IsNullOrWhiteSpace(body))
                            failure += " detail=" + TruncateForFrame(body, 800);
                    }
                }
                catch (Exception)
                {
                    // Audio download handlers may hide an error JSON body.
                }
                FallbackFromSVS(generation, sourceWav, svcFallbackPath, failure);
                yield break;
            }

            string complete = request.GetResponseHeader("X-SVS-Complete") ?? "";
            if (complete != "1" &&
                !string.Equals(complete, "true", StringComparison.OrdinalIgnoreCase))
            {
                FallbackFromSVS(
                    generation,
                    sourceWav,
                    svcFallbackPath,
                    "SVS 返回结果没有通过完成标记校验");
                yield break;
            }

            AudioClip synthesized = null;
            try
            {
                synthesized = DownloadHandlerAudioClip.GetContent(request);
            }
            catch (Exception ex)
            {
                if (m_LogHumBack)
                    Debug.LogWarning("[HumBack/SVS] WAV 解码失败: " + ex.Message);
            }
            if (synthesized == null || synthesized.length <= 0.05f)
            {
                if (synthesized != null) Destroy(synthesized);
                FallbackFromSVS(
                    generation,
                    sourceWav,
                    svcFallbackPath,
                    "SVS 返回了空音频或无法解码的音频");
                yield break;
            }

            synthesized.name = "NeEEvA_Synthesized_Singing";
            string backend = request.GetResponseHeader("X-SVS-Backend") ??
                "singing-voice-synthesis";
            string sourceSeconds = request.GetResponseHeader("X-SVS-Source-Seconds") ?? "?";
            string outputSeconds = request.GetResponseHeader("X-SVS-Output-Seconds") ?? "?";
            string serverElapsed = request.GetResponseHeader("X-SVS-Elapsed") ?? "?";
            string promptCache = request.GetResponseHeader("X-SVS-Prompt-Cache") ?? "?";
            string targetMetadata = request.GetResponseHeader("X-SVS-Target-Metadata") ?? "?";
            string alignment = request.GetResponseHeader("X-SVS-Alignment") ?? "?";
            string lyricsSource = request.GetResponseHeader("X-SVS-Lyrics-Source") ?? "?";
            string alignmentCache =
                request.GetResponseHeader("X-SVS-Alignment-Cache") ?? "?";
            string preprocessSeconds =
                request.GetResponseHeader("X-SVS-Preprocess-Seconds") ?? "?";
            string modelSeconds =
                request.GetResponseHeader("X-SVS-Model-Load-Seconds") ?? "?";
            string transferSeconds =
                request.GetResponseHeader("X-SVS-Model-Transfer-Seconds") ?? "?";
            string inferenceSeconds =
                request.GetResponseHeader("X-SVS-Inference-Seconds") ?? "?";
            string inferenceSteps =
                request.GetResponseHeader("X-SVS-Inference-Steps") ?? "?";
            string persistentWorker =
                request.GetResponseHeader("X-SVS-Persistent-Worker") ?? "?";
            string captureId =
                request.GetResponseHeader("X-SVS-Capture") ?? "?";
            float unityElapsed = Time.realtimeSinceStartup - startedAt;
            string svsDiagnostic =
                $"independent SVS complete source={sourceSeconds}s output={outputSeconds}s, " +
                $"backend={backend}, promptCache={promptCache}, target={targetMetadata}, " +
                $"alignment={alignment}, lyrics={lyricsSource}, " +
                $"alignmentCache={alignmentCache}, " +
                $"preprocess={preprocessSeconds}s, model={modelSeconds}s, " +
                $"transfer={transferSeconds}s, worker={persistentWorker}, " +
                $"inference={inferenceSeconds}s/{inferenceSteps}steps, " +
                $"server={serverElapsed}s, capture={captureId}, " +
                $"total={unityElapsed:F2}s";

            if (m_EnableSVSRVCPostPolish)
            {
                byte[] generatedWav = null;
                try
                {
                    generatedWav = WavUtility.FromAudioClip(synthesized);
                }
                catch (Exception ex)
                {
                    if (m_LogHumBack)
                        Debug.LogWarning(
                            "[HumBack/SVS→RVC] 独立结果编码失败，直接播放未润色版本: " +
                            ex.Message);
                }
                if (generatedWav != null && generatedWav.Length > 44)
                {
                    m_PendingHumRenderer = "svc-post-polish";
                    StartCoroutine(RequestNeuralHumBack(
                        generation,
                        generatedWav,
                        svcFallbackPath,
                        synthesized,
                        svsDiagnostic));
                    yield break;
                }
            }

            m_HumBackPreparingCarrier = false;
            m_PendingHumRenderer = "independent-svs";
            ApplyHumBackGain(synthesized);
            PlayHumBackClip(
                generation,
                synthesized,
                svsDiagnostic);
        }
    }

    private int ResolvePendingPracticeStableIdForStreamSegment(int segmentPosition)
    {
        if (m_PendingPracticePitchPlan == null ||
            m_PendingPracticePitchPlan.Entries.Count == 0)
            return 0;
        //单段被自然切成多块时，每块都属于同一练唱段。
        if (m_PendingPracticePitchPlan.Entries.Count == 1)
            return m_PendingPracticePitchPlan.Entries[0].StableId;
        //practice 现在始终保留自然片段边界；长片段拆出的子块也继承同一清单段号。
        //echo/曲库整曲的 SegmentSourceIndices 仍只是块号，不能误当成练唱清单段号。
        if (!m_PendingHumIsPracticeComposition || m_PendingHumSegmentSources == null ||
            segmentPosition < 0 || segmentPosition >= m_PendingHumSegmentSources.Count)
            return 0;
        int sourceIndex = m_PendingHumSegmentSources[segmentPosition];
        for (int i = 0; i < m_PendingPracticePitchPlan.Entries.Count; i++)
        {
            PracticePitchPlanEntry entry = m_PendingPracticePitchPlan.Entries[i];
            if (entry.SourceIndex == sourceIndex) return entry.StableId;
        }
        return 0;
    }

    /// <summary>
    /// 分块生产者：转换服务仍按顺序一次处理一块，但每块回来就放进播放队列，
    /// 不再等待整首全部转换完成。所有块共用一个 performance seed 和整曲基准移调。
    /// </summary>
    private IEnumerator RequestPerSegmentNeuralHumBack(int generation, string targetPath)
    {
        var segments = m_PendingHumSegmentWavs;
        var gaps = m_PendingHumSegmentGaps;
        var medians = m_PendingHumSegmentMedians;
        int[] shifts = m_PendingHumSegmentShifts;

        ClearHumStreamClipBuffers(true);
        m_HumStreamProducerDone = false;
        m_HumStreamFailure = "";
        m_HumStreamTotalSegments = segments != null ? segments.Count : 0;
        m_HumStreamConvertedSegments = 0;
        m_HumStreamPlayedSegments = 0;
        m_HumStreamPlayedSeconds = 0f;
        m_HumStreamUnderrunSeconds = 0f;

        bool serviceReady = false;
        string serviceDetail = "";
        yield return EnsureHumSVCReady((ready, detail) =>
        {
            serviceReady = ready;
            serviceDetail = detail;
        });
        if (generation != m_HumBackGeneration) yield break;
        if (!serviceReady)
        {
            m_HumStreamProducerCoroutine = null;
            m_HumBackPreparingCarrier = false;
            FinishHumBack(generation, false, "歌声转换服务未就绪: " + serviceDetail);
            yield break;
        }

        //整条转换那一路在等待期间会让语义侧复核一次，这里等得更久，更该复核。
        BeginHumBackSemanticVeto(generation);

        m_HumStreamPlaybackCoroutine = StartCoroutine(PlayStreamedHumBack(generation));
        float startedAt = Time.realtimeSinceStartup;
        for (int i = 0; i < segments.Count; i++)
        {
            //已经转好但尚未播完的部分最多保留一个有限窗口。缓冲量同时包含 ready
            //与已调度块，长歌不会随总时长线性吃满内存。
            while (generation == m_HumBackGeneration &&
                   m_HumStreamBufferedSeconds >=
                       Mathf.Max(1f, m_HumStreamMaxBufferedSeconds - 0.5f))
                yield return null;
            if (generation != m_HumBackGeneration) yield break;

            string requestId = Guid.NewGuid().ToString("N");
            WWWForm form = new WWWForm();
            form.AddBinaryData("source_audio", segments[i], "singing_stream_chunk.wav", "audio/wav");
            form.AddField("target_path", targetPath);
            form.AddField("request_id", requestId);
            form.AddField("diffusion_steps", Mathf.Clamp(m_HumSVCDiffusionSteps, 4, 30));
            //必须关：开着的话每段各自被拉到目标音色中心，段间差被抹平——正是要避免的。
            form.AddField("auto_f0_adjust", "false");
            form.AddField("semitone_shift", shifts[i]);
            form.AddField("performance_seed", m_PendingHumPerformanceSeed);
            form.AddField("rms_mix_rate", InvariantFloat(m_PendingHumRmsMixRate));
            form.AddField("protect", InvariantFloat(m_PendingHumProtect));
            form.AddField("interpretation", InvariantFloat(m_PendingHumInterpretation));
            form.AddField("index_rate", InvariantFloat(Mathf.Clamp01(m_HumSVCIndexRate)));
            form.AddField("max_seconds", m_HumBackMaxSeconds.ToString(
                "0.###", System.Globalization.CultureInfo.InvariantCulture));

            using (UnityWebRequest request = UnityWebRequest.Post(m_HumSVCURL, form))
            {
                request.downloadHandler = new DownloadHandlerAudioClip(m_HumSVCURL, AudioType.WAV);
                request.timeout = Mathf.Clamp(m_HumSVCTimeoutSeconds, 30, 600);
                m_ActiveHumSVCRequest = request;
                m_ActiveHumSVCRequestId = requestId;
                if (m_LogHumBack)
                    Debug.Log($"[HumBack/Stream] 开始转换第{i + 1}/{segments.Count}块 " +
                              $"bytes={segments[i].Length} shift={shifts[i]:+0;-0;0}");
                yield return request.SendWebRequest();
                if (m_ActiveHumSVCRequest == request)
                {
                    m_ActiveHumSVCRequest = null;
                    m_ActiveHumSVCRequestId = "";
                }
                if (generation != m_HumBackGeneration) yield break;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    m_HumStreamFailure =
                        $"流式歌唱在第 {i + 1}/{segments.Count} 块失败 " +
                        $"HTTP={request.responseCode} error={request.error}";
                    break;
                }
                AudioClip piece = null;
                try { piece = DownloadHandlerAudioClip.GetContent(request); }
                catch (Exception ex)
                {
                    if (m_LogHumBack)
                        Debug.LogWarning("[HumBack/PerSegment] WAV 解码失败: " + ex.Message);
                }
                if (piece == null || piece.samples <= 0)
                {
                    if (piece != null) Destroy(piece);
                    m_HumStreamFailure =
                        $"流式歌唱在第 {i + 1}/{segments.Count} 块拿到空音频";
                    break;
                }
                if (i == 0 && HumBackVetoedBySemantics(generation))
                {
                    Destroy(piece);
                    m_HumStreamProducerCoroutine = null;
                    yield break;
                }
                piece.name = $"NeEEvA_Singing_Stream_{i + 1:000}";
                ApplyHumBackGain(piece);
                m_HumStreamReadyClips.Enqueue(new HumStreamReadyClip
                {
                    Clip = piece,
                    GapBefore = gaps != null && i < gaps.Count ? Mathf.Max(0f, gaps[i]) : 0f,
                    SegmentNumber = i + 1,
                    PracticeStableId = ResolvePendingPracticeStableIdForStreamSegment(i),
                    ExpectedPitchCenterMidi = medians != null && i < medians.Count &&
                                              i < shifts.Length && medians[i] > 0f
                        ? medians[i] + shifts[i]
                        : 0f,
                });
                m_HumStreamBufferedSeconds += piece.length;
                m_HumStreamConvertedSegments++;
                //第一块已经能播，准备期从此结束；消费者会维持 Playing/IsAISpeaking。
                if (i == 0) m_HumBackPreparingCarrier = false;
                if (m_LogHumBack)
                    Debug.Log($"[HumBack/Stream] 第{i + 1}/{segments.Count}块就绪 " +
                              $"audio={piece.length:F2}s buffered={m_HumStreamBufferedSeconds:F2}s " +
                              $"elapsed={Time.realtimeSinceStartup - startedAt:F1}s");
            }
        }
        m_HumStreamProducerDone = true;
        m_HumStreamProducerCoroutine = null;
        if (generation != m_HumBackGeneration) yield break;
        if (m_LogHumBack)
            Debug.Log($"[HumBack/Stream] 生产结束 converted={m_HumStreamConvertedSegments}/" +
                      $"{m_HumStreamTotalSegments} elapsed={Time.realtimeSinceStartup - startedAt:F1}s " +
                      $"failure=\"{m_HumStreamFailure}\"");
    }

    private IEnumerator PlayStreamedHumBack(int generation)
    {
        AudioSource secondary = GetOrCreateHumStreamSecondaryAudioSource();
        if (secondary == null || m_AudioSource == null)
        {
            m_HumStreamFailure = "无法创建双缓冲歌声播放器";
            m_HumStreamProducerDone = true;
        }

        double[] sourceAvailableAt = { 0d, 0d };
        double lastScheduledEnd = 0d;
        bool scheduledAny = false;
        while (generation == m_HumBackGeneration)
        {
            double now = AudioSettings.dspTime;
            for (int i = m_HumStreamScheduledClips.Count - 1; i >= 0; i--)
            {
                HumStreamScheduledClip scheduled = m_HumStreamScheduledClips[i];
                if (now + 0.01d < scheduled.EndDspTime) continue;
                if (scheduled.Source != null && scheduled.Source.clip == scheduled.Clip)
                {
                    scheduled.Source.Stop();
                    scheduled.Source.clip = null;
                }
                if (m_ActiveHumBackClip == scheduled.Clip) m_ActiveHumBackClip = null;
                byte[] pitchMeasurementWav = null;
                float pitchMeasurementSeconds = 0f;
                if (scheduled.Clip != null)
                {
                    pitchMeasurementSeconds = scheduled.Clip.length;
                    if (scheduled.PracticeStableId > 0 &&
                        scheduled.ExpectedPitchCenterMidi > 0f &&
                        m_PendingPracticePitchPlan != null)
                    {
                        try
                        {
                            pitchMeasurementWav = WavUtility.FromAudioClip(scheduled.Clip);
                        }
                        catch (Exception ex)
                        {
                            if (m_LogHumBack)
                                Debug.LogWarning(
                                    "[HumBack/PitchVerify] 流式块编码失败: " + ex.Message);
                        }
                    }
                    m_HumStreamPlayedSeconds += scheduled.Clip.length;
                    m_HumStreamBufferedSeconds = Mathf.Max(
                        0f, m_HumStreamBufferedSeconds - scheduled.Clip.length);
                    Destroy(scheduled.Clip);
                }
                m_HumStreamPlayedSegments++;
                if (scheduled.PracticeStableId > 0)
                {
                    m_HumStreamPlayedPracticeStableIds.Add(scheduled.PracticeStableId);
                    CommitPracticePitchPlan(
                        m_PendingPracticePitchPlan,
                        false,
                        new HashSet<int> { scheduled.PracticeStableId });
                }
                if (pitchMeasurementWav != null && m_PendingPracticePitchPlan != null)
                    RequestRenderedPitchMeasurement(
                        pitchMeasurementWav,
                        scheduled.PracticeStableId,
                        m_PendingPracticePitchPlan.Revision,
                        scheduled.ExpectedPitchCenterMidi,
                        pitchMeasurementSeconds);
                m_HumStreamScheduledClips.RemoveAt(i);
            }

            bool scheduledOne = false;
            bool textOutputDrained = IsHumBackTextOutputDrained(
                m_HumStreamWaitForTextOutputDrain,
                m_TTSSenderDone,
                m_TextOutputDrainedGeneration == m_FormalResponseGeneration,
                m_PendingChunks.Count > 0,
                m_PendingClips.Count > 0,
                m_AudioSource != null && m_AudioSource.isPlaying);
            if (m_HumStreamWaitForTextOutputDrain && textOutputDrained)
            {
                m_HumStreamWaitForTextOutputDrain = false;
                if (m_LogHumBack)
                    Debug.Log($"[HumBack/Stream] 正文已结束，使用已缓冲的 " +
                              $"{m_HumStreamReadyClips.Count} 块开始演唱");
            }
            if (textOutputDrained && m_HumStreamReadyClips.Count > 0 &&
                secondary != null && m_AudioSource != null)
            {
                int slot = -1;
                if (sourceAvailableAt[0] <= now + 0.01d) slot = 0;
                else if (sourceAvailableAt[1] <= now + 0.01d) slot = 1;
                if (slot >= 0)
                {
                    HumStreamReadyClip ready = m_HumStreamReadyClips.Dequeue();
                    if (ready.Clip == null)
                    {
                        m_HumStreamFailure = $"第 {ready.SegmentNumber} 块在排队后丢失";
                        m_HumStreamProducerDone = true;
                    }
                    else
                    {
                        AudioSource source = slot == 0 ? m_AudioSource : secondary;
                        double idealStart = scheduledAny
                            ? lastScheduledEnd + ready.GapBefore
                            : now + 0.05d;
                        double scheduledStart = Math.Max(idealStart, now + 0.05d);
                        if (scheduledAny && scheduledStart > idealStart + 0.01d)
                            m_HumStreamUnderrunSeconds += (float)(scheduledStart - idealStart);
                        source.clip = ready.Clip;
                        source.loop = false;
                        source.time = 0f;
                        source.PlayScheduled(scheduledStart);
                        double end = scheduledStart + ready.Clip.length;
                        sourceAvailableAt[slot] = end;
                        lastScheduledEnd = end;
                        scheduledAny = true;
                        scheduledOne = true;
                        m_ActiveHumBackClip = ready.Clip;
                        m_HumStreamScheduledClips.Add(new HumStreamScheduledClip
                        {
                            Clip = ready.Clip,
                            Source = source,
                            EndDspTime = end,
                            SegmentNumber = ready.SegmentNumber,
                            PracticeStableId = ready.PracticeStableId,
                            ExpectedPitchCenterMidi = ready.ExpectedPitchCenterMidi,
                        });
                        if (!m_HumBackPlaying)
                        {
                            m_HumBackPreparingCarrier = false;
                            m_HumBackPlaying = true;
                            IsAISpeaking = true;
                            m_TextBack.text = "♪";
                            SetAnimator("state", 2);
                        }
                        if (m_LogHumBack)
                            Debug.Log($"[HumBack/Stream] 已调度第{ready.SegmentNumber}/" +
                                      $"{m_HumStreamTotalSegments}块 source={slot} " +
                                      $"start={scheduledStart:F3} end={end:F3} gap={ready.GapBefore:F2}s");
                    }
                }
            }

            if (m_HumStreamWaitForTextOutputDrain && !textOutputDrained)
            {
                yield return null;
                continue;
            }
            if (m_HumStreamProducerDone && m_HumStreamReadyClips.Count == 0 &&
                m_HumStreamScheduledClips.Count == 0)
            {
                bool completed = string.IsNullOrEmpty(m_HumStreamFailure) &&
                    m_HumStreamPlayedSegments == m_HumStreamTotalSegments;
                string detail = completed
                    ? $"streamed {m_HumStreamPlayedSegments} chunks/{m_HumStreamPlayedSeconds:F1}s; " +
                      $"underrun={m_HumStreamUnderrunSeconds:F2}s"
                    : $"{m_HumStreamFailure}; 已播放 {m_HumStreamPlayedSegments}/" +
                      $"{m_HumStreamTotalSegments} 块（{m_HumStreamPlayedSeconds:F1}s）";
                m_HumStreamPlaybackCoroutine = null;
                FinishHumBack(generation, completed, detail);
                yield break;
            }

            if (!scheduledOne) yield return null;
        }
        m_HumStreamPlaybackCoroutine = null;
    }

    private AudioSource GetOrCreateHumStreamSecondaryAudioSource()
    {
        if (m_AudioSource == null) return null;

        // Never reuse the old implementation's second AudioSource on the character object:
        // Unity cannot decide which source should feed Audio2Lip/AEC OnAudioFilterRead there.
        if (m_HumStreamSecondaryAudioSource != null &&
            m_HumStreamSecondaryAudioSource.gameObject == m_AudioSource.gameObject)
        {
            Destroy(m_HumStreamSecondaryAudioSource);
            m_HumStreamSecondaryAudioSource = null;
        }
        if (m_HumStreamSecondaryAudioSource != null)
        {
            PlaybackAudioFilterRelay existingRelay =
                m_HumStreamSecondaryAudioSource.GetComponent<PlaybackAudioFilterRelay>();
            if (existingRelay == null)
                existingRelay = m_HumStreamSecondaryAudioSource.gameObject
                    .AddComponent<PlaybackAudioFilterRelay>();
            existingRelay.Configure(
                m_AudioSource.GetComponent<Audio2LipScript>(), EchoReferenceTap);
            return m_HumStreamSecondaryAudioSource;
        }

        m_HumStreamSecondaryAudioObject = new GameObject("HumBackStreamSecondaryAudio");
        m_HumStreamSecondaryAudioObject.layer = m_AudioSource.gameObject.layer;
        m_HumStreamSecondaryAudioObject.transform.SetParent(m_AudioSource.transform, false);
        m_HumStreamSecondaryAudioSource =
            m_HumStreamSecondaryAudioObject.AddComponent<AudioSource>();
        m_HumStreamSecondaryAudioSource.playOnAwake = false;
        m_HumStreamSecondaryAudioSource.loop = false;
        m_HumStreamSecondaryAudioSource.outputAudioMixerGroup = m_AudioSource.outputAudioMixerGroup;
        m_HumStreamSecondaryAudioSource.mute = m_AudioSource.mute;
        m_HumStreamSecondaryAudioSource.bypassEffects = m_AudioSource.bypassEffects;
        m_HumStreamSecondaryAudioSource.bypassListenerEffects = m_AudioSource.bypassListenerEffects;
        m_HumStreamSecondaryAudioSource.bypassReverbZones = m_AudioSource.bypassReverbZones;
        m_HumStreamSecondaryAudioSource.priority = m_AudioSource.priority;
        m_HumStreamSecondaryAudioSource.volume = m_AudioSource.volume;
        m_HumStreamSecondaryAudioSource.pitch = m_AudioSource.pitch;
        m_HumStreamSecondaryAudioSource.panStereo = m_AudioSource.panStereo;
        m_HumStreamSecondaryAudioSource.spatialBlend = m_AudioSource.spatialBlend;
        m_HumStreamSecondaryAudioSource.reverbZoneMix = m_AudioSource.reverbZoneMix;
        m_HumStreamSecondaryAudioSource.dopplerLevel = m_AudioSource.dopplerLevel;
        m_HumStreamSecondaryAudioSource.spread = m_AudioSource.spread;
        m_HumStreamSecondaryAudioSource.rolloffMode = m_AudioSource.rolloffMode;
        m_HumStreamSecondaryAudioSource.minDistance = m_AudioSource.minDistance;
        m_HumStreamSecondaryAudioSource.maxDistance = m_AudioSource.maxDistance;
        PlaybackAudioFilterRelay relay =
            m_HumStreamSecondaryAudioObject.AddComponent<PlaybackAudioFilterRelay>();
        relay.Configure(m_AudioSource.GetComponent<Audio2LipScript>(), EchoReferenceTap);
        return m_HumStreamSecondaryAudioSource;
    }

    private void ClearHumStreamClipBuffers(bool stopSources)
    {
        while (m_HumStreamReadyClips.Count > 0)
        {
            HumStreamReadyClip ready = m_HumStreamReadyClips.Dequeue();
            if (ready != null && ready.Clip != null)
            {
                if (m_ActiveHumBackClip == ready.Clip) m_ActiveHumBackClip = null;
                Destroy(ready.Clip);
            }
        }
        for (int i = 0; i < m_HumStreamScheduledClips.Count; i++)
        {
            HumStreamScheduledClip scheduled = m_HumStreamScheduledClips[i];
            if (scheduled == null) continue;
            if (stopSources && scheduled.Source != null)
            {
                scheduled.Source.Stop();
                if (scheduled.Source.clip == scheduled.Clip) scheduled.Source.clip = null;
            }
            if (scheduled.Clip != null)
            {
                if (m_ActiveHumBackClip == scheduled.Clip) m_ActiveHumBackClip = null;
                Destroy(scheduled.Clip);
            }
        }
        m_HumStreamScheduledClips.Clear();
        if (stopSources && m_HumStreamSecondaryAudioSource != null)
        {
            m_HumStreamSecondaryAudioSource.Stop();
            m_HumStreamSecondaryAudioSource.clip = null;
        }
        m_HumStreamBufferedSeconds = 0f;
    }

    private IEnumerator RequestNeuralHumBack(
        int generation,
        byte[] sourceWav,
        string targetPath,
        AudioClip independentFallback = null,
        string independentDiagnostic = "")
    {
        bool isSVSPostPolish = independentFallback != null;
        bool serviceReady = false;
        string serviceDetail = "";
        yield return EnsureHumSVCReady((ready, detail) =>
        {
            serviceReady = ready;
            serviceDetail = detail;
        });
        if (generation != m_HumBackGeneration)
        {
            if (independentFallback != null) Destroy(independentFallback);
            yield break;
        }
        if (!serviceReady)
        {
            m_HumBackPreparingCarrier = false;
            string failure = "歌声转换服务未就绪: " + serviceDetail;
            if (m_LogHumBack) Debug.LogWarning("[HumBack/SVC] " + failure);
            if (isSVSPostPolish)
            {
                m_PendingHumRenderer = "independent-svs";
                ApplyHumBackGain(independentFallback);
                PlayHumBackClip(
                    generation,
                    independentFallback,
                    independentDiagnostic + "; RVC post-polish skipped: " + failure);
            }
            else if (m_AllowLegacyHumFallback) BeginLegacyHumBack(generation);
            else FinishHumBack(generation, false, failure);
            yield break;
        }

        string requestId = Guid.NewGuid().ToString("N");
        WWWForm form = new WWWForm();
        form.AddBinaryData("source_audio", sourceWav, "recent_singing.wav", "audio/wav");
        form.AddField("target_path", targetPath);
        form.AddField("request_id", requestId);
        form.AddField("diffusion_steps", Mathf.Clamp(m_HumSVCDiffusionSteps, 4, 30));
        form.AddField(
            "auto_f0_adjust",
            !isSVSPostPolish && !m_PendingHumUsesMusicalPitchIntent &&
            m_HumSVCAutoF0Adjust ? "true" : "false");
        form.AddField(
            "semitone_shift",
            isSVSPostPolish
                ? 0
                : Mathf.Clamp(
                    m_HumSVCSemitoneShift + m_PendingHumSemitoneOffset,
                    -12,
                    12));
        form.AddField("performance_seed", m_PendingHumPerformanceSeed);
        form.AddField("rms_mix_rate", InvariantFloat(m_PendingHumRmsMixRate));
        form.AddField("protect", InvariantFloat(m_PendingHumProtect));
        form.AddField("interpretation", InvariantFloat(m_PendingHumInterpretation));
        form.AddField("index_rate", InvariantFloat(Mathf.Clamp01(m_HumSVCIndexRate)));
        form.AddField("max_seconds", m_HumBackMaxSeconds.ToString(
            "0.###", System.Globalization.CultureInfo.InvariantCulture));

        using (UnityWebRequest request = UnityWebRequest.Post(m_HumSVCURL, form))
        {
            request.downloadHandler = new DownloadHandlerAudioClip(m_HumSVCURL, AudioType.WAV);
            request.timeout = Mathf.Clamp(m_HumSVCTimeoutSeconds, 30, 600);
            m_ActiveHumSVCRequest = request;
            m_ActiveHumSVCRequestId = requestId;
            float startedAt = Time.realtimeSinceStartup;
            //转换要跑三十秒上下，这段时间音频还没出声——正好用来让语义侧复核一次。
            BeginHumBackSemanticVeto(generation);
            if (m_LogHumBack)
                Debug.Log($"[HumBack/SVC] 开始转换 sourceBytes={sourceWav.Length} " +
                          $"mode={(isSVSPostPolish ? "svc-post-polish" : "svc")} " +
                          $"steps={m_HumSVCDiffusionSteps} " +
                          $"autoF0={!isSVSPostPolish && m_HumSVCAutoF0Adjust} " +
                          $"seed={m_PendingHumPerformanceSeed} rms={m_PendingHumRmsMixRate:F2} " +
                          $"protect={m_PendingHumProtect:F2} " +
                          $"target=\"{targetPath}\"");

            yield return request.SendWebRequest();
            if (m_ActiveHumSVCRequest == request)
            {
                m_ActiveHumSVCRequest = null;
                m_ActiveHumSVCRequestId = "";
            }

            if (generation != m_HumBackGeneration)
            {
                if (independentFallback != null) Destroy(independentFallback);
                yield break;
            }
            m_HumBackPreparingCarrier = false;
            if (request.result != UnityWebRequest.Result.Success)
            {
                string failure = $"歌声转换请求失败 HTTP={request.responseCode} error={request.error}";
                try
                {
                    byte[] responseBytes = request.downloadHandler != null
                        ? request.downloadHandler.data
                        : null;
                    if (responseBytes != null && responseBytes.Length > 0)
                    {
                        string responseBody = System.Text.Encoding.UTF8.GetString(responseBytes);
                        if (!string.IsNullOrWhiteSpace(responseBody))
                            failure += " detail=" + TruncateForFrame(responseBody, 800);
                    }
                }
                catch (Exception)
                {
                    // DownloadHandlerAudioClip 在错误响应上不保证能暴露正文；保留基础错误即可。
                }
                if (m_LogHumBack) Debug.LogWarning("[HumBack/SVC] " + failure);
                if (isSVSPostPolish)
                {
                    m_PendingHumRenderer = "independent-svs";
                    ApplyHumBackGain(independentFallback);
                    PlayHumBackClip(
                        generation,
                        independentFallback,
                        independentDiagnostic + "; RVC post-polish failed: " + failure);
                }
                else if (m_AllowLegacyHumFallback) BeginLegacyHumBack(generation);
                else FinishHumBack(generation, false, failure);
                yield break;
            }

            AudioClip converted = null;
            try
            {
                converted = DownloadHandlerAudioClip.GetContent(request);
            }
            catch (Exception ex)
            {
                if (m_LogHumBack) Debug.LogWarning("[HumBack/SVC] WAV 解码失败: " + ex.Message);
            }
            if (converted == null || converted.length <= 0.05f)
            {
                if (converted != null) Destroy(converted);
                const string failure = "Seed-VC 返回的音频为空或无法解码";
                if (isSVSPostPolish)
                {
                    m_PendingHumRenderer = "independent-svs";
                    ApplyHumBackGain(independentFallback);
                    PlayHumBackClip(
                        generation,
                        independentFallback,
                        independentDiagnostic + "; RVC post-polish failed: " + failure);
                }
                else if (m_AllowLegacyHumFallback) BeginLegacyHumBack(generation);
                else FinishHumBack(generation, false, failure);
                yield break;
            }

            string complete = request.GetResponseHeader("X-SVC-Complete") ?? "";
            string sourceSeconds = request.GetResponseHeader("X-SVC-Source-Seconds") ?? "?";
            string outputSeconds = request.GetResponseHeader("X-SVC-Output-Seconds") ?? "?";
            if (!string.Equals(complete, "true", StringComparison.OrdinalIgnoreCase))
            {
                Destroy(converted);
                string failure =
                    $"歌声转换没有通过完整性校验 source={sourceSeconds}s output={outputSeconds}s";
                if (m_LogHumBack) Debug.LogWarning("[HumBack/SVC] " + failure);
                if (isSVSPostPolish)
                {
                    m_PendingHumRenderer = "independent-svs";
                    ApplyHumBackGain(independentFallback);
                    PlayHumBackClip(
                        generation,
                        independentFallback,
                        independentDiagnostic + "; RVC post-polish failed: " + failure);
                }
                else if (m_AllowLegacyHumFallback) BeginLegacyHumBack(generation);
                else FinishHumBack(generation, false, failure);
                yield break;
            }

            if (isSVSPostPolish)
            {
                Destroy(independentFallback);
                m_PendingHumRenderer = "svc-post-polish";
            }
            converted.name = "NeEEvA_Neural_HumBack";
            if (string.IsNullOrEmpty(m_PendingHumRenderer) ||
                m_PendingHumRenderer == "pending")
                m_PendingHumRenderer = "svc";
            ApplyHumBackGain(converted);
            string backend = request.GetResponseHeader("X-SVC-Backend") ?? "seed-vc";
            string device = request.GetResponseHeader("X-SVC-Device") ?? "unknown";
            string serverElapsed = request.GetResponseHeader("X-SVC-Elapsed-Seconds") ?? "?";
            string autoF0 = request.GetResponseHeader("X-SVC-Auto-F0-Adjust") ?? "?";
            string seed = request.GetResponseHeader("X-SVC-Seed") ?? "?";
            //服务端一直在返回决策时的空闲显存，只是从没打进日志。8/11 实测同一场里
            //三次转换全落 CPU，而事后查 /health 又显示空闲 1096MiB、will_use=cuda——
            //少了这个数就无法判断当时到底差多少，只能靠猜。
            string freeVram = request.GetResponseHeader("X-SVC-Free-VRAM-MiB") ?? "?";
            float unityElapsed = Time.realtimeSinceStartup - startedAt;
            if (HumBackVetoedBySemantics(generation))
            {
                Destroy(converted);
                yield break;
            }
            PlayHumBackClip(
                generation,
                converted,
                (isSVSPostPolish
                    ? independentDiagnostic + "; RVC post-polish complete "
                    : "neural SVC complete ") +
                $"source={sourceSeconds}s output={outputSeconds}s, " +
                $"backend={backend}, device={device}, freeVRAM={freeVram}MiB, " +
                $"autoF0={autoF0}, seed={seed}, " +
                $"server={serverElapsed}s, total={unityElapsed:F2}s");
        }
    }

    //本轮的语义裁决("singing"/"speech"/"" 未判出)，按用户轮次计序。
    private int m_SemanticCheckSerial = 0;
    private int m_SemanticVerdictSerial = -1;
    private string m_SemanticVerdict = "";
    //回哼排队时抓住的轮次序号——回哼可能几十秒后才播完，期间轮次不能已经翻页。
    private int m_HumBackVetoSerial = -1;

    /// <summary>
    /// 整段被判为唱歌时并行复核一次"这到底是唱还是说"。
    ///
    /// 声学侧为了抢时间必须宽松(约定跟唱后服务端还会主动放宽，8/12 实测 prob=0.57
    /// 未到 0.58 阈值仍判为唱)，语义侧则独立看一眼。用的是既有的分类器：不带上下文、
    /// 0.4 秒、只答一个词，拿裸转写调到过 17/20。
    ///
    /// **必须在这里发问，不能等 SVC 转换。** 挂在转换上时，"声学误判成唱歌但没触发
    /// 回哼"的轮次永远不会被复核——8/12 实测用户连着三轮用说话语调说「我要骗你唱歌」，
    /// 全部被判高区(0.68~0.76、岛占比 95%+)，只因当时 armed 恰好没开才没唱出来。
    ///
    /// 裁决赶不上主回复(那个请求几乎立刻就发)，所以它只能用来挡后面的回哼，
    /// 改不了本轮感知帧里已经写好的 `[演唱片段]` 标签。
    /// </summary>
    private void BeginTurnSemanticCheck(string perceivedText)
    {
        m_SemanticCheckSerial++;
        int serial = m_SemanticCheckSerial;
        m_SemanticVerdictSerial = -1;
        m_SemanticVerdict = "";
        if (string.IsNullOrEmpty(perceivedText) ||
            perceivedText.IndexOf("[演唱片段", StringComparison.Ordinal) < 0)
            return;
        if (m_ChatSettings == null) return;
        ChatQW qw = m_ChatSettings.m_ChatModel as ChatQW;
        if (qw == null) return;
        SenseVoiceSpeechToText senseVoice = m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText;
        if (senseVoice == null) return;
        //必须是裸转写，不读 m_LastUserEvidence 中的感知前缀；“演唱片段”等
        //程序标签不是独立语义证据。此处直接读取本次 ASR，避免旧用户轮串入。
        string transcript = (senseVoice.LastText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(transcript)) return;
        string segment = senseVoice.LastSegmentLyrics ?? "";

        qw.ClassifyUtteranceMode(transcript, verdict =>
        {
            if (serial != m_SemanticCheckSerial) return;   //轮次已翻页，结果作废
            m_SemanticVerdictSerial = serial;
            m_SemanticVerdict = (verdict ?? "").Trim().ToLowerInvariant();
            if (m_LogHumBack)
                Debug.Log($"[Singing/Semantic] 本轮语义复核 = \"{m_SemanticVerdict}\" " +
                          $"(声学判唱) 文本=\"{transcript}\"");
        }, segment);
    }

    /// <summary>
    /// 回哼开始转换时记下它属于哪一轮。复核本身已经在轮次开始时发出去了。
    /// 曲库演唱和练唱合成唱的不是"用户刚这一句"，与本轮是不是唱歌无关，不受否决。
    /// </summary>
    private void BeginHumBackSemanticVeto(int generation)
    {
        m_HumBackVetoSerial =
            (m_PendingHumIsPracticeComposition || m_PendingHumIsCatalogSong)
                ? -1 : m_SemanticCheckSerial;
    }

    /// <summary>
    /// 转换完成、即将播放时问一次：语义侧有没有明确说过"这一轮是说话"。
    /// 裁决没赶上就当没否决——绝不阻塞播放，快速路径的意义就是不等。
    /// </summary>
    private bool HumBackVetoedBySemantics(int generation)
    {
        if (m_HumBackVetoSerial < 0) return false;
        if (m_HumBackVetoSerial != m_SemanticCheckSerial) return false;  //已经翻页
        if (m_SemanticVerdictSerial != m_SemanticCheckSerial) return false;
        if (m_SemanticVerdict != "speech") return false;

        //声学 uncertain 时，程序自动路径已经被 m_FinalModeSoftDowngrade 拦住；
        //还能走到这里的只能是看过完整证据后的正式角色 LLM 主动调用。此时轻量
        //裸文本分类器不能反过来否决更有上下文的角色决定。
        //这里原来完全没应用它，于是同一个冲突被两条路判出相反结果：
        //8/23 实测 prob=0.73 stab=0.71 岛=9.47s/内容10.60s 的一轮真唱，
        //[模态判定] 已经决定「保留演唱，改为出声追问」，转换也跑完了，
        //却在这里被同一个文字裁决整段丢弃，用户当场问「为什么会失败呢？」。
        SenseVoiceSpeechToText vetoSenseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (vetoSenseVoice != null && vetoSenseVoice.LastAcousticModeUncertain)
        {
            if (m_LogHumBack)
                Debug.Log("[HumBack/Veto] 声学仍为 uncertain；程序自动路径已关闭，" +
                          "保留正式角色 LLM 的主动回唱决定");
            return false;
        }
        if (vetoSenseVoice != null && vetoSenseVoice.LastSingingProbability >= 0.58f)
        {
            if (m_LogHumBack)
                Debug.Log("[HumBack/Veto] 文字判说话，但声学 " +
                          $"prob={vetoSenseVoice.LastSingingProbability:F3} 在高区" +
                          "——按软降级规则不否决，照常播出");
            return false;
        }

        if (m_LogHumBack)
            Debug.LogWarning("[HumBack/Veto] 转换完成，但语义复核判定本轮是说话；" +
                             "丢弃已转换音频，不播出");
        RecordHumBackResult(
            "未执行：转换完成前复核发现用户这一句其实是在说话(多半是在商量怎么唱、" +
            "或者宣布马上要开始)，不是演唱，已经放弃这次回哼。" +
            "不得声称唱过；就当普通对话回应他，等他真的唱出来再回哼。",
            true);
        FinishHumBack(generation, false, "semantic-veto");
        ResumeNormalTurnAfterVeto();
        return true;
    }

    //快车道跳过 LLM 时暂存的本轮输入，只在否决把回哼拦下时用来补一次普通回复。
    private string m_VetoFallbackLlmInput;
    private string m_VetoFallbackPostWord;

    /// <summary>
    /// 否决拦下回哼之后，这一轮必须退回普通对话路径——否则用户说了话却听不到任何
    /// 回应。RecordHumBackResult 写的"未执行"说明要等下一次 LLM 请求才被读到，
    /// 而下一次很可能是自主发言，读的是感知帧、不是用户这一句。
    /// </summary>
    private void ResumeNormalTurnAfterVeto()
    {
        string llmInput = m_VetoFallbackLlmInput;
        string postWord = m_VetoFallbackPostWord;
        m_VetoFallbackLlmInput = null;
        m_VetoFallbackPostWord = null;
        if (string.IsNullOrWhiteSpace(llmInput)) return;
        if (m_ChatSettings == null || m_ChatSettings.m_ChatModel == null) return;

        if (m_LogHumBack)
            Debug.Log("[HumBack/Veto] 回哼被否决，本轮退回普通对话补一次回复");
        if (m_UseStreaming && m_IsVoiceMode && m_ChatSettings.m_TextToSpeech != null)
        {
            StartStreaming(llmInput, false, postWord ?? "", false, false);
        }
        else
        {
            PublishSpokenPrefixToLlm();
            m_ChatSettings.m_ChatModel.RequestContext = BuildMotionRequestContext(BuildFormalObservationContext());
            m_FormalResponseInFlight = true;
            m_ChatSettings.m_ChatModel.PostSpeechMessage(llmInput, CaptureMotionResponseCallback());
        }
    }

    /// <summary>
    /// 回哼素材的开头比"检测到的歌声起点"早多少——早出来的那一段很可能是说话。
    ///
    /// 8/20 实测：一轮 applied=3.75s 而岛起点=7.23s，中间 3.48 秒是用户的中文说话，
    /// 被原样用歌声唱了回去。用户连着三轮说「你把我说话的部分也复读出来了」，
    /// 而工具结果只写"成功播放"——她没有任何依据能察觉，最后归结成
    /// 「歌声の中に言葉が混じってしまうことがあって……それは仕方がないの」。
    ///
    /// **只报不改。** 保守取早本身是对的(防止乐句开头被切掉，那是文档里记着的老问题)，
    /// 要区分"多留半秒余量"和"多留三秒说话"需要样本，现在只有 3 个混合轮。
    /// 触发条件是两个事实、不是调出来的阈值：前面确实多留了，且整轮里确实有说话。
    /// </summary>
    /// <summary>
    /// 把这一次移调实际发生的事写清楚：整条统一抬升 ≠ 只动某一段，她分不清就会一直空转。
    /// </summary>
    /// <summary>
    /// 练唱会话里有没有"不能按默认顺序全唱"的理由：混着不同语言的歌，或者同一句有多遍。
    /// </summary>
    /// <remarks>
    /// 用户 8/22 指出的两种情况，人类都能靠对话消歧：换歌了(不同的歌不该混着唱)、
    /// 唱错重来了(该用新的那一遍)。她两次都当场回应说理解了，但调 practice 时
    /// 读的是这份清单而不是二十轮之前的对话，于是把两首歌和同一句的两遍全合了。
    /// 这里只陈述事实并要求她定夺——判断本身要靠 ←唱这段前用户说 那一行，
    /// 实在读不出来就该问用户，而不是替他决定。
    /// </remarks>
    private static string BuildPracticeAmbiguityNote(
        List<SenseVoiceSpeechToText.PracticePhraseInfo> phrases)
    {
        if (phrases == null || phrases.Count < 2) return "";
        var languages = new List<string>();
        var repeated = new List<string>();
        foreach (var ph in phrases)
        {
            string lang = (ph.Language ?? "").Trim();
            if (lang.Length > 0 && !languages.Contains(lang)) languages.Add(lang);
            if (ph.TakeTotal > 1) repeated.Add($"[{ph.Index}]");
        }
        var reasons = new List<string>();
        if (languages.Count > 1)
            reasons.Add($"这些片段跨了 {string.Join("/", languages)} 两种以上语言，多半不是同一首歌");
        if (repeated.Count > 1)
            reasons.Add($"{string.Join("、", repeated)} 里有同一句的多遍");
        if (reasons.Count == 0) return "";
        return "；⚠ " + string.Join("；", reasons) +
               "——**这种情况下不要用默认顺序全部唱出去**。先看每段后面 ←唱这段前用户说 的那句话：" +
               "用户说过\"换一首歌\"就说明后面是另一首，说过\"唱错了重来\"就说明该用新的那一遍。" +
               "读不出来就直接问用户要哪几段，别替他决定";
    }

    /// <summary>
    /// 把 key 的绝对值语义写进工具结果，并附上上一次写的值。
    /// 单独成一个方法是因为它每次都要出现——这是个反复被误解的点，不是偶发提醒。
    /// </summary>
    private string BuildPerSegmentKeySemanticsNote()
    {
        string current = m_PendingPerSegmentKeyText;
        string previous = m_LastPerSegmentKeyText;
        m_LastPerSegmentKeyText = current;
        m_PendingPerSegmentKeyText = "";
        if (string.IsNullOrEmpty(current)) return "";

        string note = $"相对 key=\"{current}\"（相对于各段原始录音，不是增量）";
        if (!string.IsNullOrEmpty(previous) && previous != current)
            note += $"，previous_key=\"{previous}\"";
        return note + "。这是旧 key 兼容结果；以后用 pitch_plan 逐段表达 keep、绝对音名、" +
               "相对音程或 match:清单段号，用 transpose 表达整体升降。";
    }

    private PracticePitchPlan BuildPracticePitchPlan(
        SenseVoiceSpeechToText senseVoice,
        SenseVoiceSpeechToText.PracticeComposition composition,
        int[] executedShifts,
        float[] intendedCenters,
        string musicalIntent)
    {
        if (senseVoice == null || composition == null || executedShifts == null ||
            composition.SegmentMedians == null ||
            executedShifts.Length != composition.SegmentMedians.Count)
            return null;
        List<SenseVoiceSpeechToText.PracticePhraseInfo> phrases =
            senseVoice.DescribePracticePhrases();
        var plan = new PracticePitchPlan
        {
            MusicalIntent = musicalIntent ?? "",
        };
        for (int i = 0; i < executedShifts.Length; i++)
        {
            int sourceIndex = composition.SegmentSourceIndices != null &&
                              i < composition.SegmentSourceIndices.Count
                ? composition.SegmentSourceIndices[i]
                : i + 1;
            SenseVoiceSpeechToText.PracticePhraseInfo phrase =
                FindPracticePhraseInfo(phrases, sourceIndex);
            float sourceCenter = composition.SegmentMedians[i];
            float executedCenter = sourceCenter + executedShifts[i];
            float requestedCenter = intendedCenters != null && i < intendedCenters.Length
                ? intendedCenters[i]
                : executedCenter;
            plan.Entries.Add(new PracticePitchPlanEntry
            {
                StableId = phrase != null ? phrase.StableId : 0,
                SourceIndex = sourceIndex,
                SourceCenterMidi = sourceCenter,
                RequestedCenterMidi = requestedCenter,
                TargetCenterMidi = executedCenter,
                ExecutedShift = executedShifts[i],
            });
        }
        if (plan.Entries.Count == 0) return null;
        var currentStableIds = new int[m_PracticePitchStates.Count];
        m_PracticePitchStates.Keys.CopyTo(currentStableIds, 0);
        var nextStableIds = new int[plan.Entries.Count];
        for (int i = 0; i < plan.Entries.Count; i++)
            nextStableIds[i] = plan.Entries[i].StableId;
        plan.MergeWithCurrentArrangement = ShouldMergePracticeArrangement(
            currentStableIds, nextStableIds);
        plan.Revision = ++m_PracticePitchPlanRevision;
        return plan;
    }

    private static bool ShouldMergePracticeArrangement(
        int[] currentStableIds,
        int[] nextStableIds)
    {
        if (currentStableIds == null || currentStableIds.Length == 0 ||
            nextStableIds == null || nextStableIds.Length == 0)
            return false;
        for (int i = 0; i < nextStableIds.Length; i++)
            if (nextStableIds[i] <= 0)
                return false;
        return true;
    }

    /// <summary>
    /// 只有真实播放完成的块才能推进“当前角色演唱中心音高”。先写入实际执行指令的
    /// 预计结果；同一 revision 的异步输出 F0 回测回来后不会被 Finish 再覆盖掉。
    /// </summary>
    private void CommitPracticePitchPlan(
        PracticePitchPlan plan,
        bool completed,
        HashSet<int> fullyPlayedStableIds)
    {
        if (plan == null) return;
        bool anyHeard = false;
        for (int i = 0; i < plan.Entries.Count; i++)
        {
            PracticePitchPlanEntry entry = plan.Entries[i];
            if (entry.StableId > 0 && (completed ||
                (fullyPlayedStableIds != null && fullyPlayedStableIds.Contains(entry.StableId))))
            {
                anyHeard = true;
                break;
            }
        }
        if (anyHeard && !plan.ArrangementInitialized)
        {
            // State belongs to the stable recording, not the latest selected set.
            // Singing B for the first time must not erase A's measured output.
            // Drop/revision paths invalidate only their explicitly affected clip.
            plan.ArrangementInitialized = true;
        }
        for (int i = 0; i < plan.Entries.Count; i++)
        {
            PracticePitchPlanEntry entry = plan.Entries[i];
            if (entry.StableId <= 0) continue;
            bool heard = completed ||
                (fullyPlayedStableIds != null && fullyPlayedStableIds.Contains(entry.StableId));
            if (!heard) continue;
            if (m_PracticePitchStates.TryGetValue(
                    entry.StableId, out PracticePitchState current) &&
                current.Revision == plan.Revision)
            {
                current.RequestedCenterMidi = entry.RequestedCenterMidi;
                current.ExpectedCenterMidi = entry.TargetCenterMidi;
                current.ExecutedShift = entry.ExecutedShift;
                current.UpdatedRealtime = Time.realtimeSinceStartup;
                current.FullyPlayed = current.FullyPlayed || completed;
                if (!current.Measured) current.CenterMidi = entry.TargetCenterMidi;
                continue;
            }
            m_PracticePitchStates[entry.StableId] = new PracticePitchState
            {
                StableId = entry.StableId,
                Revision = plan.Revision,
                CenterMidi = entry.TargetCenterMidi,
                RequestedCenterMidi = entry.RequestedCenterMidi,
                ExpectedCenterMidi = entry.TargetCenterMidi,
                ExecutedShift = entry.ExecutedShift,
                UpdatedRealtime = Time.realtimeSinceStartup,
                FullyPlayed = completed,
                Measured = false,
            };
        }
    }

    /// <summary>
    /// 播放完成后才发起，不进入用户 ASR，也不写练唱记忆。流式长歌按每块“实测值减
    /// 本块预计值”累计偏差，避免旋律本身在不同区间的高低变化污染整段中心音高。
    /// revision 防止上一轮迟到的结果覆盖同一练唱段的新一轮演唱。
    /// </summary>
    private void RequestRenderedPitchMeasurement(
        byte[] wavBytes,
        int stableId,
        int revision,
        float expectedChunkCenterMidi,
        float durationSeconds)
    {
        if (wavBytes == null || wavBytes.Length <= 44 || stableId <= 0 ||
            revision <= 0 || expectedChunkCenterMidi <= 0f)
            return;
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (senseVoice == null) return;

        senseVoice.AnalyzeRenderedSingingPitch(wavBytes, result =>
        {
            if (result == null || !result.Ok)
            {
                if (m_LogHumBack)
                    Debug.LogWarning("[HumBack/PitchVerify] 输出回测失败，保留预计值: " +
                                     (result != null ? result.Error : "empty result"));
                return;
            }
            if (!m_PracticePitchStates.TryGetValue(
                    stableId, out PracticePitchState state) ||
                state.Revision != revision)
            {
                if (m_LogHumBack)
                    Debug.Log($"[HumBack/PitchVerify] 忽略迟到结果 stable={stableId} " +
                              $"revision={revision}");
                return;
            }

            float weight = Mathf.Max(0.05f, durationSeconds) *
                           Mathf.Max(0.05f, result.VoicedRatio);
            float deviation = result.CenterMidi - expectedChunkCenterMidi;
            state.WeightedDeviation += deviation * weight;
            state.WeightedStability += result.Stability * weight;
            state.WeightedVoicedRatio += result.VoicedRatio * weight;
            state.MeasurementWeight += weight;
            state.CenterMidi = state.ExpectedCenterMidi +
                               state.WeightedDeviation / state.MeasurementWeight;
            state.MeasurementBackend = result.Backend ?? "";
            state.Measured = true;
            state.UpdatedRealtime = Time.realtimeSinceStartup;
            if (m_LogHumBack)
                Debug.Log($"[HumBack/PitchVerify] stable={stableId} revision={revision} " +
                          $"expectedChunk={FormatPitchMidiPrecise(expectedChunkCenterMidi)} " +
                          $"measuredChunk={FormatPitchMidiPrecise(result.CenterMidi)} " +
                          $"deviation={deviation:+0.00;-0.00;0.00}st " +
                          $"current={FormatPitchMidiPrecise(state.CenterMidi)} " +
                          $"stability={result.Stability:F2} voiced={result.VoicedRatio:F2} " +
                          $"backend={result.Backend}");
        });
    }

    private static string BuildPracticePitchPlanResult(
        PracticePitchPlan plan,
        bool completed,
        HashSet<int> fullyPlayedStableIds)
    {
        if (plan == null || plan.Entries.Count == 0) return "";
        var values = new List<string>();
        for (int i = 0; i < plan.Entries.Count; i++)
        {
            PracticePitchPlanEntry entry = plan.Entries[i];
            bool heard = completed || (entry.StableId > 0 && fullyPlayedStableIds != null &&
                                        fullyPlayedStableIds.Contains(entry.StableId));
            string status = heard
                ? (completed ? "已完整播放" : "中断前已播放部分")
                : "未播放";
            string commandBasis = Mathf.Abs(
                entry.RequestedCenterMidi - entry.TargetCenterMidi) >= 0.05f
                ? $"，底层指令基准{FormatPitchMidiPrecise(entry.TargetCenterMidi)}"
                : "";
            values.Add($"清单[{entry.SourceIndex}] 目标" +
                       $"{FormatPitchMidiPrecise(entry.RequestedCenterMidi)}" +
                       $"（{status}，实发{entry.ExecutedShift:+0;-0;0}半音" +
                       commandBasis + "）");
        }
        return "本次角色演唱音高计划=" + string.Join("、", values) +
               "。这些值依据实际移调指令计算；已播放音频会在后台做无副作用 F0 回测，" +
               "完成后把感知帧更新为输出实测，不阻塞播放。当前预计值不得说成歌曲调性" +
               "或已经完成的声学验证。";
    }

    private static string BuildIgnoredExtraHumBackFact(int ignoredCount)
    {
        return ignoredCount > 0
            ? $" 同一回复中另外 {ignoredCount} 个 hum_back 没有执行；" +
              "一次回复只执行第一个真实歌唱动作。"
            : "";
    }

    private string BuildCurrentPracticePitchStateSummary(
        List<SenseVoiceSpeechToText.PracticePhraseInfo> phrases)
    {
        if (phrases == null || phrases.Count == 0 || m_PracticePitchStates.Count == 0)
            return "";
        var values = new List<string>();
        for (int i = 0; i < phrases.Count; i++)
        {
            SenseVoiceSpeechToText.PracticePhraseInfo phrase = phrases[i];
            if (phrase == null || phrase.StableId <= 0 ||
                !m_PracticePitchStates.TryGetValue(
                    phrase.StableId, out PracticePitchState state))
                continue;
            string kind = state.Measured ? "输出实测" : "预计";
            string target = state.Measured
                ? $"，目标{FormatPitchMidiPrecise(state.RequestedCenterMidi)}"
                : "";
            values.Add($"[{phrase.Index}] {FormatPitchMidiPrecise(state.CenterMidi)}" +
                       $"（{kind}{target}）");
        }
        return values.Count == 0
            ? ""
            : "\n角色当前演唱编排的逐段中心音高（每段只保留最近一次，局部重唱只更新该段）: " +
              string.Join("；", values) +
              "。输出实测来自角色生成音频的独立 F0 回测；预计值来自已执行移调指令。";
    }

    private string BuildPracticeShiftNote(
        int[] segmentShifts, List<float> segmentMedians, List<int> segmentSources,
        int wholeClipShift, bool musicalPitchIntent)
    {
        string note = m_LastPracticeShiftNote ?? "";
        if (segmentShifts != null && segmentShifts.Length >= 2)
        {
            //8/20 实测：她连着三轮用整条 key 想只抬第二段，每次都把三段一起抬走，
            //还对用户说"这次我会确保只调整第二段"。所以这里必须写明是逐段还是整条。
            note += "本次按逐段移调执行，各段实际发出的半音数为 " +
                    string.Join("、", segmentShifts) +
                    "（已含把音域拉进你声线的基准量，所以数字不等于相对 key）。";
            //光给发出去的数字还不够：8/21 实测她拿到回报之后仍然一个半音一个半音地试，
            //三轮只从 +1 加到 +3，而实际差 6 个。把**移调后各段的中心音高**也报出来，
            //她就能直接相减算出还差多少，不必靠用户一轮轮说"还是低"。
            if (segmentMedians != null && segmentMedians.Count == segmentShifts.Length)
            {
                var after = new List<string>(segmentShifts.Length);
                for (int i = 0; i < segmentShifts.Length; i++)
                {
                    //用**清单段号**而不是 order 里的位置。用户说的"第四段"指的是清单，
                    //位置编号会让两边对不上——8/25 就是这么调错了一段。
                    string label = segmentSources != null && i < segmentSources.Count
                        ? $"清单[{segmentSources[i]}]"
                        : $"第{i + 1}段";
                    if (segmentMedians[i] <= 0f) { after.Add(label + " 未知"); continue; }
                    float result = segmentMedians[i] + segmentShifts[i];
                    after.Add($"{label} {SenseVoiceSpeechToText.MidiToNoteName(result)}" +
                              $"({Mathf.RoundToInt(result)})");
                }
                note += "按移调指令计算的中心音高=" + string.Join("、", after) + "。" +
                        "这是预计值而非输出音频 F0 回测；以后以某段为准写 " +
                        "pitch_plan=\"match:清单段号\"，绝对目标写 pitch_plan=\"D4\"。";
                //光报结果中心音高还不够。8/26 实测她 key="2,0,2" 一次调齐了三段，
                //下一轮想"再抬第三段半个音"就写了 key="0,0,1"——**当成增量了**。
                //于是前两段被打回原始音高，刚调齐的又散了。她的 reason 写着
                //「第一段和第二段保持原样」，意图是对的，是没人告诉她 key 的语义。
                //所以这两句必须每次都在：本次写了什么、上次写了什么、以及"照抄再改"。
                note += BuildPerSegmentKeySemanticsNote();
            }
        }
        else if (!musicalPitchIntent && wholeClipShift != 0)
        {
            note += $"注意：本次 key={wholeClipShift:+0;-0} 作用在**整条**音频上，" +
                    "所有段一起被抬高或降低了，段与段之间的高低差没有变。" +
                    "用户要的若是「只动其中某一段」，把 key 写成逐段形式（如 key=\"0,4,0\"，" +
                    "一项对应 order 里的一段）。";
        }
        return note;
    }


    private string BuildHumBackLeadInNote()
    {
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (senseVoice == null) return "";
        float leadIn = senseVoice.LastCropLeadInSeconds;
        if (leadIn <= 0.01f) return "";
        string whole = (senseVoice.LastText ?? "").Trim();
        string segment = (senseVoice.LastSegmentLyrics ?? "").Trim();
        //整轮比唱的那段长出来的部分 = 这一轮里说过的话
        int spokenChars = Mathf.Max(0, whole.Length - segment.Length);
        if (spokenChars <= 0) return "";
        return $"（注意：这次回哼的素材从检测到的歌声起点往前多取了 {leadIn:F1} 秒，" +
               $"而这一轮里还有约 {spokenChars} 个字是说出来的——" +
               "开头那几秒有可能把用户说的话也一起唱了出去。" +
               "若用户指出你复读了他说话的部分，这就是原因，如实承认并请他重唱一小段即可，" +
               "不要说成是没办法的事。）";
    }

    private void ApplyHumBackGain(AudioClip clip)
    {
        if (clip == null || clip.samples <= 0 || clip.channels <= 0) return;
        float[] samples = new float[clip.samples * clip.channels];
        if (!clip.GetData(samples, 0)) return;
        float peak = 0f;
        for (int i = 0; i < samples.Length; i++) peak = Mathf.Max(peak, Mathf.Abs(samples[i]));
        if (peak <= 0.00001f) return;
        float scale = Mathf.Clamp(m_HumBackGain / peak, 0.05f, 3f);
        for (int i = 0; i < samples.Length; i++) samples[i] = Mathf.Clamp(samples[i] * scale, -1f, 1f);
        clip.SetData(samples, 0);
    }

    private static string SelectHumCarrierText(string language)
    {
        string lower = (language ?? "").Trim().ToLowerInvariant();
        // A longer sustained vowel gives the voice-preserving renderer enough distinct
        // pitch-synchronous grains to retain breath and natural cycle variation.
        if (lower.StartsWith("ja") || lower.Contains("日")) return "んーーー";
        if (lower.StartsWith("en") || lower.Contains("英")) return "Mmmmm...";
        return "嗯————";
    }

    private void BuildAndPlayHumBack(int generation, AudioClip carrier)
    {
        if (generation != m_HumBackGeneration) return;
        m_HumBackPreparingCarrier = false;

        string diagnostic;
        AudioClip hum = MelodyHumSynthesizer.CreateHumClip(
            carrier,
            m_PendingHumTimeline,
            m_PendingHumFrameSeconds,
            m_HumPreferredMedianMidi,
            m_HumBackMaxSeconds,
            m_HumBackGain,
            out diagnostic);
        if (hum == null)
        {
            if (m_LogHumBack) Debug.LogWarning("[HumBack] 回哼合成失败: " + diagnostic);
            FinishHumBack(generation, false, diagnostic);
            return;
        }

        PlayHumBackClip(generation, hum, diagnostic);
    }

    private void PlayHumBackClip(int generation, AudioClip hum, string diagnostic)
    {
        if (generation != m_HumBackGeneration)
        {
            if (hum != null) Destroy(hum);
            return;
        }
        m_HumBackPreparingCarrier = false;
        m_ActiveHumBackClip = hum;
        m_HumBackPlaying = true;
        IsAISpeaking = true;
        m_TextBack.text = "♪";
        m_AudioSource.clip = hum;
        m_AudioSource.loop = false;
        m_AudioSource.Play();
        SetAnimator("state", 2);
        if (m_LogHumBack)
            Debug.Log($"[HumBack] 开始播放 length={hum.length:F2}s ({diagnostic})");
        m_HumBackPlaybackCoroutine = StartCoroutine(WaitForHumBackPlayback(generation));
    }

    private IEnumerator WaitForHumBackPlayback(int generation)
    {
        yield return null;
        while (generation == m_HumBackGeneration && m_AudioSource != null && m_AudioSource.isPlaying)
            yield return null;
        m_HumBackPlaybackCoroutine = null;
        if (generation == m_HumBackGeneration)
            FinishHumBack(generation, true, "played");
    }


    private void FinishHumBack(int generation, bool completed, string detail)
    {
        if (generation != m_HumBackGeneration) return;
        int streamedPlayedSegments = m_HumStreamPlayedSegments;
        int streamedTotalSegments = m_HumStreamTotalSegments;
        float streamedPlayedSeconds = m_HumStreamPlayedSeconds;
        bool wasStreamed = streamedTotalSegments > 0;
        float playedDuration = wasStreamed
            ? streamedPlayedSeconds
            : (m_ActiveHumBackClip != null ? m_ActiveHumBackClip.length : 0f);
        PracticePitchPlan usedPitchPlan = m_PendingPracticePitchPlan;
        byte[] nonStreamPitchMeasurementWav = null;
        PracticePitchPlanEntry nonStreamPitchEntry = null;
        if (completed && !wasStreamed && m_ActiveHumBackClip != null &&
            usedPitchPlan != null && usedPitchPlan.Entries.Count == 1)
        {
            nonStreamPitchEntry = usedPitchPlan.Entries[0];
            try
            {
                nonStreamPitchMeasurementWav =
                    WavUtility.FromAudioClip(m_ActiveHumBackClip);
            }
            catch (Exception ex)
            {
                if (m_LogHumBack)
                    Debug.LogWarning(
                        "[HumBack/PitchVerify] 完整输出编码失败: " + ex.Message);
            }
        }
        bool wasUserRequested = m_SingingGoal != null
            ? string.Equals(m_SingingGoal.origin, "user_request", StringComparison.Ordinal)
            : !m_AgentCurrentRoundIsTick;
        if (m_HumStreamPlaybackCoroutine != null)
        {
            StopCoroutine(m_HumStreamPlaybackCoroutine);
            m_HumStreamPlaybackCoroutine = null;
        }
        ClearHumStreamClipBuffers(true);
        // Playback completion does not establish who sang the source recording.
        //回哼就是这一轮的回应（快速路径不生成正式回复），播完即算已回应
        ClearUserTurnAwaitingReply(completed ? "回哼播完" : "回哼结束");
        if (m_AudioSource != null && m_AudioSource.clip == m_ActiveHumBackClip)
        {
            m_AudioSource.Stop();
            m_AudioSource.clip = null;
        }
        if (m_ActiveHumBackClip != null)
        {
            Destroy(m_ActiveHumBackClip);
            m_ActiveHumBackClip = null;
        }
        if (m_FastHumBackPlaybackCoroutine != null)
        {
            StopCoroutine(m_FastHumBackPlaybackCoroutine);
            m_FastHumBackPlaybackCoroutine = null;
        }
        if (m_FastHumBackStartCoroutine != null)
        {
            StopCoroutine(m_FastHumBackStartCoroutine);
            m_FastHumBackStartCoroutine = null;
        }
        if (m_FastHumBackFullClip != null)
        {
            Destroy(m_FastHumBackFullClip);
            m_FastHumBackFullClip = null;
        }

        bool needsHistory = m_HumBackNeedsHistoryEntry;
        bool wasPracticeComposition = m_PendingHumIsPracticeComposition;
        RecordPracticePlaybackOutcome(wasPracticeComposition, completed);
        bool wasCatalogSong = m_PendingHumIsCatalogSong;
        bool wasCatalogContinuation = m_PendingHumIsCatalogContinuation;
        string catalogSongName = m_PendingCatalogSongName;
        string renderer = m_PendingHumRenderer ?? "unknown";
        string ignoredExtraHumBackFact = BuildIgnoredExtraHumBackFact(
            m_PendingHumExtraTagsIgnored);
        //必须在下面那批清空之前抓走：工具结果是在本函数**后半段**才拼的，
        //8/21 实测整场四次逐段移调，回报里一个字都没有，就是被这里清掉了。
        int[] usedSegmentShifts = m_PendingHumSegmentShifts;
        List<float> usedSegmentMedians = m_PendingHumSegmentMedians;
        List<int> usedSegmentSources = m_PendingHumSegmentSources;
        bool usedExplicitSegmentKey = m_PendingHumUsesExplicitSegmentKey;
        bool usedMusicalPitchIntent = m_PendingHumUsesMusicalPitchIntent;
        int usedWholeClipShift = m_PendingHumSemitoneOffset;
        var fullyPlayedPitchIds = new HashSet<int>(m_HumStreamPlayedPracticeStableIds);
        CommitPracticePitchPlan(usedPitchPlan, completed, fullyPlayedPitchIds);
        if (nonStreamPitchMeasurementWav != null && nonStreamPitchEntry != null)
            RequestRenderedPitchMeasurement(
                nonStreamPitchMeasurementWav,
                nonStreamPitchEntry.StableId,
                usedPitchPlan.Revision,
                nonStreamPitchEntry.TargetCenterMidi,
                playedDuration);
        string pitchPlanResult = BuildPracticePitchPlanResult(
            usedPitchPlan, completed, fullyPlayedPitchIds);
        m_HumBackNeedsHistoryEntry = false;
        m_HumBackPending = false;
        m_HumBackPreparingCarrier = false;
        m_HumBackPlaying = false;
        m_HumStreamWaitForTextOutputDrain = false;
        m_PendingHumTimeline = null;
        m_PendingHumLanguage = "";
        m_PendingHumReason = "";
        m_PendingHumMode = "echo";
        m_PendingHumLyricsOverride = "";
        m_PendingHumSourceWav = null;
        m_PendingHumSegmentWavs = null;
        m_PendingHumSegmentGaps = null;
        m_PendingHumSegmentMedians = null;
        m_PendingHumSegmentSources = null;
        m_PendingHumSegmentShifts = null;
        m_PendingHumUsesExplicitSegmentKey = false;
        m_PendingHumUsesMusicalPitchIntent = false;
        m_PendingHumExtraTagsIgnored = 0;
        m_PendingPracticePitchPlan = null;
        m_HumStreamPlayedPracticeStableIds.Clear();
        m_PendingHumIsPracticeComposition = false;
        m_PendingHumIsCatalogSong = false;
        m_PendingHumIsCatalogContinuation = false;
        m_PendingCatalogSongName = "";
        m_PendingHumVariationDiagnostic = "";
        m_PendingHumPlayedIndices = null;
        m_PendingHumRenderer = "pending";
        m_FastHumBackEouStaged = false;
        m_FastHumBackActive = false;
        m_FastHumBackFinalDecisionReceived = false;
        m_FastHumBackFinalConfirmed = false;
        m_FastHumBackPrefixPlaybackStarted = false;
        m_FastHumBackPrefixPlaybackDone = false;
        m_FastHumBackFullReady = false;
        m_FastHumBackFullPlaybackStarted = false;
        m_FastHumBackPlaybackComplete = false;
        m_FastHumBackFullSourceWav = null;
        m_FastHumBackFullSourceSeconds = 0f;
        ResetStreamingHumBackPrefix("", false);
        m_HumStreamProducerCoroutine = null;
        m_HumStreamProducerDone = false;
        m_HumStreamFailure = "";
        m_HumStreamTotalSegments = 0;
        m_HumStreamConvertedSegments = 0;
        m_HumStreamPlayedSegments = 0;
        m_HumStreamPlayedSeconds = 0f;
        m_HumStreamUnderrunSeconds = 0f;
        IsAISpeaking = false;
        m_LastVoiceOutputEndedRealtime = Time.realtimeSinceStartup;
        m_TextBack.text = "";
        SetAnimator("state", 0);

        string catalogLabel = string.IsNullOrWhiteSpace(catalogSongName)
            ? "记忆中的歌曲"
            : "记忆中的「" + catalogSongName + "」";
        //唱成功了就记下这一次唱的是什么。两个用途：拦下自说自话的重复，
        //以及给下面那个 ♪（…）占位符加上"第几次"。
        if (completed)
        {
            NoteSungTarget(wasCatalogSong
                ? BuildSungTargetKey("catalog", catalogSongName)
                : BuildSungTargetKey(
                    wasPracticeComposition ? "practice" : "echo", m_LastPracticeOrderUsed));
        }
        //占位符必须能区分"第一次"和"第四次"。原来四次长得一模一样，
        //而她上一句往往是"少し、鼻歌で返事するね"这种将来时——下一帧读起来
        //像是"我说了要唱但还没唱"，于是再唱一次，再看到同样的承诺。
        //8/25 那次循环就是这么转起来的：连着四遍，文本一字不差。
        string repeatSuffix = m_LastSungRepeatCount > 1
            ? $"——同一段已经连着唱了 {m_LastSungRepeatCount} 次，用户中间没有开口"
            : "";
        string partialStreamSuffix = !completed && wasStreamed && streamedPlayedSegments > 0
            ? $"（流式演唱在后续生成失败前已实际播放 {streamedPlayedSegments}/" +
              $"{streamedTotalSegments} 块、约 {streamedPlayedSeconds:F1} 秒）"
            : "";
        string actionText = completed
            ? (wasCatalogSong
                ? (wasCatalogContinuation
                    ? $"♪（接着唱出了{catalogLabel}的后续{repeatSuffix}）"
                    : $"♪（唱出了{catalogLabel}{repeatSuffix}）")
                : (wasPracticeComposition
                    ? $"♪（把刚才练习的几段连续唱了一遍{repeatSuffix}）"
                    : $"♪（轻声回哼了刚才的旋律{repeatSuffix}）"))
            : (wasCatalogSong
                ? $"（尝试演唱{catalogLabel}，但没有完整唱完）{partialStreamSuffix}"
                : (wasPracticeComposition
                    ? "（尝试连续演唱练习片段，但没有完整唱完）" + partialStreamSuffix
                    : "（尝试回哼，但没有完整唱完）" + partialStreamSuffix));
        if (needsHistory && m_ChatHistory != null) m_ChatHistory.Add(actionText);
        if (completed)
        {
            m_PostHumBackFeedbackActive = true;
            m_PostHumBackCompletedRealtime = Time.realtimeSinceStartup;
            string kind = wasCatalogSong
                ? "catalog"
                : wasPracticeComposition ? "practice" : "echo";
            string order = wasPracticeComposition ? m_LastPracticeOrderUsed : "-";
            m_PostHumBackFeedbackSummary =
                $"type={kind}, order={order}, duration={playedDuration:F1}s, " +
                $"requested_by={(wasUserRequested ? "user" : "autonomy")}";
        }
        else
        {
            m_PostHumBackFeedbackActive = false;
            m_PostHumBackFeedbackSummary = "";
        }
        if (completed)
        {
            string rendererFact = renderer == "independent-svs"
                ? " 渲染事实：本次由独立 SVS 根据歌词/旋律重新生成，不是变声。"
                : (renderer == "svc-post-polish"
                    ? " 渲染事实：本次先由独立 SVS 生成，再使用角色 RVC 做了音色转换润色。"
                    : (renderer == "svc-fallback" || renderer == "svc" ||
                       renderer == "svc-stream" || renderer == "svc-per-segment"
                    ? " 渲染事实：本次使用 SVC 音色转换，不得描述成独立歌声生成。"
                    : (renderer == "legacy"
                        ? " 渲染事实：本次使用旧式本地哼声合成。"
                        : "")));
            rendererFact += ignoredExtraHumBackFact;
            RecordHumBackResult(
                wasCatalogSong
                    ? (wasCatalogContinuation
                        ? $"成功：已经从本地歌曲记忆中定位并真实播放了“{catalogSongName}”当前片段之后的已学内容。{rendererFact}"
                        : $"成功：已经从本地歌曲记忆中取出“{catalogSongName}”并用角色声线真实播放完成。" +
                          BuildCatalogSegmentNote() + rendererFact)
                    //把实际用的顺序回报出来。原来只写"按原顺序"，用户连着七轮要求
                    //反过来唱，她四次都读不出这句话的意思是"我无视了你的顺序要求"。
                    : (wasPracticeComposition
                        ? $"成功：practice 已真实播放完成；order=\"{m_LastPracticeOrderUsed}\"。" +
                          BuildPracticeShiftNote(
                              usedExplicitSegmentKey && !usedMusicalPitchIntent
                                  ? usedSegmentShifts
                                  : null,
                              usedSegmentMedians,
                              usedSegmentSources, usedWholeClipShift,
                              usedMusicalPitchIntent) +
                          pitchPlanResult +
                          m_LastPracticeDuplicateNote +
                          rendererFact
                        : "成功：回哼音频已经真实播放完成。现在可以自然评价刚才的回哼，但不要夸大为同步合唱。" +
                          BuildHumBackLeadInNote() + rendererFact),
                false);
            float now = Time.realtimeSinceStartup;
            m_LastAITurnTime = now;
            m_LastAIMsgPlain = actionText;
            m_RecentAIUtterances.Enqueue(new KeyValuePair<float, string>(now, actionText));
            int cap = Mathf.Max(1, m_RecentAIUtterancesShown);
            while (m_RecentAIUtterances.Count > cap) m_RecentAIUtterances.Dequeue();
        }
        else
        {
            RecordHumBackResult(
                streamedPlayedSegments > 0
                    ? $"失败：流式歌唱只实际播放了 {streamedPlayedSegments}/{streamedTotalSegments} 块" +
                      $"（约 {streamedPlayedSeconds:F1} 秒），随后中断。原因：" +
                      TruncateForFrame(detail, 300) +
                      (string.IsNullOrEmpty(pitchPlanResult) ? "" : "。" + pitchPlanResult) +
                      "。必须如实说没有完整唱完；可以谈已经听到的部分，但不得声称整首完成。" +
                      ignoredExtraHumBackFact
                    : "失败：回哼音频没有生成或播放。原因：" +
                      TruncateForFrame(detail, 300) +
                      "。必须如实承认没有唱出来，不得让用户评价不存在的声音。" +
                      ignoredExtraHumBackFact,
                true);
        }
        //The transaction has now produced its final factual result.  Its identity
        //must not leak into a later real user turn.
        m_PendingHumBackKey = "";
        m_PendingHumBackInputRevision = -1;

        if (m_LogHumBack)
            Debug.Log($"[HumBack] {(completed ? "播放完成" : "未能播放")} detail={detail}");
        ReleaseSkillExecutionLease("singing", completed ? "歌唱动作播放完成" : "歌唱动作失败结束");
        OnAgentRoundComplete();
        if (OnAISpeakDone != null) OnAISpeakDone();
        if (m_ChatSettings != null && m_ChatSettings.m_TextToSpeech != null)
            m_ChatSettings.m_TextToSpeech.WarmUp();
    }

    private void CancelPendingHumBack(string reason, bool recordInterrupted)
    {
        bool hadWork = m_SongSingInFlight || HasHumBackExecutionWork();
        if (!hadWork)
        {
            m_PendingHumExtraTagsIgnored = 0;
            m_PendingHumBackKey = "";
            m_PendingHumBackInputRevision = -1;
            ReleaseSkillExecutionLease("singing", reason + "（尚未进入工具执行）");
            return;
        }

        int interruptedStreamPlayed = m_HumStreamPlayedSegments;
        RecordPracticePlaybackOutcome(m_PendingHumIsPracticeComposition, false);
        int interruptedStreamTotal = m_HumStreamTotalSegments;
        float interruptedStreamSeconds = m_HumStreamPlayedSeconds;
        string ignoredExtraHumBackFact = BuildIgnoredExtraHumBackFact(
            m_PendingHumExtraTagsIgnored);
        PracticePitchPlan interruptedPitchPlan = m_PendingPracticePitchPlan;
        var interruptedPitchIds = new HashSet<int>(m_HumStreamPlayedPracticeStableIds);
        CommitPracticePitchPlan(interruptedPitchPlan, false, interruptedPitchIds);
        string interruptedPitchResult = BuildPracticePitchPlanResult(
            interruptedPitchPlan, false, interruptedPitchIds);
        m_SongSingGeneration++;
        m_SongSingInFlight = false;
        m_HumBackGeneration++;
        bool wasNeuralRequest = m_ActiveSVSRequest != null ||
            m_ActiveHumSVCRequest != null;
        if (m_ActiveSVSRequest != null)
        {
            string requestId = m_ActiveSVSRequestId;
            m_ActiveSVSRequest.Abort();
            m_ActiveSVSRequest = null;
            m_ActiveSVSRequestId = "";
            if (!string.IsNullOrEmpty(requestId))
                StartCoroutine(CancelSingingVoiceSynthesis(requestId));
        }
        if (m_ActiveHumSVCRequest != null)
        {
            string requestId = m_ActiveHumSVCRequestId;
            m_ActiveHumSVCRequest.Abort();
            m_ActiveHumSVCRequest = null;
            m_ActiveHumSVCRequestId = "";
            if (!string.IsNullOrEmpty(requestId)) StartCoroutine(CancelNeuralHumSVC(requestId));
        }
        if (m_HumBackPreparingCarrier && !wasNeuralRequest &&
            m_ChatSettings != null && m_ChatSettings.m_TextToSpeech != null)
            m_ChatSettings.m_TextToSpeech.CancelPreparedSpeech();
        if (m_HumBackPlaybackCoroutine != null)
        {
            StopCoroutine(m_HumBackPlaybackCoroutine);
            m_HumBackPlaybackCoroutine = null;
        }
        if (m_HumStreamProducerCoroutine != null)
        {
            StopCoroutine(m_HumStreamProducerCoroutine);
            m_HumStreamProducerCoroutine = null;
        }
        if (m_HumStreamPlaybackCoroutine != null)
        {
            StopCoroutine(m_HumStreamPlaybackCoroutine);
            m_HumStreamPlaybackCoroutine = null;
        }
        ClearHumStreamClipBuffers(true);
        if (m_FastHumBackPlaybackCoroutine != null)
        {
            StopCoroutine(m_FastHumBackPlaybackCoroutine);
            m_FastHumBackPlaybackCoroutine = null;
        }
        if (m_FastHumBackStartCoroutine != null)
        {
            StopCoroutine(m_FastHumBackStartCoroutine);
            m_FastHumBackStartCoroutine = null;
        }
        if (m_AudioSource != null && m_AudioSource.clip == m_ActiveHumBackClip)
        {
            m_AudioSource.Stop();
            m_AudioSource.clip = null;
            m_LastVoiceOutputEndedRealtime = Time.realtimeSinceStartup;
        }
        if (m_ActiveHumBackClip != null)
        {
            Destroy(m_ActiveHumBackClip);
            m_ActiveHumBackClip = null;
        }
        if (m_FastHumBackFullClip != null)
        {
            Destroy(m_FastHumBackFullClip);
            m_FastHumBackFullClip = null;
        }

        if (recordInterrupted && m_HumBackNeedsHistoryEntry && m_ChatHistory != null)
            m_ChatHistory.Add("♪（回哼被打断）");
        if (recordInterrupted)
        {
            RecordHumBackResult(
                interruptedStreamTotal > 0
                    ? $"中断：流式歌唱被用户打断，当时已完整播放 " +
                      $"{interruptedStreamPlayed}/{interruptedStreamTotal} 块、约 " +
                      $"{interruptedStreamSeconds:F1} 秒；后续转换和队列均已取消。" +
                      (string.IsNullOrEmpty(interruptedPitchResult)
                          ? ""
                          : interruptedPitchResult) +
                      "不得声称已经完整唱完。" + ignoredExtraHumBackFact
                    : "中断：回哼开始后被用户打断，没有完整播放。不得声称已经完整唱完。" +
                      ignoredExtraHumBackFact,
                true);
        }
        m_HumBackNeedsHistoryEntry = false;
        m_HumBackPending = false;
        m_HumBackPreparingCarrier = false;
        m_HumBackPlaying = false;
        m_HumStreamWaitForTextOutputDrain = false;
        m_PendingHumTimeline = null;
        m_PendingHumLanguage = "";
        m_PendingHumReason = "";
        m_PendingHumMode = "echo";
        m_PendingHumLyricsOverride = "";
        m_PendingHumSourceWav = null;
        m_PendingHumSegmentWavs = null;
        m_PendingHumSegmentGaps = null;
        m_PendingHumSegmentMedians = null;
        m_PendingHumSegmentSources = null;
        m_PendingHumSegmentShifts = null;
        m_PendingHumUsesExplicitSegmentKey = false;
        m_PendingHumUsesMusicalPitchIntent = false;
        m_PendingHumExtraTagsIgnored = 0;
        m_PendingPracticePitchPlan = null;
        m_HumStreamPlayedPracticeStableIds.Clear();
        m_PendingHumIsPracticeComposition = false;
        m_PendingHumIsCatalogSong = false;
        m_PendingHumIsCatalogContinuation = false;
        m_PendingCatalogSongName = "";
        m_PendingHumVariationDiagnostic = "";
        m_PendingHumRenderer = "pending";
        m_FastHumBackEouStaged = false;
        m_FastHumBackActive = false;
        m_FastHumBackFinalDecisionReceived = false;
        m_FastHumBackFinalConfirmed = false;
        m_FastHumBackPrefixPlaybackStarted = false;
        m_FastHumBackPrefixPlaybackDone = false;
        m_FastHumBackFullReady = false;
        m_FastHumBackFullPlaybackStarted = false;
        m_FastHumBackPlaybackComplete = false;
        m_FastHumBackFullSourceWav = null;
        m_FastHumBackFullSourceSeconds = 0f;
        ResetStreamingHumBackPrefix(reason, true);
        m_HumStreamProducerDone = false;
        m_HumStreamFailure = "";
        m_HumStreamTotalSegments = 0;
        m_HumStreamConvertedSegments = 0;
        m_HumStreamPlayedSegments = 0;
        m_HumStreamPlayedSeconds = 0f;
        m_HumStreamUnderrunSeconds = 0f;
        m_PendingHumBackKey = "";
        m_PendingHumBackInputRevision = -1;
        IsAISpeaking = false;
        ReleaseSkillExecutionLease("singing", reason);
        if (m_LogHumBack) Debug.Log($"[HumBack] 已取消 reason={reason}");
    }

    private IEnumerator CancelNeuralHumSVC(string requestId)
    {
        string cancelURL = (m_HumSVCURL ?? "").TrimEnd('/');
        int slash = cancelURL.LastIndexOf('/');
        if (slash >= 0) cancelURL = cancelURL.Substring(0, slash);
        cancelURL += "/cancel/" + UnityWebRequest.EscapeURL(requestId);
        using (UnityWebRequest request = UnityWebRequest.PostWwwForm(cancelURL, ""))
        {
            request.timeout = 3;
            yield return request.SendWebRequest();
            if (m_LogHumBack && request.result == UnityWebRequest.Result.Success)
                Debug.Log("[HumBack/SVC] 已请求终止后台转换 requestId=" + requestId);
        }
    }

    private IEnumerator CancelSingingVoiceSynthesis(string requestId)
    {
        WWWForm form = new WWWForm();
        form.AddField("request_id", requestId ?? "");
        string cancelURL = GetSVSBaseURL() + "/cancel";
        using (UnityWebRequest request = UnityWebRequest.Post(cancelURL, form))
        {
            request.timeout = 3;
            yield return request.SendWebRequest();
            if (m_LogHumBack && request.result == UnityWebRequest.Result.Success)
                Debug.Log("[HumBack/SVS] 已请求终止后台歌声生成 requestId=" + requestId);
        }
    }

    private static string SanitizeToolFeedbackValue(string value, int maxChars = 280)
    {
        string safe = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        //反馈会作为系统事实重新交给 LLM。即使未来 reason 含有服务端返回文本，也不能
        //让其中看似标签的内容变成下一轮可执行指令。
        safe = safe.Replace('<', '‹').Replace('>', '›');
        if (safe.Length > maxChars) safe = safe.Substring(0, maxChars) + "…";
        return safe;
    }

    private void NoteSingingRoutingFact(string tool, string code, string detail)
    {
        NoteSingingRoutingFact(tool, "accepted_with_routing", code, detail);
    }

    private void NoteSingingRoutingFact(
        string tool,
        string status,
        string code,
        string detail)
    {
        string fact = "\n[歌唱工具路由事实；来自本地程序，仅说明实际发生了什么] " +
            "tool=" + SanitizeToolFeedbackValue(tool, 64) +
            ", status=" + SanitizeToolFeedbackValue(status, 64) + ", code=" +
            SanitizeToolFeedbackValue(code, 96) +
            ", detail=" + SanitizeToolFeedbackValue(detail, 420) +
            " 不要把素材纠正说成用户改变了要求；是否继续表达由你结合当前语境决定。";
        if (string.IsNullOrEmpty(m_PendingSingingRoutingFact))
            m_PendingSingingRoutingFact = fact;
        else
            m_PendingSingingRoutingFact += fact;
    }

    private static string BuildToolCorrectionFrame(
        string tool,
        string code,
        string reason,
        string activeSkills,
        string correction)
    {
        return "\n[工具纠错事实；来自本地程序，优先于聊天记忆和你的猜测]\n" +
            "tool=" + SanitizeToolFeedbackValue(tool, 64) + "\n" +
            "status=rejected\n" +
            "code=" + SanitizeToolFeedbackValue(code, 80) + "\n" +
            "reason=" + SanitizeToolFeedbackValue(reason) + "\n" +
            "round=real_user\n" +
            "loaded_skill_rules=" + SanitizeToolFeedbackValue(activeSkills, 160) + "\n" +
            "correction=" + SanitizeToolFeedbackValue(correction, 420) + "\n" +
            "请根据这些事实用你自己的自然语气重新决定：" +
            "可以选正确工具、自然询问或如实说明，程序不替你决定；" +
            "不要复述字段，不要编造权限或成功状态。" +
            "若文字声称马上执行，同轮必须提交可验证的工具请求；" +
            "选择不执行就不要先口头承诺。" +
            "参数修正可以用 silent 在内部进行；没有可执行进展时不要再次外放稍等或开始。" +
            "选择停止时可以一次说明真实原因，不能把未执行说成服务坏了。]";
    }

    private string DescribeActiveSkillsForToolFeedback()
    {
        if (m_ActiveSkillsThisRound.Count == 0) return "none";
        var sb = new System.Text.StringBuilder();
        foreach (string skill in m_ActiveSkillsThisRound)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(skill);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 把同步校验失败作为事实交回 LLM。只有真实用户正在等待时才立即纠错；自主 tick
    /// 仍用粘性失败在自然下一帧反思，避免后台小故障突然打扰用户。
    /// </summary>
    private void ReportToolFailureForLlm(
        string tool,
        string code,
        string reason,
        string correction)
    {
        // Error categories and legacy execution names are not model-facing tools.
        // Offer the canonical interface even when an old response hit an adapter.
        if (tool == "hum_back")
        {
            tool = "sing";
            correction = "按 reason 中的真实失败原因重新决定。演唱只用 <sing refs=\"清单中的clip引用\"/>，" +
                "音乐目标用 pitch_plan 或 transpose；来源/边界状态可询问或显式确认/选择范围。" +
                "不填写 source/mode/order，不改用其它录音冒充原素材。";
        }
        else if (tool == "practice_confirm") tool = "clip_confirm";
        if (tool == "sing" || tool == "clip_confirm" || tool == "clip_revise" || tool == "clip_drop")
            m_LastSingingRepairAt = Time.realtimeSinceStartup;
        string safeReason = SanitizeToolFeedbackValue(reason);
        NoteToolFailure($"{tool} 未执行：{safeReason}");
        if (m_WorkNoProgress) return;
        bool realUserWaiting = m_AgentRunning && !m_AgentCurrentRoundIsTick &&
            m_AgentRoundInFlight && m_UserTurnAwaitingReplySince > 0f;
        if (!realUserWaiting) return;

        string frame = BuildToolCorrectionFrame(
            tool, code, safeReason, DescribeActiveSkillsForToolFeedback(), correction);
        if (m_ToolCorrectionContinuationPending)
        {
            //同一份坏回复可能同时带错两个标签；它们属于同一次纠错，不消耗第二次机会。
            m_PendingToolCorrectionFrame += frame;
            return;
        }
        if (m_ToolCorrectionAttemptsThisUserTurn >= k_MaxToolCorrectionAttemptsPerUserTurn)
        {
            m_ToolCorrectionExhaustedThisRound = true;
            m_PendingNonCharacterToolNotice =
                "系统：角色连续两次未能完成本轮工具调用，请重新询问或稍后再试。";
            if (m_LogAgentLoop)
                Debug.LogWarning($"[ToolCorrection] 已达单轮纠错上限 tool={tool} code={code}");
            return;
        }

        m_ToolCorrectionAttemptsThisUserTurn++;
        m_PendingToolCorrectionFrame = frame;
        m_ToolCorrectionContinuationPending = true;
        if (m_LogAgentLoop)
            Debug.LogWarning($"[ToolCorrection] 将真实失败交回 LLM " +
                             $"tool={tool} code={code} attempt={m_ToolCorrectionAttemptsThisUserTurn}");
    }

    /// <summary>
    /// 最终安全网只显示为系统状态，不走角色 TTS，也不假装某个尚未调用的具体工具失败。
    /// </summary>
    private bool PublishPendingNonCharacterToolNotice()
    {
        if (string.IsNullOrWhiteSpace(m_PendingNonCharacterToolNotice)) return false;
        string notice = m_PendingNonCharacterToolNotice.Trim();
        m_PendingNonCharacterToolNotice = "";
        HandleSystemNotice(new SystemNotice(
            "tool_correction_exhausted",
            SystemNoticeSeverity.Error,
            notice,
            "The single-turn LLM tool-correction budget was exhausted.",
            "ToolCorrection",
            true));
        if (m_LogAgentLoop) Debug.LogWarning("[ToolCorrection/Fallback] " + notice);
        return true;
    }

    /// <summary>
    /// 记下一次没成功的工具调用。同一条重复出现时只累加次数，不刷屏。
    /// </summary>
    private void NoteToolFailure(string text)
    {
        string trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return;
        if (string.Equals(trimmed, m_StickyToolFailure, StringComparison.Ordinal))
            m_StickyToolFailureCount++;
        else
        {
            m_StickyToolFailure = trimmed;
            m_StickyToolFailureCount = 1;
        }
        //重新开始计展示次数：又失败了一次，就该再被看见几帧。
        m_StickyToolFailureShown = 0;
    }

    /// <summary>
    /// 有一次真的成功了就把粘住的失败清掉——她已经找到能走的路了。
    /// </summary>
    private void ClearToolFailure()
    {
        m_StickyToolFailure = "";
        m_StickyToolFailureCount = 0;
        m_StickyToolFailureShown = 0;
    }

    private void RecordHumBackResult(
        string result,
        bool warning,
        bool attributeWarningToPendingTransaction = true)
    {
        m_LastHumBackResult = result ?? "";
        m_HumBackResultPending = !string.IsNullOrWhiteSpace(m_LastHumBackResult);
        //warning 就是"这次没成"的现成信号，不需要再解析文本。
        if (warning)
        {
            NoteToolFailure(m_LastHumBackResult);
            if (attributeWarningToPendingTransaction &&
                !string.IsNullOrEmpty(m_PendingHumBackKey))
            {
                if (m_PendingHumBackKey == m_LastFailedHumBackKey) m_LastFailedHumBackCount++;
                else
                {
                    m_LastFailedHumBackKey = m_PendingHumBackKey;
                    m_LastFailedHumBackCount = 1;
                }
                //记完就清。这个字段的意思是"当前在飞的那一次 hum_back"，不是"最后见过的
                //参数"。不清的后果有两个，都是真的：① song_sing 失败也走这条记录路径，
                //而它压根没有 hum_back key，于是把上一次 hum_back 的计数顶上去——两次
                //查不到曲库，就能把一个还没试过的 hum_back 直接判成"连续失败 2 次"而拦掉；
                //② 拦截本身也记一次失败，计数于是自己往上滚，"已经连续失败 N 次"越报越大。
                m_PendingHumBackKey = "";
                m_PendingHumBackInputRevision = -1;
            }
        }
        else if (m_HumBackResultPending)
        {
            ClearToolFailure();
            m_LastFailedHumBackKey = "";
            m_LastFailedHumBackCount = 0;
        }
        if (!m_LogHumBack || !m_HumBackResultPending) return;
        if (warning) Debug.LogWarning("[HumBack/Result] " + m_LastHumBackResult);
        else Debug.Log("[HumBack/Result] " + m_LastHumBackResult);
    }

    //一条正则覆盖全部可执行标签(+noop):属性用 [^>]* 而不是精确引号匹配——
    //本地模型偶尔输出全角引号(＂/“)甚至漏掉自闭合斜杠,这里都要兜住,
    //否则漏网的标签会被 TTS 念出来、显示在字幕上。
    //朗读过滤是**黑名单式的**：凡是长得像工具标签的，一律不进 TTS——不管我们
    //认不认识它。以前是白名单，只认已知的 17 个名字，两次栽在同一个地方：
    //  · <note/> 漏在清单外，被朗读了 3.86 秒(它其实已被正确解析并落库)
    //  · 8/12 模型**自己发明**了 <singing_result>，工程里从不产生这个标签，
    //    白名单当然认不出来，于是歌词加音符名被念了 6.94s，闭合标签又念了 1.18s
    //白名单只能挡住"我们记得加进去的"，挡不住模型编的下一个。
    //她说的是中日文，正文里出现「<英文标识符」这种结构基本不可能；漏一个的代价是
    //念几秒音符名，误剥一个的代价几乎为零——这个不对称决定了应该反过来做。
    //代价：真要讨论 HTML/代码时 <div> 这类会被吞掉。语音陪伴场景可以接受。
    //
    //注意：工具解析(记忆、歌曲标签)跑在这一步**之前**、用的是原始文本，
    //所以这里怎么剥都不会丢掉工具调用。
    private static readonly System.Text.RegularExpressions.Regex s_AllAgentTagsRegex =
        new System.Text.RegularExpressions.Regex(
            @"</?\s*[A-Za-z_][A-Za-z0-9_.:-]*\b[^>]*>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    //非自闭合的开标签 = 容器起点。我们定义的工具标签**全是自闭合的**，所以
    //<X …> 这种一定是模型自己造的结构，后面裹着的是它回填的数据(歌词、音符名)。
    //只剥标签会把数据原样留下来接着念，必须从这里整段截断。
    private static readonly System.Text.RegularExpressions.Regex s_ContainerOpenRegex =
        new System.Text.RegularExpressions.Regex(
            @"<\s*[A-Za-z_][A-Za-z0-9_.:-]*\b[^>/]*>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// 返回"可能是工具标签"的起点：<c>&lt;</c> 或 <c>&lt;/</c> 后面跟一个 ASCII 标识符首字符。
    /// 流式切句器靠它把尚未闭合的标签连同后缀一起扣在 buffer 里，避免属性里的
    /// 逗号/句号/换行被当成正文边界，把半截标签推进 TTS。
    /// 只有 <c>&lt;</c> 结尾时也返回——那时还看不出是不是标签，先按标签压住。
    /// </summary>
    private static int FindPotentialAgentTagStart(string text)
    {
        if (string.IsNullOrEmpty(text)) return -1;
        int searchFrom = 0;
        while (searchFrom < text.Length)
        {
            int start = text.IndexOf('<', searchFrom);
            if (start < 0) return -1;
            int i = start + 1;
            if (i < text.Length && text[i] == '/') i++;
            if (i >= text.Length) return start;          //只有 "<" 或 "</"，先压住
            char c = text[i];
            if (c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                return start;
            searchFrom = start + 1;                       //"3<5"、"<、" 这类不是标签
        }
        return -1;
    }

    private string StripAgentTagsForTTS(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        //① 容器起点之后全是模型自己回填的数据，整段截掉(成对与被截断的都覆盖)
        var container = s_ContainerOpenRegex.Match(text);
        string clean = container.Success ? text.Substring(0, container.Index) : text;
        //② 剩下的自闭合/闭合标签逐个剥掉
        clean = s_AllAgentTagsRegex.Replace(clean, "");
        //③ 结尾若挂着畸形/未闭合的标签，也宁可丢掉该后缀，绝不朗读
        int pendingTagStart = FindPotentialAgentTagStart(clean);
        if (pendingTagStart >= 0) clean = clean.Substring(0, pendingTagStart);
        return clean.Trim();
    }

    /// <summary>
    /// 解析 LLM 流式回复尾部的 agent 标签：
    ///   &lt;next in="Ns" focus="..."/&gt; — 让 LLM 自己排下次 tick
    ///   &lt;continue/&gt;                 — 立刻链下一帧(连续说话)
    ///   &lt;silent/&gt;                   — 本帧不发声(纯 tick 状态延续)
    /// 标签必须在文本末尾(prompt 已规定)。
    /// 输入 raw，输出去掉标签的 cleanText 和各字段。
    /// </summary>
    private void ParseAgentTags(string raw,
        out string cleanText,
        out float? nextInSec, out string focus,
        out bool wantsContinue, out bool wantsSilent,
        out bool? wantsLook)
    {
        cleanText = raw ?? "";
        nextInSec = null;
        focus = null;
        wantsContinue = false;
        wantsSilent = false;
        wantsLook = null;
        if (string.IsNullOrEmpty(raw)) return;

        //<next /> ：属性顺序任意，in/focus 都可选
        var nextRegex = new System.Text.RegularExpressions.Regex(
            @"<next(?:\s+(?:in=""(?<in>[^""]+)""|focus=""(?<focus>[^""]+)""))*\s*/>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var contRegex = new System.Text.RegularExpressions.Regex(@"<continue\s*/>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        //调度语义只认“尾部标签区”里最后一个 next/continue。标签后面若还有
        //普通正文，它就只是模型放错位置的分隔符，不得启动新 round。
        //8/27 实测模型在一篇已经讲完的故事的开头和每段之间都写了
        //<continue/>，尾部又写 <next/>。旧解析“全文任意一个 continue 都算”，
        //于是故事完整播完后还是续了 8 轮。
        System.Text.RegularExpressions.Match schedulingMatch = null;
        bool schedulingIsContinue = false;
        var nextMatches = nextRegex.Matches(cleanText);
        for (int i = 0; i < nextMatches.Count; i++)
        {
            var candidate = nextMatches[i];
            if (!IsSchedulingTagInTrailingBlock(cleanText, candidate)) continue;
            if (schedulingMatch == null || candidate.Index > schedulingMatch.Index)
            {
                schedulingMatch = candidate;
                schedulingIsContinue = false;
            }
        }
        var continueMatches = contRegex.Matches(cleanText);
        for (int i = 0; i < continueMatches.Count; i++)
        {
            var candidate = continueMatches[i];
            if (!IsSchedulingTagInTrailingBlock(cleanText, candidate)) continue;
            if (schedulingMatch == null || candidate.Index > schedulingMatch.Index)
            {
                schedulingMatch = candidate;
                schedulingIsContinue = true;
            }
        }

        if (schedulingMatch != null && schedulingIsContinue)
        {
            wantsContinue = true;
        }
        else if (schedulingMatch != null)
        {
            var nm = schedulingMatch;
            if (nm.Groups["in"].Success)
            {
                string raw_in = nm.Groups["in"].Value.Trim().ToLowerInvariant();
                //支持 "20s"/"20"/"1m"/"30sec" 简单变种
                raw_in = raw_in.Replace("sec", "").Replace("seconds", "");
                if (raw_in.EndsWith("m") && !raw_in.EndsWith("mm"))
                {
                    string num = raw_in.Substring(0, raw_in.Length - 1);
                    float fm;
                    if (float.TryParse(num, out fm)) nextInSec = fm * 60f;
                }
                else
                {
                    raw_in = raw_in.TrimEnd('s');
                    float fs;
                    if (float.TryParse(raw_in, out fs)) nextInSec = fs;
                }
            }
            if (nm.Groups["focus"].Success) focus = nm.Groups["focus"].Value.Trim();
        }

        int schedulingTagCount = nextMatches.Count + continueMatches.Count;
        if (schedulingTagCount > 0 &&
            (schedulingMatch == null || schedulingTagCount > 1) && m_LogAgentLoop)
        {
            string selected = schedulingMatch == null
                ? "无（全部位于正文内）"
                : (schedulingIsContinue ? "<continue/>" : "<next/>");
            Debug.LogWarning($"[Agent/Tag] 调度标签 {schedulingTagCount} 个，" +
                             $"仅采用尾部最后一个：{selected}");
        }

        //调度上只采用上面选中的一个，但所有同名标签都要从正文剥掉，
        //否则放错位置的标签会被 TTS 念出来。
        cleanText = nextRegex.Replace(cleanText, "").Trim();
        cleanText = contRegex.Replace(cleanText, "").Trim();

        var silRegex = new System.Text.RegularExpressions.Regex(@"<silent\s*/>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (silRegex.IsMatch(cleanText))
        {
            wantsSilent = true;
            cleanText = silRegex.Replace(cleanText, "").Trim();
        }

        //<look/> 睁眼 / <unlook/> 闭眼 — 视觉感知通道开关。两个都出现时 unlook 优先(更保守)。
        var lookRegex = new System.Text.RegularExpressions.Regex(@"<look\s*/>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var unlookRegex = new System.Text.RegularExpressions.Regex(@"<unlook\s*/>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        bool hasLook = lookRegex.IsMatch(cleanText);
        bool hasUnlook = unlookRegex.IsMatch(cleanText);
        if (hasLook) cleanText = lookRegex.Replace(cleanText, "").Trim();
        if (hasUnlook) cleanText = unlookRegex.Replace(cleanText, "").Trim();
        if (hasUnlook) wantsLook = false;       //unlook 优先
        else if (hasLook) wantsLook = true;
    }

    private static bool IsSchedulingTagInTrailingBlock(
        string text, System.Text.RegularExpressions.Match schedulingTag)
    {
        if (string.IsNullOrEmpty(text) || schedulingTag == null || !schedulingTag.Success)
            return false;
        string after = text.Substring(schedulingTag.Index + schedulingTag.Length);
        //调度标签之后可以还有 look/memory/tool 等自闭合标签，但不能再有正文。
        return string.IsNullOrWhiteSpace(s_AllAgentTagsRegex.Replace(after, ""));
    }

#endregion

#region 文字逐个显示
    //逐字显示的时间间隔
    [SerializeField] private float m_WordWaitTime = 0.2f;
    //是否显示完成
    [SerializeField] private bool m_WriteState = false;

    /// <summary>
    /// 开始逐个打印
    /// </summary>
    /// <param name="_msg"></param>
    private void StartTypeWords(string _msg)
    {
        if (_msg == "")
            return;

        m_WriteState = true;
        StartCoroutine(SetTextPerWord(_msg, m_FormalResponseGeneration));
    }

    private IEnumerator SetTextPerWord(string _msg, int responseGeneration)
    {
        int currentPos = 0;
        while (m_WriteState)
        {
            if (responseGeneration != m_FormalResponseGeneration) yield break;
            yield return new WaitForSeconds(m_WordWaitTime);
            if (responseGeneration != m_FormalResponseGeneration) yield break;
            currentPos++;
            //更新显示的内容
            m_TextBack.text = _msg.Substring(0, currentPos);
            if (m_SubtitleOverlay != null)
                m_SubtitleOverlay.ReportSourceCharacterRevealed(
                    responseGeneration, _msg[currentPos - 1]);

            m_WriteState = currentPos < _msg.Length;

        }

        //切换到等待动作
        SetAnimator("state",0);
    }

#endregion

#region 聊天记录
    //保存聊天记录
    [SerializeField] private List<string> m_ChatHistory;
    //缓存已创建的聊天气泡
    [SerializeField] private List<GameObject> m_TempChatBox;
    //聊天记录显示层
    [SerializeField] private GameObject m_HistoryPanel;
    //聊天文本放置的层
    [SerializeField] private RectTransform m_rootTrans;
    //发送聊天气泡
    [SerializeField] private ChatPrefab m_PostChatPrefab;
    //回复的聊天气泡
    [SerializeField] private ChatPrefab m_RobotChatPrefab;
    //滚动条
    [SerializeField] private ScrollRect m_ScroTectObject;
    //获取聊天记录
    public void OpenAndGetHistory()
    {
        m_ChatPanel.SetActive(false);
        m_HistoryPanel.SetActive(true);

        ClearChatBox();
        StartCoroutine(GetHistoryChatInfo());
    }
    //返回
    public void BackChatMode()
    {
        m_ChatPanel.SetActive(true);
        m_HistoryPanel.SetActive(false);
    }

    //清空已创建的对话框
    private void ClearChatBox()
    {
        while (m_TempChatBox.Count != 0)
        {
            if (m_TempChatBox[0])
            {
                Destroy(m_TempChatBox[0].gameObject);
                m_TempChatBox.RemoveAt(0);
            }
        }
        m_TempChatBox.Clear();
    }

    //获取聊天记录列表
    private IEnumerator GetHistoryChatInfo()
    {

        yield return new WaitForEndOfFrame();

        for (int i = 0; i < m_ChatHistory.Count; i++)
        {
            if (i % 2 == 0)
            {
                ChatPrefab _sendChat = Instantiate(m_PostChatPrefab, m_rootTrans.transform);
                _sendChat.SetText(m_ChatHistory[i]);
                m_TempChatBox.Add(_sendChat.gameObject);
                continue;
            }

            ChatPrefab _reChat = Instantiate(m_RobotChatPrefab, m_rootTrans.transform);
            _reChat.SetText(m_ChatHistory[i]);
            m_TempChatBox.Add(_reChat.gameObject);
        }

        //重新计算容器尺寸
        LayoutRebuilder.ForceRebuildLayoutImmediate(m_rootTrans);
        StartCoroutine(TurnToLastLine());
    }

    private IEnumerator TurnToLastLine()
    {
        yield return new WaitForEndOfFrame();
        //滚动到最近的消息
        m_ScroTectObject.verticalNormalizedPosition = 0;
    }


#endregion

    private void SetAnimator(string _para,int _value)
    {
        if (m_Animator == null)
            return;

        m_Animator.SetInteger(_para, _value);
    }
}
