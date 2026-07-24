using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;
using WebGLSupport;
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
        get { return m_AudioSource != null && m_AudioSource.isPlaying; }
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
        if (m_ActiveHumSVCRequest != null) m_ActiveHumSVCRequest.Abort();
        if (m_HumBackPrefixSVCRequest != null) m_HumBackPrefixSVCRequest.Abort();
        //仅释放 Unity 持有的进程句柄，不终止本机服务；退出 Play Mode 后 9882 仍可复用。
        if (m_HumSVCServerProcess != null)
        {
            m_HumSVCServerProcess.Dispose();
            m_HumSVCServerProcess = null;
        }
        if (m_ChatSettings != null && m_ChatSettings.m_TextToSpeech != null)
            m_ChatSettings.m_TextToSpeech.CancelPreparedSpeech();
        if (m_PreparedSingingBridgeClip != null) Destroy(m_PreparedSingingBridgeClip);
        if (m_DeferredPreparedClipToDestroy != null) Destroy(m_DeferredPreparedClipToDestroy);
        if (m_GeneratedHumCarrierClip != null) Destroy(m_GeneratedHumCarrierClip);
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
            m_ExplicitSongSingHandled = false;
            m_AgentCurrentRoundIsTick = false;
            if (m_MemoryHub != null && m_EnableMemoryRecall)
                m_MemoryHub.NotifyUserUtterance(m_LastUserMsg);
        }

        m_InputWord.text = "";
        bool earlyEouFiller = m_LatencyFillerFromEou && m_LatencyFillerPlayed;
        m_TextBack.text = earlyEouFiller ? m_LatencyFillerText : "正在思考中...";

        //EOU 快速回应可能在 ASR 完成前已经开始；此时不要把说话动作退回思考动作。
        SetAnimator("state", earlyEouFiller ? 2 : 1);

        //agent loop 启用时：把感知帧拼到用户文本前面，让 LLM 也能"感受"时间
        //(返回的字符串才是真正喂给 LLM 的——含 [感知帧 ...] + 用户原话)
        string llmInput = (m_AgentRunning) ? PrepareUserTurn(_postWord) : _postWord;
        if (!string.IsNullOrWhiteSpace(speculativeHint))
        {
            //临时草稿不进入任何历史；只在最终转写与 partial 足够一致时，作为本轮一次性提示。
            llmInput = speculativeHint + "\n" + llmInput;
        }

        // Combining already confirmed practice phrases is also deterministic and does
        // not need an LLM round to decide whether the audio tool should really run.
        if (TryHandleDirectPracticeCompositionTurn()) return;

        // A previously armed sing-along is deterministic once final ASR confirms a
        // playable performance.  Do not spend another LLM round deciding whether to
        // invoke the tool: commit the already prepared/queued real singing directly.
        if (TryHandleDirectSingAlongTurn()) return;

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
        AgentSongMemoryRequest songMemory = ExtractSongMemoryTag(ref afterMem);
        AgentSongSearchRequest songSearch = ExtractSongSearchTag(ref afterMem);
        AgentSongSingRequest songSing = ExtractSongSingTag(ref afterMem);
        AgentHumBackRequest humBack = ExtractHumBackTag(ref afterMem);
        if (humBack != null) m_ExplicitHumBackHandled = true;
        if (songSing != null) m_ExplicitSongSingHandled = true;
        if (songMemory != null)
        {
            if (songMemory.Action == "remember" && string.IsNullOrWhiteSpace(songMemory.Title))
                songMemory.Title = ExtractExplicitSongTitle(m_LastUserMsg);
            m_ExplicitSongRememberHandled = true;
        }
        if (songMemory == null && ShouldFallbackToExplicitSongRemember())
        {
            songMemory = new AgentSongMemoryRequest
            {
                Action = "remember",
                Title = ExtractExplicitSongTitle(m_LastUserMsg),
                Reason = "用户明确要求记住最近歌声，但模型漏掉了 song_remember 标签",
            };
            m_ExplicitSongRememberHandled = true;
        }
        if (songMemory != null) BeginSongMemory(songMemory);
        else if (m_HoldSpeechForSongMemoryResult)
            CompleteSongMemoryImmediately("没有找到可用于保存的最近歌声音频，本次未写入本机曲库。");
        if (songSearch != null) BeginSongSearch(songSearch);
        if (songSing == null && ShouldFallbackToExplicitSongSing())
        {
            songSing = new AgentSongSingRequest
            {
                Title = ExtractRequestedSongTitle(m_LastUserMsg),
                Mode = IsRememberedSongContinuationRequest(m_LastUserMsg) ? "continue" : "memory",
                Reason = "用户明确要求演唱已记住的歌曲，但模型漏掉了 song_sing 标签",
            };
            m_ExplicitSongSingHandled = true;
            if (m_LogHumBack)
                Debug.LogWarning("[SongSing] 检测到明确曲库演唱请求，模型未调用 <song_sing/>，执行安全兜底");
        }
        if (songSing != null)
        {
            // 持久曲库与“刚才一句”是不同音源；同一轮只执行一种真实歌唱动作。
            humBack = null;
            BeginSongSing(songSing);
        }
        if (songSing == null && humBack == null && ShouldFallbackToExplicitHumBack())
        {
            bool composePractice = IsPracticeCompositionRequest(m_LastUserMsg);
            humBack = new AgentHumBackRequest
            {
                Mode = composePractice ? "practice" : "echo",
                Reason = composePractice
                    ? "用户明确要求连续演唱练习片段，但模型漏掉了 practice hum_back 标签"
                    : "用户明确要求回哼最近旋律，但模型漏掉了 hum_back 标签",
            };
            m_ExplicitHumBackHandled = true;
            if (m_LogHumBack)
                Debug.LogWarning("[HumBack] 检测到明确回哼请求，模型未调用 <hum_back/>，执行安全兜底");
        }
        if (humBack != null) QueueHumBack(humBack);
        bool heldForSongMemory = m_HoldSpeechForSongMemoryResult;
        bool heldForHumBack = m_HoldSpeechForHumBackResult;
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
    [Tooltip("两次临时草稿请求的最小间隔。正式回复会抢占并撤销临时请求。")]
    [Range(0.8f, 4f)] [SerializeField] private float m_SpeculativeMinRequestInterval = 1.4f;
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
    [Tooltip("只有达到此置信度的安全短开场才会被静默预合成。")]
    [Range(0.4f, 0.95f)] [SerializeField] private float m_SingingBridgeMinConfidence = 0.62f;
    [Tooltip("预合成开场的最大字符数；过长候选会放弃，避免抢占正式回答。")]
    [Range(8, 48)] [SerializeField] private int m_SingingBridgeMaxChars = 28;
    [Tooltip("临时心里话明确判断为普通说话时，达到此置信度即可否决预回唱。它只是否决依据，不会单独确认歌唱。")]
    [Range(0.65f, 0.98f)] [SerializeField] private float m_SpeculativeSpeechVetoConfidence = 0.82f;
    [Tooltip("临时心里话判断为歌唱达到此置信度时，可与流式声学证据一起请求最终歌唱分析；仍不能绕过最终声学确认。")]
    [Range(0.55f, 0.95f)] [SerializeField] private float m_SpeculativeSingingSupportConfidence = 0.70f;

    private string m_StreamingTranscript = "";
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
    private bool m_StreamingSingingExitDetected = false;
    private string m_StreamingSingingEvidence = "";
    private float m_LastSingingSpeculativeRequestTime = -999f;
    private bool m_EouCognitiveSpeechVeto = false;
    private bool m_EouCognitiveSingingSupport = false;
    private int m_SingingBridgeGeneration = 0;
    private bool m_SingingBridgeTtsInFlight = false;
    private AudioClip m_PreparedSingingBridgeClip;
    private AudioClip m_DeferredPreparedClipToDestroy;
    private string m_PreparedSingingBridgeText = "";
    private float m_PreparedSingingBridgeConfidence = 0f;
    private bool m_PreparedSingingBridgePlayedThisTurn = false;
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
            ReleasePreparedSingingBridge(false);
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
                if (m_LogSpeculativeListening)
                    Debug.Log($"[流式倾听] 临时草稿就绪 confidence={parsed.confidence:F2} " +
                              $"mode={parsed.observed_mode}/{parsed.mode_confidence:F2}: \"{parsed.draft}\"");
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

        if (!m_StreamingTurnIsSinging || transcriptVersion != m_StreamingTranscriptVersion ||
            evidence != m_StreamingSingingEvidence || m_SpeculativeRequestInFlight)
            yield break;
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
        if (draft == null || !draft.isSinging ||
            draft.confidence < m_SingingBridgeMinConfidence ||
            string.IsNullOrWhiteSpace(draft.draft) ||
            m_ChatSettings == null || m_ChatSettings.m_TextToSpeech == null)
            return;

        if (draft.draft == m_PreparedSingingBridgeText &&
            (m_PreparedSingingBridgeClip != null || m_SingingBridgeTtsInFlight))
            return;

        ReleasePreparedSingingBridge(false);
        int generation = ++m_SingingBridgeGeneration;
        m_SingingBridgeTtsInFlight = true;
        m_PreparedSingingBridgeText = draft.draft;
        m_PreparedSingingBridgeConfidence = draft.confidence;

        if (m_LogSpeculativeListening)
            Debug.Log($"[歌唱预反应] 开始静默预合成：\"{draft.draft}\"");

        m_ChatSettings.m_TextToSpeech.PrepareSpeech(draft.draft, (clip, text) =>
        {
            if (generation != m_SingingBridgeGeneration || !m_StreamingTurnIsSinging)
            {
                if (clip != null) Destroy(clip);
                return;
            }
            m_SingingBridgeTtsInFlight = false;
            if (clip == null)
            {
                if (m_LogSpeculativeListening)
                    Debug.LogWarning("[歌唱预反应] 静默预合成未完成，将在EOU使用缓存应声");
                return;
            }

            m_PreparedSingingBridgeClip = clip;
            m_PreparedSingingBridgeText = text;
            if (m_LogSpeculativeListening)
                Debug.Log($"[歌唱预反应] 安全开场已就绪，音频{clip.length:F2}s：\"{text}\"");
        });
    }

    private bool TryPlayPreparedSingingBridge(
        AudioSource output,
        out string spokenText,
        out float duration)
    {
        spokenText = "";
        duration = 0f;
        if (output == null || m_PreparedSingingBridgeClip == null ||
            m_PreparedSingingBridgeConfidence < m_SingingBridgeMinConfidence ||
            string.IsNullOrWhiteSpace(m_PreparedSingingBridgeText))
            return false;

        spokenText = m_PreparedSingingBridgeText;
        duration = m_PreparedSingingBridgeClip.length;
        output.clip = m_PreparedSingingBridgeClip;
        output.loop = false;
        output.Play();
        m_PreparedSingingBridgePlayedThisTurn = true;
        return true;
    }

    private void ReleasePreparedSingingBridge(bool cancelSynthesis)
    {
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
            hint =
                "[本轮可撤销倾听状态；最终用户转写具有最高优先级。不要提及这段内部状态。\n" +
                "此前理解：" + (draft.understanding ?? "") + "\n" +
                "不确定点：" + (draft.uncertainty ?? "") + "\n" +
                "角色瞬时感受：" + (draft.inner_reaction ?? "") + "\n" +
                "临时模态判断：" + NormalizeObservedMode(draft.observed_mode) +
                "（置信度 " + Mathf.Clamp01(draft.mode_confidence).ToString("F2") + "）\n" +
                "已准备的候选回答：" + draft.draft + "\n" +
                "若最终文本改变了含义，必须修改或放弃候选回答。]";
            if (m_LogSpeculativeListening)
                Debug.Log($"[流式倾听] 最终一致度 {similarity:F2}，复用临时准备作为本轮提示");
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
            bool alreadySpoken = m_PreparedSingingBridgePlayedThisTurn;
            hint =
                "[本轮可撤销听歌状态；最终歌唱分析具有最高优先级，不要提及内部状态。\n" +
                "听歌期间的理解：" + (draft.understanding ?? "") + "\n" +
                "仍不确定：" + (draft.uncertainty ?? "") + "\n" +
                "角色瞬时感受：" + (draft.inner_reaction ?? "") + "\n" +
                "临时模态判断：" + NormalizeObservedMode(draft.observed_mode) +
                "（置信度 " + Mathf.Clamp01(draft.mode_confidence).ToString("F2") + "）\n" +
                "安全短开场：" + draft.draft + "\n" +
                (alreadySpoken
                    ? "这句短开场已经播放；正式回答请自然接续，不要逐字重复。]"
                    : "若稍后由快速回应播放这句，正式回答请自然接续；否则可自行改写。]");
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
        bool cognitiveSpeechVeto = m_EouCognitiveSpeechVeto ||
            HasStrongSpeculativeSpeechVeto();
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
    /// RTSpeechHandler通知"用户讲完了，clip正发往ASR"。
    /// 用 realtimeSinceStartup 而不是 Time.time，避免 Time.timeScale 干扰。
    /// </summary>
    public void MarkEOU()
    {
        m_EouTime = Time.realtimeSinceStartup;
        //上一轮的 formal-first 标志不能阻止本轮 EOU 快速回应。
        m_RealFirstAudioStarted = false;
        m_EouCognitiveSpeechVeto = HasStrongSpeculativeSpeechVeto();
        m_EouCognitiveSingingSupport = !m_EouCognitiveSpeechVeto &&
            HasStrongSpeculativeSingingSupport();
        m_EouTurnWasSinging = m_StreamingTurnIsSinging &&
            !m_StreamingSingingExitDetected && !m_EouCognitiveSpeechVeto;
        m_EouSingingRejectedByFinal = false;
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
    //待合成文本队列（LLM切句后推入，TTSSender消费）
    private Queue<string> m_PendingChunks = new Queue<string>();
    //待播放音频队列（TTSSender产出，AudioPlayer消费）
    private Queue<KeyValuePair<string, AudioClip>> m_PendingClips = new Queue<KeyValuePair<string, AudioClip>>();
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
        bool holdSpeechForHumBackResult = false)
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
        m_StreamComplete = false;
        m_TTSSenderDone = false;
        m_FirstChunkFlushed = false;
        m_FirstDeltaLogged = false;
        m_RoundIsInner = false;          //每个 fresh round 默认非内心；OnStreamDelta 看 <silent/> 前缀决定
        m_RoundInnerCheckDone = false;   //inner 检测专用门——每轮新决定，不被 m_FirstChunkFlushed 牵连
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
            //有EOU锚点时额外打"EOU→LLM出发"合计——这等于 ASR + 文本入队的总耗时
            if (m_EouTime > 0f)
            {
                float eouToLlm = Time.realtimeSinceStartup - m_EouTime;
                Debug.Log($"[Timing] EOU→LLM-request: {eouToLlm:F2}s (ASR+排队总和)");
            }
            Debug.Log("[Stream] T+0.00 LLM请求发出");
        }
        //眼睛睁着就附桌面截图(MaybeCaptureScreenForLLM 内部已检查总开关 + agent 状态)
        string imageUrl = MaybeCaptureScreenForLLM();
        DispatchFormalStream(_postWord, imageUrl, responseGeneration);
    }

    private float Elapsed() { return Time.realtimeSinceStartup - m_StreamStartTime; }

    private void DispatchFormalStream(string prompt, string imageUrl, int responseGeneration)
    {
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
        bool preparedSingingBridge = m_EouTurnWasSinging && !m_EouSingingRejectedByFinal &&
            TryPlayPreparedSingingBridge(m_AudioSource, out fillerText, out fillerDuration);
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
                ? $"[歌唱预反应] 播放预合成安全开场: \"{fillerText}\""
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
        m_EouFillerContext = "neutral";
        if (m_LogStreamTimings) Debug.Log($"[LatencyFiller] 取消EOU快速回应 ({reason})");
        if (OnAISpeakDone != null) OnAISpeakDone();
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
        //记忆写入标签的提取与应用不看 agent 开关——直接对话模式她也在记忆。
        //只在全文完成时做一次(chunk 级会重复计),剥净后再做后续解析。
        string afterMemTags;
        var memOps = MemoryTagParser.Extract(full ?? "", out afterMemTags);
        if (memOps != null && m_MemoryHub != null) m_MemoryHub.ApplyMemoryOps(memOps);

        //歌曲检索/记忆是角色能力，不是 Agent Loop 的调度语义。必须在判断 agent 开关前
        //提取并执行；否则用户关闭实时模式后，模型虽然给出标签却只会被剥掉而不落盘。
        string cleanFull = afterMemTags;
        AgentSongMemoryRequest songMemory = ExtractSongMemoryTag(ref cleanFull);
        AgentSongSearchRequest songSearch = ExtractSongSearchTag(ref cleanFull);
        AgentSongSingRequest songSing = ExtractSongSingTag(ref cleanFull);
        AgentHumBackRequest humBack = ExtractHumBackTag(ref cleanFull);
        if (humBack != null) m_ExplicitHumBackHandled = true;
        if (songSing != null) m_ExplicitSongSingHandled = true;
        if (m_SongMemoryAcknowledgementInFlight &&
            (songMemory != null || songSearch != null || songSing != null))
        {
            Debug.LogWarning("[SongMemory] 结果确认阶段忽略模型重复输出的歌曲工具标签");
            songMemory = null;
            songSearch = null;
            songSing = null;
        }
        if (songMemory != null)
        {
            if (string.Equals(songMemory.Action, "remember", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(songMemory.Title))
                songMemory.Title = ExtractExplicitSongTitle(m_LastUserMsg);
            m_ExplicitSongRememberHandled = true;
        }
        else if (ShouldFallbackToExplicitSongRemember())
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

        if (songMemory != null) BeginSongMemory(songMemory);
        else if (heldForSongMemory)
            CompleteSongMemoryImmediately("没有找到可用于保存的最近歌声音频，本次未写入本机曲库。");
        if (songSearch != null) BeginSongSearch(songSearch);
        if (songSing == null && ShouldFallbackToExplicitSongSing())
        {
            songSing = new AgentSongSingRequest
            {
                Title = ExtractRequestedSongTitle(m_LastUserMsg),
                Mode = IsRememberedSongContinuationRequest(m_LastUserMsg) ? "continue" : "memory",
                Reason = "用户明确要求演唱已记住的歌曲，但模型漏掉了 song_sing 标签",
            };
            m_ExplicitSongSingHandled = true;
            if (m_LogHumBack)
                Debug.LogWarning("[SongSing] 检测到明确曲库演唱请求，模型未调用 <song_sing/>，执行安全兜底");
        }
        if (songSing != null)
        {
            humBack = null;
            BeginSongSing(songSing);
        }
        if (songSing == null && humBack == null && ShouldFallbackToExplicitHumBack())
        {
            bool composePractice = IsPracticeCompositionRequest(m_LastUserMsg);
            humBack = new AgentHumBackRequest
            {
                Mode = composePractice ? "practice" : "echo",
                Reason = composePractice
                    ? "用户明确要求连续演唱练习片段，但模型漏掉了 practice hum_back 标签"
                    : "用户明确要求回哼最近旋律，但模型漏掉了 hum_back 标签",
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
                    m_LastAIMsgPlain = display;
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
            m_RoundInnerCheckDone = false;   //★ inner 检测重置——否则 chain round 永远进不了 inner 模式，
                                             //   LLM 想用 <silent/> 自救破局会失败、内心独白被 TTS 念出来
            string frame = BuildPerceptionFrame("continue-chain");
            m_ChatHistory.Add(frame);
            ClearRoundParsed();  //清掉本轮解析；下一轮 OnStreamComplete 会重新写
            if (m_LogAgentLoop)
                Debug.Log("[Agent] <continue/> → 链接下一帧(TTS pipeline 不间断)");
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
                m_PendingChunks.Enqueue(completed);
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
                m_PendingChunks.Enqueue(tail);
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

            string chunk = m_PendingChunks.Dequeue();
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
                m_PendingClips.Enqueue(new KeyValuePair<string, AudioClip>(chunk, pending));
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

            string text = m_PendingChunks.Dequeue();
            //真正发声前的最后一道门，确保工具标签不会进入 TTS 或跟随 TTS 出现在字幕。
            text = StripAgentTagsForTTS(text);
            if (string.IsNullOrEmpty(text) || IsPurePunctuation(text)) continue;

            bool started = false;
            bool completed = false;
            bool succeeded = false;
            float audioDuration = 0f;

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
                    if (m_EouTime > 0f)
                    {
                        float total = Time.realtimeSinceStartup - m_EouTime;
                        Debug.Log($"[Timing] ★ EOU→流式首音 总延迟: {total:F2}s");
                    }
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

            var kv = m_PendingClips.Dequeue();
            AudioClip clip = kv.Value;
            string text = kv.Key;
            if (clip == null) continue;

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
                    if (m_EouTime > 0f)
                    {
                        float total = Time.realtimeSinceStartup - m_EouTime;
                        Debug.Log($"[Timing] ★ EOU→首音 总延迟: {total:F2}s (核心体感指标，<1.5s 像人)");
                    }
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
    [Tooltip("连续 AI 轮次硬上限(无用户回应)。超过强行等用户开口才再 tick——防独白循环。" +
        "讲故事/详述场景下 LLM 会用 <continue/> 链多轮，所以这个值要给得宽一点")]
    [SerializeField] private int m_MaxConsecutiveAITurns = 8;
    [Tooltip("感知帧里'你最近发言'最多展示多少条——给 LLM 看清自己最近说了什么，避免重复")]
    [SerializeField] private int m_RecentAIUtterancesShown = 3;
    [Tooltip("感知帧里 AI 自身发言摘要的字符截断上限")]
    [SerializeField] private int m_AIUtteranceTruncateChars = 60;
    [Tooltip("环境出现非语音 spike 时是否拉前下次 tick(被外界拽回注意力)")]
    [SerializeField] private bool m_BringForwardOnSpike = true;
    [Tooltip("打印 agent loop 调度日志")]
    [SerializeField] private bool m_LogAgentLoop = true;

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
    [Tooltip("把用户旋律整体平移到角色参考声线的自然音区，同时保留音程与节奏。跨性别/跨音区转换应开启。")]
    [SerializeField] private bool m_HumSVCAutoF0Adjust = true;
    [Tooltip("整体升降调；0 会严格保留用户原调。")]
    [Range(-12, 12)] [SerializeField] private int m_HumSVCSemitoneShift = 0;
    [Tooltip("神经转换最长等待时间；首次下载模型会明显更久。")]
    [Range(30, 600)] [SerializeField] private int m_HumSVCTimeoutSeconds = 600;
    [Tooltip("神经转换失败时是否退回旧 TD-PSOLA。默认关闭，避免角色播放不像人的伪哼唱。")]
    [SerializeField] private bool m_AllowLegacyHumFallback = false;
    [Tooltip("可选：手工指定一段角色稳定发出的“嗯/ん/mm”长音。留空时首次回哼由当前TTS静默生成并缓存；合成器会保留其声线、气息与共振峰。")]
    [SerializeField] private AudioClip m_CharacterHumCarrierClip;
    [Tooltip("一次回唱允许的完整演唱时长。超过上限时必须明确失败，禁止静默裁掉歌曲开头。")]
    [Range(5f, 120f)] [SerializeField] private float m_HumBackMaxSeconds = 60f;
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

    // 一次性警告标志,避免每帧刷屏
    private bool m_MemoryHubMissingWarned = false;

    // —— 运行时状态 ——
    private bool m_AgentRunning = false;
    private bool m_AgentRoundInFlight = false;        // 一帧已派给 LLM、等回复中
    private bool m_AgentCurrentRoundIsTick = false;   // 当前 round 是 tick 触发(true) 还是用户开口触发(false)
    private Coroutine m_PendingTickCo = null;
    private float m_LastUserTurnTime = -1f;
    private float m_LastAITurnTime = -1f;
    private string m_LastUserMsg = "";
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
    private bool m_SongSearchInFlight = false;
    private bool m_SongSearchResultPending = false;
    private string m_LastSongSearchResult = "";
    private float m_LastSongSearchRequestTime = -999f;
    private string m_LastSongSearchSignature = "";
    private int m_SongSearchGeneration = 0;
    private bool m_SongMemoryInFlight = false;
    private bool m_SongMemoryResultPending = false;
    private string m_LastSongMemoryResult = "";
    private float m_LastSongMemoryRequestTime = -999f;
    private string m_LastSongMemorySignature = "";
    private int m_SongMemoryGeneration = 0;
    private bool m_ExplicitSongRememberHandled = false;
    private Coroutine m_SongMemoryAcknowledgementCoroutine;
    private bool m_SongMemoryAcknowledgementInFlight = false;
    private bool m_SongSingInFlight = false;
    private int m_SongSingGeneration = 0;
    private bool m_ExplicitSongSingHandled = false;
    private bool m_HumBackPending = false;
    private bool m_HumBackPreparingCarrier = false;
    private bool m_HumBackPlaying = false;
    private int m_HumBackGeneration = 0;
    private float[] m_PendingHumTimeline;
    private float m_PendingHumFrameSeconds = 0.10f;
    private string m_PendingHumLanguage = "";
    private string m_PendingHumReason = "";
    private string m_PendingHumMode = "echo";
    private byte[] m_PendingHumSourceWav;
    private bool m_PendingHumIsPracticeComposition = false;
    private bool m_PendingHumIsCatalogSong = false;
    private bool m_PendingHumIsCatalogContinuation = false;
    private string m_PendingCatalogSongName = "";
    private int m_PendingHumPerformanceSeed = 1234;
    private int m_PendingHumSemitoneOffset = 0;
    private float m_PendingHumRmsMixRate = 0.25f;
    private float m_PendingHumProtect = 0.33f;
    private string m_PendingHumVariationDiagnostic = "";
    private int m_HumPerformanceCounter = 0;
    private AudioClip m_GeneratedHumCarrierClip;
    private AudioClip m_ActiveHumBackClip;
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
    private float m_StreamingHumRmsMixRate = 0.25f;
    private float m_StreamingHumProtect = 0.33f;
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
    private System.Diagnostics.Process m_HumSVCServerProcess;
    private bool m_HumSVCStartupInProgress = false;
    private bool m_HumSVCStartupSucceeded = false;
    private string m_HumSVCStartupDetail = "尚未检查";
    private Coroutine m_HumBackPlaybackCoroutine;
    private bool m_HumBackNeedsHistoryEntry = false;
    private bool m_ExplicitHumBackHandled = false;
    private bool m_WaitingForRequestedSingAlong = false;
    private float m_SingAlongRequestArmedAt = -999f;
    private bool m_HumBackResultPending = false;
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
        m_LastFocus = "";
        m_ConsecutiveAITurns = 0;
        m_LastSpikeTime = -1f;
        m_LastSpikePeakRms = 0f;
        m_SongSearchGeneration++;
        m_SongSearchInFlight = false;
        m_SongSearchResultPending = false;
        m_LastSongSearchResult = "";
        m_LastSongSearchRequestTime = -999f;
        m_LastSongSearchSignature = "";
        m_SongMemoryGeneration++;
        m_SongMemoryInFlight = false;
        m_SongMemoryResultPending = false;
        m_LastSongMemoryResult = "";
        m_LastSongMemoryRequestTime = -999f;
        m_LastSongMemorySignature = "";
        m_ExplicitSongRememberHandled = false;
        m_ExplicitHumBackHandled = false;
        m_ExplicitSongSingHandled = false;
        m_WaitingForRequestedSingAlong = false;
        m_SingAlongRequestArmedAt = -999f;
        m_HumBackResultPending = false;
        m_LastHumBackResult = "";
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (senseVoice != null) senseVoice.BeginSingingPracticeSession();
        m_AgentEyesOpen = false;        //每次启动默认闭眼，让 LLM 自己决定何时 <look/>
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
        if (m_PendingTickCo == null) return;     //没有待办 tick，不存在"拉前"
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
        ResetStreamingHumBackPrefix("new-user-turn", true);
        ResetSpeculativeTurn();
        m_EouTurnWasSinging = false;
        m_EouSingingRejectedByFinal = false;
        m_EouCognitiveSpeechVeto = false;
        m_EouCognitiveSingingSupport = false;
        m_EouFillerContext = "neutral";
        if (m_PendingTickCo != null)
        {
            StopCoroutine(m_PendingTickCo);
            m_PendingTickCo = null;
        }
        m_ConsecutiveAITurns = 0;
        if (m_LogAgentLoop) Debug.Log("[Agent] 用户开口 → 待 tick 撤销, 连续 AI 轮次清零");
    }

    /// <summary>
    /// 调度下一次 tick。requestedSec 来源：LLM 的 &lt;next in="Ns"/&gt; 或兜底 m_DefaultTickSec。
    /// 自动 clamp 到 [m_MinTickSec, m_MaxTickSec]。
    /// </summary>
    private void ScheduleNextTick(float requestedSec, string reason)
    {
        if (!m_AgentRunning || m_AgentGracefulShutdownPending) return;
        if (m_PendingTickCo != null)
        {
            StopCoroutine(m_PendingTickCo);
            m_PendingTickCo = null;
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
        FireTick("scheduled");
    }

    /// <summary>
    /// 真正发起一次 tick：构造感知帧 → 投给 LLM 流式管线 →
    /// 沿现有 Stream 通路走 TTS，OnStreamComplete 解析尾部标签。
    /// triggerReason 透到感知帧里告诉 LLM 这一帧是怎么来的。
    /// </summary>
    private void FireTick(string triggerReason)
    {
        if (!m_AgentRunning || m_AgentGracefulShutdownPending) return;
        //角色还在说话(<continue/>链上一帧还没收尾) → 让流水线走完再排
        if (IsAISpeaking || m_AgentRoundInFlight)
        {
            if (m_LogAgentLoop) Debug.Log($"[Agent] FireTick 排队等待(speaking={IsAISpeaking}, inflight={m_AgentRoundInFlight})");
            ScheduleNextTick(m_MinTickSec, "still-busy");
            return;
        }
        //连续 AI 轮次硬上限——工具结果仍允许回到角色手里一次，否则可能“查到了但不说”
        bool isSongToolResult = string.Equals(triggerReason, "song-search-result", StringComparison.Ordinal) ||
            string.Equals(triggerReason, "song-memory-result", StringComparison.Ordinal);
        if (m_ConsecutiveAITurns >= m_MaxConsecutiveAITurns && !isSongToolResult)
        {
            if (m_LogAgentLoop) Debug.Log($"[Agent] 连续 AI 轮次={m_ConsecutiveAITurns}≥{m_MaxConsecutiveAITurns}，停止主动tick，等用户开口");
            return;
        }

        string frame = BuildPerceptionFrame(triggerReason);
        m_ChatHistory.Add(frame);
        m_AgentRoundInFlight = true;
        m_AgentCurrentRoundIsTick = true;
        ClearRoundParsed();

        if (m_LogAgentLoop) Debug.Log("[Agent] FireTick → " + frame.Replace('\n', ' '));

        m_TextBack.text = "";  //tick 帧不应该显示"正在思考中"——会让用户莫名其妙
        StartStreaming(frame, false);
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
        m_ExplicitSongSingHandled = false;
        //情境召回:提及扫描同步生效(本帧可见),语境嵌入异步、作用于后续帧
        if (m_MemoryHub != null && m_EnableMemoryRecall)
            m_MemoryHub.NotifyUserUtterance(m_LastUserMsg);
        m_AgentRoundInFlight = m_AgentRunning;
        m_AgentCurrentRoundIsTick = false;
        ClearRoundParsed();

        //agent 没启动就不拼帧，保持向后兼容
        if (!m_AgentRunning) return userText ?? "";

        string frame = BuildPerceptionFrame("user-spoke");
        return frame + "\n" + (userText ?? "");
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

        //排下一帧——读本 chain 最后一节解析到的 m_Round*
        //(chain 中段 OnStreamComplete 会 ClearRoundParsed 清掉自己；终止节没清，所以这里能读)
        if (m_SongMemoryResultPending && m_BringForwardOnSongSearchResult)
        {
            ScheduleNextTick(m_MinTickSec, "song-memory-result");
        }
        else if (m_SongSearchResultPending && m_BringForwardOnSongSearchResult)
        {
            ScheduleNextTick(m_MinTickSec, "song-search-result");
        }
        else if (m_HumBackResultPending && m_BringForwardOnSongSearchResult)
        {
            ScheduleNextTick(m_MinTickSec, "song-singing-result");
        }
        else if (m_RoundContinue)
        {
            //理论上不该走到——willChain 路径会直接续派，不会触发收 round。
            //兜底：万一 ConsecutiveAITurns 达上限被强行终止，按短间隔再排。
            ScheduleNextTick(m_MinTickSec, "continue-fallback");
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
            if (!string.IsNullOrEmpty(m_LastUserMsg))
                sb.Append($" (\"{TruncateForFrame(m_LastUserMsg, 40)}\")");
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
            m_SongMemoryResultPending = false;
        }
        if (m_SongSingInFlight)
        {
            sb.Append("\n长期歌曲演唱工具: 正在从本地曲库定位真实音频；不要声称已经唱出或续唱成功");
        }
        if (m_HumBackResultPending && !string.IsNullOrEmpty(m_LastHumBackResult))
        {
            sb.Append("\n旋律回哼工具结果: ");
            sb.Append(m_LastHumBackResult);
            m_HumBackResultPending = false;
        }
        SenseVoiceSpeechToText practiceSenseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        if (practiceSenseVoice != null && practiceSenseVoice.PracticePhraseCount > 0)
        {
            sb.Append($"\n练唱会话: 已按最终确认顺序记录 {practiceSenseVoice.PracticePhraseCount} 段；" +
                      "mode=practice 可把这些段先合成一条连续源音频，再统一转换成你的声线");
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
                string memBlock = m_MemoryHub.BuildMemoryMap();
                if (!string.IsNullOrEmpty(memBlock)) sb.Append(memBlock);
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

        //本帧是怎么来的
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
        return !m_AgentCurrentRoundIsTick && IsExplicitSongRememberRequest(m_LastUserMsg);
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

    private static string ExtractRequestedSongTitle(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return "";
        string[] patterns =
        {
            @"(?:唱|演唱|哼)(?:一下|一遍|一段|一首|出来|给我听|给我唱)?\s*[《“""'「『]?(?<title>[A-Za-z0-9\p{L}][^，,。！？!?；;\r\n《》“”""'「」『』]{0,59})",
            @"(?:sing|perform|hum)\s+(?<title>[A-Za-z0-9][A-Za-z0-9 _'\-]{0,59})",
            @"(?<title>[A-Za-z0-9\p{L}][^，,。！？!?；;\r\n《》“”""'「」『』]{0,59})\s*(?:を)?(?:歌って|歌える|歌う)"
        };
        foreach (string pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                utterance, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            string title = match.Groups["title"].Value.Trim();
            title = System.Text.RegularExpressions.Regex.Replace(
                title,
                @"(?:吧|吗|呢|好不好|可以吗|能不能|please|for me|给我听|一下|一遍)$",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            string generic = title.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(title) || generic == "歌" || generic == "这首歌" ||
                generic == "这段" || generic == "刚才" || generic == "接下来" ||
                generic == "the song" || generic == "it")
                continue;
            return title;
        }
        return "";
    }

    private static bool IsExplicitRememberedSongSingRequest(string utterance)
    {
        string lower = (utterance ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower) || IsHumBackCancellation(lower)) return false;
        bool hasSingIntent = lower.Contains("唱") || lower.Contains("哼") ||
            lower.Contains("sing") || lower.Contains("hum") || lower.Contains("歌って") ||
            lower.Contains("続きを歌");
        if (!hasSingIntent) return false;
        if (IsRememberedSongContinuationRequest(lower)) return true;
        string[] memoryCues =
        {
            "记住的歌", "已经记住", "你记得的歌", "曲库", "以前学", "之前学",
            "remembered song", "from memory", "song library", "覚えた歌", "記憶の歌"
        };
        foreach (string cue in memoryCues)
            if (lower.Contains(cue)) return true;
        return !string.IsNullOrWhiteSpace(ExtractRequestedSongTitle(utterance));
    }

    private bool ShouldFallbackToExplicitSongSing()
    {
        return m_EnableAutonomousRememberedSongSinging && !m_ExplicitSongSingHandled &&
            !m_AgentCurrentRoundIsTick && IsExplicitRememberedSongSingRequest(m_LastUserMsg);
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
            "连起来唱", "连续唱", "接起来唱", "串起来唱", "合起来唱", "整段唱",
            "完整唱", "把几段唱", "把这些段唱", "刚才几段", "练习的几段", "练过的几段",
            "combine the phrases", "join the phrases", "sing them together", "sing the whole sequence",
            "フレーズを繋", "つなげて歌", "続けて歌", "全部通して歌"
        };
        foreach (string phrase in requests)
            if (lower.Contains(phrase)) return true;
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
        if (!m_WaitingForRequestedSingAlong) return false;
        if (Time.realtimeSinceStartup - m_SingAlongRequestArmedAt <= 300f) return true;
        m_WaitingForRequestedSingAlong = false;
        if (m_LogHumBack) Debug.Log("[HumBack] 等待跟唱已超过 5 分钟，自动取消");
        return false;
    }

    private void ArmSingAlongForNextPerformance()
    {
        bool startsNewPractice = !HasActiveSingAlongRequest();
        m_WaitingForRequestedSingAlong = true;
        m_SingAlongRequestArmedAt = Time.realtimeSinceStartup;
        m_ExplicitHumBackHandled = true;
        if (startsNewPractice)
        {
            SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
                ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
                : null;
            if (senseVoice != null) senseVoice.BeginSingingPracticeSession();
        }
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

    private bool HasPracticeCompositionMaterial()
    {
        SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
            ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
            : null;
        return senseVoice != null && senseVoice.PracticePhraseCount >= 2;
    }

    /// <summary>
    /// A real singing request must not stream ordinary TTS lyrics before the model's tool tag
    /// arrives. Hold the textual body only when a playable performance already exists, or when
    /// an armed invitation has just been fulfilled by a confirmed singing turn.
    /// </summary>
    private bool ShouldHoldSpeechForExplicitHumBack()
    {
        if (!m_EnableAutonomousHumBack || m_AgentCurrentRoundIsTick ||
            IsHumBackCancellation(m_LastUserMsg) || IsCurrentTurnSpokenSingingExit())
            return false;

        if (m_EnableAutonomousRememberedSongSinging &&
            IsExplicitRememberedSongSingRequest(m_LastUserMsg))
            return true;

        bool confirmedSinging = IsCurrentTurnConfirmedSinging();
        if (!confirmedSinging && IsPracticeCompositionRequest(m_LastUserMsg))
            return HasPracticeCompositionMaterial();
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
        if (!m_EnableAutonomousHumBack || m_ExplicitHumBackHandled ||
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
        if (IsPracticeCompositionRequest(m_LastUserMsg)) return HasPracticeCompositionMaterial();
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
        public string Reason = "";
    }

    private class AgentSongSingRequest
    {
        public string SongId = "";
        public string Title = "";
        public string Mode = "memory";
        public string Reason = "";
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
        };
        if (string.IsNullOrWhiteSpace(request.Mode)) request.Mode = "memory";
        text = s_SongSingTagRegex.Replace(text, "").Trim();
        return request;
    }

    private static readonly System.Text.RegularExpressions.Regex s_HumBackTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<hum_back\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private AgentHumBackRequest ExtractHumBackTag(ref string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var match = s_HumBackTagRegex.Match(text);
        if (!match.Success) return null;
        string attrs = match.Groups["attrs"].Value;
        AgentHumBackRequest request = new AgentHumBackRequest
        {
            Mode = ReadToolAttribute(attrs, "mode"),
            Reason = ReadToolAttribute(attrs, "reason"),
        };
        if (string.IsNullOrWhiteSpace(request.Mode)) request.Mode = "echo";
        text = s_HumBackTagRegex.Replace(text, "").Trim();
        return request;
    }

    private static readonly System.Text.RegularExpressions.Regex s_SongSearchTagRegex =
        new System.Text.RegularExpressions.Regex(
            @"<song_search\b(?<attrs>[^>]*)>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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

    private void BeginSongMemory(AgentSongMemoryRequest request)
    {
        if (request == null) return;
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
            }
            else if (result.Action == "remember")
            {
                string name = string.IsNullOrWhiteSpace(result.DisplayName)
                    ? "未命名旋律"
                    : result.DisplayName;
                m_LastSongMemoryResult =
                    $"已在本机记住“{name}”，歌曲ID={result.SongId}，" +
                    $"录音样本数={result.ReferenceCount}，独立歌曲段数={result.UniqueSegmentCount}。";
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
                m_LastSongMemoryResult =
                    $"已把歌曲ID={result.SongId}及其本机WAV改名为“{result.DisplayName}”。";
            }
            else if (result.Action == "forget")
            {
                m_LastSongMemoryResult =
                    $"已从本机曲库删除歌曲ID={result.SongId}（{result.DisplayName}）及其受管WAV。";
            }
            else
            {
                m_LastSongMemoryResult = "歌曲记忆操作已完成。";
            }
            m_SongMemoryResultPending = true;
            if (m_LogAgentLoop) Debug.Log("[SongMemory] " + m_LastSongMemoryResult);
            QueueSongMemoryAcknowledgement(result != null && result.Ok);
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

        m_SongMemoryResultPending = false;
        m_SongMemoryAcknowledgementInFlight = true;
        string toolFrame =
            "[本机歌曲记忆工具最终结果；这不是用户的新发言]\n" + resultText + "\n" +
            (success
                ? "本机操作已经成功。请严格按上面的最终结果自然确认；只有结果明确写着“已在本机记住”时，才可以说已经记住。不要再次调用歌曲工具。"
                : "落盘没有成功。必须如实说明这次尚未记住，不得声称已保存；不要再次调用歌曲工具。") +
            "\n只回复一到两句自然口语，不要解释系统流程。";

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
        StartStreaming(toolFrame, false, null, false);
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
            m_EnableNeuralHumSVC && m_IsVoiceMode && HasActiveSingAlongRequest() &&
            !HasStrongSpeculativeSpeechVeto() &&
            !m_HumBackPrefixPreparing && m_PreparedHumBackPrefixClip == null &&
            !m_FastHumBackEouStaged && !m_FastHumBackActive &&
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
            out m_StreamingHumProtect);
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
    /// A spoken request to join the already practised phrases is a deterministic local
    /// audio action. Starting it here avoids an extra LLM promise and saves one response
    /// round before the comparatively expensive voice conversion.
    /// </summary>
    private bool TryHandleDirectPracticeCompositionTurn()
    {
        if (!m_EnableAutonomousHumBack || !m_IsVoiceMode || m_AgentCurrentRoundIsTick ||
            IsCurrentTurnConfirmedSinging() || !IsPracticeCompositionRequest(m_LastUserMsg) ||
            !HasPracticeCompositionMaterial())
            return false;

        QueueHumBack(new AgentHumBackRequest
        {
            Mode = "practice",
            Reason = "用户明确要求把当前练唱会话中已确认的片段连续唱出",
        });
        if (!m_HumBackPending) return false;
        m_ExplicitHumBackHandled = true;
        CancelPendingEouLatencyFiller("direct-practice-composition", true);
        bool started = TryBeginPendingHumBack();
        if (started && m_LogHumBack)
            Debug.Log("[HumBack/Practice] 跳过LLM决策，直接启动连续练唱转换");
        return started;
    }

    /// <summary>
    /// Runs after SendDataInternal has appended the authoritative final user turn.
    /// Returning true means this deterministic tool round owns the response and LLM
    /// generation is intentionally skipped.
    /// </summary>
    private bool TryHandleDirectSingAlongTurn()
    {
        if (m_EouCognitiveSpeechVeto)
        {
            RejectFastHumBackAfterFinal("speculative-cognition-classified-speech");
            m_ExplicitHumBackHandled = true;
            if (m_LogHumBack)
                Debug.Log("[HumBack] 心里话高置信度判断为普通说话；跳过自动复唱，交给LLM正常回应");
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

        if (confirmedSinging && armed)
        {
            SenseVoiceSpeechToText senseVoice = m_ChatSettings != null
                ? m_ChatSettings.m_SpeechToText as SenseVoiceSpeechToText
                : null;
            int phraseCount;
            if (senseVoice != null &&
                senseVoice.CommitRecentSingingToPracticeSession(out phraseCount) &&
                m_LogHumBack)
                Debug.Log($"[HumBack/Practice] 已记录最终确认片段 sequence={phraseCount}");
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
        return started;
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
        m_StreamingHumRmsMixRate = 0.25f;
        m_StreamingHumProtect = 0.33f;
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
            return (Environment.TickCount * 397) ^ (m_HumPerformanceCounter * 7919) ^
                Guid.NewGuid().GetHashCode();
        }
    }

    private static void CreateHumPerformanceProfile(
        int seed,
        out int semitoneOffset,
        out float rmsMixRate,
        out float protect)
    {
        System.Random random = new System.Random(seed);
        // Most human repeats stay in the same key. Rare +/-1 semitone variants add a
        // different placement without changing the melody; dynamics/timbre vary every take.
        double keyChoice = random.NextDouble();
        semitoneOffset = keyChoice < 0.10 ? -1 : keyChoice > 0.90 ? 1 : 0;
        rmsMixRate = 0.19f + (float)random.NextDouble() * 0.14f;
        protect = 0.29f + (float)random.NextDouble() * 0.09f;
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

    private void BeginSongSing(AgentSongSingRequest request)
    {
        if (request == null) return;
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

        int performanceSeed = NextHumPerformanceSeed();
        int semitoneOffset;
        float rmsMixRate;
        float protect;
        CreateHumPerformanceProfile(
            performanceSeed, out semitoneOffset, out rmsMixRate, out protect);
        int generation = ++m_SongSingGeneration;
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
            m_HumBackMaxSeconds,
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
                    RecordHumBackResult(
                        "未执行：" + detail + "。不得声称已经从记忆中唱出或续唱成功。",
                        true);
                    if (m_LogHumBack) Debug.LogWarning("[SongSing] " + detail);
                    CompleteSongSingToolRoundIfIdle();
                    return;
                }

                m_PendingHumTimeline = result.MidiTimeline;
                m_PendingHumFrameSeconds = Mathf.Clamp(result.FrameSeconds, 0.02f, 0.25f);
                m_PendingHumLanguage = "";
                m_PendingHumReason = request.Reason ?? "";
                m_PendingHumMode = result.Continuation ? "continue" : "memory";
                m_PendingHumSourceWav = result.WavBytes;
                m_PendingHumIsPracticeComposition = false;
                m_PendingHumIsCatalogSong = true;
                m_PendingHumIsCatalogContinuation = result.Continuation;
                m_PendingCatalogSongName = string.IsNullOrWhiteSpace(result.DisplayName)
                    ? (string.IsNullOrWhiteSpace(result.Title) ? result.SongId : result.Title)
                    : result.DisplayName;
                m_PendingHumPerformanceSeed = performanceSeed;
                m_PendingHumSemitoneOffset = semitoneOffset;
                m_PendingHumRmsMixRate = rmsMixRate;
                m_PendingHumProtect = protect;
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
            });
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

    private void QueueHumBack(AgentHumBackRequest request)
    {
        if (!m_EnableAutonomousHumBack || !m_IsVoiceMode || request == null) return;
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

        bool composePractice = IsPracticeHumMode(request.Mode) ||
            IsPracticeCompositionRequest(m_LastUserMsg);
        bool confirmedSinging = IsCurrentTurnConfirmedSinging();
        if (!confirmedSinging && IsHumBackCancellation(m_LastUserMsg))
        {
            m_WaitingForRequestedSingAlong = false;
            if (m_LogHumBack) Debug.Log("[HumBack] 用户取消了待唱请求，忽略工具调用");
            return;
        }
        if (!composePractice && !confirmedSinging && IsSingAlongInvitation(m_LastUserMsg))
        {
            ArmSingAlongForNextPerformance();
            return;
        }
        if (HasActiveSingAlongRequest())
        {
            if (!confirmedSinging && !composePractice && !IsExplicitHumBackRequest(m_LastUserMsg))
            {
                if (m_LogHumBack)
                    Debug.Log("[HumBack] 仍在等待真实歌声，忽略本轮提前生成的回哼工具调用");
                return;
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
        CreateHumPerformanceProfile(
            performanceSeed, out semitoneOffset, out rmsMixRate, out protect);

        float[] timeline;
        float frameSeconds;
        string language;
        byte[] sourceWav = null;
        string variationDiagnostic = "";
        int phraseCount = 1;
        float sourceDuration = 0f;
        if (composePractice)
        {
            SenseVoiceSpeechToText.PracticeComposition composition;
            string failure;
            if (!senseVoice.TryBuildSingingPracticeComposition(
                    performanceSeed,
                    m_HumBackMaxSeconds,
                    out composition,
                    out failure) || composition == null)
            {
                RecordHumBackResult(
                    "未执行：" + failure + "。不得声称已经把练习片段连续唱出。",
                    true);
                if (m_LogHumBack) Debug.LogWarning("[HumBack/Practice] " + failure);
                return;
            }
            timeline = composition.MidiTimeline;
            frameSeconds = composition.FrameSeconds;
            language = composition.Language;
            sourceWav = composition.WavBytes;
            phraseCount = composition.PhraseCount;
            sourceDuration = composition.DurationSeconds;
            variationDiagnostic = composition.VariationDiagnostic;
        }
        else
        {
            if (!senseVoice.TryGetRecentSingingPerformance(
                    out timeline, out frameSeconds, out language) ||
                timeline == null || timeline.Length == 0)
            {
                RecordHumBackResult(
                    "未执行：当时没有取得可播放的旋律。不得声称已经回哼、跟唱或让用户评价效果。",
                    true);
                if (m_LogHumBack)
                    Debug.LogWarning("[HumBack] 最近没有仍在保留期内的可演奏歌唱旋律，本次不回哼");
                return;
            }
            senseVoice.TryGetVariedRecentSingingAudio(
                performanceSeed, out sourceWav, out variationDiagnostic);
            sourceDuration = timeline.Length * Mathf.Clamp(frameSeconds, 0.02f, 0.25f);
        }

        m_HumBackResultPending = false;
        m_LastHumBackResult = "";
        m_PendingHumTimeline = timeline;
        m_PendingHumFrameSeconds = Mathf.Clamp(frameSeconds, 0.02f, 0.25f);
        m_PendingHumLanguage = language ?? "";
        m_PendingHumReason = request.Reason ?? "";
        m_PendingHumMode = composePractice ? "practice" : "echo";
        m_PendingHumSourceWav = sourceWav;
        m_PendingHumIsPracticeComposition = composePractice;
        m_PendingHumIsCatalogSong = false;
        m_PendingHumIsCatalogContinuation = false;
        m_PendingCatalogSongName = "";
        m_PendingHumPerformanceSeed = performanceSeed;
        m_PendingHumSemitoneOffset = semitoneOffset;
        m_PendingHumRmsMixRate = rmsMixRate;
        m_PendingHumProtect = protect;
        m_PendingHumVariationDiagnostic = variationDiagnostic ?? "";
        m_HumBackPending = true;
        if (m_LogHumBack)
        {
            float duration = timeline.Length * m_PendingHumFrameSeconds;
            Debug.Log($"[HumBack] 已排队 mode={m_PendingHumMode} phrases={phraseCount} " +
                      $"frames={timeline.Length} melody={duration:F1}s source={sourceDuration:F1}s " +
                      $"seed={performanceSeed} shiftOffset={semitoneOffset} " +
                      $"rms={rmsMixRate:F2} protect={protect:F2} language={m_PendingHumLanguage} " +
                      $"variation=\"{m_PendingHumVariationDiagnostic}\" " +
                      $"reason=\"{m_PendingHumReason}\"");
        }
    }

    /// <summary>
    /// 在当前文字回复完全播放后启动旋律回哼。准备载体的阶段也算角色正在回应，
    /// 因而实时 VAD 会继续走 barge-in 路径，用户可以随时打断。
    /// </summary>
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

        if (m_EnableNeuralHumSVC)
        {
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
            if ((sourceWav != null ||
                 (senseVoice != null && senseVoice.TryGetRecentSingingAudio(out sourceWav))) &&
                sourceWav != null && sourceWav.Length > 44 && !string.IsNullOrWhiteSpace(targetPath))
            {
                m_HumBackPreparingCarrier = true;
                StartCoroutine(RequestNeuralHumBack(generation, sourceWav, targetPath));
                return true;
            }

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

    private IEnumerator RequestNeuralHumBack(int generation, byte[] sourceWav, string targetPath)
    {
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
            m_HumBackPreparingCarrier = false;
            string failure = "歌声转换服务未就绪: " + serviceDetail;
            if (m_LogHumBack) Debug.LogWarning("[HumBack/SVC] " + failure);
            if (m_AllowLegacyHumFallback) BeginLegacyHumBack(generation);
            else FinishHumBack(generation, false, failure);
            yield break;
        }

        string requestId = Guid.NewGuid().ToString("N");
        WWWForm form = new WWWForm();
        form.AddBinaryData("source_audio", sourceWav, "recent_singing.wav", "audio/wav");
        form.AddField("target_path", targetPath);
        form.AddField("request_id", requestId);
        form.AddField("diffusion_steps", Mathf.Clamp(m_HumSVCDiffusionSteps, 4, 30));
        form.AddField("auto_f0_adjust", m_HumSVCAutoF0Adjust ? "true" : "false");
        form.AddField("semitone_shift", Mathf.Clamp(
            m_HumSVCSemitoneShift + m_PendingHumSemitoneOffset, -12, 12));
        form.AddField("performance_seed", m_PendingHumPerformanceSeed);
        form.AddField("rms_mix_rate", InvariantFloat(m_PendingHumRmsMixRate));
        form.AddField("protect", InvariantFloat(m_PendingHumProtect));
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
                Debug.Log($"[HumBack/SVC] 开始转换 sourceBytes={sourceWav.Length} " +
                          $"steps={m_HumSVCDiffusionSteps} autoF0={m_HumSVCAutoF0Adjust} " +
                          $"seed={m_PendingHumPerformanceSeed} rms={m_PendingHumRmsMixRate:F2} " +
                          $"protect={m_PendingHumProtect:F2} " +
                          $"target=\"{targetPath}\"");

            yield return request.SendWebRequest();
            if (m_ActiveHumSVCRequest == request)
            {
                m_ActiveHumSVCRequest = null;
                m_ActiveHumSVCRequestId = "";
            }

            if (generation != m_HumBackGeneration) yield break;
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
                if (m_AllowLegacyHumFallback) BeginLegacyHumBack(generation);
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
                if (m_AllowLegacyHumFallback) BeginLegacyHumBack(generation);
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
                if (m_AllowLegacyHumFallback) BeginLegacyHumBack(generation);
                else FinishHumBack(generation, false, failure);
                yield break;
            }

            converted.name = "NeEEvA_Neural_HumBack";
            ApplyHumBackGain(converted);
            string backend = request.GetResponseHeader("X-SVC-Backend") ?? "seed-vc";
            string device = request.GetResponseHeader("X-SVC-Device") ?? "unknown";
            string serverElapsed = request.GetResponseHeader("X-SVC-Elapsed-Seconds") ?? "?";
            string autoF0 = request.GetResponseHeader("X-SVC-Auto-F0-Adjust") ?? "?";
            string seed = request.GetResponseHeader("X-SVC-Seed") ?? "?";
            float unityElapsed = Time.realtimeSinceStartup - startedAt;
            PlayHumBackClip(
                generation,
                converted,
                $"neural SVC complete source={sourceSeconds}s output={outputSeconds}s, " +
                $"backend={backend}, device={device}, autoF0={autoF0}, seed={seed}, " +
                $"server={serverElapsed}s, total={unityElapsed:F2}s");
        }
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
        m_HumBackNeedsHistoryEntry = false;
        m_HumBackPending = false;
        m_HumBackPreparingCarrier = false;
        m_HumBackPlaying = false;
        m_PendingHumTimeline = null;
        m_PendingHumLanguage = "";
        m_PendingHumReason = "";
        m_PendingHumMode = "echo";
        m_PendingHumSourceWav = null;
        m_PendingHumIsPracticeComposition = false;
        m_PendingHumIsCatalogSong = false;
        m_PendingHumIsCatalogContinuation = false;
        m_PendingCatalogSongName = "";
        m_PendingHumVariationDiagnostic = "";
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
        IsAISpeaking = false;
        m_LastVoiceOutputEndedRealtime = Time.realtimeSinceStartup;
        m_TextBack.text = "";
        SetAnimator("state", 0);

        string catalogLabel = string.IsNullOrWhiteSpace(catalogSongName)
            ? "记忆中的歌曲"
            : "记忆中的「" + catalogSongName + "」";
        string actionText = completed
            ? (wasCatalogSong
                ? (wasCatalogContinuation
                    ? $"♪（接着唱出了{catalogLabel}的后续）"
                    : $"♪（唱出了{catalogLabel}）")
                : (wasPracticeComposition
                    ? "♪（把刚才练习的几段连续唱了一遍）"
                    : "♪（轻声回哼了刚才的旋律）"))
            : (wasCatalogSong
                ? $"（尝试演唱{catalogLabel}，但音频没有生成）"
                : (wasPracticeComposition
                    ? "（尝试连续演唱练习片段，但音频没有生成）"
                    : "（尝试回哼，但音频没有生成）"));
        if (needsHistory && m_ChatHistory != null) m_ChatHistory.Add(actionText);
        if (completed)
        {
            RecordHumBackResult(
                wasCatalogSong
                    ? (wasCatalogContinuation
                        ? $"成功：已经从本地歌曲记忆中定位并真实播放了“{catalogSongName}”当前片段之后的已学内容。"
                        : $"成功：已经从本地歌曲记忆中取出“{catalogSongName}”并用角色声线真实播放完成。")
                    : (wasPracticeComposition
                        ? "成功：练唱会话中的片段已经按原顺序合成为一次连续演唱，并真实播放完成。每次演绎的呼吸间隔、轻微速度和力度可以不同。"
                        : "成功：回哼音频已经真实播放完成。现在可以自然评价刚才的回哼，但不要夸大为同步合唱。"),
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
                "失败：回哼音频没有生成或播放。必须如实承认没有唱出来，不得让用户评价不存在的声音。",
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
            m_ActiveHumBackClip != null || m_ActiveHumSVCRequest != null ||
            m_HumBackPrefixPreparing || m_PreparedHumBackPrefixClip != null ||
            m_FastHumBackEouStaged || m_FastHumBackActive || m_FastHumBackFullClip != null;
        if (!hadWork) return;

        m_SongSingGeneration++;
        m_SongSingInFlight = false;
        m_HumBackGeneration++;
        bool wasNeuralRequest = m_ActiveHumSVCRequest != null;
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
                "中断：回哼开始后被用户打断，没有完整播放。不得声称已经完整唱完。",
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
        m_PendingHumSourceWav = null;
        m_PendingHumIsPracticeComposition = false;
        m_PendingHumIsCatalogSong = false;
        m_PendingHumIsCatalogContinuation = false;
        m_PendingCatalogSongName = "";
        m_PendingHumVariationDiagnostic = "";
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

    private void RecordHumBackResult(string result, bool warning)
    {
        m_LastHumBackResult = result ?? "";
        m_HumBackResultPending = !string.IsNullOrWhiteSpace(m_LastHumBackResult);
        if (!m_LogHumBack || !m_HumBackResultPending) return;
        if (warning) Debug.LogWarning("[HumBack/Result] " + m_LastHumBackResult);
        else Debug.Log("[HumBack/Result] " + m_LastHumBackResult);
    }

    //一条正则覆盖全部可执行标签(+noop):属性用 [^>]* 而不是精确引号匹配——
    //本地模型偶尔输出全角引号(＂/“)甚至漏掉自闭合斜杠,这里都要兜住,
    //否则漏网的标签会被 TTS 念出来、显示在字幕上。
    private static readonly System.Text.RegularExpressions.Regex s_AllAgentTagsRegex =
        new System.Text.RegularExpressions.Regex(
            @"<(?:next|continue|silent|noop|look|unlook|memory_add|memory_update|song_search|song_remember|song_rename|song_forget|song_sing|hum_back)\b[^>]*>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    //用于流式阶段识别“尚未闭合”的标签。必须与 s_AllAgentTagsRegex 的名称集合保持一致。
    private static readonly string[] s_AgentTagStarts =
    {
        "<next", "<continue", "<silent", "<noop", "<look", "<unlook",
        "<memory_add", "<memory_update", "<song_search", "<song_remember",
        "<song_rename", "<song_forget", "<song_sing", "<hum_back"
    };

    /// <summary>
    /// 返回完整或部分已知工具标签的起点。例如“正文&lt;mem”也会返回“&lt;”的位置，
    /// 让流式切句器暂存该后缀；普通文本里的小于号不会无条件被吞掉。
    /// </summary>
    private static int FindPotentialAgentTagStart(string text)
    {
        if (string.IsNullOrEmpty(text)) return -1;
        int searchFrom = 0;
        while (searchFrom < text.Length)
        {
            int start = text.IndexOf('<', searchFrom);
            if (start < 0) return -1;
            string remainder = text.Substring(start);
            foreach (string tagStart in s_AgentTagStarts)
            {
                int compareLength = Math.Min(remainder.Length, tagStart.Length);
                if (string.Compare(remainder, 0, tagStart, 0, compareLength,
                    StringComparison.OrdinalIgnoreCase) != 0)
                    continue;

                //当前串还只是标签名的前缀（如 <m / <memory_），应继续等待。
                if (remainder.Length <= tagStart.Length) return start;

                //完整标签名后必须接空白、/ 或 >，避免把 <memory_additional 当成工具标签。
                char next = remainder[tagStart.Length];
                if (char.IsWhiteSpace(next) || next == '/' || next == '>') return start;
            }
            searchFrom = start + 1;
        }
        return -1;
    }

    private string StripAgentTagsForTTS(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        string clean = s_AllAgentTagsRegex.Replace(text, "");
        //全文结束时若模型生成了畸形/未闭合标签，也宁可丢掉该标签后缀，绝不朗读。
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
        var nm = nextRegex.Match(cleanText);
        if (nm.Success)
        {
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
            cleanText = nextRegex.Replace(cleanText, "").Trim();
        }

        var contRegex = new System.Text.RegularExpressions.Regex(@"<continue\s*/>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (contRegex.IsMatch(cleanText))
        {
            wantsContinue = true;
            cleanText = contRegex.Replace(cleanText, "").Trim();
        }

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
