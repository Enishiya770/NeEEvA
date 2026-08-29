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

public class ChatSample : MonoBehaviour
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
    /// <summary>
    /// 带文字发送
    /// </summary>
    /// <param name="_postWord"></param>
    public void SendData(string _postWord)
    {
        SendDataInternal(_postWord, null);
    }

    private void SendDataInternal(string _postWord, string speculativeHint)
    {
        if (_postWord.Equals(""))
            return;

        if (m_CreateVoiceMode)//合成输入为语音
        {
            CallBack(_postWord);
            m_InputWord.text = "";
            return;
        }

        //添加记录聊天 — 历史气泡里只显示用户原话(感知帧只去 LLM context，不进 UI)
        m_ChatHistory.Add(_postWord);

        //歌曲工具不能依赖 Agent Loop 才知道“用户刚说了什么”。直接对话模式也维护
        //当前用户轮次，供明确保存请求兜底、歌名提取和成功确认使用。
        if (!m_AgentRunning)
        {
            m_LastUserTurnTime = Time.realtimeSinceStartup;
            m_LastUserMsg = _postWord ?? "";
            m_ExplicitSongRememberHandled = false;
            m_ExplicitHumBackHandled = false;
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

        //agent loop 启用时：把感知帧拼到用户文本前面，让 LLM 也能"感受"时间
        //(返回的字符串才是真正喂给 LLM 的——含 [感知帧 ...] + 用户原话)
        PrepareActiveSkillsForRound(_postWord, "user-spoke");
        string llmInput = (m_AgentRunning) ? PrepareUserTurn(_postWord) : _postWord;
        if (!string.IsNullOrWhiteSpace(speculativeHint))
        {
            //临时草稿不进入任何历史；只在最终转写与 partial 足够一致时，作为本轮一次性提示。
            llmInput = speculativeHint + "\n" + llmInput;
        }

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
            bool waitForSongMemoryResult = ShouldHoldSpeechForExplicitSongRemember();
            bool waitForRealHumBack = ShouldHoldSpeechForExplicitHumBack();
            StartStreaming(
                llmInput,
                !singingTurn && string.IsNullOrWhiteSpace(speculativeHint),
                _postWord,
                waitForSongMemoryResult,
                waitForRealHumBack);
        }
        else
        {
            m_HoldSpeechForSongMemoryResult = ShouldHoldSpeechForExplicitSongRemember();
            m_HoldSpeechForHumBackResult = ShouldHoldSpeechForExplicitHumBack();
            PublishSpokenPrefixToLlm();
            m_ChatSettings.m_ChatModel.PostMsg(llmInput, CallBack);
        }
    }

    /// <summary>
    /// AI回复的信息的回调
    /// </summary>
    /// <param name="_response"></param>
    private void CallBack(string _response)
    {
        _response = (_response ?? "").Trim();
        //非流式路径同样处理标签:记忆标签提取应用,其余标签剥净——
        //system prompt 无条件教标签,任何模式下模型都可能输出,漏剥会被念出来
        string afterMem;
        var memOps = MemoryTagParser.Extract(_response, out afterMem);
        if (memOps != null && m_MemoryHub != null) m_MemoryHub.ApplyMemoryOps(memOps);
        AgentSkillRequest ignoredSkillRequest = ExtractSkillRequestTag(ref afterMem);
        if (ignoredSkillRequest != null && m_LogAgentLoop)
            Debug.LogWarning("[LLM技能] 非流式回复中的自主 Skill 申请已忽略；该握手只在 Agent 流式链中执行");
        AgentSkillControlRequest skillControl = ExtractSkillControlTag(ref afterMem);
        AgentSingingIntentRequest singingIntent = ExtractSingingIntentTag(ref afterMem);
        bool selfRepeat = IsVerbatimSelfRepeat(
            StripAgentTagsForTTS(afterMem), out string selfRepeatReason);
        bool singingIntentAccepted = false;
        string singingIntentResult = "";
        if (!selfRepeat && singingIntent != null)
        {
            singingIntentAccepted = TryApplySingingIntent(
                singingIntent, out singingIntentResult);
            if (!singingIntentAccepted)
                NoteToolFailure("未执行 singing 结构化意图：" + singingIntentResult);
        }
        if (!selfRepeat && skillControl != null &&
            !TryApplySkillControl(skillControl, out string skillControlResult))
        {
            NoteToolFailure("未执行 Skill 权限变更：" + skillControlResult);
        }
        bool hadSingingAction = s_PracticeDropTagRegex.IsMatch(afterMem) ||
            s_SongMemoryTagRegex.IsMatch(afterMem) || s_SongSearchTagRegex.IsMatch(afterMem) ||
            s_SongSingTagRegex.IsMatch(afterMem) || s_HumBackTagRegex.IsMatch(afterMem) ||
            (singingIntent != null && singingIntent.Action != "none");
        bool singingActionAllowed = CanExecuteSkillAction("singing", out string singingBlockReason);
        if (singingIntent != null && !singingIntentAccepted)
        {
            singingActionAllowed = false;
            singingBlockReason = "singing_intent 校验失败：" + singingIntentResult;
        }
        AgentSongMemoryRequest songMemory = ExtractSongMemoryTag(ref afterMem);
        AgentSongSearchRequest songSearch = ExtractSongSearchTag(ref afterMem);
        AgentSongSingRequest songSing = ExtractSongSingTag(ref afterMem);
        ExtractAndApplyPracticeDropTag(ref afterMem, !selfRepeat && singingActionAllowed);
        AgentHumBackRequest humBack = ExtractHumBackTag(ref afterMem);
        if (!selfRepeat)
            ApplySingingIntentAction(
                singingIntent, singingIntentAccepted, ref songSing, ref humBack);
        if (!selfRepeat && singingActionAllowed && !m_AgentCurrentRoundIsTick &&
            !ValidateUserSingingActionAuthorization(
                singingIntentAccepted && singingIntent != null ? singingIntent.Actor : "",
                singingIntentAccepted && singingIntent != null ? singingIntent.Action : "",
                songSing != null,
                humBack != null ? humBack.Mode : "",
                out string userSingingAuthorizationFailure))
        {
            singingActionAllowed = false;
            singingBlockReason = userSingingAuthorizationFailure;
        }
        string speakerName = ExtractSpeakerNameTag(ref afterMem);
        AgentSpeakerManageRequest speakerManage = ExtractSpeakerManageTag(ref afterMem);
        if (selfRepeat)
        {
            songMemory = null; songSearch = null; songSing = null; humBack = null;
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
        if (TryRerouteSongSingToPractice(ref songSing, ref humBack) && m_LogHumBack)
            Debug.LogWarning("[SongSing→HumBack] 曲库标签指向本轮练唱片段，已保留顺序并改走 practice");
        if (ShouldDiscardSongSingToolForCurrentTurn(songSing))
        {
            if (m_LogHumBack)
                Debug.LogWarning("[SongSing] 模型把跟唱约定、当前演唱或失败陈述误写成曲库演唱；已丢弃该标签并重新分流");
            songSing = null;
        }
        if (humBack != null) m_ExplicitHumBackHandled = true;
        if (songMemory != null)
        {
            if (songMemory.Action == "remember" && string.IsNullOrWhiteSpace(songMemory.Title))
                songMemory.Title = ExtractExplicitSongTitle(m_LastUserMsg);
            m_ExplicitSongRememberHandled = true;
        }
        if (songMemory == null && !selfRepeat && singingActionAllowed &&
            ShouldFallbackToExplicitSongRemember())
        {
            songMemory = new AgentSongMemoryRequest
            {
                Action = "remember",
                Title = ExtractExplicitSongTitle(m_LastUserMsg),
                Reason = "用户明确要求记住最近歌声，但模型漏掉了 song_remember 标签",
            };
            m_ExplicitSongRememberHandled = true;
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
        if (speakerManage != null) BeginSpeakerManage(speakerManage);
        //曾经在这里做「模型漏调 <song_sing/> 就按正则兜底」。已删除：8/9 实测触发 5 次、
        //成功 0 次，而正则从普通说话里编出来的"歌名"是「了呀」(出自"我刚才已经唱了呀")、
        //「点歌」、「完再唱」、以及整句歌词。判据 IsPlausibleUnquotedSongTitle 是一份
        //黑名单，不在名单里的一律放行，注定漏。
        //更要命的是它会连带扣住她那一轮的正常回复(已扣留 7 次)，查不到歌之后整轮无声。
        //而那几轮模型自己**没有**调用 <song_sing/>——它判断"这不是点歌请求"，判断是对的，
        //是正则在第二次猜并且猜错。要不要从曲库唱，交给她自己决定。
        if (songSing != null)
        {
            // 持久曲库与“刚才一句”是不同音源；同一轮只执行一种真实歌唱动作。
            humBack = null;
            BeginSongSing(songSing);
        }
        if (songSing == null && humBack == null && !selfRepeat && singingActionAllowed &&
            singingIntentAccepted && singingIntent != null &&
            singingIntent.Actor == "character" && singingIntent.Action == "echo" &&
            ShouldFallbackToExplicitHumBack())
        {
            humBack = new AgentHumBackRequest
            {
                Mode = "echo",
                Reason = "用户明确要求回哼最近旋律，但模型漏掉了 hum_back 标签",
            };
            m_ExplicitHumBackHandled = true;
            if (m_LogHumBack)
                Debug.LogWarning("[HumBack] 检测到明确回哼请求，模型未调用 <hum_back/>，执行安全兜底");
        }
        if (humBack != null) QueueHumBack(humBack);
        m_HoldSpeechForSongMemoryResult = false;
        m_HoldSpeechForHumBackResult = false;
        _response = StripAgentTagsForTTS(afterMem);
        m_TextBack.text = "";

        if (heldForSongMemory || heldForHumBack)
        {
            if (m_LogAgentLoop)
                Debug.Log(heldForSongMemory
                    ? "[SongMemory] 已扣留落盘前非流式回复，等待本机曲库结果后再确认"
                    : "[HumBack] 已扣留普通TTS歌词/舞台说明，只允许真实回哼音频发声");
            return;
        }

        
        Debug.Log("收到AI回复："+ _response);

        //记录聊天
        m_ChatHistory.Add(_response);

        if (!m_IsVoiceMode||m_ChatSettings.m_TextToSpeech == null)
        {
            //开始逐个显示返回的文本
            StartTypeWords(_response);
            return;
        }

        //切句分段合成+队列播放，降低首音延迟
        StartCoroutine(SpeakInChunks(_response));
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
    private int m_StreamingSingingLowFrames = 0;
    private bool m_StreamingSingingExitDetected = false;
    private string m_StreamingSingingEvidence = "";
    private float m_LastSingingSpeculativeRequestTime = -999f;
    private bool m_EouCognitiveSpeechVeto = false;
    [Tooltip("拿最终转写问一次 LLM「这是唱还是说」，判完再决定要不要回哼。" +
             "实测不带上下文的轻量请求 0.41s，十个历史误判样本全对；关掉则完全依赖声学" +
             "与流式判定，而流式只有约 25% 的轮次来得及回包。")]
    [SerializeField] private bool m_EnableFinalModeCheck = true;
    //本轮的最终模态结论："singing" / "speech" / "-"(判不出) / ""(还没问)。
    //非空即表示已经问过，用来防止回调重入时再问一次。
    private string m_FinalModeVerdict = "";
    //声学很确定在唱、而文字说不是时的软降级：仍按歌唱轮呈现给 LLM，但不自动回哼、
    //不写进练唱会话。文字判错时最坏只是"她没主动唱回来"，而不是整段演唱被丢弃。
    private bool m_FinalModeSoftDowngrade = false;
    //软降级这一轮要不要让她出声追问「刚才那段是在唱吗」。只在本轮内有效，
    //构造完 _msg 就清掉，不跨轮。
    private bool m_PendingSingingConfirmation = false;
    //刚靠"唱出去且用户没异议"清掉待确认标的段号，回报给她一次。
    private List<int> m_JustConfirmedPhraseIndices = null;
    private bool m_EouCognitiveSingingSupport = false;
    private int m_SingingBridgeGeneration = 0;
    private bool m_SingingBridgeTtsInFlight = false;
    private AudioClip m_PreparedSingingBridgeClip;
    private AudioClip m_DeferredPreparedClipToDestroy;
    private string m_PreparedSingingBridgeText = "";
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
        bool expectObservedSinging = HasActiveSingAlongRequest() &&
            !m_EouCognitiveSpeechVeto &&
            (m_StreamingTurnIsSinging || m_EouCognitiveSingingSupport);

        // The complete clip is available at EOU before final ASR starts.  If its
        // beginning was already voice-converted while the user was singing, stage
        // the complete continuation now so SVC and final recognition run in parallel.
        TryStageFastHumBackAtEou(_audioClip);

        m_FinalAsrRequestsInFlight++;
        bool streamingExitAtSubmission = m_StreamingSingingExitDetected;
        bool completed = false;
        Action<string> onFinalAsr = text =>
        {
            if (completed) return;
            completed = true;
            m_FinalAsrRequestsInFlight = Mathf.Max(0, m_FinalAsrRequestsInFlight - 1);
            DealingTextCallback(text, streamingExitAtSubmission);
        };
        SenseVoiceSpeechToText senseVoice = m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText;
        if (senseVoice != null)
            senseVoice.SpeechToText(
                _audioClip,
                onFinalAsr,
                allowSpeakerLearning,
                expectObservedSinging,
                GetStreamingSingingOnsetSeconds(),
                GetStreamingObservedSeconds(),
                streamingExitAtSubmission);
        else
            m_ChatSettings.m_SpeechToText.SpeechToText(_audioClip, onFinalAsr);
    }

    /// <summary>
    /// Tentative-EOU路径专用：把clip送ASR做"预测识别"，但不进LLM链路——
    /// callback里RTSpeechHandler会看尾部是否说完，再决定走AcceptText还是丢弃。
    /// 不调用DealingTextCallback——避免预测命中前就提前刷UI/SendData。
    /// </summary>
    public void PreviewASR(
        AudioClip _audioClip,
        System.Action<string> _callback,
        bool allowSpeakerLearning = true)
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
            bool expectObservedSinging = HasActiveSingAlongRequest() &&
                !cognitiveSpeechVeto &&
                (m_StreamingTurnIsSinging || HasStrongSpeculativeSingingSupport());
            senseVoice.SpeechToText(
                _audioClip,
                _callback,
                allowSpeakerLearning,
                expectObservedSinging,
                GetStreamingSingingOnsetSeconds(),
                GetStreamingObservedSeconds(),
                m_StreamingSingingExitDetected);
        }
        else
            m_ChatSettings.m_SpeechToText.SpeechToText(_audioClip, _callback);
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
            bool expectedMelodicFrame = HasActiveSingAlongRequest() &&
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
        bool expectedStableSingingEvidence = HasActiveSingAlongRequest() &&
            m_StreamingSingingConsecutiveFrames >= 3 &&
            transcript.SingingProbability >= 0.32f &&
            transcript.PitchStability >= 0.50f;
        bool stableSingingEvidence = normalStableSingingEvidence ||
            expectedStableSingingEvidence;
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
            if (m_LogSpeculativeListening)
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
            m_StreamingTurnIsSinging = false;
            m_StreamingSingingOnsetAudioMs = -1;
            m_StreamingSingingConsecutiveFrames = 0;
            m_StreamingSingingCandidateStartAudioMs = -1;
            singing = false;
        }
        if (singing)
        {
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
            ReleasePreparedSingingBridge(true);
            m_SpeculativeDraft = null;
            if (m_SpeculativeDraftCoroutine != null)
                StopCoroutine(m_SpeculativeDraftCoroutine);
            m_SpeculativeDraftCoroutine = null;

            if (m_EnableSpeculativeListening &&
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

        if (m_SpeculativeRequestInFlight && transcript.Revision &&
            TranscriptContinuity(previousLyric, lyric) < 0.42f)
            CancelSpeculativeRequestOnly();

        if (m_SpeculativeDraftCoroutine != null)
            StopCoroutine(m_SpeculativeDraftCoroutine);
        int version = m_StreamingTranscriptVersion;
        m_SpeculativeDraftCoroutine = StartCoroutine(
            RequestSingingDraftAfterDelay(version, evidence));

        if (m_LogSpeculativeListening)
            Debug.Log($"[歌唱流式倾听] v{version} audio={transcript.AudioMs}ms " +
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
        if (m_SpeculativeRequestInFlight) yield break;

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
            "\"confidence\":0.0,\"observed_mode\":\"speech|singing|uncertain\"," +
            "\"mode_confidence\":0.0}";

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
            if (parsed != null && !string.IsNullOrWhiteSpace(parsed.draft))
            {
                parsed.observed_mode = NormalizeObservedMode(parsed.observed_mode);
                parsed.sourceTranscript = transcript;
                parsed.draft = StripAgentTagsForTTS(parsed.draft).Trim();
                m_SpeculativeDraft = parsed;
                //mode 判定单独留一份：切进歌唱模式时 m_SpeculativeDraft 会被清掉
                //(UpdateStreamingSingingReaction 开头的 CancelSpeculativeRequestOnly)，
                //而正是那之后才需要靠它把误判的歌唱模式退出来。
                m_LastObservedMode = parsed.observed_mode;
                m_LastObservedModeConfidence = Mathf.Clamp01(parsed.mode_confidence);
                m_LastObservedModeTranscript = transcript;
                if (m_LogSpeculativeListening)
                    Debug.Log($"[流式倾听] 临时草稿就绪 confidence={parsed.confidence:F2} " +
                              $"mode={parsed.observed_mode}/{parsed.mode_confidence:F2}: \"{parsed.draft}\"");
                //草稿本来就在用户说话期间生成好了，顺手把开场静默预合成，
                //EOU 时就能说一句切题的话，而不是通用的"なるほど……"。
                if (!m_StreamingTurnIsSinging) PrepareSingingBridge(parsed);
            }
            ScheduleSpeculativeRefreshIfNeeded();
        });
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
        if (!m_StreamingTurnIsSinging || m_SpeculativeRequestInFlight) yield break;
        evidence = m_StreamingSingingEvidence;
        if (string.IsNullOrEmpty(evidence)) yield break;
        if (evidence == m_LastDraftTranscript) yield break;

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
            "\"confidence\":0.0,\"observed_mode\":\"speech|singing|uncertain\"," +
            "\"mode_confidence\":0.0}";

        m_ChatSettings.m_ChatModel.PostEphemeralMsg(prompt, response =>
        {
            if (requestVersion != m_SpeculativeRequestVersion) return;
            m_SpeculativeRequestInFlight = false;
            if (!m_StreamingTurnIsSinging || evidence != m_StreamingSingingEvidence)
            {
                ScheduleSingingRefreshIfNeeded();
                return;
            }

            SpeculativeDraft parsed = ParseSpeculativeDraft(response);
            if (parsed != null && !string.IsNullOrWhiteSpace(parsed.draft))
            {
                parsed.observed_mode = NormalizeObservedMode(parsed.observed_mode);
                parsed.sourceTranscript = m_StreamingTranscript;
                //歌唱草稿也要刷新这份判定，否则切进歌唱模式之后就没有新判定了——
                //退出将只能依赖切换之前的旧值，那等于只有一次机会。
                m_LastObservedMode = parsed.observed_mode;
                m_LastObservedModeConfidence = Mathf.Clamp01(parsed.mode_confidence);
                m_LastObservedModeTranscript = m_StreamingTranscript;
                parsed.sourceEvidence = evidence;
                parsed.sourceSingingProbability = m_StreamingSingingProbability;
                parsed.sourcePitchStability = m_StreamingPitchStability;
                bool speechVeto = IsStrongSpeculativeSpeechVeto(parsed);
                parsed.isSinging = !speechVeto;
                parsed.draft = speechVeto
                    ? StripAgentTagsForTTS(parsed.draft).Trim()
                    : SanitizeSingingBridge(parsed.draft);
                if (!string.IsNullOrEmpty(parsed.draft))
                {
                    m_SpeculativeDraft = parsed;
                    if (m_LogSpeculativeListening)
                    {
                        Debug.Log($"[歌唱流式倾听] 心里话：\"{parsed.inner_reaction}\"；" +
                                  $"mode={parsed.observed_mode}/{parsed.mode_confidence:F2} " +
                                  $"speechVeto={speechVeto}；候选开场 confidence={parsed.confidence:F2}: " +
                                  $"\"{parsed.draft}\"");
                    }
                    if (speechVeto)
                    {
                        ReleasePreparedSingingBridge(true);
                        ResetStreamingHumBackPrefix("speculative-cognition-speech-veto", true);
                    }
                    else
                    {
                        PrepareSingingBridge(parsed);
                    }
                }
            }
            ScheduleSingingRefreshIfNeeded();
        });
    }

    private void ScheduleSingingRefreshIfNeeded()
    {
        if (!m_StreamingTurnIsSinging || string.IsNullOrWhiteSpace(m_StreamingSingingEvidence) ||
            m_StreamingSingingEvidence == m_LastDraftTranscript ||
            m_SpeculativeDraftCoroutine != null || m_SpeculativeRequestInFlight)
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
            m_SpeculativeDraftCoroutine != null || m_SpeculativeRequestInFlight)
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

    private bool IsStrongSpeculativeSpeechVeto(SpeculativeDraft draft)
    {
        return draft != null &&
            NormalizeObservedMode(draft.observed_mode) == "speech" &&
            Mathf.Clamp01(draft.mode_confidence) >= m_SpeculativeSpeechVetoConfidence;
    }

    /// <summary>
    /// 最终证据是否足以推翻心里话的"这是说话"。
    ///
    /// 心里话是在**流式途中**、拿着残缺转写下的判断；最终模态判定拿的是完整转写
    /// (必要时还附了片段歌词)，信息严格更多。两者冲突时不该让先下的那个赢——
    /// 8/10 实测两轮被这么杀掉：岛正确(77% / 67%)、最终判定都说 singing、声学
    /// 0.63 / 0.71，仍被回滚，用户当场说「我刚才已经唱了呀」。
    ///
    /// 但心里话那道闸本身有用(见 HasStrongSpeechModeJudgment：正常说话声学稳在
    /// 0.58~0.64，单看声学挡不住)，所以不是取消它，而是要求**声学与最终文字同时
    /// 判唱**才能推翻——两票对一票，且那两票信息更全。
    ///
    /// 两处心里话闸必须用同一条规则：DealingTextCallback 里的 cognitiveSpeechVeto，
    /// 以及 TryHandleDirectSingAlongTurn 开头那道。8/10 只改了前者，结果 28214 行
    /// 日志里"不否决"已经打出来，28256 行仍被后者拦下，回哼照样没发生。
    /// </summary>
    private bool FinalEvidenceOverridesSpeculation()
    {
        if (m_FinalModeVerdict != "singing") return false;
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        return senseVoice != null && senseVoice.LastSingingProbability >= 0.58f;
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

        if ((bridgeText == m_PreparedSingingBridgeText && m_PreparedSingingBridgeClip != null) ||
            (bridgeText == m_PendingBridgeText && m_SingingBridgeTtsInFlight))
            return;

        //不销毁已就绪的旧开场。草稿在用户说话期间每 1.4 秒刷新一次，若一进来就
        //Release，用户大部分时间都处在"旧的已删、新的没好"的空窗里——实测三次预合成
        //全部落在空窗上，EOU 只能播缓存语。改为：旧的留着可播，新的合成好了再替换。
        int generation = ++m_SingingBridgeGeneration;
        m_SingingBridgeTtsInFlight = true;
        m_PendingBridgeText = bridgeText;
        m_PendingBridgeConfidence = draft.confidence;
        m_PendingBridgeIsSinging = singing;
        string label = singing ? "歌唱预反应" : "说话预反应";

        if (m_LogSpeculativeListening)
            Debug.Log($"[{label}] 开始静默预合成(conf={draft.confidence:F2})：\"{bridgeText}\"");

        m_ChatSettings.m_TextToSpeech.PrepareSpeech(bridgeText, (clip, text) =>
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
        if (cancelSynthesis && m_ChatSettings != null && m_ChatSettings.m_TextToSpeech != null)
            m_ChatSettings.m_TextToSpeech.CancelPreparedSpeech();
        m_SingingBridgeGeneration++;
        m_SingingBridgeTtsInFlight = false;

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
        m_PreparedSingingBridgeConfidence = 0f;
        m_PreparedBridgeIsSinging = false;
        m_PendingBridgeText = "";
        m_PendingBridgeConfidence = 0f;
        m_PendingBridgeIsSinging = false;
    }

    /// <summary>
    /// EOU 最终转写到达时调用。相似则把准备结果作为一次性本轮提示；差异大则彻底丢弃，
    /// 让正式模型从最终文本重新理解。无论哪条路径，临时内容都不会进入持久历史。
    /// </summary>
    private string FinalizeSpeculativeTurn(string finalTranscript)
    {
        CancelSpeculativeRequestOnly();

        SpeculativeDraft draft = m_SpeculativeDraft;
        float similarity = draft != null
            ? TranscriptSimilarity(draft.sourceTranscript, finalTranscript)
            : 0f;
        string hint = null;
        if (draft != null && !string.IsNullOrWhiteSpace(draft.draft) &&
            similarity >= m_SpeculativeReuseSimilarity)
        {
            //已经出声(或即将出声)的那句开场不再写进这里。它由 PublishSpokenPrefixToLlm
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
            bool bridgeHandledAsPrefix =
                bridgeSpoken || (speechBridgeReady && m_EouFillerScheduled);

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
                          $"(开场走前缀={bridgeHandledAsPrefix})");
        }
        else if (draft != null && m_LogSpeculativeListening)
        {
            Debug.Log($"[流式倾听] 最终一致度 {similarity:F2}，放弃临时草稿并重新理解");
        }

        m_SpeculativeDraft = null;
        m_StreamingTranscript = "";
        m_LastDraftTranscript = "";
        ResetStreamingSingingEvidence();
        return hint;
    }

    private string FinalizeSingingTurn(SenseVoiceSpeechToText senseVoice)
    {
        CancelSpeculativeRequestOnly();
        bool confirmedSinging = senseVoice != null && senseVoice.LastIsSinging;
        m_EouSingingRejectedByFinal = !confirmedSinging;

        SpeculativeDraft draft = m_SpeculativeDraft;
        bool reusable = confirmedSinging && draft != null && draft.isSinging &&
            !string.IsNullOrWhiteSpace(draft.draft) &&
            draft.confidence >= m_SingingBridgeMinConfidence;
        string hint = null;
        if (reusable)
        {
            //与说话那条一致：已经出声的短开场交给 assistant 前缀，这里不再复述。
            bool alreadySpoken = m_PreparedSingingBridgePlayedThisTurn;
            hint =
                "[本轮可撤销听歌状态；最终歌唱分析具有最高优先级，不要提及内部状态。\n" +
                "听歌期间的理解：" + (draft.understanding ?? "") + "\n" +
                "仍不确定：" + (draft.uncertainty ?? "") + "\n" +
                "角色瞬时感受：" + (draft.inner_reaction ?? "") + "\n" +
                "临时模态判断：" + NormalizeObservedMode(draft.observed_mode) +
                "（置信度 " + Mathf.Clamp01(draft.mode_confidence).ToString("F2") + "）\n" +
                (alreadySpoken
                    ? "]"
                    : "安全短开场：" + draft.draft + "\n" +
                      "若稍后由快速回应播放这句，正式回答请从它之后自然接续，" +
                      "不要重复它的意思；否则可自行改写。]");
            if (m_LogSpeculativeListening)
                Debug.Log($"[歌唱流式倾听] 最终确认为歌唱，复用内部感受；" +
                          $"开场已播放={alreadySpoken}");
        }
        else
        {
            if (!confirmedSinging) ReleasePreparedSingingBridge(true);
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
        return hint;
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

    [Serializable]
    private class SpeculativeDraft
    {
        public string understanding = "";
        public string uncertainty = "";
        public string inner_reaction = "";
        public string draft = "";
        public float confidence = 0f;
        public string observed_mode = "uncertain";
        public float mode_confidence = 0f;
        [NonSerialized] public string sourceTranscript = "";
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
            RejectFastHumBackAfterFinal("final-asr-empty");
            ResetSpeculativeTurn();
            CancelPendingEouLatencyFiller("asr-empty", true);
            if (m_LogStreamTimings) Debug.Log("[ASR] 未检测到有效人声，本轮丢弃");
            if (m_RecordTips != null) m_RecordTips.text = "";
            return;
        }

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

        // 最终模态判定：拿完整转写问一次 LLM，判完再往下走。
        //
        // 为什么放在这里、为什么阻塞：流式期间的判定只有 25% 的轮次回得来
        // (debounce 0.55~0.9s + 节流 2.4~4.0s + 往返 2.6s，而一轮常常只有 2~4 秒)，
        // 于是「说话被当成唱歌」反复发生——0.54/0.56/0.61/0.62/0.65 都出现过，
        // 其中几次还经 armed-sing-along 快速路径直接唱了回去(那条路不问 LLM)。
        // 不带上下文的轻量请求实测 **0.41s**、十个历史误判样本全对，代价是快速首音
        // 从 0.36s 变约 0.77s，仍远在 1.5s 目标之内。而且这里用的是**最终**转写，
        // 比流式 partial 可靠得多。
        if (m_EnableFinalModeCheck && string.IsNullOrEmpty(m_FinalModeVerdict) &&
            senseVoice != null && senseVoice.LastIsSinging &&
            !string.IsNullOrWhiteSpace(senseVoice.LastText))
        {
            ChatQW qw = m_ChatSettings.m_ChatModel as ChatQW;
            if (qw != null)
            {
                string pendingMsg = _msg;
                bool pendingExit = streamingExitAtSubmission;
                qw.ClassifyUtteranceMode(senseVoice.LastText, verdict =>
                {
                    //空结论时用 "-" 占位，避免判不出来时无限重入
                    m_FinalModeVerdict = string.IsNullOrEmpty(verdict) ? "-" : verdict;
                    DealingTextCallback(pendingMsg, pendingExit);
                }, senseVoice.LastSegmentLyrics);
                return;
            }
        }

        // 文字与声学取交集，而不是让文字一票否决。
        //
        // 转写里"引用歌词"和"唱歌词"长得一模一样——「我刚才唱了…就是那个雪下的那么的深
        // 那个」和真的在唱，文字上分不开(实测这一句被判成 singing)。而声学恰好知道
        // 差别：真唱时音高稳、有持续音。所以：
        //   声学模糊带(<0.58，多半是待唱宽松闸强行判成的唱) → 文字有完全否决权，
        //     那正是 0.54/0.56 那两次「不对呀你漏了一些话」被唱回去的场合；
        //   声学高区(>=0.58) → 文字说不是也只做软降级，不自动回哼但保留演唱，
        //     因为混合轮(说话开头+后面唱)文字仍有 2/5 会答错，误杀代价太大。
        bool textSaysSpeech = m_FinalModeVerdict == "speech";
        bool acousticIsConfident = senseVoice != null &&
            senseVoice.LastSingingProbability >= 0.58f;
        m_FinalModeSoftDowngrade = textSaysSpeech && acousticIsConfident;

        bool finalEvidenceOverridesSpeculation = FinalEvidenceOverridesSpeculation();
        bool speculationSaysSpeech =
            m_EouCognitiveSpeechVeto || HasStrongSpeculativeSpeechVeto();
        bool speculativeVeto =
            speculationSaysSpeech && !finalEvidenceOverridesSpeculation;
        if (speculationSaysSpeech && finalEvidenceOverridesSpeculation &&
            m_LogStreamTimings)
        {
            Debug.Log("[模态判定] 心里话判说话，但声学 " +
                      $"prob={senseVoice.LastSingingProbability:F2} 与最终文字都判唱" +
                      "——以信息更全的最终判定为准，不否决");
        }
        bool cognitiveSpeechVeto = speculativeVeto ||
            (textSaysSpeech && !acousticIsConfident);
        if (m_FinalModeSoftDowngrade)
        {
            if (m_LogStreamTimings)
                Debug.Log($"[模态判定] 软降级：声学 prob={senseVoice.LastSingingProbability:F2} " +
                          "仍判唱，但文字判为说话——保留演唱，改为出声追问");
            // 软降级以前是**静默**跳过自动回哼：素材留着，但用户不知道，于是重唱、
            // 解释、再重唱——8/10 实测一次误判换来五轮返工。
            //
            // 提示词那一侧已经到顶：拿 28 段真值 + 29 条真说话量过五种写法，漏判
            // 死死卡在 6/28，而且漏的清一色是「评价+宣布+唱」这种形态(其中三条唱的
            // 是《演员》，歌词字面就是「说话的方式」，文字侧对它没有信息量)。
            // 判据推不动，就改代价：把静默丢弃换成一句追问，五轮返工变一轮确认。
            m_PendingSingingConfirmation = true;
        }
        if (cognitiveSpeechVeto)
        {
            // 心里话只在高置信度“这是普通说话”时拥有否决权；它不能单独把一段
            // 不确定音频确认成歌唱。即使后台已经做了预转换，也必须在这里撤销，
            // 并把最终文字交还给普通对话 LLM。
            RejectFastHumBackAfterFinal("speculative-cognition-classified-speech");
            ReleasePreparedSingingBridge(true);
            if (senseVoice != null)
            {
                if (senseVoice.LastIsSinging)
                    senseVoice.DowngradeLastSingingToSpeech(
                        "high-confidence speculative cognition classified ordinary speech");
                displayMsg = string.IsNullOrWhiteSpace(senseVoice.LastText)
                    ? displayMsg
                    : senseVoice.LastText;
                _msg =
                    "[实时倾听辅助判断：本轮是普通说话；此前约定不构成本轮声学事实，" +
                    "不得复述、转换或保存本轮音频] " +
                    senseVoice.BuildLastPerceivedText();
            }
            m_EouSingingRejectedByFinal = true;
            if (m_LogSpeculativeListening)
                Debug.Log("[歌唱流式倾听] 高置信度心里话判定为普通说话；" +
                          "否决预回唱并交给正式LLM交流");
        }
        bool hadSingingEvidence = senseVoice != null &&
            (senseVoice.LastIsSinging || m_EouTurnWasSinging ||
             m_StreamingTurnIsSinging || m_StreamingSingingExitDetected);
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
            (m_EouTurnWasSinging || m_StreamingTurnIsSinging);
        if (spokenSingingExit)
        {
            RejectFastHumBackAfterFinal("singing-ended-with-spoken-exit");
            ReleasePreparedSingingBridge(true);
            senseVoice.DowngradeLastMixedSingingToSpeech("recognized tail: " + senseVoice.LastText);
            _msg = "[混合歌唱转说话; 用户在末尾表示不会继续、忘词或停止，" +
                "必须优先回应末尾口语；本轮不得自动跟唱或写入练唱片段] " +
                senseVoice.BuildLastPerceivedText();
            m_EouSingingRejectedByFinal = true;
            if (m_LogSpeculativeListening)
                Debug.Log($"[歌唱流式倾听] 歌唱后转口语安全锁生效 " +
                          $"(requestLatch={streamingExitAtSubmission}, " +
                          $"liveLatch={m_StreamingSingingExitDetected}, " +
                          $"finalText={finalTextHasSingingExit})；" +
                          "降级为普通对话并保留上一段有效歌声缓存");
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
        // 声学说在唱、文字说在说话——两边都不足以定案，那就别替用户决定，问一句。
        // 素材保留 180s，他答"是"下一轮立刻能唱回来。
        // 必须放在流式恢复之后：那一段会整个重建 _msg，写在前面会被它盖掉，
        // 结果既不回哼也不追问。
        if (m_PendingSingingConfirmation && !spokenSingingExit && !cognitiveSpeechVeto &&
            senseVoice != null)
        {
            // 写入与确认必须分开。原来这里是"不确认就不写"，代价在 8/24 兑现了：
            // 用户唱了 34 秒(声学 0.76、岛占 92%)，文字判说话 → 一个字都没写进练唱会话。
            // 她照着这段指示问了、用户也答了「非常好」，可系统这边**没有任何路径消费
            // 那个回答**——m_PendingSingingConfirmation 全文只有置位/注入/清空三处。
            // 于是他接着说"把刚才这几段连起来唱"时，会话里根本没有那一段，来回四轮说不清。
            //
            // 改法是把两件事拆开：**照常写进去**，只是标一个"待确认"。
            // 标记不拦任何用途(order 照样点得到、照样能唱)，它只是个警示；
            // 用户否认时用 <practice_drop/> 去掉，比"漏写了再也补不回来"便宜得多。
            string confirmNote;
            bool written = senseVoice.CommitRecentSingingToPracticeSession(
                out int pendingIndex, true);
            if (written)
            {
                confirmNote =
                    $"这一段**已经作为第 {pendingIndex} 段记进练唱会话**了，标着「待确认」，" +
                    $"现在就能用——要唱回来直接 <hum_back mode=\"echo\"/>，" +
                    $"要和别的段连起来就把 {pendingIndex} 写进 order。" +
                    $"如果他说那不是在唱歌，用 <practice_drop order=\"{pendingIndex}\" " +
                    "reason=\"用户确认那不是唱歌\"/> 把它去掉。";
            }
            else
            {
                confirmNote = "**刚才那段录音还完整保留在手边**，" +
                    "他只要说一句「是」，下一轮你立刻就能把它唱回来。";
            }
            _msg = "[本轮存在不确定性：声学判定用户在唱歌，但转写读起来像普通说话，" +
                "无法确定他刚才是在演唱还是在讲话。" + confirmNote +
                "请在回应里自然地问一句「刚才那段是在唱吗／要我跟着唱一下吗」，由他确认。" +
                "不要擅自当成演唱去复述或跟唱；也不要去查曲库找歌名——" +
                "要唱的就是刚才那段录音本身，跟它叫什么歌无关；" +
                "更不要在他回答之前自己说「还是算了吧」把话收回去] " +
                senseVoice.BuildLastPerceivedText();
            if (m_LogHumBack)
                Debug.Log("[HumBack] 软降级：" +
                          (written ? $"已作为第 {pendingIndex} 段写进练唱会话(待确认)"
                                   : "写入练唱会话失败，素材仍在最近演唱缓存里") +
                          "，并让她出声追问是否在唱");
        }
        m_PendingSingingConfirmation = false;
        if (senseVoice != null && senseVoice.LastIsSinging)
        {
            displayMsg = string.IsNullOrWhiteSpace(senseVoice.LastText)
                ? "♪（哼唱片段）"
                : "♪ " + senseVoice.LastText;
        }
        else if (senseVoice != null && !string.IsNullOrWhiteSpace(senseVoice.LastText))
        {
            displayMsg = senseVoice.LastText;
        }
        string speculativeHint;
        if (spokenSingingExit || cognitiveSpeechVeto)
        {
            // UpdateStreamingSingingReaction already replaced the listening-only singing
            // draft with an ordinary conversational draft as soon as the spoken exit was
            // heard. Preserve that work here; the singing finalizer would discard it just
            // because it is intentionally marked as non-singing.
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
            SendDataInternal(_msg, speculativeHint);
            return;
        }

        m_InputWord.text = _msg;
    }

    /// <summary>
    /// EOU(End-Of-Utterance)时间戳——RTSpeechHandler在StopRecording时调用，
    /// 用来给ASR/LLM/TTS各阶段的延迟做"T+0"锚点。
    /// 0 = 当前这轮没有外部EOU标记(例如push-to-talk路径不调用)。
    /// </summary>
    private float m_EouTime = 0f;
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
        m_EouRescuedByTonalOverride = rescuedByTonalOverride;
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
        if (m_StreamingSingingExitDetected)
        {
            ReleasePreparedSingingBridge(true);
            ResetStreamingHumBackPrefix("singing-exit-at-eou", true);
        }
        m_EouFillerContext = DetermineLatencyFillerContext(
            m_StreamingTranscript, m_EouTurnWasSinging);
        ScheduleEouLatencyFiller(m_StreamingTranscript);
        if (m_LogStreamTimings)
            Debug.Log($"[Timing] EOU @ {m_EouTime:F2}s — 用户停止说话 " +
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
        List<string> chunks = SplitResponseIntoChunks(_response);
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
                    SetAnimator("state", 0);
                    yield break;
                }
                yield return null;
            }

            AudioClip currentClip = pending;
            string currentText = chunks[i];

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
                yield return StartCoroutine(TypeSentence(currentText, currentClip.length));
                while (m_AudioSource.isPlaying) yield return null;
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
    private IEnumerator TypeSentence(string _sentence, float totalDuration, int responseGeneration = -1)
    {
        if (string.IsNullOrEmpty(_sentence)) yield break;
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
        }
    }

