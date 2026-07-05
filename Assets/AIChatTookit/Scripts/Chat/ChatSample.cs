using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
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
        m_CommitMsgBtn.onClick.AddListener(delegate { SendData(); });
        RegistButtonEvent();
        InputSettingWhenWebgl();
    }

    private void Start()
    {
        //TTS预热：场景加载后立刻发一条极短请求，消除首次合成的冷启动延迟
        //结果被丢弃，用户不可见
        if (m_ChatSettings != null && m_ChatSettings.m_TextToSpeech != null)
        {
            m_ChatSettings.m_TextToSpeech.WarmUp();
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

        m_InputWord.text = "";
        m_TextBack.text = "正在思考中...";

        //切换思考动作
        SetAnimator("state", 1);

        //agent loop 启用时：把感知帧拼到用户文本前面，让 LLM 也能"感受"时间
        //(返回的字符串才是真正喂给 LLM 的——含 [感知帧 ...] + 用户原话)
        string llmInput = (m_AgentRunning) ? PrepareUserTurn(_postWord) : _postWord;

        //流式 or 整段
        if (m_UseStreaming && m_IsVoiceMode && m_ChatSettings.m_TextToSpeech != null)
        {
            StartStreaming(llmInput);
        }
        else
        {
            m_ChatSettings.m_ChatModel.PostMsg(llmInput, CallBack);
        }
    }

    /// <summary>
    /// AI回复的信息的回调
    /// </summary>
    /// <param name="_response"></param>
    private void CallBack(string _response)
    {
        _response = _response.Trim();
        m_TextBack.text = "";

        
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
        if (m_ChatSettings.m_SpeechToText == null)
            return;

        m_ChatSettings.m_SpeechToText.SpeechToText(_audioClip, DealingTextCallback);
    }

    /// <summary>
    /// Tentative-EOU路径专用：把clip送ASR做"预测识别"，但不进LLM链路——
    /// callback里RTSpeechHandler会看尾部是否说完，再决定走AcceptText还是丢弃。
    /// 不调用DealingTextCallback——避免预测命中前就提前刷UI/SendData。
    /// </summary>
    public void PreviewASR(AudioClip _audioClip, System.Action<string> _callback)
    {
        if (m_ChatSettings == null || m_ChatSettings.m_SpeechToText == null)
        {
            if (_callback != null) _callback("");
            return;
        }
        m_ChatSettings.m_SpeechToText.SpeechToText(_audioClip, _callback);
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
        //ASR延迟：从用户结束发言(MarkEOU)到拿到识别文本。
        //m_EouTime为0表示这一轮没有外部EOU标记(走的是按住按钮路径)，跳过。
        if (m_LogStreamTimings && m_EouTime > 0f)
        {
            float asrLatency = Time.realtimeSinceStartup - m_EouTime;
            Debug.Log($"[Timing] ASR done +{asrLatency:F2}s: \"{_msg}\"");
        }

        m_RecordTips.text = _msg;
        StartCoroutine(SetTextVisible(m_RecordTips));
        //自动发送
        if (m_AutoSend)
        {
            SendData(_msg);
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
        if (m_LogStreamTimings) Debug.Log($"[Timing] EOU @ {m_EouTime:F2}s — 用户停止说话");
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
        if (chunks.Count == 0) yield break;

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
    private IEnumerator TypeSentence(string _sentence, float totalDuration)
    {
        if (string.IsNullOrEmpty(_sentence)) yield break;
        string prefix = m_TextBack.text;
        int pos = 0;
        float waitPerChar = totalDuration > 0f && _sentence.Length > 0
            ? Mathf.Max(0.01f, totalDuration / _sentence.Length)
            : m_WordWaitTime;
        while (pos < _sentence.Length)
        {
            yield return new WaitForSeconds(waitPerChar);
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
    //LLM流是否已结束
    private bool m_StreamComplete = false;
    //TTSSender是否已全部处理完
    private bool m_TTSSenderDone = false;
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

    /// <summary>
    /// 启动流式管线：LLM实时吐字 -> 按句送TTS -> 顺序播放
    /// </summary>
    private void StartStreaming(string _postWord)
    {
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
        m_StreamStartTime = Time.realtimeSinceStartup;

        //barge-in相关状态：开始新一轮，听到的文本清零
        m_AssistantHeardText.Length = 0;
        m_CurrentlyPlayingText = "";
        IsAISpeaking = false;  //首块开始播放时才置true(见StreamAudioPlayer)

        //起两个消费者
        StartCoroutine(StreamTTSSender());
        StartCoroutine(StreamAudioPlayer());

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
        m_ChatSettings.m_ChatModel.PostMsgStream(_postWord, OnStreamDelta, OnStreamComplete, imageUrl);
    }

    private float Elapsed() { return Time.realtimeSinceStartup - m_StreamStartTime; }

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
        string cleanFull = full ?? "";

        //解析 agent 标签 — 从全文里抽出来 next/continue/silent/look，存到 m_Round*
        if (m_AgentRunning)
        {
            //记忆写入标签先行提取并应用——只在全文完成时做一次(chunk 级会重复计),
            //剥净后再交给 ParseAgentTags,保证 cleanFull 不残留记忆标签
            string afterMemTags;
            var memOps = MemoryTagParser.Extract(full ?? "", out afterMemTags);
            if (memOps != null && m_MemoryHub != null) m_MemoryHub.ApplyMemoryOps(memOps);

            float? nextInSec;
            string focus;
            bool wantsContinue;
            bool wantsSilent;
            bool? wantsLook;
            ParseAgentTags(afterMemTags, out cleanFull, out nextInSec, out focus, out wantsContinue, out wantsSilent, out wantsLook);
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
            m_ChatSettings.m_ChatModel.PostMsgStream(frame, OnStreamDelta, OnStreamComplete, chainImageUrl);
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
        int boundary = FindFlushBoundary(buf, !m_FirstChunkFlushed);

        if (boundary >= 0)
        {
            string completed = buf.Substring(0, boundary + 1).Trim();
            string remaining = buf.Substring(boundary + 1);
            m_SentenceBuffer.Length = 0;
            m_SentenceBuffer.Append(remaining);
            //过滤 agent 标签——LLM 把 <continue/> 单写一行时，"<continue/>\n" 会被
            //\n strong boundary 切出当成"一句"推进队列，TTS 就读出来了。这里兜底。
            if (m_AgentRunning) completed = StripAgentTagsForTTS(completed);
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
            if (m_AgentRunning) tail = StripAgentTagsForTTS(tail);
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
    private IEnumerator StreamTTSSender()
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
            //没活干就等
            while (m_PendingChunks.Count == 0)
            {
                if (m_StreamComplete)
                {
                    m_TTSSenderDone = true;
                    yield break;
                }
                yield return null;
            }

            string chunk = m_PendingChunks.Dequeue();
            //双重过滤：FlushCompleteSentences已经挡过一次，这里防止旧路径或者其他来源
            if (IsPurePunctuation(chunk)) continue;

            pending = null;
            pendingDone = false;
            if (m_LogStreamTimings) Debug.Log($"[Stream] T+{Elapsed():F2}s TTS请求发出: \"{chunk}\"");
            m_ChatSettings.m_TextToSpeech.Speak(chunk, onReceive);

            //TTS客户端正常会在20s内回调(成功或失败都会调)。这里的25s只是兜底，
            //防止TTS客户端自己挂掉永远不回调。GPT-SoVITS内部失败也会调callback(null,..)
            float waitStart = Time.realtimeSinceStartup;
            while (!pendingDone)
            {
                if (Time.realtimeSinceStartup - waitStart > 25f)
                {
                    Debug.LogError("TTS客户端无响应(>25s)，跳过此段: " + chunk);
                    break;
                }
                yield return null;
            }

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
    /// 消费m_PendingClips，顺序播放+逐字显示。
    /// 每个chunk播完才追加到m_AssistantHeardText——这样Interrupt时的"已听到部分"是真实的。
    /// 自然退出时把累计的heard text写入聊天历史并触发OnAISpeakDone给RTSpeechHandler。
    /// </summary>
    private IEnumerator StreamAudioPlayer()
    {
        bool firstChunk = true;
        while (true)
        {
            while (m_PendingClips.Count == 0)
            {
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
            yield return StartCoroutine(TypeSentence(text, clip.length));
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
        IsAISpeaking = false;
        SetAnimator("state", 0);

        string heard = m_AssistantHeardText.ToString();
        m_AssistantHeardText.Length = 0;
        m_CurrentlyPlayingText = "";

        if (!string.IsNullOrEmpty(heard))
        {
            m_ChatHistory.Add(heard);
        }

        //收 agent round——per-round 状态(ring buffer / consec count) OnStreamComplete 已写过；
        //这里只负责按本 chain 最后一节的 <next in/> 排下一拍。
        OnAgentRoundComplete();

        if (OnAISpeakDone != null) OnAISpeakDone();
    }

    /// <summary>
    /// 用户在角色说话时插话——立即停止TTS播放、按播放进度切当前chunk的尾巴入历史、
    /// 清空所有待播队列、触发OnAISpeakDone把控制权交还给RTSpeechHandler。
    /// 没在出声时调用是no-op。
    /// </summary>
    public void Interrupt()
    {
        if (!IsAISpeaking) return;

        //当前正在播的那一chunk按音频时长比例算听到了多少字
        if (m_AudioSource != null && m_AudioSource.clip != null && m_AudioSource.clip.length > 0f
            && !string.IsNullOrEmpty(m_CurrentlyPlayingText))
        {
            float fraction = Mathf.Clamp01(m_AudioSource.time / m_AudioSource.clip.length);
            int charsHeard = Mathf.FloorToInt(m_CurrentlyPlayingText.Length * fraction);
            if (charsHeard > 0)
            {
                m_AssistantHeardText.Append(m_CurrentlyPlayingText.Substring(0, charsHeard));
            }
        }

        //先把状态置成"已结束"，让StreamAudioPlayer的两个yield循环都能识别出"被打断"路径退出
        IsAISpeaking = false;

        //斩断所有出声相关：停音频、清待播、让生产者协程也退出
        if (m_AudioSource != null) m_AudioSource.Stop();
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
        m_AgentEyesOpen = false;        //每次启动默认闭眼，让 LLM 自己决定何时 <look/>
        ClearRoundParsed();
        if (m_LogAgentLoop) Debug.Log($"[Agent] Loop 启动 — 首帧 {m_FirstTickDelaySec:F1}s 后投递");
        ScheduleNextTick(m_FirstTickDelaySec, "session-start");
    }

    /// <summary>
    /// RTSpeechHandler 在 DisableRealtimeMode 时调用——停止 agent loop，撤销待 tick。
    /// </summary>
    public void StopAgentLoop()
    {
        if (!m_AgentRunning) return;
        m_AgentRunning = false;
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
        if (!m_AgentRunning) return;
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
        if (!m_AgentRunning) return;
        //角色还在说话(<continue/>链上一帧还没收尾) → 让流水线走完再排
        if (IsAISpeaking || m_AgentRoundInFlight)
        {
            if (m_LogAgentLoop) Debug.Log($"[Agent] FireTick 排队等待(speaking={IsAISpeaking}, inflight={m_AgentRoundInFlight})");
            ScheduleNextTick(m_MinTickSec, "still-busy");
            return;
        }
        //连续 AI 轮次硬上限——超过就硬等用户开口(NotifyUserStartedSpeaking 会清零)
        if (m_ConsecutiveAITurns >= m_MaxConsecutiveAITurns)
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
        StartStreaming(frame);
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

        //排下一帧——读本 chain 最后一节解析到的 m_Round*
        //(chain 中段 OnStreamComplete 会 ClearRoundParsed 清掉自己；终止节没清，所以这里能读)
        if (m_RoundContinue)
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

    private string StripAgentTagsForTTS(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var ic = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<next(?:\s+[^/>]*)?\s*/>", "", ic);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<continue\s*/>", "", ic);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<silent\s*/>", "", ic);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<noop\s*/>", "", ic);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<look\s*/>", "", ic);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"<unlook\s*/>", "", ic);
        text = MemoryTagParser.Strip(text);
        return text.Trim();
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