#endregion

#region 流式生成（LLM边吐边播）

    //LLM吐出未成句的暂存
    private System.Text.StringBuilder m_SentenceBuffer = new System.Text.StringBuilder();
    //continue chain 的多个 LLM round 共用同一条 TTS pipeline。每个队列项带上
    //round id，这样判定某一轮是复读时，只删它自己尚未播放的内容，
    //不会误删上一节仍在排队的正常发言。
    private sealed class PendingSpeechChunk
    {
        public int RoundId;
        public string Text;
    }

    private sealed class PendingSpeechClip
    {
        public int RoundId;
        public string Text;
        public AudioClip Clip;
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
    //LLM流是否已结束
    private bool m_StreamComplete = true;
    //TTSSender是否已全部处理完
    private bool m_TTSSenderDone = true;
    //正式回复代次。用户重新开口时推进，所有旧 LLM/TTS 回调据此立即失效。
    private int m_FormalResponseGeneration = 0;
    private bool m_FormalResponseInFlight = false;
    private int m_FinalAsrRequestsInFlight = 0;
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
    //本轮正文中间出现 <silent/> 时，其后被切出来的内心独白文本(不发声，只入历史)。
    //每轮用完即清；见 OnStreamComplete 的尾部处理。
    private string m_PendingMidRoundInner = null;

    [Header("首音延迟快速回应")]
    [Tooltip("预计或实际等待较长时，先播放启动阶段缓存的短回应，不额外占用TTS推理队列")]
    [SerializeField] private bool m_EnableLatencyFiller = true;
    [Tooltip("从系统确认用户停止说话(EOU)到缓存短回应开始播放的目标秒数。EOU自身已有停顿确认，因此默认0.35秒，明显低于1.5秒上限。")]
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
        string transientSystemContext = null)
    {
        //防御性保证同一时刻只有一个正式生成。正常语音路径会在用户开口时更早撤销；
        //这里再兜一次，避免按钮输入或其他调用方制造并发回复。
        if (m_ChatSettings != null && m_ChatSettings.m_ChatModel != null)
            m_ChatSettings.m_ChatModel.CancelActiveResponse();
        int responseGeneration = ++m_FormalResponseGeneration;

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
        //MarkEOU 可能已在 ASR 阶段安排甚至播放了快速回应。正式管线接手时必须保留它，
        //否则会把已经在约 0.35 秒响起的缓存音频状态清掉并重复安排第二次 filler。
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
        if (continueExistingUserTurn)
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
                !holdSpeechForSongMemoryResult);
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
        m_ChatSettings.m_ChatModel.SpokenPrefix =
            spoken ? m_SpokenBridgeTextThisTurn.Trim() : "";
        if (spoken && m_LogSpeculativeListening)
            Debug.Log("[说话预反应] 已出声开场作为 assistant 前缀交给本轮回复: " +
                      $"\"{m_ChatSettings.m_ChatModel.SpokenPrefix}\"");
    }

    private void DispatchFormalStream(
        string prompt,
        string imageUrl,
        int responseGeneration,
        bool recordAssistantHistory = true)
    {
        PublishSpokenPrefixToLlm();
        m_FormalResponseInFlight = true;
        m_ChatSettings.m_ChatModel.PostMsgStream(
            prompt,
            delta =>
            {
                if (responseGeneration != m_FormalResponseGeneration) return;
                OnStreamDelta(delta);
            },
            full =>
            {
                if (responseGeneration != m_FormalResponseGeneration) return;
                m_FormalResponseInFlight = false;
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
        m_FormalResponseInFlight = true;
        m_ChatSettings.m_ChatModel.PostContinuationStream(
            transientSystemContext,
            delta =>
            {
                if (responseGeneration != m_FormalResponseGeneration) return;
                OnStreamDelta(delta);
            },
            full =>
            {
                if (responseGeneration != m_FormalResponseGeneration) return;
                m_FormalResponseInFlight = false;
                OnStreamComplete(full);
            },
            imageUrl);
    }

    private void InvalidateFormalResponse(string reason)
    {
        bool hadActiveRequest = m_FormalResponseInFlight;
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

        if (m_AudioSource == null || m_AudioSource.isPlaying) yield break;

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
    /// 用户一停止说话就开始计时，不等待最终 ASR 或 LLM。缓存音频在 EOU+目标时间直接播放，
    /// 因而正常语音和耗时较长的歌声分析都能把“有反应的首音”稳定压到1.5秒内。
    /// </summary>
    private void ScheduleEouLatencyFiller(string languageHint)
    {
        m_EouFillerGeneration++;
        m_EouFillerScheduled = false;
        if (!m_EnableLatencyFiller || !m_AutoSend || !m_UseStreaming || !m_IsVoiceMode ||
            m_ChatSettings == null ||
            m_ChatSettings.m_TextToSpeech == null || m_AudioSource == null)
            return;

        //本轮若是靠哼唱豁免捞回来的(语音VAD其实拒绝了)，不播快速应声。
        //快速应声在 EOU+0.36s 就出声，远早于正式 ASR 的判定——实测一场里 5 次环境噪音
        //全部被"回应"了一句缓存短句("ふふっ……" / "うん、ちゃんと聴いていたわ……")，
        //随后才 CancelPendingEouLatencyFiller("asr-empty")。取消发生在声音已经出去之后，
        //用户听到的就是"角色在对着杂音搭话"。
        //真哼唱的代价是少了这句应声，等正式管线的回复——比对着风扇说话好。
        if (m_EouRescuedByTonalOverride)
        {
            if (m_LogStreamTimings)
                Debug.Log("[LatencyFiller] 本轮由哼唱豁免触发(语音VAD未认可)，不播快速应声");
            return;
        }

        m_EouFillerScheduled = true;
        int generation = m_EouFillerGeneration;
        StartCoroutine(PlayEouLatencyFillerAtTarget(
            generation, m_EouTime, SelectLatencyFillerLanguageHint(languageHint)));
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

    private IEnumerator PlayEouLatencyFillerAtTarget(
        int generation, float eouTime, string languageHint)
    {
        float target = Mathf.Clamp(m_UserFirstAudioTargetSec, 0.1f, 1.45f);
        while (Time.realtimeSinceStartup - eouTime < target)
        {
            if (generation != m_EouFillerGeneration || !m_EouFillerScheduled ||
                m_RealFirstAudioStarted)
                yield break;
            yield return null;
        }

        if (generation != m_EouFillerGeneration || !m_EouFillerScheduled ||
            m_RealFirstAudioStarted)
            yield break;
        m_EouFillerScheduled = false;

        //用户在目标点前又开口、或仍有别的真实音频时，不叠音。
        if (m_AudioSource == null || m_AudioSource.isPlaying || m_LatencyFillerPlayed)
            yield break;

        string fillerText = "";
        float fillerDuration = 0f;
        //唱歌与说话都先试预合成的草稿开场；它是针对本轮内容的，比通用缓存语切题得多。
        bool singingTurn = m_EouTurnWasSinging && !m_EouSingingRejectedByFinal;
        bool preparedSingingBridge =
            TryPlayPreparedSingingBridge(singingTurn, m_AudioSource, out fillerText, out fillerDuration);
        if (!preparedSingingBridge &&
            !m_ChatSettings.m_TextToSpeech.TryPlayLatencyFiller(
                languageHint,
                m_EouSingingRejectedByFinal ? "neutral" : m_EouFillerContext,
                m_AudioSource,
                out fillerText,
                out fillerDuration))
        {
            if (m_LogStreamTimings)
                Debug.LogWarning("[LatencyFiller] EOU快速回应缓存未就绪，无法保证1.5秒首音");
            yield break;
        }

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
            float eouToFiller = Time.realtimeSinceStartup - eouTime;
            Debug.Log($"[Timing] ★ EOU→快速首音: {eouToFiller:F2}s (目标≤1.5s)");
            Debug.Log(preparedSingingBridge
                ? $"[{(singingTurn ? "歌唱预反应" : "说话预反应")}] 播放预合成开场: \"{fillerText}\""
                : $"[LatencyFiller] 播放EOU/{m_EouFillerContext}缓存短回应: \"{fillerText}\"");
        }
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

    private void MarkRealFirstAudioStarted()
    {
        if (m_RealFirstAudioStarted) return;
        m_RealFirstAudioStarted = true;
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
        if (string.IsNullOrEmpty(delta)) return;
        if (m_LogStreamTimings && !m_FirstDeltaLogged)
        {
            m_FirstDeltaLogged = true;
            Debug.Log($"[Stream] T+{Elapsed():F2}s LLM首token到达");
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
        //★ 排障用：打印**未经任何剥离**的 LLM 原文。
        //  现有流式日志(首块切出 / TTS流请求发出)打的都是切句并剥标签之后的结果，
        //  所以"标签压根没生成"和"生成了但被吞掉"在日志里长得一模一样，无法区分。
        //  典型待查问题：她把内心话当正文念出来(用第三人称指代用户)，而
        //  <silent/> 连续三场 0 次——要判断是没打标签还是标签被吞，只能看原文。
        //  平时关掉：原文会带上全部控制标签，很吵。
        if (m_LogRawLLMOutput)
            Debug.Log($"[LLM原文] {(full ?? "").Replace("\n", "\\n")}");

        //ChatQW 在调用 onComplete 之前已把“已经说出口的快速开场”
        //与本轮正式回复合并进 assistant 历史。本地副本必须在第一个
        //正式回复完成时消费掉；否则 <continue/> 每续一轮都会把同一句
        //assistant 前缀重新挂在请求末尾，把模型一次次拉回故事开头。
        if (!string.IsNullOrWhiteSpace(m_SpokenBridgeTextThisTurn))
        {
            m_SpokenBridgeTextThisTurn = "";
            if (m_LogSpeculativeListening)
                Debug.Log("[说话预反应] assistant 前缀已合并进历史，后续 chain 不再重复注入");
        }

        //记忆写入标签的提取与应用不看 agent 开关——直接对话模式她也在记忆。
        //只在全文完成时做一次(chunk 级会重复计),剥净后再做后续解析。
        string unknownTag = DetectUnknownAgentTag(full ?? "");
        if (!string.IsNullOrEmpty(unknownTag))
        {
            m_LastUnknownTagNote = unknownTag;
            Debug.LogWarning($"[Agent/Tag] 输出里有不存在的标签 {unknownTag}，已被整条丢弃");
        }

        string afterMemTags;
        var memOps = MemoryTagParser.Extract(full ?? "", out afterMemTags);
        if (memOps != null && m_MemoryHub != null) m_MemoryHub.ApplyMemoryOps(memOps);
        AgentSkillRequest skillRequest = ExtractSkillRequestTag(ref afterMemTags);
        AgentSkillControlRequest skillControl = ExtractSkillControlTag(ref afterMemTags);
        AgentSingingIntentRequest singingIntent = ExtractSingingIntentTag(ref afterMemTags);

        //歌曲检索/记忆是角色能力，不是 Agent Loop 的调度语义。必须在判断 agent 开关前
        //提取并执行；否则用户关闭实时模式后，模型虽然给出标签却只会被剥掉而不落盘。
        string cleanFull = afterMemTags;
        //先判复读，再动工具。practice_drop 是**边解析边执行**的，判定必须排在它前面，
        //否则等发现是复读时那一段已经被删掉了。
        bool selfRepeat = IsVerbatimSelfRepeat(
            StripAgentTagsForTTS(afterMemTags), out string selfRepeatReason);
        bool singingIntentAccepted = false;
        string singingIntentResult = "";
        if (!selfRepeat && singingIntent != null)
        {
            singingIntentAccepted = TryApplySingingIntent(
                singingIntent, out singingIntentResult);
            if (!singingIntentAccepted)
                NoteToolFailure("未执行 singing 结构化意图：" + singingIntentResult);
        }
        if (!selfRepeat && skillControl != null &&
            !TryApplySkillControl(skillControl, out string skillControlResult))
        {
            NoteToolFailure("未执行 Skill 权限变更：" + skillControlResult);
        }
        bool autonomousSkillApproved = false;
        string skillRequestRejection = "";
        if (!selfRepeat && skillRequest != null)
        {
            autonomousSkillApproved = TryApproveAutonomousSkillRequest(
                skillRequest, out skillRequestRejection);
            if (!autonomousSkillApproved && m_LogAgentLoop)
                Debug.LogWarning($"[LLM技能] {skillRequest.Name} 自主申请未执行：" +
                                 skillRequestRejection);
        }
        bool hadSingingAction = s_PracticeDropTagRegex.IsMatch(cleanFull) ||
            s_SongMemoryTagRegex.IsMatch(cleanFull) || s_SongSearchTagRegex.IsMatch(cleanFull) ||
            s_SongSingTagRegex.IsMatch(cleanFull) || s_HumBackTagRegex.IsMatch(cleanFull) ||
            (singingIntent != null && singingIntent.Action != "none");
        bool singingActionAllowed = CanExecuteSkillAction("singing", out string singingBlockReason);
        if (singingIntent != null && !singingIntentAccepted)
        {
            singingActionAllowed = false;
            singingBlockReason = "singing_intent 校验失败：" + singingIntentResult;
        }
        AgentSongMemoryRequest songMemory = ExtractSongMemoryTag(ref cleanFull);
        AgentSongSearchRequest songSearch = ExtractSongSearchTag(ref cleanFull);
        AgentSongSingRequest songSing = ExtractSongSingTag(ref cleanFull);
        ExtractAndApplyPracticeDropTag(ref cleanFull, !selfRepeat && singingActionAllowed);
        AgentHumBackRequest humBack = ExtractHumBackTag(ref cleanFull);
        if (!selfRepeat)
            ApplySingingIntentAction(
                singingIntent, singingIntentAccepted, ref songSing, ref humBack);
        if (!selfRepeat && singingActionAllowed && !m_AgentCurrentRoundIsTick &&
            !ValidateUserSingingActionAuthorization(
                singingIntentAccepted && singingIntent != null ? singingIntent.Actor : "",
                singingIntentAccepted && singingIntent != null ? singingIntent.Action : "",
                songSing != null,
                humBack != null ? humBack.Mode : "",
                out string userSingingAuthorizationFailure))
        {
            singingActionAllowed = false;
            singingBlockReason = userSingingAuthorizationFailure;
        }
        string speakerName = ExtractSpeakerNameTag(ref cleanFull);
        AgentSpeakerManageRequest speakerManage = ExtractSpeakerManageTag(ref cleanFull);
        if (selfRepeat)
        {
            //标签照常从正文剥掉(不剥会被念出来)，但一个都不派发。
            songMemory = null; songSearch = null; songSing = null; humBack = null;
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
            NoteToolFailure("未执行 singing Skill 动作：" + singingBlockReason);
            Debug.LogWarning("[LLM技能] 拦下未授权的 singing 动作：" + singingBlockReason);
        }
        if (m_SongMemoryAcknowledgementInFlight &&
            (songMemory != null || songSearch != null || songSing != null))
        {
            Debug.LogWarning("[SongMemory] 结果确认阶段忽略模型重复输出的歌曲工具标签");
            songMemory = null;
            songSearch = null;
            songSing = null;
        }
        if (TryRerouteSongSingToPractice(ref songSing, ref humBack) && m_LogHumBack)
            Debug.LogWarning("[SongSing→HumBack] 曲库标签指向本轮练唱片段，已保留顺序并改走 practice");
        if (ShouldDiscardSongSingToolForCurrentTurn(songSing))
        {
            if (m_LogHumBack)
                Debug.LogWarning("[SongSing] 模型把跟唱约定、当前演唱或失败陈述误写成曲库演唱；已丢弃该标签并重新分流");
            songSing = null;
        }
        if (humBack != null) m_ExplicitHumBackHandled = true;
        if (songMemory != null)
        {
            if (string.Equals(songMemory.Action, "remember", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(songMemory.Title))
                songMemory.Title = ExtractExplicitSongTitle(m_LastUserMsg);
            m_ExplicitSongRememberHandled = true;
        }
        else if (!selfRepeat && singingActionAllowed && ShouldFallbackToExplicitSongRemember())
        {
            songMemory = new AgentSongMemoryRequest
            {
                Action = "remember",
                Title = ExtractExplicitSongTitle(m_LastUserMsg),
                Reason = "用户明确要求记住最近歌声，但模型漏掉了 song_remember 标签",
            };
            m_ExplicitSongRememberHandled = true;
            if (m_LogAgentLoop)
                Debug.LogWarning("[SongMemory] 检测到用户明确保存歌声的请求，模型未调用 <song_remember/>，执行安全兜底");
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
        if (speakerManage != null) BeginSpeakerManage(speakerManage);
        //曾经在这里做「模型漏调 <song_sing/> 就按正则兜底」。已删除：8/9 实测触发 5 次、
        //成功 0 次，而正则从普通说话里编出来的"歌名"是「了呀」(出自"我刚才已经唱了呀")、
        //「点歌」、「完再唱」、以及整句歌词。判据 IsPlausibleUnquotedSongTitle 是一份
        //黑名单，不在名单里的一律放行，注定漏。
        //更要命的是它会连带扣住她那一轮的正常回复(已扣留 7 次)，查不到歌之后整轮无声。
        //而那几轮模型自己**没有**调用 <song_sing/>——它判断"这不是点歌请求"，判断是对的，
        //是正则在第二次猜并且猜错。要不要从曲库唱，交给她自己决定。
        if (songSing != null)
        {
            humBack = null;
            BeginSongSing(songSing);
        }
        if (songSing == null && humBack == null && !selfRepeat && singingActionAllowed &&
            singingIntentAccepted && singingIntent != null &&
            singingIntent.Actor == "character" && singingIntent.Action == "echo" &&
            ShouldFallbackToExplicitHumBack())
        {
            humBack = new AgentHumBackRequest
            {
                Mode = "echo",
                Reason = "用户明确要求回哼最近旋律，但模型漏掉了 hum_back 标签",
            };
            m_ExplicitHumBackHandled = true;
            if (m_LogHumBack)
                Debug.LogWarning("[HumBack] 检测到明确回哼请求，模型未调用 <hum_back/>，执行安全兜底");
        }
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
            else if (skillRequest != null && wantsContinue)
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
            //兜底:引号异形/格式畸变导致 ParseAgentTags 没认出的标签,别让它进历史和感知帧
            cleanFull = StripAgentTagsForTTS(cleanFull);
            m_RoundNextInSec = nextInSec;
            m_RoundFocus = focus;
            m_RoundContinue = wantsContinue;
            m_RoundSilent = wantsSilent;
            m_RoundLookRequest = wantsLook;

            //★ 立刻应用视觉状态变化——下一帧(chain or scheduled)就能反映新眼睛状态
            if (wantsLook.HasValue)
            {
                bool newState = wantsLook.Value && m_EnableScreenVision;  //视觉总开关关时 <look/> 不起效
                if (m_AgentEyesOpen != newState)
                {
                    m_AgentEyesOpen = newState;
                    if (m_LogAgentLoop)
                        Debug.Log($"[Agent] {(newState ? "<look/> 睁眼" : "<unlook/> 闭眼")}");
                }
            }

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
                AgentSongSingRequest ignoredSongSing = ExtractSongSingTag(ref cleanTail);
                AgentHumBackRequest ignoredHumBack = ExtractHumBackTag(ref cleanTail);
                cleanTail = StripAgentTagsForTTS(cleanTail);
                m_SentenceBuffer.Length = 0;
                if (!string.IsNullOrEmpty(cleanTail)) m_SentenceBuffer.Append(cleanTail);
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
            if (m_AgentRoundInFlight)
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
        bool isInnerThought = m_AgentRunning && m_AgentRoundInFlight && m_RoundIsInner
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

        //—— 续派 chain：LLM 给了 <continue/> 就**立即**派下一帧 ——
        //不置 m_StreamComplete=true、不收 round。
        //新一轮的 OnStreamDelta/OnStreamComplete 用同一对回调，文本进同一个 m_PendingChunks，
        //AudioPlayer 不会因为"队列空 + 流结束"退出——它会等到新文本来。
        //(内心独白 + continue 同样支持——pipeline 没被关，下一轮的 spoken/inner 都能接上)
        bool willChain = m_AgentRunning && m_AgentRoundInFlight && m_RoundContinue
                         && m_ConsecutiveAITurns < m_MaxConsecutiveAITurns;
        if (willChain)
        {
            m_RoundIsInner = false;          //新一轮 chain round 重新从普通模式开始
            m_RoundRepeatCheckDone = false;
            m_RoundRepeatHold = false;
            m_RoundInnerCheckDone = false;   //★ inner 检测重置——否则 chain round 永远进不了 inner 模式，
                                             //   LLM 想用 <silent/> 自救破局会失败、内心独白被 TTS 念出来
            string requestedSkill = m_PendingAutonomousSkillName;
            string chainReason = string.IsNullOrEmpty(requestedSkill)
                ? "continue-chain"
                : "skill-request:" + requestedSkill;
            PrepareActiveSkillsForRound(null, chainReason);
            string frame = BuildPerceptionFrame(chainReason);
            m_PendingAutonomousSkillName = "";
            m_PendingAutonomousSkillReason = "";
            HarvestSongIds(frame);
            m_ChatHistory.Add(frame);
            ClearRoundParsed();  //清掉本轮解析；下一轮 OnStreamComplete 会重新写
            m_CurrentSpeechRoundId = ++m_SpeechRoundSequence;
            if (m_LogAgentLoop)
                Debug.Log(string.IsNullOrEmpty(requestedSkill)
                    ? "[Agent] <continue/> → 链接下一帧(TTS pipeline 不间断)"
                    : $"[LLM技能] {requestedSkill} 详细规则已加载 → 链接自主执行帧");
            //chain 中段也按当前眼睛状态决定是否附图——LLM 上一节如果 <look/> 了，从这一节起就开始看
            string chainImageUrl = MaybeCaptureScreenForLLM();
            DispatchFormalStream(frame, chainImageUrl, m_FormalResponseGeneration);
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
        if (m_AgentRunning && m_AgentRoundInFlight && pipelineIdle)
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
        string buf = m_SentenceBuffer.ToString();
        //工具标签是流式逐字到达的。完整标签可以靠正则剥掉，但标签尚未闭合时，
        //属性里的“，/。/\n”会被句子切分器误认为正文边界，导致半截标签提前进入 TTS。
        //因此一旦看到已知标签的起始（哪怕当前只有“<mem”），整段后缀都先扣在 buffer 里，
        //只允许标签之前的正文参与切句；OnStreamComplete 收到完整标签后再解析/丢弃。
        int pendingTagStart = FindPotentialAgentTagStart(buf);
        string speakable = pendingTagStart >= 0 ? buf.Substring(0, pendingTagStart) : buf;
        int boundary = FindFlushBoundary(speakable, !m_FirstChunkFlushed);

        if (boundary >= 0)
        {
            string completed = buf.Substring(0, boundary + 1).Trim();
            string remaining = buf.Substring(boundary + 1);
            m_SentenceBuffer.Length = 0;
            m_SentenceBuffer.Append(remaining);
            //过滤 agent 标签——LLM 把 <continue/> 单写一行时，"<continue/>\n" 会被
            //\n strong boundary 切出当成"一句"推进队列，TTS 就读出来了。这里兜底。
            //不看 agent 开关:system prompt 无条件教标签,直接对话模式模型也会输出。
            completed = StripAgentTagsForTTS(completed);
            //过滤纯标点段(LLM偶尔会单独吐"…"或"。。。")，避免TTS 400
            if (!string.IsNullOrEmpty(completed) && !IsPurePunctuation(completed))
            {
                m_PendingChunks.Enqueue(new PendingSpeechChunk
                {
                    RoundId = m_CurrentSpeechRoundId,
                    Text = completed
                });
                if (!m_FirstChunkFlushed)
                {
                    m_FirstChunkFlushed = true;
                    if (m_LogStreamTimings) Debug.Log($"[Stream] T+{Elapsed():F2}s 首块切出: \"{completed}\"");
                }
            }
        }

        if (flushAll)
        {
            string tail = m_SentenceBuffer.ToString().Trim();
            m_SentenceBuffer.Length = 0;
            tail = StripAgentTagsForTTS(tail);
            if (!string.IsNullOrEmpty(tail) && !IsPurePunctuation(tail))
            {
                m_PendingChunks.Enqueue(new PendingSpeechChunk
                {
                    RoundId = m_CurrentSpeechRoundId,
                    Text = tail
                });
            }
        }
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
            m_ChatSettings.m_TextToSpeech.Speak(chunk, onReceive);

            //TTS客户端正常会在20s内回调(成功或失败都会调)。这里的25s只是兜底，
            //防止TTS客户端自己挂掉永远不回调。GPT-SoVITS内部失败也会调callback(null,..)
            float waitStart = Time.realtimeSinceStartup;
            while (!pendingDone)
            {
                if (responseGeneration != m_FormalResponseGeneration) yield break;
                if (Time.realtimeSinceStartup - waitStart > 25f)
                {
                    Debug.LogError("TTS客户端无响应(>25s)，跳过此段: " + chunk);
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
                        Clip = pending
                    });
                }
            }
            else
            {
                if (m_LogStreamTimings) Debug.LogWarning($"[Stream] T+{Elapsed():F2}s TTS失败/跳过: \"{chunk}\"");
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

        while (true)
        {
            if (responseGeneration != m_FormalResponseGeneration) yield break;
            while (m_PendingChunks.Count == 0)
            {
                if (responseGeneration != m_FormalResponseGeneration) yield break;
                if (m_StreamComplete)
                {
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

            bool started = false;
            bool completed = false;
            bool succeeded = false;
            float audioDuration = 0f;

            //先行开场既然已经出声，就让它把话说完。GPTSoVITSFASTAPI 只在首批 PCM 到达时
            //才接管音源，所以请求可以提前发出去，提前量由 m_FillerHandoffLeadSec 控制。
            if (firstChunk)
                yield return StartCoroutine(WaitForSpokenFillerToFinish(
                    responseGeneration, m_FillerHandoffLeadSec));
            if (responseGeneration != m_FormalResponseGeneration) yield break;

            if (m_LogStreamTimings) Debug.Log($"[Stream] T+{Elapsed():F2}s TTS流请求发出: \"{text}\"");

            m_ChatSettings.m_TextToSpeech.SpeakStreaming(
                text,
                m_AudioSource,
                _ => { started = true; },
                (success, _, duration) =>
                {
                    succeeded = success;
                    audioDuration = duration;
                    completed = true;
                });

            while (!started && !completed)
            {
                if (responseGeneration != m_FormalResponseGeneration) yield break;
                yield return null;
            }

            if (!started)
            {
                if (m_LogStreamTimings) Debug.LogWarning($"[Stream] T+{Elapsed():F2}s TTS流失败/跳过: \"{text}\"");
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
            float estimatedDuration = Mathf.Max(0.5f, text.Length * Mathf.Max(0.08f, m_WordWaitTime));
            m_DirectStreamingSubtitleCoroutine = StartCoroutine(TypeSentence(text, estimatedDuration));

            while (!completed)
            {
                if (responseGeneration != m_FormalResponseGeneration || !IsAISpeaking)
                {
                    m_ChatSettings.m_TextToSpeech.CancelStreaming();
                    StopDirectStreamingSubtitle();
                    yield break;
                }
                yield return null;
            }

            StopDirectStreamingSubtitle();
            if (IsAISpeaking)
            {
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
            m_CurrentlyPlayingText = "";

            //直播放流的下一段需要重新建立推理流，本身已有自然间隔；这里不再叠加
            //完整AudioClip路径的句间呼吸，否则会让句间停顿被重复计算。
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
        ClearUserTurnAwaitingReply("回复播完");
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
        //用户接管了这一轮，等待作废；他自己会带来新的一轮
        ClearUserTurnAwaitingReply("被用户打断");

        bool hasResponseWork = IsAISpeaking || IsVoiceOutputPlaying || m_FormalResponseInFlight
            || m_HumBackPending || m_HumBackPreparingCarrier || m_HumBackPlaying
            || m_SentenceBuffer.Length > 0 || m_PendingChunks.Count > 0 || m_PendingClips.Count > 0;
        if (!hasResponseWork) return;

        //当前正在播的那一chunk按音频时长比例算听到了多少字
        if (m_AudioSource != null && m_AudioSource.clip != null && m_AudioSource.clip.length > 0f
            && !string.IsNullOrEmpty(m_CurrentlyPlayingText))
        {
            float duration = m_LatencyFillerPlayed
                ? Mathf.Max(0.1f, m_LatencyFillerDuration)
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

    [Header("待机自主意图 — 正式轮次前先判断有没有新东西值得表达")]
    [Tooltip("普通待机 tick 先走一次不写入历史的内部判断。只有出现新的想法、观察、记忆整理或动作意图时，才开启正式角色轮次；工具结果与 session-start 等真实新事件直接放行。")]
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
    [Tooltip("演唱片段中用户明确要求记住/保存时，若模型漏掉 <song_remember/>，自动保存最近歌声；随口哼唱不会触发。")]
    [SerializeField] private bool m_EnforceExplicitSongRemember = true;
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
        public string intent;
        public string novelty;
        public float wait_seconds;
    }
    private bool m_AutonomyProbeInFlight = false;
    private int m_AutonomyProbeGeneration = 0;
    private float m_AutonomyProbeNotBefore = -999f;
    private float m_LastUserTurnTime = -1f;
    private float m_LastAITurnTime = -1f;
    private string m_LastUserMsg = "";
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
    }

    private readonly Dictionary<string, SkillRouteState> m_SkillRouteStates =
        new Dictionary<string, SkillRouteState>(StringComparer.OrdinalIgnoreCase);

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

    private sealed class AgentSingingIntentRequest
    {
        public string Permission;
        public string Scope;
        public string Actor;
        public string Action;
        public string Order;
        public float Confidence = float.NaN;
        public string Evidence;
    }

    private static readonly System.Text.RegularExpressions.Regex s_SkillRequestTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<skill_request\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex s_SkillControlTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<skill_control\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex s_SingingIntentTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<singing_intent\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly string[] s_SingingSkillTopicSignals =
    {
        "[演唱片段", "[混合歌唱转说话", "歌曲检索工具", "歌曲记忆工具", "旋律回哼工具",
        "练唱会话", "唱", "歌曲", "这首", "那首", "歌词", "旋律", "哼", "音调",
        "调高", "调低", "升调", "降调", "起调", "曲库",
        "歌って", "歌う", "歌を", "歌声", "歌詞", "曲を", "この曲", "その曲",
        "メロディ", "ハミング", "鼻歌", "キーを",
        "singing", "sing a song", "sing this", "sing that", "sing it", "sing for",
        "song", "lyric", "melody", "hum back", "humming", "vocal pitch"
    };

    private static readonly string[] s_SingingSkillFollowupSignals =
    {
        "再来", "再一次", "重来", "刚才那", "这一段", "那一段", "第一段", "第二段",
        "高一点", "低一点", "快一点", "慢一点", "顺序", "反过来",
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

    private string DescribeSkillAccess(SkillRouteState state, bool activeThisRound)
    {
        if (state.Access == SkillAccess.UserDisabled) return "用户已禁用";
        if (state.Access == SkillAccess.SoftSuppressed) return "暂时不主动使用";
        return activeThisRound ? "本轮已加载" : "可用、未加载";
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
              .Append(definition.AutonomousDescription).Append("；状态=")
              .Append(DescribeSkillAccess(state, state.ActiveThisRound)).Append('\n');
        }
        return sb.ToString().Trim();
    }

    private void CancelSkillRuntimeActivity(string skillName, string reason)
    {
        if (!string.Equals(skillName, "singing", StringComparison.OrdinalIgnoreCase)) return;
        m_WaitingForRequestedSingAlong = false;
        ReleasePreparedSingingBridge(true);
        if (m_HumBackPending || m_HumBackPreparingCarrier || m_HumBackPlaying)
            CancelPendingHumBack(reason, true);
        if (m_FastHumBackEouStaged || m_FastHumBackActive)
            RejectFastHumBackAfterFinal(reason);
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
        if (state.Access == SkillAccess.SoftSuppressed)
        {
            reason = "该 Skill 当前处于暂不主动使用状态";
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

    private static AgentSingingIntentRequest ExtractSingingIntentTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        text = SplitGluedAgentTags(text);
        var match = s_SingingIntentTagRegex.Match(text);
        if (!match.Success) return null;
        string attrs = match.Groups["attrs"].Value;
        var request = new AgentSingingIntentRequest
        {
            Permission = ReadToolAttribute(attrs, "permission").ToLowerInvariant(),
            Scope = ReadToolAttribute(attrs, "scope").ToLowerInvariant(),
            Actor = ReadToolAttribute(attrs, "actor").ToLowerInvariant(),
            Action = ReadToolAttribute(attrs, "action").ToLowerInvariant(),
            Order = ReadToolAttribute(attrs, "order"),
            Confidence = ReadToolFloatAttribute(attrs, "confidence", 0f, 1f),
            Evidence = ReadToolAttribute(attrs, "evidence"),
        };
        text = s_SingingIntentTagRegex.Replace(text, "").Trim();
        return request;
    }

    private static string NormalizeSingingIntentEvidence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var normalized = new System.Text.StringBuilder(text.Length);
        foreach (char ch in text)
            if (char.IsLetterOrDigit(ch))
                normalized.Append(char.ToLowerInvariant(ch));
        return normalized.ToString();
    }

    /// <summary>
    /// 这里只做协议与证据校验，不重新解释“不要唱”到底修饰能力还是某几段。
    /// evidence 必须逐字来自当前用户原话，避免模型拿自己的 reason 或历史内容改权限。
    /// </summary>
    private static bool ValidateSingingIntentFields(
        string permission,
        string scope,
        string actor,
        string action,
        string order,
        float confidence,
        string evidence,
        string freshUserText,
        out string rejectionReason)
    {
        rejectionReason = "";
        permission = (permission ?? "").Trim().ToLowerInvariant();
        scope = (scope ?? "").Trim().ToLowerInvariant();
        actor = (actor ?? "").Trim().ToLowerInvariant();
        action = (action ?? "").Trim().ToLowerInvariant();
        order = (order ?? "").Trim();

        if (permission != "no_change" && permission != "enable" &&
            permission != "disable" && permission != "soft_suppress")
        {
            rejectionReason = "permission 必须是 no_change/enable/disable/soft_suppress";
            return false;
        }
        if (scope != "none" && scope != "capability" && scope != "autonomy" &&
            scope != "content_constraint" && scope != "stop_current")
        {
            rejectionReason = "scope 不受支持";
            return false;
        }
        if (actor != "none" && actor != "user" && actor != "character")
        {
            rejectionReason = "actor 必须是 none/user/character";
            return false;
        }
        if (action != "none" && action != "echo" && action != "practice" &&
            action != "catalog" && action != "stop_current")
        {
            rejectionReason = "action 不受支持";
            return false;
        }
        if (action != "none" && actor != "character")
        {
            rejectionReason = "真实歌唱动作的 actor 必须是 character；用户说自己唱不能派发角色歌唱";
            return false;
        }
        if (float.IsNaN(confidence) || confidence < 0.75f)
        {
            rejectionReason = "confidence 低于 0.75；应先追问而不是改变权限或选择片段";
            return false;
        }

        string normalizedEvidence = NormalizeSingingIntentEvidence(evidence);
        string normalizedUser = NormalizeSingingIntentEvidence(
            StripSingingPerceptionMetadata(freshUserText));
        if (normalizedEvidence.Length < 2 || normalizedUser.Length == 0 ||
            normalizedUser.IndexOf(normalizedEvidence, StringComparison.Ordinal) < 0)
        {
            rejectionReason = "evidence 不是当前用户原话中的连续片段";
            return false;
        }

        if ((permission == "enable" || permission == "disable") && scope != "capability")
        {
            rejectionReason = "启用/禁用整项能力时 scope 必须是 capability";
            return false;
        }
        if (permission == "soft_suppress" && scope != "autonomy")
        {
            rejectionReason = "暂时克制自主行为时 scope 必须是 autonomy";
            return false;
        }
        if (scope == "content_constraint" &&
            (permission != "no_change" || action != "practice"))
        {
            rejectionReason = "片段范围限制只能是 no_change + practice";
            return false;
        }
        if (action == "practice" && string.IsNullOrWhiteSpace(order))
        {
            rejectionReason = "practice 必须显式列出 order，不能用空值默认全唱";
            return false;
        }
        if (action != "practice" && !string.IsNullOrWhiteSpace(order))
        {
            rejectionReason = "只有 practice 动作可以携带 order";
            return false;
        }
        if ((scope == "stop_current") != (action == "stop_current"))
        {
            rejectionReason = "停止当前播放必须使用 stop_current scope/action 配对";
            return false;
        }
        if ((permission == "disable" || permission == "soft_suppress") &&
            action != "none" && action != "stop_current")
        {
            rejectionReason = "停用或暂时克制不能同时发起新的歌唱动作";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 真实用户轮里，LLM 只有同时说明“谁唱”和“执行什么”才可触发可听见的歌唱。
    /// 这里不解析自然语言；语义仍由 singing Skill/LLM 判断，本地只核对结构化决定
    /// 与最终工具是否一致，防止“我唱”旁边误带了角色的 song_sing/hum_back。
    /// 自主 tick 没有新用户证据，走原有 Skill 申请/权限链，不使用本协议。
    /// </summary>
    private static bool ValidateUserSingingActionAuthorization(
        string actor,
        string action,
        bool hasSongSing,
        string humBackMode,
        out string rejectionReason)
    {
        rejectionReason = "";
        bool hasHumBack = !string.IsNullOrEmpty(humBackMode);
        if (!hasSongSing && !hasHumBack) return true;

        actor = (actor ?? "").Trim().ToLowerInvariant();
        action = (action ?? "").Trim().ToLowerInvariant();
        if (actor != "character")
        {
            rejectionReason = string.IsNullOrEmpty(actor)
                ? "真实用户触发的歌唱动作缺少 singing_intent/actor"
                : $"singing_intent 指定 actor={actor}，不能让角色代唱";
            return false;
        }
        if (hasSongSing && hasHumBack)
        {
            rejectionReason = "同一轮不能同时授权 song_sing 与 hum_back";
            return false;
        }
        if (hasSongSing && action != "catalog")
        {
            rejectionReason = $"song_sing 必须匹配 action=catalog，当前为 {action}";
            return false;
        }

        if (hasHumBack)
        {
            string mode = humBackMode.Trim().ToLowerInvariant();
            string expected = IsPracticeHumMode(mode) ? "practice" : "echo";
            if (action != expected)
            {
                rejectionReason = $"hum_back mode={mode} 必须匹配 action={expected}，当前为 {action}";
                return false;
            }
        }
        return true;
    }

    private bool TryApplySingingIntent(
        AgentSingingIntentRequest request, out string result)
    {
        result = "";
        if (request == null)
        {
            result = "缺少 singing_intent";
            return false;
        }
        if (m_AgentCurrentRoundIsTick)
        {
            result = "自主 tick 不能借用用户权限意图协议";
            return false;
        }
        if (!ValidateSingingIntentFields(
                request.Permission, request.Scope, request.Actor, request.Action, request.Order,
                request.Confidence, request.Evidence, m_LastUserMsg, out result))
            return false;

        SkillRouteDefinition definition = FindSkillDefinition("singing");
        SkillRouteState state = GetSkillRouteState("singing");
        switch (request.Permission)
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
            case "soft_suppress":
                state.Access = SkillAccess.SoftSuppressed;
                state.SoftSuppressedUntil = Time.realtimeSinceStartup +
                    (definition != null ? definition.SoftSuppressSeconds : 180f);
                state.RoundsRemaining = 0;
                state.ActiveThisRound = false;
                state.WasActive = false;
                m_ActiveSkillsThisRound.Remove("singing");
                CancelSkillRuntimeActivity("singing", "singing-intent-soft-suppressed");
                result = "singing 已暂时停止自主使用";
                break;
            default:
                result = "singing 权限保持不变";
                break;
        }

        m_LastReferencedSkillName = "singing";
        m_LastReferencedSkillAt = Time.realtimeSinceStartup;
        if (m_LogAgentLoop)
            Debug.Log($"[LLM技能/Intent] 已执行 permission={request.Permission}, " +
                      $"scope={request.Scope}, actor={request.Actor}, action={request.Action}, " +
                      $"order={request.Order}, " +
                      $"confidence={request.Confidence:F2}; evidence={request.Evidence}; {result}");
        return true;
    }

    private void ApplySingingIntentAction(
        AgentSingingIntentRequest intent,
        bool accepted,
        ref AgentSongSingRequest songSing,
        ref AgentHumBackRequest humBack)
    {
        if (!accepted || intent == null) return;
        switch (intent.Action)
        {
            case "practice":
                songSing = null;
                if (humBack == null) humBack = new AgentHumBackRequest();
                humBack.Mode = "practice";
                humBack.Order = intent.Order;
                if (string.IsNullOrWhiteSpace(humBack.Reason))
                    humBack.Reason = "按 LLM 结合完整语境选定的练唱片段执行";
                break;
            case "echo":
                songSing = null;
                if (humBack == null) humBack = new AgentHumBackRequest();
                humBack.Mode = "echo";
                if (string.IsNullOrWhiteSpace(humBack.Reason))
                    humBack.Reason = "按 LLM 结合完整语境确认回唱最近旋律";
                break;
            case "stop_current":
                songSing = null;
                humBack = null;
                CancelSkillRuntimeActivity("singing", "singing-intent-stop-current");
                break;
        }
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

    private bool TryApproveAutonomousSkillRequest(
        AgentSkillRequest request, out string rejectionReason)
    {
        rejectionReason = "";
        if (request == null) return false;
        SkillRouteDefinition definition = FindSkillDefinition(request.Name);
        if (definition == null)
        {
            rejectionReason = $"不存在 Skill '{request.Name}'";
            return false;
        }
        if (!m_AgentRunning || !definition.AllowsAutonomousRequest)
        {
            rejectionReason = "当前模式不允许角色自主申请该 Skill";
            return false;
        }
        SkillRouteState state = GetSkillRouteState(definition.Name);
        if (state.Access == SkillAccess.UserDisabled)
        {
            rejectionReason = "用户已明确禁用，角色不能自行恢复";
            return false;
        }
        if (state.Access == SkillAccess.SoftSuppressed)
        {
            rejectionReason = "当前处于暂不主动使用状态";
            return false;
        }
        if (state.ActiveThisRound)
        {
            rejectionReason = "详细 Skill 本轮已经加载，无需再次申请";
            return false;
        }
        float elapsed = Time.realtimeSinceStartup - state.LastAutonomousGrantAt;
        if (elapsed < definition.AutonomousCooldownSeconds)
        {
            rejectionReason =
                $"自主申请仍在冷却中（剩余约 {definition.AutonomousCooldownSeconds - elapsed:F0}s）";
            return false;
        }
        if (!string.IsNullOrEmpty(m_PendingAutonomousSkillName))
        {
            rejectionReason = "已有另一个 Skill 申请等待进入下一轮";
            return false;
        }

        state.LastAutonomousGrantAt = Time.realtimeSinceStartup;
        m_PendingAutonomousSkillName = definition.Name;
        m_PendingAutonomousSkillReason = string.IsNullOrWhiteSpace(request.Reason)
            ? "角色产生了自主使用动机"
            : request.Reason.Trim();
        if (m_LogAgentLoop)
            Debug.Log($"[LLM技能] {definition.Name} 自主申请获准：" +
                      m_PendingAutonomousSkillReason);
        return true;
    }

    private void PrepareActiveSkillsForRound(string freshUserText, string triggerReason)
    {
        m_AuthorizedSkillControlNameThisRound = "";
        m_AuthorizedSkillControlActionThisRound = "";
        if (m_ChatSettings == null || m_ChatSettings.m_ChatModel == null) return;

        m_ActiveSkillsThisRound.Clear();
        var activeSkills = new List<string>(s_SkillRouteDefinitions.Length);
        float now = Time.realtimeSinceStartup;
        bool isUserTurn = string.Equals(triggerReason, "user-spoke", StringComparison.Ordinal);
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

            bool strongSignal = false;
            string transitionReason = "（短期追问窗口）";
            bool semanticInspection = intent == SkillUserIntent.Mention;
            if (semanticInspection)
            {
                strongSignal = state.Access == SkillAccess.Available;
                transitionReason = state.Access == SkillAccess.Available
                    ? "（本轮语境相关；权限方向交给 LLM）"
                    : "（仅为判定权限语境临时加载）";
            }

            if (intent == SkillUserIntent.None)
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
            //Access；只有结构化意图明确 enable 后，同一回复里的动作才会放行。
            bool interpretationOnly = semanticInspection && state.Access != SkillAccess.Available;
            bool active = interpretationOnly || (state.Access == SkillAccess.Available &&
                (strongSignal || state.RoundsRemaining > 0));
            if (!strongSignal && state.RoundsRemaining > 0)
                state.RoundsRemaining--;
            if (active)
            {
                activeSkills.Add(definition.Name);
                m_ActiveSkillsThisRound.Add(definition.Name);
            }
            state.ActiveThisRound = active;

            if (m_LogAgentLoop && active != state.WasActive)
                Debug.Log($"[LLM技能] {definition.Name} {(active ? "已加载" : "已卸载")}" +
                          transitionReason);
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
    private bool m_ExplicitSongRememberHandled = false;
    private Coroutine m_SongMemoryAcknowledgementCoroutine;
    private bool m_SongMemoryAcknowledgementInFlight = false;
    private bool m_SongSingInFlight = false;
    private int m_SongSingGeneration = 0;
    private bool m_HumBackPending = false;
    private bool m_HumBackPreparingCarrier = false;
    private bool m_HumBackPlaying = false;
    private int m_HumBackGeneration = 0;
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
    private Coroutine m_HumBackPlaybackCoroutine;
    private sealed class HumStreamReadyClip
    {
        public AudioClip Clip;
        public float GapBefore;
        public int SegmentNumber;
    }
    private sealed class HumStreamScheduledClip
    {
        public AudioClip Clip;
        public AudioSource Source;
        public double EndDspTime;
        public int SegmentNumber;
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
    private bool m_HumBackNeedsHistoryEntry = false;
    private bool m_ExplicitHumBackHandled = false;
    private bool m_WaitingForRequestedSingAlong = false;
    private float m_SingAlongRequestArmedAt = -999f;
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

    private string m_StickyToolFailure = "";
    private int m_StickyToolFailureCount = 0;
    private int m_StickyToolFailureShown = 0;
    private const int k_StickyToolFailureMaxFrames = 4;
    private string m_LastHumBackResult = "";
    private bool m_AgentGracefulShutdownPending = false;
    // 本轮从 LLM 回复里解析出来的标签——OnStreamComplete 写、收尾时读
    private float? m_RoundNextInSec = null;
    private string m_RoundFocus = null;
    private bool m_RoundContinue = false;
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
        m_AgentGracefulShutdownPending = false;
        m_AgentRunning = true;
        m_AgentSessionStartTime = Time.realtimeSinceStartup;
        m_LastUserTurnTime = -1f;
        m_LastAITurnTime = -1f;
        m_LastUserMsg = "";
        m_LastAIMsgPlain = "";
        m_RecentAIUtterances.Clear();
        m_RepeatWatch.Clear();
        m_SelfRepeatNote = "";
        //台账和 id 账本**不清**——它们记的是"这台机器上这一场里发生过什么"，
        //和 Loop 的生死无关。练唱会话跨重启保留，这两样同理。

        m_LastFocus = "";
        m_ConsecutiveAITurns = 0;
        m_AutonomyProbeGeneration++;
        m_AutonomyProbeInFlight = false;
        m_AutonomyProbeNotBefore = -999f;
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
        m_SongMemoryGeneration++;
        m_SongMemoryInFlight = false;
        m_SongMemoryResultPending = false;
        m_SongMemoryAcknowledgementRequired = false;
        m_LastSongMemoryResult = "";
        m_LastSongMemoryRequestTime = -999f;
        m_LastSongMemorySignature = "";
        m_ExplicitSongRememberHandled = false;
        m_ExplicitHumBackHandled = false;
        m_WaitingForRequestedSingAlong = false;
        m_SingAlongRequestArmedAt = -999f;
        m_HumBackResultPending = false;
        m_LastHumBackResult = "";
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
                m_SongMemoryAcknowledgementCoroutine != null ||
                m_SongMemoryAcknowledgementInFlight || m_HumBackPending ||
                m_HumBackPreparingCarrier || m_HumBackPlaying;
        }
    }

    public void StopAgentLoop()
    {
        ResetSpeculativeTurn();
        m_AgentGracefulShutdownPending = false;
        if (!m_AgentRunning) return;
        m_AgentRunning = false;
        m_SongSearchGeneration++;
        m_SongSearchInFlight = false;
        m_SongSearchResultPending = false;
        m_SongMemoryGeneration++;
        m_SongMemoryInFlight = false;
        m_SongMemoryResultPending = false;
        m_SongMemoryAcknowledgementRequired = false;
        CancelAutonomyIntentProbe();
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
    /// RTSpeechHandler 在 StartRecording 时调用——用户开口意味着 AI 连续轮次清零，
    /// 待 tick 撤销(用户的话本身就会触发新一轮 LLM 调用)。
    /// </summary>
    public void NotifyUserStartedSpeaking()
    {
        //StartRecording 只会在神经VAD确认真人后调用。若此刻仍有角色语音，按真正的
        //barge-in 保留已听到部分；若尚未出声，则直接废弃旧请求和待播队列。
        if (IsAISpeaking || IsVoiceOutputPlaying) Interrupt();
        else CancelUnheardResponseForUserSpeech();
        //上面两条分支主要服务普通 TTS，未来状态继续增加时可能有某种歌唱工作
        //没有被其 hasResponseWork 覆盖。用户一开口是硬边界：再用歌唱任务自己的
        //完整 hadWork 判据扫一次，确保上一轮的曲库演唱定位、生成、流式队列和播放都不能越界。
        CancelPendingHumBack("user-started-speaking-safety", true);
        CancelAutonomyIntentProbe();
        ResetStreamingHumBackPrefix("new-user-turn", true);
        ResetSpeculativeTurn();
        m_EouTurnWasSinging = false;
        m_EouSingingRejectedByFinal = false;
        m_EouCognitiveSpeechVeto = false;
        m_EouCognitiveSingingSupport = false;
        m_FinalModeVerdict = "";
        m_FinalModeSoftDowngrade = false;
        m_EouFillerContext = "neutral";
        if (m_PendingTickCo != null)
        {
            StopCoroutine(m_PendingTickCo);
            m_PendingTickCo = null;
        }
        m_ConsecutiveAITurns = 0;
        m_SpikePulledForwardThisSilence = false;   //新一段沉默重新允许被拽回一次
        m_AutonomyProbeNotBefore = -999f;
        m_ScheduledWakeReason = "";
        if (m_Urge != null) m_Urge.AbsorbUserUtterance(Time.realtimeSinceStartup);
        if (m_LogAgentLoop) Debug.Log("[Agent] 用户开口 → 待 tick 撤销, 连续 AI 轮次清零");
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
    /// 她正在说话/轮次在飞时不推进：那时冲动本来就在被消耗。
    /// </summary>
    private void StepUrge()
    {
        if (m_Urge == null || !m_Urge.Enabled) return;
        if (!m_AgentRunning || m_AgentGracefulShutdownPending) return;
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
        if (m_UserTurnAwaitingReplySince > 0f)
        {
            //兜底分两档。回哼含 SVC 转换实测 30~50 秒，那期间必须能等；但普通轮
            //没有这个开销，万一还有没覆盖到的收尾路径，也不该让她哑上一分多钟。
            bool humBackBusy = m_HumBackPending || m_HumBackPreparingCarrier ||
                m_HumBackPlaying || m_FastHumBackActive;
            float cap = humBackBusy
                ? k_UserTurnAwaitingReplyMaxSeconds
                : k_UserTurnAwaitingReplyPlainCapSeconds;
            if (Time.realtimeSinceStartup - m_UserTurnAwaitingReplySince < cap) return;
            ClearUserTurnAwaitingReply($"等待超时({cap:F0}s)");
        }
        // 用户还在说话时不许自主开口。
        //
        // 这道闸原来只挡"她自己在说"，不挡"用户正在说"。而感知帧里的"距用户上句"
        // 是从**上一轮提交**算起的——一轮【说话+唱歌】要二三十秒才提交，于是 8/11
        // 实测出现：用户唱到一半，感知帧写着「距用户上句: 51秒」，时钟冲动到点，
        // 她拿上一轮的上下文开口说了「あら、またそのフレーズ？」，随后才被
        // 「用户录音期间检测到旧AI开始发声」事后打断——声音已经放出去了，
        // 紧接着回哼开始，听感非常突兀。
        //
        // 流式 partial 每来一帧就刷新一次时间戳，所以"最近还在收 partial"就等于
        // "用户还在说"。比接一条录音状态过来简单，也不依赖 RTSpeechHandler 的内部状态。
        // 和上面几条一样直接 return、不推进冲动——沿用这个函数注释里定下的规则
        // 「她正在说话/轮次在飞时不推进」。若在这里调 Step 又丢弃返回值，点火会被
        // 白白消耗掉。
        if (Time.realtimeSinceStartup - m_LastStreamingPartialRealtime
            < k_UserSpeakingHoldSeconds)
            return;

        //待机漂移点火 = 她忽然想起了什么，按事件注入
        if (m_MemoryHub != null && m_EnableMemoryRecall)
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
        if (!m_AutonomyProbeInFlight) return;
        m_AutonomyProbeInFlight = false;
        if (m_ChatSettings != null && m_ChatSettings.m_ChatModel != null)
            m_ChatSettings.m_ChatModel.CancelEphemeralMsg();
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
        return reason == "session-start" || reason == "shutdown-cancelled" ||
            reason == "song-search-result" || reason == "song-memory-result" ||
            reason == "song-singing-result" || reason == "speaker-manage-result" ||
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
        return
            "[内部自主意图预判；不要把本条写成正式回复，不要调用工具，不要输出控制标签]\n" +
            "现在没有新的用户发言。系统只是给你一次自主行动机会，触发原因=" +
            (triggerReason ?? "scheduled") + "。\n" +
            "先判断此刻是否真的出现了一个和你最近发言不同、值得开启正式轮次的新意图。" +
            "新意图可以来自你自己的新想法、刚浮现的记忆、环境变化、时间流逝后自然想换的话题，" +
            "也可以是只在心里完成的观察或记忆整理；不要求一定说出口。\n" +
            "下面这些不算新意图：重说或改写上一句话、再次回答已经回答过的用户发言、" +
            "重复表示正在等待、重复追问用户尚未回答的问题、为用户没有听见的内部复读道歉。\n" +
            "proceed=true 只表示值得让正式角色再判断一次说话/沉默/动作，不表示必须开口。" +
            "如果没有新东西，proceed=false，并给出下一次重新考虑的秒数。\n\n" +
            frame + "\n\n" +
            "只输出一个紧凑 JSON，不要 Markdown：" +
            "{\"proceed\":false,\"intent\":\"想做什么；没有则留空\"," +
            "\"novelty\":\"它相对最近发言的新信息；没有则留空\"," +
            "\"wait_seconds\":45}";
    }

    private void DispatchPreparedAgentTick(
        string triggerReason,
        string frame,
        AutonomyIntentDecision decision = null)
    {
        if (!m_AgentRunning || m_AgentGracefulShutdownPending) return;
        if (IsAISpeaking || m_AgentRoundInFlight || IsVoiceOutputPlaying) return;

        if (decision != null)
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

        HarvestSongIds(frame);
        m_ChatHistory.Add(frame);
        m_AgentRoundInFlight = true;
        m_AgentCurrentRoundIsTick = true;
        ClearRoundParsed();

        if (m_LogAgentLoop) Debug.Log("[Agent] FireTick → " + frame.Replace('\n', ' '));

        m_TextBack.text = "";
        StartStreaming(frame, false);
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
        m_AutonomyProbeInFlight = true;
        string prompt = BuildAutonomyIntentProbePrompt(triggerReason, frame);
        if (m_LogAgentLoop)
            Debug.Log($"[Agent/自主意图] 开始预判 trigger={triggerReason}");

        m_ChatSettings.m_ChatModel.PostEphemeralMsg(prompt, response =>
        {
            if (generation != m_AutonomyProbeGeneration) return;
            m_AutonomyProbeInFlight = false;
            if (!m_AgentRunning || m_AgentGracefulShutdownPending ||
                m_AgentRoundInFlight || IsAISpeaking || IsVoiceOutputPlaying)
                return;

            if (!TryParseAutonomyIntentDecision(response, out AutonomyIntentDecision decision))
            {
                if (m_LogAgentLoop)
                    Debug.LogWarning("[Agent/自主意图] 预判输出无法解析，保留角色自主性并放行正式轮次");
                PrepareActiveSkillsForRound(null, triggerReason);
                DispatchPreparedAgentTick(triggerReason, frame);
                return;
            }

            if (decision.proceed)
            {
                m_AutonomyProbeNotBefore = -999f;
                if (m_LogAgentLoop)
                    Debug.Log($"[Agent/自主意图] 放行：{TruncateForFrame(decision.intent, 120)} " +
                              $"(新意={TruncateForFrame(decision.novelty, 120)})");
                PrepareActiveSkillsForRound(null, triggerReason);
                DispatchPreparedAgentTick(triggerReason, frame, decision);
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
                Debug.Log($"[Agent/自主意图] 没有新表达动机，保持安静；{wait:F0}s 后再考虑");
            ScheduleNextTick(wait, "autonomy-no-new-intent");
        });
    }

    /// <summary>
    /// 真正发起一次 tick：构造感知帧 → 投给 LLM 流式管线 →
    /// 沿现有 Stream 通路走 TTS，OnStreamComplete 解析尾部标签。
    /// triggerReason 透到感知帧里告诉 LLM 这一帧是怎么来的。
    /// </summary>
    private void FireTick(string triggerReason)
    {
        if (!m_AgentRunning || m_AgentGracefulShutdownPending) return;
        if (m_AutonomyProbeInFlight) return;
        //角色还在说话(<continue/>链上一帧还没收尾) → 让流水线走完再排
        if (IsAISpeaking || m_AgentRoundInFlight)
        {
            if (m_LogAgentLoop) Debug.Log($"[Agent] FireTick 排队等待(speaking={IsAISpeaking}, inflight={m_AgentRoundInFlight})");
            ScheduleNextTick(m_MinTickSec, "still-busy");
            return;
        }
        //连续 AI 轮次上限——工具结果仍允许回到角色手里一次，否则可能“查到了但不说”
        bool isSongToolResult = string.Equals(triggerReason, "song-search-result", StringComparison.Ordinal) ||
            string.Equals(triggerReason, "song-memory-result", StringComparison.Ordinal) ||
            string.Equals(triggerReason, "speaker-manage-result", StringComparison.Ordinal);
        //冲动模型接手后，"没人理"由疲劳表达(间隔逐次拉长，最终被 m_MaxTickSec 夹住)，
        //而不是撞线就彻底闭嘴。原来那个断崖的问题是：撞线后 FireTick 直接 return 且不再排
        //下一次，于是必须等用户开口才解封——用户走开 30 分钟，她后 22 分钟一声不吭。
        //这里只保留一个远得多的兜底，防冲动模型出 bug 时无限独白。
        int cap = (m_Urge != null && m_Urge.Enabled) ? m_MonologueBackstopTurns : m_MaxConsecutiveAITurns;
        if (m_ConsecutiveAITurns >= cap && !isSongToolResult)
        {
            if (m_LogAgentLoop) Debug.Log($"[Agent] 连续 AI 轮次={m_ConsecutiveAITurns}≥{cap}，停止主动tick，等用户开口");
            return;
        }

        if (m_EnableAutonomyIntentProbe && !ShouldBypassAutonomyIntentProbe(triggerReason))
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
    public string PrepareUserTurn(string userText)
    {
        m_LastUserTurnTime = Time.realtimeSinceStartup;
        m_LastUserMsg = userText ?? "";
        m_ExplicitSongRememberHandled = false;
        m_ExplicitHumBackHandled = false;
        //情境召回:提及扫描同步生效(本帧可见),语境嵌入异步、作用于后续帧
        if (m_MemoryHub != null && m_EnableMemoryRecall)
            m_MemoryHub.NotifyUserUtterance(m_LastUserMsg);
        m_AgentRoundInFlight = m_AgentRunning;
        m_AgentCurrentRoundIsTick = false;
        ClearRoundParsed();

        //agent 没启动就不拼帧，保持向后兼容
        if (!m_AgentRunning) return userText ?? "";

        string frame = BuildPerceptionFrame("user-spoke");
        //用户那一句里带着 ASR 的「曲库里旋律接近的」候选行，完整 id 就在那儿——
        //她能看到的 id 都要入账，否则出处校验会把合法的调用也拦掉。
        string combined = frame + "\n" + (userText ?? "");
        HarvestSongIds(combined);
        return combined;
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

        if (m_AgentGracefulShutdownPending)
        {
            ClearRoundParsed();
            if (m_LogAgentLoop) Debug.Log("[Agent] 当前轮已收尾；优雅关闭期间不再安排新tick");
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
        if (m_SongSearchResultPending && m_BringForwardOnSongSearchResult)
        {
            ScheduleNextTick(m_MinTickSec, "song-search-result");
        }
        else if (m_HumBackResultPending && m_BringForwardOnSongSearchResult)
        {
            ScheduleNextTick(m_MinTickSec, "song-singing-result");
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

        if (!string.IsNullOrEmpty(m_PendingSkillStatusFrame))
        {
            sb.Append(m_PendingSkillStatusFrame);
            m_PendingSkillStatusFrame = "";
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
            //必须先剥掉感知前缀再截断。m_LastUserMsg 开头是
            //「[说话人:主人; speaker_id:owner; 类型:owner; 可信度:0.78] [演唱片段; …]」，
            //40 字的预算全被前缀吃掉——日志里这一栏长期是
            //`("[说话人:主人; speaker_id:owner; 类型…")`，她连用户刚说了什么都看不到，
            //自主发言时只能顺着自己上一句往下说，听感就是"慢一拍"。
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
            m_SongSearchResultPending = false;
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
                m_DroppedSongTitleNote = "";
            }
            m_SongMemoryResultPending = false;
        }
        if (m_SpeakerManageInFlight)
        {
            sb.Append("\n声纹管理工具: 正在读取或修改本机声纹仓库；结果返回前不要声称操作成功");
        }
        if (m_SpeakerManageResultPending && !string.IsNullOrEmpty(m_LastSpeakerManageResult))
        {
            sb.Append("\n声纹管理工具结果:\n");
            sb.Append(m_LastSpeakerManageResult);
            m_SpeakerManageResultPending = false;
        }
        if (m_SongSingInFlight)
        {
            sb.Append("\n长期歌曲演唱工具: 正在从本地曲库定位真实音频；不要声称已经唱出或续唱成功");
        }
        //上一次连唱的各段起调：在下一次连唱替换它之前一直有效，用户随时可能问起。
        if (!string.IsNullOrEmpty(m_LastRenderPitchSummary) &&
            Time.realtimeSinceStartup - m_LastRenderPitchAt < k_RenderPitchStickySeconds)
        {
            sb.Append("\n上次连唱各段起调: ").Append(m_LastRenderPitchSummary)
              .Append("（用户问「刚才那几段是什么调」时直接照这个答，不用再去看屏幕或猜）");
        }
        //失败要持续可见，而不是只闪一帧。次数是她最缺的那个事实。
        if (!string.IsNullOrEmpty(m_StickyToolFailure) &&
            m_StickyToolFailureShown < k_StickyToolFailureMaxFrames)
        {
            m_StickyToolFailureShown++;
            sb.Append("\n⚠ 工具还没有成功过");
            if (m_StickyToolFailureCount > 1)
                sb.Append($"（同样的调用已经连续失败 {m_StickyToolFailureCount} 次）");
            sb.Append("：").Append(m_StickyToolFailure);
            if (m_StickyToolFailureCount > 1)
                sb.Append(" 重复同一个调用不会有不同结果。换一种做法，"
                          + "或者如实告诉用户这件事你现在做不到。");
        }
        if (m_PracticeDropResultPending && !string.IsNullOrEmpty(m_LastPracticeDropResult))
        {
            sb.Append("\n练唱会话删除工具结果: ");
            sb.Append(m_LastPracticeDropResult);
            m_PracticeDropResultPending = false;
        }
        if (m_HumBackResultPending && !string.IsNullOrEmpty(m_LastHumBackResult))
        {
            sb.Append("\n旋律回哼工具结果: ");
            sb.Append(m_LastHumBackResult);
            m_HumBackResultPending = false;
        }
        if (!string.IsNullOrEmpty(m_LastUnknownTagNote))
        {
            sb.Append("\n上一轮你写了 " + m_LastUnknownTagNote +
                      " —— 没有这个标签，它被整条丢掉了，所以那一步**没有发生**。" +
                      "排下一拍用 <next in=\"Ns\" focus=\"…\"/>；标签名只能用规范里列出的那些，" +
                      "拼错不会有任何报错。如果那一轮你还打算唱/查/记，现在补上对应的标签。");
            m_LastUnknownTagNote = "";
        }
        SenseVoiceSpeechToText practiceSenseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        sb.Append(BuildSessionSongLedgerClause());
        if (!string.IsNullOrEmpty(m_SelfRepeatNote))
        {
            sb.Append("\n复读拦截: " + m_SelfRepeatNote);
            m_SelfRepeatNote = "";
        }
        //必须排在清单前面：清单为空时它是**唯一**的信号。8/25 那次会话被清空后，
        //帧里干脆什么都不写，她只能相信自己笔记里的"第5段"，于是照着撞了两次。
        if (!string.IsNullOrEmpty(m_PracticeSessionCarryNote))
        {
            sb.Append(m_PracticeSessionCarryNote);
            m_PracticeSessionCarryNote = "";
        }
        if (practiceSenseVoice != null && practiceSenseVoice.PracticePhraseCount > 0)
        {
            //必须逐段列出身份。只报一个总数时她无从分辨哪段是哪段，用户说
            //「先唱沉默着走了那段」她就翻译不成段号——8/16 实测因此空转七轮。
            //光有歌词还不够：那一场 7 段跨了两首歌、跨了好几轮，她连着三次选错段。
            //所以要带上"属于哪首歌"和"多久以前唱的"，这两条正是用户区分它们的方式。
            var phrases = practiceSenseVoice.DescribePracticePhrases();
            float totalSeconds = 0f;
            sb.Append($"\n练唱会话: 已按练唱先后记录 {phrases.Count} 段：");
            foreach (var phrase in phrases)
            {
                totalSeconds += phrase.Seconds;
                string lyric = (phrase.Lyrics ?? "").Trim();
                if (lyric.Length > 20) lyric = lyric.Substring(0, 20) + "…";
                sb.Append($" [{phrase.Index}] {(lyric.Length > 0 ? "\"" + lyric + "\"" : "（无歌词）")}" +
                          $" {phrase.Seconds:F1}s {FormatDuration(phrase.AgoSeconds)}前");
                //同一句被教了好几遍时必须标出来。不标的话清单里两段歌词一模一样，
                //用户说"第二次教你的那段"她对不上段号，只能把两遍都唱出去。
                if (phrase.TakeTotal > 1)
                    sb.Append($" ←同一句的第{phrase.TakeIndex}/{phrase.TakeTotal}遍");
                //待确认不拦任何用途，它只是个警示：这一段有可能根本不是歌声。
                //必须显出来——不显的话用户说"那不是唱歌"时，她无从知道该去掉哪一段。
                if (phrase.PendingConfirmation)
                    sb.Append(" ←待确认(声学判唱、转写读起来像说话；可以照常唱，" +
                              "用户若说那不是唱歌就 <practice_drop order=\"" +
                              phrase.Index + "\"/>)");
                //各段起调不一定一致，连起来听会觉得"某一段偏低"。她看不到就只能猜——
                //8/20 实测她猜"升八度"并写了 key="2"，三段被一起抬高 2 个半音，
                //段间差原封不动，用户连问三轮都听不出变化。
                //只摆绝对值，不写"比其它段低几个半音"：后者要先认定某几段是共同基调，
                //而用户唱另一首歌、或者每段起调本来就不同时，这个认定是凭空造的。
                //以哪一段为准由用户指定——8/20 他自己说的就是"以第一段为音调基础"。
                if (!string.IsNullOrEmpty(phrase.PitchBaseNote))
                    sb.Append($" 起调{phrase.PitchBaseNote}");
                if (!string.IsNullOrEmpty(phrase.SongName))
                    sb.Append($" 疑似《{phrase.SongName}》");
                else if (!string.IsNullOrEmpty(phrase.SongId))
                    //必须给完整 id。原来这里截到 6 位，而候选行给的是完整 id——
                    //8/22 实测她把显示用的 7a69c7 当成真 id 填进 <song_sing/>，
                    //连打八次全部 'remembered song not found'，而 7a69c7763f6f 就在曲库里。
                    sb.Append($" 疑似曲库 id={phrase.SongId}");
                //语言是最硬的换歌信号：人一般不会把中文歌和日文歌混在一条里唱。
                //8/22 实测她把中文《忘记时间》塞进 One Last Kiss 两次，用户纠正两次。
                if (!string.IsNullOrEmpty(phrase.Language))
                    sb.Append($" {phrase.Language}");
                //唱这一段之前用户说的话——换歌和重唱的意图就在这句里，而清单原本
                //只有声学与歌词事实：「第2/2遍」说明是重复，说不出为什么重复。
                if (!string.IsNullOrEmpty(phrase.PrecedingSpeech))
                    sb.Append($" ←唱这段前用户说：\"{TruncateForFrame(phrase.PrecedingSpeech, 40)}\"");
            }
            //会话里混着两首歌、或者同一句有多遍时，"默认顺序"一定是错的。
            //把这件事在清单里说破，而不是等她合完了再在工具结果里补一句。
            string mixNote = BuildPracticeAmbiguityNote(phrases);
            if (!string.IsNullOrEmpty(mixNote)) sb.Append(mixNote);
            //显式点歌没有整曲硬上限；自主发起仍应克制，避免角色未经同意占用几分钟。
            sb.Append($"；全部连起来约 {totalSeconds:F0}s。用户明确要求完整演唱时没有总时长上限，" +
                      $"系统会按约 {HumStreamChunkSeconds:F0}s 的自然块边生成边播放" +
                      $"（单块保护上限 {m_HumBackMaxSeconds:F0}s）；" +
                      $"你自主发起时总长软限制为 {m_AutonomousSingingMaxSeconds:F0}s，超出就用 order 选一小段。" +
                      "mode=practice 会保留全部片段与先后，不会为了时长裁掉歌曲开头。" +
                      "默认按上面的先后；用 order 选段与定序（如 order=\"2,1\" 先唱第2段再第1段，" +
                      "order=\"5\" 只唱第5段）。" +
                      "「疑似」只是旋律线索，最终按歌词自己判断哪几段属于同一首");
        }

        //视觉状态——告诉 LLM 自己的眼睛现在开着还是闭着
        if (m_EnableScreenVision)
        {
            sb.Append(m_AgentEyesOpen
                ? "\n视觉: 睁眼(本帧附了屏幕截图，你看得到)"
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
        if (sec < 1f) return "刚刚";
        if (sec < 60f) return $"{sec:F0}秒";
        if (sec < 3600f) return $"{sec / 60f:F0}分{(sec % 60f):F0}秒";
        return $"{sec / 3600f:F0}小时{((sec % 3600f) / 60f):F0}分";
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
        if (!m_AgentRunning || !m_EnableScreenVision || !m_AgentEyesOpen) return null;
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

    private class AgentSongMemoryRequest
    {
        public string Action = "";
        public string SongId = "";
        public string Title = "";
        public string Artist = "";
        public string Lyrics = "";
        public string Aliases = "";
        public string Reason = "";
    }

    private bool ShouldHoldSpeechForExplicitSongRemember()
    {
        //只要用户明确要求保存歌声，就扣住模型第一阶段回复；是否真的有可保存音频，
        //交给工具返回成功/失败。这样即使音频已过期，也不会先说“已经记住”。
        return CanExecuteSkillAction("singing", out _) &&
            !m_AgentCurrentRoundIsTick && IsExplicitSongRememberRequest(m_LastUserMsg);
    }

    private static bool IsExplicitSongRememberRequest(string utterance)
    {
        string text = utterance ?? "";
        string lower = text.ToLowerInvariant();

        string[] negative =
        {
            "不要记", "别记", "不用记", "不需要记", "不要保存", "别保存", "忘掉",
            "don't remember", "do not remember", "don't save", "do not save", "forget this",
            "覚えない", "覚えなく", "記録しない", "保存しない"
        };
        foreach (string phrase in negative)
            if (lower.Contains(phrase)) return false;

        string[] explicitRequests =
        {
            "记住这", "记住我", "希望你记住", "帮我记住", "记下这", "保存这", "存下这",
            "把这段记", "把我唱", "以后听到要认", "能记住", "可以记住", "要记住",
            "想让你记住", "请记住", "给我记住", "能保存", "可以保存",
            "remember this", "remember my singing", "save this", "save my singing", "keep this melody",
            "覚えて", "記録して", "保存して"
        };
        foreach (string phrase in explicitRequests)
        {
            if (!lower.Contains(phrase)) continue;
            //没有本轮演唱标签时，还必须明确指代歌声/歌曲/旋律，防止把“记住这件事”
            //误判成歌曲落盘请求。
            bool singingTurn = text.IndexOf("[演唱片段", StringComparison.Ordinal) >= 0;
            bool songReference = lower.Contains("歌") || lower.Contains("唱") || lower.Contains("哼") ||
                lower.Contains("旋律") || lower.Contains("曲") || lower.Contains("melody") ||
                lower.Contains("song") || lower.Contains("sing");
            return singingTurn || songReference;
        }
        return false;
    }

    /// <summary>
    /// 只兜底“语音服务仍持有最近歌声 + 用户明确要求保存这首歌/这段旋律”的窄场景。
    /// 用户可以先唱完、下一句话再命名并要求保存；没有明确请求的随口哼唱仍交给角色判断。
    /// </summary>
    private bool ShouldFallbackToExplicitSongRemember()
    {
        if (!m_EnableAutonomousSongMemory || !m_EnforceExplicitSongRemember ||
            m_ExplicitSongRememberHandled || m_AgentCurrentRoundIsTick)
            return false;

        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (senseVoice == null || !senseVoice.HasFreshSingingAudio()) return false;

        return IsExplicitSongRememberRequest(m_LastUserMsg);
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

    /// <summary>
    /// song_sing 只面向长期曲库；order 和“刚录的几段/练习片段”只属于本轮
    /// practice 会话。模型偶尔会把两套协议拼在一起，这里不再只丢标签，而是把
    /// 无歧义的练唱意图改走 hum_back，并保留它写出的段序。
    /// </summary>
    private bool TryRerouteSongSingToPractice(
        ref AgentSongSingRequest songSing,
        ref AgentHumBackRequest humBack)
    {
        if (songSing == null || !IsSongSingPracticeIntent(
                m_LastUserMsg, songSing.Reason, songSing.Order))
            return false;

        if (humBack == null)
        {
            humBack = new AgentHumBackRequest
            {
                Mode = "practice",
                Order = songSing.Order ?? "",
                Reason = string.IsNullOrWhiteSpace(songSing.Reason)
                    ? "曲库标签实际指向本轮练唱片段，已确定性重路由"
                    : songSing.Reason,
            };
        }
        else
        {
            humBack.Mode = "practice";
            if (string.IsNullOrWhiteSpace(humBack.Order))
                humBack.Order = songSing.Order ?? "";
        }
        songSing = null;
        m_ExplicitHumBackHandled = true;
        return true;
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
        return !string.IsNullOrEmpty(m_LastUserMsg) &&
            m_LastUserMsg.IndexOf("[混合歌唱转说话", StringComparison.Ordinal) >= 0;
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

    private bool IsCurrentTurnConfirmedSinging()
    {
        return !string.IsNullOrEmpty(m_LastUserMsg) &&
            m_LastUserMsg.IndexOf("[演唱片段", StringComparison.Ordinal) >= 0;
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
        m_ExplicitHumBackHandled = true;
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
    /// A real singing request must not stream ordinary TTS lyrics before the model's tool tag
    /// arrives. Hold the textual body only when a playable performance already exists, or when
    /// an armed invitation has just been fulfilled by a confirmed singing turn.
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
        bool directPerformance = IsExplicitHumBackRequest(m_LastUserMsg);
        return (armedPerformance || directPerformance) && HasRecentPlayableSingingPerformance();
    }

    /// <summary>
    /// 用户明确要求回哼时，即使本地模型漏写工具标签也执行；自主回哼仍完全交给模型判断。
    /// 这里只确认语义，最近是否还有可演奏旋律由 QueueHumBack 做最终检查。
    /// </summary>
    private bool ShouldFallbackToExplicitHumBack()
    {
        if (!CanExecuteSkillAction("singing", out _) ||
            !m_EnableAutonomousHumBack || m_ExplicitHumBackHandled ||
            m_AgentCurrentRoundIsTick)
            return false;

        if (IsCurrentTurnSpokenSingingExit())
        {
            m_ExplicitHumBackHandled = true;
            if (m_LogHumBack)
                Debug.Log("[HumBack] 本轮歌唱后明确转为口语退出；不执行确定性跟唱兜底");
            return false;
        }

        bool confirmedSinging = IsCurrentTurnConfirmedSinging();
        //多段范围与顺序交给 singing Skill 的结构化语义，不再在模型漏标时
        //用 order="" 兜底成“全部”。这类轮次也不预先扣留正常回答。
        if (!confirmedSinging && IsPracticeCompositionRequest(m_LastUserMsg))
            return false;
        if (!confirmedSinging && IsHumBackCancellation(m_LastUserMsg))
        {
            m_WaitingForRequestedSingAlong = false;
            m_ExplicitHumBackHandled = true;
            if (m_LogHumBack) Debug.Log("[HumBack] 用户取消了待唱请求");
            return false;
        }
        if (!confirmedSinging && IsSingAlongInvitation(m_LastUserMsg))
        {
            // “能不能跟着我唱”是在约定下一步；不能把这句普通说话本身拿去变声。
            ArmSingAlongForNextPerformance();
            return false;
        }

        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        float[] timeline;
        float frameSeconds;
        string language;
        bool hasPlayablePerformance = senseVoice != null &&
            senseVoice.TryGetRecentSingingPerformance(out timeline, out frameSeconds, out language);
        if (confirmedSinging && HasActiveSingAlongRequest())
        {
            if (hasPlayablePerformance) RefreshActiveSingAlongSession();
            return hasPlayablePerformance;
        }
        if (!IsExplicitHumBackRequest(m_LastUserMsg)) return false;
        return hasPlayablePerformance;
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

    private class AgentHumBackRequest
    {
        public string Mode = "echo";
        public string Lyrics = "";
        public string Reason = "";
        //演唱参数：她可以自己决定这一遍怎么唱。NaN/未指定时沿用按 seed 生成的默认档，
        //所以不写这些属性时行为和以前完全一样。
        public float Key = float.NaN;         //移调，半音
        public float Pace = float.NaN;        //速度倍率
        public float Expression = float.NaN;  //演绎强度：0=逐帧复刻用户，1=尽量按她自己的表现
        //practice 的演唱顺序，如 "2,1"；空串按练唱先后。段号见感知帧里的练唱会话清单。
        public string Order = "";
        //key 写成逗号分隔时(如 "0,4,0")是**逐段各自移调**，一项对应 order 里的一段。
        //整条统一移调是一个数、走原来的单次转换；逐段要 N 次转换，慢一些但能只动一段。
        public float[] KeyPerSegment = null;
    }

    //逐段移调这一次实际发生了什么，回报给她时如实写出来(包括"你写了 N 项但只有 M 段")。
    private string m_LastPracticeShiftNote = "";

    //上一次连唱之后各段的实际起调。工具结果只出现一帧，之后就沉进历史——
    //8/25 实测用户连问四轮"这三段的音调是多少"，她答不上来，还跑去开视觉看屏幕，
    //而答案就在她刚收到的那条工具结果里（一次答的还是上一轮的旧数据）。
    //这个数在下一次连唱之前一直成立，所以留着，别只闪一帧。
    private string m_LastRenderPitchSummary = "";
    private float m_LastRenderPitchAt = -999f;
    //超过这个时间就不再占感知帧的位置——再久用户多半已经在聊别的了。
    private const float k_RenderPitchStickySeconds = 300f;

    //上一轮出现过的不存在标签，点破一次就清掉。
    private string m_LastUnknownTagNote = "";

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
            //数目对不上时不猜她想动哪一段——少写的按 0 补，多写的丢掉，并且明说。
            note = $"注意：key 写了 {keyPerSegment.Length} 项，而这一次有 {count} 段。" +
                   "多出的已忽略、缺的按不移调处理。要精确控制就让 key 的项数和 order 的段数一致。";
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
        //放宽的风险(把某段拽走一个八度)由结果起调那一行兜底：写歪了立刻看得见。
        var clamped = new List<string>();
        var wide = new List<string>();
        for (int i = 0; i < keyPerSegment.Length; i++)
        {
            float capped = Mathf.Clamp(keyPerSegment[i], -12f, 12f);
            if (Mathf.Abs(capped - keyPerSegment[i]) > 0.01f)
                clamped.Add($"第{i + 1}项 {keyPerSegment[i]:0.#}→{capped:0.#}");
            //超过纯五度就不再是"同一个人唱得高一点"了。不拦，但要说。
            if (Mathf.Abs(capped) > 7f) wide.Add($"第{i + 1}项 {capped:+0.#;-0.#}");
            keyPerSegment[i] = capped;
        }
        if (clamped.Count > 0)
            note += $"注意：逐段 key 的可用范围是 −12~+12，超出的已被截断（{string.Join("、", clamped)}）。" +
                    "所以这一次的实际效果和你写的数字不一致。";
        if (wide.Count > 0)
            note += $"提醒：{string.Join("、", wide)} 超过了纯五度。" +
                    "移调这么多会明显不像同一个人在唱——若只是想让几段起调一致，" +
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
        if (Mathf.Abs(slide) <= 0.01f && clamped.Count == 0)
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
                medians.Add(composition.SegmentMedians != null && i < composition.SegmentMedians.Count
                    ? composition.SegmentMedians[i] : 0f);
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
                medians.Add(composition.SegmentMedians != null && i < composition.SegmentMedians.Count
                    ? composition.SegmentMedians[i] : 0f);
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
        public string SongId = "";
        public string Title = "";
        public string Mode = "memory";
        public string Reason = "";
        // song_sing 不消费段序；保留它只为识别模型把 practice 参数写错了工具。
        public string Order = "";
        //只唱某一段时填这一段的歌词。空 = 维持 mode 的既有语义。
        public string SegmentLyrics = "";
    }

    private static readonly System.Text.RegularExpressions.Regex s_SongSingTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<song_sing\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private AgentSongSingRequest ExtractSongSingTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = s_SongSingTagRegex.Match(text);
        if (!match.Success) return null;
        string attrs = match.Groups["attrs"].Value;
        AgentSongSingRequest request = new AgentSongSingRequest
        {
            SongId = ReadToolAttribute(attrs, "id"),
            Title = ReadToolAttribute(attrs, "title"),
            Mode = ReadToolAttribute(attrs, "mode"),
            Reason = ReadToolAttribute(attrs, "reason"),
            Order = ReadToolAttribute(attrs, "order"),
            SegmentLyrics = ReadToolAttribute(attrs, "lyrics"),
        };
        if (string.IsNullOrWhiteSpace(request.Mode)) request.Mode = "memory";
        text = s_SongSingTagRegex.Replace(text, "").Trim();
        return request;
    }

    private static readonly System.Text.RegularExpressions.Regex s_HumBackTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<hum_back\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    //她会把两个标签粘成一个词写出来，最典型的是 <silenthum_back mode="practice" …/>。
    //8/17 实测出现 4 次，每次都因为标签系统不认识而被整条剥掉——什么都没执行，
    //而她以为自己唱了(其中一次她自己发现了，下一句写着"刚才的标签写错了")。
    //两半都是精确的已知标签名时意图毫无歧义，拆开即可；不做任何拼写猜测。
    private static readonly string[] s_GluableTagNames =
    {
        "silent", "continue", "noop", "look", "unlook", "next", "skill_request", "skill_control",
        "singing_intent",
        "note", "memory_add", "memory_update", "memory_link", "speaker_name", "speaker_manage",
        "song_search", "song_remember", "song_rename", "song_forget", "song_sing",
        "practice_drop",
        "hum_back",
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
            bool known = false;
            foreach (string candidate in s_GluableTagNames)
                if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    known = true;
                    break;
                }
            if (known) continue;
            string text = m.Value;
            if (text.Length > 60) text = text.Substring(0, 60) + "…";
            return text;
        }
        return "";
    }

    //长期曲库那一层早就有 <song_forget/>，练唱会话这一层一直没有对称物：
    //只能整场重置或者到上限时挤掉最老的。于是一段被误收的说话、或者用户唱错想撤回的
    //那一遍，会一直挂在清单里，还会被默认顺序原样唱出去。
    private static readonly System.Text.RegularExpressions.Regex s_PracticeDropTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<practice_drop\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// 摘出并**立即执行** <c>&lt;practice_drop order="N"/&gt;</c>。
    /// </summary>
    /// <remarks>
    /// 就地做完而不排队：删一段是纯本地的列表操作，没有任何 IO。
    /// 排队反而会让同一轮里后面的 hum_back 拿到删除前的段号。
    /// </remarks>
    /// <param name="apply">
    /// false 时只把标签从正文剥掉、不真的删段。复读轮走这条路——
    /// 删除是**成功**动作，失败重试拦截管不到它，而段号每删一次就前移一次，
    /// 8/25 实测同一句话说四遍把练唱会话从 5 段削到了 1 段。
    /// </param>
    private void ExtractAndApplyPracticeDropTag(ref string text, bool apply = true)
    {
        if (string.IsNullOrEmpty(text)) return;
        var match = s_PracticeDropTagRegex.Match(text);
        if (!match.Success) return;
        string attrs = match.Groups["attrs"].Value;
        string order = ReadToolAttribute(attrs, "order");
        string reason = ReadToolAttribute(attrs, "reason");
        text = s_PracticeDropTagRegex.Replace(text, "").Trim();
        if (!apply)
        {
            if (m_LogAgentLoop)
                Debug.LogWarning($"[Agent/复读] practice_drop order=\"{order}\" 未执行");
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
        int index;
        if (!int.TryParse((order ?? "").Trim(), out index))
        {
            RecordPracticeDropResult(
                $"未执行：order=\"{(order ?? "").Trim()}\" 不是一个段号。" +
                $"练唱会话现在有 {senseVoice.PracticePhraseCount} 段，" +
                "照感知帧清单里的段号填一个整数。练唱会话没有变化。", true);
            return;
        }
        string dropped, failure;
        int remaining;
        if (!senseVoice.DropPracticePhrase(index, out dropped, out remaining, out failure))
        {
            RecordPracticeDropResult("未执行：" + failure + "。练唱会话没有变化。", true);
            return;
        }
        //段号会前移，必须点破。不说的话她下一句还按旧段号写 order，唱出来的是别的段。
        string note = $"成功：已从练唱会话去掉第 {index} 段（{dropped}）。" +
                      $"现在还剩 {remaining} 段";
        note += remaining > 0
            ? "，**它们的段号已经整体前移**，请照感知帧里的新清单重新认段号，不要沿用刚才那套。"
            : "，练唱会话已经空了。";
        if (!string.IsNullOrWhiteSpace(reason))
            note += $"（你给的理由：{reason.Trim()}）";
        RecordPracticeDropResult(note, false);
    }

    private string m_LastPracticeDropResult = "";
    private bool m_PracticeDropResultPending = false;

    private void RecordPracticeDropResult(string result, bool warning)
    {
        m_LastPracticeDropResult = result ?? "";
        m_PracticeDropResultPending = !string.IsNullOrWhiteSpace(m_LastPracticeDropResult);
        if (warning) NoteToolFailure(m_LastPracticeDropResult);
        if (m_LogHumBack)
            Debug.Log("[Practice/Drop] " + m_LastPracticeDropResult);
    }

    private AgentHumBackRequest ExtractHumBackTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        text = SplitGluedAgentTags(text);
        var match = s_HumBackTagRegex.Match(text);
        if (!match.Success) return null;
        string attrs = match.Groups["attrs"].Value;
        AgentHumBackRequest request = new AgentHumBackRequest
        {
            Mode = ReadToolAttribute(attrs, "mode"),
            Lyrics = ReadToolAttribute(attrs, "lyrics"),
            Reason = ReadToolAttribute(attrs, "reason"),
            Key = ReadToolFloatAttribute(attrs, "key", -4f, 4f),
            KeyPerSegment = ParseKeyList(ReadToolAttribute(attrs, "key")),
            Pace = ReadToolFloatAttribute(attrs, "pace", 0.8f, 1.25f),
            Expression = ReadToolFloatAttribute(attrs, "expression", 0f, 1f),
            Order = ReadToolAttribute(attrs, "order"),
        };
        if (string.IsNullOrWhiteSpace(request.Mode)) request.Mode = "echo";
        text = s_HumBackTagRegex.Replace(text, "").Trim();
        return request;
    }

    private static readonly System.Text.RegularExpressions.Regex s_SongSearchTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<song_search\b(?<attrs>[^>]*)>",
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
            m_LastSongSearchResult.ToLowerInvariant().Contains(probe))
        {
            source = "song_search 结果";
            return true;
        }
        //用户自己说过——含当前这一轮。历史里偶数位是用户。
        if (!string.IsNullOrEmpty(m_LastUserMsg) &&
            m_LastUserMsg.ToLowerInvariant().Contains(probe))
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
                    turn.ToLowerInvariant().Contains(probe))
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
                    request.Aliases, request.Reason, callback);
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
        string resultText = m_LastSongMemoryResult ?? "";
        m_SongMemoryAcknowledgementCoroutine = StartCoroutine(
            DeliverSongMemoryAcknowledgementWhenIdle(generation, success, resultText));
    }

    private IEnumerator DeliverSongMemoryAcknowledgementWhenIdle(
        int generation, bool success, string resultText)
    {
        //首阶段的模型回复可能仍在播放EOU filler或清理空流水线；等它完全结束后再开
        //第二阶段，避免 StartStreaming 抢占/截断当前音频。
        while (generation == m_SongMemoryGeneration &&
            (m_FormalResponseInFlight || !m_StreamComplete || IsAISpeaking ||
             IsVoiceOutputPlaying || m_AgentRoundInFlight))
        {
            yield return null;
        }

        m_SongMemoryAcknowledgementCoroutine = null;
        if (generation != m_SongMemoryGeneration) yield break;

        ClearScheduledAgentWake();
        m_SongMemoryResultPending = false;
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
    /// Returning true means this deterministic tool round owns the response and LLM
    /// generation is intentionally skipped.
    /// </summary>
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
            m_ExplicitHumBackHandled = true;
            if (m_LogHumBack)
                Debug.Log("[HumBack] 心里话高置信度判断为普通说话；跳过自动复唱，交给LLM正常回应");
            return false;
        }
        //软降级：声学确定在唱但文字判为说话。演唱素材照常保留(用户确认后仍可回哼)，
        //只是不再自动唱回来，也不写进练唱会话——那正是被误判时最扰人的两件事。
        //不再静默：DealingTextCallback 已经往 _msg 里注入了追问指示，她会开口确认。
        if (m_FinalModeSoftDowngrade)
        {
            RejectFastHumBackAfterFinal("final-mode-text-says-speech");
            m_ExplicitHumBackHandled = true;
            if (m_LogHumBack)
                Debug.Log("[HumBack] 文字判为说话；跳过自动复唱，保留素材并由她追问确认");
            return false;
        }
        if (IsCurrentTurnSpokenSingingExit())
        {
            RejectFastHumBackAfterFinal("singing-ended-with-spoken-exit");
            m_ExplicitHumBackHandled = true;
            if (m_LogHumBack)
                Debug.Log("[HumBack] 用户在歌唱末尾改为说话；跳过练习提交与自动复唱，交给LLM正常回应");
            return false;
        }
        bool confirmedSinging = IsCurrentTurnConfirmedSinging();
        // 用户可能把“再唱一遍，你跟我唱”和真正的第一句歌放在同一轮。
        // 最终 ASR 已确认歌声时，也允许这句话直接开启持续练唱会话。
        if (confirmedSinging && !HasActiveSingAlongRequest() &&
            IsSingAlongInvitation(m_LastUserMsg))
        {
            ArmSingAlongForNextPerformance();
        }
        bool armed = HasActiveSingAlongRequest();

        //唱了就记，与"要不要跟唱"无关。
        //原来这里绑着 armed：用户没先说过"跟着我唱"就一段都不记。8/16 实测用户
        //直接唱了两段、都判高区、都进了持久曲库，练唱会话却是 0 段——于是他说
        //「把刚才那两段合起来唱」时 mode=practice 连续两次失败(「只有 0 段，至少
        //需要两段」)，她只好改走 mode=continue，靠 0.69 的旋律相似度(我量过的
        //跨组中位就是 0.675，等于噪声)命中了一条混着《演员》和 Lemon 的污染条目，
        //把 Lemon 唱了出来。
        //正确的分工是：**唱了就进记忆**；要不要复现、要不要和别的段结合，
        //由用户和她自己商量决定。感知帧里已经在报「练唱会话: 已记录 N 段」，
        //她看得到有多少料，判断权在她。
        if (confirmedSinging)
        {
            SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
                ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
                : null;
            int phraseCount;
            if (senseVoice != null &&
                senseVoice.CommitRecentSingingToPracticeSession(out phraseCount) &&
                m_LogHumBack)
                Debug.Log($"[HumBack/Practice] 已记录最终确认片段 sequence={phraseCount} " +
                          $"(armed={armed})");

            if (armed) TryAbortHumBackOnRetraction(senseVoice);
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
            m_ExplicitHumBackHandled = true;
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
        m_ExplicitHumBackHandled = true;
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
            bool stillWorking = m_HumBackPending || m_HumBackPreparingCarrier ||
                m_SongSingInFlight || m_ActiveHumSVCRequest != null ||
                m_ActiveSVSRequest != null || m_FastHumBackActive ||
                m_FastHumBackEouStaged;
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
                   $"剩下 {kept} 段**已经重新编号**（现在的 [1] 是原来的 [{dropped + 1}]）。" +
                   "你之前笔记里记的段号从这一刻起全部作废——" +
                   "要唱哪一段请照下面这份清单重新认，或者直接把那一段的歌词片段写进 order。";
        return $"\n练唱会话变动: 刚才重启了一次实时模式，之前那 {dropped} 段因为太久远已经全部清掉，" +
               "现在练唱会话是空的。你笔记里记的段号（第3段、第5段之类）**全部作废**，" +
               "照着它们发 <hum_back mode=\"practice\" order=\"N\"/> 一定会失败。" +
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
        //下面每一条失败都会走 RecordHumBackResult，而那里按 m_PendingHumBackKey 记账。
        //曲库演唱不是 hum_back，先把账本清干净，免得算到上一次回哼头上。
        m_PendingHumBackKey = "";
        if (!m_EnableAutonomousRememberedSongSinging || !m_EnableAutonomousHumBack ||
            !m_IsVoiceMode)
        {
            RecordHumBackResult("未执行：长期曲库演唱功能当前已关闭。", true);
            return;
        }
        if (m_SongSingInFlight || m_HumBackPending || m_HumBackPreparingCarrier || m_HumBackPlaying)
        {
            RecordHumBackResult("未执行：已经有一个真实歌唱任务正在进行。", true);
            return;
        }
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
            RecordHumBackResult(
                $"未执行：这个目标（{TruncateForFrame(songSingKey, 60)}）刚才已经查过，" +
                "曲库里没有，重试同一个只会得到同样的结果。" +
                "不要再说「马上就唱」。可选的下一步：用感知帧候选行里 id= 后面的**完整** id 重试" +
                "（清单里的 id 也是完整的，别截断）；或者用 <hum_back mode=\"practice\" order=\"N\"/> " +
                "唱本轮练唱会话里的第 N 段；或者如实告诉用户你还没记住这一段。",
                true);
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
        float totalSongLimit = m_AgentCurrentRoundIsTick
            ? m_AutonomousSingingMaxSeconds
            : 0f;
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
                    m_LastFailedSongSingKey = songSingKey;
                    m_LastFailedSongSingTime = Time.realtimeSinceStartup;
                    RecordHumBackResult(
                        "未执行：" + detail + "。不得声称已经从记忆中唱出或续唱成功。" +
                        BuildSongSingIdHint(request.SongId),
                        true);
                    if (m_LogHumBack) Debug.LogWarning("[SongSing] " + detail);
                    CompleteSongSingToolRoundIfIdle();
                    return;
                }
                if (!ValidateResolvedSongIdentity(request, result, out string identityProblem))
                {
                    m_LastFailedSongSingKey = songSingKey;
                    m_LastFailedSongSingTime = Time.realtimeSinceStartup;
                    RecordHumBackResult(
                        "未执行：" + identityProblem + "。为避免唱错歌，返回音频已丢弃；" +
                        "请重新查询正确 id，或改用 <hum_back mode=\"practice\"/> 唱本轮练习片段。",
                        true);
                    if (m_LogHumBack)
                        Debug.LogWarning("[SongSing/Identity] " + identityProblem);
                    CompleteSongSingToolRoundIfIdle();
                    return;
                }

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
                (request.Lyrics ?? "") + "|" +
                (float.IsNaN(request.Key) ? "" : request.Key.ToString("0.##")) + "|" +
                (request.KeyPerSegment == null
                    ? ""
                    : string.Join(",", request.KeyPerSegment))).Trim().ToLowerInvariant();
    }

    private string BuildEchoUnavailableNote(SenseVoiceSpeechToText senseVoice)
    {
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

    private void QueueHumBack(AgentHumBackRequest request)
    {
        if (!m_EnableAutonomousHumBack || !m_IsVoiceMode || request == null) return;
        if (IsPracticeHumMode(request.Mode) && string.IsNullOrWhiteSpace(request.Order))
        {
            SenseVoiceSpeechToText practiceSense = m_ChatSettings != null
                ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
                : null;
            if (practiceSense != null && practiceSense.PracticePhraseCount > 1)
            {
                m_ExplicitHumBackHandled = true;
                RecordHumBackResult(
                    $"未执行：练唱会话有 {practiceSense.PracticePhraseCount} 段，" +
                    "但 practice 没有显式 order。系统不会再把空 order 猜成全部；" +
                    "请按用户语境列出准确段号，拿不准就先询问。",
                    true);
                if (m_LogHumBack)
                    Debug.LogWarning("[HumBack/Practice] 拦下多段空 order，避免默认全唱");
                return;
            }
        }
        if (m_HumBackPending || m_HumBackPreparingCarrier || m_HumBackPlaying)
        {
            if (m_LogHumBack) Debug.LogWarning("[HumBack] 已有回哼任务，忽略重复调用");
            return;
        }

        if (IsCurrentTurnSpokenSingingExit())
        {
            m_ExplicitHumBackHandled = true;
            RecordHumBackResult(
                "未执行：用户在歌唱末尾已经转为口语并表示不会继续、忘词或停止。" +
                "本轮应正常回应用户，不得复读整段混合音频。",
                false);
            if (m_LogHumBack)
                Debug.Log("[HumBack] 忽略混合歌唱转说话轮次的工具调用");
            return;
        }

        //同一个调用已经连续失败够多次：不再派发，把仅剩的两条路写清楚。
        string humBackKey = BuildHumBackKey(request);
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
        //是否属于“不要唱任何东西”还是“不要唱第 1～3 段、只唱第 4～5 段”，
        //已经由 singing_intent 结合完整语境判断。这里不再用“不要唱”子串二次否决。
        if (!composePractice && !confirmedSinging && IsSingAlongInvitation(m_LastUserMsg))
        {
            ArmSingAlongForNextPerformance();
            return;
        }
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
            SenseVoiceSpeechToText.PracticeComposition composition;
            string failure;
            if (!senseVoice.TryBuildSingingPracticeComposition(
                    performanceSeed,
                    0f,
                    out composition,
                    out failure,
                    request.Order) || composition == null)
            {
                RecordHumBackResult(
                    "未执行：" + failure + "。不得声称已经把练习片段连续唱出。",
                    true);
                if (m_LogHumBack) Debug.LogWarning("[HumBack/Practice] " + failure);
                return;
            }
            if (m_AgentCurrentRoundIsTick &&
                composition.DurationSeconds > m_AutonomousSingingMaxSeconds + 0.02f)
            {
                RecordHumBackResult(
                    $"未执行：这是角色自主发起的歌唱，完整内容约 {composition.DurationSeconds:F1}s，" +
                    $"超过自主歌唱软限制 {m_AutonomousSingingMaxSeconds:F1}s。" +
                    "若用户明确要求完整演唱，可以不受这个总时长限制；否则请用 order 主动选一小段。",
                    true);
                if (m_LogHumBack)
                    Debug.LogWarning($"[HumBack/Practice] 自主歌唱软限制 " +
                                     $"{composition.DurationSeconds:F1}s > {m_AutonomousSingingMaxSeconds:F1}s");
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
            variationDiagnostic = composition.VariationDiagnostic;
            //必须在 ResolvePerSegmentShifts 之前抄下来——那个方法会就地把
            //超范围的项截断，抄晚了记的就不是她写的原值。
            m_PendingPerSegmentKeyText = request.KeyPerSegment == null
                ? ""
                : string.Join(",", System.Array.ConvertAll(
                    request.KeyPerSegment, v => v.ToString("0.##")));
            SenseVoiceSpeechToText.PracticeComposition explicitlyShifted = ResolvePerSegmentShifts(
                request.KeyPerSegment, composition, out practiceSegmentShifts,
                out string shiftNote);
            if (!string.IsNullOrEmpty(shiftNote)) m_LastPracticeShiftNote = shiftNote;
            m_PendingHumUsesExplicitSegmentKey = explicitlyShifted != null;
            if (m_PendingHumUsesExplicitSegmentKey)
            {
                practiceSegments = composition;
                variationDiagnostic += "; 逐段移调 " + string.Join("/", practiceSegmentShifts);
                //不同段可能有不同 key，不能跨段合并；只拆开单段里超过目标块长的部分。
                ExpandOversizedStreamingSegments(
                    senseVoice, practiceSegments, ref practiceSegmentShifts);
            }
            else
            {
                //统一 key 时从已经包含全部自然停顿的完整源重新按约 30 秒切块，
                //避免十几个短句各付一次模型固定开销。
                if (!senseVoice.TryBuildSingingStreamChunks(
                        composition.WavBytes,
                        composition.MidiTimeline,
                        composition.FrameSeconds,
                        HumStreamChunkSeconds,
                        out practiceSegments,
                        out string streamPlanFailure) || practiceSegments == null)
                {
                    practiceSegments = composition;
                    if (m_LogHumBack)
                        Debug.LogWarning("[HumBack/Stream] 统一练唱分块失败，保留自然段计划: " +
                                         streamPlanFailure);
                }
                practiceSegmentShifts = BuildUniformStreamingShifts(
                    practiceSegments, semitoneOffset);
            }
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
        m_HumBackPending = true;
        if (m_LogHumBack)
        {
            float duration = timeline.Length * m_PendingHumFrameSeconds;
            Debug.Log($"[HumBack] 已排队 mode={m_PendingHumMode} phrases={phraseCount} " +
                      $"frames={timeline.Length} melody={duration:F1}s source={sourceDuration:F1}s " +
                      $"seed={performanceSeed} shiftOffset={semitoneOffset} " +
                      $"她指定=[key={(float.IsNaN(request.Key) ? "-" : request.Key.ToString("0.#"))} " +
                      $"pace={(float.IsNaN(request.Pace) ? "-" : request.Pace.ToString("0.##"))} " +
                      $"expr={(float.IsNaN(request.Expression) ? "-" : request.Expression.ToString("0.##"))}] " +
                      $"rms={rmsMixRate:F2} protect={protect:F2} interp={interpretation:F2} " +
                      $"language={m_PendingHumLanguage} " +
                      $"variation=\"{m_PendingHumVariationDiagnostic}\" " +
                      $"reason=\"{m_PendingHumReason}\"");
        }
    }

    /// <summary>
    /// 在当前文字回复完全播放后启动旋律回哼。准备载体的阶段也算角色正在回应，
    /// 因而实时 VAD 会继续走 barge-in 路径，用户可以随时打断。
    /// </summary>
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

    private bool TryBeginPendingHumBack()
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

        m_HumBackPending = false;
        m_HumBackPreparingCarrier = false;
        m_HumBackPlaying = false;
        int generation = ++m_HumBackGeneration;
        m_HumBackNeedsHistoryEntry = m_ChatHistory != null && m_ChatHistory.Count % 2 == 1;
        IsAISpeaking = true;
        m_TextBack.text = "♪ …";
        SetAnimator("state", 1);

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
        if (m_EnableStreamedFullSongSVC && m_EnableNeuralHumSVC && hasNeuralInputs &&
            m_PendingHumSegmentWavs != null && m_PendingHumSegmentShifts != null &&
            m_PendingHumSegmentWavs.Count == m_PendingHumSegmentShifts.Length &&
            m_PendingHumSegmentWavs.Count >= 2)
        {
            m_PendingHumRenderer = "svc-stream";
            m_HumBackPreparingCarrier = true;
            //分块计划已经拥有完整内容；释放整条 WAV，长歌不会同时保留两份 PCM。
            m_PendingHumSourceWav = null;
            m_HumStreamProducerCoroutine = StartCoroutine(
                RequestPerSegmentNeuralHumBack(generation, targetPath));
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

    /// <summary>
    /// 分块生产者：转换服务仍按顺序一次处理一块，但每块回来就放进播放队列，
    /// 不再等待整首全部转换完成。所有块共用一个 performance seed 和整曲基准移调。
    /// </summary>
    private IEnumerator RequestPerSegmentNeuralHumBack(int generation, string targetPath)
    {
        var segments = m_PendingHumSegmentWavs;
        var gaps = m_PendingHumSegmentGaps;
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
                if (scheduled.Clip != null)
                {
                    m_HumStreamPlayedSeconds += scheduled.Clip.length;
                    m_HumStreamBufferedSeconds = Mathf.Max(
                        0f, m_HumStreamBufferedSeconds - scheduled.Clip.length);
                    Destroy(scheduled.Clip);
                }
                m_HumStreamPlayedSegments++;
                m_HumStreamScheduledClips.RemoveAt(i);
            }

            bool scheduledOne = false;
            if (m_HumStreamReadyClips.Count > 0 && secondary != null && m_AudioSource != null)
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
            !isSVSPostPolish && m_HumSVCAutoF0Adjust ? "true" : "false");
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
        //必须是**裸转写**。m_LastUserMsg 带着 [说话人:…] [演唱片段; 歌唱概率:…; 旋律:…]
        //这些感知前缀，里面"演唱片段""歌唱概率""旋律"几个词等于在提示里先替它把
        //答案说了——8/12 首次上机 5 次复核 4 次答 singing，就是这么来的。
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

        //声学高区时文字没有否决权——这条规则 §模态判定 那里已经立过，判据是
        //「模糊带(<0.58) 文字全权否决；高区(>=0.58) 只做软降级、保留演唱」，
        //理由是混合轮文字仍有 2/5 会答错，误杀代价太大。
        //这里原来完全没应用它，于是同一个冲突被两条路判出相反结果：
        //8/23 实测 prob=0.73 stab=0.71 岛=9.47s/内容10.60s 的一轮真唱，
        //[模态判定] 已经决定「保留演唱，改为出声追问」，转换也跑完了，
        //却在这里被同一个文字裁决整段丢弃，用户当场问「为什么会失败呢？」。
        SenseVoiceSpeechToText vetoSenseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (vetoSenseVoice != null && vetoSenseVoice.LastSingingProbability >= 0.58f)
        {
            if (m_LogHumBack)
                Debug.Log("[HumBack/Veto] 文字判说话，但声学 " +
                          $"prob={vetoSenseVoice.LastSingingProbability:F2} 在高区" +
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
            m_ChatSettings.m_ChatModel.PostMsg(llmInput, CallBack);
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

        string note =
            $"本次 key=\"{current}\"。**key 是相对每段原始录音的绝对值，" +
            "不是在上一次结果上叠加**——写 0 不等于\"维持上一次的调整\"，" +
            "而是把那一段打回原始音高。";
        if (!string.IsNullOrEmpty(previous) && previous != current)
            note += $"（上一次你写的是 key=\"{previous}\"。）";
        note +=
            "想在本次结果上再动某一段：**把本次这一串照抄下来，只改要动的那一项**。" +
            $"例如只把最后一段再抬半音 → key=\"{BuildKeyBumpExample(current)}\"。";
        return note;
    }

    /// <summary>给一个照着本次 key 改最后一项的现成例子——比讲道理管用。</summary>
    private static string BuildKeyBumpExample(string current)
    {
        var parts = (current ?? "").Split(',');
        if (parts.Length == 0) return current ?? "";
        int last = parts.Length - 1;
        if (float.TryParse(
                parts[last].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v))
            parts[last] = (v + 1f).ToString("0.##");
        return string.Join(",", parts);
    }

    private string BuildPracticeShiftNote(
        int[] segmentShifts, List<float> segmentMedians, List<int> segmentSources,
        int wholeClipShift)
    {
        string note = m_LastPracticeShiftNote ?? "";
        if (segmentShifts != null && segmentShifts.Length >= 2)
        {
            //8/20 实测：她连着三轮用整条 key 想只抬第二段，每次都把三段一起抬走，
            //还对用户说"这次我会确保只调整第二段"。所以这里必须写明是逐段还是整条。
            note += "本次按逐段移调执行，各段实际发出的半音数为 " +
                    string.Join("、", segmentShifts) +
                    "（已含把音域拉进你声线的基准量，所以数字不等于你写的 key）。";
            //光给发出去的数字还不够：8/21 实测她拿到回报之后仍然一个半音一个半音地试，
            //三轮只从 +1 加到 +3，而实际差 6 个。把**移调后各段的起调**也报出来，
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
                m_LastRenderPitchSummary = string.Join("、", after);
                m_LastRenderPitchAt = Time.realtimeSinceStartup;
                note += "唱出来之后各段的起调是 " + m_LastRenderPitchSummary +
                        "。要哪几段一样高，就把这几个数相减，一次把差值补够——" +
                        "不要一个半音一个半音地试。" +
                        "（`key` 的第 i 项对应 `order` 的第 i 个位置，不是清单段号；" +
                        "上面每项前面的 清单[N] 就是它对应的清单段号。）";
                //光报结果起调还不够。8/26 实测她 key="2,0,2" 一次调齐了三段，
                //下一轮想"再抬第三段半个音"就写了 key="0,0,1"——**当成增量了**。
                //于是前两段被打回原始音高，刚调齐的又散了。她的 reason 写着
                //「第一段和第二段保持原样」，意图是对的，是没人告诉她 key 的语义。
                //所以这两句必须每次都在：本次写了什么、上次写了什么、以及"照抄再改"。
                note += BuildPerSegmentKeySemanticsNote();
            }
        }
        else if (wholeClipShift != 0)
        {
            note += $"注意：本次 key={wholeClipShift:+0;-0} 作用在**整条**音频上，" +
                    "所有段一起被抬高或降低了，段与段之间的高低差没有变。" +
                    "用户要的若是「只动其中某一段」，把 key 写成逐段形式（如 key=\"0,4,0\"，" +
                    "一项对应 order 里的一段）。";
        }
        return note;
    }

    /// <summary>刚靠"唱出去且没被打断"确认掉的段落，说给她听一次。</summary>
    private string BuildEchoPromotionNote()
    {
        var indices = m_JustConfirmedPhraseIndices;
        m_JustConfirmedPhraseIndices = null;
        if (indices == null || indices.Count == 0) return "";
        string list = string.Join("、", indices);
        return $" 另外：第 {list} 段原本因为转写读起来像说话而标着「待确认」，" +
               "现在你已经把它唱出来、用户也听完了没有异议，这个标已经去掉了。";
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

    /// <summary>
    /// 一段唱出去、用户听完没有异议，就把它的"待确认"标清掉。
    /// </summary>
    /// <remarks>
    /// 确认的形式不是用户嘴上答"是"再去解析，而是**他听到了实际生成的音频**——
    /// 那比任何文字确认都硬，也不需要新增一套"确认回传"机制。
    /// 被打断则不清：那正是他觉得不对的信号。
    /// 只清这次真的唱出去的段落：order 指名了几段，没被唱到的段落不算确认过。
    /// </remarks>
    private void ConfirmPlayedPracticePhrases(bool completed)
    {
        m_JustConfirmedPhraseIndices = null;
        if (!completed) return;
        if (m_PendingHumIsCatalogSong) return;   //曲库演唱唱的不是练唱会话里的段落
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (senseVoice == null) return;

        var pending = senseVoice.PendingConfirmationPracticeIndices();
        if (pending.Count == 0) return;

        List<int> played;
        if (m_PendingHumIsPracticeComposition)
        {
            //连唱：只有 order 点到的那几段被他听见了
            played = m_PendingHumPlayedIndices;
            if (played == null || played.Count == 0) return;
        }
        else
        {
            //回哼：唱的是最近演唱缓存，也就是刚写进去的最后一段
            played = new List<int> { senseVoice.PracticePhraseCount };
        }

        var toConfirm = new List<int>();
        foreach (int idx in pending)
            if (played.Contains(idx)) toConfirm.Add(idx);
        if (toConfirm.Count == 0) return;

        senseVoice.ConfirmPracticePhrases(toConfirm);
        m_JustConfirmedPhraseIndices = toConfirm;
        if (m_LogHumBack)
            Debug.Log("[HumBack/Practice] 唱出去且用户没打断，第 " +
                      string.Join("、", toConfirm) + " 段去掉待确认标");
    }

    private void FinishHumBack(int generation, bool completed, string detail)
    {
        if (generation != m_HumBackGeneration) return;
        int streamedPlayedSegments = m_HumStreamPlayedSegments;
        int streamedTotalSegments = m_HumStreamTotalSegments;
        float streamedPlayedSeconds = m_HumStreamPlayedSeconds;
        bool wasStreamed = streamedTotalSegments > 0;
        if (m_HumStreamPlaybackCoroutine != null)
        {
            StopCoroutine(m_HumStreamPlaybackCoroutine);
            m_HumStreamPlaybackCoroutine = null;
        }
        ClearHumStreamClipBuffers(true);
        ConfirmPlayedPracticePhrases(completed);
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
        bool wasCatalogSong = m_PendingHumIsCatalogSong;
        bool wasCatalogContinuation = m_PendingHumIsCatalogContinuation;
        string catalogSongName = m_PendingCatalogSongName;
        string renderer = m_PendingHumRenderer ?? "unknown";
        //必须在下面那批清空之前抓走：工具结果是在本函数**后半段**才拼的，
        //8/21 实测整场四次逐段移调，回报里一个字都没有，就是被这里清掉了。
        int[] usedSegmentShifts = m_PendingHumSegmentShifts;
        List<float> usedSegmentMedians = m_PendingHumSegmentMedians;
        List<int> usedSegmentSources = m_PendingHumSegmentSources;
        bool usedExplicitSegmentKey = m_PendingHumUsesExplicitSegmentKey;
        int usedWholeClipShift = m_PendingHumSemitoneOffset;
        m_HumBackNeedsHistoryEntry = false;
        m_HumBackPending = false;
        m_HumBackPreparingCarrier = false;
        m_HumBackPlaying = false;
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
            RecordHumBackResult(
                wasCatalogSong
                    ? (wasCatalogContinuation
                        ? $"成功：已经从本地歌曲记忆中定位并真实播放了“{catalogSongName}”当前片段之后的已学内容。{rendererFact}"
                        : $"成功：已经从本地歌曲记忆中取出“{catalogSongName}”并用角色声线真实播放完成。" +
                          BuildCatalogSegmentNote() + rendererFact)
                    //把实际用的顺序回报出来。原来只写"按原顺序"，用户连着七轮要求
                    //反过来唱，她四次都读不出这句话的意思是"我无视了你的顺序要求"。
                    : (wasPracticeComposition
                        ? $"成功：练唱会话中的片段已按顺序 {m_LastPracticeOrderUsed} 合成为一次连续演唱，" +
                          "并真实播放完成。每次演绎的呼吸间隔、轻微速度和力度可以不同。" +
                          "若用户要的顺序与此不同，下次用 order 指定（如 order=\"2,1\"）。" +
                          BuildPracticeShiftNote(
                              usedExplicitSegmentKey ? usedSegmentShifts : null,
                              usedSegmentMedians,
                              usedSegmentSources, usedWholeClipShift) +
                          m_LastPracticeDuplicateNote + BuildEchoPromotionNote() +
                          rendererFact
                        : "成功：回哼音频已经真实播放完成。现在可以自然评价刚才的回哼，但不要夸大为同步合唱。" +
                          BuildEchoPromotionNote() +
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
                      "。必须如实说没有完整唱完；可以谈已经听到的部分，但不得声称整首完成。"
                    : "失败：回哼音频没有生成或播放。原因：" +
                      TruncateForFrame(detail, 300) +
                      "。必须如实承认没有唱出来，不得让用户评价不存在的声音。",
                true);
        }

        if (m_LogHumBack)
            Debug.Log($"[HumBack] {(completed ? "播放完成" : "未能播放")} detail={detail}");
        OnAgentRoundComplete();
        if (OnAISpeakDone != null) OnAISpeakDone();
        if (m_ChatSettings != null && m_ChatSettings.m_TextToSpeech != null)
            m_ChatSettings.m_TextToSpeech.WarmUp();
    }

    private void CancelPendingHumBack(string reason, bool recordInterrupted)
    {
        bool hadWork = m_SongSingInFlight || m_HumBackPending || m_HumBackPreparingCarrier || m_HumBackPlaying ||
            m_ActiveHumBackClip != null || m_ActiveSVSRequest != null ||
            m_ActiveHumSVCRequest != null ||
            m_HumStreamProducerCoroutine != null || m_HumStreamPlaybackCoroutine != null ||
            m_HumStreamReadyClips.Count > 0 || m_HumStreamScheduledClips.Count > 0 ||
            m_HumBackPrefixPreparing || m_PreparedHumBackPrefixClip != null ||
            m_FastHumBackEouStaged || m_FastHumBackActive || m_FastHumBackFullClip != null;
        if (!hadWork) return;

        int interruptedStreamPlayed = m_HumStreamPlayedSegments;
        int interruptedStreamTotal = m_HumStreamTotalSegments;
        float interruptedStreamSeconds = m_HumStreamPlayedSeconds;
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
                      "不得声称已经完整唱完。"
                    : "中断：回哼开始后被用户打断，没有完整播放。不得声称已经完整唱完。",
                true);
        }
        m_HumBackNeedsHistoryEntry = false;
        m_HumBackPending = false;
        m_HumBackPreparingCarrier = false;
        m_HumBackPlaying = false;
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
        IsAISpeaking = false;
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

    private void RecordHumBackResult(string result, bool warning)
    {
        m_LastHumBackResult = result ?? "";
        m_HumBackResultPending = !string.IsNullOrWhiteSpace(m_LastHumBackResult);
        //warning 就是"这次没成"的现成信号，不需要再解析文本。
        if (warning)
        {
            NoteToolFailure(m_LastHumBackResult);
            if (!string.IsNullOrEmpty(m_PendingHumBackKey))
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
        StartCoroutine(SetTextPerWord(_msg));
    }

    private IEnumerator SetTextPerWord(string _msg)
    {
        int currentPos = 0;
        while (m_WriteState)
        {
            yield return new WaitForSeconds(m_WordWaitTime);
            currentPos++;
            //更新显示的内容
            m_TextBack.text = _msg.Substring(0, currentPos);

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
